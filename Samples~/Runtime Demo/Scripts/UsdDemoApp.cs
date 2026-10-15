using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Entry point. Builds the whole demo - camera, lighting, HUD, gizmo - at runtime.
    /// </summary>
    /// <remarks>
    /// Nothing here is authored in the scene beyond a single GameObject carrying this component.
    /// That is on purpose: a demo that constructs itself cannot turn up on the day with a broken
    /// prefab link, a missing font asset or a camera someone nudged.
    /// </remarks>
    [DefaultExecutionOrder(-100)]
    public sealed class UsdDemoApp : MonoBehaviour
    {
        private static readonly string[] UsdExtensions = { "usd", "usda", "usdc", "usdz" };

        private Camera mainCamera;
        private OrbitCamera orbitCamera;
        private Transform sceneRoot;
        private SelectionOutline outline;
        private RuntimeTransformGizmo gizmo;
        [Tooltip("The HUD instance in the scene (Prefabs/DemoHud.prefab).")]
        [SerializeField] private DemoHud hud;

        private UsdImportService importService;
        private UsdExportService exportService;

        private readonly List<UsdSceneEntry> entries = new List<UsdSceneEntry>();
        private readonly Dictionary<Transform, UsdSceneEntry> entriesByRoot = new Dictionary<Transform, UsdSceneEntry>();
        private UsdSceneEntry selected;

        private UsdExportMode exportMode = UsdExportMode.Referencing;
        private UsdExportFormat exportFormat = UsdExportFormat.Usdc;
        private bool useRelativeReferencePaths = true;
        private bool busy;
        private string lastBrowseDirectory;

        // Left mouse: click selects, drag pans. Small enough to feel immediate, large enough
        // that a shaky click on a presenter remote still counts as a click.
        private const float DragThresholdPixels = 6.0f;
        private bool leftPressPending;
        private bool leftPanning;
        private Vector2 leftPressPosition;
        private Ray leftPressRay;

        private void Awake()
        {
            // The native file panel takes focus away from the player. Without this the player
            // stops ticking the moment the dialog opens, the coroutine waiting on the panel
            // stalls, and the app only unfreezes when someone clicks back on the window - which
            // on stage looks exactly like a hang.
            Application.runInBackground = true;

            BuildEnvironment();
            BuildSceneRoots();
            BuildHud();

            importService = new UsdImportService(sceneRoot);
            exportService = new UsdExportService(sceneRoot);

            lastBrowseDirectory = ResolveSampleDirectory();

            RefreshAllUi();
            ReportRuntime();
        }

        // ---------------------------------------------------------------- construction

        private void BuildEnvironment()
        {
            var cameraObject = new GameObject("DemoCamera");
            cameraObject.transform.SetParent(transform, false);
            mainCamera = cameraObject.AddComponent<Camera>();
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = new Color(0.055f, 0.062f, 0.075f, 1.0f);
            mainCamera.fieldOfView = 50.0f;
            mainCamera.nearClipPlane = 0.02f;
            mainCamera.farClipPlane = 500.0f;
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<AudioListener>();
            orbitCamera = cameraObject.AddComponent<OrbitCamera>();

            var keyLightObject = new GameObject("KeyLight");
            keyLightObject.transform.SetParent(transform, false);
            keyLightObject.transform.rotation = Quaternion.Euler(48.0f, 41.0f, 0.0f);
            Light keyLight = keyLightObject.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 1.5f;
            keyLight.color = new Color(1.0f, 0.97f, 0.92f);
            keyLight.shadows = LightShadows.Soft;

            var fillLightObject = new GameObject("FillLight");
            fillLightObject.transform.SetParent(transform, false);
            fillLightObject.transform.rotation = Quaternion.Euler(18.0f, -135.0f, 0.0f);
            Light fillLight = fillLightObject.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.45f;
            fillLight.color = new Color(0.72f, 0.80f, 0.95f);
            fillLight.shadows = LightShadows.None;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.22f, 0.25f, 0.30f);
            RenderSettings.ambientEquatorColor = new Color(0.16f, 0.17f, 0.20f);
            RenderSettings.ambientGroundColor = new Color(0.07f, 0.07f, 0.08f);

            BuildGrid();
            EnsureEventSystem();
        }

        private void BuildGrid()
        {
            const int halfLines = 20;
            const float spacing = 0.25f;
            float extent = halfLines * spacing;

            var vertices = new List<Vector3>();
            var indices = new List<int>();

            for (int i = -halfLines; i <= halfLines; i++)
            {
                float offset = i * spacing;
                vertices.Add(new Vector3(offset, 0.0f, -extent));
                vertices.Add(new Vector3(offset, 0.0f, extent));
                vertices.Add(new Vector3(-extent, 0.0f, offset));
                vertices.Add(new Vector3(extent, 0.0f, offset));
            }

            for (int i = 0; i < vertices.Count; i++)
            {
                indices.Add(i);
            }

            var mesh = new Mesh { name = "DemoGrid", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var gridObject = new GameObject("Grid");
            gridObject.transform.SetParent(transform, false);
            gridObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            Renderer renderer = gridObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateUnlitMaterial(new Color(0.24f, 0.27f, 0.32f, 1.0f));
            renderer.enabled = renderer.sharedMaterial != null;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// Finds a shader by name, or returns null with a warning. A player only contains shaders
        /// something references (Resources/RuntimeShaders exists for that), and a null shader must
        /// cost a visual, never the rest of Awake: an exception here once meant no HUD at all.
        /// </summary>
        private static Shader FindShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader shader = Shader.Find(name);
                if (shader != null)
                {
                    return shader;
                }
            }

            Debug.LogWarning(
                $"Shader '{names[0]}' is not in this build. Run Tools > USD Runtime Demo > " +
                "Regenerate Runtime Shader Includes and rebuild.");
            return null;
        }

        private static Material CreateUnlitMaterial(Color color)
        {
            Shader shader = FindShader("Universal Render Pipeline/Unlit", "Unlit/Color");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader) { hideFlags = HideFlags.DontSave };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            material.color = color;
            return material;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var eventSystemObject = new GameObject("EventSystem");
            eventSystemObject.transform.SetParent(transform, false);
            eventSystemObject.AddComponent<EventSystem>();
            // The project is set to the new Input System only, so the legacy
            // StandaloneInputModule would silently never deliver a UI click here.
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
        }

        private void BuildSceneRoots()
        {
            var sceneRootObject = new GameObject("UsdScene");
            sceneRootObject.transform.SetParent(transform, false);
            sceneRoot = sceneRootObject.transform;

            var outlineObject = new GameObject("SelectionOutline");
            outlineObject.transform.SetParent(transform, false);
            outline = outlineObject.AddComponent<SelectionOutline>();
            outline.Initialise(CreateOutlineMaterial(), CreateMaskMaterial());

            var gizmoObject = new GameObject("TransformGizmo");
            gizmoObject.transform.SetParent(transform, false);
            gizmo = gizmoObject.AddComponent<RuntimeTransformGizmo>();
            gizmo.Initialise(mainCamera, CreateGizmoMaterial());
            gizmo.TransformChanged += OnGizmoTransformChanged;
        }

        private static Material CreateOutlineMaterial()
        {
            Shader shader = FindShader("USDRuntimeDemo/SelectionOutline");
            if (shader == null)
            {
                Debug.LogWarning("Selection outline shader missing; selection will show without an outline.");
                return null;
            }

            var material = new Material(shader) { hideFlags = HideFlags.DontSave };
            material.SetColor("_OutlineColor", DemoTheme.Accent);
            material.SetFloat("_OutlineWidth", 3.5f);
            return material;
        }

        private static Material CreateMaskMaterial()
        {
            Shader shader = FindShader("USDRuntimeDemo/SelectionMask");
            return shader == null ? null : new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        private static Material CreateGizmoMaterial()
        {
            Shader shader = FindShader("USDRuntimeDemo/GizmoHandle", "Universal Render Pipeline/Unlit");
            return shader == null ? null : new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        private void BuildHud()
        {
            if (hud == null)
            {
                hud = FindAnyObjectByType<DemoHud>();
            }

            if (hud == null)
            {
                throw new InvalidOperationException(
                    "No DemoHud in the scene. Add Prefabs/DemoHud.prefab and assign it to UsdDemoApp.");
            }

            hud.ImportRequested += () => StartFlow(ImportFlow());
            hud.ExportRequested += () => StartFlow(ExportFlow());
            hud.ClearRequested += ClearScene;
            hud.GizmoModeChanged += SetGizmoMode;
            hud.EntryClicked += Select;

            hud.ExportModeChanged += mode =>
            {
                exportMode = mode;
                RefreshExportUi();
            };

            hud.ExportFormatChanged += format =>
            {
                exportFormat = format;
                RefreshExportUi();
            };

            hud.RelativePathsChanged += relative =>
            {
                useRelativeReferencePaths = relative;
                RefreshExportUi();
            };

            hud.UpAxisModeChanged += mode =>
            {
                importService.UpAxisMode = mode;
                hud.SetConversionModes(importService.UpAxisMode, importService.UnitMode);
                hud.SetStatus(
                    mode == UsdConversionMode.Auto
                        ? "Z-up stages will be rotated to Y-up on the next import."
                        : "Z-up correction off — the next import lands exactly as the toolkit produces it.",
                    StatusKind.Info);
            };

            hud.UnitModeChanged += mode =>
            {
                importService.UnitMode = mode;
                hud.SetConversionModes(importService.UpAxisMode, importService.UnitMode);
                hud.SetStatus(
                    mode == UsdConversionMode.Auto
                        ? "metersPerUnit will be applied on the next import."
                        : "Unit correction off — a centimetre stage will import 100x too large.",
                    StatusKind.Info);
            };
        }

        private void ReportRuntime()
        {
            try
            {
                UsdRuntimeInfo info = UsdExporter.GetRuntimeInfo();
                hud.SetRuntimeInfo($"USD Toolkit · OpenUSD {info.OpenUsdVersion} · API {info.NativeApiVersion}");
                hud.SetStatus("Ready. Import a USD file to begin.", StatusKind.Info);
            }
            catch (System.Exception exception)
            {
                hud.SetRuntimeInfo("USD native runtime unavailable");
                hud.SetStatus("USD native plugin did not load: " + exception.Message, StatusKind.Error);
            }
        }

        // ---------------------------------------------------------------- input

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || mainCamera == null)
            {
                return;
            }

            HandleKeyboard();

            Ray ray = mainCamera.ScreenPointToRay(mouse.position.ReadValue());

            if (gizmo.IsDragging)
            {
                if (mouse.leftButton.isPressed)
                {
                    gizmo.UpdateDrag(ray);
                }
                else
                {
                    gizmo.EndDrag();
                }

                return;
            }

            // A left press that missed the gizmo is a click until it travels; then it is a pan.
            // Both continue even if the cursor wanders over the UI.
            if (leftPressPending)
            {
                if (!mouse.leftButton.isPressed)
                {
                    leftPressPending = false;
                    if (!busy)
                    {
                        Select(Physics.Raycast(leftPressRay, out RaycastHit hit, 1000.0f)
                            ? FindEntry(hit.transform)
                            : null);
                    }
                }
                else if ((mouse.position.ReadValue() - leftPressPosition).sqrMagnitude >
                         DragThresholdPixels * DragThresholdPixels)
                {
                    leftPressPending = false;
                    // Anchor at the press point, so the scene does not jump by the threshold.
                    orbitCamera.BeginGrab(leftPressRay);
                    leftPanning = true;
                }

                return;
            }

            if (leftPanning)
            {
                if (!mouse.leftButton.isPressed)
                {
                    leftPanning = false;
                    orbitCamera.EndGrab();
                }

                return;
            }

            bool pointerOverUi = EventSystem.current != null &&
                                 EventSystem.current.IsPointerOverGameObject();

            // The sidebar scrolls now, so the wheel means two things depending on where the
            // cursor is. Whoever it is over wins.
            orbitCamera.PointerOverUi = pointerOverUi;

            if (pointerOverUi || orbitCamera.IsGrabbing)
            {
                return;
            }

            gizmo.UpdateHover(ray);

            if (!mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            // The gizmo gets first refusal, so grabbing a handle that overlaps a part never
            // selects the part behind it.
            if (!busy && gizmo.TryBeginDrag(ray))
            {
                return;
            }

            // Selection waits for the release, so dragging off a part pans instead of selecting.
            leftPressPending = true;
            leftPressPosition = mouse.position.ReadValue();
            leftPressRay = ray;
        }

        private void HandleKeyboard()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.wKey.wasPressedThisFrame)
            {
                SetGizmoMode(GizmoMode.Translate);
            }
            else if (keyboard.eKey.wasPressedThisFrame)
            {
                SetGizmoMode(GizmoMode.Rotate);
            }
            else if (keyboard.hKey.wasPressedThisFrame)
            {
                // Collapses the sidebar for a full-bleed look at the part. The edge tab stays
                // put, so there is always a visible way back.
                hud.TogglePanel();
            }
            else if (keyboard.fKey.wasPressedThisFrame)
            {
                FrameSelectionOrAll();
            }
            else if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Select(null);
            }
        }

        private UsdSceneEntry FindEntry(Transform hit)
        {
            Transform current = hit;
            while (current != null)
            {
                if (entriesByRoot.TryGetValue(current, out UsdSceneEntry entry))
                {
                    return entry;
                }

                current = current.parent;
            }

            return null;
        }

        private void SetGizmoMode(GizmoMode mode)
        {
            gizmo.Mode = mode;
            hud.SetGizmoMode(mode);
        }

        private void Select(UsdSceneEntry entry)
        {
            selected = entry;
            gizmo.SetTarget(entry?.Root.transform);

            if (entry == null)
            {
                outline.Clear();
            }
            else
            {
                outline.Show(entry);
            }

            hud.SetSelection(entry);
            hud.SetLoadedEntries(entries, selected);
        }

        private void OnGizmoTransformChanged()
        {
            if (selected == null)
            {
                return;
            }

            hud.SetStatus(
                $"{selected.DisplayName} moved. Export will write its new placement.",
                StatusKind.Info);
        }

        // ---------------------------------------------------------------- flows

        private void StartFlow(IEnumerator flow)
        {
            if (busy)
            {
                return;
            }

            StartCoroutine(RunGuarded(flow));
        }

        /// <summary>
        /// Steps a flow (and any coroutines it yields) by hand so an exception anywhere in it
        /// releases the busy lock instead of leaving the HUD disabled for good.
        /// </summary>
        private IEnumerator RunGuarded(IEnumerator flow)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(flow);
            while (stack.Count > 0)
            {
                object current;
                try
                {
                    if (!stack.Peek().MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }

                    current = stack.Peek().Current;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    hud.SetStatus("Operation failed: " + exception.Message, StatusKind.Error);
                    busy = false;
                    hud.SetBusy(false);
                    yield break;
                }

                if (current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                yield return current;
            }
        }

        private IEnumerator ImportFlow()
        {
            busy = true;
            hud.SetBusy(true);

            string[] picked = null;
            yield return NativeFileDialog.OpenFiles(
                "Select USD file(s) to import",
                lastBrowseDirectory,
                UsdExtensions,
                true,
                result => picked = result);

            if (picked == null || picked.Length == 0)
            {
                hud.SetStatus("Import cancelled.", StatusKind.Info);
                busy = false;
                hud.SetBusy(false);
                yield break;
            }

            lastBrowseDirectory = Path.GetDirectoryName(picked[0]);

            int succeeded = 0;
            foreach (string path in picked)
            {
                bool ok = false;
                yield return ImportOne(path, result => ok = result);

                if (!ok)
                {
                    RefreshAllUi();
                    busy = false;
                    hud.SetBusy(false);
                    yield break;
                }

                succeeded++;
            }

            RefreshAllUi();

            UsdSceneEntry last = entries.Count > 0 ? entries[entries.Count - 1] : null;
            string conversionNote = last != null && !last.Conversion.IsIdentity
                ? $" ({last.Conversion.Describe()})"
                : string.Empty;

            hud.SetStatus(
                $"Loaded {succeeded} file(s). {entries.Count} stage(s) in the scene{conversionNote}.",
                StatusKind.Success);

            busy = false;
            hud.SetBusy(false);
        }

        /// <summary>
        /// Imports one file by absolute path, skipping the file dialog. Public so the demo can be
        /// driven from a script or a smoke test without a human at the panel; the interactive
        /// import goes through exactly the same coroutine.
        /// </summary>
        public void LoadFile(string absolutePath)
        {
            StartFlow(LoadFileFlow(absolutePath));
        }

        private IEnumerator LoadFileFlow(string absolutePath)
        {
            busy = true;
            hud.SetBusy(true);

            bool ok = false;
            yield return ImportOne(absolutePath, result => ok = result);

            RefreshAllUi();
            if (ok)
            {
                hud.SetStatus($"Loaded {Path.GetFileName(absolutePath)}. {entries.Count} stage(s) in the scene.",
                    StatusKind.Success);
            }

            busy = false;
            hud.SetBusy(false);
        }

        private IEnumerator ImportOne(string path, Action<bool> onComplete)
        {
            string fileName = Path.GetFileName(path);
            hud.SetStatus($"Importing {fileName}…", StatusKind.Busy);

            UsdSceneEntry imported = null;
            string failure = null;

            yield return importService.Import(
                path,
                (fraction, phase) => hud.SetStatus(
                    $"Importing {fileName} — {phase} {Mathf.RoundToInt(fraction * 100.0f)}%",
                    StatusKind.Busy),
                entry => imported = entry,
                message => failure = message);

            if (imported == null)
            {
                hud.SetStatus(failure ?? $"Import failed: {fileName}", StatusKind.Error);
                onComplete?.Invoke(false);
                yield break;
            }

            entries.Add(imported);
            entriesByRoot[imported.Root.transform] = imported;
            Select(imported);
            FrameLoadedContent();
            onComplete?.Invoke(true);
        }

        private IEnumerator ExportFlow()
        {
            if (entries.Count == 0)
            {
                hud.SetStatus("Nothing to export yet — import a USD file first.", StatusKind.Warning);
                yield break;
            }

            busy = true;
            hud.SetBusy(true);

            UsdExportFormat effectiveFormat = UsdExportService.EffectiveFormat(exportMode, exportFormat);
            string extension = UsdExportService.Extension(effectiveFormat);
            string suggested = UsdExportService.SuggestedFileName(exportMode, exportFormat);

            string destination = null;
            yield return NativeFileDialog.SaveFile(
                exportMode == UsdExportMode.Referencing
                    ? "Export assembled stage (referencing)"
                    : "Export assembled stage (flattened)",
                lastBrowseDirectory,
                suggested,
                extension,
                result => destination = result);

            if (string.IsNullOrEmpty(destination))
            {
                hud.SetStatus("Export cancelled.", StatusKind.Info);
                busy = false;
                hud.SetBusy(false);
                yield break;
            }

            hud.SetStatus($"Exporting {Path.GetFileName(destination)}…", StatusKind.Busy);
            yield return null;

            UsdExportOutcome outcome = exportService.Export(
                destination,
                exportMode,
                entries,
                useRelativeReferencePaths);

            if (!outcome.Success)
            {
                hud.SetStatus(outcome.Message, StatusKind.Error);
            }
            else if (outcome.Warnings.Count > 0)
            {
                hud.SetStatus(outcome.Message + "  ⚠ " + outcome.Warnings[0], StatusKind.Warning);
            }
            else
            {
                hud.SetStatus(outcome.Message, StatusKind.Success);
                lastBrowseDirectory = Path.GetDirectoryName(outcome.OutputPath);
            }

            busy = false;
            hud.SetBusy(false);
        }

        private void ClearScene()
        {
            Select(null);

            foreach (UsdSceneEntry entry in entries)
            {
                if (entry.Root != null)
                {
                    Destroy(entry.Root);
                }
            }

            entries.Clear();
            entriesByRoot.Clear();
            RefreshAllUi();
            hud.SetStatus("Scene cleared.", StatusKind.Info);
        }

        private void FrameLoadedContent()
        {
            FrameEntries(entries);
        }

        /// <summary>F key: the selected part if there is one, otherwise everything loaded.</summary>
        private void FrameSelectionOrAll()
        {
            if (selected != null)
            {
                FrameEntries(new[] { selected });
            }
            else
            {
                FrameEntries(entries);
            }
        }

        private void FrameEntries(IEnumerable<UsdSceneEntry> toFrame)
        {
            Bounds? bounds = null;
            foreach (UsdSceneEntry entry in toFrame)
            {
                foreach (Renderer renderer in entry.Renderers)
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    bounds = bounds.HasValue ? Encapsulate(bounds.Value, renderer.bounds) : renderer.bounds;
                }
            }

            if (bounds.HasValue)
            {
                orbitCamera.Frame(bounds.Value);
            }
        }

        private static Bounds Encapsulate(Bounds bounds, Bounds other)
        {
            bounds.Encapsulate(other);
            return bounds;
        }

        // ---------------------------------------------------------------- ui refresh

        private void RefreshAllUi()
        {
            hud.SetLoadedEntries(entries, selected);
            hud.SetSelection(selected);
            hud.SetGizmoMode(gizmo.Mode);
            hud.SetConversionModes(importService.UpAxisMode, importService.UnitMode);
            RefreshExportUi();
        }

        private void RefreshExportUi()
        {
            hud.SetExportMode(exportMode);
            bool referencing = exportMode == UsdExportMode.Referencing;
            hud.SetExportFormat(
                UsdExportService.EffectiveFormat(exportMode, exportFormat),
                !referencing);
            hud.SetRelativePaths(useRelativeReferencePaths, referencing);
        }

        /// <summary>
        /// The pre-validated sample files. They ship in the sample's USDSamples folder; a player
        /// build reads them from StreamingAssets/USDSamples, where the build preprocessor in
        /// Editor/DemoSampleFilesBuildStep.cs copies them.
        /// </summary>
        private static string ResolveSampleDirectory()
        {
            string samples = Path.Combine(Application.streamingAssetsPath, "USDSamples");
            if (Directory.Exists(samples))
            {
                return samples;
            }

#if UNITY_EDITOR
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("USDSamples t:DefaultAsset"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (UnityEditor.AssetDatabase.IsValidFolder(path) && path.EndsWith("/USDSamples", StringComparison.Ordinal))
                {
                    return Path.GetFullPath(path);
                }
            }
#endif

            return Directory.Exists(Application.streamingAssetsPath)
                ? Application.streamingAssetsPath
                : Application.persistentDataPath;
        }
    }
}
