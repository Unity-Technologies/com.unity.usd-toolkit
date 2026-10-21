using System;
using UnityEditor;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Previews a <see cref="UsdAnimationPlayer"/> in Edit Mode without changing the scene: the
    /// player poses the stage at <see cref="Time"/> inside Unity's animation mode, as the Animation
    /// window previews a clip, and <see cref="Dispose"/> puts back every transform, active state,
    /// blend-shape weight and mesh it posed. The player's Inspector previews through this; a tool
    /// can too. Only one animation-mode preview runs at a time in the Editor, so this cannot start
    /// while the Animation window or a Timeline is previewing.
    /// </summary>
    public sealed class UsdAnimationPreview : IDisposable
    {
        private readonly UsdAnimationPlayer player;
        private readonly double timeBefore;
        private AnimationModeDriver driver;
        private AnimationClip clip;

        private UsdAnimationPreview(UsdAnimationPlayer player)
        {
            this.player = player;
            timeBefore = player.CurrentTime;
        }

        /// <summary>The player being previewed.</summary>
        public UsdAnimationPlayer Player => player;

        /// <summary>Seconds from the animation's start the preview shows.</summary>
        public float Time { get; private set; }

        /// <summary>Whether the preview is still running (animation mode can be ended from elsewhere).</summary>
        public bool IsActive => driver != null && AnimationMode.InAnimationMode(driver);

        /// <summary>Whether a preview could start now: not in Play Mode, and nothing else previewing.</summary>
        public static bool CanStart(UsdAnimationPlayer player) =>
            player != null && player.Data != null && !Application.isPlaying && !EditorUtility.IsPersistent(player) &&
            !AnimationMode.InAnimationMode();

        /// <summary>
        /// Starts previewing <paramref name="player"/> at its current time, or returns null when
        /// <see cref="CanStart"/> says it cannot.
        /// </summary>
        public static UsdAnimationPreview Start(UsdAnimationPlayer player)
        {
            if (!CanStart(player))
            {
                return null;
            }

            var preview = new UsdAnimationPreview(player)
            {
                // The clip's curves name every property the animation drives; sampling it registers
                // them with animation mode, which is what puts them back afterwards.
                clip = UsdAnimationClipBuilder.Build(player)
            };

            preview.clip.hideFlags = HideFlags.HideAndDontSave;
            preview.driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
            preview.driver.hideFlags = HideFlags.HideAndDontSave;
            preview.driver.name = "USD Animation preview";
            AnimationMode.StartAnimationMode(preview.driver);
            preview.SetTime((float)preview.timeBefore);
            return preview;
        }

        /// <summary>Poses the stage at <paramref name="seconds"/> from the animation's start.</summary>
        public void SetTime(float seconds)
        {
            if (!IsActive || player == null)
            {
                return;
            }

            Time = seconds;
            AnimationMode.BeginSampling();
            try
            {
                AnimationMode.SampleAnimationClip(player.gameObject, clip, seconds);
                // The clip's rotations are Unity's quaternion curves; the player then poses
                // everything as it does in Play Mode, which is USD's interpolation exactly.
                player.Evaluate(seconds);
            }
            finally
            {
                AnimationMode.EndSampling();
            }

            SceneView.RepaintAll();
        }

        /// <summary>Ends the preview and puts back everything it posed.</summary>
        public void Dispose()
        {
            if (driver != null)
            {
                if (AnimationMode.InAnimationMode(driver))
                {
                    AnimationMode.StopAnimationMode(driver);
                }

                UnityEngine.Object.DestroyImmediate(driver);
                driver = null;
                if (player != null)
                {
                    // Point caches are drawn into copies of their meshes; the scene's own go back,
                    // and so does the time the player was at.
                    player.RestoreMeshes();
                    var serialized = new SerializedObject(player);
                    SerializedProperty time = serialized.FindProperty("time");
                    if (time != null)
                    {
                        time.doubleValue = timeBefore;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }

                SceneView.RepaintAll();
            }

            if (clip != null)
            {
                UnityEngine.Object.DestroyImmediate(clip);
                clip = null;
            }
        }
    }
}
