using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdSkel skinning on import: skeletons become joint hierarchies, skinned meshes become
    /// SkinnedMeshRenderers, and what Unity draws matches OpenUSD's own skinning of the same stage.
    /// </summary>
    public sealed class UsdSkinningTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly object[] Fixtures =
        {
            new object[] { "skel_arm", 0.0 },
            new object[] { "blender_arm", 1.0 }, // Blender's stage starts at frame 1
        };

        [Test]
        public void SkinnedMeshesAreBoundToTheirSkeletonsJoints()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdSkeleton skeleton = stage.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>();
                Assert.That(skeleton, Is.Not.Null, "UsdSkeleton on the Skeleton prim's object");
                Assert.That(skeleton.JointPaths, Is.EqualTo(new[] { "Shoulder", "Shoulder/Elbow", "Shoulder/Elbow/Wrist" }));
                Assert.That(skeleton.Joints.Select(j => j.name), Is.EqualTo(new[] { "Shoulder", "Elbow", "Wrist" }));
                Assert.That(skeleton.Joints[0].parent, Is.EqualTo(skeleton.transform));
                Assert.That(skeleton.Joints[1].parent, Is.EqualTo(skeleton.Joints[0]));
                Assert.That(skeleton.Joints[2].parent, Is.EqualTo(skeleton.Joints[1]));

                foreach (string path in new[] { "/Root/Arm/Strip", "/Root/Arm/Hand" })
                {
                    GameObject meshObject = stage.Find(path);
                    var skinned = meshObject.GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skinned, Is.Not.Null, path);
                    Assert.That(meshObject.GetComponent<MeshFilter>(), Is.Null, $"{path} has no MeshFilter");
                    Assert.That(skinned.bones, Is.EqualTo(skeleton.Joints.ToArray()), $"{path} bones");
                    Assert.That(skinned.rootBone, Is.EqualTo(skeleton.transform), $"{path} root bone");
                    Assert.That(skinned.sharedMesh.bindposes.Length, Is.EqualTo(3), $"{path} bind poses");
                    Assert.That(skinned.sharedMaterial, Is.Not.Null, $"{path} material");
                }

                Mesh strip = stage.Find("/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>().sharedMesh;
                Assert.That(strip.vertexCount, Is.EqualTo(12), "Strip after the vertex split");
                Assert.That(strip.GetBonesPerVertex().ToArray().Max(), Is.EqualTo(2), "two influences at most");

                Mesh hand = stage.Find("/Root/Arm/Hand").GetComponent<SkinnedMeshRenderer>().sharedMesh;
                Assert.That(hand.GetAllBoneWeights().ToArray().All(w => w.boneIndex == 2 && w.weight == 1f),
                            "Hand is rigidly bound to the Wrist");
            }
        }

        [Test]
        public void BlendShapesKeepTheirNameFramesAndReadTimeWeight()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                var skinned = stage.Find("/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>();
                Mesh mesh = skinned.sharedMesh;
                Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("Bulge"));
                Assert.That(mesh.GetBlendShapeFrameCount(0), Is.EqualTo(2), "the inbetween and the full shape");
                Assert.That(mesh.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(50f).Within(1e-4f));
                Assert.That(mesh.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(100f).Within(1e-4f));
                Assert.That(skinned.GetBlendShapeWeight(0), Is.EqualTo(0f), "weight 0 at the read time (t=0)");
            }
        }

        // The whole chain at once: bind transforms, geomBindTransform, the joint hierarchy, the
        // SkelRoot's own transform, the X flip and, for Blender's stage, the Z-up correction.
        [TestCaseSource(nameof(Fixtures))]
        public void SkinnedPointsAtTheReadTimeMatchOpenUsd(string fixture, double readTime)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", fixture);
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", fixture + ".usda")))
            {
                foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                {
                    var skinned = stage.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skinned, Is.Not.Null, mesh.path);
                    AssertSkinnedPoints(stage, skinned, mesh, UsdFixtures.FrameAt(mesh, readTime));
                }
            }
        }

        [Test]
        public void JointsAreNotRecordedAsPrims()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdSkeleton skeleton = stage.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>();
                foreach (Transform joint in skeleton.Joints)
                {
                    Assert.That(joint.GetComponent<UsdPrimSource>(), Is.Null, joint.name);
                }

                Assert.That(skeleton.Joints[0].GetComponent<UsdImportHelperObject>(), Is.Not.Null,
                            "the root joint is an import helper, so exporters skip the hierarchy");
                Assert.That(stage.Result.Source.PrimPaths.Any(p => p.Contains("Shoulder")), Is.False);
            }
        }

        [Test]
        public void WithoutImportSkinningMeshesStayStatic()
        {
            var options = new UsdImportOptions { ImportSkinning = false, DoubleSidedOpenMeshes = false };
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda"), options))
            {
                GameObject strip = stage.Find("/Root/Arm/Strip");
                Assert.That(strip.GetComponent<SkinnedMeshRenderer>(), Is.Null);
                Assert.That(strip.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(12));
                Assert.That(stage.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>(), Is.Null);
                Assert.That(stage.Find("/Root/Arm/Skel").transform.childCount, Is.Zero, "no joints");
            }
        }

        [Test]
        public void StaticValuesAreReadAtTheStageStartTime()
        {
            // Spinner authors its translate only as samples: (0, 0, 2) at the start time.
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                Assert.That(Vector3.Distance(stage.Find("/Root/Spinner").transform.localPosition, new Vector3(0f, 0f, 2f)),
                            Is.LessThan(Tolerance));
                Assert.That(stage.Find("/Root/PointCache").GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(3),
                            "a mesh with only sampled points imports its first frame");
            }

            // Read at Default, as before API 13, samples are ignored.
            var options = new UsdImportOptions { ReadAtStageStartTime = false };
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda"), options))
            {
                Assert.That(stage.Find("/Root/Spinner").transform.localPosition, Is.EqualTo(Vector3.zero));
                GameObject pointCache = stage.Find("/Root/PointCache");
                Assert.That(pointCache == null || pointCache.GetComponent<MeshFilter>() == null, Is.True,
                            "no geometry at Default");
            }
        }

        // Compares the renderer's skinned vertices, in world space, with OpenUSD's skinned points.
        internal static void AssertSkinnedPoints(ImportedStage stage, SkinnedMeshRenderer skinned,
                                                 ExpectedSkinnedMesh mesh, ExpectedPointsFrame frame) =>
            AssertSkinnedPoints(stage.Root, skinned, mesh, frame);

        internal static void AssertSkinnedPoints(Transform importRoot, SkinnedMeshRenderer skinned,
                                                 ExpectedSkinnedMesh mesh, ExpectedPointsFrame frame)
        {
            int[] pointOf = UsdFixtures.PointOfEachVertex(skinned.sharedMesh.vertices, mesh.restPoints);
            var baked = new Mesh();
            try
            {
                // With useScale, baked vertices are in the renderer's local space, scale included.
                skinned.BakeMesh(baked, true);
                Vector3[] vertices = baked.vertices;
                float worst = 0f;
                int worstVertex = -1;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 actual = skinned.transform.TransformPoint(vertices[v]);
                    Vector3 wanted = UsdFixtures.StageToWorld(importRoot, UsdFixtures.Point(frame.points, pointOf[v]));
                    float error = Vector3.Distance(actual, wanted);
                    if (error > worst)
                    {
                        worst = error;
                        worstVertex = v;
                    }
                }

                Assert.That(worst, Is.LessThan(Tolerance),
                            $"{mesh.path} at t={frame.time}: vertex {worstVertex} (USD point {(worstVertex >= 0 ? pointOf[worstVertex] : -1)}) is off by {worst}");
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }
    }
}
