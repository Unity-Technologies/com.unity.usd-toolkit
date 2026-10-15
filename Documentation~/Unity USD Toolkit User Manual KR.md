# Unity USD Toolkit 사용자 매뉴얼

Windows, macOS, Linux에서 Unity 런타임으로 OpenUSD stage를 읽고 씁니다.

| | |
| --- | --- |
| **패키지 버전** | 0.8.0-exp.1 |
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

C# API는 네이티브 플러그인을 로드할 때 API 버전을 확인합니다. API 6 이상인 플러그인(`UsdNative.MinimumApiVersion`)은 모두 받아들이고, 그보다 오래된 플러그인은 거부합니다. API 버전으로 구분되지 않는 수정 사항이 오래된 바이너리에는 빠져 있기 때문입니다. API 7~12가 필요한 기능은 로드된 플러그인이 해당 레벨을 갖추면 켜지고, 그렇지 않으면 그 기능만 건너뛰고 나머지 import는 그대로 동작합니다. [네이티브 API 레벨](#네이티브-api-레벨)을 참고하세요.

## 2. 지원 플랫폼과 기능

### 플랫폼

| 플랫폼 | Editor | 플레이어 | 최소 OS |
| --- | --- | --- | --- |
| Windows x64 | 지원 | 지원 | Windows 10 21H1 |
| macOS, Universal(x86_64 + arm64) | 지원 | 지원 | macOS 12.0 |
| Linux x64 | 지원 | 지원 | Ubuntu 22.04 |
| 모바일, WebGL, 콘솔 | 미지원 | 미지원 | — |

최소 OS는 Editor뿐 아니라 빌드한 플레이어에도 적용됩니다. 같은 네이티브 라이브러리가 플레이어에 복사되기 때문입니다.

