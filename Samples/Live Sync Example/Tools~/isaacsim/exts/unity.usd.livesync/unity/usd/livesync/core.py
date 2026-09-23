"""
Transport + coordinate-conversion core for the Unity -> Isaac Sim USD live transform bridge.

Deliberately depends on the Python standard library ONLY — no `pxr`, no `omni`, no `carb`. Two
reasons:

  * Isaac Sim's bundled interpreter cannot `import pxr` until `SimulationApp(...)` has booted Kit
    (verified: `python.bat -c "from pxr import Usd"` fails with ModuleNotFoundError, because
    setup_python_env.bat only puts `<isaacsim>/site` on PYTHONPATH and the USD libs arrive with the
    Kit extension paths). Keeping the socket layer pxr-free means it can be imported at any point,
    while the pxr-dependent half lives in `stage_bridge.py`, which is imported late.
  * The conversion functions are pure and can be unit-tested with a plain `python3`.

This is the Isaac-side counterpart of `Tools/usd_live_sync.py`: Unity's `UsdLiveSyncServer` is the
host (TCP :10000, newline-delimited JSON), Isaac Sim is just one more client of the same broadcast
hub — the "DCC-side client" that Phase 7 of the implementation plan left open. The wire schema is
unchanged, so Isaac and `usd_live_sync.py` can be connected at the same time.
"""

from __future__ import annotations

import json
import os
import socket
import threading
import time

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 10000

# UsdLiveSyncServer requires a shared secret before it answers any command. When its Auth Token field is
# left empty it generates one per session and writes it to this file next to base_stage.usda.
TOKEN_FILE_NAME = "live_sync_token.txt"
TOKEN_ENV_VAR = "USD_LIVE_SYNC_TOKEN"


def resolve_token(token=None, token_dir=None):
    """Explicit token, else $USD_LIVE_SYNC_TOKEN, else live_sync_token.txt in the Unity output folder."""
    if token:
        return token.strip()

    from_environment = os.environ.get(TOKEN_ENV_VAR)
    if from_environment:
        return from_environment.strip()

    if token_dir:
        try:
            with open(os.path.join(token_dir, TOKEN_FILE_NAME), "r", encoding="utf-8") as f:
                return f.read().strip()
        except OSError:
            pass

    return None


# ==================================================================================================
# Coordinate conversion
#
# The live channel carries RAW Unity local-space values. `base_stage.usda` was written by
# UsdExporter with a Unity(left-handed) -> USD(right-handed) X-axis flip (FlipX = S*M*S,
# S = diag(-1,1,1)). Isaac Sim consumes that same baseline geometry, so incoming transforms must get
# the SAME flip before they are authored onto a prim — otherwise the streamed objects sit mirrored
# relative to the geometry they are supposed to be driving.
#
# These are byte-for-byte the rules `Tools/usd_live_sync.py` already uses (and validates in
# usdview), so both clients place an object identically. Both maps are their own inverse, which is
# why the USD -> Unity direction (push-back) is the same arithmetic.
# ==================================================================================================

def unity_to_usd_translate(t):
    """Unity (x,y,z) -> USD (x,y,z): negate X."""
    return (-float(t[0]), float(t[1]), float(t[2]))


def unity_to_usd_quat(r):
    """
    Unity quaternion (x,y,z,w) -> USD orient as REAL-FIRST (w,x,y,z), ready for Gf.Quatd/Gf.Quatf.

    Conjugating a rotation by S = diag(-1,1,1) sends axis-angle (a, theta) to (S a, -theta), i.e.
    (x,y,z,w) -> (x,-y,-z,w).
    """
    x, y, z, w = float(r[0]), float(r[1]), float(r[2]), float(r[3])
    return (w, x, -y, -z)


def usd_to_unity_translate(t):
    """USD (x,y,z) -> Unity (x,y,z). Same negate-X (the map is an involution)."""
    return (-float(t[0]), float(t[1]), float(t[2]))


def usd_to_unity_quat(q):
    """USD real-first (w,x,y,z) -> Unity (x,y,z,w). Inverse of `unity_to_usd_quat`."""
    w, x, y, z = float(q[0]), float(q[1]), float(q[2]), float(q[3])
    return (x, -y, -z, w)


# ==================================================================================================
# Socket client
# ==================================================================================================

