# Runtime Import Browser Sample

Open `RuntimeImportBrowser.unity` and enter a folder or file path at runtime.
The sample scans `.usd`, `.usda`, and `.usdc` files with `UsdLibraryScanner`,
previews selected files with `UsdImporter.GetPreviewInfo`, and imports selected
content with `UsdImporter.Import`.

Use `Export Cube` to generate a small USD file in the current folder for a
self-contained smoke test.
