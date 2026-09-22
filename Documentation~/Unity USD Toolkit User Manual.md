# Unity USD Toolkit 사용자 매뉴얼

Windows/macOS Standalone 런타임 USD Export 플러그인

문서 버전: 0.1.0  
작성일: 2026-05-11  
대상 독자: Unity 프로젝트에 패키지를 적용해 런타임 USD export를 사용하려는 개발자 및 기술 사용자

## 목차

- 플러그인 개요
- 지원 범위와 제한 사항
- 새 Unity 프로젝트에 패키지 적용하기
- Windows/macOS native runtime payload 확인
- 런타임 Export 사용 방법
- Export 옵션 설명
- Windows/macOS Standalone 빌드에서 사용하기
- 결과 USD 확인 방법
- 문제 해결
- 배포 전 체크리스트

## 1. 플러그인 개요

Unity USD Toolkit은 Unity로 빌드한 Windows Standalone x64 및 macOS Standalone 앱에서 런타임에 USD 파일을 export하기 위한 Unity 패키지입니다. Editor 전용 exporter가 아니라, 실제 빌드된 플레이어 안에서 GameObject의 mesh 데이터를 Pixar OpenUSD 기반 native plugin으로 전달해 `.usd`, `.usda`, `.usdc` 파일을 생성합니다.

### 핵심 구조

- Unity C# API는 `Unity.USDToolkit` namespace 아래에 제공됩니다.
- C#은 P/Invoke로 `UnityUSDToolkitNative` native plugin의 C ABI 함수만 호출합니다.
- native plugin은 Pixar OpenUSD C++ API를 사용해 USD stage, mesh, material, transform을 작성합니다.
- Unity 공식 USD Editor 패키지, USD.NET, `com.unity.exporter.usd`에 의존하지 않습니다.

## 2. 지원 범위와 제한 사항

| 항목 | 지원 여부 | 설명 |
| --- | --- | --- |
| Windows Standalone x64 | 지원 | 이 패키지의 우선 지원 대상입니다. |
| Unity Editor Windows | 지원 | 개발/테스트용으로 Editor에서도 export 가능합니다. |
| macOS Standalone x64/arm64/Universal | 지원 | macOS OpenUSD/native payload를 빌드해 포함해야 합니다. |
| Unity Editor macOS | 지원 | macOS native payload가 있을 때 개발/테스트용 export가 가능합니다. |
| Static Mesh | 지원 | `MeshFilter` + `MeshRenderer` 기반 GameObject를 export합니다. |
| Normals / UV0 | 지원 | 옵션으로 켜고 끌 수 있습니다. |
| Submesh별 Material Binding | 지원 | USD `GeomSubset` material binding으로 작성됩니다. |
| Hierarchy Transform 보존 | 지원 | `UsdTransformPolicy.PreserveHierarchy` 사용 시 Xform prim을 작성합니다. |
| Baked Mesh Transform | 지원 | 기본값입니다. transform을 mesh point에 bake합니다. |
| Simple UsdPreviewSurface | 지원 | base color, opacity, metallic, roughness scalar 값을 export합니다. |
| Texture Export | 미지원 | 이번 버전 범위 밖입니다. |
| Skinned Mesh / Animation | 미지원 | 이번 버전 범위 밖입니다. |
| USD Import | 미지원 | 이 패키지는 export 전용입니다. |
| Linux/mobile/WebGL | 미지원 | 이번 버전 범위 밖입니다. |

## 3. 새 Unity 프로젝트에 패키지 적용하기

릴리즈 ZIP을 받은 사용자는 아래 절차로 패키지를 적용할 수 있습니다.

### 방법 A: Packages 폴더에 직접 배치

1. 전달받은 `com.unity.usd-toolkit-0.1.0.zip` 압축을 풉니다.
2. 압축 안의 `com.unity.usd-toolkit` 폴더를 Unity 프로젝트의 `Packages` 폴더 아래에 복사합니다.
3. 최종 경로가 `<UnityProject>/Packages/com.unity.usd-toolkit/package.json` 형태인지 확인합니다.
4. Unity Editor를 열고 Package Manager 또는 Console에서 컴파일 오류가 없는지 확인합니다.

### 방법 B: Package Manager에서 package.json 추가

