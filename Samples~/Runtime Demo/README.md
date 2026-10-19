# USD Toolkit Runtime Import/Export Demo

![The Runtime Demo app: a side panel with Source, Stage conversion, Loaded stages, Manipulate and Export sections next to a 3D viewport](../../Documentation~/images/usdruntimedemo.gif)

A customer-facing Unity 6 runtime demo for **USD as the interchange layer between a PLM/DCC
pipeline and the Unity runtime**. A scene is assembled from USD files at runtime, manipulated,
and written back out as a single USD stage.

Ships as the **Runtime Demo** sample of `com.unity.usd-toolkit`. Built against Unity
**6000.4.5f1**, URP 17.5.

## Setup

1. In **Window > Package Management > Package Manager**, select **Unity USD Toolkit**, open the
   **Samples** tab and import **Runtime Demo**. Unity copies it to
   `Assets/Samples/Unity USD Toolkit/<version>/Runtime Demo`.
2. The demo needs packages the toolkit itself does not depend on:

   | Package | Why |
   |---|---|
   | `com.unity.render-pipelines.universal` | imported materials, outline and gizmo shaders are URP |
   | `com.unity.inputsystem` | all input; set **Active Input Handling** to *Input System Package (New)* or *Both* |
   | `com.unity.ugui` | the HUD |

   Without Input System or uGUI the demo's assemblies are skipped rather than failing to
   compile, and the scene shows missing scripts until you add them.
3. Open `Scenes/USDRuntimeDemo.unity` and press Play.
4. For a player build, add the scene to the build's scene list. A build preprocessor copies the
   sample USD files into `Assets/StreamingAssets/USDSamples` so the player can open them.

---

## Key concepts

Three things worth knowing about how the demo works.

### 1. Two ways to export: keep your sources, or bake one file

The demo exports the assembled scene in one of two ways, chosen at export time.

**Referencing** keeps your PLM or DCC files as the single source of truth. The export is a
small layer, under 2 KB for the three samples, that points at the original files and records
only what changed in Unity: where each part sits and how it is oriented. The geometry is never
copied. When an upstream file is revised, the assembly picks up the change the next time it is
opened. Materials, metadata and anything else in the source files reach every consumer
unchanged.

The layer is written as `.usda`, the human-readable form of USD. Open it in any text editor and
you see your own file paths and the placement of each part. It is standard USD: it passes
`usdchecker`, and Omniverse, Houdini, `usdview` and any other OpenUSD tool open it like any other
layer. The demo writes this layer with its own writer (`Scripts/Core/UsdReferenceLayerWriter.cs`),
because the toolkit's exporter writes geometry, not references.

What the layer keeps from each source:

- **The part's own transform.** Each source sits under a wrapper `Xform` named after the file,
  which holds the placement from Unity. The reference is one level below it, on a prim named
  after the source's default prim. If the default prim carries its own translate, rotate or
  scale, it keeps them, and the placement applies on top. Putting the placement on the
  referencing prim itself would replace the default prim's `xformOpOrder`, and the part would
  land offset by whatever transform it carried.
- **The time range.** A stage reads `startTimeCode`, `endTimeCode`, `timeCodesPerSecond` and
  `framesPerSecond` from its root layer only, so the export copies them from the sources. It
  writes the rates of the first source that authors them, and the union of the sources' ranges.
  USD rescales a referenced layer's time samples to the root's time codes per second, so each
  range is converted to that rate first: a source at 30 time codes per second with a range of
  0–30 covers 0–24 in a layer at 24. A key that no source authors is not written, so a source
  without time metadata leaves the layer unchanged.

Two limitations:

- **Time metadata is read from text sources only.** The toolkit's import does not report a
  stage's time metadata, so the demo reads it from the layer header itself (see
  `Scripts/Core/UsdLayerTimeMetadata.cs`). That works for `.usda` and for a text `.usd`. Binary
  `.usdc` files, binary `.usd` files and `.usdz` packages add no time range; each export logs one
  info line naming the binary sources it skipped. Most Isaac Sim and Omniverse output is binary
  `.usd`, so for now an export assembled from those files carries no time range.
- **Only the default prim is referenced.** A reference brings in one prim and everything below
  it, which is standard USD behaviour. Top-level prims next to the source's default prim, such
  as a second root or a material scope outside it, are not part of the export.

**Flattening** bakes all geometry into one self-contained stage, written as compact binary
`.usdc` by default (`.usda` is available for inspection). Use it when the file has to travel
without its sources, for example to a supplier, to a review tool or into an archive.

| | Referencing | Flattening |
|---|---|---|
| Source files stay the authority | yes | no, it is a snapshot |
| Picks up upstream revisions | yes | no |
| Needs the source files to open | yes | no |
| Size (three samples) | < 2 KB | ~35 KB |

### 2. Z-up and centimetre files arrive the right way up and the right size

