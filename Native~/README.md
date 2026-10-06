# Building the Native Plugin (`UnityUSDToolkitNative`)

The toolkit calls a native OpenUSD wrapper through P/Invoke. Each desktop platform needs
its own binary, and **all platforms must be rebuilt whenever the native ABI changes** —
for example when fields are added to `RUsdMaterial` in
`include/unity_usd_toolkit_native.h` (as in the 0.3.0 texture work).

| Platform | Output | Build on |
|---|---|---|
| Windows x64 | `Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll` | Windows |
| macOS | `Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib` | macOS |
| Linux x64 | `Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so` | Linux |

> ⚠️ **Native binaries are not cross-buildable.** Build each platform on that OS.
> The Linux binary shipped here was built on Linux; the **Windows and macOS binaries
> must be rebuilt on their own machines** after any ABI change, or they will crash /
> marshal garbage against the newer C# `RUsdMaterial` layout.

## ⚠️ Read first: rebuild Windows & macOS after cloning this repo

The **Linux** native payload in this repo is current. The **Windows (`.dll`) and
macOS (`.dylib`)** payloads were last built before the 0.3.0 texture work, which
**changed the `RUsdMaterial` native ABI** (added texture-path fields). If you run
the toolkit on Windows or macOS with those stale binaries, the C#↔native struct
layout will not match and export will crash or marshal garbage.

> The recent metallic-export fix is **C#-only** (no ABI change) — but you still
> need Windows/macOS binaries built against the **0.3.0 ABI**, so rebuild once per
> OS as below. After that, future C#-only fixes need no native rebuild.

### Build on a fresh machine

The native binary cannot be cross-compiled — build Windows on Windows, macOS on
macOS. Each is a two-step process: build OpenUSD once, then build this wrapper.

OpenUSD is always built with `Native~/build_openusd.py`, never with `build_usd.py` directly:
it checks the pinned TBB archive *before* `build_usd.py` compiles it and stamps the install,
and every wrapper build (the three scripts below and `CMakeLists.txt` itself) refuses an
install without that stamp. See `REBUILD_WINDOWS_LINUX.md` §5.