1. Unity Editor에서 `Window > Package Manager`를 엽니다.
2. `+` 버튼을 누르고 `Add package from disk`를 선택합니다.
3. 압축을 푼 패키지 폴더 안의 `package.json`을 선택합니다.
4. Package Manager에 `Unity USD Toolkit`이 표시되는지 확인합니다.

### manifest.json 직접 등록 예시

```json
"dependencies": {
  "com.unity.usd-toolkit": "file:Packages/com.unity.usd-toolkit"
}
```

주의: `file:` 경로는 프로젝트 구조에 따라 달라질 수 있습니다. 팀 프로젝트에서는 패키지를 `Packages` 폴더에 직접 포함하거나 사내 UPM registry/공유 경로 정책에 맞춰 배포하는 방식을 권장합니다.

## 4. Windows/macOS native runtime payload 확인

패키지에는 플랫폼별 런타임 export에 필요한 native plugin payload가 포함되어야 합니다. Windows에서는 다음 파일/폴더가 있는지 확인합니다.

```text
Packages/com.unity.usd-toolkit/Runtime/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_ms.dll
  tbb.dll
  plugin/usd/plugInfo.json
  lib/usd/plugInfo.json
```

macOS 런타임 export를 사용하려면 `Build~/build_openusd_macos.sh`로 Pixar OpenUSD monolithic shared build를 만들고, `Native~/build_macos.sh`로 wrapper와 runtime payload를 설치합니다.

```text
Packages/com.unity.usd-toolkit/Runtime/Plugins/macOS/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  plugin/usd/plugInfo.json
  lib/usd/plugInfo.json
```

macOS dylib는 `@loader_path` 기반 dependency 경로로 배치되어야 합니다. 빌드 후 `otool -L Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib`로 `libusd_ms.dylib` 등이 package payload 안에서 해석되는지 확인합니다.

이 파일들이 누락되면 Editor 또는 빌드된 플레이어에서 `DllNotFoundException`, dependency load 실패, OpenUSD plugin discovery 실패가 발생할 수 있습니다.

### 소스 패키지에서 native plugin을 다시 빌드해야 하는 경우

일반 사용자는 릴리즈 ZIP에 포함된 native payload를 그대로 사용하면 됩니다. 단, OpenUSD 버전을 바꾸거나 native wrapper를 수정한 경우에는 대상 플랫폼에서 OpenUSD와 native wrapper를 다시 빌드해야 합니다.

```powershell
cd C:/Path/To/UnityProject/Packages/com.unity.usd-toolkit/Native~
./build_windows.ps1 -OpenUsdRoot C:/USD/OpenUSD-26.05-win-x64
```

```bash
./Build~/build_openusd_macos.sh --arch universal --install-dir "$PWD/Build~/OpenUSDInstall/macos-universal"
./Native~/build_macos.sh --openusd-root "$PWD/Build~/OpenUSDInstall/macos-universal" --arch universal
```

## 5. 런타임 Export 사용 방법

가장 간단한 사용 예시는 GameObject를 지정하고 출력 경로를 넘기는 방식입니다. 출력 확장자에 따라 `.usd`, `.usda`, `.usdc` 파일을 생성할 수 있습니다.

```csharp
using Unity.USDToolkit;
using UnityEngine;

public class ExportButton : MonoBehaviour
{
    [SerializeField] private GameObject exportRoot;

    public void Export()
    {
        string path = System.IO.Path.Combine(
            Application.persistentDataPath,
            "robot.usdc");

        UsdExportResult result = UsdExporter.ExportGameObjectWithResult(
            exportRoot,
            path,
            new UsdExportOptions
            {
                RootPrimName = "Robot",
                MetersPerUnit = 1.0f,
                TransformPolicy = UsdTransformPolicy.BakedMesh,
                CaptureNativeDiagnostics = true,
                NativeDiagnosticsLogPath = System.IO.Path.Combine(
                    Application.persistentDataPath,
                    "robot-usd-diagnostics.log")
            });

        Debug.Log(result.ToString());
    }
}
```

### 읽기 가능한 Mesh requirement

