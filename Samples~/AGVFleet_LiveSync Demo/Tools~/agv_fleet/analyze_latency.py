"""Latency report from the demo logs (stdlib only).

    python analyze_latency.py                      # newest logs in UsdSync/AGVFleet/logs
    python analyze_latency.py --isaac <csv> [--unity <csv>]

Isaac's event log alone gives the action chains (it records each obstacle's / goal's Unity
creation time); Unity's log adds the pose lag and the publish rate as seen by Unity.

Metrics (same machine, one wall clock, ms):
  pose lag        Unity applies pose - Isaac read it from physics   (unity_latency_*.csv)
  publish rate    state updates per second received by Unity
  obstacle seen   Isaac sees prim on stage - Unity wrote it
  obstacle replan first re-plan of an affected AGV - Unity wrote it
  goal moving     assigned AGV first moves on its path - Unity wrote the goal
"""

from __future__ import annotations

import argparse
import csv
import glob
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import config as cfg  # noqa: E402


def newest(pattern):
    files = sorted(glob.glob(os.path.join(str(cfg.LOG_DIR), pattern)), key=os.path.getmtime)
    return files[-1] if files else None


def stats(v):
    if not v:
        return "n=0"
    v = sorted(v)
    p95 = v[max(int(len(v) * 0.95) - 1, 0)]
    return f"median {statistics.median(v):7.1f}   p95 {p95:7.1f}   max {v[-1]:7.1f}   (n={len(v)})"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--isaac")
    ap.add_argument("--unity")
    args = ap.parse_args()
    isaac = args.isaac or newest("isaac_events_*.csv")
    unity = args.unity or newest("unity_latency_*.csv")
    print(f"isaac log: {isaac}\nunity log: {unity}\n")

    created, seen, replan, moving = {}, {}, {}, {}
    queued, blocked_by = set(), set()
    counts = {}
    if isaac:
        with open(isaac, encoding="utf-8") as fh:
            for row in csv.DictReader(fh):
                t, ev, ref = float(row["wall_ms"]), row["event"], row["ref"]
                counts[ev] = counts.get(ev, 0) + 1
                m = re.search(r"created_ms=([0-9.]+)", row["detail"])
                if ev in ("obstacle_seen", "goal_seen"):
                    if m and m.group(1) != "None":
                        created[ref] = float(m.group(1))
                    seen.setdefault(ref, t)
                elif ev in ("replan_started", "task_assigned"):
                    for r in ref.split(","):
                        replan.setdefault(r, t)
                elif ev == "moving_new_path":
                    for r in ref.split(","):
                        moving.setdefault(r, t)
                elif ev == "task_queued":
                    queued.add(ref)
                elif ev == "blocked":
                    blocked_by.update(ref.split(","))

    print("=== action chains (Isaac log) ===")
    for kind, pred in (("obstacle", lambda r: r.startswith("Obstacle_")), ("goal", lambda r: r.startswith("goal:"))):
        refs = [r for r in created if pred(r)]
        # Queued goals (every AGV busy) and obstacles that left an AGV "blocked" wait by design;
        # they are not latency and are reported separately.
        waits = queued if kind == "goal" else blocked_by
        timed = [r for r in refs if r not in waits]
        print(f"{kind}s: {len(refs)}  ({len(refs) - len(timed)} {'queued' if kind == 'goal' else 'caused a blocked AGV'}, excluded below)")
        print(f"  {kind} -> visible in Isaac   {stats([seen[r] - created[r] for r in refs if r in seen])}")
        print(f"  {kind} -> re-plan / assign   {stats([replan[r] - created[r] for r in timed if r in replan])}")
        print(f"  {kind} -> AGV moving         {stats([moving[r] - created[r] for r in timed if r in moving])}")
        if kind == "obstacle":
            print(f"  ({len([r for r in refs if r not in replan])} obstacles did not block any path -> no re-plan expected)")
    print("\nevent counts:", ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))

    if unity:
        lag, lag_rx, times = {}, {}, {}
        with open(unity, encoding="utf-8") as fh:
            for row in csv.DictReader(fh):
                if row["event"] != "pose_lag":
                    continue
                a = row["agv"]
                lag.setdefault(a, []).append(float(row["value_ms"]))
                times.setdefault(a, []).append(float(row["wall_ms"]))
                m = re.search(r"rx ([0-9.\-]+)", row["detail"])
                if m:
                    lag_rx.setdefault(a, []).append(float(m.group(1)))
        print("\n=== pose sync (Unity log) ===")
        for a in sorted(lag):
            t = times[a]
            rate = (len(t) - 1) / ((t[-1] - t[0]) / 1000.0) if len(t) > 1 else 0
            print(f"{a}: rate {rate:5.1f} Hz   applied lag {stats(lag[a])}")
            if a in lag_rx:
                print(f"        socket-arrival lag {stats(lag_rx[a])}")


if __name__ == "__main__":
    main()
