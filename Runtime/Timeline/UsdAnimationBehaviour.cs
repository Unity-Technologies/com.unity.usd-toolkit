using UnityEngine.Playables;

namespace Unity.USDToolkit.Timeline
{
    /// <summary>
    /// Poses the bound <see cref="UsdAnimationPlayer"/> at the clip's local time each frame. While a
    /// clip is active the timeline owns the time, so the player's own playback is paused.
    /// </summary>
    public sealed class UsdAnimationBehaviour : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var player = playerData as UsdAnimationPlayer;
            if (player == null || player.Data == null)
            {
                return;
            }

            if (player.IsPlaying)
            {
                player.Pause();
            }

            player.Evaluate(playable.GetTime());
        }
    }
}
