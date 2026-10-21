using System;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>What an import does when a stage is larger than its <see cref="UsdImportLimits"/>.</summary>
    public enum UsdImportLimitBehavior
    {
        /// <summary>The import fails with an error naming the limit. The default.</summary>
        Fail,

        /// <summary>
        /// What does not fit is left out, with a warning, and the rest is imported: the walk stops
        /// at the node limit, a mesh over the mesh or geometry limit is skipped (with its instance
        /// copies), and a point instancer's placements over theirs are not drawn.
        /// </summary>
        SkipWithWarning,
    }

    /// <summary>
    /// The budgets an import is held to (<see cref="UsdImportOptions.Limits"/>). A stage is untrusted
    /// input, and a few hundred bytes of it can describe unbounded work -- point instancers whose
    /// prototypes hold further instancers multiply their subtrees, and one instance count times one
    /// prototype mesh count is a matrix per pair -- so the import counts what it builds against
    /// these. The defaults are several times what the largest scenes measured need; raise them for
    /// a trusted stage that is larger, lower them to keep a device's imports small. A value of 0 or
    /// less keeps the default. The animation limits always drop only the one track, skeleton
    /// animation or blend shape that is over, with a warning. Needs the 0.8.1 native plugin unless every value
    /// is the default.
    /// </summary>
    [Serializable]
    public sealed class UsdImportLimits
    {
        public const long DefaultMaxNodes = 2000000;
        public const long DefaultMaxMeshes = 1000000;
        public const long DefaultMaxInstancePlacements = 4000000;
        public const long DefaultMaxGeometryElements = 256L * 1024 * 1024;
        public const long DefaultMaxTrackSamples = 1000000;
        public const long DefaultMaxAnimationValues = 64L * 1024 * 1024;
        public const int DefaultMaxJoints = 65536;

        [Tooltip("Prims imported as GameObjects, point-instancer prototype copies included.")]
        public long MaxNodes = DefaultMaxNodes;

        [Tooltip("Meshes, instance copies included.")]
        public long MaxMeshes = DefaultMaxMeshes;

        [Tooltip("Point-instancer placements: instances times prototype meshes.")]
        public long MaxInstancePlacements = DefaultMaxInstancePlacements;

        [Tooltip("Points and triangle indices of every mesh together; also the most one mesh may have.")]
        public long MaxGeometryElements = DefaultMaxGeometryElements;

        [Tooltip("Time samples on one animation track.")]
        public long MaxTrackSamples = DefaultMaxTrackSamples;

        [Tooltip("Animation values (floats) of every track, skeleton animation and blend shape together.")]
        public long MaxAnimationValues = DefaultMaxAnimationValues;

        [Tooltip("Joints in one skeleton.")]
        public int MaxJoints = DefaultMaxJoints;

        [Tooltip("Fail the import, or leave out what does not fit and import the rest.")]
        public UsdImportLimitBehavior WhenExceeded = UsdImportLimitBehavior.Fail;

        /// <summary>A copy of these limits.</summary>
        public UsdImportLimits Clone() => (UsdImportLimits)MemberwiseClone();

        // Every value the default (a non-positive one keeps it): the import needs nothing the
        // plain native entry points do not already do.
        internal bool IsDefault =>
            WhenExceeded == UsdImportLimitBehavior.Fail &&
            Effective(MaxNodes, DefaultMaxNodes) == DefaultMaxNodes &&
            Effective(MaxMeshes, DefaultMaxMeshes) == DefaultMaxMeshes &&
            Effective(MaxInstancePlacements, DefaultMaxInstancePlacements) == DefaultMaxInstancePlacements &&
            Effective(MaxGeometryElements, DefaultMaxGeometryElements) == DefaultMaxGeometryElements &&
            Effective(MaxTrackSamples, DefaultMaxTrackSamples) == DefaultMaxTrackSamples &&
            Effective(MaxAnimationValues, DefaultMaxAnimationValues) == DefaultMaxAnimationValues &&
            Effective(MaxJoints, DefaultMaxJoints) == DefaultMaxJoints;

        // Equal for two limits that import alike; a stage opened under one is reused under the other.
        internal string Key =>
            $"{(int)WhenExceeded}:{Effective(MaxNodes, DefaultMaxNodes)}:{Effective(MaxMeshes, DefaultMaxMeshes)}:" +
            $"{Effective(MaxInstancePlacements, DefaultMaxInstancePlacements)}:{Effective(MaxGeometryElements, DefaultMaxGeometryElements)}:" +
            $"{Effective(MaxTrackSamples, DefaultMaxTrackSamples)}:{Effective(MaxAnimationValues, DefaultMaxAnimationValues)}:" +
            $"{Effective(MaxJoints, DefaultMaxJoints)}";

        internal static string KeyOf(UsdImportLimits limits) => (limits ?? new UsdImportLimits()).Key;

        private static long Effective(long value, long fallback) => value > 0 ? value : fallback;
    }
}
