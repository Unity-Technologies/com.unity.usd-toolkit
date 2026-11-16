# SimReady Central Loader

Load the SimReady assets that NVIDIA's [SimReady Central](https://huggingface.co/spaces/nvidia/simready-central)
Space lists on Hugging Face. Copy an asset's file link in the Space (**Copy URL**), paste it here, and
the sample downloads the asset with everything it composes from. It imports the asset through the
toolkit with its UsdPhysics mapped to Unity physics, and gives every moving part a drag handle like
the Space's **Interactive USD** preview. This works in edit mode (no Play needed), in Play Mode,
and in a built player.

![The runtime viewer: the Comau N-220-27 arm loaded from SimReady Central, its joints moved with the drag handles and the sliders in the panel](../../Documentation~/images/simready_central_hero.gif)


Nothing is bundled: every asset is downloaded on demand, from its own repo, under that repo's licence.

## Use it in the Editor

1. Import the sample from the Package Manager and open **USD Toolkit > SimReady Central Loader**.
2. Paste a link and press **Download & import**, or press **Try the OnRobot RG2**, which fills in the RG2 link and loads it. The progress bar
   shows files and bytes; **Cancel** stops the download, and the next attempt resumes where it stopped.
3. The asset appears in the open scene, framed in the Scene view. The window shows it in cards:
   - **Variants**: one dropdown per variant set of the default prim. Changing one re-imports the
     asset with that selection. The **Physics** set starts at the layer's authored selection, which
     is what the Space previews.
   - **Info**: the package's thumbnail, name and author. Pills show the type, category, licence
     and SimReady profile; tiles show size (from `asset_extents`), mass, moving parts and rigid
     bodies. Below them are the source repo and commit (click it to open the repo) and the full
     `customLayerData`.
   - **Moving parts**: one row per user-facing joint, with its value and a slider over its range.
   - **Physics**: a count per status (click one to filter) above every physics prim and PhysX
     attribute and what became of it (see below). The same list goes to the Console.
4. Drag the orange handles in the Scene view, or use the sliders. Edit mode poses the links
   kinematically (forward kinematics on the transforms, no physics step) and records Undo.
5. Press Play: the links go back to the pose the import built, Unity's joints start from it, and
   each handle starts at the value it was left at, held there by its drive.

## Use it in Play Mode or a player

