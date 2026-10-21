using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// SkinnedMeshRenderer export and UsdAnimationRecorder, checked by importing what they wrote:
    /// a character written as UsdSkel and read back must draw what the original drew, in every
    /// recorded frame.
    /// </summary>
    public sealed class UsdSkinnedExportTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly UsdExportOptions Preserve = new UsdExportOptions
        {
            TransformPolicy = UsdTransformPolicy.PreserveHierarchy
        };

        private readonly List<Object> created = new List<Object>();
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
            foreach (Object item in created)
            {
                if (item != null)
                {
                    Object.DestroyImmediate(item);
                }
            }

            created.Clear();
            Directory.Delete(directory, true);
        }

        [TestCase(0.0)]
        [TestCase(5.0)]
        public void AnImportedCharacterRoundTripsInItsCurrentPose(double time)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            string path = Path.Combine(directory, "skel_arm_posed.usda");
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                stage.Result.Animation.EvaluateAtTimeCode(time);
                UsdExporter.ExportGameObjectWithResult(stage.Result.RootObject, path, Preserve);
            }

            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdSkeleton skeleton = again.Find("/Root/Arm/Skel").GetComponent<UsdSkeleton>();
                Assert.That(skeleton, Is.Not.Null, "the Skeleton prim and its joint names are kept");
                Assert.That(skeleton.JointPaths, Is.EqualTo(new[] { "Shoulder", "Shoulder/Elbow", "Shoulder/Elbow/Wrist" }));
                foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                {
                    var skinned = again.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skinned, Is.Not.Null, mesh.path);
                    UsdSkinningTests.AssertSkinnedPoints(again, skinned, mesh, UsdFixtures.FrameAt(mesh, time));
                }

                Assert.That(again.Find("/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0),
                            Is.EqualTo(BulgeWeight(time)).Within(1e-3f), "the blend-shape weight it was posed with");
            }
        }

        [Test]
        public void AUnityRigExportsAsUsdSkel()
        {
            GameObject rig = BuildRig(out SkinnedMeshRenderer renderer, out Transform hips, out Transform spine);
            hips.localRotation = Quaternion.Euler(10f, 0f, 0f);
            spine.localRotation = Quaternion.Euler(0f, 0f, 40f);
            renderer.SetBlendShapeWeight(0, 60f);
            Vector3[] drawn = RootLocalSkinned(renderer, rig.transform);
            string path = Path.Combine(directory, "rig.usda");
            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(rig, path, Preserve);
            Assert.That(result.MeshCount, Is.EqualTo(1));

            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdSkeleton skeleton = again.Find("/Rig/Skeleton").GetComponent<UsdSkeleton>();
                Assert.That(skeleton, Is.Not.Null, "a Skeleton below the SkelRoot");
                Assert.That(skeleton.JointPaths, Is.EqualTo(new[] { "Hips", "Hips/Spine" }));
                var skinned = again.Find("/Rig/Body/Body_Mesh").GetComponent<SkinnedMeshRenderer>();
                Assert.That(skinned, Is.Not.Null);
                Assert.That(skinned.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Puff"));
                Assert.That(skinned.GetBlendShapeWeight(0), Is.EqualTo(60f).Within(1e-3f));
                AssertDraws(skinned, again.Root, renderer.sharedMesh, drawn, "the posed rig");
                Assert.That(again.Find("/Rig/Hips/Spine"), Is.Not.Null, "bones stay Xform prims for what hangs off them");
            }
        }

        [Test]
        public void MeshesOnTheSameBonesShareOneSkeleton()
        {
            GameObject rig = BuildRig(out SkinnedMeshRenderer body, out Transform hips, out Transform spine);
            // A second mesh elsewhere, bound in the same rest pose: it shares the bind transforms
            // through its own geomBindTransform.
            SkinnedMeshRenderer sleeve = AddRenderer(rig, "Sleeve", new Vector3(-0.5f, 0.25f, 0.1f), 0.5f, body);
            sleeve.transform.localRotation = Quaternion.Euler(0f, 30f, 0f);
            sleeve.sharedMesh.bindposes = new[]
            {
                hips.worldToLocalMatrix * sleeve.transform.localToWorldMatrix,
                spine.worldToLocalMatrix * sleeve.transform.localToWorldMatrix,
            };

            // A third bound with the spine bent: no single geomBindTransform fits it.
            spine.localRotation = Quaternion.Euler(0f, 0f, 25f);
            SkinnedMeshRenderer patch = AddRenderer(rig, "Patch", new Vector3(0f, 0.5f, -0.1f), 1f, body);
            patch.sharedMesh.bindposes = new[]
            {
                hips.worldToLocalMatrix * patch.transform.localToWorldMatrix,
                spine.worldToLocalMatrix * patch.transform.localToWorldMatrix,
            };

            spine.localRotation = Quaternion.Euler(0f, 0f, -35f);
            hips.localRotation = Quaternion.Euler(0f, 20f, 0f);
            Vector3[] bodyDrawn = RootLocalSkinned(body, rig.transform);
            Vector3[] sleeveDrawn = RootLocalSkinned(sleeve, rig.transform);
            Vector3[] patchDrawn = RootLocalSkinned(patch, rig.transform);
            string path = Path.Combine(directory, "rig_shared.usda");
            LogAssert.Expect(LogType.Warning, new Regex("'Patch' is exported as a static mesh in its current pose: its bind poses do not match"));
            UsdExporter.ExportGameObjectWithResult(rig, path, Preserve);

            using (ImportedStage again = ImportedStage.Import(path))
            {
                Assert.That(again.Root.GetComponentsInChildren<UsdSkeleton>(true).Length, Is.EqualTo(1));
                AssertDraws(again.Find("/Rig/Body/Body_Mesh").GetComponent<SkinnedMeshRenderer>(), again.Root, body.sharedMesh,
                            bodyDrawn, "Body");
                AssertDraws(again.Find("/Rig/Sleeve/Sleeve_Mesh").GetComponent<SkinnedMeshRenderer>(), again.Root,
                            sleeve.sharedMesh, sleeveDrawn, "Sleeve");
                GameObject patchPrim = again.Find("/Rig/Patch/Patch_Mesh");
                Assert.That(patchPrim.GetComponent<SkinnedMeshRenderer>(), Is.Null, "Patch is baked");
                MeshFilter filter = patchPrim.GetComponent<MeshFilter>();
                AssertSameShape(filter.sharedMesh.vertices.Select(v => again.Root.InverseTransformPoint(filter.transform.TransformPoint(v))),
                                patchDrawn, "Patch");
            }
        }

        [Test]
        public void BakedMeshPolicyWritesWhatTheRendererDraws()
        {
            GameObject rig = BuildRig(out SkinnedMeshRenderer renderer, out _, out Transform spine);
            spine.localRotation = Quaternion.Euler(0f, 0f, 40f);
            renderer.SetBlendShapeWeight(0, 100f);
            Vector3[] drawn = RootLocalSkinned(renderer, rig.transform);
            string path = Path.Combine(directory, "rig_baked.usda");
            UsdExporter.ExportGameObjectWithResult(rig, path, new UsdExportOptions());

            using (ImportedStage again = ImportedStage.Import(path))
            {
                Assert.That(again.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true), Is.Empty);
                MeshFilter filter = again.Root.GetComponentsInChildren<MeshFilter>(true).Single();
                AssertSameShape(filter.sharedMesh.vertices.Select(v => again.Root.InverseTransformPoint(filter.transform.TransformPoint(v))),
                                drawn, "the baked mesh");
            }
        }

        [Test]
        public void ARendererWhoseBonesAreOutsideTheExportIsBaked()
        {
            BuildRig(out SkinnedMeshRenderer renderer, out _, out Transform spine);
            spine.localRotation = Quaternion.Euler(0f, 0f, 40f);
            Vector3[] drawn = RootLocalSkinned(renderer, renderer.transform);
            string path = Path.Combine(directory, "body_only.usda");
            LogAssert.Expect(LogType.Warning, new Regex("'Body' is exported as a static mesh in its current pose: a bone is missing or outside"));
            UsdExporter.ExportGameObjectWithResult(renderer.gameObject, path, Preserve);

            using (ImportedStage again = ImportedStage.Import(path))
            {
                Assert.That(again.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true), Is.Empty);
                MeshFilter filter = again.Root.GetComponentsInChildren<MeshFilter>(true).Single();
                AssertSameShape(filter.sharedMesh.vertices.Select(v => again.Root.InverseTransformPoint(filter.transform.TransformPoint(v))),
                                drawn, "the baked mesh");
            }
        }

        [TestCase("skel_arm")]
        [TestCase("blender_arm")]
        public void RecordingAPlayerRoundTripsEveryFrame(string fixture)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", fixture);
            double start = expected.stage.startTimeCode;
            string path = Path.Combine(directory, fixture + "_recorded.usda");
            // The expected points are in the source stage's frame, which the source import turned
            // and scaled on its root (Blender's stage is Z-up). The recording is written Y-up with
            // that turn on its root prim, so it is drawn where the source was: compare in the
            // source import's frame, not the recording's.
            var sourceFrame = new GameObject("SourceFrame");
            try
            {
                using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", fixture + ".usda")))
                {
                    sourceFrame.transform.localRotation = stage.Result.StageCorrectionRotation;
                    sourceFrame.transform.localScale = Vector3.one * stage.Result.StageCorrectionScale;
                    var recorder = new UsdAnimationRecorder(stage.Result.RootObject, expected.stage.timeCodesPerSecond);
                    recorder.CapturePlayer(stage.Result.Animation);
                    Assert.That(recorder.SampleCount, Is.EqualTo((int)(expected.stage.endTimeCode - start) + 1));
                    Assert.That(stage.Result.Animation.CurrentTime, Is.EqualTo(0.0), "the player is back where it was");
                    recorder.Save(path);
                }

                using (ImportedStage again = ImportedStage.Import(path))
                {
                    UsdAnimationPlayer player = again.Result.Animation;
                    Assert.That(player, Is.Not.Null);
                    Assert.That(player.Data.StartTimeCode, Is.EqualTo(0.0));
                    Assert.That(player.Data.EndTimeCode, Is.EqualTo(expected.stage.endTimeCode - start));
                    Assert.That(player.Data.SkeletonTracks.Count, Is.EqualTo(1));
                    foreach (double time in expected.times.Where(t => Math.Abs(t - Math.Round(t)) < 1e-9))
                    {
                        player.EvaluateAtTimeCode(time - start);
                        foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                        {
                            var skinned = again.Find(mesh.path).GetComponent<SkinnedMeshRenderer>();
                            Assert.That(skinned, Is.Not.Null, mesh.path);
                            UsdSkinningTests.AssertSkinnedPoints(sourceFrame.transform, skinned, mesh, UsdFixtures.FrameAt(mesh, time));
                        }

                        foreach (ExpectedVisibility visibility in expected.visibility ?? new ExpectedVisibility[0])
                        {
                            string value = visibility.frames.First(f => Math.Abs(f.time - time) < 1e-9).value;
                            Assert.That(again.Find(visibility.path).activeInHierarchy, Is.EqualTo(value != "invisible"),
                                        $"{visibility.path} at t={time}");
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(sourceFrame);
            }
        }

        [Test]
        public void RecordingAUnityRigRoundTripsEveryFrame()
        {
            GameObject rig = BuildRig(out SkinnedMeshRenderer renderer, out Transform hips, out Transform spine);
            var marker = new GameObject("Marker");
            marker.transform.SetParent(rig.transform, false);
            var recorder = new UsdAnimationRecorder(rig, 24.0);
            var frames = new List<Vector3[]>();
            const int count = 9;
            for (int f = 0; f < count; f++)
            {
                hips.localPosition = new Vector3(0f, 1f + 0.1f * f, 0f);
                spine.localRotation = Quaternion.Euler(0f, 0f, 10f * f);
                renderer.SetBlendShapeWeight(0, 12.5f * f);
                marker.SetActive(f < 5);
                recorder.Capture(f / 24.0);
                frames.Add(RootLocalSkinned(renderer, rig.transform));
            }

            string path = Path.Combine(directory, "rig_recorded.usdc");
            recorder.Save(path);
            Assert.That(marker.activeSelf, Is.False, "Save puts the scene back as it was");
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100f));

            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdAnimationPlayer player = again.Result.Animation;
                Assert.That(player, Is.Not.Null);
                Assert.That(player.Data.EndTimeCode, Is.EqualTo(count - 1));
                var skinned = again.Find("/Rig/Body/Body_Mesh").GetComponent<SkinnedMeshRenderer>();
                Transform hipsPrim = again.Find("/Rig/Hips").transform;
                GameObject markerPrim = again.Find("/Rig/Marker");
                Assert.That(markerPrim, Is.Not.Null, "an object hidden later still has its prim");
                for (int f = 0; f < count; f++)
                {
                    player.EvaluateAtTimeCode(f);
                    AssertDraws(skinned, again.Root, renderer.sharedMesh, frames[f], $"frame {f}");
                    Assert.That(skinned.GetBlendShapeWeight(0), Is.EqualTo(12.5f * f).Within(1e-3f), $"weight at frame {f}");
                    Assert.That(Vector3.Distance(hipsPrim.localPosition, new Vector3(0f, 1f + 0.1f * f, 0f)), Is.LessThan(Tolerance),
                                $"the Hips Xform at frame {f}");
                    Assert.That(markerPrim.activeSelf, Is.EqualTo(f < 5), $"Marker at frame {f}");
                }
            }
        }

        [Test]
        public void RecordingAClipSamplesItAndPutsThePoseBack()
        {
            var root = Track(new GameObject("ClipRoot"));
            var mover = new GameObject("Mover");
            mover.transform.SetParent(root.transform, false);
            var clip = Track(new AnimationClip { legacy = true });
            clip.SetCurve("Mover", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 2f));

            var recorder = new UsdAnimationRecorder(root, 10.0);
            recorder.CaptureClip(clip);
            Assert.That(recorder.SampleCount, Is.EqualTo(11));
            Assert.That(mover.transform.localPosition, Is.EqualTo(Vector3.zero), "the pose is put back");

            string path = Path.Combine(directory, "clip.usda");
            recorder.Save(path);
            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdAnimationPlayer player = again.Result.Animation;
                Assert.That(player.Data.TimeCodesPerSecond, Is.EqualTo(10.0));
                Assert.That(player.Duration, Is.EqualTo(1.0).Within(1e-9));
                Transform moved = again.Find("/ClipRoot/Mover").transform;
                player.EvaluateAtTimeCode(5.0);
                Assert.That(moved.localPosition.x, Is.EqualTo(1f).Within(Tolerance));
                player.EvaluateAtTimeCode(10.0);
                Assert.That(moved.localPosition.x, Is.EqualTo(2f).Within(Tolerance));
            }
        }

        // Renderer.enabled is recorded like the active state: on a Unity object's "<name>_Mesh"
        // prim on its own, and together with the active state on a mesh the importer made, whose
        // Xform and Mesh are one prim.
        [Test]
        public void RecordingARendererSwitchedOffHidesItsMesh()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            string meshPrim = expected.skinned[0].path;
            string path = Path.Combine(directory, "blink.usda");
            bool Enabled(int f) => f % 3 != 1;
            bool Active(int f) => f < 4;
            const int count = 6;
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                GameObject imported = stage.Find(meshPrim);
                var skinned = imported.GetComponent<SkinnedMeshRenderer>();
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Cube";
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                cube.transform.SetParent(stage.Root.GetChild(0), false);
                var cubeRenderer = cube.GetComponent<MeshRenderer>();

                var recorder = new UsdAnimationRecorder(stage.Result.RootObject, 24.0);
                for (int f = 0; f < count; f++)
                {
                    skinned.enabled = Enabled(f);
                    imported.SetActive(Active(f));
                    cubeRenderer.enabled = Enabled(f);
                    recorder.Capture(f / 24.0);
                }

                recorder.Save(path);
                Assert.That(skinned.enabled, Is.EqualTo(Enabled(count - 1)), "Save puts the renderers back");
                Assert.That(cubeRenderer.enabled, Is.EqualTo(Enabled(count - 1)));
            }

            using (ImportedStage again = ImportedStage.Import(path))
            {
                UsdAnimationPlayer player = again.Result.Animation;
                Assert.That(player, Is.Not.Null);
                GameObject mesh = again.Find(meshPrim);
                Assert.That(mesh, Is.Not.Null, "the mesh hidden later still has its prim");
                string root = meshPrim.Substring(0, meshPrim.IndexOf('/', 1));
                GameObject cubeMesh = again.Find(root + "/Cube/Cube_Mesh");
                Assert.That(cubeMesh, Is.Not.Null, "the cube's mesh prim");
                for (int f = 0; f < count; f++)
                {
                    player.EvaluateAtTimeCode(f);
                    Assert.That(mesh.activeSelf, Is.EqualTo(Enabled(f) && Active(f)), $"the imported mesh at frame {f}");
                    Assert.That(cubeMesh.activeSelf, Is.EqualTo(Enabled(f)), $"the cube's mesh at frame {f}");
                }
            }
        }

        [Test]
        public void TheRecorderRefusesWhatItCannotWrite()
        {
            var root = Track(new GameObject("Empty"));
            var recorder = new UsdAnimationRecorder(root);
            Assert.Throws<InvalidOperationException>(() => recorder.Save(Path.Combine(directory, "nothing.usda")));
            recorder.Capture(0.5);
            Assert.Throws<ArgumentException>(() => recorder.Capture(0.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UsdAnimationRecorder(root, 0.0));
        }

        // Two bones up a strip of six points, the middle pair weighted to both, the top pair moved
        // by a blend shape. The renderer sits off the root with a scale, so the bind transforms
        // have something to carry.
        private GameObject BuildRig(out SkinnedMeshRenderer renderer, out Transform hips, out Transform spine)
        {
            GameObject rig = Track(new GameObject("Rig"));
            rig.transform.position = new Vector3(1f, 0f, 0f);
            hips = new GameObject("Hips").transform;
            hips.SetParent(rig.transform, false);
            hips.localPosition = new Vector3(0f, 1f, 0f);
            spine = new GameObject("Spine").transform;
            spine.SetParent(hips, false);
            spine.localPosition = new Vector3(0f, 1f, 0f);

            var body = new GameObject("Body");
            body.transform.SetParent(rig.transform, false);
            body.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            body.transform.localScale = new Vector3(2f, 2f, 2f);
            renderer = body.AddComponent<SkinnedMeshRenderer>();

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
            var puff = new Vector3[6];
            puff[4] = puff[5] = new Vector3(0f, 0f, 0.3f);
            mesh.AddBlendShapeFrame("Puff", 100f, puff, null, null);

            renderer.sharedMesh = mesh;
            renderer.bones = new[] { hips, spine };
            renderer.rootBone = hips;
            return rig;
        }

        // Another renderer on `like`'s bones with a copy of its mesh; the caller sets its bind poses.
        private SkinnedMeshRenderer AddRenderer(GameObject rig, string name, Vector3 position, float scale,
                                                SkinnedMeshRenderer like)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(rig.transform, false);
            holder.transform.localPosition = position;
            holder.transform.localScale = Vector3.one * scale;
            SkinnedMeshRenderer renderer = holder.AddComponent<SkinnedMeshRenderer>();
            Mesh mesh = Track(Object.Instantiate(like.sharedMesh));
            mesh.name = name + "Mesh";
            renderer.sharedMesh = mesh;
            renderer.bones = like.bones;
            renderer.rootBone = like.rootBone;
            return renderer;
        }

        private T Track<T>(T item) where T : Object
        {
            created.Add(item);
            return item;
        }

        // skel_arm's Bulge goes from 0 to 1 over time codes 0 to 10; Unity's weights are 0-100.
        private static float BulgeWeight(double time) => (float)(time * 10.0);

        // What the renderer draws, vertex by vertex, in the space of `root`.
        private static Vector3[] RootLocalSkinned(SkinnedMeshRenderer renderer, Transform root)
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

        // The imported renderer draws `expected` (indexed by the source mesh's vertices), each of
        // its vertices matched to the source vertex it was written from by rest position.
        private static void AssertDraws(SkinnedMeshRenderer actual, Transform actualRoot, Mesh source, Vector3[] expected,
                                        string what)
        {
            Vector3[] rest = actual.sharedMesh.vertices;
            Vector3[] sourceVertices = source.vertices;
            Vector3[] drawn = RootLocalSkinned(actual, actualRoot);
            float worst = 0f;
            for (int v = 0; v < rest.Length; v++)
            {
                int s = Array.FindIndex(sourceVertices, p => (p - rest[v]).sqrMagnitude < 1e-10f);
                Assert.That(s, Is.GreaterThanOrEqualTo(0), $"{what}: vertex {v} at {rest[v]} matches no source vertex");
                worst = Mathf.Max(worst, Vector3.Distance(drawn[v], expected[s]));
            }

            Assert.That(worst, Is.LessThan(Tolerance), $"{what}: off by {worst}");
        }

        private static void AssertSameShape(IEnumerable<Vector3> actual, Vector3[] expected, string what)
        {
            Vector3[] points = actual.ToArray();
            Assert.That(points.Length, Is.EqualTo(expected.Length), $"{what}: vertex count");
            foreach (Vector3 point in points)
            {
                float nearest = expected.Min(e => Vector3.Distance(e, point));
                Assert.That(nearest, Is.LessThan(Tolerance), $"{what}: {point} is not a vertex the renderer drew");
            }
        }
    }
}
