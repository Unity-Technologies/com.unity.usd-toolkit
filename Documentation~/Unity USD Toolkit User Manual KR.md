# Unity USD Toolkit 사용자 매뉴얼

Windows / macOS / Linux Standalone 런타임 USD Export · Import 플러그인

패키지 버전: 0.7.2-exp.1
네이티브 ABI: API 5
최소 Unity 버전: 2023.1
문서 개정일: 2026-09-23
대상 독자: Unity 프로젝트에 이 패키지를 적용해 런타임 USD export/import를 사용하려는 개발자 및 기술 사용자

> 영문판: `Unity USD Toolkit User Manual EN.md`

## 목차

1. 플러그인 개요
2. 지원 범위와 제한 사항
3. 새 Unity 프로젝트에 패키지 적용하기
4. 네이티브 런타임 페이로드 확인
5. 런타임 Export 사용 방법
6. Export 옵션 설명
7. 런타임 Import 사용 방법
8. Import 옵션 설명
9. 신뢰할 수 없는 USD 파일 처리 정책
10. Live Sync 예제
11. Standalone 빌드에서 사용하기
12. 결과 USD 확인 방법
13. 문제 해결
14. 배포 전 체크리스트
- 부록 A. Public API 요약
- 부록 B. 네이티브 플러그인 다시 빌드하기

## 1. 플러그인 개요

Unity USD Toolkit은 빌드된 Unity 플레이어 **안에서** USD 파일을 쓰고 읽기 위한 Unity 패키지입니다. Editor 전용 도구가 아니라, 실행 중인 애플리케이션에서 GameObject의 mesh 데이터를 Pixar OpenUSD 기반 네이티브 플러그인으로 넘겨 `.usd` / `.usda` / `.usdc` / `.usdz`를 생성하고, 같은 경로로 USD stage를 읽어 Unity GameObject로 복원합니다.

### 핵심 구조

- Unity C# API는 `Unity.USDToolkit` namespace 아래에 있습니다.
- C#은 P/Invoke로 `UnityUSDToolkitNative` 네이티브 플러그인의 C ABI 함수만 호출합니다.
- 네이티브 플러그인은 Pixar OpenUSD C++ API로 stage, mesh, material, transform을 다룹니다.
- Unity 공식 USD Editor 패키지, USD.NET, `com.unity.exporter.usd`에 의존하지 않습니다.

### 네이티브 ABI 버전

관리 계층은 로드된 플러그인이 보고하는 API 버전을 확인하고, **패키지 소스와 정확히 일치하지 않으면 거부**합니다(현재 API 5). 예전에는 API 2 이상을 모두 허용했지만, ABI로 구분되지 않는 수정(보안 수정 포함)이 낡은 바이너리에 빠져 있어도 버전만으로는 알 수 없기 때문에 정확히 일치하도록 바뀌었습니다. 이 검사는 export와 **import 양쪽**에서 실행됩니다.

## 2. 지원 범위와 제한 사항

### 플랫폼

| 플랫폼 | 지원 | 최소 OS | 비고 |
| --- | --- | --- | --- |
| Windows x64 (Editor / Standalone) | 지원 | Windows 10 21H1 | `Runtime/Plugins/x86_64/Windows` |
| macOS (Editor / Standalone) | 지원 | macOS 12.0 (Monterey) | Universal(x86_64 + arm64), `Runtime/Plugins/macOS` |
| Linux x64 (Editor / Standalone) | 지원 | **Ubuntu 24.04** | self-contained 페이로드, `Runtime/Plugins/x86_64/Linux` |
| 모바일 / WebGL / 콘솔 | 미지원 | — | 이번 버전 범위 밖입니다. |

최소 OS는 C# 레이어가 아니라 동봉된 네이티브 페이로드의 특성이며, 에디터뿐 아니라 여러분이 빌드한
플레이어에도 그대로 적용됩니다. 같은 라이브러리가 스탠드얼론 빌드로 복사되기 때문입니다.

- **macOS 12.0.** 모든 dylib을 deployment target 12.0으로 빌드합니다. Unity 6.3의 macOS 플레이어
  최소 사양이며, 그보다 낮은 버전에서는 dyld가 로드를 거부합니다.
