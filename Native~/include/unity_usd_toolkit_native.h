#pragma once

#if defined(_WIN32)
#if defined(UNITY_USD_TOOLKIT_NATIVE_EXPORTS)
#define RUSD_API __declspec(dllexport)
#else
#define RUSD_API __declspec(dllimport)
#endif
#elif defined(__GNUC__) || defined(__clang__)
#define RUSD_API __attribute__((visibility("default")))
#else
#define RUSD_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct RUsdContext RUsdContext;

/* Number of UV sets (Unity uv0..uv2) the importer can return. API v3+. */
#define RUSD_MAX_UV_SETS 3

RUSD_API int RUsd_GetApiVersion(void);

RUSD_API int RUsd_GetOpenUsdVersion(
    char* buffer,
    int bufferCapacity);

typedef struct RUsdVec2
{
    float x;
    float y;
} RUsdVec2;

typedef struct RUsdVec3
{
    float x;
    float y;
    float z;
} RUsdVec3;

typedef struct RUsdMaterial
{
    float r;
    float g;
    float b;
    float a;
    float metallic;
    float roughness;
    char albedoTexturePath[512];   /* empty: flat diffuseColor; set: wired up as a UsdUVTexture (path relative to the USD) */
    char normalTexturePath[512];   /* normal map (raw) → inputs:normal */
    char metallicTexturePath[512]; /* packed metallic+smoothness -> metallic (.r), roughness (1 - .a) */
    float uvScale[2];              /* texture UV tiling (1,1 by default); anything else is applied as a UsdTransform2d */
    float uvOffset[2];             /* texture UV offset (0,0 by default) */
    float emissive[3];             /* emissiveColor (0,0,0 by default); authored when non-zero or a texture is present */
    char emissiveTexturePath[512]; /* emission map (sRGB) → emissiveColor */
} RUsdMaterial;

typedef struct RUsdSubmesh
{
    int indexStart;
    int indexCount;
    int materialIndex;
} RUsdSubmesh;

typedef struct RUsdMatrix4x4
{
    double m00;
    double m01;
    double m02;
    double m03;
    double m10;
    double m11;
    double m12;
    double m13;
    double m20;
    double m21;
    double m22;
    double m23;
    double m30;
    double m31;
    double m32;
    double m33;
} RUsdMatrix4x4;

RUSD_API int RUsd_BeginExport(
    const char* outputPath,
    const char* rootPrimName,
    float metersPerUnit,
    RUsdContext** outContext);

RUSD_API int RUsd_BeginExportEx(
    const char* outputPath,
    const char* rootPrimName,
    float metersPerUnit,
    int captureDiagnostics,
    RUsdContext** outContext);

RUSD_API int RUsd_AddMesh(
    RUsdContext* context,
    const char* primPath,
    const RUsdVec3* points,
    int pointCount,
    const int* indices,
    int indexCount,
    const RUsdVec3* normals,
    int normalCount,
    const RUsdVec2* uv0,
    int uv0Count,
    const RUsdMaterial* material);

RUSD_API int RUsd_AddMeshEx(
    RUsdContext* context,
    const char* primPath,
    const RUsdVec3* points,
    int pointCount,
    const int* indices,
    int indexCount,
    const RUsdSubmesh* submeshes,
    int submeshCount,
    const RUsdVec3* normals,
    int normalCount,
    const RUsdVec2* uv0,
    int uv0Count,
    const RUsdMaterial* materials,
    int materialCount,
    int visible,
    int writeExtent);

RUSD_API int RUsd_AddXform(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    int visible);

RUSD_API int RUsd_EndExport(RUsdContext* context);

RUSD_API void RUsd_Destroy(RUsdContext* context);

RUSD_API int RUsd_GetLastError(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity);

RUSD_API int RUsd_GetDiagnostics(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity);

RUSD_API int RUsd_OpenStage(
    const char* inputPath,
    int captureDiagnostics,
    RUsdContext** outContext);

RUSD_API int RUsd_GetImportInfo(
    RUsdContext* context,
    char* defaultPrimPathBuffer,
    int defaultPrimPathBufferCapacity,
    double* metersPerUnit,
    int* upAxis,
    int* meshCount,
    int* materialCount);

