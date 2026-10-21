using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Unreal-connector MDL that blends a texture parameter over the diffuse map with Unreal's
    /// BlendOverlay, as NVIDIA's Simple_Warehouse aisle signs do (M_AisleSign: the aisle number
    /// over the sign). The importer cannot evaluate MDL, so it recognises the pattern the
    /// connector writes and bakes the overlay into the albedo. Here the diffuse map is a uniform
    /// grey and the overlay is white on the left, black on the right: the baked albedo must be
    /// white and black, not the grey a plain diffuse read gives.
    /// </summary>
    public sealed class UsdUnrealOverlayTests
    {
        private const string Module =
            "mdl 1.3;\n\nimport ::tex::*;\nimport ::state::*;\nusing .::OmniUe4Base import *;\n\n" +
            "export material M_Overlay(\n" +
            "\tuniform texture_2d Text = texture_2d(\"./Overlay_Default.png\",::tex::gamma_linear))\n" +
            " = \n\tlet {\n" +
            "\tfloat2 CustomizedUV0_mdl = float2(state::texture_coordinate(0).x,1.0-state::texture_coordinate(0).y);\n" +
            "\tfloat4 Local1 = tex::lookup_float4(Text,float2(CustomizedUV0_mdl.x,1.0-CustomizedUV0_mdl.y),tex::wrap_repeat,tex::wrap_repeat);\n" +
            "\tfloat3 Local2 = (1.0 - float3(Local1.x,Local1.y,Local1.z));\n" +
            "\tfloat3 Local3 = (Local2 * 2.0);\n" +
            "\tfloat4 Local4 = tex::lookup_float4(texture_2d(\"./T_Overlay_D.png\",::tex::gamma_srgb),float2(CustomizedUV0_mdl.x,1.0-CustomizedUV0_mdl.y),tex::wrap_repeat,tex::wrap_repeat);\n" +
            "\tfloat3 Local5 = (1.0 - float3(Local4.x,Local4.y,Local4.z));\n" +
            "\tfloat3 Local6 = (Local3 * Local5);\n" +
            "\tfloat3 Local7 = (1.0 - Local6);\n" +
            "\tfloat3 Local8 = (float3(Local1.x,Local1.y,Local1.z) * 2.0);\n" +
            "\tfloat3 Local9 = (Local8 * float3(Local4.x,Local4.y,Local4.z));\n" +
            "\tfloat Local10 = ((float3(Local1.x,Local1.y,Local1.z).x >= 0.5) ? Local7.x : Local9.x);\n" +
            "\tfloat Local11 = ((float3(Local1.x,Local1.y,Local1.z).y >= 0.5) ? Local7.y : Local9.y);\n" +
            "\tfloat Local12 = ((float3(Local1.x,Local1.y,Local1.z).z >= 0.5) ? Local7.z : Local9.z);\n" +
            "\tfloat3 BaseColor_mdl = float3(Local10,Local11,Local12);\n" +
            "\t} in\n\t\t::OmniUe4Base(base_color: BaseColor_mdl);\n";

        private string directory;
        private string path;

        private static void WritePng(string file, Func<int, Color32> pixelAtColumn)
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    pixels[y * 64 + x] = pixelAtColumn(x);
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(file, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "M_Overlay.mdl"), Module);
            WritePng(Path.Combine(directory, "T_Overlay_D.png"), x => new Color32(128, 128, 128, 255));
            WritePng(Path.Combine(directory, "Overlay_Default.png"), x => new Color32(128, 128, 128, 255));
            WritePng(Path.Combine(directory, "Overlay_Text.png"), x => x < 32 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255));
            path = Path.Combine(directory, "overlay.usda");
            File.WriteAllText(path,
                "#usda 1.0\n(\n    defaultPrim = \"Root\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n)\n\n" +
                "def Xform \"Root\"\n{\n" +
                "    def Mesh \"Sign\" (\n        prepend apiSchemas = [\"MaterialBindingAPI\"]\n    )\n    {\n" +
                "        int[] faceVertexCounts = [4]\n        int[] faceVertexIndices = [0, 1, 2, 3]\n" +
                "        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]\n" +
                "        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)] (\n            interpolation = \"vertex\"\n        )\n" +
                "        rel material:binding = </Root/Looks/MI_Overlay>\n    }\n\n" +
                "    def Scope \"Looks\"\n    {\n        def Material \"MI_Overlay\"\n        {\n" +
                "            token outputs:mdl:surface.connect = </Root/Looks/MI_Overlay/Shader.outputs:out>\n" +
                "            def Shader \"Shader\"\n            {\n" +
                "                uniform token info:implementationSource = \"sourceAsset\"\n" +
                "                uniform asset info:mdl:sourceAsset = @./M_Overlay.mdl@\n" +
                "                uniform token info:mdl:sourceAsset:subIdentifier = \"M_Overlay\"\n" +
                "                asset inputs:Text = @./Overlay_Text.png@\n" +
                "                token outputs:out\n            }\n        }\n    }\n}\n");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        // Reads the CPU copy (KeepTexturesReadable) rather than blitting on the GPU: under
        // -nographics Blit and ReadPixels do nothing, and the copy keeps Unity's default fill
        // (205, 205, 205), which is what this test read before.
        private static Color[] ReadBack(Texture texture)
        {
            var readable = texture as Texture2D;
            Assert.That(readable, Is.Not.Null, "the albedo is a Texture2D");
            Assert.That(readable.isReadable, Is.True, "the albedo keeps its CPU copy");
            Assert.That(readable.width, Is.EqualTo(64));
            Assert.That(readable.height, Is.EqualTo(64));
            return readable.GetPixels();
        }

        [Test]
        public void TheOverlayIsBakedIntoTheAlbedo()
        {
            var options = new UsdImportOptions { ImportTextures = true, AllowExternalAssetPaths = true, KeepTexturesReadable = true };
            using (ImportedStage stage = ImportedStage.Import(path, options))
            {
                Material material = stage.Find("/Root/Sign").GetComponent<Renderer>().sharedMaterial;
                Texture albedo = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.mainTexture;
                Assert.That(albedo, Is.Not.Null, "the diffuse map is read");

                Color[] pixels = ReadBack(albedo);
                Color left = pixels[32 * 64 + 8], right = pixels[32 * 64 + 56];
                Assert.That(left.grayscale, Is.GreaterThan(0.9f), "white overlay over grey gives white");
                Assert.That(right.grayscale, Is.LessThan(0.1f), "black overlay over grey gives black");
            }
        }
    }
}
