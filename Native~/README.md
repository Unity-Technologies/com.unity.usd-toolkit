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

### Runbook for a fresh machine (e.g. open Claude Code on that OS and follow this)

The native binary cannot be cross-compiled — build Windows on Windows, macOS on
macOS. Each is a two-step process: build OpenUSD once, then build this wrapper.

**Windows (PowerShell, x64 Native Tools Command Prompt for VS):**
1. Build OpenUSD 26.05 monolithic — see the exact `build_usd.py` command in the
   repo-root `README.md` ("Build Native Runtime -> Windows"). Install to e.g.
   `C:\USD\OpenUSD-26.05-win-x64`.
2. From the package's `Native~/` folder:
   ```powershell
   .\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
   ```
   This installs `UnityUSDToolkitNative.dll` + the OpenUSD runtime/schema payload
   into `Runtime/Plugins/x86_64/Windows/`.
3. Restart the Unity Editor (native plugins are not hot-reloaded).
4. Verify: Play mode -> export with `UsdExportExample` -> `usdchecker` the output.

**macOS:**
1. Build OpenUSD (monolithic, universal) — see repo-root `README.md`
   ("Build Native Runtime -> macOS"), e.g. via `Build~/build_openusd_macos.sh`.
2. From the package root:
   ```bash
   ./Native~/build_macos.sh --openusd-root "<OpenUSD install>" --arch universal
   ```
   Installs `UnityUSDToolkitNative.dylib` + payload into `Runtime/Plugins/macOS/`.
3. Restart the Unity Editor.
4. Verify export as above; `otool -L` the dylib to confirm `@loader_path` RPATHs.

After rebuilding, commit the updated `Runtime/Plugins/x86_64/Windows/` or
`Runtime/Plugins/macOS/` payload (the large `.dll`/`.dylib` are tracked with Git
LFS — see `.gitattributes`).

## Common requirements
- An OpenUSD build (headers + libraries). Both monolithic (`usd_ms`) and the component
  layout (`libusd_usd.so`, `libusd_usdGeom.so`, …) are supported.
- A C++17 compiler.
- If the OpenUSD build is Python-enabled, matching Python dev headers/libs (its headers
  pull in `pyconfig.h`).

---

## Linux

```bash
Native~/build_linux.sh \
  --openusd-root /path/to/openusd \
  --python-root  /path/to/python3.11
```

Defaults point at the packman OpenUSD/Python that Isaac Sim / Omniverse download
(`~/.cache/packman/chk/usd.py311.manylinux_2_35_x86_64...` and the matching
`python/3.11...`), so on a machine that already has Isaac Sim you can often just run:

```bash
Native~/build_linux.sh
```

What the script does:
1. **Compiles** `libUnityUSDToolkitNative.so`, linking the OpenUSD component libs directly
   (`find_package` is bypassed) with an `$ORIGIN/lib` rpath. `--disable-new-dtags` is used
   so the rpath resolves transitive dependencies (e.g. `libpython` needed by `libboost_python`).
2. **Copies the resolved `.so` dependency closure** into `Runtime/Plugins/x86_64/Linux/lib/`.
3. **Copies the USD schema plugins** (`lib/usd`) so the runtime can load `UsdGeom` /
   `UsdShade` / `Sdf` at export time.

The result is **self-contained via `$ORIGIN/lib`** — no `LD_LIBRARY_PATH` is needed at
runtime. (`libpython` lives next to `libboost_python` in the same flat `lib/` folder, which
is why the cross-directory dependency that breaks under packman's own layout works here.)

Notes:
- The packman OpenUSD build is **Python-enabled**, so `--python-root` (Python 3.11 to match
  the build) is required even though we never call Python ourselves.
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
