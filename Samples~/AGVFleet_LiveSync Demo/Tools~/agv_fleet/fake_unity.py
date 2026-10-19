"""Stand-in for the Unity front end: writes unity_ops.usda exactly like UnityOpsLayer.cs, sends the
same hub notices, and measures the latency chains from Isaac's events. Stdlib only.

Used for scripted acceptance tests and unattended soak tests when nobody is clicking in Unity:

    python fake_unity.py goal --x 5 --y 8 [--target AGV_02]
    python fake_unity.py step3                 # goal -> drive -> wait 8 s -> back to loop
    python fake_unity.py step4                 # obstacle on the path -> re-plan; delete; block-all -> blocked -> unblock
    python fake_unity.py soak --minutes 30     # random goals / obstacles / resets, prints a summary
    python fake_unity.py reset

Do not run it while Unity is in play mode: both would write the same layer (Unity re-asserts its
own layer from its sidecar on its next start).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import socket
import statistics
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import config as cfg  # noqa: E402
from agv_fleet.transport import auth_line, client_token  # noqa: E402

PRESETS = {
    "box": ((0.8, 0.8, 0.8), (0.78, 0.58, 0.36)),
    "pallet": ((1.2, 1.0, 1.2), (0.55, 0.42, 0.26)),
    "barrier": ((2.4, 0.3, 1.0), (0.97, 0.80, 0.10)),
}


def wall_ms() -> float:
    return time.time() * 1000.0


def f(v: float) -> str:
    return f"{v:.5f}".rstrip("0").rstrip(".") if "." in f"{v:.5f}" else f"{v}"


class OpsLayer:
    """Mirror of the sample's Runtime/UnityOpsLayer.cs (same text format)."""

    def __init__(self):
        self.rev = 0
        self.next_id = 9001          # distinct from Unity's numbering
        self.reset_seq = 0
        self.goal = None             # dict(seq, x, y, target, created)
        self.goal_seq = int(time.time()) % 100000  # never collides with an earlier seq
        self.obstacles: dict[str, dict] = {}

    def add(self, preset, x, y, yaw=0.0):
        size, color = PRESETS[preset]
        name = f"Obstacle_{self.next_id:04d}"
        self.next_id += 1
        self.obstacles[name] = {"x": x, "y": y, "yaw": yaw, "size": size, "color": color, "preset": preset, "created": wall_ms()}
        self.write()
        return name, self.obstacles[name]["created"]

    def remove(self, name):
        self.obstacles.pop(name, None)
        t = wall_ms()
        self.write()
        return t

    def set_goal(self, x, y, target=""):
        self.goal_seq += 1
        self.goal = {"seq": self.goal_seq, "x": x, "y": y, "target": target, "created": wall_ms()}
        self.write()
        return f"goal:{self.goal_seq}", self.goal["created"]

    def reset(self):
        self.obstacles.clear()
        self.goal = None
        self.reset_seq += 1
        self.write()

    def write(self):
        self.rev += 1
        out = ['#usda 1.0', '(', '    doc = "Written only by Unity (AGV operator front end). Do not edit by hand."',
               '    customLayerData = {', f'        int unityRev = {self.rev}', f'        double unityWallMs = {wall_ms():.2f}', '    }', ')', '',
               'over "World"', '{', '    over "Dynamic"', '    {', '        def Scope "Obstacles"', '        {',
               f'            custom int unity:resetSeq = {self.reset_seq}']
        for n, o in self.obstacles.items():
            sx, sy, sz = o["size"]
            r, g, b = o["color"]
            out += ['', f'            def Cube "{n}" (', '                prepend apiSchemas = ["PhysicsCollisionAPI"]', '            )', '            {',
                    '                double size = 1',
                    f'                color3f[] primvars:displayColor = [({r}, {g}, {b})]',
                    f'                double3 xformOp:translate = ({f(o["x"])}, {f(o["y"])}, {f(sz * 0.5)})',
                    f'                float xformOp:rotateZ = {f(o["yaw"])}',
                    f'                float3 xformOp:scale = ({sx}, {sy}, {sz})',
                    '                uniform token[] xformOpOrder = ["xformOp:translate", "xformOp:rotateZ", "xformOp:scale"]',
                    f'                custom string unity:preset = "{o["preset"]}"',
                    f'                custom double unity:createdWallMs = {o["created"]:.2f}', '            }']
        out += ['        }']
        if self.goal:
            gl = self.goal
            out += ['', '        def Xform "Goal"', '        {', f'            double3 xformOp:translate = ({f(gl["x"])}, {f(gl["y"])}, 0)',
                    '            uniform token[] xformOpOrder = ["xformOp:translate"]', f'            custom int fleet:goalSeq = {gl["seq"]}',
                    f'            custom string fleet:targetAgv = "{gl["target"]}"', f'            custom double unity:createdWallMs = {gl["created"]:.2f}', '        }']
        out += ['    }', '}', '']
        tmp = str(cfg.UNITY_LAYER) + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(out))
        for _ in range(50):
            try:
                os.replace(tmp, cfg.UNITY_LAYER)
                break
            except PermissionError:
                time.sleep(0.005)


