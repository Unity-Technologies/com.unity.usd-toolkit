"""AGV fleet demo -- Isaac Sim side. One script: stage, sync, occupancy grid, AGVs, fleet manager.

Run with Isaac Sim's interpreter (see run_agv_fleet.bat):
    python.bat launch_agv_fleet.py [--agvs 1..3] [--headless] [--mode full|sync]

--mode sync   build step 1 only: AGV_01 drives a fixed circle, no planner (pose sync + latency test)
"""

from __future__ import annotations

import argparse
import math
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import config as cfg  # noqa: E402  (stdlib-only module, safe before Kit boots)

parser = argparse.ArgumentParser()
parser.add_argument("--agvs", type=int, default=0, help="use only the first N AGVs of the environment (0 = all)")
parser.add_argument("--headless", action="store_true")
parser.add_argument("--mode", choices=["full", "sync"], default="full")
parser.add_argument("--env", choices=sorted(cfg.ENVIRONMENTS), default=cfg.ENV, help="environment preset (config.ENVIRONMENTS)")
parser.add_argument("--seconds", type=float, default=0.0, help="quit after this many wall seconds (tests)")
args, _unknown = parser.parse_known_args()

from isaacsim import SimulationApp  # noqa: E402

simulation_app = SimulationApp({"headless": args.headless, "width": 1600, "height": 900, "anti_aliasing": 3})

# ---- everything below needs Kit / pxr ------------------------------------------------------------
import numpy as np  # noqa: E402
import omni.kit.app  # noqa: E402
import omni.timeline  # noqa: E402
import omni.usd  # noqa: E402
from pxr import Gf, Sdf, UsdGeom, UsdLux, UsdPhysics  # noqa: E402

omni.kit.app.get_app().get_extension_manager().set_extension_enabled_immediate("isaacsim.asset.gen.omap", True)

import isaacsim.core.experimental.utils.app as app_utils  # noqa: E402
from isaacsim.core.experimental.prims import Articulation  # noqa: E402
from isaacsim.core.experimental.utils import stage as stage_utils  # noqa: E402
from isaacsim.core.simulation_manager import SimulationManager  # noqa: E402
from isaacsim.robot.experimental.wheeled_robots.controllers import DifferentialController  # noqa: E402

from agv_fleet import env_colliders  # noqa: E402
from agv_fleet.fleet import Fleet  # noqa: E402
from agv_fleet.latency import EventLog  # noqa: E402
from agv_fleet.layers import IsaacStateWriter, UnityLayerReader, reset_isaac_layer, write_root_layer  # noqa: E402
from agv_fleet.occupancy import load_or_build  # noqa: E402
from agv_fleet.transport import HubServer, server_token, wall_ms  # noqa: E402


def say(msg: str):
    print(f"[agv_fleet] {msg}", flush=True)


def asset_root() -> str:
    try:
        from isaacsim.storage.native import get_assets_root_path

        r = get_assets_root_path()
        if r:
            return r.replace("\\", "/")
    except Exception as e:  # noqa: BLE001
        say(f"get_assets_root_path failed: {e}")
    return cfg.ASSET_ROOT_FALLBACK


def find_layer(stage, path) -> Sdf.Layer:
    want = os.path.normcase(os.path.realpath(str(path)))
    for layer in stage.GetLayerStack(includeSessionLayers=False):
        if layer.realPath and os.path.normcase(os.path.realpath(layer.realPath)) == want:
            return layer
    raise RuntimeError(f"layer not in stage: {path}")


def yaw_of(q) -> float:  # q = (w, x, y, z)
    w, x, y, z = q
    return math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))


