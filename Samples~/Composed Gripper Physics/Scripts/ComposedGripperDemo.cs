using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper
{
    /// <summary>
    /// The play-mode side of the sample: imports one of the gripper assets with its physics,
    /// opens and closes every gripper in it, and shows per gripper what composed: the variant
    /// selections, the articulation, the finger drive and the pad material. Runs in the Editor
    /// and in a player.
    /// </summary>
    public sealed class ComposedGripperDemo : MonoBehaviour
    {
        [Tooltip("The asset to import when play starts.")]
        public GripperAsset StartWith = GripperAsset.V3Scene;

        [Tooltip("For GripperAsset.V3Gripper: the preset variant to select (prismatic, linkage or linkage_smooth).")]
        public string Preset = "linkage";

        [Tooltip("If the scene already holds a gripper import (made in edit mode with the window or the Import menu items), use it instead of importing StartWith again.")]
        public bool UseSceneImport = true;

        [Tooltip("Seconds for a full open, close, open cycle of every gripper. 0 leaves the Closing slider in charge.")]
        public float CycleSeconds = 4f;

        [Tooltip("Set Physics.gravity and the fixed time step to the stage's PhysicsScene.")]
        public bool ApplyStageScene = true;

        [Tooltip("Frame the main camera on the grippers after the import.")]
        public bool FrameCamera = true;

        [Tooltip("Show the panel on the left. Hide it with its button or TogglePanelKey.")]
        public bool ShowPanel = true;

        [Tooltip("The key that shows or hides the panel.")]
        public KeyCode TogglePanelKey = KeyCode.H;

        [Tooltip("Draw the main camera to the right of the panel, so the panel doesn't cover the grippers.")]
        public bool DockCamera = true;

        private const float PanelWidth = 420f;

        private readonly ComposedGripperPanel panel = new ComposedGripperPanel();
        private GameObject imported;
        private Vector2 scroll;
        private Vector3 defaultGravity;
        private float defaultFixedDeltaTime;
        private Camera dockedCamera;
        private Rect defaultCameraRect;

        public GameObject Imported => imported;
        public IReadOnlyList<ComposedGripperPanel.GripperSummary> Grippers => panel.Grippers;

        private void Awake()
        {
            defaultGravity = Physics.gravity;
            defaultFixedDeltaTime = Time.fixedDeltaTime;
        }

        private void Start()
        {
            panel.CycleSeconds = CycleSeconds;
            DockMainCamera();
            if (!UseSceneImport || !TryUseSceneImport())
            {
                _ = LoadAsync(StartWith, Preset);
            }
        }

        /// <summary>
        /// Takes over a gripper import already in the scene, so an import made in edit mode isn't
        /// joined by a second one in Play Mode. Prefers an import below this object, then the first
        /// one found. Returns false when the scene holds none.
        /// </summary>
        private bool TryUseSceneImport()
        {
            UsdStageSource chosen = null;
            int found = 0;
            foreach (UsdStageSource source in FindObjectsByType<UsdStageSource>(FindObjectsSortMode.InstanceID))
            {
                if (source.gameObject.scene != gameObject.scene || source.GetComponentInChildren<ArticulationBody>() == null)
                {
                    continue;
                }

                found++;
                if (chosen == null || (source.transform.IsChildOf(transform) && !chosen.transform.IsChildOf(transform)))
                {
                    chosen = source;
                }
            }

            if (chosen == null)
            {
                return false;
            }

            imported = chosen.gameObject;
            panel.Asset = StartWith = AssetOf(chosen, StartWith);
            panel.Preset = Preset = PresetOf(chosen, Preset);
            if (ApplyStageScene && imported.GetComponentInChildren<UsdPhysicsScene>() is UsdPhysicsScene scene)
            {
                scene.Apply();
            }

            if (FrameCamera)
            {
                ComposedGripperImport.Frame(Camera.main, imported);
            }

            panel.Summarize(imported, chosen, null);
            panel.Status = $"Using {imported.name} from the scene: {panel.Grippers.Count} articulation(s).";
            string others = found > 1 ? $" The scene holds {found - 1} more gripper import(s); they run on physics too." : string.Empty;
            Debug.Log("Composed Gripper Physics: " + panel.Status + others, imported);
            return true;
        }

        /// <summary>The sample asset an import came from, read from its recorded source path.</summary>
        private static GripperAsset AssetOf(UsdStageSource source, GripperAsset fallback)
        {
            string path = (source.RecordedSourcePath ?? string.Empty).Replace('\\', '/');
            foreach (GripperAsset asset in (GripperAsset[])Enum.GetValues(typeof(GripperAsset)))
            {
                if (path.EndsWith(ComposedGripperImport.RelativePath(asset), StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            return fallback;
        }

        /// <summary>The preset variant an import selected, if it selected one.</summary>
        private static string PresetOf(UsdStageSource source, string fallback)
        {
            foreach (UsdVariantSelection selection in source.VariantSelections)
            {
                if (selection.VariantSet == ComposedGripperImport.PresetSet)
                {
                    return selection.Variant;
                }
            }

            return fallback;
        }

        private void LateUpdate() => DockMainCamera();

        private void OnDestroy()
        {
            Physics.gravity = defaultGravity;
            Time.fixedDeltaTime = defaultFixedDeltaTime;
            if (dockedCamera != null)
            {
                dockedCamera.rect = defaultCameraRect;
            }
        }

        /// <summary>The folder holding the sample's USD files: the sample's USD~ folder in the Editor, StreamingAssets in a player.</summary>
        public string UsdFolder
        {
            get
            {
#if UNITY_EDITOR
                string script = UnityEditor.AssetDatabase.GetAssetPath(UnityEditor.MonoScript.FromMonoBehaviour(this));
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(script)), ComposedGripperImport.UsdFolderName));
#else
                return ComposedGripperImport.StreamingRoot;
#endif
            }
        }

        /// <summary>Replaces the current import with another asset (and, for v3 gripper, preset).</summary>
        public async Task LoadAsync(GripperAsset asset, string preset)
        {
            if (panel.Busy)
            {
                return;
            }

            panel.Busy = true;
            panel.Asset = StartWith = asset;
            panel.Preset = Preset = preset;
            try
            {
                string path = Path.Combine(UsdFolder, ComposedGripperImport.RelativePath(asset));
                if (!File.Exists(path))
                {
                    panel.Status = "Not found: " + path;
                    return;
                }

                if (imported != null)
                {
                    Destroy(imported);
                }

                panel.Clear();
                Physics.gravity = defaultGravity;
                Time.fixedDeltaTime = defaultFixedDeltaTime;
                panel.Status = $"Importing {ComposedGripperImport.RelativePath(asset)}…";

                UsdImportResult result = await ComposedGripperImport.ImportAsync(UsdFolder, asset, preset, transform);
                imported = result.RootObject;
                if (ApplyStageScene && imported.GetComponentInChildren<UsdPhysicsScene>() is UsdPhysicsScene scene)
                {
                    scene.Apply();
                }

                if (FrameCamera)
                {
                    DockMainCamera();
                    ComposedGripperImport.Frame(Camera.main, imported);
                }

                panel.Summarize(imported, result.Source, result.PhysicsNotes);
                Debug.Log("Composed Gripper Physics: " + panel.Status);
            }
            catch (Exception exception)
            {
                panel.Status = "Failed: " + exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                panel.Busy = false;
            }
        }

        /// <summary>Gives the main camera the part of the screen right of the panel.</summary>
        private void DockMainCamera()
        {
            Camera camera = Camera.main;
            if (camera != dockedCamera)
            {
                if (dockedCamera != null)
                {
                    dockedCamera.rect = defaultCameraRect;
                }

                dockedCamera = camera;
                if (camera != null)
                {
                    defaultCameraRect = camera.rect;
                }
            }

            if (camera == null)
            {
                return;
            }

            float left = DockCamera && ShowPanel ? Mathf.Clamp01(PanelWidth / Screen.width) : 0f;
            camera.rect = new Rect(left, 0f, 1f - left, 1f);
        }

        private void OnGUI()
        {
            // Read from IMGUI's event rather than the Input Manager, which throws in a project
            // that uses only the Input System.
            Event current = Event.current;
            if (current.type == EventType.KeyDown && TogglePanelKey != KeyCode.None && current.keyCode == TogglePanelKey)
            {
                ShowPanel = !ShowPanel;
                current.Use();
            }

            if (!ShowPanel)
            {
                if (GUI.Button(new Rect(8f, 8f, 90f, 26f), "Show panel"))
                {
                    ShowPanel = true;
                }

                return;
            }

            GUILayout.BeginArea(new Rect(0f, 0f, PanelWidth, Screen.height), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.BeginHorizontal();
            panel.DrawTitle("Composed Gripper Physics");
            if (GUILayout.Button("Hide", GUILayout.Width(50f)))
            {
                ShowPanel = false;
            }

            GUILayout.EndHorizontal();
            if (panel.Draw())
            {
                _ = LoadAsync(panel.Asset, panel.Preset);
            }

            CycleSeconds = panel.CycleSeconds;
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
