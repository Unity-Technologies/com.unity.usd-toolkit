# Reference Look

A URP render setup that brings an imported stage close to how NVIDIA Isaac Sim 6.2 draws it with
the RTX Real-Time 2.0 renderer. The importer and exporter are the same as everywhere else; what
this sample adds is the rendering side: a render pipeline asset, a renderer with the needed
features, a volume profile and Isaac Sim's tone curve as a colour lookup. It was fitted on
NVIDIA's Factory stage and the toolkit's validation stages, so it is a general starting point,
not a per-scene match.

## Contents

| File | What it is |
|---|---|
| `ReferenceLook.unity` | A scene with a camera (HDR, post-processing, TAA: the closest URP comes to the samples Isaac Sim accumulates per pixel) and a global Volume. No lights and no environment: no skybox, black flat ambient, no reflection. The stage brings its own lights, and a stage with a DomeLight sets the sky, ambient and reflection on import; one without stays black around, as in Isaac Sim. |
| `ReferenceLook_Pipeline.asset` | URP asset: HDR, full render scale with no upscaling (0.67 with FSR left the stairs Isaac Sim's accumulated samples do not have, and STP left dark speckles along silhouettes), GPU Resident Drawer with GPU occlusion culling, 4096 shadow maps for the main and the additional lights, soft shadows (high), shadow distance 150 m in 4 cascades, 2048 cookie atlas, HDR colour grading with a 64-step LUT. |
| `ReferenceLook_Renderer.asset` | Deferred+ renderer (the deferred path the GPU Resident Drawer runs on) with Screen Space Ambient Occlusion, Screen Space Reflections and Surface Cache GI, intermediate texture always on. |
| `ReferenceLook_Volume.asset` | Volume profile: tonemapping off (the LUT holds the curve), post exposure -4.2, Isaac LUT at full contribution, SSR (roughness filter 2, full resolution, roughness scale -6, reflect sky), Surface Cache GI (multi-bounce, volume size 128 m before the stage fit, resolution 64, 2 samples, screen lookup and upsampling 8 samples each, temporal smoothing 0.8), SSAO (GTAO, intensity 1, radius 0.6, falloff 100 m). |
| `ReferenceLook_LUT.exr` | A per-channel Narkowicz ACES tone curve, fitted to match the HDR-to-display mapping Isaac Sim 6.2 applies, as a 64 x 64 x 64 lookup over a scene range of 16 (2^4). The curve is the public ACES fit; only its parameters were fitted. |
| `IsaacToneCurve.txt` | The fitted curve the LUT is built from (`USD Toolkit > Reference Look > Rebuild LUT from tone curve`). |
| `Editor/ReferenceLookImport.cs` | The menu items below. |

## Use

1. Import the sample from the Package Manager (**Unity USD Toolkit > Samples > Reference Look**).
2. Choose **USD Toolkit > Import into Reference Look scene...**, pick a `.usd`, `.usda`, `.usdc` or
   `.usdz` file, say whether the stage may reference files outside its own folder
   (`UsdImportOptions.AllowExternalAssetPaths`) and choose where to save the result.
3. The menu assigns `ReferenceLook_Pipeline` to the Graphics settings and every Quality level if the
   project uses another pipeline (it asks first), opens `ReferenceLook.unity`, imports the stage with
   the importer's default options, waits for the importer's reflection captures and saves the
   scene under the name you chose. The sample scene itself is not modified.
4. Position the camera and look at the Game view.

Other menu items:

- **USD Toolkit > Reference Look > Assign render pipeline** sets the pipeline asset without importing.
  It also sets Project Settings > Graphics > BatchRendererGroup Variants to Keep All, without
  which URP leaves the GPU Resident Drawer off. Measured on the Factory in the Editor (1920 x
  1080, RTX A4000): 75.7 ms to 54.2 ms a frame on the car-line view, 44.3 ms to 38.9 ms on the
  mezzanine; the remaining cost is the stage's 27 shadowed point lights (162 shadow maps).
- **USD Toolkit > Reference Look > Refresh reflection** re-captures the interior reflection of the
  stage in the open scene (three passes, one per frame). The capture is a render texture and is
  not saved with the scene, so run this after reopening a saved scene or after changing lights.
- **USD Toolkit > Reference Look > Rebuild LUT from tone curve** regenerates `ReferenceLook_LUT.exr`
  from `IsaacToneCurve.txt`.

From code (an editor script or a test), `ReferenceLookImport.Import(usdPath, allowExternalAssetPaths,
savePath, interactive: false)` runs the same steps without dialogs and
`ReferenceLookImport.LastSavedScene` holds the saved path once the capture frames have passed.

## Adjust per scene

The sample is the Factory match made general, so what was first tuned on the Factory is now
the default for every stage:

- **Ambient intensity** (1.0 since dome reflections are captured; 1.5 before) and **rect light intensity** x1.8 are the importer's own values
  (`UsdLightUnits.DomeAmbient`, `UsdLightUnits.RectPerSquareMetre`); nothing to set after import.
- **Shadow distance and Surface Cache GI volume size** are fitted to the stage on import
  (`ReferenceLookImport.FitToStage`): 2.1 x and the next power of two above 1.2 x the stage's
  bounding diagonal, at least 10 m and 32 m. The GI size is kept on the scene's own
  `ReferenceLook Stage Fit` volume (profile saved beside the scene as `<scene>_StageFit.asset`); the
  shadow distance lives on the shared pipeline asset and is applied again when a fitted scene is
  opened, so in a build it is the value of the scene opened last before building.

Still worth a look per stage: post exposure if the stage's lights are much brighter or darker
than a daylit factory, and the camera's field of view, which the sample leaves at 60 degrees.

## Requirements and limitations

- **Experimental sample.** It needs Unity 6.7, which is in beta: Surface Cache GI ships as a
  released feature there. Until 6.7 is out and the setup is tuned on it, the look can change
  between Unity versions, and the limitations below come from the current beta. The sample is a starting point to copy and adjust,
  not a supported render setup.
- Unity 6.7 (6000.7, beta) with URP 17.7 or later: Surface Cache GI and the screen-space reflection
  renderer feature are URP 17.6 features. The pipeline asset uses the Deferred path and needs
  light cookies (on by default).
- **One import per editor session.** Surface Cache GI builds its acceleration structure as
  renderers appear; a second stage imported into the same editor session can trigger
  "An item with the same key has already been added" inside URP, after which the new geometry
  does not occlude or bounce light. The import menu warns when it is the second import of the
  session; restart the editor for a clean result.
- Surface Cache GI keeps one atlas slice per Material and regrows its atlas one slice at a
  time, so a stage with thousands of unique materials can spike GPU memory during the first
  frames. Keep `UsdImportOptions.ShareIdenticalMaterials` on (the default) and watch the GPU
  memory on stages with more than about 2,000 materials.
- The interior reflection and the dome reflection are render textures: they are captured after
  the import and are gone when the saved scene is reopened. Use **Refresh reflection**.
- The look is tuned for linear colour space and an HDR Game view. The Scene view does not apply
  the volume's post-processing unless its post-processing toggle is on.
- Isaac Sim's RTX renderer path-traces area lights and glass; the sample approximates them with
  the importer's point-light grids, Surface Cache GI and screen-space reflections, so lit
  surfaces match in level while the exact shape of highlights and caustics differs.
