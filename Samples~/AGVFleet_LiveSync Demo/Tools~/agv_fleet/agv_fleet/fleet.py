"""Fleet manager: task assignment + queue, planning jobs, inter-AGV avoidance, deadlock recovery.

Runs on the sim thread; planning (scipy Dijkstra) runs on one worker thread so physics and the
30 Hz publish never wait on it. Every AGV plans against: static grid + Unity obstacles (hard
keep-out) + the other AGVs' current footprints (refreshed every DYNAMIC_UPDATE_S).
"""

from __future__ import annotations

import math
from concurrent.futures import ThreadPoolExecutor

import numpy as np

import config as cfg

from .agv import Agv
from .planner import GoalField, plan


class Fleet:
    def __init__(self, grid, agv_ids: list[str], log):
        self.grid = grid
        self.log = log
        self.agvs = [Agv(a, i) for i, a in enumerate(agv_ids)]
        self.by_id = {a.id: a for a in self.agvs}
        self.queue: list[dict] = []            # pending goals {"seq","x","y","target"}
        self.pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="planner")
        self.jobs: list = []                   # (future, kind, payload)
        self.masks: dict[str, np.ndarray] = {}
        self.last_mask_t = -1.0
        self.deadlock_recoveries = 0
        self.stall_recoveries = 0
        self.fleet_dirty = True
        self.assign_pending = None             # goal waiting for its distance field

    # ---- inputs ---------------------------------------------------------------------------------
    def update_poses(self, poses: dict, wheels: dict):
        for a in self.agvs:
            if a.id in poses:
                a.x, a.y, a.yaw = poses[a.id]
                a.wheels = wheels[a.id]

    def on_obstacles_changed(self, added: set, removed: set, now: float):
        refs = ",".join(sorted(added | removed))
        for a in self.agvs:
            if a.state == "blocked":
                a.request_plan("obstacles", refs)
                self.log.log("replan_started", a.id, refs, "blocked retry")
                continue
            if a.plan_pending:
                a.request_plan("obstacles", refs)  # its in-flight plan predates the change
                continue
            if not a.path:
                continue
            if added:
                hit = self._path_blocked(a, self.grid.hard)
                if hit:
                    ref = ",".join(sorted(added))
                    a.request_plan("obstacle", ref)
                    self.log.log("replan_started", a.id, ref, "path blocked by obstacle")
                    continue
            if removed:
                # only switch if a clearly shorter route opened up (checked when the plan returns)
                a.request_plan("shorter?", ",".join(sorted(removed)))

    def on_goal(self, goal: dict, now: float):
        if not self._goal_in_free_space(goal):
            self.log.log("goal_rejected", goal.get("target", ""), f"goal:{goal['seq']}",
                         f"({goal['x']:.1f},{goal['y']:.1f}) outside free space")
            return
        tgt = goal.get("target") or ""
        if tgt and tgt in self.by_id:
            a = self.by_id[tgt]
            if a.free_for_task or a.task is None:
                self._assign(a, goal, "targeted")
            else:
                self._enqueue(goal)
            return
        free = [a for a in self.agvs if a.task is None or a.state in ("waiting", "arrived")]
        if not free:
            self._enqueue(goal)
            return
        # nearest by planned path length: one distance field from the goal (worker thread)
        blocked = self.grid.hard.copy()
        fut = self.pool.submit(GoalField, self.grid, blocked, (goal["x"], goal["y"]))
        self.jobs.append((fut, "assign", {"goal": goal, "cands": [a.id for a in free]}))

    def reset(self):
        self.queue.clear()
        for a in self.agvs:
            a.yielding_to = ""
            a.backup = None
            a.back_to_loop()
            a.state = "looping"
        self.fleet_dirty = True

    # ---- per-tick -------------------------------------------------------------------------------
    def tick(self, now: float, dt: float) -> dict:
        """Returns {agv_id: (v, w)}."""
        if now - self.last_mask_t >= cfg.DYNAMIC_UPDATE_S:
            self.last_mask_t = now
            self.masks = {a.id: self.grid.agv_mask(a.x, a.y, a.yaw) for a in self.agvs}
            self._path_vs_agvs(now)
        self._collect_jobs(now)
        self._avoidance(now)
        cmds = {}
        for a in self.agvs:
            if a.state == "blocked" and not a.want_plan and not a.plan_pending and now - a.last_blocked_try >= cfg.REPLAN_BLOCKED_S:
                a.request_plan("blocked retry")
            events = []
            cmds[a.id] = a.control(now, dt, events)
            for name, ref in events:
                if name == "moving_new_path":
                    self.log.log("moving_new_path", a.id, ref)
                elif name == "arrived":
                    self.log.log("arrived", a.id, ref)
                    self.fleet_dirty = True
                    self._give_next(a)
                elif name == "stalled":
                    self.stall_recoveries += 1
                    rear = self._rear_free(a)
                    self.log.log("stall_recovery", a.id, "", f"#{self.stall_recoveries} at ({a.x:.2f},{a.y:.2f}) "
                                 + ("backup" if rear else "rear blocked, replan only"))
                    if rear:
                        a.backup = {"x": a.x, "y": a.y, "until": now + cfg.BACKUP_DIST / cfg.BACKUP_SPEED + 2.0}
                        a.detail = "backing up"
                    else:
                        a.request_plan("stall")
                elif name == "wait_done":
                    a.back_to_loop()
                    a.state = "looping"
                    self.fleet_dirty = True
            if a.want_plan and not a.plan_pending:
                self._submit_plan(a)
        return cmds

    # ---- planning -------------------------------------------------------------------------------
    def _blocked_for(self, a: Agv) -> np.ndarray:
        b = self.grid.hard.copy()
        for other_id, m in self.masks.items():
            if other_id != a.id:
                b |= m
        return b

    def _submit_plan(self, a: Agv):
        cause, ref = a.want_plan, a.want_ref
        a.want_plan, a.want_ref = "", ""
        a.plan_pending = True
        dest = a.destination()
        fut = self.pool.submit(plan, self.grid, self._blocked_for(a), (a.x, a.y), dest)
        self.jobs.append((fut, "plan", {"agv": a.id, "dest": dest, "cause": cause, "ref": ref}))

    def _collect_jobs(self, now: float):
        still = []
        for fut, kind, p in self.jobs:
            if not fut.done():
                still.append((fut, kind, p))
                continue
            try:
                res = fut.result()
            except Exception as e:  # noqa: BLE001
                print(f"[agv_fleet] planner job failed: {e}")
                if kind == "plan":
                    self.by_id[p["agv"]].plan_pending = False
                continue
            if kind == "plan":
                self._on_plan(self.by_id[p["agv"]], res, p, now)
            else:
                self._on_assign_field(res, p, now)
        self.jobs = still

    def _on_plan(self, a: Agv, res, p: dict, now: float):
        if p["cause"] == "shorter?" and res.ok and a.path and a.state in ("looping", "navigating"):
            if res.length >= cfg.SHORTER_ROUTE_GAIN * a.remaining_length():
                a.plan_pending = False
                return  # keep the current route
            self.log.log("replan_started", a.id, p["ref"], f"shorter route {res.length:.1f}m")
        was_blocked = a.state == "blocked"
        if a.apply_plan(res, p["dest"], now, p["ref"]):
            if not res.ok and a.detail == "skipped blocked waypoint":
                self.log.log("loop_waypoint_skipped", a.id, p["ref"], f"loop:{a.loop_idx}")
            elif a.state == "blocked" and not was_blocked:
                self.log.log("blocked", a.id, p["ref"], res.reason)
                self.fleet_dirty = True
            elif res.ok and was_blocked:
                self.log.log("unblocked", a.id, p["ref"], f"{res.length:.1f}m")
                self.fleet_dirty = True
            if res.ok and p["cause"] in ("task", "obstacle", "obstacles", "yield", "deadlock", "agv"):
                self.log.log("plan_ready", a.id, p["ref"], f"{p['cause']} {res.length:.1f}m {res.ms:.0f}ms")

    def _on_assign_field(self, field, p: dict, now: float):
        goal = p["goal"]
        best, best_len = None, math.inf
        for aid in p["cands"]:
            a = self.by_id[aid]
            if not (a.task is None or a.state in ("waiting", "arrived")):
                continue  # got busy while the field was computing
            ln = field.length_from((a.x, a.y))
            if ln < best_len:
                best, best_len = a, ln
        if best is None:
            if not self._goal_in_free_space(goal):
                self.log.log("goal_rejected", "", f"goal:{goal['seq']}", f"({goal['x']:.1f},{goal['y']:.1f}) outside free space")
                return
            # reachable in principle but every route is blocked right now: give it to the nearest
            # free AGV anyway -- it goes to "blocked" and carries on when an obstacle is removed
            cands = [self.by_id[a] for a in p["cands"] if self.by_id[a].free_for_task]
            if not cands:
                self._enqueue(goal)
                return
            best = min(cands, key=lambda a: math.dist((a.x, a.y), (goal["x"], goal["y"])))
            self._assign(best, goal, "nearest (no route yet)")
            return
        self._assign(best, goal, f"nearest {best_len:.1f}m")

    def _goal_in_free_space(self, goal: dict) -> bool:
        """Free of the static map (keep-out included); Unity obstacles do not count here."""
        r, c = self.grid.to_cell(goal["x"], goal["y"])
        if not self.grid.inside(r, c):
            return False
        static_clear = self.grid.static_clearance[r, c]
        return static_clear >= cfg.INFLATE_RADIUS - 0.3

    def _assign(self, a: Agv, goal: dict, why: str):
        a.start_task({"seq": goal["seq"], "x": goal["x"], "y": goal["y"]})
        self.log.log("task_assigned", a.id, f"goal:{goal['seq']}", why)
        self.log.log("replan_started", a.id, f"goal:{goal['seq']}", "new task")
        self.fleet_dirty = True

    def _enqueue(self, goal: dict):
        if len(self.queue) >= cfg.QUEUE_MAX:
            self.log.log("queue_full", "", f"goal:{goal['seq']}", "dropped")
            return
        self.queue.append(goal)
        self.log.log("task_queued", goal.get("target", ""), f"goal:{goal['seq']}", f"queue={len(self.queue)}")
        self.fleet_dirty = True

    def _give_next(self, a: Agv):
        for i, g in enumerate(self.queue):
            if not g.get("target") or g["target"] == a.id:
                self.queue.pop(i)
                self._assign(a, g, "from queue")
                return

    # ---- avoidance ------------------------------------------------------------------------------
    def _path_blocked(self, a: Agv, blocked: np.ndarray, ahead: float = 1e9) -> bool:
        for x, y in a.path_ahead(ahead, step=self.grid.res):
            r, c = self.grid.to_cell(x, y)
            if self.grid.inside(r, c) and blocked[r, c]:
                # ignore cells right at the robot (it may be inside its own inflation)
                if math.dist((x, y), (a.x, a.y)) > 0.5:
                    return True
        return False

    def _path_vs_agvs(self, now: float):
        """Every DYNAMIC_UPDATE_S: an AGV whose path now runs through a lower-priority (or
        stationary) AGV re-plans around it. Lower-priority AGVs yield instead (see _avoidance)."""
        for a in self.agvs:
            if not a.path or a.plan_pending or a.want_plan or a.yielding_to or now - a.last_path_check_replan < 1.0:
                continue
            for b in self.agvs:
                if b is a:
                    continue
                if b.prio < a.prio and b.state in ("looping", "navigating") and b.path:
                    continue  # moving higher-priority AGV: handled by yielding
                if self._path_blocked(a, self.masks[b.id], ahead=cfg.CONFLICT_LOOKAHEAD + 3.0):
                    a.last_path_check_replan = now
                    a.request_plan("agv", b.id)
                    self.log.log("replan_started", a.id, b.id, f"path runs through {b.id} ({b.state})")
                    break

    def _conflict(self, low: Agv, high: Agv) -> bool:
        if math.dist((low.x, low.y), (high.x, high.y)) > cfg.CONFLICT_DIST + 1.5:
            return False
        grow = 0.33 + cfg.AGV_MARGIN
        hi_poly = self.grid.footprint_corners(high.x, high.y, high.yaw, grow)
        lo_poly = self.grid.footprint_corners(low.x, low.y, low.yaw, grow)
        if any(_in_poly(p, hi_poly) for p in low.path_ahead(cfg.CONFLICT_LOOKAHEAD)):
            return True
        if high.state in ("looping", "navigating") and not high.yielding_to:
            if any(_in_poly(p, lo_poly) for p in high.path_ahead(cfg.CONFLICT_LOOKAHEAD)):
                return True
        return False

    def _avoidance(self, now: float):
        for low in self.agvs:
            if low.backup is not None:
                continue
            # who (higher priority, within CONFLICT_DIST) is this AGV in conflict with?
            culprit = ""
            if low.state in ("looping", "navigating") and low.path:
                for high in self.agvs:
                    if high.prio < low.prio and math.dist((low.x, low.y), (high.x, high.y)) <= cfg.CONFLICT_DIST and self._conflict(low, high):
                        culprit = high.id
                        break
            if culprit:
                if low.yielding_to != culprit:
                    low.yielding_to = culprit
                    low.yield_since = now
                    low.yield_replanned = False
                    low.detail = f"yield:{culprit}"
                    self.log.log("yield", low.id, culprit)
                elif not low.yield_replanned and now - low.yield_since > cfg.YIELD_REPLAN_S:
                    low.yield_replanned = True
                    low.yielding_to = ""     # resume once the detour arrives
                    low.detail = f"detour around {culprit}"
                    low.request_plan("yield", culprit)
                    self.log.log("replan_started", low.id, culprit, f"yield > {cfg.YIELD_REPLAN_S}s")
            elif low.yielding_to:
                low.yielding_to = ""
                low.detail = ""
        for a in self.agvs:
            # "stuck" survives yield -> detour -> yield cycles; it clears only after 2 s unstuck
            if a.yielding_to or a.state == "blocked":
                if a.stuck_since is None:
                    a.stuck_since = now
                a.unstuck_since = None
            elif a.stuck_since is not None:
                if a.unstuck_since is None:
                    a.unstuck_since = now
                elif now - a.unstuck_since > 2.0:
                    a.stuck_since = None
        self._deadlocks(now)

    def _stuck_since(self, a: Agv):
        if a.stuck_since is not None and (a.yielding_to or a.state == "blocked"):
            return a.stuck_since
        return None

    def _deadlocks(self, now: float):
        for i, hi in enumerate(self.agvs):
            for lo in self.agvs[i + 1:]:
                if lo.backup is not None or math.dist((hi.x, hi.y), (lo.x, lo.y)) > cfg.CONFLICT_DIST + 1.5:
                    continue
                t_hi, t_lo = self._stuck_since(hi), self._stuck_since(lo)
                if t_hi is None or t_lo is None:
                    continue
                if now - max(t_hi, t_lo) < cfg.DEADLOCK_S:
                    continue
                # both stuck >= DEADLOCK_S next to each other: the lower-priority one backs up
                self.deadlock_recoveries += 1
                rear_free = self._rear_free(lo)
                self.log.log("deadlock_recovery", lo.id, hi.id,
                             f"#{self.deadlock_recoveries} {'backup 1m' if rear_free else 'rear blocked, replan only'}")
                lo.yielding_to = ""
                lo.yield_replanned = False
                lo.blocked_since = now
                lo.stuck_since = hi.stuck_since = None  # next recovery needs another full DEADLOCK_S
                if rear_free:
                    lo.backup = {"x": lo.x, "y": lo.y, "until": now + cfg.BACKUP_DIST / cfg.BACKUP_SPEED + 2.0}
                    lo.detail = "backing up"
                    lo.state = "navigating" if lo.task else "looping"
                else:
                    lo.request_plan("deadlock", hi.id)
                if hi.state == "blocked":
                    hi.request_plan("deadlock", lo.id)
                self.fleet_dirty = True

    def _rear_free(self, a: Agv) -> bool:
        c, s = math.cos(a.yaw), math.sin(a.yaw)
        blocked = self.grid.static | self.grid.obstacles
        # real footprints here, not the planning masks (those include inflation)
        others = [self.grid.footprint_corners(o.x, o.y, o.yaw, 0.1) for o in self.agvs if o is not a]
        for d in np.arange(0.1, cfg.BACKUP_DIST + 0.05, 0.1):
            back = cfg.FOOTPRINT_X[0] - d
            for lat in (cfg.FOOTPRINT_Y[0], 0.0, cfg.FOOTPRINT_Y[1]):
                x, y = a.x + back * c - lat * s, a.y + back * s + lat * c
                r, cc = self.grid.to_cell(x, y)
                if not self.grid.inside(r, cc) or blocked[r, cc]:
                    return False
                if any(_in_poly((x, y), poly) for poly in others):
                    return False
        return True

    # ---- outputs --------------------------------------------------------------------------------
    def assignments(self) -> dict:
        out = {}
        for a in self.agvs:
            if a.task is not None:
                out[a.id] = f"goal:{a.task['seq']}"
            else:
                out[a.id] = f"loop:{a.loop_idx}"
        return out

    def queue_view(self) -> list:
        return [f"goal:{g['seq']}" + (f"->{g['target']}" if g.get("target") else "") for g in self.queue]


def _in_poly(p, poly) -> bool:
    x, y = p
    sign = 0
    n = len(poly)
    for i in range(n):
        ax, ay = poly[i]
        bx, by = poly[(i + 1) % n]
        cr = (bx - ax) * (y - ay) - (by - ay) * (x - ax)
        if cr != 0:
            s = 1 if cr > 0 else -1
            if sign == 0:
                sign = s
            elif s != sign:
                return False
    return True