RUSD_API int RUsd_GetImportMeshInfo(
    RUsdContext* context,
    int meshIndex,
    char* primPathBuffer,
    int primPathBufferCapacity,
    char* nameBuffer,
    int nameBufferCapacity,
    int* pointCount,
    int* indexCount,
    int* normalCount,
    int* uv0Count,
    int* materialIndex,
    int* visible);

/* Transform-node table (API v2). Every Xformable prim is a node, and so is every typeless or
   unknown-schema prim (with an identity transform) unless it only holds materials; localMatrix is
   the prim's USD local-to-parent transform (row-vector convention, mRC = matrix[row R][col C]). */
RUSD_API int RUsd_GetImportNodeCount(RUsdContext* context, int* nodeCount);

RUSD_API int RUsd_GetImportNodeInfo(
    RUsdContext* context,
    int nodeIndex,
    char* pathBuffer,
    int pathBufferCapacity,
    RUsdMatrix4x4* localMatrix,
    int* visible);

/* Per-mesh material subsets (API v2). Each RUsdSubmesh is a contiguous triangle range in the
   mesh's index buffer (from RUsd_CopyImportMesh) bound to one material, mapping to a Unity
   submesh. A mesh with no GeomSubsets yields one submesh covering all faces. */
RUSD_API int RUsd_GetImportSubmeshCount(RUsdContext* context, int meshIndex, int* submeshCount);

RUSD_API int RUsd_CopyImportSubmeshes(
    RUsdContext* context,
    int meshIndex,
    RUsdSubmesh* submeshes,
    int submeshCapacity);

RUSD_API int RUsd_CopyImportMesh(
    RUsdContext* context,
    int meshIndex,
    RUsdVec3* points,
    int pointCapacity,
    int* indices,
    int indexCapacity,
    RUsdVec3* normals,
    int normalCapacity,
    RUsdVec2* uv0,
    int uv0Capacity);

/* Additional UV sets (API v3). Set 0 is delivered by RUsd_CopyImportMesh's `uv0`; sets
   1..RUSD_MAX_UV_SETS-1 are read through these. A set's count is either 0 (absent) or equal to
   the mesh's point count, so a non-empty set always maps 1:1 onto the imported vertices.
   These are additive: a host built against API v2 keeps working against a v3 plugin, and a
   v3 host must skip these calls when the loaded plugin reports an older version. */
RUSD_API int RUsd_GetImportMeshUvSetInfo(
    RUsdContext* context,
    int meshIndex,
    int uvSet,
    int* uvCount);

RUSD_API int RUsd_CopyImportMeshUvSet(
    RUsdContext* context,
    int meshIndex,
    int uvSet,
    RUsdVec2* uv,
    int uvCapacity);

/* Authored opacity (API v4). The texture path is non-empty when UsdPreviewSurface's `opacity`
   input is connected to a UsdUVTexture, whose alpha channel then drives transparency; it is
   usually the same file as the albedo. `opacityThreshold` is the authored threshold: > 0 means
   alpha cutout at that value, 0 means alpha blending. With no connection, the constant opacity
   in RUsdMaterial's alpha applies and the material is opaque unless that value is < 1.
   Additive, like the UV-set calls: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_GetImportMaterialOpacity(
    RUsdContext* context,
    int materialIndex,
    float* opacityThreshold,
    char* texturePathBuffer,
    int texturePathBufferCapacity);

/* How RUsdMaterial's metallicTexturePath is packed (API v9).

   Unity's metallic map is metallic in .r and smoothness in .a. Omniverse's OmniPBR packs
   occlusion, roughness and metallic into .r, .g and .b of one linear "ORM" map, which is a
   different layout for the same three facts -- so the file is usable, but only after its channels
   are moved. Rather than rewrite the path's meaning, the layout is reported here and the caller
   repacks while it already has the decoded pixels.

   *ormPacked is 1 when metallicTexturePath is an ORM map, 0 when it is already Unity's layout.
   *occlusionIsSeparate is 1 when an ao_texture was authored instead of ORM's red channel.

   Additive, like RUsd_GetImportMaterialOpacity: a host must skip this when the plugin reports an
   older API, and a material whose metallic map is absent reports 0 for both. */
