"""Every tuning value for the AGV fleet demo, in one place.

Units: meters, seconds, radians (unless a name says _deg / _ms / _hz).
Frame: USD world of the root stage -- Z-up, right-handed, metersPerUnit = 1. The Isaac side never
converts axes; the single USD <-> Unity conversion lives in Unity (the sample's Runtime/UsdFrame.cs).
"""

import colorsys as _colorsys
import hashlib as _hashlib
import json as _json
import os as _os
from pathlib import Path

# ------------------------------------------------------------------------------------------------
# Paths
# ------------------------------------------------------------------------------------------------
def _find_unity_project(start: Path) -> Path:
    """The Unity project this copy of the tools was imported into: the first parent holding both
    Assets/ and ProjectSettings/. Outside a project (e.g. run from the package source), the
    current working directory."""
    for p in start.parents:
        if (p / "Assets").is_dir() and (p / "ProjectSettings").is_dir():
            return p
    return Path.cwd()


# The Unity project (parent of Assets/). AGV_FLEET_PROJECT overrides it.
PROJECT_ROOT = Path(_os.environ.get("AGV_FLEET_PROJECT") or _find_unity_project(Path(__file__).resolve()))
# Shared stage folder; Unity uses the same default (UsdSync/AGVFleet under the project), and both
# honour AGV_FLEET_STAGE_DIR.
STAGE_DIR = Path(_os.environ.get("AGV_FLEET_STAGE_DIR") or (PROJECT_ROOT / "UsdSync" / "AGVFleet"))
ROOT_LAYER = STAGE_DIR / "root.usda"            # sublayers + environment reference (static)
UNITY_LAYER = STAGE_DIR / "unity_ops.usda"      # written ONLY by Unity
ISAAC_LAYER = STAGE_DIR / "isaac_state.usda"    # written ONLY by Isaac Sim
LOG_DIR = STAGE_DIR / "logs"
GRID_CACHE = STAGE_DIR / "occupancy_cache.npz"

# Isaac Sim asset root. get_assets_root_path() is preferred at runtime (local pack or NVIDIA's
# cloud); ISAAC_ASSET_ROOT is the fallback when it is unavailable.
ASSET_ROOT_FALLBACK = _os.environ.get("ISAAC_ASSET_ROOT", "")
# AGV model (idealworks iw_hub, from the Isaac Sim assets -- never shipped with the sample).
AGV_USD_CANDIDATES = [
    "{assets}/Isaac/Robots/Idealworks/iwhub/iw_hub.usd",
]

# ------------------------------------------------------------------------------------------------
# Environment presets (select with --env, or AGV_FLEET_ENV; default ENV). Everything that depends on
# the environment lives here. "usd" may start with {assets} (the Isaac asset root).
#   scale         metersPerUnit of the environment file (root stage is 1 m): applied on /World/Environment
#   grid_source   "omap" = Isaac's collider-based generator (needs authored colliders);
#                 "projection" = mesh triangles in GRID_Z_BAND (for scenes without physics)
#   colliders     True = the scene has no colliders: Isaac adds a ground plane and invisible box
#                 colliders built from the occupancy grid, in ITS OWN layer (/World/EnvCollision);
#                 the environment itself is never edited
#   loops         per-AGV default waypoint loops (different areas), spawn = (x, y, yaw_deg)
# ------------------------------------------------------------------------------------------------
ENVIRONMENTS = {
    # NVIDIA USD Explorer sample "Factory" (car body shop). Lanes: west x -8..-4, east x 5..11,
    # central welding line + cell at x ~ 0, open halls north (y 17..25) and south (y -20..-30).
    "factory": {
        # NVIDIA "USD Explorer Sample Asset Pack", unzipped by the user (never shipped with the sample):
        # AGV_FLEET_FACTORY_USD, else the default folder next to the Unity project's Assets/.
        "usd": [_os.environ.get("AGV_FLEET_FACTORY_USD", ""),
                "{project}/ExternalAssets/USD_Explorer_Sample/Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd"],
        "scale": 0.01,
        "grid_source": "projection",
        "colliders": True,
        "grid_bounds": (-18.0, -31.0, 20.0, 31.0),
        "omap_origin": (2.0, 20.0, 0.0),
        "spawn": {"AGV_01": (-5.0, 18.5, 0.0), "AGV_02": (6.8, -18.0, 90.0), "AGV_03": (-7.2, -18.0, 90.0)},
        "loops": {
            "AGV_01": [(-5.0, 18.5), (8.0, 18.5), (8.0, 23.0), (-5.0, 23.0)],     # north hall
            "AGV_02": [(6.8, -18.0), (6.8, 8.0), (9.3, 8.0), (9.3, -18.0)],      # east lane
            "AGV_03": [(-7.2, -18.0), (-7.2, 8.0), (-5.6, 8.0), (-5.6, -18.0)],  # west lane
        },
    },
    # Isaac Sim 6.1 asset pack, Simple_Warehouse with three shelf rows (aisles ~7.7 m).
    "warehouse": {
        "usd": "{assets}/Isaac/Environments/Simple_Warehouse/warehouse_multiple_shelves.usd",
        "scale": 1.0,
        "grid_source": "omap",
        "colliders": False,
        "grid_bounds": (-11.0, -13.0, 10.5, 19.0),
        "omap_origin": (0.0, -8.0, 0.0),
        "spawn": {"AGV_01": (-6.0, -2.0, 90.0), "AGV_02": (3.5, -2.0, 90.0), "AGV_03": (-6.0, -9.0, 0.0)},
        "loops": {
            "AGV_01": [(-6.0, -2.0), (-6.0, 10.0), (-3.5, 10.0), (-3.5, -2.0)],  # west aisle
            "AGV_02": [(3.5, -2.0), (3.5, 10.0), (6.0, 10.0), (6.0, -2.0)],     # east aisle
            "AGV_03": [(-6.0, -9.0), (5.5, -9.0), (5.5, -6.5), (-6.0, -6.5)],   # south hall
        },
    },
}
# ------------------------------------------------------------------------------------------------
# The environment Unity runs: environment.json in the stage folder, written by the Unity sample on
# every start (factory or generated hall). When present it is the default preset, "from_unity", so
# Isaac always simulates what Unity shows. Fields (USD frame, meters, degrees):
#   identity, name, usd (absolute, or relative to the stage folder), scale, grid_source, colliders,
#   grid_bounds [xmin, ymin, xmax, ymax], free_seed [x, y], spawns [{id, x, y, yaw}],
#   loops [{id, points [x0, y0, x1, y1, ...]}], boxes [{cx, cy, sx, sy, yaw}] (exact obstacles)
# ------------------------------------------------------------------------------------------------
ENV_JSON = STAGE_DIR / "environment.json"


