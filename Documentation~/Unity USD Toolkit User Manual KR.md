# Unity USD Toolkit 사용자 매뉴얼

Windows, macOS, Linux에서 Unity 런타임으로 OpenUSD stage를 읽고 씁니다.

| | |
| --- | --- |
| **패키지 버전** | 0.7.2-exp.1 |
| **Unity** | 6.4 이상 |
| **OpenUSD** | 26.05 |

[English](Unity%20USD%20Toolkit%20User%20Manual%20EN.md)

## 목차

1. [개요](#1-개요)
2. [지원 플랫폼과 기능](#2-지원-플랫폼과-기능)
3. [패키지 설치](#3-패키지-설치)
4. [런타임 Export](#4-런타임-export)
5. [런타임 Import](#5-런타임-import)
6. [스레드](#6-스레드)
7. [Material과 텍스처](#7-material과-텍스처)
8. [신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)
9. [Standalone 플레이어 빌드](#9-standalone-플레이어-빌드)
10. [USD Live Sync 예제](#10-usd-live-sync-예제)
11. [Export한 USD 파일 확인](#11-export한-usd-파일-확인)
12. [문제 해결](#12-문제-해결)
- [부록 A. API 레퍼런스](#부록-a-api-레퍼런스)
- [부록 B. 패키지 무결성 확인](#부록-b-패키지-무결성-확인)
- [부록 C. 네이티브 플러그인 다시 빌드하기](#부록-c-네이티브-플러그인-다시-빌드하기)

## 1. 개요

Unity USD Toolkit은 Editor와 빌드된 플레이어에서, 실행 중인 Unity 애플리케이션 안에서 USD 파일을 쓰고 읽습니다. GameObject를 `.usd`, `.usda`, `.usdc`, `.usdz` 파일로 변환하고, USD stage를 다시 GameObject로 불러옵니다.

패키지는 세 계층으로 구성됩니다.

- **C# API.** `Unity.USDToolkit` namespace의 public API입니다.
- **네이티브 플러그인.** `UnityUSDToolkitNative`는 Pixar OpenUSD API를 C 인터페이스로 감싼 C++ 라이브러리입니다. C# API가 P/Invoke로 호출합니다.
- **OpenUSD.** 플랫폼별로 빌드된 Pixar OpenUSD 26.05 라이브러리와 oneTBB입니다.

이 패키지는 Unity의 다른 USD 패키지나 USD.NET에 의존하지 않습니다.

C# API는 네이티브 플러그인을 로드할 때 버전을 확인하고, 패키지와 정확히 일치하지 않는 플러그인은 거부합니다. 이를 통해 패키지의 모든 수정 사항이 실제로 실행되는 바이너리에 포함되어 있음을 보장합니다.

## 2. 지원 플랫폼과 기능

### 플랫폼

| 플랫폼 | Editor | 플레이어 | 최소 OS |
| --- | --- | --- | --- |
| Windows x64 | 지원 | 지원 | Windows 10 21H1 |
| macOS, Universal(x86_64 + arm64) | 지원 | 지원 | macOS 12.0 |
| Linux x64 | 지원 | 미지원 | Ubuntu 24.04 |
| 모바일, WebGL, 콘솔 | 미지원 | 미지원 | — |

최소 OS는 Editor뿐 아니라 빌드한 플레이어에도 적용됩니다. 같은 네이티브 라이브러리가 플레이어에 복사되기 때문입니다.

Linux에서는 네이티브 라이브러리에 glibc 2.38 이상과 `GLIBCXX_3.4.32`를 포함한 libstdc++(GCC 13)가 필요합니다. Ubuntu 22.04에서는 로드되지 않습니다. 요구 사항이 충족되지 않으면 누락된 항목을 알려 주는 오류가 발생합니다.

Windows에서는 대상 PC에 Microsoft Visual C++ 재배포 가능 패키지가 필요합니다.

### Export

| 기능 | 지원 | 비고 |
| --- | --- | --- |
| Static mesh | 지원 | `MeshFilter`와 `MeshRenderer`가 있는 GameObject |
| **Read/Write Enabled**가 꺼진 mesh | 지원 | Play mode에서 GPU로부터 읽어 옵니다. |
| Normal, UV0 | 지원 | 각각 끌 수 있습니다. |
| Submesh별 material | 지원 | `UsdGeomSubset`으로 기록합니다. |
| Transform 계층 | 지원 | `UsdTransformPolicy.PreserveHierarchy` 사용 |
| Transform bake | 지원 | 기본값 |
| `UsdPreviewSurface` material | 지원 | Base color, opacity, metallic, roughness, emission |
| PBR 텍스처 | 지원 | `<usd-name>_textures/`에 PNG로 기록합니다. |
| `.usdz` 패키지 | 지원 | ARKit 호환 모드를 선택할 수 있습니다. |
| 비활성 오브젝트, 비활성 renderer | 지원 | `visibility = "invisible"`로 기록합니다. |
| Skinned mesh, 애니메이션 | 미지원 | |

### Import

| 기능 | 지원 | 비고 |
| --- | --- | --- |
| `.usd`, `.usda`, `.usdc`, `.usdz` | 지원 | `.usdz` 내부 텍스처도 지원합니다. |
| Static `UsdGeomMesh` | 지원 | |
| Transform 계층 | 지원 | Transform을 가진 모든 prim이 Unity `Transform`이 됩니다. |
| Mesh당 여러 material | 지원 | 각 `materialBind` subset이 submesh가 됩니다. |
| `UsdPreviewSurface` material과 PBR 텍스처 | 지원 | Albedo, normal, metallic-smoothness, emission |
| 비동기 import | 지원 | 메인 스레드 밖에서 파싱하고, 여러 프레임에 걸쳐 오브젝트를 생성합니다. |
| 통계 미리보기 | 지원 | Import 전에 mesh, material, triangle, vertex 수를 확인합니다. |
| 폴더 스캔 | 지원 | `UsdLibraryScanner.ScanFolder` |
| Up axis와 단위 변환 | 미지원 | 값을 보고하지만 적용하지는 않습니다. [5. 런타임 Import](#5-런타임-import)를 참고하세요. |
| Skinned mesh, 애니메이션, variant set, payload streaming | 미지원 | |
| MaterialX 등 `UsdPreviewSurface` 이외의 material | 미지원 | |
| Physics schema | 미지원 | |

## 3. 패키지 설치

1. **Window > Package Manager**를 엽니다.
2. **+ > Add package from git URL**을 선택합니다.
3. `https://github.com/Unity-Technologies/com.unity.usd-toolkit.git`을 입력하고 **Add**를 선택합니다.

로컬 폴더에서 설치하려면 패키지를 `<YourProject>/Packages/com.unity.usd-toolkit`에 복사하거나, **+ > Add package from disk**를 선택하고 `package.json`을 지정합니다.

`Packages/manifest.json`에 직접 추가할 수도 있습니다.

```json
"dependencies": {
  "com.unity.usd-toolkit": "https://github.com/Unity-Technologies/com.unity.usd-toolkit.git"
}
```

> [!NOTE]
> 네이티브 라이브러리는 Git LFS로 저장되어 있습니다. 저장소를 clone했다면 프로젝트를 열기 전에 `git lfs install`과 `git lfs pull`을 실행하세요. 라이브러리가 LFS 포인터 파일인 상태에서는 플러그인이 로드되지 않습니다.

### 네이티브 파일

플랫폼별 네이티브 파일은 각각의 폴더에 있습니다.

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_rt.dll
  tbb_usdrt.dll
  lib/usd/**/plugInfo.json
  plugin/usd/**/plugInfo.json

Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  lib/usd/…, plugin/usd/…

Runtime/Plugins/x86_64/Linux/
  libUnityUSDToolkitNative.so
  lib/libusd_ms.so, lib/libtbb.so.2
  lib/usd/…, plugin/usd/…
```

Windows에서는 같은 프로세스에 로드된 다른 사본(예: Unity Editor 자체의 `tbb.dll`)과 충돌하지 않도록 OpenUSD와 oneTBB 라이브러리의 이름을 바꿔 두었습니다.

## 4. 런타임 Export

```csharp
using Unity.USDToolkit;
using UnityEngine;

public class ExportButton : MonoBehaviour
{
    [SerializeField] private GameObject exportRoot;

    public void Export()
    {
        string path = System.IO.Path.Combine(Application.persistentDataPath, "robot.usdc");

        UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
            exportRoot,
            path,
            new UsdExportOptions
            {
                RootPrimName = "Robot",
                MetersPerUnit = 1.0f,
                TransformPolicy = UsdTransformPolicy.BakedMesh,
                ExportTextures = true,
                CaptureNativeDiagnostics = true,
            });

        Debug.Log(result.ToString());
    }
}
```

출력 경로의 확장자가 포맷을 결정합니다. `.usdz` 경로를 지정하면 stage와 텍스처를 임시 폴더에 기록한 뒤 패키징하며, `.usdz` 옆에 다른 파일을 남기지 않습니다.

### Transform policy 선택

| Policy | 사용 시점 | 결과 |
| --- | --- | --- |
| `BakedMesh` | 대부분의 export. 다운스트림 도구에 계층이 필요 없을 때 | Transform이 mesh point에 bake되고 USD 계층은 평탄해집니다. |
| `PreserveHierarchy` | GameObject 계층과 로컬 transform을 유지해야 할 때 | 각 GameObject가 `Xform` prim이 되고 mesh point는 로컬 공간에 남습니다. |

`BakedMesh`에서는 각 mesh가 export 루트 공간으로 기록됩니다. 위치, 회전, 스케일(음수 스케일 포함)이 point에 적용되며, Unity 좌표계에서 USD 좌표계로의 변환에 맞춰 삼각형 winding이 보정됩니다.

`PreserveHierarchy`에서는 export 루트가 USD 루트 prim이 되고, 각 자식의 로컬 위치, 회전, 스케일이 `xformOp:transform` 행렬로 기록됩니다.

### Read/Write Enabled가 꺼진 mesh

Play mode에서는 **Read/Write Enabled**가 꺼진 mesh를 GPU에서 읽어 오므로, 배포된 콘텐츠를 다시 import할 필요가 없습니다. 이를 허용하려면 `RequireReadableMeshes = false`로 설정하세요. GPU readback에는 그래픽 장치가 필요합니다. Batch mode나 `-nographics`에서는 mesh의 **Read/Write Enabled**를 켜세요.

### Export 옵션

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `RootPrimName` | `null` | 루트 prim 이름. 비어 있으면 export 루트의 이름을 사용합니다. |
| `MetersPerUnit` | `1.0f` | Stage의 `metersPerUnit`. 0보다 커야 합니다. |
| `TransformPolicy` | `BakedMesh` | `BakedMesh` 또는 `PreserveHierarchy` |
| `IncludeInactive` | `false` | 비활성 자식 GameObject를 포함합니다. |
| `RequireReadableMeshes` | `true` | **Read/Write Enabled**가 꺼진 mesh에서 예외를 발생시킵니다. `false`로 설정하면 GPU에서 읽습니다. |
| `ExportNormals` | `true` | Mesh에 normal이 있으면 기록합니다. |
| `ExportUv0` | `true` | Mesh에 UV0가 있으면 기록합니다. |
| `ExportBounds` | `true` | Mesh extent를 기록합니다. |
| `ExportDisabledRenderers` | `true` | 비활성 renderer를 포함합니다. |
| `PreserveInactiveAndDisabledVisibility` | `true` | 포함된 비활성 오브젝트와 renderer를 `visibility = "invisible"`로 표시합니다. |
| `ExportTextures` | `false` | PNG 텍스처를 `<usd-name>_textures/`에 기록합니다. |
| `IgnoreAlbedoInMetallicSlot` | `true` | Albedo 맵과 같은 텍스처인 metallic 맵을 무시합니다. [7. Material과 텍스처](#7-material과-텍스처)를 참고하세요. |
| `UsdzArkitCompatible` | `false` | `.usdz` 출력을 AR Quick Look용으로 패키징합니다. Variant set 같은 기능이 제거될 수 있습니다. |
| `ValidateNativeRuntime` | `true` | 네이티브 파일이 있는지 확인합니다. |
| `VerifyNativeRuntimeIntegrity` | `true` | 로드 전에 각 네이티브 파일의 SHA-256 digest를 확인합니다. Editor와 development 빌드에서만 끌 수 있습니다. |
| `ValidateOpenUsdPluginPath` | `true` | OpenUSD 플러그인 경로를 확인합니다. |
| `PluginSearchPath` | `null` | OpenUSD가 플러그인을 찾는 경로를 변경합니다. [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| `CaptureNativeDiagnostics` | `false` | OpenUSD 경고와 오류를 수집합니다. |
| `NativeDiagnosticsLogPath` | `null` | 수집한 진단 정보를 이 파일에 기록합니다. |
| `LogExportSummary` | `false` | Export 성공 후 요약을 로그로 출력합니다. |

## 5. 런타임 Import

Stage를 미리 본 뒤 import합니다.

```csharp
UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(path);
if (preview.TriangleCount > 5_000_000)
{
    // 큰 파일을 import하기 전에 사용자에게 알리거나 취소할 수 있게 합니다.
}

UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions
{
    Parent = transform,
    ImportMaterials = true,
    ImportTextures = true,
    GenerateColliders = false,
    ProgressCallback = (fraction, phase) => Debug.Log($"{phase} {fraction:P0}")
});
```

`ImportAsync`는 worker 스레드에서 stage를 읽고, 애플리케이션이 멈추지 않도록 메인 스레드에서 여러 프레임에 걸쳐 GameObject를 생성합니다. 메인 스레드에서 호출하세요. 동기식으로 import하려면 `UsdImporter.Import`를 사용합니다.

폴더의 USD 파일과 썸네일 목록을 가져오려면 다음과 같이 합니다.

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

### Up axis와 단위

Importer는 USD 좌표계를 Unity 좌표계로 변환하지만, stage의 `upAxis`나 `metersPerUnit`에 맞춰 회전하거나 스케일하지는 않습니다. 두 값은 `UsdImportPreviewInfo`와 `UsdImportResult`로 반환됩니다.

Isaac Sim의 stage는 기본적으로 Z-up이므로 Unity에서 회전된 상태로 보입니다. 이를 보정하려면 부모 GameObject 아래에 import한 뒤 그 부모를 회전하고 스케일하세요.

```csharp
var stageRoot = new GameObject("UsdStageRoot").transform;

UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions { Parent = stageRoot });

if (result.UpAxis == "Z")
{
    stageRoot.localRotation = Quaternion.Euler(-90f, 0f, 0f); // Stage +Z가 Unity +Y가 됩니다.
}

stageRoot.localScale = Vector3.one * (float)result.MetersPerUnit; // Stage 단위를 미터로 변환합니다.
```

### Import 옵션

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `Parent` | `null` | Import된 루트의 부모 `Transform` |
| `RootObjectName` | `null` | 루트 GameObject 이름. 비어 있으면 파일 이름을 사용합니다. |
| `ImportMaterials` | `true` | `UsdPreviewSurface` 값으로 Unity material을 만듭니다. |
| `ImportTextures` | `true` | Material이 참조하는 PBR 텍스처를 로드합니다. |
| `IncludeInvisible` | `true` | `visibility = "invisible"`인 prim도 생성합니다. |
| `GenerateColliders` | `false` | 각 mesh에 `MeshCollider`를 추가합니다. |
| `RecalculateNormalsIfMissing` | `true` | 파일에 normal이 없으면 계산합니다. |
| `AllowExternalAssetPaths` | `false` | Stage 폴더 밖의 텍스처를 허용합니다. [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| `MaxMillisecondsPerFrame` | `10f` | `ImportAsync`가 프레임마다 메인 스레드에서 사용할 수 있는 시간 |
| `ProgressCallback` | `null` | 메인 스레드에서 `0`~`1` 진행률과 현재 단계를 받습니다. |
| `CaptureNativeDiagnostics` | `false` | OpenUSD 경고와 오류를 수집합니다. |
| `NativeDiagnosticsLogPath` | `null` | 수집한 진단 정보를 이 파일에 기록합니다. |

## 6. 스레드

Unity 오브젝트를 생성하거나 읽는 메서드는 메인 스레드에서 실행해야 합니다.

| 메서드 | 스레드 |
| --- | --- |
| `UsdImporter.ImportAsync` | 메인 스레드에서 호출합니다. Stage 읽기는 worker 스레드에서 실행됩니다. |
| `UsdImporter.Import` | 호출한 스레드에서 실행됩니다. 메인 스레드에서 호출하세요. |
| `UsdImporter.GetPreviewInfo` | 모든 스레드. Unity 오브젝트를 사용하지 않습니다. |
| `UsdImporter.LoadImageFile` | 메인 스레드. `Texture2D`를 생성합니다. |
| `UsdExporter.ExportGameObject`, `ExportGameObjectWithResult` | 메인 스레드. Mesh, material, 텍스처를 읽습니다. |

모든 public 메서드는 처음 사용할 때 OpenUSD 런타임을 설정하므로, 별도로 초기화할 필요가 없습니다.

## 7. Material과 텍스처

### Export

`ExportTextures = true`이면 각 material의 텍스처를 `<usd-name>_textures/`에 PNG로 기록하고 상대 경로로 참조합니다.

| Unity 프로퍼티 | USD 입력 |
| --- | --- |
| `_BaseMap`, `_MainTex`, `_BaseColorMap` | `diffuseColor` (sRGB) |
| `_BumpMap`, `_NormalMap` | `normal` |
| `_MetallicGlossMap`, `_MetallicMap` | `metallic`(red 채널), `roughness`(1 − alpha) |
| `_EmissionMap` | `emissiveColor` |
| 텍스처 tiling과 offset | 기본값이 아닐 때 `UsdTransform2d` |

원본 텍스처에 **Read/Write Enabled**가 필요하지 않습니다. 여러 material이 같은 텍스처를 사용해도 한 번만 export합니다.

### Import

Import된 material은 `Universal Render Pipeline/Lit`, `HDRP/Lit`, `Standard` 중 처음 사용할 수 있는 셰이더를 사용합니다. 텍스처는 `_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_EmissionMap`에 할당되며, tiling과 offset은 `UsdTransform2d`에서 가져옵니다. 각 텍스처 파일은 한 번만 로드되어 material 간에 공유됩니다.

### Metallic 슬롯의 albedo 텍스처

Base color 텍스처를 metallic 슬롯(`_MetallicGlossMap`)에 할당하면 거울 같은 표면이 됩니다. Unity에서는 skybox나 reflection probe가 반사할 대상을 제공하므로 정상으로 보일 수 있지만, Isaac Sim 뷰포트처럼 환경 반사가 없는 뷰어에서는 검은색으로 렌더링됩니다.

Material의 metallic 맵이 albedo 맵과 같은 텍스처이면, exporter는 metallic 맵을 무시하고 material의 **Metallic** 값을 사용하며 경고를 출력합니다. 의도적으로 두 슬롯에 같은 텍스처를 쓰려면 `IgnoreAlbedoInMetallicSlot = false`로 설정하세요.

## 8. 신뢰할 수 없는 USD 파일

Importer는 다운로드한 에셋, 공유받은 `.usdz`, 스캔한 폴더의 파일 등 모든 USD 파일을 신뢰할 수 없는 입력으로 취급합니다. 다음 보호 기능이 기본으로 켜져 있습니다.

### 텍스처 경로

USD 파일은 자체적으로 텍스처 경로를 지정하며, 이 경로는 디스크의 어느 곳이든 가리킬 수 있습니다. Importer는 stage나 그 레이어의 폴더 밖으로 해석되는 텍스처를 건너뛰고, 파일에 적힌 경로로 경고를 출력합니다. `.usdz` 내부 텍스처는 영향을 받지 않습니다.

다른 폴더의 공용 텍스처 라이브러리를 참조하는 신뢰할 수 있는 stage라면 `UsdImportOptions.AllowExternalAssetPaths = true`로 설정하세요.

### Mesh topology

Face 데이터가 일치하지 않는 mesh는 건너뛰고, prim 경로와 함께 경고를 출력합니다.

### 텍스처 크기

한 변이 16,384 픽셀을 넘거나 전체 6,400만 픽셀을 넘는다고 선언한 PNG와 JPEG 텍스처는 거부합니다.

### 플러그인 탐색 경로

OpenUSD는 `plugInfo.json` 파일이 지정한 라이브러리를 실행합니다. 따라서 `UsdExportOptions.PluginSearchPath`는 패키지 자체의 네이티브 폴더 안에 있어야 합니다. 커스텀 OpenUSD 설치를 사용하려면 코드에서 `UsdExporter.AllowExternalPluginSearchPath = true`로 설정하세요. 이 설정은 static이며 직렬화되지 않으므로, 씬이나 프리팹에 저장된 옵션 오브젝트로는 켤 수 없습니다.

## 9. Standalone 플레이어 빌드

1. **Build Profiles**에서 Windows 또는 macOS를 선택합니다. macOS에서는 플레이어 아키텍처가 네이티브 라이브러리와 일치해야 합니다.
2. Mono 또는 IL2CPP로 빌드합니다.
3. 출력 파일은 `Application.persistentDataPath` 아래에 기록하세요.

빌드할 때 패키지가 OpenUSD 파일을 플레이어에 복사합니다.

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
```

다른 PC에 배포하려면 Unity가 빌드한 macOS 앱 번들에 서명하세요.

## 10. USD Live Sync 예제

`Samples/Live Sync Example`은 실행 중인 Unity 씬과 NVIDIA Isaac Sim이나 Python 스크립트 같은 외부 도구 사이에서 transform을 양방향으로 동기화합니다. Unity는 씬 geometry를 `base_stage.usda`로 한 번 export한 뒤, transform 변경 사항을 TCP로 스트리밍합니다.

모든 연결은 데이터를 받기 전에 토큰으로 인증해야 합니다. 서버는 로컬 머신의 연결만 받으며, 트래픽은 암호화되지 않습니다.

설정 방법은 [Live Sync Example 가이드](../Samples/Live%20Sync%20Example/README.md)를 참고하세요.

## 11. Export한 USD 파일 확인

- `usdchecker <file>`로 구조와 유효성을 확인합니다.
- `usdcat <file>`로 내용을 텍스트로 출력합니다.
- usdview, Isaac Sim, Omniverse, Blender에서 geometry와 material을 확인합니다.

## 12. 문제 해결

| 증상 | 원인 | 해결 방법 |
| --- | --- | --- |
| `DllNotFoundException` | 네이티브 파일이 없거나 아직 Git LFS 포인터 파일입니다. | `git lfs pull`을 실행하고 `Runtime/Plugins` 아래 플랫폼 폴더를 확인하세요. |
| 네이티브 API 버전 불일치 | 네이티브 플러그인이 다른 버전의 패키지에서 왔습니다. | 패키지를 다시 설치하거나 플러그인을 다시 빌드하세요. [부록 C](#부록-c-네이티브-플러그인-다시-빌드하기)를 참고하세요. |
| Digest 불일치로 네이티브 플러그인이 거부됨 | 네이티브 파일이 패키지에 포함된 파일과 다릅니다. | 패키지를 다시 설치하세요. 플러그인을 다시 빌드했다면 digest를 다시 생성하세요. [부록 C](#부록-c-네이티브-플러그인-다시-빌드하기)를 참고하세요. |
| OpenUSD 플러그인 경로 오류 | `plugin/usd` 또는 `lib/usd` 폴더가 없습니다. | `plugInfo.json` 파일이 있는지 확인하세요. |
| `PluginSearchPath`가 거부됨 | 경로가 패키지의 네이티브 폴더 밖에 있습니다. | [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| Mesh를 읽을 수 없음 | **Read/Write Enabled**가 꺼져 있고 그래픽 장치가 없습니다. | **Read/Write Enabled**를 켜거나 Play mode에서 export하세요. |
| Isaac Sim에서 표면이 검게 렌더링됨 | Albedo 텍스처가 metallic 슬롯에도 할당되어 있습니다. | [7. Material과 텍스처](#7-material과-텍스처)를 참고하세요. |
| Import 후 텍스처가 없음 | 텍스처 경로가 stage 폴더 밖을 가리킵니다. | Console 경고를 확인하세요. 신뢰할 수 있는 파일이라면 `AllowExternalAssetPaths = true`로 설정하세요. |
| Import 후 일부 mesh가 없음 | Mesh의 face 데이터가 일치하지 않습니다. | Console 경고에 prim이 표시됩니다. 원본 파일을 수정하세요. |
| Import한 stage가 회전되어 있음 | Stage가 Z-up입니다. | [5. 런타임 Import](#5-런타임-import)의 Up axis와 단위를 참고하세요. |
| 파일이 생성되지 않음 | 출력 폴더에 쓸 수 없거나 export 중 예외가 발생했습니다. | `Application.persistentDataPath` 아래에 기록하고 Console을 확인하세요. |
| Editor에서는 동작하지만 플레이어에서는 동작하지 않음 | 지원하지 않는 플랫폼이거나 빌드에 네이티브 파일이 없습니다. | Windows x64 또는 macOS로 빌드하고 플레이어의 `Plugins` 폴더를 확인하세요. |

네이티브 플러그인 버전, OpenUSD 버전, 탐색 경로를 출력하려면 다음과 같이 합니다.

```csharp
Debug.Log(UsdExporter.GetRuntimeInfo().ToString());
```

## 부록 A. API 레퍼런스

모든 public 타입은 `Unity.USDToolkit` namespace에 있습니다.

| API | 설명 |
| --- | --- |
| `UsdExporter.ExportGameObject` | GameObject 계층을 export합니다. |
| `UsdExporter.ExportGameObjectWithResult` | GameObject 계층을 export하고 `UsdExportResult`를 반환합니다. |
| `UsdExporter.GetRuntimeInfo` | 네이티브 플러그인 버전, OpenUSD 버전, 탐색 경로를 반환합니다. |
| `UsdExporter.AllowExternalPluginSearchPath` | 패키지 밖의 `PluginSearchPath`를 허용합니다. |
| `UsdImporter.GetPreviewInfo` | Import하지 않고 stage 통계를 반환합니다. |
| `UsdImporter.Import` | Stage를 동기식으로 import합니다. |
| `UsdImporter.ImportAsync` | Stage를 여러 프레임에 걸쳐 비동기식으로 import합니다. |
| `UsdImporter.LoadImageFile` | 이미지 파일을 크기 제한을 적용해 `Texture2D`로 로드합니다. |
| `UsdLibraryScanner.ScanFolder` | 폴더의 USD 파일과 썸네일 목록을 반환합니다. |
| `UsdExportOptions`, `UsdImportOptions`, `UsdLibraryScanOptions` | 옵션 |
| `UsdExportResult`, `UsdExportedMeshInfo` | Export 결과 |
| `UsdImportResult`, `UsdImportedMeshInfo`, `UsdImportPreviewInfo` | Import 결과와 통계 |
| `UsdLibraryItem` | `ScanFolder`가 찾은 USD 파일 |
| `UsdRuntimeInfo` | 네이티브 플러그인 버전, OpenUSD 버전, 탐색 경로 |
| `UsdExportException`, `UsdImportException` | 진단 정보를 포함한 오류 |
| `UsdTransformPolicy` | `BakedMesh` 또는 `PreserveHierarchy` |

`Unity.USDToolkit.Native`의 타입은 internal이며 예고 없이 변경될 수 있습니다.

패키지가 `0.x`인 동안에는 모든 릴리스에 호환되지 않는 API 변경이 포함될 수 있습니다. `1.0.0`부터 public API는 시맨틱 버저닝을 따릅니다.

## 부록 B. 패키지 무결성 확인

### 네이티브 파일 digest

패키지는 네이티브 플러그인을 로드하기 전에 각 네이티브 파일의 SHA-256 digest를 `Runtime/Native/NativeRuntimeHashes.g.cs`의 값과 비교하고, 일치하지 않는 파일은 로드하지 않습니다. 이 검사는 프로세스당 한 번 실행됩니다.

### 패키지 서명

Unity가 배포한 패키지에는 네이티브 라이브러리를 포함한 패키지의 모든 파일을 다루는 서명이 `package/.attestation.p7m`에 들어 있습니다. Unity 6.3 이상은 이를 자동으로 검증하고 Package Manager에 결과를 표시합니다. 직접 검증하려면 다음을 실행합니다.

```bash
tar -xzf com.unity.usd-toolkit-<version>.tgz package/.attestation.p7m
openssl cms -verify -in package/.attestation.p7m -inform DER -noverify -out attestation.json
openssl pkcs7 -in package/.attestation.p7m -inform DER -print_certs -text | head -40
```

인증서 체인까지 확인하려면 `-noverify`를 제거하고 `-CAfile`로 Unity 루트 인증서를 지정하세요.

### 코드 서명

Windows와 macOS의 네이티브 라이브러리는 Unity 인증서로 서명되어 있습니다.

```bash
# macOS: Developer ID authority가 표시되어야 합니다.
codesign --verify --strict --verbose=2 Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib
```

```powershell
# Windows: Status: Valid가 표시되어야 합니다.
Get-ChildItem Runtime\Plugins\x86_64\Windows\*.dll | ForEach-Object {
    Get-AuthenticodeSignature $_.FullName | Select-Object Status, SignerCertificate
}
```

Linux 공유 라이브러리에는 이에 해당하는 코드 서명이 없습니다. Linux에서는 패키지 서명과 BOM의 digest를 사용하세요.

### BOM(Bill of materials)

`ThirdPartyNotices~/sbom.cdx.json`은 CycloneDX BOM입니다. 네이티브 파일에 포함된 모든 서드파티 구성 요소의 버전, 출처, OpenUSD 커밋, SHA-256 digest를 나열합니다.

```bash
sha256sum Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so
python3 -c "import json;[print(c['hashes'][0]['content'], [p['value'] for p in c['properties'] if p['name']=='unity:shippedPath'][0]) for c in json.load(open('ThirdPartyNotices~/sbom.cdx.json'))['components'][1:]]"
```

## 부록 C. 네이티브 플러그인 다시 빌드하기

네이티브 플러그인은 OpenUSD 버전을 바꾸거나 플러그인을 수정할 때만 다시 빌드하면 됩니다.

OpenUSD는 빌드 전에 OpenUSD 소스와 의존성을 검증하는 `Native~/build_openusd.py`로 빌드하세요. 예를 들어 macOS에서는 다음과 같습니다.

```bash
python3 Native~/build_openusd.py --platform macos --openusd-src <OpenUSD v26.05 clone> \
  --install /Users/Shared/usd-26.05/install --build-target universal --require-scan
bash Native~/build_macos.sh \
  --openusd-root /Users/Shared/usd-26.05/install --arch universal \
  --codesign-id "<Developer ID>"
```

다시 빌드한 후에는 digest를 다시 생성하세요.

```bash
python3 Native~/generate_native_hashes.py
```

플랫폼별 전체 절차는 `Native~/README.md`와 `Native~/REBUILD_WINDOWS_LINUX.md`를 참고하세요.
