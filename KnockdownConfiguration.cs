using Rocket.API;

namespace Knockdown
{
    /// <summary>
    /// Configuration for the Knockdown plugin. RocketMod serializes the public
    /// fields below to/from <c>Plugins/Knockdown/Knockdown.configuration.xml</c>.
    /// </summary>
    public sealed class KnockdownConfiguration : IRocketPluginConfiguration
    {
        /// <summary>Seconds a player stays downed before dying (timer). Spec default: 60.</summary>
        public float KnockDuration;

        /// <summary>Seconds the plugin key (F) must be held to revive. Spec default: 8.</summary>
        public float ReviveDuration;

        /// <summary>Health restored to a revived player (0-100). Spec default: 25.</summary>
        public byte ReviveHealth;

        /// <summary>Starting health when knocked down (0-100). HP then drains toward 0 over time.</summary>
        public byte KnockHealth;

        /// <summary>
        /// Grace period in seconds at the start of the downed state. During it the player is
        /// fully immune to damage and HP stays at KnockHealth (no draining yet). Default: 3.
        /// </summary>
        public float InvincibleDuration;

        /// <summary>Movement speed multiplier while downed (1.0 = normal). Lower = slower crawl.</summary>
        public float CrawlSpeed;

        /// <summary>Effect asset id played when a player is knocked down. Spec default: 61.</summary>
        public ushort KnockEffectID;

        /// <summary>Effect asset id played when a player is revived. Spec default: 61.</summary>
        public ushort ReviveEffectID;

        /// <summary>
        /// Sound effect asset id played once per second WHILE a revive is in progress.
        /// Default 56 = vanilla "Beep". Set to 0 to disable the revive sound.
        /// </summary>
        public ushort ReviveSoundEffectID;

        // --- Additional values required for a working server-side implementation ---

        /// <summary>Maximum distance (metres) a reviver may be from the downed player.</summary>
        public float ReviveDistance;

        /// <summary>
        /// How a reviver triggers a revive:
        ///   "CROUCH"    - hold crouch (ย่อ) near the downed player. Fully server-side, no key binding needed (default).
        ///   "PLUGINKEY" - hold a bound Unturned plugin key (see RevivePluginKeyIndex).
        /// </summary>
        public string ReviveInput;

        /// <summary>
        /// Pose forced on a downed player (best-effort):
        ///   "SIT" / "REST" - sitting rest gesture (default)
        ///   "CROUCH"       - crouched
        ///   "PRONE"        - lying down
        /// </summary>
        public string DownedPose;

        /// <summary>
        /// Zero-based Unturned "Plugin Key" index the reviver must hold when ReviveInput = "PLUGINKEY".
        /// 0 = "Plugin Key 1" in the in-game Controls menu.
        /// </summary>
        public int RevivePluginKeyIndex;

        /// <summary>
        /// Behaviour AFTER the InvincibleDuration grace period:
        ///   true  - still immune to all damage; the player can only die from HP draining to 0.
        ///   false - combat damage applies normally and can finish the player early; the HP drain
        ///           continues otherwise. (Environmental damage is always ignored while downed.)
        /// </summary>
        public bool InvincibleWhileDowned;

        /// <summary>
        /// If true, the HP bleed-out (and the downtime timer) PAUSES while a revive is in
        /// progress, so the downed player can't bleed out mid-revive. Default: true.
        /// </summary>
        public bool PauseDrainWhileReviving;

        // --- Player-facing messages ---
        public string MessageKnocked;
        public string MessageRevived;
        public string MessageBeingRevived;
        public string MessageReviveCancelled;
        public string MessageReviveStarted;

        /// <summary>
        /// Shown once per second to both reviver and downed player while reviving.
        /// Placeholders: {seconds} = seconds left, {total} = total seconds, {percent} = progress %.
        /// </summary>
        public string MessageReviveProgress;

        /// <summary>
        /// Shown once per second to the downed player while bleeding out (not while being revived).
        /// Placeholders: {hp} = current health, {seconds} = seconds left before death.
        /// </summary>
        public string MessageDownedHp;

        public void LoadDefaults()
        {
            KnockDuration = 60f;
            ReviveDuration = 8f;
            ReviveHealth = 25;
            KnockHealth = 100;
            InvincibleDuration = 3f;
            CrawlSpeed = 0.25f;
            KnockEffectID = 61;
            ReviveEffectID = 61;
            ReviveSoundEffectID = 56; // vanilla "Beep"

            ReviveDistance = 4f;
            ReviveInput = "CROUCH";
            DownedPose = "SIT";
            RevivePluginKeyIndex = 0;
            InvincibleWhileDowned = false;
            PauseDrainWhileReviving = true;

            MessageKnocked = "You are downed! A teammate can revive you by crouching next to you.";
            MessageRevived = "You have been revived.";
            MessageBeingRevived = "A teammate is reviving you...";
            MessageReviveCancelled = "Revive cancelled.";
            MessageReviveStarted = "Reviving... stay crouched and close.";
            MessageReviveProgress = "Reviving... {seconds}s left ({percent}%)";
            MessageDownedHp = "Bleeding out... HP {hp} ({seconds}s left)";
        }
    }
}