빌드된 플레이어에서 export할 mesh는 `mesh.isReadable == true`여야 합니다. FBX, OBJ, glTF 등으로 import한 모델은 Import Settings에서 `Read/Write Enabled`를 켜고 빌드해야 합니다. 런타임 생성 mesh는 일반적으로 읽기 가능하지만, `Mesh.UploadMeshData(true)`를 호출한 mesh는 CPU copy가 제거될 수 있으므로 주의해야 합니다.

## 6. Export 옵션 설명

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `RootPrimName` | `null` | 비어 있으면 export root GameObject 이름을 USD root prim 이름으로 사용합니다. |
| `MetersPerUnit` | `1.0f` | USD stage의 meter 단위입니다. 0보다 커야 합니다. |
| `IncludeInactive` | `false` | 비활성 child GameObject를 export 대상에 포함할지 결정합니다. |
| `RequireReadableMeshes` | `true` | 읽을 수 없는 mesh가 있으면 예외를 발생시킵니다. |
| `ExportNormals` | `true` | Unity mesh normals를 USD에 작성합니다. |
| `ExportUv0` | `true` | Unity mesh uv0를 USD primvar로 작성합니다. |
| `ExportBounds` | `true` | USD mesh extent를 작성합니다. |
| `ExportDisabledRenderers` | `true` | disabled MeshRenderer를 포함하되 invisible로 작성할 수 있습니다. |
| `PreserveInactiveAndDisabledVisibility` | `true` | 포함된 inactive/disabled object를 `visibility = "invisible"`로 작성합니다. |
| `TransformPolicy` | `BakedMesh` | `BakedMesh` 또는 `PreserveHierarchy` 중 선택합니다. |
| `ValidateNativeRuntime` | `true` | native plugin 및 OpenUSD runtime payload를 사전 점검합니다. |
| `ValidateOpenUsdPluginPath` | `true` | OpenUSD plugin/resource discovery path를 점검합니다. |
| `CaptureNativeDiagnostics` | `false` | Pixar OpenUSD diagnostic 로그를 수집합니다. |
| `LogExportSummary` | `false` | 성공 시 export summary를 Unity Log에 출력합니다. |
| `PluginSearchPath` | `null` | OpenUSD plugin discovery 경로를 직접 지정할 때 사용합니다. 일반적으로 비워둡니다. |
| `NativeDiagnosticsLogPath` | `null` | native diagnostic 로그를 저장할 파일 경로입니다. |

### TransformPolicy 선택 기준

| 정책 | 사용 시점 | 결과 |
| --- | --- | --- |
| `BakedMesh` | 대부분의 런타임 export, 단순 mesh 전달, 외부 툴에서 transform 구조가 중요하지 않은 경우 | Unity transform이 mesh point에 bake됩니다. USD hierarchy는 단순해집니다. |
| `PreserveHierarchy` | Unity hierarchy와 local transform 구조를 USD에서도 유지해야 하는 경우 | GameObject hierarchy가 USD Xform prim으로 작성되고 mesh point는 local space에 남습니다. |

## 7. Windows/macOS Standalone 빌드에서 사용하기

1. Build Settings에서 Platform을 Windows 또는 macOS로 선택합니다. macOS는 x64, arm64, Universal payload 중 빌드 대상과 일치해야 합니다.
2. Managed backend은 Mono 또는 IL2CPP 모두 사용할 수 있습니다.
3. export 대상 mesh의 `Read/Write Enabled`가 켜져 있는지 확인합니다.
4. 빌드 후 실행 파일에서 export 기능을 호출합니다.
5. 생성 파일은 보통 `Application.persistentDataPath` 아래에 저장하는 것을 권장합니다.

### 빌드된 플레이어 payload 위치

```text
<BuildFolder>/<AppName>_Data/Plugins/x86_64/Windows/
  UnityUSDToolkitNative.dll
  usd_ms.dll
  tbb.dll
  plugin/usd/...
  lib/usd/...
```

```text
<BuildFolder>/<AppName>.app/Contents/PlugIns/
  UnityUSDToolkitNative.dylib
  libusd_ms.dylib
  libtbb*.dylib
  plugin/usd/...
  lib/usd/...
```

패키지의 Editor build postprocessor가 OpenUSD resource tree를 built player 쪽으로 복사합니다. 빌드 결과에서 위 파일들이 누락되면 native runtime load 또는 OpenUSD plugin discovery가 실패합니다.

## 8. 결과 USD 확인 방법

