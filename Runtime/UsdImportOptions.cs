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
        /// (security review).
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

        /// <summary>
        /// Shades every mesh with smooth normals computed from its geometry, as Isaac Sim does,
        /// instead of the normals the stage authors. Isaac Sim's RTX renderer draws a mesh the same
        /// whether its <c>normals</c> or <c>primvars:normals</c> are present, blocked or replaced
        /// (measured on NVIDIA's Factory), so a stage whose exporter wrote hard breaks into its
        /// normals (the Factory's car body: facets in Unity, smooth in Isaac Sim) only matches
        /// when Unity ignores them too. Faces meeting at more than 60 degrees keep their edge.
        /// Turn it off to import the authored normals as they are. On by default.
        /// </summary>
        public bool SmoothNormalsLikeIsaac = true;

        /// <summary>
        /// Decodes a normal map whose file input authors no colour space as sRGB when the file is
        /// 8-bit, as Isaac Sim does. USD reads an unauthored colour space as "auto", which for an
        /// 8-bit image is sRGB, and Omniverse applies that to normal maps too: NVIDIA's pallet prop
        /// (an OmniPBR with a .tga normal map and no colorSpace) shades its sunlit top at a quarter
        /// of what the same map read as raw gives (Isaac Sim 6.2, 0.25 against 3.46 times the
        /// albedo, measured from its HdrColor and DiffuseAlbedo outputs). An authored "raw" or
        /// "sRGB" is always followed. Turn it off to read every normal map as linear data. On by
        /// default.
        /// </summary>
        public bool NormalMapColorSpaceLikeIsaac = true;

        /// <summary>
        /// Downloads the remote MDL modules a stage names by URL from NVIDIA's content servers,
        /// with the textures they use, as Isaac Sim does, and imports them as the OmniPBR
        /// materials they are. Isaac Sim stages often bind NVIDIA's material library this way
        /// (https://omniverse-content-production.s3.us-west-2.amazonaws.com/Materials/...), and
        /// without the download those surfaces import default grey. Only HTTPS on NVIDIA's content
        /// hosts is fetched; a stage that needed a download is read a second time once the files
        /// are in <see cref="RemoteMaterialCacheFolder"/>, and later imports read the cache. Off by
        /// default: an import reaches the network only when the caller turns this on, since an
        /// importer that goes online on its own is a policy question (air-gapped sites, security
        /// review). Modules already in the cache are read either way.
        /// </summary>
        public bool DownloadRemoteMaterials;

        /// <summary>
        /// Where downloaded remote materials are kept, by host and path. Null means
        /// <c>Application.temporaryCachePath</c>/UsdRemoteMaterials.
        /// </summary>
        public string RemoteMaterialCacheFolder;

        /// <summary>
        /// Draws open meshes from both sides, as Isaac Sim does. Omniverse RTX renders every mesh
        /// double-sided whatever its <c>doubleSided</c> says (measured on Isaac Sim 6.2: a
        /// <c>doubleSided = 0</c> quad seen from behind is drawn and lit), so a ceiling panel or a
        /// wall built as a single sheet is visible from both sides there, and blocks light from
        /// both. With this on, a mesh with open edges gets back faces as an authored
        /// <c>doubleSided</c> mesh does; a closed mesh is left one-sided, since its inside cannot
        /// be seen from outside and its triangles need not double. NVIDIA's Simple_Warehouse
        /// ceiling imported invisible from below without it, and let its sunlight in. Turn it off
        /// to follow <c>doubleSided</c> strictly. On by default.
        /// </summary>
        public bool DoubleSidedOpenMeshes = true;
        public bool CaptureNativeDiagnostics;
        public string NativeDiagnosticsLogPath;

        // A USD stage authors its own texture paths, so an imported file is untrusted input: an
        // `inputs:file` can name an absolute path or climb out of the stage folder with `../`,
        // and the importer would read whatever it pointed at. By default only assets that
        // resolve inside the stage's own directory (or inside the .usdz package) are read.
        // Enable this only for stages you trust that deliberately reference a shared texture
        // library living outside their folder.
        public bool AllowExternalAssetPaths;

        /// <summary>
        /// The budgets the import is held to: nodes, meshes, point-instancer placements, points and
        /// triangle indices, and animation samples, values and joints -- and whether a stage over
        /// one fails the import (the default) or imports what fits, with a warning. The defaults
        /// are the fixed limits earlier versions imported within. See <see cref="UsdImportLimits"/>.
        /// </summary>
        public UsdImportLimits Limits = new UsdImportLimits();

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

        // Optional receiver for the import's warnings about the stage as a whole -- prims of a type
        // no loaded schema defines, and a stage that yields no geometry -- one line each, once per
        // Import or ImportAsync, on the main thread. Null logs them with Debug.LogWarning. The
        // import's other warnings go to the Console either way. Internal: the USD asset importer
        // passes its import context's log here, reached through InternalsVisibleTo (AssemblyInfo.cs).
        internal Action<string> StageWarningCallback;

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
        /// Builds one Unity <see cref="Material"/> per distinct set of material values -- colours,
        /// scalars, UV transform and texture files -- and shares it among every renderer whose USD
        /// material resolves to the same set. Stages exported from Omniverse or the Unreal connector
        /// bind a material prim per mesh, so without this a warehouse imports thousands of
        /// identical Materials; Surface Cache GI then needs an atlas slice for each and fails past
        /// 2,048. The shared Material keeps the first prim's name, and an edit to
        /// <c>Renderer.sharedMaterial</c> shows on every object that uses it. Turn it off to get
        /// one Material per material prim, as imports did before. On by default.
        /// </summary>
        public bool ShareIdenticalMaterials = true;

        /// <summary>
        /// Adds a Unity <see cref="Light"/> for each UsdLux light (distant, sphere, disk, cylinder
        /// and rect; the 0.8.0 native plugin). It sits on a child object turned to Unity's +Z convention,
        /// which exporters skip. Intensities are mapped heuristically: USD has no single unit
        /// convention for them, so check the result against the source renderer. On by default.
        /// </summary>
        public bool ImportLights = true;

        /// <summary>
        /// Adds a disabled Unity <see cref="Camera"/> for each UsdGeomCamera (the 0.8.0 native plugin), on a
        /// child object turned to Unity's convention, so the scene's existing camera keeps
        /// rendering until one of these is enabled. On by default.
        /// </summary>
        public bool ImportCameras = true;

        /// <summary>
        /// Adds an <see cref="AudioSource"/> and a <see cref="UsdSpatialAudio"/> for each
        /// UsdMediaSpatialAudio (the 0.8.0 native plugin). The clip itself is not loaded; see
        /// <see cref="UsdSpatialAudio"/>. On by default.
        /// </summary>
        public bool ImportSpatialAudio = true;

        /// <summary>
        /// Applies a UsdLuxDomeLight to the scene. With <see cref="PhysicalLightUnits"/> its texture
        /// (OpenEXR, Radiance HDR, PNG or JPG; an untextured dome is a uniform sky) becomes the
        /// skybox, the ambient light and the environment reflection, at the calibrated exposure;
        /// without it, colour times intensity becomes a flat ambient. This changes
        /// <see cref="RenderSettings"/> for the whole scene. On by default so that a stage looks
        /// the way Isaac Sim draws it; the dome is always recorded on a <see cref="UsdDomeLight"/>.
        /// </summary>
        public bool ApplyDomeLightToEnvironment = true;

        /// <summary>
        /// Converts light intensities with fixed scale factors calibrated against Isaac Sim 6.2.0
        /// (RTX Real-Time 2.0) instead of normalising them to the stage's brightest light: a
        /// distant light of 1000 and one of 10000 then differ by ten times, as authored. Rect lights
        /// become a grid of wide spots (URP has no real-time area light). On by default.
        /// </summary>
        public bool PhysicalLightUnits = true;

        /// <summary>
        /// With <see cref="PhysicalLightUnits"/>, adds a shadowless point light at each emissive
        /// surface, sized by its emitting area, so that an emitter lights its surroundings the way
        /// a path tracer does. On by default.
        /// </summary>
        public bool EmissiveMeshLights = true;

        /// <summary>
        /// With <see cref="PhysicalLightUnits"/>, captures the scene's environment reflection from
        /// inside the stage (its horizontal centre, 1.5 m above its lowest point) once the import
        /// is lit, so metals and glass reflect the hall rather than the sky. Surfaces outside the
        /// building reflect the interior too. The capture does not follow later lighting changes;
        /// call <see cref="UsdImporter.RefreshReflection"/> after them. On by default.
        /// </summary>
        public bool InteriorReflection = true;

        /// <summary>
        /// Reads static values (transforms, points, primvars, visibility, implicit surfaces,
        /// lights and cameras) at the stage's <c>startTimeCode</c>, or at each attribute's first
        /// time sample when the stage authors no time range, as a viewer opens the stage. Turned
        /// off they are read at USD's Default time, which ignores time samples: a stage that authors
        /// its transforms only as samples, as Blender's animated exports do, then arrives with every
        /// object at its parent's origin. Needs the 0.8.1 native plugin; older plugins always read at Default.
        /// On by default.
        /// </summary>
        public bool ReadAtStageStartTime = true;

        /// <summary>
        /// Builds UsdSkel skinning: a joint hierarchy under each Skeleton prim (with a
        /// <see cref="UsdSkeleton"/> on the Skeleton's object), and a
        /// <see cref="SkinnedMeshRenderer"/> with bone weights, bind poses and blend shapes for each
        /// skinned mesh, posed as the stage is at the read time. Joints are not prims, so exporters
        /// and <see cref="UsdPrimSource"/> skip them. Turned off, skinned meshes import as static
        /// meshes in their authored points, as before. Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ImportSkinning = true;

        /// <summary>
        /// Skins the meshes UsdSkel marks <c>skel:skinningMethod = "dualQuaternion"</c> the way
        /// UsdSkel does: a <see cref="UsdDualQuaternionSkin"/> deforms them on the CPU and draws the
        /// result through their MeshRenderer, so twisting and bending joints keep their volume. Turned
        /// off, they get a SkinnedMeshRenderer like every other skinned mesh, which blends linearly
        /// on the GPU (cheaper, but the "candy wrapper" collapse dual quaternions exist to avoid
        /// comes back), with a warning. Needs <see cref="ImportSkinning"/>. On by default.
        /// </summary>
        public bool DualQuaternionSkinning = true;

        /// <summary>
        /// Imports the stage's animation -- time-sampled prim transforms and visibility, skeleton
        /// animation, blend-shape weights and time-sampled mesh points (point caches) -- as a
        /// <see cref="UsdAnimationData"/> played by a
        /// <see cref="UsdAnimationPlayer"/> on the import root (reported in
        /// <see cref="UsdImportResult.Animation"/>), starting at the read time. Skeleton animation
        /// needs <see cref="ImportSkinning"/>. Turned off, the stage imports frozen at the read
        /// time. Needs the 0.8.1 native plugin. On by default.
        /// </summary>
        public bool ImportAnimation = true;

        /// <summary>
        /// Keeps each imported texture's CPU copy, so its pixels can be read back
        /// (<c>Texture2D.isReadable</c>) and it can be saved as an asset, which serializes that copy.
        /// The USD stage importer for project assets turns this on. Off by default: the GPU has the
        /// pixels, and keeping a second copy doubles the memory textures take.
        /// </summary>
        public bool KeepTexturesReadable;

        /// <summary>
        /// Keeps the stage open after the import as <see cref="UsdStageSource.Stage"/>, so the first
        /// live switch (<see cref="UsdStageSource.SwitchVariant"/>) need not open it again. The stage
        /// holds its layers in memory for as long as the import lives; without this it is opened on
        /// first use. Needs <see cref="RecordSource"/> and the 0.8.1 native plugin. Off by default.
        /// </summary>
        public bool KeepStageOpen;

        /// <summary>
        /// Builds the stage's UsdPhysics: each articulation (PhysicsArticulationRootAPI) as an
        /// <see cref="ArticulationBody"/> tree with its joints' limits and drives, other rigid
        /// bodies as <see cref="Rigidbody"/> with FixedJoint / ConfigurableJoint, colliders,
        /// physics materials and collision filtering, read with OpenUSD's own physics parser
        /// (PhysX's attributes included). ArticulationBody needs each link under its parent link,
        /// so links an Isaac Sim robot keeps as siblings are moved under their parent link (world
        /// pose kept). Next to each Unity body and joint a <see cref="UsdPhysicsBody"/> /
        /// <see cref="UsdPhysicsJoint"/> keeps the authored values to compare against; what could
        /// not be mapped is in <see cref="UsdImportResult.PhysicsNotes"/>. Gravity and the step rate
        /// are recorded in a <see cref="UsdPhysicsScene"/>, not applied. Needs the 0.8.1 native
        /// plugin. Off by default.
        /// </summary>
        public bool ImportPhysics;

        // A copy whose lists are its own, so editing one set of options never changes another.
        internal UsdImportOptions Clone()
        {
            var copy = (UsdImportOptions)MemberwiseClone();
            copy.Limits = Limits?.Clone();
            copy.VariantSelections = new List<UsdVariantSelection>(VariantSelections ?? new List<UsdVariantSelection>());
            copy.PayloadsToLoad = new List<string>(PayloadsToLoad ?? new List<string>());
            return copy;
        }
    }
}
