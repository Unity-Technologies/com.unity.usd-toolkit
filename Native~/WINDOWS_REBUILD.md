# Windows Native Rebuild — Runbook

> **Superseded for 0.7.2:** to rebuild against the current source (native **ABI 4** — the UV,
> normal, UV-set, opacity and import-warning work), follow **`REBUILD_WINDOWS_LINUX.md`**, which
> covers Windows *and* Linux and folds in the gates below. This runbook remains as the record of
> the stale-DLL texture bug and its diagnosis.

**Task:** rebuild `Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll` from the committed
source, because the DLL currently in the repository was **not** built from it.

This is written to be executed step by step. Every step that can go wrong has an explicit
pass/fail gate. **Do not skip the gates, and stop where the runbook says stop.**

Scope: **Windows only.** macOS and Linux payloads are correct and must not be touched.

> **Update (2026-09-11, package 0.7.2):** the Windows payload now needs a rebuild for a second,
> independent reason — the import-side UV/normal primvar fix and ABI 3 (extra UV sets). Both
> Windows **and Linux** are stale: they still report ABI 2, so they load and import (the managed
> version gate is a minimum), but every mesh with `faceVarying` or indexed UVs imports with **no
> UVs** and `primvars:normals` is ignored. One rebuild per platform from the current source fixes
> both this and the stale-DLL texture bug below. After rebuilding, gate 6 should report
> `API 3, OpenUSD 0.26.5`, and `Native~/Tests~/import_uv_test.cpp` is the direct check (it
> asserts the split-vertex and multi-set behaviour; expects 8 meshes, prints `PASS`).

---

## 1. Background — what is broken and why

Users report that importing a USD with textures yields untextured materials showing only the
flat `diffuseColor` constant. The native layer returns **empty texture paths**, so the managed
pipeline never attempts to load anything and logs no warning.

The cause is a known trap already documented in `BUILD_NOTES.md`:

> **Windows and Linux will hit the exact same bug if `IsHolding<pxrType>()` is reintroduced** —
> keep using `VtValueHoldsType(value, "TypeName")` + `UncheckedGet<T>()`.

`VtValue::IsHolding<T>()` compares `typeid`, which does **not** match across the
plugin ↔ monolithic-OpenUSD boundary because the wrapper is built with
`CXX_VISIBILITY_PRESET hidden`. It returns `false` silently — no error, no diagnostic — so the
texture path is simply never read.

The committed source uses the correct `VtValueHoldsType(...)` form. The shipped Windows DLL
does not contain the string literals that form requires, while the macOS and Linux binaries do:

| literal (standalone string) | Windows `.dll` | macOS `.dylib` | Linux `.so` |
| --- | :--: | :--: | :--: |
| `SdfAssetPath` | **absent** | present | present |
| `GfVec2f` | **absent** | present | present |
| `GfVec3f` | **absent** | present | present |
| `GfVec4f` | **absent** | present | present |

Those four are exactly — and only — the type-name arguments passed to `VtValueHoldsType` in
`Native~/src/UsdExporter.cpp`. All four missing together is not a coincidence: the DLL was built
from a tree that predates the fix, most likely a **stale checkout or a stale incremental build
cache** on the Windows machine. The DLL's ABI is otherwise current (all 20 `RUsd_*` exports
present, ABI v2), which is why nothing else looked wrong.

**Therefore: a clean rebuild from current `main` is the fix.** No source change is needed.
The ABI stays at v2, so no version bump and no other platform is affected.

---

## 2. Prerequisites

- **x64 Native Tools Command Prompt for Visual Studio 2022** (the build script assumes the
  `Visual Studio 17 2022` generator).
- CMake on `PATH`.
- `git` and **`git-lfs`** (the payload binaries are LFS-tracked; a clone without LFS gives you
  pointer files, not DLLs).
- An **OpenUSD 26.05** monolithic install. Reuse the existing one if this machine already built
  it — see step 4.

---

## 3. Get the repository

```powershell
git clone https://github.cds.internal.unity3d.com/unity/com.unity.usd-toolkit C:\Dev\com.unity.usd-toolkit
cd C:\Dev\com.unity.usd-toolkit
git lfs pull
git switch main
git log --oneline -1
```

If the repo is already cloned here, **update it instead of reusing a possibly stale tree** —
a stale tree is the suspected cause of this whole problem:

