using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.USDToolkit.RobotPhysics.Editor
{
    /// <summary>
    /// USD Toolkit > Robot Physics: imports a robot with its physics into the open scene (edit
    /// mode) and shows, per joint and body, what Isaac Sim shows next to what Unity built. Enter
    /// play mode in RobotPhysics.unity to drive the joints and run the limit test.
    /// </summary>
    public sealed class RobotPhysicsWindow : EditorWindow
    {
        private const string RuntimeAssemblyName = "Unity.USDToolkit.Samples.RobotPhysics";

        private GameObject robot;
        private RobotPhysicsReport report;
        private Vector2 scroll;
        private bool mismatchesOnly;

        // The Franka download in progress: shown in the window and in Background Tasks (the
        // Progress API), not a modal progress bar, since the Editor stays usable while it runs.
        private CancellationTokenSource download;
        private int downloadProgressId;
        private float downloadFraction;
        private string downloadFile = string.Empty;

        [MenuItem("USD Toolkit/Robot Physics")]
        public static void Open() => GetWindow<RobotPhysicsWindow>("Robot Physics");

        /// <summary>The sample's folder in the project (the Package Manager may put it anywhere).</summary>
        public static string SampleRoot
        {
            get
            {
                foreach (string guid in AssetDatabase.FindAssets($"{RuntimeAssemblyName} t:{nameof(AssemblyDefinitionAsset)}"))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == RuntimeAssemblyName)
                    {
                        return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/');
                    }
                }

                throw new FileNotFoundException($"The {RuntimeAssemblyName} assembly definition was not found in the project.");
            }
        }

        public static string BundledArmPath => Path.GetFullPath($"{SampleRoot}/Robots/isaac_style_arm.usda");

        public static string FrankaManifestPath => Path.GetFullPath($"{SampleRoot}/Robots/franka_panda_isaac_6.0.files.txt");

        /// <summary>Imports a USD file with physics into the open scene and reports on it.</summary>
        public static RobotPhysicsReport ImportAndReport(string path, out GameObject root)
        {
            UsdImportResult result = RobotPhysicsImport.Import(path);
            root = result.RootObject;
            Undo.RegisterCreatedObjectUndo(root, "Import robot");
            return RobotPhysicsReport.Build(root, result.PhysicsNotes);
        }

        /// <summary>Downloads the Franka Panda (or reuses a verified download) and returns its root layer.</summary>
        public static Task<string> DownloadFrankaAsync(Action<float, string> progress = null, CancellationToken cancel = default)
        {
            RobotAssetDownload download = RobotAssetDownload.Parse(File.ReadAllText(FrankaManifestPath));
            return download.FetchAsync(RobotPhysicsImport.FrankaFolder, progress, cancel);
        }

        private void OnDisable() => download?.Cancel();

        private void OnSelectionChange()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected != null && selected.GetComponentInChildren<UsdPhysicsJoint>(true) != null)
            {
                robot = selected;
                report = RobotPhysicsReport.Build(robot);
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Imports a robot with UsdImportOptions.ImportPhysics and compares each joint and body with the values the " +
                "stage authors, which is what Isaac Sim's Property panel shows. Open RobotPhysics.unity and press Play to " +
                "drive the joints and run the limit test.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                string root = RobotPhysicsImport.DownloadRoot;
                string typed = EditorGUILayout.DelayedTextField(new GUIContent("Download location", "Where the Franka (and any downloaded robot) is kept, each in a folder of its own. Shared with the Play Mode panel."), root);
                if (typed != root)
                {
                    RobotPhysicsImport.DownloadRoot = typed;
                }

                if (GUILayout.Button("Browse…", GUILayout.Width(70f)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Download robots to", Directory.Exists(root) ? root : string.Empty, string.Empty);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        RobotPhysicsImport.DownloadRoot = picked;
                    }

                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button(new GUIContent("Default", RobotPhysicsImport.DefaultDownloadRoot), GUILayout.Width(60f)))
                {
                    RobotPhysicsImport.DownloadRoot = null;
                    GUIUtility.keyboardControl = 0;
                }

                using (new EditorGUI.DisabledScope(!Directory.Exists(root)))
                {
                    if (GUILayout.Button("Reveal", GUILayout.Width(55f)))
                    {
                        EditorUtility.RevealInFinder(root);
                    }
                }
            }

            if (download != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect bar = EditorGUILayout.GetControlRect();
                    EditorGUI.ProgressBar(bar, downloadFraction, $"Downloading the Franka Panda: {downloadFile} ({downloadFraction * 100f:0}%)");
                    if (GUILayout.Button("Cancel", GUILayout.Width(60f)))
                    {
                        download.Cancel();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(download != null))
            using (new EditorGUILayout.HorizontalScope())
            {
                // Each runs after this GUI pass: an import or a file dialog inside OnGUI breaks
                // the window's layout.
                if (GUILayout.Button("Import bundled arm"))
                {
                    EditorApplication.delayCall += () => ImportAndShow(BundledArmPath);
                }

                if (GUILayout.Button("Download and import Franka Panda (Isaac Sim 6.0)"))
                {
                    EditorApplication.delayCall += () => _ = ImportFrankaAsync();
                }

                if (GUILayout.Button("Import file…"))
                {
                    EditorApplication.delayCall += () =>
                    {
                        string path = EditorUtility.OpenFilePanel("Robot USD", string.Empty, "usd,usda,usdc,usdz");
                        if (!string.IsNullOrEmpty(path))
                        {
                            ImportAndShow(path);
                        }
                    };
                }
            }

            if (report == null)
            {
                EditorGUILayout.LabelField("Import a robot, or select one imported with physics.");
                return;
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(robot != null ? robot.name : "(deleted)", EditorStyles.boldLabel);
                mismatchesOnly = GUILayout.Toggle(mismatchesOnly, "Differences only", GUILayout.Width(120f));
                if (GUILayout.Button("Save CSV…", GUILayout.Width(90f)))
                {
                    string path = EditorUtility.SaveFilePanel("Robot physics report", string.Empty, (robot != null ? robot.name : "robot") + "_physics.csv", "csv");
                    if (!string.IsNullOrEmpty(path))
                    {
                        File.WriteAllText(path, report.ToCsv());
                    }
                }
            }

            EditorGUILayout.HelpBox(report.Summary, report.Mismatches > 0 ? MessageType.Warning : MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawTable(report.Rows, mismatchesOnly);
            if (report.Notes.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Notes", EditorStyles.boldLabel);
                foreach (string note in report.Notes)
                {
                    EditorGUILayout.LabelField("• " + note, EditorStyles.wordWrappedLabel);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void ImportAndShow(string path)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Robot Physics", "Importing " + Path.GetFileName(path) + " with physics…", 1f);
                Show(ImportAndReport(path, out robot));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Robot Physics", "The import failed:\n" + exception.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private void Show(RobotPhysicsReport made)
        {
            report = made;
            Selection.activeGameObject = robot;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log($"Robot Physics: {robot.name}: {report.Summary}");
        }

        private async Task ImportFrankaAsync()
        {
            if (download != null)
            {
                return;
            }

            download = new CancellationTokenSource();
            downloadFraction = 0f;
            downloadFile = string.Empty;
            downloadProgressId = Progress.Start("Franka Panda", "Downloading from NVIDIA's Isaac Sim assets");
            Progress.RegisterCancelCallback(downloadProgressId, () =>
            {
                download?.Cancel();
                return true;
            });

            string path;
            try
            {
                // FetchAsync reports on the main thread (see RobotAssetDownload), so no delayCall:
                // a queued call could run after the download ends and show progress again.
                path = await DownloadFrankaAsync((fraction, file) =>
                {
                    downloadFraction = fraction;
                    downloadFile = file;
                    Progress.Report(downloadProgressId, fraction, file);
                    Repaint();
                }, download.Token);
                Progress.Finish(downloadProgressId);
            }
            catch (Exception exception)
            {
                bool cancelled = exception is OperationCanceledException;
                Progress.Finish(downloadProgressId, cancelled ? Progress.Status.Canceled : Progress.Status.Failed);
                if (!cancelled)
                {
                    Debug.LogException(exception);
                    EditorUtility.DisplayDialog("Franka Panda", "The download failed:\n" + exception.Message, "OK");
                }

                return;
            }
            finally
            {
                download.Dispose();
                download = null;
                Repaint();
            }

            // The import blocks the Editor, so a modal bar is right there, and it is always cleared.
            ImportAndShow(path);
        }

        private static void DrawTable(List<RobotPhysicsReport.Row> rows, bool mismatchesOnly)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Prim", EditorStyles.boldLabel, GUILayout.Width(260f));
                EditorGUILayout.LabelField("Property", EditorStyles.boldLabel, GUILayout.Width(150f));
                EditorGUILayout.LabelField("Isaac Sim", EditorStyles.boldLabel, GUILayout.Width(150f));
                EditorGUILayout.LabelField("Unity", EditorStyles.boldLabel, GUILayout.Width(170f));
                EditorGUILayout.LabelField("Unity (native)", EditorStyles.boldLabel);
            }

            Color normal = GUI.color;
            string previous = null;
            foreach (RobotPhysicsReport.Row row in rows)
            {
                if (mismatchesOnly && row.Match != false)
                {
                    continue;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(row.Prim == previous ? string.Empty : row.Prim, GUILayout.Width(260f));
                    EditorGUILayout.LabelField(row.Property, GUILayout.Width(150f));
                    EditorGUILayout.LabelField(row.IsaacSim, GUILayout.Width(150f));
                    GUI.color = row.Match == null ? normal : row.Match.Value ? new Color(0.55f, 0.9f, 0.55f) : new Color(1f, 0.5f, 0.45f);
                    EditorGUILayout.LabelField((row.Match == null ? "  " : row.Match.Value ? "✔ " : "✘ ") + row.Unity, GUILayout.Width(170f));
                    GUI.color = normal;
                    EditorGUILayout.LabelField(row.UnityNative);
                }

                previous = row.Prim;
            }
        }
    }
}
