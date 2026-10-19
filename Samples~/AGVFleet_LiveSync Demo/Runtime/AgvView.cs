using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Unity view of one AGV (/World/AGVs/AGV_0x), driven by the fleet backend's state stream (Isaac Sim
    /// or the local fleet): pose, wheel spin, colour, path line. Read-only: nothing is written back.
    /// </summary>
    public sealed class AgvView : MonoBehaviour
    {
        public string AgvId;
        public Color Color;
        public AgvStateMsg Last;
        public double LastRxMs;
        public string Assignment = "";

        private Transform leftWheel, rightWheel;
        private Quaternion leftRest, rightRest;
        private Vector3 wheelAxis = Vector3.up;   // wheel joint axis in the wheel pivot's local frame
        private LineRenderer line;
        private float[] pathPts = new float[0];
        private int pathVersion = -1;
        private Vector3 targetPos, velocity;
        private Quaternion targetRot;
        private float targetTime;
        private GameObject selectionRing;

        /// <param name="importerBasis">true for a model imported from USD (the importer's (-X, Y, Z) basis,
        /// wheel joints about local +Y); false for <see cref="AgvModel"/> (Unity frame, axle along +Z).</param>
        public static AgvView Create(string id, Color color, GameObject modelTemplate, Transform parent, Material lineMat, bool importerBasis)
        {
            var root = new GameObject(id);
            root.transform.SetParent(parent, false);
            var view = root.AddComponent<AgvView>();
            view.AgvId = id;
            view.Color = color;

            if (modelTemplate != null)
            {
                GameObject model = Instantiate(modelTemplate, root.transform, false);
                model.name = "Model";
                model.SetActive(true);
                // an imported subtree is in the importer's (-X, Y, Z) basis; see UsdFrame
                model.transform.localRotation = importerBasis ? UsdFrame.ImportedSubtreeRotation : Quaternion.identity;
                model.transform.localPosition = Vector3.zero;
                Tint(model, color);
                view.leftWheel = FindDeep(model.transform, "left_wheel");
                view.rightWheel = FindDeep(model.transform, "right_wheel");
                if (view.leftWheel) view.leftRest = view.leftWheel.localRotation;
                if (view.rightWheel) view.rightRest = view.rightWheel.localRotation;
                // A positive joint angle rolls the AGV forward. Imported: about local +Y, with the
                // importer's X flip reversing the sense. Primitive: about Unity +Z (USD +Y), and the
                // Y/Z swap of UsdFrame reverses it the same way.
                view.wheelAxis = importerBasis ? Vector3.up : Vector3.forward;
            }

            // pick target: the footprint (USD robot frame)
            var box = root.AddComponent<BoxCollider>();
            box.center = UsdFrame.ToUnity(AgvSpec.CenterX, 0, 0.12);
            box.size = UsdFrame.ToUnity(AgvSpec.Length, AgvSpec.HalfWidth * 2f, AgvSpec.Height);

            view.selectionRing = MakeRing(root.transform, color);
            view.selectionRing.SetActive(false);

            var lineGo = new GameObject(id + "_Path");
            lineGo.transform.SetParent(parent, false);
            view.line = lineGo.AddComponent<LineRenderer>();
            view.line.sharedMaterial = lineMat;
            view.line.startColor = view.line.endColor = color;
            view.line.widthMultiplier = 0.09f;
            view.line.positionCount = 0;
            view.line.numCornerVertices = 2;
            view.line.alignment = LineAlignment.View;
            return view;
        }

        public bool Selected
        {
            set { if (selectionRing) selectionRing.SetActive(value); }
        }

        public void ApplyState(AgvStateMsg s, double rxMs)
        {
            Vector3 p = UsdFrame.ToUnity(s.p[0], s.p[1], s.p[2]);
            Quaternion r = UsdFrame.ToUnity(s.q[0], s.q[1], s.q[2], s.q[3]);
            float now = Time.unscaledTime;
            if (Last != null && now > targetTime)
            {
                velocity = (p - targetPos) / Mathf.Max(now - targetTime, 1e-3f);
            }
            targetPos = p;
            targetRot = r;
            targetTime = now;
            if (Last == null)
            {
                transform.SetPositionAndRotation(p, r);
            }
            Last = s;
            LastRxMs = rxMs;
            if (leftWheel && s.wh != null && s.wh.Length == 2)
            {
                leftWheel.localRotation = leftRest * Quaternion.AngleAxis(-s.wh[0] * Mathf.Rad2Deg, wheelAxis);
                rightWheel.localRotation = rightRest * Quaternion.AngleAxis(-s.wh[1] * Mathf.Rad2Deg, wheelAxis);
            }
        }

        public void SetPath(int version, float[] pts)
        {
            pathVersion = version;
            pathPts = pts ?? new float[0];
        }

        private void LateUpdate()
        {
            if (Last == null)
            {
                return;
            }
            // Draw at the latest received pose, extrapolated by at most one publish period: no added lag.
            float dt = Mathf.Min(Time.unscaledTime - targetTime, 0.034f);
            Vector3 v = velocity.sqrMagnitude < 4f ? velocity : Vector3.zero;
            transform.SetPositionAndRotation(targetPos + v * dt, targetRot);
            UpdateLine();
        }

        private void UpdateLine()
        {
            int n = pathPts.Length / 2;
            int start = Last != null ? Mathf.Clamp(Last.pi, 1, Mathf.Max(n, 1)) : 1;
            if (n < 2 || start >= n + 1)
            {
                line.positionCount = 0;
                return;
            }
            int count = 1 + (n - start);
            line.positionCount = count;
            Vector3 here = transform.position;
            line.SetPosition(0, new Vector3(here.x, 0.04f, here.z));
            for (int i = start, k = 1; i < n; i++, k++)
            {
                line.SetPosition(k, UsdFrame.ToUnity(pathPts[2 * i], pathPts[2 * i + 1], 0.04f));
            }
        }

        private void OnDestroy()
        {
            if (line) Destroy(line.gameObject);
        }

        // ---- helpers ----------------------------------------------------------------------------
        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                Transform r = FindDeep(c, name);
                if (r) return r;
            }
            return null;
        }

        private static readonly Dictionary<Color, Material> TintCache = new Dictionary<Color, Material>();

        /// <summary>Set by AgvFleetScene from a scene-referenced material: in a player build
        /// Shader.Find only sees shaders that some included material uses.</summary>
        public static Shader LitShader;

        public static Material LitMaterial(Color c, float smoothness = 0.4f)
        {
            if (TintCache.TryGetValue(c, out Material m) && m) return m;
            Shader sh = LitShader ? LitShader : Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(sh) { color = c };
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            TintCache[c] = m;
            return m;
        }

        private static void Tint(GameObject model, Color color)
        {
            Material body = LitMaterial(color, 0.5f);
            Material dark = LitMaterial(new Color(0.12f, 0.12f, 0.13f), 0.2f);
            Material top = LitMaterial(new Color(0.82f, 0.83f, 0.85f), 0.35f);
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.transform.parent ? r.transform.parent.name.ToLowerInvariant() : "";
                bool isDark = n.Contains("wheel") || n.Contains("caster") || n.Contains("swivel") || n == AgvModel.TrimGroup;
                Material m = isDark ? dark : n == AgvModel.TopGroup ? top : body;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = m;
                r.sharedMaterials = mats;
            }
        }

        private static GameObject MakeRing(Transform parent, Color color)
        {
            var go = new GameObject("SelectionRing");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.widthMultiplier = 0.06f;
            lr.sharedMaterial = LitMaterial(Color.white, 0f);
            lr.startColor = lr.endColor = Color.white;
            const int N = 48;
            lr.positionCount = N;
            Vector3 c = UsdFrame.ToUnity(AgvSpec.CenterX, 0, 0.02);
            for (int i = 0; i < N; i++)
            {
                float a = i * Mathf.PI * 2f / N;
                lr.SetPosition(i, c + new Vector3(Mathf.Cos(a) * 1.0f, 0f, Mathf.Sin(a) * 1.0f));
            }
            return go;
        }
    }
}