```powershell
cd C:\Dev\com.unity.usd-toolkit
git switch main
git fetch origin
git reset --hard origin/main
git lfs pull
git status --porcelain     # must print nothing
```

**Gate 3 — the source must contain the fix.** Run:

```powershell
Select-String -Path .\Native~\src\UsdExporter.cpp -Pattern 'VtValueHoldsType\(' | Measure-Object
```

**Expect `Count` ≥ 7** (one definition + six call sites). If it is 0, you are on the wrong
commit — **stop** and report.

---

## 4. OpenUSD 26.05

The shipped Windows payload is built against **OpenUSD v26.05** (`pxrInternal_v0_26_5`). Use the
**same version**. Do not "upgrade" to 26.08 as part of this task: that would change every
OpenUSD DLL in the payload and turn a one-file fix into a full payload swap.

An existing `C:\USD\OpenUSD-26.05-win-x64` can be reused only if `Native~/build_openusd.py` built
it — the wrapper build refuses an install without that script's stamp. Otherwise build it once,
into a new directory:

```bat
git clone https://github.com/PixarAnimationStudios/OpenUSD.git C:\Dev\OpenUSD
cd C:\Dev\OpenUSD
git checkout v26.05
python <package>\Native~\build_openusd.py --platform windows ^
  --openusd-src C:\Dev\OpenUSD --install C:\USD\OpenUSD-26.05-win-x64 --require-scan
```

Do not run `build_usd.py` directly: it compiles the TBB it downloads without checking it (see
`REBUILD_WINDOWS_LINUX.md` §5).

---

## 5. Clean build — this is the critical step

A stale incremental build is the leading hypothesis for the bad DLL, so **delete the build
directory first**. Do not skip this.

```powershell
cd C:\Dev\com.unity.usd-toolkit
Remove-Item -Recurse -Force .\Native~\build~ -ErrorAction SilentlyContinue
cd .\Native~
.\build_windows.ps1 -OpenUsdRoot C:\USD\OpenUSD-26.05-win-x64
```

The script builds the wrapper, installs it plus the OpenUSD runtime into
`Runtime\Plugins\x86_64\Windows\`, strips `.lib` / `.exp` / `.pdb`, and renames the OpenUSD
monolithic `usd_ms.dll` → `usd_rt.dll` (patching the wrapper's import table in place).

**Gate 5 — the build must report the rename.** Look for:

```
Renamed OpenUSD monolithic usd_ms.dll -> usd_rt.dll and patched UnityUSDToolkitNative import.
```

If that line is missing, `usd_ms.dll` was not found in the install dir. The payload would then
collide with `com.unity.pixyz.sdk-plus` at runtime. **Stop** and report.

---

## 6. Gate 6 — verify the rebuilt DLL actually contains the fix

**This is the gate that decides whether the rebuild worked.** It is a direct check for the
four literals the correct code path requires, as standalone NUL-terminated strings (a plain
substring search would also match MSVC's mangled symbol names, which are present either way).

```powershell
cd C:\Dev\com.unity.usd-toolkit
$dll = ".\Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll"
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll))
foreach ($t in 'SdfAssetPath','GfVec2f','GfVec3f','GfVec4f') {
    $hit = [regex]::Matches($text, "(?<![A-Za-z0-9_])$t\x00").Count
    "{0,-14} {1}" -f $t, $(if ($hit -gt 0) { "PRESENT ($hit)" } else { "*** MISSING ***" })
}
```

**Expected — all four PRESENT:**

```
SdfAssetPath   PRESENT (1)
GfVec2f        PRESENT (1)
GfVec3f        PRESENT (1)
GfVec4f        PRESENT (1)
```

Counts above 1 are fine (the compiler may or may not pool duplicate literals).

**If any is MISSING, the rebuild did not pick up the current source. Stop.** Do not commit.
Re-check gate 3, confirm `Native~\build~` was really deleted, and report what you find.

Useful: run this same command **before** step 5 on the DLL currently in the repo. It should show
all four MISSING — that reproduces the diagnosis and proves the gate discriminates.

---

## 7. Gate 7 — functional verification

Prove the user-facing symptom is gone.

1. Get the reported test asset — the **whole** repository, not just the `.usda`; the textures
   live beside it and are required:

   ```powershell
   git clone https://github.com/erich666/McUsd.git C:\Dev\McUsd
   ```

   The file is `C:\Dev\McUsd\model\McUsd.usda` (23 materials, 75 `UsdUVTexture` references,
   76 PNGs under `model\McUsd_materials\tex\`).

2. Open a Unity project with this package embedded. **Restart the Editor** if it was already
   open — a loaded native DLL stays loaded for the session and you would test the old binary.

3. Import it:

   ```csharp
   var result = await UsdImporter.ImportAsync(@"C:\Dev\McUsd\model\McUsd.usda",
       new UsdImportOptions { ImportMaterials = true, ImportTextures = true });
   ```

**Expected:** imported objects are textured. Materials have `_BaseMap` and `_BumpMap` populated,
not a flat colour.

**Also check the Console.** `USD import: texture not found, skipping: <path>` means the paths are
now being read but the files are missing — a *different*, lesser problem (wrong asset layout),
and it still confirms the native fix worked.

Before the fix the failure is silent: flat colour, no warning at all.

---

## 8. Gate 8 — review what changed before committing

**Only `UnityUSDToolkitNative.dll` should differ.** Everything else in the payload comes from
OpenUSD and must be byte-identical, because you rebuilt against the same OpenUSD 26.05.

```powershell
cd C:\Dev\com.unity.usd-toolkit
git status --porcelain
```

**Expected — exactly one line:**

```
 M Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll
