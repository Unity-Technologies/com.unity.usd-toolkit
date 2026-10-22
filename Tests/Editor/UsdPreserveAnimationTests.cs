using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A Preserve export of an animated import writes what the user changed, not what playback
    /// moved: an override holds one value at Default time, which would stop the source's samples.
    /// </summary>
    public sealed class UsdPreserveAnimationTests
    {
        private string folder;

        [SetUp]
        public void CreateFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "UsdToolkitTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void DeleteFolder()
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void PlaybackIsNotWrittenAndTheAnimationSurvives()
        {
            string output = Path.Combine(folder, "played.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                stage.Result.Animation.EvaluateAtTimeCode(10.0); // Spinner at (4, 0, 2), Blinker shown again
                stage.Result.Animation.EvaluateAtTimeCode(5.0);  // Blinker hidden: differs from the import
                Export(stage, output);
            }

            Assert.That(File.ReadAllText(output), Does.Not.Contain("def "),
                        "playback alone defines nothing: not the joints, which are no prims");
            using (ImportedStage exported = ImportedStage.Import(output))
            {
                Assert.That(Vector3.Distance(exported.Find("/Root/Spinner").transform.localPosition, new Vector3(0f, 0f, 2f)),
                            Is.LessThan(1e-4f), "Spinner still starts where the source starts it");
                Assert.That(exported.Result.Animation, Is.Not.Null, "the output still animates");
                Assert.That(exported.Result.Animation.Data.TransformTracks.Count, Is.EqualTo(1), "Spinner's samples are not hidden");
                Assert.That(exported.Result.Animation.Data.VisibilityTracks.Count, Is.EqualTo(2), "nor are the visibility samples");
            }
        }

        [Test]
        public void AnEditToAnAnimatedPrimIsReportedNotWritten()
        {
            string output = Path.Combine(folder, "edited.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                stage.Find("/Root/Spinner").transform.localPosition += new Vector3(0f, 3f, 0f);
                LogAssert.Expect(LogType.Warning, new Regex("'/Root/Spinner' is animated by its source stage"));
                Export(stage, output);
            }

            using (ImportedStage exported = ImportedStage.Import(output))
            {
                Assert.That(exported.Result.Animation.Data.TransformTracks.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void EditsToStaticTransformsAreStillWritten()
        {
            // Blinker animates its visibility, not its transform, so moving it is an ordinary edit.
            string output = Path.Combine(folder, "moved.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                stage.Find("/Root/Blinker").transform.localPosition += new Vector3(0f, 1f, 0f);
                Export(stage, output);
            }

            using (ImportedStage exported = ImportedStage.Import(output))
            {
                Assert.That(Vector3.Distance(exported.Find("/Root/Blinker").transform.localPosition, new Vector3(2f, 1f, 0f)),
                            Is.LessThan(1e-4f), "USD (-2, 0, 0) moved up by one");
            }
        }

        private static void Export(ImportedStage stage, string output)
        {
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
                stage.Result.RootObject, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
            Assert.That(File.Exists(output), Is.True, result?.ToString());
        }
    }
}