class Hub:
    """Hub client: keeps the latest state, collects events, sends notices."""

    def __init__(self):
        self.sock = socket.create_connection((cfg.HOST, cfg.PORT), timeout=5)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock.sendall(auth_line(client_token(cfg.STAGE_DIR)))
        self.state = None
        self.events: list[dict] = []
        self.fleet = None
        self.paths: dict[str, list] = {}
        self.lags: dict[str, list] = {}
        self.lock = threading.Lock()
        threading.Thread(target=self._rx, daemon=True).start()

    def _rx(self):
        buf = b""
        while True:
            try:
                chunk = self.sock.recv(65536)
            except OSError:
                return
            if not chunk:
                return
            rx = wall_ms()
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                m = json.loads(line)
                with self.lock:
                    if m["type"] == "state":
                        self.state = m
                        for a in m["agvs"]:
                            self.lags.setdefault(a["id"], []).append(rx - m["wall_ms"])
                    elif m["type"] == "event":
                        self.events.append(m)
                    elif m["type"] == "fleet":
                        self.fleet = m
                    elif m["type"] == "path":
                        p = m["pts"]
                        self.paths[m["agv"]] = [(p[i], p[i + 1]) for i in range(0, len(p), 2)]

    def notify(self, layer: OpsLayer):
        self.sock.sendall((json.dumps({"cmd": "layer_changed", "rev": layer.rev, "wall_ms": wall_ms()}) + "\n").encode())

    def reset(self):
        self.sock.sendall((json.dumps({"cmd": "reset", "wall_ms": wall_ms()}) + "\n").encode())

    def agv(self, aid):
        with self.lock:
            for a in (self.state or {}).get("agvs", []):
                if a["id"] == aid:
                    return a
        return None

    def wait_event(self, pred, timeout: float, since: float = 0.0):
        """First event matching pred with Isaac timestamp >= since (wall ms)."""
        t_end = time.time() + timeout
        while time.time() < t_end:
            with self.lock:
                for e in self.events:
                    if e["wall_ms"] >= since and pred(e):
                        return e
            time.sleep(0.02)
        return None


def chain(hub: Hub, ref: str, t0: float, timeout: float = 6.0) -> dict:
    """Times (ms after t0) of seen / replan-or-assign / moving for a ref."""
    def has(name):
        return lambda e: e["event"] == name and ref in (e.get("ref_id") or "").split(",")
    out = {}
    for key, names in (("seen", ("obstacle_seen", "obstacle_removed", "goal_seen")),
                       ("replan", ("task_assigned", "replan_started")),
                       ("moving", ("moving_new_path",))):
        e = hub.wait_event(lambda e: any(has(n)(e) for n in names), timeout, since=t0 - 1.0)
        out[key] = (e["wall_ms"] - t0) if e else None
        if e and key != "seen":
            out[key + "_agv"] = e["agv"]
    return out


