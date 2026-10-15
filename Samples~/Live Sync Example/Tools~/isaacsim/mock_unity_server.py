#!/usr/bin/env python3
"""
Mock `UsdLiveSyncServer` — lets you bring up and debug the Isaac Sim side with no Unity Editor running.

It speaks the exact wire protocol of the sample's `UsdLiveSyncServer.cs` (TCP :10000,
newline-delimited JSON): a `snapshot` on join, throttled `delta` broadcasts, and `ack` replies to
`set_transform` / `reset` / `get_snapshot`. Prim paths are read straight out of `base_stage.usda`, so
the paths it streams are the same ones the real server would send for that scene — which means a
"prim not found" failure in Isaac is a real bug, not an artefact of made-up test paths.

Stdlib only; runs on any Python 3.8+, including a system interpreter.

    python Tools/isaacsim/mock_unity_server.py
    python Tools/isaacsim/mock_unity_server.py --limit 12 --hz 30 --amplitude 2.0
    python Tools/isaacsim/mock_unity_server.py --prims /SyncRoot/PlayerArmature

This is a test double, not part of the runtime pipeline: Unity remains the real host.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import secrets
import socket
import sys
import threading
import time

DEF_RE = re.compile(r'^(\s*)def\s+(\w+)?\s*"([^"]+)"')
def find_unity_project_root(start):
    """
    Walk up from 'start' looking for a Unity project root (a folder holding both Assets/ and
    ProjectSettings/). This tree ships with the USD Toolkit samples, so it usually runs from the
    Assets/Samples/Unity USD Toolkit/... copy that the Package Manager imports, which sits under the
    project root. Returns None when it was copied somewhere else entirely.
    """
    current = os.path.abspath(start)
    while True:
        if (os.path.isdir(os.path.join(current, "Assets"))
                and os.path.isdir(os.path.join(current, "ProjectSettings"))):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


DEFAULT_BASE_STAGE = os.path.normpath(os.path.join(
    find_unity_project_root(os.path.dirname(os.path.abspath(__file__))) or os.getcwd(),
    "UsdSync", "base_stage.usda"))


def read_prim_paths(usda_path, indent_width=4, types=("Xform",)):
    """
    Collect prim paths from a .usda by indentation depth, keeping only the requested prim types.

    A real USD parse would need pxr, which is exactly what is unavailable outside Kit — and the
    exporter's output is uniformly indented, so tracking a stack of names by depth is sufficient and
    keeps this file stdlib-only.

    The type filter matters: the file also contains `Mesh`, `Material` and `Shader` prims, and the
    real `UsdLiveSyncServer` only ever streams the `Xform` prims it maps GameObjects onto. Streaming
    a transform onto a Shader would author meaningless xformOps, so the mock must not offer those.
    Every level still enters the name stack, so nested paths stay correct.
    """
    paths = []
    stack = []
    with open(usda_path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = DEF_RE.match(line)
            if not m:
                continue
            depth = len(m.group(1)) // indent_width
            prim_type, name = m.group(2), m.group(3)
            del stack[depth:]
            stack.append(name)
            if not types or prim_type in types:
                paths.append("/" + "/".join(stack))
    return paths


class MockServer:
    def __init__(self, args, prim_paths):
        self.args = args
        self.paths = prim_paths
        self.animated = prim_paths[:args.limit] if args.limit > 0 else list(prim_paths)

        # prim_path -> [t, r, s]; baseline is identity so a reset is visibly distinct from motion.
        self.state = {p: {"t": [0.0, 0.0, 0.0], "r": [0.0, 0.0, 0.0, 1.0], "s": [1.0, 1.0, 1.0]}
                      for p in self.paths}
        self.baseline = json.loads(json.dumps(self.state))

        self.clients = []
        self.authenticated = set()      # mirrors the real server: nothing is served before the token
        self.token = args.token
        self.lock = threading.Lock()
        self.seq = 0
        self.running = True

    # -- framing -----------------------------------------------------------------------------------

    def _next_seq(self):
        self.seq += 1
        return self.seq

    @staticmethod
    def _now_ms():
        return int(time.time() * 1000)

    def _send(self, sock, obj):
        try:
            sock.sendall((json.dumps(obj) + "\n").encode("utf-8"))
            return True
        except OSError:
            return False

    def _broadcast(self, obj):
        line = (json.dumps(obj) + "\n").encode("utf-8")
        with self.lock:
            dead = []
            for c in self.clients:
                if c not in self.authenticated:
                    continue
                try:
                    c.sendall(line)
                except OSError:
                    dead.append(c)
            for c in dead:
                self.clients.remove(c)
                self.authenticated.discard(c)
                try:
                    c.close()
                except OSError:
                    pass

    def _snapshot(self, reason):
        return {"type": "snapshot", "reason": reason, "seq": self._next_seq(),
                "t": self._now_ms(), "prims": self.state}

    # -- server ------------------------------------------------------------------------------------

    def serve(self):
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

        # Deliberately NOT SO_REUSEADDR. On Windows that option lets a second process bind a port
        # another process is already listening on: the new bind "succeeds" while the older process
        # keeps accepting the connections. For a debugging tool that is the worst possible outcome —
        # you watch a silent log and conclude the feature is broken, when in fact your client is
        # talking to a stale server. SO_EXCLUSIVEADDRUSE makes the collision an error instead.
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            srv.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)

        try:
            srv.bind((self.args.host, self.args.port))
        except OSError as e:
            print("[mock-unity] cannot bind {}:{} — {}".format(self.args.host, self.args.port, e),
                  file=sys.stderr)
            print("[mock-unity] something is already listening there: another mock server, or the "
                  "real Unity UsdLiveSyncServer (is Unity in Play mode?).", file=sys.stderr)
            raise SystemExit(2)
        srv.listen(8)
        print("[mock-unity] listening on {}:{} | {} prim(s), {} animated"
              .format(self.args.host, self.args.port, len(self.paths), len(self.animated)),
              flush=True)

        threading.Thread(target=self._animate_loop, name="mock-animate", daemon=True).start()

        try:
            while self.running:
                sock, addr = srv.accept()
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                with self.lock:
                    self.clients.append(sock)
                print("[mock-unity] client connected {} — awaiting auth".format(addr), flush=True)
                threading.Thread(target=self._read_client, args=(sock, addr),
                                 name="mock-client", daemon=True).start()
        except KeyboardInterrupt:
            print("\n[mock-unity] shutting down.", flush=True)
        finally:
            self.running = False
            try:
                srv.close()
            except OSError:
                pass

    def _read_client(self, sock, addr):
        buf = b""
        try:
            while self.running:
                chunk = sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    raw = raw.strip()
                    if raw:
                        self._handle(sock, raw)
        except OSError:
            pass
        finally:
            with self.lock:
                if sock in self.clients:
                    self.clients.remove(sock)
                self.authenticated.discard(sock)
            try:
                sock.close()
            except OSError:
                pass
            print("[mock-unity] client disconnected {}".format(addr), flush=True)

    def _handle(self, sock, raw):
        try:
            msg = json.loads(raw.decode("utf-8", errors="replace"))
        except json.JSONDecodeError:
            self._send(sock, {"type": "ack", "cmd": "?", "ok": False, "error": "malformed JSON"})
            return

        cmd = msg.get("cmd")

        if cmd == "auth":
            if secrets.compare_digest(str(msg.get("token") or ""), self.token):
                with self.lock:
                    self.authenticated.add(sock)
                self._send(sock, {"type": "ack", "cmd": "auth", "ok": True})
                self._send(sock, self._snapshot("join"))
                print("[mock-unity] client authenticated", flush=True)
            else:
                print("[mock-unity] rejected client: invalid token", flush=True)
                self._send(sock, {"type": "ack", "cmd": "auth", "ok": False, "error": "invalid token"})
                try:
                    sock.close()
                except OSError:
                    pass
            return

        with self.lock:
            is_authenticated = sock in self.authenticated
        if not is_authenticated:
            self._send(sock, {"type": "ack", "cmd": str(cmd), "ok": False,
                              "error": "authentication required"})
            return

        if cmd == "set_transform":
            prims = msg.get("prims", {}) or {}
            applied = unknown = 0
            for path, trs in prims.items():
                if path in self.state:
                    self.state[path].update(trs)
                    applied += 1
                else:
                    unknown += 1
            print("[mock-unity] set_transform: applied {} unknown {} -> {}"
                  .format(applied, unknown, list(prims)[:3]), flush=True)
            self._send(sock, {"type": "ack", "cmd": "set_transform", "ok": True,
                              "applied": applied, "ignored": 0, "unknown": unknown})
        elif cmd == "reset":
            self.state = json.loads(json.dumps(self.baseline))
            print("[mock-unity] reset", flush=True)
            self._broadcast(self._snapshot("reset"))
            self._send(sock, {"type": "ack", "cmd": "reset", "ok": True})
        elif cmd == "get_snapshot":
            self._send(sock, self._snapshot("request"))
            self._send(sock, {"type": "ack", "cmd": "get_snapshot", "ok": True})
        else:
            self._send(sock, {"type": "ack", "cmd": str(cmd), "ok": False,
                              "error": "unsupported command"})

    # -- motion ------------------------------------------------------------------------------------

    def _animate_loop(self):
        """Drive each animated prim on a phase-offset circle plus a Y spin, so motion is unmistakable."""
        period = 1.0 / max(0.5, self.args.hz)
        t0 = time.time()
        while self.running:
            time.sleep(period)
            with self.lock:
                if not self.clients:
                    continue
            elapsed = time.time() - t0
            prims = {}
            for i, path in enumerate(self.animated):
                phase = elapsed * self.args.speed + i * 0.7
                amp = self.args.amplitude
                half = math.radians(phase * 60.0) * 0.5   # half-angle for the quaternion
                trs = {
                    "t": [amp * math.cos(phase), 0.0, amp * math.sin(phase)],
                    "r": [0.0, math.sin(half), 0.0, math.cos(half)],   # Unity (x,y,z,w), spin about Y
                    "s": [1.0, 1.0, 1.0],
                }
                self.state[path] = trs
                prims[path] = trs
            self._broadcast({"type": "delta", "seq": self._next_seq(),
                             "t": self._now_ms(), "prims": prims})


TOKEN_FILE_NAME = "live_sync_token.txt"


def _path_inside(directory, name):
    """
    Join a fixed file name onto an already-canonical directory and prove the result stays in it.

    'directory' is derived from the user-supplied --base-stage, so nothing built from it is trusted
    until it has been resolved and checked: the joined path is canonicalized and must still have
    'directory' as its parent. Anything that escapes (a separator or '..' smuggled into 'name', or a
    symlink swapped in) raises instead of reaching os.open / os.unlink.
    """
    candidate = os.path.realpath(os.path.join(directory, name))
    if os.path.dirname(candidate) != directory:
        raise OSError("refusing to write outside {}: {}".format(directory, candidate))
    return candidate


def write_token_file(directory, token):
    """
    Write the token to TOKEN_FILE_NAME in 'directory' so only the current user can read it, matching
    UsdLiveSyncServer. Returns the path written.

    A plain open(path, "w") creates the file under the umask -- 0644 on a typical macOS/Linux host --
    which publishes the one secret guarding this server to every other account on the machine, and it
    writes *through* a symlink anyone may have planted at the destination. So: create an unguessably
    named file in the same directory with O_EXCL (never following a symlink, never clobbering) and
    mode 0600, write into it, then os.replace() it into place. The rename is atomic and replaces a
    symlink at the destination rather than following it. umask can only clear permission bits, so the
    result is never more permissive than 0600.

    Only the directory comes from the caller; both file names are fixed here, and every path is
    canonicalized and confined to that directory before it is used.
    """
    directory = os.path.realpath(directory or ".")
    os.makedirs(directory, exist_ok=True)
    if not os.path.isdir(directory):
        raise OSError("not a directory: {}".format(directory))

    token_path = _path_inside(directory, TOKEN_FILE_NAME)
    staging = _path_inside(directory, ".{}.{}.tmp".format(TOKEN_FILE_NAME, secrets.token_hex(8)))

    fd = os.open(staging, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(token)
        os.replace(staging, token_path)
    except BaseException:
        try:
            os.unlink(staging)
        except OSError:
            pass
        raise
    return token_path


def main(argv=None):
    p = argparse.ArgumentParser(description="Mock Unity UsdLiveSyncServer for Isaac-side testing.",
                                formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=10000)
    p.add_argument("--token", default=None,
                   help="Shared secret clients must present. Default: $USD_LIVE_SYNC_TOKEN, else a "
                        "generated one written to live_sync_token.txt beside --base-stage.")
    p.add_argument("--base-stage", default=DEFAULT_BASE_STAGE,
                   help="Read prim paths from this .usda.")
    p.add_argument("--prims", default="",
                   help="Comma-separated prim paths, instead of reading --base-stage.")
    p.add_argument("--limit", type=int, default=8,
                   help="Animate only the first N prims (0 = all). Others still appear in snapshots.")
    p.add_argument("--hz", type=float, default=20.0, help="Delta broadcast rate.")
    p.add_argument("--speed", type=float, default=1.0, help="Orbit speed (rad/s).")
    p.add_argument("--amplitude", type=float, default=1.5, help="Orbit radius, Unity units.")
    args = p.parse_args(argv)

    if args.prims:
        paths = [s.strip() for s in args.prims.split(",") if s.strip()]
    else:
        if not os.path.isfile(args.base_stage):
            print("[mock-unity] no such file: {}".format(args.base_stage), file=sys.stderr)
            return 2
        paths = read_prim_paths(args.base_stage)
        if not paths:
            print("[mock-unity] no prims parsed from {}".format(args.base_stage), file=sys.stderr)
            return 2

    # Mirror the Unity server's token resolution so the same clients work against either.
    args.token = args.token or os.environ.get("USD_LIVE_SYNC_TOKEN")
    if not args.token:
        args.token = secrets.token_hex(32)
        token_dir = os.path.dirname(os.path.realpath(args.base_stage))
        token_path = os.path.join(token_dir, TOKEN_FILE_NAME)
        try:
            token_path = write_token_file(token_dir, args.token)
            print("[mock-unity] generated auth token -> {} (owner-readable only)".format(token_path),
                  flush=True)
        except OSError as e:
            print("[mock-unity] could not write {} ({}); pass --token instead".format(token_path, e),
                  file=sys.stderr)
            return 2

    MockServer(args, paths).serve()
    return 0


if __name__ == "__main__":
    sys.exit(main())
