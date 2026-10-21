using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Converts <see cref="UsdStage"/> values between USD's basis and Unity's the way the importer
    /// does: USD is right-handed and Unity left-handed, and the importer mirrors X
    /// (USD (x, y, z) is Unity (-x, y, z)). Units and the up axis are left alone; the importer puts
    /// those on the import root (<see cref="UsdStageSource.StageCorrectionScale"/> and
    /// <see cref="UsdStageSource.StageCorrectionRotation"/>), so a value converted here is what an
    /// object below that root shows.
    /// </summary>
    public static class UsdCoordinates
    {
        /// <summary>A USD point or vector in Unity's basis.</summary>
        public static Vector3 ToUnity(Vector3 usd) => new Vector3(-usd.x, usd.y, usd.z);

        /// <summary>A Unity point or vector in USD's basis.</summary>
        public static Vector3 FromUnity(Vector3 unity) => new Vector3(-unity.x, unity.y, unity.z);

        /// <summary>A USD rotation in Unity's basis.</summary>
        public static Quaternion ToUnity(Quaternion usd) => new Quaternion(usd.x, -usd.y, -usd.z, usd.w);

        /// <summary>A Unity rotation in USD's basis.</summary>
        public static Quaternion FromUnity(Quaternion unity) => new Quaternion(unity.x, -unity.y, -unity.z, unity.w);

        /// <summary>
        /// A transform from <see cref="UsdPrim.GetLocalTransform"/> (USD's values, column vectors) in
        /// Unity's basis: B * M * B with B the X mirror, as the importer puts it on an object.
        /// </summary>
        public static Matrix4x4 ToUnity(Matrix4x4 usd) => Mirror(usd);

        /// <summary>A Unity transform in USD's basis, for <see cref="UsdPrim.SetLocalTransform"/>.</summary>
        public static Matrix4x4 FromUnity(Matrix4x4 unity) => Mirror(unity);

        private static Matrix4x4 Mirror(Matrix4x4 m)
        {
            // B * M * B with B = diag(-1, 1, 1, 1): every element in row 0 or column 0 (not both) flips.
            for (int i = 1; i < 4; i++)
            {
                m[0, i] = -m[0, i];
                m[i, 0] = -m[i, 0];
            }

            return m;
        }

        // RUsdMatrix4x4 holds USD's row-vector matrix (row r, column c); Unity's column-vector
        // matrix is its transpose.
        internal static Matrix4x4 FromRowMajor(RUsdMatrix4x4 m)
        {
            var unity = new Matrix4x4();
            unity[0, 0] = (float)m.M00; unity[1, 0] = (float)m.M01; unity[2, 0] = (float)m.M02; unity[3, 0] = (float)m.M03;
            unity[0, 1] = (float)m.M10; unity[1, 1] = (float)m.M11; unity[2, 1] = (float)m.M12; unity[3, 1] = (float)m.M13;
            unity[0, 2] = (float)m.M20; unity[1, 2] = (float)m.M21; unity[2, 2] = (float)m.M22; unity[3, 2] = (float)m.M23;
            unity[0, 3] = (float)m.M30; unity[1, 3] = (float)m.M31; unity[2, 3] = (float)m.M32; unity[3, 3] = (float)m.M33;
            return unity;
        }

        internal static RUsdMatrix4x4 ToRowMajor(Matrix4x4 unity) => new RUsdMatrix4x4
        {
            M00 = unity[0, 0], M01 = unity[1, 0], M02 = unity[2, 0], M03 = unity[3, 0],
            M10 = unity[0, 1], M11 = unity[1, 1], M12 = unity[2, 1], M13 = unity[3, 1],
            M20 = unity[0, 2], M21 = unity[1, 2], M22 = unity[2, 2], M23 = unity[3, 2],
            M30 = unity[0, 3], M31 = unity[1, 3], M32 = unity[2, 3], M33 = unity[3, 3],
        };
    }
}
