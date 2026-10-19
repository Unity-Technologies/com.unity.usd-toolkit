"""2D occupancy grid of the warehouse (built once, cached) plus live obstacle / AGV stamping.

Static layer:  "boxes": the exact obstacle boxes from environment.json (the generated hall);
               "omap": Isaac's collider-based generator (isaacsim.asset.gen.omap) while physics is
               playing; "projection" (or omap failing): mesh triangles in the height band.
               Cached to occupancy_<env>.npz keyed on env identity + settings.
Obstacle layer: Unity obstacle footprints (+ OBSTACLE_MARGIN), re-stamped whenever the set changes.
Planning:      hard keep-out = clearance < INFLATE_RADIUS (Euclidean distance transform);
               soft cost ramps up inside SOFT_RADIUS so paths stay in the middle of aisles.
"""

from __future__ import annotations

import hashlib
import math
import os
import time

import numpy as np
from scipy import ndimage

import config as cfg


class Grid:
    def __init__(self, static_occ: np.ndarray, xmin: float, ymin: float, res: float):
        self.static = static_occ.astype(bool)          # [row=y, col=x]
        self.ny, self.nx = self.static.shape
        self.xmin, self.ymin, self.res = xmin, ymin, res
        self.obstacles = np.zeros_like(self.static)
        self.obstacle_cells: dict[str, np.ndarray] = {}  # name -> bool mask (for clearing)
        self.static_clearance = ndimage.distance_transform_edt(~self.static) * res
        self.version = 0
        self._rebuild()

    # ---- coordinates ----------------------------------------------------------------------------
    def to_cell(self, x: float, y: float) -> tuple[int, int]:
        return int((y - self.ymin) / self.res), int((x - self.xmin) / self.res)

    def to_world(self, r: float, c: float) -> tuple[float, float]:
        return self.xmin + (c + 0.5) * self.res, self.ymin + (r + 0.5) * self.res

    def inside(self, r: int, c: int) -> bool:
        return 0 <= r < self.ny and 0 <= c < self.nx

    # ---- obstacles ------------------------------------------------------------------------------
    def polygon_mask(self, corners, margin: float) -> np.ndarray:
        """Cells inside a convex polygon grown by `margin` (corners in world XY, any winding)."""
        pts = np.asarray(corners, dtype=float)
        ctr = pts.mean(axis=0)
        # grow each corner away from the centre along the bisector -- exact for rectangles
        grown = []
        n = len(pts)
        for i in range(n):
            a, b, c = pts[i - 1], pts[i], pts[(i + 1) % n]
            e1 = (b - a) / (np.linalg.norm(b - a) + 1e-9)
            e2 = (c - b) / (np.linalg.norm(c - b) + 1e-9)
            n1 = np.array([e1[1], -e1[0]])
            n2 = np.array([e2[1], -e2[0]])
            if np.dot(n1, b - ctr) < 0:
                n1, n2 = -n1, -n2
            grown.append(b + (n1 + n2) * margin)
        pts = np.asarray(grown)
        lo = np.floor((pts.min(axis=0) - [self.xmin, self.ymin]) / self.res).astype(int) - 1
        hi = np.ceil((pts.max(axis=0) - [self.xmin, self.ymin]) / self.res).astype(int) + 1
        c0, r0 = max(lo[0], 0), max(lo[1], 0)
        c1, r1 = min(hi[0], self.nx), min(hi[1], self.ny)
        mask = np.zeros((self.ny, self.nx), dtype=bool)
        if c0 >= c1 or r0 >= r1:
            return mask
        cs, rs = np.meshgrid(np.arange(c0, c1), np.arange(r0, r1))
        px = self.xmin + (cs + 0.5) * self.res
        py = self.ymin + (rs + 0.5) * self.res
        inside = np.ones_like(px, dtype=bool)
        e0, e1 = pts[1] - pts[0], pts[2] - pts[1]
        sign = 1.0 if e0[0] * e1[1] - e0[1] * e1[0] > 0 else -1.0
        for i in range(len(pts)):
            a, b = pts[i], pts[(i + 1) % len(pts)]
            cross = (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0])
            inside &= cross * sign >= 0
        mask[r0:r1, c0:c1] = inside
        return mask

    def set_obstacles(self, obstacles: dict) -> tuple[set, set]:
        """Replace the obstacle set. Returns (added names, removed names)."""
        added = set(obstacles) - set(self.obstacle_cells)
        removed = set(self.obstacle_cells) - set(obstacles)
        for name in removed:
            del self.obstacle_cells[name]
        for name in obstacles:
            # re-stamp everything present: a moved obstacle keeps its name
            ob = obstacles[name]
            if ob["z"][0] > cfg.GRID_Z_BAND[1] or ob["z"][1] < cfg.GRID_Z_BAND[0]:
                self.obstacle_cells[name] = np.zeros_like(self.static)
            else:
                self.obstacle_cells[name] = self.polygon_mask(ob["corners"], cfg.OBSTACLE_MARGIN)
        self.obstacles = np.zeros_like(self.static)
        for m in self.obstacle_cells.values():
            self.obstacles |= m
        self._rebuild()
        return added, removed

    def _rebuild(self):
        blocked = self.static | self.obstacles
        self.clearance = ndimage.distance_transform_edt(~blocked) * self.res
        self.hard = self.clearance < cfg.INFLATE_RADIUS
        soft = np.clip(cfg.SOFT_RADIUS - self.clearance, 0.0, None) / max(cfg.SOFT_RADIUS - cfg.INFLATE_RADIUS, 1e-3)
        self.cost = (1.0 + cfg.SOFT_WEIGHT * soft).astype(np.float64)
        self.version += 1

    # ---- AGV footprints -------------------------------------------------------------------------
    @staticmethod
    def footprint_corners(x: float, y: float, yaw: float, grow: float = 0.0):
        cy, sy = math.cos(yaw), math.sin(yaw)
        out = []
        for fx, fy in ((cfg.FOOTPRINT_X[0] - grow, cfg.FOOTPRINT_Y[0] - grow), (cfg.FOOTPRINT_X[1] + grow, cfg.FOOTPRINT_Y[0] - grow),
                       (cfg.FOOTPRINT_X[1] + grow, cfg.FOOTPRINT_Y[1] + grow), (cfg.FOOTPRINT_X[0] - grow, cfg.FOOTPRINT_Y[1] + grow)):
            out.append((x + fx * cy - fy * sy, y + fx * sy + fy * cy))
        return out

    def agv_mask(self, x: float, y: float, yaw: float) -> np.ndarray:
        """Keep-out for another AGV: footprint + AGV_MARGIN + this AGV's own INFLATE_RADIUS."""
        return self.polygon_mask(self.footprint_corners(x, y, yaw), cfg.AGV_MARGIN + cfg.INFLATE_RADIUS)

    def is_free_world(self, x: float, y: float) -> bool:
        r, c = self.to_cell(x, y)
        return self.inside(r, c) and not self.hard[r, c]

    def save_png(self, path: str, extra=None):
        try:
            from PIL import Image
        except ImportError:
            return
        img = np.full((self.ny, self.nx, 3), 255, np.uint8)
        img[self.hard] = (255, 200, 200)
        img[self.static] = (0, 0, 0)
        img[self.obstacles] = (230, 120, 0)
        if extra:
            for (r, c), col in extra:
                if self.inside(r, c):
                    img[max(r - 2, 0):r + 3, max(c - 2, 0):c + 3] = col
        Image.fromarray(img[::-1]).save(path)


