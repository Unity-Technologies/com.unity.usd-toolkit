using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Collider pairs a stage keeps from colliding: an articulation's links with self-collisions
    /// off, physics:filteredPairs, and filtered collision groups. Unity filters per pair at run
    /// time (<see cref="Physics.IgnoreCollision(Collider, Collider, bool)"/>), which a scene does not
    /// store, so the pairs live here and are applied while this is enabled, in edit mode too, so
    /// a simulation stepped from an editor tool sees them.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPhysicsCollisionFilter : MonoBehaviour
    {
        /// <summary>Each pair is First[i] and Second[i].</summary>
        public List<Collider> First = new List<Collider>();
        public List<Collider> Second = new List<Collider>();

        public int PairCount => Mathf.Min(First.Count, Second.Count);

        private void OnEnable() => Apply(true);

        private void OnDisable() => Apply(false);

        // The importer fills the lists after AddComponent has already enabled this.
        internal void ApplyNow()
        {
            if (isActiveAndEnabled)
            {
                Apply(true);
            }
        }

        private void Apply(bool ignore)
        {
            for (int i = 0; i < PairCount; i++)
            {
                if (First[i] != null && Second[i] != null)
                {
                    Physics.IgnoreCollision(First[i], Second[i], ignore);
                }
            }
        }
    }
}
