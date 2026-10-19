using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Authored overlay for the NVIDIA "Factory" scene: where the AGVs start, the loops they drive, the
    /// stations on those loops and the planning bounds. Numbers only -- the factory geometry itself is
    /// never part of the sample. Same values as the "factory" preset in Tools~/agv_fleet/config.py.
    ///
    /// Factory hall (USD, meters): west lanes x -8..-4, east lanes x 5..11, the welding line and cell at
    /// x ~ 0, open halls north (y 17..25) and south (y -20..-30).
    /// </summary>
    [CreateAssetMenu(menuName = "USD Toolkit Samples/AGV Factory Layout", fileName = "FactoryLayout")]
    public sealed class FactoryLayout : ScriptableObject
    {
        [Tooltip("metersPerUnit of Factory.usd (a centimetre file).")]
        public float metersPerUnit = 0.01f;

        public FleetLayout layout = CreateDefault();

        private void Reset() => layout = CreateDefault();

        public static FleetLayout CreateDefault()
        {
            var l = new FleetLayout
            {
                gridBounds = Rect.MinMaxRect(-18f, -31f, 20f, 31f),
                freeSeed = new Vector2(2f, 20f),
            };
            AddLoop(l, "AGV_01", 0f, new[]
            {
                (new Vector2(-5f, 18.5f), "Dock_01", StationKind.Charge),
                (new Vector2(8f, 18.5f), "North_Pick", StationKind.Pick),
                (new Vector2(8f, 23f), "", StationKind.Pick),
                (new Vector2(-5f, 23f), "North_Drop", StationKind.Drop),
            });
            AddLoop(l, "AGV_02", 90f, new[]
            {
                (new Vector2(6.8f, -18f), "Dock_02", StationKind.Charge),
                (new Vector2(6.8f, 8f), "East_Pick", StationKind.Pick),
                (new Vector2(9.3f, 8f), "", StationKind.Pick),
                (new Vector2(9.3f, -18f), "East_Drop", StationKind.Drop),
            });
            AddLoop(l, "AGV_03", 90f, new[]
            {
                (new Vector2(-7.2f, -18f), "Dock_03", StationKind.Charge),
                (new Vector2(-7.2f, 8f), "West_Pick", StationKind.Pick),
                (new Vector2(-5.6f, 8f), "", StationKind.Pick),
                (new Vector2(-5.6f, -18f), "West_Drop", StationKind.Drop),
            });
            return l;
        }

        /// <summary>Spawn at the first waypoint (the AGV's dock), loop through all of them.</summary>
        private static void AddLoop(FleetLayout l, string agv, float spawnYawDeg, (Vector2 p, string station, StationKind kind)[] wps)
        {
            var loop = new RouteLoop { agvId = agv, waypoints = new List<Vector2>(), stations = new List<string>() };
            foreach (var (p, station, kind) in wps)
            {
                loop.waypoints.Add(p);
                loop.stations.Add(station);
                if (station.Length > 0)
                {
                    l.stations.Add(new Station { name = station, kind = kind, position = p, yawDeg = 0f });
                }
            }
            for (int i = 0; i < wps.Length; i++)
            {
                l.routeGraph.AddEdge(wps[i].p, wps[(i + 1) % wps.Length].p);   // the loops are the factory's lanes
            }
            l.loops.Add(loop);
            l.spawns.Add(new AgvSpawn { agvId = agv, position = wps[0].p, yawDeg = spawnYawDeg });
        }
    }
}
