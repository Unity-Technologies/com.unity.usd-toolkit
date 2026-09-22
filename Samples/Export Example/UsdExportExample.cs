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
        // 에디터에서는 isReadable=false 메시도 mesh.vertices로 읽히므로 기본 false.
        // (true로 켜면 readable 아닌 메시에서 예외 — 빌드 플레이어 export 시에만 의미 있음)
        [SerializeField] private bool requireReadableMeshes = false;
        [SerializeField] private bool exportNormals = true;
        [SerializeField] private bool exportUv0 = true;
        [SerializeField] private bool exportBounds = true;
        [SerializeField] private bool exportDisabledRenderers = true;
        [SerializeField] private bool preserveVisibility = true;
        // metallic 슬롯에 albedo와 같은 텍스처가 꽂힌 오배치를 무시(거울 금속 → 검은 패널 방지).
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

        // 출력 파일 포맷(드롭다운) + 폴더 아이콘(코드 생성)
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
            const float minWidth = 720.0f;   // 이보다 좁아질 때만 비례 축소
            const float minHeight = 460.0f;

            // 화면이 충분히 크면 스케일 1배(진짜 반응형), 아주 좁을 때만 축소해 잘림 방지.
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

            // 해상도와 무관하게 좌/우 패널을 정확히 반반(50/50)으로.
            float half = (width - gap) * 0.5f;
            DrawControlPanel(half, height);
            GUILayout.Space(gap);
            DrawInfoPanel(half, height);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.matrix = previousMatrix;
            DrawTooltip();
        }

        // 마우스 올린 컨트롤의 tooltip(코드 기반 설명)을 커서 옆에 팝업으로 그린다.
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

                // 데모가 아니라 현재 열려있는 씬 전체를 export (exportTextures면 PBR 텍스처 PNG 동반)
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

        // 현재 열려있는 씬의 모든 루트를 임시 묶음(bundle) 아래로 모아 한 번에 export한 뒤,
        // 원래 계층/순서로 복원하고 묶음을 제거한다. (USD API가 단일 root만 받기 때문)
        private UsdExportResult ExportActiveScene(string outputPath, string diagnosticsPath, bool exportTextures)
        {
            Scene scene = gameObject.scene;
            string bundleName = string.IsNullOrWhiteSpace(rootPrimName) ? "Scene" : rootPrimName.Trim();

            var bundle = new GameObject(bundleName);
            bundle.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            bundle.transform.localScale = Vector3.one;

            // 묶음 자신과 이 UI 오브젝트를 제외한 모든 씬 루트를 수집 (원래 sibling 순서 보존)
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
                // worldPositionStays = true 로 좌표/스케일 유지하며 임시로 묶음 아래로 이동
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
                    // 에디터에서는 isReadable=false 메시도 mesh.vertices로 읽히므로 항상 false로 고정한다.
                    // (씬에 직렬화된 인스턴스의 옛 값(true)에 영향받지 않도록 필드 대신 false 하드코딩)
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
                // 원래 루트로 복원 (계층 분리 + sibling 순서 되돌림) 후 묶음 제거
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
            float inner = width - 56.0f;   // 카드 패딩(40) + 세로 스크롤바(16) 여유 → content 부풀림 차단
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(width), GUILayout.Height(height));
            // 내용이 박스 높이를 넘으면 세로 스크롤(드래그) 가능. 가로는 막고 고정폭 컬럼으로 감싼다.
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
            // 명시적 Rect 로 정확히 반반 분할(스타일 stretch 동작에 의존하지 않음)
            Rect segRow = GUILayoutUtility.GetRect(inner, 36f, GUILayout.Width(inner), GUILayout.Height(36.0f));
            const float segGap = 8.0f;
            float segW = (segRow.width - segGap) * 0.5f;
            var bakedRect = new Rect(segRow.x, segRow.y, segW, segRow.height);
            var hierRect = new Rect(segRow.x + segW + segGap, segRow.y, segW, segRow.height);
            if (GUI.Button(bakedRect, new GUIContent("Baked Mesh", "월드 트랜스폼을 정점에 구워 export (각 메시를 월드 좌표 기준 단일 메시로)"), transformPolicy == UsdTransformPolicy.BakedMesh ? SampleTheme.SegmentOn : SampleTheme.SegmentOff))
            {
                transformPolicy = UsdTransformPolicy.BakedMesh;
            }

            if (GUI.Button(hierRect, new GUIContent("Hierarchy", "USD Xform 계층과 각 노드의 로컬 트랜스폼을 보존해 export"), transformPolicy == UsdTransformPolicy.PreserveHierarchy ? SampleTheme.SegmentOn : SampleTheme.SegmentOff))
            {
                transformPolicy = UsdTransformPolicy.PreserveHierarchy;
            }

            GUILayout.Space(10.0f);
            GUILayout.Label("Options", sectionStyle);
            includeInactive = SampleTheme.Checkbox(includeInactive, new GUIContent("Include inactive objects", "비활성(inactive) GameObject도 export 대상에 포함한다"));
            requireReadableMeshes = SampleTheme.Checkbox(requireReadableMeshes, new GUIContent("Require readable meshes", "Read/Write 꺼진 메시를 만나면 예외 발생 (끄면 GPU 버퍼를 readback해 임시 readable 사본으로 export)"));
            exportNormals = SampleTheme.Checkbox(exportNormals, new GUIContent("Export normals", "메시 노멀(normals)을 USD에 기록한다"));
            exportUv0 = SampleTheme.Checkbox(exportUv0, new GUIContent("Export UV0", "첫 번째 UV 세트(UV0)를 USD에 기록한다"));
            exportBounds = SampleTheme.Checkbox(exportBounds, new GUIContent("Author mesh extent", "메시 bounding box(extent)를 USD prim에 기록한다"));
            exportDisabledRenderers = SampleTheme.Checkbox(exportDisabledRenderers, new GUIContent("Export disabled renderers", "Renderer가 disabled인 메시도 export한다"));
            preserveVisibility = SampleTheme.Checkbox(preserveVisibility, new GUIContent("Preserve inactive/disabled visibility", "비활성/disabled 오브젝트의 visibility 상태를 USD에 반영한다"));
            ignoreAlbedoInMetallicSlot = SampleTheme.Checkbox(ignoreAlbedoInMetallicSlot, new GUIContent("Ignore albedo texture in metallic slot (fix mirror/black panels)", "metallic 슬롯에 albedo와 같은 텍스처가 꽂힌 오배치를 무시하고 스칼라 metallic만 export (거울 금속→검은 패널 방지)"));
            captureNativeDiagnostics = SampleTheme.Checkbox(captureNativeDiagnostics, new GUIContent("Capture native diagnostics", "OpenUSD 네이티브 진단 로그를 캡처해 -diagnostics.log 로 저장한다"));

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

            // 데모 지오메트리는 자동 생성하지 않는다. Export는 현재 열려있는 씬 전체를 대상으로 한다.
            // (데모가 필요하면 "Recreate Demo Geometry" 버튼으로 명시적으로 생성)
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

        // 파일명(확장자 제외) + 드롭다운 포맷으로 출력 파일명 구성
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

        // 직렬화된 옛 파일명에 확장자(.usda 등)가 박혀 있으면 떼어내고 그 포맷을 드롭다운 기본값으로 잡는다.
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

        // 포맷 선택 드롭다운(IMGUI 런타임): 버튼을 누르면 목록이 아래로 펼쳐진다.
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

        // 폴더 선택 다이얼로그: 에디터는 OpenFolderPanel(Win/mac/Linux), 빌드는 OS 네이티브 다이얼로그.
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

        // 빌드 런타임용 OS 네이티브 폴더 다이얼로그 (Windows/macOS/Linux)
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

        // 코드로 생성한 폴더 아이콘(다운로드/라이선스 불필요, 에디터·런타임 공통)
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
