# Cross-Platform Build Notes

Records the macOS development state and the exact steps to rebuild the native plugin for
**Windows** and **Linux**. The managed (C#) code is platform-agnostic — only the native C++
wrapper must be rebuilt per platform.

## How the payload is trusted, and what is still weak (SECURITY-282834)

Where the shipped binaries' trustworthiness rests today, written down so the gaps are a position
rather than an oversight. Each line is verifiable from the repository.

**What holds today**

- *Provenance of the third-party code.* All three payloads are built from the public OpenUSD
  `v26.05` tag, commit `2095faf`, with one Unity patch applied and identical flags, and the build
  environment of each platform — OS, compiler, CMake, SDK, build root — is recorded per payload
  below. A third party can rebuild from the same published source plus the patch and compare what
  the binary contains.
- *The one change to that source.* `Native~/patches/openusd-26.05-lz4-1.10.0.patch` replaces the
  LZ4 1.9.2 OpenUSD vendors in `pxr/base/tf/pxrLZ4` with upstream 1.10.0 — Pixar's nine namespace
  edits re-applied, no other line changed — and makes `TfFastCompression::DecompressFromBuffer`
  check the chunk count and every size it passes to LZ4, all of which come from the `.usdc` being
  read (SECURITY-282834, CVE-2021-3520: the crate reader's 64-bit section sizes reached LZ4 as
  negative ints above `INT_MAX`). The patch is pinned by SHA-256 next to the commit it applies to,
  applied to a worktree rather than the clone, and recorded in the SBOM's pedigree.
  `Native~/Tests~/lz4_bounds_test.cpp` checks the shipped `libusd_ms` carries it. Upstream still
  vendors 1.9.2.
- *The pin is enforced, not just written down.* `Native~/dependency-sources/<platform>.tsv`
  records the OpenUSD commit and the TBB archive a build may use;
  `Native~/verify_upstream_sources.py` checks a clone against it — pinned commit, recorded remote,
  unmodified worktree, a tag upstream has not moved. Under this package's flags TBB is the only
  dependency `build_usd.py` downloads at all; everything else, the four vendored libraries
  included, comes from the OpenUSD tree and is covered by the commit pin. `build_usd.py` passes
  `expectedSHA256` for Boost and for nothing else, so OpenUSD is built with
  `Native~/build_openusd.py` instead of calling it directly: that script downloads the TBB archive
  and checks it against the pin *before* `build_usd.py` runs, runs `build_usd.py` with downloads
  blocked, and then stamps the install with that result and the SHA-256 of every file a wrapper
  build copies out of it. `build_macos.sh`, `build_windows.ps1`, `build_linux.sh` and
  `Native~/CMakeLists.txt` refuse an install whose stamp is missing or no longer matches its
  files, and a wrapper built with that check skipped is marked so no manifest can be generated
  for it.
- *Inventory.* `ThirdPartyNotices~/sbom.cdx.json` (CycloneDX 1.6) lists every component in the
  payload with version, source and per-file SHA-256, including the four libraries OpenUSD vendors
  into the monolithic build.
- *What was read off the build host.* `Native~/dependency-digests/<platform>.sha256` records the
  digest of every OpenUSD/TBB file a build copies in, and a build refuses to run against a tree
  that does not match. Changing dependencies has to appear as a reviewed diff.
- *Tamper detection at load.* `Runtime/Native/NativeRuntimeHashes.g.cs` covers every binary **and**
  every plugin descriptor, keyed by relative path; the verifier walks the payload recursively,
  fails on an unlisted file inside `lib/usd`, `plugin/usd` or `share/usd`, and refuses a
  `plugInfo.json` whose `LibraryPath` escapes the payload root. A release player cannot opt out.
- *Publisher identity.* Code signing on Windows and macOS — except Windows `tbb_usdrt.dll`, which
  keeps Intel's own Authenticode signature, being the one binary in the payload we redistribute
  rather than build — and the package attestation
  (`package/.attestation.p7m`, CMS over the digest of every packaged file) for all three
  platforms — the latter being the only such evidence Linux can have, as ELF has no code signature
  and Unity's signing service covers PE and Mach-O only.

**What is still weak, and why**

1. *The payload is committed, not built in CI.* The binaries live in the repository under Git LFS
   and CI signs and packs what is committed; it does not compile them. So the chain of custody
   between "public OpenUSD tag" and "the bytes in this repository" is a person following
   `Native~/REBUILD_WINDOWS_LINUX.md` on a machine, rather than a build log. The source end of
   that chain is now gated rather than trusted — the build scripts refuse an OpenUSD install that
   was not verified against the pinned commit — but the machine in the middle is still a person's,
   and its output is still a commit rather than an artifact CI produced. This is the root of the
   next two items.
2. *No reproducible-build gate.* The review asks for a release that fails automatically when a
   reproducible-build check does not match. That needs (1) plus byte-reproducible builds. Partly
   prepared: build paths no longer leak into the binaries (`-ffile-prefix-map`, neutral build
   roots), and the macOS wrapper already rebuilds byte-identically from the same input. Not
   measured: whether the OpenUSD monolithic build is deterministic across two runs at the same
   path.
3. *Signing is applied at pack time, to committed binaries.* A consumer of the published package
   gets signed binaries; a reader of the repository does not. If a re-review inspects the
   repository rather than the published tarball, it will still find unsigned binaries there.
4. *The upstream source has not been scanned.* The review requires each version to be scanned
   for security issues before we compile it. Cycode runs on this repository's own code — SAST,
   secrets and vulnerable dependencies, on every pull request — but the OpenUSD and oneTBB source
   is not committed here, so nothing has looked at it. `Native~/security-scans/` holds the
   requirement, the command, and what a record must contain;
   `verify_upstream_sources.py --require-scan` gates a release stamp on one existing. No record
   exists yet.

**Why we may sign libraries we did not write**

The code-signing review's rule is that a third party's binaries should carry the third party's
signature; that if they will not sign, an alternative way to verify integrity and trust can stand
in, in which case we may sign with Unity's certificates after establishing the code is free of
malicious content and if the license allows; and that source we compile ourselves must be scanned
per version before we compile it, and may then be signed if the license allows. Against that:

1. *We compile almost all of it.* Every file in the payload except one is built here from public
   source: OpenUSD from `v26.05`, oneTBB from the `v2020.3` (macOS, Windows) / `v2020.3.1` (Linux)
   source tags. So the rule that applies is the "we compile it ourselves" one, not the
   third-party-binary one.
2. *No upstream signature exists to preserve.* OpenUSD's `v26.05` is a lightweight tag — there is
   no tag object that could carry a signature — and its commit `2095faf` is unsigned. oneTBB
   publishes no checksum for v2020.3. Asking upstream to sign is the step the rule prescribes
   first; there is nothing there to ask for today.
3. *The alternative evidence is the commit id.* A git commit id is a hash of the tree it names, so
   the source cannot be altered after the fact without changing the id, and the same id is
   observed independently by every other consumer of that tag. That is what
   `Native~/dependency-sources/` pins and what `verify_upstream_sources.py` enforces, which is the
   integrity-and-trust evidence the rule asks for in place of a signature.
4. *The license permits it.* OpenUSD is under the Tomorrow Open Source Technology License 1.0,
   which differs from Apache 2.0 only in Section 6 (Trademarks) — a restriction on using the
   Licensor's marks, which signing does not do: an Authenticode or Developer ID signature asserts
   who distributed the binary, not who endorses it. oneTBB is Apache 2.0, and the four vendored
   libraries are permissive. All permit modification and redistribution, which is what building
   and signing require. Formal compliance review is with the Security team.

The one binary we do not build is Intel's TBB on Windows, shipped as `tbb_usdrt.dll`, and it lands
on the other branch of the rule with the strongest evidence in the payload. It comes from Intel's
published `tbb-2020.3-win.zip` release asset; the file this package ships is **byte-identical** to
`tbb/bin/intel64/vc14/tbb.dll` inside that archive (SHA-256 `692380ce…`), and it carries Intel
Corporation's own Authenticode signature (Intel External Issuing CA 7B, under QuoVadis). So it is
a third-party binary that its author *did* sign, which is why `.yamato/sign.yml` signs the other
two Windows DLLs and leaves this one alone.

It ships under a different name than upstream gives it because the Unity Editor carries its own
`tbb.dll`, and Windows resolves an import by base name against the modules already loaded: the
first `tbb.dll` in the process wins, and the payload would run against a build it was not compiled
against. Only the name changes — `Native~/patch_pe_import.py` repoints `usd_rt.dll`'s import
descriptor and leaves Intel's file untouched, so the signature and the byte-identity above both
still hold. Authenticode covers a PE's contents, not its filename.

Step 3 establishes that the source is what upstream published. It does not establish what that
source contains, which is step 1's scan — see `Native~/security-scans/`.

**Planned, in order**

- *Phase B1 — build the wrapper in CI.* Treat the OpenUSD install as a pinned, hash-fixed artifact
  built once per tag, and compile `Native~/src` from source in Yamato on every release. The code
  this team authors then has a build log, and because the wrapper is already deterministic the
  reproducibility gate can be switched on for it immediately. Days of work, not weeks.
- *Phase B2 — build OpenUSD in CI too.* A tier-1 pipeline per platform producing the OpenUSD
  artifact, which is what fully closes the reproducible-build finding. Needs Bokken images with
  the C++ toolchains, artifact storage for a payload of this size, and a determinism measurement
  first.
- *Smaller items carried to the next rebuild of each platform:* the oneTBB build-host stamp (see
  below), and lowering the Linux floor to Ubuntu 22.04 by building in a 22.04 container.

## Current state (2026-09-30)

- **Package version:** `0.7.2-exp.1`
- **LZ4 1.10.0 in all three payloads (SECURITY-282834, CVE-2021-3520), 2026-09-29/30.** Each
  platform's OpenUSD was rebuilt from `v26.05` (`2095faf`) plus
  `Native~/patches/openusd-26.05-lz4-1.10.0.patch` with `Native~/build_openusd.py` into a new
  install (stamp v3, without `--require-scan`: no upstream scan is recorded yet — see *What is
  still weak*), and the payload rebuilt against it with `--record-dependency-digests`.
  `Native~/HANDOFF_LZ4_WINDOWS_LINUX.md` is the runbook and holds the full per-platform results.
  - *macOS* — `/Users/Shared/usd-26.05-lz4/install`, toolchain as in the macOS entry below.
    `macos.sha256`: 4 of 89 entries changed, the four compiled dylibs; wrapper byte-identical.
  - *Windows* — `C:\USD\u2605lz4`; VS Build Tools 2022 (MSVC 14.44), Windows SDK 10.0.26100,
    CMake 4.3.3, Python 3.11.9. `windows.sha256`: 20 of 91 changed — `lib/usd_ms.dll` plus 19
    text files whose previous record was a CRLF checkout (now LF, equal to their blobs at
    `2095faf`); `tbb_usdrt.dll` unchanged and still Intel-signed. Wrapper rebuilt.
  - *Linux* — `/opt/usd-26.05-lz4/install`; Ubuntu 24.04.5, g++ 13.3.0, CMake 3.28.3, patchelf
    0.17.2, Python 3.12.3. `linux.sha256`: 2 of 88 changed, `lib/libusd_ms.so` and
    `lib/libtbb.so.2`; wrapper byte-identical. glibc floor unchanged (see the Linux entry).
    First real run of the rewritten `build_linux.sh`; it needed no fix.
  - *Gates, on every platform:* 1, 3, 4 (25 `RUsd_*`), 5, 6 (`import_uv_test` `API 5` PASS,
    `usdz_test` PASS), `security_test` 9/9, and `lz4_bounds_test` **8/8 on the shipped
    `libusd_ms`** (the pre-patch macOS payload failed 4, three by faulting). `Invalid chunk count`
    — a string only the patch adds — is in each shipped OpenUSD library. `NativeRuntimeHashes.g.cs`
    and the SBOM were regenerated once, on Linux, after all three payloads were in: only the
    rebuilt binaries' entries moved, no descriptor.
  - *Gate 7 (Editor):* macOS done (Unity 6000.4.10f1, Built-in, headless, at `0920d0d`): McUsd
    23 / 1760 / 880, 23/23 UVs, normals and textures, 8 cutout, 1 blended; `.usdz` re-import
    23/23 textures with no warning; `.usdc` round trip identical; a symlinked `McUsd_materials`,
    dangling texture links and links to files outside all refuse every texture (handoff §11).
    Windows done (Unity 6000.4.10f1; handoff §9). Linux done
    (Unity 6000.4.11f1, headless): McUsd 23 / 1760 / 880, 23/23 UVs, normals and textures, 8
    cutout, 1 blended; `.usdz` re-import 23/23 textures with no warning; `.usdc` round trip
    identical; a symlinked `McUsd_materials` refuses all textures (handoff §10).
- **Native ABI version:** `5` (`RUsd_GetApiVersion()` in C++), and `UsdNative.MinimumApiVersion`
  is now **`5` as well** — an exact match with the source. It was `2` while the Windows and Linux
  payloads lagged: ABI 3, 4 and 5 only *added* entry points, so an older plugin still loaded and
  simply skipped UV sets 1–2, authored opacity, usdz packaging and packaged texture reads based on
  `UsdNative.LoadedApiVersion`. All three desktop payloads reached API 5 on 2026-09-22, and the
  tolerance was closed because it had become a hole: fixes that are **not** gated on the ABI
  version — the SECURITY-282834 topology and asset-path fixes among them — are absent from an
  older binary with nothing in the version number to say so. The gate now also runs on the
  **import** path, which previously never checked the version at all.
- **Linux was rebuilt on 2026-09-22 (API 5, SECURITY-282834 fixes included) and again on
  2026-09-24 from a build path with no user name in it** — see the Linux payload entry below. All three desktop payloads are now API 5 and built from the current source.
  `Native~/REBUILD_WINDOWS_LINUX.md` remains the step-by-step rebuild guide with gates for both
  platforms (`Native~/WINDOWS_REBUILD.md` is the record of the stale-DLL texture bug it supersedes).
- **Import UV/normal primvars (0.7.2, all platforms once rebuilt):** `BuildImportedMesh` resolves
  UVs and normals per face-corner (`constant`/`uniform`/`vertex`/`varying`/`faceVarying`, indexed
  or not) and splits vertices where corners disagree. Reading goes through `ReadVec2Array` /
  `ReadVec3Array`, which pick the `UncheckedGet` branch from the **registry type name**
  (`attr.GetTypeName().GetAsToken()`) for the same DSO reason as `VtValueHoldsType` below.
  **Keep the unsplit path allocation-free** when touching this code: it bulk-copies per-point
  values, reads the corner table directly from `faceVertexIndices.cdata()`, and builds neither a
  corner→face nor a corner→vertex buffer. Materializing those (the first 0.7.2 draft did) cost
  +60% on the native stage read of a 564-mesh / 1.19M-vertex scene, which is also the shape of
  regression to re-measure after changes: `Native~/Tests~/import_uv_test.cpp` covers correctness,
  not cost.
- **OpenUSD version:** built **monolithic** (`libusd_ms`/`usd_ms`) with
  `--no-python --no-imaging --no-usdview --no-examples --no-tutorials --no-tests --no-materialx`.
  **All three platforms are built against the same public OpenUSD tag, `v26.05`** (commit
  `2095faf`, "Merge release v26.05"), as of 2026-09-23. macOS was previously built from a
  non-public 26.08 source drop that carried no git history, so no third party could reproduce or
  inspect it; SECURITY-282834 raised that as its High finding and the fix was to align macOS on
  the tag Windows and Linux already used. The native ABI (`RUsd_GetApiVersion`) is `5` on all
  three.
- **macOS payload:** built against **26.05** (`PXR_VERSION 2605`, binary tag
  `pxrInternal_v0_26_5`), **API 5**, universal (`x86_64` + `arm64`), under
  `Runtime/Plugins/macOS/`. **Rebuilt 2026-09-23 from the public `v26.05` tag** (commit
  `2095faf`) to close SECURITY-282834's provenance finding — the previous payload came from a
  non-public 26.08 source with no git history, so its exact revision could not be published and
  nobody outside the build could reproduce it. Same build flags as Windows and Linux
  (`--build-monolithic --no-python --no-imaging --no-usdview --no-examples --no-tutorials
  --no-tests --no-materialx`). Verified after the rebuild: `RUsd_GetApiVersion()` -> 5 and
  `RUsd_GetOpenUsdVersion()` -> `0.26.5`; `security_test` 9/9, `import_uv_test` PASS, `usdz_test`
  PASS; Unity 6000.4.10f1 clean compile with 0 errors, the digest check passing and the import
  gate returning 1 mesh for the 3-mesh inconsistent-topology fixture. The earlier 26.08 payload
  was itself verified by
  `Native~/Tests~/security_test.cpp` (9/9), plus `import_uv_test` and `usdz_test` re-run for
  regressions (both PASS).
  **Toolchain** (recorded so a third party can reproduce the build environment, which is the
  other half of SECURITY-282834's provenance finding — Windows and Linux already recorded
  theirs below): macOS 26.6.2 (`25G83`), Xcode 26.6 (`17F113`), Apple clang 21.0.0
  (`clang-2100.1.1.101`), CMake 4.3.2, SDK 26.5, `CMAKE_OSX_ARCHITECTURES=x86_64;arm64`.
  **Build location: `/Users/Shared/usd-26.05`, never a home directory.** `__FILE__` (OpenUSD's
  `TF_AXIOM` / `TF_VERIFY` macros expand it) and `__PRETTY_FUNCTION__` (a lambda passed as a
  template argument prints the file it was written in) embed the absolute path of what is being
  compiled, so a build from `~` ships the developer's user name inside the payload — 1,134 such
  strings in the 2026-09-23 `libusd_ms.dylib`, 8 in the wrapper. Build OpenUSD from a path that
  contains no user name; the wrapper additionally passes `-ffile-prefix-map` for the package root
  and the OpenUSD install (`Native~/CMakeLists.txt`), so its own strings read `/usd-toolkit/...`
  and `/openusd/...` wherever it is built. Check with
  `strings -a <dylib> | grep '/Users/'` — the only paths left should be the neutral build root.
  One identifier is left that is not a path: the oneTBB banner in `libtbb.dylib` names the build
  host, which on this machine is a personal machine name. See "Build host names in the oneTBB
  stamp" below — it applies to macOS and Linux alike.
  **Deployment target: 12.0.** Nothing used to set `CMAKE_OSX_DEPLOYMENT_TARGET`, so every dylib
  took the SDK default — the build machine's own OS — and the payload shipped as `minos 26.0`,
  which dyld refuses on anything older. The whole macOS payload was rebuilt on 2026-09-23 with
  `MACOSX_DEPLOYMENT_TARGET=12.0` for OpenUSD and oneTBB and `--deployment-target` (default
  `12.0`) for the wrapper; all five dylibs now report `minos 12.0` on both slices. 12.0 is
  Unity 6.3's minimum for a macOS **player**; the Editor's own minimum is 13.0, so one value
  covers both surfaces the plugin loads into. Rebuild OpenUSD and the wrapper with the same
  value — the wrapper's flag does not reach the OpenUSD dylibs, which keep whatever they were
  built with.

- **Payload digest manifest:** `Runtime/Native/NativeRuntimeHashes.g.cs` records the SHA-256 of
  every shipped `.dll`/`.dylib`/`.so`, and `UsdExporter.VerifyNativeRuntimeIntegrity` compares
  them once per process before the first P/Invoke (SECURITY-282834, CWE-494). **Regenerate it in
  the same commit as any payload rebuild** — `python3 Native~/generate_native_hashes.py` — or the
  package refuses its own binaries. The digests live in a generated C# file rather than a data
  file beside the payload on purpose: a manifest shipped next to the binaries is editable by
  anyone who can edit the binaries. This does not defend against someone who already has write
  access to the package; it catches substitution or corruption in distribution and makes the
  payload auditable.
- **Signing and CI:** the payload is signed in CI, not on a developer machine. `.yamato/` holds the
  pipeline — sign the committed binaries with Unity's certificates (Azure Key Vault bricks), regenerate
  both manifests from the signed files, then pack, test and publish — and `.yamato/README.md` explains the
  ordering constraints and what still has to be requested from `#devs-code-signing` and PETS before it can
  run. A local rebuild is unsigned by design: `build_macos.sh --adhoc-codesign` or
  `build_windows.ps1 -SkipSigning` is the normal development path, and the committed manifests describe
  those unsigned binaries.
- **Software bill of materials:** `ThirdPartyNotices~/sbom.cdx.json` (CycloneDX 1.6) lists every
  third-party component the payload carries — OpenUSD `v26.05` with its commit and build flags,
  oneTBB 2020.3, and the four libraries OpenUSD vendors into the monolithic build (CLI11,
  double-conversion, LZ4, tsl robin-map) — with the per-platform toolchain and the SHA-256 of each
  shipped file (SECURITY-282834, CWE-494 remediation 4). **Regenerate it in the same commit as any
  payload rebuild** — `python3 Native~/generate_sbom.py` — alongside `generate_native_hashes.py`,
  and update the version and toolchain constants at the top of that script when they change. Its
  digests are the same values as the runtime manifest's; it is an inventory for consumers, not a
  second integrity control.

> All three payloads (Windows rebuilt 2026-09-22, macOS 2026-09-23, Linux 2026-09-24) now carry the
> SECURITY-282834 native fixes and pass `Native~/Tests~/security_test.cpp` (9/9).
- **Windows payload:** rebuilt against **26.05** (`PXR_VERSION 2605`, binary tag
  `pxrInternal_v0_26_5`), **API 5**, x64, under `Runtime/Plugins/x86_64/Windows/`. **Rebuilt and
  verified on 2026-09-22** with the SECURITY-282834 fixes, following
  `Native~/REBUILD_WINDOWS_LINUX.md` — all gates pass: 25 `RUsd_*` exports incl. the seven added
  after ABI 2 (`RUsd_CreateUsdzPackage` and `RUsd_ReadImportAsset` among them); the DSO-safe
  literals (`SdfAssetPath`, `GfVec2f/3f/4f`) and the 0.7.2 primvar/diagnostic literals
  (`texCoord2f[]`, `float2[]`, `normal3f[]`, `USD import: `, `Failed to write the usdz package`)
  all present, plus the two new refusal literals (`has inconsistent topology`, `resolves outside
  the stage's own folders and was refused`); `Native~/Tests~/import_uv_test` reports `API 5` +
  `PASS`, `usdz_test` `PASS`, and `security_test` **9/9 PASS**; in-Editor `GetRuntimeInfo()` →
  `API 5, OpenUSD 0.26.5` with the digest check passing, and a usdz export/re-import round trip
  keeps its UVs. Toolchain: VS Build Tools 2022 (MSVC 14.44) + Windows SDK 10.0.26100,
  CMake 4.3.3. This rebuild closed two gaps at once: the previous payload was **API 4**, so it
  lagged the 0.7.2 usdz work and tripped the new stale-plugin report, *and* it predated the
  security fixes. The 2026-06-24 build before it was ABI v2 and additionally predated the
  `VtValueHoldsType` texture fix. McUsd was last measured on the 2026-09-11 ABI 4 payload
  (23 meshes / 1760 vertices / 880 triangles, 23/23 UVs and normals, 23/23 `_BaseMap`, 8
  alpha-cutout, 1 alpha-blended, no console warnings); nothing in this rebuild touches that path.
  **Why 26.05:** it is the latest public tag — there is no public `v26.08`. Since 2026-09-23 all
  three platforms use it, so the payloads are now reproducible from the same published commit.
- **Linux payload:** rebuilt against **26.05** (`PXR_VERSION 2605`, binary tag `pxrInternal_v0_26_5`),
  **API 5**, x64, under `Runtime/Plugins/x86_64/Linux/`. **Rebuilt in full on 2026-09-24 from
  `/opt/usd-26.05`** (see *Build location* below) — OpenUSD, oneTBB and the wrapper — from the
  public `v26.05` tag (commit `2095faf`, a fresh full clone, not a copy of an older tree) with the
  same flags as Windows and macOS (`--build-monolithic --no-python --no-imaging --no-usdview
  --no-examples --no-tutorials --no-tests --no-materialx`). The rebuild before it, on 2026-09-22
  with the SECURITY-282834 fixes, had replaced only the wrapper and kept the 2026-06-24
  `libusd_ms.so` / `libtbb.so.2`. All three ELF files are now `strip --strip-unneeded`, the
  wrapper and `libusd_ms.so` carry `$ORIGIN`-only rpaths, and every plugin descriptor under
  `lib/usd` / `plugin/usd` is byte-identical to the previous payload (same source tag), so the
  digest manifest, the SBOM and `Native~/dependency-digests/linux.sha256` each change by exactly
  the three compiled files (86 of the 88 recorded dependency digests are unchanged). Gates, all
  re-run on the 2026-09-24 payload: `ldd` resolves purely through `$ORIGIN`; 25 `RUsd_*`
  exports incl. the seven added after ABI 2; all Gate 5 literals present plus the two refusal
  literals (`has inconsistent topology`, `resolves outside the stage's own folders and was
  refused`); `import_uv_test` → `API 5` + `PASS`, `usdz_test` `PASS`, `security_test` **9/9
  PASS**; the wrapper's four OpenUSD header paths now read `/openusd/include/pxr/...`.
  **Toolchain** (2026-09-24 payload): Ubuntu 24.04.5 LTS (glibc 2.39), g++ 13.3.0
  (`Ubuntu 13.3.0-6ubuntu2~24.04.1`), CMake 3.28.3, GNU Binutils 2.42 (`strip`, `nm`, `objdump`),
  patchelf 0.17.2, Python 3.12.3 for `build_usd.py`; wrapper BuildID
  `b38890c4531608a3ff545befaec094ebca389f71`. Shipped digests: wrapper `75391e9f…9348a`
  (213,000 bytes), `libusd_ms.so` `ba8f9c29…22d73` (43,501,936 bytes), `libtbb.so.2`
  `0147d53d…67400` (277,968 bytes). **Superseded 2026-09-30 by the LZ4 rebuild** (same
  toolchain, OpenUSD from `/opt/usd-26.05-lz4/install`): wrapper unchanged, `libusd_ms.so`
  `708420d8…166a6` (43,514,224 bytes), `libtbb.so.2` `9e3b7de8…9d21f` (277,968 bytes); floor
  still `GLIBC_2.38` / `GLIBCXX_3.4.32`; 0 `/home/` strings, and the 536 source paths in
  `libusd_ms.so` now sit under `/opt/usd-26.05-lz4/`.
  **Build location: `/opt/usd-26.05`, never a home directory.** Same defect and same remedy as
  the macOS entry above: `__FILE__` and `__PRETTY_FUNCTION__` embed the absolute path of what is
  being compiled, so the previous Linux payload — built under `/home/<user>/…` — carried 536
  strings naming the developer's home directory in `libusd_ms.so` and 4 in the wrapper (the
  `__FILE__` of `tf/refPtr.h`, `tf/weakPtrFacade.h`, `usdGeom/xformOp.h`, `usd/object.h`).
  OpenUSD is now cloned and built under `/opt/usd-26.05/{src,install}` (created once with
  `sudo mkdir -p /opt/usd-26.05 && sudo chown $(id -u):$(id -g) /opt/usd-26.05`; not `/tmp`,
  because the path is permanent in the binaries and `/tmp` is not), and the wrapper is compiled
  with `-ffile-prefix-map` (`Native~/CMakeLists.txt`) for the package root → `/usd-toolkit` and
  `OPENUSD_ROOT` → `/openusd`. Measured on the 2026-09-24 payload with
  `strings -a <file> | grep -c '/home/'`: **0 / 0 / 0** for the wrapper, `libusd_ms.so` and
  `libtbb.so.2` (also 0 for `/Users/`). What remains: 536 strings under the neutral
  `/opt/usd-26.05/src/...` root in `libusd_ms.so` (one per source file OpenUSD's macros name; the
  same count as before, now without the user), and in the wrapper only the four `/openusd/include/…`
  headers and nothing under `/usd-toolkit/` — its own sources reach the binary through no such
  macro. **Still present, not fixed here, and not Linux-specific:** the oneTBB build-host stamp — see
  "Build host names in the oneTBB stamp" below. **Why 26.05,
  not 26.08:** OpenUSD has no public `v26.08` tag; 26.05 is functionally equivalent for this
  toolkit (identical decision to the Windows build). **Minimum OS: Ubuntu 24.04 — a decision, not a defect
  (2026-09-23).** The wrapper itself only needs `GLIBC_2.32`, but `libusd_ms.so` is built on
  Ubuntu 24.04 and needs **glibc ≥ 2.38** and **`GLIBCXX_3.4.32`** (GCC 13 libstdc++, which the
  payload does not bundle). Unity 6.3 supports Ubuntu 22.04 as well (glibc 2.35 /
  `GLIBCXX_3.4.30`), so the package is deliberately narrower than the Editor it ships for; that
  is documented in `README.md` and both user manuals, and `CreateNativeLoadException` names the
  requirement so a 22.04 user is not left hunting for a missing file. Nothing in the source needs
  24.04: only three symbols force 2.38 — `__isoc23_strtol`, `fmod`, `fmodf`, all artifacts of
  compiling against 24.04 headers. To lower the floor to 22.04, rebuild OpenUSD and the wrapper
  in an Ubuntu 22.04 (glibc 2.35) container or on that distro directly — no code change. That is
  the Linux counterpart of the macOS deployment target above, and it is still open. The 2026-06-24 payload before this one was ABI v2 and predated the
  UV/normal primvar, opacity, usdz and security work.
  **2026-09-23 확인 — 재빌드 없이, 커밋된 파일만으로.** Wrapper
  `libUnityUSDToolkitNative.so` (sha256 `174665c8…b77c`, 213,000 bytes, BuildID
  `0151726566791fda0e11bc1323237579193166b0`): `nm -D --defined-only | grep -c RUsd_` → **25**, and
  `Native~/include/unity_usd_toolkit_native.h` declares exactly 25, so the export set matches the
  source; `RUsd_GetImportMeshUvSetInfo`, `RUsd_CopyImportMeshUvSet`, `RUsd_GetImportMaterialOpacity`,
  `RUsd_CreateUsdzPackage`, `RUsd_ReadImportAsset` all present. `strings -a`: `SdfAssetPath` ×5,
  `GfVec4f` (both in the mangled `UsdAttribute::_Set<T>` instantiations), `texCoord2f[]` ×1,
  `float2[]` ×1, `normal3f[]` ×1, `USD import: ` ×2, `Failed to write the usdz package` ×2. `ldd`: no
  "not found"; every non-system library resolves through `$ORIGIN:$ORIGIN/lib` (`libusd_ms.so`,
  `libtbb.so.2` from the payload's own `lib/`); DT_NEEDED is `libusd_ms.so`, `libstdc++.so.6`,
  `libgcc_s.so.1`, `libc.so.6`, `ld-linux-x86-64.so.2`. `objdump -T` measured: wrapper needs at most
  **`GLIBC_2.32` / `GLIBCXX_3.4.29` / `CXXABI_1.3.9`**; `lib/libusd_ms.so` **`GLIBC_2.38` /
  `GLIBCXX_3.4.32` / `CXXABI_1.3.11`**; `lib/libtbb.so.2` `GLIBC_2.34` / `GLIBCXX_3.4.21` /
  `CXXABI_1.3.13` — so the payload-wide floor is glibc ≥ 2.38 + `GLIBCXX_3.4.32`, exactly as
  documented above and in the manuals; no discrepancy. **One observation, fixed by the 2026-09-24
  rebuild (see *Build location* above):** `strings` showed four absolute build-machine paths in the wrapper — the `__FILE__`
  of `pxr/base/tf/refPtr.h`, `pxr/base/tf/weakPtrFacade.h`, `pxr/usd/usdGeom/xformOp.h`,
  `pxr/usd/usd/object.h` under the OpenUSD include root, baked in by `TF_AXIOM`/`TF_VERIFY`-style
  macros in those headers (`libusd_ms.so` carries 536 such strings from its own build). The
  `ldd` gate ("no absolute build paths") is about the resolver and still holds; this is a separate,
  low-severity information leak of the build tree path; the 2026-09-24 rebuild removed it with
  `-ffile-prefix-map` and a user-name-free OpenUSD build root. Also confirmed the same day:
  the shipped `lib/libtbb.so.2` is the recorded one after `strip --strip-unneeded` (binutils 2.42),
  which the runbook's §6 strip step now lists (see that commit).

## Build host names in the oneTBB stamp (open, macOS and Linux)

The path work removed 1,682 build-machine paths from the payload. One identifier per TBB library
survives it, because it is not a path: oneTBB's own build writes a version banner through
`build/version_info_<platform>.sh`, which runs `hostname -s`, and that string ships.

| file | stamp |
| --- | --- |
| `Runtime/Plugins/x86_64/Linux/lib/libtbb.so.2` | `TBB: BUILD_HOST beat-Alienware-x16-R2 (x86_64)` |
| `Runtime/Plugins/macOS/libtbb.dylib` | `TBB: BUILD_HOST YoonseokChois-MacBook-Pro-2 (arm64)` |
| `Runtime/Plugins/x86_64/Windows/tbb_usdrt.dll` | `TBB: BUILD_HOST nntpat21-250` |

Both non-Windows stamps identify a person: the Linux host name contains the user account name,
and the macOS one is the default "<owner>'s MacBook Pro" form, which carries a full name. The
Windows stamp is a neutral machine name. This is not a product of the 2026-09 rebuilds — the same
stamp is in the 2026-06-24 payload — and it is now the only place a developer's name appears in
any shipped binary.

Not fixed because the remedy is a change to the dependency build rather than ours: oneTBB has to
be built under a neutral host name, and the stamp is written at TBB build time, so that means
another OpenUSD/TBB rebuild on each machine for one string per file. Deferred to the next rebuild
of each platform rather than done on its own. Check with
`strings -a <tbb library> | grep -A1 BUILD_HOST`.

## What changed this cycle (already in the macOS build)

### Native — `Native~/src/UsdExporter.cpp`, `Native~/include/unity_usd_toolkit_native.h`
- **Import texture extraction** (`ExtractPreviewMaterial`): follows each `UsdPreviewSurface`
  input connection to its `UsdUVTexture` and reads the `file` asset path
  (albedo/normal/metallic/emissive), the `UsdTransform2d` UV scale/translation, and the
  constant `emissiveColor`.
- **DSO-safe `VtValue` type checks** (`VtValueHoldsType`): compares the registered type *name*
  instead of `VtValue::IsHolding<T>()`. `IsHolding<T>()` / `TfType::Find<T>()` rely on `typeid`,
  which does **not** match across the plugin ↔ monolithic-OpenUSD boundary because the plugin is
  built with `CXX_VISIBILITY_PRESET hidden` (`Native~/CMakeLists.txt`). **Windows and Linux will
  hit the exact same bug if `IsHolding<pxrType>()` is reintroduced** — keep using
  `VtValueHoldsType(value, "TypeName")` + `UncheckedGet<T>()`. (Builtin `float`/`double` are fine.)
- **ABI v2 additions:**
  - Transform node table: `RUsd_GetImportNodeCount` / `RUsd_GetImportNodeInfo` — every
    `UsdGeomXformable` prim's local matrix; the importer no longer bakes world transforms into
    mesh vertices (`BuildImportedMesh` keeps points/normals mesh-local).
  - `UsdGeomSubset` multi-material: `RUsd_GetImportSubmeshCount` / `RUsd_CopyImportSubmeshes`
    (`BuildImportedSubmeshes` groups triangles by `materialBind`-family subset).
  - `kApiVersion = 2`.
- **ABI v3 additions:**
  - Extra UV sets: `RUsd_GetImportMeshUvSetInfo` / `RUsd_CopyImportMeshUvSet`
    (`RUSD_MAX_UV_SETS` = 3; set 0 still ships inside `RUsd_CopyImportMesh`).
  - `kApiVersion = 3`. Purely additive — see the minimum-version note under *Current state*.
- **ABI v4 additions:**
  - Authored opacity: `RUsd_GetImportMaterialOpacity` — the texture whose alpha drives
    transparency (empty when `opacity` is not connected) plus `opacityThreshold`
    (`ExtractOpacityInfo`). `kApiVersion = 4`, also purely additive.
- **ABI v5 additions (usdz):**
  - Packaging: `RUsd_CreateUsdzPackage` wraps `UsdUtilsCreateNewUsdzPackage` /
    `UsdUtilsCreateNewARKitUsdzPackage`. usdz cannot be written as a stage (`SdfUsdzFileFormat`
    is `supportsWriting: false`), so `UsdExporter` writes a `.usdc` into a temp staging directory
    and packages that; `editLayersInPlace` stays false so source layers on disk are never
    rewritten, and the call runs after `RUsd_EndExport` has released the stage, as OpenUSD asks.
  - Asset reads: `RUsd_ReadImportAsset` resolves an authored asset path and reads its bytes
    through `ArResolver::OpenAsset`, which is the only way to reach a texture *inside* a package.
    It prefers the resolution USD already performed for that path (recorded per context in
    `assetResolutions` while reading materials, so per-layer anchoring is exact) and otherwise
    anchors on the root layer with `SdfComputeAssetPathRelativeToLayer`.
  - Both come from the monolithic OpenUSD library already shipped on **every** platform
    (`UsdSkel`/`UsdUtils` symbols and the `usdz` file format + package resolver are all present in
    the macOS, Windows and Linux payloads), so this needed **no OpenUSD rebuild** — only the
    wrapper. Non-monolithic builds need `usdUtils` and `ar` in the link list (already added to
    `Native~/CMakeLists.txt`).
  - `kApiVersion = 5`, purely additive.
  - Coverage: `Native~/Tests~/usdz_test.cpp` (self-contained — it writes its own usda + PNG, so
    there is no binary fixture in the repo).

### Managed (C#) — platform-agnostic, ships as-is (no per-platform rebuild)
- `UsdImporter`: PBR texture binding, Xform hierarchy reconstruction (USD→Unity basis flip:
  `M_unity[i,j] = sign(i)·sign(j)·M_usd[j][i]`, sign(0) = −1), submesh→material array, true async
  (`ReadStageData` on a worker thread + main-thread build), per-import texture **dedup cache**,
  **frame-sliced** build with progress, and an off-main-thread managed PNG decoder
  (`UsdPngDecoder`).
- `UsdImportOptions`: `ImportTextures`, `MaxMillisecondsPerFrame`, `ProgressCallback`.

See `CHANGELOG.md` (`0.4.0`–`0.6.0`) for the full per-feature list.

## Rebuild for Windows (x64)

Run from an **x64 Native Tools Command Prompt for Visual Studio**.

1. Build OpenUSD monolithic (once). Every shipped payload uses **`v26.05`**, the latest public
   tag (there is no public `v26.08`). Do not substitute another tag — matching, publicly pinned
   versions across all three platforms are what make the payloads reproducible, which is the
   point of SECURITY-282834's provenance finding.
   ```bat
   git clone https://github.com/PixarAnimationStudios/OpenUSD.git C:\Dev\OpenUSD
   cd C:\Dev\OpenUSD
   git checkout v26.05
   python <pkg>\Native~\build_openusd.py --platform windows ^
     --openusd-src C:\Dev\OpenUSD --install C:\USD\OpenUSD-26.05-win-x64 --require-scan
   ```
   Requires Python 3.x on PATH plus CMake and an x64 MSVC toolset. Only external dep is TBB,
   which `build_openusd.py` checks against the pin before `build_usd.py` runs.
2. Build this package's native wrapper:
   ```powershell
   cd <pkg>\Native~
   .\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
   ```
3. Expected payload in `Runtime/Plugins/x86_64/Windows/`:
   `UnityUSDToolkitNative.dll`, `usd_rt.dll`, `tbb_usdrt.dll`, `lib/usd/**/plugInfo.json`,
   `plugin/usd/plugInfo.json`.

## Rebuild for Linux (x64)

The shipped Linux payload is the **monolithic + CMake** build (the same `usd_ms` link path as
Windows/macOS), and `build_linux.sh` is now the script that produces it. It used to target the
packman component + Python layout (Isaac/Omniverse) and leave the shipped build to manual
commands, which is how the Linux path came to have no provenance check; it no longer does.

1. Build OpenUSD monolithic (public `v26.05` — there is no public `v26.08`) outside the home
   directory, since its paths are embedded in the library:
   ```bash
   git clone --branch v26.05 \
     https://github.com/PixarAnimationStudios/OpenUSD.git /opt/usd-26.05/src
   python3 Native~/build_openusd.py --platform linux \
     --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05/install --require-scan
   ```
   Needs CMake, g++ (C++17), and a Python 3.x with `setuptools` to run `build_usd.py`. Only
   external dep is TBB, which `build_openusd.py` checks against the pin before the build.
2. Build and assemble the **self-contained** payload under `Runtime/Plugins/x86_64/Linux/`:
   ```bash
   Native~/build_linux.sh --openusd-root /opt/usd-26.05/install
   ```
   - `libUnityUSDToolkitNative.so` at the root (rpath `$ORIGIN:$ORIGIN/lib`).
   - `lib/libusd_ms.so` (rpath `$ORIGIN`) + `lib/libtbb.so.2`.
   - USD schema plugins under `lib/usd` (without `usd/resources/codegenTemplates`) and shader
     plugins under `plugin/usd`.
   The script checks the stamp and the dependency digests first, sets the rpaths with
   `patchelf`, runs `strip --strip-unneeded` (keeps the `RUsd_*` dynamic exports), and fails if
   `ldd` shows "not found" or a binary contains a `/home/` path. The C# layer sets
   `PXR_PLUGINPATH_NAME` to `lib/usd` + `plugin/usd` at runtime.

## Verify (per platform)

1. Package compiles; `read_console` has no errors.
2. `Unity.USDToolkit.UsdExporter.GetRuntimeInfo()` reports **`API 2, OpenUSD 0.26.X`** (macOS
   `0.26.8`, Windows `0.26.5`). The `API 2` part is what must match across platforms.
3. Round-trip: export a cube, then `UsdImporter.GetPreviewInfo` / `Import`.
4. Export with `TransformPolicy.PreserveHierarchy` + a 2-submesh / 2-material mesh, import, and
   confirm the hierarchy transforms and submesh materials round-trip.
5. `UsdImporter.ImportAsync` on a large textured scene stays responsive (no multi-second freeze).

## Notes
- macOS caches a loaded native dylib for the Editor session — restart the Editor to reload a
  rebuilt plugin. Windows/Linux behave similarly in a running player.
- The OpenUSD install and native build caches live under `Build~/` and `Native~/build~/`, which
  are git-ignored. The macOS install used here is OpenUSD 26.05 universal, built from
  `Build~/src-2605/OpenUSD-dev` (a shallow clone of the public `v26.05` tag, commit `2095faf`).
  The older 26.08 install and its source tree are kept alongside it as the provenance record of
  the payload shipped before 2026-09-23.