# ------------------------------------------------------------------------------------------------
# Static grid construction
# ------------------------------------------------------------------------------------------------
def _cache_key(env_path: str) -> str:
    s = (f"{env_path}|{cfg.ENV_SCALE}|{cfg.GRID_SOURCE}|{cfg.GRID_RES}|{cfg.GRID_BOUNDS}|{cfg.GRID_Z_BAND}|"
         f"{cfg.OMAP_ORIGIN}|{sorted(cfg.SPAWN.items())}|{cfg.ENV_IDENTITY}|{len(cfg.ENV_BOXES)}|v5")
    return hashlib.sha1(s.encode()).hexdigest()


def load_or_build(env_path: str, stage, app_update) -> tuple[Grid, str]:
    """Returns (grid, source). source in {'cache', 'omap', 'projection'}. Physics must be playing."""
    key = _cache_key(env_path)
    if os.path.exists(cfg.GRID_CACHE):
        d = np.load(cfg.GRID_CACHE, allow_pickle=False)
        if str(d["key"]) == key:
            return Grid(d["static"], float(d["xmin"]), float(d["ymin"]), float(d["res"])), "cache"

    t0 = time.perf_counter()
    static, source = None, ""
    if cfg.GRID_SOURCE == "boxes" and cfg.ENV_BOXES:
        static = _build_boxes()
        source = "boxes"
    elif cfg.GRID_SOURCE == "omap":
        try:
            static = _build_omap(stage, app_update)
            source = "omap"
        except Exception as e:  # noqa: BLE001 -- any failure falls back to projection
            print(f"[agv_fleet] omap generator unavailable ({e}); using USD projection")
    if static is None or not static.any():
        static = _build_projection(stage)
        source = "projection"
    xmin, ymin = cfg.GRID_BOUNDS[0], cfg.GRID_BOUNDS[1]
    # omap sees every collider, including the AGVs standing at their spawn poses: clear those
    # (for all configured AGVs, so the cache is valid for any --agvs count)
    probe = Grid(static, xmin, ymin, cfg.GRID_RES)
    for x, y, yaw_deg in cfg.SPAWN.values():
        static &= ~probe.polygon_mask(Grid.footprint_corners(x, y, math.radians(yaw_deg)), 0.3)
    static = _close_outside(static)
    np.savez_compressed(cfg.GRID_CACHE, static=static, xmin=xmin, ymin=ymin, res=cfg.GRID_RES, key=key)
    print(f"[agv_fleet] occupancy grid {static.shape[1]}x{static.shape[0]} from {source} in {time.perf_counter() - t0:.1f}s")
    return Grid(static, xmin, ymin, cfg.GRID_RES), source


