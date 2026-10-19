using System.Collections.Generic;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Simulation "Auto": the local fleet until Isaac Sim streams the same environment, then Isaac Sim;
    /// back to the local fleet when Isaac Sim stops or goes quiet. Either side can start first or restart
    /// at any time. Only the active side's messages reach the scene; on every switch the new side
    /// announces itself (hello) so the views follow, and leftover paths are cleared.
    /// </summary>
    public sealed class AutoFleetBackend : IFleetBackend
    {
        private readonly LocalFleetBackend local = new LocalFleetBackend();
        private readonly IsaacHubBackend isaac;
        private readonly Queue<(HubMsg msg, double rxMs)> outbox = new Queue<(HubMsg, double)>();
        private readonly List<(HubMsg msg, double rxMs)> fromIsaac = new List<(HubMsg, double)>();
        private bool started;

        public AutoFleetBackend(string host, int port, string stageDir)
        {
            isaac = new IsaacHubBackend(host, port, stageDir);
        }

        public bool IsaacActive { get; private set; }
        public string DisplayName => IsaacActive ? isaac.DisplayName : local.DisplayName;
        public string Endpoint => isaac.Endpoint;
        public bool Connected => !IsaacActive || isaac.Connected;
        public int Reconnects => isaac.Reconnects;

        public string Note =>
            IsaacActive ? null
            : isaac.Note ?? (isaac.Connected ? "Isaac Sim connected, waiting for its state" : $"Isaac Sim offline ({isaac.Endpoint}): local fleet");

        public void Start(IFleetEnvironment environment, UnityOpsLayer ops)
        {
            local.Start(environment, ops);
            isaac.Start(environment, ops);
            started = true;
            if (IsaacActive)
            {
                IsaacActive = false;   // a new environment: Isaac must announce it again
                ClearPaths(isaac.LastHello);
            }
        }

        public void Tick(float deltaTime)
        {
            local.Tick(deltaTime);
            isaac.Tick(deltaTime);
        }

        public bool TryReceive(out HubMsg msg, out double rxMs)
        {
            if (outbox.Count == 0 && started) Pump();
            if (outbox.Count > 0)
            {
                (msg, rxMs) = outbox.Dequeue();
                return true;
            }
            msg = null;
            rxMs = 0;
            return false;
        }

        private void Pump()
        {
            fromIsaac.Clear();
            while (isaac.TryReceive(out HubMsg m, out double rx)) fromIsaac.Add((m, rx));
            bool want = isaac.Live;
            if (want != IsaacActive)
            {
                IsaacActive = want;
                if (want)
                {
                    if (isaac.LastHello != null) outbox.Enqueue((isaac.LastHello, HubClient.WallMs()));
                }
                else
                {
                    ClearPaths(isaac.LastHello);
                    local.Announce();
                }
            }
            if (IsaacActive)
            {
                foreach (var item in fromIsaac) outbox.Enqueue(item);
                while (local.TryReceive(out _, out _)) { }   // keep the local side drained
            }
            else
            {
                while (local.TryReceive(out HubMsg m, out double rx)) outbox.Enqueue((m, rx));
            }
        }

        private void ClearPaths(HubMsg hello)
        {
            if (hello?.agv_ids == null) return;
            foreach (string id in hello.agv_ids)
            {
                outbox.Enqueue((new HubMsg { type = "path", agv = id, ver = -1, pts = new float[0] }, HubClient.WallMs()));
            }
        }

        public void OpsChanged(int revision)
        {
            local.OpsChanged(revision);
            isaac.OpsChanged(revision);
        }

        public void Reset()
        {
            local.Reset();
            isaac.Reset();
        }

        public void Dispose()
        {
            local.Dispose();
            isaac.Dispose();
        }
    }
}
