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

        /// <summary>Active "flare rising into the sky" animations, one queued per knockdown.</summary>
        private readonly List<FlareAnim> _flares = new List<FlareAnim>();

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
            _flares.Clear();

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
            QueueKnockFlare(player.transform.position, cfg);
            Msg(player, cfg.MessageKnocked);
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
            float dt = Time.fixedDeltaTime;
            KnockdownConfiguration cfg = Configuration.Instance;

            // Tick the "flare into the sky" animations independently of downed players,
            // so they keep playing even if the player who triggered them is revived/killed.
            TickFlares(dt, cfg);

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
    }
}
