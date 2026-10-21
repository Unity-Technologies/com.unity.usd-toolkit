using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdGeomGprim.doubleSided: a doubleSided mesh is drawn from both sides, as Isaac Sim and
    /// Omniverse draw it. The importer bakes the back faces in (mirrored vertices with negated
    /// normals, triangles wound the other way) so each side shades with a normal that faces it;
    /// a one-sided mesh with the same buffers keeps its own, unbaked, Mesh. The stage is two
    /// identical quads, one of them doubleSided.
    /// </summary>
    public sealed class UsdDoubleSidedImportTests
    {
        private const string Quad =
            "        int[] faceVertexCounts = [4]\n" +
            "        int[] faceVertexIndices = [0, 1, 2, 3]\n" +
            "        normal3f[] normals = [(0, 0, 1), (0, 0, 1), (0, 0, 1), (0, 0, 1)] (\n            interpolation = \"vertex\"\n        )\n" +
            "        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]\n" +
            "        uniform token subdivisionScheme = \"none\"\n";

        private string directory;
        private string path;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "double_sided.usda");
            File.WriteAllText(path,
                "#usda 1.0\n(\n    defaultPrim = \"Root\"\n    metersPerUnit = 1\n    upAxis = \"Y\"\n)\n\n" +
                "def Xform \"Root\"\n{\n" +
                "    def Mesh \"Both\"\n    {\n        uniform bool doubleSided = 1\n" + Quad + "    }\n\n" +
                "    def Mesh \"Front\"\n    {\n" + Quad + "    }\n" +
                "}\n");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void ADoubleSidedMeshGetsBackFacesWithFlippedNormals()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Mesh both = stage.Find("/Root/Both").GetComponent<MeshFilter>().sharedMesh;
                Mesh front = stage.Find("/Root/Front").GetComponent<MeshFilter>().sharedMesh;

                Assert.That(front.vertexCount, Is.EqualTo(4));
                Assert.That(front.triangles.Length, Is.EqualTo(6));
                Assert.That(both, Is.Not.SameAs(front), "a one-sided mesh must not share the baked Mesh");
                Assert.That(both.vertexCount, Is.EqualTo(8));
                Assert.That(both.triangles.Length, Is.EqualTo(12));

                Vector3[] vertices = both.vertices;
                Vector3[] normals = both.normals;
                int[] triangles = both.triangles;
                for (int i = 0; i < 4; i++)
                {
                    Assert.That(vertices[i + 4], Is.EqualTo(vertices[i]));
                    Assert.That(normals[i + 4], Is.EqualTo(-normals[i]));
                }

                // Each back triangle faces the other way: its geometric normal is the opposite of
                // the matching front triangle's.
                for (int t = 0; t < 2; t++)
                {
                    Vector3 frontNormal = Face(vertices, triangles, t * 3);
                    Vector3 backNormal = Face(vertices, triangles, 6 + (t * 3));
                    Assert.That(Vector3.Dot(frontNormal, backNormal), Is.LessThan(-0.99f));
                }
            }
        }

        [Test]
        public void BakedBackFacesKeepTheMaterialOneSided()
        {
            using (ImportedStage stage = ImportedStage.Import(path))
            {
                Material material = stage.Find("/Root/Both").GetComponent<Renderer>().sharedMaterial;
                Assert.That(material, Is.Not.Null);
                if (material.HasProperty("_Cull"))
                {
                    Assert.That(material.GetFloat("_Cull"), Is.Not.EqualTo(0f));
                }
            }
        }

        [Test]
        public void ByDefaultAnOpenMeshAlsoGetsBackFaces()
        {
            // Isaac Sim draws every mesh from both sides, so the default options give an open
            // mesh back faces even without doubleSided; ImportedStage.Import without options
            // turns that off for the fixtures that count vertices.
            using (ImportedStage stage = ImportedStage.Import(path, new UsdImportOptions()))
            {
                Mesh front = stage.Find("/Root/Front").GetComponent<MeshFilter>().sharedMesh;
                Assert.That(front.vertexCount, Is.EqualTo(8));
                Assert.That(front.triangles.Length, Is.EqualTo(12));
            }
        }

        private static Vector3 Face(Vector3[] vertices, int[] triangles, int start)
        {
            Vector3 a = vertices[triangles[start]];
            Vector3 b = vertices[triangles[start + 1]];
            Vector3 c = vertices[triangles[start + 2]];
            return Vector3.Cross(b - a, c - a).normalized;
        }
    }
}
