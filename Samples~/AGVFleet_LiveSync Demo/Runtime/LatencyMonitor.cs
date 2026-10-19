using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Unity-side latency log + rolling stats. Both apps run on one machine and stamp events with
    /// the same wall clock (ms since the Unix epoch), so cross-app differences need no clock sync.
    ///
    /// CSV (logs/unity_latency_*.csv): wall_ms, event, agv, ref, value_ms, detail
    ///   pose_lag        per AGV per state update: Unity receive time - Isaac pose-read time
    ///   obstacle_created / obstacle_deleted / goal_set      (Unity actions)
    ///   isaac:<event>   every Isaac event relayed over the hub (Isaac's own timestamp)
    /// Chains (obstacle created -> seen -> replan -> moving, goal set -> assigned -> moving) are
    /// computed here for the panel and offline by Tools~/agv_fleet/analyze_latency.py.
    /// </summary>
    public sealed class LatencyMonitor : IDisposable
    {
        public sealed class Chain
        {
            public string Ref;
            public double StartMs;           // Unity action time
            public double SeenMs = -1;       // visible on the Isaac stage
            public double ReplanMs = -1;     // first re-plan / assignment caused by it
            public double MovingMs = -1;     // first AGV moving on the resulting path
            public string Agv = "";
        }

        private readonly StreamWriter csv;
        private readonly Dictionary<string, Queue<(double t, float lag)>> lags = new Dictionary<string, Queue<(double, float)>>();
        private readonly Queue<double> stateTimes = new Queue<double>();
        public readonly Dictionary<string, Chain> Chains = new Dictionary<string, Chain>();
        public readonly List<Chain> RecentChains = new List<Chain>();
        public string CsvPath { get; }

        public LatencyMonitor(string logDir)
        {
            Directory.CreateDirectory(logDir);
            CsvPath = Path.Combine(logDir, $"unity_latency_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            csv = new StreamWriter(CsvPath, false, new UTF8Encoding(false)) { AutoFlush = false };
            csv.WriteLine("wall_ms,event,agv,ref,value_ms,detail");
        }

        public void Log(double wallMs, string evt, string agv = "", string reference = "", double valueMs = double.NaN, string detail = "")
        {
            csv.Write(wallMs.ToString("F1", CultureInfo.InvariantCulture));
            csv.Write(',');
            csv.Write(evt);
            csv.Write(',');
            csv.Write(agv);
            csv.Write(',');
            csv.Write(Esc(reference));
            csv.Write(',');
            csv.Write(double.IsNaN(valueMs) ? "" : valueMs.ToString("F1", CultureInfo.InvariantCulture));
            csv.Write(',');
            csv.WriteLine(Esc(detail));
        }

        private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : (s.IndexOf(',') >= 0 ? "\"" + s + "\"" : s);

        public void Flush() => csv.Flush();

        // ---- pose lag ---------------------------------------------------------------------------
        /// <summary>
        /// lag = time the pose is applied to the Unity transform (main thread, before this frame
        /// renders) - time Isaac read it from physics. The socket-arrival lag goes in `detail`.
        /// </summary>
        public void OnState(double rxMs, double applyMs, double isaacMs, IEnumerable<string> agvIds)
        {
            float lag = (float)(applyMs - isaacMs);
            string rxLag = (rxMs - isaacMs).ToString("F1", CultureInfo.InvariantCulture);
            stateTimes.Enqueue(rxMs);
            while (stateTimes.Count > 0 && rxMs - stateTimes.Peek() > 5000)
            {
                stateTimes.Dequeue();
            }
            foreach (string id in agvIds)
            {
                if (!lags.TryGetValue(id, out var q))
                {
                    lags[id] = q = new Queue<(double, float)>();
                }
                q.Enqueue((rxMs, lag));
                while (q.Count > 0 && rxMs - q.Peek().t > 10000)
                {
                    q.Dequeue();
                }
                Log(applyMs, "pose_lag", id, "", lag, "rx " + rxLag);
            }
        }

        /// <summary>State messages per second over the last 5 s.</summary>
        public float StateRateHz
        {
            get
            {
                if (stateTimes.Count < 2)
                {
                    return 0f;
                }
                double span = (HubClient.WallMs() - stateTimes.Peek()) / 1000.0;
                return span > 0 ? (float)(stateTimes.Count / span) : 0f;
            }
        }

        public (float median, float p95, float max) LagStats(string agv)
        {
            if (!lags.TryGetValue(agv, out var q) || q.Count == 0)
            {
                return (float.NaN, float.NaN, float.NaN);
            }
            var v = new List<float>(q.Count);
            foreach (var e in q)
            {
                v.Add(e.lag);
            }
            v.Sort();
            return (v[v.Count / 2], v[Math.Max(0, (int)(v.Count * 0.95f) - 1)], v[v.Count - 1]);
        }

        // ---- action chains ----------------------------------------------------------------------
        public void StartChain(string reference, double startMs, string evt, string detail = "")
        {
            var c = new Chain { Ref = reference, StartMs = startMs };
            Chains[reference] = c;
            RecentChains.Insert(0, c);
            if (RecentChains.Count > 8)
            {
                RecentChains.RemoveAt(RecentChains.Count - 1);
            }
            Log(startMs, evt, "", reference, double.NaN, detail);
        }

        /// <summary>Relay an Isaac event into the log and advance any chain it belongs to.</summary>
        public void OnIsaacEvent(HubMsg e)
        {
            Log(e.wall_ms, "isaac:" + e.@event, e.agv, e.ref_id, double.NaN, e.detail);
            if (string.IsNullOrEmpty(e.ref_id))
            {
                return;
            }
            foreach (string r in e.ref_id.Split(','))
            {
                if (!Chains.TryGetValue(r, out Chain c))
                {
                    continue;
                }
                switch (e.@event)
                {
                    case "obstacle_seen":
                    case "obstacle_removed":
                    case "goal_seen":
                        if (c.SeenMs < 0) c.SeenMs = e.wall_ms;
                        break;
                    case "replan_started":
                    case "task_assigned":
                        if (c.ReplanMs < 0) { c.ReplanMs = e.wall_ms; c.Agv = e.agv; }
                        break;
                    case "moving_new_path":
                        if (c.MovingMs < 0) { c.MovingMs = e.wall_ms; c.Agv = e.agv; }
                        break;
                }
            }
        }

        public void Dispose()
        {
            csv.Flush();
            csv.Dispose();
        }
    }
}
