using System.Collections.Generic;
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

        /// <summary>
        /// Seconds after a player is revived during which they CANNOT be downed again: a hit
        /// that would normally knock them down kills them outright instead (no second down).
        /// Applies to every revive path (crouch-channel and item revive). 0 = disabled. Default: 60.
        /// </summary>
        public float ReviveCooldown;

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
        /// Effect asset id sprayed at the downed player's body on a repeating interval while they
        /// stay knocked (until revived or dead). Default 5 = vanilla blood splatter. 0 = disabled.
        /// </summary>
        public ushort DownedBloodEffectID;

        /// <summary>Seconds between blood-splatter bursts on a downed player. Default: 3.</summary>
        public float DownedBloodInterval;

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

        /// <summary>
        /// If true, players may opt their own character out of the knockdown system with
        /// "/knockdown off" (persisted across sessions). Set false to force the system on
        /// for everyone and disable the command. Default: true.
        /// </summary>
        public bool AllowPlayerOptOut;

        // --- Item-based instant revive ------------------------------------
        /// <summary>
        /// If true, a nearby player can INSTANTLY revive a downed teammate by using
        /// (right-click) a medical item whose id is in ItemReviveIds. The item is
        /// consumed normally (that is the cost), and the downed player is revived to
        /// the item's own heal value. The crouch-channel revive still works alongside this.
        /// </summary>
        public bool EnableItemRevive;

        /// <summary>
        /// Item ids that act as instant-revive tools when used near a downed player.
        /// Defaults to the vanilla medical items; replace with your server's ids.
        /// (Vanilla: 15 Medkit, 95 Bandage, 96 Splint, 388 Morphine, 394 Dressing, 395 Bloodbag.)
        /// The reviver must be within ReviveDistance of the downed player when they use the item.
        /// </summary>
        public List<ushort> ItemReviveIds;

        // --- Center-screen revive HUD (custom UI EffectAsset) ------------------
        /// <summary>
        /// Effect asset id of the center-screen revive HUD (a Workshop/local master-bundle UI
        /// EffectAsset whose root prefab is named "Effect", with Text children "Title" and "Bar").
        /// 0 = no HUD; the chat MessageReviveProgress is used instead. Shown to BOTH the reviver
        /// and the downed player while a revive channel is running.
        /// </summary>
        public ushort ReviveUIEffectID;

        /// <summary>Number of segments in the text progress bar (e.g. 20 -> [■■■■■□□□□□...]). Default: 20.</summary>
        public int ReviveUIBarSegments;

        /// <summary>Title line shown on the reviver's HUD.</summary>
        public string ReviveUITitleReviver;

        /// <summary>Title line shown on the downed player's HUD.</summary>
        public string ReviveUITitleDowned;

        /// <summary>Title flashed briefly on the downed player's HUD when a revive is cancelled.</summary>
        public string ReviveUITitleCancelled;

        /// <summary>
        /// Effect asset id of the "how to revive" hint HUD (a second UI EffectAsset, root prefab
        /// named "Effect", with a Text child "Hint" plus the Stand/Crouch icons). 0 = no hint HUD.
        /// Shown to a downed player (waiting) and to any nearby standing teammate who could revive.
        /// </summary>
        public ushort ReviveHintEffectID;

        /// <summary>Hint shown to the DOWNED player (what's happening / what to wait for).</summary>
        public string ReviveHintDowned;

        /// <summary>Hint shown to a nearby standing teammate within ReviveDistance (how to revive).</summary>
        public string ReviveHintReviver;

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

        /// <summary>Shown when a player turns the knockdown system OFF for themselves (/knockdown off).</summary>
        public Message MessageKnockdownDisabled;

        /// <summary>Shown when a player turns the knockdown system back ON for themselves (/knockdown on).</summary>
        public Message MessageKnockdownEnabled;

        /// <summary>Shown to a player who instantly revived a downed teammate by using a medical item.</summary>
        public Message MessageItemRevive;

        /// <summary>
        /// Shown to a player who is killed outright (instead of downed) because they were still
        /// within the post-revive ReviveCooldown window. Only used when ReviveCooldown &gt; 0.
        /// </summary>
        public Message MessageReviveCooldownDeath;

        /// <summary>
        /// If true, a downed player cannot run commands (e.g. /home, /kit) to escape - they are
        /// blocked until revived or dead. Default: true.
        /// </summary>
        public bool BlockCommandsWhileDowned;

        /// <summary>Shown when a downed player tries to use a command (blocked).</summary>
        public Message MessageNoCommandWhileDowned;

        // --- On-screen killfeed (Effect UI, top-right, global) ----------------
        /// <summary>If true, show a top-right on-screen killfeed to everyone on knockdowns + kills.</summary>
        public bool EnableKillfeed;

        /// <summary>EffectAsset id of the killfeed UI (root prefab "Effect", Text lines Kill_0..N). Default 30024.</summary>
        public ushort KillfeedEffectID;

        /// <summary>Max lines shown at once (must be &lt;= the prefab's Kill_* line count). Default: 5.</summary>
        public int KillfeedMaxLines;

        /// <summary>Seconds each killfeed line stays before fading out. Default: 6.</summary>
        public float KillfeedDurationSeconds;

        // --- Downed-logout punishment (anti-combat-log) -----------------------
        // Closes the exploit where a DOWNED player disconnects to escape death:
        // their carried loot is dropped into a public, lootable box at the logout
        // spot (their own grid is emptied so nothing dupes), and they are killed
        // the moment they next reconnect ("you died while bleeding out").

        /// <summary>Master toggle for the whole downed-logout punishment. Default: true.</summary>
        public bool DownedLogoutEnabled;

        /// <summary>On a downed logout, drop the player's carried loot into a box at their position. Default: true.</summary>
        public bool DownedLogoutDropBox;

        /// <summary>
        /// On a downed logout, mark the player so they die the instant they next reconnect.
        /// Their grid is already emptied at logout, so this death drops nothing extra. Default: true.
        /// </summary>
        public bool DownedLogoutKillOnReconnect;

        /// <summary>Effect asset id played at the logout spot when a downed player combat-logs. Default: 133. 0 = none.</summary>
        public ushort DownedLogoutEffectId;

        /// <summary>ITEM (barricade) asset id used as the dropped loot box. Default: 22202 (storage crate).</summary>
        public ushort DownedLogoutBoxAssetId;

        /// <summary>Minutes the loot box survives before it decays and is destroyed. Default: 20.</summary>
        public int DownedLogoutBoxDecayMinutes;

        /// <summary>Effect id played at the box position right before it breaks. Default: 124. 0 = none.</summary>
        public ushort DownedLogoutBoxBreakEffectId;

        /// <summary>Grid width the box is resized to (height grows to fit). 0 = the asset's native width. Default: 5.</summary>
        public byte DownedLogoutBoxStorageWidth;

        /// <summary>Safety cap on auto-grown rows. 255 (byte max) is effectively unlimited. Default: 255.</summary>
        public byte DownedLogoutBoxMaxHeight;

        /// <summary>If loot can't fit even at max height, drop the remainder on the ground. Default: true.</summary>
        public bool DownedLogoutBoxOverflowToGround;

        /// <summary>Box rotation in degrees (Euler). 0/0/0 = the asset's default orientation.</summary>
        public float DownedLogoutBoxRotationX;
        public float DownedLogoutBoxRotationY;
        public float DownedLogoutBoxRotationZ;

        /// <summary>Metres to lift the box above the logout point so it doesn't sink into terrain. Default: 0.5.</summary>
        public float DownedLogoutBoxSpawnHeightOffset;

        /// <summary>Block players from putting items INTO the box (loot-only). Default: true.</summary>
        public bool DownedLogoutBoxBlockDeposit;

        /// <summary>
        /// On plugin load, destroy any leftover boxes (same asset, owner=server) from a previous
        /// session. Boxes are in-memory only and must not survive a server restart. Default: true.
        /// </summary>
        public bool DownedLogoutBoxSweepOnLoad;

        /// <summary>Shown to a player who is killed on reconnect for logging out while downed.</summary>
        public Message MessageDownedLogoutDeath;

        public void LoadDefaults()
        {
            KnockDuration = 60f;
            ReviveDuration = 8f;
            ReviveHealth = 25;
            ReviveCooldown = 60f; // after a revive, can't be downed again for 60s (die normally instead)
            KnockHealth = 100;
            InvincibleDuration = 3f;
            CrawlSpeed = 0.25f;
            KnockEffectID = 61;
            ReviveEffectID = 61;
            ReviveSoundEffectID = 56; // vanilla "Beep"
            DownedBloodEffectID = 5;  // vanilla blood splatter on the downed player; 0 to disable
            DownedBloodInterval = 3f; // every 3 seconds while knocked
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
            AllowPlayerOptOut = true;

            ReviveUIEffectID = 0;   // set to your published Workshop UI effect id (0 = chat only)
            ReviveUIBarSegments = 20;
            ReviveUITitleReviver = "REVIVING TEAMMATE | กำลังกู้ชีพเพื่อน";
            ReviveUITitleDowned = "BEING REVIVED | กำลังถูกกู้ชีพ";
            ReviveUITitleCancelled = "REVIVE CANCELLED | ยกเลิกการกู้ชีพ";
            ReviveHintEffectID = 0;     // set to your published Hint effect id (e.g. 30022)
            ReviveHintDowned = "You're DOWN - wait for a teammate to crouch beside you | คุณล้ม! รอเพื่อนมาย่อข้างๆ เพื่อกู้ชีพ";
            ReviveHintReviver = "CROUCH next to your teammate to revive | ย่อข้างเพื่อนที่ล้มเพื่อกู้ชีพ";

            EnableItemRevive = true;
            // Vanilla medical items + this server's Workshop revive syringe (19000 Mdical_Syringe).
            // Replace with your server's healing item ids.
            ItemReviveIds = new List<ushort> { 15, 95, 96, 388, 394, 395, 19000 };

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
            MessageKnockdownDisabled = new Message(
                "Knockdown disabled for you — you will die normally | ปิดระบบล้มแล้ว คุณจะตายปกติ", "yellow");
            MessageKnockdownEnabled = new Message(
                "Knockdown enabled for you | เปิดระบบล้มแล้ว", "green");
            MessageItemRevive = new Message(
                "You revived a teammate with a medical item | คุณชุบเพื่อนด้วยไอเทมรักษา", "green");
            MessageReviveCooldownDeath = new Message(
                "You were just revived - no second down, you died | เพิ่งถูกชุบ ล้มซ้ำไม่ได้ คุณตายเลย", "red");

            BlockCommandsWhileDowned = true;
            MessageNoCommandWhileDowned = new Message(
                "You can't use commands while downed | ใช้คำสั่งไม่ได้ตอนล้ม", "red");

            EnableKillfeed = true;
            KillfeedEffectID = 30024;
            KillfeedMaxLines = 5;
            KillfeedDurationSeconds = 6f;

            DownedLogoutEnabled = true;
            DownedLogoutDropBox = true;
            DownedLogoutKillOnReconnect = true;
            DownedLogoutEffectId = 133;
            DownedLogoutBoxAssetId = 22202;
            DownedLogoutBoxDecayMinutes = 20;
            DownedLogoutBoxBreakEffectId = 124;
            DownedLogoutBoxStorageWidth = 5;
            DownedLogoutBoxMaxHeight = 255;
            DownedLogoutBoxOverflowToGround = true;
            DownedLogoutBoxRotationX = 0f;
            DownedLogoutBoxRotationY = 0f;
            DownedLogoutBoxRotationZ = 0f;
            DownedLogoutBoxSpawnHeightOffset = 0.5f;
            DownedLogoutBoxBlockDeposit = true;
            DownedLogoutBoxSweepOnLoad = true;
            MessageDownedLogoutDeath = new Message(
                "You logged out while downed - you bled out | คุณออกเกมตอนล้ม คุณเลือดไหลตาย", "red");
        }
    }
}
