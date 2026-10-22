using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdAnimationRecorder.Save with UsdCompositionMode.Preserve: a recording of an imported stage
    /// is a layer over its source file with the time samples on the source's prims (and a new
    /// SkelAnimation on an imported Skeleton prim), not a copy of the model. Isaac Sim round trips
    /// with a robot asset need this: the recording is a few kilobytes beside the asset.
    /// </summary>
    public sealed class UsdRecorderOverrideTests
    {
        private const string Robot = @"#usda 1.0
(
    defaultPrim = ""Robot""
    metersPerUnit = 1
    upAxis = ""Y""
)

def Xform ""Robot""
{
    def Xform ""Base""
    {
        double3 xformOp:translate = (0, 0.5, 0)
        uniform token[] xformOpOrder = [""xformOp:translate""]

        def Cube ""Body""
        {
            double size = 0.5
        }

        def Xform ""Link""
        {
            double3 xformOp:translate = (0, 1, 0)
            float xformOp:rotateZ = 10
            uniform token[] xformOpOrder = [""xformOp:translate"", ""xformOp:rotateZ""]

            def Cube ""Arm""
            {
                double size = 0.25
            }
        }
    }
}
";

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

        private static Quaternion LinkRotation(int frame) => Quaternion.Euler(0f, 0f, 10f + 8f * frame);

        [TestCase(false, TestName = "AnOverrideRecordingOfAnImportRootSublayersItsSource")]
        [TestCase(true, TestName = "AnOverrideRecordingBelowTheRecordedObjectReferencesItsSource")]
        public void AnOverrideRecordingHoldsOnlyTheMotion(bool underAParent)
        {
            string source = Path.Combine(directory, "robot.usda");
            File.WriteAllText(source, Robot);
            string output = Path.Combine(directory, "motion.usda");
            var parent = underAParent ? new GameObject("Take") : null;
            UsdImportResult import = UsdImporter.Import(source, new UsdImportOptions { Parent = parent != null ? parent.transform : null });
            try
            {
                Transform link = ImportedStage.FindPrim(import.RootObject, "/Robot/Base/Link").transform;
                Transform basePrim = ImportedStage.FindPrim(import.RootObject, "/Robot/Base").transform;
                var recorder = new UsdAnimationRecorder(parent != null ? parent : import.RootObject, 30.0);
                for (int f = 0; f <= 10; f++)
                {
                    link.localRotation = LinkRotation(f);
                    recorder.Capture(f / 30.0);
                }

                UsdExportResult result = recorder.Save(output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
                Assert.That(result.Composition, Is.EqualTo(UsdCompositionMode.Preserve));
                string text = File.ReadAllText(output);
                Assert.That(text, Does.Contain(underAParent ? "references = @./robot.usda@" : "subLayers = [\n        @./robot.usda@"));
                Assert.That(text, Does.Not.Contain("def Cube").And.Not.Contain("def Mesh").And.Not.Contain("points"),
                            "no geometry is copied");
                Assert.That(text, Does.Contain("xformOp:transform.timeSamples"));
                Assert.That(Regex.Matches(text, "timeSamples").Count, Is.EqualTo(1), "only the link moved");

                using (UsdStage stage = UsdStage.Open(output))
                {
                    Assert.That(stage.HasTimeRange, Is.True);
                    Assert.That(stage.StartTimeCode, Is.EqualTo(0.0));
                    Assert.That(stage.EndTimeCode, Is.EqualTo(10.0));
                    Assert.That(stage.TimeCodesPerSecond, Is.EqualTo(30.0));
                }

                Vector3 basePosition = basePrim.localPosition;
                using (ImportedStage again = ImportedStage.Import(output))
                {
                    UsdAnimationPlayer player = again.Result.Animation;
                    Assert.That(player, Is.Not.Null);
                    Transform replayed = again.Root.GetComponentsInChildren<UsdPrimSource>(true)
                        .First(p => p.PrimPath.EndsWith("/Base/Link", StringComparison.Ordinal)).transform;
                    Assert.That(replayed.GetComponentsInChildren<MeshFilter>(true).Length, Is.EqualTo(1), "the arm comes from the source");
                    Assert.That(Vector3.Distance(replayed.parent.localPosition, basePosition), Is.LessThan(1e-5f), "the base keeps its place");
                    for (int f = 0; f <= 10; f++)
                    {
                        player.EvaluateAtTimeCode(f);
                        Assert.That(Quaternion.Angle(replayed.localRotation, LinkRotation(f)), Is.LessThan(0.01f), $"frame {f}");
                        Assert.That(Vector3.Distance(replayed.localPosition, new Vector3(0f, 1f, 0f)), Is.LessThan(1e-5f), $"frame {f}");
                    }
                }
            }
            finally
            {
                ImportedStage.Of(import).Dispose();
                if (parent != null)
                {
                    Object.DestroyImmediate(parent);
                }
            }
        }

        // An imported character: its Skeleton prim gets the recorded SkelAnimation, and the
        // visibility it animates its samples, on the source's own prims.
        [Test]
        public void AnOverrideRecordingOfACharacterAnimatesItsSkeletonPrim()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            string output = Path.Combine(directory, "skel_arm_motion.usda");
            var sourceFrame = new GameObject("SourceFrame");
            try
            {
                using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
                {
                    var recorder = new UsdAnimationRecorder(stage.Result.RootObject, expected.stage.timeCodesPerSecond);
                    recorder.CapturePlayer(stage.Result.Animation);
                    LogAssert.Expect(LogType.Warning, new Regex("imported mesh\\(es\\) whose points move were left to their source stage"));
                    recorder.Save(output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
                }

                string text = File.ReadAllText(output);
                Assert.That(text, Does.Contain("subLayers"));
                Assert.That(text, Does.Contain("def SkelAnimation \"Animation\""));
                Assert.That(text, Does.Contain("rel skel:animationSource = </Root/Arm/Skel/Animation>"));
                Assert.That(text, Does.Not.Contain("def Mesh").And.Not.Contain("point3f[] points"), "no geometry is copied");

                using (ImportedStage again = ImportedStage.Import(output))
                {
                    UsdAnimationPlayer player = again.Result.Animation;
                    Assert.That(player, Is.Not.Null);
                    foreach (double time in expected.times.Where(t => Math.Abs(t - Math.Round(t)) < 1e-9))
                    {
                        player.EvaluateAtTimeCode(time);
                        foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                        {
                            var skinned = again.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                            UsdSkinningTests.AssertSkinnedPoints(sourceFrame.transform, skinned, mesh, UsdFixtures.FrameAt(mesh, time));
                        }

                        foreach (ExpectedVisibility visibility in expected.visibility ?? new ExpectedVisibility[0])
                        {
                            string value = visibility.frames.First(f => Math.Abs(f.time - time) < 1e-9).value;
                            Assert.That(again.Find(visibility.path).activeInHierarchy, Is.EqualTo(value != "invisible"),
                                        $"{visibility.path} at t={time}");
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(sourceFrame);
            }
        }

        [Test]
        public void AnAlembicRecordingCannotHoldOverrides()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var recorder = new UsdAnimationRecorder(cube, 24.0);
                recorder.Capture(0.0);
                Assert.Throws<ArgumentException>(() => recorder.Save(Path.Combine(directory, "take.abc"),
                    new UsdExportOptions { Composition = UsdCompositionMode.Preserve }));
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }
    }
}
