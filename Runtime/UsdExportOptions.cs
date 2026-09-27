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

        /// <summary>
        /// When true (the default), the SHA-256 of every shipped native file — libraries and
        /// plugin descriptors alike — is compared against the digest recorded in
        /// <c>NativeRuntimeHashes</c> before the first P/Invoke, and a mismatch throws.
        ///
        /// Setting this to false is honoured in the Editor and in development builds only. A
        /// release player always verifies: the check is what catches a payload substituted after
        /// distribution, and that is exactly the situation a shipped build is in. Unlike <see cref="ValidateNativeRuntime"/>, which only asks
        /// whether a file of the right name exists, this verifies the contents. If you rebuild
        /// the native plugin yourself, regenerate the manifest with
        /// <c>python3 Native~/generate_native_hashes.py</c>.
        /// </summary>
        public bool VerifyNativeRuntimeIntegrity = true;
        public bool ValidateOpenUsdPluginPath = true;
        public bool CaptureNativeDiagnostics;
        public bool LogExportSummary;

        /// <summary>
        /// When false, only meshes and flat-colour materials are exported (the light path).
        /// When true, albedo/normal/metallic textures are written as PNGs into a
        /// "&lt;usd-name&gt;_textures/" folder beside the USD file and referenced relatively.
        /// </summary>
        public bool ExportTextures;

        /// <summary>
        /// When true (the default), a material whose metallic map (_MetallicGlossMap /
        /// _MetallicMap) is the *same texture* as its albedo map (_BaseMap / _MainTex) is treated
        /// as a misassigned slot: the metallic texture is dropped and only the scalar _Metallic
        /// value is exported, with a warning when this triggers.
        /// A metallic mask is meant to be a linear greyscale mask, so feeding it an sRGB base
        /// colour is almost always an import or copy-paste mistake. Left alone it produces a
        /// mirror metal (metallic approximately texture.r, roughness approximately 0), which
        /// renders black in viewers without environment reflection, such as Isaac or Omniverse
        /// real-time. Set this to false to keep the original behaviour when the two genuinely
        /// share a texture on purpose.
        /// </summary>
        public bool IgnoreAlbedoInMetallicSlot = true;

        /// <summary>
        /// Only meaningful when exporting to .usdz. When true, the package is written under
        /// ARKit (AR Quick Look) constraints, which can drop features such as variant sets.
        /// When false (the default), an ordinary usdz is written.
        /// </summary>
        public bool UsdzArkitCompatible;

        /// <summary>
        /// Optional override for OpenUSD plugin discovery. Leave empty when the package
        /// runtime plugin layout is preserved.
        ///
        /// Must resolve inside the package's own native runtime folders. OpenUSD loads and
        /// executes any library a plugInfo.json under this path names, so a path outside those
        /// folders is refused unless <see cref="UsdExporter.AllowExternalPluginSearchPath"/> is
        /// set from code.
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