- **Ubuntu 24.04.** Linux 페이로드는 Ubuntu 24.04에서 빌드되어 **glibc 2.38 이상**과
  **`GLIBCXX_3.4.32`를 제공하는 libstdc++**(GCC 13)를 요구합니다. Unity 6.3은 Ubuntu 22.04도
  지원하지만(glibc 2.35 / `GLIBCXX_3.4.30`) 이 패키지는 그 환경에서 동작하지 않으며, 조용히
  실패하는 대신 요구사항을 알려주는 네이티브 로드 오류를 던집니다. 코드가 24.04를 필요로 하는 것은
  아니고 빌드 머신에서 생긴 의존성이므로, 22.04에서 다시 빌드하면 기준은 낮아집니다.

> 세 플랫폼 모두 공개 태그 **OpenUSD `v26.05`** (커밋 `2095faf`)로 빌드되며, 네이티브 ABI는 API 5로 동일합니다. 공개 태그로 통일한 것은 제3자가 페이로드를 그대로 재현해 검증할 수 있게 하기 위해서입니다.

### Export

| 항목 | 지원 | 설명 |
| --- | --- | --- |
| Static Mesh | 지원 | `MeshFilter` + `MeshRenderer` 기반 GameObject |
| 읽기 불가 mesh의 GPU readback | 지원 | `Read/Write Enabled`가 꺼져 있어도 Play 모드에서 export 가능 |
| Normals / UV0 | 지원 | 옵션으로 켜고 끕니다. |
| Submesh별 material binding | 지원 | `UsdGeomSubset`으로 작성 |
| Hierarchy transform 보존 | 지원 | `UsdTransformPolicy.PreserveHierarchy` |
| Baked mesh transform | 지원 | 기본값 |
| `UsdPreviewSurface` | 지원 | base color, opacity, metallic, roughness, emission |
| **PBR 텍스처 export** | **지원** | `<usd이름>_textures/` 폴더에 PNG로 저장하고 상대경로로 참조 |
| **`.usdz` 패키징** | **지원** | 출력 확장자가 `.usdz`면 패키지로 작성. ARKit 호환 옵션 제공 |
| mesh extent | 지원 | |
| inactive/disabled 가시성 보존 | 지원 | `visibility = "invisible"` |
| Skinned Mesh / Animation | 미지원 | 이후 마일스톤 |

### Import (MVP)

| 항목 | 지원 | 설명 |
| --- | --- | --- |
| `.usd` / `.usda` / `.usdc` / `.usdz` | 지원 | usdz 내부 텍스처는 stage resolver로 읽습니다. |
| Static `UsdGeomMesh` | 지원 | |
| Xform 계층 복원 | 지원 | 각 transformable prim이 Unity `Transform`이 됩니다. |
| 다중 material | 지원 | `materialBind` `UsdGeomSubset` → Unity submesh |
| `UsdPreviewSurface` + PBR 텍스처 | 지원 | albedo / normal / metallic-smoothness / emission |
| 비동기 import | 지원 | 파싱은 워커 스레드, 오브젝트 생성은 프레임 분할 |
| stage 미리보기 정보 | 지원 | mesh/material/삼각형/정점 수를 import 전에 조회 |
| 폴더 스캔 | 지원 | `UsdLibraryScanner.ScanFolder` |
| Skinned mesh / animation / variant / payload 스트리밍 | 미지원 | 이후 마일스톤 |

## 3. 새 Unity 프로젝트에 패키지 적용하기

### 방법 A: Packages 폴더에 직접 배치

1. 패키지 폴더 `com.unity.usd-toolkit`를 Unity 프로젝트의 `Packages` 아래에 복사합니다.
2. 최종 경로가 `<UnityProject>/Packages/com.unity.usd-toolkit/package.json`인지 확인합니다.
3. Unity Editor를 열고 Console에 컴파일 오류가 없는지 확인합니다.

### 방법 B: Package Manager에서 추가

1. `Window > Package Manager`를 엽니다.
2. `+` → `Add package from disk`를 선택합니다.
3. 패키지 폴더의 `package.json`을 선택합니다.

### manifest.json 직접 등록

```json
"dependencies": {
  "com.unity.usd-toolkit": "file:Packages/com.unity.usd-toolkit"
}
```

> 네이티브 바이너리는 Git LFS로 관리됩니다. 저장소에서 직접 받는 경우 `git lfs install` 후 `git lfs pull`을 실행해야 합니다. LFS 포인터 상태로는 플러그인이 로드되지 않습니다.

## 4. 네이티브 런타임 페이로드 확인

