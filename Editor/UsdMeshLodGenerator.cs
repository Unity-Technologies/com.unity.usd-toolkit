using UnityEditor;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Generates Mesh LOD levels for the prototypes an imported point instancer scatters.
    /// </summary>
    /// <remarks>
    /// A point instancer draws one prototype in very many places, and without LOD levels it draws
    /// every one of them at full detail however far off it is. On Moore Lane that is 16,706
    /// triangles each across 297,518 placements -- 8.6 billion a frame, and 6.2 seconds to draw
    /// one. Giving the prototypes four levels and letting
    /// <see cref="UsdPointInstancer.LodDistance"/> choose between them took the same frame to 1.18
    /// seconds, a little over five times faster, measured on an RTX A4000.
    ///
    /// This lives in the editor assembly because the generator does:
    /// <c>MeshLodUtility.GenerateMeshLods</c> is part of UnityEditor, so a runtime import has no
    /// way to call it. Selecting a level at runtime is fine -- <c>Mesh.lodCount</c> and
    /// <c>RenderParams.forceMeshLod</c> are both in the engine -- so a stage whose prototypes
    /// arrive with levels already on them benefits wherever it is loaded. What cannot happen at
    /// runtime is creating the levels in the first place.
    ///
    /// The quality is worth looking at before committing to it. Decimation removes geometry, and
    /// on vegetation the geometry it removes is leaves: a canopy thins out and more sky shows
    /// through it. That was acceptable in the comparison above and will not be on every stage,
    /// which is why this is something to invoke rather than something that happens.
    /// </remarks>
    public static class UsdMeshLodGenerator
    {
        private const int DefaultLevels = 4;

        [MenuItem("GameObject/USD/Generate Mesh LODs for Point Instancers", false, 20)]
        private static void GenerateForSelection()
        {
            GameObject target = Selection.activeGameObject;
            if (target == null)
            {
                Debug.LogWarning("USD: select an imported USD root first.");
                return;
            }

            int changed = Generate(target, DefaultLevels);
            Debug.Log($"USD: generated Mesh LODs for {changed} point instancer prototype(s) under '{target.name}'.");
        }

        [MenuItem("GameObject/USD/Generate Mesh LODs for Point Instancers", true)]
        private static bool CanGenerateForSelection()
        {
            return Selection.activeGameObject != null &&
                   Selection.activeGameObject.GetComponentInChildren<UsdPointInstancer>(true) != null;
        }

        /// <summary>
        /// Gives every prototype under <paramref name="root"/> up to <paramref name="levels"/>
        /// extra detail levels, and sets each instancer's LOD distance from the extent its own
        /// placements cover.
        /// </summary>
        /// <returns>How many prototypes gained levels.</returns>
        public static int Generate(GameObject root, int levels = DefaultLevels)
        {
            if (root == null)
            {
                return 0;
            }

            int changed = 0;
            foreach (UsdPointInstancer instancer in root.GetComponentsInChildren<UsdPointInstancer>(true))
            {
                Mesh mesh = instancer.Mesh;
                if (mesh == null || mesh.lodCount > 1)
                {
                    continue;
                }

                MeshLodUtility.GenerateMeshLods(mesh, levels);
                if (mesh.lodCount > 1)
                {
                    changed++;
                }

                // The distance at which the least detailed level is reached, taken from how far
                // the placements are spread rather than from a fixed number of metres: the same
                // figure then means the same thing on gravel scattered over a yard and on a
                // forest over kilometres.
                instancer.LodDistance = ScatterExtent(instancer);
            }

            return changed;
        }

        private static float ScatterExtent(UsdPointInstancer instancer)
        {
            Matrix4x4[] placements = instancer.Instances;
            if (placements == null || placements.Length == 0)
            {
                return 0f;
            }

            var bounds = new Bounds(placements[0].GetColumn(3), Vector3.zero);
            foreach (Matrix4x4 placement in placements)
            {
                bounds.Encapsulate((Vector3)placement.GetColumn(3));
            }

            return instancer.transform.TransformVector(bounds.size).magnitude;
        }
    }
}
