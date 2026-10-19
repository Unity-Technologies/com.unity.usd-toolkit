using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    public struct ObstaclePreset
    {
        public string Name;
        public Vector3 Size;   // USD frame: x length, y width, z height (m)
        public Color Color;
    }

    /// <summary>Operator input, orbit camera and the IMGUI panels (fleet + debug latency).</summary>
    public sealed partial class AgvFleetScene
    {
        private enum Tool { Goal, Box, Pallet, Barrier, Delete }

        private static readonly ObstaclePreset[] Presets =
        {
            new ObstaclePreset { Name = "box", Size = new Vector3(0.8f, 0.8f, 0.8f), Color = new Color(0.78f, 0.58f, 0.36f) },
            new ObstaclePreset { Name = "pallet", Size = new Vector3(1.2f, 1.0f, 1.2f), Color = new Color(0.55f, 0.42f, 0.26f) },
            new ObstaclePreset { Name = "barrier", Size = new Vector3(2.4f, 0.3f, 1.0f), Color = new Color(0.97f, 0.80f, 0.10f) },
        };

        private Tool tool = Tool.Goal;
        private float placeYawDeg;
        private Rect panelTabRect, latencyTabRect;  // screen-space (GUI y-down) pixels, for click blocking
        [SerializeField] private bool showUi = true;   // H toggles the left panel (clean view for visitors)
        private const float PanelWidth = 250f;      // GUI units at 1080p
        private const float TabWidth = 22f, TabHeight = 64f;
        private const float LatencyWidth = 200f;    // right-docked debug panel, GUI units at 1080p
        private float panelOpen = 1f;               // 0 collapsed .. 1 open, animated
        private float latencyOpen = 1f;
        private Vector2 panelScroll, latencyScroll;
        private bool pointerOverUi;
        private Vector3 camPivot = new Vector3(-0.5f, 0f, 3f);
        private float camYaw = 0f, camPitch = 62f, camDist = 34f;
        private GUIStyle panel, tab, section, label, small, header, button, toggleOn;
        private Texture2D lineTex;
        private float lastMarkerRefresh;

        // ---- input --------------------------------------------------------------------------------
        private void HandleInput()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb != null)
            {
                if (kb.hKey.wasPressedThisFrame) showUi = !showUi;
                if (kb.lKey.wasPressedThisFrame) showLatencyPanel = !showLatencyPanel;
                if (kb.rKey.wasPressedThisFrame) placeYawDeg = (placeYawDeg + 90f) % 180f;
                if (kb.digit1Key.wasPressedThisFrame) tool = Tool.Goal;
                if (kb.digit2Key.wasPressedThisFrame) tool = Tool.Box;
                if (kb.digit3Key.wasPressedThisFrame) tool = Tool.Pallet;
                if (kb.digit4Key.wasPressedThisFrame) tool = Tool.Barrier;
                if (kb.digit5Key.wasPressedThisFrame) tool = Tool.Delete;
                if (kb.escapeKey.wasPressedThisFrame) Select(null);
            }
            if (Time.unscaledTime - lastMarkerRefresh > 0.5f)
            {
                lastMarkerRefresh = Time.unscaledTime;
                UpdateGoalMarker();
            }
            if (mouse == null || viewCamera == null) return;
            Vector2 mp = mouse.position.ReadValue();
            Vector2 guiPoint = new Vector2(mp.x, Screen.height - mp.y);
            pointerOverUi = guiPoint.x < PanelPixelWidth || guiPoint.x > Screen.width - LatencyPixelWidth
                            || panelTabRect.Contains(guiPoint) || latencyTabRect.Contains(guiPoint);
            if (pointerOverUi) return;

            bool left = mouse.leftButton.wasPressedThisFrame;
            bool right = mouse.rightButton.wasPressedThisFrame;
            if (!left && !right) return;
            Ray ray = viewCamera.ScreenPointToRay(mp);

            // obstacles and AGVs have colliders; the floor is the plane y = 0
            if (Physics.Raycast(ray, out RaycastHit hit, 500f))
            {
                AgvView agv = hit.collider.GetComponentInParent<AgvView>();
                if (agv && left)
                {
                    Select(selected == agv ? null : agv);
                    tool = Tool.Goal;
                    return;
                }
                if (obstacleViews.ContainsKey(hit.collider.gameObject.name) && (right || tool == Tool.Delete))
                {
                    DeleteObstacle(hit.collider.gameObject.name);
                    return;
                }
            }
            if (!left) return;
            if (!new Plane(Vector3.up, 0f).Raycast(ray, out float enter)) return;
            Vector3 p = ray.GetPoint(enter);
            if (floorBounds.size.sqrMagnitude > 0f &&
                (p.x < floorBounds.min.x + 0.5f || p.x > floorBounds.max.x - 0.5f || p.z < floorBounds.min.z + 0.5f || p.z > floorBounds.max.z - 0.5f))
            {
                Log("click outside the warehouse ignored");
                return;
            }
            switch (tool)
            {
                case Tool.Goal: SetGoal(p); break;
                case Tool.Box: PlaceObstacle(Presets[0], p); break;
                case Tool.Pallet: PlaceObstacle(Presets[1], p); break;
                case Tool.Barrier: PlaceObstacle(Presets[2], p); break;
            }
        }

        // ---- camera -------------------------------------------------------------------------------
        private void SetupCamera()
        {
            if (!viewCamera) return;
            viewCamera.nearClipPlane = 0.1f;
            viewCamera.farClipPlane = 300f;
            viewCamera.fieldOfView = 45f;
        }

        /// <summary>Default overview of whatever environment was imported (floor-level bounds).</summary>
        private void FrameCamera(Bounds b)
        {
            if (b.size.sqrMagnitude <= 0f) return;
            camPivot = new Vector3(b.center.x, 0f, b.center.z);
            // look across the hall, so its long side runs along the (wide) screen; fit both sides
            camYaw = b.size.z >= b.size.x ? 90f : 0f;
            float longSide = Mathf.Max(b.size.x, b.size.z), shortSide = Mathf.Min(b.size.x, b.size.z);
            camDist = Mathf.Clamp(Mathf.Max(longSide * 0.95f, shortSide * 1.25f), 15f, 130f);
            camPitch = 62f;
        }

        private void UpdateCamera()
        {
            if (!viewCamera) return;
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 d = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed && d.sqrMagnitude > 0)
                {
                    camYaw += d.x * 0.25f;
                    camPitch = Mathf.Clamp(camPitch - d.y * 0.25f, 15f, 89f);
                }
                if (mouse.middleButton.isPressed && d.sqrMagnitude > 0)
                {
                    Quaternion yawRot = Quaternion.Euler(0, camYaw, 0);
                    camPivot -= yawRot * new Vector3(d.x, 0, d.y) * camDist * 0.0015f;
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (!pointerOverUi && Mathf.Abs(scroll) > 0.01f) camDist = Mathf.Clamp(camDist * (scroll > 0 ? 0.9f : 1.1f), 4f, 150f);
            }
            Quaternion rot = Quaternion.Euler(camPitch, camYaw, 0);
            viewCamera.transform.SetPositionAndRotation(camPivot - rot * Vector3.forward * camDist, rot);

            // the panels are docked, not overlaid: the 3D view gets the width between them
            panelOpen = Mathf.MoveTowards(panelOpen, showUi ? 1f : 0f, Time.unscaledDeltaTime * 6f);
            latencyOpen = Mathf.MoveTowards(latencyOpen, showLatencyPanel ? 1f : 0f, Time.unscaledDeltaTime * 6f);
            float sw = Mathf.Max(1, Screen.width);
            float left = Mathf.Clamp01(PanelPixelWidth / sw);
            float right = Mathf.Clamp(LatencyPixelWidth / sw, 0f, 1f - left - 0.05f);
            viewCamera.rect = new Rect(left, 0f, 1f - left - right, 1f);
        }

        private static float UiScale => Mathf.Max(1f, Screen.height / 1080f);
        private static float Ease(float t) => t * t * (3f - 2f * t);
        /// <summary>Current on-screen width of the docked panel in pixels (animates while toggling).</summary>
        // in a narrow window (e.g. the editor's Game view) the panels give way so the scene keeps >= ~half the width
        private static float PanelW => Mathf.Min(PanelWidth, Screen.width / UiScale * 0.28f);
        private static float LatencyW => Mathf.Min(LatencyWidth, Screen.width / UiScale * 0.22f);
        private float PanelPixelWidth => PanelW * UiScale * Ease(panelOpen);
        private float LatencyPixelWidth => LatencyW * UiScale * Ease(latencyOpen);

        private void OnDisable()
        {
            if (viewCamera) viewCamera.rect = new Rect(0f, 0f, 1f, 1f);
        }

        // ---- GUI ----------------------------------------------------------------------------------
        private void InitStyles()
        {
            if (panel != null) return;
            // opaque: the camera viewport starts at the panel's right edge, so nothing is rendered behind it
            panel = new GUIStyle { padding = new RectOffset(10, 10, 10, 10) };
            panel.normal.background = SolidTex(new Color(0.075f, 0.085f, 0.105f, 1f));
            tab = new GUIStyle(GUI.skin.button) { fontSize = 14, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
            tab.normal.background = SolidTex(new Color(0.075f, 0.085f, 0.105f, 1f));
            tab.hover.background = SolidTex(new Color(0.14f, 0.16f, 0.20f, 1f));
            tab.active.background = tab.hover.background;
            tab.normal.textColor = tab.hover.textColor = tab.active.textColor = new Color(0.75f, 0.85f, 0.95f);
            lineTex = SolidTex(new Color(1f, 1f, 1f, 0.10f));
            label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true };   // narrow panels: let long lines wrap
            label.normal.textColor = new Color(0.92f, 0.93f, 0.95f);
            small = new GUIStyle(label) { fontSize = 12 };
            small.normal.textColor = new Color(0.70f, 0.73f, 0.78f);
            header = new GUIStyle(label) { fontSize = 17, fontStyle = FontStyle.Bold };
            section = new GUIStyle(label) { fontSize = 11, fontStyle = FontStyle.Bold, margin = new RectOffset(4, 4, 2, 2) };
            section.normal.textColor = new Color(0.55f, 0.70f, 0.85f);
            button = new GUIStyle(GUI.skin.button) { fontSize = 13, fixedHeight = 28 };
            toggleOn = new GUIStyle(button) { fontStyle = FontStyle.Bold };
            toggleOn.normal.textColor = new Color(0.45f, 0.8f, 1f);
        }

        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void Section(string title)
        {
            GUILayout.Space(10);
            Rect r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, lineTex);
            GUILayout.Space(4);
            GUILayout.Label(title.ToUpperInvariant(), section);
        }

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        private void OnGUI()
        {
            InitStyles();
            float s = UiScale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1));

            // ---- docked fleet panel (slides in/out from the left edge) ----
            float h = Screen.height / s;
            float pw = PanelW, lw = LatencyW;
            float px = -pw * (1f - Ease(panelOpen));
            if (panelOpen > 0f)
            {
                GUILayout.BeginArea(new Rect(px, 0, pw, h), panel);
                DrawFleetPanel();
                GUILayout.EndArea();
            }

            // collapse/expand tab hugging the panel's right edge, always visible
            var tabRect = new Rect(px + pw, 16, TabWidth, TabHeight);
            if (GUI.Button(tabRect, new GUIContent(showUi ? "◀" : "▶", showUi ? "Hide panel (H)" : "Show panel (H)"), tab))
                showUi = !showUi;
            panelTabRect = new Rect(tabRect.x * s, tabRect.y * s, tabRect.width * s, tabRect.height * s);

            // ---- docked latency panel (slides in/out from the right edge) ----
            float w = Screen.width / s;
            float lx = w - lw * Ease(latencyOpen);
            if (latencyOpen > 0f)
            {
                GUILayout.BeginArea(new Rect(lx, 0, lw, h), panel);
                DrawLatencyPanel();
                GUILayout.EndArea();
            }
            var latTab = new Rect(lx - TabWidth, 16, TabWidth, TabHeight);
            if (GUI.Button(latTab, new GUIContent(showLatencyPanel ? "▶" : "◀", showLatencyPanel ? "Hide latency (L)" : "Show latency (L)"), tab))
                showLatencyPanel = !showLatencyPanel;
            latencyTabRect = new Rect(latTab.x * s, latTab.y * s, latTab.width * s, latTab.height * s);
        }

        private void DrawFleetPanel()
        {
            GUILayout.Label("AGV Fleet", header);
            GUILayout.Label($"<size=12><color=#9aa>{environment.Name} · {backend.DisplayName}</color></size>", small);
            GUILayout.Space(4);
            string sync = HubLive ? $"<color=#5d5>● live</color>  {latency.StateRateHz:F0} Hz"
                : backend.Connected ? "<color=#fc4>● connected, no state</color>"
                : $"<color=#f55>● {backend.DisplayName} offline</color>  <size=11>(retrying {backend.Endpoint})</size>";
            GUILayout.Label($"Sync: {sync}", label);
            GUILayout.Label($"<size=11>stage: {importStatus} · reconnects {backend.Reconnects} · missed frames {missedStates}</size>", small);
            if (envAnim != null) GUILayout.Label($"<size=11>cell animation: {EnvAnimStatus}</size>", small);
            if (backend.Note != null) GUILayout.Label($"<size=11><color=#fc4>{backend.Note}</color></size>", small);

            panelScroll = GUILayout.BeginScrollView(panelScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

            Section("Vehicles");
            foreach (AgvView v in agvs.Values)
            {
                AgvStateMsg st = v.Last;
                string state = st?.nav ?? "-";
                string stColor = state == "blocked" ? "#f66" : state == "navigating" ? "#6cf" : state == "waiting" || state == "arrived" ? "#fd6" : "#ccc";
                string detail = string.IsNullOrEmpty(st?.detail) ? "" : $"  <size=11><color=#aab>({st.detail})</color></size>";
                string sel = selected == v ? "  <color=#fff>◀ next goal</color>" : "";
                GUILayout.Label($"<color=#{Hex(v.Color)}>■</color> <b>{v.AgvId}</b>  <color={stColor}>{state}</color>{detail}{sel}", label);
                GUILayout.Label($"     task: {st?.task ?? "-"}", small);
            }
            if (agvs.Count == 0) GUILayout.Label("<color=#aab>waiting for AGVs…</color>", label);
            GUILayout.Space(4);
            string q = lastFleet?.queue != null && lastFleet.queue.Length > 0 ? string.Join(", ", lastFleet.queue) : "empty";
            GUILayout.Label($"Queue: {q}", label);
            GUILayout.Label($"<size=11>deadlock recoveries: {lastFleet?.recoveries ?? 0} · stall recoveries: {lastFleet?.stalls ?? 0} · obstacles: {ops.Obstacles.Count}</size>", small);

            Section("Click the floor to");
            GUILayout.BeginHorizontal();
            ToolButton(Tool.Goal, "1 Goal");
            ToolButton(Tool.Box, "2 Box");
            ToolButton(Tool.Pallet, "3 Pallet");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            ToolButton(Tool.Barrier, "4 Barrier");
            ToolButton(Tool.Delete, "5 Delete");
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=11>click an AGV first to send it the next goal · right-click an obstacle to delete · R rotates · L latency panel · H hide panel</size>", small);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset scene", button)) ResetScene();
            if (GUILayout.Button(showLatencyPanel ? "Hide latency" : "Show latency", button)) showLatencyPanel = !showLatencyPanel;
            GUILayout.EndHorizontal();

            if (environment is GeneratedEnvironmentProvider generated)
            {
                Section("Generated hall");
                GUILayout.Label($"<size=11>{generated.Factory.Params}</size>", small);
                GUI.enabled = CanRegenerate;
                if (GUILayout.Button($"Regenerate (seed {generatorParams.seed + 1})", button)) Regenerate();
                GUI.enabled = true;
                if (IsaacDriving) GUILayout.Label("<size=11>stop Isaac Sim to regenerate (it loads the hall at launch)</size>", small);
            }

            Section("Events");
            foreach (string l in eventLines) GUILayout.Label($"<size=11>{l}</size>", small);
            GUILayout.EndScrollView();
        }

        private void ToolButton(Tool t, string text)
        {
            if (GUILayout.Button(text, tool == t ? toggleOn : button)) tool = t;
        }

        private void DrawLatencyPanel()
        {
            GUILayout.Label("Latency (debug)", header);
            GUILayout.Label($"state stream {latency.StateRateHz:F1} Hz (target 30, min 20)", label);
            latencyScroll = GUILayout.BeginScrollView(latencyScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

            Section("Pose lag");
            foreach (AgvView v in agvs.Values)
            {
                var (med, p95, max) = latency.LagStats(v.AgvId);
                string c = p95 <= 100 ? "#5d5" : p95 <= 200 ? "#fc4" : "#f55";
                GUILayout.Label($"<color=#{Hex(v.Color)}>■</color> <b>{v.AgvId}</b>", small);
                GUILayout.Label($"     <color={c}>med {med:F1} · p95 {p95:F1} · max {max:F1} ms</color>", small);
            }
            GUILayout.Label($"<size=11>unity_ops.usda write {ops.LastWriteDurationMs:F1} ms · rev {ops.Revision}</size>", small);

            Section("Recent actions (ms after the click)");
            foreach (LatencyMonitor.Chain ch in latency.RecentChains)
            {
                GUILayout.Label($"<b>{ch.Ref}</b>  {(ch.Agv != "" ? "→ " + ch.Agv : "")}", small);
                GUILayout.Label($"     seen {Ms(ch.SeenMs, ch.StartMs)} · replan/assign {Ms(ch.ReplanMs, ch.StartMs)} · moving {Ms(ch.MovingMs, ch.StartMs)}", small);
            }

            Section("Log file");
            GUILayout.Label($"<size=10>{latency.CsvPath}</size>", small);
            GUILayout.EndScrollView();
        }

        private static string Ms(double t, double t0) => t < 0 ? "–" : $"{t - t0:F0}";
    }
}