class SyncClient:
    """
    Background-thread client of Unity's `UsdLiveSyncServer`.

    Reads the newline-delimited JSON broadcast (`snapshot` / `delta` / `ack`) on its own thread and
    parks the result in a *coalescing* pending map: `prim_path -> {'t','r','s'}`, latest value wins.
    Coalescing rather than queueing is the right call for a transform stream — if the consumer
    (Isaac's update loop) falls behind the producer (Unity at 60fps / broadcastEveryNFrames), we want
    the newest pose per prim, not a backlog of stale ones to replay.

    The consumer calls `drain()` from whichever thread is allowed to touch USD (Kit's update stream,
    or the standalone script's main loop) — USD authoring never happens on the socket thread.
    """

    def __init__(self, host=DEFAULT_HOST, port=DEFAULT_PORT, reconnect=True,
                 reconnect_interval=2.0, connect_timeout=5.0, on_log=None,
                 token=None, token_dir=None):
        self.host = host
        self.port = int(port)
        self.token = token
        self.token_dir = token_dir
        self.reconnect = reconnect
        self.reconnect_interval = float(reconnect_interval)
        self.connect_timeout = float(connect_timeout)
        self._on_log = on_log

        # ---- state shared with the socket thread (guard with _lock) ----
        self._lock = threading.Lock()
        self._pending = {}          # coalesced prim_path -> trs, consumed by drain()
        self.prims = {}             # full authoritative mirror (for UI listing / push-back)

        # ---- status (single writer = socket thread; plain attribute reads are fine) ----
        self.connected = False
        self.last_error = None
        self.seq = 0
        self.messages = 0
        self.prim_updates = 0
        self.last_message_time = 0.0
        self.reset_flag = False     # set when a snapshot with reason 'reset' arrives

        self._sock = None
        self._send_lock = threading.Lock()
        self._thread = None
        self._running = False

    # -- lifecycle ---------------------------------------------------------------------------------

    def start(self):
        if self._running:
            return
        self._running = True
        self._thread = threading.Thread(target=self._run, name="UnityLiveSync-Reader", daemon=True)
        self._thread.start()

    def stop(self):
        self._running = False
        self.last_error = None      # a requested stop is not a fault; keep the final status clean
        self._shutdown_socket()
        t = self._thread
        if t is not None and t.is_alive():
            t.join(timeout=2.0)
        self._thread = None
        self.connected = False

    @property
    def running(self):
        return self._running

    def _log(self, msg):
        if self._on_log is not None:
            try:
                self._on_log(msg)
            except Exception:
                pass
        else:
            print(msg, flush=True)

    def _shutdown_socket(self):
        s = self._sock
        self._sock = None
        if s is not None:
            # shutdown() first so a blocking recv() on the reader thread returns immediately.
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                s.close()
            except OSError:
                pass

    # -- reader thread -----------------------------------------------------------------------------

    def _run(self):
        while self._running:
            sock = self._connect()
            if sock is None:
                if not self.reconnect or not self._running:
                    break
                time.sleep(self.reconnect_interval)
                continue

            self._sock = sock

            # The server answers nothing until the token is accepted, so the handshake runs on the same
            # record stream the reader loop then continues from.
            records = self._records(sock)
            if not self._authenticate(sock, records):
                self._shutdown_socket()
                if not self.reconnect or not self._running:
                    break
                time.sleep(self.reconnect_interval)
                continue

            self.connected = True
            self.last_error = None
            self._log("[unity-live-sync] connected to {}:{}".format(self.host, self.port))

            try:
                for rec in records:
                    self._ingest(rec)
            except Exception as e:      # noqa: BLE001 - a dropped socket must not kill the thread
                # Only a genuine failure is an error. `stop()` closes the socket out from under this
                # recv() on purpose, which surfaces as e.g. WinError 10038 ("not a socket") — that is
                # the shutdown working, so it must not be reported as the last error.
                if self._running:
                    self.last_error = str(e)
            finally:
                self.connected = False
                self._shutdown_socket()

            if self._running:
                self._log("[unity-live-sync] disconnected (Unity left Play mode?)")
            if not self.reconnect or not self._running:
                break
            time.sleep(self.reconnect_interval)

        self.connected = False

    def _connect(self):
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(self.connect_timeout)
            s.connect((self.host, self.port))
            s.settimeout(None)
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            return s
        except OSError as e:
            self.last_error = str(e)
            return None

    def _authenticate(self, sock, records):
        """Send {"cmd":"auth"} and wait for its ack. Returns True only on an ok ack."""
        token = resolve_token(self.token, self.token_dir)
        if not token:
            self.last_error = ("no auth token: set ${} or point token_dir at the folder holding {}"
                               .format(TOKEN_ENV_VAR, TOKEN_FILE_NAME))
            self._log("[unity-live-sync] " + self.last_error)
            return False

        try:
            sock.settimeout(self.connect_timeout)
            sock.sendall((json.dumps({"cmd": "auth", "token": token}) + "\n").encode("utf-8"))
            for rec in records:
                if rec.get("type") != "ack" or rec.get("cmd") != "auth":
                    continue
                if rec.get("ok"):
                    return True
                self.last_error = "authentication rejected: {}".format(rec.get("error", "invalid token"))
                self._log("[unity-live-sync] " + self.last_error)
                return False
            self.last_error = "server closed the connection during authentication"
        except OSError as e:
            self.last_error = "authentication failed: {}".format(e)
        finally:
            try:
                sock.settimeout(None)
            except OSError:
                pass

        self._log("[unity-live-sync] " + str(self.last_error))
        return False

    @staticmethod
    def _records(sock):
        """Yield JSON records from the newline-delimited stream, tolerating fragmented reads."""
        buf = b""
        while True:
            chunk = sock.recv(65536)
            if not chunk:
                return
            buf += chunk
            while b"\n" in buf:
                raw, buf = buf.split(b"\n", 1)
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    yield json.loads(raw.decode("utf-8", errors="replace"))
                except json.JSONDecodeError:
                    continue

    def _ingest(self, rec):
        rtype = rec.get("type")
        if rtype == "snapshot":
            prims = rec.get("prims", {}) or {}
            with self._lock:
                self.prims = dict(prims)
                self._pending.update(prims)     # a snapshot re-asserts every prim
            if rec.get("reason") == "reset":
                self.reset_flag = True
        elif rtype == "delta":
            prims = rec.get("prims", {}) or {}
            with self._lock:
                self.prims.update(prims)
                self._pending.update(prims)
        elif rtype == "ack":
            if not rec.get("ok", True):
                self._log("[unity-live-sync] nack: {}".format(json.dumps(rec)))
            return
        else:
            return

        self.seq = rec.get("seq", self.seq)
        self.messages += 1
        self.prim_updates += len(rec.get("prims", {}) or {})
        self.last_message_time = time.time()

    # -- consumer side -----------------------------------------------------------------------------

    def drain(self):
        """Take and clear the coalesced pending updates. Returns a dict (possibly empty)."""
        with self._lock:
            if not self._pending:
                return {}
            out = self._pending
            self._pending = {}
            return out

    def snapshot_prims(self):
        """A copy of the full mirrored state (safe to iterate off-thread)."""
        with self._lock:
            return dict(self.prims)

    def take_reset_flag(self):
        """True exactly once per Unity-side reset, so a consumer can react (re-log, re-mount, ...)."""
        if self.reset_flag:
            self.reset_flag = False
            return True
        return False

    # -- Isaac -> Unity direction ------------------------------------------------------------------

    def send(self, obj):
        """Send one command. Returns False if not currently connected."""
        sock = self._sock
        if sock is None or not self.connected:
            return False
        line = (json.dumps(obj) + "\n").encode("utf-8")
        try:
            with self._send_lock:
                sock.sendall(line)
            return True
        except OSError as e:
            self.last_error = str(e)
            return False

    def send_set_transform(self, prims):
        """
        Push poses back to Unity. `prims` is {unity_prim_path: {'t':[3], 'r':[4], 's':[3]}} in RAW
        Unity space. Unity applies these only to prims whose UsdSyncNode has AcceptsRemoteWrites =
        true; the rest come back counted as 'ignored' in the ack (the ownership rule).
        """
        if not prims:
            return False
        return self.send({"cmd": "set_transform", "prims": prims})

    def send_reset(self):
        return self.send({"cmd": "reset"})

    def send_get_snapshot(self):
        return self.send({"cmd": "get_snapshot"})

    # -- status ------------------------------------------------------------------------------------

    def status_line(self):
        if self.connected:
            age = time.time() - self.last_message_time if self.last_message_time else -1.0
            age_txt = "{:.1f}s ago".format(age) if age >= 0 else "no data yet"
            return ("connected {}:{} | seq {} | {} msg / {} prim updates | last {}"
                    .format(self.host, self.port, self.seq, self.messages, self.prim_updates, age_txt))
        if self.last_error:
            return "disconnected ({})".format(self.last_error)
        return "disconnected"
