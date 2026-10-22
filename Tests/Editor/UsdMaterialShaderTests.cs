using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Imported materials use the active render pipeline's lit shader. A Built-in project can have
    /// URP installed, and URP's shaders draw nothing there.
    /// </summary>
    public sealed class UsdMaterialShaderTests
    {
        private const string Stage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Mesh ""Quad""
    {
        int[] faceVertexCounts = [4]
        int[] faceVertexIndices = [0, 1, 2, 3]
        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
    }
}
";

        [Test]
        public void ImportedMaterialsUseTheActivePipelinesShader()
        {
            string directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "quad.usda");
                File.WriteAllText(path, Stage);
                using (ImportedStage stage = ImportedStage.Import(path))
                {
                    Shader shader = stage.Find("/Root/Quad").GetComponent<MeshRenderer>().sharedMaterial.shader;
                    RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
                    string expected = pipeline == null ? "Standard"
                        : pipeline.GetType().Name.Contains("Universal") ? "Universal Render Pipeline/Lit"
                        : "HDRP/Lit";
                    Assert.That(shader.name, Is.EqualTo(expected), "pipeline: " + (pipeline == null ? "Built-in" : pipeline.GetType().Name));
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
