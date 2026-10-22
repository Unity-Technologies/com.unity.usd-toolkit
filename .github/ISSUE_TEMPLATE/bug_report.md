---
name: Bug report
about: Something imports, exports or renders wrong, or the package fails to load
title: ""
labels: bug
---

<!--
Please attach the USD file that shows the problem. Most import and export
bugs depend on how the stage is authored, and we cannot reproduce them from
a description alone. If the file is confidential, cut it down to the prims
that still show the problem, or author a small stage that does. A .usdz or
a zip with the stage and its textures is ideal.
-->

**USD file**

Attach it here, or link to it. If you really cannot share any file, say so and describe the stage: which application wrote it, its up axis and units, and which prim types and material schemas it uses.

**What happened**

**What you expected**

**Steps to reproduce**

1.
2.
3.

**Environment**

- Unity version:
- Package version (`Packages/manifest.json` or the Package Manager):
- Platform and OS version (Windows / macOS Intel or Apple silicon / Linux):
- Render pipeline (Built-in / URP / HDRP):
- Editor or player build:
- Output of `UsdExporter.GetRuntimeInfo()`:

**Console output**

Paste the full error or warning, with its stack trace, and the summary the importer prints (`UsdImportResult.ToString()`).

**Screenshots**

For rendering problems, a screenshot from Unity and, if you have it, the same view from Isaac Sim, Omniverse or usdview.
