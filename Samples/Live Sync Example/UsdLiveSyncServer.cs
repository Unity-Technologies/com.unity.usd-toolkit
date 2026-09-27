using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    /// <summary>
    /// Bidirectional, near-real-time transform sync between this Unity scene and external clients
    /// (<c>Tools~/usd_live_sync.py</c>, the Isaac Sim extension in <c>Tools~/isaacsim</c>, or a DCC tool),
    /// using USD as the on-disk record.
    ///
    /// Topology: Unity is the host, external tools are clients, and the transport is newline-delimited JSON
    /// over loopback TCP. A single connection carries both a broadcast direction (Unity streams transform
    /// changes out) and a command direction (clients push edits in).
    ///
    /// Responsibilities:
    ///   * Phase 0 — assign a USD prim path to every tracked GameObject under <see cref="syncRoot"/>, using
    ///     the same rules the package exporter uses (<c>SanitizePrimName</c> + unique-path suffixing +
    ///     <c>GetComponentsInChildren&lt;Transform&gt;</c> order), so paths agree with what
    ///     <see cref="UsdExporter"/> actually writes.
    ///   * Phase 1 — write <c>base_stage.usda</c> once via <see cref="UsdExporter"/> (PreserveHierarchy) and
    ///     capture the in-memory baseline transform of every tracked node.
    ///   * Phase 2 — TCP server: accept thread + per-client read thread + a sender thread that broadcasts.
    ///   * Phase 3 — throttled per-frame dirty-diff: prims whose local transform changed beyond an epsilon are
    ///     batched into one <c>delta</c> broadcast per tick.
    ///   * Phase 4 — inbound <c>set_transform</c> (filtered by <see cref="UsdSyncNode.AcceptsRemoteWrites"/>),
    ///     <c>reset</c> (restore baseline + fire <see cref="SceneReset"/> + broadcast snapshot), and
    ///     <c>get_snapshot</c> (reply full state to the requesting client).
    ///
    /// The live transform channel is built entirely outside <see cref="UsdImporter"/>/<see cref="UsdExporter"/>;
    /// the package is used exactly once per session for the baseline geometry snapshot. Wire values are RAW
    /// Unity local-space transforms (translate float[3], rotation quaternion [x,y,z,w], scale float[3]); the
    /// Unity→USD basis flip is applied by the Python side when it authors <c>live_overrides.usda</c>.
    /// </summary>
    public sealed class UsdLiveSyncServer : MonoBehaviour
    {
        [Header("Binding (localhost dev tool — do not expose beyond loopback)")]
        [SerializeField] private string bindAddress = "127.0.0.1";
        [Tooltip("TCP port the sample listens on. Must match the client's --port (default 10000).")]
        [SerializeField] private int port = 10000;
        [SerializeField] private int backlog = 8;

        [Header("Security")]
        [Tooltip("Shared secret every client must present — {\"cmd\":\"auth\",\"token\":\"...\"} — before any other " +
                 "command is accepted. Leave empty and the server generates a random token at start and writes it " +
                 "to '<outputDirectory>/live_sync_token.txt' for local clients to read. The " +
                 "USD_LIVE_SYNC_TOKEN environment variable is used if this is empty and the variable is set.")]
        [SerializeField] private string authToken = "";
        [Tooltip("Seconds a newly connected client has to authenticate before it is disconnected.")]
        [SerializeField] private float authTimeoutSeconds = 10f;
        [Tooltip("Maximum simultaneous connections. Further connection attempts are refused.")]
        [SerializeField] private int maxClients = 8;
        [Tooltip("Maximum bytes a client may send without a newline before the connection is dropped. Raise it " +
                 "only if a single set_transform command for your scene legitimately exceeds it.")]
        [SerializeField] private int maxCommandBytes = 64 * 1024;

        [Header("Sync scope")]
        [Tooltip("Root of the subtree to sync. If null, this GameObject is used.")]
        [SerializeField] private Transform syncRoot;
        [Tooltip("AllDescendants: every Transform under the root is tracked (good for a scene of static props). " +
                 "ExplicitNodesOnly: only Transforms carrying a UsdSyncNode are tracked — use this to sync a " +
                 "character's root transform without dragging in its animated skeleton bones, the camera, etc.")]
        [SerializeField] private TrackMode trackMode = TrackMode.AllDescendants;

        public enum TrackMode { AllDescendants, ExplicitNodesOnly }

        [Header("Baseline export")]
        [Tooltip("Write base_stage.usda on start via the USD Toolkit exporter. If the native plugin is " +
                 "unavailable the live channel still runs; only the on-disk baseline is skipped.")]
        [SerializeField] private bool exportBaselineOnStart = true;
        [Tooltip("Output folder for base_stage.usda (and where the Python client writes live_overrides.usda). " +
                 "Empty = '<project>/UsdSync' in the Editor, or '<persistentDataPath>/UsdSync' in a player.")]
        [SerializeField] private string outputDirectory = "";
        [SerializeField] private string baseStageFileName = "base_stage.usda";
        [Tooltip("Bake SkinnedMeshRenderers (characters) into temporary static meshes for the baseline export, " +
                 "so the avatar's geometry appears in base_stage.usda. The exporter otherwise only writes " +
                 "MeshFilter+MeshRenderer geometry and skips skinned meshes. Bind-pose snapshot; the live " +
                 "channel still only drives the tracked root transform.")]
        [SerializeField] private bool bakeSkinnedMeshes = true;

        [Header("Broadcast throttle")]
        [Tooltip("Run the dirty-diff every Nth frame (1 = every frame). Keeps large hierarchies cheap.")]
        [SerializeField] private int broadcastEveryNFrames = 3;
        [Tooltip("Position/scale change (squared magnitude) below this is treated as float noise and not sent.")]
        [SerializeField] private float translateEpsilon = 1e-4f;
        [Tooltip("Rotation change (degrees) below this is treated as float noise and not sent.")]
        [SerializeField] private float rotationEpsilonDegrees = 0.05f;

        [Header("Startup")]
        [SerializeField] private bool autoStart = true;

        /// <summary>
        /// Raised on the main thread when a client sends <c>{"cmd":"reset"}</c>, after every tracked transform
        /// has been restored to its captured baseline. Subscribe to restart your own simulation state alongside
        /// the transform restore (a nav run, a spawner, a score) without this server needing to know anything
        /// about that logic. Guarded so a faulty subscriber cannot break the reset.
        /// </summary>
        public static event Action SceneReset;

        // ---- Tracked node -------------------------------------------------------------------------

        private sealed class Node
        {
            public Transform Tr;
            public string Path;
            public UsdSyncNode Sync;         // may be null -> observe-only
            public Rigidbody Rb;             // may be null -> not a physics body
            public Vector3 BaseT, BaseS;
            public Quaternion BaseR;
            public Vector3 LastT, LastS;     // last value broadcast (or applied) — the echo-suppression state
            public Quaternion LastR;

            public bool AcceptsRemoteWrites => Sync != null && Sync.AcceptsRemoteWrites;

            // Per-channel toggles (a node with no UsdSyncNode syncs all three, for AllDescendants mode).
            public bool SyncPos => Sync == null || Sync.SyncPosition;
            public bool SyncRot => Sync == null || Sync.SyncRotation;
            public bool SyncScl => Sync == null || Sync.SyncScale;
        }

        private readonly List<Node> _nodes = new();
        private readonly Dictionary<string, Node> _byPath = new(StringComparer.Ordinal);

        // ---- Networking (accept + per-client read threads; one sender thread) ---------------------

        private TcpListener _listener;
        private Thread _acceptThread;
        private Thread _senderThread;
        private volatile bool _running;

        // Hard limits on anything an unauthenticated peer can drive. Every one of these exists because the
        // value it bounds is attacker-controlled: nesting depth, queue length, connection count.
        private const int MaxJsonDepth = 32;
        private const int MaxQueuedCommands = 1024;
        private const string TokenFileName = "live_sync_token.txt";
        private const string TokenEnvironmentVariable = "USD_LIVE_SYNC_TOKEN";

        /// <summary>One connected client and its authentication state. Nothing is served before it authenticates.</summary>
        private sealed class ClientSession
        {
            public TcpClient Client;
            public DateTime ConnectedUtc;
            public volatile bool Authenticated;
        }

        private readonly object _clientsLock = new();
        private readonly List<ClientSession> _sessions = new();

        // The secret clients must present. Resolved once per StartServer from authToken / env var / generated.
        private string _resolvedToken;
        private int _queuedCommands;

        private readonly ConcurrentQueue<Inbound> _inbox = new();     // socket threads -> main thread
        private readonly ConcurrentQueue<string> _outbox = new();     // main thread -> sender thread (broadcast)
        private readonly AutoResetEvent _outboxSignal = new(false);

        private long _seq;
        private int _frameCounter;

        private sealed class Inbound
        {
            public TcpClient Client;
            public string Json;
        }

        // Mirror of the package exporter's prim-name sanitizer (UsdExporter.InvalidPrimNameChars) so the paths
        // this server assigns are identical to the ones base_stage.usda is written with.
        private static readonly char[] InvalidPrimNameChars =
        {
            ' ', '-', '.', ':', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ';', '\'', '"'
        };

        // ==========================================================================================
        // Lifecycle
        // ==========================================================================================

        private void OnEnable()
        {
            if (autoStart)
                StartServer();
        }

        private void OnDisable()
        {
            StopServer();
        }

        // ---- Read-only status (for the sample UI / your own HUD) ----------------------------------

        /// <summary>Whether the listener and its worker threads are currently running.</summary>
        public bool IsRunning => _running;

        /// <summary>Number of tracked prims resolved by the last <see cref="StartServer"/>.</summary>
        public int TrackedNodeCount => _nodes.Count;

        /// <summary>Number of connected clients, including those that have not authenticated yet.</summary>
        public int ClientCount
        {
            get { lock (_clientsLock) return _sessions.Count; }
        }

        /// <summary>Number of clients that have presented a valid token. Only these are sent scene data.</summary>
        public int AuthenticatedClientCount
        {
            get
            {
                lock (_clientsLock)
                {
                    int n = 0;
                    foreach (var session in _sessions)
                        if (session.Authenticated) n++;
                    return n;
                }
            }
        }

        /// <summary>Address the listener is bound to.</summary>
        public string BindAddress => bindAddress;

        /// <summary>Port the listener is bound to.</summary>
        public int Port => port;

        /// <summary>Folder holding <c>base_stage.usda</c> (and where a client writes its override layer).</summary>
        public string OutputDirectory => ResolveOutputDir();

        /// <summary>Total messages broadcast or replied since start (the monotonic wire sequence number).</summary>
        public long SequenceNumber => Interlocked.Read(ref _seq);

        /// <summary>Read-only view of one tracked node, for a HUD or an inspector.</summary>
        public readonly struct TrackedPrim
        {
            public TrackedPrim(string primPath, Transform transform, bool acceptsRemoteWrites)
            {
                PrimPath = primPath;
                Transform = transform;
                AcceptsRemoteWrites = acceptsRemoteWrites;
            }

            /// <summary>USD prim path this transform is streamed as, e.g. <c>/SyncRoot/PropCube</c>.</summary>
            public string PrimPath { get; }

            /// <summary>The tracked transform.</summary>
            public Transform Transform { get; }

            /// <summary>Whether inbound <c>set_transform</c> writes are applied to it.</summary>
            public bool AcceptsRemoteWrites { get; }
        }

        /// <summary>Every tracked node, in the order the exporter walks the hierarchy.</summary>
        public IEnumerable<TrackedPrim> TrackedPrims
        {
            get
            {
                foreach (var n in _nodes)
                    yield return new TrackedPrim(n.Path, n.Tr, n.AcceptsRemoteWrites);
            }
        }

        /// <summary>
        /// Root of the subtree to sync. Assign before <see cref="StartServer"/> (the prim table and the
        /// baseline are built from it at start); <c>null</c> means this GameObject's own transform.
        /// </summary>
        public Transform SyncRoot
        {
            get => syncRoot;
            set => syncRoot = value;
        }

        public void StartServer()
        {
            if (_running)
                return;

            Transform root = syncRoot != null ? syncRoot : transform;

            // Phase 0 + baseline dict: assign prim paths and capture the starting local transform of every node.
            BuildPrimTable(root);
            if (_nodes.Count == 0)
                Debug.LogWarning($"[UsdLiveSyncServer] No tracked transforms under '{root.name}'. Sync will be idle.");

            // Phase 1: one-shot baseline geometry snapshot (best-effort; the live channel does not depend on it).
            if (exportBaselineOnStart)
                ExportBaseline(root);

            // Phase 2: resolve the shared secret, validate the bind address, then start the socket threads.
            if (!TryResolveAuthToken(out _resolvedToken))
                return;

            if (!TryResolveBindAddress(bindAddress, out IPAddress ip))
                return;

            try
            {
                _listener = new TcpListener(ip, port);
                // ExclusiveAddressUse (never ReuseAddress) so a co-located process cannot rebind this port and
                // hijack client connections. Not supported on every platform — a failure here is not fatal.
                try { _listener.ExclusiveAddressUse = true; }
                catch (Exception e) { Debug.LogWarning($"[UsdLiveSyncServer] Exclusive port use unavailable — {e.Message}"); }
                _listener.Start(backlog);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UsdLiveSyncServer] Failed to bind {ip}:{port} — {e.Message}");
                _listener = null;
                return;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "UsdLiveSync-Accept" };
            _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "UsdLiveSync-Sender" };
            _acceptThread.Start();
            _senderThread.Start();

            Debug.Log($"[UsdLiveSyncServer] Listening on {ip}:{port} — {_nodes.Count} tracked prim(s) under '{root.name}'.");
        }

        public void StopServer()
        {
            if (!_running)
                return;
            _running = false;

            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;

            _outboxSignal.Set(); // wake sender so it can exit

            lock (_clientsLock)
            {
                foreach (var session in _sessions) SafeClose(session.Client);
                _sessions.Clear();
            }

            while (_inbox.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedCommands, 0);
            _resolvedToken = null;

            _acceptThread?.Join(200);
            _senderThread?.Join(200);
            _acceptThread = null;
            _senderThread = null;
        }

        /// <summary>
        /// Resolve the listen address. This channel is loopback-only, and there is no opt-in to widen it.
        /// </summary>
        /// <remarks>
        /// The transport is plain TCP: the auth token and the whole scene stream cross it in clear text. That
        /// is acceptable over the loopback interface and nowhere else, so rather than warn about a non-loopback
        /// bind and serve it anyway, the server refuses to start. Run the client on the same machine — or put
        /// your own authenticated, encrypted transport in front of this sample if you need it across a network.
        /// </remarks>
        private bool TryResolveBindAddress(string addr, out IPAddress ip)
        {
            ip = IPAddress.Loopback;

            if (string.IsNullOrWhiteSpace(addr) || addr == "localhost")
                return true;

            if (!IPAddress.TryParse(addr, out IPAddress parsed))
            {
                Debug.LogWarning($"[UsdLiveSyncServer] bindAddress '{addr}' is not a valid IP address — binding to loopback.");
                return true;
            }

            if (!IPAddress.IsLoopback(parsed))
            {
                Debug.LogError(
                    $"[UsdLiveSyncServer] Refusing to bind '{addr}': this sample serves loopback only. Its traffic " +
                    "is unencrypted, so a bind reachable from other machines would put the auth token and the " +
                    "scene stream on the wire in clear text. Use 127.0.0.1 and run the client on this machine. " +
                    "Server not started.");
                return false;
            }

            ip = parsed;
            return true;
        }

        /// <summary>
        /// Resolve the shared secret: the inspector field, else <c>USD_LIVE_SYNC_TOKEN</c>, else a freshly
        /// generated one written next to <c>base_stage.usda</c> so local clients can pick it up.
        /// </summary>
        private bool TryResolveAuthToken(out string token)
        {
            token = null;

            if (!string.IsNullOrEmpty(authToken))
            {
                token = authToken;
                return true;
            }

            string fromEnvironment = null;
            try { fromEnvironment = Environment.GetEnvironmentVariable(TokenEnvironmentVariable); }
            catch (Exception) { /* some players disallow environment access */ }

            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                token = fromEnvironment;
                Debug.Log($"[UsdLiveSyncServer] Using the auth token from ${TokenEnvironmentVariable}.");
                return true;
            }

            string generated = GenerateToken();
            string tokenPath = Path.Combine(ResolveOutputDir(), TokenFileName);

            if (!TryWriteTokenFile(tokenPath, generated))
            {
                token = null;
                return false;
            }

            token = generated;
            Debug.Log(
                $"[UsdLiveSyncServer] Generated a session auth token at '{tokenPath}' (owner-readable only). " +
                "Clients running as you on this machine read it from there; usd_live_sync.py finds it automatically.");
            return true;
        }

        /// <summary>
        /// Write the generated token so that only the current user can read it.
        /// </summary>
        /// <remarks>
        /// The secret is written to a randomly named file in the destination folder, restricted to the owner
        /// while it is still empty, and only then filled in and renamed into place. Writing straight to
        /// <see cref="TokenFileName"/> and restricting afterwards would publish the token under the process
        /// umask (0644 on a typical macOS/Linux host) for the window in between, which is long enough for
        /// another local account to read it — and an attacker who opened the file in that window keeps read
        /// access through the open handle even after the permissions are tightened. An unguessable temporary
        /// name closes that window: there is nothing to open until the rename, which is atomic.
        ///
        /// If the permissions cannot be restricted the token is NOT written at all and the server does not
        /// start. A world-readable token file is the vulnerability, so failing closed is the only safe
        /// outcome; the operator can set an explicit token instead.
        /// </remarks>
        private static bool TryWriteTokenFile(string tokenPath, string token)
        {
            string directory = Path.GetDirectoryName(tokenPath);
            if (string.IsNullOrEmpty(directory))
                directory = ".";

            string stagingPath = Path.Combine(directory, TokenFileName + "." + GenerateToken().Substring(0, 16) + ".tmp");

            try
            {
                Directory.CreateDirectory(directory);

                using (File.Create(stagingPath)) { }

                if (!TryRestrictToOwner(stagingPath, out string restrictionError))
                {
                    TryDelete(stagingPath);
                    Debug.LogError(
                        $"[UsdLiveSyncServer] Refusing to write the auth token to '{tokenPath}': its permissions " +
                        $"could not be restricted to this user ({restrictionError}), which would leave the secret " +
                        "readable by anyone else on this machine. Set an explicit 'Auth Token' in the inspector, " +
                        $"or export ${TokenEnvironmentVariable}. Server not started.");
                    return false;
                }

                File.WriteAllText(stagingPath, token);

                // Replace any stale token from an earlier session. Renaming over the destination also
                // replaces a symlink an attacker may have planted there, rather than writing through it.
                TryDelete(tokenPath);
                File.Move(stagingPath, tokenPath);
                return true;
            }
            catch (Exception e)
            {
                TryDelete(stagingPath);
                Debug.LogError(
                    $"[UsdLiveSyncServer] Could not write the auth token to '{tokenPath}' — {e.Message}. " +
                    "Set an explicit 'Auth Token' in the inspector instead. Server not started.");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* best effort */ }
        }

