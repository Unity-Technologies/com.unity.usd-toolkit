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

> **Authentication is automatic here.** The server requires a token on every connection, and with its
> `authToken` field left empty it writes a per-session one to `<project>/UsdSync/live_sync_token.txt`,
> which `usd_live_sync.py` reads by itself. You only pass `--token` / `--token-file` when the server
> uses an explicit token or writes elsewhere (`--output-dir`). See [Security](#security).

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

- **Binding** — `bindAddress` (loopback only; see Security), `port` (`10000`), `backlog`.
- **Security** — `authToken`, `authTimeoutSeconds`, `maxClients`, `maxCommandBytes`.
  See [Security](#security).
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
{"cmd": "auth", "token": "<shared secret>"}
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

- `auth` must be the first command on every connection. Until it succeeds every other command — including
  the read-only `get_snapshot` — is answered with `{"ok": false, "error": "authentication required…"}`,
  and no broadcast is delivered. An invalid token is answered once and the connection is closed.
- Rotation is always a quaternion `[x,y,z,w]` — never Euler, which would invite axis-order disagreements.
- Translate and scale are `float[3]` in **raw Unity local space** (not basis-converted; see below).
- A `snapshot` is sent once the client authenticates (`join`), on `reset`, and in reply to
  `get_snapshot` (`request`).
- A `delta` carries only prims that changed beyond epsilon since the last tick.
- Broadcast reaches every *authenticated* client, so the Python client and Isaac Sim can watch at once.

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

It mirrors the real server's handshake, generating a token into `live_sync_token.txt` beside
`--base-stage` (override with `--token` or `$USD_LIVE_SYNC_TOKEN`).

## Security

This is a development tool, but the control channel is a real one: a client that reaches the port can
read the whole tracked scene and move objects in it. The server therefore authenticates every
connection and confines itself to this machine by default.

**Authentication.** Every connection must send `{"cmd":"auth","token":"…"}` before anything else.
Until it does, every command is refused and no scene data is sent — including `get_snapshot`, which
would otherwise disclose every tracked prim path and transform. An invalid token closes the
connection, and a client that never authenticates is dropped after `authTimeoutSeconds` (10s).

The token is resolved in this order:

1. the `authToken` inspector field;
2. the `USD_LIVE_SYNC_TOKEN` environment variable;
3. a random per-session token, written to `live_sync_token.txt` in the output folder (next to
   `base_stage.usda`). The server logs the exact path when it starts.

Option 3 is the default and needs no setup: `usd_live_sync.py` and the Isaac Sim client find that file
on their own. The file is created readable and writable **only by the user running the Editor** —
mode `0600` on macOS and Linux, and an ACL granting just that account on Windows. The secret is
written to an unguessably named file that is restricted while still empty and then renamed into
place, so it is never briefly world-readable. If those permissions cannot be applied the token is not
written at all and the server refuses to start, rather than leaving a readable secret on disk; set an
explicit `authToken` or `$USD_LIVE_SYNC_TOKEN` in that case.

**Binding is loopback-only.** `bindAddress` accepts `127.0.0.1`, `::1` or `localhost`; anything else
makes the server log an error and refuse to start. There is no opt-in to widen it. The transport is
plain TCP, so the token and the whole scene stream would cross a network in clear text where anyone
on the path could capture and replay them — which is why the sample does not offer that at all rather
than offering it with a warning. To drive Unity from another machine, put your own authenticated,
encrypted transport (an SSH tunnel, a VPN, a TLS proxy) in front of this loopback listener.

**Resource limits.** Inbound JSON is rejected past 32 levels of nesting (an unbounded recursive parse
would be an uncatchable stack overflow, not a caught error), a command is capped at `maxCommandBytes`
(64 KB) before a newline arrives, the pending-command queue is bounded, and `maxClients` (8) caps
simultaneous connections. The listener takes the port exclusively rather than with `SO_REUSEADDR`, so
another local process cannot rebind it and intercept clients.

**What this does not defend against.** Loopback traffic is unencrypted, and any process running as
you can read the token file and connect. The trust boundary is your user account on this machine —
the sample protects against other accounts on a shared host, not against code already running as
you.

**Per-object writes.** `UsdSyncNode.AcceptsRemoteWrites` still decides which objects accept inbound
transforms. It is a scope control layered on top of authentication, not a substitute for it.

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
