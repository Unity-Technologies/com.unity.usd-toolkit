# Changelog

All notable changes to the Unity USD Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Import warnings for unknown prim types and stages with no geometry**
  An import now warns once when the stage holds prims whose type no loaded schema defines (a plugin
  schema this build does not register): the warning gives their count and up to five prim paths, as
  only their place in the hierarchy is imported. It also warns once when the import yields no mesh.
  Both name only the file and prim paths. They reach the Console from `UsdImporter.Import` and
  `ImportAsync`, and a USD asset shows them as import warnings, once each. What gets imported is
  unchanged. Unknown types are found through the Stage API, so a native plugin older than 0.8.1 does
  not report them.

## [0.8.1-exp.1] - 2026-10-22

### Added

- **Physics on import (`UsdImportOptions.ImportPhysics`, 0.8.1 native plugin)**
  The stage's UsdPhysics, read with OpenUSD's own parser (PhysX attributes included), is built as
  Unity physics. Each articulation becomes an `ArticulationBody` tree: links an Isaac Sim robot keeps
  as siblings are moved under their parent link, world pose kept, and the root is immovable when a
  joint fixes the base. Revolute and prismatic limits carry over as authored, and the anchor is turned
  so a positive USD angle is a positive Unity angle. Drive stiffness and damping go from Isaac Sim's
  per-degree units to PhysX's per-radian units. Other rigid bodies become `Rigidbody` with
  `FixedJoint` / `ConfigurableJoint`, and colliders (convex approximations and guide-purpose
  collision meshes included), physics materials, self-collision, filtered pairs and collision groups
  are built too. A `UsdPhysicsJoint` / `UsdPhysicsBody` next to each keeps the authored values, with
  `TryReadUnityValues` to compare against. `UsdImportResult.PhysicsNotes` lists what Unity cannot
  hold: tendons, distance joints, and an inertia far below what the colliders give. A mimic joint
  (`PhysxMimicJointAPI`, as on the Franka's second finger) follows its reference joint through a
  drive that a `UsdPhysicsMimicJoint` moves every physics step, with PhysX's gearing and offset.
  **Import Physics** is the same option on a USD asset. Off by default.

- **Isaac Robot Physics sample**
  `Samples~/Isaac Robot Physics` imports a robot with its physics and puts what Isaac Sim's Property
  panel shows for every joint and body next to what Unity built. It also drives the joints and runs a
  limit test in Play Mode. It ships a small Isaac-style arm and downloads NVIDIA's Franka Panda
  (Isaac Sim 6.0, Apache 2.0) on request, each file checked against a pinned SHA-256. All 109 of the
  Franka's compared values match, and every limit holds within 0.1 degree of Isaac Sim's. The Play
  Mode panel sits on the left and can be hidden (**H**). It has a robot picker (bundled arm, Franka,
  or any USD file) and a download location, which the **Robot Physics** window shares.

- **Recordings as overrides on the imported stage**
  `UsdAnimationRecorder.Save` with `Composition = UsdCompositionMode.Preserve` writes a recording
  of an imported stage as a layer over its source file (sublayered, or referenced when the import
  sits below the recorded object) with the samples on the source's own prims: transforms and
  visibility as time samples, and an imported character's Skeleton prim a new `SkelAnimation`.
  Nothing of the source is copied, so an Isaac Sim robot's geometry and physics stay with the
  asset: Franka FR3 (17 MB) records 61 frames into 101 KB instead of 19.8 MB. The Unity Recorder's
  USD Animation recorder has an **Overrides Only** setting for it. Flattened stays the default.

- **Remote MDL materials from NVIDIA's library are downloaded and imported (0.8.1 native plugin)**
  Isaac Sim stages often bind NVIDIA's material library by URL
  (`https://omniverse-content-production.s3.us-west-2.amazonaws.com/Materials/.../Base/...mdl`).
  The resolver cannot open a URL, so those surfaces imported default grey (the SO-101 lightbox's
  Paper mat and Rubber_Smooth parts). `UsdImportOptions.DownloadRemoteMaterials` (off by default: the import reaches the network only when the caller turns it on)
  fetches such a module and the textures it names into `RemoteMaterialCacheFolder`
  (`Application.temporaryCachePath/UsdRemoteMaterials` by default) and reads the stage again; a
  module that is one `OmniPBR(...)` call is then imported as that OmniPBR, its arguments standing
  in for every input the stage does not author. Only HTTPS on NVIDIA's content hosts is fetched,
  only `.mdl` modules and the image files beside them, each within a size limit; the native layer
  itself never downloads (`RUsd_SetRemoteAssetCache`, `RUsd_GetImportRemoteRequestCount`,
  `RUsd_GetImportRemoteRequest`).

- **AGV Fleet LiveSync Demo sample**
  `Samples~/AGVFleet_LiveSync Demo` ports the AGV fleet LiveSync booth demo into the package. Unity
  is the operator front end; NVIDIA Isaac Sim 6.1.0 simulates the fleet with physics, plans the paths
  and manages the fleet; both open one USD stage, with one writer per prim and a separate layer per
  side. Unity places obstacles and goals in its own layer (`unity_ops.usda`); Isaac Sim streams AGV
  poses, paths and assignments back at 30 Hz.

- **AGV Fleet sample: NVIDIA factory or a generated warehouse hall**
  The hall is NVIDIA's Factory scene from the USD Explorer Sample Asset Pack, which users download
  from NVIDIA and unzip next to `Assets` (no NVIDIA content ships with the sample), or a warehouse
  hall the sample generates: racks with aisles, pick and drop stations, charging docks and obstacles,
  deterministic per seed. The generated hall is written as USD with UsdPhysics collision and imported
  through this package, and its lanes come from the same generator as its geometry, so the AGVs'
  routes always match it. With the environment on Auto the sample uses the factory when it finds the
  file and the generated hall otherwise, with one log line that links the download.

- **AGV Fleet sample: Isaac Sim follows Unity's environment**
  Unity writes `environment.json` to the shared stage folder (the environment's USD file, planning
  area, spawn poses, loops and, for the generated hall, the exact obstacle boxes); the Isaac Sim side
  loads it at launch, so both always run the same environment. In Auto mode Unity hands the fleet to
  Isaac Sim as soon as it streams that environment and back when it stops, in either start order, and
  ignores an Isaac Sim that runs another one. Regenerate builds the next seed while Isaac Sim is not
  driving.

- **AGV Fleet sample: authenticated hub**
  The Isaac Sim hub requires a token, as in the Live Sync Example: `AGV_FLEET_HUB_TOKEN`, or one
  written per launch to an owner-only `hub_token.txt`; clients that do not authenticate first, or
  within 3 seconds, are dropped and receive nothing.

- **AGV Fleet sample: tests**
  36 EditMode tests (generator determinism, lane connectivity and clearance, the generated USD and
  its collision, environment selection, the `environment.json` contract) and a standard-library
  Python suite for the Isaac Sim side (`Tools~/agv_fleet/tests`: environment loading, colours, hub
  authentication).

- **Documentation for the AGV Fleet sample**
  The package README lists the sample with a thumbnail and a video link, the user manual has a
  section for it, and `ThirdPartyNotices.md` describes its Isaac Sim integration, the user-downloaded
  asset pack and the documentation screenshot that shows the factory.

- **Runtime Demo: animated sample**
  `USDSamples/plm_indexer_anim.usda` has a default prim with its own translate and rotate and a
  time-sampled turntable and clamp (0–48 at 24 fps), so a referencing export shows both fixes below.

- **Lights, cameras, UV sets 1-2, vertex colours and OmniPBR / MaterialX on export (0.8.1 native plugin)**
  The export side of what import already reads. `UsdExportOptions.ExportLights` writes each Unity
  `Light` as a UsdLux light (directional to `DistantLight`, point and spot to a normalized
  `SphereLight` with a `ShapingAPI` cone, rectangle to `RectLight`, disc to `DiskLight`, with
  `ShadowAPI` and colour temperature), its intensity divided by the factors
  `UsdImportOptions.PhysicalLightUnits` multiplies by, so a light imports back as it was.
  `ExportCameras` writes each `Camera` as a `UsdGeomCamera` (a physical camera keeps its focal
  length, sensor and lens shift; any other gets a 24 mm aperture and the focal length of its field
  of view; orthographic size in the importer's tenths-of-a-unit apertures). Both sit on a child
  prim turned 180 degrees about Y, the importer's helper convention, and lights and cameras the
  importer itself created are skipped. `ExportUvSets` writes `Mesh.uv2` / `uv3` as
  `primvars:st1` / `st2`, the names the importer reads back, and `ExportVertexColors` writes
  `Mesh.colors` as per-vertex `displayColor` / `displayOpacity`. All four are on by default.
  `MaterialFlavors` (none by default) adds an OmniPBR MDL shader under `outputs:mdl:*` and a
  MaterialX 1.39 `standard_surface` under `outputs:mtlx:surface` beside the UsdPreviewSurface,
  with colour, metallic, roughness, normal map, emission, opacity and UV tiling; Unity's packed
  metallic/smoothness map stays in the UsdPreviewSurface only, because neither has an input that
  reads it. Verified by exporting from Unity and importing the file back: directional, point and
  spot lights, perspective, physical and orthographic cameras and both UV sets return with the
  same values and directions. A rectangle light returns as the importer's cookie point light,
  carrying intensity x area, since URP has no real-time area light. The MaterialX nodes and inputs
  match the MaterialX 1.39 library the payload ships. Needs the 0.8.1 native plugin; with an older one these,
  the albedo mask and the Unreal-connector blend stay off, with a warning when one was asked for.

- **Reference Look sample** (Package Manager > Samples > Reference Look)
  A URP render setup fitted against Isaac Sim 6.2's RTX Real-Time 2.0 renderer on NVIDIA's
  Factory stage: a Deferred renderer with Surface Cache GI, screen-space reflections and GTAO,
  4096 shadow maps in 4 cascades to 150 m, and a volume profile with the tonemapper off, post
  exposure -4.2 and Isaac Sim's measured tone curve as a 64-step colour lookup (the curve and a
  menu to rebuild the LUT ship with it). `USD Toolkit > Import into Reference Look scene...` assigns
  the pipeline, opens the sample scene (camera and global volume, no lights), imports a USD file
  with the importer's defaults, waits for the reflection captures and saves the result as a new
  scene. The importer and exporter are unchanged: the remaining difference to Isaac Sim was in
  the render settings, which the sample packages. One import per editor session (Surface Cache
  GI's same-key failure on a second import) and the per-scene adjustments are noted in its README.
  The pipeline renders at 0.67 scale with FSR, on Deferred+ with the GPU Resident Drawer and GPU
  occlusion culling (the assign menu keeps the BatchRendererGroup variants the drawer needs):
  Factory 75.7 to 54.2 ms a frame on the car-line view in the Editor (measured with STP), the
  look unchanged. STP was replaced by FSR at the same scale: STP left dark speckles along
  silhouettes against a distant background. On the shader ball's backdrop box, 1080p pixels
  darker than 70/255 went from 558 with STP to 165 with FSR (121 at native resolution with no
  upscaling); turning Surface Cache GI, SSAO or SSR off changed them by under 10%.
  The sample is the Factory match made general: the import fits the shadow distance (2.1 x the
  stage's bounding diagonal) and the Surface Cache GI volume (next power of two above 1.2 x, at
  least 32 m) to the stage, which gives the Factory its matched 150 m and 128 m and a table-top
  stage 32 m; and the importer's dome ambient (1.5) and rect-light factor (x1.8) are the Factory's
  final values. Over 47 stages against Isaac Sim this improved the Factory (12.9 to 10.1 mean
  CIEDE2000), the props and the assets; the Isaac Simple Warehouse got brighter than Isaac
  (its custom MDL floor and ceiling import as default grey, which the extra light amplifies).

- **`UsdImportOptions.SmoothNormalsLikeIsaac`** (on by default)
  Every mesh is shaded with smooth normals computed from its geometry (faces meeting at more than
  60 degrees keep their edge) instead of the normals the stage authors. Isaac Sim's RTX renderer
  draws a mesh the same whether its `normals` or `primvars:normals` are present, blocked or
  replaced (measured on NVIDIA's Factory by editing the session layer live), so it never shows
  the hard breaks an exporter writes into them. The Factory's car body carries such breaks across
  19% of its adjacent corners and imported as wedge-shaped facets while Isaac Sim drew it smooth;
  with this option the two match. Turn it off to import the authored normals as they are.

- **`UsdImportOptions.ShareIdenticalMaterials`** (on by default)
  Material prims that resolve to the same values and texture files share one Unity `Material`.
  Omniverse and Unreal-connector stages bind a material prim per mesh: Simple_Warehouse went from
  3,471 Materials to 1,681. Under Surface Cache GI every Material takes an atlas slice, and past
  2,048 the slice array cannot be created and GI fails for the whole scene.

### Fixed

- **Physics import on a 0.8.1 plugin without the physics reader**
  An API 13 plugin built without `RUsd_ReadPhysics` (the Linux payload built from `main` has 118
  exports instead of 120) reported the same version, so **Import Physics** called a missing entry
  point and the notes showed the raw `EntryPointNotFoundException`. The package now probes for the
  entry point once and says the plugin was built without the physics reader; the physics tests
  skip with that reason instead of failing.

- **Skinned meshes whose Skeleton is authored `guide` are skinned again (0.8.1 native plugin)**
  The import skips proxy and guide prims (ALab's proxy copies doubled its triangles), and that
  rule also dropped Skeleton prims authored `purpose = "guide"`, which DCC exporters use to mark
  bones as viewport-only. The skeleton was never built, so its meshes imported unskinned in their
  bind shape: the Elephant with Monochord stood arms down where Isaac Sim shows it playing.
  Skeletons are now exempt from the purpose rule; they are not geometry.

- **Tinted lights render at their colour, not 5.6 x darker**
  A USD light's `inputs:color` is linear; `Light.color` is sRGB, and with the Linear colour space
  URP renders `color.linear x intensity`. The importer assigned the USD value as it was, so every
  tinted light went through that conversion a second time: NVIDIA's Factory hangs its 15 sphere
  lights at 0.216 grey, which rendered at 0.038, and the hall outside the central rect light came
  out at 0.4-0.6 x Isaac Sim while the white rect matched. Light colours (and the emissive-surface
  helper lights) are now converted to sRGB on import and back to linear on export. White lights
  are unchanged. Sphere, disk and cylinder lights also reach until their illuminance falls to
  0.002 instead of a 30 m range set by size alone, which cut the strong fixtures off across the
  hall. The Factory's seven views went from mean CIEDE2000 8.84 to 7.53 against Isaac Sim.

- **Metalness, roughness and opacity maps stored as JPEG are packed again**
  The packed metallic+smoothness map (and the albedo+opacity bake) is built from decoded pixels on the decode pass, off the main thread, where only PNG, EXR and TGA
  are read; a JPEG waits for LoadImage on the main thread. A packed map fed by a JPEG therefore
  found nothing to pack and the material kept its constants. OpenChessSet stores every metalness
  and roughness map as a greyscale JPEG, so every piece imported at 0.5 smoothness with no metal
  (no gold trim, flat board). Packing that needs a JPEG now runs on the main thread before the
  materials are built, once the JPEG is decoded there. A JPEG normal map whose channels OmniPBR
  flips (`flip_tangent_v`) had the same gap and was uploaded unflipped; it is flipped there too.
- **A packed metallic+smoothness map is no longer halved by `_Smoothness`**
  URP and Standard multiply `_Smoothness` into the packed map's alpha. The importer left it at
  1 - roughness, the authored fallback for an unconnected input (0.5 by default), so every surface
  whose smoothness came from a map rendered at half of it. With a map bound it is now 1; a
  constant standing in for a missing roughness map is already in the packed alpha.
- **Transmissive glass no longer imports as a white ball (native)**
  The real-time stand-in for MaterialX `transmission` (and OmniSurface
  `specular_transmission_weight`) made the surface partly transparent and painted the transmission
  colour on as its diffuse colour. Under a bright uniform dome every clear glass then read as a
  white ball (OpenChessSet's pawn tops), where Isaac Sim, refracting what is below, draws them
  dark. The diffuse colour now keeps only the (1 - weight) share standard_surface gives the diffuse
  lobe; alpha blending cannot tint what is behind, so the transmission colour is not kept.
- **Reference Look sample: the GI volume and shadow distance reach a stage seen from afar**
  Both are centred on the camera, and `FitToStage` sized them from the stage alone, so a stage
  framed from further than half the GI volume fell outside it and its diffuse lighting went black
  (Vehicle Variants: authored in centimetres, read as a 632 m stage, seen from 1.2 km). While a
  fitted scene is open and the Game camera stands outside the stage, they now grow to reach the
  stage's farthest corner from it, and return to the stage fit when the camera comes closer or
  goes inside (growing them for an interior coarsened its GI).
- **Rect lights bounce the flux they emit, and invisible lights light mostly by bounce**
  A rect light is drawn as a grid of point lights shaped by a cosine cookie. Surface Cache GI
  ignores cookies, so the grid fed it the whole sphere, the ceiling above each fixture included:
  four times the lobe's flux, half of it upward. The grid now bounces nothing and one bounce-only
  spot per rect (a 120 degree cone, the lobe's flux; `UsdLightGI`) carries its light into the
  surface cache. A light authored invisible keeps 1.5 % of its direct light and all of its bounce,
  as Isaac Sim measures (one invisible rect over an open floor: 0.0034 against 0.22 visible); it
  used to keep a third of both. NVIDIA's Simple_Warehouse, lit by invisible ceiling rects, went
  from 23-31 to 12-13 mean CIEDE2000 against Isaac Sim with the surface cache working.
- **OmniPBR normal maps follow flip_tangent_u / flip_tangent_v (0.8.1 native plugin)**
  OmniPBR flips its normal map's V tangent unless told otherwise (`flip_tangent_v` defaults to
  true), so Isaac Sim reads an OmniPBR normal map with green pointing down, where Unity reads green
  up. The importer now flips the channels OmniPBR flips (`RUsd_GetImportMaterialNormalFlip`), and
  the exporter writes `flip_tangent_v = false` beside the normal map it writes, whose bumps came out
  inside out in Isaac Sim. A MaterialX normal image with no colorspace is data (MaterialX manages
  only colour types) and is read as raw.
- **A stage at a hundred times its size is no longer taken for an interior**
  The interior reflection is captured only for a stage with a roof over it. Any renderer above
  1.5 m counted towards the roof, so a stage authored in centimetres and read as metres (Vehicle
  Variants: a tractor 300 m tall) had its cab and body taken for a roof, the reflection was
  captured from inside the bodywork, and the tractor rendered black under a dome. Only flat
  renderers (flatter than half their width) count now; the Warehouse's and the Factory's roofs still
  do.

- **Normal maps with no authored colour space decode as sRGB, as in Isaac Sim (0.8.1 native plugin)**
  USD reads an unauthored colour space as `auto`, which is sRGB for an 8-bit image, and Omniverse
  applies that to normal maps too. NVIDIA's pallet prop (an OmniPBR with a `.tga` normal map and no
  `colorSpace`) shades its sunlit top at a quarter of what the same map read as raw gives in Isaac
  Sim 6.2 (0.25 against 3.46 times the albedo, from its HdrColor and DiffuseAlbedo outputs), while
  Unity read every normal map as linear: the pallet came out 1.7 times too bright. Normal maps now
  follow the colour space the stage authors (`colorSpace` metadata, through connections, or a
  UsdUVTexture's `sourceColorSpace`), and an unauthored 8-bit one decodes as sRGB
  (`UsdImportOptions.NormalMapColorSpaceLikeIsaac`, on by default). `RUsd_GetImportTextureColorSpace`
  reports the authored colour space of any texture the import named.
- **Dome and interior reflections are convolved for roughness**
  URP reads a reflection cubemap mip by the surface's roughness and expects it already blurred by
  that roughness's GGX lobe. The importer's dome and interior captures had only box-filtered
  automatic mips, so a rough floor or pallet reflected the HDRI's softboxes almost like a mirror.
  The captures are now prefiltered on the CPU with GGX importance sampling, per the mip-to-roughness
  mapping URP uses (`UsdReflectionConvolution`). Simple_Warehouse's shelf view moved from CIEDE2000
  8.9 to 7.9 against Isaac Sim; scenes without a dome are unchanged.

- **MaterialX metalness and roughness textures are imported**
  A `standard_surface` whose metalness or specular_roughness comes from an image used to lose
  both maps with a warning and import constant metallic 0 and roughness 0.5: OpenChessSet's gold
  crowns and borders came in flat cream and its marble uniformly semi-gloss. The images now go
  the way OmniPBR's separate metallic and roughness maps go, packed by the host into Unity's
  metallic(.r) + smoothness(.a) map, with the constant standing in for whichever is not a texture.
  A roughness image is not taken when the specular layer is weighted off.
- **UsdPreviewSurface's specular workflow is converted**
  `useSpecularWorkflow = 1` was ignored: the surface imported from its diffuseColor and its
  `metallic`, which the specular workflow does not use, so a specular metal came in as a coloured
  dielectric (CIEDE2000 45 against Isaac Sim on the look bench). A constant specularColor is now
  converted to the metallic workflow the way Khronos converts glTF specular-glossiness: metallic
  solved from the diffuse and specular brightness, the base colour blended from both. A textured
  specularColor keeps the old reading and is reported.
- **Exported texture coordinate readers write `varname` as a string**
  It was a token, which OpenUSD 26's validator reports (the type changed in USD 21.11). Import
  reads both.
- **Vertices with only degenerate faces get a normal**
  After the small-triangle fix 0.16% of OpenChessSet's vertices still had a zero normal: every
  face they belong to has zero area. They now take the average of the faces at their position.

- **Runtime Demo: a referencing export keeps each part's own transform**
  The placement was authored on the referencing prim and replaced the referenced default prim's
  `xformOpOrder`, so a part whose default prim is transformed in its source landed offset (0.5 m
  for the indexer sample). Each source now sits under a wrapper `Xform` that carries the placement.
- **Runtime Demo: a referencing export keeps the sources' time range**
  A stage reads time metadata from its root layer only, so the assembled layer had none. The
  writer now opens each source with `UsdStage`, so `.usda`, `.usdc`, `.usd` and `.usdz` all count,
  and writes the union of the ranges, converted to the rates of the first source that has one.
  Both rates are written with their effective values; the stage API does not say which one a
  layer authors.

- **Small triangles keep their normals, and imported meshes get tangents**
  The smooth normals computed for a mesh without authored normals came out zero on triangles
  under about 3 mm: `Vector3.normalized` returns zero below a length of 1e-5, and a face normal's
  length is twice the triangle's area. A 15 cm chess piece of 40,000 triangles (openchessset) had
  99.6% zero normals and rendered as dark blotches. Normalisation is now done in double. Imported
  meshes with UVs also get tangents (`RecalculateTangents`); USD carries none, and URP Lit built
  the normal map's frame from zero vectors.

- **Dome reflections are captured, and the dome ambient is 1.0**
  The sky reflection cubemap captured in the first frames after an import came back black (all
  six faces zero), so every metal under a dome reflected black; the capture is now checked (its
  1x1 mip) and retried a frame at a time. The interior reflection probe, which replaced the sky
  reflection on every stage, is now used only when the stage has something overhead (renderers
  above the probe covering half the footprint): a probe in the middle of an open stage saw
  nothing or the inside of an object. With reflections back, the dome ambient the Factory had
  been raised to (1.5) was compensating for the missing specular; 1.0 fits the 47 stages best
  (mean CIEDE2000 3.98 at 1.5, 3.81 at 1.0, 3.87 at 0.85).

- **Lights and sides as Isaac Sim renders them (NVIDIA's Simple_Warehouse from 46.8 to 10.6
  mean CIEDE2000)**
  Three rules, each measured on Isaac Sim 6.2 with a dedicated stage. (1) Omniverse RTX draws
  every mesh double-sided whatever `doubleSided` says: a `doubleSided = 0` quad seen from behind
  is drawn and lit. `UsdImportOptions.DoubleSidedOpenMeshes` (on by default) gives back faces to
  every mesh with open edges, found on positions welded across the importer's normal and UV
  seams; closed meshes stay one-sided, since their inside is not seen from outside. The
  warehouse's single-sheet ceiling was invisible from below. (2) URP shadows one directional
  light; every other one lit through walls and roofs. The brightest distant light stays
  directional and each further one becomes a spot light far away along its direction (20 x the
  stage's bounding radius, a cone just covering the stage, intensity times the distance squared),
  so it casts shadows; the warehouse's second low sun had lit its closed interior. (3) A light
  authored invisible still lights the scene in Isaac Sim, through bounces only: the warehouse
  measured 0.109 with its 37 invisible rect lights, 0.330 with them forced visible and 0.0014
  without them, and one invisible rect over the open bench lit almost nothing. Unity has no
  bounce-only light, so an invisible light keeps a third of its intensity.

- **Unreal-connector overlays are baked into the albedo**
  NVIDIA's Simple_Warehouse aisle signs (`M_AisleSign.mdl`) take one texture parameter, `Text`
  (the aisle number and "BOXES G - I"), and blend it over the sign's diffuse map with Unreal's
  BlendOverlay. The importer read the diffuse map from the module body but not the blend, so
  every sign imported blank, and since the six sign materials then read the same,
  `ShareIdenticalMaterials` merged them into one. The connector writes BlendOverlay out as a
  per-channel `>= 0.5 ?` select on the parameter's lookup; the importer now recognises that and
  bakes `t >= 0.5 ? 1 - 2(1 - t)(1 - a) : 2 t a` (overlay read linear, albedo sRGB, blended
  linear) into the albedo, through the same path as the Unreal mask blends (mask mode 2; no ABI
  change). The warehouse now imports six sign materials with their own text. The new
  `UsdUnrealOverlayTests` has not been run yet.

- **Materials import from the network Isaac Sim renders**
  A material can carry an MDL, a MaterialX and a UsdPreviewSurface network at once. The importer
  read the UsdPreviewSurface first; Isaac Sim (Omniverse RTX) renders MDL first, then MaterialX,
  then UsdPreviewSurface, measured on Isaac Sim 6.2 with one material carrying all three in
  different colours. The importer now follows that order. A known MDL module or a supported
  MaterialX surface is read in full; an unknown MDL module, of which only a base colour can be
  read, is used only when there is no MaterialX or UsdPreviewSurface network beside it. On the
  Standard Shader Ball, `neutral` has an 18% grey UsdPreviewSurface and a MaterialX OpenPBR with
  no base colour, which Isaac Sim draws white; Unity drew it grey, and now draws it white too.
  A MaterialX surface with its specular layer weighted off (`specular_weight` or `specular` 0, not
  metallic) imports fully rough rather than with its authored roughness, since it has no
  reflection at all. A file exported with `MaterialFlavors` OmniPBR or MaterialX now imports from
  the OmniPBR or the MaterialX `standard_surface`. Neither has an input for Unity's packed
  metallic/smoothness map, so the export keeps that map in the UsdPreviewSurface only; when the
  network read has no metallic or roughness map of its own and the UsdPreviewSurface beside it
  has the exporter's packed map (metallic from .r and roughness from .a of one file), the import
  takes that map from it, and the round trip keeps it. Every other OmniPBR or MaterialX surface
  imports from its own network alone.
- **The default material is UsdPreviewSurface's default grey**
  A mesh with no material and no displayColor got a 0.7 grey. It now gets UsdPreviewSurface's
  defaults (diffuse 0.18 linear, roughness 0.5, metallic 0), which is what Isaac Sim draws: an
  unbound sphere matched an 18% grey reference sphere under the same light.

- **Reference Look: the stage fit survives reopening the scene, and Surface Cache GI samples more**
  `FitToStage` wrote the fitted GI volume size to `Volume.profile`, a runtime copy that is not
  saved with the scene, so a reopened scene rendered with the sample asset's 128 m (a table-top
  stage was fitted to 32 m). The size now goes on its own global volume, `ReferenceLook Stage Fit`,
  one priority above the sample's, whose profile overrides only the volume size; the import saves
  that profile beside the scene (`<scene>_StageFit.asset`). The sample profile is not edited, so
  later changes to it still apply. The shadow distance is a pipeline-asset setting every scene
  shares; it is now applied again from the stage's bounds when a fitted scene is opened.
  The sample volume's screen lookup and upsampling sample counts go from 6 and 2 (the Medium
  preset) to 8 and 8: with only a few samples, a pixel whose samples all miss a surface patch got
  no indirect light and rendered as a dark speckle. That was hard to see in the Factory but
  covered a lone robot arm; on Franka the speckles in the robot area went from 3,543 to 1,845,
  and what remains is real detail (seams, silhouettes). The volume size and resolution made no
  difference. The GPU cost was not measured (the Editor reports no frame timings); only the
  screen-space passes do more work.

- **doubleSided meshes render from both sides**
  The importer ignored `UsdGeomGprim.doubleSided`, so every mesh was one-sided. A mesh meant to be
  seen from inside, such as the Standard Shader Ball's backdrop box, was open from outside where
  Isaac Sim and Omniverse draw it closed. `doubleSided` is now read for each mesh through the
  Stage API on the import's own stage, with no plugin rebuild. A plugin older than 0.8.1
  reads every mesh as one-sided, as before. A doubleSided mesh gets its back faces as geometry:
  mirrored vertices with negated normals and the triangles wound the other way. Each side then
  shades with a normal that faces it. A Render Face Both material alone keeps the front normal on
  the back face, and the box's outer walls rendered black under the key light. The materials stay
  one-sided. Skinned and point-cached meshes must keep their vertex count, so they get a Render
  Face Both copy of their material instead (`_Cull` 0, or HDRP's `_DoubleSidedEnable`). A
  doubleSided mesh never shares a Mesh with a one-sided mesh of the same buffers. Verified on the
  shader ball, where the box now renders closed and lit. `UsdDoubleSidedImportTests` covers the
  vertex, normal and winding layout.

- **Rect lights shadowed what they lit**
  With `PhysicalLightUnits` a rect light became a grid of 179-degree spots. A spot's single
  shadow map spread over that cone is too coarse to resolve, so walls and floor the light reached
  came out shadowed: on the Factory's mezzanine the wall measured 0.31 x Isaac Sim and the floor
  0.50 x, and narrowing the cone to recover the map left the far side unlit. Each cell is now a
  point light with a cube-map cookie shaped as the cosine lobe of a flat emitter (nothing
  upward), whose six shadow faces never degenerate; the rect scale factor is refitted to it
  (0.00018 per nit per square metre). Both Factory views now sit within 0.75-1.9 x Isaac Sim.
  Needs the render pipeline's light cookies, which URP enables by default.

- **Environment captures rendered inside the import**
  The dome's reflection cubemap and the interior reflection were rendered from inside the
  import, in the frame that had just created thousands of renderers, so the first thing Surface
  Cache GI saw of the stage was a cubemap render of a half-registered scene. Both now run after
  the scene has drawn its first frame (`UsdDeferredRender`), from an update rather than a render
  callback. Until then a dome-lit scene reflects its skybox through RenderSettings;
  `UsdImporter.RefreshReflection` still captures immediately when called. Known limitation, not
  the importer's: importing a stage into an editor session whose Surface Cache GI already held a
  previous import can throw "An item with the same key has already been added" from URP's
  ray-tracing acceleration structure (Unity 6.6), after which the renderers past the failing one
  are missing from GI and a roof no longer occludes the sky (the Factory then measured 7.6 x
  Isaac Sim under dome + sun instead of 1.3 x). A fresh editor session, or one import per session,
  does not hit it.

- **Interior reflection captured the sky it was meant to replace**
  `InteriorReflection` rendered its cube map once, while the sky was still the scene's reflection
  source, so every interior surface in the capture reflected sky that the roof blocks and the
  hall came out 6-9 x too bright in the shade (Factory, dome + sun) until the capture was taken
  a second time. The import now takes the capture three times on consecutive frames, each with
  the previous one as the scene's reflection, which converges to the hall's own specular bounce:
  0.94 x Isaac Sim on the Factory car line under full lighting. The passes have to be frames
  apart, since a second cube map render in the same frame does not see the one just assigned to
  RenderSettings and renders with no reflection (chrome car body at 0.44 x). A caller that
  changes the lights calls `UsdImporter.RefreshReflection` on consecutive frames the same way.

- **OmniPBR `emissive_intensity` normalised by its 40-nit default**
  OmniPBR authors emission in nits. The importer divided by the 40-nit default, so a 29,000-nit
  status light (Isaac Sim's Franka) came in at 725x its colour and bleached the robot around it.
  Kit renders a `UsdPreviewSurface` `emissiveColor` of E as E x pi x 1133.6 nits
  (`UsdPreviewSurface.mdl`), and that `emissiveColor` already imports 1:1 into Unity emission, so
  nits now divide by the same constant: the Franka light imports at 8.1x, and the untouched
  40-nit default at the faint glow Kit shows for it.

- **OmniGlass frosting ignored for opacity**
  `frosting_roughness` only set the roughness, and every pane blended at 0.25 opacity. A real-time
  blend has no rough refraction, so a frosted pane cannot blur what is behind it; the closest it
  gets is to show less of it. Opacity now rises with frosting (0.25 clear, up to 0.85), and a
  `thin_walled` pane stays clear. On NVIDIA's `Factory.usd` the `Glass_Tinted_Mid` roof
  (frosting 0.1) rendered see-through in Unity while Isaac Sim draws it milky.

- **Albedo texture with no file ignored its fallback colour**
  `diffuseColor` wired to a `UsdUVTexture` that names no `inputs:file` was warned about and then
  left at the default white, while the spec's `fallback` input on that texture is the colour to
  show. It is read now when authored (usd-wg StandardShaderBall `neutral`).

- **OmniGlass panes rendered as lit white sheets**
  `glass_color` became the diffuse colour of a blended Lit material, so every pane was a diffuse
  surface lit from the front: the Factory cell fence (glass_color 0.94) measured 179 against
  Isaac Sim's 107 on the same pixels. Glass only sends back its Fresnel reflection and a thin
  haze, so the diffuse colour is now `glass_color` x 0.1 and the specular carries the look.
  Frosting blurs the reflection (roughness = 2.5 x `frosting_roughness`) and veils what shows
  through (opacity 0.25 + 2.5 x frosting, up to 0.85). Known limitation: Isaac Sim's frosted pane
  blurs the view behind it more with distance (rough refraction); a blended Lit surface cannot,
  so the veil is uniform at every depth.

- **Emission colour rendered as sRGB(E)**
  `emissiveColor` was converted to sRGB like the base colour, but `_EmissionColor` (URP) and
  `_EmissiveColor` (HDRP) are `[HDR]` properties that Unity stores linear and does not convert,
  so every emitter rendered washed out and too bright (an emissive 0.18 grey showed as 0.46).
  Import now writes the authored linear value and export reads it back unchanged. Measured on the
  Isaac Sim look bench (Unity 6.6 URP against Isaac Sim 6.2.0): the emissive cube's CIE Lab
  difference dropped from 24-33 to 4-6.

- **Transparent materials cast solid shadows**
  An alpha-blended material (glass: `OmniGlass`, or any constant opacity below 1) still drew into
  the shadow map as an opaque surface, so a glazed roof blocked the sun and glass fence panels cast
  black shadows, while Isaac Sim lets light through. Import now disables the `ShadowCaster` pass on
  blended materials; cut-out materials keep their shadows. Seen on NVIDIA's `Factory.usd`.

- **Animated stages imported at the wrong time**
  Every read used `UsdTimeCode::Default()`, so on a stage that authors a time range an animated
  prim took its authored default instead of the pose usdview and Isaac Sim show at the start time.
  On NVIDIA's `Factory.usd` the car-body sledges came in about 10 m from where Isaac draws them.
  Transforms, visibility, points, topology, primvars, instancers and implicit shapes are now read
  at `startTimeCode` when the stage has a time range, and at the default time otherwise.

- **Targa textures refused**
  `.tga` maps went to `Texture2D.LoadImage`, which reads PNG and JPEG only, and were refused (Isaac
  Sim's pallet props). A managed TGA decoder (uncompressed and RLE, 8-bit grey, 24- and 32-bit
  colour, the same size limits as PNG) now reads them on the worker thread.

- **Unreal-connector MDL imported untextured**
  Materials exported by the Omniverse Unreal Engine connector are per-instance MDL modules on
  `OmniUe4Base` whose inputs carry Unreal parameter names, so none matched and NVIDIA's
  Simple_Warehouse imported without textures. Albedo, normal and ORM maps are now picked by
  parameter name and texture suffix (`_D`, `_N`, `_ORM`), with `*Tint`, `Roughness`, `Metallic`
  and `U_Tiling`/`V_Tiling`. The colour mask blend those master materials use,
  `lerp(ColorAlbedo, AlbedoTexture, MaskSelection)`, is baked into the albedo at import (native API
  13, `RUsd_GetImportMaterialAlbedoMask`), which brings back the warehouse's navy floor and orange
  racks. API 14 (`RUsd_GetImportMaterialUnrealBlend`) adds the per-channel form those materials
  use for barrels and crates, `lerp(lerp(lerp(lerp(0, Body, m.r), Handle, m.g), Cap, m.b), albedo,
  m.a)`, and the roughness range `lerp(RoughnessMin, RoughnessMax, ORM.g)` (the ORM map was read as
  roughness directly, which made worn concrete render polished). Parameters the stage leaves
  unauthored take their defaults from the module file, and modules with no parameters
  (`M_AisleSign`) take the textures their body names: 1,697 of the warehouse's 1,698 materials now
  have a colour map. The rest of the graph is not evaluated.
- **Time, animation and UsdSkel on import (0.8.1 native plugin)**
  The native importer read every attribute at `UsdTimeCode::Default()` and had no UsdSkel code,
  although UsdSkel is compiled into the shipped OpenUSD libraries on every platform. Thirteen
  additive entry points now report the stage's time range and the time static values were read
  at (`RUsd_GetImportTimeInfo`); animated transforms and visibility as per-node tracks; every
  skeleton a SkelRoot binds, with joint order, parents, bind, rest and read-time poses and its
  animation; per-vertex joint weights carried through the vertex split and moved into the
  skeleton's joint order (a mesh-local `skel:joints` order and rigid bindings included); and blend
  shapes as dense per-vertex frames (inbetweens included) with their weights and weight
  animation. Tracks, skeleton animations and blend shapes that pass the new animation budgets are
  dropped with a warning, not the import. Covered by `Native~/Tests~/skel_import_test.cpp` against
  `Tests/Fixtures~/Skel`. In the macOS, Windows and Linux payloads.
- **Skinned meshes import as `SkinnedMeshRenderer`s (`UsdImportOptions.ImportSkinning`, on by default)**
  Skinned meshes used to arrive as static meshes in their authored points, which is the bind
  pose, not what USD draws: on a warehouse stage with 17 rigged workers (97 meshes) they sat up
  to 1 m away from where OpenUSD skins them. Each Skeleton prim now gets a joint hierarchy, posed
  as the stage is at the read time, and a `UsdSkeleton` listing the joints in the skeleton's
  order; each skinned mesh gets a `SkinnedMeshRenderer` with those joints as bones, bone weights,
  bind poses (`inverse(bind) * geomBindTransform`) and its blend shapes (inbetweens as extra
  frames, weights from the skeleton's animation at the read time). On that stage every skinned
  vertex now matches OpenUSD's own skinning to 0.07 mm. Joints are not prims: the root joints
  carry `UsdImportHelperObject`, so `UsdPrimSource` and both exporters skip them. Skinned meshes
  are never shared by `ShareIdenticalMeshes` and get no `MeshCollider`. Needs the 0.8.1 native plugin; the
  option off, or an older plugin, imports them as before.
- **`UsdImportOptions.ReadAtStageStartTime`** (on by default) chooses the read time described
  below; off reads at USD's Default time as before.
- **Animation playback: `UsdAnimationPlayer` and `UsdAnimationData` (`UsdImportOptions.ImportAnimation`, on by default)**
  A stage with time-sampled prim transforms or visibility, skeleton animation or blend-shape
  weights now imports with a `UsdAnimationPlayer` on its root (`UsdImportResult.Animation`),
  playing a `UsdAnimationData` that keeps the samples at the times the stage authors them. It
  interpolates as USD does -- positions and scales linearly, rotations along the short arc,
  blend-shape weights linearly, visibility held -- and needs no Animator, AnimationClip or
  Timeline, so it works the same in players and the Editor. `Evaluate(seconds)` and
  `EvaluateAtTimeCode` pose everything; `Play`, `Pause`, `Stop`, `Loop` and `Speed` drive it in
  Play Mode and players, and `PlayOnStart` starts it. The import is posed at the read time.
  At 13 times across the two test stages, skinned vertices, transforms and visibility match
  OpenUSD.
- **Timeline track for imported animation (`Unity.USDToolkit.Timeline`)**
  `UsdAnimationTrack` binds a `UsdAnimationPlayer`, and each `UsdAnimationClip` on it poses the
  stage at the clip's local time, with clip-in, speed, looping and extrapolation, in the Timeline
  window's preview as in Play Mode and players; a clip given the `UsdAnimationData` takes its
  length from it. The assembly compiles only when com.unity.timeline 1.7 or newer is in the
  project, so the package itself still depends on nothing.
- **USD files in the project import as model assets (`UsdStageImporter`)**
  `.usd`, `.usda`, `.usdc` and `.usdz` files in a project import through the same importer as
  `UsdImporter.Import`, as a model asset: the stage's root object as the main asset, its meshes,
  materials, textures (with their pixels) and `UsdAnimationData` as sub-assets, under
  identifiers that stay the same across reimports. Content then builds for every platform Unity
  targets, not only those the native plugin supports. The importer's Inspector holds the import
  settings and a dropdown for each variant set the stage offers. The asset reimports when the
  stage or any project file it read changes: sublayers, references and payloads, through the
  new `RUsd_GetImportLayers` (API 13, additive), and textures. With com.unity.importer.usd or
  com.unity.formats.usd installed it is offered as an alternative importer instead of taking the
  extensions over.
- **`UsdImportOptions.KeepTexturesReadable`** keeps each texture's CPU copy (for reading pixels or
  saving it as an asset); **`UsdImportResult.SourceFiles`** lists the layer and texture files an
  import read.
- **`UsdAnimationPlayer` Inspector** plays, pauses, stops and scrubs in Play Mode, and the
  Timeline track registers everything it drives, so a Timeline preview in Edit Mode restores the
  scene and leaves no prefab overrides.
- **`UsdStageSource.CreateOptionsWithVariant(primPath, set, variant, template)`** copies every
  other setting from `template`. The three-argument form returned defaults for everything but
  payloads and variants, so a re-import lost its parent, texture and unit settings.
- **Animation and UsdSkel on export (0.8.1 native plugin)**
  Eight more additive entry points write the stage's time range (`RUsd_SetExportTimeRange`),
  transform and visibility samples on a written prim (`RUsd_SetXformSamples`,
  `RUsd_SetVisibilitySamples`), a SkelRoot, a Skeleton with bind and rest transforms, a mesh's
  joint weights and geomBindTransform, blend-shape frames (inbetweens included), and a
  SkelAnimation with joint transforms and blend-shape weights, static or sampled. Covered by
  `Native~/Tests~/skel_export_test.cpp`, which writes a stage through them and reads it back
  through the import entry points. In all three payloads, like the rest of API 13.
- **Skinned meshes export as UsdSkel (`TransformPolicy = PreserveHierarchy`)**
  The renderers that share their bones become one character: their lowest common ancestor is
  written as a SkelRoot, with a Skeleton whose joints are the bones (a skeleton this package
  imported keeps its Skeleton prim and joint names), and each mesh keeps its bind-pose points,
  weights and blend shapes, with bind and geomBind transforms chosen so that OpenUSD skins it
  exactly as Unity draws it; the current pose and blend-shape weights are written as the rest
  pose and a static SkelAnimation. Bones stay Xform prims too, for whatever hangs off them. On a
  warehouse stage with 17 rigged workers (97 skinned meshes), posed at random and exported,
  OpenUSD's own skinning of the file matches what Unity drew to 0.07 mm. With
  `TransformPolicy = BakedMesh`, or for a renderer the skeleton cannot carry (a mesh without
  Read/Write, bones outside the exported hierarchy, bind poses that fit no shared skeleton), the
  mesh is written as the static mesh it currently draws, with a warning in the Preserve case.
- **`UsdAnimationRecorder`: record a hierarchy as an animated stage**
  `Capture(seconds)` once per frame, or `CaptureClip(AnimationClip)` and
  `CapturePlayer(UsdAnimationPlayer)` to sample one through, then `Save(path)`. The stage is
  written with `PreserveHierarchy`; the first frame is its static pose, transforms and active
  states that change become samples on their prims (only those that change), and each skinned
  character's bones and blend-shape weights a SkelAnimation. Time codes are frames at the
  recorder's rate. Works in players as in the Editor. A recording of the skel_arm test stage
  played through, skinned again by OpenUSD, matches OpenUSD's numbers for the original to 1e-6.
  UsdSkel stores joint scales as half floats, so an animated joint scale comes back to about
  three decimal places. Needs the 0.8.1 native plugin.
- **Unity Recorder: "USD Animation" recorder (`Unity.USDToolkit.Recorder.Editor`)**
  With com.unity.recorder 5.0 or newer in the project, the Recorder window (and Timeline
  Recorder clips) offers a USD Animation recorder: pick a GameObject, a format (.usdc, .usda or
  .usdz) and whether to write textures, and the Play Mode session is saved through
  `UsdAnimationRecorder`, imported as an asset when it lands under `Assets`. At a constant frame
  rate each recorded frame is one time code. The assembly compiles only when the Recorder is
  installed; the package still depends on nothing.
- **Geometry below animated visibility** A prim hidden at the read time and shown later by its
  animation had no geometry, because hidden geometry is not read. Below a prim whose
  visibility is animated, geometry is now read regardless and every prim reports its own
  visibility opinion, leaving inheritance to Unity's active hierarchy, which is the same rule.
- **Point caches: meshes whose points are time-sampled play their animation**
  A mesh that animates its points (simulations, Alembic conversions, baked deformers) imported as
  one frame. Every sample now arrives as a `UsdAnimationData.PointCacheTrack` on the imported
  vertices (through the vertex split), and `UsdAnimationPlayer` interpolates it linearly, as USD
  does. Normals come with it: recomputed per sample when the mesh authors none, read per sample
  when authored ones animate, and left alone when authored ones hold still. The player draws the
  points through the renderer's `MeshRenderer.additionalVertexStreams`, so the imported Mesh is
  never written, an asset's mesh is never dirtied, and two instances of one mesh play at different
  times; `RestoreMeshes()` shows the imported mesh again. A mesh whose topology animates, whose
  point count changes, that is skinned, or that sits inside a point-instancer prototype keeps the
  read-time frame, with a warning saying which. `UsdAnimationRecorder` writes such a mesh's points
  back as samples (with normals), and a flattened export writes the frame on screen, as it writes
  a skinned mesh's current pose. On skel_arm's point cache every vertex matches OpenUSD at every
  sample and between them.
- **Dual-quaternion skinning (`UsdDualQuaternionSkin`, `UsdImportOptions.DualQuaternionSkinning`, on by default)**
  A mesh UsdSkel marks `primvars:skel:skinningMethod = "dualQuaternion"` was skinned linearly,
  with a warning, because SkinnedMeshRenderer only blends linearly: a twisting joint collapsed the
  skin around it. Such meshes now keep a MeshFilter and MeshRenderer and get a
  `UsdDualQuaternionSkin`, which poses them on the CPU exactly as UsdSkel does -- rotation and
  translation blended as dual quaternions on the hemisphere of each vertex's heaviest joint,
  scale and shear blended linearly first, blend shapes before skinning -- and draws the result
  through `additionalVertexStreams`, so the scene and the asset keep the bind pose. On a tube
  0.5 wide, twisted 160 degrees, bent and scaled, every vertex matches OpenUSD's own
  dual-quaternion skinning to 1e-4 (linear blending is up to 0.2 off there). The option off gives
  SkinnedMeshRenderers as before. Exports write these meshes as UsdSkel with `skinningMethod = "dualQuaternion"`, and
  the recorder records their blend-shape weights. It runs on the CPU whenever the pose changes;
  for crowds, turn the option off for GPU linear skinning.
- **Skinned point-instancer prototypes are drawn posed**
  A skeleton inside a point-instancer prototype was never linked to its node, so the prototype's
  meshes imported unskinned and every instance drew the bind pose. The prototype now keeps its
  skeleton, which the stage's animation poses, and `UsdPointInstancer.SkinnedSource` bakes the
  posed prototype once a frame and draws that mesh at every placement, so all instances play the
  prototype's animation together, as OpenUSD renders them; cells grow with the poses. On a
  three-instance test stage every instance's vertices match OpenUSD's placement of the skinned
  prototype. A prototype bound to a skeleton outside it -- or one a second instancer reuses --
  is drawn unskinned, with a warning.
- **The stage's animation as an AnimationClip (`UsdAnimationClipBuilder`)**
  `UsdAnimationClipBuilder.Build(player)` makes a generic AnimationClip, for an Animator, the
  Animation window or a Timeline Animation track: prim and joint transforms, active states and
  blend-shape weights as curves keyed at the stage's samples, linear where USD is linear, stepped
  for visibility, with extra rotation keys where samples are more than 10 degrees apart so the
  quaternion curves follow USD's arc; it lasts the stage's time range and loops like the player.
  `UsdStageImporter.GenerateAnimationClip` adds one to an imported asset as a sub-asset, and the
  player's context menu saves one as a `.anim`. Point caches have no curve type and stay with the
  player.
- **Edit Mode preview (`UsdAnimationPreview`)**
  The `UsdAnimationPlayer` Inspector has a Preview button and a time slider in Edit Mode. The
  preview runs in Unity's animation mode, as the Animation window's does: it registers every
  property the animation drives, the player poses them exactly, and ending the preview puts back
  every transform, active state, blend-shape weight and mesh, so nothing reaches the scene file or
  a prefab's overrides. It ends when Preview is clicked again, the selection changes, the scene is
  saved or Play Mode starts. Tools can use `UsdAnimationPreview.Start(player)` directly.
- **C# stage API (`UsdStage`, `UsdPrim`, `UsdAttribute`)** Read and edit USD stages from C#
  without importing them. `UsdStage.Open`, `CreateNew` and `CreateInMemory` give a stage; `Save`,
  `Export` (optionally flattened) and `Reload` write and reread it. Prims are reached by path or
  `Traverse` and carry their type, specifier, flags (active, loaded, instance, ...), kind and
  metadata, children, variant sets, references, payloads, inherits and specializes, relationships
  and transforms. `UsdAttribute.Get<T>` and `Set<T>` read and write values at any time code as C#
  and Unity types -- `Vector3[]` for point3f[], `Color` for color3f, `Matrix4x4`, `Quaternion`,
  strings, tokens and asset paths, and arrays of each -- converting precision (a double3 reads as
  a `Vector3`, a half writes from a float) but never shape or kind; `GetComponents` reads any
  real-valued attribute at full precision. Time samples, blocks and clears; `UsdEditTarget` sends
  edits to the root, session or a sublayer, or inside a variant. Values are USD's own;
  `UsdCoordinates` converts to Unity's basis as the importer does. Every read checks the type the
  value holds, so a stage that authors one type under another declaration is refused, not
  misread. Calls on one stage are serialized, so it can be used from any thread. A curated API,
  not the legacy USD.NET binding: no reflection serializer, safe under IL2CPP. Needs the 0.8.1 native plugin.
- **Switch a variant or the loaded payloads at runtime, in place (`UsdStageSource.SwitchVariant`,
  `SwitchPayloads`, `LoadPayload`, `UnloadPayload`, and `...Async`)** The change is made on the
  stage the import keeps open (`UsdStageSource.Stage`, read-only to callers; in its session layer,
  so no file changes), OpenUSD's change notices say which prims recomposed, and only those
  subtrees are imported again -- widened to a whole SkelRoot or PointInstancer, so skinning and
  instancing stay whole -- and put where the old ones were. The rest of the import, and every edit
  made to it, stays; a hand move, rename or deactivation of a rebuilt prim's own object carries
  over, and objects added by hand below it move to the new objects. Its animation tracks are
  replaced in the import's `UsdAnimationPlayer`, which plays on at its time. A change that reaches
  past one subtree (a material, a skeleton animation), a stage changed on disk since the import,
  a prefab instance in Edit Mode, or a plugin older than 0.8.1 imports the whole stage again in
  the import's place instead; `UsdSwitchResult` says which happened, what was rebuilt and why.
  Rereads are held to the import's limits less what the rest of the import holds. The async forms
  read off the main thread and keep the old parts until the new ones are built.
  `UsdImportOptions.KeepStageOpen` keeps the import's own stage for the first switch; otherwise it
  is opened on first use. `Reimport` still imports the whole stage again;
  `CreateOptionsWithVariant` and `CreateOptionsWithPayloads` build options without importing.
- **Native stage API (0.8.1 native plugin)** The stage API's 18 entry points (`RUsd_CreateStage`, `RUsd_QueryText`,
  `RUsd_EditPrim`, `RUsd_GetAttributeValue`, `RUsd_SetAttributeValue`, ... ; see the header),
  `RUSD_OPEN_NO_READ`, and `RUsd_ReadSubtrees` / `RUsd_ReleaseImportData` for live switching.
  Additive; covered by the new `stage_api_test` native suite (429 checks). In the macOS, Windows
  and Linux payloads.
- **Import limits as options (`UsdImportOptions.Limits`)** The budgets an import is held to --
  2,000,000 nodes, 1,000,000 meshes, 4,000,000 point-instancer placements, 268,435,456 points and
  triangle indices, and the animation ones -- were fixed, and a stage over the node, mesh,
  placement or geometry budget always failed the import. `UsdImportLimits` sets each per import
  (raised for a trusted large stage, lowered to keep a device's imports small), and
  `WhenExceeded = SkipWithWarning` imports what fits instead: the walk stops at the node limit, a
  mesh over its limits is left out with its instance copies, and an instancer's placements over
  theirs are not drawn, with a warning that says what was left out. `GetPreviewInfo` opens under
  the same limits, and its kept stage is reused only by an import with the same ones.
  `UsdStageImporter` shows them under Limits. The defaults and `Fail` behave as before.
- **A moved imported object is written as a relocate** An override export
  (`UsdCompositionMode.Preserve`) wrote an imported object moved under another parent as a copy,
  with its original deactivated: the copy held only what the exporter writes, so the source's
  materials, subsets, primvars and composition were lost. When the output references the source
  (the import below the export root) the move is now a relocate in the output layer, so the source
  prim is composed at its new place with everything it had, and the object's edits are written
  there; a new parent made in Unity is written too. A copy of an imported object, with the
  original in place, is still new content, and so is a move when the output sublayers the source
  (exporting the import root itself): OpenUSD relocates only prims that come in through a
  reference or payload. `UsdExportResult.RelocateCount` counts them.
- **More native import data (0.8.1 native plugin)** `RUsd_GetImportMeshPointCache` and `RUsd_CopyImportMeshPointCache`
  (a mesh's animated points on its imported vertices, with normals), `RUsdImportMeshSkin.dualQuaternion`
  (the mesh's skinning method), `RUsd_OpenStageWithLimits` (an import's budgets), and on export
  `RUsd_SetPointsSamples`, `RUsd_SetSkinningMethod` and `RUsd_AddRelocate` (a namespace move in the
  export layer's relocates).
  Additive like the rest of API 13, which is not released yet; covered by
  `Native~/Tests~/skel_import_test.cpp` and the EditMode tests.
- **`UsdAnimationPlayer.CollectAnimatedTargets(transforms, toggled, components)`** also reports the
  `UsdDualQuaternionSkin`s and the MeshRenderers point caches draw into; the Timeline track
  registers them, so its preview puts them back too.
- **`UsdSkeleton.BindTransforms`** keeps each joint's bind transform from the stage, which an
  override export needs to bind a new mesh to an imported skeleton.
- **The Recorder tests run in batch mode** The Unity Recorder captures at the end of each rendered
  Game view frame, which batch mode never reaches, so its test needed an Editor with a window. A
  second test drives the Recorder's own session frame by frame, as its frame component does, so
  the USD Animation recorder is covered in batch mode and CI; the Play Mode test still checks the
  whole RecorderController path in a windowed Editor.
- **Alembic (`.abc`) and Draco (`.drc`) files (OpenUSD's usdAbc and usdDraco plugins)**
  The payload's OpenUSD is now built with its Alembic and Draco file format plugins, so
  `UsdImporter.Import` opens an `.abc` like any stage -- meshes, transforms, normals and texture
  coordinates, with transform animation as tracks and animated points as point caches -- and a
  stage that references or payloads a `.drc` mesh composes it. Alembic 1.8.5 (with Imath 3.1.9),
  and Draco 1.5.6, are linked statically into the OpenUSD library: no new files beside it but the
  two plugins' `plugInfo.json`. In the project, `UsdStageImporter` imports `.abc` files, and `.drc`
  files on macOS and Linux; `.abc` becomes an override extension when Unity's Alembic package
  (com.unity.formats.alembic) is installed, as `.usd` does with the USD package. Alembic has no
  units, and OpenUSD reads it as centimeters, as Unity's Alembic package does by default: an
  `.abc` written in meters (Blender's default) imports at 1/100 scale unless
  `ApplyStageUpAxisAndUnits` is off. Materials are not part of what usdAbc reads. Windows has
  Draco too: OpenUSD's build refuses usdDraco in any monolithic Windows build, for symbols only its
  Python module needs, and `Native~/patches/openusd-26.05-windows-draco.patch` allows it without
  Python (Draco is static inside `usd_rt.dll`, no new file). In the macOS, Windows and Linux
  payloads (Windows with Draco since 2026-10-18, macOS and Linux rebuilt with the newest patches
  the same day); a payload without the plugins makes the importer and the tests report them
  missing. Covered by
  `Native~/Tests~/file_format_test.cpp` and `UsdFileFormatTests`, against an Alembic file Blender
  wrote and a Draco mesh OpenUSD's own writer encoded (`Tests/Fixtures~/FileFormats`).
- **Export and record to Alembic (`.abc`)**
  `UsdExporter` and `UsdAnimationRecorder.Save` write `.abc` through usdAbc. Its layers refuse
  every edit, so the native exporter builds such a stage in memory and writes it out in the
  output's format at the end (any format that can be written but not edited). Alembic has no
  skeletons, so every skinned mesh is written in its current pose, and a recording bakes it every
  frame into sampled points; the root prim scales the scene from Unity's meters into centimeters,
  so OpenUSD, Unity's Alembic package and Maya read it at its size. Material bindings and
  `metersPerUnit` are not kept by Alembic, and `ExportTextures` writes nothing for it. A
  `UsdCompositionMode.Preserve` export to `.abc` is refused, as is any `.abc` export from a
  payload without usdAbc.
- **Export to Draco (`.drc`) (0.8.1 native plugin)** An output path ending in `.drc` writes one
  Draco-compressed mesh through usdDraco's writer, which OpenUSD's file format cannot do
  (`WriteToFile` returns false) and which no OpenUSD library exported on Windows:
  `Native~/patches/openusd-26.05-windows-draco.patch` now exports `UsdDraco_WriteDraco` on every
  platform. A `.drc` holds one mesh and no hierarchy, so every visible mesh is merged in the export
  root's space, faces kept facing out through mirroring transforms, with normals and the primary
  texture coordinates where every mesh has them; skinned meshes are written in their current pose,
  and the scene in centimeters, as for Alembic (Draco has no units either). Positions are quantized
  to 14 bits, texture coordinates to 12 and normals to 10, OpenUSD's usdcompress defaults.
  Materials, animation and composition are not kept: a recording or a `UsdCompositionMode.Preserve`
  export to `.drc` is refused, as is any `.drc` export from a payload without usdDraco or below API
  15. Covered by `file_format_test` (two quads, one mirrored, and a hidden one, read back as one
  mesh) and `UsdFileFormatTests`. In the Windows, macOS and Linux payloads.


- **The kept preview stage leaked on every script reload** `GetPreviewInfo` keeps one stage for
  the import that follows, in a static the Editor's script reload loses; the native stage stayed
  allocated. It is now released before the reload, with every `UsdStage` still open.
- **Skinned meshes were left out of exports, silently** `UsdExporter` only looked at
  `MeshFilter`s. Flattened exports now write every `SkinnedMeshRenderer` (as UsdSkel or baked, see
  above), and so does `UsdCompositionMode.Preserve` for the ones added to an imported stage: a new
  character becomes its own SkelRoot and Skeleton in the override layer, and a mesh added to an
  imported character (a garment on its joints) is bound to that character's existing Skeleton prim,
  provided it is inside the character's SkelRoot and was bound in the skeleton's bind pose. One
  that fits neither -- its mesh and bones meet only at a prim of the stage being overridden, whose
  type a SkelRoot would change -- is written in its current pose, with a warning. Skinned meshes
  that came from the source are kept by composition as before; prefab assets get theirs written
  into their own layers.
- **Override exports wrote the importer's helper objects as prims** A `UsdCompositionMode.Preserve`
  export of an imported stage wrote every object without a `UsdPrimSource` as new content, and
  the importer's helpers -- the joint hierarchy under each Skeleton, the turn under a light or a
  camera -- have none: on a warehouse stage with 17 characters the override layer defined 961
  Xforms duplicating the joints, and every reader then saw a second skeleton of plain transforms.
  Helpers are skipped, as the flattened export and `UsdPrimSource` already skip them.
- **Point-instancer prototypes were also drawn where they stand** A prototype is imported once to
  be drawn at its placements, but with `IncludeInvisible` on (the default) its renderers stayed
  on, so every prototype was drawn a second time at its own position. Their renderers are now off;
  the prototype stays in the hierarchy to be inspected.
- **Assets in immutable packages failed to import** `UsdStageImporter` read the file at
  `Path.GetFullPath(assetPath)`, which for a package in `Library/PackageCache` is not where the
  file is. It now maps the asset path to the file on disk, and dependencies inside such packages
  are tracked too.
- **A recording left skinned meshes it could not write as UsdSkel standing still** A renderer the
  skeleton path cannot carry (its bones meet only at an imported prim, say) is written in its
  current pose; the recording now poses and bakes it again every frame and samples its points.
- **Recordings ignored `Renderer.enabled`** A renderer switched off and on during a recording was
  written as always shown. The recorder now samples it like the active state: on a Unity object's
  `<name>_Mesh` prim on its own, and together with the active state on a mesh the importer made,
  whose Xform and Mesh are one prim.
- **`UsdDomeLight` and `UsdSpatialAudio` could not be saved in scenes or prefabs** All three
  scene components lived in one file, `UsdSceneComponents.cs`; Unity finds a MonoBehaviour's
  script by its file name, so two of them had no script asset and came back as missing scripts.
  Each now has a file of its own (`UsdImportHelperObject.cs` keeps the old file's GUID).
- **Objects turned and scaled differently along their axes imported turned wrong** Transforms
  were split into position, rotation and scale with `Matrix4x4.rotation`, which reads the rotation
  straight from the matrix and is only right while the scale is uniform: a box scaled (1, 2, 0.5)
  and turned (10, 30, 0) came back turned (10.9, 19.6, 357.2), 0.12 m off at its corners, in every
  format and in animation tracks alike. The axes are now normalized first; a mirroring matrix puts
  its sign on X.
- **Z-up stages exported flattened, or recorded, lay on their back** The stage the exporter writes
  is always Y-up, but an imported Z-up stage (Isaac Sim, Blender) was written with its Z-up numbers
  as they stood, so it read back turned 90 degrees. The turn the importer had put on the import
  root now goes on the exported root prim.
- **`UsdLibraryScanner.ScanFolder` lists `.abc` and `.drc` files** too.
- **Linux players loaded a second OpenUSD** `UnityPlayer.so` loads every `.so` under the player's
  `Plugins` folder at startup, and the Linux payload was there twice: Unity's own flat copies in
  `Plugins/x86_64/` (the `.so` importers had Linux64 on, and those under `lib/` had no importer
  settings at all) beside the self-contained `Plugins/x86_64/Linux/` tree the build postprocessor
  copies. Two monolithic OpenUSD libraries ran in one process, and the player log said
  `Multiple definitions of TfEnvSetting variable detected (duplicate 'PXR_WORK_THREAD_LIMIT')`.
  The importers now leave the Linux player to the postprocessor's tree, the only copy.
- **A generated `AnimationClip` left Isaac Sim characters in their first pose** Omniverse writes a
  character's joint hierarchy a second time as `SkelJoint` prims below the Skeleton prim, with the
  joints' names, so the import had two siblings called `Root`, and the clip, whose curves find their
  objects by name, animated the prim copies (41 degrees and close to 3 m off on a walk). Prims below
  a Skeleton prim with no mesh under them are now left out, the joints standing for them; a root
  joint still sharing a name with a sibling gets a suffix, and `UsdAnimationClipBuilder` warns when
  a curve's path would reach another object.
- **`Preserve` turned root prims other than the default prim into empty Xforms** When the export
  references an imported stage (it sits below the export root, or `ReferenceSource`), a root prim
  beside the default prim, such as a `SphereLight` or an `OmniGraph`, was written as new content,
  and what the importer had made for it is not a prim. Each root prim is now referenced by its path,
  with the edits on it written through that reference.
- **Built-in projects with URP installed got URP materials** Imported materials took URP's Lit
  shader whenever it existed, which draws nothing on the Built-in pipeline; the active render
  pipeline now picks the shader.
- **A `UsdPreviewSurface` with no `diffuseColor` imported white** The importer started every
  material at white, but UsdPreviewSurface's own fallback for `diffuseColor` is 0.18 grey (linear),
  which other renderers show. A surface that leaves it out is now that grey; a mesh with no
  material at all still gets the default material. Textured surfaces are unchanged (the texture is
  drawn alone), except that one whose texture cannot be loaded and that authors no colour now
  falls back to the grey.

### Security

- **No build machine name in the Linux and macOS payloads** TBB's version banner, compiled into the TBB
  libraries OpenUSD's `build_usd.py` builds on Linux and macOS, recorded the building machine's
  host name, and the payload shipped it. `Native~/patches/openusd-26.05-tbb-build-host.patch`
  (pinned for Linux and macOS) writes `unknown` instead, and the Linux and macOS payloads are rebuilt with it.
  Windows ships Intel's
  prebuilt TBB, which names only Intel's build machine.
- **Damaged binary USD files no longer exhaust memory** OpenUSD 26.05's reader for binary layers
  (`.usdc`, and `.usd`/`.usdz` holding them) allocated for counts read from the file -- array
  lengths, the token, field, spec and path tables, list and dictionary sizes -- before checking
  them against it, and value-initialized what it allocated, so the memory was committed. One
  changed byte in a 921-byte file made it hold 1.3 GB with no error (a compressed integer array
  whose data was short still "succeeded", decoded from uninitialized memory); on Linux and macOS,
  which overcommit, such counts could take the whole machine, and damaged path jumps made it
  spawn work without bound. `UsdImportLimits` could not help: the memory was taken before the
  import counted anything. `Native~/patches/openusd-26.05-crate-hardening.patch`, pinned and
  applied by `Native~/build_openusd.py` after the file format patch, checks every count before it
  sizes an allocation and refuses the file (or the one value) it does not fit. Fuzzing the Windows
  payload with 8,000 mutated `.usdc` files under a 1 GB cap ran 364 of them out of memory; with the
  patch none of those nor 20,000 more take over ~100 MB, and files OpenUSD writes read exactly as
  before. The patch also checks two things that did not take memory: every section in the file's
  table of contents has to lie inside the file (the ranges went unchecked to the prefetch, which on
  Linux and macOS advises memory outside the mapping), and time samples have to hold one value per
  time (with more times than values, the reader took another attribute's samples, of any type, as
  the missing values, with no error; such a layer is now refused as corrupt). `file_format_test` replays five
  damaged files (`fuzz_regressions/crate_*.usdc`): three fail it if they raise the process's peak
  memory by more than 128 MB, and two have to be refused. In the Windows, macOS and Linux
  payloads.
- **The Alembic and Draco readers no longer crash on damaged files** A `.usda` that references an
  `.abc` or `.drc` runs those readers too, so they are part of every import. Fuzzing the payload
  with 16,000 mutated files (each through the importer's full read) crashed it 724 times, in
  OpenUSD's usdDraco and usdAbc plugins and in Alembic's reader: heap writes past buffers sized
  from one part of the file and filled from another (usdDraco's attribute values, Alembic's array
  samples), a polygon walk off the end of its face table that a mesh written by OpenUSD's own Draco
  writer also hit, and null samples dereferenced. Two Unity patches fix them, pinned and applied by
  `Native~/build_openusd.py`: `Native~/patches/openusd-26.05-file-format-hardening.patch` to the
  plugins and `Native~/patches/alembic-1.8.5-hardening.patch` to the Alembic archive (a new
  `archive-patch` row kind, which `build_openusd.py` extracts and patches where `build_usd.py`
  builds it). The same 16,000 files and 8,000 more from two further seeds now crash nothing; a
  damaged file is an import error, or a mesh without the attribute it damaged.
- **A damaged Alembic transform no longer crashes the process** An `.abc` whose transform names an
  operation type Alembic does not have (four changed bytes in a test file) made Alembic's
  `XformSample::getMatrix` read through a null pointer whenever the transform was evaluated, which
  every import does. `Native~/patches/alembic-1.8.5-hardening.patch` now refuses the unknown op,
  and also a transform sample with fewer values than its ops have channels (read past the end of
  a heap buffer), a `.vals` that is not `float64` or an `.ops` that is not `uint8`, and a missing
  sample. Found by fuzzing the Linux payload (about 144,000 runs); replayed by
  `file_format_test` (`fuzz_regressions/abc_xform_unknown_op.abc`). Every payload needs its
  OpenUSD rebuilt with the new patch.
- **Capturing native diagnostics no longer corrupts the heap** With
  `UsdImportOptions.CaptureNativeDiagnostics` (`RUSD_OPEN_CAPTURE_DIAGNOSTICS`) on, OpenUSD's
  warnings were appended to one string from every TBB worker thread that posted one, unguarded;
  a damaged `.usdc` whose warnings came from parallel reads aborted the process with
  `double free or corruption` in two to five of five runs. The buffer is now guarded on every
  access. Replayed 25 times per run by `file_format_test`
  (`fuzz_regressions/usdc_diagnostics_capture_race.usdc`).
- **Face-vertex indices outside a mesh's points are refused** On the path that hands
  `faceVertexIndices` over unchanged they reached Unity's mesh as written (Unity logged an error),
  and the other path quietly drew such a corner at point 0. The mesh is now skipped with a warning,
  as inconsistent face counts are.
- **Array attributes are read as the type their value has** Normals, texture coordinates and
  display opacity were taken out of their value by the attribute's declared type name, which is
  only what the layer claims; a value of another type (a crafted usdc, or damaged Alembic data) was
  reinterpreted as the declared one. The value's own registered type now decides.

### Changed

- **Reference Look renders the Factory 2.6-3.8 x faster: a shadow budget for point lights**
  A shadowed point light renders the stage once per cube face. NVIDIA's Factory (40 M triangles,
  15 sphere lights, a 12-light rect grid) spent most of each frame there. With
  `UsdImportOptions.PhysicalLightUnits` the importer now keeps shadows on the four strongest
  sphere, disk and cylinder lights only (switching the other eleven off changed the views by
  0-2 %), drops the shadows of lights authored invisible (1.5 % of their direct light remains),
  and stops renderers smaller than 0.4 % of the stage's diagonal from casting shadows (they still
  receive them; relative to the stage, so a chess set keeps its pieces' shadows). Rect grids and
  the sun keep theirs. The Factory's seven views went from 137-464 ms to 36-176 ms a frame
  (mean 235 to 81 ms, 1920 x 1080, RTX A4000), and the 47-scene benchmark from mean CIEDE2000
  3.61 to 3.43.
- **Reference Look sample: full render scale and TAA** The pipeline renders at render scale 1 with no
  upscaling (0.67 with FSR left stairs along thin geometry that Isaac Sim's accumulated samples do
  not have) and the camera uses TAA instead of SMAA.
- **The native interface number goes up once per release, and messages name the release**
  The native plugin's interface number (`RUsd_GetApiVersion`) went up with every change: 0.8.0
  shipped 12, and development reached 17 within a week, each step a rebuild of three platforms.
  It now goes up once per release that changes the native interface: everything new since 0.8.0
  is 13, the 0.8.1 interface. Messages and documentation name the release ("needs the 0.8.1
  native plugin"), and the table of both is in `BUILD_NOTES.md` and the manual's *Native plugin
  versions*. A development plugin that reports 14 to 17 loads as a 0.8.0 plugin until rebuilt.
- **Linux: the TBB OpenUSD runs on is checked** The dynamic linker binds a library's calls in the
  process's global scope first, so in the Editor OpenUSD runs on the Editor's own
  `Data/Tools/libtbb.so.2` rather than the payload's (the same TBB 2020.3, interface 11103, in
  the Editors so far). Renaming the payload's TBB, as Windows does with `tbb_usdrt.dll`, cannot
  prevent that on Linux: tested, it left the two TBBs serving one process between them. Instead
  every entry point that opens or creates a stage now refuses, with an error naming the file,
  a TBB older than the one the payload was built against.

- **Preserve exports keep the source's animation**
  An override export compared each imported prim with its snapshot and wrote any difference as
  a Default-time override, which hides every time sample below it: after playback, every
  animated prim would have been exported frozen in its current pose. Transforms and visibility
  that the import's `UsdAnimationPlayer` drives are now left to the source; a pose the animation
  does not explain (an animated object moved by hand) is reported in the console instead of
  written, since writing it would stop the animation.
- **`UsdStageSource` records files inside the project by their project path**
  The import's source was recorded as an absolute path, so a prefab or scene holding an imported
  stage carried the importing machine's user name and checkout location, and an override export
  failed for anyone else who opened the project. A file under `Assets` or in a package is now
  recorded as `Assets/...` or `Packages/<name>/...` (`RecordedSourcePath`); `SourcePath` resolves
  it to the file on disk in the Editor. Files outside the project are recorded as before. The
  layers composition arcs name are recorded the same way. `UsdStageImporter` assets reimport once
  (importer version 2), which also gives them the point caches, dual-quaternion skins and skinned
  instancer prototypes above.
- **A plugin older than 0.8.1 says what it lacks**, once per session: skinned meshes import
  static, no animation is imported and time-sampled values are ignored until the Windows or
  Linux payload is rebuilt.
- **Static values are read at the stage's start time**
  Transforms, points, primvars, visibility, implicit surfaces, lights and cameras are read at
  `startTimeCode` when the stage authors a time range, otherwise at the earliest time (an
  attribute's first sample when it has samples, its default when it has none), as a viewer opens
  the stage. Reading at Default ignored samples: Blender's animated exports author every xformOp
  only as samples, and their objects all arrived at their parent's origin; a mesh whose points
  were only sampled imported with no geometry. Such a mesh now imports its first frame, and its
  other samples as a point cache (see above). Stages without time samples read exactly as before. `RUSD_OPEN_DEFAULT_TIME` restores
  the old reading. Native read time is unchanged within noise on stages without skeletons
  (+0.1 to 1.5% on four test stages); a stage with 17 skeletons and 97 skinned meshes reads 4%
  longer, the cost of reading their weights.

## [0.8.0-exp.1] - 2026-10-14

### Added

- **Runtime Demo sample**
  The standalone USD Runtime Demo project now ships as a second package sample,
  `Samples~/Runtime Demo`: multi-file import through a native file dialog, a translate/rotate
  gizmo, up-axis and unit correction, and export as a referencing layer or a flattened stage. It
  locates its own folder instead of assuming `Assets/USDRuntimeDemo`, carries its three USD files
  in `USDSamples/` (copied into StreamingAssets by a build preprocessor for player builds), and
  its assemblies are skipped rather than failing to compile when the Input System or uGUI is
  missing.

- **MaterialX in every shipped payload**
  OpenUSD was built without `usdMtlx`, so a `.mtlx` reference composed to nothing, silently:
  OpenChessSet lost all 21 materials and rendered magenta while `GetPreviewInfo` already reported
  no materials. The Windows, macOS and Linux payloads are now built with MaterialX 1.39.4 and
  carry its core and format libraries and the standard node library; the wrapper points
  `PXR_MTLX_STDLIB_SEARCH_PATHS` at the copy it ships, so no path from the build machine is
  relied on. Alembic and Draco stay off.
- **`UsdImportOptions.VerifyNativeRuntimeIntegrity`**
  Export could already skip the payload digest check for a locally built wrapper; import, where
  untrusted file content is actually parsed, could not. The import option mirrors the export one
  and is honoured in the Editor and development builds only: a release player verifies regardless.
- **Texture streaming for `ImportAsync` (on by default)** 
  `ImportAsync` waited for every texture to decode before building anything, so a stage with
  large textures showed nothing until the end (ALab: most of its import time is its UDIM
  atlases). It now returns once the hierarchy, meshes and untextured materials are built and
  attaches the textures when they finish decoding on a worker thread; await
  `UsdImportResult.TexturesReady` for the final look. The textured material is built by the same
  code as before and copied onto the one already in use, so the result is identical (checked by a
  material-state hash on ALab and Factory). Measured on Windows (ALab, cold): first geometry 27.6 s
  → 16.5 s, textures complete 27.6 s → 26.3 s, peak commit +0.4 GB. `StreamTextures = false`
  restores the load-everything-before-return behaviour; the synchronous `Import` is unchanged.
  See Changed for what this means for existing callers.
- **UDIM texture sets import as atlases** 
  A `<UDIM>` texture path stands for a grid of tiles, and the importer used the first tile for
  the whole mesh, so every face outside the first UV square sampled the wrong image (915 of
  ALab's 2,047 sets span several tiles, up to 34). The tiles of a set are now stitched into one
  atlas laid out as they sit in UV space, holding only the rows and columns the set has; the
  mesh's UVs are unchanged and the material's base-map scale and offset place them on the atlas.
  An atlas is halved until it fits the graphics device's texture limit, read when the import
  starts. The first tile is found by listing the folder, so sets that start at 1002 or 1011 load.
- **The textures a renderer reads: `full` bindings, EXR, and separate PBR maps** 
  ALab imported without a single texture and without a warning. Materials are now looked up
  through `material:binding:full`, then `preview`, then the all-purpose binding, as Hydra does,
  for meshes and GeomSubsets alike (2 of ALab's 7,485 meshes had a material before). OpenEXR
  images decode (scanline and tiled; NONE, RLE, ZIPS, ZIP; half, float and uint; level 0 of a
  mipmapped file), quantised to 8 bits with an sRGB encode for colour slots. Separate metallic,
  roughness and occlusion maps, which every DCC writes, are packed into Unity's metallic map
  instead of the roughness map landing in the metallic slot (every surface a mirror), and an
  opacity map that is not the albedo's alpha is folded into the albedo (Moore Lane's foliage drew
  solid cards).
- **Texture maps of Omniverse MDL materials** 
  OmniPBR's metallic, roughness and ORM maps were skipped with a warning, and OmniGlass and
  OmniSurface read no textures at all (190, 696 and 120 materials on Factory fell back to flat
  colour). An ORM map (occlusion, roughness, metallic in red, green, blue) is repacked into
  metallic-in-red and smoothness-in-alpha on the decoding thread and assigned to both the
  metallic and the occlusion slot, uploaded once. OmniGlass and OmniSurface maps are read under
  their `*_image` names.
- **Point instancers are culled by cell and can use Mesh LOD** 
  `UsdPointInstancer` sorts its placements into a grid of cells, each with its own bounds, so the
  engine drops the cells that are off screen instead of drawing the whole set or none of it:
  Moore Lane from a camera inside the stage went from 6,511 ms to 2,287 ms a frame, with
  pixel-identical frames. **GameObject > USD > Generate Mesh LODs for Point Instancers** (Editor
  only, since mesh decimation is an Editor API) gives the selected instancers' prototypes LOD
  levels, and `UsdPointInstancer.LodDistance` chooses between them: 6,210 ms to 1,184 ms a frame
  on Moore Lane (RTX A4000). It is a menu item rather than automatic because decimation thins
  foliage visibly; check the result on each stage.
- **Lights, cameras and spatial audio import (native API 7)** 
  UsdLux lights, UsdGeomCamera and UsdMediaSpatialAudio prims arrived as empty transforms (Moore
  Lane alone has 104 lights and 21 cameras). Distant, sphere, disk, cylinder and rect lights now
  become Unity `Light`s (a shaped light becomes a spot), cameras become disabled `Camera`s with the
  authored field of view and clipping range, and spatial audio becomes an `AudioSource` with a
  `UsdSpatialAudio` record of its file (the clip is not loaded). Lights and cameras sit on a child
  turned to Unity's +Z convention, marked `UsdImportHelperObject` and skipped by both exporters.
  A dome light is recorded on a `UsdDomeLight`; `ApplyDomeLightToEnvironment` (off by default)
  turns it into the scene's ambient colour and a panoramic skybox. Light intensities are
  normalised against the brightest non-dome light on the stage, so the relative brightness the
  stage encodes is kept whatever convention it was authored in; directional lights get a lower
  ceiling, since Unity treats 1 as daylight. Options: `ImportLights`, `ImportCameras`, `ImportSpatialAudio`. Native API 7
  adds `RUsd_GetImportLight(Count)`, `RUsd_GetImportCamera(Count)` and
  `RUsd_GetImportAudio(Count)`; an API 6 plugin still loads and imports everything else.
- **Identical meshes share one Unity Mesh** 
  Instanced parts (instanceable prims, point-instancer copies) produced one Unity `Mesh` per
  instance. Meshes with identical geometry now share one, found by a content hash and confirmed by
  a full comparison, so memory grows with the number of distinct shapes. Materials still differ
  per object. `UsdImportResult.UniqueMeshCount` reports the distinct count;
  `ShareIdenticalMeshes = false` restores one Mesh per prim. Editing `MeshFilter.sharedMesh` now
  changes every object that shares it.
- **Exports that keep USD composition** 
  `UsdExportOptions.Composition = UsdCompositionMode.Preserve` writes composition instead of
  copies. An imported stage is written back as a layer that sublayers its source file and holds
  only what changed in Unity: moves as `xformOp:transform` overrides, deactivated objects and
  disabled renderers as `visibility`, deleted objects as `active = false`, added objects as new
  prims, the import's variant selections, and `instanceable = false` on an instance before
  anything below it is edited. Prefab instances reference one asset per prefab, with each
  instance's differences as overrides; `Layout` keeps the assets in the file as `class` prims
  (`SingleFile`) or writes one file each (`FolderWithAssets`). With `ShareMeshes`, objects that
  share a mesh and materials reference one instanceable prototype. `.usdz` output packages the
  source layer and asset files with it. The default (`Flatten`) output is byte-for-byte unchanged.
- **Prefab links in players** 
  `UsdPrefabMarker` carries the prefab an object came from into Play Mode and players, where
  Unity keeps no prefab connection. Scene instances are marked automatically when a scene is
  built or enters Play Mode (only that copy of the scene; define
  `USD_TOOLKIT_DISABLE_PREFAB_MARKERS` to turn it off); runtime instances use
  `UsdPrefabMarker.Instantiate` or `Attach`.
- **Variant selection and payload control on import** 
  `UsdImportOptions.VariantSelections` composes the stage with the variants given (in the session
  layer; the file is not edited), and `LoadPayloads` / `PayloadsToLoad` choose which payloads
  load. An unloaded payload's prim still arrives as an empty GameObject.
  `UsdImportResult.Source` (`UsdStageSource`) lists the stage's variant sets, payloads and direct
  references, payloads, inherits, specializes and relocates, and
  `CreateOptionsWithVariant` re-imports with another variant. With `RecordSource` (on by default)
  every imported object carries a `UsdPrimSource` with its prim path and the state it arrived in.
- **Native API 6** 
  Twelve additive entry points: `RUsd_BeginLayerExport`, `RUsd_DefinePrim`, `RUsd_AddReference`,
  `RUsd_SetInstanceable`, `RUsd_SetActive`, `RUsd_SetVariantSelection`,
  `RUsd_SetLocalTransform`, `RUsd_SetVisibility`, `RUsd_PrimExists`, `RUsd_OpenStageEx`,
  `RUsd_GetImportNodeFlags` and `RUsd_GetImportComposition`, covered by
  `Native~/Tests~/composition_test.cpp`. An API 5 plugin is
  refused (see Changed).
- **Linux x64 players** 
  `Unity.USDToolkit` and `Unity.USDToolkit.Samples` now include `LinuxStandalone64`. The Linux
  native payload, its plugin settings and the build postprocessor already handled Linux players,
  but the C# assemblies left them out, so a Linux build shipped the native libraries without the
  API. The Linux payload is now built on Ubuntu 22.04 and needs glibc 2.34 or later
  (`GLIBCXX_3.4.29`), down from glibc 2.38, so it loads on Ubuntu 22.04 and newer.

### Changed

- **`ImportAsync` returns before its textures are on** 
  With `StreamTextures` on by default, code that exports, captures, saves a prefab or inspects
  materials right after `await ImportAsync(...)` now sees untextured materials. Await
  `result.TexturesReady` first, or set `StreamTextures = false`. `ProgressCallback` reports
  "Streaming textures" at 0.9 once the objects are built and "Done" (1) only when the textures are
  attached -- or have failed, or the import was destroyed first. A texture decode that fails no
  longer fails the import: it is logged as a warning, with each underlying reason rather than
  "One or more errors occurred", and the materials keep their authored values.
- **Native API 6 or newer is required**
  `UsdNative.MinimumApiVersion` is 6: an API 5 plugin is refused instead of loading without
  composition. A newer plugin loads, and the features added since switch on when it reports them:
  lights, cameras and audio at 7, point-instancer arrays at 8, metallic and texture layouts at 9
  and 10, native EXR decode and block compression at 11, native image and UDIM atlas builds at 12.
  The shipped Windows, macOS and Linux payloads are all API 12.
- **package.json states what the package needs** 
  `unity` said 2023.1 while the documentation requires 6.4, so the Package Manager offered the
  package to Editors it does not support; it is now `6000.4`. The built-in modules the code calls
  are declared: `com.unity.modules.imageconversion` (texture decode and PNG export),
  `com.unity.modules.physics` (`GenerateColliders`) and `com.unity.modules.audio` (spatial audio
  import). URP and HDRP stay undeclared; they are only
  looked up by shader name.
- **The samples import from the Package Manager** 
  0.6.3 moved the samples into an always-compiled `Samples/` folder inside the package. Unity
  installs a package from a Git URL or a tarball as read-only, so it refused to open the sample
  scenes there ("It is not allowed to open a scene in a read-only package"), and the Package
  Manager Samples tab was empty. The samples now live in `Samples~/Runtime Examples` with a
  `samples` entry in `package.json`. Import them from the Samples tab, and Unity copies them under
  `Assets/Samples`, where the scenes open and save. The three examples ship as one entry because
  they share the internal `SampleTheme` type. The package no longer compiles the samples until you
  import them, so they no longer add code to every player build. Every file was moved with its
  `.meta`, so asset GUIDs are unchanged. If a project still has an older imported copy under
  `Assets/Samples/Unity USD Toolkit`, delete it before importing again, because both copies define
  the `Unity.USDToolkit.Samples` assembly.
- **The samples assembly builds only where the runtime does** 
  `Unity.USDToolkit.Samples` was compiled for every platform, but the `Unity.USDToolkit` assembly
  it references only targets the Editor, Windows x64 and macOS. A player build for any other
  target failed with CS0246, even in a project that never used the samples. The samples assembly
  now targets the same platforms as the runtime, so other targets skip it and build.
- **README and manual name the Unity 6.4+ Package Manager menu** 
  The Package Manager opens from **Window > Package Management > Package Manager**.
- **The Export and Import sample UIs are in English** 
  The tooltips on their checkboxes and buttons, and the code comments in `UsdExportExample` and
  `RuntimeImportBrowser`, were still in Korean. They're now in English.
- **A dependency digest record can be taken without running a build.**
  `Native~/verify_dependency_digests.py` gained `--scan`: it enumerates the dependency tree under
  `--root` itself instead of being handed the file list a build script assembles. The records for
  Windows and Linux were missing because taking one meant running that platform's build, and the gate
  sits after the compile — so the machine that has the OpenUSD tree could not simply record what it
  has. With `--scan` it can, and committing the record changes one text file: no binary is rebuilt,
  so the payload digests and the SBOM stay as they are. The scan rules mirror each platform's build
  (for Linux, the monolithic assembly in the rebuild runbook, not `build_linux.sh`) and reproduce the
  existing macOS record exactly — 89 of 89 files, same digests, with no build — which is the check
  that they describe what a build actually copies.
- **The supported operating systems are written down**
  Nothing stated a minimum OS before, although the native payload has always had one and it
  applies to players as well as to the Editor: Windows 10 21H1, macOS 12.0, Ubuntu 22.04 (glibc
  2.34, `GLIBCXX_3.4.29`). `README.md` and both user manuals carry the table.
- **A native load failure names the OS requirement.** A library that is present and intact but
  built for a newer OS is refused by the loader and surfaces as "file not found", which sent
  people looking for a missing payload. The exception text now adds the platform's minimum — on
  Linux the glibc and `GLIBCXX` versions with `ldd --version` to check them, on macOS 12.0 — on
  top of the existing payload checklist. Import failures inherit the same message.
- **The user manual is current again, and there is an English edition.**
  `Documentation~/Unity USD Toolkit User Manual.md` still described version 0.1.0 — export only,
  Windows only, no textures, no import — and is now rewritten against 0.7.2-exp.1 and split into
  `Unity USD Toolkit User Manual KR.md` (the same file, renamed) and a new
  `Unity USD Toolkit User Manual EN.md` with the same structure. New sections cover the runtime
  import API, how untrusted USD files are handled (asset-path confinement, topology checks, PNG
  dimension caps, plugin-path confinement, and the opt-in for each), the Live Sync sample's auth
  handshake, and payload integrity verification. The superseded
  `Unity USD Toolkit User Manual.docx`, which described 0.1.0 as a Windows-only export plugin,
  was removed rather than left to contradict the manual; it remains in git history.
- **Korean comments translated to English** across the package — most importantly the XML doc
  comments on `UsdExportOptions`, which surface in IntelliSense for every consumer of the public
  API. Comments only: no behaviour change, and the native payload was not rebuilt, so the digest
  manifest still matches.

### Fixed

- **Every mesh gets normals, and `leftHanded` winding is honoured**
  A mesh with no authored normals that needed no vertex split arrived with an empty normal
  buffer, since the smooth-normal fallback existed only on the split path (Moore Lane's terrain,
  1,048,416 points, had none and read as inside out). Normals and triangulation also assumed
  `rightHanded`; all 740 of that stage's meshes author `leftHanded`, so computed normals were
  inverted and the terrain was visible only from below. Both are read as authored now.
- **Per-face `displayColor`**
  A `uniform` `displayColor` is one colour per face, but it was averaged into one colour
  (Creases_SpinningPyramids came in as one muddy tone instead of 24). Each distinct colour now gets
  its own material and submesh, whether `displayColor` is the only shading or the bound material
  reads it through a primvar reader (the Teapot). Past 64 distinct colours the mean is used and a
  warning says so.
- **Asset paths are anchored to the layer that authored them, outside the root folder too**
  Outside the root layer's folder the resolved path was discarded and the authored one returned,
  so `materials/red.usda` pointing at `../textures/...` was re-anchored onto the stage folder and
  looked for a file that does not exist. What USD resolved is now returned.
- **The package compiles on Unity 6.6**
  `Object.GetInstanceID()` is obsolete from 6000.4 and an error from 6000.6, so
  `Unity.USDToolkit.dll` was not produced at all on 6.6. The two call sites use the id only as a
  key and now use `GetEntityId()`, which identifies the same object.
- **The native API mismatch message names the right version**
  `UsdNative.LatestApiVersion` still said 7, so a refused plugin was told the package's source was
  API 7. It is 12, matching the shipped payloads.
- **Export writes the texture file the import read** 
  The exporter rendered every texture to a render target, read it back and encoded a PNG, which
  carried block-compression artefacts and cut an EXR to 8 bits. Textures the importer uploaded
  as decoded now remember their source file, and the exporter copies that file byte for byte,
  keeping its format (8-bit formats only for colour slots, which are declared sRGB). Repacked
  maps (ORM, packed metallic/roughness) still go through the readback: Factory round trip, 8 of
  12 textures byte-identical.
- **A UDIM texture named without a dot no longer fails the import on Windows** 
  The texture name was taken from the atlas key with only `.<UDIM>` removed, so `tex_<UDIM>.png`
  or `tex<UDIM>.png` left `<` and `>` in the path handed to `Path`, which throws on Windows: the
  whole import failed, or with streaming every texture was dropped. The token is now removed
  whatever precedes it, and the separator it leaves is trimmed (`tex_<UDIM>.png` → `tex`).
- **Native texture memory is freed when an import does not upload it** 
  A compressed chain built by the native side (API 12) is released when it is uploaded, and had no
  other owner: one never uploaded stayed allocated for the life of the process. That happened
  whenever a streamed import was destroyed before its textures arrived -- a scene change or a
  re-import, gigabytes on ALab -- and when an import failed after decoding. Every import now frees
  what it did not upload once it ends.
- **EXR and PNG decode natively on Windows too** 
  The native image codecs (API 11, 12) looked a zlib up at run time (`libz.so.1`, `libz.1.dylib`,
  `zlib1.dll`) and handed decoding back to C# where none opened. Linux and macOS have one;
  Windows does not, and neither does the Unity Editor or the OpenUSD install, so the Windows
  payload quietly decoded every EXR tile and PNG in managed code at a fraction of the speed. The
  inflate is now compiled into the wrapper from the libdeflate OpenUSD vendors for its OpenEXR
  reader (`Native~/src/UsdDeflate.c`, MIT, part of the pinned OpenUSD tree, checked file by file
  against recorded digests at configure time), so every platform takes the same path with the
  same bytes and no library has to be found. Building the wrapper needs the OpenUSD source tree
  beside the install (`<install>/openusd-src`, where `build_openusd.py` leaves it) or
  `OPENUSD_SRC`. `Native~/Tests~/image_codec_test.cpp` decodes a PNG and a ZIP EXR and checks
  the pixels.
- **A point instancer's prototype keeps its root transform** 
  Since instances are drawn from their arrays (API 8), the prototype root's own transform was
  left out twice -- by `ComputeInstanceTransformsAtTime(ExcludeProtoXform)` and again by the
  relative transform of each mesh below the root -- so a prototype authored at, say, y = 0.5
  drew every instance at y = 0. `IncludeProtoXform` restores it. `prim_import_test` now checks
  the instanced records rather than expanded prims, and `composition_test` expects the hidden
  seat not to be read, as invisible geometry has not been since the import measurements.
- **Imported colours are no longer darkened and oversaturated** 
  USD shading colours are linear; Unity material colours are sRGB in a Linear colour space
  project. The importer passed the linear value straight through, so every constant colour (base,
  emission, displayColor and primvar colours, MaterialX and MDL constants) rendered too dark and
  too saturated: the Teapot's (0.325, 0.825, 0) showed dark green where Isaac Sim and every other
  USD renderer show yellow-green. Colours are now converted to sRGB on import and back to linear on
  export. Gamma colour space projects are unchanged.
- **Re-exporting an imported stage keeps its prim paths and units** 
  A flattened export of an import root wrote the root GameObject as a prim, so `/Teapot` came
  back as `/Teapot/Teapot`, and every mesh the importer created became an Xform with a
  `<name>_Mesh` child; a round trip matched no path at all. The export now starts at the stage's
  own root prim, writes imported meshes back at their own paths, and keeps the source stage's
  `metersPerUnit` unless `UsdExportOptions.MetersPerUnit` is set to something else (the Elephant
  came back 100 times too large).
- **Preserve exports keep the source's time rate** 
  The layer a Preserve export writes over its source did not copy the source's
  `timeCodesPerSecond`, `framesPerSecond`, `startTimeCode` and `endTimeCode`. The stage rate is
  the root layer's, so the new layer's default 24 made USD rescale the source's samples: the
  Elephant's 60 fps animation played at 0.4 speed. They are now copied from the strongest
  sublayer.
- **MaterialX transmission** 
  A transmissive `standard_surface` (glass; OpenChessSet's pawn tops) with base colour 1
  imported as an opaque white surface. It now takes the transmission colour in proportion to the
  weight and becomes partly transparent, as the OmniSurface path already did.
- **Isaac Sim's render settings no longer arrive as a GameObject** 
  Typeless prims holding only things that are not drawn (Isaac's `/Render` with RenderSettings,
  RenderProduct and RenderVar, which it writes into every stage it saves) are skipped, as
  material-only containers already were.
- **Z-up and centimeter stages import upright and at their real size** 
  The importer read a stage's `upAxis` and `metersPerUnit` but never applied them, so Isaac Sim
  and other Z-up stages came in lying on their side and a `metersPerUnit = 0.01` stage came in
  100 times too large. The import root now carries the correction: a -90 degree rotation about X
  for a Z-up stage (USD +Z becomes Unity +Y, USD +Y becomes Unity -Z) and a uniform scale of
  `metersPerUnit`. Vertices and prim transforms are unchanged, so a Preserve export writes no
  override for it and no longer warns about the corrected root. `UsdImportResult` and
  `UsdStageSource` report the correction as `StageCorrectionRotation` and
  `StageCorrectionScale`. Set `UsdImportOptions.ApplyStageUpAxisAndUnits = false` to get the
  stage's raw coordinates under an identity root, as before.
- **Meshes without a material are grey, not magenta** 
  A mesh or submesh with no material binding was left without a material and rendered magenta
  under URP and HDRP, which looks the same as a shader that failed. It now gets a shared neutral
  grey lit material, `UsdDefaultMaterial` (base colour 0.7, smoothness 0.5, one per import, in
  `UsdImportResult.DefaultMaterial`), using the same URP, HDRP or Built-in shader as imported
  materials. Magenta now only means a real failure. `UsdImportOptions.AssignDefaultMaterial =
  false` turns it off.
- **PointInstancer instances are drawn, and their prototypes no longer show in place** 
  A `UsdGeomPointInstancer` imported as an empty transform with its prototype prims drawn once
  where they were defined: OpenChessSet lost its 16 pawns, Moore Lane its forest, and AGV Fleet
  showed 635 prototype meshes on top of their instances. Each prototype is now imported once,
  hidden, and the instancer becomes one GameObject with a `UsdPointInstancer` that draws every
  placement from the instance arrays with `Graphics.RenderMeshInstanced` (native API 8; USD's
  `ComputeInstanceTransformsAtTime` does the placement, honouring `invisibleIds`). Moore Lane's
  229,717 instances are 738 mesh records instead of 297,518. Instances are not GameObjects, so
  they cannot be picked or given colliders one by one.
- **Cube, Sphere, Cylinder, Cone, Capsule and Plane import as meshes** 
  These implicit surfaces arrived as empty GameObjects (Isaac collision shapes and simple props
  among them). They are now tessellated from their schema parameters (`size`, `radius`, `height`,
  `width`, `length`, `axis`) with normals and UVs, and take their material binding like a mesh.
- **proxy and guide geometry is skipped** 
  Prims whose computed `purpose` is `proxy` or `guide` were imported next to the render geometry;
  ALab, which carries a proxy of most assets, imported a quarter of its triangles twice. Only
  `default` and `render` geometry is imported now, and purpose inherits down the hierarchy.
- **Textures from material layers in other folders are found** 
  A relative texture path is relative to the layer that authored it. Vehicle Variants keeps its
  materials in `materials/` pointing at `@../textures/...@`; that was handed to the importer as
  is, resolved against the stage folder, judged to point outside it and refused, so every
  material imported white. The path is now given relative to the stage folder whenever USD
  resolved it to a file inside that folder. Paths that really leave the folder are still refused.
- **faceVarying primvars no longer split every corner** 
  A non-indexed `faceVarying` UV or normal gave every face corner its own value index, so every
  corner became its own vertex even where neighbouring corners carried the same value: 3.1 to 3.4
  times the vertices on large scenes, with the same triangles. Corners are now merged by value,
  and only real seams split.
- **Subdivision surfaces are reported** 
  A mesh with an authored `subdivisionScheme` other than `none` is still imported as its control
  cage, and the import now says so once per mesh. An unauthored scheme does not warn.
- **MaterialX materials import** 
  A material whose surface comes from a `.mtlx` file (the `mtlx` render context, as in
  OpenChessSet) imported as "no surface shader source" and fell back to the default material.
  `standard_surface`, `open_pbr_surface` and MaterialX `UsdPreviewSurface` now import: base colour
  and weight, metalness, roughness, opacity, emission, and base colour, normal (through a
  `normalmap` node) and emission textures from node graphs. Inputs the file leaves unset take the
  MaterialX node definition's default. Separate metalness/roughness images are not put into
  Unity's packed metallic map; the import says so. Every shipped payload includes MaterialX (see Added).
- **Colours read from a primvar** 
  `UsdPreviewSurface.diffuseColor` fed by `UsdPrimvarReader_float3`/`_float4` (the Teapot sample
  reads `displayColor` this way) imported white. Each mesh now gets the primvar's colour, meshes
  with the same colour share one material, and a mesh without the primvar gets the reader's
  `fallback`.
- **Faceted shading on meshes without normals** 
  A mesh with no authored normals whose vertices are split by a per-corner attribute, typically
  `faceVarying` UVs, was flat-shaded: after the split no two faces share a vertex, so
  `RecalculateNormals` could not average across them. Such meshes now arrive with smooth,
  area-weighted normals computed before the split, as USD renderers draw them. Subdivision
  surfaces are still imported as their control cage.
- **Typeless and custom-schema prims are kept** 
  Prims with no type (`def "rig"`, used by ALab to hold referenced light rigs) and prims of a
  schema the toolkit does not know were dropped from the hierarchy. They now import as empty
  GameObjects. Containers that hold only materials stay out.
- **Rebuilding the payload keeps its `.meta` files** 
  `build_windows.ps1` and `build_macos.sh` deleted the committed Unity `.meta` files with the old
  OpenUSD runtime files, so every rebuild showed them as deleted and a fresh import would assign
  new GUIDs. They are now kept, and only a `.meta` whose file is gone is removed, as `build_linux.sh` already did. The Linux MaterialX rpath pass skips them too, so a rebuild
  no longer stops on a `.meta` with "not an ELF executable".
- **The Editor no longer looks for another platform's native files** 
  The Editor defines the scripting symbols of the active build target alongside its own, so after
  switching the build target to Linux (or macOS) on a Windows Editor the toolkit searched for `.so`
  (or `.dylib`) files, set `LD_LIBRARY_PATH` and read the Linux hash table, and every import or
  export failed with "native runtime files are missing". The platform conditions in `UsdExporter`,
  `UsdImporter` and the generated `NativeRuntimeHashes` now test the player symbols only outside
  the Editor.
- **Instanced geometry is imported.** The import walked the stage with the default traversal,
  which stops at an instanceable prim, so everything inside an instance was skipped. Omniverse
  and most CAD exports build assemblies from instances, and keep the source geometry under an
  invisible `/World/Prototypes`, so such a stage imported with only those hidden copies and
  nothing on screen (`KukaArm1.usd` from the Omniverse Factory sample: 114 meshes, 0 visible).
  The walk now includes instance proxies (`UsdTraverseInstanceProxies()`): the same stage
  imports 114 visible meshes. Materials bound inside a prototype are shared across its instances.
- **Omniverse MDL materials import with their colours.** Omniverse authors shading as MDL under
  the `mdl` render context only (`outputs:mdl:surface`), which the universal-context lookup
  never sees, so every material came in as default white with a "no surface shader source"
  warning. The importer now falls back to the `mdl` context and reads the Omniverse library
  shaders: OmniPBR and its variants (diffuse colour, tint and texture, metallic, roughness,
  normal map, emission, opacity, UV scale/offset), OmniSurface (base colour, metalness,
  roughness, emission, opacity, transmission as partial alpha) and OmniGlass (tinted
  transparent). Another MDL module contributes its base colour if it has one under a common
  name, with a warning. OmniPBR's separate metallic/roughness/ORM textures have no equivalent in
  the packed map Unity uses, so those stay at their constants and say so. UsdPreviewSurface still
  wins when a material carries both. No ABI change.
- **`primvars:displayColor` is used when a material cannot be.** A mesh with no material, or one
  whose shader is unsupported, takes its (inherited) displayColor and displayOpacity, averaged
  when authored per vertex or face, instead of default white. That is what every USD viewer
  shows for such a mesh.
- **Export writes each distinct material once and adds `primvars:displayColor`.** Every mesh used
  to get its own `Material_N` prim, so an assembly of hundreds of parts exported hundreds of
  identical materials. Materials are now shared by value. Each mesh also carries its base colour
  as displayColor (per face for multi-material meshes, with displayOpacity when translucent), so
  viewers and renderers that do not evaluate UsdPreviewSurface show colour instead of grey.
- Every payload has since been rebuilt with these fixes; see the API 12 entries under Security.
- **Textures packaged in a `.usdz` load again.** The symlink check added
  (above) looks at every path component below the stage folder and treated one it could not stat
  as unsafe. A packaged texture's path — `0/tex.png` next to `scene.usdz` — names nothing on disk,
  so every texture in every `.usdz` was refused with *"texture path resolves outside the stage
  folder"* before the resolver that reads it out of the package was consulted: a `.usdz` export
  re-imported with no textures (0 of 23 for McUsd, 66 warnings). A missing component cannot be a
  link, so it is now skipped and its parent still checked; any other failure to classify a
  component is still a refusal. Nothing is read off disk for such a path, and the native resolver
  it reaches confines the result independently (`IsResolvedAssetInsideStageRoot`, via
  `TfRealPath`). Verified on Windows: the `.usdz` round trip keeps 23/23 textures with no warning,
  and a stage whose texture folder is a junction still has all 66 of its textures refused.
- **The macOS payload no longer carries the build machine's user name.** `__FILE__`, which
  OpenUSD's `TF_AXIOM` / `TF_VERIFY` macros expand, and `__PRETTY_FUNCTION__`, which prints the
  file a lambda passed as a template argument was written in, both embed the absolute path of what
  is being compiled. Built from a home directory, that put 1,134 strings naming a developer's home
  path inside `libusd_ms.dylib` and 8 inside the wrapper, and they shipped to anyone who unpacked
  the package. OpenUSD is now built from `/Users/Shared/usd-26.05`, a path with no user name in
  it, and `Native~/CMakeLists.txt` passes `-ffile-prefix-map` for the package root and the OpenUSD
  install so the wrapper's own strings read `/usd-toolkit/...` and `/openusd/...` wherever it is
  built. All five macOS dylibs now contain zero references to a user directory. (One non-path
  identifier survives on macOS and Linux alike: the oneTBB build-host banner, tracked in
  `BUILD_NOTES.md`.) Same source and
  the same public `v26.05` tag as before — only the four compiled dylibs changed, every plugin
  descriptor is byte-identical, and the deployment target stays 12.0. The Linux payload has the
  same leak (536 strings in `libusd_ms.so`, 4 in the wrapper) and is not fixed here; it needs a
  rebuild on that machine. Windows is unaffected, since MSVC records `__FILE__` relative.
- **The Linux payload no longer carries the build machine's user name either.** The same
  `__FILE__` / `__PRETTY_FUNCTION__` leak: built under a home directory, `libusd_ms.so` held 536
  strings naming the developer's home path and the wrapper 4. OpenUSD and oneTBB were rebuilt on
  2026-09-24 from `/opt/usd-26.05` — a fresh full clone of the public `v26.05` tag (commit
  `2095faf`) with the same flags as the other two platforms — and the wrapper with the
  `-ffile-prefix-map` flags added for macOS. All three Linux ELF files now contain zero `/home/`
  strings; the wrapper's OpenUSD header paths read `/openusd/include/pxr/...`. Same source and
  tag, so every plugin descriptor is byte-identical and only the three compiled files, their
  digests in the manifest, the SBOM and the Linux dependency record change. The glibc / libstdc++
  floor was unchanged by this rebuild; the Ubuntu 22.04 build has since lowered it to glibc
  2.34 (see Added). One item is recorded, not fixed, and it is not
  Linux-specific: oneTBB's own build writes a `TBB: BUILD_HOST <hostname>` banner into every TBB
  library, and on both non-Windows machines that host name identifies a person — the Linux one
  contains the user account name, the macOS one is the default "<owner>'s MacBook Pro" form.
  Windows carries a neutral machine name. It predates these rebuilds and is now the only
  developer name left anywhere in the payload; removing it means building oneTBB under a neutral
  host name, another full rebuild per platform for one string per file. `BUILD_NOTES.md` tracks
  it under "Build host names in the oneTBB stamp".
- **The macOS payload no longer requires macOS 26.** Nothing in the build set
  `CMAKE_OSX_DEPLOYMENT_TARGET`, so each dylib took the SDK default — whatever the build machine
  was running — and the payload shipped as `minos 26.0`, which dyld refuses to load on anything
  older. OpenUSD, oneTBB and the wrapper were rebuilt with a deployment target of **12.0**,
  Unity 6.3's minimum for a macOS player (the Editor's own minimum, 13.0, is above it), and all
  five dylibs now report `minos 12.0` on both the `x86_64` and `arm64` slice.
  `Native~/build_macos.sh` takes `--deployment-target` and defaults to 12.0, so this cannot
  silently drift with the build machine again; build OpenUSD itself with the matching
  `MACOSX_DEPLOYMENT_TARGET`, as `BUILD_NOTES.md` now says. The rebuild is from the same public
  `v26.05` source as before: of the 89 recorded dependency digests only the four compiled dylibs
  changed, every plugin descriptor is byte-identical, and `Native~/build_macos.sh` is now
  executable in git, as the other build scripts already were.
- **The native build scripts verify the dependency tree they copy, and refuse to ship unsigned
  by default**. All three scripts copied the local
  OpenUSD/TBB tree into `Runtime/Plugins` verbatim, so a tampered dependency checkout on the
  build host entered the package unnoticed; macOS additionally defaulted to an ad-hoc
  `--sign '-'` signature, which carries no publisher identity, and Windows and Linux produced
  unsigned artifacts. Now each script checks every file it is about to copy against
  `Native~/dependency-digests/<platform>.sha256` and stops before touching the payload if the
  record is missing or no longer matches; `--record-dependency-digests` (macOS, Linux) and
  `-RecordDependencyDigests` (Windows) update that record deliberately, so a dependency change
  has to appear in a reviewed diff. Signing is no longer a silent default: `build_macos.sh`
  requires `--codesign-id`, `--adhoc-codesign` or `--skip-codesign`, and a real identity now gets
  a secure timestamp instead of `--timestamp=none`; `build_windows.ps1` requires
  `-SigningCertificateThumbprint` or `-SkipSigning` and applies Authenticode **after** the
  in-place import patch, which would otherwise invalidate the signature. This records the
  intent — actually signing a release still needs a Developer ID and an Authenticode
  certificate, which the project does not yet have.
- **A release player always verifies the native payload.**
  `UsdExportOptions.VerifyNativeRuntimeIntegrity` can still be turned off in the Editor and in
  development builds, where a developer may be iterating on the native plugin, but a
  non-development player ignores the opt-out and warns. Post-distribution substitution is the
  threat the check exists for, and a shipped build is exactly where it applies.
- **A stale plugin directory from OpenUSD 26.08 is removed from the macOS and Windows payloads.**
  `usdLuxValidators` exists only in 26.08, but survived the 26.05 rebuilds because
  `build_windows.ps1` copied over the top of its destination instead of clearing it first, so a
  26.05 runtime was registering a descriptor from a version it was not built from. The Windows
  copy now clears the destination, as the macOS and Linux paths already did. With descriptors
  covered by the manifest, a leftover like this now fails the integrity check instead of being
  recorded as expected content.
- **The integrity manifest now covers plugin descriptors, is keyed by path, and rejects added
  files**. The manifest fingerprinted only
  `.dll`/`.dylib`/`.so` — 11 files — while the 209 `plugInfo.json`, `.usda` and `.glslfx` files
  that tell OpenUSD *which library to load* were not covered at all. Worse, the verifier walked
  only the top level of each base path, so those descriptors were never even looked at, and a
  file it did not recognise produced a warning rather than a failure. An attacker able to write
  to the payload could therefore add a library, repoint a descriptor at it, and still be told the
  payload was intact. Now: the generator hashes descriptors as well (220 entries), keys every
  entry by `<platform>/<path relative to that platform's payload root>` so that per-platform
  copies of the same file name cannot be confused and a file in an unexpected directory cannot
  masquerade as an expected one, and the verifier walks the payload recursively and **fails** on
  any file inside `lib/usd`, `plugin/usd` or `share/usd` that the manifest does not list. A file
  merely sitting beside the payload is still only reported, because in a player build that folder
  can be shared with other packages.
- **Plugin descriptors' `LibraryPath` values are confined before OpenUSD is pointed at them**
  Each shipped `plugInfo.json` names the library
  OpenUSD loads in-process; a descriptor pointing outside the package's own payload is arbitrary
  code execution. Every `LibraryPath` is now resolved and checked against the payload root before
  `PXR_PLUGINPATH_NAME` is written, and configuration fails if any escapes. This is deliberately
  independent of the digest check above, so it still applies when
  `UsdExportOptions.VerifyNativeRuntimeIntegrity` is switched off.
- **The macOS payload is rebuilt from the public OpenUSD `v26.05` tag** 
  It was previously compiled from a non-public 26.08 source drop whose tree
  carries no git history, so its exact revision could not be published and nobody outside the
  build could reproduce or inspect it. The review's alternative — publishing the exact commit for
  the 26.08 drop — was not available for that reason, leaving the rebuild as the only route. All
  three platforms now build from the same published commit (`2095faf`, "Merge release v26.05")
  with the same flags, so a third party can reproduce the payload and compare. No ABI change:
  `RUsd_GetApiVersion()` still reports 5, and `RUsd_GetOpenUsdVersion()` now reports `0.26.5` on
  macOS instead of `0.26.8`. This also makes the "OpenUSD 26.05" claim in `README.md` and
  `package.json` true for every platform, which it was not before.

  This does **not** by itself close the finding: the review also requires integrity values that
  come from an independent source rather than from the shipped binaries, plus code signing with a
  verifiable publisher identity. Those remain open.

### Performance

- **Textures decode in parallel, in native code, and are block-compressed by default** 
  Texture work, not USD composition, was most of an import (Moore Lane: 36 of 48 seconds, and 5.9
  GB of RGBA32 textures). Images now decode on every core; EXR and PNG decoding, UDIM stitching,
  metallic/roughness and albedo/opacity packing and BC1/BC3 compression with mips run in the
  native wrapper (API 11 and 12, SSE2 on x86, large images compressed in row bands), and a
  compressed chain is uploaded straight from native memory and freed once uploaded. Opaque images
  take BC1 and the rest BC3; `CompressTextures = false` keeps them uncompressed for stages whose
  textures carry data rather than appearance. The managed decoders remain the fallback for an
  older payload or a format the native reader declines. ALab (cold, with preview) went from 80.5
  s to 23.5 s and Moore Lane's texture memory from 5,875 MB to 1,592 MB.
- **Reading the stage is faster, and invisible geometry is not read** 
  The native vertex split keyed a hash map with a string per face corner; values are now hashed
  in place in flat, pre-sized tables, with the same merge results bit for bit (Moore Lane's read
  9.8 s → 4.4 s, ALab 4.4 s → 2.9 s). A prim that is invisible keeps its GameObject and
  visibility, but its mesh is no longer read, as Hydra does not populate it (1,274 of Factory's
  meshes): making such an object visible later shows nothing. `ImportAsync` reuses the stage
  `GetPreviewInfo` opened for the same unchanged file instead of opening it again.

### Security

- **Point instancers can no longer import without end** A prototype that contains its own
  instancer -- the instancer's ancestor, or another instancer whose prototype leads back -- was
  imported again on every visit, bounded only by depth 64: 600 bytes of usda grew past 11 GB.
  Such a prototype is now skipped with a warning. The import also counts what it produces and
  fails, naming the limit, past 2,000,000 nodes, 1,000,000 meshes, 4,000,000 point-instancer
  placements (instances times prototype meshes) or 268,435,456 points and triangle indices; a 1 MB
  stage that asked for 691 MB of instance matrices now stops at the limit.
- **A mesh cannot triangulate past what an int counts** One face of 1.4 billion corners is a small
  usdc and over 4 billion triangle indices, which the copy-out entry points reported narrowed to
  int and then copied in full into a managed array of that narrowed size. Meshes over the
  geometry limit are skipped before any per-corner table is built, and every copy-out capacity is
  compared in `size_t`.
- **OpenEXR and PNG headers no longer decide allocations on their own** EXR scratch buffers were
  sized from the declared tile size and channel count: a 210-byte file with a 1x1 image and
  16384x16384 tiles held 3.2 GB, a 21 KB file with 1000 channels 2.1 GB, once per decoding core.
  Tiles are now clamped to the image, more than 64 channels is refused, and an image or chunk that
  claims more pixels than its bytes can expand to (deflate 1032:1, RLE 64:1) is refused before
  anything is allocated for it -- likewise a PNG whose IDAT cannot inflate to its IHDR size (a
  68-byte file held 270 MB). Native and managed decoders alike. dataWindow extents are computed
  in 64 bit.
- **A failed allocation in native image code no longer ends the process** The image entry points
  and their worker threads had no exception barrier, so `std::bad_alloc`, or a thread that could
  not start, terminated the Editor or player. Each entry point now returns its failure with a
  reason, a tile that fails is left out of its atlas, and a worker that cannot start runs inline.
  A managed OpenEXR decode that throws costs that texture, not the import.
- **Every UDIM tile is confined to the stage folder** Only the tile-1001 name was checked; every
  other tile found in the folder was read as listed, so a `tex.1002.png` symlink to a file outside
  the stage was stitched into the atlas. Each tile is now checked, symlinks included; the native
  reader opens regular files only (a FIFO blocked it); and nothing outside the stage folder is
  probed for a first tile (`\\host\share` paths reached SMB on Windows before being refused).
- **UDIM atlases stay within the texture limits** A thin tile stopped the halving loop on its
  short side, so three 273-byte PNGs built a 70x1,638,400 atlas. Each side is now halved on its
  own and a grid that still does not fit is refused. The native atlas also refuses tile numbers
  outside 1001-1999 or repeated, which wrapped its grid arithmetic into a heap overflow, and the
  managed fallback takes large sets on a quarter of the cores.
- **The dome-light skybox goes through the image limits** It was the one texture still read
  straight into `Texture2D.LoadImage`.
- **`UsdSpatialAudio.FilePath` is confined like a texture** A rooted path, a `../` climb or a URL
  authored in the stage was stored as written for the application to load; it is now left empty
  unless it resolves inside the stage folder.
- **Payload integrity covers MaterialX** The Linux dependency-digest rules now include
  `libMaterialX*.so*`, the SBOM lists the Windows and macOS MaterialX libraries, and the MaterialX
  standard library (`.mtlx`, parsed at run time) is in the runtime hash manifest and pinned against
  line-ending conversion. `build_macos.sh` copies MaterialX under the names `libusd_ms` links
  (`libMaterialXCore.1.dylib`) and fails on an install built without it; the generic copy shipped
  only the versioned file, which dyld could not find.
- **Every binary we build is signed, and pack checks each one** `sign_windows` named only
  `UnityUSDToolkitNative.dll` and `usd_rt.dll`, so the MaterialX DLLs -- built here from the pinned
  archive like OpenUSD -- would have shipped unsigned, and `pack` would not have noticed: it passed
  when any file in the payload had changed. It now signs all four, and `pack` requires each listed
  file to differ from its committed copy and every `.dylib`/`.dll` in the payload to be on the list,
  Intel's TBB excepted. `build_windows.ps1` no longer re-signs Intel's `tbb_usdrt.dll` either.
- **The SBOM and notices cover what ships** The SBOM reads the native API version from the source
  (it said 5), lists MaterialX's source archive with its digest, the pugixml 1.9 it vendors and the
  standard-library data under `usdMtlx/resources`, links libdeflate to the wrappers that compile it
  rather than to OpenUSD, and gives Linux oneTBB as 2020.3.1. `ThirdPartyNotices.md` gains MaterialX,
  pugixml and libdeflate, attributes the usdMtlx data to MaterialX (with its NVIDIA and Sony
  Pictures Imageworks files) instead of Pixar, and no longer says no NVIDIA material ships.
- **A source-scan record has to name the archive it covers** MaterialX pins an archive digest, not a
  commit, so a record reading "Commit scanned: -" passed without saying what had been scanned. A
  record for an archive-pinned component must now carry that archive's SHA-256; the MaterialX rows
  note the commit the v1.39.4 tag resolves to.
- **macOS payload at API 12** Rebuilt against OpenUSD 26.05 with the LZ4 patch and MaterialX
  1.39.4 (universal, macOS 12.0), with every fix above; all eight
  Gate 6 tests pass.
- **Windows and Linux payloads at API 12** Rebuilt from the same source with every fix above: all eight Gate 6 tests pass on each, and the
  Linux dependency digests now record the seven MaterialX libraries the new rule covers.
- **The source-scan gate reads the record, and a release cannot skip it** 
 `verify_upstream_sources.py --require-scan` used to pass on any file with the right
  name, an empty one included. Nothing on the release path ran it either: the wrapper build only
  printed a warning for an install built without it. Now it checks that the record is filled in,
  names the pinned commit and each patch applied to it, counts every severity, keeps no Critical
  or High finding open, and gives a reason for each one it does keep open. `--check-stamp
  --require-scan` fails an install built without the scan. `Native~/CMakeLists.txt` leaves
  `.unscanned-build` in a payload built from such an install. The pack job requires a passing
  record for every platform and runs `generate_native_hashes.py --release`, which refuses a marked
  payload. The three committed payloads carry the marker, since no scan has been recorded for
  them yet. A check of the pins against OSV and NVD on 2026-10-01 found no published
  vulnerability that applies; its write-up was later taken out of the repository with the other
  release-process security notes, so it is not a file here.
- **The import browser sample loads thumbnails within the importer's image limits**
  `RuntimeImportBrowser` handed each companion image to
  `Texture2D.LoadImage` unchecked, so a PNG whose header claimed 30000×30000 made it allocate
  about 3.4 GB. The new `UsdImporter.LoadImageFile` refuses a file over
  `UsdImageLimits.MaxFileBytes` before reading it, and refuses a header over the dimension limits
  before decoding. The sample loads its thumbnails through it.
- **OpenUSD's vendored LZ4 is 1.10.0, and the sizes OpenUSD hands it are checked**
  OpenUSD v26.05 — and upstream `dev` still —
  vendors LZ4 1.9.2, whose decoder calls `memmove()` with a negative size when given a negative
  output capacity; the fix is upstream commit `8301a21`, first released in **1.9.4**, not 1.9.3.
  OpenUSD's `.usdc` reader could supply exactly that: `TfFastCompression::DecompressFromBuffer`
  converted the crate's 64-bit section sizes to `int`, so a tokens section claiming 2 GiB became a
  negative capacity. It also never checked a chunk size against the buffer, and read the chunk
  count as a signed `char`, so a first byte of `0x80` or more made the loop walk off the end.
  `Native~/patches/openusd-26.05-lz4-1.10.0.patch` replaces `pxr/base/tf/pxrLZ4` with upstream
  1.10.0 (Pixar's nine namespace edits re-applied, nothing else changed) and checks the count and
  every size before LZ4 sees it; valid files decode exactly as before. The patch is pinned by
  SHA-256 in every `dependency-sources/<platform>.tsv`, applied by `build_openusd.py` to a
  worktree of the pinned commit (the clone stays unmodified), required by the stamp (now version
  3), and recorded in the SBOM's `pedigree`. `Native~/Tests~/lz4_bounds_test.cpp` drives the
  shipped `libusd_ms` with each hostile input behind a guard page: the previous macOS payload
  fails four of its eight cases, three by faulting. The worktree is checked out and patched with
  `core.autocrlf` off, since Git for Windows' default would check OpenUSD's C++ out with CRLF and
  the patch would not apply. `build_openusd.py` now also refuses a macOS install under the home
  directory, as it already did on Linux: the first patched build went there and baked the user
  name into `libusd_ms.dylib` 1134 times; the shipped one is built outside it. All three
  payloads carry the patch.
- **TBB is checked against its pin before `build_usd.py` compiles it, not after**
  `build_usd.py` passes `expectedSHA256` for Boost and nothing else,
  so the documented flow downloaded, unpacked, compiled and linked TBB and only then compared the
  archive with `dependency-sources/<platform>.tsv` — a manual step that reports what has already
  run. OpenUSD is now built with `Native~/build_openusd.py`: it verifies the clone, downloads the
  pinned archive into the directory `build_usd.py` downloads into and checks it (a mismatch stops
  before anything is built), runs `build_usd.py` with its downloads pointed at an unreachable
  proxy so it cannot fetch anything else, re-checks the archive, the download directory and the
  clone afterwards, and only then stamps the install. It refuses a non-empty install directory,
  because `build_usd.py` reuses an extracted archive or an installed dependency it finds there
  without looking at it, and sets `PYTHONDONTWRITEBYTECODE` so `build_usd.py` does not leave
  `__pycache__` in the clone — which made the documented post-build clone check fail every time.
  The manual `verify_upstream_sources.py --stamp` is gone.
- **The Linux build enforces the source-provenance gate like the other two platforms**
  The shipped Linux payload was built with plain CMake from manual
  commands, and `build_linux.sh` targeted a different (packman + Python) layout, so nothing on
  Linux required the stamp. `build_linux.sh` is now the shipping path — monolithic 26.05 via
  CMake, the committed payload layout, `patchelf` rpaths, `strip`, and checks for unresolved
  `ldd` entries and `/home/` paths — with the same provenance and digest gates as
  `build_macos.sh` and `build_windows.ps1`. `Native~/CMakeLists.txt` also checks the stamp, at
  configure time and on every build, so no way of building the wrapper skips it by accident.
- **A provenance stamp now describes the install it sits in** Stamp
  version 2 records that each pinned archive was checked before the build, and the SHA-256 of
  every file a wrapper build copies out of the install; `--check-stamp` requires both and
  re-hashes the files, so an install modified after its verified build is refused. Version 1
  stamps are refused — rebuild the install with `build_openusd.py`. `build_macos.sh` no longer
  re-signs `libusd_ms.dylib` inside the OpenUSD install (the payload copy is signed afterwards
  anyway), since that modified the tree the stamp describes.
- **Skipping the provenance gate can no longer produce a releasable payload or a trusted
  baseline**. A wrapper built with `--skip-source-provenance` /
  `-SkipSourceProvenance` / `-DUSD_TOOLKIT_SKIP_SOURCE_PROVENANCE=ON` gets a `.unverified-build`
  marker in its payload root, and `generate_native_hashes.py` — and so CI's `integrity_check` and
  `pack` — refuses to write a manifest while one exists. The CMake option is dropped from the
  cache after each configure. `verify_dependency_digests.py --record` refuses a tree without a
  valid stamp, and the build scripts refuse `--record-dependency-digests` together with the skip,
  so a first-use record is always of a tree whose origin was checked. Covered by
  `Native~/Tests~/test_build_provenance.py`, which `integrity_check` runs.
- **An image too large for the managed PNG decoder can no longer reach `Texture2D.LoadImage`
  anyway**. `UsdPngDecoder` refuses a header claiming more than
  16384 px a side or 64 M pixels, but a PNG it declined — for being too large, or 16-bit,
  interlaced or paletted — kept its raw bytes and was handed to `LoadImage`, which has no limits.
  So the caps only guarded the path that obeyed them: a few-dozen-byte PNG whose header claims
  20000×25000 went straight to the uncapped decoder. JPEG never touched the managed decoder at all.
  `UsdImageLimits` now holds the limits once, and sizes every image headed for `LoadImage` from its
  PNG `IHDR` or JPEG start-of-frame header first; anything over the limits, or not a PNG or JPEG
  with a readable size, is refused with a warning and that texture alone is dropped. The check runs
  on the decode pass (off the main thread for `ImportAsync`, freeing the bytes early) and again at
  `CreateTextureViaLoadImage` itself. Texture files larger than 512 MB — the most a 16-bit RGBA
  image at the pixel cap could need — are refused before they are read, and packaged `.usdz`
  textures as soon as the resolver returns them.

  Verified in Unity 6000.4.10f1 against a stage with four textures: a 45-byte PNG claiming
  20000×25000 and a 23-byte JPEG claiming 30000×10 are both refused; a real 333×333 PNG and a real
  777×777 JPEG, which goes through the `LoadImage` fallback, both still import. The same stage on
  the previous code reached `LoadImage` with the hostile bytes.
- **CI now fails if a hashed payload file could have its line endings rewritten**
  `.gitattributes` marking the plugin descriptors `-text` fixed the
  Windows checkout that refused its own payload, but nothing stopped the next descriptor type from
  arriving without a rule — and `integrity_check` runs on Linux, where the conversion never
  happens. `generate_native_hashes.py --check-attributes` asks git, for every file the manifest
  covers, whether its `text` attribute is unset, and `integrity_check` runs it before comparing
  digests. The runbook's manifest gate now also says to generate on macOS or Linux only.
- **Intel's TBB ships as `tbb_usdrt.dll`, so the Windows loader cannot substitute the Editor's own**
  Windows resolves an import by base name against the modules already
  loaded in the process, and the Unity Editor ships a `tbb.dll` of its own. Whichever loaded first
  won, which meant the payload could run against a TBB build it was not compiled against — the
  same failure this package already fixed once by shipping OpenUSD's monolithic library as
  `usd_rt.dll` rather than `usd_ms.dll`. Both copies happen to be TBB 2020.3 (interface 11103)
  today, so it worked by coincidence rather than by contract.

  Only `usd_rt.dll` imports it, and its import name string has no room to grow — seven characters,
  a NUL, then the next entry — so the equal-length byte replacement used for `usd_ms` could not
  produce a name recognisably ours. `Native~/patch_pe_import.py` redirects the import descriptor's
  Name RVA at a longer name written into mapped, zero-filled section padding instead, preferring a
  read-only data section, and re-parses the file before writing to confirm the import table still
  reads correctly. It is idempotent, so a rebuild can re-run it.

  **Intel's file is not modified** — only renamed — so its Authenticode signature and its
  byte-identity with `tbb/bin/intel64/vc14/tbb.dll` in the published `tbb-2020.3-win.zip` both
  still hold: Authenticode covers a PE's contents, not its filename. The committed Windows payload
  is already renamed — applying the patch needs no rebuild, only the script, a rename and the two
  regenerated manifests, which is why it was done on macOS where the manifests must be generated
  anyway. `build_windows.ps1` applies the same step to every future rebuild. The runtime preflight
  check, the signing job and the SBOM generator still accept the old name, so a payload someone
  rebuilt locally before this change keeps working.
- **The source scan the review requires has a place, a procedure and a gate — and no results yet**
  For code we compile ourselves the rule is to scan each version for security
  issues before compiling it. Nothing did: Cycode runs on this repository's pull requests, but
  the OpenUSD and oneTBB source is not committed here, so it has never been looked at.
  `Native~/security-scans/` now states the requirement, which versions need a record, the command
  to produce one, and what a record must contain, with a template. `verify_upstream_sources.py
  --require-scan` checks a record exists for every pinned version and `--stamp` writes whether it
  was used into the OpenUSD install root, so a wrapper build warns when its source was never
  scanned. A record existing is not the same as a scan passing — the gate checks the work was
  done and written down; reading it is the review. **No record exists yet**, and `BUILD_NOTES.md`
  lists that as an open gap rather than implying otherwise.
- **Windows `tbb.dll` keeps Intel's signature, and the payload's trust argument is written down**
  `sign_windows` signed `Windows/*.dll`, which would have re-signed the one
  binary in the payload that is redistributed rather than built: `build_usd.py` pins Intel's
  prebuilt `tbb-2020.3-win.zip` on Windows, the shipped `tbb.dll` is byte-identical to
  `tbb/bin/intel64/vc14/tbb.dll` inside that archive, and it already carries Intel Corporation's
  Authenticode signature. Authenticode keeps one signer unless a signature is explicitly
  appended, so signing it with Unity's certificate would have erased the strongest provenance
  evidence in the payload. The job now names the two DLLs it signs and asserts afterwards that
  `tbb.dll` still resolves to an Intel signer. `BUILD_NOTES.md` gains the reasoning a reviewer
  needs for the whole payload: which rule each file falls under, that no upstream signature
  exists for OpenUSD or oneTBB to preserve (`v26.05` is a lightweight tag and its commit is
  unsigned; oneTBB publishes no checksum), that the integrity evidence standing in its place is
  the content-addressed commit id we now pin and enforce, and that the licenses — Tomorrow Open
  Source Technology License 1.0, which differs from Apache 2.0 only in its trademark section, and
  Apache 2.0 for oneTBB — permit both the build and the signature.
- **The upstream source every payload is built from is now pinned and gated**
  `BUILD_NOTES.md` named the OpenUSD tag and commit, but nothing
  checked them: a build took whatever was in the clone on the build machine, and the chain
  between "public v26.05" and "the bytes in this repository" was a person following a runbook.
  `Native~/dependency-sources/{macos,linux,windows}.tsv` now records, per platform, the OpenUSD
  commit (`2095faf`) and the TBB archive a build is allowed to use, with the upstream integrity
  evidence that exists for each — and, where none exists, saying so. `Native~/verify_upstream_sources.py`
  checks a clone against that record (pinned commit, recorded remote, unmodified worktree, and a
  tag that has not been moved), checks a downloaded archive against its digest, and stamps the
  OpenUSD install root once it passes. `build_macos.sh` and `build_windows.ps1` refuse an install
  root without that stamp unless `--skip-source-provenance` / `-SkipSourceProvenance` is passed,
  which warns that the payload must not be shipped. `build_linux.sh` enforces the same gate.

  Two findings came out of writing this down. `build_usd.py` passes `expectedSHA256` for Boost
  and for no other dependency, so TBB is fetched over HTTPS with no integrity check of its own,
  which is why the archive digest is recorded here instead of relied on there. And under this
  package's build flags TBB is the *only* downloaded dependency: everything else, including the
  four libraries OpenUSD vendors in (`pxr/base/tf/pxrCLI11`, `pxrDoubleConversion`, `pxrLZ4`,
  `pxrTslRobinMap`), is compiled from the OpenUSD tree itself and is therefore covered by the
  commit pin. The whole payload's origin is two records per platform.
- **Symlinks can no longer walk an imported asset path out of the stage folder**
  The confinement added earlier compared paths
  with `Path.GetFullPath`, which normalizes `.` and `..` as *text* and does not follow links. A
  stage folder containing `tex.png -> /etc/passwd` (or an SSH key, or any file the user can read)
  therefore passed the check and was read straight off disk by `File.ReadAllBytes`. The native
  gate would have caught it — it resolves with `TfRealPath` — but it is only consulted when the
  managed file read fails, so the weaker check short-circuited the stronger one. Confirmed by
  experiment before fixing: a symlink placed in a stage folder returned the target file's bytes.
  Any path component *below* the stage folder that is a link (Unix symlink, Windows symlink or
  junction) is now refused. The stage folder itself and its ancestors are deliberately not
  examined, since a project living under a symlinked path is ordinary — on macOS `/var` is itself
  a link — and rejecting that would break normal setups while protecting nothing. Links are
  refused rather than resolved: resolving needs `realpath` on POSIX and `GetFinalPathNameByHandle`
  on Windows, neither reachable from .NET Standard 2.1 without per-platform P/Invoke.
  `UsdImportOptions.AllowExternalAssetPaths` remains the way to import a stage that genuinely
  needs to reach outside its folder.
- **The package ships a software bill of materials, and the third-party notices now cover every
  platform** `ThirdPartyNotices~/sbom.cdx.json`
  (CycloneDX 1.6) lists each native component a consumer loads: OpenUSD `v26.05` with its commit
  (`2095faf`) and build flags, oneTBB 2020.3, the four libraries OpenUSD vendors into the
  monolithic build (CLI11 2.3.1, double-conversion 3.3.0, LZ4 1.10.0 with the bounds patch, tsl robin-map),
  MaterialX 1.39.4, the per-platform toolchain, and the SHA-256 of every shipped binary — the same digests the
  runtime manifest carries. Regenerate it with `python3 Native~/generate_sbom.py` in the same
  commit as any payload rebuild. `ThirdPartyNotices.md` listed only the Windows files and no
  versions, although macOS and Linux redistribute the same libraries; it now covers all three
  platforms, states the OpenUSD and oneTBB versions, and carries the licenses of the vendored
  components. The SBOM is an inventory, not an integrity control: it records what shipped and
  where it came from, which is what the review asked for alongside the public-tag rebuild.
- **The macOS build toolchain is recorded in `BUILD_NOTES.md`**, as Windows and Linux already
  were — macOS version, Xcode and clang builds, CMake, SDK and architectures — so the v26.05
  rebuild can be reproduced by a third party.
- **Imported texture paths are confined to the stage folder** A USD
  stage authors its own `UsdUVTexture` `inputs:file` paths, so an imported file is untrusted
  input — and the importer read whatever those paths named. An absolute path, or one that climbed
  out with `../`, was passed straight to `File.Exists`/`File.ReadAllBytes`, so a crafted `.usd`
  or `.usdz` could make the importer read an arbitrary local file, load its bytes as a texture,
  and echo its absolute path back in a warning. `ResolveTexturePath` now canonicalizes the
  authored path and refuses anything that resolves outside the stage's own directory, *before*
  either read path is taken — the native resolver fallback is a second, independent sink, so a
  guard in front of the file read alone would have been walked around. The byte read re-checks at
  the sink, and a rejected path is reported by its authored spelling only, so a blocked attempt no
  longer discloses where files sit on the machine. Textures packaged inside a `.usdz` are
  unaffected: their authored paths are relative and stay inside the package.
  `UsdImportOptions.AllowExternalAssetPaths` (default `false`) re-enables reads outside the stage
  folder for stages you trust that deliberately reference a shared texture library.
- **The managed PNG decoder no longer trusts IHDR dimensions**
  `UsdPngDecoder.TryDecode` checked only that width and height were positive, then sized
  every allocation from them: a few-hundred-byte PNG claiming 20000x25000 drove a ~2 GB
  `MemoryStream` reservation, and 65536x65536 wrapped the 32-bit `stride` negative — which also
  dragged the `raw.Length` guard negative and let a negative array size through, throwing an
  uncaught `OverflowException` out of the import. Dimensions are now capped (16384 per side,
  64M pixels) before anything is allocated, every derived size is computed in 64-bit against a
  byte budget, the inflate output stream is no longer pre-sized from the header, and the inflate
  is capped at what the dimensions can legitimately produce so a small IDAT cannot expand without
  bound. Oversized or malformed PNGs keep the decoder's existing `false` return and fall back to
  `Texture2D.LoadImage`. The chunk-length guard is also computed in 64-bit: a length near
  `int.MaxValue` used to overflow it and then throw out of `idat.Write`.
- **The native ABI gate requires an exact match with the source, and now guards the import path
  too**. (Since relaxed: API 6 or newer is accepted; see Changed.) `UsdNative.MinimumApiVersion`
  accepted anything
  from API 2 upward, a tolerance that existed so payloads lagging the source kept working while
  Windows and Linux were rebuilt. All three desktop payloads reached API 5, and the tolerance had
  become a hole: fixes that are *not* gated on the ABI version — the topology and asset-path fixes
  above among them — are simply absent from an older binary, with nothing in the version number to
  say so. An older plugin is now refused with a message that says as much. Separately, the gate
  was only ever called from the export path, so an import ran against whatever plugin happened to
  be loaded — backwards, since the import path is where untrusted file content is parsed. Both
  `UsdImporter.Import`/`ImportAsync`/`GetPreviewInfo` now validate it.
- **The native payload is verified against recorded digests before the first P/Invoke**
  `ValidateNativeRuntimeFiles` only asked whether a file of the right
  name existed, which cannot tell a genuine binary from a substituted one — and the native code is
  loaded and executed in-process. `Runtime/Native/NativeRuntimeHashes.g.cs` now records the
  SHA-256 of every shipped `.dll`/`.dylib`/`.so`, and the loader compares each binary it finds
  against that digest once per process, refusing the payload on a mismatch with a message naming
  the file and how to regenerate the manifest. A file the manifest has no entry for is reported as
  unverified rather than failing the load. Regenerate after any native rebuild with
  `python3 Native~/generate_native_hashes.py` — which now refuses to run when any payload file is
  an unfetched Git LFS pointer, since the manifest covers every platform and would otherwise
  record the pointers' digests and break those platforms at load time;
  `UsdExportOptions.VerifyNativeRuntimeIntegrity`
  (default `true`) turns the check off. The digests are compiled into the assembly rather than
  shipped as a data file next to the binaries, since a manifest sitting beside the payload is
  editable by anyone who can edit the payload. This is not a defence against someone who already
  has write access to the package — it catches substitution or corruption in distribution, and it
  makes the payload auditable: anyone can hash the shipped files and compare.
- **Untrusted mesh topology is rejected instead of triangulated**
  `BuildImportedSubmeshes` accumulated each face's start offset in a signed 32-bit int,
  so a stage whose `faceVertexCounts` summed past `INT_MAX` wrapped that accumulator, and the
  corrupted offset then defeated `TriangulateFace`'s own bounds check — which added two
  file-controlled values and so overflowed in turn. `cornerVertices` was then read far outside
  its buffer: an unrecoverable native crash, with out-of-bounds values handed back to managed
  code. The offsets are now accumulated in 64-bit, every offset is kept inside the real corner
  buffer, and a mesh whose face counts do not sum to exactly the `faceVertexIndices` length is
  reported and skipped rather than trusted. `TriangulateFace`'s guard was rewritten to subtract
  from the known-good buffer length instead of adding two untrusted values.
- **Asset reads through the native resolver are confined to the stage's folders**
  `RUsd_ReadImportAsset` resolves through OpenUSD, which
  honours absolute paths and `..` climbs, so it was a second read path independent of the managed
  guard above. The resolved path is now confined to the directory of any layer that composes the
  stage — not just the root layer's, so a sublayer or reference keeping textures next to itself
  still works — and a texture packaged inside a `.usdz` is judged by the package file that
  contains it. Refusals and read errors name the authored path instead of the resolved location,
  so a blocked attempt no longer discloses local filesystem layout.
- **`UsdExportOptions.PluginSearchPath` is confined to the package's own native folders**
  The value was written verbatim into `PXR_PLUGINPATH_NAME`, and
  OpenUSD's plugin registry loads and executes any library a `plugInfo.json` under that path
  names, in-process. A path outside the package's native runtime folders is now rejected before
  the variable is written — the previous order set the variable first and validated afterwards,
  leaving the process already pointed at the untrusted directory. The escape hatch,
  `UsdExporter.AllowExternalPluginSearchPath`, is a static code-only switch rather than a field on
  `UsdExportOptions`, because that type is `[Serializable]` and an options object arriving from a
  scene or prefab would otherwise carry both a hostile path and its own permission to use it.
  `ValidatePluginSearchPath`'s `plugInfo.json` check stays, but only as a layout diagnostic: a
  manifest being present says nothing about a directory being trustworthy.
- **The generated Live Sync token is written owner-readable, and the channel is loopback-only**
  The session token was persisted with `File.WriteAllText`, which
  creates the file under the process umask — mode 0644 on a typical macOS or Linux host, so every
  other account on a shared machine could read the one secret guarding full scene read and write.
  The token is now written to an unguessably named file that is restricted to the current user
  (`chmod` 0600 on POSIX, an inheritance-free single-user ACL on Windows) while it is still empty,
  filled in only then, and renamed into place; writing first and restricting afterwards would have
  published the secret for the window in between, and a reader who opened it in that window would
  keep access through the open handle regardless. If the permissions cannot be applied the token is
  not written and the server does not start, since a readable token file is the vulnerability itself.
  `File.SetUnixFileMode` is .NET 7+ and unavailable here, hence the `chmod` P/Invoke.
  `Tools~/isaacsim/mock_unity_server.py` got the same treatment, since it generates a real token of
  its own: `os.open` with `O_EXCL` and mode 0600 into an unguessably named file, then `os.replace`.
  Besides the permissions, the plain `open(path, "w")` it used before also wrote *through* a symlink
  planted at the destination; the rename replaces the symlink instead of following it.
  Separately, the transport is plain TCP, so the token and the scene stream crossed the wire in clear
  text on any non-loopback bind. The `allowNonLoopbackBind` opt-in added alongside the
  authentication work has been **removed**: the listener now serves loopback only, with no way to
  widen it. Warning about cleartext while still serving it is not a defence, and a sample is the
  wrong place to ship a certificate-provisioning story — front this listener with SSH, a VPN or a
  TLS proxy if you need it across machines. **Breaking:** a scene that set `allowNonLoopbackBind`
  loses the field, and a `bindAddress` outside `127.0.0.1`/`::1`/`localhost` now refuses to start.
- **The Live Sync sample's control channel is authenticated, and stays on this machine**
  `UsdLiveSyncServer` accepted commands from anyone who could
  open its port. The only access control in the channel was the per-object
  `UsdSyncNode.AcceptsRemoteWrites` flag, which does not cover `get_snapshot` — the command that
  returns every tracked prim path and transform — nor `reset`, nor the parser that runs before any
  of them. Every connection now sends `{"cmd":"auth","token":"…"}` first; until it succeeds nothing
  is answered and no broadcast is delivered, an invalid token closes the connection, and a client
  that never authenticates is dropped after `authTimeoutSeconds`. The join snapshot moved from the
  accept loop to the point just after a successful handshake, so connecting no longer discloses the
  scene. The token comes from the `authToken` field, else `USD_LIVE_SYNC_TOKEN`, else a random
  per-session value written owner-readable to `live_sync_token.txt` beside `base_stage.usda`, which
  the bundled clients find on their own; comparison is constant-time. `AcceptsRemoteWrites` is unchanged and
  still scopes which objects accept writes — it is layered on authentication, not a substitute.
- **The Live Sync listener refuses to bind past loopback, and bounds what a client can spend**
  `ResolveBindAddress` passed any parseable address through,
  so `0.0.0.0` silently published the channel to the network. The listener is now loopback-only and
  the server refuses to start on any other address, logging why. The port is taken with
  `ExclusiveAddressUse` instead of `ReuseAddress`, so
  a co-located process can no longer rebind it and intercept clients. Three unbounded resources
  driven by unauthenticated input were capped: the hand-rolled JSON parser rejects nesting past 32
  levels (unbounded recursion on the main thread was an *uncatchable* `StackOverflowException`, so
  the existing `try`/`catch` around `Json.Parse` could not have contained it), the per-client line
  accumulator is capped at `maxCommandBytes` (64 KB) instead of growing until a newline arrives, and
  the pending-command queue and connection count are bounded by `maxClients`.

## [0.7.2] - 2026-09-17

### Added

- **The importer now says what it dropped.** A material input that *is* connected but yields no
  texture path used to fail in complete silence — no OpenUSD error, and the managed
  "texture not found, skipping" warning only fires for a path that exists and points at a missing
  file. An import could therefore come back fully untextured with nothing in the console, which is
  indistinguishable from "the file has no textures". The native importer now reports each such
  input through the diagnostics buffer, naming the material, the input and the reason: the
  connected source is not a `UsdUVTexture`, it has no `inputs:file`, its `inputs:file` could not
  be read as an asset path, or the asset path is empty. A surface shader that is not
  `UsdPreviewSurface` (or missing entirely) is reported too. The managed side logs these to the
  Unity console (first 10, then a count) and they remain in `UsdImportResult.NativeDiagnostics`.
  The "could not be read as an asset path" case is specifically the signature of a plugin binary
  not built from this package's source, where `VtValue` type checks fail across the
  plugin/OpenUSD boundary and *every* texture path comes back empty.
  No ABI change: the messages travel in the existing diagnostics buffer, prefixed `USD import: `.
  Healthy stages stay silent — McUsd (23 materials) and PVM-F023 (11) report nothing.
- **Authored opacity is imported: alpha cutout and transparency.** `UsdPreviewSurface`'s
  `opacity` input is usually *connected* to the albedo texture's alpha channel, with
  `opacityThreshold` deciding how it is used — the importer read neither, so it only ever saw the
  connection's constant fallback (1 = opaque) and left every material on Unity's default Opaque
  surface, which ignores alpha outright. Cut-out foliage therefore imported as solid quads and
  stained glass as a solid block. The importer now reads the connection and the threshold:
  `opacityThreshold > 0` configures alpha clipping (`_AlphaClip`, `_ALPHATEST_ON`, `_Cutoff` =
  threshold, AlphaTest queue), `opacityThreshold == 0` configures alpha blending (transparent
  surface, `SrcAlpha`/`OneMinusSrcAlpha`, no Z-write, Transparent queue), and a constant opacity
  below 1 with no texture takes the same blended path. Built-in `Standard`'s `_Mode` is set
  alongside URP's properties. Opacity authored in a file *other* than the albedo is ignored with
  a warning, since URP and Standard both read alpha from the base map.
  New native entry point `RUsd_GetImportMaterialOpacity`; native ABI is now **4**, still additive
  and still gated by `UsdNative.MinimumApiVersion` (2), so ABI 2/3 plugins keep importing exactly
  as before, without transparency.
- **Additional UV sets.** `st1`/`st2` (and aliases) import as Unity `uv1`/`uv2`, up to
  `RUSD_MAX_UV_SETS` (3) sets. New native entry points `RUsd_GetImportMeshUvSetInfo` and
  `RUsd_CopyImportMeshUvSet`; set 0 still travels with `RUsd_CopyImportMesh`.
- **UV primvar name resolution.** Each set is matched against an alias list
  (`st`/`st0`/`uv`/`UVMap`/`map1`/... for set 0). If **set 0** matches nothing, the importer then
  scans for any 2-component primvar in name order — so an unusual name no longer means a mesh
  with no UVs at all. Secondary sets are matched by name only: scanning every primvar of every
  mesh to look for a second set costs measurable time on large scenes and finds nothing in the
  overwhelmingly common single-set case.
- `double2`/`half2` (and `texCoord2d`/`texCoord2h`) UVs and double-precision normals are
  converted instead of rejected.
- `Native~/Tests~/import_uv_test.cpp` + `import_uv_fixture.usda`: eight meshes covering
  faceVarying/indexed/aliased/multi-set/double-precision/absent UVs, asserting both counts and
  values. Build/run instructions are in the test's header comment.

- **Export to `.usdz`.** Passing an output path ending in `.usdz` to `UsdExporter` now writes a
  usdz package. usdz is a read-only zip format, so it can never be written as a stage: the
  exporter writes an ordinary `.usdc` (plus, with `ExportTextures`, its texture folder) into a
  temporary staging directory, hands that to OpenUSD's packaging API, and removes the staging copy
  — so a usdz carries its textures *inside* the package instead of alongside it.
  `UsdExportOptions.UsdzArkitCompatible` packages under ARKit's (AR Quick Look's) stricter
  constraints, which may drop features such as variant sets. New native entry point
  `RUsd_CreateUsdzPackage` (ABI **5**).
- **Textures inside a `.usdz` now import.** Geometry and materials already read from a usdz, but
  textures did not: they are files *inside* the package, and the importer resolved every texture
  path against the file system, which cannot reach them — every texture was reported missing and
  the material kept its flat fallback colour (which in turn meant no alpha cutout or
  transparency, since those ride the albedo's alpha). Texture bytes that are not files on disk
  now come from the stage's own asset resolver, which reads straight out of the package. New
  native entry point `RUsd_ReadImportAsset` (ABI **5**). A texture that is genuinely missing is
  still reported and skipped as before.
- `UsdLibraryScanner` lists `.usdz` files, so a usdz shows up in the sample import browser, and
  the export sample's format dropdown offers `.usdz`.

### Fixed

- **Imported meshes lost every UV.** The importer only accepted a primvar literally named `st`
  whose value array happened to be exactly one entry per point. That excludes almost everything
  DCC tools write: `faceVarying` UVs (one value per face-corner), *indexed* UVs
  (`primvars:st:indices`, where the value array holds only the distinct coordinates) and any
  other primvar name (`uv`, `UVMap`, `map1`, ...). All of those were dropped silently, so meshes
  arrived untextured. Worse, a mesh whose point count *coincidentally* matched the size of an
  indexed value array got the raw values applied per point — UVs that were wrong rather than
  missing. The importer now resolves UVs per face-corner and **splits vertices** where corners
  disagree, so faceVarying/indexed data imports correctly; points whose corners agree still share
  one vertex. Meshes that already worked (per-point, unindexed) keep their exact previous vertex
  and index buffers.
  This was never platform-specific: the same shared C++ source failed identically on macOS and
  Windows.
- **The `UsdPreviewSurface` fallback colour was applied on top of the albedo texture.** An
  authored input value is the fallback for when that input is *not* connected, so a material
  carrying both `diffuseColor = (...)` and `diffuseColor.connect = <texture>` must render the
  texture alone. `CreateMaterial` pushed the value into `_BaseColor` regardless, and URP/Standard
  multiply it into `_BaseMap` — so every textured import came in darkened and tinted (a USD whose
  materials all carry the same fallback tinted the whole scene that colour). `_BaseColor` is now
  neutral white when an albedo texture is bound, keeping the authored alpha for opacity, and the
  same rule applies to `_EmissionColor` when an emission map is bound. A texture that is missing
  or fails to decode still leaves the fallback in place, so such materials keep degrading to a
  flat colour rather than flat white.
- **Normals authored as `primvars:normals` were ignored**, because only the `normals` schema
  attribute was read — those meshes fell back to `RecalculateNormals()` and lost their hard
  edges. Both are now read, with the same per-corner resolution as UVs (so `faceVarying` normals
  work too).

- **README described importing the samples from the Package Manager Samples tab**, which has not
  been true since 0.6.3 moved them into an always-compiled `Samples/` folder. Both sample scenes
  ship compiled with the package, which is the layout this release keeps, and the README now says
  so. (USDZ was also still listed as a later milestone in the import section.)

### Changed

- **Native ABI is now 5** (`RUsd_GetApiVersion()`), up from 2 at 0.6.4. The managed check is a
  *minimum* rather than an exact match: `UsdNative.MinimumApiVersion` (2) gates loading, while
  `UsdNative.MaxUvSets` and `UsdNative.LoadedApiVersion` gate the individual features. A plugin
  still reporting ABI 2-4 therefore keeps loading and working rather than failing outright; it
  simply cannot deliver UV sets 1-2, authored opacity, usdz packaging, or textures read out of a
  package, and export to `.usdz` fails with a message naming the rebuild guide. That is exactly
  what the Windows (ABI 4) and Linux (ABI 2) payloads do until they are rebuilt —
  `Native~/REBUILD_WINDOWS_LINUX.md` is the guide. `UsdNative.ExpectedApiVersion` is gone; use
  `MinimumApiVersion`.
- `UsdImportPreviewInfo.VertexCount` (and a mesh's Unity vertex count) can now be **higher than
  the USD point count** for meshes with faceVarying data, because those vertices are split. This
  is the real Unity vertex count; the USD file is unchanged.
- Export is unaffected and still writes a single per-point `st` set.

### Performance

- Per-point (unindexed `vertex`/`varying`) UVs and normals are bulk-copied rather than resolved
  element by element, the corner→vertex table is read straight out of the mesh's own
  `faceVertexIndices` on the unsplit path (no per-corner buffers are allocated), the corner→face
  table is built only when vertices actually split, and the primvar alias tokens are interned
  once instead of per mesh. Measured on a 564-mesh / 1.19M-vertex / 917k-triangle scene, the
  native stage read is **363 ms before the fix vs 368 ms after** — the added coverage costs
  nothing on meshes that already worked. (Without these, the same scene read in 558 ms.)

### Docs

- **`Native~/REBUILD_WINDOWS_LINUX.md`** — one step-by-step guide for rebuilding the Windows x64
  and Linux x64 native payloads against this source (ABI 4), with pass/fail gates: source sanity,
  OpenUSD version, exported ABI-4 entry points, the DSO-safe string literals that prove the binary
  was built from this source, the standalone `import_uv_test` run, in-Editor verification numbers
  for the public `McUsd.usda`, and a pre-commit review that catches wiped `.meta` files and
  non-LFS binaries. `Native~/WINDOWS_REBUILD.md` now points at it.

## [0.7.1] - 2026-09-17

### Added
- License and trademark notices for the `Live Sync Example` sample's NVIDIA Isaac Sim integration,
  to satisfy the package legal-compliance and trademark-attribution standards.
  - `ThirdPartyNotices.md` — an `NVIDIA Isaac Sim` section recording the integration and stating
    explicitly that **no NVIDIA software is redistributed** (the `isaacsim` / `omni.*` / `carb` APIs
    are resolved at run time from the user's own install), that Isaac Sim is licensed to the user
    solely by NVIDIA, and a `Trademarks` section covering every third-party mark the package names.
  - `Samples/Live Sync Example/Tools~/isaacsim/exts/unity.usd.livesync/LICENSE.md` — the extension
    carries its own copy of the notice, because that folder can be junctioned into an Isaac Sim
    install and so travels separately from the package.
  - Trademark attribution, the Third Party Product disclaimer, and the "not included, not
    redistributed" statement in the root `README.md`, the sample `README.md`, the Isaac Sim client
    `README.md`, and the extension's `docs/Overview.md` (which Kit renders in its own UI).
  - `extension.toml` — `authors` corrected from the `USDLiveSyncDemo` prototype name to
    `Unity Technologies`, `license = "SEE LICENSE.md"`, and `LICENSE.md` added to the documentation
    pages so the notice is reachable from the Extensions window.
  - First prose mentions of NVIDIA Isaac Sim now use the marked form `NVIDIA® Isaac Sim™`.

### Fixed
- **Development-machine paths removed from the `Live Sync Example` sample.** The Isaac Sim client
  documented and defaulted to one contributor's local install and prototype project layout, so the
  copy-pasteable commands did not work anywhere else.
  - `run_isaac_live_sync.bat` / `run_isaac_sim_with_livesync.bat` — the hardcoded
    `ISAACSIM_PATH=C:\0_Projects\Nvidia\isaacsim` default now probes the conventional install
    locations (`C:\isaacsim`, `%USERPROFILE%\isaacsim`) and, finding neither, prints them with the
    `set ISAACSIM_PATH=…` command instead of failing against a path that never existed on the user's
    machine. Everything else in these launchers was already resolved relative to `%~dp0`.
  - `Tools~/isaacsim/README.md` — the `mklink /J` example's two absolute paths
    (`C:\0_Projects\Nvidia\isaacsim`, `C:\2_Plastics\USD_demo\USDLiveSyncDemo\…`) became
    `%ISAACSIM_PATH%` and `%CD%`; the "verified against" line no longer names a local install path.
  - `Tools~/isaacsim/README.md`, `docs/Overview.md` — 13 paths written against the prototype
    project's `Tools/` folder corrected to the package's `Tools~/`.
  - `mock_unity_server.py` — the protocol reference pointed at the prototype's
    `Assets/Scripts/USDLiveSync/UsdLiveSyncServer.cs`; it now names the sample's
    `UsdLiveSyncServer.cs`.
  - `docs/Overview.md` — the cross-reference to the non-existent `UsdLiveSync-README.md` now points
    at the sample's own `README.md`.


## [0.7.0] - 2026-09-12

### Added
- **Live Sync Example sample** (`Samples/Live Sync Example`) — a bidirectional, near-real-time
  transform channel between a running Unity scene and an external tool (Python, a DCC, or NVIDIA
  Isaac Sim), with USD as the on-disk record. Unity is the host: it exports the scene's geometry
  **once** as `base_stage.usda` through `UsdExporter` (`PreserveHierarchy`), then streams only
  transform changes over loopback TCP as newline-delimited JSON, so the heavy geometry is written
  once and the live channel stays small. Clients observe the stream, push edits back with
  `set_transform`, and can checkpoint the live state to `live_overrides.usda`, a USD override layer
  that sublayers the baseline.
  - `UsdLiveSyncServer` — prim-path table, baseline export, TCP listener, throttled dirty-diff
    broadcast, and the inbound `set_transform` / `reset` / `get_snapshot` commands. Prim paths are
    assigned with the same name sanitizer and hierarchy walk the exporter uses, and the agreement is
    cross-checked against the export result at start rather than failing silently later when a client
    authors an `over` that binds to nothing.
  - `UsdSyncNode` — ownership marker. Everything tracked is streamed *out*; an inbound write is
    applied *only* where `AcceptsRemoteWrites` is true, so an external edit cannot fight Unity over a
    physics body or an animated object. `ExplicitNodesOnly` track mode narrows the table to tagged
    transforms, e.g. a character's root without its skeleton.
  - `UsdLiveSyncSample` — builds its hierarchy from primitives (no scene assets, nothing to import),
    starts the server, and draws a runtime HUD docked into two collapsible side columns so the synced
    geometry stays visible.
  - `Tools~/usd_live_sync.py` — reference client (stream, push, reset, checkpoint). Standard library
    only, except `--checkpoint`, which needs `usd-core`.
  - `Tools~/isaacsim/` — Isaac Sim Kit extension, a standalone runner, and a mock Unity server for
    developing a client with no Editor running.
  - The wire carries raw Unity local-space values; the Unity-to-USD X-axis flip is applied by the
    client when it authors USD. The conversion is documented in the README and in the sample's own
    `README.md`.
  - The baseline export is best-effort: if the native plugin is unavailable the live transform
    channel still runs and only `base_stage.usda` is skipped.
- `SampleTheme.TranslucentCard(alpha)` — the card style with a translucent background, for a HUD
  drawn over a 3D scene.

### Notes
- The sample's TCP server is a development tool: loopback by default, no authentication, and it
  applies transform writes from any connected client. Do not expose the port beyond `127.0.0.1`.
- Nothing under `Runtime/`, `Editor/`, or `Native~/` changed in this release; the native ABI is
  unchanged.

## [0.6.5] - 2026-09-11

### Fixed
- **Editor/process abort when OpenUSD was reached without the runtime being configured.**
  `UsdNative.OpenStage` and `UsdNative.BeginExport` now call `UsdExporter.EnsureRuntimeConfigured()`
  before touching OpenUSD. Previously, reaching these internal entry points directly — bypassing
  the public `UsdImporter` / `UsdExporter` methods that call `ConfigureNativeRuntime` — left
  `PXR_PLUGINPATH_NAME` unset, so OpenUSD could not discover its `plugInfo.json` files and killed
  the host process from `PlugFindPluginResource` through `TfFatalError`. That is a native abort:
  no managed `try`/`catch` can intercept it and no error code is ever returned. The guard is
  thread-safe, runs once per process, and is a no-op when configuration has already happened, so
  an explicit `UsdExportOptions.PluginSearchPath` is never overwritten by defaults.

  Note this was never a threading problem. Running the USD parse on a worker thread is supported
  and is what `UsdImporter.ImportAsync` has always done — only the initialization order mattered.

### Added
- **`UsdImportPreviewInfo.TriangleCount` and `UsdImportPreviewInfo.VertexCount`.** Stage-wide
  totals for sizing an import before committing to it — the "warn on heavy file" gate no longer
  needs private API. They are summed from the mesh table the stage read already built, so
  `GetPreviewInfo` does not parse the file twice.
- **README: "Threading And Initialization"**, documenting which calls may run off the main thread
  and the one ordering rule that matters.

### Changed
- `UsdImportPreviewInfo`'s internal constructor takes two more arguments. The type is constructed
  only by the toolkit, so this is source-compatible for callers that read its properties.

## [0.6.4] - 2026-06-26

### Changed
- **Sample UI redesign (Export / Import examples).** A shared `SampleTheme` now draws a flat dark
  theme with green accents — rounded cards, fields, and buttons (code-generated 9-slice textures,
  no bundled image assets), a green primary button, a segmented Transform Policy control, custom
  green checkboxes, a format dropdown, and code-drawn folder / chevron icons. Layout is responsive
  (left / right panels split 50/50 at any resolution) and every control is width-bounded so nothing
  overflows; long folder paths show their tail. Option tooltips are retained.

## [0.6.3] - 2026-06-25

### Changed
- **Samples now ship as always-loaded package content instead of an importable `Samples~` folder.**
  `Export Example` and `Import Example` live under `Samples/` with an assembly definition
  (`Unity.USDToolkit.Samples.asmdef` referencing `Unity.USDToolkit`), so they compile and are
  available as soon as the package is installed — no Package Manager "Import" step and no internet
  required (works in offline environments). The `samples` registration was removed from `package.json`.

## [0.6.2] - 2026-06-25

### Fixed
- **Native plugin failed to load when another package bundles OpenUSD (e.g. `com.unity.pixyz.sdk-plus`).**
  Both packages shipped the OpenUSD monolithic runtime under the same base name `usd_ms.dll`. Because
  Windows keeps one module per base name and the other package loads first, `UnityUSDToolkitNative.dll`
  resolved its `usd_ms.dll` import against the *other* (incompatible) OpenUSD build, so the very first
  P/Invoke (`RUsd_GetApiVersion`) threw `DllNotFoundException` (`ERROR_PROC_NOT_FOUND`, 127) and export
  aborted with "could not load the native plugin or one of its OpenUSD dependencies".

### Changed
- **OpenUSD monolithic runtime is now shipped under a unique name** to avoid the base-name collision:
  `usd_ms.dll` → `usd_rt.dll` (Windows). `Native~/build_windows.ps1` renames the payload DLL and patches
  the `UnityUSDToolkitNative` import in-place as a post-build step; `UsdExporter` runtime-file validation
  accepts the `usd_rt` names (old `usd_ms` / `libusd_ms` kept as a fallback for the other platforms).
- **Samples renamed for consistency:** `Runtime Export Example` → `Export Example`, `Runtime Import
  Browser` → `Import Example` (display names, folders, and demo prefab file names).
- **Sample UI overhaul (both samples):**
  - Resolution-independent layout — the UI scales to fit (`GUI.matrix`) so it no longer clips on small or
    high-DPI screens; the Export window splits its control / Export Target panels 50/50.
  - Output **file name** and **format** are separate now: a base name field plus a `.usd` / `.usda` /
    `.usdc` dropdown (the extension is appended at export time).
  - A **folder icon button opens a native folder/file dialog** — `EditorUtility.OpenFolderPanel` /
    `OpenFilePanel` in the Editor, and an OS-native dialog at runtime (Windows PowerShell, macOS
    `osascript`, Linux `zenity`). The folder icon is generated in code (no bundled asset).
  - Each option control shows a **tooltip describing its actual effect** on hover.

### Added
- Demo prefabs (`Export Example.prefab`, `Import Example.prefab`) now ship inside each sample, so an
  imported sample is complete without recreating them.


## [0.6.0] - 2026-06-24

### Fixed
- **Large-import editor freeze (texture decoding).** Imported materials decoded their textures
  independently, so a scene whose many materials share a small set of texture files decoded
  the same PNGs over and over on the main thread. Texture **bytes are now read once per unique
  file** and **decoded once per file** and reused across materials. On a sample industrial scene
  (96 meshes, 251 materials sharing ~25 base textures, 2K PNGs) this cut a synchronous import
  from **~19.7 s to ~1.8 s** (~11×). Mesh/transform construction was already negligible (tens of
  ms); texture decoding was ~99% of the cost.

### Added
- **Off-main-thread texture decoding.** A small managed PNG decoder (`UsdPngDecoder`) decodes
  textures to raw pixels during the read pass, which runs on a worker thread for `ImportAsync`.
  The main thread then only uploads the pixels (`Texture2D.SetPixelData` + `Apply`), which is
  far cheaper than decoding. PNGs the managed decoder does not handle (16-bit, interlaced,
  paletted) fall back to a main-thread `LoadImage`. This removed the per-large-texture
  main-thread hitches (the longest main-thread stall on the sample scene dropped from ~305 ms to
  ~180 ms — what remains is GPU upload + mipmap generation, which must run on the main thread).
- **Frame-sliced async import.** `UsdImporter.ImportAsync` now spreads the main-thread build
  pass (materials, hierarchy, meshes) across frames instead of doing it in one blocking chunk,
  so the editor/player stays responsive during large imports. Controlled by
  `UsdImportOptions.MaxMillisecondsPerFrame` (default `10`). The synchronous `UsdImporter.Import`
  is unchanged (still blocks) but benefits from the texture cache and managed decoder.
- **`UsdImportOptions.ProgressCallback`** (`Action<float, string>`) — reports import progress
  (`0..1`, phase label) on the main thread during `ImportAsync`.

## [0.5.0] - 2026-06-24

### Added
- **Imported Xform hierarchy with per-node local transforms.** The importer no longer bakes
  USD world transforms into mesh vertices. Every transformable prim (`Xform`, `Mesh`, …) is
  reconstructed as a Unity `Transform` with its local position/rotation/scale, so imported
  objects keep the original hierarchy and stay individually movable. Mesh points are kept in
  mesh-local space. Exposed natively through a node table (`RUsd_GetImportNodeCount` /
  `RUsd_GetImportNodeInfo`); the local matrix is converted USD→Unity (X-axis basis flip).
- **Multi-material import via `UsdGeomSubset`.** `materialBind`-family subsets are read back
  into Unity submeshes: the mesh gets one submesh per bound material and the renderer is
  assigned a matching `sharedMaterials` array (faces with no subset use the mesh's direct
  binding). Exposed natively through `RUsd_GetImportSubmeshCount` / `RUsd_CopyImportSubmeshes`.
- **Truly asynchronous `UsdImporter.ImportAsync`.** The USD parse, buffer copies, and texture
  file reads now run on a background thread (producing only plain CPU data); Unity object
  creation (meshes, GameObjects, textures, materials) happens on the main thread in the
  awaited continuation. Previously `ImportAsync` ran the whole import synchronously. Call it
  from the main thread.

### Changed
- **Native ABI version is now `2`** (`RUsd_GetApiVersion`). The C# `ExpectedApiVersion`
  matches. The native plugin must be rebuilt per platform; players built against API 1 are
  not compatible.
- `UsdImporter.Import` is internally split into a thread-safe stage-read pass and a
  main-thread build pass (shared by the sync and async entry points).

## [0.4.0] - 2026-06-24

### Added
- **PBR texture import.** `UsdImporter` now reconstructs Unity materials from a USD
  `UsdPreviewSurface` network's connected `UsdUVTexture` nodes instead of only reading the
  scalar/constant values. The native importer follows each surface input's connection to its
  texture shader and reads the `file` asset path (mirroring the export layout):
  - `diffuseColor` → **albedo** → `_BaseMap` / `_MainTex` (sRGB)
  - `normal` → **normal map** → `_BumpMap` / `_NormalMap`, with the `_NORMALMAP` keyword enabled
  - `metallic` / `roughness` (the same packed map on export) → `_MetallicGlossMap` /
    `_MetallicMap`, with `_METALLICSPECGLOSSMAP` enabled and smoothness sourced from the
    metallic alpha
  - `emissiveColor` → **emission** → `_EmissionMap` and/or `_EmissionColor`, with `_EMISSION`
    enabled (constant `emissiveColor` is read when there is no texture)
  - `UsdTransform2d` on the texture `st` input → material texture **scale/offset** (UV
    tiling)

  Textures are loaded at runtime via `Texture2D.LoadImage`, resolving the USD-relative asset
  path against the imported file's directory; missing/undecodable textures log a warning and
  are skipped. This completes the export→import round trip for textured materials. No native
  ABI change (API version stays `1`); the native plugin must still be rebuilt per platform.
- **`UsdImportOptions.ImportTextures`** (default `true`) to toggle texture loading and binding
  independently of `ImportMaterials`.

### Fixed
- **Material value/texture import silently failing across the plugin boundary.** Reading
  OpenUSD value types out of a `VtValue` with `IsHolding<T>()` relied on `typeid`, which does
  not match across the plugin ↔ monolithic-OpenUSD DSO boundary when the plugin is built with
  hidden symbol visibility. As a result `SdfAssetPath` (and constant `GfVec3f` colors) were
  not recognized, so connected textures — and even some constant material values — were
  dropped on import. The importer now compares the registered type name instead, so authored
  material data is read correctly.

## [0.3.0] - 2026-06-05

### Fixed
- **Metallic-map misassignment → "black panels" in environment-less viewers.**
  When a Unity material's metallic map (`_MetallicGlossMap` / `_MetallicMap`)
  references the *same* texture as its albedo map (`_BaseMap` / `_MainTex`) — a
  common authoring mistake where a base-color texture is dropped into the metallic
  slot — the metallic map is now ignored and the scalar `_Metallic` is exported
  instead. Previously such a surface was exported as a near-mirror metal
  (`metallic` ≈ the texture's red channel, `roughness` ≈ `1 - alpha` ≈ 0), which
  **renders solid black in viewers that don't supply an environment/IBL reflection**
  (e.g. Isaac Sim's real-time viewport) even though it looks like a normal gray
  surface in Unity (which always has a skybox / reflection probes to reflect).
  Controlled by the new `UsdExportOptions.IgnoreAlbedoInMetallicSlot` (default
  `true`, with a matching toggle in the Runtime Export Example sample); a warning
  is logged when it triggers, and it can be disabled if a texture is intentionally
  shared between the base-color and metallic slots. The fix is C#-only
  (`UsdExporter.CreateMaterial`); no native rebuild is required.

### Added
- **Linux (x86_64) runtime support.** Added a native plugin (`libUnityUSDToolkitNative.so`)
  built against OpenUSD and shipped self-contained under
  `Runtime/Plugins/x86_64/Linux/` (the resolved `.so` dependency closure plus USD schema
  plugins, resolved via `$ORIGIN/lib`). Linux Editor/Standalone branches were added to
  `UsdNative` (platform guard), `UsdExporter` (native/plugin search paths,
  `LD_LIBRARY_PATH`, file-name matching) and `RuntimeUsdBuildPostprocessor`
  (`StandaloneLinux64` payload copy).
- **GPU readback export for non-readable meshes.** Meshes with *Read/Write* disabled —
  including in Play mode, where CPU `Mesh.vertices` access is blocked — are now exported by
  reading their vertex/index `GraphicsBuffer`s through `AsyncGPUReadback` into a temporary
  readable copy. Source assets no longer need *Read/Write Enabled* to be exported.
- **Runtime Export Example: full active-scene export.** The sample now exports every root
  object of the currently open scene rather than its built-in demo geometry.
- **PBR texture export.** Material textures are now exported as PNGs into a
  `<usd-name>_textures/` folder next to the USD and referenced by relative path
  (`UsdUVTexture`), matching the standard USD layout:
  - **albedo** → `diffuseColor`
  - **normal** → `inputs:normal` (Unity DXT5nm/BC5 encoding is decoded back to standard
    tangent-space RGB; `[0,1]`→`[-1,1]` via scale/bias)
  - **metallic+smoothness** (`_MetallicGlossMap`) → `metallic` (`.r`) and
    `roughness` (`1 - .a`)
  - **emission** (`_EMISSION` materials) → `emissiveColor` (texture or constant) with
    `opacity` from base-color alpha
  - Non-readable textures are read via GPU `Blit`/`ReadPixels`, so *Read/Write* is not
    required.
- **UV tiling/offset export.** Material `mainTextureScale`/`Offset` is exported as a
  `UsdTransform2d` node between the `st` primvar reader and the textures (only emitted when
  not 1:1).
- **Multi-material meshes.** Each submesh is exported as a `GeomSubset` with its own
  material binding (one material per submesh).
- **`UsdExportOptions.ExportTextures`** (and sample buttons *Export USD (Mesh Only)* /
  *Export USD (with Textures)*) to choose between a lightweight mesh-only export and a full
  textured export.

### Changed
- **Runtime Export Example UI is now resolution-responsive.** The panel fills the screen
  (minus a margin) instead of a fixed 980×760 box, the control panel scrolls vertically when
  its content overflows, and the control/info panel widths are ratio-based.
- **Default export target** changed from auto-generated demo geometry to the active scene's
  roots. Demo geometry is no longer auto-created on `Start` (use the
  *Recreate Demo Geometry* button to create it explicitly).
- **`UsdExportOptions.RequireReadableMeshes` default changed from `true` to `false`.**
  The exporter now reads non-readable meshes directly (editor) or via GPU readback (runtime)
  instead of throwing.

### Notes
- GPU readback requires a graphics device (any integrated or discrete GPU). For
  headless / `-nographics` environments, enable *Read/Write* on the source meshes to use the
  CPU path instead.
- Static Batching replaces a renderer's mesh with a non-readable "Combined Mesh" asset at
  scene load. Disable Static Batching (optionally use the GPU Resident Drawer on
  Vulkan / DirectX 12 / Metal) so individual meshes can be exported.

## [0.2.0]

### Changed
- Internal version bump (no public API changes).

## [0.1.0]

### Added
- Initial release: runtime USD export/import for Windows x64 and macOS, using a custom
  native OpenUSD wrapper.
