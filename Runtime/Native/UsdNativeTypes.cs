using System;
using System.Runtime.InteropServices;

namespace Unity.USDToolkit.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdVec2
    {
        public float X;
        public float Y;

        public RUsdVec2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdVec3
    {
        public float X;
        public float Y;
        public float Z;

        public RUsdVec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdVec4
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public RUsdVec4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    // A UsdLux light to export (API v13); mirrors RUsdExportLight in unity_usd_toolkit_native.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdExportLight
    {
        public int Type;
        public float ColorR;
        public float ColorG;
        public float ColorB;
        public float Intensity;
        public float Exposure;
        public int Normalize;
        public float Radius;
        public float Width;
        public float Height;
        public float Angle;
        public float ConeAngle;
        public float ConeSoftness;
        public int CastShadows;
        public float ColorTemperature;
    }

    // A UsdGeomCamera to export (API v13); mirrors RUsdExportCamera.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdExportCamera
    {
        public int Projection;
        public float FocalLength;
        public float HorizontalAperture;
        public float VerticalAperture;
        public float HorizontalApertureOffset;
        public float VerticalApertureOffset;
        public float NearClip;
        public float FarClip;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdMaterial
    {
        public float R;
        public float G;
        public float B;
        public float A;
        public float Metallic;
        public float Roughness;

        // Empty means a flat colour; when set, the native side wires it up as a UsdUVTexture
        // (all paths are relative to the USD file).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string AlbedoTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string NormalTexturePath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string MetallicTexturePath;

        // Texture UV tiling/offset. Anything other than (1,1,0,0) is applied by the native
        // side as a UsdTransform2d.
        public float UvScaleX;
        public float UvScaleY;
        public float UvOffsetX;
        public float UvOffsetY;

        // emissiveColor (0 by default). The native side authors emissiveColor when this is
        // non-zero or an emission texture is present.
        public float EmissiveR;
        public float EmissiveG;
        public float EmissiveB;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string EmissiveTexturePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdSubmesh
    {
        public int IndexStart;
        public int IndexCount;
        public int MaterialIndex;

        public RUsdSubmesh(int indexStart, int indexCount, int materialIndex)
        {
            IndexStart = indexStart;
            IndexCount = indexCount;
            MaterialIndex = materialIndex;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdMatrix4x4
    {
        public double M00;
        public double M01;
        public double M02;
        public double M03;
        public double M10;
        public double M11;
        public double M12;
        public double M13;
        public double M20;
        public double M21;
        public double M22;
        public double M23;
        public double M30;
        public double M31;
        public double M32;
        public double M33;
    }

    internal readonly struct RUsdImportInfo
    {
        public RUsdImportInfo(
            string defaultPrimPath,
            double metersPerUnit,
            int upAxis,
            int meshCount,
            int materialCount)
        {
            DefaultPrimPath = defaultPrimPath;
            MetersPerUnit = metersPerUnit;
            UpAxis = upAxis;
            MeshCount = meshCount;
            MaterialCount = materialCount;
        }

        public string DefaultPrimPath { get; }
        public double MetersPerUnit { get; }
        public int UpAxis { get; }
        public int MeshCount { get; }
        public int MaterialCount { get; }
    }

    // Authored UsdPreviewSurface opacity. TexturePath is empty when `opacity` is not connected
    // to a texture; Threshold > 0 means alpha cutout at that value, 0 means alpha blending.
    internal readonly struct RUsdImportOpacity
    {
        public RUsdImportOpacity(string texturePath, float threshold)
        {
            TexturePath = texturePath;
            Threshold = threshold;
        }

        public string TexturePath { get; }
        public float Threshold { get; }

        public bool IsTextured => !string.IsNullOrEmpty(TexturePath);
    }

    internal readonly struct RUsdImportMeshInfo
    {
        public RUsdImportMeshInfo(
            string primPath,
            string name,
            int pointCount,
            int indexCount,
            int normalCount,
            int uv0Count,
            int materialIndex,
            bool visible)
        {
            PrimPath = primPath;
            Name = name;
            PointCount = pointCount;
            IndexCount = indexCount;
            NormalCount = normalCount;
            Uv0Count = uv0Count;
            MaterialIndex = materialIndex;
            Visible = visible;
        }

        public string PrimPath { get; }
        public string Name { get; }
        public int PointCount { get; }
        public int IndexCount { get; }
        public int NormalCount { get; }
        public int Uv0Count { get; }
        public int MaterialIndex { get; }
        public bool Visible { get; }
    }

    internal readonly struct RUsdImportNode
    {
        public RUsdImportNode(string path, RUsdMatrix4x4 localMatrix, bool visible)
        {
            Path = path;
            LocalMatrix = localMatrix;
            Visible = visible;
        }

        public string Path { get; }
        public RUsdMatrix4x4 LocalMatrix { get; }
        public bool Visible { get; }
    }

    internal readonly struct RUsdImportMaterial
    {
        public RUsdImportMaterial(string name, RUsdMaterial material)
        {
            Name = name;
            Material = material;
        }

        public string Name { get; }
        public RUsdMaterial Material { get; }
    }

    // Point instancers (API v8). One record per prototype mesh; the matrices come separately
    // because there is one per instance and Moore Lane has 297,518 of them.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportInstancedMesh
    {
        public int InstancerNodeIndex;
        public int MeshIndex;
        public int InstanceCount;
    }

    // Scene components (API v7); layouts mirror unity_usd_toolkit_native.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportLight
    {
        public int NodeIndex;
        public int Type;
        public float ColorR;
        public float ColorG;
        public float ColorB;
        public float Intensity;
        public float Radius;
        public float Width;
        public float Height;
        public float Length;
        public float Angle;
        public float ConeAngle;
        public float ConeSoftness;
        public int Normalize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportCamera
    {
        public int NodeIndex;
        public int Projection;
        public float FocalLength;
        public float HorizontalAperture;
        public float VerticalAperture;
        public float NearClip;
        public float FarClip;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportAudio
    {
        public int NodeIndex;
        public int AuralMode;
        public int Loop;
        public float Gain;
        public double StartTime;
        public double EndTime;
    }

    // Import budgets for RUsd_OpenStageWithLimits (API v13); mirrors unity_usd_toolkit_native.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportLimits
    {
        public int StructSize;
        public int SkipWhenExceeded;
        public long MaxNodes;
        public long MaxMeshes;
        public long MaxInstancePlacements;
        public long MaxGeometryElements;
        public long MaxTrackSamples;
        public long MaxAnimationValues;
        public long MaxJoints;
    }

    // The stage API (API v13); layouts mirror unity_usd_toolkit_native.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdStageInfo
    {
        public int StructSize;
        public int HasTimeRange;
        public double StartTimeCode;
        public double EndTimeCode;
        public double TimeCodesPerSecond;
        public double FramesPerSecond;
        public double MetersPerUnit;
        public int UpAxis;
        public int Dirty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdAttributeInfo
    {
        public int StructSize;
        public int Exists;
        public int ValueType;
        public int HasValue;
        public int HasAuthoredValue;
        public int TimeSampleCount;
        public int Uniform;
        public int Custom;
    }

    // Time, animation and skinning (API v13); layouts mirror unity_usd_toolkit_native.h.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportTimeInfo
    {
        public double StartTimeCode;
        public double EndTimeCode;
        public double TimeCodesPerSecond;
        public double FramesPerSecond;
        public int HasTimeRange;
        public int ReadTimeKind; // 0 Default, 1 startTimeCode, 2 earliest sample
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportNodeTrack
    {
        public int NodeIndex;
        public int Kind; // 1 transform, 2 visibility
        public int SampleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportSkeleton
    {
        public int NodeIndex;
        public int JointCount;
        public int SampleCount;
        public int JointNamesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportMeshSkin
    {
        public int SkeletonIndex;
        public int InfluencesPerVertex;
        public int BlendShapeCount;
        public int BlendShapeSampleCount;
        public RUsdMatrix4x4 GeomBindTransform;
        public int DualQuaternion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdImportPointCache
    {
        public int SampleCount;
        public int VertexCount;
        public int HasNormals;
    }

    // A mesh's animated points as the read pass copies them (see GetImportMeshPointCache).
    internal sealed class UsdNativePointCache
    {
        public double[] Times;
        public RUsdVec3[] Points;   // Times.Length x vertex count, Unity basis
        public RUsdVec3[] Normals;  // likewise, or null when the mesh keeps its imported normals
    }

    // What the read pass copies out of a context for one animated node (see GetImportNodeTracks).
    internal sealed class UsdNativeNodeTrack
    {
        public RUsdImportNodeTrack Info;
        public double[] Times;
        public float[] Values; // 16 floats a sample (row-major, USD basis) or 1 (visibility)
    }

    internal sealed class UsdNativeSkeleton
    {
        public int NodeIndex;
        public string[] JointPaths;
        public int[] Parents;
        public float[] BindTransforms; // 16 floats a joint, skeleton space, USD basis
        public float[] RestTransforms; // joint local
        public float[] PoseTransforms; // joint local, at the read time
        public double[] Times;
        public float[] Samples;        // joint local, times x joints x 16
    }

    internal sealed class UsdNativeBlendShape
    {
        public string Name;
        public float[] Weights;            // one per frame, ascending, the last is 1
        public RUsdVec3[][] DeltaVertices; // per frame, one per vertex, Unity basis
        public RUsdVec3[][] DeltaNormals;  // per frame, or null when the shape has no normal offsets
    }

    internal sealed class UsdNativeSkin
    {
        public int SkeletonIndex;
        public int Influences;
        public int[] JointIndices;  // vertexCount x Influences, skeleton joint order
        public float[] JointWeights;
        public RUsdMatrix4x4 GeomBindTransform;
        public bool DualQuaternion;              // skel:skinningMethod = dualQuaternion
        public UsdNativeBlendShape[] BlendShapes;
        public float[] BlendShapeWeights;       // at the read time, one per shape
        public double[] BlendShapeWeightTimes;
        public float[] BlendShapeWeightSamples; // times x shapes
    }

    // Physics on import (API v13); layouts mirror unity_usd_toolkit_native.h, field for field.
    // Values are USD's: stage units, degrees, right-handed, the stage's up axis; quaternions x y z w.
    // Native~/Tests~/physics_import_test.cpp asserts the C sizes and UsdNativePhysicsLayoutTests the
    // same numbers here, so a layout change on one side fails on the other.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsList
    {
        public int Offset;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsScene
    {
        public int PathIndex;
        public int NodeIndex;
        public RUsdVec3 GravityDirection;
        public float GravityMagnitude;
        public uint SetMask;
        public float TimeStepsPerSecond;
        public float BounceThreshold;
        public int SolverPositionIterations;
        public int SolverVelocityIterations;
        public int EnableCcd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsBody
    {
        public int PathIndex;
        public int NodeIndex;
        public int ArticulationIndex;
        public RUsdVec3 Position;
        public RUsdVec4 Rotation;
        public RUsdVec3 Scale;
        public RUsdVec3 LinearVelocity;
        public RUsdVec3 AngularVelocity;
        public int Enabled;
        public int Kinematic;
        public int StartsAsleep;
        public RUsdPhysicsList Colliders;
        public RUsdPhysicsList Filtered;
        public uint SetMask;
        public float Mass;
        public float Density;
        public RUsdVec3 CenterOfMass;
        public RUsdVec3 DiagonalInertia;
        public RUsdVec4 PrincipalAxes;
        public float LinearDamping;
        public float AngularDamping;
        public float MaxLinearVelocity;
        public float MaxAngularVelocity;
        public float MaxDepenetrationVelocity;
        public float SleepThreshold;
        public int SolverPositionIterations;
        public int SolverVelocityIterations;
        public int EnableCcd;
        public int DisableGravity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsCollider
    {
        public int PathIndex;
        public int NodeIndex;
        public int Shape;
        public int BodyIndex;
        public RUsdVec3 LocalPosition;
        public RUsdVec4 LocalRotation;
        public RUsdVec3 LocalScale;
        public int Enabled;
        public int Axis;
        public float Radius0;
        public float Radius1;
        public float HalfHeight;
        public RUsdVec3 HalfExtents;
        public int MeshApproximation;
        public int MeshIndex;
        public RUsdVec3 MeshScale;
        public int DoubleSided;
        public RUsdPhysicsList MeshPoints;
        public RUsdPhysicsList MeshTriangles;
        public RUsdPhysicsList SpherePoints;
        public RUsdPhysicsList Materials;
        public RUsdPhysicsList Groups;
        public RUsdPhysicsList Filtered;
        public uint SetMask;
        public float Mass;
        public float Density;
        public float ContactOffset;
        public float RestOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsMaterial
    {
        public int PathIndex;
        public int NodeIndex;
        public float StaticFriction;
        public float DynamicFriction;
        public float Restitution;
        public float Density;
        public uint SetMask;
        public int FrictionCombineMode;
        public int RestitutionCombineMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsJointDof
    {
        public int LimitEnabled;
        public float Lower;
        public float Upper;
        public int DriveEnabled;
        public int DriveIsAcceleration;
        public float TargetPosition;
        public float TargetVelocity;
        public float MaxForce;
        public float Stiffness;
        public float Damping;
        public uint SetMask;
        public float StatePosition;
        public float StateVelocity;
        public float Armature;
        public float MaxJointVelocity;
        public float JointFriction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsJoint
    {
        public int PathIndex;
        public int NodeIndex;
        public int Type;
        public int Body0Index;
        public int Body1Index;
        public int ArticulationIndex;
        public int Axis;
        public RUsdVec3 LocalPosition0;
        public RUsdVec4 LocalRotation0;
        public RUsdVec3 LocalPosition1;
        public RUsdVec4 LocalRotation1;
        public int Enabled;
        public int CollisionEnabled;
        public int ExcludeFromArticulation;
        public float BreakForce;
        public float BreakTorque;
        public int ConeLimitEnabled;
        public float ConeAngle0;
        public float ConeAngle1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UsdNative.PhysicsDofCount)]
        public RUsdPhysicsJointDof[] Dofs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsArticulation
    {
        public int PathIndex;
        public int NodeIndex;
        public int RootBodyIndex;
        public int FixedBase;
        public RUsdPhysicsList Bodies;
        public RUsdPhysicsList Joints;
        public RUsdPhysicsList Filtered;
        public uint SetMask;
        public int EnabledSelfCollisions;
        public int SolverPositionIterations;
        public int SolverVelocityIterations;
        public float SleepThreshold;
        public float StabilizationThreshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsCollisionGroup
    {
        public int PathIndex;
        public int NodeIndex;
        public int InvertFilteredGroups;
        public RUsdPhysicsList FilteredGroups;
        public int MergeGroupNameIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsUnsupported
    {
        public int PathIndex;
        public int Kind;
        public int DetailIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsData
    {
        public int StructSize;
        public int UpAxis;
        public double MetersPerUnit;
        public double KilogramsPerUnit;
        public IntPtr Scenes;
        public IntPtr Bodies;
        public IntPtr Colliders;
        public IntPtr Materials;
        public IntPtr Joints;
        public IntPtr Articulations;
        public IntPtr Groups;
        public IntPtr Unsupported;
        public IntPtr Indices;
        public IntPtr Floats;
        public IntPtr Strings;
        public IntPtr StringOffsets;
        public int SceneCount;
        public int BodyCount;
        public int ColliderCount;
        public int MaterialCount;
        public int JointCount;
        public int ArticulationCount;
        public int GroupCount;
        public int UnsupportedCount;
        public int IndexCount;
        public int FloatCount;
        public int StringBytes;
        public int StringCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RUsdPhysicsReadOptions
    {
        public int StructSize;
        public int Flags;
        public int SkipWhenExceeded;
        public long MaxObjects;
        public long MaxMeshElements;
        public IntPtr RootPaths; // NUL-terminated UTF-8, newline-separated; IntPtr.Zero: the whole stage
    }

    // A copy of what RUsd_ReadPhysics returned, owned by the managed side: the native block is
    // released once this is filled, so nothing here points into native memory.
    // One PhysxMimicJointAPI:<dof> instance as authored.
    internal struct UsdPhysicsMimicSource
    {
        public string JointPath;
        public string ReferenceJointPath;
        public float Gearing;
        public float Offset;
    }

    internal sealed class UsdNativePhysics
    {
        public int UpAxis;               // UsdNative.PhysicsAxis*
        public double MetersPerUnit;
        public double KilogramsPerUnit;
        public RUsdPhysicsScene[] Scenes;
        public RUsdPhysicsBody[] Bodies;
        public RUsdPhysicsCollider[] Colliders;
        public RUsdPhysicsMaterial[] Materials;
        public RUsdPhysicsJoint[] Joints;
        public RUsdPhysicsArticulation[] Articulations;
        public RUsdPhysicsCollisionGroup[] Groups;
        public RUsdPhysicsUnsupported[] Unsupported;
        public int[] Indices;
        public float[] Floats;
        public string[] Strings;

        // PhysxMimicJointAPI couplings, read through the Stage API after the native read (whose
        // record names only the schema). Filled by UsdImporter.ReadPhysicsData.
        public UsdPhysicsMimicSource[] Mimics = Array.Empty<UsdPhysicsMimicSource>();

        public string String(int index) => index >= 0 && index < Strings.Length ? Strings[index] : null;

        // The entries of a list that points into `Indices`.
        public int[] List(RUsdPhysicsList list)
        {
            var items = new int[list.Count];
            Array.Copy(Indices, list.Offset, items, 0, list.Count);
            return items;
        }
    }
}
