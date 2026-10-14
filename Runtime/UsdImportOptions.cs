using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    public sealed class UsdImportOptions
    {
        public Transform Parent;
        public string RootObjectName;
        public bool ImportMaterials = true;
        public bool ImportTextures = true;

        /// <summary>
        /// Check the native payload against the digests recorded for this package before the
        /// first call into it.
        /// </summary>
        /// <remarks>
        /// The same switch <see cref="UsdExportOptions.VerifyNativeRuntimeIntegrity"/> carries,
        /// and for the same reason: someone iterating on the native plugin has a payload that
        /// deliberately does not match the manifest, and regenerating the manifest is not always
        /// possible -- a locally built payload with no provenance stamp is refused one outright.
        /// Export could already opt out and import could not, which left the import path, where
        /// untrusted file content is actually parsed, as the one that could not be worked on.
        ///
        /// Honoured in the Editor and in development builds only. A release player verifies
        /// regardless, because post-distribution substitution is the threat the check exists for
        /// (SECURITY-282834).
        /// </remarks>
        public bool VerifyNativeRuntimeIntegrity = true;

        /// <summary>
        /// Block-compress imported textures instead of keeping them at 32 bits per pixel.
        /// </summary>
        /// <remarks>
        /// A decoded image arrives as RGBA32, which is four bytes a pixel however little the
        /// image needs: a 4K albedo occupies 67 MB, and 89 MB once mipped. Stages with a few
        /// hundred materials therefore run to tens of gigabytes of texture alone -- enough that
        /// importing one has been ended by the system's out-of-memory killer rather than by any
        /// limit the toolkit sets. Compression costs a fraction of that for a loss that PBR maps
        /// tolerate, which is why it is the default.
        ///
        /// Turn it off for a stage whose textures carry data rather than appearance -- an
        /// identifier packed into a channel, a lookup table -- where block compression's
        /// approximation is not a quality trade but a wrong answer.
        /// </remarks>
        public bool CompressTextures = true;
        public bool IncludeInvisible = true;
        public bool GenerateColliders;
        public bool RecalculateNormalsIfMissing = true;
        public bool CaptureNativeDiagnostics;
        public string NativeDiagnosticsLogPath;

        // A USD stage authors its own texture paths, so an imported file is untrusted input: an
        // `inputs:file` can name an absolute path or climb out of the stage folder with `../`,
        // and the importer would read whatever it pointed at. By default only assets that
        // resolve inside the stage's own directory (or inside the .usdz package) are read.
        // Enable this only for stages you trust that deliberately reference a shared texture
        // library living outside their folder.
        public bool AllowExternalAssetPaths;

        // Async import only (UsdImporter.ImportAsync): the main-thread object-build pass yields
        // to the next frame once it has spent this many milliseconds, so large imports do not
        // freeze the editor/player. Ignored by the synchronous UsdImporter.Import.
        public float MaxMillisecondsPerFrame = 10f;

        /// <summary>
        /// Async import only (<c>UsdImporter.ImportAsync</c>): return as soon as the hierarchy,
        /// meshes and untextured materials are built, and attach the textures once they finish
        /// decoding on a worker thread. Await <see cref="UsdImportResult.TexturesReady"/> to know
        /// when the import looks the way it will stay.
        /// </summary>
        /// <remarks>
        /// Texture decoding is most of an import with large textures (ALab: about 13 of 23.5
        /// seconds is its UDIM atlases), and without streaming nothing is on screen until it
        /// ends. Streamed or not, the materials end up identical: the textured material is built
        /// by the same code and copied onto the one already in use. Until then a material shows
        /// its authored constants, as it would if its textures had failed to load.
        ///
        /// Turn it off to have <c>ImportAsync</c> return only once everything, textures included,
        /// is loaded, as it did before -- for a caller that exports or captures straight after
        /// the import. The synchronous <c>UsdImporter.Import</c> always loads everything. On by
        /// default.
        /// </remarks>
        public bool StreamTextures = true;

        // Optional progress callback for ImportAsync: (fraction 0..1, phase label). Invoked on
        // the main thread.
        public Action<float, string> ProgressCallback;

        // Variant selections to compose with (native API 6). Applied in list order, so a set
        // nested inside another set's variant can follow the outer selection. The source file
        // is not edited; the choice is recorded on the import's UsdStageSource and written back
        // by a Preserve export.
        public List<UsdVariantSelection> VariantSelections = new List<UsdVariantSelection>();

        // When false, no payload is loaded except the prim paths in PayloadsToLoad (native API 6).
        // An unloaded payload's prim still arrives as an empty GameObject, so it keeps its place.
        public bool LoadPayloads = true;
        public List<string> PayloadsToLoad = new List<string>();

        // Adds a UsdStageSource to the import's root and a UsdPrimSource to every object it
        // creates, recording where each came from and its state at import. That is what lets a
        // Preserve export write only what changed. Cheap; on by default.
        public bool RecordSource = true;

        /// <summary>
        /// Applies the stage's <c>upAxis</c> and <c>metersPerUnit</c> to the import root so the
        /// content arrives Y-up and in meters. The correction sits on the root's local rotation
        /// and scale only: vertices and every prim's local transform stay exactly as authored, so
        /// a Preserve export writes no override for it. A Z-up stage gets a -90 degree rotation
        /// about X (USD +Z becomes Unity +Y, USD +Y becomes Unity -Z), and the root is scaled by
        /// <c>metersPerUnit</c> (0.01 for a centimeter stage). The correction applied is reported
        /// in <see cref="UsdImportResult.StageCorrectionRotation"/> and
        /// <see cref="UsdImportResult.StageCorrectionScale"/>. Turn it off to get the stage's raw
        /// coordinates under an identity root, as imports did before. On by default.
        /// </summary>
        public bool ApplyStageUpAxisAndUnits = true;

        /// <summary>
        /// Gives a mesh or submesh that has no material binding a shared neutral grey lit
        /// material ("UsdDefaultMaterial", one per import) instead of leaving it without one,
        /// which renders magenta under URP and HDRP and looks like a shader failure. Only used
        /// while <see cref="ImportMaterials"/> is on. On by default.
        /// </summary>
        public bool AssignDefaultMaterial = true;

        /// <summary>
        /// Builds one Unity <see cref="Mesh"/> per distinct geometry and shares it among every
        /// imported mesh with the same points, indices, normals, UV sets and submesh ranges, as
        /// instanceable prims and PointInstancer instances produce. Memory and build time then
        /// grow with the number of distinct shapes rather than the number of instances. Material
        /// bindings may still differ per object, since they live on the renderer. The shared Mesh
        /// keeps the first prim's name, and an edit to <c>MeshFilter.sharedMesh</c> shows on every
        /// object that uses it (<c>MeshFilter.mesh</c> gives an object its own copy). Turn it off
        /// to get one Mesh per prim, as imports did before. On by default.
        /// </summary>
        public bool ShareIdenticalMeshes = true;

        /// <summary>
        /// Adds a Unity <see cref="Light"/> for each UsdLux light (distant, sphere, disk, cylinder
        /// and rect; native API 7). It sits on a child object turned to Unity's +Z convention,
        /// which exporters skip. Intensities are mapped heuristically: USD has no single unit
        /// convention for them, so check the result against the source renderer. On by default.
        /// </summary>
        public bool ImportLights = true;

        /// <summary>
        /// Adds a disabled Unity <see cref="Camera"/> for each UsdGeomCamera (native API 7), on a
        /// child object turned to Unity's convention, so the scene's existing camera keeps
        /// rendering until one of these is enabled. On by default.
        /// </summary>
        public bool ImportCameras = true;

        /// <summary>
        /// Adds an <see cref="AudioSource"/> and a <see cref="UsdSpatialAudio"/> for each
        /// UsdMediaSpatialAudio (native API 7). The clip itself is not loaded; see
        /// <see cref="UsdSpatialAudio"/>. On by default.
        /// </summary>
        public bool ImportSpatialAudio = true;

        /// <summary>
        /// Applies a UsdLuxDomeLight to the scene: its colour times intensity becomes the flat
        /// ambient colour, and a PNG or JPG texture becomes a panoramic skybox. This changes
        /// <see cref="RenderSettings"/> for the whole scene, so it is off by default; the dome is
        /// always recorded on a <see cref="UsdDomeLight"/>.
        /// </summary>
        public bool ApplyDomeLightToEnvironment;
    }
}
