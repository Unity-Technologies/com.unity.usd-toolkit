using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A point instancer whose prototype is a skinned character: the prototype keeps its skeleton,
    /// which the stage's animation poses, and every instance draws that pose where OpenUSD places
    /// it (instanced_arm.expected.json, from make_instanced.py). Prototypes are drawn only through
    /// their instances.
    /// </summary>
    public sealed class UsdPointInstancerSkinningTests
    {
        private const float Tolerance = 1e-4f;

        [Serializable]
        private sealed class ExpectedInstances
        {
#pragma warning disable 0649
            public int instances;
            public float[] restPoints;
            public ExpectedPointsFrame[] frames;
#pragma warning restore 0649
        }

        [Test]
        public void EveryInstanceDrawsThePrototypesPose()
        {
            var expected = JsonUtility.FromJson<ExpectedInstances>(
                File.ReadAllText(UsdFixtures.PathOf("Skel", "instanced_arm.expected.json")));
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "instanced_arm.usda")))
            {
                UsdPointInstancer[] drawers = stage.Root.GetComponentsInChildren<UsdPointInstancer>(true);
                Assert.That(drawers.Length, Is.EqualTo(2), "one drawer per prototype mesh");
                UsdPointInstancer arm = drawers.Single(d => d.SkinnedSource != null);
                UsdPointInstancer box = drawers.Single(d => d.SkinnedSource == null);
                Assert.That(arm.InstanceCount, Is.EqualTo(expected.instances));
                Assert.That(box.InstanceCount, Is.EqualTo(1));

                var skinned = arm.SkinnedSource as SkinnedMeshRenderer;
                Assert.That(skinned, Is.Not.Null, "the prototype is skinned");
                Assert.That(skinned.enabled, Is.False, "the prototype is not drawn where it stands");
                foreach (Renderer renderer in stage.Root.GetComponentsInChildren<Renderer>(true))
                {
                    Assert.That(renderer.enabled, Is.False, $"'{renderer.name}' is a prototype, drawn only by its instances");
                }

                int[] pointOf = UsdFixtures.PointOfEachVertex(skinned.sharedMesh.vertices, expected.restPoints);
                int points = expected.restPoints.Length / 3;
                foreach (ExpectedPointsFrame frame in expected.frames)
                {
                    stage.Result.Animation.EvaluateAtTimeCode(frame.time);
                    var baked = new Mesh();
                    try
                    {
                        // What the drawer draws: the prototype baked in its own space, at every placement.
                        skinned.BakeMesh(baked, true);
                        Vector3[] vertices = baked.vertices;
                        Matrix4x4 toWorld = arm.transform.localToWorldMatrix;
                        for (int i = 0; i < arm.InstanceCount; i++)
                        {
                            Matrix4x4 placement = toWorld * arm.Instances[i];
                            for (int v = 0; v < vertices.Length; v++)
                            {
                                Vector3 actual = placement.MultiplyPoint3x4(vertices[v]);
                                Vector3 wanted = UsdFixtures.StageToWorld(stage.Root,
                                    UsdFixtures.Point(frame.points, i * points + pointOf[v]));
                                Assert.That(Vector3.Distance(actual, wanted), Is.LessThan(Tolerance),
                                            $"instance {i}, vertex {v} at t={frame.time}");
                            }
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(baked);
                    }
                }
            }
        }
    }
}
