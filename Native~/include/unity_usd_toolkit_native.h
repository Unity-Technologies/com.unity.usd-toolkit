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

/* An output path ending in .drc (API v13) builds the stage in memory, and RUsd_EndExport encodes
   it as one Draco mesh through usdDraco's writer: every visible mesh at its first time, merged in
   the stage's space, with normals and primary texture coordinates where every mesh has them. No
   hierarchy, material or animation survives; a stage with no visible mesh fails RUsd_EndExport. */
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

/* ---- API v13: extra mesh data, lights, cameras and material flavours on export ----------- */

/* Adds Unity uv1 or uv2 to a mesh written by RUsd_AddMesh(Ex) as primvars:st1 / primvars:st2
   (texCoord2f[], vertex), the names the importer reads them back from. count must equal the
   mesh's point count. */
RUSD_API int RUsd_SetMeshUvSet(
    RUsdContext* context,
    const char* primPath,
    int uvSet,
    const RUsdVec2* uv,
    int count);

typedef struct RUsdVec4
{
    float x;
    float y;
    float z;
    float w;
} RUsdVec4;

/* Writes per-vertex colours (linear RGBA) as primvars:displayColor (vertex) and, when any alpha
   is below 1 or the mesh already has displayOpacity, primvars:displayOpacity. Replaces the
   material colour RUsd_AddMesh(Ex) mirrored there. count must equal the mesh's point count. */
RUSD_API int RUsd_SetMeshVertexColors(
    RUsdContext* context,
    const char* primPath,
    const RUsdVec4* colors,
    int count);

/* Material flavours RUsd_AddMesh(Ex) writes beside UsdPreviewSurface (the universal surface,
   always written). Set before adding meshes; materials already written keep theirs. */
#define RUSD_MATERIAL_FLAVOR_OMNIPBR 1   /* outputs:mdl:* -> OmniPBR.mdl */
#define RUSD_MATERIAL_FLAVOR_MATERIALX 2 /* outputs:mtlx:surface -> ND_standard_surface_surfaceshader */

RUSD_API int RUsd_SetExportMaterialFlavors(RUsdContext* context, int flags);

/* A UsdLux light. type is RUSD_LIGHT_DISTANT, _SPHERE, _RECT or _DISK. The prim gets the
   matrix as its local transform; USD lights emit along their -Z. */
typedef struct RUsdExportLight
{
    int type;
    float color[3];           /* linear */
    float intensity;          /* inputs:intensity */
    float exposure;           /* inputs:exposure */
    int normalize;            /* inputs:normalize */
    float radius;             /* sphere, disk */
    float width;              /* rect */
    float height;             /* rect */
    float angle;              /* distant: inputs:angle (degrees) */
    float coneAngle;          /* > 0: ShapingAPI shaping:cone:angle (degrees, half angle) */
    float coneSoftness;       /* shaping:cone:softness */
    int castShadows;          /* ShadowAPI shadow:enable */
    float colorTemperature;   /* > 0: enableColorTemperature + colorTemperature (kelvin) */
} RUsdExportLight;

RUSD_API int RUsd_AddLight(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    const RUsdExportLight* light,
    int visible);

/* A UsdGeomCamera. Apertures and focal length use the same units the importer reads (mm for
   a perspective camera; tenths of a stage unit for an orthographic one); clip range is in stage
   units. USD cameras look down their -Z. */
typedef struct RUsdExportCamera
{
    int projection;           /* RUSD_CAMERA_PERSPECTIVE / RUSD_CAMERA_ORTHOGRAPHIC */
    float focalLength;
    float horizontalAperture;
    float verticalAperture;
    float horizontalApertureOffset;
    float verticalApertureOffset;
    float nearClip;
    float farClip;
} RUsdExportCamera;

RUSD_API int RUsd_AddCamera(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    const RUsdExportCamera* camera,
    int visible);

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

/* API v13. Remote MDL modules. The import never downloads: a shader whose info:mdl:sourceAsset is
   an http(s) URL is read from <folder>/<host>/<path> when that file exists (a module that is one
   OmniPBR call, whose arguments then stand in for the inputs the stage does not author), and
   otherwise its URL is listed for the host, which may fetch it and import again.
   RUsd_SetRemoteAssetCache sets the folder for every later import (NULL or "" turns the cache
   off); the requests belong to the import context.

   Additive: a host must skip these when the plugin reports an older API. */
RUSD_API int RUsd_SetRemoteAssetCache(const char* folder);
RUSD_API int RUsd_GetImportRemoteRequestCount(RUsdContext* context, int* count);
RUSD_API int RUsd_GetImportRemoteRequest(RUsdContext* context, int index, char* buffer, int bufferCapacity);

/* API v13. Whether a material's normal map has its tangent frame flipped along U / V: OmniPBR's
   flip_tangent_u (default 0) and flip_tangent_v (default 1, i.e. DirectX-style green, which is
   how Isaac Sim reads an OmniPBR normal map unless told otherwise). 0 for every other material.

   Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_GetImportMaterialNormalFlip(RUsdContext* context, int materialIndex, int* flipU, int* flipV);

/* API v13. The colour space authored for a texture path the import reported: a UsdUVTexture's
   sourceColorSpace or the colorSpace metadata on the attribute holding the asset (followed
   through connections). Empty when none is authored ("auto"). Isaac Sim reads an 8-bit file under
   "auto" as sRGB, normal maps included, so a host matching it decodes those that way.

   Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_GetImportTextureColorSpace(
    RUsdContext* context,
    const char* texturePath,
    char* buffer,
    int bufferCapacity);

/* API v13 (v14 before the UsdSkel merge). How the albedo mask is used and the roughness range, 13 floats: [0] mode (-1 none,
   0 lerp(maskColor, albedo, mask.r), 1 lerp(lerp(lerp(lerp(0, c0, m.r), c1, m.g), c2, m.b), albedo, m.a)),
   [1..9] c0, c1, c2 (linear RGB), [10] 1 when roughness = lerp([11], [12], ORM.g). Additive. */
RUSD_API int RUsd_GetImportMaterialUnrealBlend(
    RUsdContext* context,
    int materialIndex,
    float* values);

/* API v13 (v13 before the UsdSkel merge). An albedo blended with a constant colour by a mask texture: the base colour is
   lerp(maskColorRgb, albedo, mask), linear. maskTexturePathBuffer is empty when the material has
   none. Additive: a host must skip this when the plugin reports an older API. */
RUSD_API int RUsd_GetImportMaterialAlbedoMask(
    RUsdContext* context,
    int materialIndex,
    char* maskTexturePathBuffer,
    int maskTexturePathBufferCapacity,
    float* maskColorRgb);

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

/* Moves a prim in namespace (API v13): appends (sourcePath, targetPath) to the export layer's
   relocates, so the prim -- with everything its composition brings -- is composed at targetPath,
   and later overrides for it are written there. Pcp honours a relocate only for a prim that comes
   in across a composition arc (a reference or payload): the same layer stack's own prims, which a
   sublayer brings, cannot be relocated, and an opinion left at sourcePath is ignored with an
   error. Root prims, a move into the prim's own subtree, and a source or target the layer already
   relocates are refused. */