CAD and PLM tools often author Z-up and in centimetres or millimetres; Unity is Y-up and in
metres. Every USD file declares its up axis and units, and the demo reads both on import and
corrects the part to match Unity, so it lands upright and at its true size. Handedness (USD is
right-handed, Unity left-handed) is converted as well.

Both corrections are visible controls, not hidden behaviour:

| Control | Default | Effect |
|---|---|---|
| `Z-up → Y-up` | Auto | Rotates the part upright when the file declares `upAxis = "Z"` |
| `Units → metres` | Auto | Scales the part to metres when the file uses other units |

To show why this matters, switch a control to `Off` and re-import `plm_gearbox_zup_cm.usdc`: the
0.32 m gearbox arrives lying on its side at 32 m. Switch it back to `Auto` and it lands
correctly.

The correction is kept separate from the part's own position and rotation, so moving a part in
Unity never disturbs it, and the corrected size and orientation survive export and re-import.
Exported stages are written in metres and Y-up, as declared in their metadata.

### 3. This demo is New-Input-System only

With `activeInputHandler: 1`, legacy `UnityEngine.Input` throws. Everything uses
`UnityEngine.InputSystem`, and the EventSystem uses `InputSystemUIInputModule` — the legacy
`StandaloneInputModule` would silently never deliver a UI click.

---

## Layout

All controls are in one sidebar on the left, so the viewport is a single uninterrupted
rectangle rather than a letterbox between two panels.

The sidebar is **pinned header / scrolling middle / pinned footer**. The stage list is the only
thing that can grow without bound, so it is the only thing that scrolls; import, the conversion
switches, the gizmo toggle and every export option stay on screen whatever is loaded. Nothing
you reach for while a room is watching can end up below the fold — in particular the gizmo
toggle and the referencing/flattening choice, both of which are decisions made live.

With the three sample files loaded, nothing scrolls at all.

## What it does

**Import** — Native OS file dialog (macOS `NSOpenPanel` via `osascript`; Windows `comdlg32`),
filtered to `.usd/.usda/.usdc/.usdz`, multi-select. Each file lands as its own root GameObject,
laid out clear of what is already loaded. Previous imports are never touched. New geometry eases
in over 0.3 s rather than popping. The left panel lists every loaded stage with its mesh and
triangle count, and tags any stage that needed conversion.

**Manipulate** — Click to select (raycast against the colliders the importer generates). The
selection gets a cyan outline; the gizmo translates and rotates. Scale is deliberately absent.
`W`/`E` switch mode, and so do the on-screen buttons, because a keyboard shortcut does not read
on a shared screen. `Esc` deselects. RMB orbits, MMB pans, wheel zooms — left mouse belongs to
selection alone, so a missed click never swings the camera.

**Hiding the sidebar** — the tab on its inner edge collapses it, and so does `H`, for a
full-bleed look at the part. The tab sits on the canvas rather than inside the sidebar, so it
never disappears with the panel it controls; there is always something visible to click to bring
it back. The title bar and status bar stay up in every state, so the error surface is never
hidden.

**Export** — One button, with the mode chosen at export time:

| | Referencing | Flattening |
|---|---|---|
| Composition | `prepend references` to the original layers | all geometry baked into one stage |
| Format | `.usda` (see above) | `.usdc`, `.usda` as debug fallback |
| Size (3 samples) | **< 2 KB** | **35 KB** |
| Source stays authority | yes | no |
| External dependencies | yes — the source files | none |

---

## Verification already done

Everything below was run against this build; none of it needs repeating on the day.

**Samples** — `USDSamples/` holds four pre-validated files, all passing `usdchecker`. The
Editor reads them in place; a player build reads the copy the build preprocessor puts in
`Assets/StreamingAssets/USDSamples/` (an existing copy there takes precedence in both).

| File | Stage | Why it is here |
|---|---|---|
| `plm_bracket.usdc` | Y-up, metres, binary | the clean case |
| `plm_housing.usda` | Y-up, metres, ASCII | proves ASCII input works too |
| `plm_gearbox_zup_cm.usdc` | **Z-up, centimetres** | proves the conversion config is real |
| `plm_indexer_anim.usda` | Y-up, metres, ASCII, **animated** 0–48 at 24 fps | its default prim has its own transform and two child prims are time-sampled, so a referencing export shows that both survive |

**Round trip** — all three imported, moved and rotated, then exported and re-imported:

| Artifact | Re-import |
|---|---|
| `referencing`, relative paths | 9 meshes / 1320 verts / 832 tris |
| `referencing`, absolute paths | 9 meshes / 1320 verts / 832 tris |
| `flattened .usdc` | 9 meshes / 1320 verts / 832 tris |
| `flattened .usda` | 9 meshes / 1320 verts / 832 tris |

Both modes compose to identical geometry. The Z-up/cm gearbox lands at 0.32 × 0.345 × 0.32 m in
the exported stage — the correction survives the round trip.

