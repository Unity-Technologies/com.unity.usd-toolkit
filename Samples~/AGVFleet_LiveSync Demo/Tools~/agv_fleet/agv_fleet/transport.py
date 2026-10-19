"""TCP hub: newline-delimited JSON between Isaac Sim (server) and Unity (client).

Stdlib only, so it imports before Kit boots. Socket I/O runs on worker threads; the sim loop only
touches two thread-safe queues (poll() for inbound, broadcast() for outbound), so a slow or dead
client can never stall physics.

Authentication (same scheme as the USD Toolkit's Live Sync Example): a client's first line must be
{"cmd":"auth","token":"..."}. Until then it receives nothing; a wrong token, any other first line, or
no line within AUTH_TIMEOUT_S hangs up. The token is AGV_FLEET_HUB_TOKEN, or one generated per launch
and written to hub_token.txt in the stage folder, readable by the current user only. The transport is
plain TCP on loopback: the token keeps other local processes out, it does not encrypt anything.
"""

from __future__ import annotations

import hmac
import json
import os
import queue
import secrets
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

TOKEN_FILE_NAME = "hub_token.txt"
TOKEN_ENV = "AGV_FLEET_HUB_TOKEN"
AUTH_TIMEOUT_S = 3.0
MAX_LINE_BYTES = 64 * 1024


def wall_ms() -> float:
    """Wall clock in ms since the Unix epoch -- the clock both apps share on one machine."""
    return time.time() * 1000.0