def _shape():
    xmin, ymin, xmax, ymax = cfg.GRID_BOUNDS
    return int(round((ymax - ymin) / cfg.GRID_RES)), int(round((xmax - xmin) / cfg.GRID_RES))


def _build_omap(stage, app_update) -> np.ndarray:
    import omni.physx
    import omni.usd
    from isaacsim.asset.gen.omap.bindings import _omap

    for _ in range(5):
        app_update()
    gen = _omap.Generator(omni.physx.get_physx_interface(), omni.usd.get_context().get_stage_id())
    gen.update_settings(cfg.GRID_RES, 1.0, 0.0, 0.5)
    ox, oy, oz = cfg.OMAP_ORIGIN
    xmin, ymin, xmax, ymax = cfg.GRID_BOUNDS
    gen.set_transform((ox, oy, oz), (xmin - ox, ymin - oy, cfg.GRID_Z_BAND[0]), (xmax - ox, ymax - oy, cfg.GRID_Z_BAND[1]))
    gen.generate2d()
    pts = np.asarray([(p.x, p.y) if hasattr(p, "x") else (p[0], p[1]) for p in gen.get_occupied_positions()], dtype=float)
    ny, nx = _shape()
    occ = np.zeros((ny, nx), dtype=bool)
    if len(pts):
        c = ((pts[:, 0] - xmin) / cfg.GRID_RES).astype(int)
        r = ((pts[:, 1] - ymin) / cfg.GRID_RES).astype(int)
        ok = (r >= 0) & (r < ny) & (c >= 0) & (c < nx)
        occ[r[ok], c[ok]] = True
    return occ


