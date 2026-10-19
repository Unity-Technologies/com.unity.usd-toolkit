using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Isaac Sim simulates the fleet (Tools~/agv_fleet). State arrives over the hub at 30 Hz; obstacles
    /// and goals travel through Unity's own layer (unity_ops.usda), and the hub only carries the
    /// "reload now" notice.
    ///
    /// Isaac's hello names the environment it simulates; when that is not the one this scene shows
    /// (e.g. Isaac was started before a Regenerate), its stream is ignored and <see cref="Note"/> says so.
    /// </summary>
    public sealed class IsaacHubBackend : IFleetBackend
    {
        public const string TokenFileName = "hub_token.txt";
        public const string TokenVariable = "AGV_FLEET_HUB_TOKEN";
        private const double StaleMs = 1500;

        private readonly HubClient hub;
        private readonly string host;
        private readonly int port;
        private IFleetEnvironment environment;
        private bool started;
        private bool helloSeen;
        private double lastRxMs = -1e9;
        private string mismatch;
        private bool authRejected;
        private HubMsg lastHelloAny;   // whatever Isaac announced on this connection

        public IsaacHubBackend(string host, int port, string stageDir)
        {
            this.host = host;
            this.port = port;
            hub = new HubClient(host, port, () => ReadToken(stageDir));
        }

        /// <summary>AGV_FLEET_HUB_TOKEN, else the token Isaac Sim wrote to the stage folder at launch.</summary>
        public static string ReadToken(string stageDir)
        {
            string env = Environment.GetEnvironmentVariable(TokenVariable);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            try
            {
                return File.ReadAllText(Path.Combine(stageDir, TokenFileName)).Trim();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public string DisplayName => "Isaac Sim";
        public string Endpoint => $"{host}:{port}";
        public bool Connected => hub.Connected;
        public int Reconnects => hub.Reconnects;

        /// <summary>Connected, authenticated, running this environment, and streaming.</summary>
        public bool Live => hub.Connected && helloSeen && mismatch == null && HubClient.WallMs() - lastRxMs < StaleMs;

        /// <summary>The last hello from an Isaac Sim that runs this environment.</summary>
        public HubMsg LastHello { get; private set; }

        public string Note =>
            authRejected ? "Isaac Sim rejected the hub token (stale hub_token.txt? restart Isaac Sim)"
            : mismatch ?? (hub.Connected || string.IsNullOrEmpty(hub.LastError) || hub.LastError.Contains("timeout") ? null : hub.LastError);

        public void Start(IFleetEnvironment env, UnityOpsLayer ops)
        {
            environment = env;
            mismatch = null;
            // a connected Isaac does not announce itself again: re-check what it said against this environment
            helloSeen = false;
            if (hub.Connected && lastHelloAny != null) CheckHello(lastHelloAny);
            if (!started)
            {
                started = true;
                hub.Start();
            }
        }

        public void Tick(float deltaTime)
        {
            if (!hub.Connected)
            {
                helloSeen = false;
                lastHelloAny = null;
            }
        }

        public bool TryReceive(out HubMsg msg, out double rxMs)
        {
            while (hub.Inbox.TryDequeue(out var item))
            {
                HubMsg m = item.msg;
                if (m == null) continue;
                if (m.type == "auth")
                {
                    authRejected = !m.ok;
                    continue;
                }
                if (m.type == "hello")
                {
                    authRejected = false;
                    lastHelloAny = m;
                    CheckHello(m);
                }
                if (!helloSeen || mismatch != null || environment == null) continue;
                lastRxMs = item.rxMs;
                msg = m;
                rxMs = item.rxMs;
                return true;
            }
            msg = null;
            rxMs = 0;
            return false;
        }

        /// <summary>
        /// Does Isaac's hello announce <paramref name="env"/>? By identity when Isaac sends one (it reads
        /// environment.json); otherwise (launched with an explicit --env preset) by environment name.
        /// </summary>
        public static bool SameEnvironment(HubMsg hello, IFleetEnvironment env)
        {
            if (hello == null || env == null) return false;
            return !string.IsNullOrEmpty(hello.env_identity)
                ? hello.env_identity == env.Identity
                : string.IsNullOrEmpty(hello.env) || hello.env == env.Name;
        }

        private void CheckHello(HubMsg m)
        {
            helloSeen = true;
            string theirs = string.IsNullOrEmpty(m.env_identity) ? m.env : m.env_identity;
            bool same = SameEnvironment(m, environment);
            string was = mismatch;
            mismatch = same ? null
                : $"Isaac Sim runs another environment ({theirs}); restart Isaac Sim to load this one ({environment?.Identity})";
            if (mismatch != null && mismatch != was) Debug.LogWarning("[AGV] " + mismatch);
            if (same) LastHello = m;
        }

        // Built by hand, invariant culture. (Do not write "{x:F1}}}" in an interpolated string: the
        // format becomes "F1}" and the JSON breaks -- that silently disabled these notices once.)
        private static string WallMsJson() => HubClient.WallMs().ToString("F1", CultureInfo.InvariantCulture);

        public void OpsChanged(int revision) =>
            hub.Send("{\"cmd\":\"layer_changed\",\"rev\":" + revision + ",\"wall_ms\":" + WallMsJson() + "}");

        public void Reset() => hub.Send("{\"cmd\":\"reset\",\"wall_ms\":" + WallMsJson() + "}");

        public void Dispose() => hub.Dispose();

        /// <summary>True when something accepts a TCP connection on the hub port within the timeout.</summary>
        public static bool IsReachable(string host, int port, int timeoutMs)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    return c.ConnectAsync(host, port).Wait(timeoutMs) && c.Connected;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
