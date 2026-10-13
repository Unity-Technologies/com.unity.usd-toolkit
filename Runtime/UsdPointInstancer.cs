using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Draws one prototype mesh of a <c>UsdGeomPointInstancer</c> at every place the stage
    /// scatters it.
    /// </summary>
    /// <remarks>
    /// A point instancer is "one prototype, many placements", and USD stores it that way: a
    /// prototype list plus position, orientation and scale arrays. Turning that back into a
    /// GameObject per instance does not scale -- Moore Lane scatters 229,717 of them and the
    /// editor cannot hold that many objects -- so the placements stay an array and are handed to
    /// instanced drawing, which is the shape the data already had.
    ///
    /// Handing the whole array over in one piece, however, draws all of it all the time.
    /// <c>RenderParams.worldBounds</c> is the only thing that decides whether a call is drawn, and
    /// the manual is explicit that it culls the instances of a call "as a single entity" and does
    /// not then cull them individually. One volume around the whole scattered set therefore means
    /// no culling at all: Moore Lane submitted 8.6 billion triangles a frame and ran at 1 FPS on a
    /// card already at its geometry limit. So the placements are sorted into a grid of cells here,
    /// each cell declaring a volume that actually encloses it, and the engine drops the cells that
    /// are off screen. Nothing about the data changes -- only how much of it is claimed to be in
    /// view at once.
    ///
    /// What this gives up is per-instance identity: an instance cannot be clicked in the scene
    /// view and carries no collider. For scattered gravel and grass that is no loss; for an
    /// assembly whose parts are selected individually it is, and such a stage is better served by
    /// prims than by an instancer.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("USD/USD Point Instancer")]
    public sealed class UsdPointInstancer : MonoBehaviour
    {
        // Graphics.RenderMeshInstanced takes at most this many per call. Unity splits the call
        // further when a material needs the inverse matrix too, which is its business, not ours.
        private const int BatchSize = 1023;

        // Roughly how many cells to sort the placements into. Cells trade culling accuracy
        // against the number of calls per frame: too few and whole districts are drawn for one
        // visible tree, too many and the per-call cost outweighs what is culled. A few hundred
        // suits scattered vegetation across a landscape, which is what instancers are mostly used
        // for.
        private const int TargetCells = 256;

        [SerializeField] private Mesh mesh;
        [SerializeField] private Material[] materials;

        /// <summary>
        /// Distance at which the prototype's least detailed level is used. Zero to always draw at
        /// full detail.
        /// </summary>
        /// <remarks>
        /// An instance here is drawn at full detail however far away it is, and on a scattered
        /// stage that is where the frame goes: Moore Lane's prototypes average 16,706 triangles
        /// and 297,518 of them come to 8.6 billion a frame. Unity 6.2's Mesh LOD keeps the levels
        /// inside the mesh's own index buffer, and an immediate-mode draw selects one through
        /// <see cref="RenderParams.forceMeshLod"/> -- the manual names RenderMeshInstanced as an
        /// API that must say which level it wants, and left alone it always says level zero.
        ///
        /// The cells this component already sorts its placements into are the unit of selection:
        /// a cell is a group of neighbours, so it takes the level its distance earns. Meshes
        /// without levels are unaffected, which is most of them -- the toolkit cannot generate
        /// levels during a runtime import, because the utility that generates them is part of the
        /// editor.
        /// </remarks>
        [SerializeField] private float lodDistance;

        /// Instance placements, relative to this transform.
        [SerializeField] private Matrix4x4[] instances;

        // Placements reordered so each cell is contiguous, with the cell boundaries and volumes
        // beside it. Rebuilt when the placements change, and the world-space copy also when this
        // transform moves.
        private Matrix4x4[] world;
        private int[] cellStarts;
        private Bounds[] cellBounds;
        private Matrix4x4 builtFrom;
        private Matrix4x4[] builtFor;

        /// The prototype mesh being scattered.
        public Mesh Mesh
        {
            get => mesh;
            set
            {
                mesh = value;
                Invalidate();
            }
        }

        /// One material per submesh of <see cref="Mesh"/>.
        public Material[] Materials
        {
            get => materials;
            set => materials = value;
        }

        /// Where the stage puts each instance, in this transform's space.
        public Matrix4x4[] Instances
        {
            get => instances;
            set
            {
                instances = value;
                Invalidate();
            }
        }

        /// How many placements this component draws.
        public int InstanceCount => instances?.Length ?? 0;

        /// <summary>
        /// Distance at which the least detailed level of <see cref="Mesh"/> is used; zero to
        /// always draw at full detail. Has no effect on a mesh without Mesh LOD levels.
        /// </summary>
        public float LodDistance
        {
            get => lodDistance;
            set => lodDistance = Mathf.Max(0f, value);
        }

        /// How many cells the placements are sorted into; 0 before the first frame.
        public int CellCount => cellStarts != null ? cellStarts.Length - 1 : 0;

        private void OnDisable()
        {
            Invalidate();
        }

        private void Invalidate()
        {
            world = null;
            cellStarts = null;
            cellBounds = null;
            builtFor = null;
        }

        private void Update()
        {
            Draw();
        }

        /// <summary>
        /// Submits one frame's worth of instanced draws.
        /// </summary>
        /// <remarks>
        /// Called every frame by this component, and public so that a benchmark or a custom render
        /// loop can drive it directly. Measuring what the component actually does, rather than a
        /// copy of it written beside the measurement, is the difference between a benchmark that
        /// tracks the code and one that quietly stops matching it.
        /// </remarks>
        public void Draw()
        {
            if (mesh == null || instances == null || instances.Length == 0 ||
                materials == null || materials.Length == 0)
            {
                return;
            }

            // The matrices are stored relative to this transform, the way a child GameObject
            // would be, so that moving the instancer moves its instances; instanced drawing takes
            // world space, so the transform is applied here. The result is kept until something
            // it depends on changes -- on a stage with a quarter of a million placements, redoing
            // it every frame is a cost with nothing to show for it.
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            if (world == null || builtFor != instances || builtFrom != toWorld)
            {
                Build(toWorld);
            }

            // Mesh LOD selection needs somewhere to measure distance from. There is no single
            // camera at this point in the frame, so the main one stands in: a scene with several
            // cameras gets the level the player's view earns, which is wrong for the others but
            // only in how much detail they receive, never in what is drawn.
            int levels = Mathf.Max(mesh.lodCount, 1);
            Camera viewer = levels > 1 && lodDistance > 0f ? Camera.main : null;
            Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;

            for (int cell = 0; cell + 1 < cellStarts.Length; cell++)
            {
                int start = cellStarts[cell];
                int count = cellStarts[cell + 1] - start;
                Bounds bounds = cellBounds[cell];

                int lod = 0;
                if (viewer != null)
                {
                    float distance = Vector3.Distance(eye, bounds.ClosestPoint(eye));
                    lod = Mathf.Clamp(Mathf.FloorToInt(levels * distance / lodDistance), 0, levels - 1);
                }

                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = materials[Mathf.Min(submesh, materials.Length - 1)];
                    if (material == null)
                    {
                        continue;
                    }

                    // RenderMeshInstanced refuses a material that does not declare instancing, and
                    // the materials here come from the stage's own shading, which has no reason to
                    // have set it. Turn it on rather than dropping the draw; the shaders Unity
                    // ships all handle the instancing path, and a material that cannot is still
                    // better off failing visibly than silently drawing nothing.
                    if (!material.enableInstancing)
                    {
                        material.enableInstancing = true;
                    }

                    var parameters = new RenderParams(material)
                    {
                        worldBounds = bounds,
                        receiveShadows = true,
                        shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On,
                        forceMeshLod = lod,
                    };

                    for (int offset = 0; offset < count; offset += BatchSize)
                    {
                        int take = Mathf.Min(BatchSize, count - offset);
                        // The array overload takes a range, so the placements are read where they
                        // already are rather than copied into a list every frame.
                        Graphics.RenderMeshInstanced(parameters, mesh, submesh, world, take, start + offset);
                    }
                }
            }
        }

        /// <summary>
        /// Sorts the placements into a grid of cells and records each cell's volume.
        /// </summary>
        private void Build(Matrix4x4 toWorld)
        {
            int count = instances.Length;
            var positions = new Vector3[count];
            Bounds whole = default;
            Vector3 meshCentre = mesh.bounds.center;
            Vector3 meshExtents = mesh.bounds.extents;

            var worldMatrices = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 placement = toWorld * instances[i];
                worldMatrices[i] = placement;
                positions[i] = placement.GetColumn(3);
                Bounds instanceBounds = InstanceBounds(placement, meshCentre, meshExtents);
                if (i == 0)
                {
                    whole = instanceBounds;
                }
                else
                {
                    whole.Encapsulate(instanceBounds);
                }
            }

            // Cell size chosen from the extent the placements actually cover, so the same figure
            // works for gravel spread over a few metres and for a forest over kilometres.
            Vector3 size = whole.size;
            float cell = Mathf.Max(Mathf.Sqrt(Mathf.Max(size.x * size.z, 1e-6f) / TargetCells), 1e-4f);

            var members = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                long cx = (long)Mathf.Floor((positions[i].x - whole.min.x) / cell);
                long cz = (long)Mathf.Floor((positions[i].z - whole.min.z) / cell);
                long key = (cx << 32) ^ (cz & 0xffffffffL);
                if (!members.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    members[key] = list;
                }

                list.Add(i);
            }

            world = new Matrix4x4[count];
            cellStarts = new int[members.Count + 1];
            cellBounds = new Bounds[members.Count];

            int cursor = 0;
            int index = 0;
            foreach (List<int> list in members.Values)
            {
                cellStarts[index] = cursor;
                Bounds bounds = default;
                for (int m = 0; m < list.Count; m++)
                {
                    Matrix4x4 placement = worldMatrices[list[m]];
                    world[cursor + m] = placement;

                    // The instance's own volume, not just its origin: a tree whose pivot sits at
                    // its root still occupies the space above it, and a cell bounding only the
                    // pivots would cull trees that are plainly in frame.
                    Bounds instanceBounds = InstanceBounds(placement, meshCentre, meshExtents);
                    if (m == 0)
                    {
                        bounds = instanceBounds;
                    }
                    else
                    {
                        bounds.Encapsulate(instanceBounds);
                    }
                }

                cellBounds[index] = bounds;
                cursor += list.Count;
                index++;
            }

            cellStarts[index] = cursor;
            builtFrom = toWorld;
            builtFor = instances;
        }

        // The mesh's bounds carried through a placement. The extents are transformed by the
        // absolute value of the rotation and scale, which is the smallest axis-aligned box that
        // still contains the rotated one.
        private static Bounds InstanceBounds(Matrix4x4 placement, Vector3 centre, Vector3 extents)
        {
            var transformed = new Vector3(
                Mathf.Abs(placement.m00) * extents.x + Mathf.Abs(placement.m01) * extents.y + Mathf.Abs(placement.m02) * extents.z,
                Mathf.Abs(placement.m10) * extents.x + Mathf.Abs(placement.m11) * extents.y + Mathf.Abs(placement.m12) * extents.z,
                Mathf.Abs(placement.m20) * extents.x + Mathf.Abs(placement.m21) * extents.y + Mathf.Abs(placement.m22) * extents.z);
            return new Bounds(placement.MultiplyPoint3x4(centre), transformed * 2f);
        }
    }
}
