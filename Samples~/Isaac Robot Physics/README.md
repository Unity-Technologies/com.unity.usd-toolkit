# Isaac Robot Physics

![The Robot Physics panel in Play Mode: NVIDIA's Franka Panda imported with its physics, its joints driven from the panel's sliders and by the limit test](../../Documentation~/images/robot_physics_hero.gif)

Imports an NVIDIA Isaac Sim robot with its physics and checks the result against what Isaac Sim
shows. For every joint and body the importer builds, the sample puts the value the USD stage
authors, which is the number in Isaac Sim's Property panel for that prim, next to the value the
Unity component holds, read back into the same units and sign. In Play Mode you can drive the joints,
and a limit test pushes every limited joint past both of its limits and records where the physics
stops it.

The physics itself comes from the package: `UsdImportOptions.ImportPhysics` (**Import Physics** on
a USD asset). See [Physics](../../Documentation~/Unity%20USD%20Toolkit%20User%20Manual%20EN.md#physics)
in the User Manual for how each schema maps.

## Robots

| Robot | Where it comes from |
|---|---|
| **Bundled arm** (`Robots/isaac_style_arm.usda`) | Ships with the sample. It's laid out the way Isaac Sim robots are: a `PhysicsArticulationRootAPI` prim whose links are siblings, each joint under its parent link, a fixed joint to the world, angular drives in Isaac Sim's per-degree units, a guide-purpose collision mesh with a convex hull, a physics material and mass properties. Beside it there's a pendulum hinged to the world (a `Rigidbody` and `ConfigurableJoint`) and a static ground. |
| **Franka Panda** | NVIDIA's asset from the Isaac Sim 6.0 asset server, downloaded the first time you choose it (29 files, about 29 MB). `Robots/franka_panda_isaac_6.0.files.txt` lists every file with its size and SHA-256. A file that doesn't match is deleted and nothing is imported. The files keep the server's folder layout, because the robot's layers reference each other by relative path, and they're stored in `franka_panda_isaac_6.0` under the **download location**:
`Application.persistentDataPath/IsaacRobots` unless you choose another folder (see below). The asset is licensed by NVIDIA under the Apache License 2.0, and its `LICENSE` is downloaded with it. |
| **Any file** | **USD file** in the Play Mode panel, **Import file…** in the window, or `RobotPhysicsDemo.StartWith = File` with `FilePath`. |

### Download location

Downloaded robots go into a folder of their own under the download location. Change it in the
Play Mode panel or the **Robot Physics** window: type a path, choose **Browse…** (in the Editor), or
**Default** to go back to `Application.persistentDataPath/IsaacRobots`. Both places share the setting,
which is kept in `PlayerPrefs`, and in code it's `RobotPhysicsImport.DownloadRoot`. A robot already
in the chosen folder, with every file's digest matching, isn't downloaded again.

## Use it in the Editor

1. Import the sample from the Package Manager (**Unity USD Toolkit > Samples > Isaac Robot Physics**).
2. Choose **USD Toolkit > Robot Physics**, set the **Download location** if you like, and select
   **Import bundled arm**, **Download and import Franka Panda**, or **Import file…**. The robot is imported into the open scene, and the window lists
   every compared value with a check mark or a cross. **Differences only** filters the list, and
   **Save CSV…** writes the table to share.
3. Select any robot imported with physics to see its table.

## Use it in Play Mode or a player

Open `RobotPhysics.unity` and press Play. The **Robot Physics Demo** object imports the robot named
in `StartWith` (the bundled arm by default), sets the stage's gravity and step rate
(`UsdPhysicsScene.Apply`), and frames the camera. A panel on the left holds the controls. **Hide**
(or **H**, `TogglePanelKey`) folds it to a button, and the camera draws beside the panel while it's
shown (`DockCamera`). From top to bottom, the panel has:

- **Robot**: choose the bundled arm, the Franka Panda or a USD file (type its path, or **Browse…** in
  the Editor), then **Import**. For the Franka it says whether it's in the download location yet, and
  the button reads **Download and import** until it is.
- **Download location**: where downloads go (see above). **Open folder** shows it. Select the
  heading to fold it.
- The status of the import, download or limit test, and three tabs:

- **Isaac Sim vs Unity**: the comparison table and the import's notes.
- **Drive joints**: a slider per revolute or prismatic joint, within its limits. The thumb and the
  blue fill set the drive target, and the orange marker shows where the joint is now.
- **Limit test**: with gravity off and the robot's contacts with the rest of the scene off, drives
  each limited joint 15 degrees (or 20% of its travel) past its upper and then its lower limit. It
  waits until the joint holds still and records where it stopped. A row passes within 1.5 degrees
  (or 2% of the travel).

The scene runs in a player too, where the file and folder fields take typed paths (**Browse…** is
Editor-only): a build that includes `RobotPhysics.unity` copies the bundled arm to
`StreamingAssets/IsaacRobotPhysics`, and the player downloads the Franka itself.

## What is compared

| Row | Isaac Sim (authored) | Unity (read back) |
|---|---|---|
| Axis (stage) | The joint axis in stage space: body0's rotation times `localRot0`, applied to `physics:axis` | The `ArticulationBody` anchor's X axis, taken back to stage space |
| Lower / Upper Limit | `physics:lowerLimit`, `physics:upperLimit`, in degrees or stage units | `xDrive.lowerLimit` / `upperLimit`. They're the same numbers: the anchor is turned so a positive USD angle is a positive Unity angle |
| Drive Stiffness / Damping | `drive:<axis>:physics:stiffness` / `damping`, per degree for a rotation | `xDrive.stiffness` / `damping`, per radian, divided by 57.3 to compare. The **Unity (native)** column shows the value Unity holds |
| Drive Max Force, Target Position, Type | `maxForce`, `targetPosition`, `type` | `xDrive.forceLimit`, `target`, `driveType` |
| Mass, Center of Mass, Diagonal Inertia | `PhysicsMassAPI` | `ArticulationBody` / `Rigidbody` mass, `centerOfMass`, `inertiaTensor` |

Values are in the stage's units (the Franka's are meters and kilograms), and stage space is USD's
right-handed space. The **Bundled arm** and **Franka Panda** rows all match. The limit test stops
every joint within 0.1 degree (0.1 mm for the finger) of the limit Isaac Sim lists.