RUSD_API int RUsd_GetImportMaterialMetallicLayout(
    RUsdContext* context,
    int materialIndex,
    int* ormPacked,
    int* occlusionIsSeparate,
    char* occlusionTexturePathBuffer,
    int occlusionTexturePathBufferCapacity);

/* API v11. Image codecs for the host's texture pipeline, callable from any thread.

   RUsd_ExrProbe reads an OpenEXR header and returns its data-window size (0 on success; 1 with
   a reason when the file is not one this reader takes: deep, multi-part, PIZ/PXR24/B44/DWA).
   RUsd_ExrDecode writes width*height*4 bytes of RGBA8, rows bottom-up, linear float clamped to
   [0, 1] and quantised -- sRGB-encoded when toSrgb is set. Returns 0, 1 with a reason, or 2 when
   zlib could not be opened on this platform (the host then decodes in managed code).
   RUsd_BlockCompressedSize is the byte size of the BC1 (alpha 0) or BC3 (alpha 1) chain down to
   1x1 that RUsd_BlockCompress writes, with its level count; both are 0 for a width or height
   that is not a positive multiple of 4. The layout, palette rule and sRGB-aware mip averaging
   are those of the managed UsdBlockCompressor, so the two produce the same bytes. */
RUSD_API int RUsd_ExrProbe(const unsigned char* data, int length, int* width, int* height,
                           char* reasonBuffer, int reasonCapacity);
RUSD_API int RUsd_ExrDecode(const unsigned char* data, int length, int toSrgb, unsigned char* rgba,
                            long long rgbaCapacity, char* reasonBuffer, int reasonCapacity);
RUSD_API long long RUsd_BlockCompressedSize(int width, int height, int alpha, int* mipCount);

/* API v12. A whole image in one call: PNG (8-bit, non-interlaced) or OpenEXR decoded, an ORM map
   repacked when asked, and the result handed back as RGBA8 (mode 0), its BC1/BC3 chain (mode 1;
   falls back to RGBA8 when the size cannot be compressed -- read `mode` back) or its red channel
   alone (mode 2). RUsd_UdimAtlasBuild does the same for a UDIM set, reading each tile file itself
   and stitching the grid the tile numbers describe. The handle owns the bytes; RUsd_ImageData
   points at them until RUsd_ImageRelease. All of it is thread-safe and reentrant. */
RUSD_API void* RUsd_ImageBuild(const unsigned char* data, int length, int srgb, int mode, int ormRepack,
                               char* reasonBuffer, int reasonCapacity);
RUSD_API void* RUsd_UdimAtlasBuild(const char* paths, const int* tiles, int count, int srgb, int mode,
                                   int ormRepack, int maxSide, char* reasonBuffer, int reasonCapacity);
RUSD_API int RUsd_ImageInfo(void* handle, int* width, int* height, int* mode, long long* byteCount, int* mips,
                            int* hasAlpha, int* columns, int* rows, int* minU, int* minV, int* tilesUsed);
RUSD_API const unsigned char* RUsd_ImageData(void* handle);
RUSD_API int RUsd_ImageNotes(void* handle, char* buffer, int capacity);
RUSD_API void RUsd_ImageRelease(void* handle);

/* API v12. The two packings the host does after decoding, as native images: Unity's packed
   metallic map (metallic .r from a plain metallic map or a constant, smoothness .a as 1 - the
   roughness map or a constant; strides are 1 for a red-only image, 4 for RGBA; the larger map
   sets the size) and an albedo with a separate opacity map folded into its alpha (white when
   there is no albedo). Same handle protocol as RUsd_ImageBuild. */
RUSD_API void* RUsd_PackMetallicRoughness(const unsigned char* metallic, int metallicWidth, int metallicHeight,
                                          int metallicStride, const unsigned char* roughness, int roughnessWidth,
                                          int roughnessHeight, int roughnessStride, int metallicConstant,
                                          int smoothnessConstant, int mode);
RUSD_API void* RUsd_PackAlbedoOpacity(const unsigned char* albedo, int albedoWidth, int albedoHeight,
                                      const unsigned char* opacity, int opacityWidth, int opacityHeight,
                                      int opacityStride, int srgb, int mode);
RUSD_API int RUsd_BlockCompress(const unsigned char* rgba, int width, int height, int alpha, int srgb,
                                unsigned char* output, long long outputCapacity);