def main():
    t_boot = time.perf_counter()
    root = asset_root()
    if not root.startswith(("http", "omniverse")):
        say(f"asset root (local): {root}")
    else:
        say(f"WARNING asset root is remote: {root}")

    # ---- stage: root (static) + unity layer (read-only here) + isaac layer (edit target) ----------
    cfg.apply_env(args.env, root)
    agv_ids = cfg.AGV_IDS[: args.agvs] if args.agvs > 0 else list(cfg.AGV_IDS)
    cfg.GRID_CACHE = cfg.STAGE_DIR / f"occupancy_{cfg.ENV}.npz"
    say(f"environment: {cfg.ENV} ({cfg.ENV_IDENTITY})  {cfg.ENV_USD}  (scale {cfg.ENV_SCALE:g}, grid {cfg.GRID_SOURCE})")
    if not os.path.exists(cfg.ENV_USD):
        say(f"ERROR environment file not found: {cfg.ENV_USD}")
        say("For the generated hall, press Play in Unity once (it writes the file); for the factory, unzip the "
            "USD Explorer Sample Asset Pack where the sample's README says.")
        simulation_app.close()
        return
    say(f"AGV model: {cfg.AGV_USD}")
    from agv_fleet import anim_layer, look_layer

    look = look_layer.generate()      # UsdPreviewSurface translations of the MDL materials (for Unity)
    write_root_layer(look, anim_layer.env_time_range())   # range: Isaac's timeline plays the env animation
    anim_layer.generate()             # environment animation frozen + keyframes, root_unity.usda (for Unity)
    reset_isaac_layer()
    ctx = omni.usd.get_context()
    ctx.open_stage(str(cfg.ROOT_LAYER))
    while ctx.get_stage_loading_status()[2] > 0:
        simulation_app.update()
    stage = ctx.get_stage()
    isaac_layer = find_layer(stage, cfg.ISAAC_LAYER)
    stage.SetEditTarget(isaac_layer)
    say(f"stage open: {cfg.ROOT_LAYER}  edit target: {isaac_layer.identifier}")

    UsdPhysics.Scene.Define(stage, "/World/PhysicsScene").CreateGravityMagnitudeAttr(9.81)
    dome = UsdLux.DomeLight.Define(stage, "/World/Lights/Dome")
    dome.CreateIntensityAttr(800.0)
    if cfg.ENV_COLLIDERS:
        env_colliders.build_ground(isaac_layer)   # the environment has no physics: AGVs need a floor

    # ---- AGVs -----------------------------------------------------------------------------------
    paths, positions, orientations = [], [], []
    for aid in agv_ids:
        p = f"/World/AGVs/{aid}"
        stage_utils.add_reference_to_stage(usd_path=cfg.AGV_USD, path=p)
        x, y, yaw_deg = cfg.SPAWN[aid]
        h = math.radians(yaw_deg) * 0.5
        paths.append(p)
        positions.append([x, y, cfg.SPAWN_Z])
        orientations.append([math.cos(h), 0.0, 0.0, math.sin(h)])
        prim = stage.GetPrimAtPath(p)
        prim.CreateAttribute("agv:color", Sdf.ValueTypeNames.Color3f).Set(Gf.Vec3f(*cfg.color_for(aid)))
    robots = Articulation(paths, positions=np.array(positions), orientations=np.array(orientations), reset_xform_op_properties=True)
    diff = DifferentialController(wheel_radius=cfg.WHEEL_RADIUS, wheel_base=cfg.WHEEL_BASE,
                                  max_linear_speed=cfg.MAX_LIN, max_angular_speed=cfg.MAX_ANG)

    SimulationManager.setup_simulation(dt=cfg.PHYSICS_DT, device="cpu")
    SimulationManager.get_physics_scenes()[0].set_enabled_gpu_dynamics(False)
    # Let the stage settle before the timeline starts: Isaac 6.1 RC intermittently crashed inside
    # omni.graph.core on the first updates after play() when it came right after the stage open.
    for _ in range(30):
        simulation_app.update()
    while ctx.get_stage_loading_status()[2] > 0:
        simulation_app.update()
    app_utils.play()
    app_utils.update_app(steps=10)
    timeline = omni.timeline.get_timeline_interface()   # plays the environment's own animation too
    wheel_idx = robots.get_dof_indices(cfg.WHEEL_JOINTS).numpy().tolist()

    # ---- sync, grid, fleet ----------------------------------------------------------------------
    hub = HubServer(cfg.HOST, cfg.PORT, server_token(cfg.STAGE_DIR))
    hub.start()
    log = EventLog(cfg.LOG_DIR, broadcast=hub.broadcast)
    writer = IsaacStateWriter(stage, isaac_layer)
    reader = UnityLayerReader(stage)

    grid = None
    if args.mode == "full":
        grid, src = load_or_build(cfg.ENV_USD, stage, simulation_app.update)
        say(f"occupancy grid ready ({src}), {grid.nx}x{grid.ny} cells @ {grid.res} m")
        if cfg.ENV_COLLIDERS:
            n = env_colliders.build_boxes(isaac_layer, grid)
            say(f"environment colliders: ground + {n} boxes under {env_colliders.ROOT} (isaac layer)")
        for aid in agv_ids:
            bad = [wp for wp in cfg.LOOPS[aid] if not grid.is_free_world(*wp)]
            if bad:
                say(f"WARNING {aid} loop waypoints inside keep-out: {bad}")
        grid.save_png(os.path.join(str(cfg.LOG_DIR), "occupancy.png"),
                      extra=[(grid.to_cell(*wp), (40, 40, 220)) for aid in agv_ids for wp in cfg.LOOPS[aid]])
    fleet = Fleet(grid, agv_ids, log) if grid is not None else None
    if fleet:
        for a in fleet.agvs:
            a.back_to_loop()
            a.state = "looping"

    # Unity's layer as it is at startup is a baseline: obstacles count, an old goal does not.
    reader.reload()
    last_goal = reader.read_goal()
    last_goal_seq = last_goal["seq"] if last_goal else -1
    last_reset_seq = reader.read_reset_seq()
    last_obstacles: dict = {}
    collider_checks: list = []   # (due, name, x, y, ztop): confirm PhysX has a collider for each obstacle

    def check_colliders(now_mono: float):
        import omni.physx

        due = [c for c in collider_checks if c[0] <= now_mono]
        if not due:
            return
        sq = omni.physx.get_physx_scene_query_interface()
        for c in due:
            collider_checks.remove(c)
            _, name, x, y, ztop = c
            if name not in last_obstacles:
                continue
            hit = sq.raycast_closest((x, y, ztop + 0.5), (0.0, 0.0, -1.0), 1.0)
            what = str(hit.get("collision", "")) if hit and hit.get("hit") else ""
            if name in what:
                log.log("collider_ok", "", name, what)
            elif "/World/Dynamic/Obstacles/" in what:
                log.log("collider_unverified", "", name, f"ray hit {what} stacked above it")
            else:
                log.log("collider_missing", "", name, what)

    last_good = {aid: (cfg.SPAWN[aid][0], cfg.SPAWN[aid][1], math.radians(cfg.SPAWN[aid][2])) for aid in agv_ids}

    def relocate(i: int, aid: str, reason: str):
        """Teleport one AGV to the nearest free cell near its last good pose and zero its motion.
        Used when an obstacle appears on top of it (a static collider inside a moving articulation
        makes PhysX explode) and when its physics pose has gone non-finite anyway."""
        from agv_fleet.planner import nearest_free

        x, y, yaw = last_good[aid]
        cell = nearest_free(grid.hard, *grid.to_cell(x, y), max_cells=60) if grid is not None else None
        if cell is not None:
            x, y = grid.to_world(*cell)
        h = yaw * 0.5
        idx = [i]
        robots.set_world_poses(positions=np.array([[x, y, cfg.SPAWN_Z]]), orientations=np.array([[math.cos(h), 0.0, 0.0, math.sin(h)]]), indices=idx)
        robots.set_velocities(np.zeros((1, 3), dtype=np.float32), np.zeros((1, 3), dtype=np.float32), indices=idx)
        robots.set_dof_velocities(np.zeros((1, robots.num_dofs), dtype=np.float32), indices=idx)
        robots.set_dof_positions(np.zeros((1, robots.num_dofs), dtype=np.float32), indices=idx)
        last_good[aid] = (x, y, yaw)
        if fleet:
            a = fleet.by_id[aid]
            a.x, a.y, a.backup, a.v, a.w = x, y, None, 0.0, 0.0
            a.request_plan("relocated", reason)
        log.log("agv_relocated", aid, reason, f"to ({x:.2f},{y:.2f})")

    def obstacles_on_agvs(names, obs) -> list:
        hits = []
        for n in names:
            om = grid.polygon_mask(obs[n]["corners"], 0.1)
            for i, aid in enumerate(agv_ids):
                x, y, yaw = last_good[aid]
                if (om & grid.polygon_mask(grid.footprint_corners(x, y, yaw), 0.1)).any():
                    hits.append((i, aid, n))
        return hits

    def process_unity_layer(trigger: str, rx_ms: float | None):
        nonlocal last_goal_seq, last_reset_seq, last_obstacles
        t0 = wall_ms()
        if not reader.reload():
            return
        obs = reader.read_obstacles()
        changed = {n for n in obs if n in last_obstacles and obs[n]["corners"] != last_obstacles[n]["corners"]}
        added = set(obs) - set(last_obstacles)
        removed = set(last_obstacles) - set(obs)
        for n in sorted(added):
            log.log("obstacle_seen", "", n, f"{trigger} created_ms={obs[n]['created_ms']}", t_ms=t0)
            cx = sum(c[0] for c in obs[n]["corners"]) / 4
            cy = sum(c[1] for c in obs[n]["corners"]) / 4
            collider_checks.append((time.monotonic() + 0.5, n, cx, cy, obs[n]["z"][1]))
        for n in sorted(removed):
            log.log("obstacle_removed", "", n, trigger, t_ms=t0)
        if (added or removed or changed) and grid is not None:
            grid.set_obstacles(obs)
            for i, aid, n in obstacles_on_agvs(added | changed, obs):
                relocate(i, aid, n)   # before the next physics step creates the collider
            fleet.on_obstacles_changed(added | changed, removed, time.monotonic())
        last_obstacles = obs
        reset_seq = reader.read_reset_seq()
        if reset_seq != last_reset_seq:
            last_reset_seq = reset_seq
            log.log("reset", "", str(reset_seq), trigger)
            if fleet:
                fleet.reset()
        goal = reader.read_goal()
        if goal and goal["seq"] != last_goal_seq:
            last_goal_seq = goal["seq"]
            if goal["created_ms"] and t0 - goal["created_ms"] > 30000:
                # a goal is a one-shot command: one written long ago (e.g. re-asserted by a
                # restarted Unity) must not send an AGV anywhere now
                log.log("goal_stale", goal["target"], f"goal:{goal['seq']}", f"{(t0 - goal['created_ms']) / 1000:.0f}s old, ignored")
                return
            log.log("goal_seen", goal["target"], f"goal:{goal['seq']}", f"{trigger} created_ms={goal['created_ms']}", t_ms=t0)
            if fleet:
                fleet.on_goal(goal, time.monotonic())

    process_unity_layer("startup", None)

    # ---- main loop ------------------------------------------------------------------------------
    say(f"running: {len(agv_ids)} AGV(s), mode={args.mode}, boot {time.perf_counter() - t_boot:.1f}s")
    seq = 0
    pub_period = 1.0 / cfg.PUBLISH_HZ
    next_pub = time.monotonic()
    last_poll = last_save = last_fleet_pub = time.monotonic()
    last_sim_t = SimulationManager.get_simulation_time()
    path_sig: dict = {}
    t_end = time.monotonic() + args.seconds if args.seconds > 0 else None
    stats = {"frames": 0, "pubs": 0, "t0": time.monotonic()}

    # Wire format is flat on purpose: Unity parses it with JsonUtility (no dicts / nested arrays).
    def path_msg(a):
        return {"type": "path", "agv": a.id, "ver": a.path_ver, "pts": [round(v, 3) for p in a.path for v in p]}

    def fleet_msg():
        return {"type": "fleet", "assign": [{"agv": k, "task": v} for k, v in fleet.assignments().items()],
                "queue": fleet.queue_view(), "recoveries": fleet.deadlock_recoveries,
                "stalls": fleet.stall_recoveries}

    def snapshot_msgs():
        msgs = [{"type": "hello", "agv_ids": agv_ids, "colors": [c for a in agv_ids for c in cfg.color_for(a)],
                 "env_identity": cfg.ENV_IDENTITY,
                 "agv_usd": cfg.AGV_USD, "root_layer": str(cfg.ROOT_LAYER), "unity_layer": str(cfg.UNITY_LAYER),
                 "mode": args.mode, "env": cfg.ENV, "publish_hz": cfg.PUBLISH_HZ, "wall_ms": wall_ms()}]
        if fleet:
            for a in fleet.agvs:
                msgs.append(path_msg(a))
            msgs.append(fleet_msg())
        return msgs

    while simulation_app.is_running():
        simulation_app.update()
        now = time.monotonic()
        stats["frames"] += 1

        # inbound: Unity commands + layer-change notices
        for kind, client, msg in hub.poll():
            if kind == "connect":
                for m in snapshot_msgs():
                    hub.send(client, m)
            elif kind == "msg":
                cmd = msg.get("cmd")
                if cmd == "layer_changed":
                    process_unity_layer("tcp", msg.get("_rx_ms"))
                    last_poll = now
                elif cmd == "reset" and fleet:
                    fleet.reset()
                    log.log("reset", "", "tcp", "")
                elif cmd == "ping":
                    hub.send(client, {"type": "pong", "t": msg.get("t"), "wall_ms": wall_ms()})
        if now - last_poll >= cfg.LAYER_POLL_S:
            last_poll = now
            if reader.changed_on_disk():
                process_unity_layer("poll", None)

        if not app_utils.is_playing():
            continue
        if collider_checks:
            check_colliders(now)
        sim_t = SimulationManager.get_simulation_time()
        dt = sim_t - last_sim_t
        last_sim_t = sim_t

        # poses from physics
        pos, ori = robots.get_world_poses()
        pos, ori = pos.numpy(), ori.numpy()
        wheels = robots.get_dof_positions(dof_indices=wheel_idx).numpy()
        for i, aid in enumerate(agv_ids):
            if np.isfinite(pos[i]).all() and np.isfinite(ori[i]).all() and abs(pos[i][2]) < 2.0:
                last_good[aid] = (float(pos[i][0]), float(pos[i][1]), yaw_of(ori[i]))
            else:
                # physics blew up for this AGV: put it back where it last was valid, publish that
                relocate(i, aid, "non-finite physics pose")
                x, y, yaw = last_good[aid]
                pos[i] = (x, y, cfg.SPAWN_Z)
                ori[i] = (math.cos(yaw / 2), 0.0, 0.0, math.sin(yaw / 2))
                wheels[i] = (0.0, 0.0)
        poses = {aid: (float(pos[i][0]), float(pos[i][1]), yaw_of(ori[i])) for i, aid in enumerate(agv_ids)}
        wheel_map = {aid: (float(wheels[i][0]), float(wheels[i][1])) for i, aid in enumerate(agv_ids)}

        # control
        cmds = np.zeros((len(agv_ids), 2), dtype=np.float32)
        if fleet and dt > 0:
            fleet.update_poses(poses, wheel_map)
            vw = fleet.tick(now, dt)
            for i, aid in enumerate(agv_ids):
                cmds[i] = diff.forward(np.array(vw[aid]))
        elif args.mode == "sync" and dt > 0:
            # step-1 test: steady clockwise 1.5 m circle in the open floor east of AGV_01's spawn
            cmds[0] = diff.forward(np.array([0.45, -0.3]))
        robots.set_dof_velocity_targets(cmds, dof_indices=wheel_idx)

        # USD: AGV state every frame (isaac layer only)
        wall = wall_ms()
        rows = []
        for i, aid in enumerate(agv_ids):
            a = fleet.by_id[aid] if fleet else None
            rows.append({"id": aid, "pos": tuple(float(v) for v in pos[i]), "yaw": poses[aid][2], "wheels": wheel_map[aid],
                         "nav": a.state if a else "looping", "detail": a.detail if a else "",
                         "task": (f"goal:{a.task['seq']}" if a and a.task else (f"loop:{a.loop_idx}" if a else "circle")),
                         "sim_t": sim_t, "wall_ms": wall, "q": [float(v) for v in ori[i]],
                         "pi": a.path_i if a else 0})
        writer.write_agvs(rows)

        # paths: USD BasisCurves + stream, on change only
        if fleet:
            for a in fleet.agvs:
                sig = (a.path_ver, bool(a.path))
                if path_sig.get(a.id) != sig:
                    path_sig[a.id] = sig
                    writer.write_path(a.id, a.path, a.color)
                    hub.broadcast(path_msg(a))
            if fleet.fleet_dirty or now - last_fleet_pub > 1.0:
                fleet.fleet_dirty = False
                last_fleet_pub = now
                writer.write_fleet(fleet.assignments(), fleet.queue_view())
                hub.broadcast(fleet_msg())

        # state stream at PUBLISH_HZ (wall-clock paced, independent of render rate)
        if now >= next_pub:
            next_pub = max(next_pub + pub_period, now - pub_period)
            seq += 1
            stats["pubs"] += 1
            # wall_ms = when these poses were read from physics, so Unity's lag covers the whole chain
            # anim_tc: the stage time code Isaac shows the environment animation at (Unity mirrors it)
            anim_tc = timeline.get_current_time() * timeline.get_time_codes_per_seconds()
            hub.broadcast({"type": "state", "seq": seq, "wall_ms": wall, "sim_t": round(sim_t, 4), "anim_tc": round(anim_tc, 3),
                           "agvs": [{"id": r["id"], "p": [round(v, 4) for v in r["pos"]], "q": [round(v, 5) for v in r["q"]],
                                     "wh": [round(v, 3) for v in r["wheels"]], "nav": r["nav"], "detail": r["detail"],
                                     "task": r["task"], "pi": r["pi"], "sim_t": round(sim_t, 4)} for r in rows]})

        if now - last_save >= cfg.ISAAC_LAYER_SAVE_S:
            last_save = now
            writer.save()
            el = now - stats["t0"]
            if el > 10:
                say(f"app {stats['frames'] / el:.1f} fps, publish {stats['pubs'] / el:.1f} Hz, clients {hub.client_count}, "
                    f"dropped {hub.dropped_frames}" + (f", deadlock recoveries {fleet.deadlock_recoveries}, stall recoveries {fleet.stall_recoveries}" if fleet else ""))
                stats.update(frames=0, pubs=0, t0=now)
        if t_end and now > t_end:
            break

    say("shutting down")
    hub.stop()
    log.close()
    writer.save()
    simulation_app.close()


if __name__ == "__main__":
    main()
