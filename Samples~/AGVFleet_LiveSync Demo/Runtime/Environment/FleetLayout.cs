using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    // Everything here is in the USD stage frame (Z-up, right-handed, meters), like the hub wire format
    // and the USD layers. Convert to Unity only through UsdFrame.

    public enum StationKind
    {
        Pick,
        Drop,
        Charge,
    }

    /// <summary>A named stop on the floor. Stations are waypoints of the AGV loops.</summary>
    [Serializable]
    public struct Station
    {
        public string name;
        public StationKind kind;
        public Vector2 position;
        public float yawDeg;
    }

    /// <summary>Where an AGV starts (its charging dock in the generated layout).</summary>
    [Serializable]
    public struct AgvSpawn
    {
        public string agvId;
        public Vector2 position;
        public float yawDeg;
    }

    /// <summary>
    /// The closed route one AGV drives when it has no goal. Its waypoints are the route graph's edges
    /// for that AGV; each waypoint may name the station it serves.
    /// </summary>
    [Serializable]
    public class RouteLoop
    {
        public string agvId;
        public List<Vector2> waypoints = new List<Vector2>();
        public List<string> stations = new List<string>();   // per waypoint, "" when not a station
    }

    /// <summary>An axis-aligned-in-its-own-frame box that blocks AGVs (racks, machines, columns).</summary>
    [Serializable]
    public struct StaticBox
    {
        public string name;
        public Vector2 center;
        public Vector2 size;      // x length, y width
        public float height;
        public float yawDeg;
    }

    /// <summary>
    /// The drivable description of an environment: what the fleet logic needs, independent of how
    /// the geometry was made.
    /// </summary>
    [Serializable]
    public class FleetLayout
    {
        [Tooltip("Planning area (USD x min, y min, width, height in meters).")]
        public Rect gridBounds;
        [Tooltip("A point known to be on free, reachable floor; everything not connected to it is blocked.")]
        public Vector2 freeSeed;
        public List<AgvSpawn> spawns = new List<AgvSpawn>();
        public List<RouteLoop> loops = new List<RouteLoop>();
        [Tooltip("The lanes: every loop and every station spur runs along its edges.")]
        public RouteGraph routeGraph = new RouteGraph();
        public List<Station> stations = new List<Station>();
        [Tooltip("Exact obstacle footprints. Empty: the planner rasterises the environment's meshes instead.")]
        public List<StaticBox> staticObstacles = new List<StaticBox>();

        public IEnumerable<string> AgvIds
        {
            get { foreach (AgvSpawn s in spawns) yield return s.agvId; }
        }

        public RouteLoop LoopFor(string agvId) => loops.Find(l => l.agvId == agvId);

        public bool TryGetStation(string name, out Station station)
        {
            int i = stations.FindIndex(s => s.name == name);
            station = i >= 0 ? stations[i] : default;
            return i >= 0;
        }
    }
}
