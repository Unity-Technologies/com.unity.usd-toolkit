"""Isaac-side event log for the latency chain (CSV, wall-clock ms).

Events (ref = obstacle prim name or goal seq):
  obstacle_seen      obstacle prim first visible on the Isaac stage
  obstacle_removed   obstacle prim gone from the Isaac stage
  goal_seen          new goal seq visible on the Isaac stage
  replan_started     re-plan triggered for an AGV (detail = cause)
  task_assigned      goal assigned to an AGV (or queued)
  moving_new_path    AGV first commands motion on the new path
  blocked / deadlock_recovery / queue_full
Unity logs its own side (created / placed / goal_set / pose lag) in unity_latency.csv; the two
are joined by Tools/agv_fleet/analyze_latency.py.
"""

from __future__ import annotations

import csv
import os
import time

from .transport import wall_ms


class EventLog:
    def __init__(self, log_dir, broadcast=None):
        os.makedirs(log_dir, exist_ok=True)
        stamp = time.strftime("%Y%m%d_%H%M%S")
        self.path = os.path.join(str(log_dir), f"isaac_events_{stamp}.csv")
        self._f = open(self.path, "w", newline="", encoding="utf-8")
        self._w = csv.writer(self._f)
        self._w.writerow(["wall_ms", "event", "agv", "ref", "detail"])
        self._broadcast = broadcast
        self._last_flush = time.monotonic()

    def log(self, event: str, agv: str = "", ref: str = "", detail: str = "", t_ms: float | None = None):
        t = wall_ms() if t_ms is None else t_ms
        self._w.writerow([f"{t:.1f}", event, agv, ref, detail])
        if event in ("deadlock_recovery", "blocked", "queue_full"):
            print(f"[agv_fleet] {event} agv={agv} ref={ref} {detail}")
        if self._broadcast:
            # Unity shows these in the debug latency panel and appends them to its own log.
            self._broadcast({"type": "event", "event": event, "agv": agv, "ref_id": ref, "detail": detail, "wall_ms": t})
        now = time.monotonic()
        if now - self._last_flush > 1.0:
            self._f.flush()
            self._last_flush = now

    def close(self):
        self._f.close()
