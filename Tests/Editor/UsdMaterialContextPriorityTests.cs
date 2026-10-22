using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Which network a material imports from when it carries several, and what a mesh with no
    /// material gets. Both follow Isaac Sim 6.2 (Omniverse RTX), measured with the same stage:
    /// an MDL network wins, then MaterialX, then UsdPreviewSurface; an unbound mesh with no
    /// displayColor is UsdPreviewSurface's default 18% grey, and with one it takes that colour.
    /// </summary>
    public sealed class UsdMaterialContextPriorityTests
    {
        private string directory;
        private string path;

        private static string Sphere(string name, double x, string material = null, string displayColor = null)
        {
            string text = $"    def Sphere \"{name}\"" + (material != null ? " (\n        prepend apiSchemas = [\"MaterialBindingAPI\"]\n    )\n" : "\n");
            text += $"    {{\n        double radius = 0.25\n        double3 xformOp:translate = ({x}, 0.25, 0)\n        uniform token[] xformOpOrder = [\"xformOp:translate\"]\n";
            if (material != null)
            {
                text += $"        rel material:binding = </World/Looks/{material}>\n";
            }

            if (displayColor != null)
            {
                text += $"        color3f[] primvars:displayColor = [{displayColor}]\n";
            }

            return text + "    }\n";
        }

        private static string Preview(string name) =>
            $"        def Material \"{name}\"\n        {{\n" +
            $"            token outputs:surface.connect = </World/Looks/{name}/Preview.outputs:surface>\n" +
            "            def Shader \"Preview\"\n            {\n                uniform token info:id = \"UsdPreviewSurface\"\n" +
            "                color3f inputs:diffuseColor = (1, 0, 0)\n                token outputs:surface\n            }\n";

        private static string Mdl(string name) =>
            $"            token outputs:mdl:surface.connect = </World/Looks/{name}/OmniPBR.outputs:out>\n" +
            "            def Shader \"OmniPBR\"\n            {\n                uniform token info:implementationSource = \"sourceAsset\"\n" +
            "                uniform asset info:mdl:sourceAsset = @OmniPBR.mdl@\n" +
            "                uniform token info:mdl:sourceAsset:subIdentifier = \"OmniPBR\"\n" +
            "                color3f inputs:diffuse_color_constant = (0, 1, 0)\n                token outputs:out\n            }\n";

        private static string Mtlx(string name) =>
            $"            token outputs:mtlx:surface.connect = </World/Looks/{name}/Std.outputs:out>\n" +
            "            def Shader \"Std\"\n            {\n                uniform token info:id = \"ND_standard_surface_surfaceshader\"\n" +
            "                color3f inputs:base_color = (0, 0, 1)\n                float inputs:base = 1\n                token outputs:out\n            }\n";

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "material_contexts.usda");
            File.WriteAllText(path,
                "#usda 1.0\n(\n    defaultPrim = \"World\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n)\n\ndef Xform \"World\"\n{\n" +
                Sphere("AllThree", 0.0, "AllThree") + Sphere("PreviewMtlx", 0.7, "PreviewMtlx") +
                Sphere("Unbound", 1.4) + Sphere("UnboundRed", 2.1, null, "(1, 0, 0)") +
                "    def Scope \"Looks\"\n    {\n" +
                Preview("AllThree") + Mdl("AllThree") + Mtlx("AllThree") + "        }\n" +
                Preview("PreviewMtlx") + Mtlx("PreviewMtlx") + "        }\n" +
                "    }\n}\n");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        private static Color LinearBaseColor(ImportedStage stage, string primPath)
        {
            Material material = stage.Find(primPath).GetComponent<Renderer>().sharedMaterial;
            Color color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.GetColor("_Color");
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
        }

        private static void AssertColor(Color actual, float r, float g, float b, string what)
        {
            Assert.That(actual.r, Is.EqualTo(r).Within(0.01f), what + " (r)");
            Assert.That(actual.g, Is.EqualTo(g).Within(0.01f), what + " (g)");
            Assert.That(actual.b, Is.EqualTo(b).Within(0.01f), what + " (b)");
        }

        [Test]
        public void MdlWinsOverMaterialXAndPreviewSurface()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                AssertColor(LinearBaseColor(stage, "/World/AllThree"), 0f, 1f, 0f, "OmniPBR's green");
            }
        }

        [Test]
        public void MaterialXWinsOverPreviewSurface()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                AssertColor(LinearBaseColor(stage, "/World/PreviewMtlx"), 0f, 0f, 1f, "standard_surface's blue");
            }
        }

        [Test]
        public void AnUnboundMeshGetsPreviewSurfaceDefaultGreyOrItsDisplayColor()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                AssertColor(LinearBaseColor(stage, "/World/Unbound"), 0.18f, 0.18f, 0.18f, "UsdPreviewSurface's default diffuse");
                AssertColor(LinearBaseColor(stage, "/World/UnboundRed"), 1f, 0f, 0f, "displayColor");
            }
        }
    }
}