def fmt(c: dict) -> str:
    return " ".join(f"{k}={v:.0f}ms" if isinstance(v, float) else f"{k}={v}" for k, v in c.items())


# ------------------------------------------------------------------------------------------------
def scenario_step3(hub, ops, args):
    aid = "AGV_01"
    ref, t0 = ops.set_goal(5.5, 4.0, aid if args.target else "")
    hub.notify(ops)
    print("goal:", fmt(chain(hub, ref, t0)))
    arr = hub.wait_event(lambda e: e["event"] == "arrived" and e.get("ref_id") == ref, 120)
    print("arrived:", f"{(arr['wall_ms'] - t0) / 1000:.1f}s after goal" if arr else "NOT within 120 s")
    if arr:
        time.sleep(1.0)
        print("state after arrival:", hub.agv(aid)["nav"])
        time.sleep(cfg.GOAL_WAIT_S + 1.5)
        a = hub.agv(aid)
        print("state after wait:", a["nav"], a["task"])


def _point_ahead(hub, aid, dist):
    """A point on the AGV's current path `dist` metres ahead."""
    a = hub.agv(aid)
    path = hub.paths.get(aid) or []
    if not a or len(path) < 2:
        return None
    pts = [(a["p"][0], a["p"][1])] + path[max(a["pi"], 1):]
    left = dist
    for i in range(len(pts) - 1):
        seg = math.dist(pts[i], pts[i + 1])
        if seg >= left:
            t = left / seg
            return (pts[i][0] + (pts[i + 1][0] - pts[i][0]) * t, pts[i][1] + (pts[i + 1][1] - pts[i][1]) * t)
        left -= seg
    return None


def scenario_step4(hub, ops, args):
    aid = "AGV_01"
    res = {"seen": [], "replan": [], "moving": []}
    for trial in range(args.trials):
        p = None
        for _ in range(100):
            p = _point_ahead(hub, aid, 3.5)
            a = hub.agv(aid)
            if p and a and a["nav"] in ("looping", "navigating") and len(hub.paths.get(aid) or []) >= 2:
                break
            time.sleep(0.1)
        if not p:
            print("no path to block; skipping trial")
            continue
        name, t0 = ops.add("pallet", p[0], p[1])
        hub.notify(ops)
        c = chain(hub, name, t0)
        print(f"trial {trial + 1}: obstacle {name} at ({p[0]:.1f},{p[1]:.1f}):", fmt(c))
        for k in res:
            if c.get(k) is not None:
                res[k].append(c[k])
        time.sleep(4.0)
        t1 = ops.remove(name)
        hub.notify(ops)
        c = chain(hub, name, t1, timeout=3.0)
        print(f"          removed:", fmt({"seen": c["seen"]}))
        time.sleep(3.0)
    for k, v in res.items():
        if v:
            print(f"{k}: median {statistics.median(v):.0f} ms, max {max(v):.0f} ms (n={len(v)})")

    # ---- block every route to a goal, then open one ------------------------------------------
    gx, gy, h = -2.0, -8.0, 2.4
    walls = [(gx - 1.2, gy - h, 0), (gx + 1.2, gy - h, 0), (gx - 1.2, gy + h, 0), (gx + 1.2, gy + h, 0),
             (gx - h, gy - 1.2, 90), (gx - h, gy + 1.2, 90), (gx + h, gy - 1.2, 90), (gx + h, gy + 1.2, 90)]
    names = [ops.add("barrier", x, y, yaw)[0] for x, y, yaw in walls]
    hub.notify(ops)
    time.sleep(1.0)
    ref, t0 = ops.set_goal(gx, gy, aid)
    hub.notify(ops)
    e = hub.wait_event(lambda e: e["event"] == "blocked" and e["agv"] == aid, 10, since=t0)
    time.sleep(0.3)
    a = hub.agv(aid)
    print(f"block-all: blocked event {'after %.0f ms' % (e['wall_ms'] - t0) if e else 'MISSING'}; nav={a['nav']} detail={a.get('detail')}")
    time.sleep(3.0)
    t1 = ops.remove(names[0])
    hub.notify(ops)
    e = hub.wait_event(lambda e: e["event"] == "unblocked" and e["agv"] == aid, 10, since=t1)
    print(f"opened one wall: unblocked {'after %.0f ms' % (e['wall_ms'] - t1) if e else 'MISSING'}")
    m = hub.wait_event(lambda e: e["event"] == "moving_new_path" and e["agv"] == aid, 10, since=t1)
    print(f"                 moving {'after %.0f ms' % (m['wall_ms'] - t1) if m else 'MISSING'}")
    arr = hub.wait_event(lambda e: e["event"] == "arrived" and e.get("ref_id") == ref, 90, since=t0)
    print(f"reached the walled-in goal: {'yes' if arr else 'NO'}")
    ops.reset()
    hub.reset()
    hub.notify(ops)


