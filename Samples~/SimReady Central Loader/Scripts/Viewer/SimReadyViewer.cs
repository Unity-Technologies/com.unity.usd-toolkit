using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// The runtime front-end (play mode and a built player): a UI Toolkit panel to paste a link,
    /// download and import the asset, switch its variants, read its SimReady metadata and physics
    /// log, play, pause and reset the simulation, and edit the download allowlist; plus the
    /// viewport with the joint handles (<see cref="SimReadyGizmoElement"/>), the orbit camera,
    /// the grid and the contact shadow. It looks like the Editor window: both are built from
    /// <see cref="SimReadyUiKit"/>. An asset loaded in edit mode is picked up when play starts.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SimReadyViewer : MonoBehaviour
    {
        public const string ExampleLink =
            "https://huggingface.co/datasets/onrobot/simready-grippers/blob/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_rg2/simready_usd/onrobot_rg2.usda";

        [Tooltip("The link the field starts with.")]
        public string InitialLink = ExampleLink;

        public SimReadyOrbitCamera OrbitCamera;
        public SimReadyStageDecor StageDecor;

        [Tooltip("Put on the UIDocument when it has no Panel Settings. Filled with the sample's UI/SimReadyPanelSettings for every new viewer.")]
        public PanelSettings DefaultPanelSettings;

        private static readonly SimReadyPalette Palette = SimReadyPalette.Dark;

        private SimReadyHostPolicy policy;
        private SimReadyAsset asset;
        private CancellationTokenSource cancellation;
        private SimReadyGizmoElement gizmo;
        private TextField linkField;
        private Button loadButton;
        private Button exampleButton;
        private Button cancelButton;
        private SimReadyUiKit.DownloadProgress progress;
        private Label status;
        private VisualElement assetArea;
        private VisualElement settingsBody;
        private Button playButton;
        private Label modeLabel;
        private Texture2D thumbnail;
        private string logFilter = SimReadyUiKit.AllFilter;
        private readonly List<SimReadyUiKit.JointRow> jointRows = new List<SimReadyUiKit.JointRow>();

        private void OnEnable()
        {
            SimReadyAsset.Loaded += OnAssetLoaded;
            policy = SimReadyHostPolicy.Load();
            if (OrbitCamera == null)
            {
                OrbitCamera = FindAnyObjectByType<SimReadyOrbitCamera>();
            }

            // A scene built by hand may have a plain camera: give it the orbit controls, or a
            // left-drag off a handle (orbit), right-drag (pan) and the wheel (zoom) do nothing.
            Camera view = OrbitCamera != null ? OrbitCamera.Camera : Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            if (OrbitCamera == null && view != null)
            {
                OrbitCamera = view.gameObject.AddComponent<SimReadyOrbitCamera>();
                Debug.Log($"SimReady Central: added SimReadyOrbitCamera to {view.name} so the viewport can orbit, pan and zoom.");
            }

            if (StageDecor == null)
            {
                StageDecor = FindAnyObjectByType<SimReadyStageDecor>();
            }

            if (StageDecor == null && view != null)
            {
                StageDecor = view.gameObject.AddComponent<SimReadyStageDecor>();
            }

            if (!CheckDocument(out VisualElement root))
            {
                return;
            }

            Build(root);
            SimReadyAsset existing = FindAnyObjectByType<SimReadyAsset>();
            if (existing != null)
            {
                Show(existing);
            }
            else if (TryGetCommandLineLink(out string commandLink))
            {
                linkField.value = commandLink;
                _ = LoadAsync(commandLink);
            }
        }

        // The panel needs a UIDocument with Panel Settings, and those a theme (fonts, controls).
        // Say what is missing instead of showing nothing.
        private bool CheckDocument(out VisualElement root)
        {
            var document = GetComponent<UIDocument>();
            if (document != null && document.panelSettings == null && DefaultPanelSettings != null)
            {
                document.panelSettings = DefaultPanelSettings;
            }

            root = document != null ? document.rootVisualElement : null;
            if (document == null || document.panelSettings == null)
            {
                Debug.LogError($"SimReady Central: the UIDocument on '{name}' has no Panel Settings, so the panel cannot show. " +
                               "Assign the sample's UI/SimReadyPanelSettings, or set the scene up with GameObject > SimReady Central > Viewer.", this);
                return false;
            }

            if (document.panelSettings.themeStyleSheet == null)
            {
                Debug.LogWarning($"SimReady Central: the Panel Settings '{document.panelSettings.name}' have no Theme Style Sheet, so the " +
                                 "panel's text and controls do not render. Use the sample's UI/SimReadyPanelSettings, or give it UI/SimReadyTheme.", this);
            }

            if (root == null)
            {
                Debug.LogError($"SimReady Central: the UIDocument on '{name}' has no visual tree yet.", this);
                return false;
            }

            return true;
        }

#if UNITY_EDITOR
        // Added by hand, loaded with a scene or changed: an empty UIDocument (or default) is filled
        // with the sample's Panel Settings, so the panel shows without anything to assign.
        private void Reset() => AssignPanelSettings();

        private void OnValidate() => UnityEditor.EditorApplication.delayCall += AssignPanelSettings;

        private void AssignPanelSettings()
        {
            if (this == null)
            {
                return;
            }

            if (DefaultPanelSettings == null)
            {
                DefaultPanelSettings = FindPanelSettings();
            }

            var document = GetComponent<UIDocument>();
            if (document != null && document.panelSettings == null && DefaultPanelSettings != null)
            {
                UnityEditor.Undo.RecordObject(document, "Assign SimReady panel settings");
                document.panelSettings = DefaultPanelSettings;
                UnityEditor.EditorUtility.SetDirty(document);
            }
        }

        /// <summary>The sample's <c>UI/SimReadyPanelSettings</c>, wherever the sample was imported to (Editor only).</summary>
        public static PanelSettings FindPanelSettings()
        {
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("SimReadyPanelSettings t:PanelSettings"))
            {
                var settings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (settings != null)
                {
                    return settings;
                }
            }

            return null;
        }