// Key on the host OS, not the build target: a macOS/Linux Editor with Windows as the active target still
// defines UNITY_STANDALONE_WIN, and would otherwise compile the advapi32 path it cannot load.
#if UNITY_EDITOR_WIN || (!UNITY_EDITOR && (UNITY_STANDALONE_WIN || UNITY_WSA))
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string sddl, uint revision, out IntPtr securityDescriptor, IntPtr size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(IntPtr securityDescriptor, out bool present, out IntPtr dacl, out bool defaulted);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint SetNamedSecurityInfoW(
            string objectName, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        /// <summary>
        /// Windows: replace the file's ACL with a single entry for the current user. The managed ACL API
        /// (<c>FileSecurity</c>, <c>WindowsIdentity</c>) is not part of Unity's .NET Standard profile, so this
        /// goes straight to advapi32, mirroring the libc route on POSIX.
        /// </summary>
        private static bool TryRestrictToOwner(string path, out string error)
        {
            error = null;
            IntPtr securityDescriptor = IntPtr.Zero;
            try
            {
                string userSid = GetCurrentUserSid();

                // D:P = protected DACL, so inherited entries are dropped; otherwise a permissive ACL on the parent
                // folder (a shared drive, a world-readable project root) still grants other accounts.
                // (A;;FA;;;sid) = allow full file access to the current user only.
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        $"D:P(A;;FA;;;{userSid})", 1, out securityDescriptor, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception();

                if (!GetSecurityDescriptorDacl(securityDescriptor, out _, out IntPtr dacl, out _))
                    throw new System.ComponentModel.Win32Exception();

                const int seFileObject = 1;
                const uint daclSecurityInformation = 0x00000004;
                const uint protectedDaclSecurityInformation = 0x80000000;
                uint result = SetNamedSecurityInfoW(path, seFileObject,
                    daclSecurityInformation | protectedDaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
                if (result != 0)
                    throw new System.ComponentModel.Win32Exception((int)result);

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero)
                    LocalFree(securityDescriptor);
            }
        }

        private static string GetCurrentUserSid()
        {
            const uint tokenQuery = 0x0008;
            const int tokenUser = 1;

            if (!OpenProcessToken(GetCurrentProcess(), tokenQuery, out IntPtr token))
                throw new System.ComponentModel.Win32Exception();

            IntPtr buffer = IntPtr.Zero;
            try
            {
                GetTokenInformation(token, tokenUser, IntPtr.Zero, 0, out int length);
                buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, tokenUser, buffer, length, out _))
                    throw new System.ComponentModel.Win32Exception();

                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
                IntPtr sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
                if (!ConvertSidToStringSidW(sid, out IntPtr sidString))
                    throw new System.ComponentModel.Win32Exception();

                try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(sidString); }
                finally { LocalFree(sidString); }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                CloseHandle(token);
            }
        }