def scenario_step5(hub, ops, args):
    """3 AGVs: loops, nearest-by-path assignment, targeted goal, queue + limit, crossing traffic."""
    def assigned(ref, t0, timeout=5.0):
        e = hub.wait_event(lambda e: e["event"] in ("task_assigned", "task_queued", "queue_full", "goal_rejected")
                           and e.get("ref_id") == ref, timeout, since=t0 - 1)
        return (e["event"], e["agv"], e["detail"]) if e else None

    print("loops for 60 s ...")
    t0 = wall_ms()
    time.sleep(60)
    with hub.lock:
        ev = [e for e in hub.events if e["wall_ms"] >= t0]
    print("  events:", {k: sum(1 for e in ev if e["event"] == k) for k in ("yield", "replan_started", "blocked", "deadlock_recovery", "stall_recovery")})
    for a in hub.state["agvs"]:
        print(f"  {a['id']}: {a['nav']} {a['task']} at ({a['p'][0]:.1f},{a['p'][1]:.1f})")

    # nearest by planned path length (AGV_02 loops the east aisle)
    ref, t0 = ops.set_goal(5.0, 6.0)
    hub.notify(ops)
    print("untargeted goal east aisle:", assigned(ref, t0), fmt(chain(hub, ref, t0)))
    time.sleep(2)
    ref, t0 = ops.set_goal(-5.0, 6.0, "AGV_03")
    hub.notify(ops)
    print("targeted goal west aisle for AGV_03:", assigned(ref, t0), fmt(chain(hub, ref, t0)))
    time.sleep(2)
    # AGV_01 is the only free AGV now -> this one is its; then the queue fills up
    results = []
    for i, (x, y) in enumerate([(-7.0, -9.5), (6.5, -9.5), (0.0, -10.0), (-3.0, 15.0), (4.0, 15.5)]):
        ref, t0 = ops.set_goal(x, y)
        hub.notify(ops)
        results.append((ref, assigned(ref, t0)))
        time.sleep(0.4)
    for r in results:
        print("  burst:", r)
    print("fleet:", hub.fleet.get("assign"), "queue:", hub.fleet.get("queue"))
    # let the queue drain: every queued goal must be handed out as AGVs arrive
    t_end = time.time() + 180
    while time.time() < t_end and hub.fleet and hub.fleet.get("queue"):
        time.sleep(1)
    print("queue drained" if hub.fleet and not hub.fleet.get("queue") else f"queue NOT drained: {hub.fleet.get('queue')}")
    with hub.lock:
        cnt = {k: sum(1 for e in hub.events if e["event"] == k) for k in ("yield", "deadlock_recovery", "stall_recovery", "blocked", "unblocked", "arrived")}
    print("totals:", cnt)
    ops.reset()
    hub.notify(ops)
    hub.reset()


