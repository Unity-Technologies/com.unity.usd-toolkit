using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// UsdImportOptions.ImportPhysics on Tests/Fixtures~/Physics/isaac_style_arm.usda, an arm laid out
    /// like an Isaac Sim robot. The first tests check that every Unity joint and body reads back as the
    /// stage authors it; the simulated ones check the conventions behind that mapping, by driving each
    /// kind of joint and comparing the link's pose with where USD's right-handed rotation puts it.
    /// </summary>
    public sealed class UsdPhysicsImportTests
    {
        private const string Folder = "Physics";
        private const string Fixture = "isaac_style_arm.usda";
        private const string MimicFixture = "mimic_gripper.usda";

        private GameObject root;
        private SimulationMode previousMode;
        private Vector3 previousGravity;

        [SetUp]
        public void SetUp()
        {
            previousMode = Physics.simulationMode;
            previousGravity = Physics.gravity;
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
            }

            Physics.simulationMode = previousMode;
            Physics.gravity = previousGravity;
        }

        private UsdImportResult Import(bool physics = true, string fixture = Fixture)
        {
            Assume.That(UsdExporter.GetRuntimeInfo().NativeApiVersion, Is.GreaterThanOrEqualTo(13),
                        "The loaded native plugin has no physics import (API 13).");
            UsdImportResult result = UsdImporter.Import(UsdFixtures.PathOf(Folder, fixture), new UsdImportOptions
            {
                ImportPhysics = physics,
                ImportMaterials = false,
                InteriorReflection = false,
            });
            root = result.RootObject;

            // An API 13 plugin built without the physics reader says so in the notes.
            string unavailable = physics
                ? result.PhysicsNotes?.FirstOrDefault(note => note.StartsWith("Physics not imported", StringComparison.Ordinal))
                : null;
            Assume.That(unavailable, Is.Null, unavailable);
            return result;
        }

        private Dictionary<string, UsdPhysicsJoint> Joints()
        {
            var joints = new Dictionary<string, UsdPhysicsJoint>();
            foreach (UsdPhysicsJoint joint in root.GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                joints[joint.PrimPath] = joint;
            }

            return joints;
        }

        [Test]
        public void WithoutTheOptionNoPhysicsIsBuilt()
        {
            Import(physics: false);
            Assert.That(root.GetComponentsInChildren<ArticulationBody>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<UsdPhysicsJoint>(true), Is.Empty);
        }

        [Test]
        public void ArticulationBecomesALinkTreeWithAFixedBase()
        {
            Import();
            ArticulationBody[] links = root.GetComponentsInChildren<ArticulationBody>(true);
            Assert.That(links.Length, Is.EqualTo(5));

            var byPath = new Dictionary<string, ArticulationBody>();
            foreach (ArticulationBody link in links)
            {
                byPath[link.GetComponent<UsdPhysicsBody>().PrimPath] = link;
            }

            ArticulationBody baseLink = byPath["/arm_robot/base_link"];
            Assert.That(baseLink.isRoot, Is.True);
            Assert.That(baseLink.immovable, Is.True, "rootJoint ties the base to the world");
            Assert.That(baseLink.solverIterations, Is.EqualTo(32));

            // The links are siblings in the stage; Unity needs each under its parent link.
            Assert.That(ParentLink(byPath["/arm_robot/shoulder_link"]), Is.SameAs(baseLink));
            Assert.That(ParentLink(byPath["/arm_robot/upper_arm"]), Is.SameAs(byPath["/arm_robot/shoulder_link"]));
            Assert.That(ParentLink(byPath["/arm_robot/forearm"]), Is.SameAs(byPath["/arm_robot/upper_arm"]));
            Assert.That(ParentLink(byPath["/arm_robot/gripper"]), Is.SameAs(byPath["/arm_robot/forearm"]));

            // Moving a link keeps its world pose: the gripper is still 0.93 m up (Z-up stage).
            Vector3 gripper = byPath["/arm_robot/gripper"].transform.position;
            Assert.That(Vector3.Distance(gripper, UsdFixtures.StageToWorld(root.transform, new Vector3(0f, 0f, 0.93f))), Is.LessThan(1e-4f));

            Assert.That(byPath["/arm_robot/upper_arm"].mass, Is.EqualTo(1.2f).Within(1e-5f));
            Assert.That(byPath["/arm_robot/upper_arm"].automaticCenterOfMass, Is.False);
        }

        [Test]
        public void EveryJointReadsBackAsAuthored()
        {
            Import();
            Dictionary<string, UsdPhysicsJoint> joints = Joints();
            Assert.That(joints.Keys, Is.EquivalentTo(new[]
            {
                "/arm_robot/base_link/shoulder_yaw", "/arm_robot/shoulder_link/shoulder_pitch",
                "/arm_robot/upper_arm/elbow", "/arm_robot/forearm/slider", "/pendulum_hinge",
            }));

            // The values as the stage authors them (what Isaac Sim's Property panel shows).
            AssertAuthored(joints["/arm_robot/base_link/shoulder_yaw"], UsdPhysicsJointType.Revolute, -150f, 150f, 400f, 40f, 87f);
            AssertAuthored(joints["/arm_robot/shoulder_link/shoulder_pitch"], UsdPhysicsJointType.Revolute, -10f, 135f, 600f, 60f, 87f);
            AssertAuthored(joints["/arm_robot/upper_arm/elbow"], UsdPhysicsJointType.Revolute, -120f, 5f, 300f, 30f, 50f);
            AssertAuthored(joints["/arm_robot/forearm/slider"], UsdPhysicsJointType.Prismatic, 0f, 0.08f, 400f, 80f, 20f);
            Assert.That(joints["/arm_robot/upper_arm/elbow"].HasInitialPosition, Is.True);
            Assert.That(joints["/arm_robot/upper_arm/elbow"].InitialPosition, Is.EqualTo(-30f).Within(1e-4f));

            // The axis frames turn shoulder_pitch's X onto world Y.
            Assert.That(Vector3.Angle(joints["/arm_robot/shoulder_link/shoulder_pitch"].AxisInStage, Vector3.up), Is.LessThan(0.01f));

            foreach (UsdPhysicsJoint joint in joints.Values)
            {
                Assert.That(joint.TryReadUnityValues(out UsdPhysicsJointReadback unity), Is.True, joint.PrimPath);
                Assert.That(unity.LimitEnabled, Is.EqualTo(joint.LimitEnabled), joint.PrimPath);
                Assert.That(unity.LowerLimit, Is.EqualTo(joint.LowerLimit).Within(1e-3f), joint.PrimPath + " lower");
                Assert.That(unity.UpperLimit, Is.EqualTo(joint.UpperLimit).Within(1e-3f), joint.PrimPath + " upper");
                Assert.That(Vector3.Angle(unity.AxisInStage, joint.AxisInStage), Is.LessThan(0.05f), joint.PrimPath + " axis");
                if (joint.Articulated)
                {
                    Assert.That(unity.DriveStiffness, Is.EqualTo(joint.DriveStiffness).Within(joint.DriveStiffness * 1e-5f), joint.PrimPath);
                    Assert.That(unity.DriveDamping, Is.EqualTo(joint.DriveDamping).Within(joint.DriveDamping * 1e-5f), joint.PrimPath);
                    Assert.That(unity.DriveMaxForce, Is.EqualTo(joint.DriveMaxForce).Within(joint.DriveMaxForce * 1e-5f), joint.PrimPath);
                    Assert.That(unity.DriveTargetPosition, Is.EqualTo(joint.DriveTargetPosition).Within(1e-4f), joint.PrimPath);
                }
            }

            // Isaac Sim's angular stiffness is per degree; PhysX's, which Unity passes on, per radian.
            Assert.That(joints["/arm_robot/base_link/shoulder_yaw"].Link.xDrive.stiffness, Is.EqualTo(400f * Mathf.Rad2Deg).Within(0.01f));
            Assert.That(joints["/arm_robot/base_link/shoulder_yaw"].Link.jointFriction, Is.EqualTo(0.05f).Within(1e-6f));
        }

        [Test]
        public void CollidersMaterialsAndFilteringAreBuilt()
        {
            UsdImportResult result = Import();
            var colliders = new Dictionary<string, Collider>();
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                colliders[collider.gameObject.name] = collider;
            }

            Assert.That(colliders["Collider body"], Is.Not.Null);
            var hull = colliders["Collider collisions"] as MeshCollider;
            Assert.That(hull, Is.Not.Null, "the guide-purpose collision mesh still collides");
            Assert.That(hull.convex, Is.True);
            Assert.That(hull.sharedMesh.vertexCount, Is.EqualTo(8));

            Assert.That(root.GetComponentsInChildren<UsdPhysicsBody>(true).Length, Is.EqualTo(6), "five links and the pendulum bob");

            PhysicsMaterial rubber = null;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.sharedMaterial != null)
                {
                    rubber = collider.sharedMaterial;
                }
            }

            Assert.That(rubber, Is.Not.Null);
            Assert.That(rubber.staticFriction, Is.EqualTo(0.9f).Within(1e-6f));
            Assert.That(rubber.dynamicFriction, Is.EqualTo(0.8f).Within(1e-6f));

            // enabledSelfCollisions = 0: every pair of the arm's colliders is filtered.
            UsdPhysicsCollisionFilter filter = root.GetComponent<UsdPhysicsCollisionFilter>();
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.PairCount, Is.EqualTo(10), "five link colliders, ten pairs");

            UsdPhysicsScene scene = root.GetComponent<UsdPhysicsScene>();
            Assert.That(scene.TimeStepsPerSecond, Is.EqualTo(120f));
            Assert.That(scene.GravityMagnitude, Is.EqualTo(9.81f).Within(1e-4f));
            Assert.That(root.transform.TransformDirection(scene.GravityDirection).y, Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(result.PhysicsNotes, Is.Empty, string.Join("\n", result.PhysicsNotes));
        }

        [Test]
        public void RevoluteDriveTurnsTheLinkTheWayUsdDoes()
        {
            Import();
            UsdPhysicsJoint pitch = Joints()["/arm_robot/shoulder_link/shoulder_pitch"];
            Transform upperArm = pitch.Link.transform;

            // +60 degrees about the joint axis, at the joint, rotates the upper arm's origin
            // (0, 0, 0.4) around (0, 0, 0.2) as USD's right-handed rotation does.
            Vector3 expected = RotateAbout(new Vector3(0f, 0f, 0.4f), new Vector3(0f, 0f, 0.2f), pitch.AxisInStage, 60f);
            HoldElbow();
            pitch.SetDriveTarget(60f);
            Simulate(2f);
            Assert.That(pitch.GetPosition(), Is.EqualTo(60f).Within(1f));
            AssertNear(upperArm.position, UsdFixtures.StageToWorld(root.transform, expected), 0.01f);
        }

        [Test]
        public void RevoluteLimitStopsTheDriveAtTheAuthoredBound()
        {
            Import();
            UsdPhysicsJoint pitch = Joints()["/arm_robot/shoulder_link/shoulder_pitch"];
            pitch.SetDriveTarget(-60f);
            Simulate(2f);
            Assert.That(pitch.GetPosition(), Is.EqualTo(-10f).Within(1.5f), "lower limit -10");
        }

        [Test]
        public void PrismaticDriveMovesAlongTheAuthoredAxis()
        {
            Import();
            UsdPhysicsJoint slider = Joints()["/arm_robot/forearm/slider"];
            HoldElbow();
            slider.SetDriveTarget(0.06f);
            Simulate(2f);
            Assert.That(slider.GetPosition(), Is.EqualTo(0.06f).Within(0.002f));
            AssertNear(slider.Link.transform.position, UsdFixtures.StageToWorld(root.transform, new Vector3(0.06f, 0f, 0.93f)), 0.005f);
        }

        [TestCase(1f, 30f)]
        [TestCase(-1f, -60f)]
        public void FreeHingeStopsAtTheAuthoredLimit(float direction, float limit)
        {
            Import();
            UsdPhysicsJoint hinge = Joints()["/pendulum_hinge"];
            Assert.That(hinge.UnityJoint, Is.InstanceOf<ConfigurableJoint>());
            Rigidbody bob = hinge.UnityJoint.GetComponent<Rigidbody>();

            // Spin the bob about +Y (USD, right-handed) and let the limit stop it.
            Vector3 spinStage = new Vector3(0f, 1f, 0f) * direction * 3f;
            bob.angularVelocity = -root.transform.TransformDirection(new Vector3(-spinStage.x, spinStage.y, spinStage.z));
            Simulate(1.5f, keepSpinning: bob, spin: bob.angularVelocity);

            Vector3 expected = RotateAbout(new Vector3(0f, 1.1f, 0.5f), new Vector3(0f, 1.1f, 0.8f), Vector3.up, limit);
            AssertNear(bob.position, UsdFixtures.StageToWorld(root.transform, expected), 0.02f);
        }

        [Test]
        public void MimicJointFollowsItsReferenceJoint()
        {
            UsdImportResult result = Import(fixture: MimicFixture);
            Dictionary<string, UsdPhysicsJoint> joints = Joints();
            UsdPhysicsJoint finger1 = joints["/gripper_robot/hand/finger_joint1"];
            UsdPhysicsJoint finger2 = joints["/gripper_robot/hand/finger_joint2"];
            Assert.That(finger2.TryGetComponent(out UsdPhysicsMimicJoint mimic), "finger_joint2 is coupled");
            Assert.That(mimic.Reference, Is.SameAs(finger1));
            Assert.That(mimic.Gearing, Is.EqualTo(-1f));
            Assert.That(mimic.Offset, Is.EqualTo(0f));
            Assert.That(finger2.DriveEnabled, Is.False, "the stage authors no drive on the follower");
            Assert.That(finger2.Link.xDrive.stiffness, Is.EqualTo(finger1.Link.xDrive.stiffness), "borrows the reference drive");
            Assert.That(result.PhysicsNotes, Has.Some.Contains("followed with a drive"));

            finger1.SetDriveTarget(0.03f);
            SimulateMimics(2f);
            Assert.That(finger1.GetPosition(), Is.EqualTo(0.03f).Within(0.002f));
            Assert.That(finger2.GetPosition(), Is.EqualTo(0.03f).Within(0.002f), "gearing -1: both fingers open together");

            // finger_joint2's frame is turned 180 degrees about Z, so the right finger opens to -X.
            float gap = finger1.Link.transform.position.x - finger2.Link.transform.position.x;
            Assert.That(Mathf.Abs(gap), Is.EqualTo(0.04f + 0.06f).Within(0.005f));
        }

        [Test]
        public void MimicGearingAndOffsetAreInRadians()
        {
            Import(fixture: MimicFixture);
            Dictionary<string, UsdPhysicsJoint> joints = Joints();
            UsdPhysicsJoint lead = joints["/gripper_robot/hand/lead_hinge"];
            UsdPhysicsJoint follower = joints["/gripper_robot/hand/follower_hinge"];
            UsdPhysicsMimicJoint mimic = follower.GetComponent<UsdPhysicsMimicJoint>();
            Assert.That(mimic.Gearing, Is.EqualTo(-2f));

            // follower + gearing * reference + offset = 0: 2 * 20 - 10 = 30 degrees.
            Assert.That(mimic.FollowerPosition(20f), Is.EqualTo(30f).Within(0.01f));
            lead.SetDriveTarget(20f);
            SimulateMimics(3f);
            Assert.That(lead.GetPosition(), Is.EqualTo(20f).Within(1f));
            Assert.That(follower.GetPosition(), Is.EqualTo(2f * lead.GetPosition() - 10f).Within(1f));
        }

        // Physics.Simulate runs no FixedUpdate, so the couplings step here as they would in play.
        private void SimulateMimics(float seconds)
        {
            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = Vector3.zero;
            UsdPhysicsMimicJoint[] mimics = root.GetComponentsInChildren<UsdPhysicsMimicJoint>();
            const float step = 1f / 120f;
            for (float t = 0f; t < seconds; t += step)
            {
                foreach (UsdPhysicsMimicJoint mimic in mimics)
                {
                    mimic.Step();
                }

                Physics.Simulate(step);
            }

            Physics.SyncTransforms();
        }

        // The elbow's authored target (-30) would swing the forearm and gripper away from the
        // poses the tests compare against; 0 keeps the arm as imported.
        private void HoldElbow() => Joints()["/arm_robot/upper_arm/elbow"].SetDriveTarget(0f);

        private void Simulate(float seconds, Rigidbody keepSpinning = null, Vector3 spin = default)
        {
            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = Vector3.zero;
            const float step = 1f / 120f;
            for (float t = 0f; t < seconds; t += step)
            {
                if (keepSpinning != null)
                {
                    keepSpinning.angularVelocity = spin;
                }

                Physics.Simulate(step);
            }

            Physics.SyncTransforms();
        }

        // USD's right-handed rotation of a stage point about an axis through `center`.
        private static Vector3 RotateAbout(Vector3 point, Vector3 center, Vector3 axis, float degrees)
        {
            Vector3 k = axis.normalized;
            Vector3 v = point - center;
            float r = degrees * Mathf.Deg2Rad;
            Vector3 rotated = (v * Mathf.Cos(r)) + (Vector3.Cross(k, v) * Mathf.Sin(r)) + (k * Vector3.Dot(k, v) * (1f - Mathf.Cos(r)));
            return center + rotated;
        }

        private static void AssertNear(Vector3 actual, Vector3 expected, float tolerance) =>
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance), $"expected {expected:F4}, got {actual:F4}");

        private static void AssertAuthored(UsdPhysicsJoint joint, UsdPhysicsJointType type, float lower, float upper,
                                           float stiffness, float damping, float maxForce)
        {
            Assert.That(joint.Type, Is.EqualTo(type), joint.PrimPath);
            Assert.That(joint.Articulated, Is.True, joint.PrimPath);
            Assert.That(joint.LimitEnabled, Is.True, joint.PrimPath);
            Assert.That(joint.LowerLimit, Is.EqualTo(lower).Within(1e-5f), joint.PrimPath);
            Assert.That(joint.UpperLimit, Is.EqualTo(upper).Within(1e-5f), joint.PrimPath);
            Assert.That(joint.DriveEnabled, Is.True, joint.PrimPath);
            Assert.That(joint.DriveStiffness, Is.EqualTo(stiffness).Within(1e-4f), joint.PrimPath);
            Assert.That(joint.DriveDamping, Is.EqualTo(damping).Within(1e-4f), joint.PrimPath);
            Assert.That(joint.DriveMaxForce, Is.EqualTo(maxForce).Within(1e-4f), joint.PrimPath);
        }

        private static ArticulationBody ParentLink(ArticulationBody link)
        {
            for (Transform current = link.transform.parent; current != null; current = current.parent)
            {
                if (current.TryGetComponent(out ArticulationBody body))
                {
                    return body;
                }
            }

            return null;
        }
    }
}
