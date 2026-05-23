using System.Collections.Generic;
using Rocket.API;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
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

        /// <summary>Active downed players keyed by SteamID.</summary>
        private readonly Dictionary<CSteamID, DownedState> _downed = new Dictionary<CSteamID, DownedState>();

        /// <summary>Reused scratch buffer so FixedUpdate allocates nothing per tick.</summary>
        private readonly List<DownedState> _scratch = new List<DownedState>();

        // -----------------------------------------------------------------
        //  Plugin lifecycle
        // -----------------------------------------------------------------

        protected override void Load()
        {
            Instance = this;

            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            U.Events.OnPlayerDisconnected += OnPlayerDisconnected;

            Logger.Log("Knockdown loaded. KnockDuration=" + Configuration.Instance.KnockDuration +
                       "s, ReviveDuration=" + Configuration.Instance.ReviveDuration + "s.");
        }

        protected override void Unload()
        {
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;
            U.Events.OnPlayerDisconnected -= OnPlayerDisconnected;

            // Tear down every active downed state so no event handlers or speed
            // multipliers leak when the plugin reloads.
            _scratch.Clear();
            _scratch.AddRange(_downed.Values);
            foreach (DownedState state in _scratch)
                Teardown(state);
            _downed.Clear();
            _scratch.Clear();

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

            // --- Not downed: would this hit kill them? ---
            // Note: this compares raw incoming damage (damage * times) against
            // current health. It deliberately ignores armour mitigation, which is
            // the standard approach for downed-state plugins; worst case a player
            // is downed a fraction early rather than dying outright.
            float incoming = parameters.damage * parameters.times;
            if (incoming >= player.life.health)
            {
                shouldAllow = false; // prevent the death
                EnterKnockdown(player, id, parameters.cause, parameters.killer);
            }
        }

        // -----------------------------------------------------------------
        //  Enter / exit knockdown
        // -----------------------------------------------------------------

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
                ReapplyAccumulator = 0f
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
            Msg(player, cfg.MessageKnocked);
        }

        /// <summary>Restores movement and removes the equip block. Does not touch the dictionary.</summary>
        private void Teardown(DownedState state)
        {
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

        private void Revive(DownedState state)
        {
            Player player = state.Player?.Player;
            KnockdownConfiguration cfg = Configuration.Instance;

            Teardown(state);
            _downed.Remove(state.Id);

            if (player == null || player.life == null || player.life.isDead)
                return;

            // Revived players always come back at exactly ReviveHealth, regardless of
            // how much HP they had left when revived.
            player.life.serverModifyHealth(cfg.ReviveHealth - player.life.health);
            player.life.askHeal(0, true, true);

            TriggerEffect(cfg.ReviveEffectID, player.transform.position);
            Msg(player, cfg.MessageRevived);
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
            if (_downed.Count == 0)
                return;

            float dt = Time.fixedDeltaTime;
            KnockdownConfiguration cfg = Configuration.Instance;

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

                // Once a second: re-assert crawl speed / pose, and show the downed player
                // their bleeding HP (skipped while being revived - the revive message shows instead).
                state.ReapplyAccumulator += dt;
                if (state.ReapplyAccumulator >= 1f)
                {
                    state.ReapplyAccumulator = 0f;
                    ApplyDownedConstraints(player, cfg);
                    if (player.equipment.HasValidUseable)
                        player.equipment.dequip();

                    if (!beingRevived && HasText(cfg.MessageDownedHp))
                    {
                        int secondsLeft = Mathf.Max(0, Mathf.CeilToInt(total - state.Elapsed));
                        string hpText = cfg.MessageDownedHp.Text
                            .Replace("{hp}", player.life.health.ToString())
                            .Replace("{seconds}", secondsLeft.ToString());
                        Msg(player, cfg.MessageDownedHp, hpText);
                    }
                }

                UpdateRevive(state, player, cfg, dt);
            }
        }

        private void UpdateRevive(DownedState state, Player target, KnockdownConfiguration cfg, float dt)
        {
            Vector3 targetPos = target.transform.position;

            // Is the currently-claimed reviver still valid?
            Player reviver = null;
            if (state.ReviverId != CSteamID.Nil)
            {
                Player current = PlayerTool.getPlayer(state.ReviverId);
                if (IsEligibleReviver(current, target, targetPos, cfg))
                    reviver = current;
            }

            // Claim lapsed -> notify and look for a new reviver.
            if (reviver == null)
            {
                if (state.ReviverId != CSteamID.Nil)
                {
                    Msg(target, cfg.MessageReviveCancelled);
                    state.ReviverId = CSteamID.Nil;
                    state.ReviveProgress = 0f;
                    state.ProgressTickAccumulator = 0f;
                }

                foreach (SteamPlayer client in Provider.clients)
                {
                    Player candidate = client.player;
                    if (IsEligibleReviver(candidate, target, targetPos, cfg))
                    {
                        state.ReviverId = client.playerID.steamID;
                        state.ReviveProgress = 0f;
                        state.ProgressTickAccumulator = 0f;
                        reviver = candidate;
                        Msg(reviver, cfg.MessageReviveStarted);
                        Msg(target, cfg.MessageBeingRevived);
                        break;
                    }
                }
            }

            if (reviver == null)
                return;

            state.ReviveProgress += dt;
            if (state.ReviveProgress >= cfg.ReviveDuration)
            {
                Revive(state);
                return;
            }

            // Once-a-second progress feedback in chat + revive sound effect.
            state.ProgressTickAccumulator += dt;
            if (state.ProgressTickAccumulator >= 1f)
            {
                state.ProgressTickAccumulator -= 1f;

                if (HasText(cfg.MessageReviveProgress))
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

                if (cfg.ReviveSoundEffectID != 0)
                    TriggerEffect(cfg.ReviveSoundEffectID, target.transform.position);
            }
        }

        private bool IsEligibleReviver(Player reviver, Player target, Vector3 targetPos, KnockdownConfiguration cfg)
        {
            if (reviver == null || reviver == target)
                return false;
            if (reviver.life == null || reviver.life.isDead)
                return false;
            if (_downed.ContainsKey(reviver.channel.owner.playerID.steamID))
                return false; // a downed player cannot revive
            if ((reviver.transform.position - targetPos).sqrMagnitude > cfg.ReviveDistance * cfg.ReviveDistance)
                return false;
            return IsReviveInputActive(reviver, cfg);
        }

        /// <summary>
        /// Whether the reviver is performing the revive input. CROUCH (default) is fully
        /// server-side and needs no key binding; PLUGINKEY uses a bound Unturned plugin key.
        /// </summary>
        private static bool IsReviveInputActive(Player reviver, KnockdownConfiguration cfg)
        {
            string mode = (cfg.ReviveInput ?? "CROUCH").Trim().ToUpperInvariant();
            if (mode == "PLUGINKEY")
                return reviver.input != null && reviver.input.IsPluginKeyHeld(cfg.RevivePluginKeyIndex);

            // Default: hold crouch (ย่อ) near the downed player.
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
            if (_downed.TryGetValue(player.CSteamID, out DownedState state))
            {
                Teardown(state);
                _downed.Remove(player.CSteamID);
            }
            // Any downed players whose claimed reviver just left are revalidated
            // automatically on the next FixedUpdate (getPlayer returns null).
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
        }
    }
}
