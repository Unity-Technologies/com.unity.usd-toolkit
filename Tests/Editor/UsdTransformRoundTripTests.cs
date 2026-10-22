using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Transforms that came back wrong from a write and a read: an object turned and scaled
    /// differently along its axes (Matrix4x4.rotation ignores non-uniform scale), static and
    /// animated, and a Z-up stage exported flattened or recorded, which the export used to write
    /// with its Z-up numbers under a Y-up declaration.
    /// </summary>
    public sealed class UsdTransformRoundTripTests
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
            Directory.Delete(folder, true);
        }

        [Test]
        public void ATurnedNonUniformlyScaledObjectReadsBackInPlace()
        {
            // The exported object is the stage's root prim, written without a transform of its
            // own, so the turned parent sits below it.
            GameObject scene = Track(new GameObject("Scene"));
            var parent = new GameObject("Parent");
            parent.transform.SetParent(scene.transform, false);
            parent.transform.localRotation = Quaternion.Euler(0f, 0f, 20f);
            Transform box = AddBox(parent.transform);
            box.localPosition = new Vector3(1f, 2f, 3f);
            box.localRotation = Quaternion.Euler(10f, 30f, 0f);
            box.localScale = new Vector3(1f, 2f, 0.5f);
            // A mirrored one too: its sign has to land on one axis and the rest stay a rotation.
            Transform mirrored = AddBox(parent.transform);
            mirrored.name = "Mirrored";
            mirrored.localRotation = Quaternion.Euler(0f, 45f, 15f);
            mirrored.localScale = new Vector3(-1.5f, 0.5f, 1f);
            Vector3[] drawn = Drawn(scene);

            string output = Path.Combine(folder, "turned.usda");
            UsdExporter.ExportGameObject(scene, output, new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy });
            using (ImportedStage again = ImportedStage.Import(output))
            {
                AssertSamePoints(Drawn(again.Root.gameObject), drawn, "every corner");
                Transform back = again.Find("/Scene/Parent/Box").transform;
                Assert.That(Quaternion.Angle(back.localRotation, Quaternion.Euler(10f, 30f, 0f)), Is.LessThan(0.01f),
                            "the rotation (the export and the import each mirror X, so it comes back as it was)");
                Assert.That(Vector3.Distance(back.localScale, new Vector3(1f, 2f, 0.5f)), Is.LessThan(Tolerance));
            }
        }

        [Test]
        public void ATurnedNonUniformlyScaledObjectPlaysBackInPlace()
        {
            GameObject scene = Track(new GameObject("Scene"));
            Transform box = AddBox(scene.transform);
            box.localScale = new Vector3(1f, 3f, 0.25f);
            var recorder = new UsdAnimationRecorder(scene, 24.0);
            var drawn = new List<Vector3[]>();
            for (int f = 0; f < 4; f++)
            {
                box.localRotation = Quaternion.Euler(15f * f, 40f * f, 5f * f);
                drawn.Add(Drawn(scene));
                recorder.Capture(f / 24.0);
            }

            string output = Path.Combine(folder, "spinning.usda");
            recorder.Save(output);
            using (ImportedStage again = ImportedStage.Import(output))
            {
                for (int f = 0; f < 4; f++)
                {
                    again.Result.Animation.EvaluateAtTimeCode(f);
                    AssertSamePoints(Drawn(again.Root.gameObject), drawn[f], $"frame {f}");
                }
            }
        }

        [Test]
        public void AZUpStageExportedFlattenedKeepsItsPlace()
        {
            using (ImportedStage source = ImportedStage.Import(UsdFixtures.PathOf("Skel", "blender_arm.usda")))
            {
                Assert.That(source.Result.UpAxis, Is.EqualTo("Z"), "the fixture is Z-up, as Blender writes it");
                Vector3[] drawn = Drawn(source.Root.gameObject);
                string output = Path.Combine(folder, "arm.usda");
                UsdExporter.ExportGameObject(source.Result.RootObject, output,
                                             new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy });
                using (ImportedStage again = ImportedStage.Import(output))
                {
                    Assert.That(again.Result.UpAxis, Is.EqualTo("Y"));
                    AssertSamePoints(Drawn(again.Root.gameObject), drawn, "the arm, standing as it stood");
                }
            }
        }

        [Test]
        public void AZUpStageRecordedKeepsItsPlace()
        {
            using (ImportedStage source = ImportedStage.Import(UsdFixtures.PathOf("Skel", "blender_arm.usda")))
            {
                UsdAnimationPlayer player = source.Result.Animation;
                var recorder = new UsdAnimationRecorder(source.Result.RootObject, 24.0);
                recorder.CapturePlayer(player);
                string output = Path.Combine(folder, "arm_recording.usda");
                recorder.Save(output);
                using (ImportedStage again = ImportedStage.Import(output))
                {
                    foreach (int f in new[] { 0, recorder.SampleCount / 2, recorder.SampleCount - 1 })
                    {
                        player.Evaluate(f / 24.0);
                        again.Result.Animation.EvaluateAtTimeCode(f);
                        AssertSamePoints(Drawn(again.Root.gameObject), Drawn(source.Root.gameObject), $"frame {f}");
                    }
                }
            }
        }

        private Transform AddBox(Transform parent)
        {
            var box = new GameObject("Box");
            box.transform.SetParent(parent, false);
            Mesh mesh = Track(new Mesh { name = "BoxMesh" });
            var corners = new List<Vector3>();
            for (int i = 0; i < 8; i++)
            {
                corners.Add(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f));
            }

            mesh.SetVertices(corners);
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            mesh.RecalculateNormals();
            box.AddComponent<MeshFilter>().sharedMesh = mesh;
            box.AddComponent<MeshRenderer>();
            return box.transform;
        }

        // Every vertex every renderer below `root` draws, in world space: baked for skinned
        // renderers, from the vertex stream for a point cache.
        private static Vector3[] Drawn(GameObject root)
        {
            var points = new List<Vector3>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                var baked = new Mesh();
                try
                {
                    Mesh mesh;
                    if (renderer is SkinnedMeshRenderer skinned)
                    {
                        skinned.BakeMesh(baked, true);
                        mesh = baked;
                    }
                    else if (renderer.TryGetComponent(out UsdDualQuaternionSkin dualQuaternion))
                    {
                        dualQuaternion.BakeMesh(baked);
                        mesh = baked;
                    }
                    else
                    {
                        Mesh stream = ((MeshRenderer)renderer).additionalVertexStreams;
                        Mesh shared = renderer.GetComponent<MeshFilter>().sharedMesh;
                        mesh = stream != null && stream.vertexCount == shared.vertexCount ? stream : shared;
                    }

                    points.AddRange(mesh.vertices.Select(renderer.transform.TransformPoint));
                }
                finally
                {
                    Object.DestroyImmediate(baked);
                }
            }

            return points.ToArray();
        }

        // The same points in any order: the importer may lay vertices out differently.
        private static void AssertSamePoints(Vector3[] actual, Vector3[] expected, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            foreach (Vector3 point in expected)
            {
                float nearest = actual.Min(p => Vector3.Distance(p, point));
                Assert.That(nearest, Is.LessThan(Tolerance), $"{what}: nothing drawn within {Tolerance} of {point} (nearest {nearest})");
            }
        }

        private T Track<T>(T item) where T : Object
        {
            created.Add(item);
            return item;
        }
    }
}
