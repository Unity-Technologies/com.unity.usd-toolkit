using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A mesh without authored normals gets smooth normals whatever its triangle size, and every
    /// imported mesh with UVs gets tangents for its normal map. Vector3.normalized returns zero
    /// below a length of 1e-5, and a face normal's length is twice the triangle's area, so a
    /// 1 mm quad used to come out with zero normals (openchessset pieces, 2026-10-20). The stage
    /// is one 1 mm quad with UVs and no normals.
    /// </summary>
    public sealed class UsdSmallTriangleNormalsTests
    {
        private string directory;
        private string path;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "small_quad.usda");
            File.WriteAllText(path,
                "#usda 1.0\n(\n    defaultPrim = \"Root\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n)\n\n" +
                "def Xform \"Root\"\n{\n" +
                "    def Mesh \"Small\"\n    {\n" +
                "        int[] faceVertexCounts = [4]\n" +
                "        int[] faceVertexIndices = [0, 1, 2, 3]\n" +
                "        point3f[] points = [(0, 0, 0), (0.001, 0, 0), (0.001, 0.001, 0), (0, 0.001, 0)]\n" +
                "        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)] (\n            interpolation = \"vertex\"\n        )\n" +
                "        uniform token subdivisionScheme = \"none\"\n" +
                "    }\n" +
                "}\n");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void AMillimetreQuadGetsUnitNormals()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Mesh mesh = stage.Find("/Root/Small").GetComponent<MeshFilter>().sharedMesh;
                foreach (Vector3 normal in mesh.normals)
                {
                    Assert.That(normal.magnitude, Is.EqualTo(1f).Within(1e-3f), "normal " + normal);
                }
            }
        }

        [Test]
        public void AMeshWithUVsGetsTangents()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Mesh mesh = stage.Find("/Root/Small").GetComponent<MeshFilter>().sharedMesh;
                Vector4[] tangents = mesh.tangents;
                Assert.That(tangents.Length, Is.EqualTo(mesh.vertexCount));
                foreach (Vector4 tangent in tangents)
                {
                    Assert.That(new Vector3(tangent.x, tangent.y, tangent.z).magnitude, Is.EqualTo(1f).Within(1e-3f), "tangent " + tangent);
                    Assert.That(Mathf.Abs(tangent.w), Is.EqualTo(1f));
                }
            }
        }
    }
}
