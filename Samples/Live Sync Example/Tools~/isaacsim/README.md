# USD Live Sync: NVIDIA Isaac Sim client

Connect NVIDIA® Isaac Sim™ to a running Unity scene. Isaac Sim mounts the stage that Unity exports, applies the transforms Unity streams, and can send poses back to Unity.

Isaac Sim connects to the same `UsdLiveSyncServer` as `Tools~/usd_live_sync.py`, on TCP `127.0.0.1:10000`, so both clients can be connected at the same time.

> **Isaac Sim is not included.** This package contains no NVIDIA software. The client calls the `isaacsim`, `omni.*` and `carb` APIs from an Isaac Sim installation that you obtain from NVIDIA under NVIDIA's license terms. See [License and notices](#license-and-notices).

## Requirements

| Requirement | Detail |
| --- | --- |
| **Isaac Sim** | 6.0.1, installed on the same machine as Unity. |
| **Operating system** | Windows. The launchers are batch files. |
| **Unity** | The [Live Sync Example](../../README.md) scene, in Play mode. |

The launchers look for Isaac Sim in `C:\isaacsim` and `%USERPROFILE%\isaacsim`. If it's installed somewhere else, set `ISAACSIM_PATH`:

```bat
set ISAACSIM_PATH=D:\path\to\isaacsim
```

## Quick start

1. In Unity, open `LiveSyncExample.unity` and enter Play mode.
2. Run the launcher:

   ```bat
   Tools~\isaacsim\run_isaac_sim_with_livesync.bat
   ```

   Isaac Sim opens with the **Unity USD Live Sync** panel. The extension loads from this folder; nothing is installed into Isaac Sim.
3. In the panel, select **Mount Unity stage**. The path to `base_stage.usda` is already filled in.
4. Select **Connect**.

`OrbitingBeacon` moves continuously in Isaac Sim. Move `PropCube` in the Unity Scene view, and it moves in Isaac Sim too.

> [!TIP]
> If you can't see the Unity objects, select `/World/UnityScene` in the Isaac Sim **Stage** panel and press **F** to frame it.

## The panel

| Section | Description |
| --- | --- |
| **1 — Unity baseline geometry** | Mounts `base_stage.usda` into the Isaac Sim stage. |
| **2 — Live connection** | Connects to and disconnects from Unity. |
| **3 — Commands to Unity** | Requests a full snapshot, or resets every tracked Unity object to its starting transform. |
| **4 — Push back (Isaac -> Unity)** | Sends Isaac Sim poses back to Unity for the prim paths you list. |
| **Status** | Shows the connection state and the number of messages and prim updates received. |

If you close the panel, turn the extension off and on in the **Extensions** window to reopen it.

## Run without the GUI

`run_isaac_live_sync.bat` runs the client as a standalone Isaac Sim application:

```bat
Tools~\isaacsim\run_isaac_live_sync.bat
Tools~\isaacsim\run_isaac_live_sync.bat --headless --seconds 30
Tools~\isaacsim\run_isaac_live_sync.bat --push-back /SyncRoot/PropCube --push-back-hz 20
```

