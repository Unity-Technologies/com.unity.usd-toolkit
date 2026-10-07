using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.USDToolkit.Samples
{
    public sealed class UsdExportExample : MonoBehaviour
    {
        [SerializeField] private GameObject exportRoot;
        [SerializeField] private string outputFolderPath;
        [SerializeField] private string fileName = "runtime-usd-export";
        [SerializeField] private string rootPrimName = "RuntimeExport";
        [SerializeField] private UsdTransformPolicy transformPolicy = UsdTransformPolicy.BakedMesh;
        [SerializeField] private bool includeInactive;
        // Off by default: in the Editor, mesh.vertices works even when isReadable is false.
        // (When on, a non-readable mesh throws. This only matters for exports from a player build.)
        [SerializeField] private bool requireReadableMeshes = false;
        [SerializeField] private bool exportNormals = true;
        [SerializeField] private bool exportUv0 = true;
        [SerializeField] private bool exportBounds = true;
        [SerializeField] private bool exportDisabledRenderers = true;
        [SerializeField] private bool preserveVisibility = true;
        // Ignore the albedo texture when it is misassigned to the metallic slot (prevents mirror-metal black panels).
        [SerializeField] private bool ignoreAlbedoInMetallicSlot = true;
        [SerializeField] private bool captureNativeDiagnostics;

        private string runtimeText;
        private string statusText;
        private string lastOutputPath;
        private Vector2 statusScroll;
        private Vector2 controlScroll;
        private GUIStyle titleStyle;
        private GUIStyle sectionStyle;
        private GUIStyle bodyStyle;
        private GUIStyle smallStyle;
        private GUIStyle panelStyle;
        private GUIStyle buttonStyle;
        private GUIStyle textFieldStyle;
        private Texture2D panelBackground;

        // Output file format (dropdown) and folder icon (generated in code)
        private static readonly string[] UsdFormats = { ".usd", ".usda", ".usdc", ".usdz" };
        private int formatIndex;
        private bool formatDropdownOpen;
        private Texture2D folderIcon;
        private GUIStyle tooltipStyle;
        private Texture2D tooltipBackground;

        private void Start()
        {
            NormalizeFileName();
            EnsureSceneObjects();
            ApplyDefaultPaths();
            RefreshRuntimeInfo();
            statusText = "Ready. Choose an output folder and export the demo hierarchy.";
        }

        private void OnGUI()
        {
            EnsureStyles();

            const float margin = 20.0f;
            const float gap = 16.0f;
            const float minWidth = 720.0f;   // Scale down proportionally only below this width
            const float minHeight = 460.0f;

            // Use a scale of 1 on large enough screens (truly responsive) and shrink only on very narrow ones to avoid clipping.
            float scale = Mathf.Min(1.0f, Mathf.Min(Screen.width / (minWidth + margin * 2.0f), Screen.height / (minHeight + margin * 2.0f)));
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));

            float fullW = Screen.width / scale;
            float fullH = Screen.height / scale;
            if (Event.current.type == EventType.Repaint)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0f, 0f, fullW, fullH), SampleTheme.PageTex);
            }

            float width = fullW - margin * 2.0f;
            float height = fullH - margin * 2.0f;
            GUILayout.BeginArea(new Rect(margin, margin, width, height));
            GUILayout.BeginHorizontal();

            // Split the left and right panels exactly 50/50 at any resolution.
            float half = (width - gap) * 0.5f;
            DrawControlPanel(half, height);
            GUILayout.Space(gap);
            DrawInfoPanel(half, height);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.matrix = previousMatrix;
            DrawTooltip();
        }

        // Draw the hovered control's tooltip (defined in code) as a popup next to the cursor.
        private void DrawTooltip()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            string tip = GUI.tooltip;
            if (string.IsNullOrEmpty(tip))
            {
                return;
            }

            if (tooltipStyle == null)
            {
                tooltipBackground = CreateSolidTexture(new Color(0.06f, 0.07f, 0.09f, 0.97f));
                tooltipStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 12,
                    wordWrap = true,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(8, 8, 6, 6),
                    normal = { textColor = new Color(0.92f, 0.95f, 1.0f, 1.0f), background = tooltipBackground }
                };
            }

            var content = new GUIContent(tip);
            float width = Mathf.Min(380.0f, tooltipStyle.CalcSize(content).x + 4.0f);
            float height = tooltipStyle.CalcHeight(content, width);
            Vector2 mouse = Event.current.mousePosition;
            float x = Mathf.Min(mouse.x + 16.0f, Screen.width - width - 6.0f);
            float y = mouse.y + 18.0f;
            if (y + height > Screen.height)
            {
                y = mouse.y - height - 8.0f;
            }

            GUI.color = Color.white;
            GUI.Box(new Rect(Mathf.Max(4.0f, x), Mathf.Max(4.0f, y), width, height), content, tooltipStyle);
        }

        [ContextMenu("Export USD (Mesh Only)")]
        public void ExportUsdMeshOnly() => ExportUsd(false);

        [ContextMenu("Export USD (with Textures)")]
        public void ExportUsdWithTextures() => ExportUsd(true);

        public void ExportUsd(bool exportTextures)
        {
            try
            {
                ApplyDefaultPaths();
                Directory.CreateDirectory(outputFolderPath);

                string safeFileName = GetOutputFileName();
                string outputPath = Path.Combine(outputFolderPath, safeFileName);
                string diagnosticsPath = Path.Combine(outputFolderPath, Path.GetFileNameWithoutExtension(safeFileName) + "-diagnostics.log");

                // Export the whole open scene, not just the demo (with PBR texture PNGs when exportTextures is on)
                UsdExportResult result = ExportActiveScene(outputPath, diagnosticsPath, exportTextures);

                lastOutputPath = outputPath;
                statusText = result + Environment.NewLine + "Output: " + outputPath;
                Debug.Log(statusText);
            }
            catch (Exception exception)
            {
                statusText = exception.Message;
                Debug.LogException(exception);
            }
        }

        // Gather every root of the open scene under a temporary bundle and export them in one pass,
        // then restore the original hierarchy and order and remove the bundle. (The USD API takes a single root.)
        private UsdExportResult ExportActiveScene(string outputPath, string diagnosticsPath, bool exportTextures)
        {
            Scene scene = gameObject.scene;
            string bundleName = string.IsNullOrWhiteSpace(rootPrimName) ? "Scene" : rootPrimName.Trim();

            var bundle = new GameObject(bundleName);
            bundle.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            bundle.transform.localScale = Vector3.one;

            // Collect every scene root except the bundle and this UI object (keeping the original sibling order)
            var detached = new List<KeyValuePair<Transform, int>>();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == bundle || root == gameObject)
                {
                    continue;
                }

                detached.Add(new KeyValuePair<Transform, int>(root.transform, root.transform.GetSiblingIndex()));
            }

            try
            {
                // Move under the bundle temporarily with worldPositionStays = true to keep position and scale
                foreach (var entry in detached)
                {
                    entry.Key.SetParent(bundle.transform, true);
                }

                return UsdExporter.ExportGameObjectWithResult(bundle, outputPath, new UsdExportOptions
                {
                    RootPrimName = bundleName,
                    MetersPerUnit = 1.0f,
                    IncludeInactive = includeInactive,
                    ExportTextures = exportTextures,
                    // Always false: in the Editor, mesh.vertices works even when isReadable is false.
                    // (Hard-coded instead of the field so a stale serialized value of true in the scene has no effect.)
                    RequireReadableMeshes = false,
                    ExportNormals = exportNormals,
                    ExportUv0 = exportUv0,
                    ExportBounds = exportBounds,
                    ExportDisabledRenderers = exportDisabledRenderers,
                    PreserveInactiveAndDisabledVisibility = preserveVisibility,
                    IgnoreAlbedoInMetallicSlot = ignoreAlbedoInMetallicSlot,
                    TransformPolicy = transformPolicy,
                    CaptureNativeDiagnostics = captureNativeDiagnostics,
                    NativeDiagnosticsLogPath = diagnosticsPath,
                    LogExportSummary = true
                });
            }
            finally
            {
                // Restore the original roots (detach and restore sibling order), then remove the bundle
                foreach (var entry in detached)
                {
                    if (entry.Key != null)
                    {
                        entry.Key.SetParent(null, true);
                        entry.Key.SetSiblingIndex(entry.Value);
                    }
                }

                Destroy(bundle);
            }
        }

        private void DrawControlPanel(float width, float height)
        {
            float inner = width - 56.0f;   // Card padding (40) plus vertical scrollbar (16), so the content doesn't grow wider
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(width), GUILayout.Height(height));
            // Scroll vertically (or drag) when the content is taller than the box. Horizontal scrolling is off and the content wraps in a fixed-width column.
            controlScroll = GUILayout.BeginScrollView(controlScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));
            GUILayout.Label("Runtime USD Export Example", titleStyle);
            GUILayout.Label(runtimeText, smallStyle);
            GUILayout.Space(10.0f);

            GUILayout.Label("Output", sectionStyle);
            GUILayout.Label("Folder", smallStyle);
            GUILayout.BeginHorizontal();
            outputFolderPath = GUILayout.TextField(outputFolderPath ?? string.Empty, SampleTheme.PathField, GUILayout.Width(inner - 50.0f), GUILayout.Height(34.0f));
            if (GUILayout.Button(new GUIContent(SampleTheme.Folder, "Browse for folder"), buttonStyle, GUILayout.Width(44.0f), GUILayout.Height(34.0f)))
            {
                string picked = BrowseFolder("Choose output folder", outputFolderPath);
                if (!string.IsNullOrEmpty(picked))
                {
                    outputFolderPath = picked;
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.Label("File name", smallStyle);
            fileName = GUILayout.TextField(fileName ?? string.Empty, textFieldStyle, GUILayout.Width(inner), GUILayout.Height(34.0f));
            DrawFormatDropdown(inner);
            GUILayout.Label("Root prim", smallStyle);
            rootPrimName = GUILayout.TextField(rootPrimName ?? string.Empty, textFieldStyle, GUILayout.Width(inner), GUILayout.Height(34.0f));

            GUILayout.Space(10.0f);
            GUILayout.Label("Transform Policy", sectionStyle);
            // Split exactly in half with explicit Rects (doesn't rely on style stretching)
            Rect segRow = GUILayoutUtility.GetRect(inner, 36f, GUILayout.Width(inner), GUILayout.Height(36.0f));
            const float segGap = 8.0f;
            float segW = (segRow.width - segGap) * 0.5f;
            var bakedRect = new Rect(segRow.x, segRow.y, segW, segRow.height);
            var hierRect = new Rect(segRow.x + segW + segGap, segRow.y, segW, segRow.height);
            if (GUI.Button(bakedRect, new GUIContent("Baked Mesh", "Bake world transforms into the vertices on export (each mesh becomes a single mesh in world space)"), transformPolicy == UsdTransformPolicy.BakedMesh ? SampleTheme.SegmentOn : SampleTheme.SegmentOff))
            {
                transformPolicy = UsdTransformPolicy.BakedMesh;
            }

            if (GUI.Button(hierRect, new GUIContent("Hierarchy", "Export with the USD Xform hierarchy and each node's local transform preserved"), transformPolicy == UsdTransformPolicy.PreserveHierarchy ? SampleTheme.SegmentOn : SampleTheme.SegmentOff))
            {
                transformPolicy = UsdTransformPolicy.PreserveHierarchy;
            }

            GUILayout.Space(10.0f);
            GUILayout.Label("Options", sectionStyle);
            includeInactive = SampleTheme.Checkbox(includeInactive, new GUIContent("Include inactive objects", "Include inactive GameObjects in the export"));
            requireReadableMeshes = SampleTheme.Checkbox(requireReadableMeshes, new GUIContent("Require readable meshes", "Throw on a mesh with Read/Write disabled (when off, read the GPU buffers back and export a temporary readable copy)"));
            exportNormals = SampleTheme.Checkbox(exportNormals, new GUIContent("Export normals", "Write mesh normals to USD"));
            exportUv0 = SampleTheme.Checkbox(exportUv0, new GUIContent("Export UV0", "Write the first UV set (UV0) to USD"));
            exportBounds = SampleTheme.Checkbox(exportBounds, new GUIContent("Author mesh extent", "Write the mesh bounding box (extent) to the USD prim"));
            exportDisabledRenderers = SampleTheme.Checkbox(exportDisabledRenderers, new GUIContent("Export disabled renderers", "Also export meshes whose Renderer is disabled"));
            preserveVisibility = SampleTheme.Checkbox(preserveVisibility, new GUIContent("Preserve inactive/disabled visibility", "Write the visibility of inactive or disabled objects to USD"));
            ignoreAlbedoInMetallicSlot = SampleTheme.Checkbox(ignoreAlbedoInMetallicSlot, new GUIContent("Ignore albedo texture in metallic slot (fix mirror/black panels)", "Ignore the albedo texture when it is misassigned to the metallic slot and export only the scalar metallic value (prevents mirror-metal black panels)"));
            captureNativeDiagnostics = SampleTheme.Checkbox(captureNativeDiagnostics, new GUIContent("Capture native diagnostics", "Capture OpenUSD native diagnostics and save them as -diagnostics.log"));

            GUILayout.Space(12.0f);
            if (GUILayout.Button("Export USD (Mesh Only)", SampleTheme.PrimaryButton, GUILayout.Width(inner), GUILayout.Height(42.0f)))
            {
                ExportUsd(false);
            }

            GUILayout.Space(6.0f);
            if (GUILayout.Button("Export USD (with Textures)", buttonStyle, GUILayout.Width(inner), GUILayout.Height(40.0f)))
            {
                ExportUsd(true);
            }

            GUILayout.Space(6.0f);
            if (GUILayout.Button("Recreate Demo Geometry", buttonStyle, GUILayout.Width(inner), GUILayout.Height(38.0f)))
            {
                RecreateDemoGeometry();
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawInfoPanel(float width, float height)
        {
            float inner = width - 56.0f;
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(width), GUILayout.Height(height));
            statusScroll = GUILayout.BeginScrollView(statusScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));
            GUILayout.Label("Export Target", titleStyle, GUILayout.Width(inner));
            GUILayout.Space(8.0f);
            GUILayout.Label("Root object: " + (exportRoot != null ? exportRoot.name : "(none)"), bodyStyle, GUILayout.Width(inner));
            GUILayout.Label("Output: " + (string.IsNullOrWhiteSpace(lastOutputPath) ? Path.Combine(outputFolderPath ?? string.Empty, fileName ?? string.Empty) : lastOutputPath), bodyStyle, GUILayout.Width(inner));
            GUILayout.Label("The output folder can be changed after the app is built. Use an absolute path for predictable results on macOS and Windows.", smallStyle, GUILayout.Width(inner));
            GUILayout.Space(14.0f);
            GUILayout.Label("Status", sectionStyle, GUILayout.Width(inner));
            GUILayout.Label(statusText ?? string.Empty, bodyStyle, GUILayout.Width(inner));
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void EnsureSceneObjects()
        {
            if (Camera.main == null)
            {
                var cameraObject = new GameObject("Main Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.tag = "MainCamera";
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.05f, 0.06f, 0.075f, 1.0f);
                cameraObject.transform.position = new Vector3(4.0f, 3.0f, -5.0f);
                cameraObject.transform.LookAt(Vector3.zero);
            }

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Key Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                lightObject.transform.rotation = Quaternion.Euler(45.0f, -35.0f, 0.0f);
            }

            // Don't create the demo geometry automatically. Export always targets the whole open scene.
            // (To get the demo, create it explicitly with the "Recreate Demo Geometry" button.)
        }

        private void ApplyDefaultPaths()
        {
            if (string.IsNullOrWhiteSpace(outputFolderPath))
            {
                outputFolderPath = Path.Combine(Application.persistentDataPath, "UsdExports");
            }

            Directory.CreateDirectory(outputFolderPath);
        }

        private void RefreshRuntimeInfo()
        {
            try
            {
                runtimeText = UsdExporter.GetRuntimeInfo().ToString();
            }
            catch (Exception exception)
            {
                runtimeText = "Runtime unavailable: " + exception.Message;
            }
        }

        private void RecreateDemoGeometry()
        {
            if (exportRoot != null)
            {
                Destroy(exportRoot);
            }

            exportRoot = CreateDemoGeometry();
            statusText = "Recreated demo geometry.";
            FrameCamera(exportRoot);
        }

        private GameObject CreateDemoGeometry()
        {
            var root = new GameObject("Runtime Export Root");

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Blue Cube";
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(-0.75f, 0.55f, 0.0f);
            cube.transform.localRotation = Quaternion.Euler(0.0f, 28.0f, 0.0f);
            cube.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);
            SetMaterial(cube, new Color(0.14f, 0.55f, 0.95f, 1.0f));

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Green Sphere";
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.localPosition = new Vector3(0.8f, 0.45f, 0.2f);
            sphere.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            SetMaterial(sphere, new Color(0.18f, 0.72f, 0.42f, 1.0f));

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Matte Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localPosition = new Vector3(0.0f, -0.05f, 0.0f);
            floor.transform.localScale = new Vector3(3.0f, 0.1f, 2.2f);
            SetMaterial(floor, new Color(0.38f, 0.39f, 0.42f, 1.0f));

            FrameCamera(root);
            return root;
        }

        private void SetMaterial(GameObject target, Color color)
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Standard") ??
                Shader.Find("Sprites/Default");

            if (shader == null)
            {
                return;
            }

            var material = new Material(shader);
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            renderer.sharedMaterial = material;
        }

        private void FrameCamera(GameObject target)
        {
            Camera camera = Camera.main;
            if (camera == null || target == null)
            {
                return;
            }

            Bounds bounds = CalculateBounds(target);
            float radius = Mathf.Max(bounds.extents.magnitude, 1.0f);
            Vector3 direction = new Vector3(0.65f, 0.45f, -0.75f).normalized;
            camera.transform.position = bounds.center - direction * radius * 2.7f;
            camera.transform.rotation = Quaternion.LookRotation(bounds.center - camera.transform.position, Vector3.up);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = Mathf.Max(1000.0f, radius * 8.0f);
        }

        private Bounds CalculateBounds(GameObject target)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(target.transform.position, Vector3.one);
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            SampleTheme.EnsureBuilt();

            GUI.skin = Instantiate(GUI.skin);
            GUI.skin.box.normal.background = CreateSolidTexture(SampleTheme.PageBg);
            GUI.skin.label.wordWrap = true;

            titleStyle = SampleTheme.Title;
            sectionStyle = SampleTheme.Section;
            bodyStyle = SampleTheme.Body;
            smallStyle = SampleTheme.FieldLabel;
            panelStyle = SampleTheme.Card;
            buttonStyle = SampleTheme.Button;
            textFieldStyle = SampleTheme.Field;
        }

        private static Texture2D CreateSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        // Build the output file name from the base name (no extension) and the dropdown format
        private string GetOutputFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(fileName) ? "runtime-usd-export" : fileName.Trim();
            baseName = Path.GetFileNameWithoutExtension(baseName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "runtime-usd-export";
            }

            int idx = Mathf.Clamp(formatIndex, 0, UsdFormats.Length - 1);
            return baseName + UsdFormats[idx];
        }

        // If an old serialized file name includes an extension (.usda and so on), strip it and use that format as the dropdown default.
        private void NormalizeFileName()
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "runtime-usd-export";
                return;
            }

            string ext = Path.GetExtension(fileName);
            for (int i = 0; i < UsdFormats.Length; i++)
            {
                if (string.Equals(ext, UsdFormats[i], StringComparison.OrdinalIgnoreCase))
                {
                    formatIndex = i;
                    break;
                }
            }

            fileName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "runtime-usd-export";
            }
        }

        // Format dropdown (runtime IMGUI): pressing the button expands the list below it.
        private void DrawFormatDropdown(float inner)
        {
            GUILayout.Label("Format", smallStyle);
            int idx = Mathf.Clamp(formatIndex, 0, UsdFormats.Length - 1);
            Rect ddRect = GUILayoutUtility.GetRect(inner, 34f, GUILayout.Width(inner), GUILayout.Height(34.0f));
            if (GUI.Button(ddRect, UsdFormats[idx], SampleTheme.Dropdown))
            {
                formatDropdownOpen = !formatDropdownOpen;
            }

            if (Event.current.type == EventType.Repaint && SampleTheme.Chevron != null)
            {
                float cs = 16.0f;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(ddRect.xMax - cs - 12.0f, ddRect.y + (ddRect.height - cs) * 0.5f, cs, cs), SampleTheme.Chevron);
            }

            if (formatDropdownOpen)
            {
                for (int i = 0; i < UsdFormats.Length; i++)
                {
                    if (GUILayout.Button(UsdFormats[i], buttonStyle, GUILayout.Width(inner), GUILayout.Height(28.0f)))
                    {
                        formatIndex = i;
                        formatDropdownOpen = false;
                    }
                }
            }
        }

        // Folder picker: OpenFolderPanel in the Editor (Windows/macOS/Linux), the native OS dialog in a build.
        private static string BrowseFolder(string title, string startDir)
        {
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFolderPanel(
                title, string.IsNullOrEmpty(startDir) ? string.Empty : startDir, string.Empty);
            return string.IsNullOrEmpty(path) ? null : path;
#else
            return RunNativeFolderDialog(title);
#endif
        }

        // Native OS folder dialog for player builds (Windows/macOS/Linux)
        private static string RunNativeFolderDialog(string title)
        {
            try
            {
                string exe;
                string args;
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                        exe = "powershell";
                        args = "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                               "$d = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                               "if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.SelectedPath) }\"";
                        break;
                    case RuntimePlatform.OSXPlayer:
                        exe = "/usr/bin/osascript";
                        args = "-e \"POSIX path of (choose folder with prompt \\\"" + title + "\\\")\"";
                        break;
                    case RuntimePlatform.LinuxPlayer:
                        exe = "zenity";
                        args = "--file-selection --directory --title=\"" + title + "\"";
                        break;
                    default:
                        return null;
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    output = output == null ? null : output.Trim();
                    return string.IsNullOrEmpty(output) ? null : output;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Native folder dialog failed: " + exception.Message);
                return null;
            }
        }

        // Folder icon generated in code (no download or license needed; shared by the Editor and runtime)
        private Texture2D GetFolderIcon()
        {
            if (folderIcon != null)
            {
                return folderIcon;
            }

            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(0.0f, 0.0f, 0.0f, 0.0f);
            }

            Color body = new Color(1.0f, 0.80f, 0.36f, 1.0f);
            Color edge = new Color(0.45f, 0.32f, 0.10f, 1.0f);
            Color tab = new Color(1.0f, 0.87f, 0.52f, 1.0f);
            FillIconRect(pixels, size, 3, 5, 28, 22, body, edge);
            FillIconRect(pixels, size, 4, 21, 14, 26, tab, edge);

            texture.SetPixels(pixels);
            texture.Apply();
            folderIcon = texture;
            return texture;
        }

        private static void FillIconRect(Color[] pixels, int size, int x0, int y0, int x1, int y1, Color fill, Color edge)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (x < 0 || x >= size || y < 0 || y >= size)
                    {
                        continue;
                    }

                    bool border = x == x0 || x == x1 || y == y0 || y == y1;
                    pixels[y * size + x] = border ? edge : fill;
                }
            }
        }
    }
}
