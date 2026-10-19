using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    /// <summary>The generated hall: deterministic per seed, lanes connected, stations reachable, lanes clear.</summary>
    public class GeneratedFactoryTests
    {
        private static IEnumerable<GeneratedFactoryParams> Cases()
        {
            yield return new GeneratedFactoryParams();
            yield return new GeneratedFactoryParams { hallLength = 24f, hallWidth = 16f, rackRows = 10, rackColumns = 6, stationCount = 8, agvCount = 6, seed = 7 };
            yield return new GeneratedFactoryParams { hallLength = 120f, hallWidth = 80f, rackRows = 10, rackColumns = 6, aisleWidth = 6f, stationCount = 8, agvCount = 6, seed = -3 };
            yield return new GeneratedFactoryParams { hallLength = 36f, hallWidth = 24f, rackRows = 2, rackColumns = 1, aisleWidth = 2.6f, stationCount = 1, agvCount = 1, seed = 42 };
        }

        [Test]
        public void SameSeedGivesTheSameHall()
        {
            foreach (GeneratedFactoryParams p in Cases())
            {
                GeneratedFactory a = GeneratedFactoryBuilder.Build(p.Clone());
                GeneratedFactory b = GeneratedFactoryBuilder.Build(p.Clone());
                Assert.AreEqual(a.ToUsda(), b.ToUsda(), p.ToString());
                Assert.AreEqual(JsonUtility.ToJson(a.Layout), JsonUtility.ToJson(b.Layout), p.ToString());
            }
        }

        [Test]
        public void DifferentSeedsGiveDifferentHalls()
        {
            string a = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams { seed = 1 }).ToUsda();
            string b = GeneratedFactoryBuilder.Build(new GeneratedFactoryParams { seed = 2 }).ToUsda();
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void RouteGraphIsConnected()
        {
            foreach (GeneratedFactoryParams p in Cases())
            {
                RouteGraph g = GeneratedFactoryBuilder.Build(p).Layout.routeGraph;
                Assert.Greater(g.nodes.Count, 4, p.ToString());
                Assert.IsTrue(g.IsConnected, p.ToString());
            }
        }

        [Test]
        public void EveryStationIsOnTheGraphAndReachableFromEveryDock()
        {
            foreach (GeneratedFactoryParams p in Cases())
            {
                FleetLayout l = GeneratedFactoryBuilder.Build(p).Layout;
                RouteGraph g = l.routeGraph;
                foreach (AgvSpawn s in l.spawns)
                {
                    int from = g.FindNode(s.position);
                    Assert.GreaterOrEqual(from, 0, $"{p}: {s.agvId} spawn is not on a lane");
                    foreach (Station st in l.stations)
                    {
                        int to = g.FindNode(st.position);
                        Assert.GreaterOrEqual(to, 0, $"{p}: {st.name} is not on a lane");
                        Assert.IsNotEmpty(g.ShortestPath(from, to), $"{p}: {st.name} unreachable from {s.agvId}");
                    }
                }
            }
        }

        [Test]
        public void LoopsStartAtTheDockAndVisitPickAndDrop()
        {
            foreach (GeneratedFactoryParams p in Cases())
            {
                GeneratedFactory f = GeneratedFactoryBuilder.Build(p);
                FleetLayout l = f.Layout;
                Assert.AreEqual(f.Params.agvCount, l.spawns.Count, p.ToString());
                foreach (AgvSpawn s in l.spawns)
                {
                    RouteLoop loop = l.LoopFor(s.agvId);
                    Assert.IsNotNull(loop, s.agvId);
                    Assert.AreEqual(s.position, loop.waypoints[0], $"{p}: {s.agvId} starts at its dock");
                    var kinds = new HashSet<StationKind>();
                    foreach (string name in loop.stations)
                    {
                        if (name.Length > 0 && l.TryGetStation(name, out Station st)) kinds.Add(st.kind);
                    }
                    CollectionAssert.AreEquivalent(new[] { StationKind.Charge, StationKind.Pick, StationKind.Drop }, kinds, $"{p}: {s.agvId}");
                    // consecutive waypoints lie on one straight lane (axis aligned in the generated hall)
                    for (int i = 0; i < loop.waypoints.Count; i++)
                    {
                        Vector2 a = loop.waypoints[i], b = loop.waypoints[(i + 1) % loop.waypoints.Count];
                        Assert.IsTrue(Mathf.Abs(a.x - b.x) < 1e-3f || Mathf.Abs(a.y - b.y) < 1e-3f, $"{p}: {s.agvId} leg {i} is diagonal");
                    }
                }
            }
        }

        [Test]
        public void NoGeometryOverlapsTheLanes()
        {
            // station spurs end 1.0 m (GeneratedFactoryBuilder.StationStandoff) in front of their station
            const float clearance = GeneratedFactoryBuilder.StationStandoff - 0.05f;
            foreach (GeneratedFactoryParams p in Cases())
            {
                FleetLayout l = GeneratedFactoryBuilder.Build(p).Layout;
                RouteGraph g = l.routeGraph;
                Assert.IsNotEmpty(l.staticObstacles);
                foreach (StaticBox box in l.staticObstacles)
                {
                    foreach (Vector2Int e in g.edges)
                    {
                        float d = RouteGraph.SegmentBoxDistance(g.nodes[e.x], g.nodes[e.y], box);
                        Assert.GreaterOrEqual(d, clearance, $"{p}: {box.name} is {d:F2} m from lane {g.nodes[e.x]}-{g.nodes[e.y]}");
                    }
                }
            }
        }

        [Test]
        public void EverythingStaysInsideTheHall()
        {
            foreach (GeneratedFactoryParams p in Cases())
            {
                FleetLayout l = GeneratedFactoryBuilder.Build(p).Layout;
                foreach (Vector2 n in l.routeGraph.nodes) Assert.IsTrue(l.gridBounds.Contains(n), $"{p}: lane node {n}");
                Assert.IsTrue(l.gridBounds.Contains(l.freeSeed));
            }
        }
    }
}
