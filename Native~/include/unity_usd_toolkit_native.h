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
    char albedoTexturePath[512];   /* 비어 있으면 단색 diffuseColor, 채워지면 UsdUVTexture로 연결 (USD 기준 상대경로) */
    char normalTexturePath[512];   /* normal map (raw) → inputs:normal */
    char metallicTexturePath[512]; /* metallic+smoothness 합본 → metallic(.r), roughness(1-.a) */
    float uvScale[2];              /* 텍스처 UV 타일링 (기본 1,1). 1,1이 아니면 UsdTransform2d로 적용 */
    float uvOffset[2];             /* 텍스처 UV 오프셋 (기본 0,0) */
    float emissive[3];             /* emissiveColor (기본 0,0,0). 0이 아니거나 텍스처가 있으면 emissiveColor 출력 */
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

/* Transform-node table (API v2). Every Xformable prim is a node; localMatrix is the prim's
   USD local-to-parent transform (row-vector convention, mRC = matrix[row R][col C]). */
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

#ifdef __cplusplus
}
#endif
