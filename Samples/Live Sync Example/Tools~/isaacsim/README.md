# USD Live Sync — NVIDIA Isaac Sim client

NVIDIA® Isaac Sim™ as a client of the Unity `UsdLiveSyncServer` broadcast hub (TCP `127.0.0.1:10000`,
newline-delimited JSON). Unity stays the host and the single authority on the wire schema; Isaac Sim
joins the same hub `Tools~/usd_live_sync.py` already uses, so **both can be connected at once** — the
"DCC-side client" that Phase 7 of the implementation plan left open.

Verified against **Isaac Sim 6.0.1**. The launchers look for the install in the conventional
locations (`C:\isaacsim`, `%USERPROFILE%\isaacsim`); point them anywhere else with the
`ISAACSIM_PATH` environment variable:

```bat
set ISAACSIM_PATH=D:\path\to\isaacsim
```

> **Isaac Sim is not included.** Nothing NVIDIA-owned is redistributed with this package — the
> `isaacsim` / `omni.*` / `carb` APIs are resolved at run time from an Isaac Sim installation you
> obtain from NVIDIA and use under NVIDIA's own license terms. See
> [License and notices](#license-and-notices).

---

## Quick start

### A. Interactive — Isaac Sim GUI + extension (the one you want for a demo)

```bat
Tools~\isaacsim\run_isaac_sim_with_livesync.bat
```

This launches the Isaac Sim GUI with the extension enabled (`--ext-folder` points Kit at
`Tools~/isaacsim/exts`; nothing is copied into the Isaac install). Then, in the **Unity USD Live Sync**
panel:

1. **base_stage.usda** is pre-filled from the launcher → **Mount Unity stage**.
2. **Connect** (Unity must be in Play mode).
3. Move something in Unity — it moves in Isaac.

### B. Scripted — standalone SimulationApp

```bat
Tools~\isaacsim\run_isaac_live_sync.bat
Tools~\isaacsim\run_isaac_live_sync.bat --headless --seconds 30
Tools~\isaacsim\run_isaac_live_sync.bat --mode open
Tools~\isaacsim\run_isaac_live_sync.bat --push-back /SyncRoot/Box_100x100x100_Prefab_18_
```

### C. No Unity Editor? Use the mock server

Brings up the exact wire protocol with prim paths read out of the real `base_stage.usda`, so you can
develop and debug the Isaac side on its own:

```bat
python Tools~\isaacsim\mock_unity_server.py --limit 10 --hz 20
```

Then run A or B against it. Because the paths come from the actual exported stage, a "prim not found"
in Isaac is a real bug, not a test artefact.

---

## Files

| File | Role |
|------|------|
| `exts/unity.usd.livesync/` | The Kit extension (source of truth — the standalone script imports from it). |
| `…/unity/usd/livesync/core.py` | Transport + coordinate conversion. **Stdlib only**, no `pxr`/`omni`. |
| `…/unity/usd/livesync/stage_bridge.py` | The `pxr` half: mounting, up-axis/units fix-up, per-frame applier. |
| `…/unity/usd/livesync/extension.py` | Kit extension: control panel + per-update pump. |
| `isaac_live_sync_standalone.py` | Standalone `SimulationApp` entry point. |
| `run_isaac_sim_with_livesync.bat` | Launch the GUI with the extension enabled. |
| `run_isaac_live_sync.bat` | Run the standalone script under Isaac's own interpreter. |
| `mock_unity_server.py` | Test double for `UsdLiveSyncServer` — no Unity Editor needed. |

---

## Where the extension lives

Entirely inside this Unity project. **Nothing is installed into Isaac Sim** — the Isaac install
directory is never written to.

```
Tools~\isaacsim\exts\unity.usd.livesync\      <- the extension
├── config\extension.toml                    <- manifest Kit reads
├── docs\
└── unity\usd\livesync\
    ├── extension.py                         <- panel + per-frame pump
    ├── core.py                              <- transport + coordinate conversion
    └── stage_bridge.py                      <- mounting, up-axis fix-up, applier
```

`run_isaac_sim_with_livesync.bat` just adds that folder to Kit's extension *search path*:

```bat
isaac-sim.bat --ext-folder "…\Tools~\isaacsim\exts" --enable unity.usd.livesync
```

Keeping the source here means it versions alongside `UsdLiveSyncServer.cs`, and there is one copy to
edit instead of two that drift.

**Two names are coupled** — the folder name `unity.usd.livesync` is what `--enable` matches, and the
nested `unity\usd\livesync\` directory path must mirror the `[[python.module]] name` in
`extension.toml`. That is why the nesting looks redundant; rename one without the other and Kit
loads nothing.

### Optional: make it appear in Isaac's Extensions window

Isaac already searches `<isaacsim>\extsUser` (it is listed in `apps\isaacsim.exp.base.kit`), so a
directory junction puts the extension in the normal Extensions list while you still edit the project
copy:

Run this from **this folder** (the `%CD%` below is `…\Tools~\isaacsim`), with `%ISAACSIM_PATH%` set to
your install root:

```bat
mklink /J "%ISAACSIM_PATH%\extsUser\unity.usd.livesync" "%CD%\exts\unity.usd.livesync"
```

Purely optional — the launcher works without it, and this junction is untested.

---

## The three things that make this non-trivial

These are the details worth knowing before changing any of this code; each one is a silent failure
rather than an error message.

### 1. `pxr` does not exist until Kit boots

Isaac's bundled interpreter cannot import `pxr` before `SimulationApp(...)` has run —
`setup_python_env.bat` only adds `<isaacsim>/site` to `PYTHONPATH`, and the USD libraries arrive with
the Kit extension paths:

```
> python.bat -c "from pxr import Usd"
ModuleNotFoundError: No module named 'pxr'
```

So the transport layer (`core.py`) is deliberately stdlib-only, all `pxr`-dependent code lives in
`stage_bridge.py`, and `isaac_live_sync_standalone.py` does `argparse` → `SimulationApp` → everything
else, in that order. `__init__.py` guards the `extension` import for the same reason: a headless run
has no `omni.ui`.

### 2. Up-axis and units are root-layer metadata

`base_stage.usda` declares `upAxis = "Y"` / `metersPerUnit = 1` (Unity's convention). Isaac Sim
stages are **Z-up** by default. Those are *root-layer* metadata, so a referenced layer's values are
ignored by composition — reference Unity's Y-up baseline into a Z-up Isaac stage and it arrives lying
on its side, with no warning.

`mount_unity_stage()` handles it by putting the correcting `rotateX` (+90° sends +Y to +Z) and a
`metersPerUnit` scale on a **dedicated wrapper Xform**:

```
/World/UnityScene            <- wrapper: owns the up-axis / units fix-up ONLY
/World/UnityScene/SyncRoot   <- reference to base_stage.usda
/World/UnityScene/SyncRoot/… <- the Unity hierarchy, driven by the live stream
```

The fix-up must go on a separate prim, not on `SyncRoot`: Unity tracks `SyncRoot` itself in
`AllDescendants` mode, so anything authored there is overwritten by the first delta. And because the
stream only ever authors **local** transforms on descendants, the wrapper's basis change composes
through automatically — no per-prim coordinate math changes.

Use `--mode open` to sidestep all of it: opening `base_stage.usda` as the root layer keeps its own
Y-up metadata, so there is no conversion and the prim prefix is empty. Good for verifying the stream
in isolation; not useful once you want Unity content alongside Isaac robots.

### 3. A reference maps `defaultPrim` *onto* the referencing prim

`base_stage.usda` declares `defaultPrim = "SyncRoot"`. A USD reference does not nest the referenced
content *under* the referencing prim — it maps the referenced `defaultPrim` **onto** it. So the
reference is attached to a prim that is itself named `SyncRoot`, which is what makes wire-path
mapping a plain string concatenation:

```
wire  /SyncRoot/PlayerArmature
stage /World/UnityScene  +  /SyncRoot/PlayerArmature
```

Attach it to a prim named anything else and every streamed path silently fails to resolve. If you
change `--root-prim`, it must keep matching Unity's `syncRoot` GameObject name.

---

## Coordinate conversion

Identical to `Tools~/usd_live_sync.py` (and asserted equal to it in testing), because Isaac consumes
the same `base_stage.usda` geometry that was written with the exporter's X-flip:

| Component | Unity → USD |
|-----------|-------------|
| translate | negate X: `(-x, y, z)` |
| rotation  | `(x,y,z,w)` → real-first `(w, x, -y, -z)` |
| scale     | unchanged |

Both maps are their own inverse, which is why push-back (Isaac → Unity) is the same arithmetic.

**Where this differs from the checkpoint writer, on purpose:** `usd_live_sync.py --checkpoint` clears
the op stack and authors `translate`/`orient`/`scale`, because it is building a standalone override
layer from scratch. The Isaac bridge instead reuses the baseline's existing single
`xformOp:transform`, so a frame is one attribute write with **no** change to `xformOpOrder` — and the
whole batch lands in one `Sdf.ChangeBlock`. Rewriting the op stack 60×/second would churn composition
for nothing.

---

## Bidirectional use

Isaac → Unity is opt-in and per-prim, because Unity enforces the ownership rule
(`UsdSyncNode.AcceptsRemoteWrites`): poses sent for anything else come back counted as `ignored` in
the ack, by design.

- **Extension:** section 4 of the panel — list wire prim paths, set a rate, toggle on.
- **Standalone:** `--push-back /SyncRoot/A,/SyncRoot/B --push-back-hz 20`

Poses are read from each prim's **local** transform, deliberately: the protocol is local-space
throughout, and a world-space read would send Unity a pose polluted by the wrapper's Z-up correction.

To drive Unity objects with Isaac **physics**, add colliders/rigid bodies to the mounted prims in
Isaac, set `AcceptsRemoteWrites = true` on the matching Unity `UsdSyncNode`, and enable push-back.
Note that Isaac then owns those poses while Unity still streams its own outward — decide one
authority per object, or the two will fight.

---

## "I connected and nothing happens"

**Isaac's Play button is not required.** The bridge authors USD attributes on every Kit update,
independent of the timeline. `--play` / Play exists only to run Isaac's *own* physics alongside the
stream.

The usual cause is that **nothing in Unity is actually moving**, which looks identical to a broken
bridge. Unity sends a `delta` only for prims whose transform *changed*: in `Playground.unity` the
tracked set is the avatar plus 4 boxes, the boxes are static, and the avatar only moves when you
drive it with WASD **with Unity's Game view focused**. So you get one `join` snapshot and then
silence — correct behaviour, zero visible motion.

**Read the panel's status line — it is the instrument.** It shows `… | N msg / M prim updates`:

| Status line | Meaning |
|-------------|---------|
| `1 msg`, never climbs | Unity is not streaming. Focus Unity's Game view and hold WASD; the avatar must visibly move *in Unity* first. |
| `msg` climbing, nothing visible in Isaac | Data is arriving — this is a viewport problem. Select `/World/UnityScene` in the Stage panel and press **F** to frame it. Isaac's default camera is very likely not pointed at the Unity scene. |
| `disconnected` | Unity is not in Play mode, or Connect was clicked before Mount. |

**Fastest way to isolate it** — take Unity out of the loop. The mock server animates continuously, so
working motion is unmistakable:

```bat
python Tools~\isaacsim\mock_unity_server.py --limit 10 --hz 20
```

Leave it running, then **Mount** → **Connect**. Prims should orbit and `applied` should climb. Things
move ⇒ the Isaac side is fine and the problem is Unity not streaming. Nothing moves ⇒ the bridge.

**The panel has no Window-menu entry.** It is a plain floating `ui.Window` created on startup; if you
close it, it is gone until you toggle the extension off and on in the Extensions window.

---

## Troubleshooting

| Symptom | Cause |
|---------|-------|
| `no prim at '/World/UnityScene/SyncRoot/…'` | Not mounted, wrong prefix, or `--root-prim` ≠ `base_stage.usda`'s `defaultPrim`. |
| Geometry on its side | Mounted without the fix-up (check for the `up-axis fix-up` log line), or the prefix points above the wrapper. |
| `disconnected (…refused)` | Unity is not in Play mode, or no `UsdLiveSyncServer` in the scene. |
| Nothing moves, but connected | Unity is streaming nothing — with `trackMode = ExplicitNodesOnly`, only `UsdSyncNode`-carrying transforms are sent, and only when they change. See ["I connected and nothing happens"](#i-connected-and-nothing-happens) above. |
| Panel never appeared | Check the console for `UI extension unavailable: …`. If the panel was merely closed, toggle the extension off/on (it has no Window-menu entry). |
| Push-back has no effect | Target Unity node has `AcceptsRemoteWrites = false` (check `ignored` in the ack). |
| `ModuleNotFoundError: pxr` | Ran with a system Python instead of Isaac's `python.bat` — use the `.bat` launchers. |
| `mock-unity: cannot bind …:10000` | Something already listens there — a leftover mock server, or Unity in Play mode. Only one host per port. |

**A note on the mock server and stale processes.** The mock intentionally does *not* set
`SO_REUSEADDR`. On Windows that option lets a second process bind a port another process is already
listening on — the new bind "succeeds" while the *older* process keeps accepting the connections. The
failure mode is nasty: your new mock's log stays silent, you conclude the bridge is broken, and in
fact the client is talking to a stale server. With `SO_EXCLUSIVEADDRUSE` the collision is a loud
error instead. If you see that error, kill the leftover process
(`Get-NetTCPConnection -LocalPort 10000` → `Stop-Process -Id <OwningProcess>`).

---

## License and notices

Copyright (c) 2026 Unity. All rights reserved.

The code in this folder — the Kit extension, the standalone runner, the launchers, and the mock
server — is proprietary Unity software shipped as part of the Unity USD Toolkit package
(`com.unity.usd-toolkit`). See the package's `LICENSE.md`, and
`exts/unity.usd.livesync/LICENSE.md` for the extension's own copy of the notice (the extension folder
can be junctioned into an Isaac Sim install, so it carries its notice with it).

**No NVIDIA software is redistributed.** This package bundles no NVIDIA source, binaries, headers,
models, or assets. The integration only calls NVIDIA-provided Python APIs (`isaacsim`, `omni.ext`,
`omni.kit.app`, `omni.ui`, `omni.usd`, `omni.timeline`, `carb`) resolved at run time from an Isaac Sim
installation you obtain separately, and it never writes into the Isaac Sim install directory. NVIDIA
Isaac Sim, NVIDIA Omniverse, and the components they install are licensed to you solely by NVIDIA
under NVIDIA's own terms — Unity grants no rights in any NVIDIA software. See
<https://developer.nvidia.com/isaac/sim> and <https://www.nvidia.com/en-us/agreements/>. The
package's full third-party notices are in `ThirdPartyNotices.md` at the package root.

### Third Party Product disclaimer

The following concerns a product or service (each a "Third Party Product") that is not developed,
owned, or operated by Unity. This information may not be up-to-date or complete, and is provided to
you for your information and convenience only. Your access and use of any Third Party Product is
governed solely by the terms and conditions of such Third Party Product. Unity makes no express or
implied representations or warranties regarding such Third Party Products, and will not be
responsible or liable, directly or indirectly, for any actual or alleged damage or loss arising from
your use thereof (including damage or loss arising from any content, advertising, products or other
materials on or available from the provider of any Third Party Products).

### Trademarks

NVIDIA, NVIDIA Isaac Sim, and NVIDIA Omniverse are trademarks and/or registered trademarks of NVIDIA
Corporation in the U.S. and other countries. Universal Scene Description (USD) and OpenUSD are
trademarks and/or registered trademarks of Pixar in the U.S. and other countries. Windows and
PowerShell are trademarks of the Microsoft group of companies. Unity and the Unity logo are
trademarks or registered trademarks of Unity Technologies or its affiliates in the U.S. and
elsewhere. All other trademarks are the property of their respective owners. Use of a third-party
name or mark here is for identification only and does not imply any endorsement, sponsorship, or
affiliation.
