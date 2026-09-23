# runtime-usd-export

An **internal developer test sample**. It lives under `DevSamples~/` (note the trailing `~`), so
Unity does not import it as an asset and **it is not part of a release build** — only people who
cloned this dev repository see it. (Same dev-only convention as `Native~` and `Documentation~`.)

## Contents

- `runtime-usd-export.usd` — a sample scene produced by the Unity runtime exporter
- `runtime-usd-export_textures/` — the textures that USD references (a sibling folder; the
  relative path and folder name must be preserved)

## What it is for

A reference for checking exporter and importer behaviour. Open it in usdview, Isaac Sim,
Omniverse or similar to inspect the exported geometry, materials and texture links.

## Notes

- The `.usd` file and its textures are large, so they are tracked with **Git LFS**. Run
  `git lfs pull` after cloning if you need them.
- Renaming the texture folder breaks the USD references.