| Option | Description |
| --- | --- |
| `--headless` | Runs without a window. |
| `--seconds <n>` | Exits after `n` seconds. By default, runs until closed. |
| `--play` | Starts the Isaac Sim timeline so its physics runs. |
| `--mode open` | Opens `base_stage.usda` as the stage instead of mounting it. See [How the stage is mounted](#how-the-stage-is-mounted). |
| `--push-back <paths>` | Comma-separated prim paths to send back to Unity. |
| `--push-back-hz <rate>` | Push-back rate. Default: `20`. |
| `--root-prim <name>` | Name of the mounted root prim. Must match the Unity `syncRoot` GameObject. Default: `SyncRoot`. |
| `--host`, `--port`, `--token` | Connection settings. By default, the token is read from `live_sync_token.txt`. |

## Send poses back to Unity

Isaac Sim can drive Unity objects, including with Isaac Sim physics.

1. In Unity, add a `UsdSyncNode` to the object and enable **Accepts Remote Writes**. In the sample scene, `Ground`, `PropCube`, `PropSphere` and `PropCapsule` already accept remote writes.
2. In Isaac Sim, enter the object's prim path in panel section **4**, for example `/SyncRoot/PropCube`, and select **Enable push back**.

Unity ignores poses for objects that don't accept remote writes, and reports them as `ignored`.

To drive an object with Isaac Sim physics, add a collider and rigid body to its prim in Isaac Sim and start the timeline. Give each object a single owner: if Unity also moves an object that Isaac Sim pushes back, the two conflict.

## Test without Unity

`mock_unity_server.py` acts as the Unity server. It reads prim paths from your `base_stage.usda` and moves them continuously, which is useful to check the Isaac Sim side on its own.

```bat
python Tools~\isaacsim\mock_unity_server.py --limit 10 --hz 20
```

Exit Play mode in Unity first, because only one server can use the port. Then mount and connect from Isaac Sim as usual. If the prims move, Isaac Sim is working correctly.

## How the stage is mounted

Unity stages are Y-up and Isaac Sim stages are Z-up. **Mount Unity stage** references `base_stage.usda` under a wrapper prim that rotates and scales it to fit the Isaac Sim stage:

```text
/World/UnityScene            Wrapper: converts Y-up to Z-up and applies metersPerUnit
/World/UnityScene/SyncRoot   Reference to base_stage.usda
/World/UnityScene/SyncRoot/… The Unity hierarchy, driven by the live stream
```

Streamed transforms are local, so they don't need converting. Don't add your own transforms to `SyncRoot`, because Unity overwrites them.

The mounted prim must have the same name as the Unity `syncRoot` GameObject, so that the paths Unity sends, such as `/SyncRoot/PropCube`, match the stage.

To check the stream on its own, use `--mode open`. This opens `base_stage.usda` directly, with no conversion.

## Coordinate conversion

The client converts each transform from Unity to USD in the same way as the exporter:

| Component | Unity to USD |
| --- | --- |
| Translation | Negate X: `(-x, y, z)` |
| Rotation | `(x, y, z, w)` becomes `(w, x, -y, -z)`, real part first |
| Scale | Unchanged |

The same conversion applies in reverse when you send poses back to Unity.

## Files

| File | Description |
| --- | --- |
| `run_isaac_sim_with_livesync.bat` | Opens Isaac Sim with the extension enabled. |
| `run_isaac_live_sync.bat` | Runs `isaac_live_sync_standalone.py` with the Isaac Sim Python interpreter. |
| `isaac_live_sync_standalone.py` | The standalone client. |
| `mock_unity_server.py` | A stand-in for the Unity server, for testing. |
| `exts/unity.usd.livesync/` | The Omniverse Kit extension. |

The extension uses the following Kit APIs:

| Kit API | Used for |
| --- | --- |
| `omni.usd` | Mounting the stage and applying transforms. |
| `omni.kit.app` | Running the sync once per update. |
| `omni.ui` | The control panel. |
| `omni.timeline` | Starting the timeline with `--play`. The sync itself doesn't need the timeline. |
| `isaacsim.SimulationApp` | Standalone and headless runs. |
| `carb` | Logging. |

## Troubleshooting

| Symptom | Cause | Solution |
| --- | --- | --- |
| `disconnected (…refused)` | Unity isn't in Play mode, or the scene has no `UsdLiveSyncServer`. | Enter Play mode in Unity, then select **Connect**. |
| The status shows messages, but nothing is visible | The Isaac Sim camera isn't pointing at the Unity objects. | Select `/World/UnityScene` in the **Stage** panel and press **F**. |
| Connected, but the message count doesn't increase | Nothing is moving in Unity. Unity only sends transforms that change. | Move an object in Unity. With `trackMode = ExplicitNodesOnly`, only objects with a `UsdSyncNode` are sent. |
| `no prim at '/World/UnityScene/SyncRoot/…'` | The stage isn't mounted, or `--root-prim` doesn't match the Unity `syncRoot` name. | Select **Mount Unity stage** before **Connect**, and check the root prim name. |
| The Unity objects are on their side | The stage was added without the wrapper prim. | Use **Mount Unity stage**. |
| Push back has no effect | The Unity object doesn't accept remote writes. | Enable **Accepts Remote Writes** on its `UsdSyncNode`. |
| The panel doesn't appear | The UI extension failed to load, or the panel was closed. | Check the console for `UI extension unavailable`, or turn the extension off and on. |
| `ModuleNotFoundError: pxr` | The script ran with a system Python. | Use the `.bat` launchers. |
| `mock-unity: cannot bind …:10000` | Unity, or another mock server, is already using the port. | Exit Play mode, or stop the other process. In PowerShell, `Get-NetTCPConnection -LocalPort 10000` shows its process ID. |

## License and notices

Unity USD Toolkit Package © 2026 Unity Technologies. Licensed under the Unity Companion License for Unity-dependent projects. See `LICENSE.md` at the package root. Third-party components are licensed under their own terms; see `ThirdPartyNotices.md`.

**No NVIDIA software is redistributed.** This package contains no NVIDIA source code, binaries, headers, models or assets. The client calls NVIDIA-provided Python APIs (`isaacsim`, `omni.ext`, `omni.kit.app`, `omni.ui`, `omni.usd`, `omni.timeline`, `carb`) from an Isaac Sim installation that you obtain separately. NVIDIA Isaac Sim, NVIDIA Omniverse and the components they install are licensed to you solely by NVIDIA under NVIDIA's own terms. Unity grants no rights in any NVIDIA software. See <https://developer.nvidia.com/isaac/sim> and <https://www.nvidia.com/en-us/agreements/>. The package's third-party notices are in `ThirdPartyNotices.md` at the package root.

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