## What differs from Isaac Sim, and why

- **Mimic joints.** The Franka's `panda_finger_joint2` follows `panda_finger_joint1` through
  `PhysxMimicJointAPI` (gearing -1, so both fingers open together). PhysX holds that with a
  constraint and Unity has no joint coupling, so the import gives `panda_finger_joint2` a drive with
  `panda_finger_joint1`'s gains, and a `UsdPhysicsMimicJoint` moves its target with
  `panda_finger_joint1` every physics step. It follows within a step, but as a drive it can yield
  under load. **Drive joints** shows it without a slider (move `panda_finger_joint1`), the table
  has a **Mimic** row, and the limit test skips it, since the stage authors no drive on it.
- **Placeholder inertia.** The Franka (Isaac Sim 6.0) authors `physics:diagonalInertia` of about
  1e-6 kg·m² for links of 2 to 3 kg. That's a thousand times below what the links' shapes give, and
  every center of mass is at the link's origin. The import keeps those values, so the comparison
  matches, and its notes flag every such body. Unity's PhysX can't keep a driven articulation with
  that inertia stable (`panda_joint1` swings past its limits), so
  `RobotPhysicsDemo.SimulateWithColliderInertia` (on by default) simulates those links with the
  inertia derived from their colliders. The table is built before that, so it still shows the import.
- **Solver.** Isaac Sim simulates with PhysX 5 and the TGS solver; Unity 6 uses PhysX 4.1. Static
  values match exactly. Motion under the same drives is close, but not step-for-step identical.
- **Joints outside an articulation** become a `ConfigurableJoint`. Its angular limits are held to
  ±177 degrees, and drives on it aren't applied.

## Files

| File | What it is |
|---|---|
| `RobotPhysics.unity` | The Play Mode scene: a camera, a light and the **Robot Physics Demo** object. |
| `Scripts/RobotPhysicsDemo.cs` | The Play Mode panel (robot picker, download location, drive sliders, limit test). |
| `Scripts/RobotPhysicsReport.cs` | Builds the comparison from `UsdPhysicsJoint` / `UsdPhysicsBody` and writes it as CSV. |
| `Scripts/RobotAssetDownload.cs` | Downloads an asset as a manifest lists it and checks each file's SHA-256. |
| `Scripts/RobotPhysicsImport.cs` | The import options (`ImportPhysics`, `AllowExternalAssetPaths`), the download location and camera framing. |
| `Editor/RobotPhysicsWindow.cs` | **USD Toolkit > Robot Physics**. |
| `Editor/RobotPhysicsBuildStep.cs` | Copies the bundled arm to StreamingAssets for a player build. |
| `Robots/` | The bundled arm and the Franka manifest. |