def _build_boxes() -> np.ndarray:
    """Exact: every obstacle box of the environment description, rasterised (rotated rectangles)."""
    ny, nx = _shape()
    probe = Grid(np.zeros((ny, nx), dtype=bool), cfg.GRID_BOUNDS[0], cfg.GRID_BOUNDS[1], cfg.GRID_RES)
    occ = np.zeros((ny, nx), dtype=bool)
    for cx, cy, sx, sy, yaw_deg in cfg.ENV_BOXES:
        c, s = math.cos(math.radians(yaw_deg)), math.sin(math.radians(yaw_deg))
        corners = [(cx + c * dx - s * dy, cy + s * dx + c * dy)
                   for dx, dy in ((-sx / 2, -sy / 2), (sx / 2, -sy / 2), (sx / 2, sy / 2), (-sx / 2, sy / 2))]
        occ |= probe.polygon_mask(corners, 0.0)
    return occ


def _build_projection(stage) -> np.ndarray:
    """Conservative fallback: XY bounding boxes of every mesh triangle that overlaps the band."""
    from pxr import Usd, UsdGeom

    ny, nx = _shape()
    occ = np.zeros((ny, nx), dtype=bool)
    xmin, ymin = cfg.GRID_BOUNDS[0], cfg.GRID_BOUNDS[1]
    zlo, zhi = cfg.GRID_Z_BAND
    root = stage.GetPrimAtPath("/World/Environment")
    cache = UsdGeom.XformCache()
    for prim in Usd.PrimRange(root, Usd.TraverseInstanceProxies()):
        if not prim.IsA(UsdGeom.Mesh) or UsdGeom.Imageable(prim).ComputeVisibility() == UsdGeom.Tokens.invisible:
            continue
        mesh = UsdGeom.Mesh(prim)
        pts = mesh.GetPointsAttr().Get()
        counts = mesh.GetFaceVertexCountsAttr().Get()
        idx = mesh.GetFaceVertexIndicesAttr().Get()
        if not pts or not counts or not idx:
            continue
        m = np.asarray(cache.GetLocalToWorldTransform(prim), dtype=float)
        p = np.asarray(pts, dtype=float)
        p = p @ m[:3, :3] + m[3, :3]
        idx = np.asarray(idx)
        counts = np.asarray(counts)
        starts = np.concatenate([[0], np.cumsum(counts)[:-1]])
        for k in (3, 4):
            sel = counts == k
            if not sel.any():
                continue
            faces = idx[starts[sel][:, None] + np.arange(k)]
            fp = p[faces]
            fz0, fz1 = fp[..., 2].min(1), fp[..., 2].max(1)
            keep = (fz1 >= zlo) & (fz0 <= zhi)
            fp = fp[keep]
            if not len(fp):
                continue
            c0 = np.clip(((fp[..., 0].min(1) - xmin) / cfg.GRID_RES).astype(int), 0, nx - 1)
            c1 = np.clip(((fp[..., 0].max(1) - xmin) / cfg.GRID_RES).astype(int), 0, nx - 1)
            r0 = np.clip(((fp[..., 1].min(1) - ymin) / cfg.GRID_RES).astype(int), 0, ny - 1)
            r1 = np.clip(((fp[..., 1].max(1) - ymin) / cfg.GRID_RES).astype(int), 0, ny - 1)
            # bbox rasterisation is conservative; a huge slanted triangle (roof panel dipping into
            # the band) would block a whole hall -- skip those (> 400 m2 footprint)
            small = (c1 - c0) * (r1 - r0) * cfg.GRID_RES ** 2 <= 400.0
            for a, b, c, d in zip(r0[small], r1[small], c0[small], c1[small]):
                occ[a:b + 1, c:d + 1] = True
    return occ


def _close_outside(occ: np.ndarray) -> np.ndarray:
    """Everything not connected to the omap origin's free region counts as occupied."""
    labels, _ = ndimage.label(~occ)
    xmin, ymin = cfg.GRID_BOUNDS[0], cfg.GRID_BOUNDS[1]
    r = int((cfg.OMAP_ORIGIN[1] - ymin) / cfg.GRID_RES)
    c = int((cfg.OMAP_ORIGIN[0] - xmin) / cfg.GRID_RES)
    keep = labels[r, c]
    if keep == 0:
        return occ
    return labels != keep