/* API v10. Texture maps the material carries in a layout Unity's packed metallic map cannot
   take as it is: *metallicIsPlain is 1 when metallicTexturePath is a plain metallic map
   (metallic in .r, no smoothness in .a), and roughnessTexturePathBuffer receives a roughness map
   authored in a file of its own (empty when roughness is in the metallic file, or constant).
   The host folds 1 - roughness into the packed map's alpha while it holds the pixels.

   Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_GetImportMaterialTextureLayout(
    RUsdContext* context,
    int materialIndex,
    int* metallicIsPlain,
    char* roughnessTexturePathBuffer,
    int roughnessTexturePathBufferCapacity);

RUSD_API int RUsd_GetImportMaterial(
    RUsdContext* context,
    int materialIndex,
    RUsdMaterial* material,
    char* nameBuffer,
    int nameBufferCapacity);

/* USDZ packaging (API v5). usdz is a read-only format, so an export writes an ordinary
   .usdc/.usda stage first and then packages it here: the package gets a localized copy of that
   stage plus every asset it depends on (textures included). `firstLayerName` is the name the
   root layer takes inside the package (keep a usd extension); empty keeps the source name.
   `arkitCompatible` applies ARKit's stricter constraints, which may drop features such as
   variant sets. The source stage and its textures on disk are left untouched, and `context` is
   only used to report the error (it may be NULL, in which case RUsd_GetLastError(NULL) has it).
   Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_CreateUsdzPackage(
    RUsdContext* context,
    const char* sourceUsdPath,
    const char* usdzPath,
    const char* firstLayerName,
    int arkitCompatible);

/* Asset bytes read through the open stage's resolver (API v5). `assetPath` is an authored asset
   path exactly as RUsd_GetImportMaterial reports it; it is resolved the way USD resolved it when
   the material was read, so a texture inside a .usdz package - which no file-system read can
   reach - comes back here. Call once with buffer == NULL to learn `byteCount`, then again with a
   buffer at least that large. `resolvedPathBuffer` receives the resolved identifier, which is
   stable enough to use as a cache key. Fails when the asset does not resolve or cannot be
   opened. Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_ReadImportAsset(
    RUsdContext* context,
    const char* assetPath,
    char* resolvedPathBuffer,
    int resolvedPathBufferCapacity,
    unsigned char* buffer,
    long long bufferCapacity,
    long long* byteCount);

/* ---- Composition (API v6) ----
   Authoring calls for exports that keep USD composition instead of flattening it: sublayers,
   references, payloads, instancing, variant selections and sparse overrides. All of them are
   additive: a host must skip them when the plugin reports an older API. */

/* RUsd_BeginLayerExport flags. */
#define RUSD_LAYER_DEFINE_DEFAULT_PRIM 1 /* define defaultPrimPath as an Xform */
#define RUSD_LAYER_CAPTURE_DIAGNOSTICS 2

/* Like RUsd_BeginExportEx, but nothing is authored that the caller did not ask for, so the layer
   can sit over another stage. `defaultPrimPath` ("/World", may be empty) becomes the layer's
   defaultPrim and the parent of generated materials unless `materialScopePath` names another
   prim. `metersPerUnit` <= 0 and `upAxis` 0 leave that metadata unauthored; upAxis 1 = Y, 2 = Z.
   `subLayers` is a newline-separated list of layer paths, strongest first, written into the
   layer's subLayers before the stage composes. */
RUSD_API int RUsd_BeginLayerExport(
    const char* outputPath,
    const char* defaultPrimPath,
    const char* materialScopePath,
    double metersPerUnit,
    int upAxis,
    const char* subLayers,
    int flags,
    RUsdContext** outContext);

#define RUSD_SPECIFIER_DEF 0
#define RUSD_SPECIFIER_OVER 1
#define RUSD_SPECIFIER_CLASS 2

/* Authors a prim spec with the given specifier; `typeName` may be empty (keeps or omits the
   type). Missing ancestors are created as `over`. */
RUSD_API int RUsd_DefinePrim(
    RUsdContext* context,
    const char* primPath,
    const char* typeName,
    int specifier);

#define RUSD_ARC_REFERENCE 0
#define RUSD_ARC_PAYLOAD 1