**Windows (PowerShell, x64 Native Tools Command Prompt for VS):**
1. Build OpenUSD 26.05 monolithic from a `v26.05` clone into a new directory:
   ```powershell
   python Native~\build_openusd.py --platform windows --openusd-src C:\Dev\OpenUSD `
       --install C:\USD\OpenUSD-26.05-win-x64 --require-scan
   ```
2. From the package's `Native~/` folder:
   ```powershell
   .\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
   ```
   This installs `UnityUSDToolkitNative.dll` + the OpenUSD runtime/schema payload
   into `Runtime/Plugins/x86_64/Windows/`.
3. Restart the Unity Editor (native plugins are not hot-reloaded).
4. Verify: Play mode -> export with `UsdExportExample` -> `usdchecker` the output.

**macOS:**
1. Build OpenUSD (monolithic, universal) from a `v26.05` clone into a new directory:
   ```bash
   python3 Native~/build_openusd.py --platform macos --openusd-src <clone> \
       --install /Users/Shared/usd-26.05/install --build-target universal --require-scan
   ```
   It sets `MACOSX_DEPLOYMENT_TARGET=12.0` (`--deployment-target`). Nothing in OpenUSD's build
   sets a deployment target, so its dylibs otherwise take the SDK default — the build machine's
   own macOS version — and dyld refuses them on anything older. 12.0 is Unity 6.3's minimum for
   a macOS player. The wrapper's own `--deployment-target` does not reach them.
2. From the package root:
   ```bash
   ./Native~/build_macos.sh --openusd-root /Users/Shared/usd-26.05/install --arch universal \
       --codesign-id "<Developer ID>"
   ```
   Installs `UnityUSDToolkitNative.dylib` + payload into `Runtime/Plugins/macOS/`.
3. Restart the Unity Editor.
4. Verify export as above; `otool -L` the dylib to confirm `@loader_path` RPATHs, and
   `otool -l <dylib> | grep -A2 LC_BUILD_VERSION` to confirm `minos 12.0` on both slices.

After rebuilding, commit the updated `Runtime/Plugins/x86_64/Windows/`,
`Runtime/Plugins/x86_64/Linux/` or `Runtime/Plugins/macOS/` payload (the large binaries are
tracked with Git LFS — see `.gitattributes`).

## Common requirements
- A monolithic (`usd_ms`), `--no-python` OpenUSD 26.05 install built by `build_openusd.py`.
- CMake, a C++17 compiler, and Python 3 (the configure step runs the provenance check).

---

## Linux

```bash
Native~/build_linux.sh --openusd-root /opt/usd-26.05/install
```

This is the path the shipped Linux payload is built with. The script checks the install's
provenance stamp and the dependency digests, builds with CMake in a clean
`Native~/build~/linux-x64`, assembles `libUnityUSDToolkitNative.so`, `lib/libusd_ms.so`,
`lib/libtbb.so.2`, `lib/usd` and `plugin/usd` under `Runtime/Plugins/x86_64/Linux/`, sets the
`$ORIGIN` rpaths with `patchelf`, strips the binaries, and fails on an unresolved `ldd` entry or
a `/home/` path in any of them. Needs `cmake`, `patchelf`, `binutils` and `python3`.

It no longer builds the packman + Python layout (Isaac Sim / Omniverse) it used to target;
`--python-root` is refused with a pointer here.

Notes:
- GPU Resident Drawer / Vulkan are **not** needed to build or run the exporter; they were
  just used in testing. Export works on any graphics backend (and even headless if the
  source meshes have Read/Write enabled).

---

## Windows

Run on Windows (PowerShell). This uses the CMake project (`CMakeLists.txt`):

```powershell
Native~/build_windows.ps1 -OpenUsdRoot C:\path\to\openusd
```

## macOS

Run on macOS:

```bash
Native~/build_macos.sh --openusd-root /path/to/openusd
```

---

## After building (all platforms)

Restart the Unity Editor. Unity does **not** hot-reload an already-loaded native plugin, so
a rebuilt `.so` / `.dll` / `.dylib` — especially after an ABI change — only takes effect on
a fresh editor launch.

Then:

1. Run `python3 Native~/generate_native_hashes.py` to regenerate the digest manifest.
2. Run `Native~/Tests~/security_test.cpp` to confirm the security fixes are in the binary.
3. Check that no `.meta` file was deleted. Unity regenerates a missing `.meta` with a new GUID, which breaks references.

## Validation checklist

1. Confirm `UnityUSDToolkitNative` exists in the platform folder under `Runtime/Plugins/`.
2. Confirm the same folder contains the OpenUSD libraries, `plugin/usd/plugInfo.json` and `lib/usd/plugInfo.json`.
3. Open Unity on the target platform and check that the package compiles without errors.
4. In Play mode, export a Cube or readable mesh with the Export Example sample.
5. Run `usdchecker <file>` or `usdcat <file>` from the same OpenUSD install.
6. Build a Windows x64 or macOS player and repeat the export in the built player. If you ship IL2CPP, test an IL2CPP player separately.
7. Set an invalid `PluginSearchPath` and confirm the exception includes the missing directory and the plugin search diagnostics.
8. Export a multi-submesh mesh and confirm the `GeomSubset` material bindings with `usdcat`.
9. Export a mesh with more than 65k vertices and run `usdchecker` on the result.
10. Export with `TransformPolicy = UsdTransformPolicy.PreserveHierarchy` and confirm nested `Xform` prims and `xformOp:transform` entries with `usdcat`.
11. Import the exported file back (round trip).

## Release checklist

- The package id is `com.unity.usd-toolkit` and the version is correct.
- Each platform's native payload is a real binary, not a Git LFS pointer.
- `python3 Native~/generate_native_hashes.py` produces no diff. A diff means the manifest is stale.
- `plugin/usd/plugInfo.json` and `lib/usd/plugInfo.json` are included for every platform.
- The validation checklist above passes on every platform.
