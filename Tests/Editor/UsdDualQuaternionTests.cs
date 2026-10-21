using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Meshes that ask for dual-quaternion skinning are posed by UsdDualQuaternionSkin as UsdSkel
    /// poses them, checked against OpenUSD's own skinning of dqs_twist.usda (a half-turn twist that
    /// linear blending collapses), and keep the method through an export and a recording.
    /// </summary>
    public sealed class UsdDualQuaternionTests
    {
        private const float Tolerance = 1e-4f;
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void ADualQuaternionMeshIsPosedAsUsdSkelPosesIt()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "dqs_twist");
            ExpectedSkinnedMesh tube = expected.skinned[0];
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "dqs_twist.usda")))
            {
                GameObject prim = stage.Find(tube.path);
                Assert.That(prim.GetComponent<SkinnedMeshRenderer>(), Is.Null, "a SkinnedMeshRenderer only blends linearly");
                var skin = prim.GetComponent<UsdDualQuaternionSkin>();
                Assert.That(skin, Is.Not.Null);
                Assert.That(skin.Bones.Length, Is.EqualTo(2));
                foreach (ExpectedPointsFrame frame in tube.frames)
                {
                    stage.Result.Animation.EvaluateAtTimeCode(frame.time);
                    AssertDraws(stage.Root, skin, tube, frame);
                }

                // What the renderer draws is the same pose, through its vertex stream.
                stage.Result.Animation.EvaluateAtTimeCode(10.0);
                skin.Deform();
                var renderer = prim.GetComponent<MeshRenderer>();
                Assert.That(renderer.additionalVertexStreams, Is.Not.Null);
                var baked = new Mesh();
                try
                {
                    skin.BakeMesh(baked);
                    Assert.That(renderer.additionalVertexStreams.vertices, Is.EqualTo(baked.vertices));
                }
                finally
                {
                    Object.DestroyImmediate(baked);
                }
            }
        }

        [Test]
        public void TurnedOffTheMeshIsSkinnedLinearly()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "dqs_twist");
            ExpectedSkinnedMesh tube = expected.skinned[0];
            var options = new UsdImportOptions { DualQuaternionSkinning = false };
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "dqs_twist.usda"), options))
            {
                var skinned = stage.Find(tube.path).GetComponent<SkinnedMeshRenderer>();
                Assert.That(skinned, Is.Not.Null);
                stage.Result.Animation.EvaluateAtTimeCode(10.0);
                // The half-turn collapses the middle ring under linear blending: far from UsdSkel's.
                Assert.Throws<AssertionException>(() =>
                    UsdSkinningTests.AssertSkinnedPoints(stage, skinned, tube, UsdFixtures.FrameAt(tube, 10.0)));
            }
        }

        [Test]
        public void TheMethodSurvivesAnExportAndARecording()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "dqs_twist");
            ExpectedSkinnedMesh tube = expected.skinned[0];
            string exported = Path.Combine(directory, "dqs_export.usda");
            string recorded = Path.Combine(directory, "dqs_recorded.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "dqs_twist.usda")))
            {
                stage.Result.Animation.EvaluateAtTimeCode(7.5);
                UsdExporter.ExportGameObjectWithResult(stage.Result.RootObject, exported,
                    new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy });
                var recorder = new UsdAnimationRecorder(stage.Result.RootObject, expected.stage.timeCodesPerSecond);
                recorder.CapturePlayer(stage.Result.Animation);
                recorder.Save(recorded);
            }

            Assert.That(File.ReadAllText(exported), Does.Contain("primvars:skel:skinningMethod = \"dualQuaternion\""));
            using (ImportedStage again = ImportedStage.Import(exported))
            {
                var skin = again.Find(tube.path).GetComponent<UsdDualQuaternionSkin>();
                Assert.That(skin, Is.Not.Null, "the export kept the method");
                AssertDraws(again.Root, skin, tube, UsdFixtures.FrameAt(tube, 7.5));
            }

            using (ImportedStage again = ImportedStage.Import(recorded))
            {
                var skin = again.Find(tube.path).GetComponent<UsdDualQuaternionSkin>();
                Assert.That(skin, Is.Not.Null);
                foreach (ExpectedPointsFrame frame in tube.frames)
                {
                    if (Math.Abs(frame.time - Math.Round(frame.time)) > 1e-9)
                    {
                        continue; // the recording has one sample a frame
                    }

                    again.Result.Animation.EvaluateAtTimeCode(frame.time);
                    // Recorded joint scales are half floats in UsdSkel, good to about three digits.
                    AssertDraws(again.Root, skin, tube, frame, 2e-3f);
                }
            }
        }

        internal static void AssertDraws(Transform importRoot, UsdDualQuaternionSkin skin, ExpectedSkinnedMesh mesh,
                                         ExpectedPointsFrame frame, float tolerance = Tolerance)
        {
            int[] pointOf = UsdFixtures.PointOfEachVertex(skin.SharedMesh.vertices, mesh.restPoints);
            var baked = new Mesh();
            try
            {
                skin.BakeMesh(baked);
                Vector3[] vertices = baked.vertices;
                float worst = 0f;
                int worstVertex = -1;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 actual = skin.transform.TransformPoint(vertices[v]);
                    Vector3 wanted = UsdFixtures.StageToWorld(importRoot, UsdFixtures.Point(frame.points, pointOf[v]));
                    float error = Vector3.Distance(actual, wanted);
                    if (error > worst)
                    {
                        worst = error;
                        worstVertex = v;
                    }
                }

                Assert.That(worst, Is.LessThan(tolerance),
                            $"{mesh.path} at t={frame.time}: vertex {worstVertex} is off by {worst}");
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }
    }
}
