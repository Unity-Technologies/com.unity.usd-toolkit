using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdLayerInfo (RUsd_InspectLayer, API 18), the convex pieces a convexDecomposition collider
    /// is cut into, and UsdImportResult.PhysicsMapping.
    /// </summary>
    public sealed class UsdLayerInfoAndMappingTests
    {
        private GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---- UsdLayerInfo ----

        [Test]
        public void TheReportParses()
        {
            UsdLayerInfo info = UsdLayerInfo.Parse(
                "S\t./sub.usda\nR\t./ref.usda\nR\t./tex.png\nP\t./a.usda\nP\t./b.usda\n" +
                "M\tupAxis\tZ\nM\tmetersPerUnit\t0.01\nM\tkilogramsPerUnit\t1\nM\tdefaultPrim\troot\n" +
                "C\tSimReady_Metadata:mass\t0.71\nC\tSimReady_Metadata:asset_extents\t(0.1, 0.2, 0.3)\nbogus\n");
            Assert.That(info.SubLayers, Is.EqualTo(new[] { "./sub.usda" }));
            Assert.That(info.References, Is.EqualTo(new[] { "./ref.usda", "./tex.png" }));
            Assert.That(info.Payloads, Is.EqualTo(new[] { "./a.usda", "./b.usda" }));
            Assert.That(info.AllAssetPaths.Count, Is.EqualTo(5));
            Assert.That(info.UpAxis, Is.EqualTo("Z"));
            Assert.That(info.MetersPerUnit, Is.EqualTo(0.01).Within(1e-12));
            Assert.That(info.KilogramsPerUnit, Is.EqualTo(1.0));
            Assert.That(info.DefaultPrim, Is.EqualTo("root"));
            Assert.That(info.CustomLayerData["SimReady_Metadata:asset_extents"], Is.EqualTo("(0.1, 0.2, 0.3)"));
        }

        [Test]
        public void ALayerIsReadWithoutComposingIt()
        {
            Assume.That(UsdLayerInfo.IsSupported, "needs the 0.8.2 native plugin (API 18)");
            UsdLayerInfo info = UsdLayerInfo.Read(UsdFixtures.PathOf("Physics", "isaac_style_arm.usda"));
            Assert.That(info.UpAxis, Is.Not.Empty);
            Assert.That(info.MetersPerUnit, Is.Not.Null);
        }

        [Test]
        public void AMissingLayerIsAStageError()
        {
            Assume.That(UsdLayerInfo.IsSupported, "needs the 0.8.2 native plugin (API 18)");
            Assert.Throws<UsdStageException>(() => UsdLayerInfo.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no_such_layer.usda")));
        }

        // ---- Convex pieces ----

        private static Mesh Boxes(params Bounds[] boxes)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (Bounds box in boxes)
            {
                int b = vertices.Count;
                Vector3 min = box.min;
                Vector3 max = box.max;
                for (int i = 0; i < 8; i++)
                {
                    vertices.Add(new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
                }

                int[] quads = { 0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5 };
                for (int q = 0; q < quads.Length; q += 4)
                {
                    triangles.AddRange(new[] { b + quads[q], b + quads[q + 1], b + quads[q + 2], b + quads[q], b + quads[q + 2], b + quads[q + 3] });
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        [Test]
        public void SeparatePartsBecomeSeparateHulls()
        {
            Mesh mesh = Boxes(new Bounds(Vector3.zero, Vector3.one), new Bounds(new Vector3(3f, 0f, 0f), Vector3.one));
            List<Mesh> pieces = UsdConvexPieces.Split(mesh);
            Assert.That(pieces.Count, Is.EqualTo(2));
            Assert.That(pieces.All(p => p.vertexCount == 8), Is.True);
            Assert.That(UsdConvexPieces.Volume(mesh), Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void ASolidPartStaysOneHull()
        {
            Assert.That(UsdConvexPieces.Split(Boxes(new Bounds(Vector3.zero, new Vector3(1f, 2f, 3f)))).Count, Is.EqualTo(1));
        }

        [Test]
        public void ManyPartsAreCappedAtTheMaximum()
        {
            var boxes = Enumerable.Range(0, 12).Select(i => new Bounds(new Vector3(i * 2f, 0f, 0f), Vector3.one)).ToArray();
            Assert.That(UsdConvexPieces.Split(Boxes(boxes)).Count, Is.EqualTo(UsdConvexPieces.DefaultMaxPieces));
        }

        // ---- The mapping log ----

        [Test]
        public void EveryPhysicsPrimHasAMappingEntry()
        {
            Assume.That(UsdExporter.GetRuntimeInfo().NativeApiVersion, Is.GreaterThanOrEqualTo(13), "needs the physics import (API 13)");
            UsdImportResult result = UsdImporter.Import(UsdFixtures.PathOf("Physics", "isaac_style_arm.usda"), new UsdImportOptions
            {
                ImportPhysics = true,
                ImportMaterials = false,
                InteriorReflection = false,
            });
            root = result.RootObject;
            Assert.That(result.PhysicsMapping, Is.Not.Empty);
            Assert.That(result.PhysicsMapping.Count(e => e.Kind == UsdPhysicsPrimKind.Body), Is.EqualTo(root.GetComponentsInChildren<UsdPhysicsBody>().Length));
            Assert.That(result.PhysicsMapping.Count(e => e.Kind == UsdPhysicsPrimKind.Joint), Is.GreaterThanOrEqualTo(root.GetComponentsInChildren<UsdPhysicsJoint>().Length));
            foreach (UsdPhysicsMappingEntry entry in result.PhysicsMapping)
            {
                Assert.That(entry.PrimPath, Is.Not.Empty, entry.ToString());
                if (entry.Status != UsdPhysicsMappingStatus.Skipped)
                {
                    Assert.That(entry.Unity, Is.Not.Empty, entry.ToString());
                }
                else
                {
                    Assert.That(entry.Detail, Is.Not.Empty, "a skipped prim says why: " + entry);
                }
            }

            // Every joint has a rest pose to measure and pose from.
            foreach (UsdPhysicsJoint joint in root.GetComponentsInChildren<UsdPhysicsJoint>())
            {
                if (joint.Articulated || joint.UnityJoint != null)
                {
                    Assert.That(joint.HasRestPose, Is.True, joint.PrimPath);
                    Assert.That(joint.MeasurePosition(), Is.EqualTo(joint.RestPosition).Within(1e-3f), joint.PrimPath);
                }
            }
        }
    }
}
