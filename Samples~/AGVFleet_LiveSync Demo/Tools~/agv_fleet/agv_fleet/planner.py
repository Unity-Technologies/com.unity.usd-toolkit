"""Grid path planning: Dijkstra (scipy, C) over an 8-connected sparse graph of free cells.

A single search from the *goal* gives the exact path length from every cell, so the fleet manager
gets planned-path distances for all candidate AGVs from one call, and each AGV's path is read off
the predecessor tree. Edge cost = step length x mean soft cost of the two cells; diagonals may not
cut blocked corners. Paths are then shortened with line-of-sight checks on the hard keep-out.
"""

from __future__ import annotations

import math

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import dijkstra

SQRT2 = math.sqrt(2.0)


class PlanResult:
    __slots__ = ("ok", "points", "length", "reason", "ms")

    def __init__(self, ok, points=None, length=math.inf, reason="", ms=0.0):
        self.ok, self.points, self.length, self.reason, self.ms = ok, points or [], length, reason, ms


def build_graph(blocked: np.ndarray, cost: np.ndarray) -> csr_matrix:
    ny, nx = blocked.shape
    free = ~blocked
    idx = np.arange(ny * nx).reshape(ny, nx)
    rows, cols, w = [], [], []
    # (dr, dc, length) -- each undirected edge listed once; the search is undirected
    for dr, dc, ln in ((0, 1, 1.0), (1, 0, 1.0), (1, 1, SQRT2), (1, -1, SQRT2)):
        r0, r1 = 0, ny - dr
        c0, c1 = max(0, -dc), nx - max(0, dc)
        a = free[r0:r1, c0:c1] & free[r0 + dr:r1 + dr, c0 + dc:c1 + dc]
        if dr and dc:  # no corner cutting
            a &= free[r0 + dr:r1 + dr, c0:c1] & free[r0:r1, c0 + dc:c1 + dc]
        ia = idx[r0:r1, c0:c1][a]
        ib = idx[r0 + dr:r1 + dr, c0 + dc:c1 + dc][a]
        ca = cost[r0:r1, c0:c1][a]
        cb = cost[r0 + dr:r1 + dr, c0 + dc:c1 + dc][a]
        rows.append(ia)
        cols.append(ib)
        w.append(ln * 0.5 * (ca + cb))
    rows = np.concatenate(rows)
    cols = np.concatenate(cols)
    w = np.concatenate(w)
    return csr_matrix((w, (rows, cols)), shape=(ny * nx, ny * nx))


def nearest_free(blocked: np.ndarray, r: int, c: int, max_cells: int):
    """Closest non-blocked cell to (r, c) within max_cells, or None."""
    ny, nx = blocked.shape
    if 0 <= r < ny and 0 <= c < nx and not blocked[r, c]:
        return r, c
    r0, r1 = max(r - max_cells, 0), min(r + max_cells + 1, ny)
    c0, c1 = max(c - max_cells, 0), min(c + max_cells + 1, nx)
    win = ~blocked[r0:r1, c0:c1]
    if not win.any():
        return None
    rr, cc = np.nonzero(win)
    d = (rr + r0 - r) ** 2 + (cc + c0 - c) ** 2
    k = int(np.argmin(d))
    if d[k] > max_cells * max_cells:
        return None
    return int(rr[k] + r0), int(cc[k] + c0)


class GoalField:
    """Distance field from one goal over one blocked/cost map."""

    def __init__(self, grid, blocked: np.ndarray, goal_xy, goal_snap_m: float = 0.6):
        self.grid = grid
        self.blocked = blocked
        g = nearest_free(blocked, *grid.to_cell(*goal_xy), max_cells=int(goal_snap_m / grid.res))
        self.ok = g is not None
        if not self.ok:
            return
        self.goal_cell = g
        graph = build_graph(blocked, grid.cost)
        src = g[0] * grid.nx + g[1]
        self.dist, self.pred = dijkstra(graph, directed=False, indices=src, return_predecessors=True)
        self.dist = self.dist * grid.res

    def length_from(self, xy, start_snap_m: float = 1.5) -> float:
        if not self.ok:
            return math.inf
        s = nearest_free(self.blocked, *self.grid.to_cell(*xy), max_cells=int(start_snap_m / self.grid.res))
        if s is None:
            return math.inf
        return float(self.dist[s[0] * self.grid.nx + s[1]])

    def path_from(self, xy, goal_xy, start_snap_m: float = 1.5) -> PlanResult:
        grid = self.grid
        if not self.ok:
            return PlanResult(False, reason="goal blocked")
        s = nearest_free(self.blocked, *grid.to_cell(*xy), max_cells=int(start_snap_m / grid.res))
        if s is None:
            return PlanResult(False, reason="start blocked")
        node = s[0] * grid.nx + s[1]
        if not np.isfinite(self.dist[node]):
            return PlanResult(False, reason="no path")
        cells = [s]
        goal = self.goal_cell[0] * grid.nx + self.goal_cell[1]
        guard = grid.nx * grid.ny
        while node != goal and guard > 0:
            node = int(self.pred[node])
            if node < 0:
                return PlanResult(False, reason="broken tree")
            cells.append(divmod(node, grid.nx))
            guard -= 1
        pts = [tuple(xy)] + [grid.to_world(r, c) for r, c in shortcut(self.blocked, cells)[1:]]
        gr, gc = grid.to_cell(*goal_xy)
        if grid.inside(gr, gc) and not self.blocked[gr, gc]:
            pts[-1] = tuple(goal_xy)  # end exactly on the goal, not on its cell centre
        length = sum(math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1))
        return PlanResult(True, pts, length)


def plan(grid, blocked: np.ndarray, start_xy, goal_xy) -> PlanResult:
    import time

    t0 = time.perf_counter()
    res = GoalField(grid, blocked, goal_xy).path_from(start_xy, goal_xy)
    res.ms = (time.perf_counter() - t0) * 1000.0
    return res


def _line_free(blocked: np.ndarray, a, b) -> bool:
    n = int(max(abs(b[0] - a[0]), abs(b[1] - a[1]))) * 2 + 1
    rs = np.rint(np.linspace(a[0], b[0], n)).astype(int)
    cs = np.rint(np.linspace(a[1], b[1], n)).astype(int)
    return not blocked[rs, cs].any()


def shortcut(blocked: np.ndarray, cells: list) -> list:
    """Greedy line-of-sight pruning of a cell path."""
    if len(cells) <= 2:
        return cells
    out = [cells[0]]
    i = 0
    while i < len(cells) - 1:
        j = len(cells) - 1
        while j > i + 1 and not _line_free(blocked, cells[i], cells[j]):
            # binary-ish backoff keeps this fast on long paths
            j = i + 1 + (j - i - 1) // 2 if j - i > 16 else j - 1
        out.append(cells[j])
        i = j
    return out
