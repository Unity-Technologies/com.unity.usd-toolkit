"""Stdlib-only probe for the Isaac hub: connects like Unity does and reports stream health.

    python probe_client.py [--seconds 20] [--host 127.0.0.1] [--port 10010]

Prints publish rate, per-AGV pose lag (receive wall clock - Isaac publish stamp; same machine),
and the latest nav state. Use it to tell "Isaac is not publishing" apart from
"Unity is not receiving".
"""

import argparse
import json
import os
import socket
import statistics
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import config as cfg  # noqa: E402
from agv_fleet.transport import auth_line, client_token  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=10010)
ap.add_argument("--seconds", type=float, default=20.0)
args = ap.parse_args()

deadline = time.time() + 30
while True:
    try:
        sock = socket.create_connection((args.host, args.port), timeout=2)
        sock.sendall(auth_line(client_token(cfg.STAGE_DIR)))
        break
    except OSError:
        if time.time() > deadline:
            raise SystemExit("hub not reachable")
        time.sleep(1)
sock.settimeout(2)
buf = b""
t_end = time.time() + args.seconds
states, lags, kinds, last = 0, {}, {}, None
first = None
while time.time() < t_end:
    try:
        chunk = sock.recv(65536)
    except socket.timeout:
        continue
    if not chunk:
        break
    rx = time.time() * 1000.0
    buf += chunk
    while b"\n" in buf:
        line, buf = buf.split(b"\n", 1)
        m = json.loads(line)
        kinds[m.get("type")] = kinds.get(m.get("type"), 0) + 1
        if m.get("type") == "state":
            states += 1
            first = first or rx
            last = m
            for a in m["agvs"]:
                lags.setdefault(a["id"], []).append(rx - m["wall_ms"])
dur = (time.time() * 1000.0 - first) / 1000.0 if first else 0
print(f"messages: {kinds}")
print(f"state rate: {states / dur:.1f} Hz over {dur:.1f}s" if dur else "no state messages")
for aid, v in lags.items():
    v = sorted(v)
    print(f"{aid}: lag median {statistics.median(v):.1f} ms, p95 {v[int(len(v) * 0.95) - 1]:.1f} ms, max {v[-1]:.1f} ms (n={len(v)})")
if last:
    for a in last["agvs"]:
        print(f"{a['id']}: p={a['p']} nav={a['nav']} task={a['task']} detail={a.get('detail', '')}")