플랫폼별로 아래 파일들이 있어야 합니다.

```text
Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_rt.dll          (OpenUSD monolithic. 다른 패키지와의 이름 충돌을 피해 usd_ms에서 rename)
  tbb_usdrt.dll       (Intel TBB. Windows 로더가 Editor의 tbb.dll을 대신 물리지 않도록
                       tbb.dll에서 rename)
  lib/usd/**/plugInfo.json
  plugin/usd/**/plugInfo.json

Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  lib/usd/… , plugin/usd/…

Runtime/Plugins/x86_64/Linux/
  libUnityUSDToolkitNative.so
  lib/libusd_ms.so , lib/libtbb.so.2
  lib/usd/… , plugin/usd/…
```

macOS dylib는 `@loader_path` 기반으로 의존성을 찾습니다. `otool -L Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib`로 확인할 수 있습니다.

### 페이로드 무결성 검증

패키지는 첫 P/Invoke 전에 각 네이티브 바이너리의 SHA-256을 `Runtime/Native/NativeRuntimeHashes.g.cs`에 기록된 값과 대조하고, 일치하지 않으면 로드를 거부합니다. 파일 존재 여부만 보던 기존 검사와 달리 내용을 확인하므로, 배포 과정에서 바이너리가 교체되거나 손상된 경우를 잡아냅니다. 검사는 프로세스당 한 번만 실행됩니다.

네이티브를 직접 다시 빌드했다면 매니페스트를 반드시 재생성해야 합니다.

```bash
python3 Native~/generate_native_hashes.py
```

> 이 스크립트는 **모든 플랫폼**의 바이너리를 해싱합니다. 다른 플랫폼 페이로드가 LFS 포인터 상태면 실행이 거부되므로, 먼저 `git lfs pull`로 실제 내용을 받아야 합니다.

### 받은 패키지가 Unity가 배포한 것인지 확인하기

확인할 수 있는 것이 셋이고, 각각 증명하는 성질이 다릅니다. 위의 다이제스트 매니페스트는 패킹
이후 페이로드가 바뀌지 않았음을 증명하고, 아래 둘은 그것이 **어디서 왔는지**를 증명합니다.

**1. 패키지 서명 (전 플랫폼).** Unity 파이프라인으로 배포된 패키지에는 `package/.attestation.p7m`
CMS/PKCS#7 어테스테이션이 들어 있습니다. Unity PKI가 서명하며, tarball 안 **모든 파일**의
다이제스트를 커버합니다 — `Runtime/Plugins/**`의 네이티브 바이너리 포함. 패킹 이후 파일이 추가·
삭제·변경되면 서명이 깨집니다. Unity 6.3 이상은 이를 자동 검증해 Package Manager 창에 표시하며,
직접 확인하려면:

```bash
tar -xzf com.unity.usd-toolkit-<version>.tgz package/.attestation.p7m
openssl cms -verify -in package/.attestation.p7m -inform DER -noverify -out attestation.json
openssl pkcs7 -in package/.attestation.p7m -inform DER -print_certs -text | head -40
```

`-noverify`를 빼고 `-CAfile`로 Unity 루트 인증서를 지정하면 구조뿐 아니라 체인까지 검증합니다.
내부적으로는 PVP-28-3(서명 존재)과 PVP-29-3(서명 유효 및 아카이브 내용 일치)이 같은 두 성질을
검사합니다.

**2. 플랫폼 코드 서명 (Windows / macOS).** 네이티브 바이너리는 Unity 인증서로 서명된 게시자
서명을 가집니다:

```bash
# macOS — Developer ID authority가 나와야 하며 "Signature=adhoc"이면 안 됩니다
codesign --verify --strict --verbose=2 Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib
codesign -dv --verbose=4 Runtime/Plugins/macOS/libusd_ms.dylib
```

```powershell
# Windows — Status: Valid 를 기대
Get-ChildItem Runtime\Plugins\x86_64\Windows\*.dll | ForEach-Object {
    Get-AuthenticodeSignature $_.FullName | Select-Object Status, SignerCertificate
}
```

**Linux은 의도적으로 코드 서명하지 않습니다.** ELF 포맷에도, 동적 링커에도, Unity의 서명
인프라에도 `.so`에 대한 Authenticode/Developer ID 상당물이 없습니다. Unity의 코드 서명 서비스는
Windows PE와 macOS Mach-O만 대상으로 하며, 네이티브 Linux 라이브러리를 배포하는 다른 Unity
패키지들도 동일하게 패키지 레벨에서 처리합니다. Linux의 무결성 근거는 (1)의 어테스테이션과
(3)의 다이제스트입니다.

