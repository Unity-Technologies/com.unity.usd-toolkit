using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.USDToolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The stage's animation as an AnimationClip (UsdAnimationClipBuilder), and the Edit Mode preview
    /// that scrubs a player without changing the scene (UsdAnimationPreview).
    /// </summary>
    public sealed class UsdAnimationClipTests
    {
        private const float PositionTolerance = 1e-4f;
        private const float AngleTolerance = 0.05f;

        [Test]
        public void TheClipPosesWhatThePlayerPoses()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                AnimationClip clip = UsdAnimationClipBuilder.Build(player);
                try
                {
                    Assert.That(clip.legacy, Is.False, "a generic clip, for an Animator");
                    Assert.That(clip.length, Is.EqualTo(10f / 24f).Within(1e-5f));
                    Assert.That(AnimationUtility.GetAnimationClipSettings(clip).stopTime, Is.EqualTo(10f / 24f).Within(1e-5f));
                    Assert.That(AnimationUtility.GetCurveBindings(clip).Any(b => b.propertyName == "m_IsActive"), Is.True);
                    Assert.That(AnimationUtility.GetCurveBindings(clip).Any(b => b.propertyName.StartsWith("blendShape.")), Is.True);

                    List<Transform> transforms = Targets(player, out List<GameObject> toggled, out List<SkinnedMeshRenderer> renderers);
                    foreach (double timeCode in new[] { 0.0, 2.5, 3.3, 5.0, 7.5, 9.9, 10.0 })
                    {
                        player.EvaluateAtTimeCode(timeCode);
                        Snapshot expected = Snapshot.Of(transforms, toggled, renderers);
                        player.EvaluateAtTimeCode(10.0 - timeCode); // somewhere else, so the clip has to move it back
                        clip.SampleAnimation(player.gameObject, (float)(timeCode / 24.0));
                        expected.AssertMatches(Snapshot.Of(transforms, toggled, renderers), $"t={timeCode}");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(clip);
                }
            }
        }

        // Isaac Sim's characters write their joints a second time as SkelJoint prims below the
        // Skeleton prim, with the joints' names. Those prims are left out, so the clip's paths
        // reach the joints the player poses.
        [Test]
        public void TheClipMovesJointsThatSkelJointPrimsMirror()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "joint_mirror.usda")))
            {
                Transform skeleton = stage.Find("/Biped/Root").transform;
                Assert.That(skeleton.Cast<Transform>().Count(child => child.name == "Root"), Is.EqualTo(1),
                            "one object called Root below the Skeleton prim");
                Transform root = skeleton.Find("Root");
                Assert.That(root.GetComponent<UsdImportHelperObject>(), Is.Not.Null, "and it is the joint");
                Assert.That(root.GetComponent<UsdPrimSource>(), Is.Null);
                Transform hip = root.Find("Hip");
                Assert.That(skeleton.GetComponent<UsdSkeleton>().Joints, Is.EqualTo(new[] { root, hip }));

                UsdAnimationPlayer player = stage.Result.Animation;
                AnimationClip clip = UsdAnimationClipBuilder.Build(player);
                try
                {
                    foreach (double timeCode in new[] { 2.5, 5.0, 10.0 })
                    {
                        player.EvaluateAtTimeCode(timeCode);
                        Vector3 rootPosition = root.localPosition;
                        Quaternion rootRotation = root.localRotation, hipRotation = hip.localRotation;
                        player.EvaluateAtTimeCode(0.0);
                        clip.SampleAnimation(player.gameObject, (float)(timeCode / 24.0));
                        Assert.That(Vector3.Distance(root.localPosition, rootPosition), Is.LessThan(PositionTolerance), $"t={timeCode}");
                        Assert.That(Quaternion.Angle(root.localRotation, rootRotation), Is.LessThan(AngleTolerance), $"t={timeCode}");
                        Assert.That(Quaternion.Angle(hip.localRotation, hipRotation), Is.LessThan(AngleTolerance), $"t={timeCode}");
                    }

                    Assert.That(Quaternion.Angle(hip.localRotation, Quaternion.identity), Is.GreaterThan(45f), "the joints really moved");
                }
                finally
                {
                    Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void AClipWarnsWhenASiblingTakesATargetsName()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "joint_mirror.usda")))
            {
                Transform skeleton = stage.Find("/Biped/Root").transform;
                var impostor = new GameObject("Root");
                impostor.transform.SetParent(skeleton, false);
                impostor.transform.SetAsFirstSibling();
                LogAssert.Expect(LogType.Warning, new Regex(@"share their path with an earlier sibling.*first: 'Biped/Root/Root'"));
                Object.DestroyImmediate(UsdAnimationClipBuilder.Build(stage.Result.Animation));
            }
        }

        [Test]
        public void APreviewPutsTheSceneBack()
        {
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdAnimationPlayer player = stage.Result.Animation;
                List<Transform> transforms = Targets(player, out List<GameObject> toggled, out List<SkinnedMeshRenderer> renderers);
                var pointCache = stage.Find("/Root/PointCache").GetComponent<MeshRenderer>();
                Snapshot before = Snapshot.Of(transforms, toggled, renderers);
                Assert.That(pointCache.additionalVertexStreams, Is.Null, "an import leaves the read-time frame in the Mesh");

                Assert.That(UsdAnimationPreview.CanStart(player), Is.True);
                UsdAnimationPreview preview = UsdAnimationPreview.Start(player);
                Assert.That(preview, Is.Not.Null);
                try
                {
                    Assert.That(preview.IsActive, Is.True);
                    Assert.That(UsdAnimationPreview.CanStart(player), Is.False, "one animation-mode preview at a time");
                    preview.SetTime(5f / 24f);
                    player.EvaluateAtTimeCode(5.0);
                    Snapshot atFive = Snapshot.Of(transforms, toggled, renderers);
                    Assert.That(atFive.Differs(before), Is.True, "the preview poses the stage");
                    Assert.That(pointCache.additionalVertexStreams, Is.Not.Null, "and draws the point cache");
                }
                finally
                {
                    preview.Dispose();
                }

                Assert.That(preview.IsActive, Is.False);
                before.AssertMatches(Snapshot.Of(transforms, toggled, renderers), "after the preview");
                Assert.That(pointCache.additionalVertexStreams, Is.Null, "the imported Mesh shows again");
                Assert.That(player.CurrentTime, Is.EqualTo(0.0), "and the player is back at its time");
            }
        }

        private static List<Transform> Targets(UsdAnimationPlayer player, out List<GameObject> toggled,
                                               out List<SkinnedMeshRenderer> renderers)
        {
            var transforms = new List<Transform>();
            toggled = new List<GameObject>();
            renderers = new List<SkinnedMeshRenderer>();
            player.CollectAnimatedTargets(transforms, toggled, renderers);
            Assert.That(transforms.Count, Is.GreaterThan(3));
            Assert.That(toggled.Count, Is.GreaterThan(0));
            Assert.That(renderers.Count, Is.GreaterThan(0));
            return transforms;
        }

        // The state the animation drives: local poses, active states and blend-shape weights.
        private sealed class Snapshot
        {
            private readonly List<Transform> transforms = new List<Transform>();
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<Quaternion> rotations = new List<Quaternion>();
            private readonly List<Vector3> scales = new List<Vector3>();
            private readonly List<bool> active = new List<bool>();
            private readonly List<float> weights = new List<float>();

            public static Snapshot Of(List<Transform> transforms, List<GameObject> toggled, List<SkinnedMeshRenderer> renderers)
            {
                var snapshot = new Snapshot();
                foreach (Transform t in transforms)
                {
                    snapshot.transforms.Add(t);
                    snapshot.positions.Add(t.localPosition);
                    snapshot.rotations.Add(t.localRotation);
                    snapshot.scales.Add(t.localScale);
                }

                snapshot.active.AddRange(toggled.Select(g => g.activeSelf));
                foreach (SkinnedMeshRenderer renderer in renderers)
                {
                    for (int s = 0; s < renderer.sharedMesh.blendShapeCount; s++)
                    {
                        snapshot.weights.Add(renderer.GetBlendShapeWeight(s));
                    }
                }

                return snapshot;
            }

            public bool Differs(Snapshot other)
            {
                for (int i = 0; i < positions.Count; i++)
                {
                    if ((positions[i] - other.positions[i]).sqrMagnitude > 1e-8f || Quaternion.Angle(rotations[i], other.rotations[i]) > 1e-3f)
                    {
                        return true;
                    }
                }

                return !active.SequenceEqual(other.active) || !weights.SequenceEqual(other.weights);
            }

            public void AssertMatches(Snapshot actual, string when)
            {
                for (int i = 0; i < positions.Count; i++)
                {
                    string name = transforms[i].name;
                    Assert.That(Vector3.Distance(actual.positions[i], positions[i]), Is.LessThan(PositionTolerance), $"{name} position {when}");
                    Assert.That(Quaternion.Angle(actual.rotations[i], rotations[i]), Is.LessThan(AngleTolerance), $"{name} rotation {when}");
                    Assert.That(Vector3.Distance(actual.scales[i], scales[i]), Is.LessThan(PositionTolerance), $"{name} scale {when}");
                }

                Assert.That(actual.active, Is.EqualTo(active), $"active states {when}");
                for (int i = 0; i < weights.Count; i++)
                {
                    Assert.That(actual.weights[i], Is.EqualTo(weights[i]).Within(1e-3f), $"blend-shape weight {i} {when}");
                }
            }
        }
    }
}