RUSD_API int RUsd_AddRelocate(RUsdContext* context, const char* sourcePath, const char* targetPath);
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
#define RUSD_OPEN_DEFAULT_TIME 4    /* API v13: read static values at UsdTimeCode::Default() */

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

/* Import budgets (API v13). RUsd_OpenStageEx imports within fixed limits -- a stage is untrusted
   input, and a few hundred bytes of it can describe unbounded work -- and fails the import, naming
   the limit, once the nodes, meshes, point-instancer placements (instances times prototype meshes)
   or points and triangle indices of every mesh pass them. RUsd_OpenStageWithLimits sets them for
   one import: a field of 0 (or less) keeps its default, and with skipWhenExceeded set what does not
   fit is left out with a warning in the diagnostics instead -- the walk stops at the node limit, a
   mesh over the mesh or geometry limit is skipped (with its instance copies), and an instancer's
   placements over theirs are not drawn. The animation limits (samples per track, animation values
   in all, joints per skeleton) always drop the one track, skeleton animation or blend shape.
   structSize is sizeof(RUsdImportLimits); a smaller one is refused. Defaults: 2,000,000 nodes,
   1,000,000 meshes, 4,000,000 placements, 268,435,456 points and indices, 1,000,000 samples per
   track, 67,108,864 animation values, 65,536 joints. */
typedef struct RUsdImportLimits
{
    int structSize;
    int skipWhenExceeded;
    long long maxNodes;
    long long maxMeshes;
    long long maxInstancePlacements;
    long long maxGeometryElements;
    long long maxTrackSamples;
    long long maxAnimationValues;
    long long maxJoints;
} RUsdImportLimits;

