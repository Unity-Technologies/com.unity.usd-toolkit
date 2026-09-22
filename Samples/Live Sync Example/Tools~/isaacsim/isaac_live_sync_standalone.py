#!/usr/bin/env python3
"""
Standalone Isaac Sim client for the Unity USD live transform sync (UsdLiveSyncServer, TCP :10000).

Boots Isaac Sim via SimulationApp, gets Unity's `base_stage.usda` geometry onto the stage, then
applies the live transform stream to it once per frame. This is the no-install path — nothing has to
be copied into the Isaac Sim installation. For interactive use inside the Isaac Sim GUI, use the Kit
extension in `exts/unity.usd.livesync` instead (see `run_isaac_sim_with_livesync.bat`).

Run it with Isaac Sim's OWN interpreter, never a system python:

    Tools\\isaacsim\\run_isaac_live_sync.bat
    Tools\\isaacsim\\run_isaac_live_sync.bat --headless --seconds 30
    Tools\\isaacsim\\run_isaac_live_sync.bat --mode open        # open base_stage.usda as the stage
    Tools\\isaacsim\\run_isaac_live_sync.bat --push-back /SyncRoot/Box_100x100x100_Prefab_18_

Two stage modes:

  --mode mount (default)  A fresh Isaac stage (Z-up, /World), with base_stage.usda REFERENCED under
                          /World/UnityScene/SyncRoot and the up-axis / units fix-up on the wrapper.
                          This is the mode that matters — Unity content composes alongside whatever
                          Isaac content (robots, ground plane, physics scene) you add.
  --mode open             Open base_stage.usda directly as the root layer. Its own Y-up metadata then
                          governs, so there is no basis conversion at all and the prim prefix is
                          empty. The cleanest way to verify the stream in isolation before trusting
                          the mount path.

IMPORTANT — import order. `pxr` and `omni` are NOT importable from Isaac's bundled interpreter until
SimulationApp has booted Kit (`python.bat -c "from pxr import Usd"` fails: setup_python_env.bat only
adds <isaacsim>/site to PYTHONPATH, and the USD libraries arrive with the Kit extension paths). So
argparse runs first (fast --help, fails fast on bad args), SimulationApp second, and every
pxr/omni-dependent import — including this project's own `stage_bridge` — strictly after that.
"""

import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
EXT_ROOT = os.path.join(HERE, "exts", "unity.usd.livesync")
def find_unity_project_root(start):
    """
    Walk up from 'start' looking for a Unity project root (a folder holding both Assets/ and
    ProjectSettings/). This tree ships inside the USD Toolkit package, so it may run from an embedded
    Packages/com.unity.usd-toolkit/... path or from Library/PackageCache/... — both sit under the
    project root. Returns None when it was copied somewhere else entirely.
    """
    current = os.path.abspath(start)
    while True:
        if (os.path.isdir(os.path.join(current, "Assets"))
                and os.path.isdir(os.path.join(current, "ProjectSettings"))):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


# '<unity project>/UsdSync/base_stage.usda' — where UsdLiveSyncServer writes the baseline when its
# Output Directory is left empty in the Editor. Override with --base-stage for a built player.
DEFAULT_BASE_STAGE = os.path.normpath(os.path.join(
    find_unity_project_root(HERE) or os.getcwd(), "UsdSync", "base_stage.usda"))


