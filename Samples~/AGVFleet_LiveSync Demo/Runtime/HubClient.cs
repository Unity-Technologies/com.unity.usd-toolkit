using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    // ---- wire messages (newline-delimited JSON from Tools~/agv_fleet; USD frame, meters) ----------
    // One union class: JsonUtility fills the fields a message has and leaves the rest at defaults.
    [Serializable]
    public class HubMsg
    {
        public string type;
        // state
        public int seq;
        public double wall_ms;
        public double sim_t;
        public double anim_tc;         // stage time code of Isaac's timeline (environment animation); 0 = not sent
        public AgvStateMsg[] agvs;
        // path
        public string agv;
        public int ver;
        public float[] pts;            // flat x0, y0, x1, y1, ...
        // fleet
        public AgvTaskMsg[] assign;
        public string[] queue;
        public int recoveries;         // deadlock recoveries
        public int stalls;             // stall (no-progress) recoveries
        // hello
        public string[] agv_ids;
        public float[] colors;         // flat r, g, b per AGV
        public string agv_usd;
        public string root_layer;
        public string unity_layer;
        public string mode;
        public float publish_hz;
        public string env;
        public string env_identity;    // the environment content Isaac simulates (environment.json identity)
        // auth (only sent on failure)
        public bool ok;
        // event
        public string @event;
        public string ref_id;
        public string detail;
        // pong
        public double t;
    }

    [Serializable]
    public class AgvStateMsg
    {
        public string id;
        public float[] p;              // USD position
        public float[] q;              // USD orientation (w, x, y, z)
        public float[] wh;             // wheel joint angles (rad): left, right
        public string nav;
        public string detail;
        public string task;
        public int pi;                 // index of the next path point
        public double sim_t;
    }

    [Serializable]
    public class AgvTaskMsg
    {
        public string agv;
        public string task;
    }

    /// <summary>
    /// TCP client to the Isaac hub with automatic reconnect. Socket I/O runs on a worker thread;
    /// the main thread drains <see cref="Inbox"/> and calls <see cref="Send"/>.
    ///
    /// Every connection starts with {"cmd":"auth","token":"..."}: the token comes from the provider,
    /// asked again on each attempt (Isaac Sim writes a new one to hub_token.txt on every launch).
    /// </summary>
    public sealed class HubClient : IDisposable
    {
        public readonly ConcurrentQueue<(HubMsg msg, double rxMs)> Inbox = new ConcurrentQueue<(HubMsg, double)>();
        public volatile bool Connected;
        public volatile int Reconnects;
        public string LastError = "";

        private readonly string host;
        private readonly int port;
        private readonly Func<string> tokenProvider;
        private readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
        private readonly AutoResetEvent outboxSignal = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool stop;
        private volatile TcpClient current;

        public HubClient(string host, int port, Func<string> tokenProvider)
        {
            this.host = host;
            this.port = port;
            this.tokenProvider = tokenProvider;
        }

        /// <summary>The token line; JSON-escaped by hand (the token is URL-safe base64, but be strict).</summary>
        private static string AuthLine(string token)
        {
            var sb = new StringBuilder("{\"cmd\":\"auth\",\"token\":\"");
            foreach (char ch in token ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\');
                if (ch >= ' ') sb.Append(ch);
            }
            return sb.Append("\"}\n").ToString();
        }

        public static double WallMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        public void Start()
        {
            stop = false;
            thread = new Thread(Run) { IsBackground = true, Name = "agv-hub-client" };
            thread.Start();
        }

        public void Send(string json)
        {
            if (!Connected)
            {
                return; // nothing queues while offline: the layer file is the durable channel
            }
            outbox.Enqueue(json);
            outboxSignal.Set();
        }

        public void Dispose()
        {
            stop = true;
            outboxSignal.Set();
            current?.Close(); // unblocks a pending Read so exiting play mode never waits on the socket
            thread?.Join(1000);
        }

        private void Run()
        {
            while (!stop)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = new TcpClient { NoDelay = true };
                    current = tcp;
                    var connect = tcp.ConnectAsync(host, port);
                    if (!connect.Wait(1000) || !tcp.Connected)
                    {
                        throw new IOException("connect timeout");
                    }
                    NetworkStream stream = tcp.GetStream();
                    string token = tokenProvider?.Invoke();
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new IOException("no hub token (hub_token.txt not found and AGV_FLEET_HUB_TOKEN not set)");
                    }
                    byte[] auth = Encoding.UTF8.GetBytes(AuthLine(token));
                    stream.Write(auth, 0, auth.Length);
                    Connected = true;
                    LastError = "";
                    var writer = new Thread(() => WriteLoop(stream, tcp)) { IsBackground = true, Name = "agv-hub-writer" };
                    writer.Start();
                    ReadLoop(stream);
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
                finally
                {
                    if (Connected)
                    {
                        Reconnects++;
                    }
                    Connected = false;
                    outboxSignal.Set();
                    tcp?.Close();
                }
                if (!stop)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private void ReadLoop(NetworkStream stream)
        {
            var buffer = new byte[1 << 16];
            var line = new StringBuilder();
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[1 << 16];
            while (!stop)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0)
                {
                    return;
                }
                double rx = WallMs();
                int c = decoder.GetChars(buffer, 0, n, chars, 0);
                for (int i = 0; i < c; i++)
                {
                    if (chars[i] != '\n')
                    {
                        line.Append(chars[i]);
                        continue;
                    }
                    if (line.Length > 0)
                    {
                        try
                        {
                            Inbox.Enqueue((JsonUtility.FromJson<HubMsg>(line.ToString()), rx));
                        }
                        catch (Exception e)
                        {
                            LastError = "bad message: " + e.Message;
                        }
                    }
                    line.Clear();
                }
            }
        }

        private void WriteLoop(NetworkStream stream, TcpClient tcp)
        {
            try
            {
                while (!stop && Connected)
                {
                    outboxSignal.WaitOne(500);
                    while (outbox.TryDequeue(out string json))
                    {
                        byte[] data = Encoding.UTF8.GetBytes(json + "\n");
                        stream.Write(data, 0, data.Length);
                    }
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
                tcp.Close();
            }
        }
    }
}
