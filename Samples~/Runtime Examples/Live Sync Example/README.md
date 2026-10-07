# Live Sync Example

Synchronize transforms in both directions between a running Unity scene and an external tool, such as NVIDIA® Isaac Sim™ or a Python script, with USD as the file format.

Unity is the host. It exports the scene geometry once as `base_stage.usda`, then streams transform changes over TCP as newline-delimited JSON. Clients receive the stream, send changes back, and can save the current state to a USD layer.

The sample synchronizes transforms only, and accepts connections from the same machine only.

## Requirements

| Requirement | Detail |
| --- | --- |
| **Python** | Python 3, for `Tools~/usd_live_sync.py`. Saving a checkpoint also needs `pip install usd-core`. |
| **Isaac Sim** | Optional. Isaac Sim 6.0.1 on Windows. See the [Isaac Sim client guide](Tools~/isaacsim/README.md). |

## Quick start

1. Open `LiveSyncExample.unity` and enter Play mode.

   The sample creates its objects, writes `base_stage.usda`, and starts listening on `127.0.0.1:10000`. The on-screen panel shows the address, the tracked objects, the connected clients and the output folder. Press `H` to hide it.

2. In a terminal, go to this sample's `Tools~` folder and watch the stream:

   ```sh
   python usd_live_sync.py --watch
   ```

   A `snapshot` of every tracked object appears, followed by `delta` messages as `OrbitingBeacon` moves.

