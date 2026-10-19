using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    /// <summary>The single USD (Z-up, right-handed) to Unity (Y-up, left-handed) mapping.</summary>
    public class UsdFrameTests
    {
        [Test]
        public void PointRoundTripsThroughUnityFrame()
        {
            var usd = new Vector3(1.5f, -2.25f, 0.75f);
            Vector3 unity = UsdFrame.ToUnity(usd);
            Assert.AreEqual(new Vector3(1.5f, 0.75f, -2.25f), unity);
            Assert.AreEqual(usd, UsdFrame.ToUsd(unity));
        }

        [Test]
        public void UsdYawIsUnityRotationAboutUpInTheOppositeSense()
        {
            // +90 deg yaw about USD +Z turns +X (forward) into +Y; in Unity that is +X into +Z.
            Quaternion q = UsdFrame.YawToUnity(Mathf.PI * 0.5f);
            Vector3 fwd = q * Vector3.right;
            Assert.That(Vector3.Distance(fwd, UsdFrame.ToUnity(new Vector3(0f, 1f, 0f))), Is.LessThan(1e-5f));
            Assert.AreEqual(-30f, UsdFrame.UnityYawToUsdDeg(30f));
        }

        [Test]
        public void ImportedSubtreeRotationMapsImporterBasisToUnityFrame()
        {
            // The importer (with ApplyStageUpAxisAndUnits off) only flips X: USD (X, Y, Z) arrives as (-X, Y, Z).
            var usd = new Vector3(2f, 3f, 5f);
            Vector3 imported = new Vector3(-usd.x, usd.y, usd.z);
            Vector3 world = UsdFrame.ImportedSubtreeRotation * imported;
            Assert.That(Vector3.Distance(world, UsdFrame.ToUnity(usd)), Is.LessThan(1e-5f));
        }
    }
}
