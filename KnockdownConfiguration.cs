using System.Xml.Serialization;
using Rocket.API;

namespace Knockdown
{
    /// <summary>
    /// A chat message with a configurable colour. Serializes as:
    /// <c>&lt;FieldName Text="..." Color="white" /&gt;</c>
    /// Color accepts names (white, red, green, yellow, ...) or hex (#RRGGBB).
    /// </summary>
    public sealed class Message
    {
        [XmlAttribute]
        public string Text;

        [XmlAttribute]
        public string Color;

        public Message() { }

        public Message(string text, string color)
        {
            Text = text;
            Color = color;
        }
    }

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

        /// <summary>
        /// Effect asset id used to draw a "ring" around the downed player showing the
        /// revive range. Set to 0 to disable the ring. Default: 130.
        /// </summary>
        public ushort RangeEffectID;

        /// <summary>Seconds between ring effect bursts. Default: 0.5.</summary>
        public float RangeEffectInterval;

        /// <summary>Number of effect points distributed around the ring. Default: 16.</summary>
        public int RangeEffectPoints;

        /// <summary>
        /// Vertical offset applied to every ring point relative to the downed player's position.
        /// Negative values sink the ring closer to / below the ground. Default: -0.5.
        /// </summary>
        public float RangeEffectYOffset;

        /// <summary>
        /// Effect asset id "fired into the sky" from the downed player like a signal flare.
        /// Set to 0 to disable. Default: 125.
        /// </summary>
        public ushort KnockFlareEffectID;

        /// <summary>Peak height the flare reaches above the downed player (metres). Default: 50.</summary>
        public float KnockFlareHeight;

        /// <summary>Seconds the flare takes to travel from ground to peak. Default: 1.5.</summary>
        public float KnockFlareDuration;

        /// <summary>How many effect points are spawned along the flare trajectory. Default: 10.</summary>
        public int KnockFlareSteps;

        /// <summary>
        /// After the flare reaches peak, keep re-triggering the effect at that point for this
        /// many seconds so it appears to "hang" in the sky. 0 = no hang. Default: 5.
        /// </summary>
        public float KnockFlareHangDuration;

        /// <summary>Seconds between re-triggers while the flare is hanging at peak. Default: 0.3.</summary>
        public float KnockFlareHangInterval;

        /// <summary>
        /// Radius of the ring drawn at the peak during the hang phase. 0 = single point at peak
        /// (no ring). Default: 6.
        /// </summary>
        public float KnockFlareHangRingRadius;

        /// <summary>Number of effect points around the hang ring. Default: 8.</summary>
        public int KnockFlareHangRingPoints;

        // --- Additional values required for a working server-side implementation ---

        /// <summary>Maximum distance (metres) a reviver may be from the downed player.</summary>
        public float ReviveDistance;

        /// <summary>
        /// How a reviver triggers a revive:
        ///   "CROUCH"       - hold crouch (ย่อ) near the downed player. Fully server-side, no key binding needed (default).
        ///   "CROUCH_START" - press crouch ONCE to start; afterwards the reviver may walk/stand freely
        ///                    and the revive only cancels if they leave ReviveDistance.
        ///   "PLUGINKEY"    - hold a bound Unturned plugin key (see RevivePluginKeyIndex).
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

        /// <summary>
        /// Gesture the reviver's character plays while reviving (visual flavour), e.g. they
        /// point at the downed teammate. Any EPlayerGesture name: POINT (default), WAVE,
        /// SALUTE, FACEPALM, ... Set to "NONE" to disable.
        /// </summary>
        public string ReviverGesture;

        /// <summary>
        /// Seconds between MessageDownedHp chat repeats to the downed player. Larger = less spam.
        /// Default: 5. (Was 1 in older versions.)
        /// </summary>
        public float DownedHpMessageInterval;

        /// <summary>
        /// Seconds between MessageReviveProgress chat repeats to reviver + downed player.
        /// Default: 2. (Was 1 in older versions.)
        /// </summary>
        public float ReviveProgressMessageInterval;

        // --- Player-facing messages (Text + Color attributes) ---
        public Message MessageKnocked;
        public Message MessageRevived;
        public Message MessageBeingRevived;
        public Message MessageReviveCancelled;
        public Message MessageReviveStarted;

        /// <summary>
        /// Shown once per second to both reviver and downed player while reviving.
        /// Placeholders: {seconds} = seconds left, {total} = total seconds, {percent} = progress %.
        /// </summary>
        public Message MessageReviveProgress;

        /// <summary>
        /// Shown once per second to the downed player while bleeding out (not while being revived).
        /// Placeholders: {hp} = current health, {seconds} = seconds left before death.
        /// </summary>
        public Message MessageDownedHp;

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
            RangeEffectID = 130;      // ring effect enabled by default; set to 0 to disable
            RangeEffectInterval = 0.5f;
            RangeEffectPoints = 16;
            RangeEffectYOffset = -0.5f;
            KnockFlareEffectID = 125; // signal-flare-into-the-sky on knock; set to 0 to disable
            KnockFlareHeight = 50f;
            KnockFlareDuration = 1.5f;
            KnockFlareSteps = 10;
            KnockFlareHangDuration = 5f;
            KnockFlareHangInterval = 0.3f;
            KnockFlareHangRingRadius = 6f;
            KnockFlareHangRingPoints = 8;

            ReviveDistance = 4f;
            ReviveInput = "CROUCH";
            DownedPose = "SIT";
            RevivePluginKeyIndex = 0;
            InvincibleWhileDowned = false;
            PauseDrainWhileReviving = true;
            ReviverGesture = "POINT";
            DownedHpMessageInterval = 5f;
            ReviveProgressMessageInterval = 2f;

            MessageKnocked = new Message(
                "If knocked down, wait for a teammate to revive you | ถ้าล้มให้รอเพื่อนชุบชีวิต", "white");
            MessageRevived = new Message(
                "You have been revived | คุณถูกชุบชีวิตแล้ว", "green");
            MessageBeingRevived = new Message(
                "A teammate is reviving you | เพื่อนกำลังชุบชีวิตคุณ", "green");
            MessageReviveCancelled = new Message(
                "Revive cancelled | ยกเลิกการชุบชีวิต", "red");
            MessageReviveStarted = new Message(
                "Reviving... stay crouched and close | กำลังชุบ... ย่อค้างไว้และอยู่ใกล้ๆ", "yellow");
            MessageReviveProgress = new Message(
                "Reviving... {seconds}s left ({percent}%) | กำลังชุบ... เหลือ {seconds} วิ ({percent}%)", "yellow");
            MessageDownedHp = new Message(
                "Bleeding out... HP {hp} ({seconds}s left) | เลือดไหล... HP {hp} (เหลือ {seconds} วิ)", "red");
        }
    }
}
