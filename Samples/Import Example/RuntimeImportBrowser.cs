using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    public sealed class RuntimeImportBrowser : MonoBehaviour
    {
        [SerializeField] private string initialFolderPath;
        [SerializeField] private string directFilePath;
        [SerializeField] private string generatedUsdName = "sample-cube.usda";
        [SerializeField] private Transform importParent;
        [SerializeField] private bool recursiveScan = true;
        [SerializeField] private bool importMaterials = true;
        [SerializeField] private bool generateColliders;
        [SerializeField] private bool clearBeforeImport = true;

        private readonly List<UsdLibraryItem> libraryItems = new List<UsdLibraryItem>();
        private readonly Dictionary<string, Texture2D> thumbnailCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private Vector2 listScroll;
        private Vector2 statusScroll;
        private Vector2 controlScroll;
        private string folderPath;
        private string filePath;
        private string selectedPath;
        private string previewText = "No file selected.";
        private string statusText;
        private string runtimeText;
        private GameObject importedRoot;
        private GUIStyle titleStyle;
        private GUIStyle sectionStyle;
        private GUIStyle bodyStyle;
        private GUIStyle smallStyle;
        private GUIStyle itemStyle;
        private GUIStyle selectedItemStyle;
        private GUIStyle buttonStyle;
        private GUIStyle textFieldStyle;
        private Texture2D itemBackground;
        private Texture2D selectedItemBackground;
        private Texture2D thumbnailFallback;

        // 공통 UI(Export 샘플과 통일): 출력 포맷(드롭다운) + 폴더 아이콘(코드 생성)
        private static readonly string[] UsdFormats = { ".usd", ".usda", ".usdc" };
        private int formatIndex = 1;
        private bool formatDropdownOpen;
        private Texture2D folderIcon;
        private string exportFileName = "sample-cube";
        private GUIStyle tooltipStyle;
        private Texture2D tooltipBackground;

        private void Start()
        {
            EnsureSceneObjects();
            ApplyDefaultPaths();
            RefreshRuntimeInfo();
            ScanFolder();
        }

        private void OnDestroy()
        {
            foreach (Texture2D texture in thumbnailCache.Values)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }

            thumbnailCache.Clear();
        }

        private void OnGUI()
        {
            EnsureStyles();

            const float margin = 20.0f;
            const float gap = 16.0f;
            const float minWidth = 720.0f;
            const float minHeight = 460.0f;

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

            float half = (width - gap) * 0.5f;
            DrawControlPanel(half, height);
            GUILayout.Space(gap);
            DrawLibraryPanel(half, height);

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
                tooltipBackground = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tooltipBackground.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.09f, 0.97f));
                tooltipBackground.Apply();
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

        private void DrawControlPanel(float width, float height)
        {
            float inner = width - 56.0f;
            GUILayout.BeginVertical(SampleTheme.Card, GUILayout.Width(width), GUILayout.Height(height));
            controlScroll = GUILayout.BeginScrollView(controlScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));

            GUILayout.Label("Runtime USD Import Browser", titleStyle, GUILayout.Width(inner));
            GUILayout.Label(runtimeText, smallStyle, GUILayout.Width(inner));
            GUILayout.Space(10.0f);

            GUILayout.Label("Folder", smallStyle);
            GUILayout.BeginHorizontal();
            folderPath = GUILayout.TextField(folderPath ?? string.Empty, SampleTheme.PathField, GUILayout.Width(inner - 50.0f), GUILayout.Height(34.0f));
            if (GUILayout.Button(new GUIContent(SampleTheme.Folder, "Browse for folder"), buttonStyle, GUILayout.Width(44.0f), GUILayout.Height(34.0f)))
            {
                string picked = BrowseFolder("Choose folder", folderPath);
                if (!string.IsNullOrEmpty(picked))
                {
                    folderPath = picked;
                }
            }

            GUILayout.EndHorizontal();
            recursiveScan = SampleTheme.Checkbox(recursiveScan, new GUIContent("Recursive scan", "하위 폴더까지 재귀적으로 USD 파일을 검색한다"));

            GUILayout.Space(8.0f);
            GUILayout.Label("Export file name", smallStyle);
            exportFileName = GUILayout.TextField(exportFileName ?? string.Empty, textFieldStyle, GUILayout.Width(inner), GUILayout.Height(34.0f));
            DrawFormatDropdown(inner);
            if (GUILayout.Button("Scan", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                ScanFolder();
            }

            GUILayout.Space(6.0f);
            if (GUILayout.Button("Export Cube", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                ExportSampleUsd();
            }

            GUILayout.Space(10.0f);
            GUILayout.Label("Direct File", smallStyle);
            GUILayout.BeginHorizontal();
            filePath = GUILayout.TextField(filePath ?? string.Empty, SampleTheme.PathField, GUILayout.Width(inner - 50.0f), GUILayout.Height(34.0f));
            if (GUILayout.Button(new GUIContent(SampleTheme.Folder, "Browse for file"), buttonStyle, GUILayout.Width(44.0f), GUILayout.Height(34.0f)))
            {
                string pickedFile = BrowseFile("Choose USD file", filePath);
                if (!string.IsNullOrEmpty(pickedFile))
                {
                    filePath = pickedFile;
                }
            }

            GUILayout.EndHorizontal();
            if (GUILayout.Button("Preview File", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                SelectPath(filePath);
            }

            GUILayout.Space(6.0f);
            if (GUILayout.Button("Import File", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                ImportPath(filePath);
            }

            GUILayout.Space(10.0f);
            GUILayout.Label("Options", sectionStyle);
            importMaterials = SampleTheme.Checkbox(importMaterials, new GUIContent("Import materials", "USD의 머티리얼(색/텍스처)을 함께 임포트한다"));
            generateColliders = SampleTheme.Checkbox(generateColliders, new GUIContent("Generate mesh colliders", "임포트된 메시에 MeshCollider를 생성한다"));
            clearBeforeImport = SampleTheme.Checkbox(clearBeforeImport, new GUIContent("Clear previous import", "임포트 전에 이전 임포트 결과를 먼저 제거한다"));

            GUILayout.Space(12.0f);
            GUI.enabled = !string.IsNullOrWhiteSpace(selectedPath);
            if (GUILayout.Button("Import Selected", SampleTheme.PrimaryButton, GUILayout.Width(inner), GUILayout.Height(42.0f)))
            {
                ImportPath(selectedPath);
            }

            GUI.enabled = true;
            GUILayout.Space(6.0f);
            if (GUILayout.Button("Clear Imported", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                ClearImported();
            }

            GUILayout.Space(12.0f);
            GUILayout.Label("Preview", sectionStyle);
            GUILayout.TextArea(previewText, bodyStyle, GUILayout.Width(inner), GUILayout.Height(110.0f));

            GUILayout.Space(8.0f);
            GUILayout.Label("Status", sectionStyle);
            GUILayout.Label(statusText ?? string.Empty, bodyStyle, GUILayout.Width(inner));

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawLibraryPanel(float width, float height)
        {
            float inner = width - 56.0f;
            GUILayout.BeginVertical(SampleTheme.Card, GUILayout.Width(width), GUILayout.Height(height));
            GUILayout.Label("USD Files", titleStyle, GUILayout.Width(inner));
            GUILayout.Space(8.0f);

            listScroll = GUILayout.BeginScrollView(listScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));
            if (libraryItems.Count == 0)
            {
                GUILayout.Label("No USD files found. Export a sample cube or enter another folder path.", bodyStyle, GUILayout.Width(inner));
            }

            foreach (UsdLibraryItem item in libraryItems)
            {
                DrawLibraryItem(item, inner);
                GUILayout.Space(8.0f);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawLibraryItem(UsdLibraryItem item, float itemWidth)
        {
            bool selected = string.Equals(selectedPath, item.Path, StringComparison.OrdinalIgnoreCase);
            GUILayout.BeginVertical(selected ? selectedItemStyle : itemStyle, GUILayout.Width(itemWidth));
            GUILayout.BeginHorizontal();

            Rect thumbnailRect = GUILayoutUtility.GetRect(64.0f, 64.0f, GUILayout.Width(64.0f), GUILayout.Height(64.0f));
            Texture2D thumbnail = GetThumbnail(item.ThumbnailPath) ?? thumbnailFallback;
            GUI.DrawTexture(thumbnailRect, thumbnail, ScaleMode.ScaleAndCrop);

            GUILayout.Space(8.0f);
            float infoW = Mathf.Max(60.0f, itemWidth - 64.0f - 88.0f - 36.0f);
            GUILayout.BeginVertical(GUILayout.Width(infoW));
            GUILayout.Label(item.FileName, sectionStyle, GUILayout.Width(infoW));
            GUILayout.Label(FormatBytes(item.FileSizeBytes) + "  " + item.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), smallStyle, GUILayout.Width(infoW));
            GUILayout.Label(item.Path, smallStyle, GUILayout.Width(infoW));
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(80.0f));
            if (GUILayout.Button("Select", buttonStyle, GUILayout.Width(80.0f), GUILayout.Height(30.0f)))
            {
                SelectPath(item.Path);
            }

            GUILayout.Space(4.0f);
            if (GUILayout.Button("Import", buttonStyle, GUILayout.Width(80.0f), GUILayout.Height(30.0f)))
            {
                ImportPath(item.Path);
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
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

            if (importParent == null)
            {
                var parentObject = new GameObject("Imported USD Root");
                importParent = parentObject.transform;
            }
        }

        private void ApplyDefaultPaths()
        {
            folderPath = string.IsNullOrWhiteSpace(initialFolderPath)
                ? Path.Combine(Application.persistentDataPath, "UsdLibrary")
                : initialFolderPath;

            Directory.CreateDirectory(folderPath);
            filePath = directFilePath ?? string.Empty;
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

        private void ScanFolder()
        {
            selectedPath = null;
            previewText = "No file selected.";
            libraryItems.Clear();

            try
            {
                libraryItems.AddRange(UsdLibraryScanner.ScanFolder(folderPath, new UsdLibraryScanOptions
                {
                    Recursive = recursiveScan,
                    IncludeCompanionThumbnails = true
                }));

                statusText = "Scanned " + libraryItems.Count + " USD file(s).";
            }
            catch (Exception exception)
            {
                statusText = exception.Message;
                Debug.LogException(exception);
            }
        }

        private void ExportSampleUsd()
        {
            GameObject sample = null;
            try
            {
                Directory.CreateDirectory(folderPath);
                string outputPath = Path.Combine(folderPath, GetOutputFileName());

                sample = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sample.name = "SampleCube";
                sample.transform.position = new Vector3(0.0f, 0.5f, 0.0f);
                sample.transform.rotation = Quaternion.Euler(0.0f, 25.0f, 0.0f);

                Renderer renderer = sample.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = CreatePreviewMaterial(new Color(0.14f, 0.55f, 0.95f, 1.0f));
                }

                UsdExportResult result = UsdExporter.ExportGameObjectWithResult(sample, outputPath, new UsdExportOptions
                {
                    RootPrimName = "RuntimeImportSample",
                    TransformPolicy = UsdTransformPolicy.BakedMesh,
                    ExportNormals = true,
                    ExportUv0 = true,
                    CaptureNativeDiagnostics = false,
                    LogExportSummary = false
                });

                filePath = outputPath;
                statusText = result.ToString();
                ScanFolder();
                SelectPath(outputPath);
            }
            catch (Exception exception)
            {
                statusText = exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                if (sample != null)
                {
                    Destroy(sample);
                }
            }
        }

        // 파일명(확장자 제외) + 드롭다운 포맷으로 출력 파일명 구성 (Export 샘플과 공통)
        private string GetOutputFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(exportFileName) ? "sample-cube" : exportFileName.Trim();
            baseName = Path.GetFileNameWithoutExtension(baseName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "sample-cube";
            }

            int idx = Mathf.Clamp(formatIndex, 0, UsdFormats.Length - 1);
            return baseName + UsdFormats[idx];
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

        // 폴더 선택: 에디터는 OpenFolderPanel(Win/mac/Linux), 빌드는 OS 네이티브 다이얼로그.
        private static string BrowseFolder(string title, string startDir)
        {
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFolderPanel(
                title, string.IsNullOrEmpty(startDir) ? string.Empty : startDir, string.Empty);
            return string.IsNullOrEmpty(path) ? null : path;
#else
            return RunNativeDialog(title, false);
#endif
        }

        // 파일 선택: 에디터는 OpenFilePanel(usd 계열), 빌드는 OS 네이티브 다이얼로그.
        private static string BrowseFile(string title, string startPath)
        {
#if UNITY_EDITOR
            string dir = string.Empty;
            try
            {
                dir = string.IsNullOrEmpty(startPath) ? string.Empty : Path.GetDirectoryName(startPath);
            }
            catch
            {
                dir = string.Empty;
            }

            string path = UnityEditor.EditorUtility.OpenFilePanel(title, dir ?? string.Empty, "usd,usda,usdc,usdz");
            return string.IsNullOrEmpty(path) ? null : path;
#else
            return RunNativeDialog(title, true);
#endif
        }

        // 빌드 런타임용 OS 네이티브 다이얼로그(Windows/macOS/Linux). pickFile=false면 폴더 선택.
        private static string RunNativeDialog(string title, bool pickFile)
        {
            try
            {
                string exe;
                string args;
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                        exe = "powershell";
                        args = pickFile
                            ? "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.OpenFileDialog; $d.Filter = 'USD|*.usd;*.usda;*.usdc;*.usdz|All|*.*'; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.FileName) }\""
                            : "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.SelectedPath) }\"";
                        break;
                    case RuntimePlatform.OSXPlayer:
                        exe = "/usr/bin/osascript";
                        args = pickFile
                            ? "-e \"POSIX path of (choose file with prompt \\\"" + title + "\\\")\""
                            : "-e \"POSIX path of (choose folder with prompt \\\"" + title + "\\\")\"";
                        break;
                    case RuntimePlatform.LinuxPlayer:
                        exe = "zenity";
                        args = pickFile
                            ? "--file-selection --title=\"" + title + "\""
                            : "--file-selection --directory --title=\"" + title + "\"";
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
                Debug.LogWarning("Native file dialog failed: " + exception.Message);
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

        private void SelectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                selectedPath = null;
                previewText = "No file selected.";
                return;
            }

            try
            {
                selectedPath = Path.GetFullPath(path);
                filePath = selectedPath;
                UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(selectedPath);
                previewText =
                    Path.GetFileName(selectedPath) + Environment.NewLine +
                    "Default prim: " + EmptyFallback(preview.DefaultPrimPath, "(none)") + Environment.NewLine +
                    "Meshes: " + preview.MeshCount + Environment.NewLine +
                    "Materials: " + preview.MaterialCount + Environment.NewLine +
                    "Meters/unit: " + preview.MetersPerUnit.ToString("0.###") + Environment.NewLine +
                    "Up axis: " + preview.UpAxis;
                statusText = "Selected " + selectedPath;
            }
            catch (Exception exception)
            {
                previewText = "Preview failed.";
                statusText = exception.Message;
                selectedPath = null;
                Debug.LogException(exception);
            }
        }

        private void ImportPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                statusText = "Enter or select a USD file path.";
                return;
            }

            try
            {
                if (clearBeforeImport)
                {
                    ClearImported();
                }

                UsdImportResult result = UsdImporter.Import(path, new UsdImportOptions
                {
                    Parent = importParent,
                    RootObjectName = Path.GetFileNameWithoutExtension(path),
                    ImportMaterials = importMaterials,
                    GenerateColliders = generateColliders,
                    IncludeInvisible = true,
                    RecalculateNormalsIfMissing = true
                });

                importedRoot = result.RootObject;
                FrameCamera(importedRoot);
                statusText = result.ToString();
            }
            catch (Exception exception)
            {
                statusText = exception.Message;
                Debug.LogException(exception);
            }
        }

        private void ClearImported()
        {
            if (importParent == null)
            {
                return;
            }

            for (int i = importParent.childCount - 1; i >= 0; i--)
            {
                Destroy(importParent.GetChild(i).gameObject);
            }

            importedRoot = null;
            statusText = "Cleared imported objects.";
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
            camera.transform.position = bounds.center - direction * radius * 2.6f;
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

        private Material CreatePreviewMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                Shader.Find("HDRP/Lit") ??
                Shader.Find("Standard") ??
                Shader.Find("Sprites/Default");

            var material = new Material(shader);
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            return material;
        }

        private Texture2D GetThumbnail(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            Texture2D cached;
            if (thumbnailCache.TryGetValue(path, out cached))
            {
                return cached;
            }

            // Through the importer's size limits: a companion image comes from whatever folder was
            // scanned, and LoadImage alone will allocate whatever its header claims.
            Texture2D texture = UsdImporter.LoadImageFile(path);

            // Cached even when refused, so a bad file is not re-read on every repaint.
            thumbnailCache[path] = texture;
            return texture;
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
            buttonStyle = SampleTheme.Button;
            textFieldStyle = SampleTheme.Field;

            thumbnailFallback = CreateThumbnailFallback();

            // 리스트 아이템: 어두운 둥근 카드 / 선택 시 초록 강조
            itemStyle = new GUIStyle
            {
                border = new RectOffset(12, 12, 12, 12),
                padding = new RectOffset(10, 10, 8, 8),
                normal = { background = SampleTheme.ButtonTile }
            };
            selectedItemStyle = new GUIStyle(itemStyle)
            {
                normal = { background = SampleTheme.AccentTile }
            };
        }

        private static Texture2D CreateSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateThumbnailFallback()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color background = new Color(0.08f, 0.09f, 0.105f, 1.0f);
            Color stripe = new Color(0.18f, 0.37f, 0.62f, 1.0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    texture.SetPixel(x, y, x > y - 8 && x < y + 8 ? stripe : background);
                }
            }

            texture.Apply();
            return texture;
        }

        private static string EmptyFallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }
    }
}
