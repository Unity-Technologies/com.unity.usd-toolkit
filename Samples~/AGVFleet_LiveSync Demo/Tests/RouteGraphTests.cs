using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo.Tests
{
    public class RouteGraphTests
    {
        //  a(0,0) --- b(4,0) --- c(8,0)
        //               |
        //             d(4,3)        e(20,20) on its own
        private static RouteGraph Sample()
        {
            var g = new RouteGraph();
            g.AddEdge(new Vector2(0, 0), new Vector2(4, 0));
            g.AddEdge(new Vector2(4, 0), new Vector2(8, 0));
            g.AddEdge(new Vector2(4, 0), new Vector2(4, 3));
            g.AddNode(new Vector2(20, 20));
            return g;
        }

        [Test]
        public void NodesMergeAndEdgesAreUndirectedAndUnique()
        {
            RouteGraph g = Sample();
            Assert.AreEqual(5, g.nodes.Count);
            Assert.AreEqual(g.FindNode(new Vector2(4, 0)), g.AddNode(new Vector2(4.001f, 0)));
            g.AddEdge(new Vector2(4, 0), new Vector2(0, 0));   // same edge, reversed
            Assert.AreEqual(3, g.edges.Count);
        }

        [Test]
        public void ConnectivityAndReachability()
        {
            RouteGraph g = Sample();
            Assert.IsFalse(g.IsConnected);
            Assert.AreEqual(4, g.ReachableCount(g.FindNode(new Vector2(0, 0))));
            g.AddEdge(new Vector2(8, 0), new Vector2(20, 20));
            Assert.IsTrue(g.IsConnected);
        }

        [Test]
        public void ShortestPathFollowsTheEdges()
        {
            RouteGraph g = Sample();
            var path = g.ShortestPath(g.FindNode(new Vector2(0, 0)), g.FindNode(new Vector2(4, 3)));
            CollectionAssert.AreEqual(new[] { g.FindNode(new Vector2(0, 0)), g.FindNode(new Vector2(4, 0)), g.FindNode(new Vector2(4, 3)) }, path);
            Assert.IsEmpty(g.ShortestPath(g.FindNode(new Vector2(0, 0)), g.FindNode(new Vector2(20, 20))));
        }

        [Test]
        public void SegmentToBoxDistance()
        {
            var box = new StaticBox { center = new Vector2(0, 0), size = new Vector2(2, 1) };
            Assert.AreEqual(1.5f, RouteGraph.SegmentBoxDistance(new Vector2(-5, 2), new Vector2(5, 2), box), 1e-3f);
            Assert.AreEqual(0f, RouteGraph.SegmentBoxDistance(new Vector2(-5, 0), new Vector2(5, 0), box), 1e-3f, "crossing");
            // rotated 90 degrees: the long side now runs along y
            var turned = new StaticBox { center = Vector2.zero, size = new Vector2(2, 1), yawDeg = 90f };
            Assert.AreEqual(1.0f, RouteGraph.SegmentBoxDistance(new Vector2(-5, 2), new Vector2(5, 2), turned), 1e-3f);
            Assert.AreEqual(0f, RouteGraph.SegmentDistance(new Vector2(1, 0), new Vector2(0, 0), new Vector2(2, 0)), 1e-6f);
        }
    }
}