```

**Stop and report if you see any of these:**

- **Any `.meta` file modified, added, or deleted.** Unity `.meta` files carry asset GUIDs.
  Committing regenerated GUIDs breaks every scene and prefab referencing those assets for
  everyone. Never let a `.meta` change ride along with a binary rebuild. Restore them:
  `git checkout -- "*.meta"`
- **Other OpenUSD DLLs modified** (`usd_rt.dll`, `tbb_usdrt.dll`, …), or files under `lib/usd`,
  `plugin/`, `share/`, `resources/` added or removed. That means you built against a *different*
  OpenUSD version than the payload — go back to step 4.
- **`usd_ms.dll` present** alongside or instead of `usd_rt.dll` — the rename did not happen;
  see gate 5.
- Any `.lib`, `.exp`, or `.pdb` files — the script should have removed them.

Confirm the changed file is real content and not an LFS pointer:

```powershell
git lfs status
(Get-Item .\Runtime\Plugins\x86_64\Windows\UnityUSDToolkitNative.dll).Length   # expect ~150-200 KB
```

---

## 9. Commit and push

Use a branch; do not push to `main` directly (another branch, `add_usd_sync`, is active work).

```powershell
git switch -c fix/windows-native-rebuild
git add Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll
git commit -F -
```

Suggested message:

```
fix(native/windows): rebuild wrapper from current source (restores texture import)

The shipped Windows DLL was not built from the committed source: it lacked the
"SdfAssetPath" / "GfVec2f" / "GfVec3f" / "GfVec4f" literals that VtValueHoldsType
passes, i.e. it still used IsHolding<T>(). That comparison comes out false across
the plugin <-> monolithic-OpenUSD boundary under hidden visibility, so
ReadConnectedTextureFile bailed out and every texture path came back empty --
silently, with no diagnostic. Imported materials showed only the flat diffuseColor
constant. macOS and Linux were built from the correct source and were unaffected.

Clean rebuild from main against the same OpenUSD 26.05, so only the wrapper DLL
changes. ABI stays at v2; no managed change and no version bump.

Verified: the four literals are present in the rebuilt DLL, and McUsd.usda
(23 materials / 75 UsdUVTexture refs) imports textured in the Editor.
```

```powershell
git push -u origin fix/windows-native-rebuild
```

Then open a PR and link it to the user's report.

---

## 10. Report back

Include:

- Gate 6 output **before** and **after** the rebuild (the four literals).
- Gate 7 result — textured or not, and any Console warnings.
- Gate 8 `git status --porcelain` output.
- `UsdExporter.GetRuntimeInfo()` — expect `API 2, OpenUSD 0.26.5`.
- Whether the OpenUSD install was reused or rebuilt, and its path.

---

## Appendix A — native-only check, without Unity

Useful to isolate the native layer from the managed pipeline, and it avoids the Editor's
DLL-caching-per-session behaviour. Loads the DLL dynamically, so it needs no import library
(the build script deletes `.lib`).

Save as `Native~\build~\test_mat.c`:

```c
#include "unity_usd_toolkit_native.h"
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef int (*fnOpen)(const char*, int, void**);
typedef int (*fnInfo)(void*, char*, int, double*, int*, int*, int*);
typedef int (*fnMat)(void*, int, RUsdMaterial*, char*, int);