/* Prepends a reference or payload on `primPath`. An empty `assetPath` makes an internal arc to
   `targetPrimPath` in the same layer stack; an empty `targetPrimPath` targets the asset's
   defaultPrim. The asset path is written as given, so pass a path relative to the layer to keep
   the files relocatable. */
RUSD_API int RUsd_AddReference(
    RUsdContext* context,
    const char* primPath,
    const char* assetPath,
    const char* targetPrimPath,
    int arcType);

/* Sparse overrides. Each one writes only its own opinion into the export layer and creates an
   `over` when the layer has no spec for the prim yet, so they apply equally to prims the layer
   defines and to prims a sublayer or reference brings in. Editing below an instance needs that
   instance's instanceable cleared first (RUsd_SetInstanceable 0). */
RUSD_API int RUsd_SetInstanceable(RUsdContext* context, const char* primPath, int instanceable);
RUSD_API int RUsd_SetActive(RUsdContext* context, const char* primPath, int active);
RUSD_API int RUsd_SetVariantSelection(
    RUsdContext* context,
    const char* primPath,
    const char* variantSet,
    const char* variant);
/* Replaces the prim's transform with a single matrix op (xformOpOrder included). */
RUSD_API int RUsd_SetLocalTransform(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix);
RUSD_API int RUsd_SetVisibility(RUsdContext* context, const char* primPath, int visible);

/* Whether the export stage composes a prim at `primPath` (from this layer or its sublayers). */
RUSD_API int RUsd_PrimExists(RUsdContext* context, const char* primPath, int* exists);

/* RUsd_OpenStageEx flags. */
#define RUSD_OPEN_CAPTURE_DIAGNOSTICS 1
#define RUSD_OPEN_LOAD_NO_PAYLOADS 2

/* RUsd_OpenStage with composition control. `variantSelections` is newline-separated records of
   "primPath\tvariantSet\tvariant", applied in order in the session layer (no file is edited).
   With RUSD_OPEN_LOAD_NO_PAYLOADS no payload is loaded except the prims listed, newline-separated,
   in `loadPaths`; an unloaded payload's own prim still comes through as an empty node. */
RUSD_API int RUsd_OpenStageEx(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    RUsdContext** outContext);

/* Composition flags of an imported node (RUsd_GetImportNodeInfo's index). */
#define RUSD_NODE_INSTANCE 1        /* an instanceable prim (an instance root) */
#define RUSD_NODE_INSTANCE_PROXY 2  /* below an instance: read-only until the instance is cleared */
#define RUSD_NODE_PAYLOAD 4         /* has a payload */
#define RUSD_NODE_UNLOADED 8        /* ... that is not loaded */
#define RUSD_NODE_REFERENCES 16     /* has references */
#define RUSD_NODE_VARIANT_SETS 32   /* has variant sets */
#define RUSD_NODE_INHERITS 64       /* has inherits or specializes */

RUSD_API int RUsd_GetImportNodeFlags(RUsdContext* context, int nodeIndex, int* flags);

/* The opened stage's composition as newline-separated, tab-separated records:
     V  primPath  variantSet  selection  variant...   (one per variant set)
     P  primPath  loaded(0|1)                          (prims with payloads)
     I  primPath                                       (instances)
     A  primPath  kind  layerIdentifier  targetPrimPath (direct reference / payload / inherit /
                                                          specialize / relocate arcs)
   Instance proxies are not listed; their arcs belong to the prototype. Call with buffer == NULL
   to learn `requiredLength` (terminator included), then with a buffer that large. */
RUSD_API int RUsd_GetImportComposition(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity,
    int* requiredLength);

/* Scene components (API v7): lights, cameras and spatial audio read on import. Each belongs to a
   node (RUsd_GetImportNodeInfo's index); every value is in that node's local space and the stage's
   units, as USD authors it. Additive: a host must skip these when the plugin reports an older API.

   UsdLux lights emit along their local -Z, and a UsdGeomCamera looks down -Z with +Y up, as USD
   defines them; the host turns that into its own convention. */