#else
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, int mode);

        /// <summary>
        /// POSIX: chmod 0600. Unity's runtime predates <c>File.SetUnixFileMode</c> (.NET 7), and Mono.Posix
        /// is not dependable under IL2CPP, so this goes straight to libc.
        /// </summary>
        private static bool TryRestrictToOwner(string path, out string error)
        {
            error = null;
            try
            {
                const int ownerReadWrite = 0x180; // 0600
                if (chmod(path, ownerReadWrite) == 0)
                    return true;

                error = "chmod failed with errno " +
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
#endif

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// Compare two tokens in time that does not depend on how many leading characters match, so a client
        /// cannot recover the token one character at a time by measuring how long a rejection takes.
        /// </summary>
        private static bool TokensMatch(string a, string b)
        {
            if (a == null || b == null)
                return false;

            int difference = a.Length ^ b.Length;
            for (int i = 0; i < a.Length; i++)
                difference |= a[i] ^ b[i < b.Length ? i : 0];
            return difference == 0;
        }

        // ---- Session bookkeeping ----------------------------------------------------------------

        private ClientSession FindSession(TcpClient client)
        {
            lock (_clientsLock)
            {
                foreach (var session in _sessions)
                    if (ReferenceEquals(session.Client, client))
                        return session;
                return null;
            }
        }

        private void DropClient(TcpClient client)
        {
            lock (_clientsLock)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(_sessions[i].Client, client))
                        _sessions.RemoveAt(i);
            }
            SafeClose(client);
        }

        private static string Describe(TcpClient client)
        {
            try { return client?.Client?.RemoteEndPoint?.ToString() ?? "<unknown>"; }
            catch (Exception) { return "<disconnected>"; }
        }

        // ==========================================================================================
        // Phase 0 — prim-path table (mirrors UsdExporter.ExportXforms exactly)
        // ==========================================================================================

        private void BuildPrimTable(Transform root)
        {
            _nodes.Clear();
            _byPath.Clear();

            // Root prim name: the exporter uses options.RootPrimName ?? root.name, sanitized. We pass root.name
            // as RootPrimName at export time, so both sides agree on "/<rootPrimName>".
            string rootPrimName = SanitizePrimName(root.name);
            var usedPaths = new HashSet<string>(StringComparer.Ordinal) { "/" + rootPrimName };
            var pathByTransform = new Dictionary<Transform, string> { { root, "/" + rootPrimName } };

            // Paths are computed for EVERY transform (so the unique-path suffixing matches the exporter, and a
            // tracked node's path is correct regardless of untracked siblings). A transform only becomes a
            // tracked Node if it qualifies for the current TrackMode.
            if (Qualifies(root))
                AddNode(root, "/" + rootPrimName);

            // GetComponentsInChildren returns hierarchical (parent-before-child) order, so a parent's path is
            // always resolved before its children — identical to the exporter's enumeration.
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in transforms)
            {
                if (t == root)
                    continue;

                string parentPath = t.parent != null && pathByTransform.TryGetValue(t.parent, out string p)
                    ? p
                    : "/" + rootPrimName;
                string primPath = MakeUniquePath(parentPath + "/" + SanitizePrimName(t.name), usedPaths);
                pathByTransform[t] = primPath;
                if (Qualifies(t))
                    AddNode(t, primPath);
            }
        }

        /// <summary>Whether a transform is live-tracked under the current <see cref="trackMode"/>.</summary>
        private bool Qualifies(Transform t)
        {
            if (trackMode == TrackMode.ExplicitNodesOnly)
                return t.GetComponent<UsdSyncNode>() != null;
            return true; // AllDescendants
        }

        private void AddNode(Transform t, string primPath)
        {
            var node = new Node
            {
                Tr = t,
                Path = primPath,
                Sync = t.GetComponent<UsdSyncNode>(),
                Rb = t.GetComponent<Rigidbody>(),
                BaseT = t.localPosition,
                BaseR = t.localRotation,
                BaseS = t.localScale,
                LastT = t.localPosition,
                LastR = t.localRotation,
                LastS = t.localScale,
            };
            _nodes.Add(node);
            _byPath[primPath] = node;
        }

        // ==========================================================================================
        // Phase 1 — baseline export
        // ==========================================================================================

        private void ExportBaseline(Transform root)
        {
            string dir = ResolveOutputDir();
            string basePath = Path.Combine(dir, baseStageFileName);

            // Skinned meshes (characters) are invisible to the exporter (it only walks MeshFilter+MeshRenderer).
            // Bake each SkinnedMeshRenderer into a temporary static MeshFilter+MeshRenderer so it lands in
            // base_stage.usda, then tear the temporaries down in the finally block. These temps are created
            // AFTER BuildPrimTable, so they are never live-tracked — they only exist for this one export.
            var tempObjects = new List<GameObject>();
            var tempMeshes = new List<Mesh>();
            try
            {
                Directory.CreateDirectory(dir);

                if (bakeSkinnedMeshes)
                    BakeSkinnedMeshes(root, tempObjects, tempMeshes);

                UsdExportResult result = UsdExporter.ExportGameObjectWithResult(root.gameObject, basePath,
                    new UsdExportOptions
                    {
                        RootPrimName = root.name, // keep the root prim name aligned with the Phase 0 table
                        TransformPolicy = UsdTransformPolicy.PreserveHierarchy,
                        IncludeInactive = false,
                        // Do NOT require Read/Write-enabled meshes: many imported FBX assets ship
                        // non-readable. With this false the exporter GPU-reads them back instead of throwing
                        // on the first one and aborting the whole baseline (which left an empty stub).
                        RequireReadableMeshes = false,
                        ExportTextures = true,
                    });

                Debug.Log($"[UsdLiveSyncServer] Baseline export: {result} (root prim '/{SanitizePrimName(root.name)}')" +
                          (tempObjects.Count > 0 ? $"; baked {tempObjects.Count} skinned mesh(es)." : "."));
                CrossCheckPrimPaths(result);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UsdLiveSyncServer] Baseline export skipped ({e.GetType().Name}: {e.Message}). " +
                                 "Live sync continues without an on-disk base_stage.usda.");
            }
            finally
            {
                foreach (var go in tempObjects)
                    if (go != null) DestroyImmediate(go);
                foreach (var m in tempMeshes)
                    if (m != null) DestroyImmediate(m);
            }
        }

        /// <summary>
        /// For every active <see cref="SkinnedMeshRenderer"/> under <paramref name="root"/>, bake the current
        /// (bind) pose into a fresh static <see cref="Mesh"/> and hang it on a temporary child GameObject with
        /// an identity local transform + a normal MeshFilter/MeshRenderer, so the package exporter picks it up.
        /// Vertices from <see cref="SkinnedMeshRenderer.BakeMesh(Mesh)"/> are in the renderer's local space, so
        /// parenting the temp under the renderer (world placement via the parent chain, scale included by the
        /// renderer's own xform) reproduces the character at the correct location. Temps are collected for
        /// teardown by the caller. Best-effort per renderer: a failure is logged and skipped, never fatal.
        /// </summary>
        private void BakeSkinnedMeshes(Transform root, List<GameObject> tempObjects, List<Mesh> tempMeshes)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                try
                {
                    if (smr.sharedMesh == null) continue;

                    var baked = new Mesh { name = smr.name + "_baked" };
                    smr.BakeMesh(baked);
                    tempMeshes.Add(baked);

                    var go = new GameObject(smr.name + "_BakedStatic");
                    go.transform.SetParent(smr.transform, worldPositionStays: false); // identity local
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = baked;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = smr.sharedMaterials;
                    tempObjects.Add(go);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UsdLiveSyncServer] Skinned-mesh bake skipped for '{smr.name}': {e.Message}");
                }
            }
        }

        /// <summary>
        /// Phase 0 self-check: for each exported mesh, the exporter writes it at
        /// &lt;xform prim path&gt;/&lt;Sanitize(leaf)&gt;_Mesh. Confirm the mesh's owning transform resolves to
        /// the same prim path in our table. A mismatch means our walker diverged from the exporter and the
        /// Python 'over' statements would silently fail to bind — so it is logged loudly.
        /// </summary>
        private void CrossCheckPrimPaths(UsdExportResult result)
        {
            if (result?.Meshes == null || result.Meshes.Count == 0)
                return;

            int checkedCount = 0, mismatches = 0;
            foreach (var mesh in result.Meshes)
            {
                string meshPrim = mesh.PrimPath;
                int slash = meshPrim.LastIndexOf('/');
                if (slash <= 0)
                    continue;
                string xformPrim = meshPrim.Substring(0, slash); // strip the "/<leaf>_Mesh" leaf

                checkedCount++;
                if (!_byPath.ContainsKey(xformPrim))
                {
                    mismatches++;
                    if (mismatches <= 5)
                        Debug.LogWarning($"[UsdLiveSyncServer] Prim-path cross-check: exporter mesh '{meshPrim}' " +
                                         $"maps to xform '{xformPrim}' which is NOT in the sync table.");
                }
            }

            if (mismatches == 0)
                Debug.Log($"[UsdLiveSyncServer] Prim-path cross-check passed ({checkedCount} mesh prim(s) agree).");
            else
                Debug.LogWarning($"[UsdLiveSyncServer] Prim-path cross-check: {mismatches}/{checkedCount} mesh prim(s) " +
                                 "did not match the sync table — override binding may fail. Investigate SanitizePrimName parity.");
        }

        private string ResolveOutputDir()
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                return Path.GetFullPath(outputDirectory);

            // In the Editor, '<project>/UsdSync' sits next to Assets/ where the Python client looks for it by
            // default. A built player's dataPath is inside the app bundle (read-only on macOS), so fall back to
            // the platform's writable persistent location and let the client point at it with --output-dir.
            if (Application.isEditor)
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, "UsdSync");
            }

            return Path.Combine(Application.persistentDataPath, "UsdSync");
        }

        // ==========================================================================================
        // Phase 2 — accept + per-client read (background threads)
        // ==========================================================================================

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_running) break;
                    continue;
                }

                client.NoDelay = true;

                int clientCount;
                lock (_clientsLock)
                {
                    if (_sessions.Count >= Math.Max(1, maxClients))
                    {
                        Debug.LogWarning($"[UsdLiveSyncServer] Refused {Describe(client)}: already at maxClients ({maxClients}).");
                        SafeClose(client);
                        continue;
                    }

                    _sessions.Add(new ClientSession { Client = client, ConnectedUtc = DateTime.UtcNow });
                    clientCount = _sessions.Count;
                }

                // No snapshot is queued here: a connection carries no entitlement to scene data. The join
                // snapshot is sent from HandleAuth, once the client has presented a valid token.
                var t = new Thread(() => ReadClient(client)) { IsBackground = true, Name = "UsdLiveSync-Client" };
                t.Start();
                Debug.Log($"[UsdLiveSyncServer] Client connected ({Describe(client)}), awaiting auth. Clients: {clientCount}");
            }
        }

        private void ReadClient(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var acc = new StringBuilder();

                while (_running)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break; // client closed

                    acc.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    // A client that never sends a newline would otherwise grow this buffer until the process
                    // runs out of memory, so an over-long command costs the connection instead.
                    if (acc.Length > Math.Max(1024, maxCommandBytes))
                    {
                        Debug.LogWarning(
                            $"[UsdLiveSyncServer] Client {Describe(client)} exceeded the {maxCommandBytes}-byte " +
                            "command limit without a newline — disconnecting.");
                        break;
                    }

                    int nl;
                    while ((nl = IndexOf(acc, '\n')) >= 0)
                    {
                        string line = acc.ToString(0, nl).Trim();
                        acc.Remove(0, nl + 1);
                        if (line.Length == 0)
                            continue;

                        // Update() drains one frame's worth at a time; a client that outruns it does not get to
                        // queue an unbounded backlog.
                        if (Interlocked.Increment(ref _queuedCommands) > MaxQueuedCommands)
                        {
                            Interlocked.Decrement(ref _queuedCommands);
                            Debug.LogWarning($"[UsdLiveSyncServer] Client {Describe(client)} flooded the command queue — disconnecting.");
                            return;
                        }

                        _inbox.Enqueue(new Inbound { Client = client, Json = line });
                    }
                }
            }
            catch (Exception)
            {
                // fall through to cleanup
            }
            finally
            {
                DropClient(client);
            }
        }

        private static int IndexOf(StringBuilder sb, char c)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == c) return i;
            return -1;
        }

        // ==========================================================================================
        // Phase 2 — sender thread (broadcast)
        // ==========================================================================================

        private void SenderLoop()
        {
            while (_running)
            {
                _outboxSignal.WaitOne(250);
                while (_outbox.TryDequeue(out var line))
                    Broadcast(line);
            }
        }

        private void Broadcast(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            List<ClientSession> dead = null;

            lock (_clientsLock)
            {
                foreach (var session in _sessions)
                {
                    // Scene state goes only to clients that proved they hold the token.
                    if (!session.Authenticated)
                        continue;

                    try
                    {
                        session.Client.GetStream().Write(bytes, 0, bytes.Length);
                    }
                    catch (Exception)
                    {
                        (dead ??= new List<ClientSession>()).Add(session);
                    }
                }

                if (dead != null)
                    foreach (var session in dead) { _sessions.Remove(session); SafeClose(session.Client); }
            }
        }

        private void Enqueue(string line)
        {
            _outbox.Enqueue(line);
            _outboxSignal.Set();
        }

        // ==========================================================================================
        // Main-thread processing (Update)
        // ==========================================================================================

        private void Update()
        {
            if (!_running)
                return;

            // 1) Drain inbound commands (applies transforms on the main thread).
            while (_inbox.TryDequeue(out var msg))
            {
                Interlocked.Decrement(ref _queuedCommands);
                ProcessCommand(msg);
            }

            // 1b) Hang up on anyone who connected but never authenticated, so idle sockets cannot be parked
            //     against the connection limit.
            DropTimedOutSessions();

            // 2) Throttled dirty-diff broadcast.
            if (broadcastEveryNFrames < 1) broadcastEveryNFrames = 1;
            if (++_frameCounter >= broadcastEveryNFrames)
            {
                _frameCounter = 0;
                BroadcastDeltas();
            }
        }

        // ---- Phase 3: dirty-diff --------------------------------------------------------------------

        private void BroadcastDeltas()
        {
            if (AuthenticatedClientCount == 0)
                return; // no authenticated listeners — still cheap to skip building the message

            StringBuilder sb = null;
            int changed = 0;

            foreach (var n in _nodes)
            {
                if (n.Tr == null)
                    continue;

                Vector3 t = n.Tr.localPosition;
                Quaternion r = n.Tr.localRotation;
                Vector3 s = n.Tr.localScale;

                if (!Changed(n, t, r, s))
                    continue;

                n.LastT = t; n.LastR = r; n.LastS = s;

                if (sb == null)
                {
                    sb = new StringBuilder(256);
                    sb.Append("{\"type\":\"delta\",\"seq\":").Append(NextSeq())
                      .Append(",\"t\":").Append(NowMillis())
                      .Append(",\"prims\":{");
                }
                if (changed > 0) sb.Append(',');
                AppendPrim(sb, n.Path, t, r, s);
                changed++;
            }

            if (sb != null)
            {
                sb.Append("}}\n");
                Enqueue(sb.ToString());
            }
        }

        private bool Changed(Node n, Vector3 t, Quaternion r, Vector3 s)
        {
            if (n.SyncPos && (t - n.LastT).sqrMagnitude > translateEpsilon * translateEpsilon) return true;
            if (n.SyncScl && (s - n.LastS).sqrMagnitude > translateEpsilon * translateEpsilon) return true;
            if (n.SyncRot && Quaternion.Angle(n.LastR, r) > rotationEpsilonDegrees) return true;
            return false;
        }

        // ---- Phase 4: inbound commands --------------------------------------------------------------

        private void DropTimedOutSessions()
        {
            List<TcpClient> expired = null;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(1f, authTimeoutSeconds));

            lock (_clientsLock)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    ClientSession session = _sessions[i];
                    if (session.Authenticated || session.ConnectedUtc > cutoff)
                        continue;

                    _sessions.RemoveAt(i);
                    (expired ??= new List<TcpClient>()).Add(session.Client);
                }
            }

            if (expired == null)
                return;

            foreach (var client in expired)
            {
                Debug.LogWarning($"[UsdLiveSyncServer] Client {Describe(client)} did not authenticate within {authTimeoutSeconds:0.#}s — disconnecting.");
                SafeClose(client);
            }
        }

        private void ProcessCommand(Inbound msg)
        {
            ClientSession session = FindSession(msg.Client);
            if (session == null)
                return; // client went away while this command sat in the queue

            object parsed;
            try
            {
                parsed = Json.Parse(msg.Json);
            }
            catch (Exception e)
            {
                SendAck(msg.Client, "?", false, "malformed JSON: " + e.Message);
                return;
            }

            if (parsed is not Dictionary<string, object> obj || !obj.TryGetValue("cmd", out object cmdObj))
            {
                SendAck(msg.Client, "?", false, "missing 'cmd'");
                return;
            }

            string cmd = cmdObj as string ?? "";

            if (cmd == "auth")
            {
                HandleAuth(session, obj);
                return;
            }

            // Every other command — including the read-only get_snapshot, which discloses the whole scene —
            // is refused until the client has presented the token.
            if (!session.Authenticated)
            {
                SendAck(msg.Client, cmd, false, "authentication required: send {\"cmd\":\"auth\",\"token\":\"...\"} first");
                return;
            }

            switch (cmd)
            {
                case "set_transform":
                    HandleSetTransform(msg.Client, obj);
                    break;
                case "reset":
                    HandleReset(msg.Client);
                    break;
                case "get_snapshot":
                    SendSnapshotTo(msg.Client, "request");
                    SendAck(msg.Client, cmd, true, null);
                    break;
                default:
                    SendAck(msg.Client, cmd, false, $"unsupported command '{cmd}'");
                    break;
            }
        }

        private void HandleAuth(ClientSession session, Dictionary<string, object> obj)
        {
            if (session.Authenticated)
            {
                SendAck(session.Client, "auth", true, null);
                return;
            }

            string presented = obj.TryGetValue("token", out object tokenObj) ? tokenObj as string : null;
            if (string.IsNullOrEmpty(presented) || !TokensMatch(_resolvedToken, presented))
            {
                Debug.LogWarning($"[UsdLiveSyncServer] Client {Describe(session.Client)} presented an invalid auth token — disconnecting.");
                SendAck(session.Client, "auth", false, "invalid token");
                DropClient(session.Client);
                return;
            }

            session.Authenticated = true;
            SendAck(session.Client, "auth", true, null);

            // The join snapshot the accept loop used to send unconditionally: it belongs here, after the token.
            SendSnapshotTo(session.Client, "join");
            Debug.Log($"[UsdLiveSyncServer] Client {Describe(session.Client)} authenticated.");
        }

        private void HandleSetTransform(TcpClient client, Dictionary<string, object> obj)
        {
            if (!obj.TryGetValue("prims", out object primsObj) || primsObj is not Dictionary<string, object> prims)
            {
                SendAck(client, "set_transform", false, "missing 'prims' map");
                return;
            }

            int applied = 0, ignored = 0, unknown = 0;
            foreach (var kv in prims)
            {
                if (!_byPath.TryGetValue(kv.Key, out Node n) || n.Tr == null)
                {
                    unknown++;
                    continue;
                }
                if (!n.AcceptsRemoteWrites)
                {
                    ignored++; // Unity-authoritative: observe-only, never fight the client
                    continue;
                }
                if (kv.Value is not Dictionary<string, object> trs)
                    continue;

                if (n.SyncPos && TryVec3(trs, "t", out Vector3 t)) n.Tr.localPosition = t;
                if (n.SyncRot && TryQuat(trs, "r", out Quaternion r)) n.Tr.localRotation = r;
                if (n.SyncScl && TryVec3(trs, "s", out Vector3 s)) n.Tr.localScale = s;

                // Physics bodies own their pose: poking the Transform alone is ignored (and, for a dynamic
                // body, gravity/velocity pull it back next step). Teleport the Rigidbody to the new world pose
                // and clear velocities so the remote write actually takes effect. A dynamic body will still
                // fall afterwards unless it is kinematic — a continuous external stream re-teleports it each
                // tick, which visually holds it; a one-shot edit places it and lets physics resume.
                if (n.Rb != null)
                {
                    n.Rb.position = n.Tr.position;
                    n.Rb.rotation = n.Tr.rotation;
                    if (!n.Rb.isKinematic)
                    {
#if UNITY_6000_0_OR_NEWER
                        n.Rb.linearVelocity = Vector3.zero;
#else
                        n.Rb.velocity = Vector3.zero;
#endif
                        n.Rb.angularVelocity = Vector3.zero;
                    }
                }

                // Echo suppression: record the just-applied value as "last sent" so the next dirty-diff pass
                // does not immediately re-broadcast this remote edit back to the clients.
                n.LastT = n.Tr.localPosition;
                n.LastR = n.Tr.localRotation;
                n.LastS = n.Tr.localScale;
                applied++;
            }

            SendAck(client, "set_transform", true,
                applied == 0 && (ignored > 0 || unknown > 0)
                    ? $"applied 0 (ignored {ignored} observe-only, {unknown} unknown)"
                    : null,
                applied, ignored, unknown);
        }

        private void HandleReset(TcpClient client)
        {
            ResetToBaseline();

            // Broadcast the restored baseline to everyone so all clients converge without inferring from deltas.
            Enqueue(BuildSnapshot("reset"));
            SendAck(client, "reset", true, null);
        }

        /// <summary>
        /// Restore every tracked transform to the baseline captured at <see cref="StartServer"/> and fire
        /// <see cref="SceneReset"/>. Call this from your own UI to reset without a client round-trip; the
        /// inbound <c>reset</c> command runs this and additionally broadcasts a snapshot so connected clients
        /// converge immediately. Main thread only.
        /// </summary>
        public void ResetToBaseline()
        {
            foreach (var n in _nodes)
            {
                if (n.Tr == null) continue;
                n.Tr.localPosition = n.BaseT;
                n.Tr.localRotation = n.BaseR;
                n.Tr.localScale = n.BaseS;
                n.LastT = n.BaseT; n.LastR = n.BaseR; n.LastS = n.BaseS;

                // A physics body owns its pose: move the Rigidbody too, or it snaps the Transform back.
                if (n.Rb != null)
                {
                    n.Rb.position = n.Tr.position;
                    n.Rb.rotation = n.Tr.rotation;
                    if (!n.Rb.isKinematic)
                    {
#if UNITY_6000_0_OR_NEWER
                        n.Rb.linearVelocity = Vector3.zero;
#else
                        n.Rb.velocity = Vector3.zero;
#endif
                        n.Rb.angularVelocity = Vector3.zero;
                    }
                }
            }

            try { SceneReset?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[UsdLiveSyncServer] SceneReset subscriber threw: {e}"); }

            Debug.Log("[UsdLiveSyncServer] Reset: restored baseline for all tracked prims.");
        }

        // ==========================================================================================
        // Outbound message builders
        // ==========================================================================================

        private string BuildSnapshot(string reason)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"snapshot\",\"reason\":\"").Append(reason)
              .Append("\",\"seq\":").Append(NextSeq())
              .Append(",\"t\":").Append(NowMillis())
              .Append(",\"prims\":{");
            bool first = true;
            foreach (var n in _nodes)
            {
                if (n.Tr == null) continue;
                if (!first) sb.Append(',');
                AppendPrim(sb, n.Path, n.Tr.localPosition, n.Tr.localRotation, n.Tr.localScale);
                first = false;
            }
            sb.Append("}}\n");
            return sb.ToString();
        }

        private void SendSnapshotTo(TcpClient client, string reason)
        {
            SendDirect(client, BuildSnapshot(reason));
        }

        private void SendAck(TcpClient client, string cmd, bool ok, string error,
            int applied = -1, int ignored = -1, int unknown = -1)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"type\":\"ack\",\"cmd\":");
            AppendEscaped(sb, cmd);
            sb.Append(",\"ok\":").Append(ok ? "true" : "false");
            if (applied >= 0) sb.Append(",\"applied\":").Append(applied);
            if (ignored >= 0) sb.Append(",\"ignored\":").Append(ignored);
            if (unknown >= 0) sb.Append(",\"unknown\":").Append(unknown);
            if (!string.IsNullOrEmpty(error)) { sb.Append(",\"error\":"); AppendEscaped(sb, error); }
            sb.Append("}\n");
            SendDirect(client, sb.ToString());
        }

        /// <summary>Write one message to a single client from the main thread (small, targeted replies).</summary>
        private void SendDirect(TcpClient client, string line)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                client.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                lock (_clientsLock)
                {
                    for (int i = _sessions.Count - 1; i >= 0; i--)
                        if (ReferenceEquals(_sessions[i].Client, client))
                            _sessions.RemoveAt(i);
                }
                SafeClose(client);
            }
        }

        private long NextSeq() => Interlocked.Increment(ref _seq);
        private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ==========================================================================================
        // JSON helpers (output) — hand-rolled StringBuilder, because JsonUtility cannot serialize the
        // Dictionary-shaped "prims" map.
        // ==========================================================================================

        private static void AppendPrim(StringBuilder sb, string path, Vector3 t, Quaternion r, Vector3 s)
        {
            AppendEscaped(sb, path);
            sb.Append(":{\"t\":[").Append(F(t.x)).Append(',').Append(F(t.y)).Append(',').Append(F(t.z))
              .Append("],\"r\":[").Append(F(r.x)).Append(',').Append(F(r.y)).Append(',').Append(F(r.z)).Append(',').Append(F(r.w))
              .Append("],\"s\":[").Append(F(s.x)).Append(',').Append(F(s.y)).Append(',').Append(F(s.z)).Append("]}");
        }

        private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        // ---- Inbound value extraction ---------------------------------------------------------------

        private static bool TryVec3(Dictionary<string, object> d, string key, out Vector3 v)
        {
            v = default;
            if (!d.TryGetValue(key, out object o) || o is not List<object> a || a.Count < 3) return false;
            v = new Vector3(ToF(a[0]), ToF(a[1]), ToF(a[2]));
            return true;
        }

        private static bool TryQuat(Dictionary<string, object> d, string key, out Quaternion q)
        {
            q = Quaternion.identity;
            if (!d.TryGetValue(key, out object o) || o is not List<object> a || a.Count < 4) return false;
            q = new Quaternion(ToF(a[0]), ToF(a[1]), ToF(a[2]), ToF(a[3]));
            return true;
        }

        private static float ToF(object o) => o is double d ? (float)d : 0f;

        // ==========================================================================================
        // Prim-name sanitizer + unique-path suffixing (verbatim copies of the package exporter's private
        // helpers, so our prim paths match base_stage.usda byte-for-byte).
        // ==========================================================================================

        private static string SanitizePrimName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Prim";

            string sanitized = name.Trim();
            foreach (char invalid in InvalidPrimNameChars)
                sanitized = sanitized.Replace(invalid, '_');

            if (char.IsDigit(sanitized[0]))
                sanitized = "_" + sanitized;

            return sanitized;
        }

        private static string MakeUniquePath(string path, HashSet<string> usedPaths)
        {
            string uniquePath = path;
            int suffix = 1;
            while (!usedPaths.Add(uniquePath))
                uniquePath = path + "_" + suffix++;
            return uniquePath;
        }

        private static void SafeClose(TcpClient c)
        {
            try { c?.Close(); } catch { /* ignore */ }
        }

        // ==========================================================================================
        // Minimal JSON parser (inbound). Recursive-descent; supports object/array/string/number/bool/null,
        // sufficient for the fixed command schema. Numbers surface as double, objects as
        // Dictionary<string,object>, arrays as List<object>.
        //
        // Nesting is capped at MaxJsonDepth. Without that cap a deeply nested payload recurses until the
        // thread's stack is exhausted, and a .NET StackOverflowException cannot be caught — it would take the
        // whole process down from ProcessCommand's try/catch. Exceeding the cap raises a FormatException
        // instead, which that catch turns into an ordinary "malformed JSON" rejection.
        // ==========================================================================================

        private static class Json
        {
            public static object Parse(string s)
            {
                int i = 0;
                object v = ParseValue(s, ref i, 0);
                SkipWs(s, ref i);
                if (i != s.Length)
                    throw new FormatException($"trailing characters at {i}");
                return v;
            }

            private static object ParseValue(string s, ref int i, int depth)
            {
                if (depth > MaxJsonDepth)
                    throw new FormatException($"nesting deeper than {MaxJsonDepth} levels at {i}");

                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end");
                char c = s[i];
                switch (c)
                {
                    case '{': return ParseObject(s, ref i, depth + 1);
                    case '[': return ParseArray(s, ref i, depth + 1);
                    case '"': return ParseString(s, ref i);
                    case 't': Expect(s, ref i, "true"); return true;
                    case 'f': Expect(s, ref i, "false"); return false;
                    case 'n': Expect(s, ref i, "null"); return null;
                    default: return ParseNumber(s, ref i);
                }
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; // {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs(s, ref i);
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at {i}");
                    i++;
                    d[key] = ParseValue(s, ref i, depth);
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("unterminated object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                    throw new FormatException($"expected ',' or '}}' at {i}");
                }
                return d;
            }

            private static List<object> ParseArray(string s, ref int i, int depth)
            {
                var list = new List<object>();
                i++; // [
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i, depth));
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("unterminated array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                    throw new FormatException($"expected ',' or ']' at {i}");
                }
                return list;
            }

            private static string ParseString(string s, ref int i)
            {
                if (i >= s.Length || s[i] != '"') throw new FormatException($"expected string at {i}");
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) break;
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default: throw new FormatException($"bad escape '\\{e}'");
                        }
                    }
                    else sb.Append(c);
                }
                throw new FormatException("unterminated string");
            }

            private static double ParseNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == start) throw new FormatException($"invalid value at {start}");
                return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
            }

            private static void Expect(string s, ref int i, string literal)
            {
                if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                    throw new FormatException($"expected '{literal}' at {i}");
                i += literal.Length;
            }

            private static void SkipWs(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            }
        }
    }
}