def load_unity_environment(path: Path = ENV_JSON):
    """The preset dict for environment.json, or None when there is none (or it is unreadable)."""
    try:
        d = _json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    usd = Path(d["usd"])
    if not usd.is_absolute():
        usd = (path.parent / usd).resolve()

    def pairs(flat):
        return [(float(flat[i]), float(flat[i + 1])) for i in range(0, len(flat) - 1, 2)]

    sx = d.get("free_seed", [0.0, 0.0])
    return {
        "usd": [usd.as_posix()],
        "scale": float(d.get("scale", 1.0)),
        "grid_source": d.get("grid_source", "projection"),
        "colliders": bool(d.get("colliders", True)),
        "grid_bounds": tuple(float(v) for v in d["grid_bounds"]),
        "omap_origin": (float(sx[0]), float(sx[1]), 0.0),
        "spawn": {s["id"]: (float(s["x"]), float(s["y"]), float(s["yaw"])) for s in d["spawns"]},
        "loops": {lp["id"]: pairs(lp["points"]) for lp in d["loops"]},
        "boxes": [(float(b["cx"]), float(b["cy"]), float(b["sx"]), float(b["sy"]), float(b["yaw"])) for b in d.get("boxes", [])],
        "identity": d.get("identity", ""),
        "label": d.get("name", "from_unity"),
    }


_unity_env = load_unity_environment()
if _unity_env:
    ENVIRONMENTS["from_unity"] = _unity_env
ENV = "from_unity" if _unity_env else "factory"
ENV_COLLIDER_HEIGHT = 1.8     # generated box colliders span z = 0 .. this (environments without physics)


def apply_env(name: str, asset_root: str = ASSET_ROOT_FALLBACK):
    """Make the chosen environment preset the active one (module-level names below)."""
    global ENV, ENV_USD, ENV_SCALE, GRID_SOURCE, ENV_COLLIDERS, GRID_BOUNDS, OMAP_ORIGIN, SPAWN, LOOPS, AGV_USD
    global AGV_IDS, AGV_COUNT, ENV_BOXES, ENV_IDENTITY
    e = ENVIRONMENTS[name]
    ENV = name

    def first_existing(cands):
        cands = [c.replace("{assets}", asset_root.rstrip("/")).replace("{project}", PROJECT_ROOT.as_posix()) for c in cands]
        return next((c for c in cands if _os.path.exists(c)), cands[-1])

    ENV_USD = first_existing(e["usd"] if isinstance(e["usd"], list) else [e["usd"]])
    AGV_USD = first_existing(AGV_USD_CANDIDATES)
    ENV_SCALE = e["scale"]
    GRID_SOURCE = e["grid_source"]
    ENV_COLLIDERS = e["colliders"]
    GRID_BOUNDS = e["grid_bounds"]
    OMAP_ORIGIN = e["omap_origin"]
    SPAWN = dict(e["spawn"])
    LOOPS = {k: list(v) for k, v in e["loops"].items()}
    ENV_BOXES = list(e.get("boxes", []))
    # identity of the content: Unity checks it against its own; it also keys the grid cache
    ENV_IDENTITY = e.get("identity") or f"{name}:{Path(ENV_USD).name}"
    # one AGV per spawn, in id order (index = priority, AGV_01 highest)
    AGV_IDS = sorted(SPAWN)
    AGV_COUNT = len(AGV_IDS)


