This folder is populated by `Native~/build_windows.ps1`.

Expected runtime layout:

- `UnityUSDToolkitNative.dll`
- OpenUSD monolithic runtime DLL `usd_rt.dll` (renamed from OpenUSD's `usd_ms.dll` by the
  build script to avoid base-name collisions with other packages that bundle OpenUSD), or
  component `usd_*.dll` files
- dependency DLLs such as TBB and zlib
- `plugin/usd/plugInfo.json`
- `share/usd/plugins` and any other resource folders copied from the OpenUSD install root
