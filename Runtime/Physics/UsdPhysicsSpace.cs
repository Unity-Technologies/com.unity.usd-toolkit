using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // Physics records arrive in USD's space: right-handed, stage units, degrees. The import root's
    // local space is the stage with X negated (USD (x, y, z) -> Unity (-x, y, z), the same flip the
    // importer applies to every point and matrix), and the root carries the up-axis and unit
    // correction (UsdImportOptions.ApplyStageUpAxisAndUnits).
    //
    // Mirroring a rotation R in X is B R B with B = diag(-1, 1, 1): still a rotation, as a quaternion
    // (x, -y, -z, w). Its X column is -B(R e_x) and its Y and Z columns are B(R e_y), B(R e_z), so a
    // rotation by +t about an axis a becomes a rotation by +t about -B(a). That is why a revolute
    // joint's Unity anchor X is -B(axis): the angle then keeps its sign and its limits copy over as
    // authored (UsdPhysicsBuilder.AnchorFrame).
    internal static class UsdPhysicsSpace
    {
        // A ConfigurableJoint (PhysX's D6 with this body as actor0) measures its connected body
        // against itself, the opposite way to USD's body1 against body0.
        internal const float ConfigurableAngularSign = -1f;

        internal static Vector3 Position(RUsdVec3 v) => new Vector3(-v.X, v.Y, v.Z);

        internal static Vector3 UsdVector(RUsdVec3 v) => new Vector3(v.X, v.Y, v.Z);

        // A USD quaternion as a USD-space rotation, for composing before Mirror.
        internal static Quaternion UsdRotation(RUsdVec4 q)
        {
            var rotation = new Quaternion(q.X, q.Y, q.Z, q.W);
            float length = Mathf.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
            if (length < 1e-6f || float.IsNaN(length))
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
        }

        internal static Quaternion Mirror(Quaternion usd) => new Quaternion(usd.x, -usd.y, -usd.z, usd.w);

        internal static Quaternion Rotation(RUsdVec4 q) => Mirror(UsdRotation(q));

        // A scale as physics divides by it: absolute, and 1 where it is zero or not a number.
        internal static Vector3 SafeScale(RUsdVec3 s)
        {
            float Safe(float value)
            {
                float a = Mathf.Abs(value);
                return a > 1e-12f && !float.IsNaN(a) && !float.IsInfinity(a) ? a : 1f;
            }

            return new Vector3(Safe(s.X), Safe(s.Y), Safe(s.Z));
        }

        internal static Vector3 Divide(Vector3 a, Vector3 b) => new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);

        internal static Vector3 AxisVector(int axis) =>
            axis == UsdNative.PhysicsAxisY ? Vector3.up : axis == UsdNative.PhysicsAxisZ ? Vector3.forward : Vector3.right;

        // A world direction in stage space, USD's basis.
        internal static Vector3 WorldDirectionToStage(Transform root, Vector3 worldDirection)
        {
            Vector3 local = root != null ? root.InverseTransformDirection(worldDirection) : worldDirection;
            return new Vector3(-local.x, local.y, local.z).normalized;
        }

        // From USD drive and limit values to Unity's: a length is stage units to meters (1 for an
        // angle, degrees on both sides). Stiffness and damping are per degree in USD and per
        // radian in PhysX, which Unity passes them to as they are; forces and torques go from the
        // stage's mass and length units to newtons and newton-metres.
        internal static void DriveScales(bool angular, double metersPerUnit, double kilogramsPerUnit,
                                         out float length, out float stiffness, out float force)
        {
            double mpu = metersPerUnit > 0.0 ? metersPerUnit : 1.0;
            double kpu = kilogramsPerUnit > 0.0 ? kilogramsPerUnit : 1.0;
            if (angular)
            {
                length = 1f;
                force = (float)(kpu * mpu * mpu);
                stiffness = (float)(kpu * mpu * mpu * Mathf.Rad2Deg);
            }
            else
            {
                length = (float)mpu;
                force = (float)(kpu * mpu);
                stiffness = (float)kpu;
            }
        }
    }
}