Linux에서는 네이티브 라이브러리에 glibc 2.34 이상과 `GLIBCXX_3.4.29`를 포함한 libstdc++(GCC 11)가 필요합니다. Ubuntu 22.04 이상이면 두 조건을 모두 충족합니다. 요구 사항이 충족되지 않으면 누락된 항목을 알려 주는 오류가 발생합니다.

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
| USD composition 유지 | 지원 | `UsdCompositionMode.Preserve`로 sublayer, reference, instancing, override를 기록합니다. [USD composition 유지](#usd-composition-유지)를 참고하세요. 네이티브 API 6이 필요합니다. |
| Skinned mesh, 애니메이션 | 미지원 | |

### Import

| 기능 | 지원 | 비고 |
| --- | --- | --- |
| `.usd`, `.usda`, `.usdc`, `.usdz` | 지원 | `.usdz` 내부 텍스처도 지원합니다. |
| Static `UsdGeomMesh` | 지원 | Normal이 기록되지 않은 mesh는 smooth normal을 생성합니다. 값이 같은 face-varying normal과 UV는 하나의 vertex로 합칩니다. |
| Implicit surface | 지원 | `Cube`, `Sphere`, `Cylinder`, `Cone`, `Capsule`, `Plane`을 mesh로 만듭니다. |
| `PointInstancer` | 지원 | 각 prototype은 한 번만 import되고 숨겨집니다. Instancer는 `UsdPointInstancer` 컴포넌트를 가진 GameObject 하나가 되며, 이 컴포넌트가 인스턴스 배열의 모든 placement를 `Graphics.RenderMeshInstanced`로 cell 단위 culling을 적용해 그립니다. 인스턴스는 개별 선택이 되지 않고 collider도 없습니다. `invisibleIds`를 반영합니다. [Point instancer](#point-instancer)를 참고하세요. 네이티브 API 8이 필요합니다. |
| Subdivision surface | 일부 지원 | Subdivision 없이 control cage를 가져오고 경고를 남깁니다. |
| Transform 계층 | 지원 | Transform을 가진 모든 prim이 Unity `Transform`이 됩니다. 타입이 없거나 custom schema인 prim은 빈 GameObject가 됩니다. Purpose가 `proxy`나 `guide`인 prim은 건너뜁니다. |
| Mesh당 여러 material | 지원 | 각 `materialBind` subset이 submesh가 됩니다. |
| `UsdPreviewSurface` material과 PBR 텍스처 | 지원 | Albedo, normal, metallic-smoothness, emission |
| MaterialX material | 지원 | `standard_surface`, `open_pbr_surface`, `UsdPreviewSurface` 노드. Stage 안에 기록된 material과 참조된 `.mtlx` 파일 모두 읽습니다. [7. Material과 텍스처](#7-material과-텍스처)를 참고하세요. |
| MDL material | 일부 지원 | `OmniPBR`, `OmniSurface`, `OmniGlass`의 색상과 텍스처. 다른 MDL material은 읽지 않습니다. |
| Primvar 색상 | 지원 | `diffuseColor`에 연결된 `UsdPrimvarReader`(예: `displayColor`)를 base color로 사용합니다. |
| Material이 없는 mesh | 지원 | 공유 회색 material `UsdDefaultMaterial`을 할당합니다. `AssignDefaultMaterial`을 참고하세요. |
| Mesh 공유 | 지원 | Geometry가 같은 인스턴스는 Unity `Mesh` 하나를 공유합니다. `ShareIdenticalMeshes`를 참고하세요. |
| Light | 지원 | UsdLux distant, sphere, disk, cylinder, rect light가 Unity `Light`가 됩니다. Intensity 변환은 근사값입니다. 네이티브 API 7이 필요합니다. |
| Dome light | 일부 지원 | `UsdDomeLight` 컴포넌트에 기록합니다. `ApplyDomeLightToEnvironment`를 켜면 ambient 색상과 panoramic skybox로 적용합니다. 네이티브 API 7이 필요합니다. |
| Camera | 지원 | `UsdGeomCamera`가 비활성화된 Unity `Camera`가 됩니다. 네이티브 API 7이 필요합니다. |
| Spatial audio | 일부 지원 | `UsdMediaSpatialAudio`가 `AudioSource`와 `UsdSpatialAudio` 컴포넌트가 됩니다. Clip은 로드하지 않습니다. 네이티브 API 7이 필요합니다. |
| 비동기 import | 지원 | 메인 스레드 밖에서 파싱하고, 여러 프레임에 걸쳐 오브젝트를 생성합니다. `ImportAsync`는 텍스처가 붙기 전에 반환되고, 텍스처가 모두 붙으면 `UsdImportResult.TexturesReady`가 완료됩니다(`StreamTextures`, 기본값 켜짐). |
| 통계 미리보기 | 지원 | Import 전에 mesh, material, triangle, vertex 수를 확인합니다. |
| 폴더 스캔 | 지원 | `UsdLibraryScanner.ScanFolder` |
| Up axis와 단위 변환 | 지원 | 기본값으로 import 루트에 적용합니다. [5. 런타임 Import](#5-런타임-import)의 Up axis와 단위를 참고하세요. |
| Variant 선택, payload 로드 제어 | 지원 | Variant를 고르고 로드할 payload를 정합니다. [Variant set과 payload](#variant-set과-payload)를 참고하세요. 네이티브 API 6이 필요합니다. |
| Composition, instancing | 지원 | Reference, payload, inherit, specialize, instance를 합성해 읽습니다. 인스턴스마다 별도의 GameObject가 생깁니다. Geometry가 같은 인스턴스는 `Mesh` 하나를 공유합니다(`ShareIdenticalMeshes`. 고유 mesh 수는 `UsdImportResult.UniqueMeshCount`로 확인합니다). |
| Skinned mesh, 애니메이션 | 미지원 | |
| Physics schema | 미지원 | |

## 3. 패키지 설치

1. **Window > Package Management > Package Manager**를 엽니다.
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

### 네이티브 API 레벨

네이티브 플러그인은 `RUsd_GetApiVersion`으로 API 버전을 보고합니다. 배포된 Windows, macOS, Linux payload는 모두 API 12입니다. C# API는 API 6 이상을 받아들이고, 이후 레벨의 기능은 로드된 플러그인이 해당 레벨에 도달했을 때만 사용합니다.

| API | 추가된 기능 | `UsdNative`의 상수 |
| --- | --- | --- |
| 6 | Composition. Export에서 sublayer, reference, payload, instancing, variant 선택, override를 기록하고, import에서 variant와 payload를 제어합니다. 허용되는 최소 버전입니다. | `CompositionApiVersion` |
| 7 | Import에서 light, camera, spatial audio | `SceneComponentsApiVersion` |
| 8 | Point instancer를 prim으로 펼치지 않고 prototype과 행렬 배열로 전달 | `InstancedMeshApiVersion` |
| 9 | Material의 metallic 맵이 packing된 방식 | `MetallicLayoutApiVersion` |
| 10 | 단독 metallic 맵과 별도의 roughness 맵. C# 쪽에서 텍스처 하나로 packing합니다. | `TextureLayoutApiVersion` |
| 11 | 네이티브 코드의 EXR 디코딩과 block compression | `ImageCodecApiVersion` |
| 12 | 네이티브 코드에서 텍스처 전체와 UDIM atlas를 한 번의 호출로 빌드 | `ImageBuildApiVersion` |

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

### USD composition 유지

기본 설정에서는 모든 오브젝트를 하나의 레이어에 전부 풀어서 기록합니다. `Composition = UsdCompositionMode.Preserve`로 설정하면 USD composition을 유지합니다. 이때 아래 세 가지가 복사본이 아니라 composition으로 기록됩니다.

**Import한 stage는 원본 파일 위의 override로 기록됩니다.** Importer는 생성한 루트에 `UsdStageSource` 컴포넌트를, 각 오브젝트에 `UsdPrimSource` 컴포넌트를 붙여 각 오브젝트가 어떤 prim이고 import 직후 어떤 상태였는지 기록합니다. Export 루트가 그 import 루트라면, 출력 파일은 원본을 sublayer로 두고 Unity에서 바뀐 것만 담습니다.

| Unity에서 한 변경 | 기록 방식 |
| --- | --- |
| 오브젝트 이동, 회전, 스케일 | `xformOp:transform`을 가진 `over` |
| 오브젝트 비활성화, renderer 끄기 | `visibility = "invisible"` |
| 오브젝트 삭제 | 해당 prim에 `active = false` |
| 오브젝트 추가 | 부모 prim 아래의 새 prim |
| Variant를 선택해 import | 그 variant 선택 |
| 인스턴스 안쪽 오브젝트 변경 | 인스턴스에 `instanceable = false`를 쓴 뒤 변경 내용 |
| Mesh나 material 교체 | 원본 prim에 `active = false`, 옆에 새 prim |

```csharp
UsdImportResult imported = UsdImporter.Import(sourcePath);
// ... 사용자가 imported.RootObject를 편집 ...
UsdExporter.ExportGameObjectWithResult(imported.RootObject, editedPath, new UsdExportOptions
{
    Composition = UsdCompositionMode.Preserve,
});
```

출력은 아래와 같은 형태입니다. 원본의 reference, payload, variant set, inherit, specialize는 그대로 남습니다.

```usda
#usda 1.0
(
    defaultPrim = "World"
    subLayers = [@./scene.usda@]
)

over "World"
{
    over "Robot" { matrix4d xformOp:transform = ( ... ) ... }
    over "Heavy" ( active = false ) { }
}
```

출력 파일은 원본 파일에 의존하므로 함께 보관하거나, 원본까지 패키징하는 `.usdz`로 export하세요. 출력 경로를 원본 파일 자체로 지정할 수는 없습니다. 오브젝트는 이름이 아니라 prim 경로로 식별하므로 이름을 바꿔도 결과는 달라지지 않습니다. 오브젝트를 다른 부모 아래로 옮기는 것은 override로 표현할 수 없어서, 원본 prim을 비활성화하고 옮긴 위치에 새 콘텐츠로 기록합니다.

Import한 stage가 export 계층의 더 깊은 곳에 있거나 `ImportedContent = UsdImportedContentMode.ReferenceSource`이면, stage의 default prim에 해당하는 오브젝트가 원본 파일을 reference하고 변경 내용은 그 reference 위의 override로 기록됩니다. Default prim이 아닌 루트 prim은 reference로 가져올 수 없어서 콘텐츠로 기록합니다. `UsdImportedContentMode.Inline`은 import한 오브젝트도 일반 오브젝트처럼 기록합니다.

**Prefab 인스턴스는 prefab당 하나의 에셋을 reference합니다.** Prefab은 한 번만 기록하고 각 인스턴스가 이를 reference합니다. 인스턴스가 바꾼 내용(자식 이동, 자식 숨김, 자식 추가와 삭제, mesh나 material 변경)은 그 인스턴스의 override로 기록합니다. 변경이 없는 인스턴스는 `instanceable`로도 표시합니다. Prefab 에셋의 위치는 `Layout`으로 정합니다.

| Layout | 결과 |
| --- | --- |
| `SingleFile` (기본값) | 파일 하나. Prefab 에셋은 `/UnityPrototypes` 아래의 `class` prim이 되고, 파일 안에서 reference합니다. |
| `FolderWithAssets` | Scene 파일과 함께 `<usd-name>_assets/`에 prefab당 파일 하나를 쓰고, 상대 경로로 reference합니다. |

출력 경로가 `.usdz`로 끝나면 어느 layout이든 에셋 파일까지 함께 패키징합니다.

Unity는 prefab과 인스턴스의 연결을 Editor에서만 유지합니다. 이 연결을 Play mode와 player까지 가져가기 위해, 패키지는 scene을 빌드하거나 Play mode에 들어갈 때 scene의 모든 prefab 인스턴스 루트에 `UsdPrefabMarker` 컴포넌트를 붙입니다. 바뀌는 것은 그 scene의 복사본뿐이며 디스크의 scene 파일과 prefab은 그대로입니다. 런타임에 prefab을 생성할 때는 `Object.Instantiate` 대신 `UsdPrefabMarker.Instantiate(prefab, parent)`를 쓰거나 `UsdPrefabMarker.Attach(instance, prefab)`를 호출하세요. 일반 `Object.Instantiate`로 만든 복사본은 연결 정보가 없어서 일반 오브젝트로 export됩니다. Play mode가 아닌 Editor에서는 exporter가 prefab 연결을 직접 읽습니다. 자동 marker를 끄려면 scripting symbol `USD_TOOLKIT_DISABLE_PREFAB_MARKERS`를 정의하세요.

**반복되는 mesh는 하나의 prototype을 공유하는 인스턴스가 됩니다** (`ShareMeshes = true`일 때). 같은 `Mesh`와 같은 material을 쓰는 오브젝트들이 prototype mesh 하나를 reference하고 `instanceable`로 표시됩니다.

`UsdExportResult`는 `ReferenceCount`, `OverrideCount`, `PrototypeCount`, override export가 의존하는 `SourceLayer`, 그리고 기록한 `AssetFiles`를 보고합니다.

`Preserve`는 `TransformPolicy` 값과 관계없이 항상 계층을 유지합니다. 네이티브 API 6이 필요하며, 이전 네이티브 플러그인에서는 그 사실을 알려 주는 예외가 발생합니다.

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
| `Composition` | `Flatten` | `Flatten`은 모든 오브젝트를 풀어서 기록하고, `Preserve`는 USD composition을 유지합니다. [USD composition 유지](#usd-composition-유지)를 참고하세요. |
| `Layout` | `SingleFile` | `Preserve`에서 `SingleFile` 또는 `FolderWithAssets` |
| `ImportedContent` | `OverrideSource` | `Preserve`에서 import한 stage를 기록하는 방식. `OverrideSource`, `ReferenceSource`, `Inline` |
| `ExportPrefabsAsReferences` | `true` | `Preserve`에서 각 prefab을 한 번만 기록하고 인스턴스가 reference합니다. |
| `ShareMeshes` | `false` | `Preserve`에서 mesh와 material이 같은 오브젝트들이 prototype 하나를 reference합니다. |

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

기본값(`StreamTextures = true`)에서 `ImportAsync`는 계층 구조, mesh, 텍스처 없는 material이 만들어지면 바로 반환합니다. 텍스처는 worker 스레드에서 디코딩이 끝나는 대로 붙습니다. 그 전까지 material은 작성된 상수 값으로 보입니다. Export, 캡처, prefab 저장, material 텍스처 읽기를 하기 전에는 `UsdImportResult.TexturesReady`를 await하세요.

```csharp
UsdImportResult result = await UsdImporter.ImportAsync(path);
await result.TexturesReady; // 모든 텍스처가 material에 붙은 상태
UsdExporter.ExportGameObject(result.RootObject, outputPath);
```

`TexturesReady`는 실패 상태로 끝나지 않습니다. 로드에 실패한 텍스처는 경고로 기록되고, 해당 material은 상수 값을 유지합니다. 텍스처가 도착하기 전에 import 루트가 삭제되면 남은 텍스처는 버려지고 메모리도 해제됩니다. 텍스처까지 모두 로드된 뒤에 `ImportAsync`가 반환되게 하려면 `StreamTextures = false`로 설정하세요.

폴더의 USD 파일과 썸네일 목록을 가져오려면 다음과 같이 합니다.

```csharp
IReadOnlyList<UsdLibraryItem> items = UsdLibraryScanner.ScanFolder(folderPath);
```

### Up axis와 단위

Importer는 USD 좌표계를 Unity 좌표계로 변환하고, `ApplyStageUpAxisAndUnits = true`(기본값)이면 stage의 `upAxis`와 `metersPerUnit`을 import 루트에 적용합니다. 따라서 내용은 Y-up, 미터 단위로 들어옵니다. Isaac Sim stage처럼 Z-up인 stage는 루트에 X축 기준 -90도 회전이 들어가고, 루트는 `metersPerUnit`만큼 스케일됩니다(센티미터 stage는 0.01).

보정은 루트의 로컬 회전과 스케일에만 들어갑니다. Vertex와 각 prim의 로컬 transform은 원본 그대로이므로 `Preserve` export는 이 보정에 대한 override를 기록하지 않습니다. 적용된 값은 `UsdImportResult.StageCorrectionRotation`과 `UsdImportResult.StageCorrectionScale`로 확인할 수 있고, `UpAxis`와 `MetersPerUnit`은 계속 `UsdImportPreviewInfo`와 `UsdImportResult`로 반환됩니다.

이전처럼 identity 루트 아래에 stage 원래 좌표로 가져오려면 보정을 끄세요.

```csharp
UsdImportResult result = await UsdImporter.ImportAsync(path, new UsdImportOptions
{
    ApplyStageUpAxisAndUnits = false
});
```

### Variant set과 payload

Stage를 합성하기 전에 variant를 고르고, 어떤 payload를 로드할지 정할 수 있습니다. 원본 파일은 바뀌지 않습니다.

```csharp
var options = new UsdImportOptions { LoadPayloads = false };
options.VariantSelections.Add(new UsdVariantSelection("/World/Robot", "color", "red"));
options.PayloadsToLoad.Add("/World/Robot/Arm");

UsdImportResult result = UsdImporter.Import(path, options);
```

선택은 목록 순서대로 적용되므로, 다른 variant 안에 중첩된 variant set은 바깥 선택 뒤에 두면 됩니다. 존재하지 않는 선택은 건너뛰고 진단 정보에 경고를 남깁니다. `LoadPayloads = false`이면 `PayloadsToLoad`에 있는 prim만 payload를 로드합니다. 로드하지 않은 payload의 prim은 빈 GameObject로 생성되어 계층에서 자리를 유지합니다.

`result.Source`(`UsdStageSource`)는 stage가 제공하는 정보를 담습니다. `VariantSets`에는 variant 목록과 현재 선택이, `Payloads`에는 각 payload의 로드 여부가, `Arcs`에는 직접 연결된 reference, payload, inherit, specialize, relocate가 들어 있습니다. Variant를 바꾸려면 `CreateOptionsWithVariant`가 돌려주는 옵션으로 다시 import하세요.

```csharp
UsdImportOptions next = result.Source.CreateOptionsWithVariant("/World/Robot", "color", "blue");
UsdImportResult switched = UsdImporter.Import(result.Source.SourcePath, next);
```

`Preserve` export는 import 때의 variant 선택을 다시 기록하므로, 출력 파일에도 Unity에서 고른 variant가 보입니다.

### Point instancer

`UsdGeomPointInstancer`는 prototype 하나를 여러 placement에 흩뿌린 것이고, importer도 그 형태를 유지합니다. 각 prototype은 숨겨진 GameObject로 한 번만 import되고, instancer prim은 `UsdPointInstancer` 컴포넌트를 가진 GameObject가 됩니다. 이 컴포넌트는 prototype의 `Mesh`를 `Materials`와 함께 모든 placement에 `Graphics.RenderMeshInstanced`로 그립니다. Placement는 각각 자체 bounds를 가진 cell 격자로 정렬되므로, 화면 밖의 cell은 그리지 않습니다. 인스턴스는 개별 선택이 되지 않고 collider도 없습니다. 부품을 하나씩 선택해야 하는 stage라면 instancer보다 prim이 적합합니다.

`UsdPointInstancer`는 `Mesh`, `Materials`, `InstanceCount`, `LodDistance`, `CellCount`, `Draw`를 제공합니다. 컴포넌트가 매 프레임 `Draw`를 호출하며, 커스텀 렌더 루프나 벤치마크에서 직접 호출할 수 있도록 public입니다. Import 한 번에 허용되는 placement는 모든 instancer를 합쳐 최대 4,000,000개(인스턴스 수 × prototype mesh 수)이고, 이를 넘으면 import가 거부됩니다.

모든 placement는 거리와 관계없이 최대 디테일로 그려집니다. Mesh LOD를 쓰려면 import 루트를 선택하고 **GameObject > USD > Generate Mesh LODs for Point Instancers**(`Unity.USDToolkit.Editor.UsdMeshLodGenerator`)를 선택하세요. 각 prototype mesh에 최대 4개의 추가 레벨을 만들고, placement가 퍼진 범위로 `LodDistance`를 설정합니다. 레벨은 cell마다 메인 카메라까지의 거리로 고르며, `LodDistance`에서 가장 낮은 디테일 레벨에 도달합니다. 0이면 항상 최대 디테일로 그립니다. 레벨 생성은 mesh decimation이 Editor API이므로 Editor에서만 가능하지만, mesh에 이미 레벨이 있으면 레벨 선택은 플레이어에서도 동작합니다. Decimation은 나뭇잎 같은 geometry를 눈에 띄게 줄이므로 stage마다 결과를 확인하세요.

`UsdImportResult.DefaultMaterial`은 material binding이 없는 mesh에 할당된 공유 회색 material을 담고, 필요한 mesh가 없었으면 `null`입니다. `UniqueMeshCount`는 import가 만든 고유 `Mesh` 오브젝트 수입니다.

### Import 옵션

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `Parent` | `null` | Import된 루트의 부모 `Transform` |
| `RootObjectName` | `null` | 루트 GameObject 이름. 비어 있으면 파일 이름을 사용합니다. |
| `ImportMaterials` | `true` | `UsdPreviewSurface`, MaterialX, MDL(`OmniPBR`, `OmniSurface`, `OmniGlass`) 값으로 Unity material을 만듭니다. [7. Material과 텍스처](#7-material과-텍스처)를 참고하세요. |
| `ImportTextures` | `true` | Material이 참조하는 PBR 텍스처를 로드합니다. |
| `VerifyNativeRuntimeIntegrity` | `true` | 플러그인을 처음 호출하기 전에 각 네이티브 파일의 SHA-256 digest를 확인합니다. Editor와 development 빌드에서만 반영되며, release 플레이어는 항상 확인합니다. |
| `CompressTextures` | `true` | Import한 텍스처를 worker 스레드에서 mipmap과 함께 block compression합니다. 불투명 이미지는 BC1, alpha가 있는 이미지는 BC3입니다. 식별자나 lookup table처럼 외형이 아니라 데이터를 담은 텍스처는 압축하면 값이 틀어지므로 끄세요. |
| `IncludeInvisible` | `true` | `visibility = "invisible"`인 prim도 생성합니다. |
| `GenerateColliders` | `false` | 각 mesh에 `MeshCollider`를 추가합니다. |
| `RecalculateNormalsIfMissing` | `true` | 파일에 normal이 없으면 smooth normal을 계산합니다. |
| `AllowExternalAssetPaths` | `false` | Stage 폴더 밖의 텍스처를 허용합니다. [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| `MaxMillisecondsPerFrame` | `10f` | `ImportAsync`가 프레임마다 메인 스레드에서 사용할 수 있는 시간 |
| `StreamTextures` | `true` | `ImportAsync`가 텍스처를 붙이기 전에 반환합니다. 최종 material은 `UsdImportResult.TexturesReady`를 await해서 받습니다. `UsdImporter.Import`에서는 무시됩니다. |
| `ProgressCallback` | `null` | 메인 스레드에서 `0`~`1` 진행률과 현재 단계를 받습니다. `StreamTextures`를 쓰면 `ImportAsync`는 `0.9`("Streaming textures")에서 반환되고, 텍스처가 모두 붙으면 `1`("Done")이 보고됩니다. |
| `CaptureNativeDiagnostics` | `false` | OpenUSD 경고와 오류를 수집합니다. |
| `NativeDiagnosticsLogPath` | `null` | 수집한 진단 정보를 이 파일에 기록합니다. |
| `VariantSelections` | 비어 있음 | 합성할 때 적용할 variant 선택(순서대로). [Variant set과 payload](#variant-set과-payload)를 참고하세요. |
| `LoadPayloads` | `true` | `false`이면 `PayloadsToLoad`에 있는 prim만 payload를 로드합니다. |
| `PayloadsToLoad` | 비어 있음 | `LoadPayloads`가 `false`일 때 로드할 payload prim |
| `RecordSource` | `true` | `UsdStageSource`와 `UsdPrimSource` 컴포넌트를 붙입니다. `Preserve` export가 변경분만 기록하려면 필요합니다. |
| `ApplyStageUpAxisAndUnits` | `true` | Stage의 `upAxis`와 `metersPerUnit`을 import 루트에 적용합니다. [5. 런타임 Import](#5-런타임-import)의 Up axis와 단위를 참고하세요. |
| `AssignDefaultMaterial` | `true` | Material binding이 없는 mesh나 submesh에 공유 회색 lit material `UsdDefaultMaterial`을 할당합니다. 끄면 material이 비어 URP와 HDRP에서 마젠타로 보입니다. `ImportMaterials`가 켜져 있을 때만 사용합니다. |
| `ShareIdenticalMeshes` | `true` | 서로 다른 geometry마다 Unity `Mesh`를 하나씩 만들고, point, index, normal, UV set, submesh 범위가 같은 mesh끼리 공유합니다. Instanceable prim과 `PointInstancer` 인스턴스가 이런 경우입니다. `MeshFilter.sharedMesh`를 수정하면 이를 쓰는 모든 오브젝트에 반영되고, `MeshFilter.mesh`는 오브젝트별 복사본을 만듭니다. 끄면 prim마다 `Mesh`를 하나씩 만듭니다. |
| `ImportLights` | `true` | UsdLux distant, sphere, disk, cylinder, rect light마다 Unity `Light`를 추가합니다. [Light, camera, spatial audio](#light-camera-spatial-audio)를 참고하세요. |
| `ImportCameras` | `true` | `UsdGeomCamera`마다 비활성화된 Unity `Camera`를 추가합니다. 직접 켜기 전까지 씬의 기존 카메라가 계속 렌더링합니다. |
| `ImportSpatialAudio` | `true` | `UsdMediaSpatialAudio`마다 `AudioSource`와 `UsdSpatialAudio` 컴포넌트를 추가합니다. Clip은 로드하지 않습니다. |
| `ApplyDomeLightToEnvironment` | `false` | `UsdLuxDomeLight`를 씬에 적용합니다. 색상과 intensity를 곱한 값이 flat ambient 색상이 되고, PNG나 JPG 텍스처는 panoramic skybox가 됩니다. 씬 전체의 `RenderSettings`를 바꾸므로 기본값은 꺼져 있습니다. Dome light는 항상 `UsdDomeLight` 컴포넌트에 기록됩니다. |

### Light, camera, spatial audio

이 기능들은 네이티브 API 7이 필요하며, 배포된 모든 payload에 포함되어 있습니다. 네이티브 플러그인이 API 7보다 오래된 경우에도 import는 그대로 동작하고, 이 prim들은 컴포넌트 없이 빈 transform으로 들어옵니다.

- **Light와 camera.** USD의 light와 camera는 로컬 -Z 방향을, Unity는 +Z 방향을 기준으로 합니다. 그래서 `Light`나 `Camera`는 Y축으로 180도 돌린 자식 GameObject에 붙고, 이 자식에는 `UsdImportHelperObject`가 표시됩니다. 이 자식은 prim이 아니므로 exporter는 이 오브젝트와 그 아래를 건너뜁니다. USD에는 light intensity의 단일 단위 규약이 없어 Unity로의 변환은 근사값입니다. 원본 렌더러와 결과를 비교해 확인하세요.
- **Dome light.** Unity에는 dome light가 없습니다. 기록된 색상, intensity, 텍스처 경로는 `UsdDomeLight` 컴포넌트에 남습니다. `RenderSettings`에도 적용하려면 `ApplyDomeLightToEnvironment = true`로 설정하세요.
- **Spatial audio.** 오디오 파일은 비동기로 로드되므로 importer는 `AudioSource`만 설정하고 clip은 로드하지 않습니다. `UsdSpatialAudio.FilePath`를 `UnityWebRequestMultimedia` 등으로 로드한 뒤 할당하세요.

## 6. 스레드

Unity 오브젝트를 생성하거나 읽는 메서드는 메인 스레드에서 실행해야 합니다.

`ImportAsync`는 import 작업을 여러 스레드로 나눕니다. 메인 스레드에서 호출하세요. Stage 읽기, PNG와 EXR 디코딩, UDIM 타일 합치기, metallic과 roughness 맵 및 albedo와 opacity 맵 packing, block compression은 worker 스레드에서 병렬로 실행됩니다. 플러그인이 API 11이나 12이면 네이티브 코드로, 그렇지 않으면 managed 코드로 실행됩니다. GameObject, mesh, material은 메인 스레드에서 프레임당 `MaxMillisecondsPerFrame` 안에서 생성됩니다. `ImportAsync`는 오브젝트와 텍스처 없는 material이 만들어지면 반환되고, 이후 디코딩이 끝나는 텍스처를 `TexturesReady`가 완료될 때까지 메인 스레드에서 계속 붙입니다. JPEG 파일과 16-bit PNG는 메인 스레드에서 `Texture2D.LoadImage`로 디코딩합니다.

| 메서드 | 스레드 |
| --- | --- |
| `UsdImporter.ImportAsync` | 메인 스레드에서 호출합니다. Stage 읽기와 텍스처 디코딩은 worker 스레드에서, 오브젝트 생성은 메인 스레드에서 실행됩니다. |
| `UsdImporter.Import` | 호출한 스레드에서 실행됩니다. 텍스처 디코딩만 worker 스레드를 사용합니다. 메인 스레드에서 호출하세요. |
| `UsdImporter.GetPreviewInfo` | 모든 스레드. Unity 오브젝트를 사용하지 않습니다. |
| `UsdImporter.LoadImageFile` | 메인 스레드. `Texture2D`를 생성합니다. |
| `UsdLibraryScanner.ScanFolder` | 모든 스레드. 파일 목록만 읽고 Unity 오브젝트를 사용하지 않습니다. |
| `UsdExporter.ExportGameObject`, `ExportGameObjectWithResult` | 메인 스레드. Mesh, material, 텍스처를 읽습니다. |
| `UsdPointInstancer.Draw` | 메인 스레드, 매 프레임. 컴포넌트가 `Update`에서 호출합니다. |

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

Import된 material은 `Universal Render Pipeline/Lit`, `HDRP/Lit`, `Standard` 중 처음 사용할 수 있는 셰이더를 사용합니다. 텍스처는 `_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_OcclusionMap`, `_EmissionMap`에 할당되며, tiling과 offset은 `UsdTransform2d`에서 가져옵니다. 각 텍스처 파일은 한 번만 로드되어 material 간에 공유됩니다.

Importer가 읽는 surface shader는 다음과 같습니다.

| 원본 | 읽는 내용 |
| --- | --- |
| `UsdPreviewSurface` | Base color, opacity, metallic, roughness, emission과 각 텍스처 |
| MaterialX(`standard_surface`, `open_pbr_surface`, `UsdPreviewSurface`) | 같은 값과 텍스처. Stage 안에 기록된 material과 참조된 `.mtlx` 파일 모두 읽습니다. |
| MDL `OmniPBR`, `OmniSurface`, `OmniGlass` | 색상과 텍스처. 다른 MDL material은 읽지 않습니다. |
| `diffuseColor`에 연결된 `UsdPrimvarReader` | `displayColor` 같은 primvar 색상을 base color로 사용합니다. |

하위 폴더에 있는 material layer가 참조하는 텍스처는 그 layer 기준으로 경로를 찾습니다. Material binding이 없는 mesh나 submesh에는 `AssignDefaultMaterial = false`가 아니면 공유 회색 material `UsdDefaultMaterial`이 할당됩니다.

### 텍스처 포맷과 packing

Importer가 읽는 이미지 포맷은 다음과 같습니다.

| 포맷 | 비고 |
| --- | --- |
| PNG | 8-bit, non-interlaced 파일은 메인 스레드 밖에서 디코딩합니다. 그 외 PNG는 메인 스레드에서 `Texture2D.LoadImage`로 읽습니다. |
| JPEG | 메인 스레드에서 `Texture2D.LoadImage`로 읽습니다. |
| OpenEXR | `NONE`, `RLE`, `ZIPS`, `ZIP` compression의 scanline과 tiled 파일. half, float, uint를 지원합니다. Mipmap이 있는 파일은 level 0을 읽습니다. 픽셀은 8 bit로 양자화되고, 색상 슬롯에서는 sRGB로 인코딩됩니다. |

`<UDIM>` 텍스처 경로는 타일 격자를 뜻합니다. 한 set의 타일은 UV 공간에서의 위치대로 atlas 하나로 합쳐지며, atlas에는 set에 있는 행과 열만 들어갑니다. Mesh의 UV는 그대로이고, material의 base map scale과 offset이 UV를 atlas 위에 배치합니다. 그래픽 장치가 허용하는 크기보다 큰 atlas는 들어갈 때까지 절반으로 줄입니다. 타일은 PNG나 OpenEXR이어야 하며, 다른 포맷의 타일은 경고와 함께 제외됩니다.

Mesh와 `GeomSubset`의 material은 `material:binding:full`, `material:binding:preview`, 범용 `material:binding` 순서로 찾습니다.

별도의 metallic, roughness, occlusion 맵은 worker 스레드에서 Unity 레이아웃으로 packing됩니다. `_MetallicGlossMap`의 red 채널에 metallic, alpha 채널에 smoothness(1 − roughness)가 들어가고, occlusion 맵은 `_OcclusionMap`에 들어갑니다. OmniPBR의 ORM 맵(red, green, blue에 occlusion, roughness, metallic)도 같은 방식으로 다시 packing되어 occlusion은 green에 들어가고, 두 슬롯 모두에 할당됩니다. Albedo의 alpha가 아닌 별도의 opacity 맵은 albedo의 alpha 채널로 합쳐집니다. 텍스처는 기본적으로 block compression됩니다. `CompressTextures`를 참고하세요.

### Metallic 슬롯의 albedo 텍스처

Base color 텍스처를 metallic 슬롯(`_MetallicGlossMap`)에 할당하면 거울 같은 표면이 됩니다. Unity에서는 skybox나 reflection probe가 반사할 대상을 제공하므로 정상으로 보일 수 있지만, Isaac Sim 뷰포트처럼 환경 반사가 없는 뷰어에서는 검은색으로 렌더링됩니다.

Material의 metallic 맵이 albedo 맵과 같은 텍스처이면, exporter는 metallic 맵을 무시하고 material의 **Metallic** 값을 사용하며 경고를 출력합니다. 의도적으로 두 슬롯에 같은 텍스처를 쓰려면 `IgnoreAlbedoInMetallicSlot = false`로 설정하세요.

## 8. 신뢰할 수 없는 USD 파일

Importer는 다운로드한 에셋, 공유받은 `.usdz`, 스캔한 폴더의 파일 등 모든 USD 파일을 신뢰할 수 없는 입력으로 취급합니다. 다음 보호 기능이 기본으로 켜져 있습니다.

### 텍스처 경로

USD 파일은 자체적으로 텍스처 경로를 지정하며, 이 경로는 디스크의 어느 곳이든 가리킬 수 있습니다. Importer는 stage나 그 레이어의 폴더 밖으로 해석되는 텍스처를 건너뛰고, 파일에 적힌 경로로 경고를 출력합니다. `.usdz` 내부 텍스처는 영향을 받지 않습니다. Stage 폴더 아래의 symbolic link나 junction을 거치는 경로도 거부합니다. Link는 어디든 가리킬 수 있기 때문입니다. `<UDIM>` set의 타일은 폴더 목록으로 찾으며, 타일마다 같은 방식으로 범위를 제한합니다.

다른 폴더의 공용 텍스처 라이브러리를 참조하거나 link를 거쳐 텍스처를 읽는 신뢰할 수 있는 stage라면 `UsdImportOptions.AllowExternalAssetPaths = true`로 설정하세요.

### Mesh topology

Face 데이터가 일치하지 않는 mesh는 건너뛰고, prim 경로와 함께 경고를 출력합니다.

### 텍스처 크기

한 변이 16,384 픽셀을 넘거나 전체 6,400만 픽셀을 넘는다고 선언한 이미지는 모두 거부합니다. 이 제한은 PNG, JPEG, OpenEXR 파일과 UDIM atlas에 적용됩니다. 512 MB를 넘는 이미지 파일은 읽기 전에 거부합니다. 헤더가 압축된 바이트로 풀 수 있는 것보다 많은 픽셀을 선언한 PNG나 EXR은 메모리를 할당하기 전에 거부합니다.

### Point instancer

Point instancer의 placement 합계가 4,000,000개(인스턴스 수 × prototype mesh 수)를 넘는 import는 제한값을 알려 주는 오류와 함께 거부됩니다. 자기 instancer, 그 상위 prim, 또는 이미 펼치는 중인 instancer를 포함하는 prototype은 자신을 다시 import하게 되므로 경고와 함께 건너뜁니다.

### 플러그인 탐색 경로

OpenUSD는 `plugInfo.json` 파일이 지정한 라이브러리를 실행합니다. 따라서 `UsdExportOptions.PluginSearchPath`는 패키지 자체의 네이티브 폴더 안에 있어야 합니다. 커스텀 OpenUSD 설치를 사용하려면 코드에서 `UsdExporter.AllowExternalPluginSearchPath = true`로 설정하세요. 이 설정은 static이며 직렬화되지 않으므로, 씬이나 프리팹에 저장된 옵션 오브젝트로는 켤 수 없습니다.

## 9. Standalone 플레이어 빌드

1. **Build Profiles**에서 Windows, macOS, Linux 중 하나를 선택합니다. macOS에서는 플레이어 아키텍처가 네이티브 라이브러리와 일치해야 합니다.
2. Mono 또는 IL2CPP로 빌드합니다.
3. 출력 파일은 `Application.persistentDataPath` 아래에 기록하세요.

빌드할 때 패키지가 OpenUSD 파일을 플레이어에 복사합니다.

```text
<Build>/<App>_Data/Plugins/x86_64/Windows/     (Windows)
<Build>/<App>.app/Contents/PlugIns/            (macOS)
<Build>/<App>_Data/Plugins/x86_64/Linux/       (Linux)
```

다른 PC에 배포하려면 Unity가 빌드한 macOS 앱 번들에 서명하세요.

## 10. USD Live Sync 예제

`Samples~/Live Sync Example`은 실행 중인 Unity 씬과 NVIDIA Isaac Sim이나 Python 스크립트 같은 외부 도구 사이에서 transform을 양방향으로 동기화합니다. Unity는 씬 geometry를 `base_stage.usda`로 한 번 export한 뒤, transform 변경 사항을 TCP로 스트리밍합니다.

모든 연결은 데이터를 받기 전에 토큰으로 인증해야 합니다. 서버는 로컬 머신의 연결만 받으며, 트래픽은 암호화되지 않습니다.

설정 방법은 [Live Sync Example 가이드](../Samples~/Live%20Sync%20Example/README.md)를 참고하세요.

## 11. Export한 USD 파일 확인

- `usdchecker <file>`로 구조와 유효성을 확인합니다.
- `usdcat <file>`로 내용을 텍스트로 출력합니다.
- usdview, Isaac Sim, Omniverse, Blender에서 geometry와 material을 확인합니다.

## 12. 문제 해결

| 증상 | 원인 | 해결 방법 |
| --- | --- | --- |
| `DllNotFoundException` | 네이티브 파일이 없거나 아직 Git LFS 포인터 파일입니다. | `git lfs pull`을 실행하고 `Runtime/Plugins` 아래 플랫폼 폴더를 확인하세요. |
| 네이티브 API 버전 불일치 | 네이티브 플러그인이 패키지가 허용하는 최소 버전인 API 6보다 오래되었습니다. | 패키지를 다시 설치하거나 플러그인을 다시 빌드하세요. [부록 C](#부록-c-네이티브-플러그인-다시-빌드하기)를 참고하세요. |
| Digest 불일치로 네이티브 플러그인이 거부됨 | 네이티브 파일이 패키지에 포함된 파일과 다릅니다. | 패키지를 다시 설치하세요. 플러그인을 다시 빌드했다면 digest를 다시 생성하세요. [부록 C](#부록-c-네이티브-플러그인-다시-빌드하기)를 참고하세요. |
| OpenUSD 플러그인 경로 오류 | `plugin/usd` 또는 `lib/usd` 폴더가 없습니다. | `plugInfo.json` 파일이 있는지 확인하세요. |
| `PluginSearchPath`가 거부됨 | 경로가 패키지의 네이티브 폴더 밖에 있습니다. | [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| Mesh를 읽을 수 없음 | **Read/Write Enabled**가 꺼져 있고 그래픽 장치가 없습니다. | **Read/Write Enabled**를 켜거나 Play mode에서 export하세요. |
| Isaac Sim에서 표면이 검게 렌더링됨 | Albedo 텍스처가 metallic 슬롯에도 할당되어 있습니다. | [7. Material과 텍스처](#7-material과-텍스처)를 참고하세요. |
| Import 후 텍스처가 없음 | 텍스처 경로가 stage 폴더 밖을 가리킵니다. | Console 경고를 확인하세요. 신뢰할 수 있는 파일이라면 `AllowExternalAssetPaths = true`로 설정하세요. |
| Import 후 일부 mesh가 없음 | Mesh의 face 데이터가 일치하지 않습니다. | Console 경고에 prim이 표시됩니다. 원본 파일을 수정하세요. |
| Import한 stage의 회전이나 스케일이 예상과 다름 | `ApplyStageUpAxisAndUnits`가 stage의 `upAxis`와 `metersPerUnit`을 import 루트에 적용합니다. | [5. 런타임 Import](#5-런타임-import)의 Up axis와 단위를 참고하세요. Stage 원래 좌표가 필요하면 `ApplyStageUpAxisAndUnits = false`로 설정하세요. |
| Import 후 매끄러운 표면이 각져 보임 | Subdivision surface이고 control cage만 가져왔습니다. | 경고에 prim이 표시됩니다. 원본 도구에서 subdivision을 적용한 mesh로 export하세요. |
| 파일이 생성되지 않음 | 출력 폴더에 쓸 수 없거나 export 중 예외가 발생했습니다. | `Application.persistentDataPath` 아래에 기록하고 Console을 확인하세요. |
| Editor에서는 동작하지만 플레이어에서는 동작하지 않음 | 지원하지 않는 플랫폼이거나 빌드에 네이티브 파일이 없습니다. | Windows x64, macOS, Linux x64 중 하나로 빌드하고 플레이어의 `Plugins` 폴더를 확인하세요. |
| `ImportAsync` 직후 material이 단색으로 보임 | `StreamTextures`(기본값)에서는 `ImportAsync`가 텍스처를 붙이기 전에 반환됩니다. | `UsdImportResult.TexturesReady`를 await하거나 `StreamTextures = false`로 설정하세요. |
| UDIM 타일이 빠지거나 atlas가 예상보다 작음 | 타일이 PNG나 OpenEXR이 아니거나, 그래픽 장치의 텍스처 제한에 맞춰 atlas를 절반으로 줄였습니다. | Console 경고를 확인하세요. 타일 이름 또는 atlas 크기와 제한값이 표시됩니다. |
| EXR 텍스처가 거부됨 | `NONE`, `RLE`, `ZIPS`, `ZIP` 이외의 compression을 쓰거나, 채널이 64개를 넘거나, 크기 제한을 넘습니다. | 지원되는 compression과 더 적은 채널로 텍스처를 다시 export하세요. [8. 신뢰할 수 없는 USD 파일](#8-신뢰할-수-없는-usd-파일)을 참고하세요. |
| Point instancer 인스턴스가 선택되지 않고 collider가 없음 | 의도된 동작입니다. `UsdPointInstancer`는 GameObject가 아니라 배열로 placement를 그립니다. | [Point instancer](#point-instancer)를 참고하세요. 개별 선택이 필요한 부품은 prim으로 작성하세요. |
| Point instancer가 너무 조밀하다며 거부됨 | Stage가 placement 예산 4,000,000개(인스턴스 수 × prototype mesh 수)를 넘습니다. | 원본에서 인스턴스 수나 prototype mesh 수를 줄이세요. |

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
| `UsdImportResult.TexturesReady` | 모든 텍스처가 material에 붙으면 완료되는 `Task`. 실패 상태로 끝나지 않습니다. |
| `UsdImportResult.UniqueMeshCount` | Import가 만든 고유 `Mesh` 오브젝트 수 |
| `UsdImportResult.DefaultMaterial` | 공유 회색 `UsdDefaultMaterial`. 필요한 mesh가 없었으면 `null` |
| `UsdLibraryItem` | `ScanFolder`가 찾은 USD 파일 |
| `UsdRuntimeInfo` | 네이티브 플러그인 버전, OpenUSD 버전, 탐색 경로 |
| `UsdExportException`, `UsdImportException` | 진단 정보를 포함한 오류 |
| `UsdTransformPolicy` | `BakedMesh` 또는 `PreserveHierarchy` |
| `UsdCompositionMode`, `UsdExportLayout`, `UsdImportedContentMode` | Export composition 설정 |
| `UsdStageSource`, `UsdPrimSource` | Import한 오브젝트의 출처를 기록하는 컴포넌트 |
| `UsdStageSource.CreateOptionsWithVariant` | 다른 variant를 선택해 stage를 다시 import하는 옵션을 반환합니다. |
| `UsdPrefabMarker` | Export를 위해 런타임 오브젝트를 prefab과 연결합니다. |
| `UsdPrefabMarker.Instantiate`, `UsdPrefabMarker.Attach` | 연결 정보를 가진 prefab 인스턴스를 생성하거나, 기존 인스턴스에 연결 정보를 붙입니다. |
| `UsdVariantSelection`, `UsdVariantSetInfo`, `UsdPayloadInfo`, `UsdCompositionArcInfo`, `UsdPrimCompositionFlags` | Import한 stage의 composition 정보 |
| `UsdPointInstancer` | Point instancer의 prototype을 모든 placement에 그리는 컴포넌트. `Mesh`, `Materials`, `InstanceCount`, `LodDistance`, `CellCount`, `Draw` |
| `UsdDomeLight` | `UsdLuxDomeLight`의 색상, intensity, 텍스처 경로를 기록하는 컴포넌트 |
| `UsdSpatialAudio` | `UsdMediaSpatialAudio`의 파일 경로, 시작 시간, 종료 시간을 기록하는 컴포넌트 |
| `UsdImportHelperObject` | Light나 camera를 Unity 방향에 맞추기 위해 importer가 만든 자식 오브젝트 표시. Exporter는 이를 건너뜁니다. |
| `UsdMeshLodGenerator` | Editor 전용, `Unity.USDToolkit.Editor` 어셈블리. Point instancer prototype의 Mesh LOD 레벨을 생성합니다. `Generate(root, levels)` |

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
