This folder is populated by `Native~/build_macos.sh`.

Expected runtime payload:

- `UnityUSDToolkitNative.dylib`
- `libusd_ms.dylib` or `libusd_m.dylib`
- OpenUSD dependency dylibs such as `libtbb*.dylib`
- `libMaterialXCore.1.dylib` and `libMaterialXFormat.1.dylib` (MaterialX 1.39.4).
  `libusd_ms` links them by these `.1` names, so they are copied under those names
  rather than as the install's versioned `*.1.39.4.dylib` files.
- `lib/usd/**/plugInfo.json` and schema resources
- `lib/usd/usdMtlx/resources/libraries/**`, the MaterialX standard library
  (`.mtlx` node definitions and shader-generator sources) that `usdMtlx` reads
  at run time
- `plugin/usd/**/plugInfo.json` and shader resources
- optional `share` and `resources` directories from the OpenUSD install

The native dylibs should be relocatable with `@loader_path` install names so
they can load from the Unity Editor package folder and from a macOS player
bundle under `Contents/PlugIns`.
