#!/usr/bin/env python3
"""
External client for the Unity USD live transform sync (UsdLiveSyncServer, TCP :10000).

Bidirectional, near-real-time transform sync with the Unity scene, over newline-delimited JSON — the
transport the USD Toolkit's Live Sync Example uses. This client:

  * receives 'snapshot' (on connect / on reset / on request) and 'delta' broadcasts and maintains an
    authoritative in-memory copy of every tracked prim's transform;
  * can push edits back with 'set_transform';
  * can trigger a scene 'reset';
  * can checkpoint the current state to 'live_overrides.usda' — a session layer that sublayers
    base_stage.usda and authors 'over' Xform prims with xformOp:translate / :orient / :scale.

Streaming, --set and --reset use the Python standard library only. Writing live_overrides.usda needs
OpenUSD's Python bindings ('pip install usd-core'); the tool degrades gracefully and tells you if they
are missing.

CRITICAL — coordinate conversion. UsdExporter writes base_stage.usda with a Unity(left-handed) ->
USD(right-handed) X-axis flip. The live channel carries RAW Unity local values, so this client must
apply the SAME flip when authoring the override layer, or the overrides will visually disagree with the
baseline geometry (objects appear mirrored/offset). The flip is: negate translate X, and map the Unity
quaternion (x, y, z, w) to USD orient Gf.Quatf(w, x, -y, -z); scale is unchanged. This mirrors the
exporter's FlipX (S*M*S with S=diag(-1,1,1)). Per the implementation plan this MUST be confirmed in
usdview with both layers composed before the pipeline is trusted.

Examples:
    python3 usd_live_sync.py --watch                      # stream + print live transforms
    python3 usd_live_sync.py --watch --checkpoint-interval 5   # ...and write overrides every 5s
    python3 usd_live_sync.py --set /SyncRoot/PropCube --translate 0.5,1,-2
    python3 usd_live_sync.py --set /SyncRoot/PropCube --translate=-1.5,0.5,0   # leading '-' needs '='
    python3 usd_live_sync.py --set /SyncRoot/PropCube --rot 0,0,0,1 --scale 2,2,2
    python3 usd_live_sync.py --reset
    python3 usd_live_sync.py --checkpoint                 # snapshot once -> live_overrides.usda (overwrites)
    python3 usd_live_sync.py --checkpoint --timestamped   # -> live_overrides_YYYYMMDD_HHMMSS_mmm.usda (own file)
"""

import argparse
import json
import os
import socket
import sys
import time
from datetime import datetime

# ANSI colors (auto-disabled when stdout is not a TTY). Kept inline so this client is a single
# self-contained file you can copy anywhere.
_COLORS = {
    "info": "[37m",       # grey/white
    "warning": "[33m",    # yellow
    "error": "[31m",      # red
    "run": "[36m",        # cyan
    "hit": "[32m",        # green
    "dim": "[2m",
    "reset": "[0m",
}


def make_colorizer(enabled):
    if not enabled:
        return lambda _key, text: text
    return lambda key, text: f"{_COLORS.get(key, '')}{text}{_COLORS['reset']}"


def make_overrides_path(path, timestamped):
    """Optionally insert a timestamp before the extension so each checkpoint is its own file
    (e.g. live_overrides_20260720_153012_244.usda) instead of overwriting the previous one."""
    if not timestamped:
        return path
    root, ext = os.path.splitext(path)
    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")[:-3]  # ms precision -> unique even in fast --watch
    return f"{root}_{ts}{ext}"


# ================================================================================================
# Socket framing (newline-delimited JSON) — same conventions as the other clients here.
# ================================================================================================

TOKEN_FILE_NAME = "live_sync_token.txt"


