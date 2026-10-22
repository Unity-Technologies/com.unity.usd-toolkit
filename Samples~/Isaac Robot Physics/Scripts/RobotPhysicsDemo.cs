using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.RobotPhysics
{
    /// <summary>
    /// The play-mode side of the sample: imports a robot with its physics, puts what Isaac Sim
    /// shows for each joint and body next to what Unity built (<see cref="RobotPhysicsReport"/>), lets
    /// you drive the joints, and runs a limit test that drives every limited joint past both of its
    /// limits and records where the physics stops it. Runs in the Editor and in a player.
    /// </summary>
    public sealed class RobotPhysicsDemo : MonoBehaviour
    {
        public enum Robot
        {
            /// <summary>The small arm the sample ships (Robots/isaac_style_arm.usda).</summary>
            BundledArm,

            /// <summary>NVIDIA's Franka Panda from the Isaac Sim 6.0 assets, downloaded on first use.</summary>
            FrankaPanda,

            /// <summary>Any USD file on disk.</summary>
            File
        }

        [Tooltip("The robot to import when play starts.")]
        public Robot StartWith = Robot.BundledArm;

        [Tooltip("For Robot.File: the USD file to import.")]
        public string FilePath = string.Empty;

        [Tooltip("Robots/franka_panda_isaac_6.0.files.txt: what to download for the Franka, with each file's SHA-256.")]
        public TextAsset FrankaManifest;

#if UNITY_EDITOR
        [Tooltip("Robots/isaac_style_arm.usda, so the Editor reads it where the sample put it.")]
        public Object BundledArmAsset;
#endif

        [Tooltip("Set Physics.gravity and the fixed time step to the stage's PhysicsScene.")]
        public bool ApplyStageScene = true;

        [Tooltip("Frame the main camera on the robot after the import.")]
        public bool FrameCamera = true;

        [Tooltip("Show the panel on the left. Hide it with its button or TogglePanelKey.")]
        public bool ShowPanel = true;

        [Tooltip("The key that shows or hides the panel (ignored while a text field has focus).")]
        public KeyCode TogglePanelKey = KeyCode.H;

        [Tooltip("Draw the main camera to the right of the panel, so the panel doesn't cover the robot.")]
        public bool DockCamera = true;

        [Tooltip("Simulate links whose authored inertia is a placeholder (over 1000x below what their colliders give, as " +
                 "the Franka's is) with the collider-derived inertia. The comparison table always shows the import as authored.")]
        public bool SimulateWithColliderInertia = true;

        private GameObject robot;
        private RobotPhysicsReport report;
        private readonly List<UsdPhysicsJoint> drivable = new List<UsdPhysicsJoint>();
        private readonly List<UsdPhysicsMimicJoint> mimics = new List<UsdPhysicsMimicJoint>();
        private readonly Dictionary<UsdPhysicsJoint, float> targets = new Dictionary<UsdPhysicsJoint, float>();
        private readonly List<RobotPhysicsReport.Row> limitRows = new List<RobotPhysicsReport.Row>();
        private string status = string.Empty;
        private float progress = -1f;
        private bool busy;
        private int tab;
        private Vector2 scroll;
        private Vector2 notesScroll;
        private bool showDownloadLocation = true;
        private Robot selected;
        private Vector3 defaultGravity;
        private float defaultFixedDeltaTime;
        private Camera dockedCamera;
        private Rect defaultCameraRect;
        private RobotAssetDownload franka;
        private string frankaCheckedFolder;
        private bool frankaDownloaded;

        public GameObject ImportedRobot => robot;
        public RobotPhysicsReport Report => report;

        private void Awake()
        {
            defaultGravity = Physics.gravity;
            defaultFixedDeltaTime = Time.fixedDeltaTime;
            selected = StartWith;
        }

        private void Start()
        {
            DockMainCamera();
            _ = LoadAsync(StartWith);
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

        private void FixedUpdate()
        {
            if (busy)
            {
                return;
            }

            foreach (KeyValuePair<UsdPhysicsJoint, float> entry in targets)
            {
                if (entry.Key != null)
                {
                    entry.Key.SetDriveTarget(entry.Value);
                }
            }
        }

        /// <summary>Replaces the current robot with another, downloading the Franka if needed.</summary>
        public async Task LoadAsync(Robot which)
        {
            if (busy)
            {
                return;
            }

            busy = true;
            selected = which;
            try
            {
                string path = await ResolveAsync(which);
                if (path == null)
                {
                    return;
                }

                if (robot != null)
                {
                    Destroy(robot);
                }

                Physics.gravity = defaultGravity;
                Time.fixedDeltaTime = defaultFixedDeltaTime;
                status = $"Importing {Path.GetFileName(path)}…";
                progress = -1f;
                UsdImportResult result = await RobotPhysicsImport.ImportAsync(path, transform);
                robot = result.RootObject;
                if (ApplyStageScene && robot.TryGetComponent(out UsdPhysicsScene scene))
                {
                    scene.Apply();
                }

                if (FrameCamera)
                {
                    DockMainCamera();
                    RobotPhysicsImport.Frame(Camera.main, robot);
                }

                report = RobotPhysicsReport.Build(robot, result.PhysicsNotes);
                limitRows.Clear();
                CollectDrivableJoints();
                status = report.Summary;

                // After the report, so the table compares the import as authored.
                int replaced = 0;
                foreach (UsdPhysicsBody body in robot.GetComponentsInChildren<UsdPhysicsBody>())
                {
                    if (SimulateWithColliderInertia && body.ImplausibleInertia)
                    {
                        body.UseColliderInertia(true);
                        replaced++;
                    }
                }

                if (replaced > 0)
                {
                    status += $"\nSimulating {replaced} links with collider-derived inertia: the stage authors a placeholder (see notes).";
                }
                Debug.Log($"Robot Physics: {Path.GetFileName(path)}: {report.Summary}");
            }
            catch (Exception exception)
            {
                status = "Failed: " + exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                busy = false;
                progress = -1f;
                frankaCheckedFolder = null;
            }
        }

        private async Task<string> ResolveAsync(Robot which)
        {
            switch (which)
            {
                case Robot.BundledArm:
                {
#if UNITY_EDITOR
                    // The file, not the imported asset: an asset whose import failed (it is a USD
                    // asset too) has no object to reference, but its file is still there. Without
                    // the reference, the file sits in Robots/ beside this script's Scripts/ folder.
                    string assetPath = BundledArmAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(BundledArmAsset) : null;
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        string script = UnityEditor.AssetDatabase.GetAssetPath(UnityEditor.MonoScript.FromMonoBehaviour(this));
                        assetPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(script)), "Robots", "isaac_style_arm.usda");
                    }

                    string path = Path.GetFullPath(assetPath);
#else
                    string path = RobotPhysicsImport.StreamingArmPath;
#endif
                    if (!System.IO.File.Exists(path))
                    {
                        status = "The bundled arm was not found at " + path;
                        return null;
                    }

                    return path;
                }

                case Robot.FrankaPanda:
                {
                    if (FrankaManifest == null)
                    {
                        status = "No Franka manifest assigned.";
                        return null;
                    }

                    RobotAssetDownload download = Franka;
                    string folder = RobotPhysicsImport.FrankaFolder;
                    status = $"Downloading the Franka Panda ({download.TotalBytes / (1024f * 1024f):0.#} MB) from NVIDIA's Isaac Sim assets to {folder}…";
                    return await download.FetchAsync(folder, (fraction, file) =>
                    {
                        progress = fraction;
                        status = "Downloading " + file;
                    });
                }

                default:
                    if (string.IsNullOrWhiteSpace(FilePath))
                    {
                        status = "Choose a USD file to import.";
                        return null;
                    }

                    FilePath = FilePath.Trim().Trim('"');
                    if (!System.IO.File.Exists(FilePath))
                    {
                        status = "File not found: " + FilePath;
                        return null;
                    }

                    return FilePath;
            }
        }

        /// <summary>The Franka manifest, parsed once.</summary>
        private RobotAssetDownload Franka => franka ?? (franka = FrankaManifest != null ? RobotAssetDownload.Parse(FrankaManifest.text) : null);

        /// <summary>Whether the Franka's files are in the download folder, checked again when the folder changes.</summary>
        private bool FrankaDownloaded
        {
            get
            {
                string folder = RobotPhysicsImport.FrankaFolder;
                if (folder != frankaCheckedFolder)
                {
                    frankaCheckedFolder = folder;
                    try
                    {
                        frankaDownloaded = Franka != null && Franka.HasFiles(folder);
                    }
                    catch (Exception)
                    {
                        frankaDownloaded = false; // a path the OS refuses, while it's being typed
                    }
                }

                return frankaDownloaded;
            }
        }

        private void CollectDrivableJoints()
        {
            drivable.Clear();
            mimics.Clear();
            targets.Clear();
            if (robot == null)
            {
                return;
            }

            // A mimic joint's target follows its reference joint, so it gets no slider.
            mimics.AddRange(robot.GetComponentsInChildren<UsdPhysicsMimicJoint>());
            mimics.Sort((a, b) => string.CompareOrdinal(a.Follower.PrimPath, b.Follower.PrimPath));
            foreach (UsdPhysicsJoint joint in robot.GetComponentsInChildren<UsdPhysicsJoint>())
            {
                if (joint.Articulated && joint.Link != null && !joint.TryGetComponent(out UsdPhysicsMimicJoint _) &&
                    (joint.UnityType == UsdPhysicsJointType.Revolute || joint.UnityType == UsdPhysicsJointType.Prismatic))
                {
                    drivable.Add(joint);
                }
            }

            drivable.Sort((a, b) => string.CompareOrdinal(a.PrimPath, b.PrimPath));
        }

        // ---- Limit test ----

        /// <summary>
        /// Drives every limited joint past its upper and then its lower limit, one at a time, with
        /// gravity off and the robot's contacts with the rest of the scene off, and records where it
        /// stops: once it has held still for a quarter of a second, or after
        /// <paramref name="maxSecondsPerSide"/> (a robot may cap its links' speed, as the Franka's
        /// 100 degrees per second does). A row matches when it stops within 1.5 degrees (or 2% of the
        /// travel) of the limit.
        /// </summary>
        public IEnumerator RunLimitTest(float maxSecondsPerSide = 8f)
        {
            if (busy || robot == null)
            {
                yield break;
            }

            busy = true;
            limitRows.Clear();
            var gravity = new Dictionary<ArticulationBody, bool>();
            foreach (ArticulationBody link in robot.GetComponentsInChildren<ArticulationBody>())
            {
                gravity[link] = link.useGravity;
                link.useGravity = false;
            }

            var disabled = new List<Collider>();
            foreach (Collider collider in FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (collider.enabled && collider.attachedArticulationBody == null && collider.attachedRigidbody == null)
                {
                    collider.enabled = false;
                    disabled.Add(collider);
                }
            }

            var hold = new Dictionary<UsdPhysicsJoint, float>();
            foreach (UsdPhysicsJoint joint in drivable)
            {
                hold[joint] = joint.GetPosition();
            }

            int index = 0;
            foreach (UsdPhysicsJoint joint in drivable)
            {
                index++;
                if (!joint.LimitEnabled || joint.LowerLimit > joint.UpperLimit)
                {
                    continue;
                }

                if (!joint.DriveEnabled)
                {
                    limitRows.Add(new RobotPhysicsReport.Row
                    {
                        Prim = joint.PrimPath,
                        Property = "Limits",
                        IsaacSim = $"[{joint.LowerLimit:0.###}, {joint.UpperLimit:0.###}]",
                        Unity = "not tested",
                        UnityNative = "no drive authored, so nothing can push it to a limit",
                    });
                    continue;
                }

                float range = joint.UpperLimit - joint.LowerLimit;
                float margin = joint.IsAngular ? 15f : Mathf.Max(0.2f * range, 0.005f);
                float tolerance = joint.IsAngular ? 1.5f : Mathf.Max(0.02f * range, 0.001f);
                for (int side = 0; side < 2; side++)
                {
                    float limit = side == 0 ? joint.UpperLimit : joint.LowerLimit;
                    float push = side == 0 ? limit + margin : limit - margin;
                    status = $"Limit test {index}/{drivable.Count}: {Short(joint.PrimPath)} → {(side == 0 ? "upper" : "lower")}";
                    float still = 0f;
                    float last = joint.GetPosition();
                    float epsilon = joint.IsAngular ? 0.01f : 1e-5f;
                    for (float t = 0f; t < maxSecondsPerSide && still < 0.25f; t += Time.fixedDeltaTime)
                    {
                        foreach (KeyValuePair<UsdPhysicsJoint, float> entry in hold)
                        {
                            entry.Key.SetDriveTarget(entry.Key == joint ? push : entry.Value);
                        }

                        yield return new WaitForFixedUpdate();
                        float now = joint.GetPosition();
                        still = Mathf.Abs(now - last) < epsilon ? still + Time.fixedDeltaTime : 0f;
                        last = now;
                    }

                    float reached = joint.GetPosition();
                    string unit = joint.IsAngular ? "°" : " m";
                    limitRows.Add(new RobotPhysicsReport.Row
                    {
                        Prim = joint.PrimPath,
                        Property = side == 0 ? "Stops at Upper Limit" : "Stops at Lower Limit",
                        IsaacSim = limit.ToString("0.###") + unit,
                        Unity = reached.ToString("0.###") + unit,
                        UnityNative = $"driven to {push:0.###}{unit}",
                        Match = Mathf.Abs(reached - limit) <= tolerance,
                    });
                }

                hold[joint] = Mathf.Clamp(0f, joint.LowerLimit, joint.UpperLimit);
            }

            foreach (KeyValuePair<ArticulationBody, bool> entry in gravity)
            {
                if (entry.Key != null)
                {
                    entry.Key.useGravity = entry.Value;
                }
            }

            foreach (Collider collider in disabled)
            {
                if (collider != null)
                {
                    collider.enabled = true;
                }
            }

            int passed = limitRows.FindAll(r => r.Match == true).Count;
            status = $"Limit test: {passed} of {limitRows.Count} limits hold where Isaac Sim puts them.";
            Debug.Log("Robot Physics: " + status);
            busy = false;
        }

        // ---- UI ----

        private static readonly string[] Tabs = { "Isaac Sim vs Unity", "Drive joints", "Limit test" };

        private static readonly GUIContent[] RobotChoices =
        {
            new GUIContent("Bundled arm", "Robots/isaac_style_arm.usda, which ships with the sample"),
            new GUIContent("Franka Panda (Isaac Sim 6.0)", "NVIDIA's asset, downloaded on first use"),
            new GUIContent("USD file", "Any robot USD on disk"),
        };

        /// <summary>The panel's width: 40% of the screen, at least 360 pixels and at most 640.</summary>
        private static float PanelWidth => Mathf.Min(Screen.width, Mathf.Clamp(Screen.width * 0.4f, 360f, 640f));

        private void OnGUI()
        {
            Event current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == TogglePanelKey && GUIUtility.keyboardControl == 0)
            {
                ShowPanel = !ShowPanel;
                current.Use();
            }

            if (!ShowPanel)
            {
                if (GUI.Button(new Rect(10f, 10f, 170f, 28f), $"▶  Robot Physics ({TogglePanelKey})"))
                {
                    ShowPanel = true;
                }

                return;
            }

            float width = PanelWidth;
            var panel = new Rect(0f, 0f, width, Screen.height);
            // Opaque: with DockCamera the camera clears only the viewport beside the panel, so a
            // see-through panel would keep every earlier frame's text under the current one.
            if (Event.current.type == EventType.Repaint)
            {
                Color tint = GUI.color;
                GUI.color = PanelColor;
                GUI.DrawTexture(panel, Texture2D.whiteTexture);
                GUI.color = tint;
            }
            GUILayout.BeginArea(new Rect(10f, 10f, width - 20f, Screen.height - 20f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>USD Physics: Isaac Sim robot round trip</b>", RichLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("◀  Hide", $"Hide the panel ({TogglePanelKey})"), GUILayout.Width(70f)))
            {
                ShowPanel = false;
            }

            GUILayout.EndHorizontal();

            DrawRobotChoice();
            GUILayout.Space(6f);
            DrawDownloadLocation();
            GUILayout.Space(6f);
            DrawStatus(width - 20f);
            GUILayout.Space(6f);

            tab = GUILayout.Toolbar(tab, Tabs);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            switch (tab)
            {
                case 0:
                    DrawRows(report != null ? report.Rows : null);
                    break;
                case 1:
                    DrawDriveSliders();
                    break;
                default:
                    GUI.enabled = !busy && robot != null;
                    if (GUILayout.Button("Run limit test (gravity off, drives each joint past both limits)"))
                    {
                        StartCoroutine(RunLimitTest());
                    }

                    GUI.enabled = true;
                    DrawRows(limitRows);
                    break;
            }

            GUILayout.EndScrollView();

            // The notes wrap to the panel, so they sit outside the table's horizontal scroll.
            if (tab == 0 && report != null && report.Notes.Count > 0)
            {
                GUILayout.Label("<b>Notes</b>", RichLabel);
                notesScroll = GUILayout.BeginScrollView(notesScroll, GUILayout.MaxHeight(Screen.height * 0.2f));
                foreach (string note in report.Notes)
                {
                    GUILayout.Label("• " + note, Wrapped);
                }

                GUILayout.EndScrollView();
            }

            GUILayout.EndArea();
        }

        private void DrawRobotChoice()
        {
            GUILayout.Label("<b>Robot</b>", RichLabel);
            GUI.enabled = !busy;
            for (int i = 0; i < RobotChoices.Length; i++)
            {
                if (GUILayout.Toggle((int)selected == i, RobotChoices[i]) && (int)selected != i)
                {
                    selected = (Robot)i;
                    GUIUtility.keyboardControl = 0;
                }
            }

            switch (selected)
            {
                case Robot.BundledArm:
                    GUILayout.Label("A small arm laid out like an Isaac Sim robot, with a pendulum and a ground.", Wrapped);
                    break;
                case Robot.FrankaPanda:
                    string size = Franka != null ? $"{Franka.TotalBytes / (1024f * 1024f):0.#} MB" : "no manifest assigned";
                    GUILayout.Label(FrankaDownloaded
                        ? "Downloaded to the download location."
                        : $"Not downloaded yet ({size}). It is fetched from NVIDIA's Isaac Sim assets and each file's SHA-256 is checked.", Wrapped);
                    break;
                default:
                    GUILayout.BeginHorizontal();
                    FilePath = GUILayout.TextField(FilePath ?? string.Empty);
#if UNITY_EDITOR
                    if (GUILayout.Button("Browse…", GUILayout.Width(80f)))
                    {
                        string picked = UnityEditor.EditorUtility.OpenFilePanel("Robot USD", DirectoryOf(FilePath), "usd,usda,usdc,usdz");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            FilePath = picked;
                        }

                        GUIUtility.ExitGUI();
                    }
#endif
                    GUILayout.EndHorizontal();
                    GUILayout.Label("The path of a .usd, .usda, .usdc or .usdz file.", Wrapped);
                    break;
            }

            bool download = selected == Robot.FrankaPanda && !FrankaDownloaded;
            if (GUILayout.Button(download ? "Download and import" : "Import", GUILayout.Height(26f)))
            {
                _ = LoadAsync(selected);
            }

            GUI.enabled = true;
        }

        private void DrawDownloadLocation()
        {
            string root = RobotPhysicsImport.DownloadRoot;
            string header = showDownloadLocation ? "▾ <b>Download location</b>" : $"▸ <b>Download location</b>  {root}";
            if (GUILayout.Button(header, Foldout))
            {
                showDownloadLocation = !showDownloadLocation;
            }

            if (!showDownloadLocation)
            {
                return;
            }

            GUI.enabled = !busy;
            string typed = GUILayout.TextField(root);
            if (typed != root)
            {
                RobotPhysicsImport.DownloadRoot = typed;
            }

            GUILayout.BeginHorizontal();
#if UNITY_EDITOR
            if (GUILayout.Button("Browse…"))
            {
                string picked = UnityEditor.EditorUtility.OpenFolderPanel("Download robots to", Directory.Exists(root) ? root : string.Empty, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    RobotPhysicsImport.DownloadRoot = picked;
                }

                GUIUtility.ExitGUI();
            }
#endif
            if (GUILayout.Button(new GUIContent("Default", RobotPhysicsImport.DefaultDownloadRoot)))
            {
                RobotPhysicsImport.DownloadRoot = null;
                GUIUtility.keyboardControl = 0;
            }

            GUI.enabled = Directory.Exists(root);
            if (GUILayout.Button("Open folder"))
            {
                Application.OpenURL(new Uri(Path.GetFullPath(root)).AbsoluteUri);
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Each robot downloads into a folder of its own here.", Wrapped);
        }

        private void DrawStatus(float width)
        {
            GUILayout.Label(status, Wrapped);
            if (progress >= 0f)
            {
                Rect bar = GUILayoutUtility.GetRect(width, 6f);
                GUI.Box(bar, GUIContent.none);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(progress), bar.height), Texture2D.whiteTexture);
            }
        }

        private static string DirectoryOf(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(Path.GetFullPath(path.Trim().Trim('"')));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void DrawDriveSliders()
        {
            if (drivable.Count == 0)
            {
                GUILayout.Label("No articulated revolute or prismatic joints.");
                return;
            }

            foreach (UsdPhysicsJoint joint in drivable)
            {
                float lower = joint.LimitEnabled ? joint.LowerLimit : joint.IsAngular ? -180f : -1f;
                float upper = joint.LimitEnabled ? joint.UpperLimit : joint.IsAngular ? 180f : 1f;
                if (!targets.TryGetValue(joint, out float target))
                {
                    target = Mathf.Clamp(joint.DriveTargetPosition, lower, upper);
                }

                string unit = joint.IsAngular ? "°" : " m";
                float now = joint.GetPosition();
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(
                    $"<b>{Short(joint.PrimPath)}</b>   <color={TargetHex}>target {target:0.###}{unit}</color>   " +
                    $"<color={NowHex}>now {now:0.###}{unit}</color>", RichLabel);
                float next = DriveSlider(target, now, lower, upper);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{lower:0.###}{unit}", Small);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{upper:0.###}{unit}", Small);
                GUILayout.EndHorizontal();
                if (!Mathf.Approximately(next, target) || !targets.ContainsKey(joint))
                {
                    targets[joint] = next;
                }

                if (!joint.DriveEnabled)
                {
                    GUILayout.Label("No drive authored: the slider sets the target, but nothing pulls the joint there.", Wrapped);
                }

                GUILayout.EndVertical();
            }

            foreach (UsdPhysicsMimicJoint mimic in mimics)
            {
                UsdPhysicsJoint joint = mimic.Follower;
                string unit = joint.IsAngular ? "°" : " m";
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(
                    $"<b>{Short(joint.PrimPath)}</b>   <color={NowHex}>now {joint.GetPosition():0.###}{unit}</color>", RichLabel);
                GUILayout.Label($"Mimic joint: follows {Short(mimic.ReferenceJointPath)} (gearing {mimic.Gearing:0.###}, " +
                                $"offset {mimic.Offset:0.###}). Move {Short(mimic.ReferenceJointPath)} to move it.", Wrapped);
                GUILayout.EndVertical();
            }
        }

        private static readonly Color PanelColor = new Color(0.09f, 0.09f, 0.1f, 1f);

        private const string TargetHex = "#4fc3f7";
        private const string NowHex = "#ffb74d";
        private const float ThumbWidth = 12f;

        /// <summary>
        /// A slider that reads on the dark panel: a light track filled up to the drive target, a
        /// marker where the joint is now, and a large thumb for the target.
        /// </summary>
        private static float DriveSlider(float target, float now, float lower, float upper)
        {
            Rect rect = GUILayoutUtility.GetRect(100f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                float travel = rect.width - ThumbWidth;
                float XOf(float value) => rect.x + (ThumbWidth * 0.5f) + (travel * Mathf.InverseLerp(lower, upper, value));
                var track = new Rect(rect.x + (ThumbWidth * 0.5f), rect.center.y - 3f, travel, 6f);
                Color tint = GUI.color;
                GUI.color = new Color(0.55f, 0.55f, 0.58f, tint.a);
                GUI.DrawTexture(track, Texture2D.whiteTexture);
                GUI.color = TargetColor * new Color(1f, 1f, 1f, tint.a);
                GUI.DrawTexture(new Rect(track.x, track.y, XOf(target) - track.x, track.height), Texture2D.whiteTexture);
                GUI.color = NowColor * new Color(1f, 1f, 1f, tint.a);
                GUI.DrawTexture(new Rect(XOf(now) - 1.5f, rect.y + 1f, 3f, rect.height - 2f), Texture2D.whiteTexture);
                GUI.color = tint;
            }

            return GUI.HorizontalSlider(rect, target, lower, upper, SliderBackground, SliderThumb);
        }

        private static readonly Color TargetColor = new Color(0.31f, 0.76f, 0.97f);
        private static readonly Color NowColor = new Color(1f, 0.72f, 0.3f);

        // The table is wider than a narrow panel; its scroll view scrolls sideways.
        private const float PrimWidth = 150f;
        private const float PropertyWidth = 150f;
        private const float ValueWidth = 190f;

        private static void DrawRows(List<RobotPhysicsReport.Row> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                GUILayout.Label("Nothing yet.");
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Prim</b>", Cell, GUILayout.Width(PrimWidth));
            GUILayout.Label("<b>Property</b>", Cell, GUILayout.Width(PropertyWidth));
            GUILayout.Label("<b>Isaac Sim</b>", Cell, GUILayout.Width(ValueWidth));
            GUILayout.Label("<b>Unity</b>", Cell, GUILayout.Width(ValueWidth));
            GUILayout.Label("<b>Unity (native)</b>", Cell);
            GUILayout.EndHorizontal();
            string previous = null;
            foreach (RobotPhysicsReport.Row row in rows)
            {
                string mark = row.Match == null ? " " : row.Match.Value ? "<color=#4caf50>✔</color>" : "<color=#f44336>✘</color>";
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Prim == previous ? string.Empty : Short(row.Prim), Cell, GUILayout.Width(PrimWidth));
                GUILayout.Label(row.Property, Cell, GUILayout.Width(PropertyWidth));
                GUILayout.Label(row.IsaacSim, Cell, GUILayout.Width(ValueWidth));
                GUILayout.Label(mark + " " + row.Unity, Cell, GUILayout.Width(ValueWidth));
                GUILayout.Label(row.UnityNative, Cell);
                GUILayout.EndHorizontal();
                previous = row.Prim;
            }
        }

        private static string Short(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static GUIStyle richLabel;
        private static GUIStyle wrapped;

        private static GUIStyle cell;
        private static GUIStyle small;
        private static GUIStyle sliderBackground;
        private static GUIStyle sliderThumb;

        private static GUIStyle Small => small ?? (small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.7f, 0.7f, 0.7f) } });

        // The slider draws its own track (DriveSlider), so its background is empty.
        private static GUIStyle SliderBackground => sliderBackground ?? (sliderBackground = new GUIStyle());

        private static GUIStyle SliderThumb => sliderThumb ?? (sliderThumb = new GUIStyle
        {
            fixedWidth = ThumbWidth,
            normal = { background = Solid(new Color(0.92f, 0.92f, 0.92f)) },
            hover = { background = Solid(Color.white) },
            active = { background = Solid(TargetColor) },
        });

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
        private static GUIStyle foldout;

        // A table cell: one line, so a long value widens the table (it scrolls sideways) instead of its row.
        private static GUIStyle Cell => cell ?? (cell = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, clipping = TextClipping.Clip });

        private static GUIStyle Foldout => foldout ?? (foldout = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, clipping = TextClipping.Clip });

        private static GUIStyle RichLabel => richLabel ?? (richLabel = new GUIStyle(GUI.skin.label) { richText = true });

        private static GUIStyle Wrapped => wrapped ?? (wrapped = new GUIStyle(GUI.skin.label) { wordWrap = true });
    }
}
