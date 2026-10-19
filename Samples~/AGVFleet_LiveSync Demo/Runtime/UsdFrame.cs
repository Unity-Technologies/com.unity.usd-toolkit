using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// The ONE place where the USD stage frame and the Unity frame meet.
    ///
    /// Root stage: Z-up, right-handed, metersPerUnit = 1 (Isaac Sim convention). Unity: Y-up,
    /// left-handed, meters. Mapping (a swap of Y and Z, which is a reflection, so it fixes the
    /// up-axis AND the handedness in one step):
    ///
    ///     unity (x, y, z) = usd (X, Z, Y)          usd (X, Y, Z) = unity (x, z, y)
    ///
    ///     rotations:  conjugation by the swap S.  USD quaternion (w, x, y, z)
    ///                 -> Unity Quaternion(x: -x, y: -z, z: -y, w: w)
    ///     yaw:        USD yaw psi about +Z (CCW seen from above)
    ///                 -> Unity rotation of -psi degrees about +Y
    ///     units:      both meters; metersPerUnit is checked at import (must be 1).
    ///
    /// The package importer (Unity.USDToolkit.UsdImporter) does not convert up-axis or units; it only
    /// flips X (B = diag(-1, 1, 1), see UsdImporter.UsdLocalToUnity). An imported USD subtree
    /// therefore sits under a wrapper with rotation <see cref="ImportedSubtreeRotation"/>, which turns
    /// the importer's (-X, Y, Z) into (X, Z, Y): a 180 deg rotation about (0, 1, 1)/sqrt2.
    /// Everything on the wire (Isaac hub messages) and in unity_ops.usda is in USD frame; nothing else
    /// in the project converts axes.
    /// </summary>
    public static class UsdFrame
    {
        public static readonly Quaternion ImportedSubtreeRotation = new Quaternion(0f, 0.70710678f, 0.70710678f, 0f);

        public static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)x, (float)z, (float)y);

        public static Vector3 ToUnity(Vector3 usd) => new Vector3(usd.x, usd.z, usd.y);

        public static Vector3 ToUsd(Vector3 unity) => new Vector3(unity.x, unity.z, unity.y);

        /// <summary>USD quaternion given as (w, x, y, z), the order Isaac / Gf use.</summary>
        public static Quaternion ToUnity(float w, float x, float y, float z) => new Quaternion(-x, -z, -y, w);

        public static Quaternion YawToUnity(float usdYawRad) => Quaternion.Euler(0f, -usdYawRad * Mathf.Rad2Deg, 0f);

        /// <summary>Unity rotation about +Y (degrees) -> USD yaw about +Z (degrees).</summary>
        public static float UnityYawToUsdDeg(float unityYawDeg) => -unityYawDeg;

        /// <summary>
        /// A USD local matrix (16 floats, row-major, row-vector convention) as the importer turns it into
        /// a Unity local matrix: B * transpose(M) * B with B = diag(-1, 1, 1) -- mirrors
        /// Unity.USDToolkit.UsdImporter.UsdLocalToUnity, for values that sit under the imported subtree.
        /// </summary>
        public static Matrix4x4 ImporterLocal(float[] m, int offset)
        {
            var u = new Matrix4x4();
            for (int i = 0; i < 4; i++)
            {
                float si = i == 0 ? -1f : 1f;
                for (int j = 0; j < 4; j++)
                {
                    float sj = j == 0 ? -1f : 1f;
                    u[i, j] = si * sj * m[offset + j * 4 + i];
                }
            }
            return u;
        }
    }
}
