using System.IO;
using NUnit.Framework;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    /// <summary>Mode selection: Auto falls back with one message; explicit modes are always respected.</summary>
    public class EnvironmentSelectionTests
    {
        private static readonly string Factory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pack", "Factory.usd"));

        [Test]
        public void AutoWithMissingFactoryFallsBackToGeneratedWithDownloadLink()
        {
            EnvironmentChoice c = EnvironmentSelector.Choose(EnvironmentMode.Auto, Factory, _ => false);
            Assert.AreEqual(EnvironmentKind.Generated, c.Kind);
            Assert.IsFalse(c.FactoryFound);
            Assert.IsFalse(c.MessageIsError);
            StringAssert.Contains(AgvFleetSettings.AssetPackUrl, c.Message);
            StringAssert.Contains(Factory, c.Message);
        }

        [Test]
        public void AutoWithFactoryPresentUsesFactorySilently()
        {
            EnvironmentChoice c = EnvironmentSelector.Choose(EnvironmentMode.Auto, Factory, p => p == Factory);
            Assert.AreEqual(EnvironmentKind.Factory, c.Kind);
            Assert.IsNull(c.Message);
        }

        [Test]
        public void AutoWithNoPathFallsBackToGenerated()
        {
            EnvironmentChoice c = EnvironmentSelector.Choose(EnvironmentMode.Auto, "", _ => true);
            Assert.AreEqual(EnvironmentKind.Generated, c.Kind);
            Assert.IsNotNull(c.Message);
        }

        [Test]
        public void FactoryOverrideIsRespectedEvenWhenMissing()
        {
            EnvironmentChoice c = EnvironmentSelector.Choose(EnvironmentMode.Factory, Factory, _ => false);
            Assert.AreEqual(EnvironmentKind.Factory, c.Kind);
            Assert.IsTrue(c.MessageIsError);
            StringAssert.Contains(AgvFleetSettings.AssetPackUrl, c.Message);
        }

        [Test]
        public void GeneratedOverrideIsRespectedEvenWhenFactoryExists()
        {
            EnvironmentChoice c = EnvironmentSelector.Choose(EnvironmentMode.Generated, Factory, _ => true);
            Assert.AreEqual(EnvironmentKind.Generated, c.Kind);
            Assert.IsTrue(c.FactoryFound);
            Assert.IsNull(c.Message);
        }

        [Test]
        public void RelativePackPathResolvesAgainstProjectFolder()
        {
            string baseDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Project"));
            string dir = EnvironmentSelector.ResolvePackDir("ExternalAssets/Pack", null, baseDir);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(baseDir, "ExternalAssets", "Pack")), dir);
        }

        [Test]
        public void EnvironmentVariableOverridesConfiguredPackPath()
        {
            string baseDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Project"));
            string over = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Elsewhere"));
            Assert.AreEqual(over, EnvironmentSelector.ResolvePackDir("ExternalAssets/Pack", over, baseDir));
        }

        [Test]
        public void EmptyPackPathResolvesToNoFactory()
        {
            Assert.AreEqual("", EnvironmentSelector.ResolvePackDir("  ", null, Path.GetTempPath()));
            Assert.AreEqual("", EnvironmentSelector.FactoryUsdPath("", "Factory.usd"));
        }

        [Test]
        public void FactoryLayoutDefaultsAreConsistent()
        {
            FleetLayout l = FactoryLayout.CreateDefault();
            Assert.AreEqual(3, l.spawns.Count);
            foreach (AgvSpawn s in l.spawns)
            {
                RouteLoop loop = l.LoopFor(s.agvId);
                Assert.IsNotNull(loop, s.agvId);
                Assert.AreEqual(loop.waypoints.Count, loop.stations.Count);
                Assert.AreEqual(s.position, loop.waypoints[0], "an AGV starts at its dock, the loop's first waypoint");
                Assert.IsTrue(l.gridBounds.Contains(s.position));
                foreach (string name in loop.stations)
                {
                    if (name.Length > 0) Assert.IsTrue(l.TryGetStation(name, out _), name);
                }
            }
            Assert.IsTrue(l.gridBounds.Contains(l.freeSeed));
        }
    }
}