def resolve_token(args, color):
    """
    Find the shared secret UsdLiveSyncServer requires, in the order the server itself resolves it:
    --token, --token-file, $USD_LIVE_SYNC_TOKEN, then live_sync_token.txt in the output folder (which is
    where the server writes the token it generates when its Auth Token field is left empty).
    """
    if args.token:
        return args.token

    if args.token_file:
        path = os.path.abspath(os.path.expanduser(args.token_file))
        try:
            with open(path, "r", encoding="utf-8") as f:
                return f.read().strip()
        except OSError as e:
            print(color("warning", f"[client] could not read --token-file '{path}' ({e})"))
            return None

    from_environment = os.environ.get("USD_LIVE_SYNC_TOKEN")
    if from_environment:
        return from_environment.strip()

    out, _, _ = resolve_paths(args)
    path = os.path.join(out, TOKEN_FILE_NAME)
    try:
        with open(path, "r", encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        print(color("warning", f"[client] no auth token: not in --token/--token-file/$USD_LIVE_SYNC_TOKEN, "
                               f"and '{path}' is not readable."))
        print(color("dim", "[client] the server prints the token file's location when it starts; pass the "
                           "folder with --output-dir, or the token itself with --token."))
        return None


def connect(args, color):
    """
    Connect and authenticate. Returns (sock, reader); (None, None) on failure. The reader is handed back
    because the handshake may already have buffered the join snapshot that follows the auth ack.
    """
    token = resolve_token(args, color)
    if not token:
        return None, None

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(args.connect_timeout)
    try:
        sock.connect((args.host, args.port))
    except (ConnectionRefusedError, socket.timeout, OSError) as e:
        print(color("dim", f"[client] could not connect to {args.host}:{args.port} ({e})"))
        print(color("dim", "[client] is Unity in Play mode with a UsdLiveSyncServer in the scene?"))
        sock.close()
        return None, None
    sock.settimeout(None)

    # The server refuses every other command — including get_snapshot — until this succeeds, and hangs up
    # on an invalid token, so a failure here is authoritative.
    reader = LineReader(sock)
    try:
        send(sock, {"cmd": "auth", "token": token})
        ack = reader.next_matching(lambda r: r.get("type") == "ack" and r.get("cmd") == "auth",
                                   timeout=args.timeout)
    except OSError as e:
        print(color("warning", f"[client] authentication failed ({e})"))
        sock.close()
        return None, None

    if ack is None:
        print(color("warning", "[client] no reply to the auth handshake — is this an older server build?"))
        sock.close()
        return None, None

    if not ack.get("ok"):
        print(color("warning", f"[client] authentication rejected: {ack.get('error', 'invalid token')}"))
        sock.close()
        return None, None

    return sock, reader


class LineReader:
    """Buffers a socket into newline-delimited JSON records (handles fragmented reads)."""

    def __init__(self, sock):
        self.sock = sock
        self.buffer = b""

    def records(self):
        while True:
            try:
                chunk = self.sock.recv(4096)
            except OSError as e:
                print(f"[client] socket error: {e}", file=sys.stderr)
                return
            if not chunk:
                return  # server closed
            self.buffer += chunk
            while b"\n" in self.buffer:
                raw, self.buffer = self.buffer.split(b"\n", 1)
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    yield json.loads(raw.decode("utf-8", errors="replace"))
                except json.JSONDecodeError:
                    continue

    def next_record(self, timeout=None):
        """Block for a single record (used by one-shot verbs). Returns dict or None."""
        return self.next_matching(lambda _r: True, timeout)

    def next_matching(self, pred, timeout=None):
        """
        Block until a record satisfying pred arrives. Skips records that don't match — notably the
        unsolicited 'join' snapshot the server sends on connect, which would otherwise be mistaken for a
        command reply. Returns the matching dict or None on timeout / disconnect.
        """
        self.sock.settimeout(timeout)
        try:
            for rec in self.records():
                if pred(rec):
                    return rec
        except socket.timeout:
            return None
        finally:
            self.sock.settimeout(None)
        return None


def send(sock, obj):
    sock.sendall((json.dumps(obj) + "\n").encode("utf-8"))


# ================================================================================================
# In-memory authoritative state
# ================================================================================================

class SyncState:
    """prim_path -> {'t': [x,y,z], 'r': [x,y,z,w], 's': [x,y,z]} in RAW Unity local space."""

    def __init__(self):
        self.prims = {}
        self.seq = 0

    def apply(self, rec):
        rtype = rec.get("type")
        if rtype == "snapshot":
            self.prims = dict(rec.get("prims", {}))
        elif rtype == "delta":
            self.prims.update(rec.get("prims", {}))
        else:
            return None
        self.seq = rec.get("seq", self.seq)
        return rtype


# ================================================================================================
# USD authoring (live_overrides.usda) — requires OpenUSD Python bindings.
# ================================================================================================

def unity_to_usd_translate(t):
    """Unity left-handed -> USD right-handed: negate X (mirrors UsdExporter.ToUsdVector / FlipX)."""
    return (-float(t[0]), float(t[1]), float(t[2]))


def unity_to_usd_quat(r):
    """
    Unity quaternion (x,y,z,w) -> USD orient. Conjugating a rotation by S=diag(-1,1,1) (the exporter's
    X flip) sends axis-angle (a, theta) to (S a, -theta), i.e. quaternion (x,y,z,w) -> (x,-y,-z,w).
    Returns (w, x, y, z) real-first for Gf.Quatf(real, i, j, k).
    """
    x, y, z, w = float(r[0]), float(r[1]), float(r[2]), float(r[3])
    return (w, x, -y, -z)


def write_overrides(state, base_stage, overrides_path, color):
    try:
        from pxr import Usd, UsdGeom, Gf, Sdf  # noqa: F401
    except ImportError:
        print(color("error", "[checkpoint] OpenUSD Python bindings not found. Install with:"))
        print(color("dim", "               pip install usd-core"))
        return False

    from pxr import Usd, UsdGeom, Gf, Sdf

    overrides_path = os.path.abspath(overrides_path)
    base_stage = os.path.abspath(base_stage)

    # Fresh session layer each checkpoint so removed prims don't linger.
    if os.path.exists(overrides_path):
        os.remove(overrides_path)
    layer = Sdf.Layer.CreateNew(overrides_path)

    # Sublayer the baseline (weaker) so 'over' prims at identical paths override its transforms.
    if os.path.exists(base_stage):
        rel = os.path.relpath(base_stage, os.path.dirname(overrides_path))
        layer.subLayerPaths.append(rel)
    else:
        print(color("warning", f"[checkpoint] base stage not found next to overrides ({base_stage}); "
                               "writing override-only layer (no composed geometry)."))

    stage = Usd.Stage.Open(layer)
    stage.SetEditTarget(Usd.EditTarget(layer))

    count = 0
    root_prim = None
    for prim_path, trs in sorted(state.prims.items()):
        prim = stage.OverridePrim(prim_path)  # specifier = "over"
        if root_prim is None or len(prim_path) < len(root_prim.GetPath().pathString):
            root_prim = prim
        xform = UsdGeom.Xformable(prim)
        # Replace whatever op order the base authored (a single xformOp:transform matrix) with an explicit
        # translate/orient/scale stack from this layer (the stronger opinion).
        xform.ClearXformOpOrder()
        t = unity_to_usd_translate(trs.get("t", [0, 0, 0]))
        w, qx, qy, qz = unity_to_usd_quat(trs.get("r", [0, 0, 0, 1]))
        s = trs.get("s", [1, 1, 1])
        xform.AddTranslateOp().Set(Gf.Vec3d(*t))
        xform.AddOrientOp(UsdGeom.XformOp.PrecisionFloat).Set(Gf.Quatf(w, qx, qy, qz))
        xform.AddScaleOp().Set(Gf.Vec3f(float(s[0]), float(s[1]), float(s[2])))
        count += 1

    if root_prim is not None:
        stage.SetDefaultPrim(root_prim)  # so `usdview live_overrides.usda` opens on the sync root
    layer.Save()
    print(color("hit", f"[checkpoint] wrote {count} override(s) -> {overrides_path}"))
    return True


def find_unity_project_root(start):
    """
    Walk up from 'start' looking for a Unity project root (a folder holding both Assets/ and
    ProjectSettings/). This file ships inside the USD Toolkit package, so it may run from an embedded
    Packages/com.unity.usd-toolkit/... path or from Library/PackageCache/... — both sit under the
    project root. Returns None when the script was copied somewhere else entirely.
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


def resolve_paths(args):
    """
    Resolve the output dir and the two USD file paths. Default: '<unity project>/UsdSync', which is where
    UsdLiveSyncServer writes base_stage.usda when its Output Directory is left empty in the Editor. A
    built player writes to '<persistentDataPath>/UsdSync' instead — the sample HUD prints the exact
    folder, so pass it with --output-dir when driving a player.
    """
    if args.output_dir:
        out = os.path.abspath(os.path.expanduser(args.output_dir))
    else:
        project_root = find_unity_project_root(os.path.dirname(os.path.abspath(__file__)))
        out = os.path.join(project_root or os.getcwd(), "UsdSync")
    base = os.path.join(out, args.base_name)
    overrides = os.path.join(out, args.overrides_name)
    return out, base, overrides


# ================================================================================================
# Pretty printing
# ================================================================================================

def fmt_prim(path, trs):
    t = trs.get("t", [0, 0, 0])
    r = trs.get("r", [0, 0, 0, 1])
    s = trs.get("s", [1, 1, 1])
    return (f"{path}\n"
            f"      t=({t[0]:+.3f},{t[1]:+.3f},{t[2]:+.3f})  "
            f"r=({r[0]:+.3f},{r[1]:+.3f},{r[2]:+.3f},{r[3]:+.3f})  "
            f"s=({s[0]:.3f},{s[1]:.3f},{s[2]:.3f})")


# ================================================================================================
# Verbs
# ================================================================================================

def parse_floats(text, n, name):
    parts = [p for p in text.replace(" ", "").split(",") if p != ""]
    if len(parts) != n:
        raise SystemExit(f"--{name} expects {n} comma-separated numbers, got '{text}'")
    return [float(p) for p in parts]


def verb_set(args, color):
    sock, reader = connect(args, color)
    if sock is None:
        return 2
    trs = {}
    if args.translate is not None:
        trs["t"] = parse_floats(args.translate, 3, "translate")
    if args.rot is not None:
        trs["r"] = parse_floats(args.rot, 4, "rot")
    if args.scale is not None:
        trs["s"] = parse_floats(args.scale, 3, "scale")
    if not trs:
        print(color("warning", "[set] nothing to set — pass --translate and/or --rot and/or --scale"))
        sock.close()
        return 1
    try:
        send(sock, {"cmd": "set_transform", "prims": {args.set: trs}})
        ack = reader.next_matching(
            lambda r: r.get("type") == "ack" and r.get("cmd") == "set_transform", timeout=args.timeout)
    finally:
        sock.close()
    print(color("hit", "[set] ") + json.dumps(ack))
    return 0 if ack and ack.get("ok") else 1


def verb_reset(args, color):
    sock, reader = connect(args, color)
    if sock is None:
        return 2
    try:
        send(sock, {"cmd": "reset"})
        ack = reader.next_matching(
            lambda r: r.get("type") == "ack" and r.get("cmd") == "reset", timeout=args.timeout)
    finally:
        sock.close()
    print(color("hit", "[reset] ") + json.dumps(ack))
    return 0 if ack and ack.get("ok") else 1


def verb_checkpoint(args, color):
    _, base, overrides = resolve_paths(args)
    sock, reader = connect(args, color)
    if sock is None:
        return 2
    state = SyncState()
    try:
        send(sock, {"cmd": "get_snapshot"})
        # Read until we get the snapshot (an ack may arrive too).
        deadline = time.time() + args.timeout
        while time.time() < deadline:
            rec = reader.next_record(timeout=args.timeout)
            if rec is None:
                break
            if state.apply(rec) == "snapshot":
                break
    finally:
        sock.close()
    if not state.prims:
        print(color("warning", "[checkpoint] no prims received — is the sync root populated?"))
        return 1
    ok = write_overrides(state, base, make_overrides_path(overrides, args.timestamped), color)
    return 0 if ok else 1


def verb_watch(args, color):
    _, base, overrides = resolve_paths(args)
    while True:
        sock, reader = connect(args, color)
        if sock is not None:
            print(color("hit", f"[client] connected to {args.host}:{args.port} — watching (Ctrl+C to quit)"))
            state = SyncState()
            last_checkpoint = time.time()
            try:
                for rec in reader.records():
                    rtype = rec.get("type")
                    if rtype in ("snapshot", "delta"):
                        state.apply(rec)
                        if not args.quiet:
                            tag = "SNAPSHOT" if rtype == "snapshot" else "delta"
                            reason = f" ({rec.get('reason')})" if rtype == "snapshot" else ""
                            changed = rec.get("prims", {})
                            print(color("run", f"[{tag}{reason} seq={rec.get('seq')}] {len(changed)} prim(s)"))
                            for path, trs in changed.items():
                                print(color("dim", "    " + fmt_prim(path, trs)))
                    elif rtype == "ack":
                        if not args.quiet:
                            print(color("dim", f"[ack] {json.dumps(rec)}"))

                    if args.checkpoint_interval > 0 and time.time() - last_checkpoint >= args.checkpoint_interval:
                        write_overrides(state, base, make_overrides_path(overrides, args.timestamped), color)
                        last_checkpoint = time.time()
            finally:
                sock.close()
            print(color("warning", "[client] disconnected (Unity stopped or client dropped)"))

        if not args.reconnect:
            return 0
        print(color("dim", f"[client] retrying in {args.reconnect_interval:.0f}s…"))
        time.sleep(args.reconnect_interval)


# ================================================================================================
# CLI
# ================================================================================================

def main():
    p = argparse.ArgumentParser(description="External client for the Unity USD live transform sync.")
    p.add_argument("--host", default="127.0.0.1", help="Server address (default 127.0.0.1).")
    p.add_argument("--port", type=int, default=10000, help="Server port (UsdLiveSyncServer, default 10000).")
    p.add_argument("--token", help="Shared secret the server requires. Default: $USD_LIVE_SYNC_TOKEN, else "
                                   f"'{TOKEN_FILE_NAME}' in the output folder (see --output-dir).")
    p.add_argument("--token-file", help="Read the shared secret from this file instead.")

    mode = p.add_mutually_exclusive_group()
    mode.add_argument("--watch", action="store_true", help="Stream deltas/snapshots and print live transforms.")
    mode.add_argument("--set", metavar="PRIM", help="Push a transform edit to one prim path (needs --translate/--rot/--scale).")
    mode.add_argument("--reset", action="store_true", help="Trigger a scene reset (restore Unity baseline).")
    mode.add_argument("--checkpoint", action="store_true", help="Snapshot once and write live_overrides.usda, then exit.")

    # NOTE: a value that starts with a minus sign (e.g. -1.5,0,0) must use the '=' form,
    # --translate=-1.5,0,0, because argparse otherwise reads the leading '-' as another option.
    p.add_argument("--translate", help="Comma-separated x,y,z (Unity local) for --set. "
                                        "For negative values use '=': --translate=-1.5,0,0")
    p.add_argument("--rot", help="Comma-separated quaternion x,y,z,w for --set.")
    p.add_argument("--scale", help="Comma-separated x,y,z scale for --set.")

    p.add_argument("--checkpoint-interval", type=float, default=0.0,
                   help="With --watch: write live_overrides.usda every N seconds (0 = never).")
    p.add_argument("--output-dir", help="Folder holding base_stage.usda / live_overrides.usda "
                        "(default '<unity project>/UsdSync').")
    p.add_argument("--base-name", default="base_stage.usda", help="Baseline stage filename.")
    p.add_argument("--overrides-name", default="live_overrides.usda", help="Override layer filename.")
    p.add_argument("--timestamped", action="store_true",
                   help="Write each checkpoint to its own timestamped file (…_YYYYMMDD_HHMMSS_mmm.usda) "
                        "instead of overwriting live_overrides.usda. Applies to --checkpoint and to the "
                        "periodic checkpoints of --watch --checkpoint-interval.")

    p.add_argument("--reconnect", action="store_true", help="With --watch: auto-retry if the connection drops.")
    p.add_argument("--reconnect-interval", type=float, default=3.0, help="Seconds between reconnect attempts (default 3).")
    p.add_argument("--connect-timeout", type=float, default=5.0, help="Connection attempt timeout (default 5).")
    p.add_argument("--timeout", type=float, default=10.0, help="Reply timeout for one-shot verbs (default 10).")
    p.add_argument("--quiet", action="store_true", help="With --watch: suppress per-record printing.")
    p.add_argument("--no-color", action="store_true", help="Disable ANSI colors.")
    args = p.parse_args()

    try:
        sys.stdout.reconfigure(line_buffering=True)
    except AttributeError:
        pass

    color = make_colorizer(sys.stdout.isatty() and not args.no_color)

    try:
        if args.set:
            return verb_set(args, color)
        if args.reset:
            return verb_reset(args, color)
        if args.checkpoint:
            return verb_checkpoint(args, color)
        if args.watch:
            return verb_watch(args, color)
        p.print_help()
        return 0
    except KeyboardInterrupt:
        print(color("dim", "\n[client] interrupted, exiting."))
        return 130


if __name__ == "__main__":
    sys.exit(main())
