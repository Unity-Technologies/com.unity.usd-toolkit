using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// The fleet without Isaac Sim, in-process. Speaks the same messages as the Isaac hub. This version
    /// parks every AGV at its spawn pose; planning and driving are not implemented yet.
    /// </summary>
    public sealed class LocalFleetBackend : IFleetBackend
    {
        public const float PublishHz = 30f;

        private readonly Queue<(HubMsg msg, double rxMs)> outbox = new Queue<(HubMsg, double)>();
        private readonly List<AgvSpawn> spawns = new List<AgvSpawn>();
        private float sinceState;
        private float sinceFleet;
        private int seq;
        private double simT;

        public string DisplayName => "Local fleet";
        public string Endpoint => "in-process";
        public bool Connected => true;
        public int Reconnects => 0;
        public string Note => null;

        public void Start(IFleetEnvironment environment, UnityOpsLayer ops)
        {
            spawns.Clear();
            spawns.AddRange(environment.Layout.spawns);
            Announce();
        }

        /// <summary>Posts the hello (AGV ids and colours) again, e.g. when this side takes over.</summary>
        public void Announce()
        {
            var ids = new string[spawns.Count];
            var colors = new float[3 * spawns.Count];
            for (int i = 0; i < spawns.Count; i++)
            {
                ids[i] = spawns[i].agvId;
                Color c = AgvPalette.ForIndex(i);
                colors[3 * i] = c.r;
                colors[3 * i + 1] = c.g;
                colors[3 * i + 2] = c.b;
            }
            Post(new HubMsg { type = "hello", agv_ids = ids, colors = colors, mode = "local", publish_hz = PublishHz });
        }

        public void Tick(float deltaTime)
        {
            simT += deltaTime;
            sinceState += deltaTime;
            sinceFleet += deltaTime;
            if (sinceState >= 1f / PublishHz)
            {
                sinceState = 0f;
                PostState();
            }
            if (sinceFleet >= 1f)
            {
                sinceFleet = 0f;
                Post(new HubMsg { type = "fleet", assign = new AgvTaskMsg[0], queue = new string[0] });
            }
        }

        private void PostState()
        {
            var agvs = new AgvStateMsg[spawns.Count];
            for (int i = 0; i < spawns.Count; i++)
            {
                AgvSpawn s = spawns[i];
                float half = s.yawDeg * Mathf.Deg2Rad * 0.5f;
                agvs[i] = new AgvStateMsg
                {
                    id = s.agvId,
                    p = new[] { s.position.x, s.position.y, AgvSpec.AxleHeight },
                    q = new[] { Mathf.Cos(half), 0f, 0f, Mathf.Sin(half) },   // (w, x, y, z), yaw about +Z
                    wh = new[] { 0f, 0f },
                    nav = "idle",
                    detail = "parked",
                    task = "",
                    sim_t = simT,
                };
            }
            double now = HubClient.WallMs();
            Post(new HubMsg { type = "state", seq = ++seq, wall_ms = now, sim_t = simT, agvs = agvs });
        }

        private void Post(HubMsg m) => outbox.Enqueue((m, HubClient.WallMs()));

        public bool TryReceive(out HubMsg msg, out double rxMs)
        {
            if (outbox.Count > 0)
            {
                (msg, rxMs) = outbox.Dequeue();
                return true;
            }
            msg = null;
            rxMs = 0;
            return false;
        }

        public void OpsChanged(int revision)
        {
        }

        public void Reset()
        {
        }

        public void Dispose() => outbox.Clear();
    }
}
