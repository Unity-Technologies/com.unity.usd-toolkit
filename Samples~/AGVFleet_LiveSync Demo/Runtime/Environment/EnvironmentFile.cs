using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// environment.json in the stage folder: what Unity runs, for Isaac Sim. Written on every start and
    /// on Regenerate; Tools~/agv_fleet reads it at launch (preset "from_unity", the default when the file
    /// exists), so both sides load the same USD, spawns, loops and obstacles. Isaac echoes
    /// <see cref="identity"/> in its hello, which is how Unity knows Isaac runs the same environment.
    /// USD frame throughout (Z-up, meters, degrees).
    /// </summary>
    [Serializable]
    public sealed class EnvironmentFile
    {
        public const string FileName = "environment.json";

        [Serializable]
        public struct Spawn
        {
            public string id;
            public float x, y, yaw;
        }

        [Serializable]
        public struct Loop
        {
            public string id;
            public float[] points;   // x0, y0, x1, y1, ...
        }

        [Serializable]
        public struct Box
        {
            public float cx, cy, sx, sy, yaw;
        }

        public int version = 1;
        public string identity;
        public string name;
        public string usd;            // absolute path of the environment's USD file
        public float scale;           // metersPerUnit of that file
        public string grid_source;    // "boxes" (exact, from the layout) or "projection" (Isaac projects the meshes)
        public bool colliders;        // true: the USD has no colliders, Isaac generates them
        public float[] grid_bounds;   // xmin, ymin, xmax, ymax
        public float[] free_seed;     // x, y
        public Spawn[] spawns;
        public Loop[] loops;
        public Box[] boxes;

        public static EnvironmentFile From(IFleetEnvironment env)
        {
            FleetLayout l = env.Layout;
            bool exact = l.staticObstacles.Count > 0;
            var spawns = new List<Spawn>();
            foreach (AgvSpawn s in l.spawns) spawns.Add(new Spawn { id = s.agvId, x = s.position.x, y = s.position.y, yaw = s.yawDeg });
            var loops = new List<Loop>();
            foreach (RouteLoop lp in l.loops)
            {
                var pts = new float[lp.waypoints.Count * 2];
                for (int i = 0; i < lp.waypoints.Count; i++)
                {
                    pts[2 * i] = lp.waypoints[i].x;
                    pts[2 * i + 1] = lp.waypoints[i].y;
                }
                loops.Add(new Loop { id = lp.agvId, points = pts });
            }
            var boxes = new List<Box>();
            foreach (StaticBox b in l.staticObstacles)
            {
                boxes.Add(new Box { cx = b.center.x, cy = b.center.y, sx = b.size.x, sy = b.size.y, yaw = b.yawDeg });
            }
            Rect r = l.gridBounds;
            return new EnvironmentFile
            {
                identity = env.Identity,
                name = env.Name,
                usd = Path.GetFullPath(env.UsdPath).Replace('\\', '/'),
                scale = env.MetersPerUnit,
                grid_source = exact ? "boxes" : "projection",
                colliders = !exact,
                grid_bounds = new[] { r.xMin, r.yMin, r.xMax, r.yMax },
                free_seed = new[] { l.freeSeed.x, l.freeSeed.y },
                spawns = spawns.ToArray(),
                loops = loops.ToArray(),
                boxes = boxes.ToArray(),
            };
        }

        /// <summary>Writes atomically (Isaac Sim may be reading it at launch).</summary>
        public static string Write(IFleetEnvironment env, string stageDir)
        {
            string path = Path.Combine(stageDir, FileName);
            AtomicFile.WriteIfChanged(path, JsonUtility.ToJson(From(env), true));
            return path;
        }
    }
}
