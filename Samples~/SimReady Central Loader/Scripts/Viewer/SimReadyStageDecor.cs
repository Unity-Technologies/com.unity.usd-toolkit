using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// The viewer's floor: a ground grid under the asset and a soft contact shadow where it stands,
    /// drawn with GL after the camera renders, so it works in URP and the Built-in pipeline alike
    /// and ships no material or texture.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class SimReadyStageDecor : MonoBehaviour
    {
        public Bounds AssetBounds = new Bounds(Vector3.zero, Vector3.one * 0.2f);
        public bool ShowGrid = true;
        public bool ShowContactShadow = true;
        public int GridLines = 20;

        private Material material;
        private Camera view;

        public void Fit(Bounds bounds) => AssetBounds = bounds;

        /// <summary>The square the grid covers on the floor, for framing an empty stage.</summary>
        public Bounds GridBounds
        {
            get
            {
                Layout(out Vector3 center, out float _, out float half);
                return new Bounds(center, new Vector3(2f * half, 0.001f, 2f * half));
            }
        }

        // Where the grid goes: cells of 1, 2 or 5 x 10^n about the asset's size, GridLines across.
        private void Layout(out Vector3 center, out float cell, out float half)
        {
            float floor = AssetBounds.min.y;
            float extent = Mathf.Max(AssetBounds.extents.x, AssetBounds.extents.z);
            cell = NiceCell(extent);
            half = cell * GridLines * 0.5f;
            center = new Vector3(Mathf.Round(AssetBounds.center.x / cell) * cell, floor, Mathf.Round(AssetBounds.center.z / cell) * cell);
        }

        private void OnEnable()
        {
            view = GetComponent<Camera>();
            RenderPipelineManager.endCameraRendering += OnEndCamera;
            Camera.onPostRender += OnPostRenderCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            Camera.onPostRender -= OnPostRenderCamera;
        }

        private void OnDestroy()
        {
            if (material != null)
            {
                Destroy(material);
            }
        }

        private void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == view)
            {
                Draw(camera);
            }
        }

        private void OnPostRenderCamera(Camera camera)
        {
            if (camera == view && GraphicsSettings.currentRenderPipeline == null)
            {
                Draw(camera);
            }
        }

        private void Draw(Camera camera)
        {
            if (!EnsureMaterial())
            {
                return;
            }

            Layout(out Vector3 center, out float cell, out float half);
            float floor = center.y;
            float extent = Mathf.Max(AssetBounds.extents.x, AssetBounds.extents.z);

            // A camera rendering into a texture: draw into that texture (URP leaves another target
            // active when it calls back).
            RenderTexture previous = RenderTexture.active;
            if (camera.targetTexture != null)
            {
                RenderTexture.active = camera.targetTexture;
            }

            GL.PushMatrix();
            GL.LoadProjectionMatrix(camera.projectionMatrix);
            GL.modelview = camera.worldToCameraMatrix;
            material.SetPass(0);

            if (ShowContactShadow)
            {
                // A fan whose alpha falls from the centre to the rim, the asset's footprint.
                const int segments = 48;
                float rx = Mathf.Max(AssetBounds.extents.x * 1.25f, 1e-3f);
                float rz = Mathf.Max(AssetBounds.extents.z * 1.25f, 1e-3f);
                Vector3 c = new Vector3(AssetBounds.center.x, floor + (extent * 0.001f), AssetBounds.center.z);
                GL.Begin(GL.TRIANGLES);
                for (int i = 0; i < segments; i++)
                {
                    float a0 = i * Mathf.PI * 2f / segments;
                    float a1 = (i + 1) * Mathf.PI * 2f / segments;
                    GL.Color(new Color(0f, 0f, 0f, 0.45f));
                    GL.Vertex(c);
                    GL.Color(new Color(0f, 0f, 0f, 0f));
                    GL.Vertex(c + new Vector3(Mathf.Cos(a0) * rx, 0f, Mathf.Sin(a0) * rz));
                    GL.Vertex(c + new Vector3(Mathf.Cos(a1) * rx, 0f, Mathf.Sin(a1) * rz));
                }

                GL.End();
            }

            if (ShowGrid)
            {
                GL.Begin(GL.LINES);
                for (int i = 0; i <= GridLines; i++)
                {
                    float t = -half + (i * cell);
                    // Lines fade towards the edge so the grid has no hard border.
                    float fade = 1f - Mathf.Abs(t) / half;
                    Color color = SimReadyStyle.GridColor;
                    color.a *= fade;
                    GL.Color(color);
                    GL.Vertex(center + new Vector3(t, 0f, -half));
                    GL.Vertex(center + new Vector3(t, 0f, half));
                    GL.Vertex(center + new Vector3(-half, 0f, t));
                    GL.Vertex(center + new Vector3(half, 0f, t));
                }

                GL.End();
            }

            GL.PopMatrix();
            RenderTexture.active = previous;
        }

        // 1, 2 or 5 times a power of ten, about a fifth of the asset's size.
        private static float NiceCell(float extent)
        {
            float raw = Mathf.Max(extent * 0.4f, 1e-4f);
            float power = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float scaled = raw / power;
            return power * (scaled < 2f ? 1f : scaled < 5f ? 2f : 5f);
        }

        private bool EnsureMaterial()
        {
            if (material != null)
            {
                return true;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return false;
            }

            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return true;
        }
    }
}
