using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Unity.USDToolkit.Timeline
{
    /// <summary>
    /// A clip on a <see cref="UsdAnimationTrack"/>. Its local time, in seconds from the start of the
    /// stage's animation, is what the bound <see cref="UsdAnimationPlayer"/> is evaluated at, so
    /// clip-in, speed and looping work as for any Timeline clip.
    /// </summary>
    public sealed class UsdAnimationClip : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("Optional. A new clip takes its length from this animation.")]
        [SerializeField] private UsdAnimationData animation;

        /// <summary>The animation the clip's default length comes from; the binding is what plays.</summary>
        public UsdAnimationData Animation
        {
            get => animation;
            set => animation = value;
        }

        public ClipCaps clipCaps => ClipCaps.ClipIn | ClipCaps.Looping | ClipCaps.Extrapolation | ClipCaps.SpeedMultiplier;

        public override double duration =>
            animation != null && animation.Duration > 0.0 ? animation.Duration : base.duration;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner) =>
            ScriptPlayable<UsdAnimationBehaviour>.Create(graph);
    }
}
