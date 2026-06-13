using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Rocket.API;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Events;
using Rocket.Unturned.Player;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace Knockdown
{
    /// <summary>
    /// Down-but-not-out / revive system for Unturned 3.26.3.2 + RocketMod 4.9.3.18.
    ///
    /// All RocketMod/Unturned gameplay events and Unity's FixedUpdate run on the
    /// single Unity main thread, so the shared state below needs no locking.
    /// </summary>
    public sealed class Knockdown : RocketPlugin<KnockdownConfiguration>
    {
        public static Knockdown Instance { get; private set; }

        private const string HarmonyId = "com.imaximum.knockdown";
        private Harmony _harmony;

        /// <summary>Active downed players keyed by SteamID.</summary>
        private readonly Dictionary<CSteamID, DownedState> _downed = new Dictionary<CSteamID, DownedState>();

        /// <summary>
        /// Post-revive cooldown: SteamID -> Time.realtimeSinceStartup at which the player can be
        /// downed again. While an entry is in the future, a would-be-down hit kills them normally.
        /// In-memory only (a reconnect clears it; combat-logging while downed is punished separately).
        /// </summary>
        private readonly Dictionary<ulong, float> _reviveCooldownUntil = new Dictionary<ulong, float>();

        /// <summary>True if the player was revived within the last ReviveCooldown seconds.</summary>
        private bool IsOnReviveCooldown(CSteamID id)
        {
            if (!_reviveCooldownUntil.TryGetValue(id.m_SteamID, out float until))
                return false;
            if (Time.realtimeSinceStartup >= until)
            {
                _reviveCooldownUntil.Remove(id.m_SteamID); // expired - prune lazily
                return false;
            }
            return true;
        }

        /// <summary>True while the given player is in the downed state (used by the command-block patch).</summary>
        public static bool IsDowned(CSteamID id)
        {
            Knockdown inst = Instance;
            return inst != null && inst._downed.ContainsKey(id);
        }

        /// <summary>Tell a player their command was blocked because they're downed.</summary>
        internal static void TellNoCommandWhileDowned(Player player)
        {
            Knockdown inst = Instance;
            if (inst == null || player == null) return;
            Msg(player, inst.Configuration.Instance.MessageNoCommandWhileDowned);
        }

        /// <summary>Reused scratch buffer so FixedUpdate allocates nothing per tick.</summary>
        private readonly List<DownedState> _scratch = new List<DownedState>();

        /// <summary>Active "flare rising into the sky" animations, one queued per knockdown.</summary>
        private readonly List<FlareAnim> _flares = new List<FlareAnim>();

        /// <summary>Active "/testreviveui" HUD previews (animate the bar 0->100% then clear).</summary>
        private readonly List<UIDemo> _uiDemos = new List<UIDemo>();

        /// <summary>SteamIDs currently shown the "how to revive" hint HUD (downed + nearby revivers).</summary>
        private readonly HashSet<ulong> _hintShown = new HashSet<ulong>();
        /// <summary>Scratch: who should see the hint this tick, and with what text.</summary>
        private readonly Dictionary<ulong, string> _hintDesired = new Dictionary<ulong, string>();
        /// <summary>Scratch buffer for diffing _hintShown without mutating it mid-iteration.</summary>
        private readonly List<ulong> _hintScratch = new List<ulong>();
        /// <summary>Throttle accumulator for the hint recompute.</summary>
        private float _hintAccum;

        /// <summary>
        /// SteamIDs of players who opted out of the knockdown system (they die normally
        /// instead of being downed). Persisted to Knockdown.optout.xml beside the plugin DLL.
        /// </summary>
        private readonly HashSet<ulong> _optedOut = new HashSet<ulong>();

        /// <summary>Manager for loot boxes dropped when a downed player combat-logs.</summary>
        private DeathBoxManager _deathBoxes;

        /// <summary>
        /// SteamIDs of players who disconnected while downed: they die the instant they next
        /// reconnect. Persisted to Knockdown.pendingdeaths.txt so a restart can't wash it out.
        /// </summary>
        private readonly HashSet<ulong> _pendingDeaths = new HashSet<ulong>();

        // -----------------------------------------------------------------
        //  Plugin lifecycle
        // -----------------------------------------------------------------

        protected override void Load()
        {
            Instance = this;

            LoadOptOut();
            LoadPendingDeaths();

            _deathBoxes = new DeathBoxManager();

            // Harmony patches (attribute-based, applied to the whole assembly):
            //   * BlockDownedCommandsPatch    - stop downed players running /home etc. (respects config).
            //   * KnockdownRemovePlayerPatch   - capture loot + close the combat-log exploit on disconnect.
            // Adding Harmony means this plugin can no longer be `/rocket reload`-ed: swap the DLL with a
            // full server restart.
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(Knockdown).Assembly);

                // Loot-only: block depositing INTO a death box (manual patch, graceful fallback).
                if (Configuration.Instance.DownedLogoutBoxBlockDeposit)
                    TryPatchDeathBoxDepositBlock();
            }
            catch (System.Exception ex)
            {
                Logger.LogException(ex, "Knockdown: Harmony patch failed - command block + downed-logout punishment inactive.");
            }

            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            U.Events.OnPlayerDisconnected += OnPlayerDisconnected;
            U.Events.OnPlayerConnected += OnPlayerConnected;

            // Boxes don't exist until the level is loaded; sweep leftovers from the
            // previous session then (or immediately on a runtime reload).
            if (Configuration.Instance.DownedLogoutBoxSweepOnLoad)
            {
                if (Level.isLoaded) _deathBoxes.SweepOrphans();
                else Level.onLevelLoaded += OnLevelLoadedSweep;
            }

            // On-screen killfeed: every death feeds a line; respawn re-spawns the cleared effect.
            if (Configuration.Instance.EnableKillfeed)
            {
                UnturnedPlayerEvents.OnPlayerDeath += OnPlayerDeathFeed;
                UnturnedPlayerEvents.OnPlayerRevive += OnPlayerReviveFeed;
            }

            // Item-based instant revive: a nearby player using a configured medical
            // item revives a downed teammate. The item is consumed by the game itself.
            // Two ways players use a heal item:
            //   - onPerformingAid: AIM at the downed player and use it (heal-another). Gives us the exact target.
            //   - onConsumePerformed: use it on THEMSELVES while standing near the downed player.
            if (Configuration.Instance.EnableItemRevive)
            {
                UseableConsumeable.onPerformingAid += OnPerformingAid;
                UseableConsumeable.onConsumePerformed += OnConsumePerformed;
            }

            Logger.Log("Knockdown loaded. KnockDuration=" + Configuration.Instance.KnockDuration +
                       "s, ReviveDuration=" + Configuration.Instance.ReviveDuration + "s.");
        }

        protected override void Unload()
        {
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;
            U.Events.OnPlayerDisconnected -= OnPlayerDisconnected;
            U.Events.OnPlayerConnected -= OnPlayerConnected;
            Level.onLevelLoaded -= OnLevelLoadedSweep; // in case we unload before the level loaded
            UnturnedPlayerEvents.OnPlayerDeath -= OnPlayerDeathFeed;
            UnturnedPlayerEvents.OnPlayerRevive -= OnPlayerReviveFeed;
            UseableConsumeable.onPerformingAid -= OnPerformingAid;
            UseableConsumeable.onConsumePerformed -= OnConsumePerformed;

            if (_deathBoxes != null)
            {
                _deathBoxes.ClearAll();
                _deathBoxes = null;
            }

            if (_harmony != null)
            {
                try { _harmony.UnpatchAll(HarmonyId); } catch { /* ignore */ }
                _harmony = null;
            }

            // Tear down every active downed state so no event handlers or speed
            // multipliers leak when the plugin reloads.
            _scratch.Clear();
            _scratch.AddRange(_downed.Values);
            foreach (DownedState state in _scratch)
                Teardown(state);
            _downed.Clear();
            _scratch.Clear();
            _flares.Clear();
            _reviveCooldownUntil.Clear();

            foreach (UIDemo demo in _uiDemos)
                ClearReviveUI(demo.Id);
            _uiDemos.Clear();

            ClearAllHints();

            Instance = null;
            Logger.Log("Knockdown unloaded.");
        }

        // -----------------------------------------------------------------
        //  Damage interception
        // -----------------------------------------------------------------

        private void OnDamagePlayerRequested(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            Player player = parameters.player;
            if (player == null || player.life == null || player.life.isDead)
                return;

            CSteamID id = player.channel.owner.playerID.steamID;

            // --- Player is already downed ---
            if (_downed.TryGetValue(id, out DownedState state))
            {
                KnockdownConfiguration c = Configuration.Instance;

                // Grace period: fully immune while bleeding hasn't started yet.
                if (state.Elapsed < c.InvincibleDuration)
                {
                    shouldAllow = false;
                    return;
                }

                if (c.InvincibleWhileDowned)
                {
                    // Immune after grace too: only the HP drain can kill them.
                    shouldAllow = false;
                    return;
                }

                // Not immune after grace: let combat damage subtract from the draining
                // HP pool (it can finish the player early); ignore environmental ticks.
                shouldAllow = IsCombatCause(parameters.cause);
                return;
            }

            // --- Opted out of the system: never get downed, take damage (incl. lethal) normally. ---
            if (_optedOut.Contains(id.m_SteamID))
                return;

            // --- Not downed: would this hit kill them? ---
            // Note: this compares raw incoming damage (damage * times) against
            // current health. It deliberately ignores armour mitigation, which is
            // the standard approach for downed-state plugins; worst case a player
            // is downed a fraction early rather than dying outright.
            float incoming = parameters.damage * parameters.times;
            if (incoming >= player.life.health)
            {
                // Post-revive cooldown: no second down. Let the hit kill them normally.
                if (IsOnReviveCooldown(id))
                {
                    Msg(player, Configuration.Instance.MessageReviveCooldownDeath);
                    return; // shouldAllow stays true -> ordinary death
                }

                shouldAllow = false; // prevent the death
                EnterKnockdown(player, id, parameters.cause, parameters.killer);
            }
        }

        // -----------------------------------------------------------------
        //  Enter / exit knockdown
        // -----------------------------------------------------------------

        /// <summary>Public entry point for test commands: force the player into knockdown immediately.</summary>
        public void ForceKnockdown(Player player)
        {
            if (player == null || player.life == null || player.life.isDead)
                return;
            CSteamID id = player.channel.owner.playerID.steamID;
            if (_downed.ContainsKey(id))
                return;
            EnterKnockdown(player, id, EDeathCause.PUNCH, id);
        }

        private void EnterKnockdown(Player player, CSteamID id, EDeathCause cause, CSteamID killer)
        {
            if (_downed.ContainsKey(id))
                return;

            KnockdownConfiguration cfg = Configuration.Instance;

            DownedState state = new DownedState
            {
                Id = id,
                Player = UnturnedPlayer.FromPlayer(player),
                Elapsed = 0f,
                DeathCause = cause,
                KillerId = killer,
                ReviverId = CSteamID.Nil,
                ReviveProgress = 0f,
                ReapplyAccumulator = 0f,
                UIReviverId = CSteamID.Nil,
                BloodAccumulator = cfg.DownedBloodInterval // fire the first blood burst right away
            };
            _downed[id] = state;

            // Set downed HP and stop any bleeding/broken bones so the knock timer
            // (not a bleed-out) decides when they die.
            player.life.serverModifyHealth(cfg.KnockHealth - player.life.health);
            player.life.askHeal(0, true, true);

            ApplyDownedConstraints(player, cfg);

            // Block equipping anything (covers shooting, weapons and item use).
            player.equipment.dequip();
            player.equipment.onEquipRequested += OnEquipRequested;

            TriggerEffect(cfg.KnockEffectID, player.transform.position);
            QueueKnockFlare(player.transform.position, cfg);
            Msg(player, cfg.MessageKnocked);

            KillfeedDowned(killer, id, cause);
        }

        /// <summary>Schedules a flare-into-the-sky animation from <paramref name="origin"/> using current config.</summary>
        private void QueueKnockFlare(Vector3 origin, KnockdownConfiguration cfg)
        {
            if (cfg.KnockFlareEffectID == 0 || cfg.KnockFlareSteps <= 0 || cfg.KnockFlareDuration <= 0f || cfg.KnockFlareHeight <= 0f)
                return;
            _flares.Add(new FlareAnim { Start = origin, Elapsed = 0f, LastStep = -1 });
        }

        /// <summary>Restores movement and removes the equip block. Does not touch the dictionary.</summary>
        private void Teardown(DownedState state)
        {
            // Tear down any revive HUD on both the downed player and their reviver.
            ClearReviveUI(state.Id);
            ClearReviveUI(state.UIReviverId);
            state.UIReviverId = CSteamID.Nil;

            // Also drop the downed player's "how to revive" hint immediately.
            if (_hintShown.Remove(state.Id.m_SteamID))
                ClearHint(state.Id);

            Player player = state.Player?.Player;
            if (player == null)
                return;

            player.equipment.onEquipRequested -= OnEquipRequested;
            if (player.movement != null)
                player.movement.sendPluginSpeedMultiplier(1f);

            // If we forced the sitting/rest pose, stand the player back up.
            if (player.animator != null && IsSitPose(Configuration.Instance.DownedPose))
                player.animator.sendGesture(EPlayerGesture.REST_STOP, true);
        }

        /// <summary>Crouch-channel revive: brings the player back at ReviveHealth.</summary>
        private void Revive(DownedState state)
        {
            ReviveInternal(state, Configuration.Instance.ReviveHealth);
        }

        /// <summary>
        /// Clears the downed state and stands the player back up at <paramref name="targetHp"/> HP.
        /// Used by both the crouch-channel revive (ReviveHealth) and the item revive (item heal value).
        /// </summary>
        private void ReviveInternal(DownedState state, int targetHp)
        {
            Player player = state.Player?.Player;
            KnockdownConfiguration cfg = Configuration.Instance;

            Teardown(state);
            _downed.Remove(state.Id);

            if (player == null || player.life == null || player.life.isDead)
                return;

            // Revived players come back at exactly targetHp, regardless of how much
            // HP they had left when revived.
            byte hp = (byte)Mathf.Clamp(targetHp, 1, 100);
            player.life.serverModifyHealth(hp - player.life.health);

            // Clear the bleeding / broken-legs status so a revived player comes back clean.
            // askHeal(0, ...) doesn't reliably clear these on this build (amount=0), so set them
            // explicitly via the server-authority setters.
            if (player.life.isBleeding)
                player.life.serverSetBleeding(false);
            if (player.life.isBroken)
                player.life.serverSetLegsBroken(false);

            // Start the post-revive cooldown: they can't be downed again until it elapses
            // (a lethal hit kills them normally instead). Applies to every revive path.
            if (cfg.ReviveCooldown > 0f)
                _reviveCooldownUntil[state.Id.m_SteamID] = Time.realtimeSinceStartup + cfg.ReviveCooldown;

            TriggerEffect(cfg.ReviveEffectID, player.transform.position);
            Msg(player, cfg.MessageRevived);
        }

        /// <summary>
        /// A player AIMED at a downed teammate and used a medical item (heal-another).
        /// If the item id is configured, instantly revive that exact target. The item is
        /// still consumed (we leave shouldAllow = true), which is the cost.
        /// </summary>
        private void OnPerformingAid(Player instigator, Player target, ItemConsumeableAsset asset, ref bool shouldAllow)
        {
            KnockdownConfiguration cfg = Configuration.Instance;
            if (!cfg.EnableItemRevive || asset == null || target == null || instigator == null)
                return;
            if (target.channel?.owner == null)
                return;

            // Only act when the AID target is actually a downed player.
            CSteamID targetId = target.channel.owner.playerID.steamID;
            if (!_downed.TryGetValue(targetId, out DownedState state))
                return;

            // A downed player cannot be the reviver.
            if (instigator.channel?.owner != null && _downed.ContainsKey(instigator.channel.owner.playerID.steamID))
                return;

            bool listed = cfg.ItemReviveIds != null && cfg.ItemReviveIds.Contains(asset.id);
            // Diagnostic: tells you the exact id of the item used on a downed player, so you
            // can add a Workshop heal item's id to ItemReviveIds if it isn't there yet.
            Logger.Log("Knockdown: AID used on a downed player with item id=" + asset.id +
                       (listed ? " -> REVIVE" : " -> NOT in ItemReviveIds (add this id to enable)"));

            if (!listed)
                return;

            int hp = asset.health > 0 ? asset.health : cfg.ReviveHealth;
            ReviveInternal(state, hp);
            if (instigator.channel?.owner != null)
                Msg(instigator, cfg.MessageItemRevive);
            // shouldAllow stays true: let the game consume the item as the revive cost.
        }

        /// <summary>
        /// A player used a configured medical item ON THEMSELVES while standing near a downed
        /// player: instantly revive the closest downed player within ReviveDistance, to the
        /// item's own heal value. The item was already consumed by the game (the cost).
        /// </summary>
        private void OnConsumePerformed(Player instigatingPlayer, ItemConsumeableAsset consumeableAsset)
        {
            KnockdownConfiguration cfg = Configuration.Instance;
            if (!cfg.EnableItemRevive || consumeableAsset == null || instigatingPlayer == null)
                return;
            if (instigatingPlayer.channel?.owner == null)
                return;

            // A downed player cannot revive anyone (and is blocked from using items anyway).
            CSteamID reviverId = instigatingPlayer.channel.owner.playerID.steamID;
            if (_downed.ContainsKey(reviverId))
                return;

            // Find the closest downed player within revive range of the item user.
            Vector3 pos = instigatingPlayer.transform.position;
            float bestSqr = cfg.ReviveDistance * cfg.ReviveDistance;
            DownedState best = null;
            foreach (DownedState s in _downed.Values)
            {
                Player p = s.Player?.Player;
                if (p == null || p.life == null || p.life.isDead)
                    continue;
                float d = (p.transform.position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    best = s;
                }
            }

            // Nobody downed in range: the item just healed the user normally.
            if (best == null)
                return;

            bool listed = cfg.ItemReviveIds != null && cfg.ItemReviveIds.Contains(consumeableAsset.id);
            Logger.Log("Knockdown: item used near a downed player, id=" + consumeableAsset.id +
                       (listed ? " -> REVIVE" : " -> NOT in ItemReviveIds (add this id to enable)"));

            if (!listed)
                return;

            // Revive to the item's heal value; items with no heal fall back to ReviveHealth.
            int hp = consumeableAsset.health > 0 ? consumeableAsset.health : cfg.ReviveHealth;
            ReviveInternal(best, hp);
            Msg(instigatingPlayer, cfg.MessageItemRevive);
        }

        /// <summary>Knock timer expired - kill the player normally, crediting the original attacker.</summary>
        private void KillDowned(DownedState state)
        {
            Player player = state.Player?.Player;

            Teardown(state);
            _downed.Remove(state.Id);

            if (player == null || player.life == null || player.life.isDead)
                return;

            player.life.askDamage(byte.MaxValue, Vector3.up * 16f, state.DeathCause,
                                  ELimb.SKULL, state.KillerId, out EPlayerKill _);
        }

        private static void ApplyDownedConstraints(Player player, KnockdownConfiguration cfg)
        {
            if (player.movement != null)
                player.movement.sendPluginSpeedMultiplier(cfg.CrawlSpeed); // crawl only
            ApplyPose(player, cfg);
        }

        /// <summary>Best-effort downed pose: SIT (rest gesture), CROUCH or PRONE.</summary>
        private static void ApplyPose(Player player, KnockdownConfiguration cfg)
        {
            string pose = (cfg.DownedPose ?? "SIT").Trim().ToUpperInvariant();
            switch (pose)
            {
                case "SIT":
                case "REST":
                    if (player.animator != null)
                        player.animator.sendGesture(EPlayerGesture.REST_START, true);
                    break;
                case "CROUCH":
                    if (player.stance != null)
                        player.stance.checkStance(EPlayerStance.CROUCH);
                    break;
                default: // PRONE
                    if (player.stance != null)
                        player.stance.checkStance(EPlayerStance.PRONE);
                    break;
            }
        }

        private static bool IsSitPose(string pose)
        {
            string p = (pose ?? "SIT").Trim().ToUpperInvariant();
            return p == "SIT" || p == "REST";
        }

        // -----------------------------------------------------------------
        //  Per-tick processing (timers + revive channel)
        // -----------------------------------------------------------------

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            KnockdownConfiguration cfg = Configuration.Instance;

            // Tick the "flare into the sky" animations independently of downed players,
            // so they keep playing even if the player who triggered them is revived/killed.
            TickFlares(dt, cfg);
            TickUIDemos(dt, cfg);
            TickHints(dt, cfg);
            TickKillfeed(dt);

            // Decay loot boxes dropped by downed logouts (independent of live downed players).
            if (_deathBoxes != null)
                _deathBoxes.Tick(dt);

            if (_downed.Count == 0)
                return;

            _scratch.Clear();
            _scratch.AddRange(_downed.Values);

            foreach (DownedState state in _scratch)
            {
                Player player = state.Player?.Player;

                // Player vanished (kicked mid-frame, etc.) - drop the state.
                if (player == null || player.life == null || player.life.isDead)
                {
                    Teardown(state);
                    _downed.Remove(state.Id);
                    continue;
                }

                // Tick a pending "revive cancelled" HUD flash, then clear it.
                if (state.UICancelFlash > 0f)
                {
                    state.UICancelFlash -= dt;
                    if (state.UICancelFlash <= 0f)
                        ClearReviveUI(state.Id);
                }

                // Repeating blood splatter on the downed player's body until they revive/die.
                if (cfg.DownedBloodEffectID != 0 && cfg.DownedBloodInterval > 0f)
                {
                    state.BloodAccumulator += dt;
                    if (state.BloodAccumulator >= cfg.DownedBloodInterval)
                    {
                        state.BloodAccumulator = 0f;
                        TriggerEffect(cfg.DownedBloodEffectID, player.transform.position + Vector3.up * 0.4f);
                    }
                }

                // Bleed-out (and downtime) pauses while a revive is in progress, if enabled.
                bool beingRevived = state.ReviverId != CSteamID.Nil;
                bool paused = beingRevived && cfg.PauseDrainWhileReviving;

                if (!paused)
                    state.Elapsed += dt;

                float graceEnd = cfg.InvincibleDuration;
                float total = Mathf.Max(graceEnd + 0.01f, cfg.KnockDuration);

                // Combat emptied the HP pool, or (when not paused) the downtime ran out -> die.
                if (player.life.health == 0 || (!paused && state.Elapsed >= total))
                {
                    KillDowned(state);
                    continue;
                }

                // After the grace period, bleed HP down along a linear curve toward 0.
                // We only ever push HP DOWN, so combat damage that dropped them below the
                // curve is preserved (the player can be finished early).
                if (!paused && state.Elapsed > graceEnd)
                {
                    float frac = (state.Elapsed - graceEnd) / (total - graceEnd); // 0..1
                    int targetHp = Mathf.Clamp(Mathf.CeilToInt(cfg.KnockHealth * (1f - frac)), 1, cfg.KnockHealth);
                    int current = player.life.health;
                    if (current > targetHp)
                        player.life.serverModifyHealth(targetHp - current);
                }

                // Once a second: re-assert crawl speed / pose (must stay short to fight back
                // against player-controlled stance changes promptly).
                state.ReapplyAccumulator += dt;
                if (state.ReapplyAccumulator >= 1f)
                {
                    state.ReapplyAccumulator = 0f;
                    ApplyDownedConstraints(player, cfg);
                    if (player.equipment.HasValidUseable)
                        player.equipment.dequip();
                }

                // HP-bleeding chat message runs on its own (configurable) interval so it
                // doesn't spam every second. Skipped while being revived — revive message shows instead.
                state.HpMessageAccumulator += dt;
                float hpInterval = cfg.DownedHpMessageInterval > 0f ? cfg.DownedHpMessageInterval : 5f;
                if (!beingRevived && state.HpMessageAccumulator >= hpInterval)
                {
                    state.HpMessageAccumulator = 0f;
                    if (HasText(cfg.MessageDownedHp))
                    {
                        int secondsLeft = Mathf.Max(0, Mathf.CeilToInt(total - state.Elapsed));
                        string hpText = cfg.MessageDownedHp.Text
                            .Replace("{hp}", player.life.health.ToString())
                            .Replace("{seconds}", secondsLeft.ToString());
                        Msg(player, cfg.MessageDownedHp, hpText);
                    }
                }

                // Optional: draw a ring of effect points around the downed player to visualise revive range.
                if (cfg.RangeEffectID != 0 && cfg.RangeEffectPoints > 0)
                {
                    state.RangeEffectAccumulator += dt;
                    float interval = cfg.RangeEffectInterval > 0f ? cfg.RangeEffectInterval : 0.5f;
                    if (state.RangeEffectAccumulator >= interval)
                    {
                        state.RangeEffectAccumulator = 0f;
                        Vector3 ringCenter = player.transform.position + new Vector3(0f, cfg.RangeEffectYOffset, 0f);
                        TriggerRingEffect(cfg.RangeEffectID, ringCenter, cfg.ReviveDistance, cfg.RangeEffectPoints);
                    }
                }

                UpdateRevive(state, player, cfg, dt);
            }
        }

        private void UpdateRevive(DownedState state, Player target, KnockdownConfiguration cfg, float dt)
        {
            Vector3 targetPos = target.transform.position;

            // Is the currently-claimed reviver still valid?
            // Re-validation passes initialClaim=false so CROUCH_START doesn't require the
            // reviver to keep crouching after they started the channel.
            Player reviver = null;
            if (state.ReviverId != CSteamID.Nil)
            {
                Player current = PlayerTool.getPlayer(state.ReviverId);
                if (IsEligibleReviver(current, target, targetPos, cfg, initialClaim: false))
                    reviver = current;
            }

            // Claim lapsed -> notify and look for a new reviver.
            if (reviver == null)
            {
                if (state.ReviverId != CSteamID.Nil)
                {
                    Msg(target, cfg.MessageReviveCancelled);
                    ClearReviveUI(state.UIReviverId); // reviver's HUD off immediately
                    // Flash "REVIVE CANCELLED" on the downed player's HUD, then auto-clear (ticked below).
                    if (cfg.ReviveUIEffectID != 0)
                    {
                        RevealBar(state.Id, state.UIBarFill, 0); // drain the bar
                        state.UIBarFill = 0;
                        UpdateReviveUI(state.Id, cfg.ReviveUITitleCancelled, "");
                        state.UICancelFlash = 1.5f;
                    }
                    state.UIReviverId = CSteamID.Nil;
                    state.ReviverId = CSteamID.Nil;
                    state.ReviveProgress = 0f;
                    state.ProgressTickAccumulator = 0f;
                    state.ProgressMessageAccumulator = 0f;
                }

                foreach (SteamPlayer client in Provider.clients)
                {
                    Player candidate = client.player;
                    if (IsEligibleReviver(candidate, target, targetPos, cfg, initialClaim: true))
                    {
                        state.ReviverId = client.playerID.steamID;
                        state.ReviveProgress = 0f;
                        state.ProgressTickAccumulator = 0f;
                    state.ProgressMessageAccumulator = 0f;
                        reviver = candidate;
                        Msg(reviver, cfg.MessageReviveStarted);
                        Msg(target, cfg.MessageBeingRevived);
                        SendReviverGesture(reviver, cfg);

                        // Center-screen HUD for both parties (fresh prefab spawns with an empty bar).
                        state.UIReviverId = state.ReviverId;
                        state.UIAccumulator = 0f;
                        state.UICancelFlash = 0f; // a fresh revive cancels any pending "cancelled" flash
                        state.UIBarFill = 0;
                        ShowReviveUI(state.ReviverId);
                        ShowReviveUI(state.Id);
                        UpdateReviveUI(state.ReviverId, cfg.ReviveUITitleReviver, Pct(0f, cfg.ReviveDuration));
                        UpdateReviveUI(state.Id, cfg.ReviveUITitleDowned, Pct(0f, cfg.ReviveDuration));
                        break;
                    }
                }
            }

            if (reviver == null)
                return;

            state.ReviveProgress += dt;
            if (state.ReviveProgress >= cfg.ReviveDuration)
            {
                Revive(state); // -> Teardown clears the HUD for both
                return;
            }

            // Smoothly refresh the center-screen HUD bar (throttled, independent of the chat interval).
            if (cfg.ReviveUIEffectID != 0)
            {
                state.UIAccumulator += dt;
                if (state.UIAccumulator >= 0.15f)
                {
                    state.UIAccumulator = 0f;
                    int newFill = FillCount(state.ReviveProgress, cfg.ReviveDuration);
                    if (newFill != state.UIBarFill)
                    {
                        RevealBar(state.ReviverId, state.UIBarFill, newFill);
                        RevealBar(state.Id, state.UIBarFill, newFill);
                        state.UIBarFill = newFill;
                    }
                    string pct = Pct(state.ReviveProgress, cfg.ReviveDuration);
                    UpdateRevivePercent(state.ReviverId, pct);
                    UpdateRevivePercent(state.Id, pct);
                }
            }

            // Progress feedback + revive sound. The sound stays on a 1s tick (gameplay cue);
            // the chat message uses its own configurable interval to avoid spam.
            state.ProgressTickAccumulator += dt;
            state.ProgressMessageAccumulator += dt;
            float progressMsgInterval = cfg.ReviveProgressMessageInterval > 0f ? cfg.ReviveProgressMessageInterval : 2f;
            bool soundTick = state.ProgressTickAccumulator >= 1f;
            bool messageTick = state.ProgressMessageAccumulator >= progressMsgInterval;
            if (soundTick)
                state.ProgressTickAccumulator -= 1f;
            if (messageTick)
                state.ProgressMessageAccumulator = 0f;

            if (messageTick && HasText(cfg.MessageReviveProgress))
            {
                int secondsLeft = Mathf.CeilToInt(cfg.ReviveDuration - state.ReviveProgress);
                int total = Mathf.RoundToInt(cfg.ReviveDuration);
                int percent = Mathf.Clamp(Mathf.RoundToInt(state.ReviveProgress / cfg.ReviveDuration * 100f), 0, 100);

                string text = cfg.MessageReviveProgress.Text
                    .Replace("{seconds}", secondsLeft.ToString())
                    .Replace("{total}", total.ToString())
                    .Replace("{percent}", percent.ToString());

                Msg(reviver, cfg.MessageReviveProgress, text);
                Msg(target, cfg.MessageReviveProgress, text);
            }

            if (soundTick)
            {
                if (cfg.ReviveSoundEffectID != 0)
                    TriggerEffect(cfg.ReviveSoundEffectID, target.transform.position);

                // Re-play the gesture each second so the reviver keeps pointing for the
                // whole channel (POINT is a one-shot animation).
                SendReviverGesture(reviver, cfg);
            }
        }

        /// <summary>Plays the configured gesture on the reviver (e.g. POINT at the downed player).</summary>
        private static void SendReviverGesture(Player reviver, KnockdownConfiguration cfg)
        {
            if (reviver?.animator == null)
                return;
            string name = (cfg.ReviverGesture ?? "POINT").Trim();
            if (name.Length == 0 || name.ToUpperInvariant() == "NONE")
                return;
            if (System.Enum.TryParse(name, true, out EPlayerGesture gesture))
                reviver.animator.sendGesture(gesture, true);
        }

        private bool IsEligibleReviver(Player reviver, Player target, Vector3 targetPos, KnockdownConfiguration cfg, bool initialClaim)
        {
            if (reviver == null || reviver == target)
                return false;
            if (reviver.life == null || reviver.life.isDead)
                return false;
            if (_downed.ContainsKey(reviver.channel.owner.playerID.steamID))
                return false; // a downed player cannot revive
            if ((reviver.transform.position - targetPos).sqrMagnitude > cfg.ReviveDistance * cfg.ReviveDistance)
                return false;
            return IsReviveInputActive(reviver, cfg, initialClaim);
        }

        /// <summary>
        /// Whether the reviver is performing the revive input. CROUCH (default) is fully
        /// server-side and needs no key binding; PLUGINKEY uses a bound Unturned plugin key;
        /// CROUCH_START requires crouch only to begin — after that, staying in range is enough.
        /// </summary>
        private static bool IsReviveInputActive(Player reviver, KnockdownConfiguration cfg, bool initialClaim)
        {
            string mode = (cfg.ReviveInput ?? "CROUCH").Trim().ToUpperInvariant();
            if (mode == "PLUGINKEY")
                return reviver.input != null && reviver.input.IsPluginKeyHeld(cfg.RevivePluginKeyIndex);

            if (mode == "CROUCH_START")
            {
                // Only the initial claim needs crouch; while the channel is running
                // we don't care about stance — the range check in IsEligibleReviver does the gating.
                if (!initialClaim)
                    return true;
                return reviver.stance != null && reviver.stance.stance == EPlayerStance.CROUCH;
            }

            // Default "CROUCH": hold crouch (ย่อ) continuously near the downed player.
            return reviver.stance != null && reviver.stance.stance == EPlayerStance.CROUCH;
        }

        // -----------------------------------------------------------------
        //  Equip blocking + disconnect cleanup
        // -----------------------------------------------------------------

        private void OnEquipRequested(PlayerEquipment equipment, ItemJar jar, ItemAsset asset, ref bool shouldAllow)
        {
            if (equipment?.player == null)
                return;
            if (_downed.ContainsKey(equipment.player.channel.owner.playerID.steamID))
                shouldAllow = false;
        }

        private void OnPlayerDisconnected(UnturnedPlayer player)
        {
            if (player == null)
                return;

            // Anti-combat-log: the Provider.removePlayer prefix is the ideal pre-save hook, but it
            // doesn't fire on this Unturned build, so handle the downed-logout here. This fires while
            // the player is still downed (verified). HandleDownedLogout is idempotent (guarded by the
            // _downed check), so if the prefix ever does fire first this is a no-op.
            if (player.Player != null)
                HandleDownedLogout(player.Player, player.CSteamID);

            if (_downed.TryGetValue(player.CSteamID, out DownedState state))
            {
                Teardown(state);
                _downed.Remove(player.CSteamID);
            }
            _feedSpawned.Remove(player.CSteamID.m_SteamID);
            _reviveCooldownUntil.Remove(player.CSteamID.m_SteamID);
            // Any downed players whose claimed reviver just left are revalidated
            // automatically on the next FixedUpdate (getPlayer returns null).
        }

        // -----------------------------------------------------------------
        //  Downed-logout punishment (anti-combat-log)
        // -----------------------------------------------------------------

        /// <summary>
        /// Called from the Provider.removePlayer Harmony PREFIX, BEFORE the disconnect save.
        /// If the leaving player is downed, turn the logout into a death: box their carried
        /// loot at the logout spot (clearing their grid so the save persists nothing -> no
        /// dupe) and mark them to die the instant they next reconnect.
        /// </summary>
        internal void HandleDownedLogout(Player p, CSteamID id)
        {
            if (p == null) return;
            KnockdownConfiguration cfg = Configuration.Instance;
            if (!cfg.DownedLogoutEnabled) return;

            if (!_downed.TryGetValue(id, out DownedState state))
                return; // not downed -> ordinary logout, nothing to punish

            try
            {
                Vector3 pos = p.transform.position;

                // Tear down the downed visuals/constraints; we're converting this into a death.
                Teardown(state);
                _downed.Remove(id);
                _feedSpawned.Remove(id.m_SteamID);

                // Visual/sound marker at the logout spot (e.g. a flare on the combat-log box).
                TriggerEffect(cfg.DownedLogoutEffectId, pos);

                // Box their loot BEFORE the imminent disconnect save. Emptying the grid now
                // means the save writes nothing -> the reconnecting character can't dupe it.
                if (cfg.DownedLogoutDropBox && _deathBoxes != null)
                {
                    List<Item> loot = SnapshotAndClearGrid(p);
                    if (loot.Count > 0)
                        _deathBoxes.SpawnLootBox(pos, loot);
                }

                // Mark them dead-on-reconnect (persisted so a restart can't wash it out).
                if (cfg.DownedLogoutKillOnReconnect && _pendingDeaths.Add(id.m_SteamID))
                    SavePendingDeaths();

                Logger.Log("Knockdown: " + id.m_SteamID +
                           " logged out while downed -> loot boxed, marked dead on reconnect.");
            }
            catch (System.Exception ex)
            {
                Logger.LogException(ex, "Knockdown: HandleDownedLogout failed for " + id.m_SteamID);
            }
        }

        /// <summary>If the connecting player logged out while downed, kill them now.</summary>
        private void OnPlayerConnected(UnturnedPlayer player)
        {
            if (player == null || player.Player == null) return;
            ulong sid = player.CSteamID.m_SteamID;
            if (!_pendingDeaths.Remove(sid))
                return;
            SavePendingDeaths();

            KnockdownConfiguration cfg = Configuration.Instance;
            try
            {
                Player p = player.Player;
                if (p.life != null && !p.life.isDead)
                {
                    // When their loot was boxed at logout, drop any items that survived via the
                    // disconnect save (in case the save ran before the logout grid-clear) so this
                    // death drops nothing extra and the box stays the only copy (no dupe).
                    if (cfg.DownedLogoutDropBox)
                        SnapshotAndClearGrid(p);

                    Msg(p, cfg.MessageDownedLogoutDeath);
                    p.life.askDamage(byte.MaxValue, Vector3.up * 16f, EDeathCause.SUICIDE,
                                     ELimb.SKULL, CSteamID.Nil, out EPlayerKill _);
                }
                Logger.Log("Knockdown: " + sid + " reconnected after a downed logout -> killed.");
            }
            catch (System.Exception ex)
            {
                Logger.LogException(ex, "Knockdown: OnPlayerConnected kill failed for " + sid);
            }
        }

        /// <summary>
        /// Pull every grid item (pages 0..STORAGE-1: held + clothing pockets, NOT the worn
        /// clothing itself) off the player and return them, clearing each slot as we go.
        /// </summary>
        private static List<Item> SnapshotAndClearGrid(Player p)
        {
            List<Item> items = new List<Item>();
            if (p == null || p.inventory == null) return items;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                Items grid = p.inventory.items[page];
                if (grid == null) continue;
                byte count = grid.getItemCount();
                for (int i = count - 1; i >= 0; i--)
                {
                    ItemJar jar = grid.getItem((byte)i);
                    if (jar != null && jar.item != null) items.Add(jar.item);
                    try { grid.removeItem((byte)i); }
                    catch (System.Exception ex) { Logger.LogException(ex, "Knockdown: removeItem failed"); }
                }
            }
            return items;
        }

        /// <summary>Deferred death-box orphan sweep: fires once the level has finished loading.</summary>
        private void OnLevelLoadedSweep(int level)
        {
            Level.onLevelLoaded -= OnLevelLoadedSweep;
            if (_deathBoxes != null) _deathBoxes.SweepOrphans();
        }

        /// <summary>Manual Harmony patch for the death-box deposit block (graceful fallback).</summary>
        private void TryPatchDeathBoxDepositBlock()
        {
            string[] candidates = { "ReceiveDragItem", "ReceiveSwapItem" };
            System.Reflection.MethodInfo prefix = typeof(DeathBoxDepositGuard).GetMethod(
                nameof(DeathBoxDepositGuard.Prefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

            foreach (string name in candidates)
            {
                try
                {
                    System.Reflection.MethodInfo target = AccessTools.Method(typeof(PlayerInventory), name);
                    if (target == null)
                    {
                        Logger.LogWarning("Knockdown: deposit-block: " + name + " not found (skipped).");
                        continue;
                    }
                    _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                    Logger.Log("Knockdown: death-box deposit-block patched: " + name);
                }
                catch (System.Exception ex)
                {
                    Logger.LogException(ex, "Knockdown: deposit-block patch failed: " + name);
                }
            }
        }

        // ---- pending downed-logout deaths (plain-text store, like the opt-out list) ----
        private static string PendingDeathsFilePath
        {
            get
            {
                string dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                return System.IO.Path.Combine(dir, "Knockdown.pendingdeaths.txt");
            }
        }

        private void LoadPendingDeaths()
        {
            _pendingDeaths.Clear();
            try
            {
                string path = PendingDeathsFilePath;
                if (!System.IO.File.Exists(path))
                    return;
                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                        continue;
                    if (ulong.TryParse(trimmed, out ulong sid))
                        _pendingDeaths.Add(sid);
                }
                if (_pendingDeaths.Count > 0)
                    Logger.Log("Knockdown: loaded " + _pendingDeaths.Count + " pending downed-logout death(s).");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Knockdown: failed to load pending deaths: " + ex.Message);
            }
        }

        private void SavePendingDeaths()
        {
            try
            {
                List<string> lines = new List<string>(_pendingDeaths.Count);
                foreach (ulong sid in _pendingDeaths)
                    lines.Add(sid.ToString());
                System.IO.File.WriteAllLines(PendingDeathsFilePath, lines);
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Knockdown: failed to save pending deaths: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  On-screen killfeed (top-right Effect UI, broadcast to everyone)
        // -----------------------------------------------------------------
        private const short KillfeedKey = 30024;
        private readonly List<KillfeedEntry> _feed = new List<KillfeedEntry>();
        private readonly HashSet<ulong> _feedSpawned = new HashSet<ulong>();
        private float _feedTick;

        private sealed class KillfeedEntry { public string Text; public string Category; public float Expiry; }

        // Cause categories (MUST match KillfeedCats in BuildReviveUI.cs). One icon per line.
        private static readonly string[] KillfeedCats =
            { "downed", "gun", "melee", "punch", "car", "zombie", "blood", "skull", "biohazard", "droplet", "meat" };

        private static string CauseCategory(EDeathCause c)
        {
            switch (c)
            {
                case EDeathCause.GUN: return "gun";
                case EDeathCause.MELEE: case EDeathCause.SHRED: case EDeathCause.BOULDER: return "melee";
                case EDeathCause.PUNCH: return "punch";
                case EDeathCause.ROADKILL: case EDeathCause.VEHICLE: return "car";
                case EDeathCause.ZOMBIE: case EDeathCause.ANIMAL: return "zombie";
                case EDeathCause.BLEEDING: return "blood";
                case EDeathCause.INFECTION: return "biohazard";
                case EDeathCause.WATER: case EDeathCause.FREEZING: return "droplet";
                case EDeathCause.FOOD: return "meat";
                default: return "skull";
            }
        }

        // A player died (any cause): red "Killer [weapon] Victim", or "Victim died (cause)".
        private void OnPlayerDeathFeed(UnturnedPlayer player, EDeathCause cause, ELimb limb, CSteamID murderer)
        {
            if (player == null) return;
            _feedSpawned.Remove(player.CSteamID.m_SteamID); // their UI clears on death; re-spawn on revive
            KillfeedKill(murderer, player.CSteamID, cause);
        }

        // Player respawned: their UI effect was cleared, so re-spawn the killfeed and show the feed.
        private void OnPlayerReviveFeed(UnturnedPlayer player, Vector3 position, byte angle)
        {
            if (player?.Player != null) { _feedSpawned.Remove(player.CSteamID.m_SteamID); SpawnKillfeedFor(player.Player); }
        }

        // A player was knocked down: gold "Killer [weapon] Victim DOWN".
        private void KillfeedDowned(CSteamID killer, CSteamID victim, EDeathCause cause)
        {
            var cfg = Configuration.Instance;
            if (!cfg.EnableKillfeed || cfg.KillfeedEffectID == 0) return;
            string v = RichEscape(CharName(victim));
            if (!IsRealPlayer(killer) || killer == victim)
            {
                // zombie / animal / fall / etc. have no steam killer -> show the CAUSE so you know how
                AddKillfeed("<color=#EBB94A>" + v + "</color> <color=#9aa1ad>went down (" + CauseWord(cause) + ")</color>", "downed");
                return;
            }
            string k = RichEscape(CharName(killer));
            AddKillfeed("<color=#EBB94A><b>" + k + "</b></color>  <color=#9aa1ad>downed</color>  <color=#EBB94A>" + v + "</color>" + DistanceTag(killer, victim), "downed");
        }

        private void KillfeedKill(CSteamID killer, CSteamID victim, EDeathCause cause)
        {
            var cfg = Configuration.Instance;
            if (!cfg.EnableKillfeed || cfg.KillfeedEffectID == 0) return;
            string v = RichEscape(CharName(victim));
            if (!IsRealPlayer(killer) || killer == victim)
            {
                // unresolved / non-player killer (zombie, fall, suicide) -> show the cause, not "?"
                AddKillfeed("<color=#9aa1ad>" + v + " died (" + CauseWord(cause) + ")</color>", CauseCategory(cause));
                return;
            }
            string k = RichEscape(CharName(killer));
            AddKillfeed("<color=#EF6A6A><b>" + k + "</b></color>  <color=#EF6A6A>" + v + "</color>" + DistanceTag(killer, victim), CauseCategory(cause));
        }

        // A real, resolvable player (so non-player killers like zombies fall back to a cause line).
        private static bool IsRealPlayer(CSteamID id)
        {
            return id != CSteamID.Nil && PlayerTool.getSteamPlayer(id) != null;
        }

        private int _demoIdx;
        /// <summary>/kftest helper: add one demo line, cycling categories so repeated calls show
        /// every icon + colour without needing real kills.</summary>
        public void KillfeedDemo(Player caller)
        {
            if (caller == null) return;
            string name = RichEscape(caller.channel.owner.playerID.characterName);
            string cat = KillfeedCats[_demoIdx % KillfeedCats.Length];
            _demoIdx++;
            if (cat == "downed")
                AddKillfeed("<color=#EBB94A><b>" + name + "</b></color>  <color=#9aa1ad>downed</color>  <color=#EBB94A>Target</color>  <color=#6b7280>12m</color>", "downed");
            else
                AddKillfeed("<color=#EF6A6A><b>" + name + "</b></color>  <color=#EF6A6A>Target</color>  <color=#6b7280>" + (8 + _demoIdx * 4) + "m</color>  <color=#9aa1ad>" + cat + "</color>", cat);
        }

        // " 14m" between killer and victim (empty if either position is unavailable).
        private static string DistanceTag(CSteamID a, CSteamID b)
        {
            Player pa = PlayerTool.getPlayer(a);
            Player pb = PlayerTool.getPlayer(b);
            if (pa == null || pb == null) return "";
            int d = Mathf.RoundToInt(Vector3.Distance(pa.transform.position, pb.transform.position));
            return "  <color=#6b7280>" + d + "m</color>";
        }

        private void AddKillfeed(string text, string category)
        {
            var cfg = Configuration.Instance;
            _feed.Insert(0, new KillfeedEntry { Text = text, Category = category, Expiry = Time.realtimeSinceStartup + Mathf.Max(1f, cfg.KillfeedDurationSeconds) });
            int max = Mathf.Clamp(cfg.KillfeedMaxLines, 1, 5);
            while (_feed.Count > max) _feed.RemoveAt(_feed.Count - 1);
            BroadcastKillfeed();
        }

        private void TickKillfeed(float dt)
        {
            if (_feed.Count == 0) return;
            _feedTick += dt;
            if (_feedTick < 0.4f) return;
            _feedTick = 0f;
            float now = Time.realtimeSinceStartup;
            bool changed = false;
            for (int i = _feed.Count - 1; i >= 0; i--)
                if (_feed[i].Expiry <= now) { _feed.RemoveAt(i); changed = true; }
            if (changed) BroadcastKillfeed();
        }

        private void BroadcastKillfeed()
        {
            var cfg = Configuration.Instance;
            if (cfg.KillfeedEffectID == 0) return;
            int max = Mathf.Clamp(cfg.KillfeedMaxLines, 1, 5);
            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp == null) continue;
                ITransportConnection tc = sp.transportConnection;
                if (_feedSpawned.Add(sp.playerID.steamID.m_SteamID))
                    EffectManager.sendUIEffect(cfg.KillfeedEffectID, KillfeedKey, tc, true);
                WriteFeedLines(tc, max);
            }
        }

        private void SpawnKillfeedFor(Player player)
        {
            var cfg = Configuration.Instance;
            if (!cfg.EnableKillfeed || cfg.KillfeedEffectID == 0 || player?.channel?.owner == null) return;
            ITransportConnection tc = player.channel.owner.transportConnection;
            EffectManager.sendUIEffect(cfg.KillfeedEffectID, KillfeedKey, tc, true);
            _feedSpawned.Add(player.channel.owner.playerID.steamID.m_SteamID);
            WriteFeedLines(tc, Mathf.Clamp(cfg.KillfeedMaxLines, 1, 5));
        }

        private void WriteFeedLines(ITransportConnection tc, int max)
        {
            for (int i = 0; i < max; i++)
            {
                bool active = i < _feed.Count;
                EffectManager.sendUIEffectVisibility(KillfeedKey, tc, true, "KLine_" + i, active); // whole row (icon + text)
                if (!active) continue;
                string cat = _feed[i].Category;
                EffectManager.sendUIEffectText(KillfeedKey, tc, true, "Kill_" + i, _feed[i].Text);
                foreach (string c in KillfeedCats) // reveal exactly the matching category icon
                    EffectManager.sendUIEffectVisibility(KillfeedKey, tc, true, "KIco_" + i + "_" + c, c == cat);
            }
        }

        private static string CharName(CSteamID id)
        {
            SteamPlayer sp = PlayerTool.getSteamPlayer(id);
            return sp != null ? sp.playerID.characterName : "?";
        }

        private static string CauseWord(EDeathCause c)
        {
            switch (c)
            {
                case EDeathCause.GUN: return "Gun";
                case EDeathCause.MELEE: return "Melee";
                case EDeathCause.PUNCH: return "Fists";
                case EDeathCause.ROADKILL: return "Roadkill";
                case EDeathCause.VEHICLE: return "Vehicle";
                case EDeathCause.GRENADE: return "Grenade";
                case EDeathCause.LANDMINE: return "Landmine";
                case EDeathCause.SHRED: return "Barbed Wire";
                case EDeathCause.CHARGE: return "Charge";
                case EDeathCause.MISSILE: return "Missile";
                case EDeathCause.SENTRY: return "Sentry";
                case EDeathCause.SPARK: return "Shock";
                case EDeathCause.BURNER: return "Fire";
                case EDeathCause.ACID: return "Acid";
                case EDeathCause.SPIT: return "Acid";
                case EDeathCause.SPLASH: return "Splash";
                case EDeathCause.BOULDER: return "Boulder";
                case EDeathCause.ZOMBIE: return "Zombie";
                case EDeathCause.ANIMAL: return "Animal";
                case EDeathCause.BLEEDING: return "Bleeding";
                case EDeathCause.BONES: return "Fall";
                case EDeathCause.FREEZING: return "Cold";
                case EDeathCause.BURNING: return "Burning";
                case EDeathCause.FOOD: return "Hunger";
                case EDeathCause.WATER: return "Thirst";
                case EDeathCause.INFECTION: return "Infection";
                case EDeathCause.BREATH: return "Suffocation";
                case EDeathCause.ARENA: return "Arena";
                default: return "Killed";
            }
        }

        // Player names can contain '<' / '>' which would break the rich-text tags - strip them.
        private static string RichEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Replace("<", "").Replace(">", "");
        }

        // -----------------------------------------------------------------
        //  Per-player opt-out (enable/disable the system for yourself)
        // -----------------------------------------------------------------

        /// <summary>True if the player has opted their character out of the knockdown system.</summary>
        public bool IsOptedOut(CSteamID id)
        {
            return _optedOut.Contains(id.m_SteamID);
        }

        /// <summary>
        /// Records a player's opt-out preference and persists it. When opting OUT while the
        /// player is currently downed, they are killed immediately (they can't keep crawling
        /// under a system they just disabled). Returns true if the stored set actually changed.
        /// </summary>
        public bool SetOptOut(CSteamID id, bool optOut)
        {
            bool changed = optOut ? _optedOut.Add(id.m_SteamID) : _optedOut.Remove(id.m_SteamID);

            // Disabling mid-knockdown: finish them off rather than leave them downed.
            if (optOut && _downed.TryGetValue(id, out DownedState state))
                KillDowned(state);

            if (changed)
                SaveOptOut();

            return changed;
        }

        /// <summary>Absolute path of the opt-out store, next to the plugin DLL / its config.</summary>
        private static string OptOutFilePath
        {
            get
            {
                string dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                return System.IO.Path.Combine(dir, "Knockdown.optout.txt");
            }
        }

        // The store is a plain text file (one SteamID per line) rather than XML on purpose:
        // using XmlSerializer here makes the CLR probe for a non-existent
        // "Knockdown.XmlSerializers" assembly, which RocketMod logs as a noisy
        // "could not find dependency" line on every load/save.

        private void LoadOptOut()
        {
            _optedOut.Clear();
            try
            {
                string path = OptOutFilePath;
                if (!System.IO.File.Exists(path))
                    return;

                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                        continue; // allow blank lines and # comments
                    if (ulong.TryParse(trimmed, out ulong sid))
                        _optedOut.Add(sid);
                }
                Logger.Log("Knockdown: loaded " + _optedOut.Count + " opted-out player(s).");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Knockdown: failed to load opt-out list: " + ex.Message);
            }
        }

        private void SaveOptOut()
        {
            try
            {
                List<string> lines = new List<string>(_optedOut.Count);
                foreach (ulong sid in _optedOut)
                    lines.Add(sid.ToString());
                System.IO.File.WriteAllLines(OptOutFilePath, lines);
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Knockdown: failed to save opt-out list: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------

        /// <summary>True for player/combat damage; false for environmental drain.</summary>
        private static bool IsCombatCause(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.BLEEDING:
                case EDeathCause.BONES:
                case EDeathCause.FREEZING:
                case EDeathCause.BURNING:
                case EDeathCause.FOOD:
                case EDeathCause.WATER:
                case EDeathCause.INFECTION:
                case EDeathCause.BREATH:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Advances each queued flare and triggers the effect at the next step(s) it has passed
        /// since the previous tick. Removes finished flares.
        /// </summary>
        private void TickFlares(float dt, KnockdownConfiguration cfg)
        {
            if (_flares.Count == 0 || cfg.KnockFlareEffectID == 0)
                return;

            float riseDuration = cfg.KnockFlareDuration > 0f ? cfg.KnockFlareDuration : 1.5f;
            int steps = cfg.KnockFlareSteps > 0 ? cfg.KnockFlareSteps : 10;
            float height = cfg.KnockFlareHeight;
            float hangDuration = Mathf.Max(0f, cfg.KnockFlareHangDuration);
            float hangInterval = cfg.KnockFlareHangInterval > 0f ? cfg.KnockFlareHangInterval : 0.3f;

            for (int i = _flares.Count - 1; i >= 0; i--)
            {
                FlareAnim f = _flares[i];
                f.Elapsed += dt;

                // --- Rise phase ---
                if (f.Elapsed < riseDuration)
                {
                    float frac = f.Elapsed / riseDuration;
                    int reachedStep = Mathf.FloorToInt(frac * steps);
                    while (f.LastStep < reachedStep && f.LastStep < steps)
                    {
                        f.LastStep++;
                        float t = (float)f.LastStep / steps;
                        Vector3 pos = f.Start + new Vector3(0f, height * t, 0f);
                        TriggerEffect(cfg.KnockFlareEffectID, pos);
                    }
                    continue;
                }

                // --- Hang phase: re-trigger at the peak so the flare lingers in the sky.
                // If a ring radius is configured, emit a ring of points around the peak instead. ---
                Vector3 peak = f.Start + new Vector3(0f, height, 0f);
                float ringRadius = cfg.KnockFlareHangRingRadius;
                int ringPoints = cfg.KnockFlareHangRingPoints;
                bool useRing = ringRadius > 0f && ringPoints > 0;

                // Ensure the first hang burst fires exactly once when rise just ended.
                if (f.LastStep < steps)
                {
                    f.LastStep = steps;
                    if (useRing)
                        TriggerRingEffect(cfg.KnockFlareEffectID, peak, ringRadius, ringPoints);
                    else
                        TriggerEffect(cfg.KnockFlareEffectID, peak);
                }

                f.HangElapsed += dt;
                f.HangAccumulator += dt;
                if (f.HangAccumulator >= hangInterval)
                {
                    f.HangAccumulator = 0f;
                    if (useRing)
                        TriggerRingEffect(cfg.KnockFlareEffectID, peak, ringRadius, ringPoints);
                    else
                        TriggerEffect(cfg.KnockFlareEffectID, peak);
                }

                if (f.HangElapsed >= hangDuration)
                    _flares.RemoveAt(i);
            }
        }

        /// <summary>Emits the same effect at N points evenly distributed around a horizontal ring.</summary>
        private static void TriggerRingEffect(ushort effectId, Vector3 center, float radius, int points)
        {
            EffectAsset asset = Assets.find(EAssetType.EFFECT, effectId) as EffectAsset;
            if (asset == null)
                return;

            float twoPi = Mathf.PI * 2f;

            for (int i = 0; i < points; i++)
            {
                float a = twoPi * i / points;
                Vector3 pos = center + new Vector3(radius * Mathf.Cos(a), 0f, radius * Mathf.Sin(a));
                TriggerEffectParameters parameters = new TriggerEffectParameters(asset)
                {
                    position = pos,
                    relevantDistance = 64f,
                    reliable = false // decorative; OK to drop on packet loss
                };
                EffectManager.triggerEffect(parameters);
            }
        }

        // -----------------------------------------------------------------
        //  Center-screen revive HUD (custom UI EffectAsset)
        // -----------------------------------------------------------------
        // The UI key is a per-player UI-effect instance id; one revive HUD per player at a time.
        private const short ReviveUIKey = 30021;

        // Number of fill segments in the graphical progress bar. MUST match the prefab generator
        // (ReviveBarSegments in BuildReviveUI.cs). The server reveals Fill_0..Fill_{n-1} via visibility.
        private const int ReviveBarSegments = 30;

        private static ITransportConnection Tc(CSteamID id)
        {
            Player p = PlayerTool.getPlayer(id);
            return p?.channel?.owner?.transportConnection;
        }

        /// <summary>Spawn the revive HUD on a player (no plugin-modal: it's a passive overlay, never grabs the cursor).</summary>
        private void ShowReviveUI(CSteamID who)
        {
            ushort id = Configuration.Instance.ReviveUIEffectID;
            if (id == 0) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.sendUIEffect(id, ReviveUIKey, tc, true);
        }

        /// <summary>Update the HUD's Title + Bar text for one player.</summary>
        private void UpdateReviveUI(CSteamID who, string title, string bar)
        {
            if (Configuration.Instance.ReviveUIEffectID == 0) return;
            ITransportConnection tc = Tc(who);
            if (tc == null) return;
            EffectManager.sendUIEffectText(ReviveUIKey, tc, true, "Title", title);
            EffectManager.sendUIEffectText(ReviveUIKey, tc, true, "Bar", bar);
        }

        /// <summary>Remove the revive HUD from a player.</summary>
        private void ClearReviveUI(CSteamID who)
        {
            ushort id = Configuration.Instance.ReviveUIEffectID;
            if (id == 0 || who == CSteamID.Nil) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.askEffectClearByID(id, tc);
        }

        /// <summary>How many bar segments should be lit at the given progress.</summary>
        private static int FillCount(float progress, float duration)
        {
            float frac = duration > 0f ? Mathf.Clamp01(progress / duration) : 0f;
            return Mathf.Clamp(Mathf.RoundToInt(frac * ReviveBarSegments), 0, ReviveBarSegments);
        }

        /// <summary>The "45%  4s" label shown over the bar.</summary>
        private static string Pct(float progress, float duration)
        {
            float frac = duration > 0f ? Mathf.Clamp01(progress / duration) : 0f;
            int secsLeft = Mathf.CeilToInt(Mathf.Max(0f, duration - progress));
            return Mathf.RoundToInt(frac * 100f) + "%  " + secsLeft + "s";
        }

        /// <summary>Reveal/hide the bar's green segments for a player as the fill count changes.</summary>
        private void RevealBar(CSteamID who, int oldFill, int newFill)
        {
            if (Configuration.Instance.ReviveUIEffectID == 0 || oldFill == newFill) return;
            ITransportConnection tc = Tc(who);
            if (tc == null) return;
            if (newFill > oldFill)
                for (int i = oldFill; i < newFill; i++)
                    EffectManager.sendUIEffectVisibility(ReviveUIKey, tc, true, "Fill_" + i, true);
            else
                for (int i = newFill; i < oldFill; i++)
                    EffectManager.sendUIEffectVisibility(ReviveUIKey, tc, true, "Fill_" + i, false);
        }

        /// <summary>Updates only the "Bar" percent label (cheaper than resending the title each tick).</summary>
        private void UpdateRevivePercent(CSteamID who, string text)
        {
            if (Configuration.Instance.ReviveUIEffectID == 0) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.sendUIEffectText(ReviveUIKey, tc, true, "Bar", text);
        }

        /// <summary>
        /// Test helper for "/testreviveui": show the revive HUD on the player and animate the bar
        /// 0 -> 100% once (over ReviveDuration), then clear it. No real downed/reviver needed, so
        /// the UI can be verified solo. Returns false if the HUD effect isn't configured.
        /// </summary>
        public bool StartReviveUIDemo(Player player)
        {
            if (player?.channel?.owner == null) return false;
            if (Configuration.Instance.ReviveUIEffectID == 0) return false;

            CSteamID id = player.channel.owner.playerID.steamID;
            _uiDemos.RemoveAll(d => d.Id == id); // restart if already previewing
            ShowReviveUI(id);
            UpdateReviveUI(id, "REVIVE HUD TEST", Pct(0f, Configuration.Instance.ReviveDuration));
            _uiDemos.Add(new UIDemo { Id = id, Elapsed = 0f, Accum = 0f, BarFill = 0 });
            return true;
        }

        /// <summary>Advances the "/testreviveui" HUD previews; throttled to ~0.15s between text updates.</summary>
        private void TickUIDemos(float dt, KnockdownConfiguration cfg)
        {
            if (_uiDemos.Count == 0) return;
            for (int i = _uiDemos.Count - 1; i >= 0; i--)
            {
                UIDemo d = _uiDemos[i];
                d.Elapsed += dt;
                d.Accum += dt;
                bool done = d.Elapsed >= cfg.ReviveDuration;
                if (d.Accum >= 0.15f || done)
                {
                    d.Accum = 0f;
                    float p = Mathf.Min(d.Elapsed, cfg.ReviveDuration);
                    int newFill = FillCount(p, cfg.ReviveDuration);
                    if (newFill != d.BarFill) { RevealBar(d.Id, d.BarFill, newFill); d.BarFill = newFill; }
                    UpdateRevivePercent(d.Id, Pct(p, cfg.ReviveDuration));
                }
                if (done)
                {
                    ClearReviveUI(d.Id);
                    _uiDemos.RemoveAt(i);
                }
            }
        }

        // -----------------------------------------------------------------
        //  "How to revive" hint HUD (second UI effect, icons + instruction)
        // -----------------------------------------------------------------
        private const short ReviveHintKey = 30022;

        private void ShowHint(CSteamID who)
        {
            ushort id = Configuration.Instance.ReviveHintEffectID;
            if (id == 0) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.sendUIEffect(id, ReviveHintKey, tc, true);
        }

        private void UpdateHintText(CSteamID who, string text)
        {
            if (Configuration.Instance.ReviveHintEffectID == 0) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.sendUIEffectText(ReviveHintKey, tc, true, "Hint", text);
        }

        private void ClearHint(CSteamID who)
        {
            ushort id = Configuration.Instance.ReviveHintEffectID;
            if (id == 0 || who == CSteamID.Nil) return;
            ITransportConnection tc = Tc(who);
            if (tc != null)
                EffectManager.askEffectClearByID(id, tc);
        }

        /// <summary>
        /// Show the "how to revive" hint to (a) downed players who aren't being revived yet, and
        /// (b) any non-downed teammate standing within ReviveDistance of a downed player. Recomputed
        /// on a throttle and diffed against what's currently shown so it appears/disappears cleanly.
        /// </summary>
        private void TickHints(float dt, KnockdownConfiguration cfg)
        {
            if (cfg.ReviveHintEffectID == 0)
            {
                if (_hintShown.Count > 0) ClearAllHints();
                return;
            }

            _hintAccum += dt;
            if (_hintAccum < 0.4f) return;
            _hintAccum = 0f;

            _hintDesired.Clear();
            float rangeSqr = cfg.ReviveDistance * cfg.ReviveDistance;

            foreach (DownedState st in _downed.Values)
            {
                Player dp = st.Player?.Player;
                if (dp == null || dp.life == null || dp.life.isDead) continue;

                // Downed player: show the "wait for a crouch" hint unless they're already being revived.
                if (st.ReviverId == CSteamID.Nil)
                    _hintDesired[st.Id.m_SteamID] = cfg.ReviveHintDowned;

                // Nearby standing teammates who could revive -> show them the "crouch to revive" hint.
                Vector3 dpos = dp.transform.position;
                foreach (SteamPlayer client in Provider.clients)
                {
                    Player cp = client.player;
                    if (cp == null) continue;
                    CSteamID cid = client.playerID.steamID;
                    if (cid == st.Id) continue;
                    if (_downed.ContainsKey(cid)) continue;       // a downed player can't revive
                    if (st.ReviverId == cid) continue;            // the active reviver sees the progress HUD
                    if ((cp.transform.position - dpos).sqrMagnitude > rangeSqr) continue;
                    if (!_hintDesired.ContainsKey(cid.m_SteamID)) // don't clobber a downed-instruction entry
                        _hintDesired[cid.m_SteamID] = cfg.ReviveHintReviver;
                }
            }

            // Show/refresh desired hints.
            foreach (KeyValuePair<ulong, string> kv in _hintDesired)
            {
                CSteamID id = new CSteamID(kv.Key);
                if (_hintShown.Add(kv.Key)) ShowHint(id);
                UpdateHintText(id, kv.Value);
            }

            // Clear hints no longer desired.
            _hintScratch.Clear();
            _hintScratch.AddRange(_hintShown);
            foreach (ulong id in _hintScratch)
                if (!_hintDesired.ContainsKey(id))
                {
                    ClearHint(new CSteamID(id));
                    _hintShown.Remove(id);
                }
        }

        private void ClearAllHints()
        {
            foreach (ulong id in _hintShown)
                ClearHint(new CSteamID(id));
            _hintShown.Clear();
        }

        private static void TriggerEffect(ushort effectId, Vector3 position)
        {
            if (effectId == 0)
                return;

            // Resolve the asset by id and use the asset-based ctor (the ushort ctor
            // is obsolete in this Unturned build).
            EffectAsset asset = Assets.find(EAssetType.EFFECT, effectId) as EffectAsset;
            if (asset == null)
            {
                Logger.LogWarning("Knockdown: effect id " + effectId + " not found.");
                return;
            }

            TriggerEffectParameters parameters = new TriggerEffectParameters(asset)
            {
                position = position,
                relevantDistance = 64f, // broadcast to players within 64m of the event
                reliable = true
            };
            EffectManager.triggerEffect(parameters);
        }

        private static bool HasText(Message msg)
        {
            return msg != null && !string.IsNullOrEmpty(msg.Text);
        }

        /// <summary>Sends a configured message (uses its Text and Color).</summary>
        private static void Msg(Player player, Message msg)
        {
            if (HasText(msg))
                Msg(player, msg, msg.Text);
        }

        /// <summary>Sends a message using <paramref name="text"/> but the colour from <paramref name="msg"/>.</summary>
        private static void Msg(Player player, Message msg, string text)
        {
            if (player == null || msg == null || string.IsNullOrEmpty(text))
                return;
            Color color = UnturnedChat.GetColorFromName(msg.Color, Color.white);
            // serverSendMessage with useRichTextFormatting:true so inline TMP
            // tags (<color=...>, <b>, <size=...>) in Text render per-segment.
            // The msg.Color above is the base/fallback colour for untagged text.
            ChatManager.serverSendMessage(
                text, color,
                fromPlayer: null,
                toPlayer: player.channel.owner,
                mode: EChatMode.SAY,
                iconURL: null,
                useRichTextFormatting: true);
        }

        /// <summary>Mutable per-player downed record.</summary>
        private sealed class DownedState
        {
            public CSteamID Id;
            public UnturnedPlayer Player;
            public float Elapsed;
            public float ReapplyAccumulator;
            public EDeathCause DeathCause;
            public CSteamID KillerId;
            public CSteamID ReviverId;
            public float ReviveProgress;
            public float ProgressTickAccumulator;
            public float ProgressMessageAccumulator;
            public float HpMessageAccumulator;
            public float RangeEffectAccumulator;
            /// <summary>Time accumulator for the repeating blood-splatter burst while downed.</summary>
            public float BloodAccumulator;

            /// <summary>Player currently shown the reviver-side HUD (so we can clear it if they change/leave).</summary>
            public CSteamID UIReviverId;
            /// <summary>Throttle accumulator for HUD text updates.</summary>
            public float UIAccumulator;
            /// <summary>Seconds remaining to keep showing the "revive cancelled" HUD flash before clearing it.</summary>
            public float UICancelFlash;
            /// <summary>How many progress-bar segments are currently lit (so we only toggle the delta).</summary>
            public int UIBarFill;
        }

        /// <summary>One active "flare rising into the sky" animation.</summary>
        private sealed class FlareAnim
        {
            public Vector3 Start;
            public float Elapsed;
            public int LastStep;
            public float HangElapsed;
            public float HangAccumulator;
        }

        /// <summary>One active "/testreviveui" HUD preview.</summary>
        private sealed class UIDemo
        {
            public CSteamID Id;
            public float Elapsed;
            public float Accum;
            public int BarFill;
        }
    }

    // Harmony: block RocketMod command execution (e.g. /home, /kit) for downed players so they
    // can't escape the downed state. Console + non-downed players pass through untouched.
    [HarmonyPatch(typeof(Rocket.Core.Commands.RocketCommandManager), "Execute")]
    internal static class BlockDownedCommandsPatch
    {
        private static bool Prefix(IRocketPlayer player)
        {
            try
            {
                // Harmony is now always applied (for the disconnect/loot patches), so honour the
                // config toggle here rather than by skipping the patch at load time.
                Knockdown inst = Knockdown.Instance;
                if (inst != null && !inst.Configuration.Instance.BlockCommandsWhileDowned)
                    return true;

                if (player is UnturnedPlayer up && Knockdown.IsDowned(up.CSteamID))
                {
                    Knockdown.TellNoCommandWhileDowned(up.Player);
                    return false; // skip the original command execution
                }
            }
            catch { /* never break command handling */ }
            return true;
        }
    }
}
