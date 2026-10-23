using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The import's warnings about the stage as a whole: prims of a type no loaded schema defines,
    /// and a stage that yields no geometry. Each is one line, once per import, naming the file and
    /// prim paths only. The fixtures are Tests/Fixtures~/StageWarnings (make_stage_warnings.py);
    /// UnknownSolid is a made-up type no schema defines.
    /// </summary>
    public sealed class UsdStageWarningTests
    {
        private const string Folder = "StageWarnings";

        private static readonly Regex UnknownTypeWarning =
            new Regex(@"^USD import: '[^'/\\]+\.usda' has \d+ prim\(s\) of a type no loaded schema defines");

        private static readonly Regex NoGeometryWarning =
            new Regex(@"^USD import: '[^'/\\]+\.usda' yields no geometry");

        [Test]
        public void AnUnknownTypeBesideAMeshWarnsOnceAndTheMeshImports()
        {
            List<string> warnings = ImportCollectingWarnings("mesh_and_unknown.usda", out UsdImportResult result);
            using (ImportedStage.Of(result))
            {
                Assert.That(result.MeshCount, Is.EqualTo(1));
                Assert.That(warnings, Is.EqualTo(new[]
                {
                    "USD import: 'mesh_and_unknown.usda' has 1 prim(s) of a type no loaded schema defines; " +
                    "only their place in the hierarchy is imported: '/Root/Part'."
                }));
            }
        }

        [Test]
        public void OnlyAnUnknownTypeWarnsOfItAndOfNoGeometry()
        {
            List<string> warnings = ImportCollectingWarnings("unknown_only.usda", out UsdImportResult result);
            using (ImportedStage.Of(result))
            {
                Assert.That(result.MeshCount, Is.Zero);
                Assert.That(warnings, Is.EqualTo(new[]
                {
                    "USD import: 'unknown_only.usda' has 1 prim(s) of a type no loaded schema defines; " +
                    "only their place in the hierarchy is imported: '/Part'.",
                    "USD import: 'unknown_only.usda' yields no geometry; no mesh was imported."
                }));
            }
        }

        [Test]
        public void ValidGeometryGivesNoStageWarning()
        {
            List<string> warnings = ImportCollectingWarnings("mesh_only.usda", out UsdImportResult result);
            using (ImportedStage.Of(result))
            {
                Assert.That(result.MeshCount, Is.EqualTo(1));
                Assert.That(warnings, Is.Empty);
            }
        }

        // Without a StageWarningCallback the warnings go to the Console, as at runtime.
        [Test]
        public void WithoutACallbackTheWarningsAreLogged()
        {
            LogAssert.Expect(LogType.Warning, UnknownTypeWarning);
            LogAssert.Expect(LogType.Warning, NoGeometryWarning);
            using (ImportedStage.Import(UsdFixtures.PathOf(Folder, "unknown_only.usda")))
            {
            }

            LogAssert.NoUnexpectedReceived();
        }

        // ImportAsync, which the Runtime Demo uses, warns the same way.
        [UnityTest]
        public IEnumerator ImportAsyncWarnsTheSame()
        {
            var warnings = new List<string>();
            var options = new UsdImportOptions { StageWarningCallback = warnings.Add };
            Task<UsdImportResult> import = UsdImporter.ImportAsync(UsdFixtures.PathOf(Folder, "unknown_only.usda"), options);
            while (!import.IsCompleted)
            {
                yield return null;
            }

            Assert.That(import.Status, Is.EqualTo(TaskStatus.RanToCompletion), import.Exception?.ToString());
            using (ImportedStage.Of(import.Result))
            {
                Assert.That(warnings.Count, Is.EqualTo(2));
                Assert.That(warnings[0], Does.Match(UnknownTypeWarning.ToString()));
                Assert.That(warnings[1], Does.Match(NoGeometryWarning.ToString()));
            }
        }

        // With a callback (the USD asset importer passes the import context's) nothing reaches
        // the Console, so an asset import shows each warning once.
        private static List<string> ImportCollectingWarnings(string fixture, out UsdImportResult result)
        {
            var warnings = new List<string>();
            result = UsdImporter.Import(UsdFixtures.PathOf(Folder, fixture), new UsdImportOptions
            {
                DoubleSidedOpenMeshes = false,
                StageWarningCallback = warnings.Add
            });
            Assert.That(result?.RootObject, Is.Not.Null);
            LogAssert.NoUnexpectedReceived();
            return warnings;
        }
    }
}
