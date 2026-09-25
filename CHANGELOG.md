# Changelog

All notable changes to the Unity USD Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

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

### Security

Fixes for the importer/exporter findings of security review SECURITY-282834. Version number
still to be decided.

- **Imported texture paths are confined to the stage folder** (SECURITY-282834 / CWE-22). A USD
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
- **The managed PNG decoder no longer trusts IHDR dimensions** (SECURITY-282834 / CWE-190,
  CWE-789). `UsdPngDecoder.TryDecode` checked only that width and height were positive, then sized
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
  too** (SECURITY-282834 / CWE-494 remediation 4). `UsdNative.MinimumApiVersion` accepted anything
  from API 2 upward, a tolerance that existed so payloads lagging the source kept working while
  Windows and Linux were rebuilt. All three desktop payloads reached API 5, and the tolerance had
  become a hole: fixes that are *not* gated on the ABI version — the topology and asset-path fixes
  above among them — are simply absent from an older binary, with nothing in the version number to
  say so. An older plugin is now refused with a message that says as much. Separately, the gate
  was only ever called from the export path, so an import ran against whatever plugin happened to
  be loaded — backwards, since the import path is where untrusted file content is parsed. Both
  `UsdImporter.Import`/`ImportAsync`/`GetPreviewInfo` now validate it.
- **The native payload is verified against recorded digests before the first P/Invoke**
  (SECURITY-282834 / CWE-494). `ValidateNativeRuntimeFiles` only asked whether a file of the right
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
- **Untrusted mesh topology is rejected instead of triangulated** (SECURITY-282834 / CWE-190,
  native). `BuildImportedSubmeshes` accumulated each face's start offset in a signed 32-bit int,
  so a stage whose `faceVertexCounts` summed past `INT_MAX` wrapped that accumulator, and the
  corrupted offset then defeated `TriangulateFace`'s own bounds check — which added two
  file-controlled values and so overflowed in turn. `cornerVertices` was then read far outside
  its buffer: an unrecoverable native crash, with out-of-bounds values handed back to managed
  code. The offsets are now accumulated in 64-bit, every offset is kept inside the real corner
  buffer, and a mesh whose face counts do not sum to exactly the `faceVertexIndices` length is
  reported and skipped rather than trusted. `TriangulateFace`'s guard was rewritten to subtract
  from the known-good buffer length instead of adding two untrusted values.
- **Asset reads through the native resolver are confined to the stage's folders**
  (SECURITY-282834 / CWE-22, native). `RUsd_ReadImportAsset` resolves through OpenUSD, which
  honours absolute paths and `..` climbs, so it was a second read path independent of the managed
  guard above. The resolved path is now confined to the directory of any layer that composes the
  stage — not just the root layer's, so a sublayer or reference keeping textures next to itself
  still works — and a texture packaged inside a `.usdz` is judged by the package file that
  contains it. Refusals and read errors name the authored path instead of the resolved location,
  so a blocked attempt no longer discloses local filesystem layout.
- **`UsdExportOptions.PluginSearchPath` is confined to the package's own native folders**
  (SECURITY-282834 / CWE-427). The value was written verbatim into `PXR_PLUGINPATH_NAME`, and
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
  (SECURITY-282834 / CWE-312). The session token was persisted with `File.WriteAllText`, which
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
  (SECURITY-282834 / CWE-306, CWE-284). `UsdLiveSyncServer` accepted commands from anyone who could
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
  (SECURITY-282834 / CWE-284, CWE-400). `ResolveBindAddress` passed any parseable address through,
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
