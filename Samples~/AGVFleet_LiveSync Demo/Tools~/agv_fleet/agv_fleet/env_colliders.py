"""Physics stand-ins for an environment that ships without colliders (e.g. the USD Explorer Factory).

Isaac authors, in ITS OWN layer and under its own prims (the environment stays untouched):
  /World/EnvCollision/Ground      static ground plane (thin box at z = 0)
  /World/EnvCollision/Box_####    invisible static boxes, z = 0 .. ENV_COLLIDER_HEIGHT, one per
                                  rectangle of merged occupied grid cells
So AGVs stand on the floor and physically cannot drive through racks or cells, matching exactly what
the planner sees in the occupancy grid.
"""

from __future__ import annotations

import numpy as np

import config as cfg


def merge_rects(occ: np.ndarray) -> list[tuple[int, int, int, int]]:
    """Occupied cells -> rectangles (r0, r1, c0, c1), half-open: row runs merged downwards."""
    ny, nx = occ.shape
    out, open_ = [], {}
    for r in range(ny + 1):
        runs = set()
        if r < ny:
            row = occ[r]
            c = 0
            while c < nx:
                if row[c]:
                    c0 = c
                    while c < nx and row[c]:
                        c += 1
                    runs.add((c0, c))
                else:
                    c += 1
        nxt = {}
        for k, r0 in open_.items():
            if k in runs:
                nxt[k] = r0
            else:
                out.append((r0, r, k[0], k[1]))
        for k in runs:
            nxt.setdefault(k, r)
        open_ = nxt
    return out


ROOT = "/World/EnvCollision"


def _cube(layer, path, cx, cy, cz, sx, sy, sz):
    """Invisible static box collider spec (Cube, size 1, translate + scale)."""
    from pxr import Sdf, Vt

    spec = Sdf.CreatePrimInLayer(layer, path)
    spec.specifier = Sdf.SpecifierDef
    spec.typeName = "Cube"
    spec.SetInfo("apiSchemas", Sdf.TokenListOp.Create(prependedItems=["PhysicsCollisionAPI"]))
    for name, tn, val in (
        ("size", Sdf.ValueTypeNames.Double, 1.0),
        ("xformOp:translate", Sdf.ValueTypeNames.Double3, (cx, cy, cz)),
        ("xformOp:scale", Sdf.ValueTypeNames.Float3, (sx, sy, sz)),
        ("visibility", Sdf.ValueTypeNames.Token, "invisible"),
        ("purpose", Sdf.ValueTypeNames.Token, "guide"),
    ):
        Sdf.AttributeSpec(spec, name, tn).default = val
    Sdf.AttributeSpec(spec, "xformOpOrder", Sdf.ValueTypeNames.TokenArray, Sdf.VariabilityUniform).default = Vt.TokenArray(
        ["xformOp:translate", "xformOp:scale"])


def _scope(layer):
    from pxr import Sdf

    scope = Sdf.CreatePrimInLayer(layer, ROOT)
    scope.specifier = Sdf.SpecifierDef
    scope.typeName = "Scope"


def build_ground(layer):
    """Static ground at z = 0 covering the grid bounds (+2 m). Call before physics starts."""
    from pxr import Sdf

    xmin, ymin, xmax, ymax = cfg.GRID_BOUNDS
    with Sdf.ChangeBlock():
        _scope(layer)
        _cube(layer, f"{ROOT}/Ground", (xmin + xmax) / 2, (ymin + ymax) / 2, -0.05, xmax - xmin + 4, ymax - ymin + 4, 0.1)


def build_boxes(layer, grid) -> int:
    """Invisible static boxes for every merged rectangle of grid.static. Returns the box count."""
    from pxr import Sdf

    h = cfg.ENV_COLLIDER_HEIGHT
    rects = merge_rects(grid.static)
    res = grid.res
    with Sdf.ChangeBlock():
        _scope(layer)
        for i, (r0, r1, c0, c1) in enumerate(rects):
            x0, x1 = grid.xmin + c0 * res, grid.xmin + c1 * res
            y0, y1 = grid.ymin + r0 * res, grid.ymin + r1 * res
            _cube(layer, f"{ROOT}/Box_{i:04d}", (x0 + x1) / 2, (y0 + y1) / 2, h / 2, x1 - x0, y1 - y0, h)
    return len(rects)