**Animated sample** — `plm_indexer_anim.usda` and `plm_bracket.usdc` were imported, moved and
rotated, then exported as a referencing layer and re-imported. Every prim landed within 1e-7 m of
where it was. The layer carries `startTimeCode = 0`, `endTimeCode = 48`, `timeCodesPerSecond = 24`
and `framesPerSecond = 24`, and the indexer's default prim keeps its own
`translate`/`rotateY`.

**Relative-path fragility — tested specifically, because it is the likely failure:**

| Scenario | Result |
|---|---|
| Referencing layer exported next to its sources | asset paths are short: `@../USDSamples/plm_bracket.usdc@` |
| That whole folder moved elsewhere | **composes fine** — 9 meshes |
| Relative layer moved **alone**, away from its sources | **breaks — 0 meshes** |
| Absolute layer moved alone | composes fine, but pinned to this machine |

A broken reference does **not** throw and does **not** print to the console. The toolkit returns
a stage with 0 meshes, which the demo reports as `Import failed: no geometry found in <file>` on
the status bar.

**The default is the safe one.** The export dialog opens in the folder you last imported from, so
the natural save location is next to the sample files, which produces short relative paths. Use
`Absolute` if you intend to move the exported layer on its own.

---

## Stage safety

- Every file I/O and toolkit call is wrapped; failures become one sentence on the status bar
  (`Import failed: …`, `Export failed: …`), never a stack trace. Cancelling a dialog is a normal
  outcome and stays silent.
- Verified: **zero console errors** through the full import → select → move → export → re-import
  flow.
- `Application.runInBackground = true` is set in `Awake`. Without it the player stops ticking the
  moment the native dialog takes focus, and the app appears to hang until you click back on the
  window.
- The macOS dialog runs out of process and is polled from a coroutine, so the app keeps rendering
  while the panel is open — no beachball behind the dialog.
- Selection outlines and gizmo handles live outside the exported hierarchy, so they can never be
  baked into a customer's file.

### Two things to check on the machine you present from

1. **If you present from a player build rather than the Editor**, do one build and one
   import/export first. Imported materials use `Shader.Find("Universal Render Pipeline/Lit")`,
   and `Shader.Find` only sees shaders included in the build.
2. **Keep the player windowed.** The native panel is a separate window; a fullscreen player can
   put it behind you.

---

## Out of scope

Editor-time workflow, multi-user editing, undo/redo, and scale in the gizmo — all deliberately
absent.

**Materials and shading round-trip is also out of scope, and it is the question a PLM-heavy
audience is most likely to ask.** Worth knowing where it actually stands: import already reads
`UsdPreviewSurface` (diffuse, metallic, roughness, opacity, and textures) onto URP/Lit materials
— that is why the samples arrive coloured. The gap is outbound: `UsdExportOptions.ExportTextures`
exists and writes PNGs next to the stage, but this demo leaves it off, so exports carry geometry
and flat colour only. In *referencing* mode the question is largely moot — the source materials
are never touched, so they survive by construction. That is a good answer to have ready, and a
natural follow-up demo.

---

## Layout

```
Runtime Demo/
  Scenes/USDRuntimeDemo.unity      one GameObject; everything else is built at runtime
  Scripts/
    UsdDemoApp.cs                  bootstrap, input routing, import/export flows
    Core/
      UsdImportService.cs          import + failure messages + load-in beat
      UsdExportService.cs          both export modes behind one call
      UsdReferenceLayerWriter.cs   the composition layer the toolkit cannot write
      UsdLayerTimeMetadata.cs      time metadata from a text layer's header
      UsdStageConversion.cs        up-axis / unit correction
      UsdSceneEntry.cs             one loaded stage
    Interaction/                   gizmo, outline, selection, orbit camera
    UI/                            HUD logic (DemoHud), UiButton, StageListRow
    Platform/NativeFileDialog.cs   macOS + Windows native panels
  Prefabs/DemoHud.prefab           the uGUI HUD, placed in the scene and assigned to UsdDemoApp
  Editor/
    DemoHudPrefabBuilder.cs        one-shot generator for the HUD prefab
    RuntimeShaderIncludes.cs       keeps the runtime shaders and URP/Lit variants in a build
    DemoSampleFilesBuildStep.cs    copies USDSamples/ into StreamingAssets for a player build
  Resources/RuntimeShaders/        materials + variant collection written by RuntimeShaderIncludes
  Shaders/                         outline, stencil mask, gizmo handle
  USDSamples/                      the four pre-validated files
```

The scene (camera, lights, gizmo) is built in code on purpose: a demo that constructs itself cannot
turn up on the day with a broken prefab link. The HUD is the exception: it is an authored uGUI prefab
so layout and styling can be edited in the Editor. `DemoHud` only holds serialized references and
wires button clicks in `Awake`. *Tools › USD Runtime Demo › Generate HUD Prefab* rebuilds the prefab
from code. It overwrites hand edits, so use it only to bootstrap or reset.

`UsdDemoApp.LoadFile(absolutePath)` imports without the dialog, for scripted preloads or smoke
tests.
