using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// An override export (UsdCompositionMode.Preserve) of a stage imported below the export root,
    /// which references its source: an imported object moved under another parent is written as a
    /// relocate, so the source prim -- with what its composition brings -- is composed at the new
    /// place, and the object's edits go there. A copy of an imported object, and a move in a stage
    /// the output sublayers (whose prims relocates cannot move), stay new content as before.
    /// </summary>
    public sealed class UsdRelocateExportTests
    {
        private const string Source = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Xform ""A""
    {
        def Xform ""Box""
        {
            custom string note = ""from the source""

            def Cube ""Lid""
            {
                double size = 0.5
                double3 xformOp:translate = (0, 1, 0)
                uniform token[] xformOpOrder = [""xformOp:translate""]
            }
        }
    }

    def Xform ""B""
    {
        double3 xformOp:translate = (5, 0, 0)
        uniform token[] xformOpOrder = [""xformOp:translate""]
    }
}
";

        private string directory;
        private string sourcePath;
        private GameObject scene;
        private UsdImportResult import;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            sourcePath = Path.Combine(directory, "source.usda");
            File.WriteAllText(sourcePath, Source);
            scene = new GameObject("Scene");
            import = UsdImporter.Import(sourcePath, new UsdImportOptions { Parent = scene.transform });
        }

        [TearDown]
        public void TearDown()
        {
            if (import?.RootObject != null)
            {
                ImportedStage.Of(import).Dispose();
            }

            Object.DestroyImmediate(scene);
            Directory.Delete(directory, true);
        }

        private Transform Prim(string path) => ImportedStage.FindPrim(import.RootObject, path).transform;

        private UsdExportResult Export(GameObject root, string name, out string text)
        {
            string output = Path.Combine(directory, name);
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
                root, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
            text = File.ReadAllText(output);
            return result;
        }

        // The output's prims, composed, by name below `under`.
        private static GameObject FindByPath(ImportedStage stage, string suffix) =>
            stage.Root.GetComponentsInChildren<UsdPrimSource>(true)
                .Where(p => p.PrimPath.EndsWith(suffix, StringComparison.Ordinal)).Select(p => p.gameObject).FirstOrDefault();

        [Test]
        public void AMovedObjectIsWrittenAsARelocate()
        {
            Transform box = Prim("/Root/A/Box");
            box.SetParent(Prim("/Root/B"), false);

            UsdExportResult result = Export(scene, "moved.usda", out string text);
            Assert.That(result.RelocateCount, Is.EqualTo(1));
            Assert.That(text, Does.Contain("relocates"));
            Assert.That(text, Does.Match(@"</Scene/[^>]*/Root/A/Box>: </Scene/[^>]*/Root/B/Box>"),
                        "from its old place in the referenced source to its new one");
            Assert.That(text, Does.Not.Contain("def Cube \"Lid\""), "the source's prims are not copied");

            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "moved.usda")))
            {
                Assert.That(FindByPath(again, "/Root/A/Box"), Is.Null, "nothing is left at the old place");
                GameObject moved = FindByPath(again, "/Root/B/Box");
                Assert.That(moved, Is.Not.Null, "the box is composed under B");
                Assert.That(FindByPath(again, "/Root/B/Box/Lid").GetComponent<MeshFilter>(), Is.Not.Null,
                            "with the source's own children");
                Assert.That(again.Result.NativeDiagnostics ?? string.Empty, Does.Not.Contain("relocation"),
                            "and no composition error");
            }
        }

        [Test]
        public void AMoveUnderANewObjectIsARelocateToo()
        {
            var group = new GameObject("Group");
            group.transform.SetParent(Prim("/Root"), false);
            group.transform.localPosition = new Vector3(0f, 0f, 3f);
            Prim("/Root/A/Box").SetParent(group.transform, false);

            UsdExportResult result = Export(scene, "grouped.usda", out string text);
            Assert.That(result.RelocateCount, Is.EqualTo(1));
            Assert.That(text, Does.Match(@"</Scene/[^>]*/Root/A/Box>: </Scene/[^>]*/Root/Group/Box>"));
            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "grouped.usda")))
            {
                Assert.That(FindByPath(again, "/Root/Group/Box/Lid"), Is.Not.Null);
                Assert.That(FindByPath(again, "/Root/Group").transform.localPosition.z, Is.EqualTo(3f).Within(1e-5f),
                            "the new group is written too");
            }
        }

        [Test]
        public void ACopyIsNewContent()
        {
            Transform box = Prim("/Root/A/Box");
            Object.Instantiate(box.gameObject, Prim("/Root/B"), false).name = "Box";

            UsdExportResult result = Export(scene, "copied.usda", out string text);
            Assert.That(result.RelocateCount, Is.EqualTo(0), "the original is still in place");
            Assert.That(text, Does.Not.Contain("relocates"));
            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "copied.usda")))
            {
                Assert.That(FindByPath(again, "/Root/A/Box/Lid"), Is.Not.Null, "the original");
                Assert.That(FindByPath(again, "/Root/B/Box/Lid"), Is.Not.Null, "and the copy");
            }
        }

        [Test]
        public void EditsToAMovedObjectGoToItsNewPlace()
        {
            Transform box = Prim("/Root/A/Box");
            box.SetParent(Prim("/Root/B"), false);
            Transform lid = box.Find("Lid");
            lid.localPosition = new Vector3(0f, 2f, 0f);

            Export(scene, "edited.usda", out string text);
            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "edited.usda")))
            {
                Vector3 position = FindByPath(again, "/Root/B/Box/Lid").transform.localPosition;
                Assert.That(Vector3.Distance(position, new Vector3(0f, 2f, 0f)), Is.LessThan(1e-5f),
                            "the lid's new position, written at the relocated path");
            }
        }

        [Test]
        public void TheOldParentCanBeDeletedAfterTheMove()
        {
            Transform box = Prim("/Root/A/Box");
            box.SetParent(Prim("/Root/B"), false);
            Object.DestroyImmediate(Prim("/Root/A").gameObject);

            UsdExportResult result = Export(scene, "deleted.usda", out string _);
            Assert.That(result.RelocateCount, Is.EqualTo(1));
            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "deleted.usda")))
            {
                Assert.That(FindByPath(again, "/Root/A"), Is.Null, "A is deactivated");
                Assert.That(FindByPath(again, "/Root/B/Box/Lid"), Is.Not.Null, "the box it held lives on under B");
            }
        }

        [Test]
        public void AMoveInASublayeredStageStaysNewContent()
        {
            // Exporting the import root itself sublayers the source: its prims are the output's
            // own layer stack, which a relocate cannot move.
            Transform box = Prim("/Root/A/Box");
            box.SetParent(Prim("/Root/B"), false);
            UsdExportResult result = Export(import.RootObject, "sublayered.usda", out string text);
            Assert.That(result.RelocateCount, Is.EqualTo(0));
            Assert.That(text, Does.Not.Contain("relocates"));
            using (ImportedStage again = ImportedStage.Import(Path.Combine(directory, "sublayered.usda")))
            {
                Assert.That(FindByPath(again, "/Root/A/Box"), Is.Null, "the original is deactivated");
                Assert.That(FindByPath(again, "/Root/B/Box/Lid"), Is.Not.Null, "and the box written under B");
            }
        }
    }
}