RUSD_API int RUsd_OpenStageWithLimits(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    const RUsdImportLimits* limits,
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

/* ---- Time, animation and skinning (API v13) ------------------------------------------------
   Additive, like the UV-set calls: a host must skip these when the plugin reports an older API.

   Static values -- points, primvars, transforms, visibility, implicit surfaces, lights and
   cameras -- are read at one time code: the stage's startTimeCode when the stage authors a time
   range, else UsdTimeCode::EarliestTime() (an attribute's first sample when it has samples, its
   default otherwise). Older plugins read at UsdTimeCode::Default(), which ignores time samples, so
   a stage that authors its transforms only as samples (Blender's animated exports) lost them;
   RUSD_OPEN_DEFAULT_TIME restores that reading. */
#define RUSD_READ_TIME_DEFAULT 0
#define RUSD_READ_TIME_START 1
#define RUSD_READ_TIME_EARLIEST 2

typedef struct RUsdImportTimeInfo
{
    double startTimeCode;
    double endTimeCode;
    double timeCodesPerSecond;
    double framesPerSecond;
    int hasTimeRange;          /* startTimeCode and endTimeCode are authored */
    int readTimeKind;          /* RUSD_READ_TIME_*: when static values were read */
} RUsdImportTimeInfo;

RUSD_API int RUsd_GetImportTimeInfo(RUsdContext* context, RUsdImportTimeInfo* info);

/* Animated nodes. One track per node and kind whose value has more than one time sample. Times
   are stage time codes, ascending: for a transform the union of the node's xformOp samples, with
   the node's local-to-parent matrix at each (16 floats, row-major like RUsdMatrix4x4); for
   visibility the attribute's own samples, 1 visible or 0 invisible, held until the next sample.
   Visibility here is the node's own opinion, not the inherited result. */
#define RUSD_TRACK_TRANSFORM 1
#define RUSD_TRACK_VISIBILITY 2

typedef struct RUsdImportNodeTrack
{
    int nodeIndex;
    int kind;                  /* RUSD_TRACK_* */
    int sampleCount;
} RUsdImportNodeTrack;

RUSD_API int RUsd_GetImportNodeTrackCount(RUsdContext* context, int* count);
RUSD_API int RUsd_GetImportNodeTrack(RUsdContext* context, int index, RUsdImportNodeTrack* track);

/* `times` takes sampleCount doubles, `values` sampleCount * 16 floats (transform) or sampleCount
   floats (visibility). Capacities count elements. */
RUSD_API int RUsd_CopyImportNodeTrack(
    RUsdContext* context,
    int index,
    double* times,
    int timeCapacity,
    float* values,
    int valueCapacity);

/* Skeletons bound to skinned meshes, one record per UsdSkelSkeleton. nodeIndex is the node the
   Skeleton prim became (-1 if the walk never reached it). Joints are in the skeleton's order,
   named by their joint paths ("Shoulder/Elbow") and written newline-separated into
   `jointNames`; parents[i] is joint i's parent index or -1. Bind transforms are in skeleton
   space, rest and pose transforms joint-local; pose is the skeleton at the time static values
   were read (its animation there, else its rest pose). Matrices are 16 floats each, row-major in
   USD's row-vector convention. An animated skeleton has sampleCount > 0 joint-local poses. */
typedef struct RUsdImportSkeleton
{
    int nodeIndex;
    int jointCount;
    int sampleCount;
    int jointNamesLength;      /* bytes jointNames needs, including the terminator */
} RUsdImportSkeleton;

RUSD_API int RUsd_GetImportSkeletonCount(RUsdContext* context, int* count);
RUSD_API int RUsd_GetImportSkeleton(RUsdContext* context, int index, RUsdImportSkeleton* skeleton);

/* `jointCapacity` counts joints: parents takes that many ints, each transform array 16 times
   that many floats. */
RUSD_API int RUsd_CopyImportSkeletonJoints(
    RUsdContext* context,
    int index,
    char* jointNames,
    int jointNamesCapacity,
    int* parents,
    float* bindTransforms,
    float* restTransforms,
    float* poseTransforms,
    int jointCapacity);

/* `times` takes sampleCount doubles, `transforms` sampleCount * jointCount * 16 floats: every
   joint at the first sample, then every joint at the next. */
RUSD_API int RUsd_CopyImportSkeletonAnimation(
    RUsdContext* context,
    int index,
    double* times,
    int timeCapacity,
    float* transforms,
    int transformCapacity);

/* Skinning of an imported mesh. skeletonIndex is -1 for a mesh that is not skinned. Weights are
   per imported vertex (after vertex splitting), influencesPerVertex pairs each, normalised, with
   joint indices in the skeleton's order; geomBindTransform is the mesh's bind-time transform into
   skeleton space (USD convention). Blend-shape weights are in the mesh's skel:blendShapes order. */
typedef struct RUsdImportMeshSkin
{
    int skeletonIndex;
    int influencesPerVertex;
    int blendShapeCount;
    int blendShapeSampleCount; /* samples of the blend-shape weights; 0 when not animated */
    RUsdMatrix4x4 geomBindTransform;
    int dualQuaternion;        /* 1 when skel:skinningMethod is dualQuaternion, else 0 (classicLinear) */
} RUsdImportMeshSkin;

RUSD_API int RUsd_GetImportMeshSkin(RUsdContext* context, int meshIndex, RUsdImportMeshSkin* skin);

/* `capacity` counts elements of each array: vertexCount * influencesPerVertex. */
RUSD_API int RUsd_CopyImportMeshSkinWeights(
    RUsdContext* context,
    int meshIndex,
    int* jointIndices,
    float* jointWeights,
    int capacity);

/* A blend shape's frames are its inbetweens by ascending weight (only those strictly between 0
   and 1) followed by the full shape at weight 1. */
RUSD_API int RUsd_GetImportBlendShape(
    RUsdContext* context,
    int meshIndex,
    int shapeIndex,
    char* nameBuffer,
    int nameBufferCapacity,
    int* frameCount,
    int* hasNormals);

/* Dense per-vertex deltas in the same basis as the mesh's points. `deltaNormals` may be null, and
   is left untouched when the shape has no normal offsets. `vertexCapacity` counts vertices. */
RUSD_API int RUsd_CopyImportBlendShapeFrame(
    RUsdContext* context,
    int meshIndex,
    int shapeIndex,
    int frame,
    float* weight,
    RUsdVec3* deltaVertices,
    RUsdVec3* deltaNormals,
    int vertexCapacity);

/* The files the stage composed from: every layer OpenUSD used that has a path on disk (root,
   sublayers, referenced and payload layers that loaded, value-clip layers), newline-separated,
   each once. Package-relative layers inside a .usdz report the package. An asset importer uses
   this to reimport when any of them changes. Call with buffer == NULL to learn `requiredLength`
   (terminator included), then with a buffer that large. */
RUSD_API int RUsd_GetImportLayers(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity,
    int* requiredLength);

/* `weights` takes blendShapeCount floats (at the time static values were read); `times`
   blendShapeSampleCount doubles; `samples` blendShapeSampleCount * blendShapeCount floats. */
RUSD_API int RUsd_CopyImportMeshBlendShapeWeights(
    RUsdContext* context,
    int meshIndex,
    float* weights,
    int weightCapacity,
    double* times,
    int timeCapacity,
    float* samples,
    int sampleCapacity);

/* Animated points of an imported mesh (a point cache): a mesh that is not skinned and whose points
   are time-sampled, with topology that holds still and every point at every sample, gets
   sampleCount > 0. Points are every imported vertex (after vertex splitting, as RUsd_CopyImportMesh
   gives them) at every sample, the first sample first, in the same basis; normals likewise when
   hasNormals is 1 -- recomputed for each sample when the mesh authors none, read for each sample
   when authored ones animate -- and absent when the mesh keeps the normals it was imported with.
   Meshes inside point-instancer prototypes report none. */
typedef struct RUsdImportPointCache
{
    int sampleCount;
    int vertexCount;
    int hasNormals;
} RUsdImportPointCache;

RUSD_API int RUsd_GetImportMeshPointCache(RUsdContext* context, int meshIndex, RUsdImportPointCache* pointCache);

/* `times` takes sampleCount doubles; `points` and `normals` (may be null) sampleCount * vertexCount
   vectors each, which `valueCapacity` counts. */
RUSD_API int RUsd_CopyImportMeshPointCache(
    RUsdContext* context,
    int meshIndex,
    double* times,
    int timeCapacity,
    RUsdVec3* points,
    RUsdVec3* normals,
    int valueCapacity);

/* ---- Animation and skinning on export (API v13) ----------------------------------------------
   These write into a stage begun with RUsd_BeginExport(Ex), on prims RUsd_AddXform and
   RUsd_AddMesh(Ex) have defined. Matrices are 16 floats, row-major, USD's row-vector convention;
   times are stage time codes. */

/* startTimeCode, endTimeCode, timeCodesPerSecond and framesPerSecond of the stage. */
RUSD_API int RUsd_SetExportTimeRange(
    RUsdContext* context,
    double startTimeCode,
    double endTimeCode,
    double timeCodesPerSecond,
    double framesPerSecond);

/* Time samples of an Xform's local transform, on the single transform op RUsd_AddXform wrote;
   its default value stays for readers at Default time. */
RUSD_API int RUsd_SetXformSamples(
    RUsdContext* context,
    const char* primPath,
    const double* times,
    const float* matrices,
    int sampleCount);

/* Time samples of a prim's visibility: non-zero visible ("inherited"), 0 invisible. */
RUSD_API int RUsd_SetVisibilitySamples(
    RUsdContext* context,
    const char* primPath,
    const double* times,
    const int* visible,
    int sampleCount);

/* Time samples of a Mesh's points (and extent), in USD's basis, sampleCount x pointCount vectors,
   the first sample first; `normals` (vertex interpolation, same layout) may be null. The points'
   default value stays for readers at Default time. */
RUSD_API int RUsd_SetPointsSamples(
    RUsdContext* context,
    const char* meshPath,
    const double* times,
    const RUsdVec3* points,
    const RUsdVec3* normals,
    int pointCount,
    int sampleCount);

/* Turns an existing Xform into a SkelRoot, keeping its transform. */
RUSD_API int RUsd_DefineSkelRoot(RUsdContext* context, const char* primPath);

/* Defines a Skeleton. `jointPaths` is newline-separated joint paths ("Hips/Spine"), parents
   before children; bind transforms are in the Skeleton's space, rest transforms joint-local. */
RUSD_API int RUsd_AddSkeleton(
    RUsdContext* context,
    const char* skeletonPath,
    const char* jointPaths,
    int jointCount,
    const float* bindTransforms,
    const float* restTransforms);

/* Binds a mesh to a skeleton: `influences` joint indices (in the skeleton's order) and weights
   per point, and the mesh's geomBindTransform into skeleton space (null for identity). */
RUSD_API int RUsd_BindSkinnedMesh(
    RUsdContext* context,
    const char* meshPath,
    const char* skeletonPath,
    const int* jointIndices,
    const float* jointWeights,
    int pointCount,
    int influences,
    const RUsdMatrix4x4* geomBindTransform);

/* skel:skinningMethod of a bound mesh: dualQuaternion when non-zero, classicLinear otherwise. */
RUSD_API int RUsd_SetSkinningMethod(RUsdContext* context, const char* meshPath, int dualQuaternion);

/* One frame of a blend shape on a mesh, at `weight`: 1 writes the shape itself, a weight between
   0 and 1 an inbetween. `pointIndices` are the points every frame of the shape moves (pass the
   same list for each frame); `normalOffsets` may be null. The shape is added to the mesh's
   skel:blendShapes under its name made a valid identifier. */
RUSD_API int RUsd_AddBlendShapeFrame(
    RUsdContext* context,
    const char* meshPath,
    const char* shapeName,
    float weight,
    const int* pointIndices,
    int pointCount,
    const RUsdVec3* offsets,
    const RUsdVec3* normalOffsets);

/* The skeleton's animation as a SkelAnimation child bound as its animation source: joint-local
   transforms for every joint (sampleCount x jointCount x 16 floats) and blend-shape weights
   (sampleCount x shapeCount, `blendShapeNames` newline-separated, as RUsd_AddBlendShapeFrame
   named them). With `times` null there is one sample, at Default time. */
RUSD_API int RUsd_SetSkeletonAnimation(
    RUsdContext* context,
    const char* skeletonPath,
    const char* blendShapeNames,
    int shapeCount,
    const double* times,
    int sampleCount,
    const float* jointTransforms,
    const float* blendShapeWeights);

/* ---- Stage API (API v13) ---------------------------------------------------------------------
   A stage kept open for reading and editing: a context RUsd_CreateStage makes, one
   RUsd_OpenStageWithLimits opens with RUSD_OPEN_NO_READ, or any import context until RUsd_Destroy.
   Prim paths are absolute; names are property names ("points", "primvars:st"). Edits go to the
   edit target (RUsd_SetEditTarget; the root layer until it is changed) through the Usd API, so
   they are refused below an instance (an instance proxy). Layers are shared by every stage in the
   process that uses them: an unsaved edit is seen by another stage, or an import, of the same file.
   A context must not be used from two threads at once. */

#define RUSD_OPEN_NO_READ 8 /* API v13: open (selections and loads applied) without walking the stage */

/* A new stage whose root layer is written to `path` by RUsd_SaveStage, or, with `inMemory`, an
   anonymous one (`path` may be empty) that only RUsd_ExportStage writes out. */
RUSD_API int RUsd_CreateStage(const char* path, int inMemory, RUsdContext** outContext);

/* Saves every edited layer the stage uses except the session layer (UsdStage::Save). */
RUSD_API int RUsd_SaveStage(RUsdContext* context);

/* Writes the root layer to `path` -- or, with `flatten`, the composed stage as one layer -- in the
   format the extension names; the stage is unchanged. */
RUSD_API int RUsd_ExportStage(RUsdContext* context, const char* path, int flatten);

/* Rereads every layer the stage uses from disk, dropping their unsaved edits (UsdStage::Reload),
   for every stage that shares them. */
RUSD_API int RUsd_ReloadStage(RUsdContext* context);

typedef struct RUsdStageInfo
{
    int structSize;      /* sizeof(RUsdStageInfo); a smaller one is refused */
    int hasTimeRange;    /* startTimeCode and endTimeCode are authored */
    double startTimeCode;
    double endTimeCode;
    double timeCodesPerSecond;
    double framesPerSecond;
    double metersPerUnit;
    int upAxis;          /* 1 = Y, 2 = Z */
    int dirty;           /* a layer the stage uses, the session layer included, has unsaved edits */
} RUsdStageInfo;

RUSD_API int RUsd_GetStageInfo(RUsdContext* context, RUsdStageInfo* info);

/* RUsd_SetStageMetadata fields, authored in the edit target's layer (the root or session layer). */
#define RUSD_STAGE_START_TIME_CODE 1
#define RUSD_STAGE_END_TIME_CODE 2
#define RUSD_STAGE_TIME_CODES_PER_SECOND 3
#define RUSD_STAGE_FRAMES_PER_SECOND 4
#define RUSD_STAGE_METERS_PER_UNIT 5
#define RUSD_STAGE_UP_AXIS 6 /* 1 = Y, 2 = Z */

RUSD_API int RUsd_SetStageMetadata(RUsdContext* context, int field, double value);

#define RUSD_EDIT_TARGET_ROOT 0
#define RUSD_EDIT_TARGET_SESSION 1
#define RUSD_EDIT_TARGET_LAYER 2 /* `layer`: a layer of the root layer stack, by identifier or path */

/* Where edits go. With `variantPrimPath`, `variantSet` and `variant` all given, they go inside that
   variant of the prim, in the chosen layer; the variant must exist (RUSD_EDIT_ADD_VARIANT) and be the
   one the prim composes (RUSD_EDIT_SELECT_VARIANT), as OpenUSD makes specs inside a variant only then. */
RUSD_API int RUsd_SetEditTarget(
    RUsdContext* context,
    int target,
    const char* layer,
    const char* variantPrimPath,
    const char* variantSet,
    const char* variant);

/* RUsd_GetPrimInfo flags. */
#define RUSD_PRIM_VALID 1          /* the stage composes a prim at the path */
#define RUSD_PRIM_ACTIVE 2
#define RUSD_PRIM_DEFINED 4        /* a def, not only overs */
#define RUSD_PRIM_ABSTRACT 8       /* a class, or below one */
#define RUSD_PRIM_LOADED 16
#define RUSD_PRIM_HAS_PAYLOAD 32   /* authored payloads */
#define RUSD_PRIM_INSTANCE 64
#define RUSD_PRIM_INSTANCE_PROXY 128
#define RUSD_PRIM_VARIANT_SETS 256
#define RUSD_PRIM_MODEL 512        /* kind is a model kind */
#define RUSD_PRIM_HAS_REFERENCES 1024

/* `flags` 0 (and `specifier` -1) when no prim composes at the path. */
RUSD_API int RUsd_GetPrimInfo(RUsdContext* context, const char* primPath, int* flags, int* specifier);

/* RUsd_QueryText queries. Records are newline-separated, their fields tab-separated. */
#define RUSD_QUERY_DEFAULT_PRIM 0  /* the stage's defaultPrim path, or empty */
#define RUSD_QUERY_CHILDREN 1      /* child names: active, defined, not abstract, instance proxies included */
#define RUSD_QUERY_ALL_CHILDREN 2  /* every composed child name: inactive, over and class prims too */
#define RUSD_QUERY_TYPE_NAME 3     /* the prim's type name ("Mesh"); empty when typeless */
#define RUSD_QUERY_PROPERTIES 4    /* "name  a|r  typeName": attributes (with their value type, "point3f[]") and relationships */
#define RUSD_QUERY_VARIANT_SETS 5  /* "set  selection  variant..." */
#define RUSD_QUERY_TARGETS 6       /* relationship `name`'s targets, forwarded ones resolved */
#define RUSD_QUERY_METADATA 7      /* the prim's string or token metadata field `name` ("kind", "documentation") */
#define RUSD_QUERY_LAYERS 8        /* the layer stack, strongest first: the session layer, the root layer, its sublayers */
#define RUSD_QUERY_REBUILD_ROOT 9  /* where an import must be reread from after a change at primPath: the path,
                                      widened to its outermost SkelRoot or PointInstancer ancestor; empty when
                                      the change reaches past one subtree (a material, a skeleton animation,
                                      the stage itself) */
#define RUSD_QUERY_CHANGES 10      /* prims recomposed or edited since the last such query, an instance prototype's
                                      changes given as its instances; the call that copies them forgets them */

/* Call with buffer == NULL to learn `requiredLength` (terminator included), then with a buffer that
   large. An unknown prim, property or field gives empty text. */
RUSD_API int RUsd_QueryText(
    RUsdContext* context,
    int query,
    const char* primPath,
    const char* name,
    char* buffer,
    int bufferCapacity,
    int* requiredLength);

/* RUsd_EditPrim edits. `name` and `value` as each one says; others ignore them. */
#define RUSD_EDIT_DEFINE 1            /* def, typed `value` (may be empty); missing ancestors too */
#define RUSD_EDIT_OVERRIDE 2          /* over */
#define RUSD_EDIT_CLASS 3             /* class */
#define RUSD_EDIT_REMOVE 4            /* the prim's spec in the edit target, with everything below it */
#define RUSD_EDIT_ACTIVE 5            /* active = `value` "1" or "0"; empty clears the opinion */
#define RUSD_EDIT_LOAD 6              /* loads the prim's payloads and those below it (stage state, not authored) */
#define RUSD_EDIT_UNLOAD 7
#define RUSD_EDIT_TYPE_NAME 8         /* type name = `value` */
#define RUSD_EDIT_METADATA 9          /* string or token metadata field `name` ("kind", "documentation") = `value` */
#define RUSD_EDIT_INSTANCEABLE 10     /* instanceable = `value` "1" or "0"; empty clears the opinion */
#define RUSD_EDIT_DEFAULT_PRIM 11     /* the root prim becomes the stage's defaultPrim */
#define RUSD_EDIT_SELECT_VARIANT 12   /* set `name` selects `value`; empty clears the selection */
#define RUSD_EDIT_ADD_VARIANT 13      /* adds variant `value` to set `name`, making the set when needed */
#define RUSD_EDIT_ADD_REFERENCE 14    /* asset `name` (empty: this layer stack), prim `value` (empty: its defaultPrim) */
#define RUSD_EDIT_ADD_PAYLOAD 15      /* as RUSD_EDIT_ADD_REFERENCE */
#define RUSD_EDIT_CLEAR_REFERENCES 16
#define RUSD_EDIT_CLEAR_PAYLOADS 17
#define RUSD_EDIT_ADD_INHERIT 18      /* class prim `value` */
#define RUSD_EDIT_ADD_SPECIALIZE 19   /* prim `value` */
#define RUSD_EDIT_SET_TARGETS 20      /* relationship `name`, made when missing, targets `value` (newline-separated) */
#define RUSD_EDIT_REMOVE_PROPERTY 21  /* property `name`'s spec in the edit target */
#define RUSD_EDIT_CLEAR_VALUE 22      /* attribute `name`'s default and samples in the edit target */

RUSD_API int RUsd_EditPrim(RUsdContext* context, const char* primPath, int edit, const char* name, const char* value);

/* Attribute value types. Numbers are carried as their own C type; half as float; bool as one byte;
   quaternions as x, y, z, w (imaginary part first); matrices as row-major doubles; string, token and
   asset values as UTF-8, each NUL-terminated (an array's back to back). RUSD_VALUE_ARRAY marks the
   array form. An attribute is read and written only as the type it holds -- a point3f[] as
   RUSD_VALUE_FLOAT3 | RUSD_VALUE_ARRAY -- never converted. */
#define RUSD_VALUE_BOOL 1
#define RUSD_VALUE_UCHAR 2
#define RUSD_VALUE_INT 3
#define RUSD_VALUE_UINT 4
#define RUSD_VALUE_INT64 5
#define RUSD_VALUE_UINT64 6
#define RUSD_VALUE_HALF 7
#define RUSD_VALUE_FLOAT 8
#define RUSD_VALUE_DOUBLE 9
#define RUSD_VALUE_TIMECODE 10
#define RUSD_VALUE_STRING 11
#define RUSD_VALUE_TOKEN 12
#define RUSD_VALUE_ASSET 13
#define RUSD_VALUE_INT2 14
#define RUSD_VALUE_INT3 15
#define RUSD_VALUE_INT4 16
#define RUSD_VALUE_HALF2 17
#define RUSD_VALUE_HALF3 18
#define RUSD_VALUE_HALF4 19
#define RUSD_VALUE_FLOAT2 20
#define RUSD_VALUE_FLOAT3 21
#define RUSD_VALUE_FLOAT4 22
#define RUSD_VALUE_DOUBLE2 23
#define RUSD_VALUE_DOUBLE3 24
#define RUSD_VALUE_DOUBLE4 25
#define RUSD_VALUE_QUATH 26
#define RUSD_VALUE_QUATF 27
#define RUSD_VALUE_QUATD 28
#define RUSD_VALUE_MATRIX2D 29
#define RUSD_VALUE_MATRIX3D 30
#define RUSD_VALUE_MATRIX4D 31
#define RUSD_VALUE_ARRAY 0x100
#define RUSD_VALUE_RESOLVED 0x200 /* reading RUSD_VALUE_ASSET: the resolved path (empty when it does not resolve) */

/* Time a value is read or written at. */
#define RUSD_TIME_DEFAULT 0  /* UsdTimeCode::Default() */
#define RUSD_TIME_CODE 1     /* the time code `time` */
#define RUSD_TIME_EARLIEST 2 /* UsdTimeCode::EarliestTime() (reading) */

typedef struct RUsdAttributeInfo
{
    int structSize;        /* sizeof(RUsdAttributeInfo); a smaller one is refused */
    int exists;            /* the prim has an attribute of that name */
    int valueType;         /* RUSD_VALUE_* (| RUSD_VALUE_ARRAY) of its declared type; 0 when this API has no form for it */
    int hasValue;          /* an authored value or a fallback */
    int hasAuthoredValue;
    int timeSampleCount;
    int uniform;
    int custom;
} RUsdAttributeInfo;

RUSD_API int RUsd_GetAttributeInfo(RUsdContext* context, const char* primPath, const char* name, RUsdAttributeInfo* info);

/* Reads the value at `time`. `elementCount` is the array length (1 for a single value) and
   `requiredBytes` the bytes it takes, -1 when there is no value (none authored, or blocked, and no
   schema fallback). With `buffer` NULL or smaller than that nothing is copied: call again with a buffer that
   large (the value is held until then). Asking for a type the attribute does not hold fails, naming
   the type it holds. */
RUSD_API int RUsd_GetAttributeValue(
    RUsdContext* context,
    const char* primPath,
    const char* name,
    int timeKind,
    double time,
    int valueType,
    void* buffer,
    long long bufferBytes,
    long long* elementCount,
    long long* requiredBytes);

/* RUsd_SetAttributeValue flags. */
#define RUSD_SET_CUSTOM 1   /* when the attribute is made: custom */
#define RUSD_SET_UNIFORM 2  /* when the attribute is made: uniform */
#define RUSD_SET_BLOCK 4    /* author a value block instead of `data` (at the default: every sample too) */
#define RUSD_SET_CLEAR 8    /* clear the value at `time` (the default at RUSD_TIME_DEFAULT) instead */
/* A block or a clear needs only an existing attribute: `typeName`, `valueType` and the data are ignored. */

/* Writes a value at `time` in the edit target. A missing attribute is made with `typeName` ("float3",
   "point3f[]", "color3f"; empty: the plain name of `valueType`), which must hold `valueType`; an
   existing one must already hold it. `elementCount` is 1 for a single value; `dataBytes` the bytes
   at `data` -- for strings, every NUL-terminated string. */
RUSD_API int RUsd_SetAttributeValue(
    RUsdContext* context,
    const char* primPath,
    const char* name,
    const char* typeName,
    int flags,
    int timeKind,
    double time,
    int valueType,
    const void* data,
    long long elementCount,
    long long dataBytes);

/* The attribute's time samples, ascending. With `times` NULL, `count` alone is filled. */
RUSD_API int RUsd_GetAttributeTimeSamples(
    RUsdContext* context,
    const char* primPath,
    const char* name,
    double* times,
    int capacity,
    int* count);

#define RUSD_TRANSFORM_LOCAL 0 /* local to parent */
#define RUSD_TRANSFORM_WORLD 1 /* local to world */

/* An Xformable prim's transform at `time`, in USD's row-vector convention like RUsdMatrix4x4
   everywhere. A prim that is not Xformable gives identity (local) or its parent's (world). */
RUSD_API int RUsd_GetPrimTransform(
    RUsdContext* context,
    const char* primPath,
    int space,
    int timeKind,
    double time,
    RUsdMatrix4x4* matrix,
    int* resetsXformStack);

/* Writes the local transform at `time` in the edit target as one matrix op, xformOpOrder included
   (UsdGeomXformable::MakeMatrixXform). */
RUSD_API int RUsd_SetPrimTransform(
    RUsdContext* context,
    const char* primPath,
    int timeKind,
    double time,
    const RUsdMatrix4x4* matrix);

/* Rereads parts of an open stage into the import tables (API v13), replacing what they held: the
   subtree at each of `rootPaths` (newline-separated), walked as the full import walks it -- with the
   purpose, visibility and depth its ancestors pass down -- within `limits` (NULL: the defaults). A
   root that no longer composes (removed, inactive, under an unloaded payload) yields nothing. Widen
   each root first with RUSD_QUERY_REBUILD_ROOT. `flags` is reserved: 0. */
RUSD_API int RUsd_ReadSubtrees(
    RUsdContext* context,
    const char* rootPaths,
    int flags,
    const RUsdImportLimits* limits);

/* Frees the import tables -- meshes, materials, tracks and the rest -- and keeps the stage open. */
RUSD_API int RUsd_ReleaseImportData(RUsdContext* context);

/* ---- API v13: physics on import ----------------------------------------------------------------
   One call reads the open stage's physics with OpenUSD's own parser (UsdPhysicsLoadStageFromPrimRange)
   plus what that parser does not report -- UsdPhysicsMassAPI, collision mesh geometry, and NVIDIA's
   PhysX attributes, read by name because PhysxSchema is not part of this build -- and returns it as
   flat arrays the host reads in place. The plugin owns the block; it stays valid until
   RUsd_ReleasePhysics, RUsd_ReleaseImportData, the next RUsd_ReadPhysics, or the context closing.

   Space and units, as everywhere in this header: USD's, as authored. Lengths in the stage's units
   (metersPerUnit), masses in its kilogramsPerUnit, angles in degrees, right-handed, the stage's up
   axis; values at UsdTimeCode::Default(), as the parser reads them. The host converts. Quaternions
   are RUsdVec4 x, y, z, w, not GfQuatf's real-first order.

   Physics has no scale. As the parser reports it, a body's pose is its world transform split into
   position, rotation and scale; a collider's dimensions are world-scaled; and a collider's local
   position is relative to its body with the body's scale already applied.

   References between records are indices into the matching array (-1: none). Lists are
   RUsdPhysicsList (offset, count) into the `indices` or `floats` table, as each field says. Prim
   paths are string indices (`pathIndex`), and `nodeIndex` is RUsd_GetImportNodeInfo's index for the
   same prim, or -1 when the import did not walk it (a guide-purpose collision mesh, a prim under an
   unloaded payload, or physics read without an import).

   `setMask` bits mark attributes that are authored, so the host keeps its own default otherwise.

   A stage is untrusted input: the read is bounded by RUsdPhysicsReadOptions and fails, naming the
   limit, when the stage holds more (or, with skipWhenExceeded, keeps what fits and adds an
   RUSD_PHYSICS_UNSUPPORTED_LIMIT record). Additive: a host must skip this when the plugin reports an
   older API. */

/* Object kinds -- this API's own numbering, stable across OpenUSD versions. */
#define RUSD_PHYSICS_SHAPE_SPHERE 1
#define RUSD_PHYSICS_SHAPE_CUBE 2
#define RUSD_PHYSICS_SHAPE_CAPSULE 3            /* radius0 == radius1 */
#define RUSD_PHYSICS_SHAPE_CYLINDER 4           /* radius0 == radius1 */
#define RUSD_PHYSICS_SHAPE_CONE 5
#define RUSD_PHYSICS_SHAPE_PLANE 6
#define RUSD_PHYSICS_SHAPE_MESH 7
#define RUSD_PHYSICS_SHAPE_SPHERE_POINTS 8
#define RUSD_PHYSICS_SHAPE_CAPSULE_TAPERED 9    /* Capsule_1: radius0 top, radius1 bottom */
#define RUSD_PHYSICS_SHAPE_CYLINDER_TAPERED 10  /* Cylinder_1: radius0 top, radius1 bottom */

#define RUSD_PHYSICS_JOINT_FIXED 1
#define RUSD_PHYSICS_JOINT_REVOLUTE 2
#define RUSD_PHYSICS_JOINT_PRISMATIC 3
#define RUSD_PHYSICS_JOINT_SPHERICAL 4
#define RUSD_PHYSICS_JOINT_DISTANCE 5
#define RUSD_PHYSICS_JOINT_D6 6                 /* a plain PhysicsJoint: per-DOF limits and drives */

#define RUSD_PHYSICS_AXIS_X 0
#define RUSD_PHYSICS_AXIS_Y 1
#define RUSD_PHYSICS_AXIS_Z 2

/* Degrees of freedom: the index into RUsdPhysicsJoint.dofs. */
#define RUSD_PHYSICS_DOF_TRANS_X 0
#define RUSD_PHYSICS_DOF_TRANS_Y 1
#define RUSD_PHYSICS_DOF_TRANS_Z 2
#define RUSD_PHYSICS_DOF_ROT_X 3
#define RUSD_PHYSICS_DOF_ROT_Y 4
#define RUSD_PHYSICS_DOF_ROT_Z 5
#define RUSD_PHYSICS_DOF_DISTANCE 6
#define RUSD_PHYSICS_DOF_COUNT 7

/* physics:approximation on a mesh collider. */
#define RUSD_PHYSICS_MESH_NONE 0                /* the triangle mesh itself */
#define RUSD_PHYSICS_MESH_CONVEX_HULL 1
#define RUSD_PHYSICS_MESH_CONVEX_DECOMPOSITION 2
#define RUSD_PHYSICS_MESH_BOUNDING_SPHERE 3
#define RUSD_PHYSICS_MESH_BOUNDING_CUBE 4
#define RUSD_PHYSICS_MESH_SIMPLIFICATION 5
#define RUSD_PHYSICS_MESH_SDF 6                 /* PhysX's; the host falls back */
#define RUSD_PHYSICS_MESH_OTHER 7

/* physxMaterial:frictionCombineMode / restitutionCombineMode */
#define RUSD_PHYSICS_COMBINE_AVERAGE 0
#define RUSD_PHYSICS_COMBINE_MIN 1
#define RUSD_PHYSICS_COMBINE_MULTIPLY 2
#define RUSD_PHYSICS_COMBINE_MAX 3

typedef struct RUsdPhysicsList
{
    int offset;
    int count;
} RUsdPhysicsList;

typedef struct RUsdPhysicsScene
{
    int pathIndex;
    int nodeIndex;
    RUsdVec3 gravityDirection;     /* the parser derives it from the up axis when unauthored */
    float gravityMagnitude;        /* stage units / s^2; the parser derives 9.81 m/s^2 when unauthored */
    unsigned int setMask;          /* RUSD_PHYSICS_SCENE_SET_*: PhysxSceneAPI */
    float timeStepsPerSecond;      /* physxScene:timeStepsPerSecond */
    float bounceThreshold;         /* physxScene:bounceThreshold, stage units / s */
    int solverPositionIterations;  /* physxScene:minPositionIterationCount */
    int solverVelocityIterations;  /* physxScene:minVelocityIterationCount */
    int enableCCD;                 /* physxScene:enableCCD */
} RUsdPhysicsScene;

#define RUSD_PHYSICS_SCENE_SET_TIME_STEPS 1u
#define RUSD_PHYSICS_SCENE_SET_BOUNCE_THRESHOLD 2u
#define RUSD_PHYSICS_SCENE_SET_POSITION_ITERATIONS 4u
#define RUSD_PHYSICS_SCENE_SET_VELOCITY_ITERATIONS 8u
#define RUSD_PHYSICS_SCENE_SET_CCD 16u

typedef struct RUsdPhysicsBody
{
    int pathIndex;
    int nodeIndex;
    int articulationIndex;         /* -1: not part of an articulation */
    RUsdVec3 position;             /* world */
    RUsdVec4 rotation;             /* world */
    RUsdVec3 scale;                /* world */
    RUsdVec3 linearVelocity;       /* physics:velocity, as authored */
    RUsdVec3 angularVelocity;      /* physics:angularVelocity, degrees / s */
    int enabled;                   /* physics:rigidBodyEnabled */
    int kinematic;                 /* physics:kinematicEnabled */
    int startsAsleep;              /* physics:startsAsleep */
    RUsdPhysicsList colliders;     /* indices: collider indices */
    RUsdPhysicsList filtered;      /* indices: string indices of physics:filteredPairs targets */

    unsigned int setMask;          /* RUSD_PHYSICS_BODY_SET_* */
    /* UsdPhysicsMassAPI on the body prim (a collider's own is on RUsdPhysicsCollider) */
    float mass;
    float density;
    RUsdVec3 centerOfMass;         /* body local */
    RUsdVec3 diagonalInertia;
    RUsdVec4 principalAxes;
    /* PhysxRigidBodyAPI */
    float linearDamping;
    float angularDamping;
    float maxLinearVelocity;
    float maxAngularVelocity;      /* degrees / s */
    float maxDepenetrationVelocity;
    float sleepThreshold;
    int solverPositionIterations;
    int solverVelocityIterations;
    int enableCCD;
    int disableGravity;
} RUsdPhysicsBody;

#define RUSD_PHYSICS_BODY_SET_MASS 1u
#define RUSD_PHYSICS_BODY_SET_DENSITY 2u
#define RUSD_PHYSICS_BODY_SET_CENTER_OF_MASS 4u
#define RUSD_PHYSICS_BODY_SET_INERTIA 8u
#define RUSD_PHYSICS_BODY_SET_PRINCIPAL_AXES 16u
#define RUSD_PHYSICS_BODY_SET_LINEAR_DAMPING 32u
#define RUSD_PHYSICS_BODY_SET_ANGULAR_DAMPING 64u
#define RUSD_PHYSICS_BODY_SET_MAX_LINEAR_VELOCITY 128u
#define RUSD_PHYSICS_BODY_SET_MAX_ANGULAR_VELOCITY 256u
#define RUSD_PHYSICS_BODY_SET_MAX_DEPENETRATION 512u
#define RUSD_PHYSICS_BODY_SET_SLEEP_THRESHOLD 1024u
#define RUSD_PHYSICS_BODY_SET_POSITION_ITERATIONS 2048u
#define RUSD_PHYSICS_BODY_SET_VELOCITY_ITERATIONS 4096u
#define RUSD_PHYSICS_BODY_SET_CCD 8192u
#define RUSD_PHYSICS_BODY_SET_DISABLE_GRAVITY 16384u

typedef struct RUsdPhysicsCollider
{
    int pathIndex;
    int nodeIndex;
    int shape;                     /* RUSD_PHYSICS_SHAPE_* */
    int bodyIndex;                 /* -1: static */
    RUsdVec3 localPosition;        /* relative to the body, body scale applied (world when static) */
    RUsdVec4 localRotation;
    RUsdVec3 localScale;
    int enabled;                   /* physics:collisionEnabled */
    int axis;                      /* RUSD_PHYSICS_AXIS_*: capsule, cylinder, cone, plane */
    float radius0;                 /* sphere, capsule, cylinder, cone; the top radius when tapered */
    float radius1;                 /* the bottom radius when tapered, else radius0 */
    float halfHeight;
    RUsdVec3 halfExtents;          /* cube */
    int meshApproximation;         /* RUSD_PHYSICS_MESH_* */
    int meshIndex;                 /* RUsd_GetImportMeshInfo's index for the mesh prim; -1 if not imported */
    RUsdVec3 meshScale;            /* the mesh prim's world scale */
    int doubleSided;
    RUsdPhysicsList meshPoints;    /* floats: `count` points, 3 floats each, the mesh prim's local space */
    RUsdPhysicsList meshTriangles; /* indices: `count` indices, 3 per triangle, into meshPoints */
    RUsdPhysicsList spherePoints;  /* floats: `count` spheres, 4 floats each (center xyz, radius) */
    RUsdPhysicsList materials;     /* indices: material indices; more than one for GeomSubsets */
    RUsdPhysicsList groups;        /* indices: collision group indices */
    RUsdPhysicsList filtered;      /* indices: string indices */

    unsigned int setMask;          /* RUSD_PHYSICS_COLLIDER_SET_* */
    float mass;                    /* UsdPhysicsMassAPI on the collider */
    float density;
    float contactOffset;           /* physxCollision:contactOffset */
    float restOffset;              /* physxCollision:restOffset */
} RUsdPhysicsCollider;

#define RUSD_PHYSICS_COLLIDER_SET_MASS 1u
#define RUSD_PHYSICS_COLLIDER_SET_DENSITY 2u
#define RUSD_PHYSICS_COLLIDER_SET_CONTACT_OFFSET 4u
#define RUSD_PHYSICS_COLLIDER_SET_REST_OFFSET 8u

typedef struct RUsdPhysicsMaterial
{
    int pathIndex;
    int nodeIndex;
    float staticFriction;
    float dynamicFriction;
    float restitution;
    float density;                 /* 0: unauthored */
    unsigned int setMask;          /* RUSD_PHYSICS_MATERIAL_SET_* */
    int frictionCombineMode;       /* RUSD_PHYSICS_COMBINE_* */
    int restitutionCombineMode;
} RUsdPhysicsMaterial;

#define RUSD_PHYSICS_MATERIAL_SET_FRICTION_COMBINE 1u
#define RUSD_PHYSICS_MATERIAL_SET_RESTITUTION_COMBINE 2u

/* One degree of freedom of a joint. A revolute or prismatic joint fills the slot its axis names, a
   D6 joint any of them, a distance joint RUSD_PHYSICS_DOF_DISTANCE; the rest stay disabled. */
typedef struct RUsdPhysicsJointDof
{
    int limitEnabled;              /* lower > upper means locked, as UsdPhysics defines it */
    float lower;                   /* degrees for rotation, stage units for translation and distance */
    float upper;
    int driveEnabled;
    int driveIsAcceleration;       /* drive:<dof>:physics:type == "acceleration" */
    float targetPosition;
    float targetVelocity;
    float maxForce;                /* +inf when unauthored */
    float stiffness;
    float damping;
    unsigned int setMask;          /* RUSD_PHYSICS_DOF_SET_* */
    float statePosition;           /* PhysX's PhysicsJointStateAPI: the initial position */
    float stateVelocity;
    float armature;                /* PhysxJointAxisAPI, else PhysxJointAPI */
    float maxJointVelocity;
    float jointFriction;
} RUsdPhysicsJointDof;

#define RUSD_PHYSICS_DOF_SET_STATE_POSITION 1u
#define RUSD_PHYSICS_DOF_SET_STATE_VELOCITY 2u
#define RUSD_PHYSICS_DOF_SET_ARMATURE 4u
#define RUSD_PHYSICS_DOF_SET_MAX_VELOCITY 8u
#define RUSD_PHYSICS_DOF_SET_FRICTION 16u

typedef struct RUsdPhysicsJoint
{
    int pathIndex;
    int nodeIndex;
    int type;                      /* RUSD_PHYSICS_JOINT_* */
    int body0Index;                /* -1: the world */
    int body1Index;
    int articulationIndex;         /* -1: a free (maximal-coordinate) joint */
    int axis;                      /* RUSD_PHYSICS_AXIS_*: revolute, prismatic, spherical */
    RUsdVec3 localPosition0;       /* the joint frame in body0's frame (world when body0 is -1) */
    RUsdVec4 localRotation0;
    RUsdVec3 localPosition1;
    RUsdVec4 localRotation1;
    int enabled;
    int collisionEnabled;          /* between the two bodies */
    int excludeFromArticulation;
    float breakForce;              /* +inf: unbreakable */
    float breakTorque;
    int coneLimitEnabled;          /* spherical */
    float coneAngle0;              /* degrees */
    float coneAngle1;
    RUsdPhysicsJointDof dofs[RUSD_PHYSICS_DOF_COUNT];
} RUsdPhysicsJoint;

typedef struct RUsdPhysicsArticulation
{
    int pathIndex;                 /* the prim carrying PhysicsArticulationRootAPI */
    int nodeIndex;
    int rootBodyIndex;             /* the root body, or the body a root joint holds; -1 when neither */
    int fixedBase;                 /* one of its joints ties it to the world */
    RUsdPhysicsList bodies;        /* indices: body indices */
    RUsdPhysicsList joints;        /* indices: joint indices the parser places in it, less those with
                                      excludeFromArticulation; the host builds the tree and keeps a
                                      loop-closing joint out of it */
    RUsdPhysicsList filtered;      /* indices: string indices */
    unsigned int setMask;          /* RUSD_PHYSICS_ARTICULATION_SET_*: PhysxArticulationAPI */
    int enabledSelfCollisions;
    int solverPositionIterations;
    int solverVelocityIterations;
    float sleepThreshold;
    float stabilizationThreshold;
} RUsdPhysicsArticulation;

#define RUSD_PHYSICS_ARTICULATION_SET_SELF_COLLISIONS 1u
#define RUSD_PHYSICS_ARTICULATION_SET_POSITION_ITERATIONS 2u
#define RUSD_PHYSICS_ARTICULATION_SET_VELOCITY_ITERATIONS 4u
#define RUSD_PHYSICS_ARTICULATION_SET_SLEEP_THRESHOLD 8u
#define RUSD_PHYSICS_ARTICULATION_SET_STABILIZATION 16u

typedef struct RUsdPhysicsCollisionGroup
{
    int pathIndex;
    int nodeIndex;
    int invertFilteredGroups;
    RUsdPhysicsList filteredGroups; /* indices: collision group indices */
    int mergeGroupNameIndex;       /* string index, -1 none: groups sharing a name act as one */
} RUsdPhysicsCollisionGroup;

/* What the read saw and left out, so the host can say so. */
#define RUSD_PHYSICS_UNSUPPORTED_CUSTOM_SHAPE 1
#define RUSD_PHYSICS_UNSUPPORTED_CUSTOM_JOINT 2
#define RUSD_PHYSICS_UNSUPPORTED_MIMIC_JOINT 3
#define RUSD_PHYSICS_UNSUPPORTED_TENDON 4
#define RUSD_PHYSICS_UNSUPPORTED_INVALID 5       /* the parser found the object invalid */
#define RUSD_PHYSICS_UNSUPPORTED_LIMIT 6         /* detail names the limit */
#define RUSD_PHYSICS_UNSUPPORTED_MESH 7          /* a mesh collider's geometry could not be read */

typedef struct RUsdPhysicsUnsupported
{
    int pathIndex;                 /* -1 for a limit */
    int kind;                      /* RUSD_PHYSICS_UNSUPPORTED_* */
    int detailIndex;               /* string index: the schema, type or limit; -1 none */
} RUsdPhysicsUnsupported;

typedef struct RUsdPhysicsData
{
    int structSize;                /* sizeof(RUsdPhysicsData) as the plugin was built */
    int upAxis;                    /* RUSD_PHYSICS_AXIS_Y or RUSD_PHYSICS_AXIS_Z */
    double metersPerUnit;
    double kilogramsPerUnit;

    const RUsdPhysicsScene* scenes;
    const RUsdPhysicsBody* bodies;
    const RUsdPhysicsCollider* colliders;
    const RUsdPhysicsMaterial* materials;
    const RUsdPhysicsJoint* joints;
    const RUsdPhysicsArticulation* articulations;
    const RUsdPhysicsCollisionGroup* groups;
    const RUsdPhysicsUnsupported* unsupported;
    const int* indices;
    const float* floats;
    const char* strings;           /* UTF-8, each NUL-terminated */
    const int* stringOffsets;      /* byte offset of string i in `strings` */

    int sceneCount;
    int bodyCount;
    int colliderCount;
    int materialCount;
    int jointCount;
    int articulationCount;
    int groupCount;
    int unsupportedCount;
    int indexCount;
    int floatCount;
    int stringBytes;
    int stringCount;
} RUsdPhysicsData;

#define RUSD_PHYSICS_READ_PHYSX 1          /* also read PhysX attributes (physx*:*) */

typedef struct RUsdPhysicsReadOptions
{
    int structSize;                /* sizeof(RUsdPhysicsReadOptions); a smaller one is refused */
    int flags;                     /* RUSD_PHYSICS_READ_* */
    int skipWhenExceeded;
    long long maxObjects;          /* objects the parser reports, all kinds; <= 0: 1,000,000 */
    long long maxMeshElements;     /* collision mesh points and triangle indices; <= 0: 268,435,456 */
    const char* rootPaths;         /* newline-separated subtrees to read; NULL or "": the whole stage */
} RUsdPhysicsReadOptions;

/* Reads the open stage's physics into a block the plugin owns and points *outData at it. NULL
   options: the defaults, PhysX attributes included. Works after any RUsd_OpenStage*, with or without
   the import walk. */
RUSD_API int RUsd_ReadPhysics(
    RUsdContext* context,
    const RUsdPhysicsReadOptions* options,
    const RUsdPhysicsData** outData);

/* Frees what RUsd_ReadPhysics returned and keeps the stage open. */
RUSD_API int RUsd_ReleasePhysics(RUsdContext* context);

#ifdef __cplusplus
}
#endif
