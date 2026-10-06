# Export Example

Exports a scene to USD from a runtime UI.

1. Open `RuntimeExportExample.unity` and enter Play mode, or build a player that contains the scene.
2. Choose an output folder and file name. The default is `UsdExports/runtime-usd-export.usd` under `Application.persistentDataPath`.
3. Choose **Baked Mesh** or **Hierarchy**, and a format: `.usd`, `.usda`, `.usdc` or `.usdz`.
4. Select **Export USD (Mesh Only)** or **Export USD (with Textures)**.

To reset the objects to export, select **Recreate Demo Geometry**.

This sample uses `UsdExporter.ExportGameObjectWithResult`. For details, see the [User Manual](../../Documentation~/Unity%20USD%20Toolkit%20User%20Manual%20EN.md#4-export-at-runtime).
