using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Time-sampled mesh points (point caches): imported as a track of the stage's animation, drawn
    /// through a vertex stream without touching the imported Mesh, written back by the recorder
    /// and by a flattened export of the frame on screen.
    /// </summary>
    public sealed class UsdPointCacheTests
    {
        private const float Tolerance = 1e-5f;
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(directory, true);
        }

        [Test]
        public void APointCachePlaysEverySampleAndInBetween()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            ExpectedPointCache cache = expected.points.Single(p => p.path == "/Root/PointCache");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                Assert.That(player.Data.PointCacheTracks.Count, Is.EqualTo(1));
                UsdAnimationData.PointCacheTrack track = player.Data.PointCacheTracks[0];
                Assert.That(track.Times, Is.EqualTo(cache.timeSamples));
                Assert.That(track.Normals.Length, Is.EqualTo(track.Positions.Length), "normals are recomputed per sample");

                GameObject prim = stage.Find("/Root/PointCache");
                var renderer = prim.GetComponent<MeshRenderer>();
                Mesh imported = prim.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] importedVertices = imported.vertices;
                foreach (ExpectedPointsFrame frame in cache.frames)
                {
                    player.EvaluateAtTimeCode(frame.time);
                    Mesh stream = renderer.additionalVertexStreams;
                    Assert.That(stream, Is.Not.Null, $"t={frame.time} draws through a stream");
                    AssertPoints(stage.Root, prim.transform, stream.vertices, importedVertices, cache.frames[0].points, frame);
                    Assert.That(renderer.localBounds.Contains(stream.vertices[1]), Is.True, "the bounds hold every sample");
                }

                Assert.That(imported.vertices, Is.EqualTo(importedVertices), "the imported Mesh is never written");
                player.RestoreMeshes();
                Assert.That(renderer.additionalVertexStreams, Is.Null, "RestoreMeshes shows the imported Mesh again");
            }
        }

        [Test]
        public void TwoInstancesOfOneMeshPlayIndependently()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                GameObject copy = Object.Instantiate(stage.Result.RootObject);
                try
                {
                    UsdAnimationPlayer first = stage.Result.Animation;
                    UsdAnimationPlayer second = copy.GetComponent<UsdAnimationPlayer>();
                    first.EvaluateAtTimeCode(0.0);
                    second.EvaluateAtTimeCode(10.0);
                    Vector3 a = PointCacheRenderer(first).additionalVertexStreams.vertices[1];
                    Vector3 b = PointCacheRenderer(second).additionalVertexStreams.vertices[1];
                    Assert.That(PointCacheRenderer(first).GetComponent<MeshFilter>().sharedMesh,
                                Is.SameAs(PointCacheRenderer(second).GetComponent<MeshFilter>().sharedMesh), "one Mesh");
                    Assert.That(Mathf.Abs(a.x), Is.EqualTo(1f).Within(Tolerance));
                    Assert.That(Mathf.Abs(b.x), Is.EqualTo(2f).Within(Tolerance));
                }
                finally
                {
                    Object.DestroyImmediate(copy);
                }
            }
        }

        [Test]
        public void ARecordingWritesThePointsBack()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            ExpectedPointCache cache = expected.points.Single(p => p.path == "/Root/PointCache");
            string path = Path.Combine(directory, "points.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                var recorder = new UsdAnimationRecorder(stage.Result.RootObject, expected.stage.timeCodesPerSecond);
                recorder.CapturePlayer(stage.Result.Animation);
                recorder.Save(path);
            }

            Assert.That(File.ReadAllText(path), Does.Contain("points.timeSamples"));
            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdAnimationPlayer player = again.Result.Animation;
                UsdAnimationData.PointCacheTrack track = player.Data.PointCacheTracks.Single();
                Assert.That(track.Times.Length, Is.EqualTo(11), "one sample a frame");
                GameObject prim = again.Find("/Root/PointCache");
                Vector3[] vertices = prim.GetComponent<MeshFilter>().sharedMesh.vertices;
                foreach (ExpectedPointsFrame frame in cache.frames)
                {
                    player.EvaluateAtTimeCode(frame.time);
                    AssertPoints(again.Root, prim.transform, prim.GetComponent<MeshRenderer>().additionalVertexStreams.vertices,
                                 vertices, cache.frames[0].points, frame);
                }
            }
        }

        [Test]
        public void AFlattenedExportWritesTheFrameOnScreen()
        {
            string path = Path.Combine(directory, "frame.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                stage.Result.Animation.EvaluateAtTimeCode(10.0);
                UsdExporter.ExportGameObjectWithResult(stage.Result.RootObject, path,
                    new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy });
            }

            using (ImportedStage again = ImportedStage.Import(path, new UsdImportOptions { ImportAnimation = false }))
            {
                Vector3[] vertices = again.Find("/Root/PointCache").GetComponent<MeshFilter>().sharedMesh.vertices;
                Assert.That(vertices.Max(v => Mathf.Abs(v.x)), Is.EqualTo(2f).Within(Tolerance), "the frame at t=10");
            }
        }

        // Vertex painting tools put colours in the same stream; colours alone move no point.
        [Test]
        public void AColourOnlyStreamIsNotTakenForPoints()
        {
            var root = new GameObject("Painted");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.SetParent(root.transform, false);
            Mesh mesh = cube.GetComponent<MeshFilter>().sharedMesh;
            var stream = new Mesh { name = "Colours" };
            var colourArray = new Color[mesh.vertexCount];
            for (int i = 0; i < colourArray.Length; i++)
            {
                colourArray[i] = Color.red;
            }

            // A stream must have as many vertices as the mesh; positions are what it lacks.
            var layout = new[]
            {
                new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Color,
                                                                    UnityEngine.Rendering.VertexAttributeFormat.Float32, 4)
            };
            stream.SetVertexBufferParams(mesh.vertexCount, layout);
            stream.SetVertexBufferData(colourArray, 0, 0, colourArray.Length);
            cube.GetComponent<MeshRenderer>().additionalVertexStreams = stream;
            string flat = Path.Combine(directory, "painted.usda");
            string recorded = Path.Combine(directory, "painted_recorded.usda");
            try
            {
                var options = new UsdExportOptions { TransformPolicy = UsdTransformPolicy.PreserveHierarchy };
                UsdExporter.ExportGameObjectWithResult(root, flat, options);
                var recorder = new UsdAnimationRecorder(root, 24.0);
                recorder.Capture(0.0);
                cube.transform.localPosition = Vector3.up;
                recorder.Capture(1.0 / 24.0);
                recorder.Save(recorded);
                Assert.That(File.ReadAllText(recorded), Does.Not.Contain("points.timeSamples"));
                using (ImportedStage again = ImportedStage.Import(flat))
                {
                    Mesh imported = again.Root.GetComponentInChildren<MeshFilter>().sharedMesh;
                    Assert.That(imported.vertexCount, Is.EqualTo(mesh.vertexCount), "the cube's own points were written");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(stream);
            }
        }

        // The point cache and UsdDualQuaternionSkin both draw through a renderer's additional
        // vertex stream; this checks the GPU really takes positions from it. A quad whose Mesh is
        // off screen and whose stream puts it in front of the camera has to show, and the other way
        // round. Needs a graphics device, so it runs in an Editor with a window.
        [Test]
        public void TheVertexStreamIsWhatIsDrawn()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device (-nographics): nothing is drawn to look at.");
            }

            // The active pipeline's unlit shader: URP's draws nothing in a Built-in project that
            // merely has URP installed.
            Shader shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("HDRP/Unlit")
                : Shader.Find("Unlit/Color");
            var material = new Material(shader) { color = Color.white };
            material.SetColor("_BaseColor", Color.white);
            var texture = new RenderTexture(64, 64, 24);
            var cameraObject = new GameObject("StreamCamera");
            var quad = new GameObject("StreamQuad");
            Mesh mesh = Quad(new Vector3(10f, 0f, 0f));
            Mesh stream = Quad(Vector3.zero);
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 1f;
                camera.transform.position = new Vector3(0f, 0f, -5f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.targetTexture = texture;
                quad.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = quad.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                Assert.That(CentreBrightness(camera, texture), Is.LessThan(0.1f), "the Mesh alone is off screen");
                renderer.additionalVertexStreams = stream;
                renderer.localBounds = stream.bounds;
                Assert.That(CentreBrightness(camera, texture), Is.GreaterThan(0.9f), "the stream's positions are drawn");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(quad);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(stream);
                Object.DestroyImmediate(material);
                texture.Release();
                Object.DestroyImmediate(texture);
            }
        }

        private static Mesh Quad(Vector3 offset)
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                offset + new Vector3(-0.5f, -0.5f, 0f), offset + new Vector3(0.5f, -0.5f, 0f),
                offset + new Vector3(-0.5f, 0.5f, 0f), offset + new Vector3(0.5f, 0.5f, 0f)
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float CentreBrightness(Camera camera, RenderTexture texture)
        {
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = texture };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.Render();
            }

            RenderTexture previous = RenderTexture.active;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                read.Apply();
                return read.GetPixel(texture.width / 2, texture.height / 2).grayscale;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        private static MeshRenderer PointCacheRenderer(UsdAnimationPlayer player) =>
            player.transform.Find(player.Data.PointCacheTracks[0].Path).GetComponent<MeshRenderer>();

        private static void AssertPoints(Transform importRoot, Transform mesh, Vector3[] drawn, Vector3[] restVertices,
                                         float[] restPoints, ExpectedPointsFrame frame)
        {
            int[] pointOf = UsdFixtures.PointOfEachVertex(restVertices, restPoints);
            Assert.That(drawn.Length, Is.EqualTo(restVertices.Length));
            for (int v = 0; v < drawn.Length; v++)
            {
                Vector3 actual = mesh.TransformPoint(drawn[v]);
                Vector3 wanted = UsdFixtures.StageToWorld(importRoot, UsdFixtures.Point(frame.points, pointOf[v]));
                Assert.That(Vector3.Distance(actual, wanted), Is.LessThan(Tolerance), $"vertex {v} at t={frame.time}");
            }
        }
    }
}
