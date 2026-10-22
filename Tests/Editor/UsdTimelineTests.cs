#if USD_TOOLKIT_TIMELINE
using NUnit.Framework;
using Unity.USDToolkit.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Unity.USDToolkit.Tests
{
    /// <summary>A Timeline track drives an imported stage's UsdAnimationPlayer.</summary>
    public sealed class UsdTimelineTests
    {
        [Test]
        public void ATimelineClipPosesTheStageAtItsLocalTime()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                var director = new GameObject("Director").AddComponent<PlayableDirector>();
                try
                {
                    var track = timeline.CreateTrack<UsdAnimationTrack>(null, "USD");
                    TimelineClip clip = track.CreateClip<UsdAnimationClip>();
                    ((UsdAnimationClip)clip.asset).Animation = player.Data;
                    clip.start = 1.0;    // the stage starts one second into the timeline
                    clip.duration = player.Duration;
                    Assert.That(((UsdAnimationClip)clip.asset).duration, Is.EqualTo(player.Duration).Within(1e-9),
                                "a clip takes its default length from the animation");

                    director.playableAsset = timeline;
                    director.SetGenericBinding(track, player);
                    director.timeUpdateMode = DirectorUpdateMode.Manual;

                    // Timeline time 1 + 5/24 s is clip time 5/24 s, which is stage time code 5.
                    director.time = 1.0 + 5.0 / 24.0;
                    director.Evaluate();
                    Assert.That(player.CurrentTime, Is.EqualTo(5.0 / 24.0).Within(1e-6));

                    foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                    {
                        var skinned = stage.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                        UsdSkinningTests.AssertSkinnedPoints(stage, skinned, mesh, UsdFixtures.FrameAt(mesh, 5.0));
                    }

                    Assert.That(stage.Find("/Root/Blinker").activeInHierarchy, Is.False, "hidden from t=4 to t=8");
                }
                finally
                {
                    Object.DestroyImmediate(director.gameObject);
                    Object.DestroyImmediate(timeline);
                }
            }
        }
    }
}
#endif
