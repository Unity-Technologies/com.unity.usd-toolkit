This folder is populated by `Native~/build_macos.sh`.

Expected runtime payload:

- `UnityUSDToolkitNative.dylib`
- `libusd_ms.dylib` or `libusd_m.dylib`
- OpenUSD dependency dylibs such as `libtbb*.dylib`
- `lib/usd/**/plugInfo.json` and schema resources
- `plugin/usd/**/plugInfo.json` and shader resources
- optional `share` and `resources` directories from the OpenUSD install

The native dylibs should be relocatable with `@loader_path` install names so
they can load from the Unity Editor package folder and from a macOS player
bundle under `Contents/PlugIns`.
