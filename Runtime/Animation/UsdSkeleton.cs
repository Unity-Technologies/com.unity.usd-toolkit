using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A UsdSkelSkeleton as imported: the joint objects the importer built below the Skeleton
    /// prim's object, in the skeleton's joint order, and the joint paths that name them
    /// ("Shoulder/Elbow"). Every <see cref="SkinnedMeshRenderer"/> bound to this skeleton uses
    /// <see cref="Joints"/> as its bones, in the same order. Joints are not prims: the root joints
    /// carry a <see cref="UsdImportHelperObject"/>, so exporters and <see cref="UsdPrimSource"/>
    /// skip the whole joint hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdSkeleton : MonoBehaviour
    {
        [SerializeField] private string[] jointPaths = Array.Empty<string>();
        [SerializeField] private Transform[] joints = Array.Empty<Transform>();
        [SerializeField] private Matrix4x4[] bindTransforms = Array.Empty<Matrix4x4>();

        /// <summary>The skeleton's joint paths, in its joint order.</summary>
        public IReadOnlyList<string> JointPaths => jointPaths;

        /// <summary>The joint objects, in the skeleton's joint order.</summary>
        public IReadOnlyList<Transform> Joints => joints;

        /// <summary>
        /// Each joint's bind transform in the skeleton's space (UsdSkel bindTransforms, in Unity's
        /// basis), in joint order. Empty for a skeleton imported before this was recorded.
        /// </summary>
        public IReadOnlyList<Matrix4x4> BindTransforms => bindTransforms;

        internal Transform[] JointArray => joints;

        internal Matrix4x4[] BindTransformArray => bindTransforms;

        internal void Initialize(string[] paths, Transform[] transforms, Matrix4x4[] binds = null)
        {
            jointPaths = paths ?? Array.Empty<string>();
            joints = transforms ?? Array.Empty<Transform>();
            bindTransforms = binds ?? Array.Empty<Matrix4x4>();
        }
    }
}