#define RUSD_LIGHT_DISTANT 1
#define RUSD_LIGHT_SPHERE 2
#define RUSD_LIGHT_RECT 3
#define RUSD_LIGHT_DISK 4
#define RUSD_LIGHT_CYLINDER 5
#define RUSD_LIGHT_DOME 6

typedef struct RUsdImportLight
{
    int nodeIndex;
    int type;                  /* RUSD_LIGHT_* */
    float color[3];            /* inputs:color, times the colour temperature when it is enabled */
    float intensity;           /* inputs:intensity x 2^inputs:exposure */
    float radius;              /* sphere, disk, cylinder */
    float width;               /* rect */
    float height;              /* rect */
    float length;              /* cylinder */
    float angle;               /* distant: angular diameter in degrees */
    float coneAngle;           /* shaping:cone:angle in degrees; 0 when the light is not shaped */
    float coneSoftness;        /* shaping:cone:softness */
    int normalize;             /* inputs:normalize */
} RUsdImportLight;

/* Point instancers drawn from their arrays (API v8). A PointInstancer scatters prototypes by
   index and transform; expanding that into one prim per instance does not scale, as Moore Lane's
   229,717 instances show. Each record here is one prototype mesh together with its placement for
   every instance, so the host can draw them instanced and keep one GameObject per instancer.
   The prototype itself is imported once as hidden nodes, so nothing is drawn in place.
   Additive, like the UV-set calls: a host must skip these when the plugin reports an older API. */
typedef struct RUsdImportInstancedMesh
{
    int instancerNodeIndex;  /* node carrying the instancer's own transform */
    int meshIndex;           /* the prototype mesh being instanced */
    int instanceCount;       /* matrices to read: instanceCount * 16 floats */
} RUsdImportInstancedMesh;

RUSD_API int RUsd_GetImportInstancedMeshCount(RUsdContext* context, int* count);

RUSD_API int RUsd_GetImportInstancedMesh(
    RUsdContext* context,
    int index,
    RUsdImportInstancedMesh* instanced);

/* Row-major 4x4 per instance, relative to the instancer node, with the mesh's own place inside
   the prototype already folded in. `capacity` counts floats. */
RUSD_API int RUsd_CopyImportInstanceMatrices(
    RUsdContext* context,
    int index,
    float* matrices,
    int capacity);

RUSD_API int RUsd_GetImportLightCount(RUsdContext* context, int* lightCount);

/* `texturePathBuffer` receives a dome or rect light's inputs:texture:file (authored, relative to
   the stage folder when it resolves inside it, like material textures); empty when none. */
RUSD_API int RUsd_GetImportLight(
    RUsdContext* context,
    int lightIndex,
    RUsdImportLight* light,
    char* texturePathBuffer,
    int texturePathBufferCapacity);

#define RUSD_CAMERA_PERSPECTIVE 0
#define RUSD_CAMERA_ORTHOGRAPHIC 1

typedef struct RUsdImportCamera
{
    int nodeIndex;
    int projection;            /* RUSD_CAMERA_* */
    float focalLength;         /* tenths of a stage unit, as authored (ratios with the apertures) */
    float horizontalAperture;
    float verticalAperture;
    float nearClip;            /* clippingRange, stage units */
    float farClip;
} RUsdImportCamera;

RUSD_API int RUsd_GetImportCameraCount(RUsdContext* context, int* cameraCount);
RUSD_API int RUsd_GetImportCamera(RUsdContext* context, int cameraIndex, RUsdImportCamera* camera);

#define RUSD_AUDIO_SPATIAL 0
#define RUSD_AUDIO_NONSPATIAL 1

typedef struct RUsdImportAudio
{
    int nodeIndex;
    int auralMode;             /* RUSD_AUDIO_* */
    int loop;                  /* playbackMode loopFromStart, loopFromStartToEnd or loopFromStage */
    float gain;
    double startTime;          /* time codes */
    double endTime;
} RUsdImportAudio;

RUSD_API int RUsd_GetImportAudioCount(RUsdContext* context, int* audioCount);

/* `filePathBuffer` receives inputs:filePath on the same terms as a texture path. */
RUSD_API int RUsd_GetImportAudio(
    RUsdContext* context,
    int audioIndex,
    RUsdImportAudio* audio,
    char* filePathBuffer,
    int filePathBufferCapacity);

#ifdef __cplusplus
}
#endif