3. Move an object in the running scene:

   ```sh
   python usd_live_sync.py --set /SyncRoot/PropCube --translate=0.5,1.2,-0.4
   ```

   `PropCube` moves in the Game view. If you try the same with `/SyncRoot/OrbitingBeacon`, Unity reports it as `ignored`. See [Ownership](#ownership).

4. Save the current state to a USD layer:

   ```sh
   python usd_live_sync.py --checkpoint
   ```

   Open `<project>/UsdSync/live_overrides.usda` in usdview to see the objects in their current positions.

5. To restore every object to its starting transform, run `python usd_live_sync.py --reset`.

The client authenticates automatically with a token that the server writes to `<project>/UsdSync/live_sync_token.txt`. See [Security](#security).

> [!NOTE]
> In Git Bash on Windows, prim paths such as `/SyncRoot/PropCube` are converted to Windows paths and Unity reports them as `unknown`. Add `MSYS_NO_PATHCONV=1` before the command, or use PowerShell or Command Prompt.

To connect Isaac Sim, see the [Isaac Sim client guide](Tools~/isaacsim/README.md).

## The sample scene

The sample creates its objects at startup, so it needs no assets.

| Object | `UsdSyncNode` | Behavior |
| --- | --- | --- |
| `Ground`, `PropCube`, `PropSphere`, `PropCapsule` | Yes, with `AcceptsRemoteWrites = true` | Clients can move them. |
| `OrbitingBeacon` | None | Unity moves it continuously. Clients can see it but not move it. |

To use your own scene instead, disable **Build Demo Scene** on the `UsdLiveSyncSample` component and set **Sync Root** on the server.

## Ownership

Unity streams every tracked object. It only applies changes from clients to objects with a `UsdSyncNode` that has **Accepts Remote Writes** enabled, and reports any other change as `ignored`.

This prevents a client from moving an object that Unity also controls, such as a physics body or an animated object, which Unity would immediately move back.

## Server settings

These settings are on the `UsdLiveSyncServer` component.

| Group | Settings |
| --- | --- |
| **Binding** | `bindAddress` (`127.0.0.1`, `::1` or `localhost`), `port` (default `10000`), `backlog`. |
| **Security** | `authToken`, `authTimeoutSeconds`, `maxClients`, `maxCommandBytes`. See [Security](#security). |
| **Sync scope** | `syncRoot`, which defaults to the server's GameObject, and `trackMode`. |
| **Baseline export** | `exportBaselineOnStart`, `outputDirectory`, `baseStageFileName` and `bakeSkinnedMeshes`. |
| **Broadcast** | `broadcastEveryNFrames` (default `3`), `translateEpsilon` and `rotationEpsilonDegrees`. Smaller changes aren't sent. |
| **Startup** | `autoStart`. The sample scene starts the server from `UsdLiveSyncSample` instead. |

`trackMode` has two values:

- `AllDescendants` (default): tracks every transform under `syncRoot`.
- `ExplicitNodesOnly`: tracks only transforms with a `UsdSyncNode`. Use this, for example, to track a character's root without its animated bones.

By default, files are written to `<project>/UsdSync` in the Editor and `<persistentDataPath>/UsdSync` in a player.

Skinned meshes are converted to static meshes for the baseline export when `bakeSkinnedMeshes` is enabled.

If the native USD plugin isn't available, the server still streams transforms, but doesn't write `base_stage.usda`.

### Scripting

- `UsdLiveSyncServer.SceneReset` is a static event that fires on the main thread after a client resets the scene, so your own systems can reset too.
- `ResetToBaseline()` resets the scene from code. The panel's **Reset To Baseline** button calls it.

## Wire protocol

Each message is one JSON object on one line.

**Client to Unity:**

```jsonc
{"cmd": "auth", "token": "<shared secret>"}
{"cmd": "set_transform", "prims": {
  "/SyncRoot/PropCube": {"t": [0.5, 2, -1], "r": [0,0,0,1], "s": [1,1,1]}
}}
{"cmd": "reset"}
{"cmd": "get_snapshot"}
```

**Unity to clients:**

```jsonc
{"type": "snapshot", "reason": "join"|"reset"|"request", "seq": N, "t": <ms>, "prims": { ...all tracked... }}
{"type": "delta",    "seq": N, "t": <ms>, "prims": { ...only changed... }}
{"type": "ack", "cmd": "set_transform", "ok": true, "applied": 1, "ignored": 0, "unknown": 0}
```

- `auth` must be the first command on each connection. Until it succeeds, Unity refuses every other command and sends no data. An invalid token closes the connection.
- `t` (translation) and `s` (scale) are `float[3]`, and `r` (rotation) is a quaternion `[x, y, z, w]`. All values are in Unity local space.
- Unity sends a `snapshot` when a client authenticates, after a `reset`, and in reply to `get_snapshot`.
- A `delta` contains only the objects that changed since the previous one.
- Every authenticated client receives the stream, so several clients can connect at once.

## Coordinate conversion

`base_stage.usda` is written in USD's coordinate system, but the stream carries Unity values. A client that writes USD must convert them in the same way as the exporter:

| Component | Unity to USD |
| --- | --- |
| Translation | Negate X: `(-x, y, z)` |
| Rotation | `(x, y, z, w)` becomes `Gf.Quatf(w, x, -y, -z)` |
| Scale | Unchanged |

`usd_live_sync.py` does this in `unity_to_usd_translate` and `unity_to_usd_quat`.

`live_overrides.usda` sublayers `base_stage.usda` and overrides each object's translation, orientation and scale.

## Files

| Path | Description |
| --- | --- |
| `LiveSyncExample.unity` | The sample scene. |
| `UsdLiveSyncSample.cs` | Creates the sample objects, starts the server and draws the on-screen panel. |
| `UsdLiveSyncServer.cs` | The server: exports the baseline, streams changes and applies client commands. |
| `UsdSyncNode.cs` | Marks an object as accepting changes from clients. |
| `Tools~/usd_live_sync.py` | Python client: watch, move objects, reset and save checkpoints. Uses the Python standard library only, except for `--checkpoint`. |
| `Tools~/isaacsim/` | The Isaac Sim client. See its [guide](Tools~/isaacsim/README.md). |

`Tools~/isaacsim/mock_unity_server.py` acts as the Unity server, so you can develop a client without the Editor:

```sh
python isaacsim/mock_unity_server.py --port 10099
python usd_live_sync.py --port 10099 --watch
```

## Security

The sample is a development tool. Any client that connects can read the tracked scene and move objects in it, so the server requires authentication and accepts local connections only.

**Authentication.** Each connection must send `{"cmd":"auth","token":"…"}` first. A client that doesn't authenticate within `authTimeoutSeconds` (default 10 seconds) is disconnected.

The server uses the first token it finds:

1. The **Auth Token** field on the component.
2. The `USD_LIVE_SYNC_TOKEN` environment variable.
3. A random token for the session, written to `live_sync_token.txt` in the output folder. The server logs the file's path when it starts.

The third option is the default, and the Python and Isaac Sim clients read the file automatically. Only your user account can read the file. If the server can't restrict the file's permissions, it doesn't start; set **Auth Token** or `USD_LIVE_SYNC_TOKEN` instead.

**Local connections only.** The server only binds to `127.0.0.1`, `::1` or `localhost`, and doesn't start with any other address. Traffic is not encrypted. To connect from another machine, use your own encrypted, authenticated connection to the local port, such as an SSH tunnel or VPN.

**Limits.** The server limits each command to `maxCommandBytes` (default 64 KB) and the number of connections to `maxClients` (default 8), and rejects deeply nested JSON. It takes exclusive use of its port, so another process can't take it over.

**Scope.** Any process running under your user account can read the token and connect. The sample protects against other users on a shared machine, not against software running as you.

`UsdSyncNode.AcceptsRemoteWrites` controls which objects clients can move. It applies in addition to authentication, not instead of it.

## License and notices

Unity USD Toolkit Package © 2026 Unity Technologies. Licensed under the Unity Companion License for Unity-dependent projects. See `LICENSE.md` at the package root. Third-party components are licensed under their own terms; see `ThirdPartyNotices.md`.

**No NVIDIA software is redistributed.** This package contains no NVIDIA source code, binaries, models or assets. The Isaac Sim client calls NVIDIA-provided Python APIs (`isaacsim`, `omni.*`, `carb`) from an Isaac Sim installation that you obtain separately. NVIDIA Isaac Sim, NVIDIA Omniverse and the components they install are licensed to you solely by NVIDIA under NVIDIA's own terms. Unity grants no rights in any NVIDIA software. See <https://developer.nvidia.com/isaac/sim> and <https://www.nvidia.com/en-us/agreements/>.

`--checkpoint` in `Tools~/usd_live_sync.py` requires `usd-core`, which you install yourself with `pip`, under its own license. It is not redistributed with this package.

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