**3. 파일별 다이제스트 (전 플랫폼).** `ThirdPartyNotices~/sbom.cdx.json`은 CycloneDX 1.6 형식의
BOM으로, 페이로드에 포함된 모든 서드파티 구성요소를 버전·출처·SHA-256과 함께 나열합니다.
런타임 검사가 쓰는 것과 같은 다이제스트이며, 에디터 밖에서 검증할 수 있습니다:

```bash
sha256sum Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so
python3 -c "import json;[print(c['hashes'][0]['content'], [p['value'] for p in c['properties'] if p['name']=='unity:shippedPath'][0]) for c in json.load(open('ThirdPartyNotices~/sbom.cdx.json'))['components'][1:]]"
```

SBOM에는 각 바이너리가 어떤 OpenUSD 태그와 커밋으로 빌드됐는지도 기록되어 있어, 제3자가 같은
공개 소스에서 다시 빌드해 내용을 비교할 수 있습니다.

## 5. 런타임 Export 사용 방법

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

출력 확장자가 결과 형식을 정합니다. `.usdz`를 지정하면 usdz는 읽기 전용 zip이므로 임시 스테이징 폴더에 `.usdc`와 텍스처를 쓴 뒤 패키징하며, `.usdz` 옆에는 아무것도 남지 않습니다.

### 읽기 가능한 mesh

빌드된 플레이어에서도 `Read/Write Enabled`가 꺼진 mesh를 export할 수 있습니다. Play 모드에서는 GPU 메모리에서 버퍼를 읽어오는 readback 경로가 동작하고, `Read/Write Enabled`가 켜져 있으면 CPU 경로를 사용합니다. `RequireReadableMeshes`를 `true`(기본)로 두면 어느 경로로도 읽을 수 없는 mesh에서 예외가 발생합니다.

## 6. Export 옵션 설명

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `RootPrimName` | `null` | 비우면 export root GameObject 이름을 사용합니다. |
| `MetersPerUnit` | `1.0f` | 0보다 커야 합니다. |
| `IncludeInactive` | `false` | 비활성 child 포함 여부 |
| `RequireReadableMeshes` | `true` | 읽을 수 없는 mesh에서 예외 |
| `ExportNormals` | `true` | |
| `ExportUv0` | `true` | |
| `ExportBounds` | `true` | mesh extent 작성 |
| `ExportDisabledRenderers` | `true` | disabled renderer를 invisible로 포함 |
| `PreserveInactiveAndDisabledVisibility` | `true` | `visibility = "invisible"` 작성 |
| `TransformPolicy` | `BakedMesh` | `BakedMesh` 또는 `PreserveHierarchy` |
| `ExportTextures` | `false` | PNG 텍스처를 `<usd이름>_textures/`에 저장하고 참조 |
| `IgnoreAlbedoInMetallicSlot` | `true` | metallic 슬롯에 albedo와 같은 텍스처가 꽂힌 경우 슬롯 오배치로 보고 스칼라 값만 export(경고 출력) |
| `UsdzArkitCompatible` | `false` | `.usdz` 출력에서만 의미. ARKit 제약에 맞춰 패키징 |
| `ValidateNativeRuntime` | `true` | 네이티브 페이로드 **존재** 확인 |
| `VerifyNativeRuntimeIntegrity` | `true` | 네이티브 페이로드 **내용**(SHA-256) 확인 |
| `ValidateOpenUsdPluginPath` | `true` | plugin/resource discovery 경로 점검 |
| `PluginSearchPath` | `null` | OpenUSD plugin 경로 직접 지정. **9장 참고** |
| `CaptureNativeDiagnostics` | `false` | OpenUSD diagnostic 수집 |
| `NativeDiagnosticsLogPath` | `null` | diagnostic 로그 파일 경로 |
| `LogExportSummary` | `false` | 성공 시 요약 로그 출력 |

### TransformPolicy 선택 기준

