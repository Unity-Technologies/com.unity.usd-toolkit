using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>
    /// The acceptance checks against the real assets on Hugging Face (pinned to the commits SimReady
    /// Central lists). They download, so they only run when asked for: select them in the Test
    /// Runner, or pass <c>-testCategory Network</c>. Downloads go to the sample's cache.
    /// </summary>
    [Explicit("Downloads assets from huggingface.co")]
    [Category("Network")]
    public sealed class SimReadyCentralAcceptanceTests
    {
        public const string Rg2 =
            "https://huggingface.co/datasets/onrobot/simready-grippers/blob/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_rg2/simready_usd/onrobot_rg2.usda";

        public const string Fg14 =
            "https://huggingface.co/datasets/onrobot/simready-grippers/blob/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_2fg14/simready_usd/onrobot_2fg14.usda";

        private const string NvidiaAssets = "https://huggingface.co/datasets/nvidia/simready-assets/blob/2464ca32165bfff81ba34bc703ddb932822cfd27/";

        public static readonly string[] NonGrippers =
        {
            NvidiaAssets + "cabinet_file_a01/simready_usd/sm_cabinet_file_a01_01.usd",
            NvidiaAssets + "kitchenware_kettle_a01/simready_usd/sm_kitchenware_kettle_a01_01.usd",
            "https://huggingface.co/datasets/LGElectronics/simready-assets/blob/70ea043e1a85455227e516766443d1fa235a5ea0/cooking_ldgl6924s/simready_usd/cooking_ldgl6924s.usd",
        };

        private readonly List<GameObject> made = new List<GameObject>();
        private SimulationMode previousMode;

        [SetUp]
        public void SetUp() => previousMode = Physics.simulationMode;

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in made)
            {
                if (root != null)
                {
                    Object.DestroyImmediate(root);
                }
            }

            made.Clear();
            Physics.simulationMode = previousMode;
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
        }

        private async Task<SimReadyAsset> Load(string link)
        {
            SimReadyLoader.LoadResult result = await SimReadyLoader.LoadAsync(link, SimReadyHostPolicy.CreateDefault(), null, null, CancellationToken.None);
            made.Add(result.Asset.gameObject);
            Debug.Log($"{result.Asset.Metadata.AssetName}: {result.Download.Files.Count} files, {result.Download.TotalBytes} bytes, " +
                      $"handles [{string.Join(", ", result.Asset.Handles.Select(h => $"{h.Label} {h.Min}..{h.Max}"))}], " +
                      $"physics {SimReadyPhysicsReport.Summary(result.Asset.PhysicsLog)}");
            return result.Asset;
        }

        private static Dictionary<string, UsdPhysicsJoint> Joints(SimReadyAsset asset) =>
            asset.GetComponentsInChildren<UsdPhysicsJoint>(true).ToDictionary(j => j.PrimPath.Substring(j.PrimPath.LastIndexOf('/') + 1));

        private static Bounds RenderBounds(SimReadyAsset asset)
        {
            Renderer[] renderers = asset.GetComponentsInChildren<Renderer>();
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            return bounds;
        }

        // Mass and extents within 1 % of SimReady_Metadata (Z-up stage: world Y is stage Z).
        private static void AssertMassAndExtents(SimReadyAsset asset)
        {
            float mass = asset.GetComponentsInChildren<ArticulationBody>().Sum(b => b.mass) + asset.GetComponentsInChildren<Rigidbody>().Sum(b => b.mass);
            Assert.That(mass, Is.EqualTo((float)asset.Metadata.MassKilograms).Within(0.01f * (float)asset.Metadata.MassKilograms), "mass");
            Vector3 size = RenderBounds(asset).size;
            Vector3 extents = asset.Metadata.ExtentsMeters;
            Assert.That(size.x, Is.EqualTo(extents.x).Within(0.01f * extents.x), "extent X");
            Assert.That(size.y, Is.EqualTo(extents.z).Within(0.01f * extents.z), "extent Z (up)");
            Assert.That(size.z, Is.EqualTo(extents.y).Within(0.01f * extents.y), "extent Y");
        }

        // The RG2's two sides are mirror images through the stage's X = 0 plane: each right link's
        // local +X point (3 cm out) is the left one's with X negated. Checked for the inner and
        // outer proximal links and the finger bases, which the coupling moves.
        private static void AssertMirrored(SimReadyAsset asset, string when)
        {
            var bodies = asset.GetComponentsInChildren<UsdPhysicsBody>().ToDictionary(b => b.PrimPath.Substring(b.PrimPath.LastIndexOf('/') + 1), b => b.transform);
            Vector3 Stage(Vector3 world)
            {
                Vector3 local = asset.transform.InverseTransformPoint(world);
                return new Vector3(-local.x, local.y, local.z);
            }

            foreach (string link in new[] { "inner_proximal_finger_link", "outer_proximal_finger_link", "finger_base_link" })
            {
                // USD's local +X is Unity's local -X (the importer mirrors X).
                Vector3 right = Stage(bodies["right_" + link].TransformPoint(new Vector3(-0.03f, 0f, 0f)));
                Vector3 left = Stage(bodies["left_" + link].TransformPoint(new Vector3(-0.03f, 0f, 0f)));
                Assert.That(Vector3.Distance(right, new Vector3(-left.x, left.y, left.z)), Is.LessThan(0.0005f),
                            $"{when}: right_{link} {right:F4} mirrors left_{link} {left:F4}");
            }
        }

        private static void Simulate(SimReadyAsset asset, float seconds, bool start = true)
        {
            if (start)
            {
                // What UsdPhysicsJoint.Start does when play mode begins: articulated joints start at their
                // authored JointStateAPI position (Unity's articulation starts every joint at 0).
                foreach (UsdPhysicsJoint joint in asset.GetComponentsInChildren<UsdPhysicsJoint>())
                {
                    if (joint.Articulated && joint.HasInitialPosition)
                    {
                        joint.SetPosition(joint.InitialPosition);
                    }
                }
            }

            Physics.simulationMode = SimulationMode.Script;
            UsdPhysicsMimicJoint[] mimics = asset.GetComponentsInChildren<UsdPhysicsMimicJoint>();
            float step = Time.fixedDeltaTime;
            for (float t = 0f; t < seconds; t += step)
            {
                foreach (UsdPhysicsMimicJoint mimic in mimics)
                {
                    mimic.Step();
                }

                Physics.Simulate(step);
            }
        }

        [Test]
        public async Task OnRobotRg2()
        {
            SimReadyAsset asset = await Load(Rg2);
            Assert.That(asset.Handles.Select(h => h.Label), Is.EqualTo(new[] { "Fingers" }));
            SimReadyJointHandle fingers = asset.Handles[0];
            Assert.That(fingers.Min, Is.EqualTo(0f));
            Assert.That(fingers.Max, Is.EqualTo(70.06f).Within(1e-3f));
            Assert.That(fingers.RestValue, Is.EqualTo(60f));
            Assert.That(fingers.Followers.Count, Is.EqualTo(9));
            Assert.That(asset.Metadata.Profile, Is.EqualTo("Robot-Gripper 3.0.0"));
            AssertMassAndExtents(asset);

            // Edit mode: the two fingers open and close as mirror images.
            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            Transform body = joints["finger_joint"].FrameTransform;
            Transform rightTip = joints["right_finger_tip_joint"].MovingTransform;
            Transform leftTip = joints["left_finger_tip_joint"].MovingTransform;
            foreach (float value in new[] { 0f, 35f, 70.06f })
            {
                asset.SetValue(fingers, value);
                Assert.That(joints["left_outer_proximal_finger_joint"].MeasurePosition(), Is.EqualTo(value).Within(0.01f));
                Assert.That(joints["right_finger_cover_arm_joint"].MeasurePosition(), Is.EqualTo(-value).Within(0.01f));
                float right = Vector3.Distance(rightTip.position, body.position);
                float left = Vector3.Distance(leftTip.position, body.position);
                Assert.That(left, Is.EqualTo(right).Within(0.001f), $"fingertips at {value}°");
                AssertMirrored(asset, $"edit mode at {value}°");
            }

            asset.ResetToRest();

            // Play mode physics: at rest it stays put; driven, the linkage follows.
            Simulate(asset, 2f);
            Assert.That(joints["finger_joint"].GetPosition(), Is.EqualTo(60f).Within(1f), "no drift at rest");
            Assert.That(joints["left_outer_proximal_finger_joint"].GetPosition(), Is.EqualTo(60f).Within(1f));
            joints["finger_joint"].SetDriveTarget(20f);
            Simulate(asset, 4f, false);
            Assert.That(joints["finger_joint"].GetPosition(), Is.EqualTo(20f).Within(1f));
            Assert.That(joints["left_outer_proximal_finger_joint"].GetPosition(), Is.EqualTo(joints["finger_joint"].GetPosition()).Within(0.5f), "symmetric");

            // Every follower of the linkage holds the angle its coupling gives it, the lightest
            // links (the finger cover arms, ~5e-9 kg·m² before their armature) included.
            AssertMirrored(asset, "play mode driven to 20°");

            // Dragged from where it is to 70° in 1.5 s, the way a handle moves it (smoothly, not in a
            // jump): every follower keeps up with its leader. It trailed by 3.5° without the
            // coupling's velocity feed-forward.
            float worstLag = 0f;
            float from = joints["finger_joint"].GetPosition();
            UsdPhysicsMimicJoint[] mimics = asset.GetComponentsInChildren<UsdPhysicsMimicJoint>();
            for (float time = 0f; time < 1.5f; time += Time.fixedDeltaTime)
            {
                joints["finger_joint"].SetDriveTarget(Mathf.Lerp(from, 70f, time / 1.5f));
                foreach (UsdPhysicsMimicJoint mimic in mimics)
                {
                    mimic.Step();
                }

                Physics.Simulate(Time.fixedDeltaTime);
                float now = joints["finger_joint"].GetPosition();
                foreach (UsdPhysicsMimicJoint mimic in mimics)
                {
                    worstLag = Mathf.Max(worstLag, Mathf.Abs(mimic.Follower.GetPosition() - mimic.FollowerPosition(now)));
                }
            }

            Assert.That(worstLag, Is.LessThan(1f), "followers keep up while the fingers are dragged");
            float leader = joints["finger_joint"].GetPosition();
            foreach (UsdPhysicsMimicJoint follower in fingers.Followers)
            {
                float expected = SimReadyJointMath.FollowerPosition(leader, follower.Gearing, follower.Offset, true, true);
                Assert.That(follower.Follower.GetPosition(), Is.EqualTo(expected).Within(0.5f), follower.Follower.PrimPath);
            }
        }

        [Test]
        public async Task OnRobot2Fg14()
        {
            SimReadyAsset asset = await Load(Fg14);
            Assert.That(asset.Handles.Select(h => h.Label), Is.EqualTo(new[] { "Fingers" }));
            SimReadyJointHandle fingers = asset.Handles[0];
            Assert.That(fingers.Angular, Is.False);
            Assert.That(fingers.Min, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(fingers.Max, Is.EqualTo(0.025f).Within(1e-6f));
            AssertMassAndExtents(asset);

            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            Transform right = joints["finger_stroke"].MovingTransform;
            Transform left = joints["left_finger_base_joint"].MovingTransform;
            Transform body = joints["finger_stroke"].FrameTransform;
            foreach (float value in new[] { 0f, 0.0125f, 0.025f })
            {
                asset.SetValue(fingers, value);
                Assert.That(joints["left_finger_base_joint"].MeasurePosition(), Is.EqualTo(value).Within(1e-5f));
                Assert.That(Vector3.Distance(left.position, body.position), Is.EqualTo(Vector3.Distance(right.position, body.position)).Within(0.0005f));
            }

            asset.ResetToRest();
            Simulate(asset, 2f);
            Assert.That(joints["finger_stroke"].GetPosition(), Is.EqualTo(0f).Within(0.001f), "no drift at rest");
            joints["finger_stroke"].SetDriveTarget(0.02f);
            Simulate(asset, 3f, false);
            Assert.That(joints["finger_stroke"].GetPosition(), Is.EqualTo(0.02f).Within(0.002f));
            Assert.That(joints["left_finger_base_joint"].GetPosition(), Is.EqualTo(joints["finger_stroke"].GetPosition()).Within(0.002f), "symmetric");
        }

        [TestCaseSource(nameof(NonGrippers))]
        public async Task NonGrippersStandStillOnTheGround(string link)
        {
            SimReadyAsset asset = await Load(link);
            Assert.That(asset.NeedsGround, Is.True, "a prop is not fixed to the world");
            GameObject ground = asset.CreateGround();
            made.Add(ground);
            Rigidbody[] bodies = asset.GetComponentsInChildren<Rigidbody>().Where(b => !b.isKinematic).ToArray();
            Vector3[] start = bodies.Select(b => b.position).ToArray();
            Simulate(asset, 2f);
            for (int i = 0; i < bodies.Length; i++)
            {
                Assert.That(Vector3.Distance(bodies[i].position, start[i]), Is.LessThan(0.02f), bodies[i].name + " stays where it was");
            }

            // Each part holds its rest pose, unless gravity turns an undriven part to its limit, as it
            // does in Isaac (the kettle's switch drops to -8°). No approximation overlap pushes one.
            foreach (SimReadyJointHandle handle in asset.Handles)
            {
                float position = handle.Joint.GetPosition();
                float tolerance = handle.Angular ? 1f : 0.002f;
                bool atRest = Mathf.Abs(position - handle.RestValue) <= tolerance;
                bool droppedToLimit = handle.Joint.DriveStiffness <= 0f &&
                                      (Mathf.Abs(position - handle.Min) <= tolerance || Mathf.Abs(position - handle.Max) <= tolerance);
                Assert.That(atRest || droppedToLimit, Is.True, $"{handle.Label} at {position} (rest {handle.RestValue}, range {handle.Min}..{handle.Max})");
            }
        }

        [TestCaseSource(nameof(NonGrippers))]
        public async Task NonGripperAssetsLoadWithPhysicsAndHandles(string link)
        {
            SimReadyAsset asset = await Load(link);
            Assert.That(asset.PhysicsLog.Count(r => r.Kind == "Body" && r.Status != SimReadyPhysicsReport.Skipped), Is.EqualTo(asset.Metadata.RigidBodyCount),
                        "every rigid body the metadata counts is built");
            Assert.That(asset.Handles.Count, Is.GreaterThan(0), "moving parts have handles");
            foreach (SimReadyJointHandle handle in asset.Handles)
            {
                Assert.That(handle.Label, Is.Not.Empty);
                asset.SetValue(handle, handle.Max);
                Assert.That(handle.Joint.MeasurePosition(), Is.EqualTo(handle.Max).Within(handle.Angular ? 0.05f : 1e-4f), handle.Label);
                asset.SetValue(handle, handle.RestValue);
            }
        }
    }
}
