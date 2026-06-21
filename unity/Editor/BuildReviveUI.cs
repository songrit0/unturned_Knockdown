// ============================================================================
//  Knockdown Revive HUD generator  (Unturned / Knockdown plugin)
//
//  Put this file in your Unturned-SDK Unity project under:  Assets/Editor/
//  Then use the top menu:  Knockdown UI -> Generate Revive HUD Prefab
//
//  It builds the whole center-screen revive HUD prefab in code, with the exact
//  child names Knockdown.dll writes to:
//      Title   (e.g. "REVIVING TEAMMATE")
//      Bar     (e.g. "[■■■■■□□□□□]  50%  4s")
//
//  CRITICAL: the ROOT GameObject is named "Effect" — Unturned loads an EffectAsset's
//  prefab from a child named "Effect" (exactly like ItemAsset -> "Item"/"Barricade").
//  Naming it anything else leaves _effect null on the client and the UI never renders
//  ("tellUIEffectText: key not found").
//
//  Unity version: use the one Unturned ships with (currently 2021.3.x).
//  After generating: build the master bundle (official Unturned SDK menu preferred),
//  publish it to Steam Workshop (File Type = Item, visibility Public), subscribe the
//  server (WorkshopDownloadConfig.json) + clients, then set the plugin config
//  ReviveUIEffectID = 30020 (the ID in ReviveUI/ReviveUI.dat).
// ============================================================================
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace KnockdownEditor
{
    public static class BuildReviveUI
    {
        private const string BundleName = "knockdownui.masterbundle";
        private const string PrefabDir = "Assets/KnockdownUI";
        private const string UIDir = PrefabDir + "/ReviveUI";
        // Folder "ReviveUI" matches ReviveUI.dat; the prefab inside MUST be named "Effect".
        private const string PrefabPath = UIDir + "/Effect.prefab";

        // Second effect: the "how to revive" hint (icons + instruction text), folder Hint/ -> Hint.dat.
        private const string HintDir = PrefabDir + "/Hint";
        private const string HintPrefabPath = HintDir + "/Effect.prefab";
        private const string IconsDir = PrefabDir + "/Icons";

        // Third effect: a top-right killfeed (folder Killfeed/ -> Killfeed.dat, ID 30024).
        private const string KillfeedDir = PrefabDir + "/Killfeed";
        private const string KillfeedPrefabPath = KillfeedDir + "/Effect.prefab";
        private const int KillfeedLines = 5;   // MUST be >= KillfeedMaxLines in the plugin config

        // Fourth effect: instant cuff-status toast (folder Cuff/ -> Cuff.dat, ID 30026).
        private const string CuffDir = PrefabDir + "/Cuff";
        private const string CuffPrefabPath = CuffDir + "/Effect.prefab";

        // Number of fill segments in the progress bar. MUST match ReviveBarSegments in Knockdown.cs.
        private const int ReviveBarSegments = 30;

        private static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.10f, 0.85f);
        private static readonly Color TitleColor = new Color(0.80f, 0.80f, 0.82f, 1f); // flat light gray, no green
        private static readonly Color BarColor = Color.white;

        [MenuItem("Knockdown UI/Generate Revive HUD Prefab")]
        public static void Generate()
        {
            if (!Directory.Exists(UIDir)) Directory.CreateDirectory(UIDir);

            // ---- root canvas -------------------------------------------------
            // A UI effect prefab MUST carry a Canvas or Unturned renders nothing.
            var root = new GameObject("Effect", // MUST be "Effect" (EffectAsset._effect convention)
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Three separate flat boxes in a row: STATUS | BAR | TIME (reference-image style:
            // near-black flat boxes, light-gray uppercase text, no green).
            const float BoxH = 64f, StatusW = 230f, BarW = 250f, TimeW = 90f, Gap = 8f;
            float rowW = StatusW + BarW + TimeW + Gap * 2f;

            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(root.transform, false);
            BottomCenter((RectTransform)row.transform, rowW, BoxH, yFromBottom: 150f);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = Gap;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = true;

            var statusBox = NewFlatBox("StatusBox", row.transform, StatusW);
            var title = NewText("Title", statusBox.transform, "STATUS", 18, FontStyle.Bold, TextAnchor.MiddleCenter);
            title.color = TitleColor;
            title.horizontalOverflow = HorizontalWrapMode.Wrap; // bilingual title shrinks/wraps to fit
            title.verticalOverflow = VerticalWrapMode.Truncate;
            title.resizeTextForBestFit = true; title.resizeTextMinSize = 8; title.resizeTextMaxSize = 20;
            Anchor(title.rectTransform, 0.06f, 0.08f, 0.94f, 0.92f);

            var barBox = NewFlatBox("BarBox", row.transform, BarW);

            // Graphical fill bar: a dark rounded track + N gray segments the server reveals one by
            // one via UI visibility (Unturned has no fill-amount API). Child name "Bar" = % text.
            var track = NewImage("BarTrack", barBox.transform, new Color(0.03f, 0.03f, 0.035f, 1f));
            track.sprite = MakeRoundedSprite(8);
            track.type = Image.Type.Sliced;
            track.pixelsPerUnitMultiplier = 1f;
            Anchor(track.rectTransform, 0.08f, 0.18f, 0.92f, 0.50f);

            var fillColor = new Color(0.55f, 0.55f, 0.58f, 1f); // gray, not green
            const float fpad = 0.012f;
            for (int i = 0; i < ReviveBarSegments; i++)
            {
                var seg = NewImage("Fill_" + i, track.transform, fillColor);
                float a = fpad + (1f - 2f * fpad) * i / ReviveBarSegments;
                float b = fpad + (1f - 2f * fpad) * (i + 1) / ReviveBarSegments;
                Anchor(seg.rectTransform, a, 0.16f, b, 0.84f);
                seg.gameObject.SetActive(false); // server reveals up to current progress
            }

            // Percent text centered over the bar (child name MUST stay "Bar").
            var bar = NewText("Bar", barBox.transform, "0%", 16, FontStyle.Bold, TextAnchor.MiddleCenter);
            bar.color = TitleColor;
            Anchor(bar.rectTransform, 0.08f, 0.55f, 0.92f, 0.92f);

            var timeBox = NewFlatBox("TimeBox", row.transform, TimeW);
            var time = NewText("Time", timeBox.transform, "0S", 18, FontStyle.Bold, TextAnchor.MiddleCenter);
            time.color = TitleColor;
            Anchor(time.rectTransform, 0.06f, 0.08f, 0.94f, 0.92f);

            // ---- save prefab + tag bundle ------------------------------------
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            var importer = AssetImporter.GetAtPath(PrefabPath);
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();

            EditorUtility.SetDirty(prefab);

            BuildHintPrefab();     // second effect: the revive instruction hint
            BuildKillfeedPrefab(); // third effect: the top-right killfeed
            BuildCuffPrefab();     // fourth effect: instant cuff-status toast

            AssetDatabase.SaveAssets();
            Debug.Log($"[Knockdown] Revive HUD + Hint + Killfeed + Cuff prefabs generated and tagged bundle '{BundleName}'. " +
                      "Build the master bundle, then set ReviveUIEffectID=30020, ReviveHintEffectID=30022, " +
                      "KillfeedEffectID=30024, CuffEffectID=30026 in Knockdown.configuration.xml.");
        }

        // One flat near-black box used by the revive row (Status/Bar/Time) — small rounded corners,
        // no border accent. Width is fixed via LayoutElement; height stretches to the row (HLG).
        private static GameObject NewFlatBox(string name, Transform parent, float width)
        {
            var img = NewImage(name, parent, PanelColor);
            img.sprite = MakeRoundedSprite(8);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            var le = img.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            return img.gameObject;
        }

        // Cause categories (MUST match Knockdown.cs). Each has an icon Icons/<cat>.png; the server
        // shows one per line. Stacked at the line's right edge so it reads "Killer  Victim 14m [icon]".
        private static readonly string[] KillfeedCats =
            { "downed", "gun", "melee", "punch", "car", "zombie", "blood", "skull", "biohazard", "droplet", "meat" };

        // Per-category icon tint (baked; one colour per cause type, matches KillfeedCats order).
        private static readonly Color[] KillfeedCatColors =
        {
            new Color(0.92f, 0.73f, 0.29f), // downed  gold
            new Color(1f, 1f, 1f),          // gun     white
            new Color(0.91f, 0.91f, 0.91f), // melee   white
            new Color(0.91f, 0.91f, 0.91f), // punch   white
            new Color(0.62f, 0.71f, 0.82f), // car     steel
            new Color(0.49f, 0.81f, 0.42f), // zombie  green
            new Color(0.94f, 0.42f, 0.42f), // blood   red
            new Color(0.80f, 0.80f, 0.82f), // skull   gray
            new Color(0.60f, 0.80f, 0.20f), // biohazard lime
            new Color(0.36f, 0.66f, 0.96f), // droplet  blue
            new Color(0.85f, 0.64f, 0.29f), // meat     amber
        };

        // Top-right killfeed. Each line is a right-anchored auto-sized row: [icon][text], so the
        // icon sits to the LEFT of the name and the whole row hugs the right edge. The server shows
        // KLine_i, sets Kill_i text, and reveals the matching KIco_i_<cat>.
        private static void BuildKillfeedPrefab()
        {
            if (!Directory.Exists(KillfeedDir)) Directory.CreateDirectory(KillfeedDir);

            var root = new GameObject("Effect",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            const float lineH = 34f, gap = 4f, top = 80f, rightMargin = 24f, iconSize = 30f, spacing = 8f;
            for (int i = 0; i < KillfeedLines; i++)
            {
                float lineY = top + i * (lineH + gap);

                // auto-sized right-anchored row: HorizontalLayoutGroup grows leftward from the right edge
                var lineGo = new GameObject("KLine_" + i, typeof(RectTransform));
                lineGo.transform.SetParent(root.transform, false);
                var lrt = (RectTransform)lineGo.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(1f, 1f);
                lrt.pivot = new Vector2(1f, 1f);
                lrt.anchoredPosition = new Vector2(-rightMargin, -lineY);
                lrt.sizeDelta = new Vector2(10f, lineH);
                var hlg = lineGo.AddComponent<HorizontalLayoutGroup>();
                hlg.childAlignment = TextAnchor.MiddleRight; hlg.spacing = spacing;
                hlg.childControlWidth = true; hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
                var fit = lineGo.AddComponent<ContentSizeFitter>();
                fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

                // icon box (LEFT) holding 11 stacked category icons; server reveals one
                var iconBox = new GameObject("KIconBox_" + i, typeof(RectTransform));
                iconBox.transform.SetParent(lineGo.transform, false);
                var ibLE = iconBox.AddComponent<LayoutElement>();
                ibLE.minWidth = ibLE.preferredWidth = iconSize;
                ibLE.minHeight = ibLE.preferredHeight = iconSize;
                for (int c = 0; c < KillfeedCats.Length; c++)
                {
                    var ico = NewImage("KIco_" + i + "_" + KillfeedCats[c], iconBox.transform, KillfeedCatColors[c]);
                    Sprite sp = LoadIconSprite(KillfeedCats[c]);
                    if (sp != null) ico.sprite = sp;
                    ico.preserveAspect = true; ico.raycastTarget = false;
                    var irt = ico.rectTransform;
                    irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.pivot = new Vector2(0.5f, 0.5f);
                    irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
                    ico.gameObject.SetActive(false);
                }

                // rich-text line (RIGHT); its own preferred width drives the row size
                var line = NewText("Kill_" + i, lineGo.transform, "", 20, FontStyle.Bold, TextAnchor.MiddleLeft);
                line.supportRichText = true; line.color = Color.white;
                line.horizontalOverflow = HorizontalWrapMode.Overflow;
                line.verticalOverflow = VerticalWrapMode.Truncate;

                lineGo.SetActive(false); // server shows lines as events arrive
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, KillfeedPrefabPath);
            Object.DestroyImmediate(root);
            var importer = AssetImporter.GetAtPath(KillfeedPrefabPath);
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
            EditorUtility.SetDirty(prefab);
        }

        // Import a pre-made white-on-transparent icon PNG (Icons/<cat>.png) as a tintable Sprite.
        private static Sprite LoadIconSprite(string cat)
        {
            string path = IconsDir + "/" + cat + ".png";
            if (!File.Exists(path)) { Debug.LogWarning("[Knockdown] missing killfeed icon: " + path); return null; }
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        // The "how to revive" hint: a Stand -> Crouch icon row plus a "Hint" text the server sets
        // per role (downed player vs nearby reviver). Same prefab, shown to whoever needs it.
        private static void BuildHintPrefab()
        {
            if (!Directory.Exists(HintDir)) Directory.CreateDirectory(HintDir);

            var root = new GameObject("Effect",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Wider panel so the bilingual instruction line fits; icons up top, text wraps below.
            const float PanelW = 760f, PanelH = 190f;
            var panel = NewImage("Panel", root.transform, PanelColor);
            panel.sprite = MakeRoundedSprite(24);
            panel.type = Image.Type.Sliced;
            panel.pixelsPerUnitMultiplier = 1f;
            BottomCenter(panel.rectTransform, PanelW, PanelH, yFromBottom: 106f); // sits above the progress HUD

            // Icon row (TOP half): Stand --> Crouch  (the action the rescuer must perform)
            var standImg = NewImage("IconStand", panel.transform, new Color(0.75f, 0.78f, 0.82f, 1f));
            standImg.sprite = MakeIconSprite(IconsDir + "/Stand.png", "Stand_proc");
            standImg.preserveAspect = true;
            Anchor(standImg.rectTransform, 0.36f, 0.46f, 0.46f, 0.95f);

            var arrow = NewText("Arrow", panel.transform, "→", 40, FontStyle.Bold, TextAnchor.MiddleCenter);
            Anchor(arrow.rectTransform, 0.46f, 0.46f, 0.54f, 0.95f);

            var crouchImg = NewImage("IconCrouch", panel.transform, TitleColor); // green = do this
            crouchImg.sprite = MakeIconSprite(IconsDir + "/Crouch.png", "Crouch_proc");
            crouchImg.preserveAspect = true;
            Anchor(crouchImg.rectTransform, 0.54f, 0.46f, 0.64f, 0.95f);

            // Server-set instruction line (child name MUST stay "Hint"). WRAPS + shrinks to fit
            // the rect so long bilingual text never spills past the panel edges.
            var hint = NewText("Hint", panel.transform, "Crouch next to revive", 22, FontStyle.Bold, TextAnchor.MiddleCenter);
            hint.horizontalOverflow = HorizontalWrapMode.Wrap;
            hint.verticalOverflow = VerticalWrapMode.Truncate;
            hint.resizeTextForBestFit = true; hint.resizeTextMinSize = 8; hint.resizeTextMaxSize = 22;
            Anchor(hint.rectTransform, 0.05f, 0.04f, 0.95f, 0.42f);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, HintPrefabPath);
            Object.DestroyImmediate(root);
            var importer = AssetImporter.GetAtPath(HintPrefabPath);
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
            EditorUtility.SetDirty(prefab);
        }

        // Instant cuff-status banner: shown to the cuffer ("CUFFING X") and the target
        // ("BEING CUFFED BY X") the moment /cuff (or the cuff item) lands. Single flat box,
        // single text child "Cuff" — the server clears it itself after a short duration.
        private static void BuildCuffPrefab()
        {
            if (!Directory.Exists(CuffDir)) Directory.CreateDirectory(CuffDir);

            var root = new GameObject("Effect",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            const float PanelW = 520f, PanelH = 70f;
            var panel = NewImage("Panel", root.transform, PanelColor);
            panel.sprite = MakeRoundedSprite(10);
            panel.type = Image.Type.Sliced;
            panel.pixelsPerUnitMultiplier = 1f;
            BottomCenter(panel.rectTransform, PanelW, PanelH, yFromBottom: 320f); // sits above the hint + revive boxes

            var cuff = NewText("Cuff", panel.transform, "CUFFING", 20, FontStyle.Bold, TextAnchor.MiddleCenter);
            cuff.color = TitleColor;
            cuff.horizontalOverflow = HorizontalWrapMode.Wrap;
            cuff.verticalOverflow = VerticalWrapMode.Truncate;
            cuff.resizeTextForBestFit = true; cuff.resizeTextMinSize = 8; cuff.resizeTextMaxSize = 22;
            Anchor(cuff.rectTransform, 0.05f, 0.08f, 0.95f, 0.92f);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, CuffPrefabPath);
            Object.DestroyImmediate(root);
            var importer = AssetImporter.GetAtPath(CuffPrefabPath);
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
            EditorUtility.SetDirty(prefab);
        }

        // Loads a black-on-white icon PNG and turns it into a tintable sprite: near-white background
        // -> transparent, the silhouette -> solid white (so the Image's color tints it). Returns a Sprite.
        private static Sprite MakeIconSprite(string srcPath, string outName)
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            src.LoadImage(File.ReadAllBytes(srcPath)); // resizes to the PNG and makes it readable
            Color32[] px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                float lum = (c.r + c.g + c.b) / 3f / 255f;
                px[i] = lum > 0.82f ? new Color32(255, 255, 255, 0)      // background -> transparent
                                    : new Color32(255, 255, 255, 255);   // silhouette -> white (tintable)
            }
            src.SetPixels32(px);
            src.Apply();

            string outPath = IconsDir + "/" + outName + ".png";
            File.WriteAllBytes(outPath, src.EncodeToPNG());
            Object.DestroyImmediate(src);

            AssetDatabase.ImportAsset(outPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(outPath);
        }

        // ---- helpers ----------------------------------------------------------

        private static Image NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // passive HUD, never interactive
            return img;
        }

        private static Text NewText(string name, Transform parent, string content, int size, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.text = content;
            t.font = GetFont();
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.resizeTextForBestFit = true;
            t.resizeTextMinSize = 2;
            t.resizeTextMaxSize = size;
            return t;
        }

        // Anchor a RectTransform to a fractional region of its parent (0..1), zero offsets.
        private static void Anchor(RectTransform rt, float minX, float minY, float maxX, float maxY)
        {
            rt.anchorMin = new Vector2(minX, minY);
            rt.anchorMax = new Vector2(maxX, maxY);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Fixed pixel size anchored to the BOTTOM-CENTER of the screen, lifted yFromBottom px up.
        private static void BottomCenter(RectTransform rt, float w, float h, float yFromBottom)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, yFromBottom);
            rt.sizeDelta = new Vector2(w, h);
        }

        // Generates a white rounded-rectangle sprite with a transparent-outside alpha mask and a
        // 9-slice border = radius, so the panel Image (type Sliced) keeps crisp rounded corners at
        // any size. The Image's color tints it. Referenced assets are pulled into the bundle
        // automatically, so this PNG needs no separate bundle tag.
        private static Sprite MakeRoundedSprite(int radius)
        {
            int size = radius * 2 + 4;
            string path = PrefabDir + "/ReviveRounded.png";

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    byte a = RoundedInside(x, y, size, radius) ? (byte)255 : (byte)0;
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.spriteBorder = new Vector4(radius, radius, radius, radius); // 9-slice corners
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        // True if pixel (x,y) is inside the rounded rectangle (only the 4 corner quadrants are clipped).
        private static bool RoundedInside(int x, int y, int size, int r)
        {
            bool cornerX = x < r || x >= size - r;
            bool cornerY = y < r || y >= size - r;
            if (!(cornerX && cornerY)) return true;
            float cx = (x < r) ? r : (size - 1 - r);
            float cy = (y < r) ? r : (size - 1 - r);
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= (float)r * r;
        }

        private static Font _font;
        private static Font GetFont()
        {
            if (_font != null) return _font;
            // Match the GameMenu phone: English/numbers in Pixelify Sans, Thai glyphs fall back to
            // 2005_iannnnnAMD. Both live in Assets/KnockdownUI/Fonts and ride in THIS bundle.
            string fontDir = PrefabDir + "/Fonts";
            string enPath = fontDir + "/PixelifySans-VariableFont_wght.ttf";
            string thPath = fontDir + "/2005_iannnnnAMD.ttf";
            Font en = File.Exists(enPath) ? AssetDatabase.LoadAssetAtPath<Font>(enPath) : null;
            if (en != null)
            {
                Font th = File.Exists(thPath) ? AssetDatabase.LoadAssetAtPath<Font>(thPath) : null;
                if (th != null)
                {
                    try { var ti = AssetImporter.GetAtPath(thPath); if (ti != null && ti.assetBundleName != BundleName) { ti.assetBundleName = BundleName; ti.SaveAndReimport(); } } catch { }
                    try {
                        var imp = AssetImporter.GetAtPath(enPath) as TrueTypeFontImporter;
                        if (imp != null) {
                            var so = new SerializedObject(imp);
                            var prop = so.FindProperty("m_FallbackFontReferences") ?? so.FindProperty("fallbackFontReferences");
                            if (prop != null) { prop.ClearArray(); prop.arraySize = 1; prop.GetArrayElementAtIndex(0).objectReferenceValue = th; so.ApplyModifiedProperties(); imp.SaveAndReimport(); }
                        }
                    } catch { }
                    en = AssetDatabase.LoadAssetAtPath<Font>(enPath);
                }
                return _font = en;
            }
            string tkPath = fontDir + "/TKCultureThin.ttf";
            if (File.Exists(tkPath)) { Font tk = AssetDatabase.LoadAssetAtPath<Font>(tkPath); if (tk != null) return _font = tk; }
#if UNITY_2022_2_OR_NEWER
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 14);
            return _font = f;
        }
    }
}
#endif