def scenario_conflict(hub, ops, args):
    """Head-on: two barriers narrow the west aisle to one lane at y = 4; AGV_01 (high prio) and
    AGV_03 (low prio) are sent through it from opposite ends at the same time."""
    def arrived(ref, since, timeout):
        return hub.wait_event(lambda e: e["event"] == "arrived" and e.get("ref_id") == ref, timeout, since=since)

    ops.reset(); hub.notify(ops); hub.reset()
    time.sleep(1)
    for x in (-7.4, -5.0):
        ops.add("barrier", x, 4.0, 0)
    hub.notify(ops)
    time.sleep(1)
    r1, t1 = ops.set_goal(-6.5, -1.0, "AGV_01"); hub.notify(ops); time.sleep(0.5)
    r3, t3 = ops.set_goal(-2.6, 9.0, "AGV_03"); hub.notify(ops)
    ok = arrived(r1, t1, 120) and arrived(r3, t3, 120)
    print("staged (AGV_01 south of the gap, AGV_03 north):", "yes" if ok else "NO")
    t0 = wall_ms()
    r1, _ = ops.set_goal(-6.5, 9.5, "AGV_01"); hub.notify(ops); time.sleep(0.3)
    r3, _ = ops.set_goal(-3.0, -1.5, "AGV_03"); hub.notify(ops)
    a1 = arrived(r1, t0, 180)
    a3 = arrived(r3, t0, 180)
    print(f"AGV_01 through: {'%.1fs' % ((a1['wall_ms'] - t0) / 1000) if a1 else 'NO'}   AGV_03 through: {'%.1fs' % ((a3['wall_ms'] - t0) / 1000) if a3 else 'NO'}")
    with hub.lock:
        ev = [e for e in hub.events if e["wall_ms"] >= t0]
    for e in ev:
        if e["event"] in ("yield", "deadlock_recovery", "stall_recovery", "blocked", "unblocked") or (e["event"] == "replan_started" and "AGV" in (e.get("ref_id") or "")):
            print(f"  +{(e['wall_ms'] - t0) / 1000:5.1f}s {e['event']:18s} {e['agv']} {e.get('ref_id')} {e.get('detail')}")
    ops.reset(); hub.notify(ops); hub.reset()


def scenario_deadlock(hub, ops, args):
    """Force a real deadlock: barriers close the east aisle and narrow the west aisle to one lane
    at y = 4, so that lane is the ONLY north-south route; AGV_01 and AGV_03 enter it head-on."""
    def arrived(ref, since, timeout):
        return hub.wait_event(lambda e: e["event"] == "arrived" and e.get("ref_id") == ref, timeout, since=since)

    ops.reset(); hub.notify(ops); hub.reset()
    time.sleep(1)
    for x in (-7.4, -5.0, 2.2, 4.6, 7.0):       # west: -8.6..-3.8 closed (lane -3.8..-1.1); east: 1.0..8.2 closed
        ops.add("barrier", x, 4.0, 0)
    hub.notify(ops)
    time.sleep(1)
    r1, t1 = ops.set_goal(-2.5, -1.5, "AGV_01"); hub.notify(ops); time.sleep(0.5)
    r3, t3 = ops.set_goal(-2.5, 9.5, "AGV_03"); hub.notify(ops)
    ok = arrived(r1, t1, 150) and arrived(r3, t3, 150)
    print("staged:", "yes" if ok else "NO")
    t0 = wall_ms()
    r1, _ = ops.set_goal(-2.5, 11.0, "AGV_01"); hub.notify(ops); time.sleep(0.3)
    r3, _ = ops.set_goal(-2.5, -2.5, "AGV_03"); hub.notify(ops)
    a1 = arrived(r1, t0, 240)
    a3 = arrived(r3, t0, 240)
    print(f"AGV_01 through: {'%.1fs' % ((a1['wall_ms'] - t0) / 1000) if a1 else 'NO'}   AGV_03 through: {'%.1fs' % ((a3['wall_ms'] - t0) / 1000) if a3 else 'NO'}")
    with hub.lock:
        ev = [e for e in hub.events if e["wall_ms"] >= t0]
    for e in ev:
        if e["event"] in ("yield", "deadlock_recovery", "stall_recovery", "blocked", "unblocked") or (e["event"] == "replan_started" and "AGV" in (e.get("ref_id") or "")):
            print(f"  +{(e['wall_ms'] - t0) / 1000:5.1f}s {e['event']:18s} {e['agv']} {e.get('ref_id')} {e.get('detail')}")
    print("deadlock recoveries:", hub.fleet.get("recoveries") if hub.fleet else "?")
    ops.reset(); hub.notify(ops); hub.reset()