int main(int argc, char** argv)
{
    HMODULE h = LoadLibraryA("UnityUSDToolkitNative.dll");
    if (!h) { printf("LoadLibrary failed: %lu\n", GetLastError()); return 1; }
    fnOpen open = (fnOpen)GetProcAddress(h, "RUsd_OpenStage");
    fnInfo info = (fnInfo)GetProcAddress(h, "RUsd_GetImportInfo");
    fnMat  mat  = (fnMat) GetProcAddress(h, "RUsd_GetImportMaterial");

    void* ctx = NULL;
    if (open(argv[1], 1, &ctx) != 0) { printf("OpenStage failed\n"); return 1; }

    char prim[512]; double mpu; int up, meshes, mats;
    info(ctx, prim, sizeof(prim), &mpu, &up, &meshes, &mats);
    printf("meshes=%d materials=%d\n", meshes, mats);

    int withAlbedo = 0;
    for (int i = 0; i < mats; i++) {
        RUsdMaterial m; char name[256];
        memset(&m, 0, sizeof(m));
        if (mat(ctx, i, &m, name, sizeof(name)) != 0) continue;
        if (m.albedoTexturePath[0]) withAlbedo++;
        if (i < 3) printf("  [%d] %-20s albedo=\"%s\"\n", i, name, m.albedoTexturePath);
    }
    printf("==> %d/%d materials have an albedo path\n", withAlbedo, mats);
    return 0;
}
```

Build and run from the payload folder (so the DLLs resolve):

```powershell
cd .\Runtime\Plugins\x86_64\Windows
cl /nologo /I ..\..\..\..\Native~\include ..\..\..\..\Native~\build~\test_mat.c /Fe:test_mat.exe
$env:PXR_PLUGINPATH_NAME = "$PWD\lib\usd;$PWD\plugin\usd"
.\test_mat.exe C:\Dev\McUsd\model\McUsd.usda
Remove-Item test_mat.exe, test_mat.obj
```

**Expected after the fix** (this is the macOS result, which the Windows build should match):

```
meshes=23 materials=23
  [0] /McUsd/Looks/grass_block_top  albedo="./McUsd_materials/tex/grass_block_top_y.png"
  [1] /McUsd/Looks/dirt             albedo="./McUsd_materials/tex/dirt.png"
==> 23/23 materials have an albedo path
```

**Before the fix:** `==> 0/23`.

Delete `test_mat.exe` when done — do not commit build artefacts into the payload folder.

---

## Appendix B — troubleshooting

**`DllNotFound` / `PROC_NOT_FOUND` when Unity loads the plugin.** The `usd_ms` → `usd_rt` rename
did not apply, and another package's OpenUSD (Pixyz) won the base-name race. Check gate 5.

**The Editor still shows untextured materials after a successful gate 6.** Unity keeps a loaded
native DLL for the session — restart the Editor. If it persists, run appendix A, which bypasses
Unity entirely and tells you whether the problem is native or managed.

**`git status` shows the whole payload changed.** You built against a different OpenUSD version.
Check that `-OpenUsdRoot` points at a **26.05** install:
`(Get-Content <root>\include\pxr\pxr.h | Select-String 'PXR_VERSION')` should show `2605`.

**The build succeeds but gate 6 still fails.** `Native~\build~` was not actually removed, or
CMake reused a cached configuration. Delete the whole directory and re-run; if it still fails,
confirm `Native~\src\UsdExporter.cpp` on disk contains `VtValueHoldsType` (gate 3) — you may be
building a different working copy than the one you inspected.

---

## Appendix C — why this was not caught

Nothing verifies that a committed binary was built from the committed source. The gate-6 literal
check is cheap and catches exactly this class of failure; consider adding it to the release
checklist in `BUILD_NOTES.md` for all three platforms. The same check run across the three
current binaries is what identified this bug.
