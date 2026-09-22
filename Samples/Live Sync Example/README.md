# Live Sync Example

Bidirectional, near-real-time transform sync between a running Unity scene and an external tool
(Python, NVIDIA® Isaac Sim™, or a DCC), using USD as the on-disk record.

Unity is the host: it exports the scene's geometry **once** as `base_stage.usda`, then streams
transform changes over loopback TCP as newline-delimited JSON. Clients observe that stream, push
edits back, and can checkpoint the live state to a USD override layer that sublayers the baseline.

## Quick start

1. Open `LiveSyncExample.unity` and press Play. The sample builds its own hierarchy from primitives,
   writes `base_stage.usda`, and starts listening on `127.0.0.1:10000`. The HUD shows the endpoint,
   the tracked prim list, the connected client count, and the output folder. It docks into two narrow
   columns and leaves the middle of the view clear, so the synced geometry stays visible while you use
   it: collapse either column with the button in its header, or press `H` to hide the HUD entirely.
2. In a terminal, from this sample's `Tools~` folder:

   ```sh
   python usd_live_sync.py --watch
   ```

   You should immediately see a `snapshot` of every tracked prim, then a stream of `delta` records as
   `OrbitingBeacon` moves.
3. Push an edit back into the running scene:

   ```sh
   python usd_live_sync.py --set /SyncRoot/PropCube --translate=0.5,1.2,-0.4
   ```

   `PropCube` jumps in the Unity view. Try the same against `/SyncRoot/OrbitingBeacon` and the ack
   comes back `"applied": 0, "ignored": 1` — see [Ownership](#ownership) below.
4. Write a USD override layer from the live state (needs `pip install usd-core`):

   ```sh
   python usd_live_sync.py --checkpoint
   ```

   Then open `<project>/UsdSync/live_overrides.usda` in usdview: it sublayers `base_stage.usda`, so
   you see the sample's geometry posed at the transforms Unity was streaming.
5. `python usd_live_sync.py --reset` restores every tracked prim to the baseline captured at start.

> **Windows Git Bash:** an argument that looks like a POSIX path is rewritten by MSYS, so
> `--set /SyncRoot/PropCube` arrives as `C:/Program Files/Git/SyncRoot/PropCube` and comes back
> `unknown`. Prefix the command with `MSYS_NO_PATHCONV=1`, or use PowerShell or `cmd`.

## What the scene contains

`UsdLiveSyncSample` builds the hierarchy in `Awake`, so the sample needs no scene assets, no art
dependencies and nothing to import. Two ownership directions are represented:

| Prim | `UsdSyncNode` | Behaviour |
|------|---------------|-----------|
| `Ground`, `PropCube`, `PropSphere`, `PropCapsule` | yes, `AcceptsRemoteWrites = true` | A client may move them. |
| `OrbitingBeacon` | none | Observe-only. Unity keeps animating it, so the outbound `delta` stream has traffic with no client connected; inbound writes to it are ignored rather than fought frame by frame. |

Turn off **Build Demo Scene** on the component and assign the server's **Sync Root** yourself to run
the live channel against your own scene content instead.

## Ownership

Every tracked object is streamed **out** regardless of `UsdSyncNode`. An inbound `set_transform` is
applied **only** where `UsdSyncNode.AcceptsRemoteWrites` is true; otherwise it is counted as
`ignored`. Without that rule, an external edit to a Unity-authoritative object (a physics body, an
animation, an object under scripted motion) would be overwritten by Unity on the very next frame and
immediately re-broadcast, fighting the client.

**Echo suppression:** after applying an inbound edit the server records the applied value as that
node's "last sent" state, so the next dirty-diff pass does not bounce it straight back.

## Server configuration

`UsdLiveSyncServer` inspector fields:

- **Binding** — `bindAddress` (keep it on loopback), `port` (`10000`), `backlog`.
- **Sync scope** — `syncRoot` (this GameObject when unset) and `trackMode`:
  - `AllDescendants` (default) — every Transform under the root is tracked. Good for a scene of props.
  - `ExplicitNodesOnly` — only Transforms carrying a `UsdSyncNode` are tracked. Use this to sync a
    character's root transform without dragging in its animated skeleton bones. Prim paths are still
    computed against the full hierarchy, so a tracked node's path still matches `base_stage.usda`.
- **Baseline export** — `exportBaselineOnStart`, `outputDirectory` (empty means `<project>/UsdSync` in
  the Editor and `<persistentDataPath>/UsdSync` in a player), `baseStageFileName`, and
  `bakeSkinnedMeshes` (the exporter only walks `MeshFilter`+`MeshRenderer`, so skinned characters are
  baked into temporary static meshes for the one baseline export).
- **Broadcast throttle** — `broadcastEveryNFrames` (default `3`), `translateEpsilon`,
  `rotationEpsilonDegrees`. Changes below epsilon are treated as float noise and not sent.
- **Startup** — `autoStart`. The sample scene leaves this off and calls `StartServer()` from
  `UsdLiveSyncSample.Start()`, so the order is explicit: build the geometry, then build the prim
  table, export the baseline and bind the listener.

`public static event Action SceneReset` fires on the main thread after a client `reset`, so your own
systems can restart alongside the transform restore. `ResetToBaseline()` does the same thing without
a client round-trip (the HUD's **Reset To Baseline** button calls it).

The baseline export is best-effort: if the native USD plugin is unavailable the live transform channel
still runs, and only `base_stage.usda` is skipped.

## Wire protocol

One JSON object per line.

**Client to Unity**

```jsonc
{"cmd": "set_transform", "prims": {
  "/SyncRoot/PropCube": {"t": [0.5, 2, -1], "r": [0,0,0,1], "s": [1,1,1]}
}}
{"cmd": "reset"}
{"cmd": "get_snapshot"}
```

**Unity to clients**

```jsonc
{"type": "snapshot", "reason": "join"|"reset"|"request", "seq": N, "t": <ms>, "prims": { ...all tracked... }}
{"type": "delta",    "seq": N, "t": <ms>, "prims": { ...only changed... }}
{"type": "ack", "cmd": "set_transform", "ok": true, "applied": 1, "ignored": 0, "unknown": 0}
```

- Rotation is always a quaternion `[x,y,z,w]` — never Euler, which would invite axis-order disagreements.
- Translate and scale are `float[3]` in **raw Unity local space** (not basis-converted; see below).
- A `snapshot` is sent on connect (`join`), on `reset`, and in reply to `get_snapshot` (`request`).
- A `delta` carries only prims that changed beyond epsilon since the last tick.
- Broadcast reaches every connected client, so the Python client and Isaac Sim can watch at once.

## Coordinate conversion

`UsdExporter` writes `base_stage.usda` with a Unity (left-handed) to USD (right-handed) **X-axis
flip** (`FlipX` = `S·M·S`, `S = diag(-1,1,1)`). The live channel carries raw Unity values, so a client
must apply the same flip when authoring USD, or its overrides visually disagree with the baseline
geometry:

| Component | Unity to USD |
|-----------|--------------|
| translate | negate X: `(-x, y, z)` |
| rotation  | `(x,y,z,w)` becomes `Gf.Quatf(w, x, -y, -z)` |
| scale     | unchanged |

The quaternion rule is the conjugation of a rotation by the reflection `S`: axis-angle `(a, θ)` maps
to `(S a, -θ)`, i.e. `(x,y,z,w)` becomes `(x,-y,-z,w)`. `usd_live_sync.py` does this in
`unity_to_usd_translate` and `unity_to_usd_quat`.

`live_overrides.usda` sublayers `base_stage.usda` and authors `over` Xform prims with an explicit
`xformOp:translate` / `:orient` / `:scale` stack, replacing the baseline's single `xformOp:transform`
matrix, since the override layer is the stronger opinion.

## Files

| Path | Role |
|------|------|
| `LiveSyncExample.unity` | The sample scene. |
| `UsdLiveSyncSample.cs` | Builds the demo hierarchy, starts the server, draws the runtime HUD. |
| `UsdLiveSyncServer.cs` | Prim-path table, baseline export, TCP server, dirty-diff broadcast, inbound commands. |
| `UsdSyncNode.cs` | Per-object ownership marker and per-channel toggles. |
| `Tools~/usd_live_sync.py` | Reference client: stream, push edits, reset, checkpoint to USD. Standard library only, except `--checkpoint`, which needs `usd-core`. |
| `Tools~/isaacsim/` | NVIDIA Isaac Sim client — Kit extension, standalone runner, and a mock Unity server for testing without the Editor. See its own `README.md`. |

`Tools~` ends in a tilde so Unity does not import the Python and batch files as assets. The folder is
still on disk and still ships with the package.

## NVIDIA Isaac Sim

Isaac Sim joins as another client of the same port — Unity stays the host and the wire schema is
unchanged, so Isaac and `usd_live_sync.py` can be connected simultaneously. See
`Tools~/isaacsim/README.md`.

Isaac Sim itself is **not included**: the client under `Tools~/isaacsim/` calls NVIDIA-provided
Python APIs resolved at run time from an Isaac Sim installation you obtain from NVIDIA under NVIDIA's
own license terms. See [License and notices](#license-and-notices).

`Tools~/isaacsim/mock_unity_server.py` stands in for Unity if you want to develop a client without the
Editor running:

```sh
python isaacsim/mock_unity_server.py --port 10099
python usd_live_sync.py --port 10099 --watch
```

## Security

This is a development tool. The server binds loopback by default — keep it there. It performs no
authentication and applies transform writes from any connected client, so do not expose the port
beyond `127.0.0.1`.

## License and notices

Copyright (c) 2026 Unity. All rights reserved.

This sample — the C# scripts, the scene, the Python client, and the Isaac Sim client under
`Tools~/isaacsim/` — is proprietary Unity software shipped as part of the Unity USD Toolkit package.
See `LICENSE.md` and `ThirdPartyNotices.md` at the package root.

**No NVIDIA software is redistributed.** This package bundles no NVIDIA source, binaries, models, or
assets. The Isaac Sim client only calls NVIDIA-provided Python APIs (`isaacsim`, `omni.*`, `carb`)
resolved at run time from an Isaac Sim installation you obtain separately, and it never writes into
the Isaac Sim install directory. NVIDIA Isaac Sim, NVIDIA Omniverse, and the components they install
are licensed to you solely by NVIDIA under NVIDIA's own terms — Unity grants no rights in any NVIDIA
software. See <https://developer.nvidia.com/isaac/sim> and
<https://www.nvidia.com/en-us/agreements/>.

`--checkpoint` in `Tools~/usd_live_sync.py` requires `usd-core`, which you install yourself with
`pip` under its own license (Tomorrow Open Source Technology License 1.0); it is not redistributed
here either.

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
trademarks and/or registered trademarks of Pixar in the U.S. and other countries. Windows is a
trademark of the Microsoft group of companies. Unity and the Unity logo are trademarks or registered
trademarks of Unity Technologies or its affiliates in the U.S. and elsewhere. All other trademarks
are the property of their respective owners. Use of a third-party name or mark here is for
identification only and does not imply any endorsement, sponsorship, or affiliation.
