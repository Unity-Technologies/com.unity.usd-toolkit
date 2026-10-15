# Changelog

## [0.1.1]

### Changed
- `extension.toml`: `authors` set to `Unity Technologies`.

## [0.1.0]

### Added
- Initial Isaac Sim bridge: mount `base_stage.usda`, apply streamed per-prim transforms each Kit
  update, push Isaac-side poses back to Unity, forward `reset` / `get_snapshot`.
- Up-axis and `metersPerUnit` fix-up applied on a dedicated wrapper Xform.
