using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    /// <summary>
    /// Scene driver and runtime HUD for the Live Sync Example.
    ///
    /// The sample is deliberately self-contained: it builds its own sync hierarchy from primitives in
    /// <see cref="Awake"/> (so there are no scene assets, no art dependencies and nothing to import), hands
    /// that root to <see cref="UsdLiveSyncServer"/>, and starts the server explicitly in <see cref="Start"/>.
    ///
    /// The built hierarchy shows both ownership directions the protocol distinguishes:
    ///   * <c>Ground</c> / <c>PropCube</c> / <c>PropSphere</c> / <c>PropCapsule</c> carry a
    ///     <see cref="UsdSyncNode"/> with <c>AcceptsRemoteWrites</c> = true — an external client may move them.
    ///   * <c>OrbitingBeacon</c> carries no <see cref="UsdSyncNode"/>, so it is observe-only: this script keeps
    ///     animating it, Unity stays authoritative, and inbound writes to it are ignored rather than fought
    ///     frame by frame. It also gives the outbound <c>delta</c> stream something to broadcast with no client
    ///     input at all, which makes `usd_live_sync.py --watch` show traffic immediately.
    ///
    /// The HUD stays clear of the thing it drives: it docks into two narrow columns with an open band
    /// between them, draws on translucent cards, and either column collapses to a corner tab. <c>H</c>
    /// hides both at once for an unobstructed look at the scene.
    /// </summary>
    [RequireComponent(typeof(UsdLiveSyncServer))]
    public sealed class UsdLiveSyncSample : MonoBehaviour
    {
        [Tooltip("Build the demo hierarchy from primitives on start. Turn off and assign the server's Sync " +
                 "Root yourself to run the live channel against your own scene content.")]
        [SerializeField] private bool buildDemoScene = true;

        [Tooltip("Keep animating the observe-only beacon, so the outbound delta stream has traffic even when " +
                 "no client is pushing edits.")]
        [SerializeField] private bool animateBeacon = true;

        [Tooltip("Degrees per second for the observe-only beacon's orbit.")]
        [SerializeField] private float orbitDegreesPerSecond = 40.0f;

        private const int MaxLogLines = 80;

        private UsdLiveSyncServer server;
        private Transform beacon;
        private Vector3 beaconPivot;
        private float beaconRadius = 1.9f;
        private float beaconAngle;

        private readonly List<string> log = new();
        private int lastClientCount = -1;
        private string runtimeText;
        private Vector2 controlScroll;
        private Vector2 infoScroll;
        private bool showControls = true;
        private bool showInfo = true;

        private GUIStyle titleStyle;
        private GUIStyle sectionStyle;
        private GUIStyle bodyStyle;
        private GUIStyle smallStyle;
        private GUIStyle panelStyle;
        private GUIStyle buttonStyle;

        // Both columns share one collapse affordance: a compact button in the card header.
        private static readonly GUIContent CollapseContent =
            new("–", "Collapse this column. Press H to hide the whole HUD.");

        // ==========================================================================================
        // Lifecycle
        // ==========================================================================================

        private void Awake()
        {
            server = GetComponent<UsdLiveSyncServer>();

            EnsureCameraAndLight();

            // Built in Awake so the hierarchy exists before the server's prim table and baseline export run.
            if (buildDemoScene && server.SyncRoot == null)
            {
                server.SyncRoot = BuildDemoScene().transform;
            }

            UsdLiveSyncServer.SceneReset += OnSceneReset;
        }

        private void Start()
        {
            RefreshRuntimeInfo();

            // The scene ships with the server's Auto Start off, so the order here is explicit and readable:
            // geometry (Awake) -> prim table + baseline + listener (StartServer). Calling it when the server is
            // already running is a no-op, so an inspector-enabled Auto Start does no harm either.
            server.StartServer();

            Append(server.IsRunning
                ? $"Listening on {server.BindAddress}:{server.Port} — {server.TrackedNodeCount} tracked prim(s)."
                : "Server failed to start — see the console for the bind error.");
            Append("Baseline + overrides folder: " + server.OutputDirectory);
            Append("Connect a client:  python usd_live_sync.py --watch");
        }

        private void OnDestroy()
        {
            UsdLiveSyncServer.SceneReset -= OnSceneReset;
        }

        private void Update()
        {
            if (animateBeacon && beacon != null)
            {
                beaconAngle += orbitDegreesPerSecond * Time.deltaTime;
                float radians = beaconAngle * Mathf.Deg2Rad;
                beacon.localPosition = beaconPivot + new Vector3(
                    Mathf.Cos(radians) * beaconRadius,
                    0.15f * Mathf.Sin(beaconAngle * 2.0f * Mathf.Deg2Rad),
                    Mathf.Sin(radians) * beaconRadius);
                beacon.localRotation = Quaternion.Euler(0.0f, -beaconAngle, 0.0f);
            }

            // Connect/disconnect is only observable from the outside through the client count, so poll it and
            // narrate the change in the HUD log.
            int clients = server.ClientCount;
            if (clients != lastClientCount)
            {
                if (lastClientCount >= 0)
                {
                    Append(clients > lastClientCount
                        ? $"Client connected ({clients} connected)."
                        : $"Client disconnected ({clients} connected).");
                }

                lastClientCount = clients;
            }
        }

        private void OnSceneReset()
        {
            // Restoring the baseline also rewinds the beacon's orbit, so its transform and the angle this
            // script integrates do not drift apart after a reset.
            beaconAngle = 0.0f;
            Append("Reset — every tracked prim restored to its baseline.");
        }

        // ==========================================================================================
        // Demo scene
        // ==========================================================================================

        private GameObject BuildDemoScene()
        {
            var root = new GameObject("SyncRoot");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            GameObject ground = CreatePrimitive(root.transform, PrimitiveType.Cube, "Ground",
                new Vector3(0.0f, -0.05f, 0.0f), new Vector3(6.0f, 0.1f, 6.0f), new Color(0.34f, 0.36f, 0.40f));
            AddSyncNode(ground);

            GameObject cube = CreatePrimitive(root.transform, PrimitiveType.Cube, "PropCube",
                new Vector3(-1.1f, 0.5f, 0.4f), Vector3.one, new Color(0.14f, 0.55f, 0.95f));
            cube.transform.localRotation = Quaternion.Euler(0.0f, 24.0f, 0.0f);
            AddSyncNode(cube);

            GameObject sphere = CreatePrimitive(root.transform, PrimitiveType.Sphere, "PropSphere",
                new Vector3(0.5f, 0.45f, -0.9f), Vector3.one * 0.9f, new Color(0.18f, 0.72f, 0.42f));
            AddSyncNode(sphere);

            GameObject capsule = CreatePrimitive(root.transform, PrimitiveType.Capsule, "PropCapsule",
                new Vector3(1.4f, 0.75f, 0.6f), Vector3.one * 0.75f, new Color(0.92f, 0.62f, 0.24f));
            AddSyncNode(capsule);

            // No UsdSyncNode: streamed out to clients, never driven by them.
            GameObject beaconObject = CreatePrimitive(root.transform, PrimitiveType.Cube, "OrbitingBeacon",
                new Vector3(beaconRadius, 0.9f, 0.0f), Vector3.one * 0.35f, new Color(0.86f, 0.24f, 0.35f));
            beacon = beaconObject.transform;
            beaconPivot = new Vector3(0.0f, 0.9f, 0.0f);

            FrameCamera(root);
            return root;
        }

        private static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name,
            Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            SetColor(go, color);
            return go;
        }

        private static void AddSyncNode(GameObject target)
        {
            target.AddComponent<UsdSyncNode>().AcceptsRemoteWrites = true;
        }

        private static void SetColor(GameObject target, Color color)
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

        private static void EnsureCameraAndLight()
        {
            if (Camera.main == null)
            {
                var cameraObject = new GameObject("Main Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.tag = "MainCamera";
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.05f, 0.06f, 0.075f, 1.0f);
            }

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Key Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                lightObject.transform.rotation = Quaternion.Euler(45.0f, -35.0f, 0.0f);
            }
        }

        // The HUD docks to both edges, so the geometry is centred in the open band between the columns.
        private static void FrameCamera(GameObject target)
        {
            Camera camera = Camera.main;
            if (camera == null || target == null)
            {
                return;
            }

            camera.transform.position = new Vector3(3.2f, 3.7f, -7.5f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0.1f, 0.5f, 0.0f) - camera.transform.position, Vector3.up);
            camera.nearClipPlane = 0.01f;
        }

        // ==========================================================================================
        // HUD
        // ==========================================================================================

        private void OnGUI()
        {
            EnsureStyles();
            HandleHudHotkey();

            const float margin = 20.0f;
            const float columnWidth = 320.0f;
            const float tabWidth = 150.0f;
            const float tabHeight = 34.0f;

            // Wide enough for both columns, the margins, and a centre band the scene reads through. Below
            // this the whole HUD scales down, so the band keeps its share of the view instead of closing up.
            const float minWidth = 920.0f;
            const float minHeight = 460.0f;

            // Scale down only when the window is too small for the layout, matching the other samples.
            float scale = Mathf.Min(1.0f, Mathf.Min(Screen.width / (minWidth + margin * 2.0f), Screen.height / (minHeight + margin * 2.0f)));
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));

            float fullW = Screen.width / scale;
            float fullH = Screen.height / scale;
            float height = fullH - margin * 2.0f;

            // Capped rather than full-height, so the lower part of the view stays open too. Content past
            // the cap scrolls inside the card.
            float controlHeight = Mathf.Min(height, 520.0f);
            float infoHeight = Mathf.Min(height, 560.0f);

            if (showControls)
            {
                GUILayout.BeginArea(new Rect(margin, margin, columnWidth, controlHeight));
                DrawControlPanel(columnWidth, controlHeight);
                GUILayout.EndArea();
            }
            else if (GUI.Button(new Rect(margin, margin, tabWidth, tabHeight), "Controls  ▸", buttonStyle))
            {
                showControls = true;
            }

            if (showInfo)
            {
                GUILayout.BeginArea(new Rect(fullW - margin - columnWidth, margin, columnWidth, infoHeight));
                DrawInfoPanel(columnWidth, infoHeight);
                GUILayout.EndArea();
            }
            else if (GUI.Button(new Rect(fullW - margin - tabWidth, margin, tabWidth, tabHeight), "◂  Activity", buttonStyle))
            {
                showInfo = true;
            }

            GUI.matrix = previousMatrix;
        }

        // H toggles the whole HUD: hide it for an unobstructed look at the scene, press again to bring the
        // columns back. Read from the GUI event stream, so it works under either input backend.
        private void HandleHudHotkey()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown || current.keyCode != KeyCode.H)
            {
                return;
            }

            bool anyVisible = showControls || showInfo;
            showControls = !anyVisible;
            showInfo = !anyVisible;
            current.Use();
        }

        private void DrawControlPanel(float width, float height)
        {
            float inner = width - 56.0f;
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(width), GUILayout.Height(height));
            controlScroll = GUILayout.BeginScrollView(controlScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));

            GUILayout.BeginHorizontal(GUILayout.Width(inner));
            GUILayout.Label("USD Live Sync", titleStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(CollapseContent, buttonStyle, GUILayout.Width(30.0f), GUILayout.Height(26.0f)))
            {
                showControls = false;
            }

            GUILayout.EndHorizontal();
            GUILayout.Label(runtimeText, smallStyle, GUILayout.Width(inner));

            GUILayout.Space(10.0f);
            GUILayout.Label("Server", sectionStyle);
            GUILayout.Label(server.IsRunning
                ? $"Listening on {server.BindAddress}:{server.Port}"
                : "Stopped", bodyStyle, GUILayout.Width(inner));
            GUILayout.Label($"Clients: {server.ClientCount}          Tracked prims: {server.TrackedNodeCount}", bodyStyle, GUILayout.Width(inner));
            GUILayout.Label($"Messages sent: {server.SequenceNumber}", smallStyle, GUILayout.Width(inner));

            GUILayout.Space(10.0f);
            if (server.IsRunning)
            {
                if (GUILayout.Button("Stop Server", buttonStyle, GUILayout.Width(inner), GUILayout.Height(40.0f)))
                {
                    server.StopServer();
                    lastClientCount = -1;
                    Append("Server stopped.");
                }
            }
            else if (GUILayout.Button("Start Server", SampleTheme.PrimaryButton, GUILayout.Width(inner), GUILayout.Height(40.0f)))
            {
                server.StartServer();
                Append(server.IsRunning
                    ? $"Listening on {server.BindAddress}:{server.Port} — {server.TrackedNodeCount} tracked prim(s)."
                    : "Server failed to start — see the console for the bind error.");
            }

            GUILayout.Space(6.0f);
            if (GUILayout.Button(new GUIContent("Reset To Baseline",
                    "Restore every tracked prim to the transform captured when the server started, and fire the " +
                    "SceneReset event. A client can trigger the same thing with --reset."),
                buttonStyle, GUILayout.Width(inner), GUILayout.Height(38.0f)))
            {
                server.ResetToBaseline();
            }

            GUILayout.Space(10.0f);
            GUILayout.Label("Options", sectionStyle);
            animateBeacon = SampleTheme.Checkbox(animateBeacon, new GUIContent("Animate the observe-only beacon",
                "Keeps OrbitingBeacon moving so the outbound delta stream has traffic with no client input. " +
                "The beacon has no UsdSyncNode, so remote writes to it are ignored."));

            GUILayout.Space(10.0f);
            GUILayout.Label("USD output", sectionStyle);
            GUILayout.Label(server.OutputDirectory, smallStyle, GUILayout.Width(inner));
            if (GUILayout.Button("Reveal Output Folder", buttonStyle, GUILayout.Width(inner), GUILayout.Height(34.0f)))
            {
                RevealOutputFolder();
            }

            GUILayout.Space(12.0f);
            GUILayout.Label("Press H to hide the HUD entirely.", smallStyle, GUILayout.Width(inner));

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawInfoPanel(float width, float height)
        {
            float inner = width - 56.0f;
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(width), GUILayout.Height(height));
            infoScroll = GUILayout.BeginScrollView(infoScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(inner));

            GUILayout.BeginHorizontal(GUILayout.Width(inner));
            GUILayout.Label("Tracked Prims", titleStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(CollapseContent, buttonStyle, GUILayout.Width(30.0f), GUILayout.Height(26.0f)))
            {
                showInfo = false;
            }

            GUILayout.EndHorizontal();
            GUILayout.Label("'rw' accepts inbound set_transform; the rest are observe-only.", smallStyle, GUILayout.Width(inner));
            GUILayout.Space(6.0f);

            foreach (UsdLiveSyncServer.TrackedPrim prim in server.TrackedPrims)
            {
                GUILayout.Label((prim.AcceptsRemoteWrites ? "rw   " : "ro   ") + prim.PrimPath, bodyStyle, GUILayout.Width(inner));
            }

            GUILayout.Space(14.0f);
            GUILayout.Label("Activity", sectionStyle, GUILayout.Width(inner));
            for (int i = log.Count - 1; i >= 0; i--)
            {
                GUILayout.Label(log[i], smallStyle, GUILayout.Width(inner));
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            SampleTheme.EnsureBuilt();

            GUI.skin = Instantiate(GUI.skin);
            GUI.skin.label.wordWrap = true;

            titleStyle = SampleTheme.Title;
            sectionStyle = SampleTheme.Section;
            bodyStyle = SampleTheme.Body;
            smallStyle = SampleTheme.FieldLabel;
            // Translucent, so the scene reads through the columns and not only between them.
            panelStyle = SampleTheme.TranslucentCard(0.88f);
            buttonStyle = SampleTheme.Button;
        }

        // ==========================================================================================
        // Helpers
        // ==========================================================================================

        private void RefreshRuntimeInfo()
        {
            try
            {
                runtimeText = UsdExporter.GetRuntimeInfo().ToString();
            }
            catch (Exception exception)
            {
                // The live transform channel does not need the native plugin — only the baseline export does.
                runtimeText = "USD runtime unavailable: " + exception.Message +
                              " — the live channel still runs, without base_stage.usda.";
            }
        }

        private void RevealOutputFolder()
        {
            try
            {
                string dir = server.OutputDirectory;
                Directory.CreateDirectory(dir);
                Application.OpenURL("file://" + dir.Replace('\\', '/'));
            }
            catch (Exception exception)
            {
                Append("Could not open the output folder: " + exception.Message);
            }
        }

        private void Append(string line)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (log.Count > MaxLogLines)
            {
                log.RemoveRange(0, log.Count - MaxLogLines);
            }
        }
    }
}
