using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Stage animation on import: UsdAnimationPlayer poses skeletons, blend shapes, transforms and
    /// visibility at any time, and every time the expected files cover matches OpenUSD.
    /// </summary>
    public sealed class UsdAnimationTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly object[] Frames =
        {
            new object[] { "skel_arm", 0.0 }, new object[] { "skel_arm", 2.5 }, new object[] { "skel_arm", 4.0 },
            new object[] { "skel_arm", 5.0 }, new object[] { "skel_arm", 6.0 }, new object[] { "skel_arm", 7.5 },
            new object[] { "skel_arm", 8.0 }, new object[] { "skel_arm", 10.0 },
            new object[] { "blender_arm", 1.0 }, new object[] { "blender_arm", 6.5 }, new object[] { "blender_arm", 12.0 },
            new object[] { "blender_arm", 18.0 }, new object[] { "blender_arm", 24.0 },
        };

        [Test]
        public void ThePlayerHoldsEveryAnimatedTrack()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                Assert.That(player, Is.Not.Null);
                Assert.That(player.gameObject, Is.EqualTo(stage.Result.RootObject));
                UsdAnimationData data = player.Data;
                Assert.That(data.TransformTracks.Count, Is.EqualTo(1), "Spinner");
                Assert.That(data.TransformTracks[0].Path, Is.EqualTo("Root/Spinner"));
                Assert.That(data.VisibilityTracks.Count, Is.EqualTo(2), "Blinker and LateBlinker");
                Assert.That(data.SkeletonTracks.Count, Is.EqualTo(1));
                Assert.That(data.SkeletonTracks[0].JointCount, Is.EqualTo(3));
                Assert.That(data.BlendShapeTracks.Count, Is.EqualTo(1), "Strip's Bulge");
                Assert.That(data.StartTimeCode, Is.EqualTo(0.0));
                Assert.That(data.EndTimeCode, Is.EqualTo(10.0));
                Assert.That(player.Duration, Is.EqualTo(10.0 / 24.0).Within(1e-9));
            }
        }

        [TestCaseSource(nameof(Frames))]
        public void SkinnedPointsFollowOpenUsd(string fixture, double time)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", fixture);
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", fixture + ".usda")))
            {
                stage.Result.Animation.EvaluateAtTimeCode(time);
                foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                {
                    var skinned = stage.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                    UsdSkinningTests.AssertSkinnedPoints(stage, skinned, mesh, UsdFixtures.FrameAt(mesh, time));
                }
            }
        }

        [Test]
        public void TransformsAndVisibilityFollowOpenUsd()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                foreach (double time in expected.times)
                {
                    player.EvaluateAtTimeCode(time);
                    foreach (ExpectedXform xform in expected.xforms)
                    {
                        float[] m = FrameMatrix(xform, time);
                        Transform target = stage.Find(xform.path).transform;
                        // Three points of the prim's own space, through USD's matrix (row vectors)
                        // and through Unity's hierarchy, must land in the same place.
                        foreach (Vector3 local in new[] { Vector3.zero, Vector3.right, Vector3.forward })
                        {
                            var usdWorld = new Vector3(
                                local.x * m[0] + local.y * m[4] + local.z * m[8] + m[12],
                                local.x * m[1] + local.y * m[5] + local.z * m[9] + m[13],
                                local.x * m[2] + local.y * m[6] + local.z * m[10] + m[14]);
                            Vector3 actual = target.TransformPoint(new Vector3(-local.x, local.y, local.z));
                            Vector3 wanted = UsdFixtures.StageToWorld(stage.Root, usdWorld);
                            Assert.That(Vector3.Distance(actual, wanted), Is.LessThan(Tolerance),
                                        $"{xform.path} at t={time}, local {local}");
                        }
                    }

                    foreach (ExpectedVisibility visibility in expected.visibility)
                    {
                        string value = TokenAt(visibility, time);
                        Assert.That(stage.Find(visibility.path).activeInHierarchy, Is.EqualTo(value != "invisible"),
                                    $"{visibility.path} at t={time} is {value}");
                    }
                }
            }
        }

        [Test]
        public void TheImportStartsPosedAtTheReadTime()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                Assert.That(stage.Result.Animation.CurrentTime, Is.EqualTo(0.0));
                Assert.That(stage.Find("/Root/LateBlinker").activeInHierarchy, Is.False, "hidden at t=0");
                GameObject box = stage.Find("/Root/LateBlinker/Box");
                Assert.That(box.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0),
                            "the geometry is there for when the animation shows it");
                stage.Result.Animation.EvaluateAtTimeCode(6.0);
                Assert.That(box.activeInHierarchy, Is.True, "shown at t=6");
            }
        }

        [Test]
        public void WithoutImportAnimationThereIsNoPlayer()
        {
            var options = new UsdImportOptions { ImportAnimation = false };
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda"), options))
            {
                Assert.That(stage.Result.Animation, Is.Null);
                Assert.That(stage.Result.RootObject.GetComponent<UsdAnimationPlayer>(), Is.Null);
                Assert.That(stage.Find("/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>(), Is.Not.Null,
                            "skinning does not depend on animation");
            }
        }

        [Test]
        public void EvaluateTakesSecondsFromTheStart()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                Transform spinner = stage.Find("/Root/Spinner").transform;
                player.Evaluate(10.0 / 24.0); // time code 10
                Assert.That(Vector3.Distance(spinner.localPosition, new Vector3(-4f, 0f, 2f)), Is.LessThan(Tolerance));
                player.Stop();
                Assert.That(player.CurrentTime, Is.EqualTo(0.0));
                Assert.That(player.IsPlaying, Is.False);
                Assert.That(Vector3.Distance(spinner.localPosition, new Vector3(0f, 0f, 2f)), Is.LessThan(Tolerance));
            }
        }

        private static float[] FrameMatrix(ExpectedXform xform, double time)
        {
            foreach (ExpectedMatrixFrame frame in xform.frames)
            {
                if (System.Math.Abs(frame.time - time) < 1e-9)
                {
                    return frame.matrix;
                }
            }

            Assert.Fail($"{xform.path} has no expected matrix at {time}.");
            return null;
        }

        private static string TokenAt(ExpectedVisibility visibility, double time)
        {
            foreach (ExpectedTokenFrame frame in visibility.frames)
            {
                if (System.Math.Abs(frame.time - time) < 1e-9)
                {
                    return frame.value;
                }
            }

            Assert.Fail($"{visibility.path} has no expected value at {time}.");
            return null;
        }
    }
}
