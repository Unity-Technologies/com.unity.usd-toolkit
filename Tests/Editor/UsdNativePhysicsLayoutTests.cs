using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The physics records cross the native boundary by layout alone (Marshal.PtrToStructure over
    /// the block RUsd_ReadPhysics returns), so the managed mirrors in UsdNativeTypes.cs must keep the
    /// sizes Native~/Tests~/physics_import_test.cpp asserts for the C structs. The two lists of
    /// numbers are the same on purpose: a layout change that touches only one side fails here or
    /// there. The types are internal, so they are found by name.
    /// </summary>
    public sealed class UsdNativePhysicsLayoutTests
    {
        [TestCase("RUsdPhysicsList", 8)]
        [TestCase("RUsdPhysicsScene", 48)]
        [TestCase("RUsdPhysicsBody", 196)]
        [TestCase("RUsdPhysicsCollider", 180)]
        [TestCase("RUsdPhysicsMaterial", 36)]
        [TestCase("RUsdPhysicsJointDof", 64)]
        [TestCase("RUsdPhysicsJoint", 564)]
        [TestCase("RUsdPhysicsArticulation", 64)]
        [TestCase("RUsdPhysicsCollisionGroup", 24)]
        [TestCase("RUsdPhysicsUnsupported", 12)]
        [TestCase("RUsdPhysicsData", 168)]
        [TestCase("RUsdPhysicsReadOptions", 40)]
        public void ManagedMirrorMatchesNativeSize(string typeName, int nativeSize)
        {
            Type type = typeof(UsdImportOptions).Assembly.GetType("Unity.USDToolkit.Native." + typeName);
            Assert.That(type, Is.Not.Null, typeName + " is missing from Runtime/Native/UsdNativeTypes.cs");
            Assert.That(Marshal.SizeOf(type), Is.EqualTo(nativeSize),
                typeName + " no longer matches its C layout in unity_usd_toolkit_native.h");
        }
    }
}