| 정책 | 사용 시점 | 결과 |
| --- | --- | --- |
| `BakedMesh` | 대부분의 런타임 export. 외부 툴에서 transform 구조가 중요하지 않을 때 | transform이 mesh point에 bake되고 USD 계층이 단순해집니다. |
| `PreserveHierarchy` | Unity 계층과 local transform을 USD에서도 유지해야 할 때 | GameObject 계층이 Xform prim이 되고 mesh point는 local space에 남습니다. |

## 7. 런타임 Import 사용 방법

### 미리보기 후 import

```csharp
UsdImportPreviewInfo preview = UsdImporter.GetPreviewInfo(path);
if (preview.TriangleCount > 5_000_000)
{
    // 무거운 파일이므로 경고하거나 취소할 기회를 줍니다.
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

`ImportAsync`는 USD 파싱과 파일 읽기를 워커 스레드에서 수행하고, Unity 오브젝트 생성만 메인 스레드에서 프레임 단위로 나눠 실행합니다(`MaxMillisecondsPerFrame`). **메인 스레드에서 호출해야 합니다.** 동기 버전이 필요하면 `UsdImporter.Import`를 사용합니다.

### 폴더 스캔

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

## 8. Import 옵션 설명

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `Parent` | `null` | 생성될 root의 부모 Transform |
| `RootObjectName` | `null` | 비우면 파일 이름을 사용합니다. |
| `ImportMaterials` | `true` | `UsdPreviewSurface` 값을 Unity material로 |
| `ImportTextures` | `true` | 참조된 PBR 텍스처 로드 |
| `IncludeInvisible` | `true` | `visibility = "invisible"` prim도 생성 |
| `GenerateColliders` | `false` | `MeshCollider` 생성 |
| `RecalculateNormalsIfMissing` | `true` | normal이 없으면 계산 |
| `AllowExternalAssetPaths` | `false` | **9장 참고.** stage 폴더 밖의 asset 읽기 허용 |
| `MaxMillisecondsPerFrame` | `10f` | `ImportAsync`의 프레임당 메인 스레드 예산 |
| `ProgressCallback` | `null` | `(0..1, 단계명)`, 메인 스레드에서 호출 |
| `CaptureNativeDiagnostics` | `false` | |
| `NativeDiagnosticsLogPath` | `null` | |

## 9. 신뢰할 수 없는 USD 파일 처리 정책

import 대상 USD 파일은 **외부에서 들어온 신뢰할 수 없는 입력**으로 취급합니다. 마켓플레이스 애셋, 공유받은 `.usdz`, 스캔 폴더에 놓인 파일 모두 마찬가지입니다. 기본 동작은 다음과 같습니다.

### asset 경로 제한 (기본 적용)

USD 파일은 자기 텍스처 경로를 스스로 기술하므로, `inputs:file`에 절대경로나 `../`를 적어 스테이지 폴더 밖 파일을 읽게 만들 수 있습니다. 이를 막기 위해 **스테이지 자신의 폴더(그리고 stage를 구성하는 각 레이어의 폴더) 밖으로 해석되는 경로는 건너뜁니다.** `.usdz` 내부 텍스처는 패키지 안에 있으므로 영향받지 않습니다.

정상적으로 외부 공용 텍스처 라이브러리를 참조하는 신뢰된 파일이라면 `UsdImportOptions.AllowExternalAssetPaths = true`로 풀 수 있습니다.

거부된 경로는 authored 경로만 경고에 표시되고, 실제 해석된 절대경로는 출력하지 않습니다.

### topology 검사 (기본 적용)

`faceVertexCounts`의 합이 `faceVertexIndices` 길이와 정확히 일치하지 않는 mesh는 경고와 함께 건너뜁니다. 이전에는 이런 파일이 버퍼 밖을 읽어 프로세스를 죽일 수 있었습니다.

### 텍스처 크기 상한 (기본 적용)

PNG 헤더가 선언한 크기가 한 변 16384 또는 총 64M 픽셀을 넘으면 내장 디코더가 거부하고 Unity 기본 디코더로 넘깁니다. 헤더만으로 수 GB 할당을 유발하는 파일을 막기 위한 것입니다.

### 플러그인 탐색 경로 제한 (기본 적용)

OpenUSD는 `plugInfo.json`이 지정한 라이브러리를 프로세스 안에서 로드·실행합니다. 따라서 `UsdExportOptions.PluginSearchPath`는 **패키지 자신의 네이티브 폴더 안**으로 해석되어야 하며, 벗어나면 거부됩니다. 커스텀 OpenUSD 설치를 의도적으로 쓰려면 코드에서 `UsdExporter.AllowExternalPluginSearchPath = true`를 설정합니다. 이 스위치는 직렬화되지 않는 static이므로, 씬이나 프리팹에 저장된 옵션이 스스로 권한을 부여할 수 없습니다.

## 10. Live Sync 예제

`Samples/Live Sync Example`은 실행 중인 Unity 씬과 외부 도구(Python, NVIDIA Isaac Sim, DCC) 사이에서 transform을 양방향으로 주고받는 예제입니다. Unity가 호스트가 되어 지오메트리를 `base_stage.usda`로 한 번 export한 뒤, 이후에는 loopback TCP로 개행 구분 JSON을 스트리밍합니다.

**이 채널은 인증을 요구합니다.** 모든 연결은 먼저 `{"cmd":"auth","token":"..."}`를 보내야 하며, 인증 전에는 어떤 명령도 처리되지 않고 브로드캐스트도 전달되지 않습니다. 토큰은 인스펙터의 `Auth Token`, 환경변수 `USD_LIVE_SYNC_TOKEN`, 그 둘이 비어 있으면 `base_stage.usda` 옆에 생성되는 `live_sync_token.txt` 순으로 결정됩니다.

기본 바인드는 loopback입니다. 외부에서 접근 가능한 주소로 바인드하려면 `allowNonLoopbackBind`와 **명시적인** `authToken`이 모두 필요합니다(자동 생성 토큰은 로컬 파일로만 공유되므로 허용되지 않습니다). 이 경우 트래픽은 암호화되지 않고 토큰이 평문으로 전송되므로 신뢰된 네트워크에서만 사용해야 합니다.

자세한 사용법은 `Samples/Live Sync Example/README.md`를 참고하세요.

## 11. Standalone 빌드에서 사용하기

1. Build Settings에서 대상 플랫폼을 선택합니다. macOS는 빌드 아키텍처와 페이로드가 맞아야 합니다.
2. Mono와 IL2CPP 모두 사용할 수 있습니다.
3. 출력 파일은 `Application.persistentDataPath` 아래에 저장하는 것을 권장합니다.

### 빌드된 플레이어의 페이로드 위치

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
<Build>/<App>_Data/Plugins/x86_64/Linux/       (Linux)
```