- `usdchecker <file>`: USD 파일 구조와 기본 유효성을 검사합니다.
- `usdcat <file>`: usdc/usd 파일 내용을 텍스트로 확인합니다.
- Blender: USD import로 geometry/material을 육안 확인할 수 있습니다.
- Omniverse/UsdView: OpenUSD 기반 뷰어에서 stage 구조와 material binding을 확인할 수 있습니다.

```powershell
usdchecker C:/Exports/robot.usdc
usdcat C:/Exports/robot.usdc
```

## 9. 문제 해결

| 증상 | 가능한 원인 | 해결 방법 |
| --- | --- | --- |
| `DllNotFoundException` | `UnityUSDToolkitNative` 또는 OpenUSD dependency 누락 | 플랫폼별 `Runtime/Plugins` payload 및 built player `Plugins`/`PlugIns` 폴더에 native 파일이 있는지 확인합니다. |
| OpenUSD plugin path 오류 | `plugin/usd` 또는 `lib/usd` resource tree 누락 | `plugInfo.json` 파일이 포함되어 있는지 확인합니다. `PluginSearchPath`를 직접 지정했다면 경로를 점검합니다. |
| Mesh is not readable | 모델 import setting에서 `Read/Write Enabled`가 꺼져 있음 | 해당 mesh asset의 `Read/Write Enabled`를 켜고 다시 빌드합니다. |
| 파일이 생성되지 않음 | 출력 경로 권한 문제 또는 예외 발생 | `Application.persistentDataPath` 아래에 저장하고 Unity Console/player log를 확인합니다. |
| Standalone에서는 실패하지만 Editor에서는 성공 | 빌드 payload 누락 또는 mesh readability 차이 | 빌드 결과의 platform native payload 폴더와 mesh import setting을 확인합니다. |
| `EntryPointNotFoundException` | managed API와 native ABI 불일치 | 현재 패키지의 `Native~` 소스에서 `UnityUSDToolkitNative`를 다시 빌드합니다. |

### 진단 정보 출력 예시

```csharp
UsdRuntimeInfo info = UsdExporter.GetRuntimeInfo();
Debug.Log(info.ToString());

UsdExportResult result = UsdExporter.ExportGameObjectWithResult(root, path, options);
Debug.Log(result.ToString());
```

## 10. 배포 전 체크리스트

- 패키지 ID가 `com.unity.usd-toolkit`인지 확인합니다.
- Windows는 `Runtime/Plugins/x86_64/Windows` 아래에 `UnityUSDToolkitNative.dll`, `usd_ms.dll`, `tbb.dll`이 있는지 확인합니다.
- macOS는 `Runtime/Plugins/macOS` 아래에 `UnityUSDToolkitNative.dylib`, `libusd_ms.dylib`, `libtbb*.dylib`이 있는지 확인합니다.
- `plugin/usd/plugInfo.json` 및 `lib/usd/plugInfo.json`이 포함되어 있는지 확인합니다.
- Unity Editor에서 패키지가 컴파일되는지 확인합니다.
- Cube 또는 간단한 readable mesh를 Editor에서 export해 봅니다.
- Windows Standalone x64 또는 macOS Standalone player를 빌드하고 player 안에서 export를 호출해 봅니다.
- `usdchecker` 또는 `usdcat`으로 결과 파일을 확인합니다.
- IL2CPP를 사용할 프로젝트라면 IL2CPP player export도 별도로 검증합니다.
- 사용자에게 texture, skinned mesh, animation, USDZ는 아직 미지원임을 명확히 안내합니다.

## 부록 A. Public API 요약

| API | 용도 |
| --- | --- |
| `UsdExporter.ExportGameObject` | 결과 객체가 필요 없는 간단한 export 호출 |
| `UsdExporter.ExportGameObjectWithResult` | export 결과 요약이 필요한 호출 |
| `UsdExporter.GetRuntimeInfo` | native plugin/API/OpenUSD/runtime path 정보 조회 |
| `UsdExportOptions` | export 동작 옵션 |
| `UsdExportResult` | mesh count, vertex count, triangle count, output path 등 결과 요약 |
| `UsdExportException` | export 실패 시 발생하는 예외와 diagnostics |
| `UsdTransformPolicy` | `BakedMesh` 또는 `PreserveHierarchy` 선택 |
