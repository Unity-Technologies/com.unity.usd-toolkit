using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A roughness map that is a JPEG still reaches the packed metallic+smoothness texture. JPEGs
    /// are decoded by LoadImage on the main thread, after the decode pass that packs, so a JPEG
    /// source used to leave the material at its constants (OpenChessSet: every metalness and
    /// roughness map is a greyscale JPEG, 2026-10-20). URP multiplies _Smoothness into the
    /// packed alpha, so with a map bound it must be 1, not the authored fallback.
    /// The stage is one quad whose UsdPreviewSurface roughness reads a uniform 0.25 JPEG.
    /// </summary>
    public sealed class UsdJpegPackedMapTests
    {
        private string directory;
        private string path;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var texture = new Texture2D(64, 64, TextureFormat.RGB24, false, true);
            var pixels = new Color32[64 * 64];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(64, 64, 64, 255);
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(Path.Combine(directory, "roughness.jpg"), texture.EncodeToJPG(100));
            UnityEngine.Object.DestroyImmediate(texture);

            path = Path.Combine(directory, "jpeg_roughness.usda");
            File.WriteAllText(path,
                "#usda 1.0\n(\n    defaultPrim = \"Root\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n)\n\n" +
                "def Xform \"Root\"\n{\n" +
                "    def Mesh \"Quad\" (\n        prepend apiSchemas = [\"MaterialBindingAPI\"]\n    )\n    {\n" +
                "        int[] faceVertexCounts = [4]\n        int[] faceVertexIndices = [0, 1, 2, 3]\n" +
                "        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]\n" +
                "        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)] (\n            interpolation = \"vertex\"\n        )\n" +
                "        rel material:binding = </Root/Looks/Rough>\n    }\n\n" +
                "    def Scope \"Looks\"\n    {\n        def Material \"Rough\"\n        {\n" +
                "            token outputs:surface.connect = </Root/Looks/Rough/Surface.outputs:surface>\n" +
                "            def Shader \"Surface\"\n            {\n" +
                "                uniform token info:id = \"UsdPreviewSurface\"\n" +
                "                float inputs:roughness = 0.5\n" +
                "                float inputs:roughness.connect = </Root/Looks/Rough/RoughTex.outputs:r>\n" +
                "                token outputs:surface\n            }\n" +
                "            def Shader \"RoughTex\"\n            {\n" +
                "                uniform token info:id = \"UsdUVTexture\"\n" +
                "                asset inputs:file = @./roughness.jpg@\n" +
                "                token inputs:sourceColorSpace = \"raw\"\n" +
                "                float outputs:r\n            }\n" +
                "        }\n    }\n" +
                "}\n");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void AJpegRoughnessMapIsPackedIntoSmoothness()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Material material = stage.Find("/Root/Quad").GetComponent<Renderer>().sharedMaterial;
                Texture packed = material.GetTexture("_MetallicGlossMap");
                Assert.That(packed, Is.Not.Null, "the JPEG roughness map is packed into the metallic slot");
                // URP Lit scales the packed alpha by _Smoothness; Built-in Standard has no
                // _Smoothness and scales it by _GlossMapScale (default 1).
                string scale = material.HasProperty("_Smoothness") ? "_Smoothness" : "_GlossMapScale";
                Assert.That(material.HasProperty(scale), Is.True, $"{material.shader.name} has {scale}");
                Assert.That(material.GetFloat(scale), Is.EqualTo(1f),
                            $"{scale} scales the packed alpha, so it is 1 with a map bound");

                // The packed alpha is read back through the GPU; without a device (-nographics)
                // the binding above is what can be checked.
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    return;
                }

                var target = RenderTexture.GetTemporary(packed.width, packed.height, 0, RenderTextureFormat.ARGB32,
                                                        RenderTextureReadWrite.Linear);
                Graphics.Blit(packed, target);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                var read = new Texture2D(packed.width, packed.height, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, packed.width, packed.height), 0, 0);
                read.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Color centre = read.GetPixel(packed.width / 2, packed.height / 2);
                UnityEngine.Object.DestroyImmediate(read);

                Assert.That(centre.a, Is.EqualTo(1f - 64f / 255f).Within(0.03f), "smoothness = 1 - roughness 0.25");
                Assert.That(centre.r, Is.EqualTo(0f).Within(0.03f), "metallic keeps its constant 0");
            }
        }
    }
}