패키지의 Editor build postprocessor가 OpenUSD resource tree를 플레이어 쪽으로 복사합니다. `plugInfo.json`과 `share/usd/plugins`는 네이티브 DLL이 아니라 데이터 파일이라 Unity의 네이티브 플러그인 importer가 자동으로 옮겨주지 않기 때문입니다.

## 12. 결과 USD 확인 방법

- `usdchecker <file>` — 구조와 기본 유효성 검사
- `usdcat <file>` — 내용을 텍스트로 확인
- Blender USD import — geometry/material 육안 확인
- usdview / Omniverse — stage 구조와 material binding 확인

## 13. 문제 해결

| 증상 | 가능한 원인 | 해결 방법 |
| --- | --- | --- |
| `DllNotFoundException` | 네이티브 파일 누락, 또는 LFS 포인터 상태 | 플랫폼 페이로드 폴더를 확인하고 `git lfs pull`을 실행합니다. |
| 네이티브 API 버전 불일치 예외 | 플러그인이 패키지 소스보다 낮은 API 보고 | `Native~`에서 해당 플랫폼 플러그인을 다시 빌드합니다(부록 B). |
| 다이제스트 불일치로 로드 거부 | 페이로드가 매니페스트와 다름 | 직접 리빌드했다면 `python3 Native~/generate_native_hashes.py`로 매니페스트를 재생성합니다. 아니면 패키지를 다시 받습니다. |
| OpenUSD plugin path 오류 | `plugin/usd` 또는 `lib/usd` 누락 | `plugInfo.json` 포함 여부를 확인합니다. |
| `PluginSearchPath`가 거부됨 | 패키지 네이티브 폴더 밖 경로 | 9장 참고. 의도적이라면 `UsdExporter.AllowExternalPluginSearchPath`를 설정합니다. |
| Mesh is not readable | 어느 경로로도 mesh를 읽을 수 없음 | `Read/Write Enabled`를 켜거나 Play 모드에서 export합니다. |
| import 후 텍스처가 비어 있음 | 텍스처 경로가 stage 폴더 밖으로 해석됨 | Console 경고를 확인하고, 신뢰된 파일이면 `AllowExternalAssetPaths`를 켭니다. |
| import 시 일부 mesh가 빠짐 | topology 불일치로 거부됨 | Console 경고에 prim 경로가 표시됩니다. 원본 파일을 수정해야 합니다. |
| 파일이 생성되지 않음 | 출력 경로 권한 또는 예외 | `Application.persistentDataPath`를 사용하고 로그를 확인합니다. |
| Editor에서는 되는데 Standalone에서 실패 | 빌드 페이로드 누락 | 플레이어의 Plugins 폴더를 확인합니다. |