#endif

        private void OnDisable()
        {
            SimReadyAsset.Loaded -= OnAssetLoaded;
            cancellation?.Cancel();
            Time.timeScale = 1f;
            if (thumbnail != null)
            {
                Destroy(thumbnail);
            }
        }

        // Whoever loaded it (this panel, the Editor window, a script), the current asset is shown.
        private void OnAssetLoaded(SimReadyAsset loaded)
        {
            if (loaded != null && loaded != asset)
            {
                Show(loaded);
            }
        }

        // A player started with "-simready-link <url>" loads that asset straight away.
        private static bool TryGetCommandLineLink(out string link)
        {
            link = null;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (arguments[i] == "-simready-link")
                {
                    link = arguments[i + 1];
                    return true;
                }
            }

            return false;
        }

        private void Update()
        {
            gizmo?.Refresh();
            if (asset == null)
            {
                return;
            }

            // The rows follow the physics without feeding back into it.
            foreach (SimReadyUiKit.JointRow row in jointRows)
            {
                row.Refresh(asset);
            }
        }

        // ---- Layout ----

        private void Build(VisualElement root)
        {
            root.Clear();
            root.style.flexDirection = FlexDirection.Row;

            var side = new ScrollView(ScrollViewMode.Vertical);
            side.style.width = 380f;
            side.style.flexShrink = 0f;
            side.style.backgroundColor = Palette.Window;
            side.style.paddingBottom = 12f;
            root.Add(side);

            var viewport = new VisualElement { name = "viewport" };
            viewport.style.flexGrow = 1f;
            // The view is centred on the viewport, not on the screen behind the panel.
            viewport.RegisterCallback<GeometryChangedEvent>(_ => FitCameraToViewport(root, viewport));
            gizmo = new SimReadyGizmoElement { OrbitCamera = OrbitCamera };
            viewport.Add(gizmo);
            root.Add(viewport);

            side.Add(SimReadyUiKit.Header("SimReady Central", "Load physics-ready USD assets from Hugging Face and move their parts."));
            side.Add(LinkCard());
            assetArea = new VisualElement();
            side.Add(assetArea);

            var settings = new Foldout { text = "Download settings", value = false };
            settings.style.marginLeft = settings.style.marginRight = 10f;
            settings.style.marginTop = 8f;
            settings.style.color = Palette.Text;
            settingsBody = new VisualElement();
            settings.Add(settingsBody);
            side.Add(settings);
            BuildSettings();
            ShowEmpty();
        }

        private void FitCameraToViewport(VisualElement root, VisualElement viewport)
        {
            float width = root.layout.width;
            float x = viewport.layout.x;
            // No layout yet (or none at all, without a graphics device): leave the camera as it is.
            if (OrbitCamera == null || float.IsNaN(width) || float.IsNaN(x) || width <= 0f || !(viewport.layout.width >= 1f))
            {
                return;
            }

            OrbitCamera.ViewportLeft = Mathf.Clamp(x / width, 0f, 0.9f);
            if (asset != null)
            {
                OrbitCamera.Frame(BoundsOf(asset));
            }
            else
            {
                FrameEmptyStage();
            }
        }

        private VisualElement LinkCard()
        {
            VisualElement card = SimReadyUiKit.Card(Palette, "Asset link", "Copy an asset's link in SimReady Central (Copy URL) and paste it here.");
            linkField = new TextField { value = InitialLink };
            linkField.textEdition.placeholder = "https://huggingface.co/datasets/…/blob/…/asset.usda";
            linkField.style.marginLeft = linkField.style.marginRight = 0f;
            card.Add(linkField);

            VisualElement row = SimReadyUiKit.Row();
            row.style.marginTop = 8f;
            row.style.flexWrap = Wrap.Wrap;
            loadButton = SimReadyUiKit.PrimaryButton("Download & import", () => _ = LoadAsync(linkField.value));
            exampleButton = SimReadyUiKit.SecondaryButton("Try the OnRobot RG2", () =>
            {
                linkField.value = ExampleLink;
                _ = LoadAsync(ExampleLink);
            });
            cancelButton = SimReadyUiKit.SecondaryButton("Cancel", () => cancellation?.Cancel());
            cancelButton.style.display = DisplayStyle.None;
            row.Add(loadButton);
            row.Add(exampleButton);
            row.Add(cancelButton);
            card.Add(row);

            progress = new SimReadyUiKit.DownloadProgress(Palette);
            card.Add(progress.Root);
            status = SimReadyUiKit.StatusBox();
            card.Add(status);
            return card;
        }

        private void BuildSettings()
        {
            settingsBody.Clear();
            SimReadyUiKit.HostSettings(Palette, settingsBody, policy, BuildSettings);
            VisualElement buttons = SimReadyUiKit.Row();
            buttons.style.marginTop = 6f;
            buttons.Add(SimReadyUiKit.SecondaryButton("Reset to defaults", () =>
            {
                SimReadyHostPolicy.ClearSaved();
                policy = SimReadyHostPolicy.Load();
                BuildSettings();
            }));
            buttons.Add(SimReadyUiKit.SecondaryButton("Clear download cache", () => SimReadyDownloader.ClearCache(SimReadyDownloader.DefaultCacheRoot)));
            settingsBody.Add(buttons);
        }

        // ---- Loading ----

        private async Task LoadAsync(string text)
        {
            if (cancellation != null)
            {
                return;
            }

            if (!SimReadyLink.TryParse(text, out _, out string error))
            {
                SetStatus(error, true);
                return;
            }

            cancellation = new CancellationTokenSource();
            SetBusy(true);
            SetStatus(null, false);
            var reporter = new Progress<SimReadyProgress>(progress.Report);

            try
            {
                // The loader replaces the asset in the scene and raises SimReadyAsset.Loaded, which shows it.
                SimReadyLoader.LoadResult result = await SimReadyLoader.LoadAsync(text, policy, null, reporter, cancellation.Token);
                OnAssetLoaded(result.Asset);
                SetStatus($"Loaded {result.Asset.Metadata.AssetName}: {result.Download.Files.Count} files, " +
                          $"{SimReadyWebTransport.Megabytes(result.Download.TotalBytes)}{(result.Download.FromCache ? " from the cache" : string.Empty)}; " +
                          $"moving parts: {string.Join(", ", result.Asset.Handles.Select(h => h.Label))}.", false);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled. Downloaded files stay in the cache; the next attempt resumes.", false);
            }
            catch (Exception exception) when (exception is SimReadyDownloadException || exception is UsdImportException || exception is UsdStageException)
            {
                SetStatus(exception.Message, true);
            }
            finally
            {
                cancellation?.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            loadButton.SetEnabled(!busy);
            exampleButton.SetEnabled(!busy);
            linkField.SetEnabled(!busy);
            cancelButton.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            progress.Show(busy);
        }

        // ---- The asset ----

        private void ShowEmpty()
        {
            FrameEmptyStage();
            assetArea.Clear();
            VisualElement empty = SimReadyUiKit.Card(Palette, null, null);
            Label hint = SimReadyUiKit.Muted(Palette, "No asset loaded yet. Load one above: its parts, metadata and physics show here.");
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            hint.style.paddingTop = hint.style.paddingBottom = 18f;
            empty.Add(hint);
            assetArea.Add(empty);
        }

        private void Show(SimReadyAsset loaded)
        {
            if (gizmo == null)
            {
                return; // no panel (CheckDocument said why)
            }

            asset = loaded;
            gizmo.Asset = loaded;
            Bounds bounds = BoundsOf(loaded);
            if (OrbitCamera != null)
            {
                OrbitCamera.Frame(bounds);
            }

            if (StageDecor != null)
            {
                StageDecor.Fit(bounds);
                StageDecor.ShowContactShadow = true;
            }

            if (thumbnail != null)
            {
                Destroy(thumbnail);
            }

            thumbnail = SimReadyUiKit.LoadThumbnail(loaded.ThumbnailPath);
            assetArea.Clear();
            jointRows.Clear();

            VisualElement actions = SimReadyUiKit.Row();
            actions.style.marginTop = 8f;
            actions.Add(SimReadyUiKit.SecondaryButton("Frame", () =>
            {
                if (OrbitCamera != null && asset != null)
                {
                    OrbitCamera.Frame(BoundsOf(asset));
                }
            }));
            assetArea.Add(SimReadyUiKit.AssetCard(Palette, loaded, thumbnail, actions));

            VisualElement variants = VariantsCard();
            if (variants != null)
            {
                assetArea.Add(variants);
            }

            assetArea.Add(SimulationCard());
            assetArea.Add(JointsCard());
            VisualElement physics = SimReadyUiKit.Card(Palette, "Physics", "What each UsdPhysics prim and PhysX attribute became in Unity. Tap a count to filter.");
            physics.Add(SimReadyUiKit.PhysicsLog(Palette, loaded.PhysicsLog, () => logFilter, f => logFilter = f));
            assetArea.Add(physics);
            if (loaded.DownloadWarnings.Count > 0)
            {
                var notes = new Foldout { text = $"Download notes ({loaded.DownloadWarnings.Count})", value = false };
                notes.style.marginLeft = notes.style.marginRight = 10f;
                notes.style.marginTop = 6f;
                notes.style.color = Palette.Text;
                foreach (string warning in loaded.DownloadWarnings)
                {
                    notes.Add(SimReadyUiKit.Muted(Palette, "• " + warning));
                }

                assetArea.Add(notes);
            }
        }

        private VisualElement VariantsCard()
        {
            IReadOnlyList<UsdVariantSetInfo> sets = SimReadyLoader.VariantSets(asset);
            if (sets.Count == 0)
            {
                return null;
            }

            VisualElement card = SimReadyUiKit.Card(Palette, "Variants", "Switching one re-imports the asset with that selection.");
            foreach (UsdVariantSetInfo set in sets)
            {
                var dropdown = new DropdownField(set.Name, set.Variants, Mathf.Max(0, set.Variants.IndexOf(set.Selection)));
                UsdVariantSetInfo captured = set;
                dropdown.RegisterValueChangedCallback(async e =>
                {
                    if (asset == null)
                    {
                        return;
                    }

                    try
                    {
                        OnAssetLoaded(await SimReadyLoader.SwitchVariantAsync(asset, captured.PrimPath, captured.Name, e.newValue));
                        SetStatus($"{captured.Name}: {e.newValue}", false);
                    }
                    catch (Exception exception) when (exception is UsdImportException || exception is UsdStageException || exception is InvalidOperationException)
                    {
                        SetStatus(exception.Message, true);
                    }
                });
                card.Add(dropdown);
            }

            return card;
        }

        // Shown only with an asset loaded (ShowAsset), so `asset` is set.
        private VisualElement SimulationCard()
        {
            VisualElement card = SimReadyUiKit.Card(Palette, "Simulation", null);
            modeLabel = SimReadyUiKit.Muted(Palette, string.Empty);
            UpdateModeLabel();
            card.Add(modeLabel);
            VisualElement row = SimReadyUiKit.Row();
            row.style.marginTop = 6f;
            playButton = Time.timeScale > 0f
                ? SimReadyUiKit.SecondaryButton("Pause", TogglePause)
                : SimReadyUiKit.PrimaryButton("Play", TogglePause);
            row.Add(playButton);
            row.Add(SimReadyUiKit.SecondaryButton("Reset to rest", () =>
            {
                if (asset != null)
                {
                    asset.ResetToRest();
                }
            }));
            Toggle kinematicToggle = SimReadyUiKit.KinematicToggle(Palette, asset, UpdateModeLabel);
            kinematicToggle.style.marginLeft = 12f;
            row.Add(kinematicToggle);
            card.Add(row);
            return card;
        }

        private VisualElement JointsCard()
        {
            VisualElement card = SimReadyUiKit.Card(Palette, "Moving parts", "Drag the orange handles in the view, or use the sliders.");
            foreach (SimReadyJointHandle handle in asset.Handles)
            {
                if (handle.Joint == null)
                {
                    continue;
                }

                SimReadyJointHandle captured = handle;
                SimReadyAsset owner = asset;
                SimReadyUiKit.JointRow row = SimReadyUiKit.JointRowFor(Palette, asset, handle,
                                                                       value => { if (owner != null) owner.SetValue(captured, value); },
                                                                       () => { if (owner != null) owner.BeginDrag(captured); },
                                                                       () => { if (owner != null) owner.EndDrag(captured); });
                card.Add(row.Root);
                jointRows.Add(row);
            }

            if (asset.Handles.Count == 0)
            {
                card.Add(SimReadyUiKit.Muted(Palette, "Nothing in this asset moves on its own."));
            }

            return card;
        }

        // Nothing loaded: the camera frames the whole grid, so the empty stage is seen in full.
        private void FrameEmptyStage()
        {
            if (StageDecor != null)
            {
                StageDecor.ShowContactShadow = false;
            }

            if (OrbitCamera != null && StageDecor != null)
            {
                OrbitCamera.Frame(StageDecor.GridBounds);
            }
        }

        private static Bounds BoundsOf(SimReadyAsset target)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(target.transform.position, Vector3.one * 0.2f);
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            return bounds;
        }

        private void TogglePause()
        {
            Time.timeScale = Time.timeScale > 0f ? 0f : 1f;
            // Rebuild the card so the button takes the primary style when it would resume.
            if (asset != null)
            {
                VisualElement old = playButton.parent.parent;
                VisualElement replacement = SimulationCard();
                old.parent.Insert(old.parent.IndexOf(old), replacement);
                old.RemoveFromHierarchy();
            }
        }

        private void UpdateModeLabel()
        {
            if (modeLabel != null)
            {
                modeLabel.text = asset != null && asset.Kinematic
                    ? "Kinematic: the physics is off and the handles pose the links directly."
                    : Time.timeScale > 0f
                        ? "Running: a handle sets its joint's drive target and the physics moves the linkage."
                        : "Paused: the physics holds still until you press Play.";
            }
        }

        private void SetStatus(string text, bool error)
        {
            SimReadyUiKit.SetStatus(Palette, status, text, error);
            if (!string.IsNullOrEmpty(text))
            {
                Debug.Log("SimReady Central: " + text);
            }
        }
    }
}
