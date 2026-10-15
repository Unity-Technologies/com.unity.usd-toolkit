using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    /// <summary>
    /// Ownership marker for the USD live transform sync (<see cref="UsdLiveSyncServer"/>). Attach to any
    /// GameObject under the sync root that an external client (Python / a DCC tool) is allowed to move.
    ///
    /// Every tracked GameObject is streamed OUT to clients regardless of this component — external tools can
    /// always observe the running scene (e.g. a Unity-simulated object). But an inbound <c>set_transform</c>
    /// is only applied to a prim whose GameObject carries a <c>UsdSyncNode</c> with
    /// <see cref="AcceptsRemoteWrites"/> = <c>true</c>. Without this rule an external edit to a
    /// Unity-authoritative object (a physics body, an animation, an object under scripted motion) would be
    /// overwritten by Unity on the very next frame and immediately re-broadcast, fighting the client.
    ///
    /// A GameObject with no <c>UsdSyncNode</c> (or one with <see cref="AcceptsRemoteWrites"/> = <c>false</c>)
    /// is therefore read-only from the client's perspective.
    ///
    /// When the server runs in <c>ExplicitNodesOnly</c> track mode, ONLY GameObjects carrying this component
    /// are tracked at all — so tagging just the objects you care about (e.g. a character's root transform,
    /// not its animated skeleton) keeps the sync channel focused. The per-channel toggles
    /// (<see cref="SyncPosition"/> / <see cref="SyncRotation"/> / <see cref="SyncScale"/>) further restrict
    /// which components are streamed and applied — e.g. position + rotation only, ignoring scale.
    /// </summary>
    public sealed class UsdSyncNode : MonoBehaviour
    {
        [Tooltip("If true, this object's transform may be driven by inbound set_transform commands from an " +
                 "external client. If false (default), the object is observe-only: Unity remains authoritative " +
                 "and remote edits to it are ignored.")]
        [SerializeField] private bool acceptsRemoteWrites;

        [Header("Tracked channels")]
        [Tooltip("Include local position in the sync (both streamed out and applied on inbound writes).")]
        [SerializeField] private bool syncPosition = true;
        [Tooltip("Include local rotation in the sync.")]
        [SerializeField] private bool syncRotation = true;
        [Tooltip("Include local scale in the sync. Turn off to track only position/rotation (e.g. a character " +
                 "whose scale never changes).")]
        [SerializeField] private bool syncScale = true;

        /// <summary>Whether inbound remote transform writes are applied to this GameObject.</summary>
        public bool AcceptsRemoteWrites
        {
            get => acceptsRemoteWrites;
            set => acceptsRemoteWrites = value;
        }

        /// <summary>Whether local position is part of the sync for this node.</summary>
        public bool SyncPosition { get => syncPosition; set => syncPosition = value; }

        /// <summary>Whether local rotation is part of the sync for this node.</summary>
        public bool SyncRotation { get => syncRotation; set => syncRotation = value; }

        /// <summary>Whether local scale is part of the sync for this node.</summary>
        public bool SyncScale { get => syncScale; set => syncScale = value; }
    }
}
