"""One AGV: navigation state machine + pure-pursuit path follower for a differential drive.

Nav states (published): idle / looping / navigating / waiting / blocked / arrived.
`detail` carries the sub-state Unity shows next to it (e.g. "yield:AGV_01", "backing up").
"""

from __future__ import annotations

import math

import config as cfg

COLORS = {aid: cfg.color_for(aid) for aid in ("AGV_01", "AGV_02", "AGV_03")}   # blue, orange, green


def wrap(a: float) -> float:
    return (a + math.pi) % (2 * math.pi) - math.pi


class Agv:
    def __init__(self, agv_id: str, prio: int):
        self.id = agv_id
        self.prio = prio                       # 0 = highest
        self.color = cfg.color_for(agv_id)
        self.loop = list(cfg.LOOPS[agv_id])
        self.loop_idx = 1 % len(self.loop)
        sx, sy, syaw = cfg.SPAWN[agv_id]
        self.x, self.y, self.yaw = sx, sy, math.radians(syaw)
        self.wheels = (0.0, 0.0)
        self.v = 0.0
        self.w = 0.0

        self.state = "idle"
        self.detail = ""
        self.task = None                       # {"seq", "x", "y"} while serving a goal
        self.path: list = []
        self.path_i = 1                        # index of the next path point
        self.path_ver = 0
        self.dest = None                       # (x, y) the current path leads to

        self.want_plan = ""                    # non-empty = a plan is wanted; value = cause
        self.want_ref = ""                     # event ref for the latency log
        self.plan_pending = False
        self.plan_ref = ""                     # ref of the plan in effect, until first motion
        self.wait_until = 0.0
        self.blocked_since = None
        self.last_blocked_try = 0.0
        self.yielding_to = ""
        self.yield_since = 0.0
        self.yield_replanned = False
        self.backup = None                     # {"x","y","until"}
        self._loop_skips = 0                   # consecutive loop waypoints skipped as blocked
        self.stuck_since = None                # yielding or blocked since (deadlock detection)
        self.unstuck_since = None
        self.last_path_check_replan = 0.0

    # ---- targets --------------------------------------------------------------------------------
    @property
    def free_for_task(self) -> bool:
        return self.task is None or self.state in ("waiting", "arrived")

    def destination(self):
        if self.task is not None:
            return (self.task["x"], self.task["y"])
        return self.loop[self.loop_idx]

    def request_plan(self, cause: str, ref: str = ""):
        self.want_plan = cause
        self.want_ref = ref

    def start_task(self, task: dict):
        self.task = task
        self.yielding_to = ""
        self.request_plan("task", f"goal:{task['seq']}")

    def back_to_loop(self):
        self.task = None
        # resume at the loop waypoint nearest to where the AGV is now
        d = [math.dist((self.x, self.y), p) for p in self.loop]
        self.loop_idx = int(min(range(len(d)), key=d.__getitem__))
        if d[self.loop_idx] < 1.0:
            self.loop_idx = (self.loop_idx + 1) % len(self.loop)
        self.request_plan("loop")

    # ---- planner results ------------------------------------------------------------------------
    def apply_plan(self, res, dest, now: float, ref: str) -> bool:
        self.plan_pending = False
        if dest != self.destination():
            return False  # the destination changed while planning; a new plan is already wanted
        if not res.ok:
            if self.task is None and res.reason == "goal blocked" and self._loop_skips < len(self.loop) - 1:
                # an obstacle sits on this loop waypoint: keep the loop going via the next one
                # (goals, in contrast, stay "blocked" until the route opens)
                self._loop_skips += 1
                self.loop_idx = (self.loop_idx + 1) % len(self.loop)
                self.detail = "skipped blocked waypoint"
                self.request_plan("loop")
                return True
            self.path = []
            self.dest = None
            if self.state != "blocked":
                self.blocked_since = now
            self.state = "blocked"
            self.detail = res.reason
            self.last_blocked_try = now
            return True
        self._loop_skips = 0
        self.path = res.points
        self.path_i = 1
        self.path_ver += 1
        self.dest = dest
        self.state = "navigating" if self.task is not None else "looping"
        self.detail = ""
        self.blocked_since = None
        self.plan_ref = ref
        return True

    def remaining_length(self) -> float:
        if not self.path:
            return math.inf
        pts = [(self.x, self.y)] + self.path[self.path_i:]
        return sum(math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1))

    def path_ahead(self, dist: float, step: float = 0.2) -> list:
        """Points along the remaining path up to `dist` metres ahead."""
        out = []
        if not self.path:
            return out
        prev = (self.x, self.y)
        left = dist
        for p in self.path[self.path_i:]:
            seg = math.dist(prev, p)
            n = max(int(min(seg, left) / step), 1)
            for k in range(1, n + 1):
                t = min(k * step, seg) / seg if seg > 1e-6 else 1.0
                out.append((prev[0] + (p[0] - prev[0]) * t, prev[1] + (p[1] - prev[1]) * t))
            left -= seg
            if left <= 0:
                break
            prev = p
        return out

    # ---- control --------------------------------------------------------------------------------
    def control(self, now: float, dt: float, events) -> tuple[float, float]:
        """Returns (v, w). `events` is a list the fleet drains: (name, detail)."""
        v_t, w_t = 0.0, 0.0
        if self.backup is not None:
            moved = math.dist((self.x, self.y), (self.backup["x"], self.backup["y"]))
            if moved >= cfg.BACKUP_DIST or now > self.backup["until"]:
                self.backup = None
                self.detail = ""
                self.request_plan("deadlock")
            else:
                v_t = -cfg.BACKUP_SPEED
        elif self.state == "waiting":
            if now >= self.wait_until:
                events.append(("wait_done", ""))
        elif self.state == "arrived":
            self.state = "waiting"
            self.wait_until = now + cfg.GOAL_WAIT_S
        elif self.state in ("looping", "navigating") and self.path and not self.yielding_to:
            v_t, w_t = self._pursue(now, events)
        self._watch_stall(now, v_t, w_t, events)
        # acceleration-limited speed
        dv = cfg.ACCEL_LIN * dt
        self.v = max(min(v_t, self.v + dv), self.v - dv * 2.0)
        self.w = w_t
        if self.plan_ref and (abs(self.v) > 0.05 or abs(self.w) > 0.1):
            events.append(("moving_new_path", self.plan_ref))
            self.plan_ref = ""
        return self.v, self.w

    def _watch_stall(self, now: float, v_t: float, w_t: float, events):
        """Commanded to move but not moving (pressed against something): report once per window."""
        moving_cmd = abs(v_t) > 0.1 or abs(w_t) > 0.2
        ref = getattr(self, "_stall_ref", None)
        if not moving_cmd or ref is None:
            self._stall_ref = (now, self.x, self.y, self.yaw)
            return
        t0, x0, y0, yaw0 = ref
        if math.dist((x0, y0), (self.x, self.y)) > cfg.STALL_DIST or abs(wrap(self.yaw - yaw0)) > 0.15:
            self._stall_ref = (now, self.x, self.y, self.yaw)
        elif now - t0 > cfg.STALL_S:
            self._stall_ref = (now, self.x, self.y, self.yaw)
            events.append(("stalled", ""))

    def _lookahead(self, pos):
        """Project pos onto the path (from the current segment on), advance path_i, and return the
        point LOOKAHEAD metres further along the path. Paths are sparse after line-of-sight
        shortcutting, so the lookahead must be interpolated, never taken from a far vertex."""
        best_k, best_t, best_d = self.path_i - 1, 0.0, math.inf
        for k in range(max(self.path_i - 1, 0), len(self.path) - 1):
            ax, ay = self.path[k]
            bx, by = self.path[k + 1]
            dx, dy = bx - ax, by - ay
            ln2 = dx * dx + dy * dy
            t = 0.0 if ln2 < 1e-9 else max(0.0, min(1.0, ((pos[0] - ax) * dx + (pos[1] - ay) * dy) / ln2))
            d = math.dist(pos, (ax + dx * t, ay + dy * t))
            if d < best_d - 1e-6:
                best_k, best_t, best_d = k, t, d
            if d > best_d + 2.0:
                break  # only search the nearby part of the path
        self.path_i = max(self.path_i, best_k + 1)
        left = cfg.LOOKAHEAD
        k, t = best_k, best_t
        while k < len(self.path) - 1:
            a, b = self.path[k], self.path[k + 1]
            seg = math.dist(a, b)
            rest = seg * (1.0 - t)
            if rest >= left and seg > 1e-9:
                f = t + left / seg
                return (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)
            left -= rest
            k, t = k + 1, 0.0
        return self.path[-1]

    def _pursue(self, now: float, events) -> tuple[float, float]:
        pos = (self.x, self.y)
        target = self._lookahead(pos)
        end = self.path[-1]
        d_end = math.dist(pos, end)
        is_loop = self.task is None
        if d_end < (0.6 if is_loop else cfg.GOAL_TOL):
            if is_loop:
                if not (self.want_plan or self.plan_pending):
                    self.loop_idx = (self.loop_idx + 1) % len(self.loop)
                    self.request_plan("loop")
                # keep rolling toward the old end point until the next segment arrives
                return min(self.v, 0.3), 0.0
            self.path = []
            self.state = "arrived"
            events.append(("arrived", f"goal:{self.task['seq']}"))
            return 0.0, 0.0
        err = wrap(math.atan2(target[1] - self.y, target[0] - self.x) - self.yaw)
        if abs(err) > math.radians(cfg.ROTATE_IN_PLACE_DEG):
            return 0.0, math.copysign(cfg.MAX_ANG * min(1.0, abs(err)), err)
        v = min(cfg.MAX_LIN, math.sqrt(2 * cfg.ACCEL_LIN * max(d_end - (0 if is_loop else cfg.GOAL_TOL * 0.5), 0.0)) + (0.3 if is_loop else 0.05))
        v *= max(0.35, math.cos(err))
        ld = max(math.dist(pos, target), 0.3)
        w = 2.0 * v * math.sin(err) / ld
        w = max(-cfg.MAX_ANG, min(cfg.MAX_ANG, w))
        return v, w
