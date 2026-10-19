using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    /// <summary>
    /// The generated hall as USD: layer metadata, the root prim and its groups, collision on exactly the floor
    /// and every blocker, and a round trip through the USD Toolkit importer (the file is valid USD, and Unity
    /// sees the visible parts only).
    /// </summary>
    public class GeneratedUsdTests
    {
        private static readonly string[] Groups = { "Looks", "Floor", "Walls", "Markings", "Racks", "Docks", "Stations", "Obstacles" };

        private string dir;

        [SetUp]
        public void MakeDir()
        {
            dir = Path.Combine(Path.GetTempPath(), "AgvFleetUsdTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void RemoveDir()
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }

        [Test]
        public void LayerIsZUpMetersWithTheGeneratedRootAsDefaultPrim()
        {
            string usda = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams()).ToUsda();
            StringAssert.StartsWith("#usda 1.0", usda);
            StringAssert.Contains("defaultPrim = \"GeneratedFactory\"", usda);
            StringAssert.Contains("upAxis = \"Z\"", usda);
            StringAssert.Contains("metersPerUnit = 1", usda);
            StringAssert.Contains("def Xform \"GeneratedFactory\" (\n    kind = \"assembly\"", usda);
        }

        [Test]
        public void RootHasTheExpectedGroups()
        {
            string usda = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams()).ToUsda();
            // direct children of the root are indented by exactly four spaces
            var top = Regex.Matches(usda, "^    def (?:Xform|Scope) \"(\\w+)\"", RegexOptions.Multiline)
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            CollectionAssert.AreEquivalent(Groups, top);
        }

        [Test]
        public void CollisionIsOnTheFloorAndEveryBlockerOnly()
        {
            GeneratedFactory f = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams());
            string usda = f.ToUsda();
            List<(string type, string name, string body)> prims = Prims(usda);
            var colliders = prims.Where(p => p.body.Contains("PhysicsCollisionAPI")).ToList();

            // the floor is a plane collider (see GeneratedFactoryBuilder.BuildShell for why not a box)
            Assert.AreEqual(1, colliders.Count(p => p.type == "Plane"));
            Assert.IsTrue(colliders.Any(p => p.type == "Plane" && p.name == "Ground"));
            // walls + one proxy per rack, dock, station and obstacle: exactly the layout's blockers
            Assert.AreEqual(1 + f.Layout.staticObstacles.Count, colliders.Count);
            foreach (var p in colliders.Where(p => p.type == "Cube"))
            {
                StringAssert.Contains("rel material:binding", p.body, p.name);
            }
            // paint, rack parts and loads never collide: AGVs must be able to drive over the markings
            Assert.IsFalse(prims.Any(p => p.name.StartsWith("Dash_") && p.body.Contains("PhysicsCollisionAPI")));
            Assert.IsFalse(prims.Any(p => p.name.StartsWith("Upright_") && p.body.Contains("PhysicsCollisionAPI")));
        }

        [Test]
        public void CollisionProxiesAreInvisibleAndEverythingElseIsVisible()
        {
            GeneratedFactory f = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams());
            List<(string type, string name, string body)> prims = Prims(f.ToUsda());
            var invisible = prims.Where(p => p.body.Contains("visibility = \"invisible\"")).ToList();
            Assert.AreEqual(f.Parts.Count(p => !p.visible), invisible.Count);
            Assert.IsTrue(invisible.All(p => p.name == "Collision"));
        }

        [Test]
        public void EveryBoundLookIsDefined()
        {
            string usda = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams()).ToUsda();
            var defined = new HashSet<string>(Regex.Matches(usda, "def Material \"(\\w+)\"").Cast<Match>().Select(m => m.Groups[1].Value));
            foreach (Match m in Regex.Matches(usda, "material:binding = </GeneratedFactory/Looks/(\\w+)>"))
            {
                Assert.IsTrue(defined.Contains(m.Groups[1].Value), m.Groups[1].Value);
            }
            StringAssert.Contains("uniform token info:id = \"UsdPreviewSurface\"", usda);
        }

        [Test]
        public void TheToolkitImportsTheHallWithItsGroupsAndWithoutTheProxies()
        {
            var provider = new GeneratedEnvironmentProvider(new GeneratedFactoryParams(), dir);
            provider.WriteUsd();
            UsdImportResult visibleOnly = null, all = null;
            try
            {
                visibleOnly = UsdImporter.Import(provider.UsdPath, new UsdImportOptions
                {
                    IncludeInvisible = false, ImportTextures = false, ApplyStageUpAxisAndUnits = false,
                });
                Transform root = visibleOnly.RootObject.transform.Find(UsdaSceneWriter.RootName);
                Assert.IsNotNull(root, "root prim /GeneratedFactory");
                var children = new List<string>();
                foreach (Transform c in root) children.Add(c.name);
                foreach (string g in Groups.Where(g => g != "Looks")) CollectionAssert.Contains(children, g);
                Assert.AreEqual("Z", visibleOnly.UpAxis);
                Assert.AreEqual(1.0, visibleOnly.MetersPerUnit, 1e-9);

                int proxies = provider.Factory.Parts.Count(p => !p.visible);
                Assert.AreEqual(provider.Factory.Parts.Count - proxies, visibleOnly.MeshCount, "one mesh per visible part");

                // The toolkit never reads invisible geometry (0.8.0): the proxy prims arrive as nodes without
                // a mesh, even with IncludeInvisible -- the collision boxes never render in Unity.
                all = UsdImporter.Import(provider.UsdPath, new UsdImportOptions
                {
                    IncludeInvisible = true, ImportTextures = false, ApplyStageUpAxisAndUnits = false,
                });
                var proxyNodes = all.RootObject.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Collision").ToList();
                Assert.AreEqual(proxies, proxyNodes.Count, "one node per collision proxy");
                Assert.IsTrue(proxyNodes.All(t => !t.GetComponent<MeshRenderer>()), "proxies have no mesh");
            }
            finally
            {
                if (visibleOnly?.RootObject) Object.DestroyImmediate(visibleOnly.RootObject);
                if (all?.RootObject) Object.DestroyImmediate(all.RootObject);
            }
        }

        [Test]
        public void WritingTheSameHallTwiceLeavesTheFileAlone()
        {
            var provider = new GeneratedEnvironmentProvider(new GeneratedFactoryParams(), dir);
            provider.WriteUsd();
            System.DateTime first = File.GetLastWriteTimeUtc(provider.UsdPath);
            File.SetLastWriteTimeUtc(provider.UsdPath, first.AddMinutes(-5));
            provider.WriteUsd();
            Assert.AreEqual(first.AddMinutes(-5), File.GetLastWriteTimeUtc(provider.UsdPath), "unchanged content is not rewritten");
        }

        /// <summary>(type, name, text up to the next def) of every Cube / Plane prim.</summary>
        private static List<(string type, string name, string body)> Prims(string usda)
        {
            var list = new List<(string, string, string)>();
            MatchCollection ms = Regex.Matches(usda, "def (Cube|Plane) \"(\\w+)\"");
            for (int i = 0; i < ms.Count; i++)
            {
                int end = i + 1 < ms.Count ? ms[i + 1].Index : usda.Length;
                list.Add((ms[i].Groups[1].Value, ms[i].Groups[2].Value, usda.Substring(ms[i].Index, end - ms[i].Index)));
            }
            return list;
        }
    }
}
