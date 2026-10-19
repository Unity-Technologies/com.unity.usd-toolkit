using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    [Serializable]
    public class ObstacleRecord
    {
        public string name;            // prim name under /World/Dynamic/Obstacles
        public string preset;
        public float x, y;             // USD frame, meters (footprint centre)
        public float yawDeg;           // USD yaw about +Z
        public Vector3 size;           // USD extents x (length), y (width), z (height)
        public Color color;
        public double createdWallMs;
    }

    [Serializable]
    public class GoalRecord
    {
        public bool active;
        public int seq;
        public float x, y;
        public string targetAgv = "";
        public double createdWallMs;
    }

    /// <summary>
    /// Owner and only writer of unity_ops.usda: /World/Dynamic/Obstacles/* and /World/Dynamic/Goal.
    ///
    /// The layer is written as plain USDA text (the usd-toolkit has no API for editing an opened
    /// layer), atomically (temp file + replace) so Isaac never reloads a half-written file. A JSON
    /// sidecar with the same records lets Unity restore its own state after a restart without
    /// parsing USD. After every write the hub is told "layer_changed"; Isaac also polls the file's
    /// mtime, so a missed notice costs at most LAYER_POLL_S.
    /// </summary>
    public sealed class UnityOpsLayer
    {
        [Serializable]
        private class Sidecar
        {
            public int rev;
            public int nextObstacleId = 1;
            public int resetSeq;
            public string environment = "";   // root.usda's environment reference these obstacles belong to
            public GoalRecord goal = new GoalRecord();
            public List<ObstacleRecord> obstacles = new List<ObstacleRecord>();
        }

        private readonly string layerPath;
        private readonly string sidecarPath;
        private Sidecar state = new Sidecar();

        public UnityOpsLayer(string layerPath)
        {
            this.layerPath = layerPath;
            sidecarPath = Path.ChangeExtension(layerPath, ".state.json");
        }

        public IReadOnlyList<ObstacleRecord> Obstacles => state.obstacles;
        public GoalRecord Goal => state.goal;
        public int Revision => state.rev;
        public double LastWriteMs { get; private set; }
        public float LastWriteDurationMs { get; private set; }

        /// <param name="environment">identity of the environment in root.usda; saved obstacles from
        /// a different environment are dropped (they would land inside walls or machines).</param>
        public void LoadOrCreate(string environment)
        {
            if (File.Exists(sidecarPath))
            {
                try
                {
                    state = JsonUtility.FromJson<Sidecar>(File.ReadAllText(sidecarPath)) ?? new Sidecar();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AGV] unity_ops sidecar unreadable, starting empty: {e.Message}");
                    state = new Sidecar();
                }
            }
            // A goal is a one-shot command, obstacles are state: after a restart keep the obstacles
            // but never re-issue the last goal (its seq keeps counting).
            state.goal.active = false;
            if (state.environment != environment)
            {
                if (state.obstacles.Count > 0)
                {
                    Debug.Log($"[AGV] environment changed ({state.environment} -> {environment}): dropping {state.obstacles.Count} saved obstacles");
                }
                state.obstacles.Clear();
                state.environment = environment;
            }
            Write(); // always re-assert our layer on startup (it is ours alone)
        }

        public ObstacleRecord AddObstacle(string preset, float x, float y, float yawDeg, Vector3 size, Color color)
        {
            var rec = new ObstacleRecord
            {
                name = $"Obstacle_{state.nextObstacleId++:D4}",
                preset = preset,
                x = x,
                y = y,
                yawDeg = yawDeg,
                size = size,
                color = color,
                createdWallMs = HubClient.WallMs(),
            };
            state.obstacles.Add(rec);
            Write();
            return rec;
        }

        public bool RemoveObstacle(string name)
        {
            int n = state.obstacles.RemoveAll(o => o.name == name);
            if (n > 0)
            {
                Write();
            }
            return n > 0;
        }

        public GoalRecord SetGoal(float x, float y, string targetAgv)
        {
            state.goal = new GoalRecord
            {
                active = true,
                seq = state.goal.seq + 1,
                x = x,
                y = y,
                targetAgv = targetAgv ?? "",
                createdWallMs = HubClient.WallMs(),
            };
            Write();
            return state.goal;
        }

        /// <summary>Clears obstacles and the goal; bumps resetSeq (Isaac clears queue + tasks).</summary>
        public void Reset()
        {
            state.obstacles.Clear();
            int seq = state.goal.seq; // keep counting so a later goal is never mistaken for an old one
            state.goal = new GoalRecord { active = false, seq = seq };
            state.resetSeq++;
            Write();
        }

        private void Write()
        {
            double t0 = HubClient.WallMs();
            state.rev++;
            Directory.CreateDirectory(Path.GetDirectoryName(layerPath));
            AtomicWrite(layerPath, BuildUsda());
            AtomicWrite(sidecarPath, JsonUtility.ToJson(state, true));
            LastWriteMs = HubClient.WallMs();
            LastWriteDurationMs = (float)(LastWriteMs - t0);
        }

        private static void AtomicWrite(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            // Isaac may hold the file open for a moment while reloading it: retry briefly.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tmp, path, null);
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(5);
                }
            }
        }

        private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        private string BuildUsda()
        {
            var sb = new StringBuilder(2048);
            sb.Append("#usda 1.0\n(\n");
            sb.Append("    doc = \"Written only by Unity (AGV operator front end). Do not edit by hand.\"\n");
            sb.Append($"    customLayerData = {{\n        int unityRev = {state.rev}\n        double unityWallMs = {F(HubClient.WallMs())}\n    }}\n");
            sb.Append(")\n\n");
            sb.Append("over \"World\"\n{\n    over \"Dynamic\"\n    {\n");

            sb.Append("        def Scope \"Obstacles\"\n        {\n");
            sb.Append($"            custom int unity:resetSeq = {state.resetSeq}\n");
            foreach (ObstacleRecord o in state.obstacles)
            {
                sb.Append($"\n            def Cube \"{o.name}\" (\n");
                sb.Append("                prepend apiSchemas = [\"PhysicsCollisionAPI\"]\n            )\n            {\n");
                sb.Append("                double size = 1\n");
                sb.Append($"                color3f[] primvars:displayColor = [({F(o.color.r)}, {F(o.color.g)}, {F(o.color.b)})]\n");
                sb.Append($"                double3 xformOp:translate = ({F(o.x)}, {F(o.y)}, {F(o.size.z * 0.5f)})\n");
                sb.Append($"                float xformOp:rotateZ = {F(o.yawDeg)}\n");
                sb.Append($"                float3 xformOp:scale = ({F(o.size.x)}, {F(o.size.y)}, {F(o.size.z)})\n");
                sb.Append("                uniform token[] xformOpOrder = [\"xformOp:translate\", \"xformOp:rotateZ\", \"xformOp:scale\"]\n");
                sb.Append($"                custom string unity:preset = \"{o.preset}\"\n");
                sb.Append($"                custom double unity:createdWallMs = {F(o.createdWallMs)}\n");
                sb.Append("            }\n");
            }
            sb.Append("        }\n");

            if (state.goal.active)
            {
                GoalRecord g = state.goal;
                sb.Append("\n        def Xform \"Goal\"\n        {\n");
                sb.Append($"            double3 xformOp:translate = ({F(g.x)}, {F(g.y)}, 0)\n");
                sb.Append("            uniform token[] xformOpOrder = [\"xformOp:translate\"]\n");
                sb.Append($"            custom int fleet:goalSeq = {g.seq}\n");
                sb.Append($"            custom string fleet:targetAgv = \"{g.targetAgv}\"\n");
                sb.Append($"            custom double unity:createdWallMs = {F(g.createdWallMs)}\n");
                sb.Append("        }\n");
            }
            sb.Append("    }\n}\n");
            return sb.ToString();
        }
    }
}
