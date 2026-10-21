using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Unity.USDToolkit.Native
{
    // Physics on import (native API 13): one RUsd_ReadPhysics call returns OpenUSD's own physics
    // parse, plus mass, collision meshes and PhysX attributes, as flat records. Everything is copied
    // into a UsdNativePhysics and the native block released before returning, so the caller never
    // holds a pointer into the plugin.
    internal static partial class UsdNative
    {
        // RUsd_ReadPhysics: physics on import.
        internal const int PhysicsApiVersion = 13;

        internal static bool SupportsPhysics =>
            IsSupportedPlatform && LoadedApiVersion >= PhysicsApiVersion && HasPhysicsExports;

        // Why physics import is unavailable, for messages; null when it is available.
        internal static string PhysicsUnavailableReason
        {
            get
            {
                if (!IsSupportedPlatform || LoadedApiVersion < PhysicsApiVersion)
                {
                    return $"physics import needs {NeedsPlugin(PhysicsApiVersion)}; {LoadedPluginDescription}";
                }

                return HasPhysicsExports
                    ? null
                    : $"{LoadedPluginDescription}, but it was built without the physics reader (RUsd_ReadPhysics)";
            }
        }

        // 0: not probed yet, 1: present, -1: missing.
        private static int physicsExports;

        // An API 13 plugin can lack the physics reader: a 0.8.1 wrapper built from a branch without
        // it reports the same version (118 RUsd_ exports instead of 120). The entry point is probed
        // once: RUsd_ReleasePhysics with no context fails with an error code and touches nothing.
        private static bool HasPhysicsExports
        {
            get
            {
                if (physicsExports == 0)
                {
                    try
                    {
                        RUsd_ReleasePhysics(IntPtr.Zero);
                        physicsExports = 1;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        physicsExports = -1;
                    }
                }

                return physicsExports > 0;
            }
        }

        // RUsdPhysicsCollider.Shape
        internal const int PhysicsShapeSphere = 1;
        internal const int PhysicsShapeCube = 2;
        internal const int PhysicsShapeCapsule = 3;
        internal const int PhysicsShapeCylinder = 4;
        internal const int PhysicsShapeCone = 5;
        internal const int PhysicsShapePlane = 6;
        internal const int PhysicsShapeMesh = 7;
        internal const int PhysicsShapeSpherePoints = 8;
        internal const int PhysicsShapeCapsuleTapered = 9;
        internal const int PhysicsShapeCylinderTapered = 10;

        // RUsdPhysicsJoint.Type
        internal const int PhysicsJointFixed = 1;
        internal const int PhysicsJointRevolute = 2;
        internal const int PhysicsJointPrismatic = 3;
        internal const int PhysicsJointSpherical = 4;
        internal const int PhysicsJointDistance = 5;
        internal const int PhysicsJointD6 = 6;

        // RUsdPhysics*.Axis, RUsdPhysicsData.UpAxis
        internal const int PhysicsAxisX = 0;
        internal const int PhysicsAxisY = 1;
        internal const int PhysicsAxisZ = 2;

        // Indices into RUsdPhysicsJoint.Dofs.
        internal const int PhysicsDofTransX = 0;
        internal const int PhysicsDofTransY = 1;
        internal const int PhysicsDofTransZ = 2;
        internal const int PhysicsDofRotX = 3;
        internal const int PhysicsDofRotY = 4;
        internal const int PhysicsDofRotZ = 5;
        internal const int PhysicsDofDistance = 6;
        internal const int PhysicsDofCount = 7;

        // RUsdPhysicsCollider.MeshApproximation
        internal const int PhysicsMeshNone = 0;
        internal const int PhysicsMeshConvexHull = 1;
        internal const int PhysicsMeshConvexDecomposition = 2;
        internal const int PhysicsMeshBoundingSphere = 3;
        internal const int PhysicsMeshBoundingCube = 4;
        internal const int PhysicsMeshSimplification = 5;
        internal const int PhysicsMeshSdf = 6;
        internal const int PhysicsMeshOther = 7;

        // RUsdPhysicsMaterial.*CombineMode
        internal const int PhysicsCombineAverage = 0;
        internal const int PhysicsCombineMin = 1;
        internal const int PhysicsCombineMultiply = 2;
        internal const int PhysicsCombineMax = 3;

        // RUsdPhysicsUnsupported.Kind
        internal const int PhysicsUnsupportedCustomShape = 1;
        internal const int PhysicsUnsupportedCustomJoint = 2;
        internal const int PhysicsUnsupportedMimicJoint = 3;
        internal const int PhysicsUnsupportedTendon = 4;
        internal const int PhysicsUnsupportedInvalid = 5;
        internal const int PhysicsUnsupportedLimit = 6;
        internal const int PhysicsUnsupportedMesh = 7;

        // SetMask bits, per record.
        internal const uint PhysicsSceneSetTimeSteps = 1u;
        internal const uint PhysicsSceneSetBounceThreshold = 2u;
        internal const uint PhysicsSceneSetPositionIterations = 4u;
        internal const uint PhysicsSceneSetVelocityIterations = 8u;
        internal const uint PhysicsSceneSetCcd = 16u;

        internal const uint PhysicsBodySetMass = 1u;
        internal const uint PhysicsBodySetDensity = 2u;
        internal const uint PhysicsBodySetCenterOfMass = 4u;
        internal const uint PhysicsBodySetInertia = 8u;
        internal const uint PhysicsBodySetPrincipalAxes = 16u;
        internal const uint PhysicsBodySetLinearDamping = 32u;
        internal const uint PhysicsBodySetAngularDamping = 64u;
        internal const uint PhysicsBodySetMaxLinearVelocity = 128u;
        internal const uint PhysicsBodySetMaxAngularVelocity = 256u;
        internal const uint PhysicsBodySetMaxDepenetration = 512u;
        internal const uint PhysicsBodySetSleepThreshold = 1024u;
        internal const uint PhysicsBodySetPositionIterations = 2048u;
        internal const uint PhysicsBodySetVelocityIterations = 4096u;
        internal const uint PhysicsBodySetCcd = 8192u;
        internal const uint PhysicsBodySetDisableGravity = 16384u;

        internal const uint PhysicsColliderSetMass = 1u;
        internal const uint PhysicsColliderSetDensity = 2u;
        internal const uint PhysicsColliderSetContactOffset = 4u;
        internal const uint PhysicsColliderSetRestOffset = 8u;

        internal const uint PhysicsMaterialSetFrictionCombine = 1u;
        internal const uint PhysicsMaterialSetRestitutionCombine = 2u;

        internal const uint PhysicsDofSetStatePosition = 1u;
        internal const uint PhysicsDofSetStateVelocity = 2u;
        internal const uint PhysicsDofSetArmature = 4u;
        internal const uint PhysicsDofSetMaxVelocity = 8u;
        internal const uint PhysicsDofSetFriction = 16u;

        internal const uint PhysicsArticulationSetSelfCollisions = 1u;
        internal const uint PhysicsArticulationSetPositionIterations = 2u;
        internal const uint PhysicsArticulationSetVelocityIterations = 4u;
        internal const uint PhysicsArticulationSetSleepThreshold = 8u;
        internal const uint PhysicsArticulationSetStabilization = 16u;

        // RUsdPhysicsReadOptions.Flags
        internal const int PhysicsReadPhysx = 1;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReadPhysics(IntPtr context, ref RUsdPhysicsReadOptions options, out IntPtr data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RUsd_ReleasePhysics(IntPtr context);

        // Reads the open stage's physics. `rootPaths` limits the read to those subtrees (null: the
        // whole stage); a budget of 0 keeps the plugin's default. Throws UsdExportException when the
        // read fails or, without skipWhenExceeded, a budget is exceeded.
        internal static UsdNativePhysics ReadPhysics(
            IntPtr context,
            bool readPhysx = true,
            string[] rootPaths = null,
            long maxObjects = 0,
            long maxMeshElements = 0,
            bool skipWhenExceeded = false)
        {
            ThrowIfUnsupported();
            if (!SupportsPhysics)
            {
                throw new NotSupportedException(
                    $"{PhysicsUnavailableReason}. Rebuild the native plugin for this platform (see Native~/REBUILD_WINDOWS_LINUX.md).");
            }

            byte[] roots = rootPaths == null || rootPaths.Length == 0 ? null : Utf8(string.Join("\n", rootPaths));
            GCHandle pinned = roots != null ? GCHandle.Alloc(roots, GCHandleType.Pinned) : default;
            try
            {
                var options = new RUsdPhysicsReadOptions
                {
                    StructSize = Marshal.SizeOf<RUsdPhysicsReadOptions>(),
                    Flags = readPhysx ? PhysicsReadPhysx : 0,
                    SkipWhenExceeded = skipWhenExceeded ? 1 : 0,
                    MaxObjects = maxObjects,
                    MaxMeshElements = maxMeshElements,
                    RootPaths = roots != null ? pinned.AddrOfPinnedObject() : IntPtr.Zero,
                };

                Check(context, RUsd_ReadPhysics(context, ref options, out IntPtr block));
                try
                {
                    return CopyPhysics(block);
                }
                finally
                {
                    Check(context, RUsd_ReleasePhysics(context));
                }
            }
            finally
            {
                if (pinned.IsAllocated)
                {
                    pinned.Free();
                }
            }
        }

        private static UsdNativePhysics CopyPhysics(IntPtr block)
        {
            var data = Marshal.PtrToStructure<RUsdPhysicsData>(block);
            if (data.StructSize < Marshal.SizeOf<RUsdPhysicsData>())
            {
                throw new InvalidOperationException(
                    "The native plugin returned a RUsdPhysicsData smaller than this package's; rebuild the plugin.");
            }

            var physics = new UsdNativePhysics
            {
                UpAxis = data.UpAxis,
                MetersPerUnit = data.MetersPerUnit,
                KilogramsPerUnit = data.KilogramsPerUnit,
                Scenes = CopyRecords<RUsdPhysicsScene>(data.Scenes, data.SceneCount),
                Bodies = CopyRecords<RUsdPhysicsBody>(data.Bodies, data.BodyCount),
                Colliders = CopyRecords<RUsdPhysicsCollider>(data.Colliders, data.ColliderCount),
                Materials = CopyRecords<RUsdPhysicsMaterial>(data.Materials, data.MaterialCount),
                Joints = CopyRecords<RUsdPhysicsJoint>(data.Joints, data.JointCount),
                Articulations = CopyRecords<RUsdPhysicsArticulation>(data.Articulations, data.ArticulationCount),
                Groups = CopyRecords<RUsdPhysicsCollisionGroup>(data.Groups, data.GroupCount),
                Unsupported = CopyRecords<RUsdPhysicsUnsupported>(data.Unsupported, data.UnsupportedCount),
                Indices = new int[data.IndexCount],
                Floats = new float[data.FloatCount],
                Strings = new string[data.StringCount],
            };

            if (data.IndexCount > 0)
            {
                Marshal.Copy(data.Indices, physics.Indices, 0, data.IndexCount);
            }
            if (data.FloatCount > 0)
            {
                Marshal.Copy(data.Floats, physics.Floats, 0, data.FloatCount);
            }
            if (data.StringCount > 0)
            {
                var bytes = new byte[data.StringBytes];
                var offsets = new int[data.StringCount];
                Marshal.Copy(data.Strings, bytes, 0, data.StringBytes);
                Marshal.Copy(data.StringOffsets, offsets, 0, data.StringCount);
                for (int i = 0; i < offsets.Length; i++)
                {
                    int start = offsets[i];
                    int end = Array.IndexOf(bytes, (byte)0, start);
                    physics.Strings[i] = Encoding.UTF8.GetString(bytes, start, (end < 0 ? bytes.Length : end) - start);
                }
            }

            return physics;
        }

        private static T[] CopyRecords<T>(IntPtr first, int count) where T : struct
        {
            var records = new T[count];
            int stride = Marshal.SizeOf<T>();
            for (int i = 0; i < count; i++)
            {
                records[i] = Marshal.PtrToStructure<T>(first + i * stride);
            }
            return records;
        }
    }
}
