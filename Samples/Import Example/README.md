# Import Example

Scans a folder for USD files, previews their statistics, and imports them at runtime.

1. Open `RuntimeImportBrowser.unity` and enter Play mode.
2. Enter a folder or file path, or use the browse buttons.
3. Select **Scan** to list the `.usd`, `.usda`, `.usdc` and `.usdz` files in the folder.
4. Select a file, then **Preview File** to see its mesh and triangle counts.
5. Select **Import File** or **Import Selected**.

To create a test file in the current folder, select **Export Cube**. To remove imported objects, select **Clear Imported**.

This sample uses `UsdLibraryScanner.ScanFolder`, `UsdImporter.GetPreviewInfo` and `UsdImporter.Import`. For details, see the [User Manual](../../Documentation~/Unity%20USD%20Toolkit%20User%20Manual%20EN.md#5-import-at-runtime).
