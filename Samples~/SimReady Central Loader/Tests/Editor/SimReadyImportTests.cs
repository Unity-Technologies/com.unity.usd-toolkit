using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>
    /// The sample's import of its fixtures: units and axes, the physics mapping and its log, the
    /// joint model (handles, labels, ranges, coupling), kinematic posing against where USD's
    /// right-handed rotation puts a point, and the drives, simulated with Physics.Simulate.
    /// </summary>
    public sealed class SimReadyImportTests
    {
        private readonly List<GameObject> made = new List<GameObject>();
        private SimulationMode previousMode;
        private string temporary;

        [SetUp]
        public void SetUp()
        {
            Assume.That(UsdExporter.GetRuntimeInfo().NativeApiVersion, Is.GreaterThanOrEqualTo(13), "needs the physics import (API 13)");
            previousMode = Physics.simulationMode;
        }

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
            if (temporary != null && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }
        }

        private SimReadyAsset Import(string fixture)
        {
            SimReadyAsset asset = SimReadyLoader.ImportLocal(SimReadyFixtures.PathOf(fixture));
            made.Add(asset.gameObject);
            return asset;
        }

        private static Dictionary<string, UsdPhysicsJoint> Joints(SimReadyAsset asset) =>
            asset.GetComponentsInChildren<UsdPhysicsJoint>(true).ToDictionary(j => j.PrimPath);

        // A stage point (USD basis, stage units) in world space.
        private static Vector3 StageToWorld(SimReadyAsset asset, Vector3 stage) =>
            asset.transform.TransformPoint(new Vector3(-stage.x, stage.y, stage.z));

        // USD's right-handed rotation of a point about an axis through `center`.
        private static Vector3 RotateAbout(Vector3 point, Vector3 center, Vector3 axis, float degrees)
        {
            Vector3 k = axis.normalized;
            Vector3 v = point - center;
            float r = degrees * Mathf.Deg2Rad;
            return center + (v * Mathf.Cos(r)) + (Vector3.Cross(k, v) * Mathf.Sin(r)) + (k * Vector3.Dot(k, v) * (1f - Mathf.Cos(r)));
        }

        private static void AssertNear(Vector3 actual, Vector3 expected, float tolerance, string what = "") =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance), $"{what} expected {expected:F5}, got {actual:F5}");

        // ---- Units and axes ----

        [Test]
        public void ZUpCentimetresBecomeYUpMetres()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Assert.That(asset.Metadata.UpAxis, Is.EqualTo("Z"));
            Assert.That(asset.Metadata.MetersPerUnit, Is.EqualTo(0.01).Within(1e-9));
            // Stage +Z is world up, and one stage unit is a centimetre.
            Vector3 up = StageToWorld(asset, Vector3.forward) - StageToWorld(asset, Vector3.zero);
            Assert.That(Vector3.Dot(up.normalized, Vector3.up), Is.GreaterThan(0.9999f));
            Assert.That(up.magnitude, Is.EqualTo(0.01f).Within(1e-6f));
            // Asset extents from the metadata, in metres.
            Assert.That(asset.Metadata.ExtentsMeters.z, Is.EqualTo(0.07f).Within(1e-6f));
        }

        [Test]
        public void KilogramsPerUnitScalesTheMass()
        {
            temporary = SimReadyFixtures.TemporaryFolder();
            string grams = Path.Combine(temporary, "gripper_grams.usda");
            File.WriteAllText(grams, File.ReadAllText(SimReadyFixtures.PathOf("gripper.usda")).Replace("kilogramsPerUnit = 1", "kilogramsPerUnit = 0.001"));
            SimReadyAsset asset = SimReadyLoader.ImportLocal(grams);
            made.Add(asset.gameObject);
            UsdPhysicsBody basebody = asset.GetComponentsInChildren<UsdPhysicsBody>().First(b => b.PrimPath.EndsWith("/base"));
            Assert.That(basebody.GetComponent<ArticulationBody>().mass, Is.EqualTo(0.4f * 0.001f).Within(1e-7f));
            Assert.That(asset.Metadata.KilogramsPerUnit, Is.EqualTo(0.001).Within(1e-12));
        }

        // ---- Physics mapping ----

        [Test]
        public void EveryPhysicsPrimIsLogged()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Dictionary<string, SimReadyMappingRow> rows = asset.PhysicsLog.Where(r => r.Kind != "PhysxSchema").ToDictionary(r => r.PrimPath + "|" + r.Kind);

            Assert.That(rows["/test_gripper|Articulation"].Status, Is.EqualTo("mapped"));
            Assert.That(rows["/test_gripper/base|Body"].Unity, Is.EqualTo("ArticulationBody"));
            Assert.That(rows["/test_gripper/base/box|Collider"].Unity, Is.EqualTo("BoxCollider"));
            Assert.That(rows["/test_gripper/Physics/root_joint|Joint"].Status, Is.EqualTo("mapped"));
            Assert.That(rows["/test_gripper/Physics/finger_joint|Joint"].Unity, Is.EqualTo("ArticulationBody (Revolute joint)"));
            Assert.That(rows["/test_gripper/Physics/right_finger_joint|Joint"].Status, Is.EqualTo("approximated"), "a mimic drive, not a constraint");
            Assert.That(rows["/test_gripper/Physics/pad|Material"].Unity, Is.EqualTo("PhysicsMaterial"));

            // convexDecomposition of two separate boxes: two convex hulls.
            SimReadyMappingRow decomposed = rows["/test_gripper/left_finger/geo|Collider"];
            Assert.That(decomposed.Status, Is.EqualTo("approximated"));
            Assert.That(decomposed.Unity, Is.EqualTo("MeshCollider ×2 (convex)"));
            Assert.That(rows["/test_gripper/right_finger/geo|Collider"].Unity, Is.EqualTo("MeshCollider (convex)"));
        }

        [Test]
        public void PhysxAttributesAreAppliedRecordedOrLoggedNeverDropped()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Dictionary<string, string> physx = asset.PhysicsLog.Where(r => r.Kind == "PhysxSchema").ToDictionary(r => r.Unity, r => r.Status);
            Assert.That(physx["physxMimicJoint:rotY:gearing"], Is.EqualTo("applied"));
            Assert.That(physx["physxJoint:armature"], Is.EqualTo("applied"), "added to the link's inertia");
            Assert.That(physx["physxJoint:customUnmapped"], Is.EqualTo("logged"));
        }

        [Test]
        public void MassComesFromTheBodyItsCollidersOrDensityTimesVolume()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Dictionary<string, ArticulationBody> links = asset.GetComponentsInChildren<UsdPhysicsBody>().ToDictionary(b => b.PrimPath, b => b.GetComponent<ArticulationBody>());
            Assert.That(links["/test_gripper/base"].mass, Is.EqualTo(0.4f).Within(1e-6f), "authored on the body");
            Assert.That(links["/test_gripper/left_finger"].mass, Is.EqualTo(0.004f).Within(1e-6f), "authored on its collider");
            // 0.001 kg/cm³ (1000 kg/m³) over a 1 × 1 × 5 cm collider.
            Assert.That(links["/test_gripper/right_finger"].mass, Is.EqualTo(0.005f).Within(5e-5f), "density × volume");
            float total = links.Values.Sum(l => l.mass);
            Assert.That(total, Is.EqualTo((float)asset.Metadata.Mass).Within(0.01f * (float)asset.Metadata.Mass), "within 1 % of SimReady_Metadata");
        }

        // ---- The joint model ----

        [Test]
        public void TheGripperHasOneFingersHandleWithTheAuthoredRange()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Assert.That(asset.Handles.Count, Is.EqualTo(1));
            SimReadyJointHandle fingers = asset.Handles[0];
            Assert.That(fingers.Label, Is.EqualTo("Fingers"));
            Assert.That(fingers.Joint.PrimPath, Is.EqualTo("/test_gripper/Physics/finger_joint"));
            Assert.That((fingers.Min, fingers.Max), Is.EqualTo((0f, 60f)));
            Assert.That(fingers.RestValue, Is.EqualTo(30f), "JointStateAPI");
            Assert.That(fingers.Followers.Select(f => f.Follower.PrimPath), Is.EqualTo(new[] { "/test_gripper/Physics/right_finger_joint" }));
            Assert.That(asset.MovingParts, Is.EqualTo(1));
        }

        [Test]
        public void TheRestPoseReadsAsTheAuthoredState()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            Assert.That(joints["/test_gripper/Physics/finger_joint"].MeasurePosition(), Is.EqualTo(30f).Within(1e-3f));
            Assert.That(joints["/test_gripper/Physics/right_finger_joint"].MeasurePosition(), Is.EqualTo(-30f).Within(1e-3f));
        }

        [TestCase(0f)]
        [TestCase(50f)]
        [TestCase(60f)]
        public void EditModePosingMovesBothFingersAsUsdRotatesThem(float degrees)
        {
            SimReadyAsset asset = Import("gripper.usda");
            SimReadyJointHandle fingers = asset.Handles[0];
            asset.SetValue(fingers, degrees);
            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            Transform left = joints["/test_gripper/Physics/finger_joint"].MovingTransform;
            Transform right = joints["/test_gripper/Physics/right_finger_joint"].MovingTransform;

            // Each finger's tip, 5 cm up its own Z, swings about its hinge by the joint angle.
            Vector3 leftTip = RotateAbout(new Vector3(-2f, 0f, 6f), new Vector3(-2f, 0f, 1f), Vector3.up, degrees);
            Vector3 rightTip = RotateAbout(new Vector3(2f, 0f, 6f), new Vector3(2f, 0f, 1f), Vector3.up, -degrees);
            AssertNear(left.TransformPoint(new Vector3(0f, 0f, 5f)), StageToWorld(asset, leftTip), 1e-5f, "left tip");
            AssertNear(right.TransformPoint(new Vector3(0f, 0f, 5f)), StageToWorld(asset, rightTip), 1e-5f, "right tip");
            Assert.That(joints["/test_gripper/Physics/finger_joint"].MeasurePosition(), Is.EqualTo(degrees).Within(1e-3f));
            Assert.That(joints["/test_gripper/Physics/right_finger_joint"].MeasurePosition(), Is.EqualTo(-degrees).Within(1e-3f));
        }

        [Test]
        public void PosingClampsToTheLimitsAndResets()
        {
            SimReadyAsset asset = Import("gripper.usda");
            SimReadyJointHandle fingers = asset.Handles[0];
            Assert.That(asset.SetValue(fingers, 75f), Is.EqualTo(60f));
            Assert.That(asset.SetValue(fingers, -5f), Is.EqualTo(0f));
            asset.ResetToRest();
            Assert.That(fingers.Joint.MeasurePosition(), Is.EqualTo(30f).Within(1e-3f));
        }

        [Test]
        public void ThePrismaticGripperOpensSymmetrically()
        {
            SimReadyAsset asset = Import("prismatic_gripper.usda");
            Assert.That(asset.Handles.Count, Is.EqualTo(1));
            SimReadyJointHandle fingers = asset.Handles[0];
            Assert.That(fingers.Label, Is.EqualTo("Fingers"));
            Assert.That((fingers.Min, fingers.Max), Is.EqualTo((0f, 0.025f)));
            Assert.That(fingers.Angular, Is.False);

            asset.SetValue(fingers, 0.02f);
            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            Transform right = joints["/test_prismatic_gripper/Physics/finger_stroke"].MovingTransform;
            Transform left = joints["/test_prismatic_gripper/Physics/left_finger_base_joint"].MovingTransform;
            AssertNear(right.position, StageToWorld(asset, new Vector3(0.03f, 0f, 0.02f)), 1e-6f, "right");
            AssertNear(left.position, StageToWorld(asset, new Vector3(-0.03f, 0f, 0.02f)), 1e-6f, "left, mirrored by its localRot0");
            Assert.That(SimReadyJointMath.Caption(fingers.Label, asset.GetValue(fingers), false, asset.Metadata.MetersPerUnit), Is.EqualTo("Fingers · 20 mm"));
        }

        [Test]
        public void TheDragDirectionIsWhereTheJointMovesTheHandle()
        {
            SimReadyAsset asset = Import("prismatic_gripper.usda");
            SimReadyJointHandle fingers = asset.Handles[0];
            // Stage +X is world -X (the importer mirrors X); the stage is Z-up, so that stays horizontal.
            Vector3 motion = fingers.Joint.WorldMotionPerUnit(fingers.HandleWorld);
            AssertNear(motion.normalized, (StageToWorld(asset, Vector3.right) - StageToWorld(asset, Vector3.zero)).normalized, 1e-5f);
            Assert.That(motion.magnitude, Is.EqualTo(1f).Within(1e-5f), "metres per stage unit");
        }

        // ---- Simulated: the drives, with the physics stepped by hand ----

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
            for (float t = 0f; t < seconds; t += 0.005f)
            {
                foreach (UsdPhysicsMimicJoint mimic in mimics)
                {
                    mimic.Step();
                }

                Physics.Simulate(0.005f);
            }
        }

        [Test]
        public void AtRestNothingDrifts()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Physics.gravity = Vector3.zero;
            try
            {
                Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
                Simulate(asset, 1f);
                Assert.That(joints["/test_gripper/Physics/finger_joint"].GetPosition(), Is.EqualTo(30f).Within(0.5f));
                Assert.That(joints["/test_gripper/Physics/right_finger_joint"].GetPosition(), Is.EqualTo(-30f).Within(0.5f));
            }
            finally
            {
                Physics.gravity = new Vector3(0f, -9.81f, 0f);
            }
        }

        [Test]
        public void TheDriveMovesTheLeaderAndTheCouplingTheFollower()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Physics.gravity = Vector3.zero;
            try
            {
                Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
                joints["/test_gripper/Physics/finger_joint"].SetDriveTarget(50f);
                Simulate(asset, 3f);
                Assert.That(joints["/test_gripper/Physics/finger_joint"].GetPosition(), Is.EqualTo(50f).Within(1.5f));
                Assert.That(joints["/test_gripper/Physics/right_finger_joint"].GetPosition(), Is.EqualTo(-50f).Within(1.5f));
                // The physics and the kinematic posing agree on where the fingers are.
                Assert.That(joints["/test_gripper/Physics/finger_joint"].MeasurePosition(), Is.EqualTo(joints["/test_gripper/Physics/finger_joint"].GetPosition()).Within(0.1f));
            }
            finally
            {
                Physics.gravity = new Vector3(0f, -9.81f, 0f);
            }
        }

        [Test]
        public void TheFollowerKeepsUpWhileTheLeaderIsDragged()
        {
            SimReadyAsset asset = Import("gripper.usda");
            Physics.gravity = Vector3.zero;
            try
            {
                Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
                UsdPhysicsJoint leader = joints["/test_gripper/Physics/finger_joint"];
                UsdPhysicsMimicJoint mimic = asset.GetComponentsInChildren<UsdPhysicsMimicJoint>().Single();
                Simulate(asset, 0.5f);
                float worst = 0f;
                for (float time = 0f; time < 1f; time += 0.005f)
                {
                    leader.SetDriveTarget(Mathf.Lerp(30f, 55f, time));
                    mimic.Step();
                    Physics.Simulate(0.005f);
                    worst = Mathf.Max(worst, Mathf.Abs(mimic.Follower.GetPosition() - mimic.FollowerPosition(leader.GetPosition())));
                }

                Assert.That(worst, Is.LessThan(1f), "the coupling's target velocity keeps the follower with a moving leader");
            }
            finally
            {
                Physics.gravity = new Vector3(0f, -9.81f, 0f);
            }
        }

        [Test]
        public void JointsOutsideAnArticulationAreDrivenTheWayUsdTurnsThem()
        {
            SimReadyAsset asset = Import("cabinet.usda");
            Assert.That(asset.Handles.Select(h => h.Label), Is.EquivalentTo(new[] { "Door 01", "Drawer 01" }));
            Dictionary<string, UsdPhysicsJoint> joints = Joints(asset);
            UsdPhysicsJoint door = joints["/test_cabinet/Physics/joint_door_01_joint_00"];
            UsdPhysicsJoint drawer = joints["/test_cabinet/Physics/joint_drawer_01_joint_00"];
            Assert.That(door.UnityJoint, Is.InstanceOf<ConfigurableJoint>());
            Assert.That(asset.PhysicsLog.First(r => r.PrimPath == door.PrimPath).Detail, Does.Contain("ConfigurableJoint drive"));

            Physics.gravity = Vector3.zero;
            try
            {
                door.SetDriveTarget(30f);
                drawer.SetDriveTarget(0.1f);
                Simulate(asset, 3f);
                Assert.That(door.GetPosition(), Is.EqualTo(30f).Within(1.5f));
                Assert.That(drawer.GetPosition(), Is.EqualTo(0.1f).Within(0.005f));

                // The door's far edge, 0.5 m along stage +Y from the hinge, swings +30° about stage +Z.
                Vector3 edge = RotateAbout(new Vector3(0.3f, 0.25f, 0.75f), new Vector3(0.3f, -0.25f, 0.75f), Vector3.forward, 30f);
                AssertNear(door.MovingTransform.TransformPoint(new Vector3(0f, 0.5f, 0f)), StageToWorld(asset, edge), 0.01f, "door edge");
                AssertNear(drawer.MovingTransform.position, StageToWorld(asset, new Vector3(0.1f, 0f, 0.2f)), 0.005f, "drawer");
            }
            finally
            {
                Physics.gravity = new Vector3(0f, -9.81f, 0f);
            }
        }

        [Test]
        public void FreeJointsArePosedKinematicallyToo()
        {
            SimReadyAsset asset = Import("cabinet.usda");
            SimReadyJointHandle door = asset.Handles.First(h => h.Label == "Door 01");
            asset.SetValue(door, 45f);
            Vector3 edge = RotateAbout(new Vector3(0.3f, 0.25f, 0.75f), new Vector3(0.3f, -0.25f, 0.75f), Vector3.forward, 45f);
            AssertNear(door.Joint.MovingTransform.TransformPoint(new Vector3(0f, 0.5f, 0f)), StageToWorld(asset, edge), 1e-5f);
            Assert.That(door.Joint.MeasurePosition(), Is.EqualTo(45f).Within(1e-3f));
        }
    }
}
