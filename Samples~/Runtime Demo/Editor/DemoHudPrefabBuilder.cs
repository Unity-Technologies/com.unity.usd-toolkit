using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.RuntimeDemo.Editor
{
    /// <summary>
    /// Generates Prefabs/DemoHud.prefab — the uGUI hierarchy the HUD used to build at runtime —
    /// and places it in the open scene, wired to <see cref="UsdDemoApp"/>.
    /// </summary>
    /// <remarks>
    /// A bootstrap tool, not the source of truth: once the prefab exists, edit the prefab.
    /// Running this again overwrites the prefab and discards any hand edits made to it.
    /// </remarks>
    public static class DemoHudPrefabBuilder
    {
        public static string PrefabPath => DemoSamplePaths.PrefabPath;

        private const float PanelWidth = 450.0f;
        private const float TopBarHeight = 68.0f;
        private const float StatusBarHeight = 68.0f;
        private const float TabWidth = 30.0f;
        private const float TabHeight = 86.0f;

        [MenuItem("Tools/USD Runtime Demo/Generate HUD Prefab")]
        public static void GenerateFromMenu()
        {
            string prefabPath = PrefabPath;
            if (File.Exists(prefabPath) && !EditorUtility.DisplayDialog("Generate HUD Prefab",
                    $"{prefabPath} already exists. Regenerating discards any edits made to it.",
                    "Regenerate", "Cancel"))
            {
                return;
            }

            Generate();
        }

        /// <summary>Writes the prefab and puts one wired instance in the active scene.</summary>
        public static GameObject Generate()
        {
            string prefabPath = PrefabPath;
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath));

            GameObject prefab;
            GameObject root = new GameObject("DemoHud", typeof(RectTransform));
            try
            {
                var refs = new SerializedObject(root.AddComponent<DemoHud>());
                Build(root, refs);
                refs.ApplyModifiedPropertiesWithoutUndo();

                prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                // Gone before PlaceInScene looks for an existing DemoHud, so it cannot find this one.
                Object.DestroyImmediate(root);
            }

            PlaceInScene(prefab);
            return prefab;
        }

        private static void PlaceInScene(GameObject prefab)
        {
            var app = Object.FindAnyObjectByType<UsdDemoApp>();
            DemoHud existing = Object.FindAnyObjectByType<DemoHud>();

            DemoHud hud = existing;
            if (hud == null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                Undo.RegisterCreatedObjectUndo(instance, "Add DemoHud");
                hud = instance.GetComponent<DemoHud>();
            }

            if (app != null)
            {
                var appRefs = new SerializedObject(app);
                appRefs.FindProperty("hud").objectReferenceValue = hud;
                appRefs.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        // ---------------------------------------------------------------- hierarchy

        private static void Build(GameObject root, SerializedObject refs)
        {
            // The canvas sits on the prefab root itself, so the prefab is self-contained.
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            BuildTopBar(root.transform, refs);
            BuildSidebar(root.transform, refs);
            BuildStatusBar(root.transform, refs);

            // Last, so the tab draws over the panel it sits against: with a screen-space overlay
            // canvas, draw order is sibling order.
            BuildPanelTab(root.transform, refs);
        }

        private static void Set(SerializedObject refs, string field, Object value)
        {
            SerializedProperty property = refs.FindProperty(field)
                ?? throw new InvalidOperationException($"DemoHud has no serialized field '{field}'.");
            property.objectReferenceValue = value;
        }

        private static void BuildTopBar(Transform parent, SerializedObject refs)
        {
            Image bar = UiFactory.Panel(parent, "TopBar", DemoTheme.Background);
            RectTransform rect = UiFactory.Rect(bar.gameObject);
            rect.anchorMin = new Vector2(0.0f, 1.0f);
            rect.anchorMax = new Vector2(1.0f, 1.0f);
            rect.pivot = new Vector2(0.5f, 1.0f);
            rect.sizeDelta = new Vector2(0.0f, TopBarHeight);
            rect.anchoredPosition = Vector2.zero;

            Text title = UiFactory.Label(bar.transform, "Title",
                "USD as the interchange layer  ·  PLM / DCC  →  Unity runtime",
                DemoTheme.SizeTitle, DemoTheme.TextPrimary, FontStyle.Bold, TextAnchor.MiddleLeft);
            RectTransform titleRect = UiFactory.Rect(title.gameObject);
            titleRect.anchorMin = new Vector2(0.0f, 0.0f);
            titleRect.anchorMax = new Vector2(0.62f, 1.0f);
            titleRect.offsetMin = new Vector2(28.0f, 0.0f);
            titleRect.offsetMax = Vector2.zero;

            Text runtimeInfo = UiFactory.Label(bar.transform, "RuntimeInfo", "USD Toolkit",
                DemoTheme.SizeSmall, DemoTheme.TextMuted, FontStyle.Normal, TextAnchor.MiddleRight);
            RectTransform infoRect = UiFactory.Rect(runtimeInfo.gameObject);
            infoRect.anchorMin = new Vector2(0.62f, 0.0f);
            infoRect.anchorMax = new Vector2(1.0f, 1.0f);
            infoRect.offsetMin = Vector2.zero;
            infoRect.offsetMax = new Vector2(-28.0f, 0.0f);
            Set(refs, "runtimeInfoLabel", runtimeInfo);
        }

        // ---------------------------------------------------------------- sidebar

        private static void BuildSidebar(Transform parent, SerializedObject refs)
        {
            Image panel = UiFactory.Panel(parent, "Sidebar", DemoTheme.Background);
            RectTransform rect = UiFactory.Rect(panel.gameObject);
            rect.anchorMin = new Vector2(0.0f, 0.0f);
            rect.anchorMax = new Vector2(0.0f, 1.0f);
            rect.pivot = new Vector2(0.0f, 0.5f);
            rect.sizeDelta = new Vector2(PanelWidth, -(TopBarHeight + StatusBarHeight + 4.0f));
            rect.anchoredPosition = new Vector2(0.0f, -2.0f);
            Set(refs, "panel", rect);

            VerticalLayoutGroup outer = UiFactory.VerticalGroup(panel.transform, "Content", 0.0f,
                new RectOffset(20, 20, 12, 12));
            UiFactory.Stretch(outer.gameObject, 0.0f, 0.0f, 0.0f, 0.0f);

            // Only the stage list can grow without bound, so it is the only thing that scrolls.
            BuildHeader(outer.transform, refs);
            Transform scroll = BuildScrollBody(outer.transform);
            BuildFooter(outer.transform, refs);

            BuildStagesSection(scroll, refs);
        }

        /// <summary>Pinned above the scroll area: import, and the settings that affect it.</summary>
        private static void BuildHeader(Transform parent, SerializedObject refs)
        {
            GameObject header = UiFactory.Create("Header", parent);
            UiFactory.AddVerticalGroup(header, 5.0f, new RectOffset(0, 0, 0, 6));

            UiFactory.SectionHeading(header.transform, "Source");

            UiButton import = UiFactory.Button(header.transform, "ImportButton", "Import USD file…",
                DemoTheme.SizeBody + 2, 48.0f, null);
            MakePrimary(import);
            Set(refs, "importButton", import);

            Text hint = UiFactory.Label(header.transform, "ImportHint",
                "Native dialog · .usd .usda .usdc .usdz · additive",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.SetPreferredHeight(hint.gameObject, 20.0f);

            UiFactory.SectionHeading(header.transform, "Stage conversion");

            Text conversionHint = UiFactory.Label(header.transform, "ConversionHint",
                "Toolkit reports these; it does not apply them.",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.SetPreferredHeight(conversionHint.gameObject, 20.0f);

            BuildChoiceRow(header.transform, "Z-up → Y-up", "Auto", "Off", 72.0f, 60.0f,
                out UiButton upAuto, out UiButton upOff);
            Set(refs, "upAxisAutoButton", upAuto);
            Set(refs, "upAxisOffButton", upOff);

            BuildChoiceRow(header.transform, "Units → metres", "Auto", "Off", 72.0f, 60.0f,
                out UiButton unitAuto, out UiButton unitOff);
            Set(refs, "unitAutoButton", unitAuto);
            Set(refs, "unitOffButton", unitOff);
        }

        /// <summary>
        /// The scrolling middle. Takes all slack in the sidebar, so the pinned header and footer
        /// keep their size and the stage list is the only thing that has to give.
        /// </summary>
        private static Transform BuildScrollBody(Transform parent)
        {
            GameObject scrollView = UiFactory.Create("ScrollView", parent);
            var scrollLayout = scrollView.AddComponent<LayoutElement>();
            scrollLayout.flexibleHeight = 1.0f;
            scrollLayout.minHeight = 90.0f;

            var scrollRect = scrollView.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28.0f;
            scrollRect.inertia = false;

            GameObject viewport = UiFactory.Create("Viewport", scrollView.transform);
            UiFactory.Stretch(viewport, 0.0f, 0.0f, 10.0f, 0.0f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = UiFactory.Create("ScrollContent", viewport.transform);
            RectTransform contentRect = UiFactory.Rect(content);
            contentRect.anchorMin = new Vector2(0.0f, 1.0f);
            contentRect.anchorMax = new Vector2(1.0f, 1.0f);
            contentRect.pivot = new Vector2(0.5f, 1.0f);
            contentRect.offsetMin = new Vector2(0.0f, contentRect.offsetMin.y);
            contentRect.offsetMax = new Vector2(0.0f, contentRect.offsetMax.y);
            UiFactory.AddVerticalGroup(content, 5.0f, new RectOffset(0, 0, 0, 6));
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = UiFactory.Rect(viewport);
            scrollRect.content = contentRect;

            BuildScrollbar(scrollView.transform, scrollRect);
            return content.transform;
        }

        /// <summary>
        /// A thin scrollbar. Not for dragging so much as for telling a room that there is more
        /// below the fold — a scroll area with no visible affordance reads as a clipped panel.
        /// </summary>
        private static void BuildScrollbar(Transform parent, ScrollRect scrollRect)
        {
            Image track = UiFactory.Panel(parent, "Scrollbar", new Color(1.0f, 1.0f, 1.0f, 0.05f));
            RectTransform trackRect = UiFactory.Rect(track.gameObject);
            trackRect.anchorMin = new Vector2(1.0f, 0.0f);
            trackRect.anchorMax = new Vector2(1.0f, 1.0f);
            trackRect.pivot = new Vector2(1.0f, 0.5f);
            trackRect.sizeDelta = new Vector2(4.0f, 0.0f);
            trackRect.anchoredPosition = Vector2.zero;

            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            Image handle = UiFactory.Panel(track.transform, "Handle", DemoTheme.Accent);
            RectTransform handleRect = UiFactory.Rect(handle.gameObject);
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;

            scrollbar.targetGraphic = handle;
            scrollbar.handleRect = handleRect;

            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        private static void BuildStagesSection(Transform parent, SerializedObject refs)
        {
            Set(refs, "loadedHeading", UiFactory.SectionHeading(parent, "Loaded stages (0)"));

            GameObject listHolder = UiFactory.Create("LoadedList", parent);
            UiFactory.AddVerticalGroup(listHolder, 5.0f, new RectOffset(0, 0, 2, 2));
            Set(refs, "listContainer", listHolder.transform);

            GameObject empty = UiFactory.Create("EmptyRow", listHolder.transform);
            Text emptyLabel = UiFactory.Label(empty.transform, "Text", "Nothing loaded yet.",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.Stretch(emptyLabel.gameObject, 4.0f, 0.0f, 0.0f, 0.0f);
            UiFactory.SetPreferredHeight(empty, 24.0f);
            Set(refs, "emptyListRow", empty);

            Set(refs, "rowTemplate", BuildRowTemplate(listHolder.transform));
        }

        /// <summary>The inactive row cloned per loaded stage. Shown here with placeholder text.</summary>
        private static StageListRow BuildRowTemplate(Transform parent)
        {
            GameObject row = UiFactory.Create("RowTemplate", parent);
            var background = row.AddComponent<Image>();
            background.color = DemoTheme.RowIdle;

            var button = row.AddComponent<Button>();
            button.targetGraphic = background;

            UiFactory.SetPreferredHeight(row, 68.0f);
            var layout = row.GetComponent<LayoutElement>();

            VerticalLayoutGroup group = UiFactory.VerticalGroup(row.transform, "Inner", 1.0f,
                new RectOffset(10, 10, 5, 5));
            UiFactory.Stretch(group.gameObject, 0.0f, 0.0f, 0.0f, 0.0f);

            Text name = UiFactory.Label(group.transform, "Name", "Stage name",
                DemoTheme.SizeBody, DemoTheme.TextPrimary);
            UiFactory.SetPreferredHeight(name.gameObject, 21.0f);

            Text detail = UiFactory.Label(group.transform, "Detail", "Prims · meshes · materials",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.SetPreferredHeight(detail.gameObject, 19.0f);

            Text conversion = UiFactory.Label(group.transform, "Conversion", "↻ Z-up → Y-up",
                DemoTheme.SizeSmall, DemoTheme.Warning);
            UiFactory.SetPreferredHeight(conversion.gameObject, 19.0f);

            var stageRow = row.AddComponent<StageListRow>();
            var rowRefs = new SerializedObject(stageRow);
            rowRefs.FindProperty("background").objectReferenceValue = background;
            rowRefs.FindProperty("button").objectReferenceValue = button;
            rowRefs.FindProperty("layout").objectReferenceValue = layout;
            rowRefs.FindProperty("nameLabel").objectReferenceValue = name;
            rowRefs.FindProperty("detailLabel").objectReferenceValue = detail;
            rowRefs.FindProperty("conversionLabel").objectReferenceValue = conversion;
            rowRefs.ApplyModifiedPropertiesWithoutUndo();

            row.SetActive(false);
            return stageRow;
        }

        /// <summary>
        /// Pinned below the scroll area: the gizmo toggle and every export-time decision.
        /// </summary>
        /// <remarks>
        /// These are the controls you reach for while the room is watching, so none of them may
        /// sit behind a scroll.
        /// </remarks>
        private static void BuildFooter(Transform parent, SerializedObject refs)
        {
            GameObject footer = UiFactory.Create("Footer", parent);
            UiFactory.AddVerticalGroup(footer, 5.0f, new RectOffset(0, 0, 6, 0));

            UiFactory.SectionHeading(footer.transform, "Manipulate");

            BuildChoiceRow(footer.transform, "Gizmo", "Translate (W)", "Rotate (E)", 124.0f, 108.0f,
                out UiButton translate, out UiButton rotate);
            Set(refs, "translateButton", translate);
            Set(refs, "rotateButton", rotate);

            // Doubles as the selection readout, so selection costs no extra row.
            Text selectionLabel = UiFactory.Label(footer.transform, "SelectionLabel",
                "Click a part to select it · scale not offered",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.SetPreferredHeight(selectionLabel.gameObject, 20.0f);
            Set(refs, "selectionLabel", selectionLabel);

            UiFactory.SectionHeading(footer.transform, "Export");

            GameObject modeRow = UiFactory.Create("ExportModeRow", footer.transform);
            HorizontalLayoutGroup modeGroup = UiFactory.HorizontalGroup(modeRow.transform, "Inner", 8.0f);
            UiFactory.Stretch(modeGroup.gameObject, 0.0f, 0.0f, 0.0f, 0.0f);
            UiFactory.SetPreferredHeight(modeRow, 38.0f);

            Set(refs, "referencingButton", UiFactory.Button(modeGroup.transform, "Referencing", "Referencing",
                DemoTheme.SizeBody, 36.0f, null));
            Set(refs, "flatteningButton", UiFactory.Button(modeGroup.transform, "Flattening", "Flattening",
                DemoTheme.SizeBody, 36.0f, null));

            Text blurb = UiFactory.Label(footer.transform, "ExportModeBlurb",
                "References the original layers — small file, source stays the authority. Written as .usda.",
                DemoTheme.SizeSmall, DemoTheme.TextMuted);
            UiFactory.SetPreferredHeight(blurb.gameObject, 40.0f);
            Set(refs, "exportModeBlurb", blurb);

            BuildChoiceRow(footer.transform, "Format", ".usdc", ".usda", 68.0f, 68.0f,
                out UiButton usdc, out UiButton usda);
            // Third choice, appended to the same row.
            UiButton usd = UiFactory.Button(usda.transform.parent, "Third", ".usd",
                DemoTheme.SizeSmall, 32.0f, null);
            FixWidth(usd, 68.0f);
            Set(refs, "usdcButton", usdc);
            Set(refs, "usdaButton", usda);
            Set(refs, "usdButton", usd);

            BuildChoiceRow(footer.transform, "Ref paths", "Relative", "Absolute", 84.0f, 84.0f,
                out UiButton relative, out UiButton absolute);
            Set(refs, "relativeButton", relative);
            Set(refs, "absoluteButton", absolute);

            UiButton export = UiFactory.Button(footer.transform, "ExportButton", "Export scene…",
                DemoTheme.SizeBody + 2, 48.0f, null);
            MakePrimary(export);
            Set(refs, "exportButton", export);

            Set(refs, "clearButton", UiFactory.Button(footer.transform, "ClearButton", "Clear scene",
                DemoTheme.SizeSmall, 30.0f, null));

            Text navHint = UiFactory.Label(footer.transform, "NavHint",
                "Drag pan · RMB orbit · wheel zoom · F frame · H panel",
                DemoTheme.SizeSmall, DemoTheme.TextMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
            UiFactory.SetPreferredHeight(navHint.gameObject, 20.0f);
        }

        private static void MakePrimary(UiButton button)
        {
            button.SetPalette(DemoTheme.Accent, DemoTheme.TextOnAccent);
            button.Label.fontStyle = FontStyle.Bold;
        }

        /// <summary>A caption with a pair of mutually exclusive buttons on the right.</summary>
        private static void BuildChoiceRow(
            Transform parent,
            string caption,
            string firstLabel,
            string secondLabel,
            float firstWidth,
            float secondWidth,
            out UiButton first,
            out UiButton second)
        {
            GameObject row = UiFactory.Create("Row_" + caption, parent);
            HorizontalLayoutGroup group = UiFactory.HorizontalGroup(row.transform, "Inner", 6.0f);
            UiFactory.Stretch(group.gameObject, 0.0f, 0.0f, 0.0f, 0.0f);
            UiFactory.SetPreferredHeight(row, 36.0f);

            Text label = UiFactory.Label(group.transform, "Caption", caption, DemoTheme.SizeBody,
                DemoTheme.TextPrimary, FontStyle.Normal, TextAnchor.MiddleLeft);
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.0f;

            first = UiFactory.Button(group.transform, "First", firstLabel, DemoTheme.SizeSmall, 32.0f, null);
            FixWidth(first, firstWidth);

            second = UiFactory.Button(group.transform, "Second", secondLabel, DemoTheme.SizeSmall, 32.0f, null);
            FixWidth(second, secondWidth);
        }

        private static void FixWidth(UiButton button, float width)
        {
            LayoutElement layout = button.GetComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.flexibleWidth = 0.0f;
        }

        // ---------------------------------------------------------------- chrome

        private static void BuildPanelTab(Transform parent, SerializedObject refs)
        {
            UiButton tab = UiFactory.Button(parent, "SidebarTab", "<", DemoTheme.SizeBody, TabHeight, null);

            RectTransform rect = UiFactory.Rect(tab.gameObject);
            rect.anchorMin = new Vector2(0.0f, 0.5f);
            rect.anchorMax = new Vector2(0.0f, 0.5f);
            rect.pivot = new Vector2(0.0f, 0.5f);
            rect.sizeDelta = new Vector2(TabWidth, TabHeight);
            rect.anchoredPosition = new Vector2(PanelWidth, 0.0f);

            // Button() adds a LayoutElement for use inside a layout group; this one floats free
            // on the canvas, so that component would only be misleading.
            Object.DestroyImmediate(tab.GetComponent<LayoutElement>());

            // Translucent accent: findable against a dark viewport from the back of a room,
            // without competing with Import/Export for attention.
            tab.SetPalette(DemoTheme.AccentDim, DemoTheme.Accent);
            tab.Label.fontStyle = FontStyle.Bold;
            Set(refs, "panelTab", tab);
        }

        private static void BuildStatusBar(Transform parent, SerializedObject refs)
        {
            Image bar = UiFactory.Panel(parent, "StatusBar", DemoTheme.Background);
            RectTransform rect = UiFactory.Rect(bar.gameObject);
            rect.anchorMin = new Vector2(0.0f, 0.0f);
            rect.anchorMax = new Vector2(1.0f, 0.0f);
            rect.pivot = new Vector2(0.5f, 0.0f);
            rect.sizeDelta = new Vector2(0.0f, StatusBarHeight);
            rect.anchoredPosition = Vector2.zero;

            Image stripe = UiFactory.Panel(bar.transform, "Stripe", DemoTheme.Accent);
            RectTransform stripeRect = UiFactory.Rect(stripe.gameObject);
            stripeRect.anchorMin = new Vector2(0.0f, 0.0f);
            stripeRect.anchorMax = new Vector2(0.0f, 1.0f);
            stripeRect.pivot = new Vector2(0.0f, 0.5f);
            stripeRect.sizeDelta = new Vector2(6.0f, 0.0f);
            stripeRect.anchoredPosition = Vector2.zero;
            Set(refs, "statusStripe", stripe);

            Text status = UiFactory.Label(bar.transform, "StatusText", "Ready.", DemoTheme.SizeStatus,
                DemoTheme.TextPrimary, FontStyle.Normal, TextAnchor.MiddleLeft);
            RectTransform textRect = UiFactory.Rect(status.gameObject);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(28.0f, 0.0f);
            textRect.offsetMax = new Vector2(-28.0f, 0.0f);
            Set(refs, "statusText", status);
        }
    }
}
