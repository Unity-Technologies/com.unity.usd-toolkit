# Unity USD Live Sync

Makes Isaac Sim a client of the Unity `UsdLiveSyncServer` broadcast hub (TCP `:10000`,
newline-delimited JSON) — the same hub `Tools~/usd_live_sync.py` connects to. Both can be connected
at once.

## Panel

1. **Unity baseline geometry** — point at the `base_stage.usda` that `UsdLiveSyncServer` writes on
   Play, then **Mount Unity stage**. This references the file under a wrapper Xform that carries the
   up-axis (Unity Y-up vs. Isaac Z-up) and `metersPerUnit` fix-up, and fills in the prim prefix.
2. **Live connection** — **Connect** while Unity is in Play mode. Transforms then stream in and are
   applied once per Kit update.
3. **Commands to Unity** — request a full snapshot, or reset every tracked Unity transform to its
   Play-time baseline.
4. **Push back** — list wire prim paths whose Isaac-side pose should be sent back to Unity as
   `set_transform`. Unity applies these only where `UsdSyncNode.AcceptsRemoteWrites` is true.

See `Tools~/isaacsim/README.md` in the Live Sync Example sample for the full walkthrough, and the
sample's own `README.md` for how it fits the wider architecture.

## License and notices

This extension is part of the Unity USD Toolkit package (`com.unity.usd-toolkit`). Unity USD Toolkit
Package © 2026 Unity Technologies. Licensed under the Unity Companion License for Unity-dependent
projects. See `LICENSE.md` at the package root.

**No NVIDIA software is redistributed.** The `isaacsim`, `omni.*` and `carb` APIs are provided by
your own Isaac Sim installation, which NVIDIA licenses to you under NVIDIA's own terms.

NVIDIA, NVIDIA Isaac Sim, and NVIDIA Omniverse are trademarks and/or registered trademarks of NVIDIA
Corporation in the U.S. and other countries. Universal Scene Description (USD) and OpenUSD are
trademarks and/or registered trademarks of Pixar in the U.S. and other countries. All other
trademarks are the property of their respective owners. Use of a third-party name or mark here is
for identification only and does not imply any endorsement, sponsorship, or affiliation.
