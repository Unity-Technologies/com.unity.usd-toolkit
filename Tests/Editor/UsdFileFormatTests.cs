using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Alembic (.abc) and Draco (.drc) files, read through OpenUSD's usdAbc and usdDraco plugins in
    /// the payload: an Alembic file Blender wrote imports with its animation, and a stage that
    /// references a Draco mesh composes it. The expected values are what OpenUSD itself reads from
    /// the fixtures (usdcat of them, quoted below), in USD's frame; Unity mirrors X. A scene and a
    /// recording written as Alembic read back where they were drawn, skinned meshes baked.
    /// </summary>
    public sealed class UsdFileFormatTests
    {
        private const float Tolerance = 1e-5f;
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
        public void AnAlembicFileImportsWithItsAnimation()
        {
            RequirePlugin("usdAbc");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("FileFormats", "alembic_box.abc")))
            {
                GameObject box = stage.Find("/Box");
                Assert.That(box, Is.Not.Null, "Blender's Box object");
                Assert.That(box.GetComponent<MeshFilter>().sharedMesh.vertices.Length, Is.GreaterThanOrEqualTo(8));

                UsdAnimationPlayer player = stage.Result.Animation;
                Assert.That(player, Is.Not.Null, "the file is animated");
                Assert.That(player.Data.TimeCodesPerSecond, Is.EqualTo(24.0));

                // xformOp:transform samples at 1..11 move Box from the origin to (2, 0, 0).
                player.EvaluateAtTimeCode(1.0);
                Assert.That(Vector3.Distance(box.transform.localPosition, Vector3.zero), Is.LessThan(Tolerance));
                player.EvaluateAtTimeCode(6.0);
                Assert.That(Vector3.Distance(box.transform.localPosition, new Vector3(-1f, 0f, 0f)), Is.LessThan(Tolerance));
                player.EvaluateAtTimeCode(11.0);
                Assert.That(Vector3.Distance(box.transform.localPosition, new Vector3(-2f, 0f, 0f)), Is.LessThan(Tolerance));

                // Wave's points are sampled every frame: point 0 rises from (-1, 0, 1) to (-1, 1, 1).
                GameObject wave = stage.Find("/Wave");
                Assert.That(player.Data.PointCacheTracks.Any(t => t.Path.EndsWith("Wave")), Is.True, "a point cache");
                var renderer = wave.GetComponent<MeshRenderer>();
                Vector3[] rest = wave.GetComponent<MeshFilter>().sharedMesh.vertices;
                int[] corner = Enumerable.Range(0, rest.Length)
                    .Where(v => Vector3.Distance(rest[v], new Vector3(1f, 0f, 1f)) < Tolerance).ToArray();
                Assert.That(corner, Is.Not.Empty, "a vertex at USD point 0");
                player.EvaluateAtTimeCode(11.0);
                Vector3[] drawn = renderer.additionalVertexStreams.vertices;
                foreach (int v in corner)
                {
                    Assert.That(Vector3.Distance(drawn[v], new Vector3(1f, 1f, 1f)), Is.LessThan(Tolerance), $"vertex {v} at t=11");
                }

                player.EvaluateAtTimeCode(6.0);
                drawn = renderer.additionalVertexStreams.vertices;
                Assert.That(drawn[corner[0]].y, Is.EqualTo(0.5f).Within(Tolerance), "halfway at t=6");
            }
        }

        [Test]
        public void AStageReferencingADracoMeshComposesIt()
        {
            RequirePlugin("usdDraco");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("FileFormats", "draco_stage.usda")))
            {
                GameObject quad = stage.Find("/Root/Quad");
                Assert.That(quad, Is.Not.Null);
                Assert.That(Vector3.Distance(quad.transform.localPosition, new Vector3(0f, 0f, 2f)), Is.LessThan(Tolerance));
                Mesh mesh = quad.GetComponent<MeshFilter>().sharedMesh;
                var points = new[]
                {
                    new Vector3(-0.5f, 0.25f, -1f), new Vector3(-1.5f, 0.25f, -1f),
                    new Vector3(-1.5f, 1.25f, -1f), new Vector3(-0.5f, 1.25f, -1f)
                };
                foreach (Vector3 point in points)
                {
                    Assert.That(mesh.vertices.Any(v => Vector3.Distance(v, point) < Tolerance), Is.True,
                                $"the Draco point {point}, unquantized");
                }

                Assert.That(mesh.triangles.Length, Is.EqualTo(6), "the quad, triangulated");
                var uv = new System.Collections.Generic.List<Vector2>();
                mesh.GetUVs(0, uv);
                Assert.That(uv.Count, Is.EqualTo(mesh.vertexCount), "its texture coordinates came through");
                Assert.That(stage.Result.SourceFiles.Any(f => f.EndsWith("draco_quad.drc")), Is.True,
                            "the Draco file is one of the stage's layers");
            }
        }

        // Suzanne written by OpenUSD's own Draco writer with its polygons preserved. usdDraco rebuilt
        // them across a boundary edge into a face that does not exist, and crashed, until the
        // file-format hardening patch (Native~/patches).
        [Test]
        public void APolygonDracoMeshWithBoundariesImports()
        {
            RequirePlugin("usdDraco");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("FileFormats", "draco_polygons.drc")))
            {
                Mesh mesh = stage.Root.GetComponentsInChildren<MeshFilter>().Single().sharedMesh;
                Assert.That(mesh.triangles.Length / 3, Is.EqualTo(968), "467 quads and 34 triangles, triangulated");
                Assert.That(mesh.vertexCount, Is.EqualTo(512));
            }
        }

        [Test]
        public void BothImportAsProjectAssets()
        {
            RequirePlugin("usdAbc");
            const string folder = "Assets/UsdToolkitFileFormatTests";
            AssetDatabase.CreateFolder("Assets", "UsdToolkitFileFormatTests");
            try
            {
                string abc = folder + "/alembic_box.abc";
                File.Copy(UsdFixtures.PathOf("FileFormats", "alembic_box.abc"), abc);
                AssetDatabase.ImportAsset(abc, ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetImporter.GetAtPath(abc), Is.InstanceOf<Unity.USDToolkit.Editor.UsdStageImporter>());
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(abc);
                Assert.That(root, Is.Not.Null);
                Assert.That(root.GetComponent<UsdAnimationPlayer>().Data.PointCacheTracks.Count, Is.EqualTo(1));

                RequirePlugin("usdDraco");
                string drc = folder + "/draco_quad.drc";
                File.Copy(UsdFixtures.PathOf("FileFormats", "draco_quad.drc"), drc);
                AssetDatabase.ImportAsset(drc, ImportAssetOptions.ForceSynchronousImport);
                var mesh = AssetDatabase.LoadAllAssetsAtPath(drc).OfType<Mesh>().Single();
                // The project-asset importer uses the default options, so the open quad also gets
                // back faces (UsdImportOptions.DoubleSidedOpenMeshes): 4 vertices front, 4 back.
                Assert.That(mesh.vertexCount, Is.EqualTo(8), "a .drc on its own is a mesh asset");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ASceneExportedAsAlembicReadsBackInCentimeters()
        {
            RequirePlugin("usdAbc");
            GameObject scene = Track(new GameObject("Scene"));
            MeshFilter box = AddQuad(scene.transform, "Box");
            box.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f));
            SkinnedMeshRenderer body = BuildRig(scene.transform, out Transform spine);
            spine.localRotation = Quaternion.Euler(0f, 0f, 35f);
            Vector3[] boxDrawn = World(box.transform, box.sharedMesh.vertices);
            Vector3[] bodyDrawn = Baked(body);

            string output = Path.Combine(folder, "scene.abc");
            UsdExporter.ExportGameObjectWithResult(scene, output, new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy });
            Assert.That(File.Exists(output), Is.True);

            using (ImportedStage again = ImportedStage.Import(output))
            {
                Assert.That(again.Result.StageCorrectionScale, Is.EqualTo(0.01f), "Alembic is read as centimeters");
                Assert.That(Vector3.Distance(again.Find("/Scene").transform.localScale, Vector3.one * 100f), Is.LessThan(1e-3f),
                            "the root prim scales the scene into them");
                MeshFilter boxAgain = again.Find("/Scene/Box").GetComponentInChildren<MeshFilter>();
                AssertSamePoints(World(boxAgain.transform, boxAgain.sharedMesh.vertices), boxDrawn, "the box");

                GameObject bodyAgain = again.Find("/Scene/Rig/Body");
                Assert.That(bodyAgain.GetComponentInChildren<SkinnedMeshRenderer>(), Is.Null, "Alembic has no skeletons");
                MeshFilter bodyMesh = bodyAgain.GetComponentInChildren<MeshFilter>();
                AssertSamePoints(World(bodyMesh.transform, bodyMesh.sharedMesh.vertices), bodyDrawn, "the body in its pose");
                Assert.That(again.Root.GetComponentsInChildren<UsdSkeleton>(), Is.Empty);
            }
        }

        [Test]
        public void ARecordingSavedAsAlembicBakesItsSkinnedMeshesEveryFrame()
        {
            RequirePlugin("usdAbc");
            GameObject scene = Track(new GameObject("Scene"));
            SkinnedMeshRenderer body = BuildRig(scene.transform, out Transform spine);
            var recorder = new UsdAnimationRecorder(scene, 24.0);
            var drawn = new List<Vector3[]>();
            for (int f = 0; f < 3; f++)
            {
                spine.localRotation = Quaternion.Euler(0f, 0f, 45f * f);
                drawn.Add(Baked(body));
                recorder.Capture(f / 24.0);
            }

            string output = Path.Combine(folder, "recording.abc");
            recorder.Save(output);
            Assert.That(Quaternion.Angle(spine.localRotation, Quaternion.Euler(0f, 0f, 90f)), Is.LessThan(1e-3f), "the scene is put back");

            using (ImportedStage again = ImportedStage.Import(output))
            {
                UsdAnimationPlayer player = again.Result.Animation;
                Assert.That(player, Is.Not.Null, "the recording is animated");
                Assert.That(player.Data.PointCacheTracks.Count, Is.EqualTo(1), "the body's points are sampled");
                MeshRenderer renderer = again.Find("/Scene/Rig/Body").GetComponentInChildren<MeshRenderer>();
                for (int f = 0; f < 3; f++)
                {
                    player.EvaluateAtTimeCode(f);
                    AssertSamePoints(World(renderer.transform, renderer.additionalVertexStreams.vertices), drawn[f], $"frame {f}");
                }
            }
        }

        // A .drc is one mesh: the box and the posed body merged in the scene's space, in centimeters
        // (Draco has no units and OpenUSD reads it as Alembic), quantized by Draco to well under a
        // millimeter.
        [Test]
        public void ASceneExportedAsDracoReadsBackAsOneMesh()
        {
            RequireDracoExport();
            GameObject scene = Track(new GameObject("Scene"));
            MeshFilter box = AddQuad(scene.transform, "Box");
            box.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f));
            MeshFilter mirrored = AddQuad(scene.transform, "Mirrored");
            mirrored.transform.localPosition = new Vector3(-2f, 0f, 0f);
            mirrored.transform.localScale = new Vector3(-1f, 1f, 1f);
            SkinnedMeshRenderer body = BuildRig(scene.transform, out Transform spine);
            spine.localRotation = Quaternion.Euler(0f, 0f, 35f);
            // Texture coordinates are kept when every mesh has them.
            foreach (Mesh withUv in new[] { box.sharedMesh, mirrored.sharedMesh, body.sharedMesh })
            {
                withUv.uv = withUv.vertices.Select(v => new Vector2(v.x, v.y)).ToArray();
            }

            Vector3[] drawn = World(box.transform, box.sharedMesh.vertices)
                .Concat(World(mirrored.transform, mirrored.sharedMesh.vertices))
                .Concat(Baked(body))
                .ToArray();

            string output = Path.Combine(folder, "scene.drc");
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(scene, output, new UsdExportOptions());
            Assert.That(File.Exists(output), Is.True);
            Assert.That(result.MeshCount, Is.EqualTo(3), "three meshes went in");

            using (ImportedStage again = ImportedStage.Import(output))
            {
                Assert.That(again.Result.StageCorrectionScale, Is.EqualTo(0.01f), "Draco is read as centimeters");
                MeshFilter[] filters = again.Root.GetComponentsInChildren<MeshFilter>();
                Assert.That(filters.Length, Is.EqualTo(1), "one Draco mesh");
                Assert.That(again.Root.GetComponentsInChildren<SkinnedMeshRenderer>(), Is.Empty, "no skeletons");
                Vector3[] read = World(filters[0].transform, filters[0].sharedMesh.vertices);
                foreach (Vector3 point in drawn)
                {
                    Assert.That(read.Any(p => Vector3.Distance(p, point) < 1e-3f), Is.True, $"nothing read back at {point}");
                }

                foreach (Vector3 point in read)
                {
                    Assert.That(drawn.Any(p => Vector3.Distance(p, point) < 1e-3f), Is.True, $"{point} was not exported");
                }

                Mesh mesh = filters[0].sharedMesh;
                Assert.That(mesh.uv.Length, Is.EqualTo(mesh.vertexCount), "texture coordinates kept");
                // Every face of the three keeps facing where it did: its normal from the winding
                // agrees with the normal the vertices carry, the mirrored quad's too.
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                int[] triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                    Assert.That(Vector3.Dot(faceNormal, normals[triangles[i]]), Is.GreaterThan(0f), $"triangle {i / 3} faces its normal");
                }
            }
        }

        [Test]
        public void ARecordingSavedAsDracoIsRefused()
        {
            RequireDracoExport();
            GameObject scene = Track(new GameObject("Scene"));
            AddQuad(scene.transform, "Box");
            var recorder = new UsdAnimationRecorder(scene, 24.0);
            recorder.Capture(0.0);
            recorder.Capture(1.0 / 24.0);
            string output = Path.Combine(folder, "recording.drc");
            Assert.Throws<UsdExportException>(() => recorder.Save(output));
            Assert.That(File.Exists(output), Is.False);
        }

        [Test]
        public void AnOverrideExportToDracoIsRefused()
        {
            string output = Path.Combine(folder, "override.drc");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                Assert.Throws<UsdExportException>(() => UsdExporter.ExportGameObjectWithResult(
                    stage.Result.RootObject, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve }));
            }

            Assert.That(File.Exists(output), Is.False);
        }

        [Test]
        public void AnOverrideExportToAlembicIsRefused()
        {
            string output = Path.Combine(folder, "override.abc");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                Assert.Throws<UsdExportException>(() => UsdExporter.ExportGameObjectWithResult(
                    stage.Result.RootObject, output, new UsdExportOptions { Composition = UsdCompositionMode.Preserve }));
            }

            Assert.That(File.Exists(output), Is.False);
        }

        private T Track<T>(T item) where T : Object
        {
            created.Add(item);
            return item;
        }

        private MeshFilter AddQuad(Transform parent, string name)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            Mesh mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            MeshFilter filter = holder.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            holder.AddComponent<MeshRenderer>();
            return filter;
        }

        // Two bones up a strip of six points, the middle pair weighted to both.
        private SkinnedMeshRenderer BuildRig(Transform parent, out Transform spine)
        {
            var rig = new GameObject("Rig");
            rig.transform.SetParent(parent, false);
            rig.transform.localPosition = new Vector3(0f, 0f, 3f);
            Transform hips = new GameObject("Hips").transform;
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

        // What the renderer draws now, in world space.
        private static Vector3[] Baked(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked, true);
                return World(renderer.transform, baked.vertices);
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        private static Vector3[] World(Transform transform, Vector3[] points) =>
            points.Select(transform.TransformPoint).ToArray();

        // The same points, in any order: the importer may lay vertices out differently.
        private static void AssertSamePoints(Vector3[] actual, Vector3[] expected, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            foreach (Vector3 point in expected)
            {
                Assert.That(actual.Any(p => Vector3.Distance(p, point) < 1e-4f), Is.True, $"{what}: nothing drawn at {point}");
            }
        }

        // Writing .drc needs usdDraco and a plugin at native API 13, whose OpenUSD exports the writer.
        private static void RequireDracoExport()
        {
            RequirePlugin("usdDraco");
            UsdRuntimeInfo runtime = UsdExporter.GetRuntimeInfo();
            if (runtime.NativeApiVersion < 13)
            {
                Assert.Ignore($"Writing .drc needs the 0.8.1 native plugin; this payload is {runtime}. Rebuild it (Native~/REBUILD_WINDOWS_LINUX.md).");
            }
        }

        // The plugins are in a payload built with them; a platform whose payload predates them
        // has nothing to read these files with.
        private static void RequirePlugin(string plugin)
        {
            string platform = Application.platform == RuntimePlatform.OSXEditor ? "macOS"
                : Application.platform == RuntimePlatform.WindowsEditor ? "x86_64/Windows" : "x86_64/Linux";
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UsdImporter).Assembly).resolvedPath;
            string info = Path.Combine(package, "Runtime", "Plugins", platform, "plugin", "usd", plugin, "resources", "plugInfo.json");
            if (!File.Exists(info))
            {
                Assert.Ignore($"This platform's payload has no {plugin} ({info}); rebuild it with Native~/build_openusd.py.");
            }
        }
    }
}
