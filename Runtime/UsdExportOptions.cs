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

        /// <summary>
        /// Writes Unity's uv1 and uv2 (<c>Mesh.uv2</c>, <c>Mesh.uv3</c>) as <c>primvars:st1</c> and
        /// <c>primvars:st2</c>, the names the importer reads them back from, when the mesh has
        /// them. uv1 is often a lightmap layout. Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ExportUvSets = true;

        /// <summary>
        /// Writes <c>Mesh.colors</c> as per-vertex <c>primvars:displayColor</c> (and
        /// <c>displayOpacity</c> when any alpha is below 1) instead of the material colour the
        /// exporter mirrors there otherwise. Colours are written linear, as material colours are.
        /// Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ExportVertexColors = true;

        /// <summary>
        /// Writes each enabled-or-not Unity <see cref="Light"/> as a UsdLux light (directional to
        /// DistantLight, point and spot to SphereLight with a ShapingAPI cone, rectangle to
        /// RectLight, disc to DiskLight), with intensities from the inverse of the importer's
        /// <see cref="UsdImportOptions.PhysicalLightUnits"/> factors, so a light round-trips.
        /// Lights the importer created for a stage's own lights are skipped: a Preserve export
        /// keeps the source's lights through its sublayer. Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ExportLights = true;

        /// <summary>
        /// Writes each Unity <see cref="Camera"/> as a UsdGeomCamera: a physical camera keeps its
        /// focal length and sensor size, any other gets a 24 mm-high aperture with the focal
        /// length that gives its field of view. Cameras the importer created are skipped, as
        /// lights are. Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ExportCameras = true;

        /// <summary>
        /// Material networks written beside the <c>UsdPreviewSurface</c> every exported material
        /// has: <see cref="UsdExportMaterialFlavors.OmniPbr"/> adds an OmniPBR MDL shader under the
        /// <c>mdl</c> render context, which Omniverse and Isaac Sim render natively;
        /// <see cref="UsdExportMaterialFlavors.MaterialX"/> adds a MaterialX
        /// <c>standard_surface</c> under <c>mtlx</c>. Both carry the colour, metallic, roughness,
        /// normal map, emission, opacity and UV tiling; Unity's packed metallic/smoothness map
        /// stays in the UsdPreviewSurface only. Needs the 0.8.1 native plugin. None by default.
        /// </summary>
        public UsdExportMaterialFlavors MaterialFlavors = UsdExportMaterialFlavors.None;
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

        /// <summary>
        /// <see cref="UsdCompositionMode.Flatten"/> (the default) writes every object out in
        /// full, as earlier versions did. <see cref="UsdCompositionMode.Preserve"/> keeps USD
        /// composition: an imported stage is written back as overrides on top of its source
        /// file, prefab instances become references to one asset each, and (with
        /// <see cref="ShareMeshes"/>) repeated meshes become instances of one prototype.
        /// Preserve always keeps the hierarchy, whatever <see cref="TransformPolicy"/> says, and
        /// needs the 0.8.0 native plugin.
        /// </summary>
        public UsdCompositionMode Composition = UsdCompositionMode.Flatten;

        /// <summary>
        /// Where shared content goes in <see cref="UsdCompositionMode.Preserve"/>:
        /// <see cref="UsdExportLayout.SingleFile"/> keeps prefab assets and mesh prototypes in
        /// the one file, as class prims referenced internally;
        /// <see cref="UsdExportLayout.FolderWithAssets"/> writes each prefab asset to its own
        /// file in a "&lt;usd-name&gt;_assets/" folder beside the USD and references it. An
        /// output path ending in .usdz packages either layout, dependencies included.
        /// </summary>
        public UsdExportLayout Layout = UsdExportLayout.SingleFile;

        /// <summary>
        /// How <see cref="UsdCompositionMode.Preserve"/> writes objects that came from
        /// <see cref="UsdImporter"/> (they carry a <see cref="UsdStageSource"/>).
        /// </summary>
        public UsdImportedContentMode ImportedContent = UsdImportedContentMode.OverrideSource;

        /// <summary>
        /// Preserve mode only. When true, meshes that share one Mesh asset and the same materials
        /// are written once, as a prototype, and every user references it as an instance. Off by
        /// default so the shape of an export does not change for existing callers.
        /// </summary>
        public bool ShareMeshes;

        /// <summary>
        /// Preserve mode only. Detects prefab instances (in the Editor through the prefab
        /// connection, at runtime through <see cref="UsdPrefabMarker"/>) and writes each prefab
        /// once as an asset that its instances reference, with each instance's differences as
        /// overrides. On by default.
        /// </summary>
        public bool ExportPrefabsAsReferences = true;

        // Set by UsdAnimationRecorder: visibility is written per object (activeSelf) rather than as
        // shown (activeInHierarchy), so an ancestor's animated visibility is the only thing hiding
        // what is below it.
        [NonSerialized]
        internal bool VisibilityPerObject;

        // Set by UsdAnimationRecorder on a Preserve recording: the prims the source animates get
        // the recording's samples, so a pose that differs from the source's animation is no edit
        // the override layer drops, and nothing is reported about it.
        [NonSerialized]
        internal bool SamplesFollow;
    }

    public enum UsdTransformPolicy
    {
        BakedMesh = 0,
        PreserveHierarchy = 1
    }

    public enum UsdCompositionMode
    {
        /// <summary>Every object is written out in full into one layer (the original behaviour).</summary>
        Flatten = 0,

        /// <summary>Sublayers, references and instancing are kept or created; see UsdExportOptions.Composition.</summary>
        Preserve = 1
    }

    public enum UsdExportLayout
    {
        /// <summary>One file; shared content lives in it as class prims.</summary>
        SingleFile = 0,

        /// <summary>The scene file plus one file per prefab asset in "&lt;usd-name&gt;_assets/".</summary>
        FolderWithAssets = 1
    }

    public enum UsdImportedContentMode
    {
        /// <summary>
        /// When the export root is the object an import created, the output sublayers the
        /// source file and holds only what changed in Unity (moves, visibility, deletions,
        /// additions, variant selections), so every composition arc in the source survives.
        /// An imported stage nested deeper in the exported hierarchy is referenced instead.
        /// </summary>
        OverrideSource = 0,

        /// <summary>Imported stages are always referenced from the source file, with changes as overrides.</summary>
        ReferenceSource = 1,

        /// <summary>Imported objects are written out like any other object; the source file is not used.</summary>
        Inline = 2
    }

    /// <summary>
    /// Material networks an export writes beside the UsdPreviewSurface (see
    /// <see cref="UsdExportOptions.MaterialFlavors"/>).
    /// </summary>
    [Flags]
    public enum UsdExportMaterialFlavors
    {
        /// <summary>UsdPreviewSurface only.</summary>
        None = 0,

        /// <summary>An OmniPBR MDL shader under the <c>mdl</c> render context.</summary>
        OmniPbr = 1,

        /// <summary>A MaterialX <c>standard_surface</c> under the <c>mtlx</c> render context.</summary>
        MaterialX = 2
    }
}
