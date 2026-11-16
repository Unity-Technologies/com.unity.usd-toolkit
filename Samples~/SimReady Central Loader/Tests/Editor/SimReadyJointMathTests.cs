using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    public sealed class SimReadyJointMathTests
    {
        // ---- Coupling: follower + gearing * reference + offset = 0 (radians) ----

        [TestCase(60f, 1f, 0f, -60f)]
        [TestCase(60f, -1f, 0f, 60f)]
        [TestCase(10f, 2f, 0f, -20f)]
        public void RevoluteFollowersFollowTheGearing(float leader, float gearing, float offset, float expected)
        {
            Assert.That(SimReadyJointMath.FollowerPosition(leader, gearing, offset, true, true), Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void TheOffsetIsInRadiansForARotation()
        {
            // follower = -(1 * 0) - 0.5 rad
            Assert.That(SimReadyJointMath.FollowerPosition(0f, 1f, 0.5f, true, true), Is.EqualTo(-0.5f * Mathf.Rad2Deg).Within(1e-3f));
        }

        [Test]
        public void PrismaticFollowersStayInStageUnits()
        {
            // 2FG14: gearing -1 makes the left finger move as far as the right one.
            Assert.That(SimReadyJointMath.FollowerPosition(0.02f, -1f, 0f, false, false), Is.EqualTo(0.02f).Within(1e-6f));
        }

        [Test]
        public void TheSampleMathMatchesTheToolkitsMimicComponent()
        {
            var host = new GameObject("mimic");
            try
            {
                UsdPhysicsJoint leader = host.AddComponent<UsdPhysicsJoint>();
                leader.UnityType = UsdPhysicsJointType.Revolute;
                var followerHost = new GameObject("follower");
                followerHost.transform.SetParent(host.transform);
                UsdPhysicsJoint follower = followerHost.AddComponent<UsdPhysicsJoint>();
                follower.UnityType = UsdPhysicsJointType.Revolute;
                UsdPhysicsMimicJoint mimic = followerHost.AddComponent<UsdPhysicsMimicJoint>();
                mimic.Follower = follower;
                mimic.Reference = leader;
                mimic.Gearing = -1.5f;
                mimic.Offset = 0.1f;
                Assert.That(SimReadyJointMath.FollowerPosition(40f, -1.5f, 0.1f, true, true), Is.EqualTo(mimic.FollowerPosition(40f)).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        // ---- Limits ----

        [Test]
        public void TheRangeIsTheAuthoredLimit()
        {
            Assert.That(SimReadyJointMath.TryRange(true, 0f, 70.06f, true, 1f, out float min, out float max), Is.True);
            Assert.That(min, Is.EqualTo(0f));
            Assert.That(max, Is.EqualTo(70.06f));
            Assert.That(SimReadyJointMath.TryRange(true, 0f, 0.025f, false, 1f, out min, out max), Is.True);
            Assert.That(max, Is.EqualTo(0.025f));
        }

        [Test]
        public void UnlimitedAxesGetAUsableRangeAndLockedOnesNone()
        {
            Assert.That(SimReadyJointMath.TryRange(false, 0f, 0f, true, 1f, out float min, out float max), Is.True);
            Assert.That((min, max), Is.EqualTo((-180f, 180f)));
            Assert.That(SimReadyJointMath.TryRange(true, float.NegativeInfinity, float.PositiveInfinity, true, 1f, out min, out max), Is.True);
            Assert.That((min, max), Is.EqualTo((-180f, 180f)), "the tires of the scissor lift author ±inf");
            Assert.That(SimReadyJointMath.TryRange(false, 0f, 0f, false, 0.5f, out min, out max), Is.True);
            Assert.That((min, max), Is.EqualTo((-0.5f, 0.5f)));
            Assert.That(SimReadyJointMath.TryRange(true, 1f, -1f, true, 1f, out _, out _), Is.False, "lower above upper locks the axis");
        }

        [TestCase(-5f, 0f)]
        [TestCase(35f, 35f)]
        [TestCase(99f, 70f)]
        [TestCase(float.NaN, 0f)]
        public void ValuesAreClampedToTheRange(float value, float expected)
        {
            Assert.That(SimReadyJointMath.Clamp(value, 0f, 70f), Is.EqualTo(expected));
        }

        // ---- Display ----

        [Test]
        public void CaptionsShowWholeDegreesOrMillimetres()
        {
            Assert.That(SimReadyJointMath.Caption("Fingers", 69.6f, true, 1.0), Is.EqualTo("Fingers · 70°"));
            Assert.That(SimReadyJointMath.Caption("Fingers", 0.0114f, false, 1.0), Is.EqualTo("Fingers · 11 mm"));
            Assert.That(SimReadyJointMath.Caption("Drawer", 2.5f, false, 0.01), Is.EqualTo("Drawer · 25 mm"), "centimetre stage");
            Assert.That(SimReadyJointMath.Badge(25.4f, true, 1.0), Is.EqualTo("25°"));
            Assert.That(SimReadyJointMath.Badge(0.025f, false, 1.0), Is.EqualTo("25mm"));
        }

        // ---- Labels ----

        [Test]
        public void TheGrippersLeadersAreFingers()
        {
            // OnRobot RG2: finger_joint drives right_outer_proximal_finger_link and nine followers on both sides.
            Assert.That(SimReadyJointMath.Label("finger_joint", null,
                new[] { "right_finger_cover_arm_joint", "left_outer_proximal_finger_joint", "left_inner_proximal_finger_joint", "right_inner_proximal_finger_joint" },
                "right_outer_proximal_finger_link", "onrobot_rg2"), Is.EqualTo("Fingers"));

            // OnRobot 2FG14: finger_stroke moves the right finger; left_finger_base_joint mimics it.
            Assert.That(SimReadyJointMath.Label("finger_stroke", null, new[] { "left_finger_base_joint" }, "right_finger_base_link", "onrobot_2fg14"),
                        Is.EqualTo("Fingers"));
        }

        [TestCase("joint_door_04_joint_00", "cabinet_file_a01", "Door 04")]
        [TestCase("joint_drawer_01_joint_00", "cabinet_file_a01", "Drawer 01")]
        [TestCase("joint_sm_kitchenware_kettle_a01_lid_joint_00", "kitchenware_kettle_a01", "Lid")]
        [TestCase("joint_tire_04_joint_00", "equipment_liftTable_scissorMobile_a01", "Tire 04")]
        [TestCase("Lower_door_joint", "cooking_ldgl6924s", "Lower door")]
        [TestCase("joint", "x", "Joint")]
        public void OtherJointsAreNamedFromTheirPrim(string prim, string asset, string expected)
        {
            Assert.That(SimReadyJointMath.Label(prim, null, null, "door_link", asset), Is.EqualTo(expected));
        }

        [Test]
        public void ADisplayNameWins()
        {
            Assert.That(SimReadyJointMath.Label("finger_joint", " Jaw ", new[] { "left_finger_joint" }, "right_finger", "x"), Is.EqualTo("Jaw"));
        }

        [Test]
        public void ALeaderWithFollowersOnOneSideIsNotPlural()
        {
            Assert.That(SimReadyJointMath.Label("knuckle_joint", null, new[] { "tip_joint" }, "knuckle", "hand"), Is.EqualTo("Knuckle"));
        }

        // ---- Dragging ----

        [Test]
        public void TheDragIsProjectedOnTheJointsScreenDirection()
        {
            // One unit moves the handle 10 px right: a 5 px right, 3 px down drag is half a unit.
            Assert.That(SimReadyJointMath.DragDelta(Vector2.zero, new Vector2(5f, 3f), new Vector2(10f, 0f), 0.1f), Is.EqualTo(0.5f).Within(1e-5f));
            // Against the direction it is negative; across it, nothing.
            Assert.That(SimReadyJointMath.DragDelta(Vector2.zero, new Vector2(-20f, 0f), new Vector2(10f, 0f), 0.1f), Is.EqualTo(-2f).Within(1e-5f));
            Assert.That(SimReadyJointMath.DragDelta(Vector2.zero, new Vector2(0f, 20f), new Vector2(10f, 0f), 0.1f), Is.EqualTo(0f).Within(1e-5f));
            // Along a diagonal direction.
            Assert.That(SimReadyJointMath.DragDelta(Vector2.zero, new Vector2(3f, 4f), new Vector2(3f, 4f), 0.1f), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void AnEdgeOnJointFallsBackToVerticalMotion()
        {
            Assert.That(SimReadyJointMath.DragDelta(Vector2.zero, new Vector2(0f, -10f), Vector2.zero, -0.5f), Is.EqualTo(5f).Within(1e-5f));
        }

        [Test]
        public void TheScreenDirectionIsMeasuredPerUnit()
        {
            // A projection that maps world X to 100 px per unit and ignores depth.
            Vector2 Project(Vector3 p) => new Vector2(p.x * 100f, -p.y * 100f);
            Vector2 perUnit = SimReadyJointMath.ScreenPerUnit(Project, new Vector3(1f, 2f, 3f), new Vector3(0.5f, 0f, 1f), 0.01f);
            Assert.That(perUnit.x, Is.EqualTo(50f).Within(1e-3f));
            Assert.That(perUnit.y, Is.EqualTo(0f).Within(1e-3f));
        }
    }
}
