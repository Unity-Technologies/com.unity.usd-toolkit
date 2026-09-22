using System;

namespace Unity.USDToolkit
{
    [Serializable]
    public sealed class UsdExportOptions
    {
        public string RootPrimName;
        public float MetersPerUnit = 1.0f;
        public bool IncludeInactive;
        public bool RequireReadableMeshes = true;
        public bool ExportNormals = true;
        public bool ExportUv0 = true;
        public bool ExportBounds = true;
        public bool ExportDisabledRenderers = true;
        public bool PreserveInactiveAndDisabledVisibility = true;
        public UsdTransformPolicy TransformPolicy = UsdTransformPolicy.BakedMesh;
        public bool ValidateNativeRuntime = true;
        public bool ValidateOpenUsdPluginPath = true;
        public bool CaptureNativeDiagnostics;
        public bool LogExportSummary;

        /// <summary>
        /// false면 mesh + 단색 material만 export(가벼움). true면 albedo/normal/metallic 텍스처를
        /// PNG로 USD 파일 옆 "&lt;usd이름&gt;_textures/" 폴더에 저장하고 USD가 상대경로로 참조한다.
        /// </summary>
        public bool ExportTextures;

        /// <summary>
        /// true(기본)면, material의 metallic 맵(_MetallicGlossMap/_MetallicMap)이 albedo 맵
        /// (_BaseMap/_MainTex)과 '같은 텍스처'일 때 이를 슬롯 오배치로 보고 metallic 텍스처를
        /// 무시한 채 스칼라 _Metallic 값만 export한다(트리거 시 경고 로그 출력).
        /// metallic 마스크는 본래 linear 회색 마스크라 sRGB base color를 그대로 꽂는 건 거의
        /// 임포트/복붙 실수이고, 그대로 두면 거울 금속(metallic≈texture.r, roughness≈0)이 되어
        /// 환경 반사가 없는 뷰어(예: Isaac/Omniverse 실시간)에서 검게 렌더된다.
        /// 의도적으로 같은 텍스처를 공유하는 경우라면 false로 두어 원본 동작을 유지한다.
        /// </summary>
        public bool IgnoreAlbedoInMetallicSlot = true;

        /// <summary>
        /// .usdz로 export할 때만 의미가 있다. true면 ARKit(AR Quick Look) 제약에 맞춰 패키징하며,
        /// 그 과정에서 variant set 같은 일부 기능이 빠질 수 있다. false(기본)면 일반 usdz.
        /// </summary>
        public bool UsdzArkitCompatible;

        /// <summary>
        /// Optional override for OpenUSD plugin discovery. Leave empty when the package
        /// runtime plugin layout is preserved.
        /// </summary>
        public string PluginSearchPath;

        /// <summary>
        /// Optional file path for captured Pixar OpenUSD diagnostics. Requires
        /// CaptureNativeDiagnostics to be enabled.
        /// </summary>
        public string NativeDiagnosticsLogPath;
    }

    public enum UsdTransformPolicy
    {
        BakedMesh = 0,
        PreserveHierarchy = 1
    }
}
