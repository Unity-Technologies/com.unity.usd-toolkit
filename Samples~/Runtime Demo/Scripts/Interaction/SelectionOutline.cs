using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Draws an inverted-hull outline around the selected asset.
    /// </summary>
    /// <remarks>
    /// The hull renderers live under their own root, deliberately outside the USD scene root. If
    /// they sat under the imported asset, the flattening exporter would walk straight into them
    /// and bake a slightly inflated ghost copy of every selected part into the customer's file.
    /// </remarks>
    public sealed class SelectionOutline : MonoBehaviour
    {
        private readonly List<GameObject> pool = new List<GameObject>();
        private readonly List<Transform> activeSources = new List<Transform>();
        private readonly Dictionary<Mesh, Mesh> weldedMeshes = new Dictionary<Mesh, Mesh>();

        private Material outlineMaterial;
        private Material maskMaterial;
        private int activeCount;

        public void Initialise(Material outline, Material mask)
        {
            outlineMaterial = outline;
            maskMaterial = mask;
        }

        public void Show(UsdSceneEntry entry)
        {
            Clear();

            if (entry == null || outlineMaterial == null)
            {
                return;
            }

            MeshFilter[] filters = entry.Root.GetComponentsInChildren<MeshFilter>(false);
            foreach (MeshFilter filter in filters)
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                var renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                GameObject shell = Rent();
                Mesh welded = GetWelded(filter.sharedMesh);
                // The mask uses the original mesh (exact silhouette); only the rim is expanded.
                shell.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                shell.transform.GetChild(0).GetComponent<MeshFilter>().sharedMesh = welded;
                shell.SetActive(true);
                activeSources.Add(filter.transform);
                activeCount++;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < activeCount && i < pool.Count; i++)
            {
                pool[i].SetActive(false);
            }

            activeCount = 0;
            activeSources.Clear();
        }

        private GameObject Rent()
        {
            if (activeCount < pool.Count)
            {
                return pool[activeCount];
            }

            // Two renderers per source mesh. They are separate objects, not two passes on one,
            // because Unity orders draws by material render queue across renderers: that is what
            // guarantees EVERY mask is written before ANY rim is drawn.
            var shell = new GameObject("OutlineShell");
            shell.transform.SetParent(transform, false);
            shell.AddComponent<MeshFilter>();
            ConfigureRenderer(shell.AddComponent<MeshRenderer>(), maskMaterial);

            var rim = new GameObject("Rim");
            rim.transform.SetParent(shell.transform, false);
            rim.AddComponent<MeshFilter>();
            ConfigureRenderer(rim.AddComponent<MeshRenderer>(), outlineMaterial);

            shell.SetActive(false);
            pool.Add(shell);
            return shell;
        }

        private static void ConfigureRenderer(Renderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void LateUpdate()
        {
            // Runs after the gizmo has moved the asset, so the outline never trails a drag.
            for (int i = 0; i < activeCount && i < activeSources.Count; i++)
            {
                Transform source = activeSources[i];
                if (source == null)
                {
                    continue;
                }

                Transform shell = pool[i].transform;
                shell.SetPositionAndRotation(source.position, source.rotation);
                shell.localScale = source.lossyScale;
            }
        }

        /// <summary>
        /// Returns a copy of <paramref name="source"/> whose normals are averaged across
        /// coincident vertices.
        /// </summary>
        /// <remarks>
        /// CAD geometry is overwhelmingly hard edged: a cube arrives as 24 vertices with 6
        /// distinct face normals. Extruding those along their own normals blows the corners apart
        /// and the "outline" comes out as three disconnected slabs. Welding by position first
        /// gives every corner a single averaged direction to push along, which is what closes the
        /// silhouette.
        /// </remarks>
        private Mesh GetWelded(Mesh source)
        {
            if (weldedMeshes.TryGetValue(source, out Mesh cached) && cached != null)
            {
                return cached;
            }

            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;

            if (normals == null || normals.Length != vertices.Length)
            {
                source.RecalculateNormals();
                normals = source.normals;
            }

            var accumulated = new Dictionary<Vector3Int, Vector3>(vertices.Length);
            var keys = new Vector3Int[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                // Quantise to ~0.1 mm so vertices that are "the same corner" actually collide.
                Vector3Int key = new Vector3Int(
                    Mathf.RoundToInt(vertices[i].x * 10000.0f),
                    Mathf.RoundToInt(vertices[i].y * 10000.0f),
                    Mathf.RoundToInt(vertices[i].z * 10000.0f));
                keys[i] = key;
                accumulated[key] = accumulated.TryGetValue(key, out Vector3 sum) ? sum + normals[i] : normals[i];
            }

            var welded = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 sum = accumulated[keys[i]];
                welded[i] = sum.sqrMagnitude > 1e-8f ? sum.normalized : normals[i];
            }

            var copy = new Mesh
            {
                name = source.name + "_OutlineHull",
                hideFlags = HideFlags.DontSave,
                indexFormat = source.indexFormat
            };
            copy.vertices = vertices;
            copy.normals = welded;
            copy.subMeshCount = 1;
            copy.SetTriangles(source.triangles, 0);
            copy.RecalculateBounds();

            weldedMeshes[source] = copy;
            return copy;
        }

        private void OnDestroy()
        {
            foreach (Mesh mesh in weldedMeshes.Values)
            {
                if (mesh != null)
                {
                    Destroy(mesh);
                }
            }

            weldedMeshes.Clear();
        }
    }
}
