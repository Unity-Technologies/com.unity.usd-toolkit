using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    /// <summary>
    /// What Unity tells Isaac Sim (environment.json, read by Tools~/agv_fleet/config.py) and how Unity decides
    /// that a running Isaac Sim simulates the same environment.
    /// </summary>
    public class LiveSyncContractTests
    {
        private string dir;

        [SetUp]
        public void MakeDir()
        {
            dir = Path.Combine(Path.GetTempPath(), "AgvFleetContract_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void RemoveDir()
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }

        [Test]
        public void GeneratedHallIsDescribedExactly()
        {
            var env = new GeneratedEnvironmentProvider(new GeneratedFactoryParams(), dir);
            EnvironmentFile f = EnvironmentFile.From(env);
            FleetLayout l = env.Layout;
            Assert.AreEqual(env.Identity, f.identity);
            Assert.AreEqual("generated", f.name);
            Assert.AreEqual("boxes", f.grid_source, "Isaac plans on the exact boxes");
            Assert.IsFalse(f.colliders, "the hall brings its own UsdPhysics colliders");
            Assert.AreEqual(1f, f.scale);
            Assert.IsTrue(Path.IsPathRooted(f.usd));
            Assert.AreEqual(l.spawns.Count, f.spawns.Length);
            Assert.AreEqual(l.loops.Count, f.loops.Length);
            Assert.AreEqual(l.staticObstacles.Count, f.boxes.Length);
            for (int i = 0; i < l.loops.Count; i++)
            {
                Assert.AreEqual(l.loops[i].waypoints.Count * 2, f.loops[i].points.Length);
                Assert.AreEqual(l.loops[i].waypoints[0].x, f.loops[i].points[0]);
                Assert.AreEqual(l.loops[i].waypoints[0].y, f.loops[i].points[1]);
            }
            CollectionAssert.AreEqual(new[] { l.gridBounds.xMin, l.gridBounds.yMin, l.gridBounds.xMax, l.gridBounds.yMax }, f.grid_bounds);
        }

        [Test]
        public void FactoryLetsIsaacProjectTheMeshesAndBuildColliders()
        {
            string usd = Path.Combine(dir, "Factory.usd");
            var env = new FactoryEnvironmentProvider(usd, null, dir);
            EnvironmentFile f = EnvironmentFile.From(env);
            Assert.AreEqual("projection", f.grid_source);
            Assert.IsTrue(f.colliders, "the factory has no colliders of its own");
            Assert.AreEqual(0.01f, f.scale);
            Assert.AreEqual(0, f.boxes.Length);
            Assert.AreEqual(3, f.spawns.Length);
            Assert.AreEqual("factory:Factory.usd", f.identity);
        }

        [Test]
        public void FileRoundTripsAndIsOnlyRewrittenWhenItChanges()
        {
            var env = new GeneratedEnvironmentProvider(new GeneratedFactoryParams(), dir);
            string path = EnvironmentFile.Write(env, dir);
            var back = JsonUtility.FromJson<EnvironmentFile>(File.ReadAllText(path));
            Assert.AreEqual(env.Identity, back.identity);
            Assert.AreEqual(env.Layout.spawns.Count, back.spawns.Length);
            // the JSON keys the Isaac side reads (Tools~/agv_fleet/config.py load_unity_environment)
            string json = File.ReadAllText(path);
            foreach (string key in new[] { "identity", "usd", "scale", "grid_source", "colliders", "grid_bounds", "free_seed", "spawns", "loops", "boxes" })
            {
                StringAssert.Contains($"\"{key}\"", json);
            }
            Assert.IsFalse(AtomicFile.WriteIfChanged(path, json), "same content: left alone");
            Assert.IsTrue(AtomicFile.WriteIfChanged(path, json + " "), "new content: written");
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test]
        public void IsaacRunningTheSameEnvironmentIsRecognisedByIdentity()
        {
            var env = new GeneratedEnvironmentProvider(new GeneratedFactoryParams { seed = 5 }, dir);
            var other = new GeneratedEnvironmentProvider(new GeneratedFactoryParams { seed = 6 }, dir);
            var hello = new HubMsg { type = "hello", env = "from_unity", env_identity = env.Identity };
            Assert.IsTrue(IsaacHubBackend.SameEnvironment(hello, env));
            Assert.IsFalse(IsaacHubBackend.SameEnvironment(hello, other), "another seed is another environment");
        }

        [Test]
        public void IsaacLaunchedWithAPresetIsRecognisedByName()
        {
            var factory = new FactoryEnvironmentProvider(Path.Combine(dir, "Factory.usd"), null, dir);
            var generated = new GeneratedEnvironmentProvider(new GeneratedFactoryParams(), dir);
            var hello = new HubMsg { type = "hello", env = "factory" };   // --env factory, no environment.json
            Assert.IsTrue(IsaacHubBackend.SameEnvironment(hello, factory));
            Assert.IsFalse(IsaacHubBackend.SameEnvironment(hello, generated));
            Assert.IsFalse(IsaacHubBackend.SameEnvironment(new HubMsg { env = "warehouse" }, factory));
            Assert.IsFalse(IsaacHubBackend.SameEnvironment(null, factory));
        }

        [Test]
        public void PaletteMatchesTheIsaacSide()
        {
            // Tools~/agv_fleet/config.py color_for: the first three are fixed, then an HSV sequence
            void Same(Color expected, Color actual)
            {
                for (int i = 0; i < 3; i++) Assert.AreEqual(expected[i], actual[i], 1e-5f, $"{expected} vs {actual}");
            }
            Same(new Color(0.10f, 0.55f, 1.00f), AgvPalette.ForIndex(0));
            Same(new Color(1.00f, 0.55f, 0.05f), AgvPalette.ForIndex(1));
            Same(new Color(0.20f, 0.80f, 0.30f), AgvPalette.ForIndex(2));
            // colorsys.hsv_to_rgb((0.13 + 3 * 0.31) % 1.0, 0.75, 0.95), computed in Python
            Same(new Color(0.95f, 0.49400002f, 0.2375f), AgvPalette.ForIndex(3));
            var six = Enumerable.Range(0, 6).Select(AgvPalette.ForIndex).ToList();
            Assert.AreEqual(6, six.Distinct().Count());
        }
    }
}