def parse_args(argv=None):
    p = argparse.ArgumentParser(
        description="Isaac Sim client for the Unity USD live transform sync.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)

    p.add_argument("--host", default="127.0.0.1", help="UsdLiveSyncServer address.")
    p.add_argument("--port", type=int, default=10000, help="UsdLiveSyncServer port.")

    p.add_argument("--base-stage", default=DEFAULT_BASE_STAGE,
                   help="Unity's baseline stage, written by UsdLiveSyncServer on Play.")
    p.add_argument("--mode", choices=("mount", "open"), default="mount",
                   help="mount: reference the baseline into a fresh Isaac stage. "
                        "open: open the baseline itself as the stage.")
    p.add_argument("--wrapper-path", default="/World/UnityScene",
                   help="[mount] Wrapper Xform carrying the up-axis / units fix-up.")
    p.add_argument("--root-prim", default="SyncRoot",
                   help="[mount] base_stage.usda's defaultPrim (Unity's syncRoot name).")
    p.add_argument("--payload", action="store_true",
                   help="[mount] Attach the baseline as a payload instead of a reference "
                        "(unloadable, useful for very heavy scenes).")
    p.add_argument("--prim-prefix", default=None,
                   help="Override the wire-path prefix. Defaults to the wrapper path in mount mode "
                        "and to empty in open mode.")

    p.add_argument("--headless", action="store_true", help="No renderer window.")
    p.add_argument("--play", action="store_true",
                   help="Start the timeline, so Isaac physics/sensors run while Unity streams.")
    p.add_argument("--seconds", type=float, default=0.0,
                   help="Exit after N seconds (0 = run until the window closes / Ctrl+C).")
    p.add_argument("--renderer", default="RaytracedLighting", help="Kit renderer.")

    p.add_argument("--push-back", default="",
                   help="Comma-separated wire prim paths whose Isaac pose is sent back to Unity. "
                        "Unity honours these only where UsdSyncNode.AcceptsRemoteWrites is true.")
    p.add_argument("--push-back-hz", type=float, default=20.0, help="Push-back rate.")

    p.add_argument("--reset-on-start", action="store_true",
                   help="Send {'cmd':'reset'} once connected, so both sides begin from the baseline.")
    p.add_argument("--stats-interval", type=float, default=5.0,
                   help="Seconds between status lines (0 = never).")
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)

    base_stage = os.path.abspath(os.path.expanduser(args.base_stage))
    if not os.path.isfile(base_stage):
        print("[isaac-live-sync] baseline not found: {}".format(base_stage), file=sys.stderr)
        print("[isaac-live-sync] press Play in Unity once so UsdLiveSyncServer writes it, or pass "
              "--base-stage.", file=sys.stderr)
        return 2

    # ---- boot Kit. Nothing pxr/omni-flavoured may be imported above this line. ------------------
    from isaacsim import SimulationApp
    kit = SimulationApp({"headless": bool(args.headless), "renderer": args.renderer})

    import omni.timeline
    import omni.usd

    # Our own pxr-dependent module, plus the stdlib-only transport, both live in the Kit extension so
    # the extension and this script cannot drift apart.
    sys.path.insert(0, EXT_ROOT)
    from unity.usd.livesync.core import SyncClient
    from unity.usd.livesync.stage_bridge import StageBridge, mount_unity_stage

    def log(message):
        print("[isaac-live-sync] {}".format(message), flush=True)

    usd_context = omni.usd.get_context()

    # ---- get the Unity geometry onto a stage ----------------------------------------------------
    if args.mode == "open":
        usd_context.open_stage(base_stage)
        _settle(kit)
        prefix = "" if args.prim_prefix is None else args.prim_prefix
        log("opened {} directly (Y-up preserved, no basis fix-up needed)".format(base_stage))
    else:
        usd_context.new_stage()
        _settle(kit)
        stage = usd_context.get_stage()
        mounted_prefix = mount_unity_stage(stage, base_stage,
                                           root_prim_name=args.root_prim,
                                           wrapper_path=args.wrapper_path,
                                           use_payload=args.payload,
                                           log=log)
        prefix = mounted_prefix if args.prim_prefix is None else args.prim_prefix
        _settle(kit)

    stage = usd_context.get_stage()
    if stage is None:
        log("failed to obtain a stage — aborting.")
        kit.close()
        return 1

    bridge = StageBridge(stage, prim_prefix=prefix, log=log)

    if args.play:
        omni.timeline.get_timeline_interface().play()
        log("timeline playing — Isaac simulation runs alongside the Unity stream.")

    # ---- connect ---------------------------------------------------------------------------------
    client = SyncClient(host=args.host, port=args.port, reconnect=True, on_log=log)
    client.start()
    log("client started against {}:{} (Unity must be in Play mode)".format(args.host, args.port))

    push_paths = [p.strip() for p in args.push_back.split(",") if p.strip()]
    if push_paths:
        log("push-back armed for {} prim(s) at {:.1f} Hz".format(len(push_paths), args.push_back_hz))

    started = time.time()
    last_stats = started
    last_push = 0.0
    reset_sent = False
    exit_code = 0

    try:
        while kit.is_running():
            # Unity -> Isaac. Coalesced, so a slow frame costs staleness rather than a backlog.
            pending = client.drain()
            if pending:
                bridge.apply(pending)

            if client.take_reset_flag():
                log("Unity reset — baseline re-broadcast and applied.")

            if args.reset_on_start and not reset_sent and client.connected:
                reset_sent = client.send_reset()

            # Isaac -> Unity.
            now = time.time()
            if push_paths and client.connected and now - last_push >= 1.0 / max(0.5, args.push_back_hz):
                last_push = now
                payload = {}
                for wire_path in push_paths:
                    trs = bridge.read_unity_trs(wire_path)
                    if trs is not None:
                        payload[wire_path] = trs
                if payload:
                    client.send_set_transform(payload)

            kit.update()

            if args.stats_interval > 0 and now - last_stats >= args.stats_interval:
                last_stats = now
                log("{} | {}".format(client.status_line(), bridge.stats_line()))

            if args.seconds > 0 and now - started >= args.seconds:
                log("--seconds elapsed, shutting down.")
                break
    except KeyboardInterrupt:
        log("interrupted.")
        exit_code = 130
    finally:
        client.stop()
        log("final: {} | {}".format(client.status_line(), bridge.stats_line()))
        kit.close()

    return exit_code


def _settle(kit, frames=2):
    """Let Kit process stage changes; opening/referencing is not complete after a single update."""
    for _ in range(frames):
        kit.update()


if __name__ == "__main__":
    sys.exit(main())
