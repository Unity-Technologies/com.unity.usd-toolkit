# AGV Fleet LiveSync Demo

A fleet of AGVs (automated guided vehicles) in a factory hall, with Unity as the operator front end and NVIDIA® Isaac Sim™ as the simulator. Both open the same USD stage: Unity shows the fleet live and lets you place obstacles and send goals; Isaac Sim drives the AGVs with physics, plans their paths and manages the fleet.

The hall is either NVIDIA's "Factory" scene from the USD Explorer Sample Asset Pack, which you download separately, or a warehouse hall that the sample generates as USD. Both work with or without Isaac Sim.

▶ **Video:** [AGV Fleet LiveSync Demo on YouTube](https://youtu.be/P8_rpQgEzSU)

![Isaac Sim and Unity side by side, both showing the NVIDIA factory with the AGV fleet and its planned paths](../../Documentation~/images/agv_fleet_livesync.png)

## Run configurations

| Asset pack | Isaac Sim | Result |
| --- | --- | --- |
| No | No | Generated hall; the AGVs are shown at their charging docks. Unity does not simulate the fleet itself. |
| No | Yes | Generated hall, sent to Isaac Sim; the AGVs drive in Isaac Sim and Unity shows them live. |
| Yes | No | NVIDIA factory; the AGVs are shown at their charging docks. |
| Yes | Yes | NVIDIA factory; the AGVs drive in Isaac Sim and Unity shows them live. |

With the default settings the sample picks all of this by itself: the factory when its USD file is found, the generated hall otherwise, and Isaac Sim whenever it is running the same environment.

## Requirements

| Requirement | Detail |
| --- | --- |
| **Unity** | 6000.4 or later. Tested with 6000.4.5f1. |
| **Packages** | Unity USD Toolkit 0.8.1-exp.1, Universal Render Pipeline (tested with 17.4), Input System 1.7.0 or later. Without the Input System, the sample's assemblies are skipped rather than failing to compile. |
| **Isaac Sim** | Optional. Isaac Sim 6.1.0 on Windows 10 or 11 with an NVIDIA RTX GPU, on the same machine as Unity. |
| **Asset pack** | Optional. NVIDIA USD Explorer Sample Asset Pack, see [Use the NVIDIA factory](#use-the-nvidia-factory). |
| **Python** | Only for the optional tools and tests in `Tools~/agv_fleet`: Python 3.10 or later, standard library only. Isaac Sim brings its own Python for the simulator. |

## Quick start: the generated hall

1. In **Window > Package Management > Package Manager**, select **Unity USD Toolkit**, open **Samples** and import **AGV Fleet LiveSync Demo**.
2. Open `Scenes/AGVFleet_LiveSync.unity` from the imported sample and enter Play mode.

The sample generates a 48 × 32 m warehouse hall, writes it to `UsdSync/AGVFleet/generated_factory.usda` in your project folder, and imports it with the USD Toolkit. Three AGVs wait at their charging docks on the west wall. The Console shows one line saying that the factory was not found and the generated hall is used instead; that is expected until you add the asset pack.

To make the AGVs drive, [connect Isaac Sim](#connect-isaac-sim).

## Use the NVIDIA factory

The factory is NVIDIA's car body shop from the **USD Explorer Sample Asset Pack**. It is not part of this package and must be downloaded separately.

| | |
| --- | --- |
| **Download** | [USD_Explorer_Sample_NVD@10011.zip](https://d4i3qtqj3r0z5.cloudfront.net/USD_Explorer_Sample_NVD%4010011.zip), listed on NVIDIA's [Downloadable Asset Packs](https://docs.omniverse.nvidia.com/usd/latest/usd_content_samples/downloadable_packs.html) page |
| **Size** | About 550 MB download, about 0.8 GB unzipped |
| **License** | The pack states that it "is bound by the NVIDIA Omniverse terms" (`PACKAGE-LICENSES/USD_Explorer_Sample_NVD-license.txt` in the zip). Read those terms before you use or share anything made with it. Do not commit the pack, or files derived from it, to a repository. |
| **Files used** | `Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd` and the files it references (`SubUSDs/`, `Source_Files/`). Keep the folder structure as it is in the zip. |

1. Unzip the pack into `ExternalAssets/USD_Explorer_Sample` in your project folder, next to `Assets` (not inside it). Afterwards, this file exists:

   ```text
   <your project>/ExternalAssets/USD_Explorer_Sample/Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd
   ```

2. Add `ExternalAssets/` (and `UsdSync/`, see [Files the sample writes](#files-the-sample-writes)) to your version control's ignore list, for example in `.gitignore`.
3. Enter Play mode. The sample imports the factory (about 20 seconds; the scene holds about 13,000 meshes).

To keep the pack somewhere else, set **Asset Pack Path** in `Settings/AgvFleetSettings.asset`. A relative path is resolved from the project folder (in a player build, from the folder of the executable); an absolute path is used as it is. The environment variable `AGV_FLEET_ASSET_PACK` overrides the setting.

Isaac Sim does not need its own copy: it runs on the same machine and loads the same file, because Unity tells it which file to use (see [How it works](#how-it-works)).

Without Isaac Sim, the factory shows the materials that the USD Toolkit translates from the pack's MDL materials, and the welding cell in its rest pose. Once Isaac Sim has run, Unity also uses the preview materials and the welding-cell animation that Isaac Sim derives from the factory into the stage folder.

## Connect Isaac Sim

The sample's Isaac Sim side is in `Tools~/agv_fleet`. Unity does not import folders whose name ends with `~`; after importing the sample, the folder is at `Assets/Samples/Unity USD Toolkit/<version>/AGV Fleet LiveSync Demo/Tools~/agv_fleet`.

Install Isaac Sim 6.1.0 from NVIDIA and run its `post_install.bat` once. Isaac Sim is not included with this package; it is licensed to you by NVIDIA under NVIDIA's terms. The AGV is idealworks' iw_hub from the Isaac Sim assets, loaded from Isaac Sim's asset root (NVIDIA's cloud by default, or a local Isaac Sim asset pack). Set `ISAAC_ASSET_ROOT` if Isaac Sim cannot resolve its asset root.

1. Enter Play mode in Unity. Unity writes `UsdSync/AGVFleet/environment.json`, which tells Isaac Sim what to simulate. Keep Play mode running, or stop it; the file stays.
2. Run `Tools~/agv_fleet/run_agv_fleet.bat`. It finds Isaac Sim in `ISAACSIM_PATH`, in a one-line `isaacsim_path.txt` next to the script (the folder that contains `python.bat`), or in `C:\isaacsim`, `C:\isaac-sim` or your user folder.

   ```bat
   run_agv_fleet.bat                 :: GUI, every AGV of the environment
   run_agv_fleet.bat --headless      :: no Isaac Sim window
   run_agv_fleet.bat --agvs 1        :: only the first AGV
   ```

   Wait for `[agv_fleet] running: 3 AGV(s)` in the Isaac Sim console. The first launch on a new machine can take several minutes (shader cache); later launches take 10 to 60 seconds.
3. Unity connects by itself. The panel on the left changes from `Local fleet` to `Isaac Sim` and shows `● live 30 Hz`, and the AGVs start their loops.

Either side can start first or restart at any time: Unity looks for Isaac Sim about once a second, and falls back to showing the AGVs at their docks when Isaac Sim stops.

> **Isaac Sim 6.1 sometimes crashes during start-up**, inside its own extensions, before the sample's code runs. `run_agv_fleet.bat` starts it again, up to three times. On the machine this sample was tested on, about one launch in three needed a retry.

### What is synchronized

| Prim | Written by | Read by | How |
| --- | --- | --- | --- |
| `/World/Environment` (factory or generated hall) | nobody at runtime | both | The same USD file, named in `environment.json` |
| `/World/AGVs/AGV_0x`: pose, wheel angles, navigation state, task | Isaac Sim | Unity | Hub stream at 30 Hz; Isaac Sim also saves it in `isaac_state.usda` once a second |
| `/World/Dynamic/Paths/*`, `/World/Dynamic/Fleet` | Isaac Sim | Unity | Hub `path` and `fleet` messages |
| `/World/Dynamic/Obstacles/*`, `/World/Dynamic/Goal` | Unity | Isaac Sim | `unity_ops.usda` (a USD layer Unity writes), plus a notice over the hub; Isaac Sim reloads the layer |

Each prim has one writer, and Unity and Isaac Sim write to separate layers. The hub is a TCP connection on `127.0.0.1:10010` that carries newline-delimited JSON.

## Using the demo

| Action | How |
| --- | --- |
| Send a goal (the fleet picks the nearest free AGV) | Tool **1 Goal**, then click the floor |
| Send the next goal to a specific AGV | Click the AGV, then click the floor |
| Place an obstacle | Tools **2 Box**, **3 Pallet**, **4 Barrier**, then click the floor; **R** rotates by 90° |
| Delete an obstacle | Right-click it, or tool **5 Delete** |
| Clear obstacles, the goal queue and tasks | **Reset scene** |
| Show or hide the panel, the latency panel | **H**, **L** |
| Camera | Right-drag to orbit, middle-drag to pan, scroll to zoom |

Obstacles and goals are always written to `unity_ops.usda`, and obstacles are kept between sessions. Isaac Sim acts on them; without Isaac Sim they are only shown.

## Settings

All settings are in `Settings/AgvFleetSettings.asset`, which the scene's **AGV Fleet** object references.

| Setting | Default | Meaning |
| --- | --- | --- |
| **Environment** | Auto | **Auto**: the factory when its USD file is found, otherwise the generated hall. **Factory**, **Generated**: always that one; Factory reports an error when the file is missing. |
| **Asset Pack Path** | `ExternalAssets/USD_Explorer_Sample` | Where the asset pack is unzipped |
| **Factory Usd In Pack** | `Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd` | The factory file inside the pack |
| **Factory Layout** | `Settings/FactoryLayout.asset` | Spawn poses, loops, stations and planning bounds for the factory: numbers only, no NVIDIA content. Edit it to change the AGV routes in the factory. |
| **Generator** | 48 × 32 m, 4 × 3 racks, seed 1 | The generated hall, see below |
| **Simulation** | Auto | **Auto**: Isaac Sim while it runs the same environment, otherwise the AGVs at their docks. **Local**: never connect. **Isaac Sim**: always wait for Isaac Sim. |
| **Stage Dir** | `UsdSync/AGVFleet` | The folder Unity and Isaac Sim share; `AGV_FLEET_STAGE_DIR` overrides it (both sides read it) |
| **Hub Host**, **Hub Port** | `127.0.0.1`, `10010` | Isaac Sim's hub |
| **Agv Model Usd** | empty | Optional USD file for the AGV model, such as `iw_hub.usd` from the Isaac Sim assets. Empty: a model built from primitives, with the iw_hub's footprint. |

### The generated hall

| Parameter | Range | Effect |
| --- | --- | --- |
| **Hall Length**, **Hall Width** | 24–120 m, 16–80 m | Hall size along x and y |
| **Rack Rows**, **Rack Columns** | 1–10, 1–6 | Rack rows along x, and segments per row separated by cross aisles. Fewer are built when they do not fit; the Console says so. |
| **Aisle Width** | 2.6–6 m | Width of aisles and lanes; the AGVs drive on their centre lines |
| **Station Count** | 1–8 | Pick stations (conveyors on the east wall) and drop stations (tables on the north wall), each |
| **Agv Count** | 1–6 | AGVs, each with a charging dock on the west wall |
| **Seed** | any | Rack loads, obstacle placement and station assignment. The same values always give the same hall, byte for byte. |

Each AGV loops from its dock to a pick station, then a drop station, then back to its dock, along the lanes. The lanes are painted on the floor as dashed lines: they are the route graph the loops are made from.

**Regenerate** in the panel builds the hall for the next seed, replaces `generated_factory.usda` and `environment.json`, and removes your obstacles (they belonged to the old layout). It is available while Isaac Sim is not driving the fleet; Isaac Sim loads an environment when it starts, so restart it to use the new hall there. To change other parameters, edit the settings asset and enter Play mode again.

## How it works

On every start, and on Regenerate, Unity writes `environment.json` into the stage folder: the environment's identity, its USD file, units, the planning area, the AGV spawn poses and loops, and, for the generated hall, the exact obstacle boxes. Isaac Sim reads the file when it starts (preset `from_unity`, the default whenever the file exists), so it always loads what Unity shows. It writes `root.usda`, which references the environment at `/World/Environment` and sublayers `unity_ops.usda` and its own `isaac_state.usda`.

- **Factory:** Isaac Sim builds its planning grid by projecting the factory's meshes, and adds invisible box colliders in its own layer, because the factory has no colliders. The factory file is never edited.
- **Generated hall:** the file brings its own `UsdPhysics` colliders (a floor plane, walls, and one invisible box per rack, station, dock and obstacle), and Isaac Sim plans on the exact boxes from `environment.json`. Lanes and geometry come from the same generator, so the AGVs' loops always match the hall.

Isaac Sim announces the environment's identity when Unity connects. If Isaac Sim runs another environment, for example an older seed, Unity ignores its stream, keeps the AGVs at their docks, and says in the panel that Isaac Sim needs a restart.

**Coordinates.** The shared stage uses Isaac Sim's convention: Z-up, right-handed, meters. Unity maps `unity (x, y, z) = usd (x, z, y)` in one place, `Runtime/UsdFrame.cs`; the hub messages and both layers stay in the USD frame. The sample imports with `ApplyStageUpAxisAndUnits` off, so that this mapping is the only conversion.

### Files the sample writes

In the stage folder (`UsdSync/AGVFleet` in your project folder, outside `Assets`):

| File | Written by | Content |
| --- | --- | --- |
| `environment.json` | Unity | What Isaac Sim simulates |
| `generated_factory.usda` | Unity | The generated hall |
| `unity_ops.usda`, `unity_ops.state.json` | Unity | Obstacles and the goal |
| `root.usda`, `isaac_state.usda` | Isaac Sim | The shared stage and Isaac Sim's own layer |
| `env_look_*.usda`, `env_anim.usda`, `env_anim.json`, `root_unity.usda` | Isaac Sim | Preview materials and the frozen animation derived from the environment, for Unity |
| `occupancy_*.npz`, `logs/` | Isaac Sim | Planning grid cache; event and latency logs |
| `hub_token.txt` | Isaac Sim | The hub's access token, readable by the current user only |

All of it is regenerated as needed. Do not commit it: for the factory, several of these files are derived from NVIDIA's assets.

### Player builds

Add `Scenes/AGVFleet_LiveSync.unity` to the build. Relative paths (asset pack, stage folder) are resolved from the folder of the executable, or set `AGV_FLEET_ASSET_PACK` and `AGV_FLEET_STAGE_DIR`. Point `AGV_FLEET_STAGE_DIR` at the same folder for `run_agv_fleet.bat`. The scene references URP/Lit materials in `Settings/ShaderKeepAlive` so that the shader variants runtime-created materials need are kept in the build.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Console: factory USD not found, using the generated layout | The asset pack is not where **Asset Pack Path** points. Check that `.../Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd` exists under it. Unzipping into an extra folder level (`USD_Explorer_Sample/USD_Explorer_Sample_NVD@10011/Usd_Explorer/...`) is a common cause. |
| Environment set to Factory: error and no hall | Same as above; or set **Environment** to Auto or Generated. |
| The factory is huge, tilted or tiny | The sample's imports keep **Apply Stage Up Axis And Units** off: `UsdFrame` and the 0.01 scale on `/World/Environment` already convert the centimetre, Z-up file. Also check that **Factory Usd In Pack** names `Factory.usd` and not one of its parts. |
| Panel: `Isaac Sim offline` | Isaac Sim is not running, or is still starting. Wait for `[agv_fleet] running` in its console. Check that nothing else uses port 10010. |
| Panel: `Isaac Sim rejected the hub token` | Unity read an old `hub_token.txt`. Isaac Sim writes a new token into the stage folder on every launch; check that Unity and Isaac Sim use the same stage folder (`AGV_FLEET_STAGE_DIR`). |
| Panel: `Isaac Sim runs another environment` | Isaac Sim was started before you changed the environment, the seed or the generator parameters. Restart `run_agv_fleet.bat`. |
| Isaac Sim console: `ERROR environment file not found` | For the generated hall, enter Play mode in Unity once before starting Isaac Sim. For the factory, see [Use the NVIDIA factory](#use-the-nvidia-factory). |
| The generated hall does not appear in Isaac Sim | Isaac Sim loaded an older `environment.json`: restart it after Unity has written the file. `run_agv_fleet.bat --env factory` forces the factory and ignores the file. |
| Isaac Sim console: `WARNING ... loop waypoints inside keep-out` | A loop point in **Factory Layout** is too close to a wall or machine for the planner. Move it to open floor. |
| AGVs stop and report `blocked` | An obstacle closes their route: remove it, or select **Reset scene**. |
| Isaac Sim crashes during start-up | An Isaac Sim 6.1 issue, see [Connect Isaac Sim](#connect-isaac-sim). `run_agv_fleet.bat` retries; if every attempt fails, run it again. |
| Isaac Sim warns that the asset root is remote, slow start | Isaac Sim loads the AGV model from NVIDIA's cloud. That works, but a local Isaac Sim asset pack is faster; point Isaac Sim at it, or set `ISAAC_ASSET_ROOT`. |
| Pink materials | The project does not use URP. |

To check the Isaac Sim side without Unity, run `python probe_client.py --seconds 10` in `Tools~/agv_fleet`: it prints the stream rate and the latest state of each AGV.

## Tests

**Automated (no Isaac Sim needed).**

- Unity, EditMode: open **Window > General > Test Runner > EditMode** and run `AGVFleet.LiveSyncDemo.Tests` (36 tests):
  - generator: the same seed gives the same hall (byte-identical USD), lanes connected, every station reachable from every dock, no geometry within reach of a lane, everything inside the hall;
  - USD output: Z-up meters, `/GeneratedFactory` with its groups, collision on exactly the floor and every blocker, every bound material defined, imports through the USD Toolkit;
  - mode selection: a missing factory falls back to the generated hall with the download link, an explicit mode is respected, path resolution;
  - LiveSync contract: `environment.json` content, Isaac Sim hello matching, AGV colours, route graph, `UsdFrame`.
- Isaac Sim side: `python -m unittest discover -s tests -v` in `Tools~/agv_fleet` (Python 3.10 or later, standard library only, 13 tests): `environment.json` loading, colour palette, hub authentication.

**Manual (Isaac Sim 6.1, Windows).** Run each with the sample scene in Play mode and the Isaac Sim side started with `Tools~/agv_fleet/run_agv_fleet.bat`. The fleet panel on the left shows the simulator, and any problem in yellow.

| # | Setup | Do | Expect |
| --- | --- | --- | --- |
| 1 | Environment Generated, Simulation Auto, Isaac Sim not running | Enter Play mode | The hall appears; AGVs at their docks; panel: `Local fleet`, `Isaac Sim offline (127.0.0.1:10010): local fleet` |
| 2 | Continue from 1 | Start `run_agv_fleet.bat` | Console: `environment: from_unity (generated:... seed 1)`, `grid boxes`, `running: 3 AGV(s)`. Within a few seconds Unity switches to `Isaac Sim`, `● live 30 Hz`; the AGVs drive dock, pick, drop, dock |
| 3 | Continue from 2 | Place a box on an AGV's lane (tool 2), set a goal (tool 1), select **Reset scene** | The AGV re-plans around the box; the nearest free AGV drives to the goal; reset clears both |
| 4 | Continue from 2 | Close Isaac Sim | Unity falls back to `Local fleet` without errors; the AGVs are back at their docks |
| 5 | Isaac Sim running the generated hall | Exit Play mode, set **Seed** to 2, enter Play mode | Panel: `Isaac Sim runs another environment (... seed 1); restart Isaac Sim ...`; local fleet. Restart Isaac Sim: it loads seed 2 and Unity switches to it |
| 6 | Generated, Isaac Sim not running | Select **Regenerate** | A new hall (next seed); `generated_factory.usda` and `environment.json` replaced. The button is disabled while Isaac Sim drives |
| 7 | Environment Factory, asset pack unzipped | Enter Play mode, then start `run_agv_fleet.bat` | The factory imports (about 20 s); Isaac Sim console: `environment: from_unity (factory:Factory.usd)`, `grid projection`, colliders built; the AGVs drive their loops in Unity |
| 8 | Environment Auto, no asset pack | Enter Play mode | One log line: factory not found, using the generated layout, with the download link |
| 9 | Isaac Sim running | `python probe_client.py --seconds 10` in `Tools~/agv_fleet` | About 30 Hz and the per-AGV lag; a client without the token is refused (`rejected` in the Isaac Sim console) |

## Files

| Path | What |
| --- | --- |
| `Scenes/AGVFleet_LiveSync.unity` | The sample scene |
| `Runtime/AgvFleetScene.cs` (+ `.Ui.cs`) | The operator front end: picks the environment and the simulator, imports, draws the panel, writes the operator's layer |
| `Runtime/Environment/` | `IFleetEnvironment`, `FleetLayout`, `RouteGraph`, `EnvironmentSelector`, `EnvironmentFile` (`environment.json`), `FactoryEnvironmentProvider` + `FactoryLayout`, `GeneratedEnvironmentProvider` |
| `Runtime/Environment/Generated/` | `GeneratedFactoryBuilder` (the hall and its lanes, deterministic per `GeneratedFactoryParams`), `UsdaSceneWriter` (writes it as USD with UsdPhysics collision) |
| `Runtime/Backends/` | `IFleetBackend`; `IsaacHubBackend` (Isaac Sim over the hub), `LocalFleetBackend` (AGVs at their docks), `AutoFleetBackend` (switches between them) |
| `Runtime/` (other) | `AgvView` and `AgvModel` (the AGVs), `UnityOpsLayer` (`unity_ops.usda`), `HubClient`, `LatencyMonitor`, `EnvAnimation`, `UsdFrame`, `AgvFleetSettings` |
| `Settings/` | `AgvFleetSettings.asset`, `FactoryLayout.asset`, `ShaderKeepAlive/` |
| `Tests/` | EditMode tests |
| `Tools~/agv_fleet/` | Isaac Sim side: `run_agv_fleet.bat`, `launch_agv_fleet.py`, `config.py` (every tuning value), `agv_fleet/` (planner, fleet manager, path follower, hub), `probe_client.py`, `fake_unity.py` (a scripted Unity stand-in for soak tests), `analyze_latency.py`, `tests/` |

## Known limitations

- **Unity does not simulate the fleet.** Without Isaac Sim the AGVs stand at their docks, and obstacles and goals are only shown. When Isaac Sim stops during a session, the AGVs jump back to their docks.
- **Isaac Sim loads the environment when it starts.** After Regenerate, or after changing the environment, restart Isaac Sim.
- Unity and Isaac Sim must run on the same machine: they share the stage folder, the hub listens on loopback only, and the latency figures assume one clock.
- The factory's welding cell animates in Isaac Sim, and in Unity follows Isaac Sim's timeline once Isaac Sim has run; the planner treats the whole cell as a fixed obstacle. MDL-only material effects (procedural grid lines, clear coat, glass refraction) do not carry over to Unity.
- The factory is heavy for a runtime import: about 20 seconds and 13,000 meshes. The sample hides the roof, overhead structure and very small parts for the top-down view.
- Avoidance is priority-based re-planning without reservations or traffic rules. In a single-lane passage, a head-on jam resolves through repeated short back-ups.
- One goal at a time in the operator's layer; Isaac Sim queues up to three and drops more.
- The generated hall's floor is a plane collider: with a large box collider, the iw_hub's wheels sank through the floor in Isaac Sim 6.1.

## Security

The hub accepts connections from the same machine only, and every client must first send the access token: `AGV_FLEET_HUB_TOKEN` when set, otherwise a random token that Isaac Sim writes to `hub_token.txt` in the stage folder on every launch, readable by the current user only. If that file cannot be restricted to the current user, Isaac Sim stops instead of starting the hub. A client that sends anything else first, a wrong token, or nothing within 3 seconds is disconnected and receives nothing. The connection is plain TCP: the token keeps other local processes out; it does not encrypt the stream.

## License and notices

Unity USD Toolkit Package © 2026 Unity Technologies. Licensed under the Unity Companion License for Unity-dependent projects. See `LICENSE.md` at the package root. Third-party components are licensed under their own terms; see `ThirdPartyNotices.md`.

**No NVIDIA software or content is redistributed.** This sample contains no NVIDIA source code, binaries, models, scenes, textures or materials. The screenshot at the top of this page, which shows the NVIDIA factory, is documentation in the package's `Documentation~/images` folder and contains no scene data. The NVIDIA factory comes from the USD Explorer Sample Asset Pack, which you download from NVIDIA; the pack states that it is bound by the NVIDIA Omniverse terms. The AGV model in Isaac Sim, idealworks' iw_hub, comes from the Isaac Sim assets under its own license. The Isaac Sim side calls NVIDIA-provided Python APIs (`isaacsim`, `omni.*`, `pxr`) from an Isaac Sim installation that you obtain separately. NVIDIA Isaac Sim, NVIDIA Omniverse and the components and content they install are licensed to you solely by NVIDIA under NVIDIA's own terms. Unity grants no rights in any NVIDIA software or content. See <https://developer.nvidia.com/isaac/sim> and <https://www.nvidia.com/en-us/agreements/>.

The linked video is hosted on YouTube and is not part of this package.

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
