# Third-Party Notices

This package includes third-party runtime components required for Windows x64
runtime USD export. These notices apply to the files redistributed in
`Runtime/Plugins/x86_64/Windows`.

This file also records third-party products that the package *integrates with*
but does not redistribute. See [NVIDIA Isaac Sim](#nvidia-isaac-sim) and
[Trademarks](#trademarks).

## Pixar OpenUSD

Redistributed files:

- `Runtime/Plugins/x86_64/Windows/usd_rt.dll` (OpenUSD monolithic, renamed from `usd_ms.dll`)
- `Runtime/Plugins/x86_64/Windows/lib/usd/**`
- `Runtime/Plugins/x86_64/Windows/plugin/usd/**`

Source:

- Pixar Animation Studios OpenUSD
- https://github.com/PixarAnimationStudios/OpenUSD

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

- `Runtime/Plugins/x86_64/Windows/tbb.dll`

Source:

- oneAPI Threading Building Blocks / oneTBB
- https://github.com/oneapi-src/oneTBB

License:

- Apache License 2.0
- Full text: `ThirdPartyNotices~/licenses/oneTBB-LICENSE.txt`

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
  proprietary Unity software. See `LICENSE.md` and
  `Samples/Live Sync Example/Tools~/isaacsim/exts/unity.usd.livesync/LICENSE.md`.
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
in this package are not third-party components. They are proprietary Unity
software unless a separate written agreement grants additional rights. See
`LICENSE.md`.

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
