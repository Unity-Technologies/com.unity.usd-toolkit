using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A Preserve (override) export of an imported stage writes the skinned meshes added to it: a
    /// new character as its own SkelRoot and Skeleton, a mesh added to an imported character bound
    /// to that character's Skeleton prim, and anything that fits neither in its current pose.
    /// Read back over the source, each draws what the scene drew.
    /// </summary>
    public sealed class UsdPreserveSkinnedExportTests
    {
        private const float Tolerance = 1e-4f;
        private readonly List<Object> created = new List<Object>();
        private string folder;

        [SetUp]
        public void CreateFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "UsdToolkitTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void DeleteFolder()
        {
            foreach (Object item in created)
            {
                if (item != null)
                {
                    Object.DestroyImmediate(item);
                }
            }

            created.Clear();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void ANewCharacterIsWrittenAsItsOwnSkelRoot()
        {
            string output = Path.Combine(folder, "added.usda");
            Vector3[] drawn;
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                SkinnedMeshRenderer body = BuildRig(stage.Find("/Root").transform, out Transform hips, out Transform spine);
                spine.localRotation = Quaternion.Euler(0f, 0f, 35f);
                hips.localPosition += new Vector3(0f, 0.25f, 0f);
                drawn = RootLocal(body, stage.Root);
                Export(stage, output);
            }

            string layer = File.ReadAllText(output);
            Assert.That(layer, Does.Not.Contain("\"Shoulder\""), "the imported joints are not prims of the layer");
            Assert.That(layer, Does.Contain("def SkelRoot \"Rig\""));
            Assert.That(layer, Does.Contain("def Skeleton \"Skeleton\""));
            using (ImportedStage again = ImportedStage.Import(output))
            {
                var body = again.Find("/Root/Rig/Body/Body_Mesh").GetComponent<SkinnedMeshRenderer>();
                Assert.That(body, Is.Not.Null, "the new mesh is skinned");
                AssertSame(RootLocal(body, again.Root), drawn, "the new character");
                Assert.That(again.Find("/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>(), Is.Not.Null,
                            "the source's own character is still there");
            }
        }

        [Test]
        public void AMeshAddedToAnImportedCharacterBindsToItsSkeleton()
        {
            string output = Path.Combine(folder, "sleeve.usda");
            Vector3[] drawn;
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                // Bound in the read-time pose, which for this stage is the bind pose.
                UsdSkeleton skeleton = stage.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>();
                SkinnedMeshRenderer sleeve = AddSleeve(stage.Find("/Root/Arm").transform, skeleton);
                stage.Result.Animation.EvaluateAtTimeCode(5.0);
                drawn = RootLocal(sleeve, stage.Root);
                Export(stage, output);
            }

            string layer = File.ReadAllText(output);
            Assert.That(layer, Does.Not.Contain("def Skeleton"), "the source's Skeleton is not written again");
            Assert.That(layer, Does.Contain("rel skel:skeleton = </Root/Arm/Skel>"));
            using (ImportedStage again = ImportedStage.Import(output))
            {
                var sleeve = again.Find("/Root/Arm/Sleeve/Sleeve_Mesh").GetComponent<SkinnedMeshRenderer>();
                Assert.That(sleeve, Is.Not.Null, "the sleeve is skinned");
                Assert.That(sleeve.bones, Is.EqualTo(again.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>().Joints),
                            "to the imported character's joints");
                again.Result.Animation.EvaluateAtTimeCode(5.0);
                AssertSame(RootLocal(sleeve, again.Root), drawn, "the sleeve at t=5");
            }
        }

        [Test]
        public void BonesThatMeetOnlyAtAnImportedPrimAreBaked()
        {
            string output = Path.Combine(folder, "split.usda");
            Vector3[] drawn;
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                Transform root = stage.Find("/Root").transform;
                SkinnedMeshRenderer body = BuildRig(root, out Transform hips, out Transform _);
                // The renderer moves out of the rig: renderer and bones now meet only at /Root.
                body.transform.SetParent(root, true);
                drawn = RootLocal(body, stage.Root);
                LogAssert.Expect(LogType.Warning, new Regex("'Body' is exported as a static mesh in its current pose: .*meet at 'Root'"));
                Export(stage, output);
            }

            using (ImportedStage again = ImportedStage.Import(output))
            {
                GameObject body = again.Find("/Root/Body/Body_Mesh");
                Assert.That(body.GetComponent<SkinnedMeshRenderer>(), Is.Null);
                Mesh mesh = body.GetComponent<MeshFilter>().sharedMesh;
                var vertices = new Vector3[mesh.vertexCount];
                for (int v = 0; v < vertices.Length; v++)
                {
                    vertices[v] = again.Root.InverseTransformPoint(body.transform.TransformPoint(mesh.vertices[v]));
                }

                AssertSame(vertices, drawn, "the baked pose");
            }
        }

        private static void Export(ImportedStage stage, string output)
        {
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
                stage.Result.RootObject, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve });
            Assert.That(File.Exists(output), Is.True, result?.ToString());
        }

        // Two bones up a strip of six points, the middle pair weighted to both.
        private SkinnedMeshRenderer BuildRig(Transform parent, out Transform hips, out Transform spine)
        {
            var rig = new GameObject("Rig");
            rig.transform.SetParent(parent, false);
            rig.transform.localPosition = new Vector3(0f, 0f, 3f);
            hips = new GameObject("Hips").transform;
            hips.SetParent(rig.transform, false);
            hips.localPosition = new Vector3(0f, 1f, 0f);
            spine = new GameObject("Spine").transform;
            spine.SetParent(hips, false);
            spine.localPosition = new Vector3(0f, 1f, 0f);

            var body = new GameObject("Body");
            body.transform.SetParent(rig.transform, false);
            body.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            SkinnedMeshRenderer renderer = body.AddComponent<SkinnedMeshRenderer>();
            Mesh mesh = Track(new Mesh { name = "BodyMesh" });
            mesh.vertices = new[]
            {
                new Vector3(0f, 0.5f, 0f), new Vector3(0.25f, 0.5f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0.25f, 1f, 0f),
                new Vector3(0f, 1.5f, 0f), new Vector3(0.25f, 1.5f, 0f),
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5 };
            mesh.RecalculateNormals();
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }, new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 1, weight0 = 1f }, new BoneWeight { boneIndex0 = 1, weight0 = 1f },
            };
            mesh.bindposes = new[]
            {
                hips.worldToLocalMatrix * body.transform.localToWorldMatrix,
                spine.worldToLocalMatrix * body.transform.localToWorldMatrix,
            };
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { hips, spine };
            renderer.rootBone = hips;
            return renderer;
        }

        // A sleeve around the imported arm's elbow, weighted to its Elbow and Wrist joints.
        private SkinnedMeshRenderer AddSleeve(Transform skelRoot, UsdSkeleton skeleton)
        {
            var holder = new GameObject("Sleeve");
            holder.transform.SetParent(skelRoot, false);
            SkinnedMeshRenderer renderer = holder.AddComponent<SkinnedMeshRenderer>();
            Transform elbow = skeleton.Joints[1];
            Transform wrist = skeleton.Joints[2];
            Mesh mesh = Track(new Mesh { name = "SleeveMesh" });
            var vertices = new List<Vector3>();
            foreach (float height in new[] { 1.2f, 1.6f, 2.0f })
            {
                vertices.Add(holder.transform.InverseTransformPoint(skeleton.transform.TransformPoint(new Vector3(-0.3f, height, 0f))));
                vertices.Add(holder.transform.InverseTransformPoint(skeleton.transform.TransformPoint(new Vector3(0.3f, height, 0f))));
            }

            mesh.SetVertices(vertices);
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5 };
            mesh.RecalculateNormals();
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }, new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 1, weight0 = 1f }, new BoneWeight { boneIndex0 = 1, weight0 = 1f },
            };
            mesh.bindposes = new[]
            {
                elbow.worldToLocalMatrix * holder.transform.localToWorldMatrix,
                wrist.worldToLocalMatrix * holder.transform.localToWorldMatrix,
            };
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { elbow, wrist };
            renderer.rootBone = skeleton.transform;
            return renderer;
        }

        private T Track<T>(T item) where T : Object
        {
            created.Add(item);
            return item;
        }

        private static Vector3[] RootLocal(SkinnedMeshRenderer renderer, Transform root)
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked, true);
                Vector3[] vertices = baked.vertices;
                for (int v = 0; v < vertices.Length; v++)
                {
                    vertices[v] = root.InverseTransformPoint(renderer.transform.TransformPoint(vertices[v]));
                }

                return vertices;
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        private static void AssertSame(Vector3[] actual, Vector3[] expected, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            for (int v = 0; v < actual.Length; v++)
            {
                Assert.That(Vector3.Distance(actual[v], expected[v]), Is.LessThan(Tolerance),
                            $"{what}: vertex {v} at {actual[v]} instead of {expected[v]}");
            }
        }
    }
}
