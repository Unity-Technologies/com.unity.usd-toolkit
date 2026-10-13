using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Sits on every object an import creates: the prim it stands for and the state it arrived
    /// in. A <see cref="UsdCompositionMode.Preserve"/> export compares the object's current
    /// state against this snapshot and writes only the differences, as overrides on the prim.
    ///
    /// The prim path, not the object's name or position, identifies the prim, so renaming an
    /// object changes nothing in the export. Moving it under another parent does: USD cannot
    /// move a prim from an override layer, so the export deactivates the original and writes
    /// the object as new content where it now is.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPrimSource : MonoBehaviour
    {
        [SerializeField] private UsdStageSource stage;
        [SerializeField] private string primPath;
        [SerializeField] private string instanceRootPath;
        [SerializeField] private int flags;
        [SerializeField] private bool hasTransform;
        [SerializeField] private Vector3 localPosition;
        [SerializeField] private Quaternion localRotation = Quaternion.identity;
        [SerializeField] private Vector3 localScale = Vector3.one;
        [SerializeField] private bool activeSelf = true;
        [SerializeField] private Mesh mesh;
        [SerializeField] private Material[] materials;
        [SerializeField] private bool rendererEnabled = true;

        /// <summary>The import this object belongs to.</summary>
        public UsdStageSource Stage => stage;

        /// <summary>The prim this object stands for, in the source stage's namespace.</summary>
        public string PrimPath => primPath;

        public UsdPrimCompositionFlags CompositionFlags => (UsdPrimCompositionFlags)flags;

        /// <summary>For a prim below an instance, the instance's prim path; otherwise empty.</summary>
        public string InstanceRootPath => instanceRootPath;

        /// <summary>False for prims that have no transform of their own (a Scope, say).</summary>
        public bool HasTransform => hasTransform;

        internal Vector3 ImportedLocalPosition => localPosition;
        internal Quaternion ImportedLocalRotation => localRotation;
        internal Vector3 ImportedLocalScale => localScale;
        internal bool ImportedActiveSelf => activeSelf;
        internal Mesh ImportedMesh => mesh;
        internal Material[] ImportedMaterials => materials;
        internal bool ImportedRendererEnabled => rendererEnabled;

        internal void Initialize(UsdStageSource owner, string path, int compositionFlags, string instanceRoot, bool xformable)
        {
            stage = owner;
            primPath = path;
            flags = compositionFlags;
            instanceRootPath = instanceRoot ?? string.Empty;
            hasTransform = xformable;
            CaptureState();
        }

        // Snapshot of what the import produced; the baseline the export diffs against.
        internal void CaptureState()
        {
            Transform t = transform;
            localPosition = t.localPosition;
            localRotation = t.localRotation;
            localScale = t.localScale;
            activeSelf = gameObject.activeSelf;

            MeshFilter filter = GetComponent<MeshFilter>();
            mesh = filter != null ? filter.sharedMesh : null;
            MeshRenderer renderer = GetComponent<MeshRenderer>();
            materials = renderer != null ? renderer.sharedMaterials : null;
            rendererEnabled = renderer == null || renderer.enabled;
        }
    }
}