# ---- token ----------------------------------------------------------------------------------------
def _restrict_to_owner(path: str):
    """Owner-only access on an (empty) file; raises when that cannot be guaranteed."""
    if sys.platform != "win32":
        os.chmod(path, 0o600)
        return
    who = subprocess.run(["whoami"], capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run(["icacls", path, "/inheritance:r", "/grant:r", f"{who}:F"],
                   capture_output=True, text=True, check=True)


def server_token(stage_dir: Path) -> str:
    """AGV_FLEET_HUB_TOKEN, else a new random token written (owner-only) to <stage_dir>/hub_token.txt.
    Fails closed: if the file cannot be restricted to this user, nothing is written and this raises."""
    env = os.environ.get(TOKEN_ENV, "").strip()
    if env:
        print(f"[agv_fleet] hub token from ${TOKEN_ENV}")
        return env
    token = secrets.token_urlsafe(32)
    stage_dir.mkdir(parents=True, exist_ok=True)
    final = stage_dir / TOKEN_FILE_NAME
    # an unguessable staging name, restricted while still empty, then filled and renamed into place
    staging = stage_dir / f"{TOKEN_FILE_NAME}.{secrets.token_hex(8)}.tmp"
    try:
        fd = os.open(str(staging), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        _restrict_to_owner(str(staging))
        with open(staging, "w", encoding="utf-8") as f:
            f.write(token)
        os.replace(staging, final)
    except Exception:
        try:
            os.remove(staging)
        except OSError:
            pass
        raise
    print(f"[agv_fleet] hub token written to {final} (owner-readable only)")
    return token


def client_token(stage_dir: Path) -> str | None:
    """For local clients (fake_unity, probe_client): AGV_FLEET_HUB_TOKEN, else hub_token.txt."""
    env = os.environ.get(TOKEN_ENV, "").strip()
    if env:
        return env
    try:
        return (stage_dir / TOKEN_FILE_NAME).read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def auth_line(token: str | None) -> bytes:
    return (json.dumps({"cmd": "auth", "token": token or ""}, separators=(",", ":")) + "\n").encode("utf-8")


class _Client:
    def __init__(self, sock: socket.socket, addr):
        self.sock = sock
        self.addr = addr
        self.outbox: "queue.Queue[bytes | None]" = queue.Queue(maxsize=256)
        self.alive = True
        self.authed = False


class HubServer:
    def __init__(self, host: str, port: int, token: str, on_connect=None):
        if not token:
            raise ValueError("the hub needs a token")
        self._token = token.encode("utf-8")
        self._host = host
        self._port = port
        self._on_connect = on_connect          # called on the sim thread via poll() -> ("connect", client)
        self._inbox: "queue.Queue[tuple]" = queue.Queue()
        self._clients: list[_Client] = []
        self._lock = threading.Lock()
        self._srv: socket.socket | None = None
        self._stop = threading.Event()
        self.dropped_frames = 0

    # ---- lifecycle ------------------------------------------------------------------------------
    def start(self):
        self._srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._srv.bind((self._host, self._port))
        self._srv.listen(4)
        self._srv.settimeout(0.5)
        threading.Thread(target=self._accept_loop, name="hub-accept", daemon=True).start()
        print(f"[agv_fleet] hub listening on {self._host}:{self._port}")

    def stop(self):
        self._stop.set()
        with self._lock:
            for c in self._clients:
                self._close(c)
        if self._srv:
            self._srv.close()

    @property
    def client_count(self) -> int:
        with self._lock:
            return sum(1 for c in self._clients if c.alive and c.authed)

    # ---- sim-thread API -------------------------------------------------------------------------
    def poll(self) -> list[tuple]:
        """Drain inbound events: ("msg", client, dict), ("connect", client, None) once a client has
        authenticated, or ("disconnect", client, None)."""
        out = []
        while True:
            try:
                out.append(self._inbox.get_nowait())
            except queue.Empty:
                return out

    def broadcast(self, msg: dict):
        data = (json.dumps(msg, separators=(",", ":")) + "\n").encode("utf-8")
        with self._lock:
            clients = [c for c in self._clients if c.authed]
        for c in clients:
            self._enqueue(c, data)

    def send(self, client: _Client, msg: dict):
        self._enqueue(client, (json.dumps(msg, separators=(",", ":")) + "\n").encode("utf-8"))

    def _enqueue(self, c: _Client, data: bytes):
        if not c.alive:
            return
        try:
            c.outbox.put_nowait(data)
        except queue.Full:
            # The client is not keeping up: drop the oldest frame rather than grow latency.
            try:
                c.outbox.get_nowait()
                c.outbox.put_nowait(data)
            except (queue.Empty, queue.Full):
                pass
            self.dropped_frames += 1

    # ---- worker threads -------------------------------------------------------------------------
    def _accept_loop(self):
        while not self._stop.is_set():
            try:
                sock, addr = self._srv.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            sock.settimeout(AUTH_TIMEOUT_S)   # until authenticated
            c = _Client(sock, addr)
            with self._lock:
                self._clients = [x for x in self._clients if x.alive] + [c]
            threading.Thread(target=self._read_loop, args=(c,), name="hub-read", daemon=True).start()
            threading.Thread(target=self._write_loop, args=(c,), name="hub-write", daemon=True).start()

    def _read_loop(self, c: _Client):
        buf = b""
        try:
            while c.alive and not self._stop.is_set():
                chunk = c.sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
                if len(buf) > MAX_LINE_BYTES and b"\n" not in buf:
                    break   # no hub line is ever this long: not a hub client
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    if not line.strip():
                        continue
                    try:
                        msg = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(msg, dict):
                        continue
                    if not c.authed:
                        if not self._authenticate(c, msg):
                            self._close(c)
                            return
                        continue
                    msg["_rx_ms"] = wall_ms()
                    self._inbox.put(("msg", c, msg))
        except OSError:   # includes the auth timeout
            pass
        self._close(c)

    def _authenticate(self, c: _Client, msg: dict) -> bool:
        token = msg.get("token") if msg.get("cmd") == "auth" else None
        ok = isinstance(token, str) and hmac.compare_digest(token.encode("utf-8"), self._token)
        if not ok:
            why = "invalid token" if msg.get("cmd") == "auth" else "not authenticated"
            print(f"[agv_fleet] client {c.addr} rejected: {why}")
            try:
                c.sock.sendall(b'{"type":"auth","ok":false}\n')
            except OSError:
                pass
            return False
        c.sock.settimeout(None)
        c.authed = True
        self._inbox.put(("connect", c, None))   # the sim thread sends the snapshot
        print(f"[agv_fleet] client connected {c.addr}")
        return True

    def _write_loop(self, c: _Client):
        try:
            while c.alive and not self._stop.is_set():
                try:
                    data = c.outbox.get(timeout=0.5)
                except queue.Empty:
                    continue
                if data is None:
                    break
                c.sock.sendall(data)
        except OSError:
            pass
        self._close(c)

    def _close(self, c: _Client):
        if not c.alive:
            return
        c.alive = False
        try:
            c.outbox.put_nowait(None)
        except queue.Full:
            pass
        try:
            c.sock.close()
        except OSError:
            pass
        if c.authed:
            self._inbox.put(("disconnect", c, None))
            print(f"[agv_fleet] client disconnected {c.addr}")