Open `SimReadyCentral.unity` and press Play, or build it. The panel on the left has the same cards
as the Editor window: the link, the asset with its thumbnail, pills and tiles, variants, moving
parts and the physics log. Both are built from `Scripts/Viewer/SimReadyUiKit.cs`, so they look
alike. A **Simulation** card adds **Pause/Play** (`Time.timeScale`), **Reset to rest** and
**Kinematic** (see [Kinematic mode](#kinematic-mode)). In the
viewport:

- Drag a handle to move its joint. This sets the leader's drive target, and the physics moves the
  coupled linkage, so contacts and drive gains matter.
- Left-drag elsewhere to orbit, right/middle-drag or Shift-drag to pan, and use the wheel to zoom.
  The camera frames the asset when it loads; a grid and a contact shadow mark the floor.

An asset loaded in edit mode is picked up when play starts. The scene holds one SimReady asset at
a time: a new load replaces the one there, whether the Editor window, the runtime panel or a
script loads it, and every view switches to it. In edit mode the replaced asset comes back with
Undo; in Play Mode, an asset imported in edit mode is back when you exit Play. A player started with
`-simready-link <url>` loads that link at once.

The viewer is plain UI Toolkit and reads pointer events itself, so it needs no input package. It
renders in URP and the Built-in pipeline.

In URP, a build keeps only the shader variants that its materials use, but the importer creates its
URP/Lit materials at runtime. `Resources/SimReadyShaders/SimReady_URP_Lit_ImporterVariants.shadervariants`
keeps every keyword combination the importer sets (normal, metallic, occlusion and emission maps;
opaque, cut-out and transparent). Without it, a player draws those materials with the nearest
variant it has: maps go missing and glass comes out opaque, although the Editor looks right. If the
importer gains a keyword, extend `Editor/SimReadyShaderIncludes.cs` and run
**Tools > SimReady Central > Regenerate Shader Variants**.

## Use it in your own scene

Choose **GameObject > SimReady Central > Viewer**. It sets the open scene up as
`SimReadyCentral.unity` has it:

- the main camera gets `SimReadyOrbitCamera` (orbit, pan, zoom, framing) and `SimReadyStageDecor`
  (grid, contact shadow); a camera is created if the scene has none;
- a directional light is added if the scene has no light;
- a **SimReady Viewer** object gets a `UIDocument` with the sample's `UI/SimReadyPanelSettings`,
  whose theme (`UI/SimReadyTheme`) provides the panel's fonts and controls, and `SimReadyViewer`.

To set it up by hand, give the UIDocument those Panel Settings and leave its Source Asset empty:
the viewer builds the panel in code. If the Panel Settings are missing, the Console says so at
Play. If the camera has no `SimReadyOrbitCamera`, the viewer adds one.

## Supported links

| Form | Example |
| --- | --- |
| `https://huggingface.co/{datasets\|models}/{owner}/{repo}/blob/{revision}/{path}` | what **Copy URL** gives |
| `…/resolve/{revision}/{path}`, `…/raw/{revision}/{path}` | direct file links |
| `…/tree/{revision}/{folder}` | the package's root layer is found from its `.metadata/com.nvidia.simready.root_usds.json`, else the single shallowest layer |

- `{revision}` may be a commit, branch or tag. A branch or tag is resolved to its commit through
  the Hub API before anything is downloaded; the cache is keyed by repo, commit and path.
- The link must name a `.usd`, `.usda` or `.usdc` file (or a folder, for `tree`). The query and
  fragment are ignored.
- Rejected, each with the reason: `http://`, any other host, ports and user info, other URL kinds
  (`spaces`, `commits`, …), `.`/`..` segments (also percent-encoded), and `hf://` addresses.

## What gets downloaded, and from where

The walk starts at the root layer and reads each layer without composing it: `UsdLayerInfo` for
text and binary layers alike. Every sublayer, reference, payload of every variant (selected or
not), texture and MDL module the layer names is resolved relative to that layer and fetched. Only
what the layers reference is downloaded: for the NVIDIA assets that is about half the folder (their
`source_assets/` and duplicate textures are skipped). The Hub's tree listing is used for sizes and
hashes, and as a cross-check; the info panel counts the files nothing referenced.

Every asset path is checked before it is fetched. A path is refused, and listed in the download
notes, when it is absolute, uses another scheme (`https:`, `omniverse:`, `file:`), is a template
(`<UDIM>`), is package-relative, climbs out of the asset's folder in the repo (`onrobot_rg2/`), or
is not a data type (USD layers, images, MDL, JSON and text only). The local path is checked again
after the OS normalises it.

**Security.** These rules hold for every request:

- **HTTPS only**, to hosts on an allowlist, checked on the first request and on every redirect hop
  (redirects are followed by hand). The defaults are the hosts a download was seen to use on
  2026-11-04: `huggingface.co`, which serves small files and redirects large ones, and
  `us.aws.cdn.hf.co`, the Xet/LFS CDN.
- **The allowlist is yours to edit** under **Download settings**, for a region whose CDN host
  differs. Hosts match exactly; wildcards, IP addresses and local names are refused.
- **Size caps:** 256 MB per file and 2 GB in all by default, editable up to fixed ceilings. A
  download is aborted as soon as its Content-Length or the bytes received pass the cap.
- **Integrity:** every file must match the size and hash the Hub lists for it: the SHA-256 for LFS
  files, otherwise the git blob SHA-1. A mismatch is deleted. A file whose first bytes look like a
  program (PE, ELF, Mach-O, `#!`) is deleted however it is named.
- **No token:** public assets need none, and the sample sends none. Gated or private repos are
  refused with a message.
- **The cache** is `Library/SimReadyCache` in the Editor, outside `Assets/`, so nothing is
  reimported. In a player it is `Application.persistentDataPath/SimReadyCache`. Once a commit's
  walk has completed, loading it again needs no network. **Clear download cache** deletes it.

## Physics mapping

![A humanoid in kinematic mode: the hand's finger joints posed one by one with their handles while the body stands still](../../Documentation~/images/simready_central_physics.gif)

The import is the toolkit's own (`UsdImporter` with `ImportPhysics`); composition is not flattened.
Z-up stages are turned Y-up on the root, and `metersPerUnit` and `kilogramsPerUnit` are applied.

| UsdPhysics | Unity | Status |
| --- | --- | --- |
| `RigidBodyAPI` under an `ArticulationRootAPI` | `ArticulationBody` (one tree; the root immovable when fixed to the world) | mapped |
| `RigidBodyAPI` elsewhere | `Rigidbody`, kinematic flag and enabled state kept | mapped |
| `MassAPI` mass, centre of mass, diagonal inertia, principal axes | the body's | mapped |
| no mass on the body | the sum of its colliders' authored masses | approximated (noted) |
| no mass anywhere | density × collider volume (body, collider or material density, else 1000 kg/m³) | approximated |
| `convexHull` | convex `MeshCollider` | mapped |
| `convexDecomposition` | up to 8 convex `MeshCollider`s, one per connected part or half | approximated (not V-HACD) |
| `sdf` (PhysX SDF) | convex `MeshCollider` | approximated |
| `boundingCube` / `boundingSphere` | `BoxCollider` / `SphereCollider` | mapped |
| `none` | non-convex `MeshCollider` on static colliders; convex on moving bodies (Unity needs that) | mapped / approximated |
| `Cube`, `Sphere`, `Capsule` | matching primitive collider | mapped |
| `Cylinder`, `Cone` | convex `MeshCollider` | mapped |
| `PhysicsMaterialAPI` | `PhysicsMaterial` with combine modes | mapped |
| Revolute, Prismatic, Spherical, Fixed joints in an articulation | the link's `ArticulationBody` joint, limits and `DriveAPI` (degrees and metres converted) | mapped |
| D6 (`PhysicsJoint` with per-axis limits) | the single-axis joint its free axes amount to | mapped / approximated |
| Revolute or prismatic outside an articulation | `ConfigurableJoint` with its drive | approximated (D6 spring) |
| Loop-closing joints | joint outside the articulation where Unity allows it, else skipped | approximated / skipped |
| `PhysxMimicJointAPI` | `UsdPhysicsMimicJoint`: the follower's drive target tracks `−gearing · leader − offset` | approximated |
| `physxJoint:armature` | added to the articulation link's inertia (Unity has no armature) | applied (approximated) |
| `PhysxSchema` attributes | applied (solver iterations, damping, CCD, friction, …), recorded (max joint velocity), or logged (no Unity equivalent) | per attribute |
| no solver iterations authored | PhysX's defaults: 16 per rigid body, 32 per articulation (Unity's are 6) | mapped |
| approximated colliders overlapping at the authored pose | that pair filtered (the overlap is the approximation's, not the asset's) | approximated |
| Distance joints, tendons, custom shapes | not built | skipped (logged) |

### Kinematic mode

Some assets can't hold themselves up in Play Mode. A humanoid whose joints have no drives
collapses as soon as the physics runs. Turn on **Kinematic**, in the runtime panel's Simulation
card or the Editor window's Moving parts card (`SimReadyAsset.Kinematic` from a script), and Play
Mode poses the asset as edit mode does:

- The articulations are disabled, rigid bodies made kinematic, and mimic drives stopped. No floor
  is added.
- A handle places its links directly, followers included, with no physics step.
- It can be switched while playing. Turned off, the asset goes back to the pose Play Mode started
  from and the drives hold each handle's value. Turned on, it keeps the values the physics left;
  **Reset to rest** stands it back up.

The choice is saved with the scene, and the next asset loaded takes the last one made
(`SimReadyAsset.KinematicByDefault`).

## Handles

A handle is a user-facing joint: revolute or prismatic, not locked, and not a mimic follower. Its
followers move with it. The OnRobot RG2 (14 bodies, 10 revolute joints, nine of them mimic
followers of `finger_joint`) and the 2FG14 (one prismatic follower) each show exactly one handle.

- **Label:** the joint's `displayName`, else its prim name made readable. "joint", the asset's name
  and zero counters are dropped, so `joint_drawer_01_joint_00` reads "Drawer 01". A leader whose
  followers move both sides of the asset takes their shared word in the plural, so both grippers'
  leaders read **Fingers**.
- **Range:** the authored limits, or ±180° (±the asset's size for a prismatic joint) when there are none.
- **Value:** whole degrees, or whole millimetres for prismatic joints.
- **Drag:** the mouse motion is projected on where the joint moves the handle on screen: the
  prismatic axis, or the tangent of the revolute arc at the handle. The result is clamped to the
  range. When that motion is edge-on to the view, vertical mouse motion drives the joint instead.
- **Play Mode:** a joint whose drive has no stiffness (most doors and drawers) gets a spring
  towards the handle only while it is dragged. Released, it is free again.

## Differences from the Space preview

The preview runs PhysX 5 in Isaac/RTX; Unity runs its own PhysX integration, so the target is
matching behaviour, not identical simulation.

- **Handle labels and ranges.** The Space takes them from a joint description its own server
  generates, not from the USD. The sample derives them from the USD:
  - The labels come out the same for the OnRobot grippers ("Fingers").
  - The ranges are the authored limits. RG2 matches the Space's 0–70°.
  - The 2FG14 handle runs 0–25 mm, the USD limits, where the Space showed about 11–25 mm. The
    Space's lower end is its own "home" value, which the asset does not author.
- **Which joints get a handle.** Every movable joint that does not follow another gets one, driven
  or not (a free cabinet door or an undriven oven door included). The Space may show fewer.
- **Mimic joints are drives, not constraints.** PhysX holds a mimic joint exactly; Unity has no
  joint coupling, so the follower's drive tracks the leader. It lags a step and yields under load.
  Edit-mode posing applies the coupling exactly.
- **`convexDecomposition` is approximated:**
  - Shells are split by connected part, and a single hollow part by halves, up to 8 pieces.
  - A piece's hull can still cover a concavity, so fingertip contact can differ.
  - `sdf` colliders become one convex hull each.
- **Joints outside an articulation** (most props) are `ConfigurableJoint`s with D6 drives, not
  reduced-coordinate articulation joints, so they are softer under load.
- **Armature** is added to the link's inertia, which is close to, but not the same as, PhysX's
  joint-space armature. Maximum joint velocity is recorded but not applied, and other PhysX-only
  attributes are logged, not applied (see the physics log).
- **A floor in Play Mode.** Props that nothing fixes to the world (most NVIDIA and LG assets)
  stand on a static floor at their lowest point (`SimReadyAsset.GroundInPlayMode`); without one
  they would fall. An undriven part on a horizontal hinge (the kettle's switch) still drops to its
  limit under gravity, as it does in Isaac.
- **Approximated colliders that overlap at rest are filtered.** A convex hull for an SDF or a
  decomposed mesh can fill a hollow its neighbours sit in. Those pairs stop colliding with each
  other, so parts that touch only through such an overlap do not contact as they would in Isaac.
- **Rendering** is the toolkit's material import, not RTX. Use the Reference Look sample for a
  closer match.

## Platforms

Windows, macOS and Linux, in the Editor and in a player. The dependency walk reads binary
(`.usd`/`.usdc`) layers through `UsdLayerInfo`, which needs the native plugin at interface 18. The
Windows, macOS and Linux payloads all have it (see `BUILD_NOTES.md`). With an older plugin the
walk scans text layers itself, and for a binary layer it fetches every data file in the root
layer's folder. The download notes say when this happens, and the OnRobot assets (text layers
apart from their geometry) are unaffected.

## Licences

Each asset's licence is set by its repo (for example BSD-3-Clause for the OnRobot grippers and
CC-BY-4.0 for NVIDIA's props), shown in the info panel from `asset_license`, and shipped as the
package's `LICENSE.txt`, which the walk downloads with the asset. Read it before you use or
redistribute an asset. The sample's code is under this package's licence.

## Tests

`Tests/Editor` and `Tests/Runtime` (Test Runner, EditMode and PlayMode) run on the synthetic
fixtures in `Tests/Fixtures~`, never on downloaded assets. They cover:

- link parsing and rewriting, path resolution and traversal checks, and the host policy;
- the dependency walk (variant payloads, hashes, resume, caps, the cache), served by a fake Hub;
- unit and axis conversion, the physics mapping and its log;
- the coupling maths, kinematic posing against USD's own rotation, the drives, and the runtime viewer.

`SimReadyCentralAcceptanceTests` checks the acceptance criteria against the real assets: RG2,
2FG14, a cabinet, a kettle and an LG range. It downloads, so it runs only when asked: select it,
or pass `-testCategory Network`.

## Files

| Path | What |
| --- | --- |
| `Scripts/SimReadyLink.cs` | link parsing and rewriting |
| `Scripts/SimReadyAssetPaths.cs` | asset path resolution, traversal and data-type checks |
| `Scripts/SimReadyHostPolicy.cs` | the allowlist and size caps |
| `Scripts/SimReadyTransport.cs` | `UnityWebRequest` transport: manual redirects, caps, range resume |
| `Scripts/SimReadyDownloader.cs` | revision, tree listing, dependency walk, hashes, cache |
| `Scripts/SimReadyLayerScanner.cs` | a layer's asset paths and metadata (`UsdLayerInfo`, or a text scan) |
| `Scripts/SimReadyLoader.cs` | download, import, variant switching |
| `Scripts/SimReadyAsset.cs` | the joint model both front-ends share |
| `Scripts/SimReadyJointMath.cs` | labels, units, coupling, limits, drag projection |
| `Scripts/SimReadyMetadata.cs`, `SimReadyPhysicsReport.cs` | the info panel and the physics log |
| `Scripts/Viewer/` | runtime viewer: UI Toolkit panel, gizmo, orbit camera, grid and shadow |
| `Editor/SimReadyCentralWindow.cs`, `SimReadySceneGizmo.cs` | Editor window and Scene-view handles |
| `Editor/SimReadyShaderIncludes.cs`, `Resources/SimReadyShaders/` | URP/Lit variants kept in a player build |
| `SimReadyCentral.unity`, `UI/` | demo scene, panel settings and theme |
