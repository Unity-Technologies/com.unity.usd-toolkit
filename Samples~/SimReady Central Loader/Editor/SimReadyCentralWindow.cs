using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Editor
{
    /// <summary>
    /// <b>USD Toolkit &gt; SimReady Central Loader</b>: paste a file link copied from SimReady
    /// Central, load it into the open scene (no Play needed), pick the Physics variant, read the
    /// asset's SimReady metadata and physics log, and pose its moving parts with the sliders here
    /// or the handles in the Scene view (<see cref="SimReadySceneGizmo"/>). Built from
    /// <see cref="SimReadyUiKit"/>, like the runtime viewer.
    /// </summary>
    public sealed class SimReadyCentralWindow : EditorWindow
    {
        public const string ExampleLink =
            "https://huggingface.co/datasets/onrobot/simready-grippers/blob/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_rg2/simready_usd/onrobot_rg2.usda";

        private const string LinkKey = "Unity.USDToolkit.SimReadyCentral.LastLink";

        private SimReadyHostPolicy policy;
        private CancellationTokenSource cancellation;
        private SimReadyAsset asset;
        private SimReadyAsset shown;
        private string logFilter = SimReadyUiKit.AllFilter;
        private Texture2D thumbnail;

        private TextField linkField;
        private Button loadButton;
        private Button exampleButton;
        private Button cancelButton;
        private SimReadyUiKit.DownloadProgress progress;
        private Label status;
        private VisualElement settingsBody;
        private VisualElement assetArea;
        private Label modeLabel;
        private readonly List<SimReadyUiKit.JointRow> jointRows = new List<SimReadyUiKit.JointRow>();

        private static SimReadyPalette Palette => EditorGUIUtility.isProSkin ? SimReadyPalette.Dark : SimReadyPalette.Light;

        [MenuItem("USD Toolkit/SimReady Central Loader")]
        public static void Open()
        {
            var window = GetWindow<SimReadyCentralWindow>();
            window.titleContent = new GUIContent("SimReady Central", EditorGUIUtility.IconContent("d_Prefab Icon").image);
            window.minSize = new Vector2(380f, 480f);
        }

        private void OnEnable()
        {
            SimReadyAsset.Loaded += OnAssetLoaded;
            EditorApplication.playModeStateChanged += OnPlayModeEnteredOrLeft;
            policy = SimReadyHostPolicy.Load();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            SimReadyAsset.Loaded -= OnAssetLoaded;
            EditorApplication.playModeStateChanged -= OnPlayModeEnteredOrLeft;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            cancellation?.Cancel();
            if (thumbnail != null)
            {
                DestroyImmediate(thumbnail);
            }
        }

        // Loaded from here, from the runtime viewer or by a script: the window shows the current asset.
        private void OnAssetLoaded(SimReadyAsset loaded)
        {
            if (loaded == null)
            {
                return;
            }

            asset = loaded;
            ShowAsset(loaded);
        }

        // Entering or leaving play mode swaps the scene's objects: an asset imported in play mode is
        // gone afterwards, one from edit mode is a new object. Start over from what the scene holds.
        private void OnPlayModeEnteredOrLeft(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode || change == PlayModeStateChange.EnteredPlayMode)
            {
                asset = null;
                ShowAsset(Current);
            }
        }

        // Unity's == says a destroyed object equals null, so `current != shown` cannot tell "the
        // shown asset was destroyed" from "nothing is shown": this can.
        private bool ShownWasDestroyed => !ReferenceEquals(shown, null) && shown == null;

        // Unity's joints start from the transforms: put the links back at the pose the import
        // built before play mode starts, and let SimReadyAsset move them to the handle values.
        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                foreach (SimReadyAsset each in FindObjectsByType<SimReadyAsset>())
                {
                    each.PrepareForPlayMode();
                }
            }
        }

        // ---- Layout ----

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.style.backgroundColor = Palette.Window;
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            root.Add(scroll);

            scroll.Add(SimReadyUiKit.Header("SimReady Central", "Load physics-ready USD assets from Hugging Face and move their parts."));
            scroll.Add(LinkCard());
            assetArea = new VisualElement();
            scroll.Add(assetArea);

            var settings = new Foldout { text = "Download settings", value = false };
            settings.style.marginLeft = settings.style.marginRight = 10f;
            settings.style.marginTop = 8f;
            settings.style.marginBottom = 12f;
            settingsBody = new VisualElement();
            settings.Add(settingsBody);
            scroll.Add(settings);
            BuildSettings();

            root.schedule.Execute(Tick).Every(100);
            ShowAsset(Current);
        }

        private VisualElement LinkCard()
        {
            VisualElement card = SimReadyUiKit.Card(Palette, "Asset link", "Copy an asset's link in SimReady Central (Copy URL) and paste it here.");
            linkField = new TextField { value = EditorPrefs.GetString(LinkKey, ExampleLink) };
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
            buttons.Add(SimReadyUiKit.SecondaryButton("Clear download cache", () =>
            {
                if (EditorUtility.DisplayDialog("Clear download cache", "Delete every downloaded SimReady asset in " +
                                                SimReadyDownloader.DefaultCacheRoot + "?", "Delete", "Keep"))
                {
                    SimReadyDownloader.ClearCache(SimReadyDownloader.DefaultCacheRoot);
                }
            }));
            settingsBody.Add(buttons);
            Label cache = SimReadyUiKit.Muted(Palette, "Cache: " + SimReadyDownloader.DefaultCacheRoot);
            cache.style.marginTop = 4f;
            settingsBody.Add(cache);
        }

        // ---- The asset ----

        private SimReadyAsset Current
        {
            get
            {
                SimReadyAsset selected = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<SimReadyAsset>() : null;
                if (selected != null)
                {
                    asset = selected;
                }

                if (asset == null)
                {
                    asset = FindAnyObjectByType<SimReadyAsset>();
                }

                return asset;
            }
        }

        private void Tick()
        {
            SimReadyAsset current = Current;
            if (ShownWasDestroyed || current != shown)
            {
                ShowAsset(current);
                return;
            }

            if (current == null)
            {
                return;
            }

            if (modeLabel != null)
            {
                modeLabel.text = !Application.isPlaying
                    ? current.Kinematic
                        ? "Edit mode: the handles pose the links directly, no physics step. Kinematic: Play Mode will pose them the same way."
                        : "Edit mode: the handles pose the links directly, no physics step."
                    : current.Kinematic
                        ? "Play Mode, kinematic: the physics is off and the handles pose the links directly."
                        : "Play Mode: a handle sets its joint's drive target and the physics moves the linkage.";
            }

            foreach (SimReadyUiKit.JointRow row in jointRows)
            {
                row.Refresh(current);
            }
        }

        private void ShowAsset(SimReadyAsset current)
        {
            if (assetArea == null)
            {
                return;
            }

            shown = current;
            assetArea.Clear();
            jointRows.Clear();
            modeLabel = null;
            if (current == null)
            {
                VisualElement empty = SimReadyUiKit.Card(Palette, null, null);
                Label hint = SimReadyUiKit.Muted(Palette, "No SimReady asset in the scene yet. Load one above: its parts, metadata and physics show here.");
                hint.style.unityTextAlign = TextAnchor.MiddleCenter;
                hint.style.paddingTop = hint.style.paddingBottom = 18f;
                empty.Add(hint);
                assetArea.Add(empty);
                return;
            }

            if (thumbnail != null)
            {
                DestroyImmediate(thumbnail);
            }

            thumbnail = SimReadyUiKit.LoadThumbnail(current.ThumbnailPath);
            VisualElement actions = SimReadyUiKit.Row();
            actions.style.marginTop = 8f;
            actions.Add(SimReadyUiKit.SecondaryButton("Select", () =>
            {
                if (current != null)
                {
                    Selection.activeGameObject = current.gameObject;
                }
            }));
            actions.Add(SimReadyUiKit.SecondaryButton("Frame in Scene view", () =>
            {
                if (current != null)
                {
                    FrameInSceneView(current);
                }
            }));
            Texture image = thumbnail != null ? thumbnail : EditorGUIUtility.IconContent("d_Prefab Icon").image;
            assetArea.Add(SimReadyUiKit.AssetCard(Palette, current, image, actions));

            VisualElement variants = VariantsCard(current);
            if (variants != null)
            {
                assetArea.Add(variants);
            }

            assetArea.Add(JointsCard(current));
            VisualElement physics = SimReadyUiKit.Card(Palette, "Physics", "What each UsdPhysics prim and PhysX attribute became in Unity. Click a count to filter.");
            physics.Add(SimReadyUiKit.PhysicsLog(Palette, current.PhysicsLog, () => logFilter, f => logFilter = f));
            assetArea.Add(physics);
            if (current.DownloadWarnings.Count > 0)
            {
                var notes = new Foldout { text = $"Download notes ({current.DownloadWarnings.Count})", value = false };
                notes.style.marginLeft = notes.style.marginRight = 10f;
                notes.style.marginTop = 6f;
                foreach (string warning in current.DownloadWarnings)
                {
                    notes.Add(SimReadyUiKit.Muted(Palette, "• " + warning));
                }

                assetArea.Add(notes);
            }

            Tick();
        }

        private VisualElement VariantsCard(SimReadyAsset current)
        {
            IReadOnlyList<UsdVariantSetInfo> sets = SimReadyLoader.VariantSets(current);
            if (sets.Count == 0)
            {
                return null;
            }

            VisualElement card = SimReadyUiKit.Card(Palette, "Variants", "Switching one re-imports the asset with that selection.");
            foreach (UsdVariantSetInfo set in sets)
            {
                var dropdown = new DropdownField(set.Name, set.Variants, Mathf.Max(0, set.Variants.IndexOf(set.Selection)));
                UsdVariantSetInfo captured = set;
                dropdown.RegisterValueChangedCallback(e =>
                {
                    if (current != null)
                    {
                        _ = SwitchAsync(current, captured, e.newValue);
                    }
                });
                card.Add(dropdown);
            }

            return card;
        }

        private VisualElement JointsCard(SimReadyAsset current)
        {
            VisualElement card = SimReadyUiKit.Card(Palette, "Moving parts", null);
            modeLabel = SimReadyUiKit.Muted(Palette, string.Empty);
            modeLabel.style.marginBottom = 2f;
            card.Add(modeLabel);
            card.Add(SimReadyUiKit.KinematicToggle(Palette, current, () =>
            {
                if (current != null && !Application.isPlaying)
                {
                    // The choice is the asset's, saved with the scene for play mode.
                    EditorUtility.SetDirty(current);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(current.gameObject.scene);
                }

                SceneView.RepaintAll();
            }));
            foreach (SimReadyJointHandle handle in current.Handles)
            {
                if (handle.Joint == null)
                {
                    continue;
                }

                SimReadyJointHandle captured = handle;
                SimReadyUiKit.JointRow row = SimReadyUiKit.JointRowFor(Palette, current, handle, value =>
                {
                    if (current == null)
                    {
                        return; // destroyed (play mode left, or replaced); the next tick rebuilds the panel
                    }

                    if (!Application.isPlaying)
                    {
                        Undo.RecordObjects(current.GetComponentsInChildren<Transform>(), "Pose " + captured.Label);
                    }

                    current.SetValue(captured, value);
                    SceneView.RepaintAll();
                }, () =>
                {
                    if (current != null)
                    {
                        current.BeginDrag(captured);
                    }
                }, () =>
                {
                    if (current != null)
                    {
                        current.EndDrag(captured);
                    }
                });
                card.Add(row.Root);
                jointRows.Add(row);
            }

            if (current.Handles.Count == 0)
            {
                card.Add(SimReadyUiKit.Muted(Palette, "Nothing in this asset moves on its own."));
                return card;
            }

            VisualElement actions = SimReadyUiKit.Row();
            actions.style.marginTop = 8f;
            actions.Add(SimReadyUiKit.SecondaryButton("Reset to rest", () =>
            {
                if (current == null)
                {
                    return;
                }

                if (!Application.isPlaying)
                {
                    Undo.RecordObjects(current.GetComponentsInChildren<Transform>(), "Reset SimReady pose");
                }

                current.ResetToRest();
                SceneView.RepaintAll();
            }));
            Label tip = SimReadyUiKit.Muted(Palette, "or drag the orange handles in the Scene view");
            tip.style.marginLeft = 8f;
            actions.Add(tip);
            card.Add(actions);
            return card;
        }

        // ---- Actions ----

        private async Task LoadAsync(string text)
        {
            if (cancellation != null)
            {
                return;
            }

            EditorPrefs.SetString(LinkKey, text);
            if (!SimReadyLink.TryParse(text, out _, out string error))
            {
                SimReadyUiKit.SetStatus(Palette, status, error, true);
                return;
            }

            cancellation = new CancellationTokenSource();
            SetBusy(true);
            SimReadyUiKit.SetStatus(Palette, status, null, false);
            var reporter = new Progress<SimReadyProgress>(progress.Report);

            try
            {
                SimReadyLoader.LoadResult result = await SimReadyLoader.LoadAsync(text, policy, null, reporter, cancellation.Token);
                asset = result.Asset;
                Undo.RegisterCreatedObjectUndo(asset.gameObject, "Load SimReady asset");
                Selection.activeGameObject = asset.gameObject;
                FrameInSceneView(asset);
                SimReadyUiKit.SetStatus(Palette, status, $"Loaded {asset.Metadata.AssetName}: {result.Download.Files.Count} files, " +
                    $"{SimReadyWebTransport.Megabytes(result.Download.TotalBytes)}{(result.Download.FromCache ? " from the cache" : string.Empty)}.", false);
                ShowAsset(asset);
            }
            catch (OperationCanceledException)
            {
                SimReadyUiKit.SetStatus(Palette, status, "Cancelled. Downloaded files stay in the cache; the next attempt resumes.", false);
            }
            catch (Exception exception) when (exception is SimReadyDownloadException || exception is UsdImportException || exception is UsdStageException)
            {
                SimReadyUiKit.SetStatus(Palette, status, exception.Message, true);
            }
            finally
            {
                cancellation?.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private async Task SwitchAsync(SimReadyAsset current, UsdVariantSetInfo set, string variant)
        {
            try
            {
                asset = await SimReadyLoader.SwitchVariantAsync(current, set.PrimPath, set.Name, variant);
                Selection.activeGameObject = asset.gameObject;
                SimReadyUiKit.SetStatus(Palette, status, $"{set.Name}: {variant}", false);
                ShowAsset(asset);
            }
            catch (Exception exception) when (exception is UsdImportException || exception is UsdStageException || exception is InvalidOperationException)
            {
                SimReadyUiKit.SetStatus(Palette, status, exception.Message, true);
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

        private static void FrameInSceneView(SimReadyAsset target)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0 || SceneView.lastActiveSceneView == null)
            {
                return;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            SceneView.lastActiveSceneView.Frame(bounds, false);
        }
    }
}