### 진단 정보 출력

```csharp
UsdRuntimeInfo info = UsdExporter.GetRuntimeInfo();
Debug.Log(info.ToString());   // API 버전, OpenUSD 버전, 네이티브 경로
```

## 14. 배포 전 체크리스트

- 패키지 ID가 `com.unity.usd-toolkit`, 버전이 의도한 값인지 확인
- 플랫폼별 네이티브 페이로드가 실제 바이너리(LFS 포인터 아님)인지 확인
- `python3 Native~/generate_native_hashes.py` 결과에 변경이 없는지 확인(있으면 매니페스트 미갱신)
- `plugin/usd/plugInfo.json`과 `lib/usd/plugInfo.json` 포함 확인
- Unity Editor에서 패키지가 컴파일되는지 확인
- 간단한 mesh export → `usdchecker` 통과 확인
- export한 파일을 다시 import해 왕복 확인
- 대상 플랫폼 Standalone 플레이어에서 export/import 호출 확인
- IL2CPP를 쓴다면 IL2CPP 플레이어에서도 별도 검증
- 사용자에게 skinned mesh와 animation은 아직 미지원임을 안내

## 부록 A. Public API 요약

| API | 용도 |
| --- | --- |
| `UsdExporter.ExportGameObject` | 결과 객체가 필요 없는 간단한 export |
| `UsdExporter.ExportGameObjectWithResult` | 결과 요약이 필요한 export |
| `UsdExporter.GetRuntimeInfo` | 네이티브 API/OpenUSD 버전, 런타임 경로 조회 |
| `UsdExporter.AllowExternalPluginSearchPath` | 패키지 밖 plugin 경로 허용(static, 직렬화되지 않음) |
| `UsdImporter.GetPreviewInfo` | import 전 stage 통계 조회 |
| `UsdImporter.Import` | 동기 import |
| `UsdImporter.ImportAsync` | 비동기 import(프레임 분할) |
| `UsdLibraryScanner.ScanFolder` | 폴더의 USD 파일 목록 |
| `UsdExportOptions` / `UsdImportOptions` | 동작 옵션 |
| `UsdExportResult` / `UsdImportResult` / `UsdImportPreviewInfo` | 결과 및 통계 |
| `UsdExportException` / `UsdImportException` | 실패 시 예외와 diagnostics |
| `UsdTransformPolicy` | `BakedMesh` 또는 `PreserveHierarchy` |

## 부록 B. 네이티브 플러그인 다시 빌드하기

일반 사용자는 패키지에 포함된 페이로드를 그대로 쓰면 됩니다. OpenUSD 버전을 바꾸거나 네이티브 wrapper를 수정한 경우에만 필요합니다.

- Windows / Linux: `Native~/REBUILD_WINDOWS_LINUX.md` (단계별 게이트 포함)
- macOS:

```bash
./Build~/build_openusd_macos.sh --arch universal \
  --install-dir "$PWD/Build~/OpenUSDInstall/macos-universal"
bash Native~/build_macos.sh \
  --openusd-root "$PWD/Build~/OpenUSDInstall/macos-universal" --arch universal
```

리빌드 후에는 **반드시** 다음을 수행합니다.

1. `python3 Native~/generate_native_hashes.py` — 다이제스트 매니페스트 재생성
2. `Native~/Tests~/security_test.cpp` 실행 — 보안 수정이 바이너리에 들어갔는지 확인
3. 삭제된 `.meta` 파일이 없는지 확인 — 설치 단계가 `.meta`를 지울 수 있으며, Unity가 새 GUID로 다시 만들면 참조가 깨집니다.

자세한 절차와 검증 게이트는 `Native~/REBUILD_WINDOWS_LINUX.md`와 `BUILD_NOTES.md`를 참고하세요.
