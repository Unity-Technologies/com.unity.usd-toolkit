using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// An override export (UsdCompositionMode.Preserve) of a stage imported below the export root
    /// references its source. Every root prim comes through a reference of its own: the default
    /// prim by the file's default reference, the others (a light, an OmniGraph next to the scene,
    /// as Isaac Sim writes them) by their paths. Written as content they became empty Xforms.
    /// </summary>
    public sealed class UsdReferenceRootPrimsTests
    {
        private const string Source = @"#usda 1.0
(
    defaultPrim = ""World""
    metersPerUnit = 1
    upAxis = ""Y""
)

def Xform ""World""
{
    def Cube ""Box""
    {
    }
}

def SphereLight ""Lamp""
{
    float inputs:intensity = 3000
    double3 xformOp:translate = (0, 3, 0)
    uniform token[] xformOpOrder = [""xformOp:translate""]
}

def OmniGraph ""ActionGraph""
{
    token evaluator:type = ""execution""
}
";

        private string directory;
        private GameObject scene;
        private UsdImportResult import;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string sourcePath = Path.Combine(directory, "source.usda");
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

        [Test]
        public void RootPrimsOtherThanTheDefaultPrimAreReferencedByPath()
        {
            Transform lamp = ImportedStage.FindPrim(import.RootObject, "/Lamp").transform;
            lamp.localPosition += new Vector3(1f, 0f, 0f);

            string output = Path.Combine(directory, "scene.usda");
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
                scene, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
            string text = File.ReadAllText(output);
            Assert.That(result.ReferenceCount, Is.EqualTo(3), "World, Lamp and ActionGraph");
            Assert.That(text, Does.Contain("@./source.usda@</Lamp>"));
            Assert.That(text, Does.Contain("@./source.usda@</ActionGraph>"));
            Assert.That(text, Does.Not.Contain("def Xform \"Lamp\""));
            Assert.That(text, Does.Not.Contain("def Xform \"ActionGraph\""));

            using (UsdStage stage = UsdStage.Open(output))
            {
                string under = "/Scene/" + import.RootObject.name;
                Assert.That(stage.GetPrimAtPath(under + "/Lamp").TypeName, Is.EqualTo("SphereLight"));
                Assert.That(stage.GetPrimAtPath(under + "/ActionGraph").TypeName, Is.EqualTo("OmniGraph"));
                Assert.That(stage.GetPrimAtPath(under + "/World/Box").TypeName, Is.EqualTo("Cube"));
            }

            using (ImportedStage again = ImportedStage.Import(output))
            {
                Light light = again.Root.GetComponentInChildren<Light>(true);
                Assert.That(light, Is.Not.Null, "the light comes back as a light");
                Vector3 moved = light.transform.parent.position - again.Root.position;
                Assert.That(moved.x, Is.EqualTo(1f).Within(1e-4f), "with the move written through its reference");
                Assert.That(moved.y, Is.EqualTo(3f).Within(1e-4f));
            }
        }
    }
}
