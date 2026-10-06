# Third-Party Notices

This package includes third-party runtime components required for USD export and
import on every supported platform. These notices apply to the files
redistributed in `Runtime/Plugins/macOS`, `Runtime/Plugins/x86_64/Windows` and
`Runtime/Plugins/x86_64/Linux`.

A machine-readable inventory of the same components — versions, source commits,
build flags, per-file SHA-256 — is in `ThirdPartyNotices~/sbom.cdx.json`
(CycloneDX 1.6). Regenerate it with `python3 Native~/generate_sbom.py` whenever a
payload is rebuilt.

This file also records third-party products that the package *integrates with*
but does not redistribute. See [NVIDIA Isaac Sim](#nvidia-isaac-sim) and
[Trademarks](#trademarks).

## Pixar OpenUSD

Redistributed files:

- `Runtime/Plugins/macOS/libusd_ms.dylib` (OpenUSD monolithic, universal x86_64 + arm64)
- `Runtime/Plugins/macOS/lib/usd/**`
- `Runtime/Plugins/macOS/plugin/usd/**`
- `Runtime/Plugins/x86_64/Windows/usd_rt.dll` (OpenUSD monolithic, renamed from `usd_ms.dll`)
- `Runtime/Plugins/x86_64/Windows/lib/usd/**`
- `Runtime/Plugins/x86_64/Windows/plugin/usd/**`
- `Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so` (OpenUSD monolithic)
- `Runtime/Plugins/x86_64/Linux/lib/usd/**`
- `Runtime/Plugins/x86_64/Linux/plugin/usd/**`

Version:

- OpenUSD **26.05**, public tag `v26.05`, commit `2095faf` ("Merge release v26.05")
- The same tag and commit on all three platforms, built monolithic with
  `--build-variant release --build-monolithic --no-python --no-imaging --no-usdview
  --no-examples --no-tutorials --no-tests --no-materialx`

Source:

- Pixar Animation Studios OpenUSD
- https://github.com/PixarAnimationStudios/OpenUSD
- https://github.com/PixarAnimationStudios/OpenUSD/releases/tag/v26.05

License:

- Tomorrow Open Source Technology License 1.0
- Full text: `ThirdPartyNotices~/licenses/OpenUSD-LICENSE.txt`
- Notice text: `ThirdPartyNotices~/licenses/OpenUSD-NOTICE.txt`

OpenUSD notice:

```text
Universal Scene Description
Copyright 2016 Pixar

All rights reserved.

This product includes software developed at:

Pixar (http://www.pixar.com/).
```

## oneTBB

Redistributed files:

- `Runtime/Plugins/macOS/libtbb.dylib`
- `Runtime/Plugins/macOS/libtbbmalloc.dylib`
- `Runtime/Plugins/macOS/libtbbmalloc_proxy.dylib`
- `Runtime/Plugins/x86_64/Windows/tbb_usdrt.dll` (Intel's `tbb.dll`, renamed only)
- `Runtime/Plugins/x86_64/Linux/lib/libtbb.so.2`

Version:

- **2020.3** (interface version 11103) on every platform, the version OpenUSD's
  `build_scripts/build_usd.py` pins for a v26.05 build. That script takes it from
  a different place per platform: the prebuilt `tbb-2020.3-win.zip` release on
  Windows, the `v2020.3` source tag on macOS, and the `v2020.3.1` point release on
  Linux (which still reports itself as 2020.3).

Source:

- oneAPI Threading Building Blocks / oneTBB
- https://github.com/oneapi-src/oneTBB

License:

- Apache License 2.0
- Full text: `ThirdPartyNotices~/licenses/oneTBB-LICENSE.txt`

## Components vendored inside OpenUSD

These are third-party libraries that live in the OpenUSD source tree and are
compiled into the monolithic OpenUSD library redistributed above. They ship as
part of `libusd_ms.dylib` / `usd_rt.dll` / `libusd_ms.so` rather than as separate
files, at the versions OpenUSD v26.05 vendors — except LZ4, which OpenUSD v26.05
vendors at 1.9.2 and which this package's build replaces with upstream 1.10.0
(`Native~/patches/openusd-26.05-lz4-1.10.0.patch`, SECURITY-282834 / CVE-2021-3520).

| Component | Version | License | Full text |
|---|---|---|---|
| CLI11 (`pxr/base/tf/pxrCLI11`) | 2.3.1 | BSD-3-Clause | `ThirdPartyNotices~/licenses/CLI11-LICENSE.txt` |
| double-conversion (`pxr/base/tf/pxrDoubleConversion`) | 3.3.0 | BSD-3-Clause | `ThirdPartyNotices~/licenses/double-conversion-LICENSE.txt` |
| LZ4 (`pxr/base/tf/pxrLZ4`) | 1.10.0 | BSD-2-Clause | `ThirdPartyNotices~/licenses/LZ4-LICENSE.txt` |
| tsl robin-map (`pxr/base/tf/pxrTslRobinMap`) | not declared by the vendored copy | MIT | `ThirdPartyNotices~/licenses/tsl-robin-map-LICENSE.txt` |

Sources:

- https://github.com/CLIUtils/CLI11
- https://github.com/google/double-conversion
- https://github.com/lz4/lz4
- https://github.com/Tessil/robin-map

## Microsoft Visual C++ Runtime

This package does not bundle Microsoft Visual C++ Runtime redistributable DLLs.
If a target machine does not already have the required runtime installed, the
application installer should include the matching Microsoft Visual C++
Redistributable for the toolset used to build `UnityUSDToolkitNative.dll` and OpenUSD.

## NVIDIA Isaac Sim

Component name:

- NVIDIA Isaac Sim / NVIDIA Omniverse Kit

Nature of the integration:

- The `Live Sync Example` sample ships an Omniverse Kit extension and a
  standalone runner under
  `Samples/Live Sync Example/Tools~/isaacsim` that connect an Isaac Sim stage to
  the sample's `UsdLiveSyncServer`.

Redistributed files:

- **None.** This package does not bundle, embed, or redistribute any NVIDIA
  software, source code, binaries, headers, models, or assets. The extension
  only calls NVIDIA-provided Python APIs (`isaacsim`, `omni.ext`, `omni.kit.app`,
  `omni.ui`, `omni.usd`, `omni.timeline`, `carb`) that are resolved at run time
  from an Isaac Sim installation the user obtains separately from NVIDIA. The
  Isaac Sim installation directory is never written to.

License:

- The integration code under `Samples/Live Sync Example/Tools~/isaacsim` is
  Unity code licensed under the Unity Companion License for Unity-dependent
  projects. See `LICENSE.md`.
- NVIDIA Isaac Sim, NVIDIA Omniverse, and any NVIDIA components they install are
  licensed to the user solely by NVIDIA under NVIDIA's own terms. Unity grants no
  rights in any NVIDIA software. Obtain Isaac Sim from NVIDIA and review the
  license that accompanies it:
  - https://developer.nvidia.com/isaac/sim
  - https://www.nvidia.com/en-us/agreements/

Third Party Product disclaimer:

```text
The following concerns a product or service (each a "Third Party Product") that
is not developed, owned, or operated by Unity. This information may not be
up-to-date or complete, and is provided to you for your information and
convenience only. Your access and use of any Third Party Product is governed
solely by the terms and conditions of such Third Party Product. Unity makes no
express or implied representations or warranties regarding such Third Party
Products, and will not be responsible or liable, directly or indirectly, for any
actual or alleged damage or loss arising from your use thereof (including damage
or loss arising from any content, advertising, products or other materials on or
available from the provider of any Third Party Products).
```

## Package Code

The Unity C# API, sample scripts, build scripts, and custom native wrapper code
in this package are not third-party components. They are licensed under the
Unity Companion License for Unity-dependent projects. See `LICENSE.md`.

## Trademarks

- NVIDIA, NVIDIA Isaac Sim, and NVIDIA Omniverse are trademarks and/or
  registered trademarks of NVIDIA Corporation in the U.S. and other countries.
- Universal Scene Description (USD), OpenUSD, and Pixar are trademarks and/or
  registered trademarks of Pixar in the U.S. and other countries.
- Intel and oneAPI are trademarks of Intel Corporation or its subsidiaries.
- Microsoft and Visual C++ are trademarks of the Microsoft group of companies.
- Unity, the Unity logo, and other Unity trademarks are trademarks or registered
  trademarks of Unity Technologies or its affiliates in the U.S. and elsewhere.

All other trademarks are the property of their respective owners. Use of a
third-party name or mark in this package is for identification only and does not
imply any endorsement, sponsorship, or affiliation.
