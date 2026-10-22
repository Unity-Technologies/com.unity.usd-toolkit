using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdImportOptions.Limits: the budgets an import is held to. Over one, the import fails by
    /// default, or with SkipWithWarning imports what fits and warns about the rest; the preview
    /// opens under the same limits, and its stage is reused only by an import with the same ones.
    /// The stage here is ten cubes and a point instancer with five placements of a one-cube
    /// prototype: eleven meshes, fourteen nodes.
    /// </summary>
    public sealed class UsdImportLimitsTests
    {
        private string directory;
        private string path;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "budget.usda");
            var text = new StringBuilder("#usda 1.0\n(\n    defaultPrim = \"Root\"\n)\n\ndef Xform \"Root\"\n{\n");
            for (int i = 0; i < 10; i++)
            {
                text.Append($"    def Cube \"Cube{i}\"\n    {{\n    }}\n");
            }

            text.Append("    def PointInstancer \"Scatter\"\n    {\n        rel prototypes = </Root/Scatter/Proto>\n");
            text.Append("        int[] protoIndices = [0, 0, 0, 0, 0]\n");
            text.Append("        point3f[] positions = [(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0)]\n");
            text.Append("        def Cube \"Proto\"\n        {\n        }\n    }\n}\n");
            File.WriteAllText(path, text.ToString());
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void TheDefaultsImportTheWholeStage()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Assert.That(stage.Result.MeshCount, Is.EqualTo(11));
            }
        }

        [Test]
        public void OverALimitTheImportFailsByDefault()
        {
            var options = new UsdImportOptions { Limits = new UsdImportLimits { MaxMeshes = 4 } };
            var exception = Assert.Throws<UsdImportException>(() => UsdImporter.Import(path, options));
            Assert.That(exception.Message, Does.Contain("import limit of 4 meshes"));
        }

        [Test]
        public void SkippingImportsWhatFitsAndWarns()
        {
            var options = new UsdImportOptions
            {
                Limits = new UsdImportLimits { MaxMeshes = 4, WhenExceeded = UsdImportLimitBehavior.SkipWithWarning }
            };
            LogAssert.Expect(LogType.Warning, new Regex("7 meshes were not imported: over the import limit of 4 meshes"));
            using (ImportedStage stage = ImportedStage.Import(path, options))
            {
                Assert.That(stage.Result.MeshCount, Is.EqualTo(4));
                Assert.That(stage.Root.GetComponentsInChildren<MeshFilter>(true).Length, Is.EqualTo(4));
                Assert.That(stage.Find("/Root/Cube9"), Is.Not.Null, "every node is still there");
            }
        }

        [Test]
        public void TheWalkStopsAtTheNodeLimit()
        {
            var options = new UsdImportOptions
            {
                Limits = new UsdImportLimits { MaxNodes = 5, WhenExceeded = UsdImportLimitBehavior.SkipWithWarning }
            };
            LogAssert.Expect(LogType.Warning, new Regex("import limit of 5 nodes; the walk stopped at '/Root/Cube4'"));
            using (ImportedStage stage = ImportedStage.Import(path, options))
            {
                Assert.That(stage.Find("/Root/Cube3"), Is.Not.Null);
                Assert.That(stage.Find("/Root/Cube4"), Is.Null, "the first prim over the limit");
                Assert.That(stage.Find("/Root/Scatter"), Is.Null, "and everything after it");
            }
        }

        [Test]
        public void PlacementsOverTheirLimitAreNotDrawn()
        {
            var options = new UsdImportOptions
            {
                Limits = new UsdImportLimits { MaxInstancePlacements = 3, WhenExceeded = UsdImportLimitBehavior.SkipWithWarning }
            };
            LogAssert.Expect(LogType.Warning, new Regex("5 point-instancer placements were not drawn"));
            using (ImportedStage stage = ImportedStage.Import(path, options))
            {
                Assert.That(stage.Result.MeshCount, Is.EqualTo(11), "the prototype mesh itself is imported");
                UsdPointInstancer instancer = stage.Root.GetComponentInChildren<UsdPointInstancer>(true);
                Assert.That(instancer == null || instancer.InstanceCount == 0, Is.True, "but none of its placements");
            }
        }

        [Test]
        public void ThePreviewUsesTheImportsLimits()
        {
            var limited = new UsdImportOptions
            {
                Limits = new UsdImportLimits { MaxMeshes = 4, WhenExceeded = UsdImportLimitBehavior.SkipWithWarning }
            };
            LogAssert.ignoreFailingMessages = true;
            Assert.That(UsdImporter.GetPreviewInfo(path, limited).MeshCount, Is.EqualTo(4));

            // A preview under the defaults keeps a stage the limited import must not reuse.
            Assert.That(UsdImporter.GetPreviewInfo(path).MeshCount, Is.EqualTo(11));
            using (ImportedStage stage = ImportedStage.Import(path, limited))
            {
                Assert.That(stage.Result.MeshCount, Is.EqualTo(4));
                UsdImportLimits recorded = stage.Result.Source.ImportOptions.Limits;
                Assert.That(recorded.MaxMeshes, Is.EqualTo(4), "the source records the limits it imported with");
                Assert.That(recorded, Is.Not.SameAs(limited.Limits), "as a copy");
            }
        }

        [Test]
        public void NonPositiveValuesKeepTheDefaults()
        {
            var options = new UsdImportOptions
            {
                Limits = new UsdImportLimits { MaxNodes = 0, MaxMeshes = -1, MaxJoints = 0 }
            };
            using (ImportedStage stage = ImportedStage.Import(path, options))
            {
                Assert.That(stage.Result.MeshCount, Is.EqualTo(11));
            }
        }
    }
}
