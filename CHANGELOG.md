# Changelog

All notable changes to the Unity USD Toolkit are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

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
  them yet. `Native~/security-scans/known-vulnerabilities-2026-10-01.md` records a check of the
  pins against OSV and NVD: no published vulnerability applies.
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
  name into `libusd_ms.dylib` 1134 times; the shipped one is built under `/Users/Shared`. macOS is
  rebuilt; Windows and Linux follow `Native~/HANDOFF_LZ4_WINDOWS_LINUX.md`, and the branch is not
  mergeable until they have.
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
  which warns that the payload must not be shipped. Linux has no wrapper build script — the
  release path is plain CMake — so its gate is the two commands in the runbook.

  Two findings came out of writing this down. `build_usd.py` passes `expectedSHA256` for Boost
  and for no other dependency, so TBB is fetched over HTTPS with no integrity check of its own,
  which is why the archive digest is recorded here instead of relied on there. And under this
  package's build flags TBB is the *only* downloaded dependency: everything else, including the
  four libraries OpenUSD vendors in (`pxr/base/tf/pxrCLI11`, `pxrDoubleConversion`, `pxrLZ4`,
  `pxrTslRobinMap`), is compiled from the OpenUSD tree itself and is therefore covered by the
  commit pin. The whole payload's origin is two records per platform.

### Security

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

### Security

- **The package ships a software bill of materials, and the third-party notices now cover every
  platform** `ThirdPartyNotices~/sbom.cdx.json`
  (CycloneDX 1.6) lists each native component a consumer loads: OpenUSD `v26.05` with its commit
  (`2095faf`) and build flags, oneTBB 2020.3, the four libraries OpenUSD vendors into the
  monolithic build (CLI11 2.3.1, double-conversion 3.3.0, LZ4 1.9.2, tsl robin-map), the
  per-platform toolchain, and the SHA-256 of all 11 shipped binaries — the same digests the
  runtime manifest carries. Regenerate it with `python3 Native~/generate_sbom.py` in the same
  commit as any payload rebuild. `ThirdPartyNotices.md` listed only the Windows files and no
  versions, although macOS and Linux redistribute the same libraries; it now covers all three
  platforms, states the OpenUSD and oneTBB versions, and carries the licenses of the vendored
  components. The SBOM is an inventory, not an integrity control: it records what shipped and
  where it came from, which is what the review asked for alongside the public-tag rebuild.
- **The macOS build toolchain is recorded in `BUILD_NOTES.md`**, as Windows and Linux already
  were — macOS version, Xcode and clang builds, CMake, SDK and architectures — so the v26.05
  rebuild can be reproduced by a third party.

### Changed

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
- **The supported operating systems are written down, and Linux is Ubuntu 24.04 or newer**
  (a decision, recorded rather than discovered). Nothing stated a minimum OS before, although the
  native payload has always had one and it applies to players as well as to the Editor: Windows 10
  21H1, macOS 12.0, Ubuntu 24.04. The Linux payload is built on 24.04 and needs glibc >= 2.38 and
  a libstdc++ providing `GLIBCXX_3.4.32`, so it does not run on Ubuntu 22.04 even though Unity 6.3
  supports that distro. Only three symbols force it — `__isoc23_strtol`, `fmod`, `fmodf`, all
  artifacts of compiling against 24.04 headers — so a rebuild in a 22.04 container would lower the
  floor with no source change; that is noted as open work in `BUILD_NOTES.md` and the Linux
  rebuild runbook. `README.md` and both user manuals now carry the table.
- **A native load failure names the OS requirement.** A library that is present and intact but
  built for a newer OS is refused by the loader and surfaces as "file not found", which sent
  people looking for a missing payload. The exception text now adds the platform's minimum — on
  Linux the glibc and `GLIBCXX` versions with `ldd --version` to check them, on macOS 12.0 — on
  top of the existing payload checklist. Import failures inherit the same message.

### Fixed

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
- The macOS payload is rebuilt from the same pinned OpenUSD 26.05 commit and patch, and
  `Native~/dependency-digests/macos.sha256` and `NativeRuntimeHashes.g.cs` are updated for it.
  **The Windows and Linux payloads still need a rebuild on their own OS** to pick up these fixes;
  until then they behave as before.

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
  floor is unchanged (glibc >= 2.38, `GLIBCXX_3.4.32`, measured again). One item is recorded, not fixed, and it is not
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

Fixes for the importer/exporter findings. Version number
still to be decided.

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
  too**. `UsdNative.MinimumApiVersion` accepted anything
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