def color_for(agv_id: str) -> tuple:
    """AGV colour; the same palette as the Unity sample's AgvPalette."""
    known = [(0.10, 0.55, 1.00), (1.00, 0.55, 0.05), (0.20, 0.80, 0.30)]
    try:
        i = int(agv_id.rsplit("_", 1)[1]) - 1
    except (IndexError, ValueError):
        return (0.8, 0.8, 0.8)
    if 0 <= i < len(known):
        return known[i]
    return _colorsys.hsv_to_rgb((0.13 + i * 0.31) % 1.0, 0.75, 0.95)


# ------------------------------------------------------------------------------------------------
# Fleet
# ------------------------------------------------------------------------------------------------
AGV_COUNT = 3                 # set by apply_env: one AGV per spawn of the environment (--agvs N takes the first N)
AGV_IDS = ["AGV_01", "AGV_02", "AGV_03"]   # set by apply_env; index = priority (AGV_01 highest)
ENV_BOXES: list = []          # exact obstacle boxes (grid_source "boxes")
ENV_IDENTITY = ""
SPAWN_Z = 0.09                # iw_hub wheel bottom is 0.081 below its origin

QUEUE_MAX = 3
GOAL_WAIT_S = 8.0             # wait at a goal before returning to the loop

# ------------------------------------------------------------------------------------------------
# Robot (idealworks iw_hub)
# ------------------------------------------------------------------------------------------------
WHEEL_JOINTS = ["left_wheel_joint", "right_wheel_joint"]
WHEEL_RADIUS = 0.081
WHEEL_BASE = 0.579
# Footprint in the robot frame (origin = drive axle midpoint, +X forward).
FOOTPRINT_X = (-1.033, 0.398)
FOOTPRINT_Y = (-0.330, 0.330)

MAX_LIN = 0.8                 # m/s
MAX_ANG = 0.9                 # rad/s
ACCEL_LIN = 0.6               # m/s^2 (speed ramp)
LOOKAHEAD = 0.9               # pure-pursuit lookahead
ROTATE_IN_PLACE_DEG = 35.0    # heading error above which the AGV turns on the spot (long tail: keep low)
GOAL_TOL = 0.25               # arrival tolerance
BACKUP_DIST = 1.0             # deadlock recovery reverse distance
BACKUP_SPEED = 0.3
STALL_S = 4.0                 # commanded to move but moved < STALL_DIST for this long -> back up + re-plan
STALL_DIST = 0.05

# ------------------------------------------------------------------------------------------------
# Occupancy grid / planner
# ------------------------------------------------------------------------------------------------
GRID_RES = 0.1                # m per cell (bounds + omap origin come from the environment preset)
GRID_Z_BAND = (0.05, 1.8)     # height band that counts as an obstacle
INFLATE_RADIUS = 0.75         # hard keep-out from static/obstacle cells (robot half-width + clearance)
SOFT_RADIUS = 1.6             # extra cost inside this clearance keeps AGVs centred in aisles
SOFT_WEIGHT = 4.0
OBSTACLE_MARGIN = 0.15        # safety margin added around every Unity obstacle footprint
AGV_MARGIN = 0.35             # margin around other AGVs' footprints when planning
DYNAMIC_UPDATE_S = 0.2        # other-AGV footprints refreshed at least this often
REPLAN_BLOCKED_S = 1.0        # a blocked AGV retries this often (in addition to on obstacle change)
SHORTER_ROUTE_GAIN = 0.85     # on obstacle removal, switch if new path < 85 % of the remaining one

# ------------------------------------------------------------------------------------------------
# Inter-AGV avoidance (starting points -- tune per environment)
# ------------------------------------------------------------------------------------------------
CONFLICT_DIST = 3.0           # pairs closer than this are checked for conflicting paths
CONFLICT_LOOKAHEAD = 3.0      # how much of a path ahead counts for a conflict
YIELD_REPLAN_S = 5.0          # lower-priority AGV re-plans around the other after waiting this long
DEADLOCK_S = 10.0             # mutual blocking longer than this triggers backup + re-plan

# ------------------------------------------------------------------------------------------------
# Sync / transport
# ------------------------------------------------------------------------------------------------
HOST = "127.0.0.1"
PORT = 10010
PUBLISH_HZ = 30.0
LAYER_POLL_S = 0.5            # fallback poll of unity_ops.usda mtime (the TCP notice is the fast path)
ISAAC_LAYER_SAVE_S = 1.0      # on-disk checkpoint of isaac_state.usda
PHYSICS_DT = 1.0 / 60.0

apply_env(_os.environ.get("AGV_FLEET_ENV", ENV))
