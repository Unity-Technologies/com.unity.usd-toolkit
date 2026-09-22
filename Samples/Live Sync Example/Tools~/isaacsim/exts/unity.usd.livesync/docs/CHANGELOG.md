# Changelog

## [0.1.1]

### Added
- `LICENSE.md` — Unity proprietary notice, the statement that no NVIDIA software is redistributed,
  the Third Party Product disclaimer, and trademark attribution. Reachable from the Extensions
  window via `[documentation] pages`.
- `extension.toml`: `authors` corrected to `Unity Technologies`, `license = "SEE LICENSE.md"`.

## [0.1.0]

### Added
- Initial Isaac Sim bridge: mount `base_stage.usda`, apply streamed per-prim transforms each Kit
  update, push Isaac-side poses back to Unity, forward `reset` / `get_snapshot`.
- Up-axis and `metersPerUnit` fix-up applied on a dedicated wrapper Xform.