def scenario_drop_on_agv(hub, ops, args):
    """Obstacle placed right on top of a moving AGV (what a restarted Unity can do when it restores
    its obstacles): Isaac must move the AGV out first -- no PhysX explosion, no NaN pose."""
    for aid in [a["id"] for a in hub.state["agvs"]]:
        a = hub.agv(aid)
        t0 = wall_ms()
        name, _ = ops.add("pallet", a["p"][0], a["p"][1])
        hub.notify(ops)
        e = hub.wait_event(lambda e: e["event"] == "agv_relocated" and e["agv"] == aid, 3, since=t0 - 1)
        time.sleep(3)
        b = hub.agv(aid)
        finite = all(math.isfinite(v) for v in b["p"])
        print(f"{aid}: relocated={'yes ' + e['detail'] if e else 'NO'}; 3 s later pose finite={finite} nav={b['nav']} p=({b['p'][0]:.2f},{b['p'][1]:.2f},{b['p'][2]:.2f})")
        ops.remove(name)
        hub.notify(ops)
        time.sleep(2)
    with hub.lock:
        bad = [e for e in hub.events if e["event"] == "agv_relocated" and "non-finite" in (e.get("ref_id") or "")]
    print("non-finite recoveries:", len(bad))
    ops.reset(); hub.notify(ops); hub.reset()


def scenario_soak(hub, ops, args):
    rng = random.Random(args.seed)
    t_end = time.time() + args.minutes * 60
    free_pts = [(x, y) for x in (-6.0, -4.5, -3.0, 2.5, 4.5, 6.0) for y in (-9.0, -7.0, -4.0, 2.0, 6.0, 10.0)] + [(-4, 14.5), (3.5, 15.5)]
    goal_res, obs_res = {"replan": [], "moving": []}, {"seen": [], "replan": [], "moving": []}
    n_goals = n_obs = n_resets = 0
    last_goal = last_obs = last_reset = time.time()
    last_progress = {}
    stuck_reports = []
    while time.time() < t_end:
        now = time.time()
        if now - last_goal > args.goal_every:
            last_goal = now
            x, y = rng.choice(free_pts)
            tgt = rng.choice(["", "", hub.state["agvs"][rng.randrange(len(hub.state["agvs"]))]["id"]]) if hub.state else ""
            ref, t0 = ops.set_goal(x, y, tgt)
            hub.notify(ops)
            n_goals += 1
            c = chain(hub, ref, t0, timeout=4.0)
            for k in goal_res:
                if c.get(k) is not None:
                    goal_res[k].append(c[k])
        if now - last_obs > args.obstacle_every:
            last_obs = now
            if len(ops.obstacles) >= 4 or (ops.obstacles and rng.random() < 0.4):
                name = rng.choice(list(ops.obstacles))
                ops.remove(name)
                hub.notify(ops)
            else:
                agvs = hub.state["agvs"] if hub.state else []
                aid = rng.choice(agvs)["id"] if agvs else "AGV_01"
                p = _point_ahead(hub, aid, rng.uniform(3.0, 6.0))
                if p and all(math.dist(p, (a["p"][0], a["p"][1])) > 2.0 for a in agvs):
                    name, t0 = ops.add(rng.choice(["box", "pallet"]), p[0], p[1], rng.choice([0, 30, 90]))
                    hub.notify(ops)
                    n_obs += 1
                    c = chain(hub, name, t0, timeout=4.0)
                    for k in obs_res:
                        if c.get(k) is not None:
                            obs_res[k].append(c[k])
        if now - last_reset > args.reset_every * 60:
            last_reset = now
            ops.reset()
            hub.notify(ops)
            hub.reset()
            n_resets += 1
        # progress watchdog: an AGV that should move but has not for 60 s is a failure
        for a in (hub.state or {}).get("agvs", []):
            p = (a["p"][0], a["p"][1])
            ref = last_progress.get(a["id"])
            if ref is None or math.dist(ref[1], p) > 0.5 or a["nav"] in ("waiting", "blocked"):
                last_progress[a["id"]] = (now, p)
            elif now - ref[0] > 60:
                stuck_reports.append((time.strftime("%H:%M:%S"), a["id"], a["nav"], a.get("detail"), p))
                last_progress[a["id"]] = (now, p)
        time.sleep(0.5)

    ops.reset()
    hub.notify(ops)
    hub.reset()
    with hub.lock:
        ev = list(hub.events)
        lags = {k: sorted(v) for k, v in hub.lags.items()}
    count = {}
    for e in ev:
        count[e["event"]] = count.get(e["event"], 0) + 1
    print(f"soak {args.minutes} min: goals {n_goals}, obstacles placed {n_obs}, resets {n_resets}")
    print("events:", json.dumps(count, sort_keys=True))
    for k, v in lags.items():
        print(f"{k} pose lag (transport): median {statistics.median(v):.1f} ms, p95 {v[int(len(v) * .95) - 1]:.1f}, max {v[-1]:.1f} (n={len(v)})")
    for name, res in (("goal", goal_res), ("obstacle", obs_res)):
        for k, v in res.items():
            if v:
                v = sorted(v)
                print(f"{name} -> {k}: median {statistics.median(v):.0f} ms, p95 {v[int(len(v) * .95) - 1]:.0f}, max {v[-1]:.0f} (n={len(v)})")
    print("no-progress reports (>60 s):", stuck_reports if stuck_reports else "none")
    if hub.fleet:
        print("deadlock recoveries:", hub.fleet.get("recoveries"), "stall recoveries:", hub.fleet.get("stalls"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scenario", choices=["goal", "step3", "step4", "step5", "conflict", "deadlock", "drop_on_agv", "soak", "reset"])
    ap.add_argument("--x", type=float)
    ap.add_argument("--y", type=float)
    ap.add_argument("--target", default="")
    ap.add_argument("--trials", type=int, default=5)
    ap.add_argument("--minutes", type=float, default=30)
    ap.add_argument("--goal-every", type=float, default=40)
    ap.add_argument("--obstacle-every", type=float, default=15)
    ap.add_argument("--reset-every", type=float, default=10)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()

    hub = Hub()
    ops = OpsLayer()
    ops.write()
    hub.notify(ops)
    t_end = time.time() + 10
    while hub.state is None and time.time() < t_end:
        time.sleep(0.1)
    time.sleep(1.0)
    if args.scenario == "goal":
        ref, t0 = ops.set_goal(args.x, args.y, args.target)
        hub.notify(ops)
        print(ref, fmt(chain(hub, ref, t0)))
    elif args.scenario == "step3":
        scenario_step3(hub, ops, args)
    elif args.scenario == "step4":
        scenario_step4(hub, ops, args)
    elif args.scenario == "step5":
        scenario_step5(hub, ops, args)
    elif args.scenario == "conflict":
        scenario_conflict(hub, ops, args)
    elif args.scenario == "deadlock":
        scenario_deadlock(hub, ops, args)
    elif args.scenario == "drop_on_agv":
        scenario_drop_on_agv(hub, ops, args)
    elif args.scenario == "soak":
        scenario_soak(hub, ops, args)
    elif args.scenario == "reset":
        ops.reset()
        hub.notify(ops)
        hub.reset()
    time.sleep(0.5)


if __name__ == "__main__":
    main()
