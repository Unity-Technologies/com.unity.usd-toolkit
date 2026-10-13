#include "unity_usd_toolkit_native.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#include <algorithm>
#include <cstdlib>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <exception>
#include <ios>
#include <memory>
#include <set>
#include <sstream>
#include <string>
#include <tuple>
#include <unordered_map>
#include <stdexcept>
#include <vector>

#include <pxr/pxr.h>
#include <pxr/base/gf/vec2d.h>
#include <pxr/base/gf/vec2f.h>
#include <pxr/base/gf/vec2h.h>
#include <pxr/base/gf/vec3d.h>
#include <pxr/base/gf/vec3f.h>
#include <pxr/base/gf/vec4f.h>
#include <pxr/base/gf/matrix4d.h>
#include <pxr/base/tf/diagnosticMgr.h>
#include <pxr/base/tf/pathUtils.h>
#include <pxr/base/tf/error.h>
#include <pxr/base/tf/status.h>
#include <pxr/base/tf/stringUtils.h>
#include <pxr/base/tf/token.h>
#include <pxr/base/tf/warning.h>
#include <pxr/base/vt/array.h>
#include <pxr/usd/ar/asset.h>
#include <pxr/usd/ar/packageUtils.h>
#include <pxr/usd/ar/resolvedPath.h>
#include <pxr/usd/ar/resolver.h>
#include <pxr/usd/sdf/assetPath.h>
#include <pxr/usd/sdf/layer.h>
#include <pxr/usd/sdf/layerUtils.h>
#include <pxr/usd/sdf/path.h>
#include <pxr/usd/sdf/payload.h>
#include <pxr/usd/sdf/primSpec.h>
#include <pxr/usd/sdf/attributeSpec.h>
#include <pxr/usd/sdf/reference.h>
#include <pxr/usd/sdf/valueTypeName.h>
#include <pxr/usd/pcp/layerStack.h>
#include <pxr/usd/usd/primCompositionQuery.h>
#include <pxr/usd/usd/primRange.h>
#include <pxr/usd/usd/stage.h>
#include <pxr/usd/usd/typed.h>
#include <pxr/usd/usd/variantSets.h>
#include <pxr/usd/sdr/registry.h>
#include <pxr/usd/sdr/shaderNode.h>
#include <pxr/usd/sdr/shaderProperty.h>
#include <pxr/usd/usdGeom/camera.h>
#include <pxr/usd/usdGeom/capsule.h>
#include <pxr/usd/usdGeom/cone.h>
#include <pxr/usd/usdGeom/cube.h>
#include <pxr/usd/usdGeom/cylinder.h>
#include <pxr/usd/usdGeom/gprim.h>
#include <pxr/usd/usdGeom/mesh.h>
#include <pxr/usd/usdGeom/plane.h>
#include <pxr/usd/usdGeom/pointInstancer.h>
#include <pxr/usd/usdGeom/sphere.h>
#include <pxr/usd/usdGeom/imageable.h>
#include <pxr/usd/usdGeom/metrics.h>
#include <pxr/usd/usdGeom/primvarsAPI.h>
#include <pxr/usd/usdGeom/subset.h>
#include <pxr/usd/usdGeom/tokens.h>
#include <pxr/usd/usdGeom/xform.h>
#include <pxr/usd/usdGeom/xformable.h>
#include <pxr/usd/usdGeom/xformCache.h>
#include <pxr/usd/usdLux/blackbody.h>
#include <pxr/usd/usdLux/cylinderLight.h>
#include <pxr/usd/usdLux/diskLight.h>
#include <pxr/usd/usdLux/distantLight.h>
#include <pxr/usd/usdLux/domeLight.h>
#include <pxr/usd/usdLux/lightAPI.h>
#include <pxr/usd/usdLux/rectLight.h>
#include <pxr/usd/usdLux/shapingAPI.h>
#include <pxr/usd/usdLux/sphereLight.h>
#include <pxr/usd/usdMedia/spatialAudio.h>
#include <pxr/usd/usdShade/connectableAPI.h>
#include <pxr/usd/usdShade/input.h>
#include <pxr/usd/usdShade/material.h>
#include <pxr/usd/usdShade/nodeGraph.h>
#include <pxr/usd/usdShade/materialBindingAPI.h>
#include <pxr/usd/usdShade/output.h>
#include <pxr/usd/usdShade/shader.h>
#include <pxr/usd/usdUtils/usdzPackage.h>

PXR_NAMESPACE_USING_DIRECTIVE

struct RUsdContext
{
    UsdStageRefPtr stage;
    SdfPath rootPath;
    // Where generated materials go. Empty means <rootPath>/Looks (the original layout); a layer
    // written over another stage sets its own so it cannot collide with that stage's materials.
    SdfPath materialScopePath;
    std::string lastError;
    std::string diagnostics;
    std::unique_ptr<TfDiagnosticMgr::Delegate> diagnosticDelegate;
    int materialIndex = 0;
    size_t importWarningCount = 0;

    struct ImportedMaterial
    {
        std::string name;
        RUsdMaterial material;
        // Authored opacity: the texture whose alpha drives transparency (empty when `opacity`
        // is not connected) and the cutout threshold (0 = alpha blend).
        std::string opacityTexturePath;
        float opacityThreshold = 0.0f;
        // Name of the mesh primvar a UsdPrimvarReader feeds into the base colour (usually
        // "displayColor"). The colour then belongs to each bound mesh, not to the material, so
        // AddImportedMaterial makes one copy per distinct primvar value. Empty when unused.
        std::string diffusePrimvar;
        // How metallicTexturePath is packed. OmniPBR writes one linear map with occlusion,
        // roughness and metallic in .r, .g and .b; Unity wants metallic in .r and smoothness in
        // .a. The file is reported as it is and the layout travels beside it, so the caller can
        // move the channels while it still has the decoded pixels.
        bool metallicIsOrm = false;
        std::string occlusionTexturePath;
        // metallicTexturePath holds a plain metallic map (metallic in .r, nothing in .a) rather
        // than Unity's packed metallic+smoothness. ALab's UsdPreviewSurface and OmniPBR's
        // metallic_texture both author it that way, with roughness in a file of its own.
        bool metallicIsPlain = false;
        // A roughness map in a file of its own (API 10). Unity reads smoothness from the metallic
        // map's alpha, so the host folds 1 - roughness into that alpha while it has the pixels.
        std::string roughnessTexturePath;
    };

    struct ImportedMesh
    {
        std::string primPath;
        // The USD prim this came from. `primPath` is where it was placed, which differs for
        // point-instancer prototypes; the source is needed to ask USD about the prim again.
        std::string sourcePath;
        std::string name;
        std::vector<RUsdVec3> points;
        std::vector<int> indices;
        std::vector<RUsdVec3> normals;
        std::vector<RUsdVec2> uvSets[RUSD_MAX_UV_SETS]; // uvSets[0] is the legacy `uv0` payload
        std::vector<RUsdSubmesh> submeshes; // per-material triangle ranges into `indices`
        int materialIndex = -1;             // default/whole-mesh material (first submesh)
        int visible = 1;
    };

    // A transform node in the imported hierarchy (every Xformable prim: Xform, Mesh, ...).
    // localMatrix is the prim's USD local-to-parent transform (row-vector convention,
    // mRC = matrix[row R][col C]); C# converts it to Unity basis and applies it per node.
    struct ImportedNode
    {
        std::string path;
        RUsdMatrix4x4 localMatrix;
        int visible = 1;
        int compositionFlags = 0; // RUSD_NODE_* bits (API v6)
    };

    std::string defaultPrimPath;
    double importMetersPerUnit = 1.0;
    int importUpAxis = 1;
    std::vector<ImportedMaterial> importedMaterials;
    std::vector<ImportedMesh> importedMeshes;
    std::vector<ImportedNode> importedNodes;

    // Point instancers drawn from their arrays instead of expanded into prims (API v8). One
    // record per prototype mesh: the mesh is imported once, and `matrices` holds that mesh's
    // placement for every instance, relative to the instancer node. Expanding Moore Lane's
    // 229,717 instances into prims costs a node and a mesh entry each; this costs one of each
    // per prototype mesh and an array the host hands straight to instanced drawing.
    struct ImportedInstancedMesh
    {
        int instancerNodeIndex = -1;
        int meshIndex = -1;
        std::vector<float> matrices;   // 16 floats per instance, row-major like RUsdMatrix4x4
    };

    std::vector<ImportedInstancedMesh> importedInstancedMeshes;

    // Scene components on nodes (API v7).
    struct ImportedLight
    {
        RUsdImportLight light;
        std::string texturePath;
    };
    struct ImportedAudio
    {
        RUsdImportAudio audio;
        std::string filePath;
    };
    std::vector<ImportedLight> importedLights;
    std::vector<RUsdImportCamera> importedCameras;
    std::vector<ImportedAudio> importedAudio;

    // Material cache key -> index into importedMaterials. The key is the material prim in its
    // prototype, so every instance of one part shares a single entry; displayColor stand-ins add
    // their colour to the key. Unrecognised materials are cached too, so they warn only once.
    struct MaterialCacheEntry
    {
        int index = -1;
        bool recognized = false;
    };
    std::unordered_map<std::string, MaterialCacheEntry> importedMaterialCache;

    // Export: identical materials (by value) are written once and shared between meshes.
    std::unordered_map<std::string, UsdShadeMaterial> exportedMaterials;

    // Authored asset path -> the path OpenUSD resolved it to, recorded while reading materials.
    // The authored path is all the managed side gets, and for a stage inside a .usdz package it
    // resolves to a file no file-system read can reach, so RUsd_ReadImportAsset resolves through
    // this table (per-layer correct) before falling back to anchoring on the root layer.
    std::unordered_map<std::string, std::string> assetResolutions;

    // RUsd_GetImportComposition's report, built on first request.
    std::string compositionReport;
    bool compositionReportBuilt = false;
};

namespace
{
constexpr int kSuccess = 0;
constexpr int kFailure = 1;
constexpr int kApiVersion = 12;
thread_local std::string g_lastError;
thread_local std::string g_lastDiagnostics;

class RUsdDiagnosticDelegate final : public TfDiagnosticMgr::Delegate
{
public:
    explicit RUsdDiagnosticDelegate(std::string* diagnostics)
        : _diagnostics(diagnostics)
    {
    }

    void IssueStatus(TfStatus const& status) override
    {
        Append("status", status);
    }

    void IssueWarning(TfWarning const& warning) override
    {
        Append("warning", warning);
    }

    void IssueError(TfError const& error) override
    {
        Append("error", error);
    }

    void IssueFatalError(TfCallContext const& context, std::string const& message) override
    {
        if (_diagnostics == nullptr)
        {
            return;
        }

        std::ostringstream stream;
        stream << "[fatal] " << message;
        if (context.GetFile() != nullptr)
        {
            stream << " (" << context.GetFile() << ":" << context.GetLine() << ")";
        }

        AppendLine(stream.str());
    }

private:
    template <typename TDiagnostic>
    void Append(const char* level, TDiagnostic const& diagnostic)
    {
        if (_diagnostics == nullptr)
        {
            return;
        }

        std::ostringstream stream;
        stream << "[" << level << "] ";
        if (!diagnostic.GetDiagnosticCodeAsString().empty())
        {
            stream << diagnostic.GetDiagnosticCodeAsString() << ": ";
        }

        stream << diagnostic.GetCommentary();

        std::string file = diagnostic.GetSourceFileName();
        if (!file.empty())
        {
            stream << " (" << file << ":" << diagnostic.GetSourceLineNumber() << ")";
        }

        AppendLine(stream.str());
    }

    void AppendLine(const std::string& line)
    {
        if (!_diagnostics->empty())
        {
            *_diagnostics += "\n";
        }

        *_diagnostics += line;
    }

    std::string* _diagnostics;
};

std::string SafeString(const char* value)
{
    return value != nullptr ? std::string(value) : std::string();
}

// Path comparison for the asset confinement check below. OpenUSD normalizes to forward
// slashes; Windows and macOS are case-insensitive by default, Linux is not.
std::string NormalizeForCompare(const std::string& path)
{
    std::string normalized = TfNormPath(path);
#if defined(_WIN32) || defined(__APPLE__)
    std::transform(normalized.begin(), normalized.end(), normalized.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
#endif
    return normalized;
}

// True when `resolvedPath` sits in a folder that belongs to this stage. An imported stage is
// untrusted content: its authored asset paths may be absolute or climb out with `..`, and both
// resolve perfectly well, so the decision has to be made on the final resolved location rather
// than on the authored spelling — scanning for ".." would miss an absolute path entirely.
//
// The allowed set is the directory of every layer that composes the stage, not just the root
// layer's: a sublayer or reference legitimately keeps its own textures next to itself, and
// confining to the root layer alone would refuse those.
bool IsResolvedAssetInsideStageRoot(RUsdContext* context, const std::string& resolvedPath)
{
    if (context == nullptr || !context->stage || resolvedPath.empty())
    {
        return false;
    }

    // Judge a packaged asset by the package file that contains it:
    // "/a/b/scene.usdz[tex.png]" -> "/a/b/scene.usdz". A path inside a package cannot escape it.
    std::string outer = resolvedPath;
    for (int depth = 0; depth < 8 && ArIsPackageRelativePath(outer); ++depth)
    {
        outer = ArSplitPackageRelativePathOuter(outer).first;
    }

    const std::string real = TfRealPath(outer);
    const std::string candidate = NormalizeForCompare(real.empty() ? outer : real);
    if (candidate.empty())
    {
        return false;
    }

    for (const SdfLayerHandle& layer : context->stage->GetUsedLayers())
    {
        if (!layer)
        {
            continue;
        }

        // A layer inside a package (the `model.usda` of a .usdz) has no real path, and its
        // identifier carries the package syntax: "/a/b/scene.usdz[model.usda]". Strip that to
        // the package file so the folder holding the .usdz becomes the anchor — otherwise a
        // packaged stage has no anchor at all and every one of its own textures is refused.
        std::string layerPath = layer->GetRealPath();
        if (layerPath.empty())
        {
            layerPath = layer->GetIdentifier();
        }

        for (int depth = 0; depth < 8 && ArIsPackageRelativePath(layerPath); ++depth)
        {
            layerPath = ArSplitPackageRelativePathOuter(layerPath).first;
        }

        if (layerPath.empty())
        {
            // An anonymous or in-memory layer has no folder to anchor against.
            continue;
        }

        // Both sides go through TfRealPath, or neither: on macOS the resolved asset path comes
        // back under /private/var while a layer path stays /var (the same directory through a
        // symlink), and a prefix comparison between the two forms never matches.
        const std::string layerDirectory = TfGetPathName(layerPath);
        const std::string layerReal = TfRealPath(layerDirectory);
        std::string root = NormalizeForCompare(layerReal.empty() ? layerDirectory : layerReal);
        if (root.empty() || TfIsRelativePath(root))
        {
            // A relative anchor (a bare "./" from a layer with no directory part) would match
            // almost anything, so it is no anchor at all.
            continue;
        }

        if (root.back() != '/')
        {
            root += '/';
        }

        if (candidate.size() >= root.size() && candidate.compare(0, root.size(), root) == 0)
        {
            return true;
        }
    }

    return false;
}

void SetError(RUsdContext* context, const std::string& error)
{
    g_lastError = error;
    if (context != nullptr)
    {
        context->lastError = error;
        g_lastDiagnostics = context->diagnostics;
    }
}

// Importer-generated warnings. They share the buffer RUsd_GetDiagnostics returns (which is
// otherwise empty unless OpenUSD diagnostics were captured), so the managed side can surface
// them without a new entry point. Capped so a pathological stage cannot grow it without bound.
constexpr size_t kMaxImportWarnings = 64;

// Import budgets. A stage is untrusted input and a few hundred bytes of it can describe an
// unbounded amount of work: point instancers whose prototypes contain further instancers re-import
// their subtrees multiplicatively (two prototypes per level, 32 levels, is 2^32 walks inside the
// depth cap), and one instance count times one prototype mesh count is a matrix per pair. The walk
// counts what it produces and fails the import, with an error naming the limit, once any of these
// is passed. Each is several times what the largest scenes measured so far need (Moore Lane:
// 229,717 instances; Factory: 13,843 meshes).
constexpr size_t kMaxImportNodes = 2000000;
constexpr size_t kMaxImportMeshes = 1000000;
constexpr size_t kMaxImportInstancePlacements = 4000000;     // instances x prototype meshes
constexpr size_t kMaxImportGeometryElements = 256u * 1024u * 1024u; // points + indices, all meshes

// Destination-capacity check for the copy-out entry points. Compared in size_t: narrowing the
// source size to int first let a mesh over INT_MAX indices report a tiny count, pass the check
// against an equally tiny managed buffer, and then be copied into it in full.
static bool FitsCapacity(int capacity, size_t needed)
{
    return capacity >= 0 && static_cast<size_t>(capacity) >= needed;
}

void AppendImportWarning(RUsdContext* context, const std::string& message)
{
    if (context == nullptr)
    {
        return;
    }

    if (context->importWarningCount >= kMaxImportWarnings)
    {
        return;
    }

    ++context->importWarningCount;
    context->diagnostics += "USD import: " + message + "\n";
    if (context->importWarningCount == kMaxImportWarnings)
    {
        context->diagnostics += "USD import: further import warnings suppressed.\n";
    }
}

std::string MakeError(const std::string& prefix, const std::exception& exception)
{
    std::ostringstream stream;
    stream << prefix << ": " << exception.what();
    return stream.str();
}

void EnableDiagnostics(RUsdContext* context)
{
    if (context == nullptr || context->diagnosticDelegate)
    {
        return;
    }

    context->diagnosticDelegate.reset(new RUsdDiagnosticDelegate(&context->diagnostics));
    TfDiagnosticMgr::GetInstance().AddDelegate(context->diagnosticDelegate.get());
}

void DisableDiagnostics(RUsdContext* context)
{
    if (context == nullptr || !context->diagnosticDelegate)
    {
        return;
    }

    TfDiagnosticMgr::GetInstance().RemoveDelegate(context->diagnosticDelegate.get());
    context->diagnosticDelegate.reset();
    g_lastDiagnostics = context->diagnostics;
}

int CopyStringToBuffer(const std::string& value, char* buffer, int bufferCapacity)
{
    if (buffer == nullptr || bufferCapacity <= 0)
    {
        return kFailure;
    }

    int length = std::min(static_cast<int>(value.size()), bufferCapacity - 1);
    std::memcpy(buffer, value.data(), static_cast<size_t>(length));
    buffer[length] = '\0';
    return kSuccess;
}

bool IsValidMeshInput(
    const RUsdVec3* points,
    int pointCount,
    const int* indices,
    int indexCount,
    const RUsdSubmesh* submeshes,
    int submeshCount,
    std::string& error)
{
    if (points == nullptr || pointCount <= 0)
    {
        error = "Mesh has no points.";
        return false;
    }

    if (indices == nullptr || indexCount <= 0 || indexCount % 3 != 0)
    {
        error = "Mesh indices must be a non-empty triangle index buffer.";
        return false;
    }

    for (int i = 0; i < indexCount; ++i)
    {
        if (indices[i] < 0 || indices[i] >= pointCount)
        {
            error = "Mesh index is out of point range.";
            return false;
        }
    }

    if (submeshCount > 0 && submeshes == nullptr)
    {
        error = "Submesh descriptors are missing.";
        return false;
    }

    for (int i = 0; i < submeshCount; ++i)
    {
        const RUsdSubmesh& submesh = submeshes[i];
        if (submesh.indexStart < 0 ||
            submesh.indexCount < 0 ||
            submesh.indexCount % 3 != 0 ||
            submesh.indexStart + submesh.indexCount > indexCount)
        {
            error = "Submesh descriptor is outside the mesh index buffer.";
            return false;
        }

        if (submesh.indexStart % 3 != 0)
        {
            error = "Submesh indexStart must be triangle-aligned.";
            return false;
        }
    }

    return true;
}

void EnsureParentXforms(const UsdStageRefPtr& stage, const SdfPath& path)
{
    std::vector<SdfPath> prefixes = path.GetPrefixes();
    for (const SdfPath& prefix : prefixes)
    {
        if (prefix == path || prefix.IsAbsoluteRootPath())
        {
            continue;
        }

        if (!stage->GetPrimAtPath(prefix))
        {
            UsdGeomXform::Define(stage, prefix);
        }
    }
}

VtArray<GfVec3f> CopyPoints(const RUsdVec3* points, int pointCount)
{
    VtArray<GfVec3f> result;
    result.resize(static_cast<size_t>(pointCount));
    for (int i = 0; i < pointCount; ++i)
    {
        result[static_cast<size_t>(i)] = GfVec3f(points[i].x, points[i].y, points[i].z);
    }

    return result;
}

VtArray<GfVec3f> ComputeExtent(const RUsdVec3* points, int pointCount)
{
    VtArray<GfVec3f> extent;
    extent.resize(2);
    if (points == nullptr || pointCount <= 0)
    {
        extent[0] = GfVec3f(0.0f);
        extent[1] = GfVec3f(0.0f);
        return extent;
    }

    GfVec3f minimum(points[0].x, points[0].y, points[0].z);
    GfVec3f maximum = minimum;
    for (int i = 1; i < pointCount; ++i)
    {
        minimum[0] = std::min(minimum[0], points[i].x);
        minimum[1] = std::min(minimum[1], points[i].y);
        minimum[2] = std::min(minimum[2], points[i].z);
        maximum[0] = std::max(maximum[0], points[i].x);
        maximum[1] = std::max(maximum[1], points[i].y);
        maximum[2] = std::max(maximum[2], points[i].z);
    }

    extent[0] = minimum;
    extent[1] = maximum;
    return extent;
}

GfMatrix4d ToGfMatrix(const RUsdMatrix4x4& matrix)
{
    return GfMatrix4d(
        matrix.m00, matrix.m01, matrix.m02, matrix.m03,
        matrix.m10, matrix.m11, matrix.m12, matrix.m13,
        matrix.m20, matrix.m21, matrix.m22, matrix.m23,
        matrix.m30, matrix.m31, matrix.m32, matrix.m33);
}

RUsdMatrix4x4 FromGfMatrix(const GfMatrix4d& matrix)
{
    RUsdMatrix4x4 result;
    result.m00 = matrix[0][0]; result.m01 = matrix[0][1]; result.m02 = matrix[0][2]; result.m03 = matrix[0][3];
    result.m10 = matrix[1][0]; result.m11 = matrix[1][1]; result.m12 = matrix[1][2]; result.m13 = matrix[1][3];
    result.m20 = matrix[2][0]; result.m21 = matrix[2][1]; result.m22 = matrix[2][2]; result.m23 = matrix[2][3];
    result.m30 = matrix[3][0]; result.m31 = matrix[3][1]; result.m32 = matrix[3][2]; result.m33 = matrix[3][3];
    return result;
}

VtArray<int> CopyIndices(const int* indices, int indexCount)
{
    VtArray<int> result;
    result.resize(static_cast<size_t>(indexCount));
    for (int i = 0; i < indexCount; ++i)
    {
        result[static_cast<size_t>(i)] = indices[i];
    }

    return result;
}

VtArray<int> CreateTriangleFaceCounts(int indexCount)
{
    VtArray<int> result;
    result.resize(static_cast<size_t>(indexCount / 3));
    std::fill(result.begin(), result.end(), 3);
    return result;
}

VtArray<GfVec2f> CopyUv0(const RUsdVec2* uv0, int uv0Count)
{
    VtArray<GfVec2f> result;
    result.resize(static_cast<size_t>(uv0Count));
    for (int i = 0; i < uv0Count; ++i)
    {
        result[static_cast<size_t>(i)] = GfVec2f(uv0[i].x, uv0[i].y);
    }

    return result;
}

RUsdVec3 ToUnityVector(const GfVec3d& value)
{
    return {
        static_cast<float>(-value[0]),
        static_cast<float>(value[1]),
        static_cast<float>(value[2])
    };
}

RUsdVec3 ToUnityVector(const GfVec3f& value)
{
    return {
        -value[0],
        value[1],
        value[2]
    };
}

// Robust "does this VtValue hold type <typeName>" check across the plugin/OpenUSD DSO
// boundary. VtValue::IsHolding<T>() compares typeid, which breaks when this plugin is built
// with hidden visibility (its typeid for OpenUSD value types differs from libusd_ms's), so
// compare the registered type name instead. Pair with UncheckedGet<T>() (no typeid check).
bool VtValueHoldsType(const VtValue& value, const char* typeName)
{
    return value.GetTypeName() == typeName;
}

bool GetUsdShadeInputFloat(const UsdShadeShader& shader, const TfToken& name, float* value)
{
    if (value == nullptr)
    {
        return false;
    }

    UsdShadeInput input = shader.GetInput(name);
    if (!input)
    {
        return false;
    }

    VtValue vtValue;
    if (!input.Get(&vtValue))
    {
        return false;
    }

    if (vtValue.IsHolding<float>())
    {
        *value = vtValue.UncheckedGet<float>();
        return true;
    }

    if (vtValue.IsHolding<double>())
    {
        *value = static_cast<float>(vtValue.UncheckedGet<double>());
        return true;
    }

    return false;
}

bool GetUsdShadeInputColor(const UsdShadeShader& shader, const TfToken& name, RUsdMaterial* material)
{
    if (material == nullptr)
    {
        return false;
    }

    UsdShadeInput input = shader.GetInput(name);
    if (!input)
    {
        return false;
    }

    VtValue vtValue;
    if (!input.Get(&vtValue))
    {
        return false;
    }

    if (VtValueHoldsType(vtValue, "GfVec3f"))
    {
        const GfVec3f& color = vtValue.UncheckedGet<GfVec3f>();
        material->r = color[0];
        material->g = color[1];
        material->b = color[2];
        return true;
    }

    if (VtValueHoldsType(vtValue, "GfVec4f"))
    {
        const GfVec4f& color = vtValue.UncheckedGet<GfVec4f>();
        material->r = color[0];
        material->g = color[1];
        material->b = color[2];
        material->a = color[3];
        return true;
    }

    return false;
}

void CopyStringToBuffer(const std::string& source, char* buffer, size_t capacity)
{
    if (buffer == nullptr || capacity == 0)
    {
        return;
    }

    size_t count = std::min(source.size(), capacity - 1);
    std::memcpy(buffer, source.data(), count);
    buffer[count] = '\0';
}

// Resolves the shader prim that drives a UsdPreviewSurface input through its connection.
// Returns an invalid shader when the input is unconnected.
// The material a renderer binds to a prim. ComputeBoundMaterial() with no purpose considers
// all-purpose bindings only, and a stage authored with purpose-specific bindings has none of
// those: ALab binds everything as material:binding:full and :preview, and under the default
// purpose 2 of its 7,485 meshes came back with a material, the rest with nothing -- no warning,
// no texture, the whole stage flat. Hydra asks for `full` (which falls back to all-purpose on
// its own) and that is what is asked for here, then `preview` for a prim that only has that.
UsdShadeMaterial ComputeRenderedMaterial(const UsdPrim& prim)
{
    UsdShadeMaterialBindingAPI binding(prim);
    UsdShadeMaterial material = binding.ComputeBoundMaterial(UsdShadeTokens->full);
    if (!material)
    {
        material = binding.ComputeBoundMaterial(UsdShadeTokens->preview);
    }

    return material;
}

UsdShadeShader GetConnectedShader(const UsdShadeShader& shader, const TfToken& inputName)
{
    UsdShadeInput input = shader.GetInput(inputName);
    if (!input)
    {
        return UsdShadeShader();
    }

    UsdShadeSourceInfoVector sources = input.GetConnectedSources();
    if (sources.empty() || !sources[0].source)
    {
        return UsdShadeShader();
    }

    return UsdShadeShader(sources[0].source.GetPrim());
}

// Follows a UsdPreviewSurface input to a connected UsdUVTexture and writes its `file`
// asset path (authored, USD-relative when authored that way) into the buffer.
// Why a connected texture input produced no file path. Reported per input so a silent
// "everything imported untextured" is diagnosable instead of guesswork.
enum class TextureReadStatus
{
    Ok,
    NotConnected,
    NotUvTexture,
    NoFileInput,
    UnreadableFileValue,
    EmptyFilePath
};

// A relative asset path is relative to the layer that authored it, but the host resolves the path
// it is given against the stage's root folder. When a material lives in a sub-folder layer -- the
// Vehicle Variants sample keeps them in materials/ and points at @../textures/...@ -- the two
// disagree: rebased on the root, "../textures" climbs out of the stage folder, the host refuses it
// as an escape, and every texture is lost. So when OpenUSD resolved the asset to a file inside the
// root folder and the authored text would not land there, the path is rewritten relative to the
// root folder. A file outside it keeps its authored path and is still refused; a package path
// (inside a .usdz) is left as it is, since only the resolver can reach it.
std::string RebaseOnStageRoot(RUsdContext* context, const std::string& authored, const std::string& resolved)
{
    if (context == nullptr || !context->stage || ArIsPackageRelativePath(resolved) ||
        !TfIsRelativePath(authored))
    {
        return authored;
    }

    const std::string rootLayer = context->stage->GetRootLayer()->GetRealPath();
    if (rootLayer.empty() || ArIsPackageRelativePath(rootLayer))
    {
        return authored;
    }

    std::string rootDir = TfNormPath(TfGetPathName(rootLayer));
    const std::string file = TfNormPath(resolved);
    if (TfNormPath(rootDir + "/" + authored) == file)
    {
        return authored;
    }

    if (!rootDir.empty() && rootDir.back() != '/')
    {
        rootDir += '/';
    }

#if defined(_WIN32)
    auto lower = [](std::string text) {
        std::transform(text.begin(), text.end(), text.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return text;
    };
    const bool inside = file.size() > rootDir.size() && lower(file.substr(0, rootDir.size())) == lower(rootDir);
#else
    const bool inside = file.size() > rootDir.size() && file.compare(0, rootDir.size(), rootDir) == 0;
#endif
    // Outside the root layer's folder, the authored path must not be handed back: it was anchored
    // against the layer that wrote it, and the host re-anchors what it receives against the root
    // stage instead. USD_Mini_Car_Kit shows the gap -- materials/red.usda authors
    // "../textures/global-colors/red.jpg", which USD resolves to <kit>/textures/..., while the host
    // joined it onto assets/vehicles/ and looked for <kit>/assets/textures/..., a file that does not
    // exist. Returning the resolved path keeps USD's own anchoring instead of discarding it.
    return inside ? file.substr(rootDir.size()) : file;
}

// Writes an authored texture asset path into `buffer` and remembers what OpenUSD resolved it
// to. USD anchored it against the layer that authored it, which is more than
// RUsd_ReadImportAsset could work out on its own. Returns false for an empty path.
bool ContainsUdimToken(const std::string& path)
{
    static const char* tokens[] = {"<UDIM>", "<udim>", "<Udim>"};
    for (const char* token : tokens)
    {
        if (path.find(token) != std::string::npos)
        {
            return true;
        }
    }

    return false;
}

// Where a relative asset path authored on `source` would land: anchored on the layer that
// authored it, the way the resolver anchors a path it can resolve. Empty when it cannot be told.
std::string AnchorOnAuthoringLayer(const UsdAttribute& source, const std::string& authored)
{
    if (!source || !TfIsRelativePath(authored))
    {
        return std::string();
    }

    for (const SdfPropertySpecHandle& spec : source.GetPropertyStack())
    {
        if (!spec)
        {
            continue;
        }

        const std::string layerPath = spec->GetLayer()->GetRealPath();
        if (layerPath.empty() || ArIsPackageRelativePath(layerPath))
        {
            continue;
        }

        return TfNormPath(TfGetPathName(layerPath) + authored);
    }

    return std::string();
}

bool StoreAssetPath(RUsdContext* context, const SdfAssetPath& asset, char* buffer, size_t capacity,
                    const UsdAttribute& source = UsdAttribute())
{
    std::string path = asset.GetAssetPath();
    if (path.empty())
    {
        path = asset.GetResolvedPath();
    }

    if (path.empty())
    {
        return false;
    }

    if (context != nullptr)
    {
        std::string resolved = asset.GetResolvedPath();

        // A UDIM reference names no file, so the resolver leaves it unresolved and the rebasing
        // below never runs: the authored "../../texture/x.<UDIM>.exr" reached the host as it
        // was, the host joined it onto the stage folder, and every one of ALab's 2,047 UDIM
        // references climbed out of the stage into a folder that does not exist. The anchor the
        // resolver would have used is the authoring layer's folder, so that is applied here.
        if (resolved.empty() && ContainsUdimToken(path))
        {
            resolved = AnchorOnAuthoringLayer(source, path);
        }

        if (!resolved.empty())
        {
            path = RebaseOnStageRoot(context, path, resolved);
            context->assetResolutions[path] = resolved;
        }
    }

    CopyStringToBuffer(path, buffer, capacity);
    return true;
}

TextureReadStatus ReadConnectedTexture(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const TfToken& inputName,
    char* buffer,
    size_t capacity,
    std::string* sourcePrimPath)
{
    UsdShadeShader textureShader = GetConnectedShader(shader, inputName);
    if (!textureShader)
    {
        return TextureReadStatus::NotConnected;
    }

    if (sourcePrimPath != nullptr)
    {
        *sourcePrimPath = textureShader.GetPath().GetString();
    }

    TfToken textureId;
    if (!textureShader.GetShaderId(&textureId) || textureId != TfToken("UsdUVTexture"))
    {
        return TextureReadStatus::NotUvTexture;
    }

    UsdShadeInput fileInput = textureShader.GetInput(TfToken("file"));
    if (!fileInput)
    {
        return TextureReadStatus::NoFileInput;
    }

    VtValue vtValue;
    if (!fileInput.Get(&vtValue) || !VtValueHoldsType(vtValue, "SdfAssetPath"))
    {
        return TextureReadStatus::UnreadableFileValue;
    }

    return StoreAssetPath(context, vtValue.UncheckedGet<SdfAssetPath>(), buffer, capacity, fileInput.GetAttr())
        ? TextureReadStatus::Ok
        : TextureReadStatus::EmptyFilePath;
}

// Reads the texture path for one input and, when a connection exists but yields nothing, says
// why. `UnreadableFileValue` in particular is the signature of a plugin binary that was not
// built from this source: VtValue type checks then fail across the plugin/OpenUSD boundary and
// every texture path comes back empty with no error from OpenUSD itself.
bool ReadConnectedTextureFileReported(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const TfToken& inputName,
    const std::string& materialPath,
    char* buffer,
    size_t capacity)
{
    std::string sourcePrimPath;
    TextureReadStatus status =
        ReadConnectedTexture(context, shader, inputName, buffer, capacity, &sourcePrimPath);
    if (status == TextureReadStatus::Ok || status == TextureReadStatus::NotConnected)
    {
        return status == TextureReadStatus::Ok;
    }

    std::ostringstream message;
    message << "material '" << materialPath << "' input '" << inputName.GetString()
            << "' is connected to <" << sourcePrimPath << "> but no texture file path could be read: ";

    switch (status)
    {
        case TextureReadStatus::NotUvTexture:
            message << "the connected source is not a UsdUVTexture, so it cannot be imported.";
            break;
        case TextureReadStatus::NoFileInput:
            message << "that UsdUVTexture has no 'inputs:file'.";
            break;
        case TextureReadStatus::UnreadableFileValue:
            message << "its 'inputs:file' value could not be read as an asset path. If every "
                       "material in the stage reports this, the loaded native plugin was most "
                       "likely not built from this package's source - see BUILD_NOTES.md.";
            break;
        case TextureReadStatus::EmptyFilePath:
            message << "its 'inputs:file' asset path is empty.";
            break;
        default:
            message << "unknown reason.";
            break;
    }

    AppendImportWarning(context, message.str());
    return false;
}

bool ReadConnectedTextureFile(
    const UsdShadeShader& shader,
    const TfToken& inputName,
    char* buffer,
    size_t capacity)
{
    return ReadConnectedTexture(nullptr, shader, inputName, buffer, capacity, nullptr) ==
        TextureReadStatus::Ok;
}

bool GetUsdShadeInputVec3(const UsdShadeShader& shader, const TfToken& name, float* out3)
{
    if (out3 == nullptr)
    {
        return false;
    }

    UsdShadeInput input = shader.GetInput(name);
    if (!input)
    {
        return false;
    }

    VtValue vtValue;
    if (!input.Get(&vtValue) || !VtValueHoldsType(vtValue, "GfVec3f"))
    {
        return false;
    }

    const GfVec3f& value = vtValue.UncheckedGet<GfVec3f>();
    out3[0] = value[0];
    out3[1] = value[1];
    out3[2] = value[2];
    return true;
}

// Reads UV tiling/offset from a texture shader's `st` input when it routes through a
// UsdTransform2d node (matching the export layout).
void ReadUvTransformFromTexture(const UsdShadeShader& textureShader, RUsdMaterial* material)
{
    if (!textureShader || material == nullptr)
    {
        return;
    }

    UsdShadeShader stSource = GetConnectedShader(textureShader, TfToken("st"));
    if (!stSource)
    {
        return;
    }

    TfToken stId;
    if (!stSource.GetShaderId(&stId) || stId != TfToken("UsdTransform2d"))
    {
        return;
    }

    VtValue vtValue;
    UsdShadeInput scale = stSource.GetInput(TfToken("scale"));
    if (scale && scale.Get(&vtValue) && VtValueHoldsType(vtValue, "GfVec2f"))
    {
        const GfVec2f& value = vtValue.UncheckedGet<GfVec2f>();
        material->uvScale[0] = value[0];
        material->uvScale[1] = value[1];
    }

    UsdShadeInput translation = stSource.GetInput(TfToken("translation"));
    if (translation && translation.Get(&vtValue) && VtValueHoldsType(vtValue, "GfVec2f"))
    {
        const GfVec2f& value = vtValue.UncheckedGet<GfVec2f>();
        material->uvOffset[0] = value[0];
        material->uvOffset[1] = value[1];
    }
}

// Reads UsdPreviewSurface's `opacity` connection and `opacityThreshold`. A connected opacity
// means the authored transparency lives in a texture's alpha channel, which the constant in
// RUsdMaterial cannot express (it reports the connection's fallback, i.e. usually 1 = opaque).
void ExtractOpacityInfo(
    RUsdContext* context,
    const UsdShadeMaterial& material,
    std::string* texturePath,
    float* threshold)
{
    texturePath->clear();
    *threshold = 0.0f;

    if (!material)
    {
        return;
    }

    UsdShadeShader shader = material.ComputeSurfaceSource();
    TfToken shaderId;
    if (!shader || !shader.GetShaderId(&shaderId) || shaderId != TfToken("UsdPreviewSurface"))
    {
        return;
    }

    char buffer[1024] = {0};
    if (ReadConnectedTextureFileReported(
            context, shader, TfToken("opacity"), material.GetPath().GetString(),
            buffer, sizeof(buffer)))
    {
        *texturePath = buffer;
    }

    GetUsdShadeInputFloat(shader, TfToken("opacityThreshold"), threshold);
}

// A UsdPrimvarReader_float3/float4 feeding `inputName`: its varname, and its fallback as the
// colour to use where the bound mesh lacks that primvar. The Teapot sample does exactly this --
// diffuseColor <- UsdPrimvarReader_float3 reading displayColor -- and every renderer shows the
// mesh's displayColor there.
bool ReadPrimvarReaderColor(
    const UsdShadeShader& shader,
    const TfToken& inputName,
    std::string* varname,
    RUsdMaterial* fallback)
{
    UsdShadeShader reader = GetConnectedShader(shader, inputName);
    TfToken readerId;
    if (!reader || !reader.GetShaderId(&readerId) ||
        (readerId != TfToken("UsdPrimvarReader_float3") && readerId != TfToken("UsdPrimvarReader_float4")))
    {
        return false;
    }

    UsdShadeInput nameInput = reader.GetInput(TfToken("varname"));
    VtValue value;
    if (!nameInput || !nameInput.Get(&value))
    {
        return false;
    }

    // varname is a token in the current schema and was a string before USD 21.11; both occur.
    if (VtValueHoldsType(value, "TfToken"))
    {
        *varname = value.UncheckedGet<TfToken>().GetString();
    }
    else if (VtValueHoldsType(value, "string") || VtValueHoldsType(value, "std::string"))
    {
        *varname = value.UncheckedGet<std::string>();
    }

    if (varname->empty())
    {
        return false;
    }

    GetUsdShadeInputColor(reader, TfToken("fallback"), fallback);
    return true;
}

RUsdMaterial ExtractPreviewMaterial(
    RUsdContext* context,
    const UsdShadeMaterial& material,
    RUsdContext::ImportedMaterial* extras)
{
    std::string* diffusePrimvar = extras != nullptr ? &extras->diffusePrimvar : nullptr;
    RUsdMaterial result = {1.0f, 1.0f, 1.0f, 1.0f, 0.0f, 0.5f};
    result.uvScale[0] = 1.0f;
    result.uvScale[1] = 1.0f;
    if (!material)
    {
        return result;
    }

    const std::string materialPath = material.GetPath().GetString();

    UsdShadeShader shader = material.ComputeSurfaceSource();
    if (!shader)
    {
        AppendImportWarning(
            context,
            "material '" + materialPath + "' has no surface shader source; the default material is used.");
        return result;
    }

    TfToken shaderId;
    if (!shader.GetShaderId(&shaderId) || shaderId != TfToken("UsdPreviewSurface"))
    {
        AppendImportWarning(
            context,
            "material '" + materialPath + "' surface shader is '" +
                (shaderId.IsEmpty() ? std::string("(no info:id)") : shaderId.GetString()) +
                "', not UsdPreviewSurface; only UsdPreviewSurface is imported.");
        return result;
    }

    // Scalar fallbacks (used when the matching input has no texture connection).
    GetUsdShadeInputColor(shader, TfToken("diffuseColor"), &result);
    GetUsdShadeInputFloat(shader, TfToken("opacity"), &result.a);
    GetUsdShadeInputFloat(shader, TfToken("metallic"), &result.metallic);
    GetUsdShadeInputFloat(shader, TfToken("roughness"), &result.roughness);

    // Connected UsdUVTexture file paths (mirror of the export layout). The metallic and
    // roughness inputs are driven by the same packed metallic+smoothness texture on export,
    // so reading either yields the combined map.
    // A primvar reader is not a texture, so it is handled before the texture read, which would
    // otherwise warn that the source "is not a UsdUVTexture".
    std::string primvarName;
    bool hasAlbedo = false;
    if (ReadPrimvarReaderColor(shader, TfToken("diffuseColor"), &primvarName, &result))
    {
        if (diffusePrimvar != nullptr)
        {
            *diffusePrimvar = primvarName;
        }
    }
    else
    {
        hasAlbedo = ReadConnectedTextureFileReported(
            context, shader, TfToken("diffuseColor"), materialPath,
            result.albedoTexturePath, sizeof(result.albedoTexturePath));
    }
    ReadConnectedTextureFileReported(
        context, shader, TfToken("normal"), materialPath,
        result.normalTexturePath, sizeof(result.normalTexturePath));
    bool hasMetallic = ReadConnectedTextureFileReported(
        context, shader, TfToken("metallic"), materialPath,
        result.metallicTexturePath, sizeof(result.metallicTexturePath));

    // Roughness in the same file as metallic is the packed map this exporter writes (metallic
    // in .r, smoothness in .a). Roughness in a file of its own is what every DCC writes -- ALab
    // has 1,853 materials with metallic, roughness and occlusion as three separate maps -- and
    // it used to be read into the metallic slot as if it were packed, so its red channel became
    // metallic and its (absent) alpha became smoothness 1: every surface a mirror.
    char roughnessPath[512] = {0};
    const bool hasRoughness = ReadConnectedTextureFileReported(
        context, shader, TfToken("roughness"), materialPath, roughnessPath, sizeof(roughnessPath));
    const bool roughnessIsSeparate =
        hasRoughness && (!hasMetallic || std::strcmp(roughnessPath, result.metallicTexturePath) != 0);
    if (extras != nullptr)
    {
        if (roughnessIsSeparate)
        {
            extras->roughnessTexturePath = roughnessPath;
        }

        extras->metallicIsPlain = hasMetallic && (roughnessIsSeparate || !hasRoughness);

        char occlusionPath[512] = {0};
        if (ReadConnectedTextureFileReported(
                context, shader, TfToken("occlusion"), materialPath, occlusionPath, sizeof(occlusionPath)))
        {
            extras->occlusionTexturePath = occlusionPath;
        }
    }

    // emissiveColor: texture takes priority, otherwise read the constant color.
    bool hasEmissiveTex = ReadConnectedTextureFileReported(
        context, shader, TfToken("emissiveColor"), materialPath,
        result.emissiveTexturePath, sizeof(result.emissiveTexturePath));
    if (!hasEmissiveTex)
    {
        GetUsdShadeInputVec3(shader, TfToken("emissiveColor"), result.emissive);
    }

    // UV tiling/offset is shared across textures on export; read it from any present texture.
    if (hasAlbedo)
    {
        ReadUvTransformFromTexture(
            GetConnectedShader(shader, TfToken("diffuseColor")), &result);
    }
    else if (hasMetallic)
    {
        ReadUvTransformFromTexture(
            GetConnectedShader(shader, TfToken("metallic")), &result);
    }
    else if (hasEmissiveTex)
    {
        ReadUvTransformFromTexture(
            GetConnectedShader(shader, TfToken("emissiveColor")), &result);
    }

    return result;
}

RUsdMaterial DefaultImportMaterial()
{
    RUsdMaterial result = {1.0f, 1.0f, 1.0f, 1.0f, 0.0f, 0.5f};
    result.uvScale[0] = 1.0f;
    result.uvScale[1] = 1.0f;
    return result;
}

// ---- MDL (Omniverse) materials ---------------------------------------------------------
// Omniverse authors shading as MDL under the `mdl` render context only (outputs:mdl:surface),
// so ComputeSurfaceSource() with the default universal context finds no shader at all. The
// Omniverse library shaders (OmniPBR, OmniSurface, OmniGlass) have documented parameters that
// map onto RUsdMaterial closely enough for a real-time preview. MDL leaves an unauthored
// parameter at the module's own default, which USD cannot see, so those defaults are restated
// here.

// An input's effective value, following connections up to material interface inputs. Returns
// false when the value is produced by another shader node, which cannot be evaluated here.
bool GetMdlInputValue(const UsdShadeShader& shader, const char* name, VtValue* value)
{
    UsdShadeInput input = shader.GetInput(TfToken(name));
    if (!input)
    {
        return false;
    }

    for (const UsdAttribute& attribute : input.GetValueProducingAttributes())
    {
        if (UsdShadeOutput::IsOutput(attribute))
        {
            return false;
        }

        if (attribute.Get(value) && !value->IsEmpty())
        {
            return true;
        }
    }

    return input.Get(value) && !value->IsEmpty();
}

bool GetMdlFloat(const UsdShadeShader& shader, const char* name, float* out)
{
    VtValue value;
    if (!GetMdlInputValue(shader, name, &value))
    {
        return false;
    }

    if (VtValueHoldsType(value, "float"))
    {
        *out = value.UncheckedGet<float>();
    }
    else if (VtValueHoldsType(value, "double"))
    {
        *out = static_cast<float>(value.UncheckedGet<double>());
    }
    else if (VtValueHoldsType(value, "int"))
    {
        *out = static_cast<float>(value.UncheckedGet<int>());
    }
    else
    {
        return false;
    }

    return true;
}

bool GetMdlBool(const UsdShadeShader& shader, const char* name, bool fallback)
{
    VtValue value;
    if (!GetMdlInputValue(shader, name, &value))
    {
        return fallback;
    }

    if (VtValueHoldsType(value, "bool"))
    {
        return value.UncheckedGet<bool>();
    }

    if (VtValueHoldsType(value, "int"))
    {
        return value.UncheckedGet<int>() != 0;
    }

    return fallback;
}

bool GetMdlColor(const UsdShadeShader& shader, const char* name, GfVec3f* out)
{
    VtValue value;
    if (!GetMdlInputValue(shader, name, &value))
    {
        return false;
    }

    if (VtValueHoldsType(value, "GfVec3f"))
    {
        *out = value.UncheckedGet<GfVec3f>();
    }
    else if (VtValueHoldsType(value, "GfVec3d"))
    {
        const GfVec3d& v = value.UncheckedGet<GfVec3d>();
        *out = GfVec3f(static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2]));
    }
    else if (VtValueHoldsType(value, "GfVec4f"))
    {
        const GfVec4f& v = value.UncheckedGet<GfVec4f>();
        *out = GfVec3f(v[0], v[1], v[2]);
    }
    else
    {
        return false;
    }

    return true;
}

bool GetMdlVec2(const UsdShadeShader& shader, const char* name, float* out2)
{
    VtValue value;
    if (!GetMdlInputValue(shader, name, &value))
    {
        return false;
    }

    if (VtValueHoldsType(value, "GfVec2f"))
    {
        const GfVec2f& v = value.UncheckedGet<GfVec2f>();
        out2[0] = v[0];
        out2[1] = v[1];
        return true;
    }

    if (VtValueHoldsType(value, "GfVec2d"))
    {
        const GfVec2d& v = value.UncheckedGet<GfVec2d>();
        out2[0] = static_cast<float>(v[0]);
        out2[1] = static_cast<float>(v[1]);
        return true;
    }

    return false;
}

// MDL texture parameters are plain asset-valued inputs, not connections to a texture node.
bool GetMdlTexture(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const char* name,
    char* buffer,
    size_t capacity)
{
    VtValue value;
    if (!GetMdlInputValue(shader, name, &value) || !VtValueHoldsType(value, "SdfAssetPath"))
    {
        return false;
    }

    UsdShadeInput input = shader.GetInput(TfToken(name));
    return StoreAssetPath(context, value.UncheckedGet<SdfAssetPath>(), buffer, capacity,
                          input ? input.GetAttr() : UsdAttribute());
}

void SetColor(RUsdMaterial* material, const GfVec3f& color)
{
    material->r = color[0];
    material->g = color[1];
    material->b = color[2];
}

// The MDL module a shader runs: its sub-identifier ("OmniPBR"), else the module file's stem.
std::string GetMdlShaderName(const UsdShadeShader& shader)
{
    const TfToken mdl("mdl");
    TfToken subIdentifier;
    if (shader.GetSourceAssetSubIdentifier(&subIdentifier, mdl) && !subIdentifier.IsEmpty())
    {
        return subIdentifier.GetString();
    }

    SdfAssetPath asset;
    if (!shader.GetSourceAsset(&asset, mdl))
    {
        return std::string();
    }

    std::string name = TfGetBaseName(asset.GetAssetPath());
    const std::string extension = ".mdl";
    if (name.size() > extension.size() &&
        name.compare(name.size() - extension.size(), extension.size(), extension) == 0)
    {
        name.resize(name.size() - extension.size());
    }

    return name;
}

bool StartsWith(const std::string& value, const char* prefix)
{
    return value.rfind(prefix, 0) == 0;
}

// OmniPBR and its variants (OmniPBR_ClearCoat, OmniPBR_Opacity, ...).
void ReadOmniPbr(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const std::string& materialPath,
    RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    // With a diffuse texture, OmniPBR multiplies it by the tint alone; the constant is the
    // untextured colour. Same rule as UsdPreviewSurface, so the C# side needs nothing new.
    GfVec3f diffuse(0.2f, 0.2f, 0.2f);
    GetMdlColor(shader, "diffuse_color_constant", &diffuse);
    GfVec3f tint(1.0f, 1.0f, 1.0f);
    GetMdlColor(shader, "diffuse_tint", &tint);
    SetColor(&m, GfCompMult(diffuse, tint));
    GetMdlTexture(context, shader, "diffuse_texture", m.albedoTexturePath, sizeof(m.albedoTexturePath));

    m.metallic = 0.0f;
    GetMdlFloat(shader, "metallic_constant", &m.metallic);
    m.roughness = 0.5f;
    GetMdlFloat(shader, "reflection_roughness_constant", &m.roughness);

    GetMdlTexture(context, shader, "normalmap_texture", m.normalTexturePath, sizeof(m.normalTexturePath));

    // Unity's packed metallic+smoothness map has no equivalent of OmniPBR's separate (or ORM)
    // maps, so those stay at their constants rather than being misread.
    // OmniPBR's ORM map carries the same three facts Unity's metallic map does, in different
    // channels, so it is imported and repacked rather than discarded: 190 of Factory's materials
    // were falling back to constants with a warning, which is most of what that stage is shaded
    // with. enable_ORM_texture decides whether the map is in use; OmniPBR ignores it otherwise.
    if (GetMdlBool(shader, "enable_ORM_texture", false) &&
        GetMdlTexture(context, shader, "ORM_texture", m.metallicTexturePath,
                      sizeof(m.metallicTexturePath)))
    {
        out->metallicIsOrm = true;
    }

    // Separate metallic and roughness maps: two files where Unity wants one. They are reported
    // as they are and the host packs them while it holds the pixels (API 10).
    if (!out->metallicIsOrm)
    {
        if (GetMdlTexture(context, shader, "metallic_texture", m.metallicTexturePath,
                          sizeof(m.metallicTexturePath)))
        {
            out->metallicIsPlain = true;
        }

        char roughnessPath[512] = {0};
        if (GetMdlTexture(context, shader, "reflectionroughness_texture", roughnessPath,
                          sizeof(roughnessPath)))
        {
            out->roughnessTexturePath = roughnessPath;
        }
    }

    // ORM's red channel is occlusion, but an authored ao_texture overrides it.
    char occlusionPath[512] = {0};
    if (GetMdlTexture(context, shader, "ao_texture", occlusionPath, sizeof(occlusionPath)))
    {
        out->occlusionTexturePath = occlusionPath;
    }

    if (GetMdlBool(shader, "enable_emission", false))
    {
        GfVec3f emissive(1.0f, 0.1f, 0.1f);
        GetMdlColor(shader, "emissive_color", &emissive);
        // emissive_intensity is in nits with a default of 40. Normalising by that default keeps
        // an untouched emitter at its authored colour instead of blowing it out to white.
        float intensity = 40.0f;
        GetMdlFloat(shader, "emissive_intensity", &intensity);
        const float scale = std::max(0.0f, intensity / 40.0f);
        m.emissive[0] = emissive[0] * scale;
        m.emissive[1] = emissive[1] * scale;
        m.emissive[2] = emissive[2] * scale;
        GetMdlTexture(context, shader, "emissive_color_texture",
            m.emissiveTexturePath, sizeof(m.emissiveTexturePath));
    }

    if (GetMdlBool(shader, "enable_opacity", false))
    {
        GetMdlFloat(shader, "opacity_constant", &m.a);
        if (GetMdlBool(shader, "enable_opacity_texture", false))
        {
            char opacityTexture[1024] = {0};
            if (GetMdlTexture(context, shader, "opacity_texture", opacityTexture, sizeof(opacityTexture)))
            {
                out->opacityTexturePath = opacityTexture;
            }
        }

        GetMdlFloat(shader, "opacity_threshold", &out->opacityThreshold);
    }

    GetMdlVec2(shader, "texture_scale", m.uvScale);
    GetMdlVec2(shader, "texture_translate", m.uvOffset);
}

void ReadOmniSurface(RUsdContext* context, const UsdShadeShader& shader,
                     RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    // OmniSurface's maps are *_image, and each sits beside the constant it overrides.
    GetMdlTexture(context, shader, "diffuse_reflection_color_image", m.albedoTexturePath,
                  sizeof(m.albedoTexturePath));
    GetMdlTexture(context, shader, "geometry_normal_image", m.normalTexturePath,
                  sizeof(m.normalTexturePath));
    // metalness_image and specular_reflection_roughness_image are two plain greyscale maps
    // (names from OmniSurface.mdl in the Kit kernel); the host packs them.
    if (GetMdlTexture(context, shader, "metalness_image", m.metallicTexturePath,
                      sizeof(m.metallicTexturePath)))
    {
        out->metallicIsPlain = true;
    }

    char roughnessPath[512] = {0};
    if (GetMdlTexture(context, shader, "specular_reflection_roughness_image", roughnessPath,
                      sizeof(roughnessPath)))
    {
        out->roughnessTexturePath = roughnessPath;
    }

    GetMdlTexture(context, shader, "emission_color_image", m.emissiveTexturePath,
                  sizeof(m.emissiveTexturePath));

    GfVec3f base(1.0f, 1.0f, 1.0f);
    GetMdlColor(shader, "diffuse_reflection_color", &base);
    float weight = 0.8f;
    GetMdlFloat(shader, "diffuse_reflection_weight", &weight);
    SetColor(&m, base * weight);

    m.metallic = 0.0f;
    GetMdlFloat(shader, "metalness", &m.metallic);
    m.roughness = 0.2f;
    GetMdlFloat(shader, "specular_reflection_roughness", &m.roughness);

    float emissionWeight = 0.0f;
    GetMdlFloat(shader, "emission_weight", &emissionWeight);
    if (emissionWeight > 0.0f)
    {
        GfVec3f emission(1.0f, 1.0f, 1.0f);
        GetMdlColor(shader, "emission_color", &emission);
        m.emissive[0] = emission[0] * emissionWeight;
        m.emissive[1] = emission[1] * emissionWeight;
        m.emissive[2] = emission[2] * emissionWeight;
    }

    if (GetMdlBool(shader, "enable_opacity", false))
    {
        GetMdlFloat(shader, "geometry_opacity", &m.a);
    }

    // Transmission has no real-time equivalent; partial alpha is the closest readable stand-in.
    float transmission = 0.0f;
    GetMdlFloat(shader, "specular_transmission_weight", &transmission);
    if (transmission > 0.0f)
    {
        m.a = std::min(m.a, 1.0f - 0.75f * std::min(transmission, 1.0f));
    }
}

void ReadOmniGlass(RUsdContext* context, const UsdShadeShader& shader,
                   RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    GfVec3f color(1.0f, 1.0f, 1.0f);
    GetMdlColor(shader, "glass_color", &color);
    SetColor(&m, color);
    m.a = 0.25f;
    m.metallic = 0.0f;
    m.roughness = 0.0f;
    GetMdlFloat(shader, "frosting_roughness", &m.roughness);

    // Parameter names from OmniGlass.mdl in the Kit kernel: glass_color_texture,
    // normal_map_texture, roughness_texture, reflection_color_texture, cutout_opacity_texture.
    // An earlier version of this read "roughness_image", which is OmniGridExplorer's name, not
    // OmniGlass's; nothing in Factory binds a textured OmniGlass, so the mistake had no witness.
    GetMdlTexture(context, shader, "glass_color_texture", m.albedoTexturePath,
                  sizeof(m.albedoTexturePath));
    GetMdlTexture(context, shader, "normal_map_texture", m.normalTexturePath,
                  sizeof(m.normalTexturePath));

    char roughnessPath[512] = {0};
    if (GetMdlTexture(context, shader, "roughness_texture", roughnessPath, sizeof(roughnessPath)))
    {
        out->roughnessTexturePath = roughnessPath;
    }

    if (GetMdlBool(shader, "enable_opacity", false))
    {
        char opacityTexture[1024] = {0};
        if (GetMdlTexture(context, shader, "cutout_opacity_texture", opacityTexture, sizeof(opacityTexture)))
        {
            out->opacityTexturePath = opacityTexture;
        }
    }
}

// OmniGridExplorer, the procedural grid USD Explorer stands its samples on (712 shaders in
// Factory). The grid lines cannot be reproduced without the MDL, so the surface takes the mean
// of its two checkerboard colours, its roughness constant and its roughness_image (the one
// texture_2d parameter the module declares).
void ReadOmniGridExplorer(RUsdContext* context, const UsdShadeShader& shader,
                          RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    GfVec3f first(0.5f, 0.5f, 0.5f);
    GfVec3f second(0.5f, 0.5f, 0.5f);
    const bool hasFirst = GetMdlColor(shader, "background_color_01", &first);
    const bool hasSecond = GetMdlColor(shader, "background_color_02", &second);
    if (!hasFirst && !hasSecond)
    {
        GetMdlColor(shader, "background_color", &first);
        second = first;
    }

    SetColor(&m, (first + second) * 0.5f);
    m.metallic = 0.0f;
    m.roughness = 0.5f;
    GetMdlFloat(shader, "roughness", &m.roughness);

    char roughnessPath[512] = {0};
    if (GetMdlTexture(context, shader, "roughness_image", roughnessPath, sizeof(roughnessPath)))
    {
        out->roughnessTexturePath = roughnessPath;
    }

    GetMdlVec2(shader, "texture_scale", m.uvScale);
    GetMdlVec2(shader, "texture_translate", m.uvOffset);
}

// Returns false when the MDL module is not one this reader understands.
bool ExtractMdlMaterial(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const std::string& materialPath,
    RUsdContext::ImportedMaterial* out)
{
    const std::string name = GetMdlShaderName(shader);
    if (StartsWith(name, "OmniPBR"))
    {
        ReadOmniPbr(context, shader, materialPath, out);
        return true;
    }

    if (StartsWith(name, "OmniSurface"))
    {
        ReadOmniSurface(context, shader, out);
        return true;
    }

    if (StartsWith(name, "OmniGlass"))
    {
        ReadOmniGlass(context, shader, out);
        return true;
    }

    if (StartsWith(name, "OmniGridExplorer"))
    {
        ReadOmniGridExplorer(context, shader, out);
        return true;
    }

    // Unknown module: take a base colour if it carries one under a common name.
    GfVec3f color;
    for (const char* input : {"diffuse_color_constant", "base_color", "diffuse_color", "color"})
    {
        if (GetMdlColor(shader, input, &color))
        {
            SetColor(&out->material, color);
            AppendImportWarning(context,
                "material '" + materialPath + "' uses MDL module '" + name +
                "'; only its base colour (inputs:" + input + ") is imported.");
            return true;
        }
    }

    return false;
}

// ---- MaterialX materials -----------------------------------------------------------------
// usdMtlx turns a .mtlx file into USD shading under the `mtlx` render context
// (outputs:mtlx:surface), with shader ids that are MaterialX nodedef names. The surface shader's
// inputs usually connect to interface inputs on the Material, and those stay unauthored unless
// the .mtlx set them: an unauthored input means the nodedef's default, which Sdr knows because
// usdMtlx registers every nodedef in the standard library. Textures sit in a NodeGraph, at the
// end of a chain that ends in an image node.

// One MaterialX surface shader's inputs, by role. Empty means the role has no input.
struct MtlxSurfaceInputs
{
    const char* baseColor;
    const char* baseWeight;
    const char* metallic;
    const char* roughness;
    const char* normal;
    const char* emissionColor;
    const char* emissionWeight;
    const char* opacity;
    const char* transmission;
    const char* transmissionColor;
};

const MtlxSurfaceInputs* FindMtlxSurfaceInputs(const std::string& id)
{
    static const MtlxSurfaceInputs standardSurface = {
        "base_color", "base", "metalness", "specular_roughness", "normal",
        "emission_color", "emission", "opacity", "transmission", "transmission_color"};
    static const MtlxSurfaceInputs openPbr = {
        "base_color", "base_weight", "base_metalness", "specular_roughness", "geometry_normal",
        "emission_color", "emission_luminance", "geometry_opacity", "transmission_weight", "transmission_color"};
    static const MtlxSurfaceInputs previewSurface = {
        "diffuseColor", "", "metallic", "roughness", "normal", "emissiveColor", "", "opacity", "", ""};

    if (StartsWith(id, "ND_standard_surface_"))
    {
        return &standardSurface;
    }

    if (StartsWith(id, "ND_open_pbr_surface_"))
    {
        return &openPbr;
    }

    if (StartsWith(id, "ND_UsdPreviewSurface_"))
    {
        return &previewSurface;
    }

    return nullptr;
}

// An input's value: authored (directly or on the Material interface it connects to), else the
// nodedef default. False when a node produces it, or the name is empty.
bool GetMtlxInputValue(const UsdShadeShader& shader, const TfToken& shaderId, const char* name, VtValue* value)
{
    if (name == nullptr || name[0] == '\0')
    {
        return false;
    }

    UsdShadeInput input = shader.GetInput(TfToken(name));
    if (input)
    {
        for (const UsdAttribute& attribute : input.GetValueProducingAttributes())
        {
            if (UsdShadeOutput::IsOutput(attribute))
            {
                return false;
            }

            if (attribute.Get(value) && !value->IsEmpty())
            {
                return true;
            }
        }
    }

    SdrShaderNodeConstPtr node = SdrRegistry::GetInstance().GetShaderNodeByIdentifier(shaderId);
    SdrShaderPropertyConstPtr property = node ? node->GetShaderInput(TfToken(name)) : nullptr;
    if (property == nullptr)
    {
        return false;
    }

    *value = property->GetDefaultValueAsSdfType();
    if (value->IsEmpty())
    {
        *value = property->GetDefaultValue();
    }

    return !value->IsEmpty();
}

bool GetMtlxFloat(const UsdShadeShader& shader, const TfToken& shaderId, const char* name, float* out)
{
    VtValue value;
    if (!GetMtlxInputValue(shader, shaderId, name, &value))
    {
        return false;
    }

    if (VtValueHoldsType(value, "float"))
    {
        *out = value.UncheckedGet<float>();
        return true;
    }

    if (VtValueHoldsType(value, "double"))
    {
        *out = static_cast<float>(value.UncheckedGet<double>());
        return true;
    }

    return false;
}

bool GetMtlxColor(const UsdShadeShader& shader, const TfToken& shaderId, const char* name, GfVec3f* out)
{
    VtValue value;
    if (!GetMtlxInputValue(shader, shaderId, name, &value))
    {
        return false;
    }

    if (VtValueHoldsType(value, "GfVec3f"))
    {
        *out = value.UncheckedGet<GfVec3f>();
        return true;
    }

    // A scalar where a colour was expected (OpenPBR's geometry_opacity is a float).
    if (VtValueHoldsType(value, "float"))
    {
        const float scalar = value.UncheckedGet<float>();
        *out = GfVec3f(scalar, scalar, scalar);
        return true;
    }

    return false;
}

// The node whose output feeds `input`, through node-graph outputs. Invalid when a value does.
UsdShadeShader GetMtlxSourceNode(const UsdShadeInput& input)
{
    if (!input)
    {
        return UsdShadeShader();
    }

    for (const UsdAttribute& attribute : input.GetValueProducingAttributes())
    {
        if (UsdShadeOutput::IsOutput(attribute))
        {
            UsdShadeShader node(attribute.GetPrim());
            if (node)
            {
                return node;
            }
        }
    }

    return UsdShadeShader();
}

// Follows a surface input back to the image node that supplies it and writes that image's file.
// A normal map node is stepped through to its `in`; any other node in between (a multiply, a
// colour correction) is stepped through to the first input another node feeds, so the texture is
// still found but whatever the node did to it is not reproduced.
bool ReadMtlxTexture(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const char* name,
    char* buffer,
    size_t capacity)
{
    if (name == nullptr || name[0] == '\0')
    {
        return false;
    }

    UsdShadeShader node = GetMtlxSourceNode(shader.GetInput(TfToken(name)));
    for (int depth = 0; node && depth < 16; ++depth)
    {
        TfToken id;
        node.GetShaderId(&id);
        const std::string idString = id.GetString();
        if (StartsWith(idString, "ND_image_") || StartsWith(idString, "ND_tiledimage_") ||
            StartsWith(idString, "ND_gltf_image_") || idString == "UsdUVTexture")
        {
            UsdShadeInput file = node.GetInput(TfToken("file"));
            VtValue value;
            return file && file.Get(&value) && VtValueHoldsType(value, "SdfAssetPath") &&
                StoreAssetPath(context, value.UncheckedGet<SdfAssetPath>(), buffer, capacity, file.GetAttr());
        }

        UsdShadeShader next;
        if (StartsWith(idString, "ND_normalmap"))
        {
            next = GetMtlxSourceNode(node.GetInput(TfToken("in")));
        }
        else
        {
            for (const UsdShadeInput& input : node.GetInputs())
            {
                next = GetMtlxSourceNode(input);
                if (next)
                {
                    break;
                }
            }
        }

        node = next;
    }

    return false;
}

void ExtractMtlxMaterial(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const TfToken& shaderId,
    const MtlxSurfaceInputs& inputs,
    const std::string& materialPath,
    RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    float baseWeight = 1.0f;
    GetMtlxFloat(shader, shaderId, inputs.baseWeight, &baseWeight);

    const bool hasAlbedo =
        ReadMtlxTexture(context, shader, inputs.baseColor, m.albedoTexturePath, sizeof(m.albedoTexturePath));
    GfVec3f baseColor(1.0f, 1.0f, 1.0f);
    if (!hasAlbedo)
    {
        GetMtlxColor(shader, shaderId, inputs.baseColor, &baseColor);
    }

    // With a texture the colour is the weight alone, which Unity multiplies into the texture.
    SetColor(&m, baseColor * baseWeight);

    GetMtlxFloat(shader, shaderId, inputs.metallic, &m.metallic);
    GetMtlxFloat(shader, shaderId, inputs.roughness, &m.roughness);
    ReadMtlxTexture(context, shader, inputs.normal, m.normalTexturePath, sizeof(m.normalTexturePath));

    GfVec3f opacity(1.0f, 1.0f, 1.0f);
    if (GetMtlxColor(shader, shaderId, inputs.opacity, &opacity))
    {
        m.a = (opacity[0] + opacity[1] + opacity[2]) / 3.0f;
    }

    // Transmission (glass: OpenChessSet's pawn tops) has no real-time equivalent. Left out, a
    // fully transmissive surface with base_color 1 imported as an opaque white ball. The surface
    // takes the transmission colour in proportion to the weight and becomes partly transparent,
    // the same stand-in the OmniSurface path uses.
    float transmission = 0.0f;
    if (!hasAlbedo && GetMtlxFloat(shader, shaderId, inputs.transmission, &transmission) && transmission > 0.0f)
    {
        transmission = std::min(transmission, 1.0f);
        GfVec3f tint(1.0f, 1.0f, 1.0f);
        GetMtlxColor(shader, shaderId, inputs.transmissionColor, &tint);
        const GfVec3f surface(m.r, m.g, m.b);
        SetColor(&m, surface * (1.0f - transmission) + tint * transmission);
        m.a = std::min(m.a, 1.0f - 0.75f * transmission);
    }

    const bool hasEmissiveTexture = ReadMtlxTexture(
        context, shader, inputs.emissionColor, m.emissiveTexturePath, sizeof(m.emissiveTexturePath));
    float emissionWeight = 1.0f;
    GetMtlxFloat(shader, shaderId, inputs.emissionWeight, &emissionWeight);
    GfVec3f emission(1.0f, 1.0f, 1.0f);
    if (hasEmissiveTexture || GetMtlxColor(shader, shaderId, inputs.emissionColor, &emission))
    {
        emission *= emissionWeight;
        m.emissive[0] = emission[0];
        m.emissive[1] = emission[1];
        m.emissive[2] = emission[2];
    }

    // Unity's metallic slot takes one packed metallic(.r) + smoothness(.a) map. MaterialX keeps
    // metalness and roughness as separate greyscale images, and putting one of those in that slot
    // would read its alpha as smoothness, so they are left out and the constants above apply.
    char unused[1024] = {0};
    if (ReadMtlxTexture(nullptr, shader, inputs.metallic, unused, sizeof(unused)) ||
        ReadMtlxTexture(nullptr, shader, inputs.roughness, unused, sizeof(unused)))
    {
        AppendImportWarning(context,
            "material '" + materialPath + "' (MaterialX " + shaderId.GetString() +
            ") has metalness/roughness textures, which Unity's packed metallic map cannot take "
            "unconverted; constant values are used for them.");
    }
}

// Fills `out` from the material's surface shader. Returns false when no supported shader is
// found, after saying why; `out` then holds the default material.
bool ExtractMaterial(
    RUsdContext* context,
    const UsdShadeMaterial& material,
    RUsdContext::ImportedMaterial* out)
{
    out->material = DefaultImportMaterial();
    const std::string materialPath = material.GetPath().GetString();

    // UsdPreviewSurface wins when a material carries both, as it does for every other renderer.
    UsdShadeShader universal = material.ComputeSurfaceSource();
    TfToken shaderId;
    if (universal && universal.GetShaderId(&shaderId) && shaderId == TfToken("UsdPreviewSurface"))
    {
        out->material = ExtractPreviewMaterial(context, material, out);
        ExtractOpacityInfo(context, material, &out->opacityTexturePath, &out->opacityThreshold);
        return true;
    }

    // This falls back to the universal output when there is no mdl one; that shader was
    // already rejected above.
    UsdShadeShader mdl = material.ComputeSurfaceSource(TfTokenVector{TfToken("mdl")});
    if (mdl && universal && mdl.GetPath() == universal.GetPath())
    {
        mdl = UsdShadeShader();
    }

    if (mdl && ExtractMdlMaterial(context, mdl, materialPath, out))
    {
        return true;
    }

    // As with mdl, this falls back to the universal output, which was already rejected above.
    UsdShadeShader mtlx = material.ComputeSurfaceSource(TfTokenVector{TfToken("mtlx")});
    TfToken mtlxId;
    if (mtlx && !(universal && mtlx.GetPath() == universal.GetPath()) && mtlx.GetShaderId(&mtlxId))
    {
        if (const MtlxSurfaceInputs* inputs = FindMtlxSurfaceInputs(mtlxId.GetString()))
        {
            ExtractMtlxMaterial(context, mtlx, mtlxId, *inputs, materialPath, out);
            return true;
        }

        AppendImportWarning(context,
            "material '" + materialPath + "' uses MaterialX surface '" + mtlxId.GetString() +
            "', which is not supported; the mesh's displayColor, else the default material, is used.");
        return false;
    }

    std::string reason;
    if (universal)
    {
        reason = "surface shader is '" +
            (shaderId.IsEmpty() ? std::string("(no info:id)") : shaderId.GetString()) +
            "', not UsdPreviewSurface or a supported MDL shader";
    }
    else if (mdl)
    {
        const std::string name = GetMdlShaderName(mdl);
        reason = "MDL shader '" + (name.empty() ? std::string("(unnamed)") : name) + "' is not supported";
    }
    else
    {
        reason = "has no surface shader source";
    }

    AppendImportWarning(context,
        "material '" + materialPath + "' " + reason +
        "; the mesh's displayColor, else the default material, is used.");
    return false;
}

// primvars:displayColor/displayOpacity, the colour every USD viewer falls back to when it
// cannot evaluate a material. There is no vertex-colour channel in the import ABI, so
// per-vertex or per-face colours are averaged, which beats default white by a long way.
struct DisplayColor
{
    bool valid = false;
    GfVec3f color = GfVec3f(1.0f, 1.0f, 1.0f);
    float opacity = 1.0f;

    // Per-face colours, kept only when displayColor is authored `uniform` with one entry per
    // face. `color` above stays the mean so every caller that cannot express per-face colour
    // keeps working. BuildImportedSubmeshes turns these into one submesh per distinct colour,
    // which is the only way to show them without a vertex-colour channel in the import ABI or
    // a shader that reads one. Capped, because a mesh with thousands of distinct face colours
    // would otherwise become thousands of Unity materials.
    std::vector<GfVec3f> faceColors;
};

// Above this many distinct face colours the mesh falls back to the mean. Creases_SpinningPyramids
// has 24 faces in 24 colours; a scattering mesh with a colour per blade of grass does not belong
// in submeshes.
constexpr size_t kMaxDisplayColorSubmeshes = 64;

int AddDisplayColorMaterial(
    RUsdContext* context,
    const std::string& baseKey,
    const std::string& name,
    const DisplayColor& displayColor)
{
    std::ostringstream key;
    key << baseKey << "|displayColor" << std::hexfloat << displayColor.color[0] << ','
        << displayColor.color[1] << ',' << displayColor.color[2] << ',' << displayColor.opacity;

    RUsdContext::MaterialCacheEntry& entry = context->importedMaterialCache[key.str()];
    if (entry.recognized)
    {
        return entry.index;
    }

    RUsdContext::ImportedMaterial importedMaterial;
    importedMaterial.name = name;
    importedMaterial.material = DefaultImportMaterial();
    SetColor(&importedMaterial.material, displayColor.color);
    importedMaterial.material.a = displayColor.opacity;
    context->importedMaterials.push_back(importedMaterial);

    entry.index = static_cast<int>(context->importedMaterials.size() - 1);
    entry.recognized = true;
    return entry.index;
}

bool ReadVec3Array(const UsdAttribute& attribute, VtArray<GfVec3f>* out);

// The mean of a colour primvar on `prim` (inherited from ancestors, as primvars are). Like
// displayColor below, per-vertex or per-face values are averaged: the import ABI has no
// vertex-colour channel.
bool ReadPrimvarColorMean(const UsdPrim& prim, const std::string& name, GfVec3f* color)
{
    if (!prim || name.empty())
    {
        return false;
    }

    UsdGeomPrimvar primvar = UsdGeomPrimvarsAPI(prim).FindPrimvarWithInheritance(TfToken(name));
    VtArray<GfVec3f> values;
    if (!primvar || !ReadVec3Array(primvar.GetAttr(), &values) || values.empty())
    {
        return false;
    }

    GfVec3f sum(0.0f, 0.0f, 0.0f);
    for (const GfVec3f& value : values)
    {
        sum += value;
    }

    *color = sum / static_cast<float>(values.size());
    return true;
}

// The per-face values of a colour primvar, or empty when it is not authored `uniform` with one
// entry per face. Used to give each face its own material; everything else stays averaged.
std::vector<GfVec3f> ReadPrimvarColorPerFace(
    const UsdPrim& prim, const std::string& name, size_t faceCount)
{
    std::vector<GfVec3f> result;
    if (!prim || name.empty() || faceCount == 0)
    {
        return result;
    }

    UsdGeomPrimvar primvar =
        UsdGeomPrimvarsAPI(prim).FindPrimvarWithInheritance(TfToken("primvars:" + name));
    VtArray<GfVec3f> values;
    if (primvar && primvar.GetInterpolation() == UsdGeomTokens->uniform &&
        ReadVec3Array(primvar.GetAttr(), &values) && values.size() == faceCount)
    {
        result.assign(values.begin(), values.end());
    }

    return result;
}

// One colour's variant of a primvar-driven material. Same specialisation AddPrimvarColorMaterial
// does for a whole mesh, but for a colour the caller already has, so faces of one mesh can take
// different variants. Keyed by base material and colour, so repeats collapse.
int AddPrimvarColorVariant(RUsdContext* context, int baseIndex, const GfVec3f& color)
{
    RUsdContext::ImportedMaterial specialised =
        context->importedMaterials[static_cast<size_t>(baseIndex)];

    std::ostringstream key;
    key << "base:" << baseIndex << "|primvar:" << specialised.diffusePrimvar << std::hexfloat
        << '=' << color[0] << ',' << color[1] << ',' << color[2];

    RUsdContext::MaterialCacheEntry& entry = context->importedMaterialCache[key.str()];
    if (entry.recognized)
    {
        return entry.index;
    }

    SetColor(&specialised.material, color);
    context->importedMaterials.push_back(specialised);
    entry.index = static_cast<int>(context->importedMaterials.size() - 1);
    entry.recognized = true;
    return entry.index;
}

// A material whose base colour comes from a mesh primvar, specialised for one mesh: the shared
// material with that mesh's primvar colour in place of the reader's fallback. Meshes with the
// same colour share the copy.
int AddPrimvarColorMaterial(RUsdContext* context, const std::string& baseKey, int baseIndex, const UsdPrim& meshPrim)
{
    const RUsdContext::ImportedMaterial& base =
        context->importedMaterials[static_cast<size_t>(baseIndex)];
    GfVec3f color;
    if (!ReadPrimvarColorMean(meshPrim, base.diffusePrimvar, &color))
    {
        return baseIndex;
    }

    std::ostringstream key;
    key << baseKey << "|primvar:" << base.diffusePrimvar << std::hexfloat << '=' << color[0] << ','
        << color[1] << ',' << color[2];

    RUsdContext::MaterialCacheEntry& entry = context->importedMaterialCache[key.str()];
    if (entry.recognized)
    {
        return entry.index;
    }

    RUsdContext::ImportedMaterial specialised = base;
    SetColor(&specialised.material, color);
    context->importedMaterials.push_back(specialised);
    entry.index = static_cast<int>(context->importedMaterials.size() - 1);
    entry.recognized = true;
    return entry.index;
}

int AddImportedMaterial(
    RUsdContext* context,
    const UsdShadeMaterial& material,
    const DisplayColor& displayColor,
    const UsdPrim& meshPrim)
{
    if (context == nullptr)
    {
        return -1;
    }

    if (!material)
    {
        return displayColor.valid
            ? AddDisplayColorMaterial(context, std::string(), "displayColor", displayColor)
            : -1;
    }

    // Instance proxies of one prototype bind the same material prim inside the prototype. Keying
    // by that prim keeps forty instances of a part from becoming forty identical Unity materials.
    UsdPrim prim = material.GetPrim();
    const std::string key =
        (prim.IsInstanceProxy() ? prim.GetPrimInPrototype() : prim).GetPath().GetString();
    const std::string name = material.GetPath().GetString();

    auto found = context->importedMaterialCache.find(key);
    if (found == context->importedMaterialCache.end())
    {
        RUsdContext::ImportedMaterial importedMaterial;
        importedMaterial.name = name;
        RUsdContext::MaterialCacheEntry entry;
        entry.recognized = ExtractMaterial(context, material, &importedMaterial);
        context->importedMaterials.push_back(importedMaterial);
        entry.index = static_cast<int>(context->importedMaterials.size() - 1);
        found = context->importedMaterialCache.emplace(key, entry).first;
    }

    if (found->second.recognized &&
        !context->importedMaterials[static_cast<size_t>(found->second.index)].diffusePrimvar.empty())
    {
        return AddPrimvarColorMaterial(context, key, found->second.index, meshPrim);
    }

    if (found->second.recognized || !displayColor.valid)
    {
        return found->second.index;
    }

    return AddDisplayColorMaterial(context, key, name, displayColor);
}

// Triangulates one polygon face (fan) into `indices` with the import winding flip.
// `leftHanded` is USD's other face orientation. The flip below turns USD's default rightHanded
// winding into Unity's; a mesh that already authors the opposite winding needs that flip undone,
// or its faces end up pointing away from the viewer. Moore Lane authors leftHanded on all 740 of
// its meshes, which is why its terrain could only be seen from underneath.
void TriangulateFace(
    const int* cornerVertices,
    int cornerCount,
    int faceStart,
    int faceVertexCount,
    bool leftHanded,
    std::vector<int>* indices)
{
    // Subtraction, not addition. `faceStart + faceVertexCount` sums two values that come from
    // the file, so the sum itself can overflow and then compare as if it were in range, which is
    // exactly how a hostile stage got past this guard. Subtracting from a cornerCount that is
    // known to be the real buffer length cannot overflow.
    if (faceVertexCount < 3 || faceStart < 0 || cornerCount < faceVertexCount ||
        faceStart > cornerCount - faceVertexCount)
    {
        return;
    }

    for (int i = 1; i + 1 < faceVertexCount; ++i)
    {
        const int first = cornerVertices[static_cast<size_t>(faceStart)];
        const int second = cornerVertices[static_cast<size_t>(faceStart + i + 1)];
        const int third = cornerVertices[static_cast<size_t>(faceStart + i)];
        indices->push_back(first);
        indices->push_back(leftHanded ? third : second);
        indices->push_back(leftHanded ? second : third);
    }
}

// Triangulates the mesh grouped by material binding. Faces bound by `materialBind`-family
// UsdGeomSubsets get the subset's material; the rest use the mesh's direct binding. Produces
// a single `indices` buffer with one contiguous RUsdSubmesh range per material (so each maps
// to a Unity submesh). Returns the mesh's default (whole-mesh) material index.
int BuildImportedSubmeshes(
    RUsdContext* context,
    const UsdPrim& meshPrim,
    const VtIntArray& faceVertexCounts,
    const int* cornerVertices,
    int cornerCount,
    bool leftHanded,
    const DisplayColor& displayColor,
    std::vector<int>* indices,
    std::vector<RUsdSubmesh>* submeshes)
{
    const int numFaces = static_cast<int>(faceVertexCounts.size());

    // Authored topology is untrusted input. A stage can declare face counts that sum past
    // INT_MAX, which wrapped this accumulator when it was a 32-bit int and handed corrupted
    // start offsets to TriangulateFace — whose bounds check then overflowed in turn. Accumulate
    // in 64-bit, keep every offset inside the real corner buffer, and require the counts to
    // describe exactly that buffer rather than trusting them to be consistent with it.
    std::vector<int> faceStart(static_cast<size_t>(numFaces));
    int64_t cursor = 0;
    bool topologyValid = true;
    for (int f = 0; f < numFaces; ++f)
    {
        const int faceVertexCount = faceVertexCounts[static_cast<size_t>(f)];
        if (faceVertexCount < 0 || cursor > static_cast<int64_t>(cornerCount) - faceVertexCount)
        {
            topologyValid = false;
            break;
        }

        faceStart[static_cast<size_t>(f)] = static_cast<int>(cursor);
        cursor += faceVertexCount;
    }

    if (!topologyValid || cursor != static_cast<int64_t>(cornerCount))
    {
        // Leaves `indices` empty, so BuildImportedMesh reports the mesh as unusable and skips
        // it instead of triangulating from offsets that do not describe this buffer.
        AppendImportWarning(context,
            "mesh '" + meshPrim.GetPath().GetString() +
            "' has inconsistent topology (faceVertexCounts do not sum to the faceVertexIndices "
            "length); mesh skipped.");
        return -1;
    }

    // Triangulation turns an n-gon into 3(n-2) indices, so a consistent buffer can still describe
    // more triangle indices than an int counts: one 1.4-billion-corner face of zeros is a small
    // usdc and over 4 billion indices, which the copy-out entry points then reported narrowed to
    // int. Counted in 64-bit before anything is built, against the same budget the whole import has.
    int64_t triangleIndices = 0;
    for (int f = 0; f < numFaces; ++f)
    {
        triangleIndices += 3 * static_cast<int64_t>(std::max(0, faceVertexCounts[static_cast<size_t>(f)] - 2));
    }

    if (triangleIndices > static_cast<int64_t>(kMaxImportGeometryElements))
    {
        AppendImportWarning(context,
            "mesh '" + meshPrim.GetPath().GetString() + "' triangulates to " +
            std::to_string(triangleIndices) + " indices, over the import limit of " +
            std::to_string(kMaxImportGeometryElements) + "; mesh skipped.");
        return -1;
    }

    UsdShadeMaterial meshMaterial = ComputeRenderedMaterial(meshPrim);
    int defaultMaterialIndex = AddImportedMaterial(context, meshMaterial, displayColor, meshPrim);

    std::vector<int> faceMaterial(static_cast<size_t>(numFaces), defaultMaterialIndex);

    // Per-face displayColor: one material per distinct colour, so the faces keep the colours the
    // stage authored instead of the mean of all of them. Only when nothing else shades the mesh --
    // a bound material is a stronger statement than displayColor, which is the fallback colour a
    // viewer uses when there is no material at all. AddDisplayColorMaterial keys its cache on the
    // colour, so repeated colours collapse to one material on their own.
    // Two ways a mesh gets per-face colour. Without a bound material it is displayColor itself,
    // the fallback every USD viewer draws. With one, the material's base colour can be wired to a
    // primvar reader -- Creases_SpinningPyramids binds PyramidMaterial whose diffuseColor reads
    // displayColor -- and then the per-face values belong to that primvar, not necessarily
    // displayColor. Both end up as one material per distinct colour.
    const std::string& defaultPrimvar =
        defaultMaterialIndex >= 0
            ? context->importedMaterials[static_cast<size_t>(defaultMaterialIndex)].diffusePrimvar
            : std::string();
    const bool primvarDriven = !defaultPrimvar.empty();
    std::vector<GfVec3f> faceColors =
        primvarDriven ? ReadPrimvarColorPerFace(meshPrim, defaultPrimvar, static_cast<size_t>(numFaces))
                      : displayColor.faceColors;

    if ((!meshMaterial || primvarDriven) && !faceColors.empty() &&
        faceColors.size() == static_cast<size_t>(numFaces))
    {
        std::set<std::tuple<float, float, float>> distinct;
        for (const GfVec3f& faceColor : faceColors)
        {
            distinct.emplace(faceColor[0], faceColor[1], faceColor[2]);
            if (distinct.size() > kMaxDisplayColorSubmeshes)
            {
                break;
            }
        }

        if (distinct.size() <= kMaxDisplayColorSubmeshes)
        {
            for (int f = 0; f < numFaces; ++f)
            {
                const GfVec3f& faceColor = faceColors[static_cast<size_t>(f)];
                if (primvarDriven)
                {
                    faceMaterial[static_cast<size_t>(f)] =
                        AddPrimvarColorVariant(context, defaultMaterialIndex, faceColor);
                    continue;
                }

                DisplayColor faceDisplayColor;
                faceDisplayColor.valid = true;
                faceDisplayColor.color = faceColor;
                faceDisplayColor.opacity = displayColor.opacity;
                faceMaterial[static_cast<size_t>(f)] =
                    AddDisplayColorMaterial(context, std::string(), "displayColor", faceDisplayColor);
            }
        }
        else
        {
            AppendImportWarning(context,
                "mesh '" + meshPrim.GetPath().GetString() + "' authors per-face displayColor in " +
                std::to_string(distinct.size()) + "+ distinct colours; the mean is used instead of "
                "one submesh per colour.");
        }
    }

    std::vector<UsdGeomSubset> subsets =
        UsdShadeMaterialBindingAPI(meshPrim).GetMaterialBindSubsets();
    for (const UsdGeomSubset& subset : subsets)
    {
        UsdShadeMaterial subsetMaterial = ComputeRenderedMaterial(subset.GetPrim());
        // An unbound subset keeps the mesh's material rather than becoming a displayColor stand-in.
        int subsetMaterialIndex = subsetMaterial
            ? AddImportedMaterial(context, subsetMaterial, displayColor, meshPrim)
            : -1;
        if (subsetMaterialIndex < 0)
        {
            subsetMaterialIndex = defaultMaterialIndex;
        }

        VtIntArray subsetFaces;
        subset.GetIndicesAttr().Get(&subsetFaces);
        for (int faceIndex : subsetFaces)
        {
            if (faceIndex >= 0 && faceIndex < numFaces)
            {
                faceMaterial[static_cast<size_t>(faceIndex)] = subsetMaterialIndex;
            }
        }
    }

    // Distinct material indices, in order of first face appearance (stable, deterministic), and
    // the faces belonging to each gathered as we go. Both used to be found by re-reading the face
    // array: a linear search through the distinct list for every face, and then another full pass
    // over every face for every material. On a mesh with twenty subsets that is the face array
    // read forty times over. One pass now, with the order preserved exactly as before.
    std::vector<int> materialOrder;
    std::unordered_map<int, size_t> materialSlot;
    std::vector<std::vector<int>> facesByMaterial;
    for (int f = 0; f < numFaces; ++f)
    {
        const int matIndex = faceMaterial[static_cast<size_t>(f)];
        auto found = materialSlot.find(matIndex);
        if (found == materialSlot.end())
        {
            found = materialSlot.emplace(matIndex, materialOrder.size()).first;
            materialOrder.push_back(matIndex);
            facesByMaterial.emplace_back();
        }

        facesByMaterial[found->second].push_back(f);
    }

    // The triangle count is known from the face sizes, so the index buffer is sized once rather
    // than grown. A stage with twenty million triangles pushes sixty million indices, and letting
    // the vector double its way there copies roughly the whole buffer again on the way.
    {
        size_t triangles = 0;
        for (int f = 0; f < numFaces; ++f)
        {
            const int corners = faceVertexCounts[static_cast<size_t>(f)];
            if (corners > 2)
            {
                triangles += static_cast<size_t>(corners - 2);
            }
        }

        indices->reserve(indices->size() + (triangles * 3));
    }

    for (size_t slot = 0; slot < materialOrder.size(); ++slot)
    {
        const int matIndex = materialOrder[slot];
        int start = static_cast<int>(indices->size());
        for (int f : facesByMaterial[slot])
        {
            TriangulateFace(cornerVertices, cornerCount, faceStart[static_cast<size_t>(f)],
                faceVertexCounts[static_cast<size_t>(f)], leftHanded, indices);
        }

        int count = static_cast<int>(indices->size()) - start;
        if (count > 0)
        {
            submeshes->push_back(RUsdSubmesh{start, count, matIndex});
        }
    }

    return defaultMaterialIndex;
}

// ---- Import-side primvar reading -------------------------------------------------------
// UV and normal data can be authored per point (`vertex`/`varying`), per face-corner
// (`faceVarying`), per face (`uniform`) or once (`constant`), and any of those can be indexed.
// Most DCC exports use faceVarying+indexed `st`, which has no per-point representation at all,
// so the importer resolves each face-corner to a value index and splits vertices when needed.

// Reads a float2-ish array without VtValue::IsHolding<T>(): typeid does not match across the
// plugin/monolithic-OpenUSD boundary (hidden visibility), so the registry type name decides
// which UncheckedGet is safe. See BUILD_NOTES.md.
bool ReadVec2Array(const UsdAttribute& attribute, VtArray<GfVec2f>* out)
{
    if (!attribute || out == nullptr)
    {
        return false;
    }

    VtValue value;
    if (!attribute.Get(&value) || value.IsEmpty())
    {
        return false;
    }

    const std::string typeName = attribute.GetTypeName().GetAsToken().GetString();
    if (typeName == "texCoord2f[]" || typeName == "float2[]")
    {
        *out = value.UncheckedGet<VtArray<GfVec2f>>();
    }
    else if (typeName == "texCoord2d[]" || typeName == "double2[]")
    {
        const VtArray<GfVec2d>& source = value.UncheckedGet<VtArray<GfVec2d>>();
        out->resize(source.size());
        for (size_t i = 0; i < source.size(); ++i)
        {
            (*out)[i] = GfVec2f(static_cast<float>(source[i][0]), static_cast<float>(source[i][1]));
        }
    }
    else if (typeName == "texCoord2h[]" || typeName == "half2[]")
    {
        const VtArray<GfVec2h>& source = value.UncheckedGet<VtArray<GfVec2h>>();
        out->resize(source.size());
        for (size_t i = 0; i < source.size(); ++i)
        {
            (*out)[i] = GfVec2f(static_cast<float>(source[i][0]), static_cast<float>(source[i][1]));
        }
    }
    else
    {
        return false;
    }

    return !out->empty();
}

bool ReadVec3Array(const UsdAttribute& attribute, VtArray<GfVec3f>* out)
{
    if (!attribute || out == nullptr)
    {
        return false;
    }

    VtValue value;
    if (!attribute.Get(&value) || value.IsEmpty())
    {
        return false;
    }

    const std::string typeName = attribute.GetTypeName().GetAsToken().GetString();
    if (typeName == "normal3f[]" || typeName == "vector3f[]" || typeName == "point3f[]" ||
        typeName == "float3[]" || typeName == "color3f[]")
    {
        *out = value.UncheckedGet<VtArray<GfVec3f>>();
    }
    else if (typeName == "normal3d[]" || typeName == "vector3d[]" || typeName == "point3d[]" ||
             typeName == "double3[]")
    {
        const VtArray<GfVec3d>& source = value.UncheckedGet<VtArray<GfVec3d>>();
        out->resize(source.size());
        for (size_t i = 0; i < source.size(); ++i)
        {
            (*out)[i] = GfVec3f(
                static_cast<float>(source[i][0]),
                static_cast<float>(source[i][1]),
                static_cast<float>(source[i][2]));
        }
    }
    else
    {
        return false;
    }

    return !out->empty();
}

// Interpolation + optional index table for one imported vertex attribute.
struct ImportAttrLayout
{
    TfToken interpolation = UsdGeomTokens->vertex;
    VtIntArray indices;
    int valueCount = 0;
    bool valid = false;

    // True when values map straight onto points with no indirection: one value per point, in
    // order. The importer then bulk-copies instead of resolving every element, which matters on
    // scenes with millions of vertices.
    bool IsDirectPerPoint(int pointCount) const
    {
        return valid && indices.empty() && valueCount >= pointCount &&
            (interpolation == UsdGeomTokens->vertex || interpolation == UsdGeomTokens->varying);
    }

    // True when the data has no per-point form, so vertices must be split to carry it.
    bool NeedsSplit() const
    {
        return valid &&
            (interpolation == UsdGeomTokens->faceVarying || interpolation == UsdGeomTokens->uniform);
    }

    // Which element of the value array a face-corner uses, or -1 when the data cannot cover it
    // (short/out-of-range arrays). Callers drop the whole attribute on -1 rather than emitting
    // garbage coordinates.
    int Element(int corner, int point, int face) const
    {
        int element;
        if (interpolation == UsdGeomTokens->constant)
        {
            element = 0;
        }
        else if (interpolation == UsdGeomTokens->uniform)
        {
            element = face;
        }
        else if (interpolation == UsdGeomTokens->faceVarying)
        {
            element = corner;
        }
        else
        {
            element = point;
        }

        if (element < 0)
        {
            return -1;
        }

        if (!indices.empty())
        {
            if (element >= static_cast<int>(indices.size()))
            {
                return -1;
            }

            element = indices[static_cast<size_t>(element)];
        }

        return element >= 0 && element < valueCount ? element : -1;
    }
};

struct ImportUvSource
{
    ImportAttrLayout layout;
    VtArray<GfVec2f> values;
};

struct ImportNormalSource
{
    ImportAttrLayout layout;
    VtArray<GfVec3f> values;
};

bool IsUvPrimvarType(const UsdGeomPrimvar& primvar)
{
    const std::string typeName = primvar.GetAttr().GetTypeName().GetAsToken().GetString();
    return typeName == "texCoord2f[]" || typeName == "texCoord2d[]" || typeName == "texCoord2h[]" ||
        typeName == "float2[]" || typeName == "double2[]" || typeName == "half2[]";
}

void ReadAttrLayout(const UsdGeomPrimvar& primvar, int valueCount, ImportAttrLayout* layout)
{
    layout->interpolation = primvar.GetInterpolation();
    layout->indices.clear();
    if (primvar.IsIndexed())
    {
        primvar.GetIndices(&layout->indices);
    }

    layout->valueCount = valueCount;
    layout->valid = valueCount > 0;
}

bool LoadUvSource(const UsdGeomPrimvar& primvar, ImportUvSource* source)
{
    if (!primvar || !IsUvPrimvarType(primvar) || !ReadVec2Array(primvar.GetAttr(), &source->values))
    {
        return false;
    }

    ReadAttrLayout(primvar, static_cast<int>(source->values.size()), &source->layout);
    return source->layout.valid;
}

// UV set names, most common first. Exporters disagree wildly here ("st" is the USD convention,
// but DCC round-trips produce "uv", "UVMap", "map1", ...), and anything not matched by name is
// picked up by the type scan below, so a mesh never silently loses its texture coordinates.
const char* const kUvSetAliases[RUSD_MAX_UV_SETS][8] = {
    {"st", "st0", "st_0", "uv", "uv0", "UVMap", "map1", nullptr},
    {"st1", "st_1", "uv1", "UVMap1", "UVMap_1", "map2", nullptr, nullptr},
    {"st2", "st_2", "uv2", "UVMap2", "UVMap_2", "map3", nullptr, nullptr},
};

// Interned once: building TfTokens per mesh costs real time on scenes with hundreds of meshes.
const std::vector<std::vector<TfToken>>& UvSetAliasTokens()
{
    static const std::vector<std::vector<TfToken>> tokens = []()
    {
        std::vector<std::vector<TfToken>> result;
        for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
        {
            std::vector<TfToken> names;
            for (int alias = 0; alias < 8 && kUvSetAliases[set][alias] != nullptr; ++alias)
            {
                names.push_back(TfToken(kUvSetAliases[set][alias]));
            }

            result.push_back(std::move(names));
        }

        return result;
    }();

    return tokens;
}

void ResolveUvSources(const UsdGeomMesh& mesh, ImportUvSource sources[RUSD_MAX_UV_SETS])
{
    UsdGeomPrimvarsAPI primvars(mesh.GetPrim());

    const std::vector<std::vector<TfToken>>& aliases = UvSetAliasTokens();
    std::vector<std::string> claimed;
    for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
    {
        for (const TfToken& name : aliases[static_cast<size_t>(set)])
        {
            if (LoadUvSource(primvars.GetPrimvar(name), &sources[set]))
            {
                claimed.push_back(name.GetString());
                break;
            }
        }

    }

    // The scan below exists so a mesh never silently loses its texture coordinates to an
    // unexpected primvar name -- that is a set-0 concern. Enumerating every primvar of every
    // mesh costs real time (most files have exactly one UV set, so the scan would otherwise run
    // for every mesh and find nothing), so secondary sets are matched by name only.
    if (sources[0].layout.valid)
    {
        return;
    }

    // Fall back to any 2-component primvar for the sets still empty, in name order so the
    // choice is deterministic across runs.
    std::vector<UsdGeomPrimvar> candidates = primvars.GetPrimvars();
    std::sort(candidates.begin(), candidates.end(),
        [](const UsdGeomPrimvar& a, const UsdGeomPrimvar& b)
        {
            return a.GetPrimvarName().GetString() < b.GetPrimvarName().GetString();
        });

    for (const UsdGeomPrimvar& primvar : candidates)
    {
        if (!IsUvPrimvarType(primvar))
        {
            continue;
        }

        const std::string name = primvar.GetPrimvarName().GetString();
        if (std::find(claimed.begin(), claimed.end(), name) != claimed.end())
        {
            continue;
        }

        for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
        {
            if (!sources[set].layout.valid && LoadUvSource(primvar, &sources[set]))
            {
                claimed.push_back(name);
                break;
            }
        }
    }
}

void ResolveNormalSource(const UsdGeomMesh& mesh, ImportNormalSource* source)
{
    if (ReadVec3Array(mesh.GetNormalsAttr(), &source->values))
    {
        source->layout.interpolation = mesh.GetNormalsInterpolation();
        source->layout.indices.clear();
        source->layout.valueCount = static_cast<int>(source->values.size());
        source->layout.valid = true;
        return;
    }

    // Some exporters author normals as a primvar instead of the schema attribute, which the
    // importer used to ignore entirely (mesh then fell back to RecalculateNormals).
    static const TfToken normalsToken("normals");
    UsdGeomPrimvar primvar = UsdGeomPrimvarsAPI(mesh.GetPrim()).GetPrimvar(normalsToken);
    if (primvar && ReadVec3Array(primvar.GetAttr(), &source->values))
    {
        ReadAttrLayout(primvar, static_cast<int>(source->values.size()), &source->layout);
    }
}

// Resolves an attribute for every face-corner up front. Returns false (and leaves the source
// marked invalid) when any corner is unreachable, so a malformed primvar drops out cleanly.
bool ResolveCornerElements(
    ImportAttrLayout* layout,
    const VtIntArray& faceVertexIndices,
    const std::vector<int>& cornerFace,
    int pointCount,
    std::vector<int>* elements)
{
    if (!layout->valid)
    {
        return false;
    }

    const int cornerCount = static_cast<int>(cornerFace.size());
    elements->resize(static_cast<size_t>(cornerCount));
    for (int corner = 0; corner < cornerCount; ++corner)
    {
        int point = faceVertexIndices[static_cast<size_t>(corner)];
        if (point < 0 || point >= pointCount)
        {
            point = 0;
        }

        const int element = layout->Element(corner, point, cornerFace[static_cast<size_t>(corner)]);
        if (element < 0)
        {
            layout->valid = false;
            elements->clear();
            return false;
        }

        (*elements)[static_cast<size_t>(corner)] = element;
    }

    return true;
}

// A flat hash table: open addressing with linear probing, and one allocation for the whole thing.
//
// The two tables the mesh split uses are where its time goes -- measured at 2.97 and 2.11 seconds
// of a 6.5 second read on one stage, against 0.07 for triangulating the same mesh and 0.02 for
// its normals. std::unordered_map allocates a node per entry and follows a pointer per lookup, so
// 15.8 million inserts and 43.6 million lookups are 15.8 million allocations and a cache miss
// each. Keeping the entries in one array makes a probe read the line it already fetched.
//
// Both callers know their upper bound before they start -- the element count, the corner count --
// so the table is sized once and never grows. Capacity is a power of two at twice the expected
// count: linear probing degrades sharply past about a 70% load factor.
template <typename K, typename Hash, typename Equal>
class FlatIndexTable
{
public:
    void reset(size_t expected)
    {
        size_t wanted = 16;
        while (wanted < expected * 2)
        {
            wanted <<= 1;
        }

        mask_ = wanted - 1;
        // Only the value array needs its sentinel; a key is never read before its slot is
        // taken, so the key storage is allocated without being written (trivial K: no
        // constructor runs). Writing the empty keys was half of a reset that came to a second
        // per large stage; the pages are now touched by the lookups that need them.
        if (wanted > capacity_)
        {
            keys_.reset(new K[wanted]);
            capacity_ = wanted;
        }

        values_.assign(wanted, -1);
    }

    // The value stored for `key`, inserting `candidate` if the key is not present. Values are
    // vertex and element indices, so a negative value marks a slot as empty.
    int get_or_insert(const K& key, int candidate)
    {
        size_t slot = hash_(key) & mask_;
        while (true)
        {
            if (values_[slot] < 0)
            {
                keys_[slot] = key;
                values_[slot] = candidate;
                return candidate;
            }

            if (equal_(keys_[slot], key))
            {
                return values_[slot];
            }

            slot = (slot + 1) & mask_;
        }
    }

private:
    std::unique_ptr<K[]> keys_;
    size_t capacity_ = 0;
    std::vector<int> values_;
    size_t mask_ = 0;
    Hash hash_;
    Equal equal_;
};

// Rewrites each corner's element to the first element holding the same value. A non-indexed
// faceVarying primvar gives every corner its own element even where neighbouring corners carry
// identical values, so keying the split on element numbers split every corner: 3.1-3.4x the
// vertices on the large survey scenes, with exactly the same triangles. Keyed on values, corners
// that agree share a vertex and only real seams split.
// Hashes and compares a value by its bytes. Equality is a memcmp, which is what comparing the
// bytes as a std::string did and keeps the merge deciding exactly what it decided before:
// -0.0f and 0.0f stay distinct, and two NaNs of the same bit pattern stay equal.
template <typename T>
struct ByteValueHash
{
    size_t operator()(const T& value) const
    {
        const unsigned char* bytes = reinterpret_cast<const unsigned char*>(&value);
        size_t hash = 1469598103934665603ull;
        for (size_t i = 0; i < sizeof(T); ++i)
        {
            hash ^= static_cast<size_t>(bytes[i]);
            hash *= 1099511628211ull;
        }

        return hash;
    }
};

template <typename T>
struct ByteValueEqual
{
    bool operator()(const T& left, const T& right) const
    {
        return std::memcmp(&left, &right, sizeof(T)) == 0;
    }
};

template <typename T>
void MergeEqualElements(std::vector<int>* elements, const VtArray<T>& values)
{
    if (elements->empty())
    {
        return;
    }

    // Keyed on the value itself rather than on a std::string built from its bytes. A
    // non-indexed faceVarying primvar gives every corner its own element, so the lookup below
    // runs once per corner -- 43.6 million of them on one survey stage, and 731 of its 740
    // meshes take this path. Each of those was allocating a short-lived string, hashing it and
    // freeing it again. The bytes are hashed in place now; what is merged with what does not
    // change.
    FlatIndexTable<T, ByteValueHash<T>, ByteValueEqual<T>> firstByValue;
    firstByValue.reset(values.size());
    std::vector<int> canonical(values.size(), -1);
    for (int& element : *elements)
    {
        if (element < 0 || static_cast<size_t>(element) >= values.size())
        {
            continue;
        }

        int& mapped = canonical[static_cast<size_t>(element)];
        if (mapped < 0)
        {
            mapped = firstByValue.get_or_insert(values[static_cast<size_t>(element)], element);
        }

        element = mapped;
    }
}

// Split vertices are identified by the point plus the value each attribute resolved to, so
// corners that agree on all of them keep sharing one Unity vertex.
using SplitVertexKey = std::array<int, RUSD_MAX_UV_SETS + 2>;

struct SplitVertexKeyHash
{
    size_t operator()(const SplitVertexKey& key) const
    {
        size_t hash = 1469598103934665603ull;
        for (int value : key)
        {
            hash ^= static_cast<size_t>(static_cast<uint32_t>(value));
            hash *= 1099511628211ull;
        }

        return hash;
    }
};

// Smooth per-point normals for a mesh that authors none: each face's area-weighted normal
// (Newell's method, so non-planar polygons work too) summed into its points. Needed only when
// vertices get split by a per-corner attribute -- usually faceVarying UVs. Unity's
// RecalculateNormals averages over shared vertices, and after the split neighbouring faces no
// longer share any, so every face came out flat: the faceted Teapot. USD renderers compute
// smooth normals for such a mesh, which is what this reproduces. Winding is taken as
// rightHanded, as the triangulation below takes it.
// `leftHanded` reverses the winding the cross products are taken from. USD's orientation
// defaults to rightHanded, but a stage is free to author the other one -- every one of Moore
// Lane's 740 meshes does -- and ignoring it points every computed normal the wrong way, which
// reads on screen as a surface turned inside out rather than as a normals bug.
std::vector<GfVec3f> ComputeSmoothPointNormals(
    const VtArray<GfVec3f>& points,
    const VtIntArray& faceVertexCounts,
    const VtIntArray& faceVertexIndices,
    bool leftHanded)
{
    const int pointCount = static_cast<int>(points.size());
    const size_t cornerCount = faceVertexIndices.size();
    std::vector<GfVec3f> normals(points.size(), GfVec3f(0.0f, 0.0f, 0.0f));

    size_t start = 0;
    for (int count : faceVertexCounts)
    {
        if (count < 3 || start + static_cast<size_t>(count) > cornerCount)
        {
            start += static_cast<size_t>(std::max(0, count));
            continue;
        }

        GfVec3f faceNormal(0.0f, 0.0f, 0.0f);
        for (int i = 0; i < count; ++i)
        {
            const int a = faceVertexIndices[start + static_cast<size_t>(i)];
            const int b = faceVertexIndices[start + static_cast<size_t>((i + 1) % count)];
            if (a < 0 || a >= pointCount || b < 0 || b >= pointCount)
            {
                continue;
            }

            const GfVec3f& p = points[static_cast<size_t>(a)];
            const GfVec3f& q = points[static_cast<size_t>(b)];
            faceNormal[0] += (p[1] - q[1]) * (p[2] + q[2]);
            faceNormal[1] += (p[2] - q[2]) * (p[0] + q[0]);
            faceNormal[2] += (p[0] - q[0]) * (p[1] + q[1]);
        }

        for (int i = 0; i < count; ++i)
        {
            const int point = faceVertexIndices[start + static_cast<size_t>(i)];
            if (point >= 0 && point < pointCount)
            {
                normals[static_cast<size_t>(point)] += faceNormal;
            }
        }

        start += static_cast<size_t>(count);
    }

    for (GfVec3f& normal : normals)
    {
        const float length = normal.GetLength();
        normal = length > 1e-20f ? normal / length : GfVec3f(0.0f, 1.0f, 0.0f);
    }

    if (leftHanded)
    {
        for (GfVec3f& normal : normals)
        {
            normal = -normal;
        }
    }

    return normals;
}

DisplayColor ReadDisplayColor(const UsdPrim& prim)
{
    DisplayColor result;
    UsdGeomPrimvarsAPI primvars(prim);

    // With inheritance: a colour authored once on a parent Xform applies to every mesh below it.
    UsdGeomPrimvar color = primvars.FindPrimvarWithInheritance(UsdGeomTokens->primvarsDisplayColor);
    VtArray<GfVec3f> colors;
    if (color && ReadVec3Array(color.GetAttr(), &colors))
    {
        GfVec3f sum(0.0f, 0.0f, 0.0f);
        for (const GfVec3f& value : colors)
        {
            sum += value;
        }

        result.color = sum / static_cast<float>(colors.size());
        result.valid = true;

        // `uniform` is one value per face. Keep the array so the caller, which knows the face
        // count, can decide whether to split; anything else (constant, vertex, faceVarying)
        // stays averaged as before.
        if (color.GetInterpolation() == UsdGeomTokens->uniform && colors.size() > 1)
        {
            result.faceColors.assign(colors.begin(), colors.end());
        }
    }

    UsdGeomPrimvar opacity =
        primvars.FindPrimvarWithInheritance(UsdGeomTokens->primvarsDisplayOpacity);
    VtValue value;
    if (opacity && opacity.GetAttr().Get(&value) &&
        opacity.GetAttr().GetTypeName().GetAsToken().GetString() == "float[]")
    {
        const VtArray<float>& opacities = value.UncheckedGet<VtArray<float>>();
        if (!opacities.empty())
        {
            float sum = 0.0f;
            for (float o : opacities)
            {
                sum += o;
            }

            result.opacity = std::min(1.0f, std::max(0.0f, sum / static_cast<float>(opacities.size())));
            result.valid = true;
        }
    }

    return result;
}

bool BuildImportedMesh(
    RUsdContext* context,
    const UsdGeomMesh& mesh,
    UsdGeomXformCache* xformCache,
    RUsdContext::ImportedMesh* importedMesh)
{
    if (context == nullptr || !mesh || xformCache == nullptr || importedMesh == nullptr)
    {
        return false;
    }

    VtArray<GfVec3f> points;
    if (!mesh.GetPointsAttr().Get(&points) || points.empty())
    {
        return false;
    }

    VtIntArray faceVertexCounts;
    VtIntArray faceVertexIndices;
    if (!mesh.GetFaceVertexCountsAttr().Get(&faceVertexCounts) ||
        !mesh.GetFaceVertexIndicesAttr().Get(&faceVertexIndices) ||
        faceVertexCounts.empty() ||
        faceVertexIndices.empty())
    {
        return false;
    }

    // Everything below allocates per corner (split tables, normals, UVs) before triangulation
    // counts its output, so an oversized corner list is refused here, before any of it.
    if (faceVertexIndices.size() > kMaxImportGeometryElements)
    {
        AppendImportWarning(context,
            "mesh '" + mesh.GetPath().GetString() + "' has " +
            std::to_string(faceVertexIndices.size()) + " face-vertex indices, over the import limit of " +
            std::to_string(kMaxImportGeometryElements) + "; mesh skipped.");
        return false;
    }

    // Points/normals are kept in mesh-local space; the prim's transform is reconstructed
    // per node on the Unity side (see importedNodes). ToUnityVector still applies the
    // USD→Unity basis flip (negate X) so local coordinates match the converted transforms.
    importedMesh->primPath = mesh.GetPath().GetString();
    importedMesh->name = mesh.GetPrim().GetName().GetString();

    // Face winding. Only consulted when normals have to be computed; authored normals are used
    // as they stand.
    TfToken orientation = UsdGeomTokens->rightHanded;
    mesh.GetOrientationAttr().Get(&orientation);
    const bool leftHanded = orientation == UsdGeomTokens->leftHanded;

    // Subdivision is not performed: the control cage is imported, which is coarser than what
    // USD renderers draw (the Teapot's lid and handle). Said once per mesh, and only when the
    // scheme is authored -- its fallback is catmullClark, so an unauthored mesh would warn on
    // every polygon export in existence.
    TfToken subdivisionScheme;
    UsdAttribute schemeAttr = mesh.GetSubdivisionSchemeAttr();
    if (schemeAttr.HasAuthoredValue() && schemeAttr.Get(&subdivisionScheme) &&
        subdivisionScheme != UsdGeomTokens->none)
    {
        AppendImportWarning(context,
            "mesh '" + importedMesh->primPath + "' is a " + subdivisionScheme.GetString() +
            " subdivision surface; its control cage is imported without subdividing.");
    }

    const int pointCount = static_cast<int>(points.size());
    const int cornerCount = static_cast<int>(faceVertexIndices.size());

    ImportUvSource uvSources[RUSD_MAX_UV_SETS];
    ResolveUvSources(mesh, uvSources);

    ImportNormalSource normalSource;
    ResolveNormalSource(mesh, &normalSource);

    bool needsSplit = normalSource.layout.NeedsSplit();
    for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
    {
        needsSplit = needsSplit || uvSources[set].layout.NeedsSplit();
    }

    // Corner -> Unity vertex table that drives triangulation below. On the unsplit path the
    // mesh's own faceVertexIndices already *is* that table, so nothing is copied.
    std::vector<int> splitCornerVertices;
    const int* cornerVertices = faceVertexIndices.cdata();

    if (!needsSplit)
    {
        // Everything is authored per point (possibly indexed), so USD points map 1:1 onto Unity
        // vertices and the buffers stay identical to what previous versions produced.
        importedMesh->points.reserve(points.size());
        for (const GfVec3f& point : points)
        {
            importedMesh->points.push_back(ToUnityVector(point));
        }

        if (normalSource.layout.IsDirectPerPoint(pointCount))
        {
            importedMesh->normals.reserve(static_cast<size_t>(pointCount));
            for (int point = 0; point < pointCount; ++point)
            {
                importedMesh->normals.push_back(
                    ToUnityVector(normalSource.values[static_cast<size_t>(point)]));
            }
        }
        else if (normalSource.layout.valid)
        {
            std::vector<RUsdVec3> normals;
            normals.reserve(static_cast<size_t>(pointCount));
            for (int point = 0; point < pointCount; ++point)
            {
                const int element = normalSource.layout.Element(point, point, 0);
                if (element < 0)
                {
                    normals.clear();
                    break;
                }

                normals.push_back(ToUnityVector(normalSource.values[static_cast<size_t>(element)]));
            }

            importedMesh->normals = std::move(normals);
        }
        else
        {
            // No authored normals and nothing forcing a split: this mesh used to arrive with an
            // empty normal buffer, which reads as flipped or unlit rather than as missing data.
            // Moore Lane's terrain is the case -- '/ROOT/Core/Core_FarRim/height', 1,048,416
            // points and not one normal. The split path already computes smooth normals here; do
            // the same so both paths produce a shaded mesh.
            const std::vector<GfVec3f> smooth =
                ComputeSmoothPointNormals(points, faceVertexCounts, faceVertexIndices, leftHanded);
            importedMesh->normals.reserve(smooth.size());
            for (const GfVec3f& normal : smooth)
            {
                importedMesh->normals.push_back(ToUnityVector(normal));
            }
        }

        for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
        {
            const ImportUvSource& source = uvSources[set];
            if (!source.layout.valid)
            {
                continue;
            }

            if (source.layout.IsDirectPerPoint(pointCount))
            {
                importedMesh->uvSets[set].reserve(static_cast<size_t>(pointCount));
                for (int point = 0; point < pointCount; ++point)
                {
                    const GfVec2f& value = source.values[static_cast<size_t>(point)];
                    importedMesh->uvSets[set].push_back({value[0], value[1]});
                }

                continue;
            }

            std::vector<RUsdVec2> uv;
            uv.reserve(static_cast<size_t>(pointCount));
            for (int point = 0; point < pointCount; ++point)
            {
                const int element = source.layout.Element(point, point, 0);
                if (element < 0)
                {
                    uv.clear();
                    break;
                }

                const GfVec2f& value = source.values[static_cast<size_t>(element)];
                uv.push_back({value[0], value[1]});
            }

            importedMesh->uvSets[set] = std::move(uv);
        }
    }
    else
    {
        // At least one attribute is per face-corner, so vertices are split: corners that agree
        // on the point and on every resolved attribute value still share a vertex.
        std::vector<int> cornerFace(static_cast<size_t>(cornerCount), 0);
        {
            int corner = 0;
            for (size_t face = 0; face < faceVertexCounts.size() && corner < cornerCount; ++face)
            {
                const int faceVertexCount = std::max(0, faceVertexCounts[face]);
                for (int i = 0; i < faceVertexCount && corner < cornerCount; ++i, ++corner)
                {
                    cornerFace[static_cast<size_t>(corner)] = static_cast<int>(face);
                }
            }
        }

        splitCornerVertices.assign(static_cast<size_t>(cornerCount), 0);
        cornerVertices = splitCornerVertices.data();

        std::vector<int> normalElements;
        ResolveCornerElements(
            &normalSource.layout, faceVertexIndices, cornerFace, pointCount, &normalElements);
        MergeEqualElements(&normalElements, normalSource.values);

        // No authored normals: smooth ones per point, so the split below does not facet the mesh.
        std::vector<GfVec3f> smoothNormals;
        if (!normalSource.layout.valid)
        {
            smoothNormals = ComputeSmoothPointNormals(points, faceVertexCounts, faceVertexIndices, leftHanded);
        }

        std::vector<int> uvElements[RUSD_MAX_UV_SETS];
        for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
        {
            ResolveCornerElements(
                &uvSources[set].layout, faceVertexIndices, cornerFace, pointCount, &uvElements[set]);
            MergeEqualElements(&uvElements[set], uvSources[set].values);
        }

        FlatIndexTable<SplitVertexKey, SplitVertexKeyHash, std::equal_to<SplitVertexKey>> vertexMap;
        vertexMap.reset(static_cast<size_t>(cornerCount));

        for (int corner = 0; corner < cornerCount; ++corner)
        {
            int point = faceVertexIndices[static_cast<size_t>(corner)];
            if (point < 0 || point >= pointCount)
            {
                point = 0;
            }

            SplitVertexKey key;
            key.fill(-1);
            key[0] = point;
            if (normalSource.layout.valid)
            {
                key[1] = normalElements[static_cast<size_t>(corner)];
            }

            for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
            {
                if (uvSources[set].layout.valid)
                {
                    key[static_cast<size_t>(set) + 2] = uvElements[set][static_cast<size_t>(corner)];
                }
            }

            const int candidate = static_cast<int>(importedMesh->points.size());
            const int vertex = vertexMap.get_or_insert(key, candidate);
            splitCornerVertices[static_cast<size_t>(corner)] = vertex;
            if (vertex != candidate)
            {
                continue;
            }

            importedMesh->points.push_back(ToUnityVector(points[static_cast<size_t>(point)]));
            if (normalSource.layout.valid)
            {
                importedMesh->normals.push_back(
                    ToUnityVector(normalSource.values[static_cast<size_t>(key[1])]));
            }
            else if (!smoothNormals.empty())
            {
                importedMesh->normals.push_back(ToUnityVector(smoothNormals[static_cast<size_t>(point)]));
            }

            for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
            {
                if (!uvSources[set].layout.valid)
                {
                    continue;
                }

                const GfVec2f& value =
                    uvSources[set].values[static_cast<size_t>(key[static_cast<size_t>(set) + 2])];
                importedMesh->uvSets[set].push_back({value[0], value[1]});
            }
        }
    }

    importedMesh->materialIndex = BuildImportedSubmeshes(
        context, mesh.GetPrim(), faceVertexCounts, cornerVertices, cornerCount, leftHanded,
        ReadDisplayColor(mesh.GetPrim()), &importedMesh->indices, &importedMesh->submeshes);

    importedMesh->visible = UsdGeomImageable(mesh.GetPrim()).ComputeVisibility() == UsdGeomTokens->invisible ? 0 : 1;
    return !importedMesh->indices.empty();
}

// Value identity of an export material. Built field by field: the fixed-size path buffers
// carry whatever follows their terminator, so comparing raw bytes would split equal materials.
std::string ExportMaterialKey(const RUsdMaterial& m)
{
    std::ostringstream key;
    key << std::hexfloat << m.r << ',' << m.g << ',' << m.b << ',' << m.a << ',' << m.metallic << ','
        << m.roughness << ',' << m.uvScale[0] << ',' << m.uvScale[1] << ',' << m.uvOffset[0] << ','
        << m.uvOffset[1] << ',' << m.emissive[0] << ',' << m.emissive[1] << ',' << m.emissive[2];
    for (const char* path :
         {m.albedoTexturePath, m.normalTexturePath, m.metallicTexturePath, m.emissiveTexturePath})
    {
        key << '|' << std::string(path, strnlen(path, sizeof(m.albedoTexturePath)));
    }

    return key.str();
}

UsdShadeMaterial CreateMaterial(
    RUsdContext* context,
    const RUsdMaterial* material)
{
    const RUsdMaterial fallback = {1.0f, 1.0f, 1.0f, 1.0f, 0.0f, 0.5f};
    const RUsdMaterial& source = material != nullptr ? *material : fallback;

    // An assembly of a few hundred parts typically uses a dozen distinct materials; write each
    // once and bind it everywhere instead of one Material prim per mesh.
    const std::string materialKey = ExportMaterialKey(source);
    auto existing = context->exportedMaterials.find(materialKey);
    if (existing != context->exportedMaterials.end())
    {
        return existing->second;
    }

    SdfPath looksPath = context->materialScopePath.IsEmpty()
        ? context->rootPath.AppendChild(TfToken("Looks"))
        : context->materialScopePath;
    if (!context->stage->GetPrimAtPath(looksPath))
    {
        UsdGeomXform::Define(context->stage, looksPath);
    }

    std::ostringstream materialName;
    materialName << "Material_" << context->materialIndex++;

    SdfPath materialPath = looksPath.AppendChild(TfToken(materialName.str()));
    UsdShadeMaterial usdMaterial = UsdShadeMaterial::Define(context->stage, materialPath);
    UsdShadeShader shader = UsdShadeShader::Define(
        context->stage,
        materialPath.AppendChild(TfToken("PreviewSurface")));

    shader.CreateIdAttr().Set(TfToken("UsdPreviewSurface"));

    const bool hasAlbedo = source.albedoTexturePath[0] != '\0';
    const bool hasNormal = source.normalTexturePath[0] != '\0';
    const bool hasMetallic = source.metallicTexturePath[0] != '\0';
    const bool hasEmissiveTex = source.emissiveTexturePath[0] != '\0';

    // Create the shared st (UV) reader as soon as there is at least one texture.
    UsdShadeOutput stOutput;
    if (hasAlbedo || hasNormal || hasMetallic || hasEmissiveTex)
    {
        UsdShadeShader stReader = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("stReader")));
        stReader.CreateIdAttr().Set(TfToken("UsdPrimvarReader_float2"));
        stReader.CreateInput(TfToken("varname"), SdfValueTypeNames->Token).Set(TfToken("st"));
        stOutput = stReader.CreateOutput(TfToken("result"), SdfValueTypeNames->Float2);

        // When the UV tiling/offset is not (1,1,0,0), route st through a UsdTransform2d.
        bool hasUvTransform = source.uvScale[0] != 1.0f || source.uvScale[1] != 1.0f ||
                              source.uvOffset[0] != 0.0f || source.uvOffset[1] != 0.0f;
        if (hasUvTransform)
        {
            UsdShadeShader stTransform = UsdShadeShader::Define(
                context->stage, materialPath.AppendChild(TfToken("stTransform")));
            stTransform.CreateIdAttr().Set(TfToken("UsdTransform2d"));
            stTransform.CreateInput(TfToken("in"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
            stTransform.CreateInput(TfToken("scale"), SdfValueTypeNames->Float2)
                .Set(GfVec2f(source.uvScale[0], source.uvScale[1]));
            stTransform.CreateInput(TfToken("translation"), SdfValueTypeNames->Float2)
                .Set(GfVec2f(source.uvOffset[0], source.uvOffset[1]));
            stOutput = stTransform.CreateOutput(TfToken("result"), SdfValueTypeNames->Float2);
        }
    }

    // albedo → diffuseColor
    if (hasAlbedo)
    {
        UsdShadeShader tex = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("diffuseTexture")));
        tex.CreateIdAttr().Set(TfToken("UsdUVTexture"));
        tex.CreateInput(TfToken("file"), SdfValueTypeNames->Asset).Set(SdfAssetPath(source.albedoTexturePath));
        tex.CreateInput(TfToken("st"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
        tex.CreateInput(TfToken("wrapS"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("wrapT"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("sourceColorSpace"), SdfValueTypeNames->Token).Set(TfToken("sRGB"));
        UsdShadeOutput rgb = tex.CreateOutput(TfToken("rgb"), SdfValueTypeNames->Float3);
        shader.CreateInput(TfToken("diffuseColor"), SdfValueTypeNames->Color3f).ConnectToSource(rgb);
    }
    else
    {
        shader.CreateInput(TfToken("diffuseColor"), SdfValueTypeNames->Color3f)
            .Set(GfVec3f(source.r, source.g, source.b));
    }

    // normal map → inputs:normal (raw, [0,1] → [-1,1])
    if (hasNormal)
    {
        UsdShadeShader tex = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("normalTexture")));
        tex.CreateIdAttr().Set(TfToken("UsdUVTexture"));
        tex.CreateInput(TfToken("file"), SdfValueTypeNames->Asset).Set(SdfAssetPath(source.normalTexturePath));
        tex.CreateInput(TfToken("st"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
        tex.CreateInput(TfToken("wrapS"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("wrapT"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("sourceColorSpace"), SdfValueTypeNames->Token).Set(TfToken("raw"));
        tex.CreateInput(TfToken("scale"), SdfValueTypeNames->Float4).Set(GfVec4f(2.0f, 2.0f, 2.0f, 2.0f));
        tex.CreateInput(TfToken("bias"), SdfValueTypeNames->Float4).Set(GfVec4f(-1.0f, -1.0f, -1.0f, -1.0f));
        UsdShadeOutput rgb = tex.CreateOutput(TfToken("rgb"), SdfValueTypeNames->Float3);
        shader.CreateInput(TfToken("normal"), SdfValueTypeNames->Normal3f).ConnectToSource(rgb);
    }

    // Packed metallic+smoothness -> metallic (.r), roughness (1 - .a)
    if (hasMetallic)
    {
        UsdShadeShader mTex = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("metallicTexture")));
        mTex.CreateIdAttr().Set(TfToken("UsdUVTexture"));
        mTex.CreateInput(TfToken("file"), SdfValueTypeNames->Asset).Set(SdfAssetPath(source.metallicTexturePath));
        mTex.CreateInput(TfToken("st"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
        mTex.CreateInput(TfToken("sourceColorSpace"), SdfValueTypeNames->Token).Set(TfToken("raw"));
        UsdShadeOutput mR = mTex.CreateOutput(TfToken("r"), SdfValueTypeNames->Float);
        shader.CreateInput(TfToken("metallic"), SdfValueTypeNames->Float).ConnectToSource(mR);

        // roughness = 1 - smoothness(.a): scale a=-1, bias a=1
        UsdShadeShader rTex = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("roughnessTexture")));
        rTex.CreateIdAttr().Set(TfToken("UsdUVTexture"));
        rTex.CreateInput(TfToken("file"), SdfValueTypeNames->Asset).Set(SdfAssetPath(source.metallicTexturePath));
        rTex.CreateInput(TfToken("st"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
        rTex.CreateInput(TfToken("sourceColorSpace"), SdfValueTypeNames->Token).Set(TfToken("raw"));
        rTex.CreateInput(TfToken("scale"), SdfValueTypeNames->Float4).Set(GfVec4f(1.0f, 1.0f, 1.0f, -1.0f));
        rTex.CreateInput(TfToken("bias"), SdfValueTypeNames->Float4).Set(GfVec4f(0.0f, 0.0f, 0.0f, 1.0f));
        UsdShadeOutput rA = rTex.CreateOutput(TfToken("a"), SdfValueTypeNames->Float);
        shader.CreateInput(TfToken("roughness"), SdfValueTypeNames->Float).ConnectToSource(rA);
    }
    else
    {
        shader.CreateInput(TfToken("metallic"), SdfValueTypeNames->Float).Set(source.metallic);
        shader.CreateInput(TfToken("roughness"), SdfValueTypeNames->Float).Set(source.roughness);
    }

    shader.CreateInput(TfToken("opacity"), SdfValueTypeNames->Float).Set(source.a);

    // emissiveColor: a UsdUVTexture when a texture is present, otherwise a flat colour.
    bool hasEmissiveColor = source.emissive[0] != 0.0f || source.emissive[1] != 0.0f || source.emissive[2] != 0.0f;
    if (hasEmissiveTex)
    {
        UsdShadeShader tex = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken("emissiveTexture")));
        tex.CreateIdAttr().Set(TfToken("UsdUVTexture"));
        tex.CreateInput(TfToken("file"), SdfValueTypeNames->Asset).Set(SdfAssetPath(source.emissiveTexturePath));
        tex.CreateInput(TfToken("st"), SdfValueTypeNames->Float2).ConnectToSource(stOutput);
        tex.CreateInput(TfToken("wrapS"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("wrapT"), SdfValueTypeNames->Token).Set(TfToken("repeat"));
        tex.CreateInput(TfToken("sourceColorSpace"), SdfValueTypeNames->Token).Set(TfToken("sRGB"));
        UsdShadeOutput rgb = tex.CreateOutput(TfToken("rgb"), SdfValueTypeNames->Float3);
        shader.CreateInput(TfToken("emissiveColor"), SdfValueTypeNames->Color3f).ConnectToSource(rgb);
    }
    else if (hasEmissiveColor)
    {
        shader.CreateInput(TfToken("emissiveColor"), SdfValueTypeNames->Color3f)
            .Set(GfVec3f(source.emissive[0], source.emissive[1], source.emissive[2]));
    }

    UsdShadeOutput shaderSurfaceOutput = shader.CreateOutput(
        TfToken("surface"),
        SdfValueTypeNames->Token);
    usdMaterial.CreateSurfaceOutput().ConnectToSource(shaderSurfaceOutput);

    context->exportedMaterials.emplace(materialKey, usdMaterial);
    return usdMaterial;
}

std::vector<UsdShadeMaterial> CreateMaterials(
    RUsdContext* context,
    const RUsdMaterial* materials,
    int materialCount)
{
    std::vector<UsdShadeMaterial> result;
    if (materials == nullptr || materialCount <= 0)
    {
        result.push_back(CreateMaterial(context, nullptr));
        return result;
    }

    result.reserve(static_cast<size_t>(materialCount));
    for (int i = 0; i < materialCount; ++i)
    {
        result.push_back(CreateMaterial(context, &materials[i]));
    }

    return result;
}

VtIntArray CreateFaceIndices(int indexStart, int indexCount)
{
    VtIntArray faces;
    int faceStart = indexStart / 3;
    int faceCount = indexCount / 3;
    faces.resize(static_cast<size_t>(faceCount));
    for (int i = 0; i < faceCount; ++i)
    {
        faces[static_cast<size_t>(i)] = faceStart + i;
    }

    return faces;
}

void BindMaterials(
    RUsdContext* context,
    const UsdGeomMesh& mesh,
    const RUsdSubmesh* submeshes,
    int submeshCount,
    const RUsdMaterial* materials,
    int materialCount)
{
    std::vector<UsdShadeMaterial> usdMaterials = CreateMaterials(context, materials, materialCount);
    UsdShadeMaterialBindingAPI meshBinding = UsdShadeMaterialBindingAPI::Apply(mesh.GetPrim());

    if (submeshes == nullptr || submeshCount <= 1)
    {
        int materialIndex = submeshes != nullptr && submeshCount == 1 ? submeshes[0].materialIndex : 0;
        materialIndex = std::max(0, std::min(materialIndex, static_cast<int>(usdMaterials.size()) - 1));
        meshBinding.Bind(usdMaterials[static_cast<size_t>(materialIndex)]);
        return;
    }

    meshBinding.SetMaterialBindSubsetsFamilyType(UsdGeomTokens->partition);
    for (int i = 0; i < submeshCount; ++i)
    {
        const RUsdSubmesh& submesh = submeshes[i];
        if (submesh.indexCount <= 0)
        {
            continue;
        }

        int materialIndex = std::max(0, std::min(submesh.materialIndex, static_cast<int>(usdMaterials.size()) - 1));
        std::ostringstream subsetName;
        subsetName << "Material_" << materialIndex << "_Faces_" << i;
        UsdGeomSubset subset = meshBinding.CreateMaterialBindSubset(
            TfToken(subsetName.str()),
            CreateFaceIndices(submesh.indexStart, submesh.indexCount));
        UsdShadeMaterialBindingAPI::Apply(subset.GetPrim()).Bind(usdMaterials[static_cast<size_t>(materialIndex)]);
    }
}
}

// Mirrors the bound material's base colour into primvars:displayColor (and displayOpacity when
// translucent), which viewers and renderers that do not evaluate UsdPreviewSurface show instead
// of grey. A mesh with several materials gets per-face (uniform) colours from its subsets.
void WriteDisplayColor(
    const UsdGeomMesh& mesh,
    const RUsdSubmesh* submeshes,
    int submeshCount,
    const RUsdMaterial* materials,
    int materialCount)
{
    if (materials == nullptr || materialCount <= 0)
    {
        return;
    }

    auto clampIndex = [materialCount](int index)
    {
        return static_cast<size_t>(std::max(0, std::min(index, materialCount - 1)));
    };

    VtArray<GfVec3f> colors;
    VtArray<float> opacities;
    TfToken interpolation = UsdGeomTokens->constant;
    bool translucent = false;

    if (submeshes == nullptr || submeshCount <= 1)
    {
        const RUsdMaterial& m =
            materials[clampIndex(submeshes != nullptr && submeshCount == 1 ? submeshes[0].materialIndex : 0)];
        colors.push_back(GfVec3f(m.r, m.g, m.b));
        opacities.push_back(m.a);
        translucent = m.a < 1.0f;
    }
    else
    {
        // Submeshes are contiguous triangle ranges, and the exported faces are those triangles.
        interpolation = UsdGeomTokens->uniform;
        for (int i = 0; i < submeshCount; ++i)
        {
            const RUsdMaterial& m = materials[clampIndex(submeshes[i].materialIndex)];
            const int faceStart = submeshes[i].indexStart / 3;
            const int faceCount = std::max(0, submeshes[i].indexCount / 3);
            if (colors.size() < static_cast<size_t>(faceStart + faceCount))
            {
                colors.resize(static_cast<size_t>(faceStart + faceCount), GfVec3f(1.0f));
                opacities.resize(static_cast<size_t>(faceStart + faceCount), 1.0f);
            }

            for (int f = faceStart; f < faceStart + faceCount; ++f)
            {
                colors[static_cast<size_t>(f)] = GfVec3f(m.r, m.g, m.b);
                opacities[static_cast<size_t>(f)] = m.a;
            }

            translucent = translucent || m.a < 1.0f;
        }
    }

    mesh.CreateDisplayColorPrimvar(interpolation).Set(colors);
    if (translucent)
    {
        mesh.CreateDisplayOpacityPrimvar(interpolation).Set(opacities);
    }
}

// Composition flags for an imported node (RUSD_NODE_*). Instance proxies carry the arcs of
// their prototype, so the arc bits come from the prim itself only when it is not a proxy.
static int ComputeCompositionFlags(const UsdPrim& prim)
{
    int flags = 0;
    if (prim.IsInstance())
    {
        flags |= RUSD_NODE_INSTANCE;
    }

    if (prim.IsInstanceProxy())
    {
        flags |= RUSD_NODE_INSTANCE_PROXY;
        return flags;
    }

    if (prim.HasAuthoredPayloads())
    {
        flags |= RUSD_NODE_PAYLOAD;
        if (!prim.IsLoaded())
        {
            flags |= RUSD_NODE_UNLOADED;
        }
    }

    if (prim.HasAuthoredReferences())
    {
        flags |= RUSD_NODE_REFERENCES;
    }

    if (prim.HasVariantSets())
    {
        flags |= RUSD_NODE_VARIANT_SETS;
    }

    if (prim.HasAuthoredInherits() || prim.HasAuthoredSpecializes())
    {
        flags |= RUSD_NODE_INHERITS;
    }

    return flags;
}

// A typeless prim that only organises shading -- inside a material or node graph, or holding
// nothing but materials, as usdMtlx's own `def "Materials"` does -- would become an empty
// GameObject next to the scene, so it is not a node.
static bool IsShadingOnlyPrim(const UsdPrim& prim)
{
    for (UsdPrim parent = prim.GetParent(); parent && !parent.IsPseudoRoot(); parent = parent.GetParent())
    {
        if (parent.IsA<UsdShadeNodeGraph>())
        {
            return true;
        }
    }

    // Holding only things that are not drawn: materials, or render settings -- Isaac Sim adds a
    // typeless /Render with RenderSettings, RenderProduct and RenderVar to every stage it saves,
    // which would otherwise arrive as an empty GameObject on every stage-3 round trip. A
    // typeless prim with anything drawable below it is kept, and so is one holding only typeless
    // prims (ALab's light-rig holders, whose content arrives by reference).
    bool hasUndrawnTyped = false;
    for (const UsdPrim& descendant : prim.GetAllDescendants())
    {
        if (descendant.IsA<UsdGeomImageable>())
        {
            return false;
        }

        hasUndrawnTyped = hasUndrawnTyped || descendant.IsA<UsdTyped>();
    }

    return hasUndrawnTyped;
}

// ---- Implicit surfaces (Cube, Sphere, Cylinder, Cone, Capsule, Plane) -------------------
// These are Gprims, not Meshes. They passed the Xformable test and became nodes, then failed
// the Mesh test, so they arrived as empty GameObjects -- Isaac's collision shapes and simple
// props among them, and they still looked present in the hierarchy. They are tessellated here
// from their schema parameters into the same buffers a Mesh fills.

struct ProceduralMesh
{
    std::vector<GfVec3f> points;
    std::vector<GfVec3f> normals;
    std::vector<GfVec2f> uvs;
    VtIntArray faceCounts;
    std::vector<int> corners;

    int Add(const GfVec3f& point, const GfVec3f& normal, const GfVec2f& uv)
    {
        points.push_back(point);
        normals.push_back(normal);
        uvs.push_back(uv);
        return static_cast<int>(points.size()) - 1;
    }

    // Corners counter-clockwise seen from the side the normals face (USD rightHanded).
    void Face(std::initializer_list<int> vertices)
    {
        faceCounts.push_back(static_cast<int>(vertices.size()));
        corners.insert(corners.end(), vertices.begin(), vertices.end());
    }
};

constexpr float kProceduralPi = 3.14159265358979323846f;
constexpr int kProceduralSlices = 32;
constexpr int kProceduralStacks = 16;

void AddCube(ProceduralMesh* m, double size)
{
    const float h = static_cast<float>(size) * 0.5f;
    // Face normal, then two in-plane axes with u x v == normal.
    const GfVec3f faces[6][3] = {
        {GfVec3f(1, 0, 0), GfVec3f(0, 1, 0), GfVec3f(0, 0, 1)},
        {GfVec3f(-1, 0, 0), GfVec3f(0, 0, 1), GfVec3f(0, 1, 0)},
        {GfVec3f(0, 1, 0), GfVec3f(0, 0, 1), GfVec3f(1, 0, 0)},
        {GfVec3f(0, -1, 0), GfVec3f(1, 0, 0), GfVec3f(0, 0, 1)},
        {GfVec3f(0, 0, 1), GfVec3f(1, 0, 0), GfVec3f(0, 1, 0)},
        {GfVec3f(0, 0, -1), GfVec3f(0, 1, 0), GfVec3f(1, 0, 0)},
    };
    for (const auto& face : faces)
    {
        const GfVec3f& n = face[0];
        const GfVec3f& u = face[1];
        const GfVec3f& v = face[2];
        const int a = m->Add((n - u - v) * h, n, GfVec2f(0, 0));
        const int b = m->Add((n + u - v) * h, n, GfVec2f(1, 0));
        const int c = m->Add((n + u + v) * h, n, GfVec2f(1, 1));
        const int d = m->Add((n - u + v) * h, n, GfVec2f(0, 1));
        m->Face({a, b, c, d});
    }
}

// Rows of a surface of revolution about +Z, joined into quads. Each row is (radius, z, normal
// radial part, normal z part, v); consecutive rows are stitched, top row first.
void AddRevolution(ProceduralMesh* m, const std::vector<std::array<float, 5>>& rows)
{
    const int slices = kProceduralSlices;
    std::vector<int> previous;
    for (size_t r = 0; r < rows.size(); ++r)
    {
        const auto& row = rows[r];
        std::vector<int> current;
        for (int j = 0; j <= slices; ++j)
        {
            const float phi = 2.0f * kProceduralPi * static_cast<float>(j) / slices;
            const float c = std::cos(phi), s = std::sin(phi);
            GfVec3f normal(c * row[2], s * row[2], row[3]);
            const float length = normal.GetLength();
            normal = length > 0.0f ? normal / length : GfVec3f(0, 0, row[3] >= 0 ? 1.0f : -1.0f);
            current.push_back(m->Add(GfVec3f(c * row[0], s * row[0], row[1]), normal,
                                     GfVec2f(static_cast<float>(j) / slices, row[4])));
        }

        if (!previous.empty())
        {
            for (int j = 0; j < slices; ++j)
            {
                // previous is above current: going down then around faces outward.
                m->Face({previous[j], current[j], current[j + 1], previous[j + 1]});
            }
        }

        previous = std::move(current);
    }
}

void AddDisk(ProceduralMesh* m, float radius, float z, bool up)
{
    const GfVec3f normal(0, 0, up ? 1.0f : -1.0f);
    const int center = m->Add(GfVec3f(0, 0, z), normal, GfVec2f(0.5f, 0.5f));
    std::vector<int> rim;
    for (int j = 0; j <= kProceduralSlices; ++j)
    {
        const float phi = 2.0f * kProceduralPi * static_cast<float>(j) / kProceduralSlices;
        const float c = std::cos(phi), s = std::sin(phi);
        rim.push_back(m->Add(GfVec3f(c * radius, s * radius, z), normal, GfVec2f(0.5f + 0.5f * c, 0.5f + 0.5f * s)));
    }

    for (int j = 0; j < kProceduralSlices; ++j)
    {
        if (up)
        {
            m->Face({center, rim[j], rim[j + 1]});
        }
        else
        {
            m->Face({center, rim[j + 1], rim[j]});
        }
    }
}

// Sphere-like rows: a hemisphere of radius r centred at +offset, a band, and one at -offset.
std::vector<std::array<float, 5>> CapsuleRows(float radius, float halfHeight)
{
    std::vector<std::array<float, 5>> rows;
    const int stacks = kProceduralStacks;
    for (int i = 0; i <= stacks; ++i)
    {
        const float theta = kProceduralPi * static_cast<float>(i) / stacks;
        const float s = std::sin(theta), c = std::cos(theta);
        const float v = 1.0f - static_cast<float>(i) / stacks;
        if (i * 2 == stacks && halfHeight > 0.0f)
        {
            rows.push_back({radius * s, radius * c + halfHeight, s, c, v});
            rows.push_back({radius * s, radius * c - halfHeight, s, c, v});
            continue;
        }

        rows.push_back({radius * s, radius * c + (i * 2 < stacks ? halfHeight : -halfHeight), s, c, v});
    }

    return rows;
}

// Builds the canonical +Z-axis shape, then turns it onto the authored axis. Both turns are cyclic
// permutations of the coordinates, which are rotations, so the winding survives.
bool GenerateImplicitSurface(const UsdPrim& prim, ProceduralMesh* m)
{
    TfToken axis = UsdGeomTokens->z;
    if (prim.IsA<UsdGeomCube>())
    {
        double size = 2.0;
        UsdGeomCube(prim).GetSizeAttr().Get(&size);
        AddCube(m, size);
    }
    else if (prim.IsA<UsdGeomSphere>())
    {
        double radius = 1.0;
        UsdGeomSphere(prim).GetRadiusAttr().Get(&radius);
        AddRevolution(m, CapsuleRows(static_cast<float>(radius), 0.0f));
    }
    else if (prim.IsA<UsdGeomCylinder>())
    {
        UsdGeomCylinder cylinder(prim);
        double radius = 1.0, height = 2.0;
        cylinder.GetRadiusAttr().Get(&radius);
        cylinder.GetHeightAttr().Get(&height);
        cylinder.GetAxisAttr().Get(&axis);
        const float r = static_cast<float>(radius), h = static_cast<float>(height) * 0.5f;
        AddRevolution(m, {{r, h, 1, 0, 1}, {r, -h, 1, 0, 0}});
        AddDisk(m, r, h, true);
        AddDisk(m, r, -h, false);
    }
    else if (prim.IsA<UsdGeomCone>())
    {
        UsdGeomCone cone(prim);
        double radius = 1.0, height = 2.0;
        cone.GetRadiusAttr().Get(&radius);
        cone.GetHeightAttr().Get(&height);
        cone.GetAxisAttr().Get(&axis);
        const float r = static_cast<float>(radius), h = static_cast<float>(height);
        // Side normal leans up by the slope: radial h, axial r.
        AddRevolution(m, {{0.0f, h * 0.5f, h, r, 1}, {r, -h * 0.5f, h, r, 0}});
        AddDisk(m, r, -h * 0.5f, false);
    }
    else if (prim.IsA<UsdGeomCapsule>())
    {
        UsdGeomCapsule capsule(prim);
        double radius = 0.5, height = 1.0;
        capsule.GetRadiusAttr().Get(&radius);
        capsule.GetHeightAttr().Get(&height);
        capsule.GetAxisAttr().Get(&axis);
        AddRevolution(m, CapsuleRows(static_cast<float>(radius), static_cast<float>(height) * 0.5f));
    }
    else if (prim.IsA<UsdGeomPlane>())
    {
        UsdGeomPlane plane(prim);
        double width = 2.0, length = 2.0;
        plane.GetWidthAttr().Get(&width);
        plane.GetLengthAttr().Get(&length);
        plane.GetAxisAttr().Get(&axis);
        const float w = static_cast<float>(width) * 0.5f, l = static_cast<float>(length) * 0.5f;
        const GfVec3f n(0, 0, 1);
        const int a = m->Add(GfVec3f(-w, -l, 0), n, GfVec2f(0, 0));
        const int b = m->Add(GfVec3f(w, -l, 0), n, GfVec2f(1, 0));
        const int c = m->Add(GfVec3f(w, l, 0), n, GfVec2f(1, 1));
        const int d = m->Add(GfVec3f(-w, l, 0), n, GfVec2f(0, 1));
        m->Face({a, b, c, d});
    }
    else
    {
        return false;
    }

    if (axis == UsdGeomTokens->x || axis == UsdGeomTokens->y)
    {
        const bool toX = axis == UsdGeomTokens->x;
        auto turn = [toX](GfVec3f* v) {
            const GfVec3f p = *v;
            *v = toX ? GfVec3f(p[2], p[0], p[1]) : GfVec3f(p[1], p[2], p[0]);
        };
        for (GfVec3f& p : m->points)
        {
            turn(&p);
        }

        for (GfVec3f& n : m->normals)
        {
            turn(&n);
        }
    }

    return true;
}

bool BuildImportedImplicitSurface(RUsdContext* context, const UsdPrim& prim, RUsdContext::ImportedMesh* out)
{
    ProceduralMesh m;
    if (!GenerateImplicitSurface(prim, &m))
    {
        return false;
    }

    out->primPath = prim.GetPath().GetString();
    out->name = prim.GetName().GetString();
    for (size_t i = 0; i < m.points.size(); ++i)
    {
        out->points.push_back(ToUnityVector(m.points[i]));
        out->normals.push_back(ToUnityVector(m.normals[i]));
        out->uvSets[0].push_back({m.uvs[i][0], m.uvs[i][1]});
    }

    out->materialIndex = BuildImportedSubmeshes(
        context, prim, m.faceCounts, m.corners.data(), static_cast<int>(m.corners.size()),
        /*leftHanded=*/false,   // built here, always in USD's default winding
        ReadDisplayColor(prim), &out->indices, &out->submeshes);
    out->visible = UsdGeomImageable(prim).ComputeVisibility() == UsdGeomTokens->invisible ? 0 : 1;
    return !out->indices.empty();
}

// ---- Stage walk ---------------------------------------------------------------------------

struct ImportWalk
{
    ImportWalk(RUsdContext* c, const Usd_PrimFlagsPredicate& p) : context(c), predicate(p) {}

    RUsdContext* context;
    Usd_PrimFlagsPredicate predicate;
    UsdGeomXformCache xformCache;
    // Prototype geometry under a PointInstancer is built once and copied per instance.
    std::unordered_map<std::string, int> builtMeshes;
    // Point instancers whose prototypes are being imported right now, innermost last. A prototype
    // that contains one of them would import it again, and it again, without end.
    std::vector<SdfPath> expandingInstancers;
    size_t geometryElements = 0;
    size_t instancePlacements = 0;
};

[[noreturn]] void ThrowImportBudget(const char* what, size_t limit)
{
    throw std::runtime_error(std::string("the stage exceeds the import limit of ") +
        std::to_string(limit) + " " + what + "; it was not imported");
}

void CountGeometry(ImportWalk* walk, const RUsdContext::ImportedMesh& mesh)
{
    if (walk->context->importedMeshes.size() >= kMaxImportMeshes)
    {
        ThrowImportBudget("meshes", kMaxImportMeshes);
    }

    const size_t elements = mesh.points.size() + mesh.indices.size();
    if (elements > kMaxImportGeometryElements - walk->geometryElements)
    {
        ThrowImportBudget("points and triangle indices", kMaxImportGeometryElements);
    }

    walk->geometryElements += elements;
}

// `inheritedPurpose` and `parentVisible` carry down what purpose and visibility resolve to above
// this prim. Both attributes inherit, and computing them from the prim alone walks back up the
// ancestor chain on every call -- which the recursion has already walked on the way down.
// UsdImaging keeps the same two as memoised caches for the same reason.
void ImportSubtree(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, int depth,
                   const UsdGeomImageable::PurposeInfo& inheritedPurpose, int parentVisible);

void EmitNode(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, const GfMatrix4d& local, int visible)
{
    RUsdContext::ImportedNode node;
    node.path = outPath;
    node.localMatrix = FromGfMatrix(local);
    node.visible = visible;
    // A copy inside a point-instancer instance is not that prim; it carries no composition of
    // its own to report.
    node.compositionFlags = outPath == prim.GetPath().GetString() ? ComputeCompositionFlags(prim) : 0;
    if (walk->context->importedNodes.size() >= kMaxImportNodes)
    {
        ThrowImportBudget("nodes", kMaxImportNodes);
    }

    walk->context->importedNodes.push_back(node);
}

// Geometry for one prim (Mesh or implicit surface), built once per source prim and copied for
// every point-instancer instance that reuses it.
void EmitGeometry(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath)
{
    RUsdContext* context = walk->context;
    const std::string sourcePath = prim.GetPath().GetString();
    auto built = walk->builtMeshes.find(sourcePath);
    if (built != walk->builtMeshes.end())
    {
        if (built->second >= 0)
        {
            CountGeometry(walk, context->importedMeshes[static_cast<size_t>(built->second)]);
            RUsdContext::ImportedMesh copy = context->importedMeshes[static_cast<size_t>(built->second)];
            copy.primPath = outPath;
            copy.sourcePath = sourcePath;
            context->importedMeshes.push_back(std::move(copy));
        }

        return;
    }

    RUsdContext::ImportedMesh mesh;
    bool ok = false;
    if (prim.IsA<UsdGeomMesh>())
    {
        ok = BuildImportedMesh(context, UsdGeomMesh(prim), &walk->xformCache, &mesh);
    }
    else if (prim.IsA<UsdGeomGprim>())
    {
        ok = BuildImportedImplicitSurface(context, prim, &mesh);
    }
    else
    {
        return;
    }

    if (!ok)
    {
        walk->builtMeshes[sourcePath] = -1;
        return;
    }

    CountGeometry(walk, mesh);
    mesh.primPath = outPath;
    mesh.sourcePath = sourcePath;
    walk->builtMeshes[sourcePath] = static_cast<int>(context->importedMeshes.size());
    context->importedMeshes.push_back(std::move(mesh));
}

// A PointInstancer draws its prototypes only through its instances: the prototype prims (usually
// its own children) are never drawn where they stand. Both halves were wrong. The instances were
// not expanded, so OpenChessSet lost its 16 pawns and Moore Lane its forest, and the prototypes
// were imported in place, which in AGV Fleet left 635 meshes overlapping their instances.
// Each prototype is imported once and hidden, and its meshes are recorded with one matrix per
// instance for the host to draw instanced (API v8). Expanding into a prim per instance made
// Moore Lane's 229,717 instances 229,717 nodes and mesh entries, which the editor cannot hold.
void ExpandPointInstancer(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, int depth)
{
    UsdGeomPointInstancer instancer(prim);
    SdfPathVector prototypes;
    instancer.GetPrototypesRel().GetForwardedTargets(&prototypes);
    VtIntArray protoIndices;

    // The prototype root's own transform has to come from somewhere: ComputeRelativeTransform
    // below leaves it out (it concatenates the transforms *beneath* the ancestor), so it is
    // included here, as USD does by default. Excluding it on both sides dropped the root's
    // placement from every instance (a prototype authored at y = 0.5 drew at y = 0).
    UsdTimeCode time = UsdTimeCode::Default();
    VtArray<GfMatrix4d> transforms;
    if (!instancer.GetProtoIndicesAttr().Get(&protoIndices, time) ||
        !instancer.ComputeInstanceTransformsAtTime(&transforms, time, time,
            UsdGeomPointInstancer::IncludeProtoXform, UsdGeomPointInstancer::IgnoreMask))
    {
        // Animated instancers author only time samples; use the first one.
        time = UsdTimeCode::EarliestTime();
        transforms.clear();
        if (!instancer.GetProtoIndicesAttr().Get(&protoIndices, time) ||
            !instancer.ComputeInstanceTransformsAtTime(&transforms, time, time,
                UsdGeomPointInstancer::IncludeProtoXform, UsdGeomPointInstancer::IgnoreMask))
        {
            AppendImportWarning(walk->context,
                "point instancer '" + prim.GetPath().GetString() +
                "' has no readable instances; it imports empty.");
            return;
        }
    }

    const std::vector<bool> mask = instancer.ComputeMaskAtTime(time);
    const int visible = UsdGeomImageable(prim).ComputeVisibility() == UsdGeomTokens->invisible ? 0 : 1;
    const size_t count = std::min(transforms.size(), protoIndices.size());

    // Which instances belong to each prototype. Gathered first so every prototype is imported
    // once and its meshes get one matrix array covering all their instances.
    std::vector<std::vector<size_t>> byPrototype(prototypes.size());
    for (size_t i = 0; i < count; ++i)
    {
        if (!mask.empty() && (i >= mask.size() || !mask[i]))
        {
            continue;   // invisibleIds
        }

        const int index = protoIndices[i];
        if (index < 0 || static_cast<size_t>(index) >= prototypes.size())
        {
            continue;
        }

        byPrototype[static_cast<size_t>(index)].push_back(i);
    }

    const int instancerNodeIndex = static_cast<int>(walk->context->importedNodes.size()) - 1;

    for (size_t k = 0; k < prototypes.size(); ++k)
    {
        if (byPrototype[k].empty())
        {
            continue;
        }

        UsdPrim prototype = prim.GetStage()->GetPrimAtPath(prototypes[k]);
        if (!prototype)
        {
            continue;
        }

        // A prototype that is this instancer, one of its ancestors, or contains an instancer
        // already being expanded further up would import itself again. USD renderers refuse the
        // same cycle; it is skipped here with a warning.
        const SdfPath& prototypePath = prototype.GetPath();
        bool cyclic = prim.GetPath().HasPrefix(prototypePath);
        for (const SdfPath& expanding : walk->expandingInstancers)
        {
            cyclic = cyclic || expanding.HasPrefix(prototypePath);
        }

        if (cyclic)
        {
            AppendImportWarning(walk->context,
                "point instancer '" + prim.GetPath().GetString() + "' uses '" +
                prototypePath.GetString() + "' as a prototype, which contains the instancer "
                "itself; that prototype is skipped.");
            continue;
        }

        // Import the prototype once, hidden. Its nodes exist so the host can see what is being
        // instanced and so the meshes have somewhere to live, but nothing is drawn in place --
        // that double-drawing is what left AGV Fleet with 635 meshes on top of their instances.
        const size_t firstMesh = walk->context->importedMeshes.size();
        const std::string protoPath =
            outPath + "/prototype_" + std::to_string(k) + "_" + prototype.GetName().GetString();
        EmitNode(walk, prim, protoPath, GfMatrix4d(1.0), 0);
        walk->expandingInstancers.push_back(prim.GetPath());
        ImportSubtree(walk, prototype, protoPath + "/" + prototype.GetName().GetString(), depth + 1,
                      UsdGeomImageable::PurposeInfo(), 1);
        walk->expandingInstancers.pop_back();

        // Every mesh the prototype contributed, placed for each of its instances. The mesh's own
        // position inside the prototype is folded in here, because the instance transform is
        // relative to the instancer, not to that mesh's parent.
        for (size_t m = firstMesh; m < walk->context->importedMeshes.size(); ++m)
        {
            const RUsdContext::ImportedMesh& mesh = walk->context->importedMeshes[m];
            UsdPrim meshPrim = prim.GetStage()->GetPrimAtPath(SdfPath(mesh.sourcePath));
            GfMatrix4d protoRelative(1.0);
            if (meshPrim)
            {
                bool resets = false;
                protoRelative =
                    walk->xformCache.ComputeRelativeTransform(meshPrim, prototype, &resets);
            }

            const size_t placements = byPrototype[k].size();
            if (placements > kMaxImportInstancePlacements - walk->instancePlacements)
            {
                ThrowImportBudget("point-instancer placements (instances times prototype meshes)",
                    kMaxImportInstancePlacements);
            }

            walk->instancePlacements += placements;
            RUsdContext::ImportedInstancedMesh record;
            record.instancerNodeIndex = instancerNodeIndex;
            record.meshIndex = static_cast<int>(m);
            record.matrices.reserve(byPrototype[k].size() * 16);
            for (size_t i : byPrototype[k])
            {
                const GfMatrix4d placed = protoRelative * transforms[i];
                for (int r = 0; r < 4; ++r)
                {
                    for (int c = 0; c < 4; ++c)
                    {
                        record.matrices.push_back(static_cast<float>(placed[r][c]));
                    }
                }
            }

            walk->context->importedInstancedMeshes.push_back(std::move(record));
        }
    }

    if (visible == 0)
    {
        AppendImportWarning(walk->context,
            "point instancer '" + prim.GetPath().GetString() + "' is invisible; its instances are "
            "imported but not drawn.");
    }
}

// ---- Scene components (API v7) ------------------------------------------------------------
// Lights, cameras and spatial audio became plain nodes with nothing on them: Moore Lane brings
// 104 lights and 21 cameras and none arrived, and Elephant's five SpatialAudio prims were empty
// transforms. They are read here into tables keyed by node index; the host adds the components.

template <typename T>
T GetAttrOr(const UsdAttribute& attribute, T fallback)
{
    T value = fallback;
    if (attribute)
    {
        attribute.Get(&value);
    }
    return value;
}

std::string ReadAssetAttr(RUsdContext* context, const UsdAttribute& attribute)
{
    SdfAssetPath asset;
    char buffer[1024] = {0};
    if (attribute && attribute.Get(&asset) && StoreAssetPath(context, asset, buffer, sizeof(buffer)))
    {
        return buffer;
    }
    return std::string();
}

void EmitSceneComponents(ImportWalk* walk, const UsdPrim& prim, int nodeIndex)
{
    RUsdContext* context = walk->context;

    if (prim.HasAPI<UsdLuxLightAPI>())
    {
        RUsdContext::ImportedLight entry = {};
        RUsdImportLight& light = entry.light;
        light.nodeIndex = nodeIndex;

        UsdLuxLightAPI api(prim);
        const GfVec3f color = GetAttrOr(api.GetColorAttr(), GfVec3f(1.0f));
        GfVec3f tint(1.0f);
        if (GetAttrOr(api.GetEnableColorTemperatureAttr(), false))
        {
            tint = UsdLuxBlackbodyTemperatureAsRgb(GetAttrOr(api.GetColorTemperatureAttr(), 6500.0f));
        }
        light.color[0] = color[0] * tint[0];
        light.color[1] = color[1] * tint[1];
        light.color[2] = color[2] * tint[2];
        light.intensity = GetAttrOr(api.GetIntensityAttr(), 1.0f) *
            std::pow(2.0f, GetAttrOr(api.GetExposureAttr(), 0.0f));
        light.normalize = GetAttrOr(api.GetNormalizeAttr(), false) ? 1 : 0;

        if (prim.IsA<UsdLuxDistantLight>())
        {
            light.type = RUSD_LIGHT_DISTANT;
            light.angle = GetAttrOr(UsdLuxDistantLight(prim).GetAngleAttr(), 0.53f);
        }
        else if (prim.IsA<UsdLuxSphereLight>())
        {
            light.type = RUSD_LIGHT_SPHERE;
            light.radius = GetAttrOr(UsdLuxSphereLight(prim).GetRadiusAttr(), 0.5f);
        }
        else if (prim.IsA<UsdLuxRectLight>())
        {
            UsdLuxRectLight rect(prim);
            light.type = RUSD_LIGHT_RECT;
            light.width = GetAttrOr(rect.GetWidthAttr(), 1.0f);
            light.height = GetAttrOr(rect.GetHeightAttr(), 1.0f);
            entry.texturePath = ReadAssetAttr(context, rect.GetTextureFileAttr());
        }
        else if (prim.IsA<UsdLuxDiskLight>())
        {
            light.type = RUSD_LIGHT_DISK;
            light.radius = GetAttrOr(UsdLuxDiskLight(prim).GetRadiusAttr(), 0.5f);
        }
        else if (prim.IsA<UsdLuxCylinderLight>())
        {
            UsdLuxCylinderLight cylinder(prim);
            light.type = RUSD_LIGHT_CYLINDER;
            light.radius = GetAttrOr(cylinder.GetRadiusAttr(), 0.5f);
            light.length = GetAttrOr(cylinder.GetLengthAttr(), 1.0f);
        }
        else if (prim.IsA<UsdLuxDomeLight>())
        {
            light.type = RUSD_LIGHT_DOME;
            entry.texturePath = ReadAssetAttr(context, UsdLuxDomeLight(prim).GetTextureFileAttr());
        }
        else
        {
            // GeometryLight, PortalLight, PluginLight and the like have no host equivalent.
            AppendImportWarning(context,
                "light '" + prim.GetPath().GetString() + "' is a " + prim.GetTypeName().GetString() +
                ", which is not imported.");
            light.type = 0;
        }

        if (prim.HasAPI<UsdLuxShapingAPI>())
        {
            UsdLuxShapingAPI shaping(prim);
            light.coneAngle = GetAttrOr(shaping.GetShapingConeAngleAttr(), 90.0f);
            light.coneSoftness = GetAttrOr(shaping.GetShapingConeSoftnessAttr(), 0.0f);
            if (light.coneAngle >= 90.0f)
            {
                light.coneAngle = 0.0f;  // a 90 degree cone is the unshaped default
            }
        }

        if (light.type != 0)
        {
            context->importedLights.push_back(entry);
        }
    }
    else if (prim.IsA<UsdGeomCamera>())
    {
        UsdGeomCamera camera(prim);
        RUsdImportCamera entry = {};
        entry.nodeIndex = nodeIndex;
        entry.projection = GetAttrOr(camera.GetProjectionAttr(), UsdGeomTokens->perspective) ==
                UsdGeomTokens->orthographic
            ? RUSD_CAMERA_ORTHOGRAPHIC
            : RUSD_CAMERA_PERSPECTIVE;
        entry.focalLength = GetAttrOr(camera.GetFocalLengthAttr(), 50.0f);
        entry.horizontalAperture = GetAttrOr(camera.GetHorizontalApertureAttr(), 20.955f);
        entry.verticalAperture = GetAttrOr(camera.GetVerticalApertureAttr(), 15.2908f);
        const GfVec2f clipping = GetAttrOr(camera.GetClippingRangeAttr(), GfVec2f(1.0f, 1000000.0f));
        entry.nearClip = clipping[0];
        entry.farClip = clipping[1];
        context->importedCameras.push_back(entry);
    }
    else if (prim.IsA<UsdMediaSpatialAudio>())
    {
        UsdMediaSpatialAudio audio(prim);
        RUsdContext::ImportedAudio entry = {};
        entry.audio.nodeIndex = nodeIndex;
        entry.audio.auralMode =
            GetAttrOr(audio.GetAuralModeAttr(), TfToken("spatial")) == TfToken("nonSpatial")
            ? RUSD_AUDIO_NONSPATIAL
            : RUSD_AUDIO_SPATIAL;
        const std::string playback = GetAttrOr(audio.GetPlaybackModeAttr(), TfToken("onceFromStart")).GetString();
        entry.audio.loop = playback.rfind("loop", 0) == 0 ? 1 : 0;
        entry.audio.gain = static_cast<float>(GetAttrOr(audio.GetGainAttr(), 1.0));
        entry.audio.startTime = GetAttrOr(audio.GetStartTimeAttr(), SdfTimeCode(0.0)).GetValue();
        entry.audio.endTime = GetAttrOr(audio.GetEndTimeAttr(), SdfTimeCode(0.0)).GetValue();
        entry.filePath = ReadAssetAttr(context, audio.GetFilePathAttr());
        context->importedAudio.push_back(entry);
    }
}

// Emits `prim` and everything below it. `outPath` is where it lands in the node and mesh tables:
// its own path, or a path under a point-instancer instance for a prototype copy.
void ImportSubtree(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, int depth,
                   const UsdGeomImageable::PurposeInfo& inheritedPurpose, int parentVisible)
{
    // A cycle of instancers referencing each other's prototypes would not end on its own.
    if (depth > 64)
    {
        return;
    }

    // proxy and guide geometry is for viewports and tools, not the render: ALab carries a proxy
    // copy of most assets and imported 24.6% of its triangles twice. Purpose inherits, so the
    // whole subtree goes.
    UsdGeomImageable::PurposeInfo purposeInfo = inheritedPurpose;
    int visible = parentVisible;
    if (prim.IsA<UsdGeomImageable>())
    {
        const UsdGeomImageable imageable(prim);
        purposeInfo = imageable.ComputePurposeInfo(inheritedPurpose);
        if (purposeInfo.purpose == UsdGeomTokens->proxy || purposeInfo.purpose == UsdGeomTokens->guide)
        {
            return;
        }

        // Visibility inherits the same way: invisible anywhere above wins, and otherwise the
        // prim's own opinion decides.
        if (visible != 0)
        {
            TfToken own;
            if (imageable.GetVisibilityAttr().Get(&own) && own == UsdGeomTokens->invisible)
            {
                visible = 0;
            }
        }
    }

    // Record every transformable prim (Xform, Mesh, ...) so the Unity side can rebuild the
    // hierarchy with per-node local transforms instead of baking.
    if (prim.IsA<UsdGeomXformable>())
    {
        bool resetsXformStack = false;
        GfMatrix4d local = walk->xformCache.GetLocalTransformation(prim, &resetsXformStack);
        EmitNode(walk, prim, outPath, local, visible);
        EmitSceneComponents(walk, prim, static_cast<int>(walk->context->importedNodes.size()) - 1);
    }
    else if (!prim.IsA<UsdTyped>() && !prim.IsPseudoRoot() && !IsShadingOnlyPrim(prim))
    {
        // Typeless prims (`def "rig"`) and prims of a schema this build does not know (a
        // studio's custom types) were dropped, and everything they carried with them: ALab
        // lost 19, among them typeless light-rig prims that exist to hold a reference. They
        // have no transform of their own, so they become identity nodes; a known
        // non-transformable type (Material, Shader, GeomSubset, ...) stays out as before.
        // A typeless prim has no visibility of its own, so it takes what it inherited.
        EmitNode(walk, prim, outPath, GfMatrix4d(1.0), visible);
    }

    // Invisible geometry is not read: Hydra does not populate it, and the importer used to
    // read, split and triangulate it only to mark the renderer disabled -- 1,274 of Factory's
    // 13,843 meshes, 102 of Moore Lane's. The node stays, with its visibility, so the
    // hierarchy is unchanged; only the mesh behind it is absent.
    if (prim.IsLoaded() && visible != 0)
    {
        EmitGeometry(walk, prim, outPath);
    }

    if (prim.IsA<UsdGeomPointInstancer>())
    {
        ExpandPointInstancer(walk, prim, outPath, depth);
        return;
    }

    for (const UsdPrim& child : prim.GetFilteredChildren(walk->predicate))
    {
        ImportSubtree(walk, child, outPath + "/" + child.GetName().GetString(), depth + 1,
                      purposeInfo, visible);
    }
}

// Walks an opened stage into the context's node and mesh tables. Shared by RUsd_OpenStage and
// RUsd_OpenStageEx so both import paths read a stage identically.
static void ReadImportedStage(RUsdContext* context)
{
    UsdPrim defaultPrim = context->stage->GetDefaultPrim();
    context->defaultPrimPath = defaultPrim ? defaultPrim.GetPath().GetString() : std::string();
    context->importMetersPerUnit = UsdGeomGetStageMetersPerUnit(context->stage);
    TfToken upAxis = UsdGeomGetStageUpAxis(context->stage);
    context->importUpAxis = upAxis == UsdGeomTokens->z ? 2 : 1;

    // Instance proxies included: instanceable prims (Omniverse and most CAD exports build
    // assemblies this way) keep all their geometry in prototypes, and the default
    // traversal stops at the instance, which imported such stages as empty.
    // The default predicate minus IsLoaded: with every payload loaded (RUsd_OpenStage) the two
    // are the same, and with payloads left unloaded the payload's own prim still comes through
    // as an empty placeholder node, so the hierarchy keeps its place for it.
    ImportWalk walk(context, UsdTraverseInstanceProxies(UsdPrimIsActive && UsdPrimIsDefined && !UsdPrimIsAbstract));
    for (const UsdPrim& prim : context->stage->GetPseudoRoot().GetFilteredChildren(walk.predicate))
    {
        ImportSubtree(&walk, prim, prim.GetPath().GetString(), 0, UsdGeomImageable::PurposeInfo(), 1);
    }
}

// Splits "a\tb\tc" style records. Empty input yields no records.
static std::vector<std::vector<std::string>> SplitRecords(const std::string& text)
{
    std::vector<std::vector<std::string>> records;
    std::istringstream lines(text);
    std::string line;
    while (std::getline(lines, line))
    {
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }

        if (line.empty())
        {
            continue;
        }

        std::vector<std::string> fields;
        size_t start = 0;
        while (true)
        {
            size_t tab = line.find('\t', start);
            fields.push_back(line.substr(start, tab == std::string::npos ? std::string::npos : tab - start));
            if (tab == std::string::npos)
            {
                break;
            }

            start = tab + 1;
        }

        records.push_back(fields);
    }

    return records;
}

static const char* ArcTypeName(PcpArcType arcType)
{
    switch (arcType)
    {
        case PcpArcTypeReference: return "reference";
        case PcpArcTypePayload: return "payload";
        case PcpArcTypeInherit: return "inherit";
        case PcpArcTypeSpecialize: return "specialize";
        case PcpArcTypeVariant: return "variant";
        case PcpArcTypeRelocate: return "relocate";
        default: return nullptr;
    }
}

// The composition report RUsd_GetImportComposition returns. Built on demand from the still-open
// stage, so an import that never asks for it pays nothing.
static std::string BuildCompositionReport(RUsdContext* context)
{
    std::ostringstream report;
    const auto predicate = UsdPrimIsActive && UsdPrimIsDefined && !UsdPrimIsAbstract;
    for (const UsdPrim& prim : context->stage->Traverse(predicate))
    {
        const std::string path = prim.GetPath().GetString();
        if (prim.HasVariantSets())
        {
            UsdVariantSets variantSets = prim.GetVariantSets();
            for (const std::string& setName : variantSets.GetNames())
            {
                UsdVariantSet variantSet = variantSets.GetVariantSet(setName);
                report << "V\t" << path << '\t' << setName << '\t' << variantSet.GetVariantSelection();
                for (const std::string& variantName : variantSet.GetVariantNames())
                {
                    report << '\t' << variantName;
                }

                report << '\n';
            }
        }

        if (prim.HasAuthoredPayloads())
        {
            report << "P\t" << path << '\t' << (prim.IsLoaded() ? 1 : 0) << '\n';
        }

        if (prim.IsInstance())
        {
            report << "I\t" << path << '\n';
        }

        if (!prim.HasAuthoredReferences() && !prim.HasAuthoredPayloads() &&
            !prim.HasAuthoredInherits() && !prim.HasAuthoredSpecializes())
        {
            continue;
        }

        UsdPrimCompositionQuery::Filter filter;
        filter.dependencyTypeFilter = UsdPrimCompositionQuery::DependencyTypeFilter::Direct;
        UsdPrimCompositionQuery query(prim, filter);
        for (const UsdPrimCompositionQueryArc& arc : query.GetCompositionArcs())
        {
            const char* kind = ArcTypeName(arc.GetArcType());
            if (kind == nullptr || arc.GetArcType() == PcpArcTypeVariant)
            {
                continue;
            }

            const PcpNodeRef target = arc.GetTargetNode();
            std::string layer;
            if (target.GetLayerStack() && target.GetLayerStack()->GetIdentifier().rootLayer)
            {
                layer = target.GetLayerStack()->GetIdentifier().rootLayer->GetIdentifier();
            }

            report << "A\t" << path << '\t' << kind << '\t' << layer << '\t' << target.GetPath().GetString() << '\n';
        }
    }

    return report.str();
}

// Sdf-level authoring for the override calls. They write straight into the root layer, so they
// work the same on prims the layer defines, on prims a sublayer or reference brings in, and on
// prims below an instance (which the Usd API refuses to edit through a proxy).
static SdfPrimSpecHandle GetOrCreateSpec(RUsdContext* context, const SdfPath& path)
{
    return SdfCreatePrimInLayer(context->stage->GetRootLayer(), path);
}

static bool ParseAbsolutePrimPath(RUsdContext* context, const char* primPath, SdfPath* out)
{
    const std::string text = SafeString(primPath);
    if (text.empty() || !SdfPath::IsValidPathString(text))
    {
        SetError(context, "USD prim path '" + text + "' is not a valid path.");
        return false;
    }

    SdfPath path(text);
    if (!path.IsAbsolutePath() || !path.IsPrimPath())
    {
        SetError(context, "USD prim path '" + text + "' must be an absolute prim path.");
        return false;
    }

    *out = path;
    return true;
}

static void SetAttributeDefault(
    const SdfPrimSpecHandle& spec,
    const TfToken& name,
    const SdfValueTypeName& typeName,
    SdfVariability variability,
    const VtValue& value)
{
    SdfAttributeSpecHandle attribute =
        spec->GetLayer()->GetAttributeAtPath(spec->GetPath().AppendProperty(name));
    if (!attribute)
    {
        attribute = SdfAttributeSpec::New(spec, name.GetString(), typeName, variability);
    }

    if (attribute)
    {
        attribute->SetDefaultValue(value);
    }
}

// Every RUsd_* call that edits the export layer shares this shape: validate, run, and turn an
// exception into the context's last error.
template <typename TBody>
static int RunExportEdit(RUsdContext* context, const char* what, TBody body)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    try
    {
        return body();
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError(std::string("Failed to ") + what, exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, std::string("Failed to ") + what + ".");
        return kFailure;
    }
}

// MaterialX's standard library is data, not code, and usdMtlx has to be told where it is.
//
// usdMtlx looks in three places (pxr/usd/usdMtlx/utils.cpp): PXR_MTLX_STDLIB_SEARCH_PATHS, then
// PXR_MATERIALX_STDLIB_DIR baked in at compile time, then the plugin's own "libraries" resource.
// The baked value is the build machine's install prefix -- /opt/usd-26.05/install/libraries on the
// Linux builder -- and ships verbatim inside libusd_ms. It resolves on no other machine. The
// resource fallback does cover us, but only by accident of being last in that list, and a package
// that works because of a fallback is a package nobody can reason about.
//
// So the path is computed here instead, from where this library actually got loaded, and exported
// before anything touches USD. It is an absolute path at run time and relative to the package by
// construction, which is what USD already does for plugins: plugInfo.json carries "Root": ".."
// and PlugRegistry resolves it against the plugin's own file.
//
// Kept generic on purpose. The next bundled data directory gets to call PackageRootDir() and skip
// this whole discussion.
namespace
{

std::string PackageRootDir()
{
#if defined(_WIN32)
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&PackageRootDir), &self))
    {
        return std::string();
    }

    wchar_t wide[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameW(self, wide, MAX_PATH);
    if (length == 0 || length == MAX_PATH)
    {
        return std::string();
    }

    const int bytes = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0)
    {
        return std::string();
    }

    std::string path(static_cast<size_t>(bytes - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, path.data(), bytes, nullptr, nullptr);
#else
    Dl_info info = {};
    if (dladdr(reinterpret_cast<const void*>(&PackageRootDir), &info) == 0 || info.dli_fname == nullptr)
    {
        return std::string();
    }

    std::string path(info.dli_fname);
#endif

    const size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

// Runs once, before the first call that can reach USD.
void EnsureMaterialXStdlibPath()
{
    static const bool once = []() {
        // An explicit setting wins. Someone debugging a different MaterialX build should not have
        // to fight the package for it.
#if defined(_WIN32)
        size_t existing = 0;
        if (getenv_s(&existing, nullptr, 0, "PXR_MTLX_STDLIB_SEARCH_PATHS") == 0 && existing > 0)
        {
            return true;
        }
#else
        if (const char* value = std::getenv("PXR_MTLX_STDLIB_SEARCH_PATHS"))
        {
            if (value[0] != '\0')
            {
                return true;
            }
        }
#endif

        const std::string root = PackageRootDir();
        if (root.empty())
        {
            return false;
        }

        // Laid out by Native~/build_<platform>.{sh,ps1}; usdMtlx carries the stdlib in its own
        // resource directory and that whole tree is copied into the payload.
        const std::string stdlib = root + "/lib/usd/usdMtlx/resources/libraries";

#if defined(_WIN32)
        _putenv_s("PXR_MTLX_STDLIB_SEARCH_PATHS", stdlib.c_str());
#else
        setenv("PXR_MTLX_STDLIB_SEARCH_PATHS", stdlib.c_str(), 0);
#endif
        return true;
    }();
    (void)once;
}

}  // namespace

int RUsd_GetApiVersion(void)
{
    // First call the managed side makes, and it makes it before anything opens a stage.
    EnsureMaterialXStdlibPath();
    return kApiVersion;
}

int RUsd_GetOpenUsdVersion(char* buffer, int bufferCapacity)
{
    if (buffer == nullptr || bufferCapacity <= 0)
    {
        return kFailure;
    }

    std::ostringstream stream;
    stream << PXR_MAJOR_VERSION << "." << PXR_MINOR_VERSION << "." << PXR_PATCH_VERSION;
    return CopyStringToBuffer(stream.str(), buffer, bufferCapacity);
}

int RUsd_BeginExport(
    const char* outputPath,
    const char* rootPrimName,
    float metersPerUnit,
    RUsdContext** outContext)
{
    return RUsd_BeginExportEx(outputPath, rootPrimName, metersPerUnit, 0, outContext);
}

int RUsd_BeginExportEx(
    const char* outputPath,
    const char* rootPrimName,
    float metersPerUnit,
    int captureDiagnostics,
    RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    std::unique_ptr<RUsdContext> context(new RUsdContext());

    try
    {
        if (captureDiagnostics != 0)
        {
            EnableDiagnostics(context.get());
        }

        std::string output = SafeString(outputPath);
        std::string rootName = SafeString(rootPrimName);
        if (output.empty())
        {
            SetError(context.get(), "Output path is empty.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        if (rootName.empty())
        {
            rootName = "Root";
        }

        context->stage = UsdStage::CreateNew(output);
        if (!context->stage)
        {
            SetError(context.get(), "OpenUSD failed to create a new stage.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        context->rootPath = SdfPath("/" + rootName);
        UsdGeomXform root = UsdGeomXform::Define(context->stage, context->rootPath);
        context->stage->SetDefaultPrim(root.GetPrim());
        UsdGeomSetStageUpAxis(context->stage, UsdGeomTokens->y);
        UsdGeomSetStageMetersPerUnit(context->stage, static_cast<double>(metersPerUnit));

        *outContext = context.release();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context.get(), MakeError("Failed to begin USD export", exception));
        DisableDiagnostics(context.get());
        return kFailure;
    }
    catch (...)
    {
        SetError(context.get(), "Failed to begin USD export.");
        DisableDiagnostics(context.get());
        return kFailure;
    }
}

int RUsd_AddMesh(
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
    const RUsdMaterial* material)
{
    RUsdSubmesh submesh = {0, indexCount, 0};
    return RUsd_AddMeshEx(
        context,
        primPath,
        points,
        pointCount,
        indices,
        indexCount,
        &submesh,
        indexCount > 0 ? 1 : 0,
        normals,
        normalCount,
        uv0,
        uv0Count,
        material,
        material != nullptr ? 1 : 0,
        1,
        1);
}

int RUsd_AddMeshEx(
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
    int writeExtent)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    try
    {
        std::string error;
        if (!IsValidMeshInput(points, pointCount, indices, indexCount, submeshes, submeshCount, error))
        {
            SetError(context, error);
            return kFailure;
        }

        SdfPath path(SafeString(primPath));
        if (!path.IsAbsolutePath() || path.IsAbsoluteRootPath())
        {
            SetError(context, "USD mesh prim path must be absolute.");
            return kFailure;
        }

        EnsureParentXforms(context->stage, path);

        UsdGeomMesh mesh = UsdGeomMesh::Define(context->stage, path);
        mesh.CreatePointsAttr().Set(CopyPoints(points, pointCount));
        mesh.CreateFaceVertexCountsAttr().Set(CreateTriangleFaceCounts(indexCount));
        mesh.CreateFaceVertexIndicesAttr().Set(CopyIndices(indices, indexCount));
        mesh.CreateSubdivisionSchemeAttr().Set(UsdGeomTokens->none);
        mesh.CreateOrientationAttr().Set(UsdGeomTokens->rightHanded);
        mesh.CreateVisibilityAttr().Set(visible != 0 ? UsdGeomTokens->inherited : UsdGeomTokens->invisible);

        if (writeExtent != 0)
        {
            mesh.CreateExtentAttr().Set(ComputeExtent(points, pointCount));
        }

        if (normals != nullptr && normalCount == pointCount)
        {
            mesh.CreateNormalsAttr().Set(CopyPoints(normals, normalCount));
            mesh.SetNormalsInterpolation(UsdGeomTokens->vertex);
        }

        if (uv0 != nullptr && uv0Count == pointCount)
        {
            UsdGeomPrimvarsAPI primvars(mesh.GetPrim());
            UsdGeomPrimvar st = primvars.CreatePrimvar(
                TfToken("st"),
                SdfValueTypeNames->TexCoord2fArray,
                UsdGeomTokens->vertex);
            st.Set(CopyUv0(uv0, uv0Count));
        }

        BindMaterials(context, mesh, submeshes, submeshCount, materials, materialCount);
        WriteDisplayColor(mesh, submeshes, submeshCount, materials, materialCount);
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to add mesh", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to add mesh.");
        return kFailure;
    }
}

int RUsd_AddXform(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    int visible)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    try
    {
        SdfPath path(SafeString(primPath));
        if (!path.IsAbsolutePath() || path.IsAbsoluteRootPath())
        {
            SetError(context, "USD xform prim path must be absolute.");
            return kFailure;
        }

        EnsureParentXforms(context->stage, path);
        UsdGeomXform xform = UsdGeomXform::Define(context->stage, path);
        xform.ClearXformOpOrder();

        if (matrix != nullptr)
        {
            xform.MakeMatrixXform().Set(ToGfMatrix(*matrix));
        }

        xform.CreateVisibilityAttr().Set(visible != 0 ? UsdGeomTokens->inherited : UsdGeomTokens->invisible);
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to add xform", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to add xform.");
        return kFailure;
    }
}

int RUsd_EndExport(RUsdContext* context)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    try
    {
        bool saved = context->stage->GetRootLayer()->Save();
        if (!saved)
        {
            SetError(context, "OpenUSD failed to save the root layer.");
            return kFailure;
        }

        context->stage.Reset();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to finish USD export", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to finish USD export.");
        return kFailure;
    }
}

void RUsd_Destroy(RUsdContext* context)
{
    DisableDiagnostics(context);
    delete context;
}

int RUsd_GetLastError(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity)
{
    std::string error = context != nullptr ? context->lastError : g_lastError;
    if (error.empty())
    {
        error = "Unknown OpenUSD export error.";
    }

    return CopyStringToBuffer(error, buffer, bufferCapacity);
}

int RUsd_GetDiagnostics(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity)
{
    std::string diagnostics = context != nullptr ? context->diagnostics : g_lastDiagnostics;
    if (diagnostics.empty())
    {
        diagnostics = std::string();
    }

    return CopyStringToBuffer(diagnostics, buffer, bufferCapacity);
}

int RUsd_OpenStage(
    const char* inputPath,
    int captureDiagnostics,
    RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    std::unique_ptr<RUsdContext> context(new RUsdContext());

    try
    {
        if (captureDiagnostics != 0)
        {
            EnableDiagnostics(context.get());
        }

        std::string input = SafeString(inputPath);
        if (input.empty())
        {
            SetError(context.get(), "Input path is empty.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        context->stage = UsdStage::Open(input);
        if (!context->stage)
        {
            SetError(context.get(), "OpenUSD failed to open the stage.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        ReadImportedStage(context.get());
        *outContext = context.release();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context.get(), MakeError("Failed to open USD stage", exception));
        DisableDiagnostics(context.get());
        return kFailure;
    }
    catch (...)
    {
        SetError(context.get(), "Failed to open USD stage.");
        DisableDiagnostics(context.get());
        return kFailure;
    }
}

int RUsd_GetImportInfo(
    RUsdContext* context,
    char* defaultPrimPathBuffer,
    int defaultPrimPathBufferCapacity,
    double* metersPerUnit,
    int* upAxis,
    int* meshCount,
    int* materialCount)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD import context is invalid.");
        return kFailure;
    }

    if (metersPerUnit != nullptr)
    {
        *metersPerUnit = context->importMetersPerUnit;
    }

    if (upAxis != nullptr)
    {
        *upAxis = context->importUpAxis;
    }

    if (meshCount != nullptr)
    {
        *meshCount = static_cast<int>(context->importedMeshes.size());
    }

    if (materialCount != nullptr)
    {
        *materialCount = static_cast<int>(context->importedMaterials.size());
    }

    if (defaultPrimPathBuffer != nullptr && defaultPrimPathBufferCapacity > 0)
    {
        return CopyStringToBuffer(context->defaultPrimPath, defaultPrimPathBuffer, defaultPrimPathBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportMeshInfo(
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
    int* visible)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh& mesh = context->importedMeshes[static_cast<size_t>(meshIndex)];
    if (pointCount != nullptr)
    {
        *pointCount = static_cast<int>(mesh.points.size());
    }

    if (indexCount != nullptr)
    {
        *indexCount = static_cast<int>(mesh.indices.size());
    }

    if (normalCount != nullptr)
    {
        *normalCount = static_cast<int>(mesh.normals.size());
    }

    if (uv0Count != nullptr)
    {
        *uv0Count = static_cast<int>(mesh.uvSets[0].size());
    }

    if (materialIndex != nullptr)
    {
        *materialIndex = mesh.materialIndex;
    }

    if (visible != nullptr)
    {
        *visible = mesh.visible;
    }

    if (primPathBuffer != nullptr && primPathBufferCapacity > 0)
    {
        CopyStringToBuffer(mesh.primPath, primPathBuffer, primPathBufferCapacity);
    }

    if (nameBuffer != nullptr && nameBufferCapacity > 0)
    {
        CopyStringToBuffer(mesh.name, nameBuffer, nameBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportNodeCount(RUsdContext* context, int* nodeCount)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD import context is invalid.");
        return kFailure;
    }

    if (nodeCount != nullptr)
    {
        *nodeCount = static_cast<int>(context->importedNodes.size());
    }

    return kSuccess;
}

int RUsd_GetImportNodeInfo(
    RUsdContext* context,
    int nodeIndex,
    char* pathBuffer,
    int pathBufferCapacity,
    RUsdMatrix4x4* localMatrix,
    int* visible)
{
    if (context == nullptr || nodeIndex < 0 || nodeIndex >= static_cast<int>(context->importedNodes.size()))
    {
        SetError(context, "USD import node index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedNode& node = context->importedNodes[static_cast<size_t>(nodeIndex)];
    if (localMatrix != nullptr)
    {
        *localMatrix = node.localMatrix;
    }

    if (visible != nullptr)
    {
        *visible = node.visible;
    }

    if (pathBuffer != nullptr && pathBufferCapacity > 0)
    {
        CopyStringToBuffer(node.path, pathBuffer, pathBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportSubmeshCount(RUsdContext* context, int meshIndex, int* submeshCount)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    if (submeshCount != nullptr)
    {
        *submeshCount = static_cast<int>(context->importedMeshes[static_cast<size_t>(meshIndex)].submeshes.size());
    }

    return kSuccess;
}

int RUsd_CopyImportSubmeshes(
    RUsdContext* context,
    int meshIndex,
    RUsdSubmesh* submeshes,
    int submeshCapacity)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    const std::vector<RUsdSubmesh>& source = context->importedMeshes[static_cast<size_t>(meshIndex)].submeshes;
    if (submeshes == nullptr || !FitsCapacity(submeshCapacity, source.size()))
    {
        SetError(context, "USD import submesh destination buffer is too small.");
        return kFailure;
    }

    std::copy(source.begin(), source.end(), submeshes);
    return kSuccess;
}

int RUsd_CopyImportMesh(
    RUsdContext* context,
    int meshIndex,
    RUsdVec3* points,
    int pointCapacity,
    int* indices,
    int indexCapacity,
    RUsdVec3* normals,
    int normalCapacity,
    RUsdVec2* uv0,
    int uv0Capacity)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh& mesh = context->importedMeshes[static_cast<size_t>(meshIndex)];
    if (points == nullptr || !FitsCapacity(pointCapacity, mesh.points.size()) ||
        indices == nullptr || !FitsCapacity(indexCapacity, mesh.indices.size()))
    {
        SetError(context, "USD import mesh destination buffers are too small.");
        return kFailure;
    }

    std::copy(mesh.points.begin(), mesh.points.end(), points);
    std::copy(mesh.indices.begin(), mesh.indices.end(), indices);

    if (!mesh.normals.empty())
    {
        if (normals == nullptr || !FitsCapacity(normalCapacity, mesh.normals.size()))
        {
            SetError(context, "USD import normal destination buffer is too small.");
            return kFailure;
        }

        std::copy(mesh.normals.begin(), mesh.normals.end(), normals);
    }

    if (!mesh.uvSets[0].empty())
    {
        if (uv0 == nullptr || !FitsCapacity(uv0Capacity, mesh.uvSets[0].size()))
        {
            SetError(context, "USD import UV destination buffer is too small.");
            return kFailure;
        }

        std::copy(mesh.uvSets[0].begin(), mesh.uvSets[0].end(), uv0);
    }

    return kSuccess;
}

int RUsd_GetImportMaterialOpacity(
    RUsdContext* context,
    int materialIndex,
    float* opacityThreshold,
    char* texturePathBuffer,
    int texturePathBufferCapacity)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material =
        context->importedMaterials[static_cast<size_t>(materialIndex)];

    if (opacityThreshold != nullptr)
    {
        *opacityThreshold = material.opacityThreshold;
    }

    if (texturePathBuffer != nullptr && texturePathBufferCapacity > 0)
    {
        CopyStringToBuffer(material.opacityTexturePath, texturePathBuffer, texturePathBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportMaterialMetallicLayout(
    RUsdContext* context,
    int materialIndex,
    int* ormPacked,
    int* occlusionIsSeparate,
    char* occlusionTexturePathBuffer,
    int occlusionTexturePathBufferCapacity)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material =
        context->importedMaterials[static_cast<size_t>(materialIndex)];

    if (ormPacked != nullptr)
    {
        *ormPacked = material.metallicIsOrm ? 1 : 0;
    }

    if (occlusionIsSeparate != nullptr)
    {
        *occlusionIsSeparate = material.occlusionTexturePath.empty() ? 0 : 1;
    }

    if (occlusionTexturePathBuffer != nullptr && occlusionTexturePathBufferCapacity > 0)
    {
        CopyStringToBuffer(material.occlusionTexturePath, occlusionTexturePathBuffer,
                           occlusionTexturePathBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportMaterialTextureLayout(
    RUsdContext* context,
    int materialIndex,
    int* metallicIsPlain,
    char* roughnessTexturePathBuffer,
    int roughnessTexturePathBufferCapacity)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material =
        context->importedMaterials[static_cast<size_t>(materialIndex)];

    if (metallicIsPlain != nullptr)
    {
        *metallicIsPlain = material.metallicIsPlain ? 1 : 0;
    }

    if (roughnessTexturePathBuffer != nullptr && roughnessTexturePathBufferCapacity > 0)
    {
        CopyStringToBuffer(material.roughnessTexturePath, roughnessTexturePathBuffer,
                           roughnessTexturePathBufferCapacity);
    }

    return kSuccess;
}

int RUsd_GetImportMeshUvSetInfo(
    RUsdContext* context,
    int meshIndex,
    int uvSet,
    int* uvCount)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    if (uvSet < 0 || uvSet >= RUSD_MAX_UV_SETS)
    {
        SetError(context, "USD import UV set index is out of range.");
        return kFailure;
    }

    if (uvCount != nullptr)
    {
        *uvCount = static_cast<int>(
            context->importedMeshes[static_cast<size_t>(meshIndex)].uvSets[uvSet].size());
    }

    return kSuccess;
}

int RUsd_CopyImportMeshUvSet(
    RUsdContext* context,
    int meshIndex,
    int uvSet,
    RUsdVec2* uv,
    int uvCapacity)
{
    if (context == nullptr || meshIndex < 0 || meshIndex >= static_cast<int>(context->importedMeshes.size()))
    {
        SetError(context, "USD import mesh index is invalid.");
        return kFailure;
    }

    if (uvSet < 0 || uvSet >= RUSD_MAX_UV_SETS)
    {
        SetError(context, "USD import UV set index is out of range.");
        return kFailure;
    }

    const std::vector<RUsdVec2>& source =
        context->importedMeshes[static_cast<size_t>(meshIndex)].uvSets[uvSet];
    if (source.empty())
    {
        return kSuccess;
    }

    if (uv == nullptr || !FitsCapacity(uvCapacity, source.size()))
    {
        SetError(context, "USD import UV destination buffer is too small.");
        return kFailure;
    }

    std::copy(source.begin(), source.end(), uv);
    return kSuccess;
}

int RUsd_GetImportMaterial(
    RUsdContext* context,
    int materialIndex,
    RUsdMaterial* material,
    char* nameBuffer,
    int nameBufferCapacity)
{
    if (context == nullptr || materialIndex < 0 || materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& importedMaterial =
        context->importedMaterials[static_cast<size_t>(materialIndex)];
    if (material != nullptr)
    {
        *material = importedMaterial.material;
    }

    if (nameBuffer != nullptr && nameBufferCapacity > 0)
    {
        return CopyStringToBuffer(importedMaterial.name, nameBuffer, nameBufferCapacity);
    }

    return kSuccess;
}

int RUsd_CreateUsdzPackage(
    RUsdContext* context,
    const char* sourceUsdPath,
    const char* usdzPath,
    const char* firstLayerName,
    int arkitCompatible)
{
    std::string source = SafeString(sourceUsdPath);
    std::string target = SafeString(usdzPath);
    if (source.empty() || target.empty())
    {
        SetError(context, "USDZ packaging needs both a source stage path and an output path.");
        return kFailure;
    }

    try
    {
        // usdz cannot be written as a stage, so the caller writes an ordinary .usdc/.usda first
        // and this packages it. Dependency discovery is left to OpenUSD: it pulls in every
        // referenced layer and texture. editLayersInPlace stays false so the source layers on
        // disk are not rewritten.
        const SdfAssetPath asset(source);
        const std::string firstLayer = SafeString(firstLayerName);
        const bool created = arkitCompatible != 0
            ? UsdUtilsCreateNewARKitUsdzPackage(asset, target, firstLayer)
            : UsdUtilsCreateNewUsdzPackage(asset, target, firstLayer);
        if (!created)
        {
            SetError(context, "OpenUSD failed to write the usdz package '" + target +
                "' from '" + source + "'.");
            return kFailure;
        }

        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to write the usdz package", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to write the usdz package.");
        return kFailure;
    }
}

int RUsd_ReadImportAsset(
    RUsdContext* context,
    const char* assetPath,
    char* resolvedPathBuffer,
    int resolvedPathBufferCapacity,
    unsigned char* buffer,
    long long bufferCapacity,
    long long* byteCount)
{
    if (byteCount != nullptr)
    {
        *byteCount = 0;
    }

    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD import context is invalid.");
        return kFailure;
    }

    const std::string authored = SafeString(assetPath);
    if (authored.empty())
    {
        SetError(context, "USD asset path is empty.");
        return kFailure;
    }

    try
    {
        // Prefer the resolution USD itself performed when the material was read; fall back to
        // anchoring on the root layer, which covers paths this context never resolved.
        std::string identifier;
        std::unordered_map<std::string, std::string>::const_iterator recorded =
            context->assetResolutions.find(authored);
        if (recorded != context->assetResolutions.end())
        {
            identifier = recorded->second;
        }
        else
        {
            const SdfLayerHandle& rootLayer = context->stage->GetRootLayer();
            identifier = rootLayer
                ? SdfComputeAssetPathRelativeToLayer(rootLayer, authored)
                : authored;
        }

        ArResolver& resolver = ArGetResolver();
        ArResolvedPath resolved = resolver.Resolve(identifier);
        if (!resolved)
        {
            SetError(context, "USD asset '" + authored + "' does not resolve to a readable asset.");
            return kFailure;
        }

        // Second, independent gate to the managed one in UsdImporter.ResolveTexturePath: this
        // entry point resolves through OpenUSD, which honours absolute paths and `..` climbs, so
        // without this a stage that failed the managed file check still reached a real read here.
        if (!IsResolvedAssetInsideStageRoot(context, resolved.GetPathString()))
        {
            // Reports the authored spelling, which the stage's author already knows. Echoing the
            // resolved path would tell them where files sit on this machine.
            SetError(context,
                "USD asset '" + authored + "' resolves outside the stage's own folders and was refused.");
            return kFailure;
        }

        std::shared_ptr<ArAsset> asset = resolver.OpenAsset(resolved);
        if (!asset)
        {
            SetError(context, "USD asset '" + authored + "' could not be opened.");
            return kFailure;
        }

        const size_t size = asset->GetSize();
        if (byteCount != nullptr)
        {
            *byteCount = static_cast<long long>(size);
        }

        if (resolvedPathBuffer != nullptr && resolvedPathBufferCapacity > 0)
        {
            CopyStringToBuffer(resolved.GetPathString(), resolvedPathBuffer, resolvedPathBufferCapacity);
        }

        if (buffer == nullptr)
        {
            return kSuccess; // size query
        }

        if (bufferCapacity < static_cast<long long>(size))
        {
            SetError(context, "USD asset destination buffer is too small.");
            return kFailure;
        }

        if (size > 0 && asset->Read(buffer, size, 0) != size)
        {
            SetError(context, "USD asset '" + authored + "' could not be read in full.");
            return kFailure;
        }

        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to read a USD asset", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to read a USD asset.");
        return kFailure;
    }
}

// ---- Composition (API v6) ----

int RUsd_BeginLayerExport(
    const char* outputPath,
    const char* defaultPrimPath,
    const char* materialScopePath,
    double metersPerUnit,
    int upAxis,
    const char* subLayers,
    int flags,
    RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    std::unique_ptr<RUsdContext> context(new RUsdContext());

    try
    {
        if ((flags & RUSD_LAYER_CAPTURE_DIAGNOSTICS) != 0)
        {
            EnableDiagnostics(context.get());
        }

        const std::string output = SafeString(outputPath);
        if (output.empty())
        {
            SetError(context.get(), "Output path is empty.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        SdfPath rootPath;
        const std::string defaultPrim = SafeString(defaultPrimPath);
        if (!defaultPrim.empty())
        {
            if (!ParseAbsolutePrimPath(context.get(), defaultPrimPath, &rootPath) || !rootPath.IsRootPrimPath())
            {
                SetError(context.get(), "The default prim '" + defaultPrim + "' must be a root prim path such as /World.");
                DisableDiagnostics(context.get());
                return kFailure;
            }
        }

        SdfPath scopePath;
        if (!SafeString(materialScopePath).empty() &&
            !ParseAbsolutePrimPath(context.get(), materialScopePath, &scopePath))
        {
            DisableDiagnostics(context.get());
            return kFailure;
        }

        // Sublayers are set on the layer before the stage opens it, so the stage composes once.
        SdfLayerRefPtr layer = SdfLayer::CreateNew(output);
        if (!layer)
        {
            SetError(context.get(), "OpenUSD failed to create the layer '" + output + "'.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        std::vector<std::string> subLayerPaths;
        for (const std::vector<std::string>& record : SplitRecords(SafeString(subLayers)))
        {
            subLayerPaths.push_back(record[0]);
        }

        if (!subLayerPaths.empty())
        {
            layer->SetSubLayerPaths(subLayerPaths);

            // A stage's time rate is the root layer's. The source's own timeCodesPerSecond (60
            // for the Elephant) stayed on the sublayer, the new root fell back to 24, and USD then
            // rescaled the sublayer's time samples by 24/60: the animation ran at 0.4 speed. The
            // strongest sublayer's timing goes onto the new root so the rate and range hold.
            const std::string strongest =
                SdfComputeAssetPathRelativeToLayer(layer, subLayerPaths.front());
            if (SdfLayerRefPtr source = SdfLayer::FindOrOpen(strongest))
            {
                if (source->HasTimeCodesPerSecond())
                {
                    layer->SetTimeCodesPerSecond(source->GetTimeCodesPerSecond());
                }
                if (source->HasFramesPerSecond())
                {
                    layer->SetFramesPerSecond(source->GetFramesPerSecond());
                }
                if (source->HasStartTimeCode())
                {
                    layer->SetStartTimeCode(source->GetStartTimeCode());
                }
                if (source->HasEndTimeCode())
                {
                    layer->SetEndTimeCode(source->GetEndTimeCode());
                }
            }
        }

        context->stage = UsdStage::Open(layer);
        if (!context->stage)
        {
            SetError(context.get(), "OpenUSD failed to open a stage on the layer '" + output + "'.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        context->stage->SetEditTarget(UsdEditTarget(layer));
        context->rootPath = rootPath;
        context->materialScopePath = scopePath;
        if (rootPath.IsEmpty() && scopePath.IsEmpty())
        {
            context->materialScopePath = SdfPath("/Looks");
        }

        if (!rootPath.IsEmpty())
        {
            if ((flags & RUSD_LAYER_DEFINE_DEFAULT_PRIM) != 0)
            {
                UsdGeomXform::Define(context->stage, rootPath);
            }

            layer->SetDefaultPrim(rootPath.GetNameToken());
        }

        if (upAxis == 1 || upAxis == 2)
        {
            UsdGeomSetStageUpAxis(context->stage, upAxis == 2 ? UsdGeomTokens->z : UsdGeomTokens->y);
        }

        if (metersPerUnit > 0.0)
        {
            UsdGeomSetStageMetersPerUnit(context->stage, metersPerUnit);
        }

        *outContext = context.release();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context.get(), MakeError("Failed to begin the USD layer export", exception));
        DisableDiagnostics(context.get());
        return kFailure;
    }
    catch (...)
    {
        SetError(context.get(), "Failed to begin the USD layer export.");
        DisableDiagnostics(context.get());
        return kFailure;
    }
}

int RUsd_DefinePrim(RUsdContext* context, const char* primPath, const char* typeName, int specifier)
{
    return RunExportEdit(context, "define a prim", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        SdfPrimSpecHandle spec = GetOrCreateSpec(context, path);
        if (!spec)
        {
            SetError(context, "OpenUSD could not create a prim spec at '" + path.GetString() + "'.");
            return kFailure;
        }

        switch (specifier)
        {
            case RUSD_SPECIFIER_DEF: spec->SetSpecifier(SdfSpecifierDef); break;
            case RUSD_SPECIFIER_OVER: spec->SetSpecifier(SdfSpecifierOver); break;
            case RUSD_SPECIFIER_CLASS: spec->SetSpecifier(SdfSpecifierClass); break;
            default:
                SetError(context, "Unknown prim specifier.");
                return kFailure;
        }

        const std::string type = SafeString(typeName);
        if (!type.empty())
        {
            spec->SetTypeName(type);
        }

        return kSuccess;
    });
}

int RUsd_AddReference(
    RUsdContext* context,
    const char* primPath,
    const char* assetPath,
    const char* targetPrimPath,
    int arcType)
{
    return RunExportEdit(context, "add a reference", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        const std::string asset = SafeString(assetPath);
        SdfPath target;
        if (!SafeString(targetPrimPath).empty() && !ParseAbsolutePrimPath(context, targetPrimPath, &target))
        {
            return kFailure;
        }

        if (asset.empty() && target.IsEmpty())
        {
            SetError(context, "An internal reference needs a target prim path.");
            return kFailure;
        }

        SdfPrimSpecHandle spec = GetOrCreateSpec(context, path);
        if (!spec)
        {
            SetError(context, "OpenUSD could not create a prim spec at '" + path.GetString() + "'.");
            return kFailure;
        }

        if (arcType == RUSD_ARC_PAYLOAD)
        {
            spec->GetPayloadList().Prepend(SdfPayload(asset, target));
        }
        else if (arcType == RUSD_ARC_REFERENCE)
        {
            spec->GetReferenceList().Prepend(SdfReference(asset, target));
        }
        else
        {
            SetError(context, "Unknown composition arc type.");
            return kFailure;
        }

        return kSuccess;
    });
}

int RUsd_SetInstanceable(RUsdContext* context, const char* primPath, int instanceable)
{
    return RunExportEdit(context, "set instanceable", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        GetOrCreateSpec(context, path)->SetInstanceable(instanceable != 0);
        return kSuccess;
    });
}

int RUsd_SetActive(RUsdContext* context, const char* primPath, int active)
{
    return RunExportEdit(context, "set active", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        GetOrCreateSpec(context, path)->SetActive(active != 0);
        return kSuccess;
    });
}

int RUsd_SetVariantSelection(
    RUsdContext* context,
    const char* primPath,
    const char* variantSet,
    const char* variant)
{
    return RunExportEdit(context, "set a variant selection", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        const std::string setName = SafeString(variantSet);
        if (setName.empty())
        {
            SetError(context, "The variant set name is empty.");
            return kFailure;
        }

        GetOrCreateSpec(context, path)->SetVariantSelection(setName, SafeString(variant));
        return kSuccess;
    });
}

int RUsd_SetLocalTransform(RUsdContext* context, const char* primPath, const RUsdMatrix4x4* matrix)
{
    return RunExportEdit(context, "set a local transform", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path) || matrix == nullptr)
        {
            if (matrix == nullptr)
            {
                SetError(context, "The transform matrix is null.");
            }

            return kFailure;
        }

        // A single matrix op whose xformOpOrder replaces whatever ops weaker layers author, so
        // the override wins however the source spelled its transform.
        static const TfToken transformOp("xformOp:transform");
        SdfPrimSpecHandle spec = GetOrCreateSpec(context, path);
        SetAttributeDefault(spec, transformOp, SdfValueTypeNames->Matrix4d, SdfVariabilityVarying,
            VtValue(ToGfMatrix(*matrix)));
        VtTokenArray order(1, transformOp);
        SetAttributeDefault(spec, UsdGeomTokens->xformOpOrder, SdfValueTypeNames->TokenArray,
            SdfVariabilityUniform, VtValue(order));
        return kSuccess;
    });
}

int RUsd_SetVisibility(RUsdContext* context, const char* primPath, int visible)
{
    return RunExportEdit(context, "set visibility", [&]() {
        SdfPath path;
        if (!ParseAbsolutePrimPath(context, primPath, &path))
        {
            return kFailure;
        }

        SetAttributeDefault(GetOrCreateSpec(context, path), UsdGeomTokens->visibility,
            SdfValueTypeNames->Token, SdfVariabilityVarying,
            VtValue(visible != 0 ? UsdGeomTokens->inherited : UsdGeomTokens->invisible));
        return kSuccess;
    });
}

int RUsd_PrimExists(RUsdContext* context, const char* primPath, int* exists)
{
    return RunExportEdit(context, "look up a prim", [&]() {
        SdfPath path;
        if (exists == nullptr || !ParseAbsolutePrimPath(context, primPath, &path))
        {
            if (exists == nullptr)
            {
                SetError(context, "The exists output is null.");
            }

            return kFailure;
        }

        *exists = context->stage->GetPrimAtPath(path) ? 1 : 0;
        return kSuccess;
    });
}

int RUsd_OpenStageEx(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    std::unique_ptr<RUsdContext> context(new RUsdContext());

    try
    {
        if ((flags & RUSD_OPEN_CAPTURE_DIAGNOSTICS) != 0)
        {
            EnableDiagnostics(context.get());
        }

        const std::string input = SafeString(inputPath);
        if (input.empty())
        {
            SetError(context.get(), "Input path is empty.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        const bool loadNone = (flags & RUSD_OPEN_LOAD_NO_PAYLOADS) != 0;
        context->stage = UsdStage::Open(input, loadNone ? UsdStage::LoadNone : UsdStage::LoadAll);
        if (!context->stage)
        {
            SetError(context.get(), "OpenUSD failed to open the stage.");
            DisableDiagnostics(context.get());
            return kFailure;
        }

        // Selections go into the session layer: they change what this import composes without
        // touching any file. Applied in order, so a set nested inside another set's variant
        // can be selected after the outer one.
        const std::vector<std::vector<std::string>> selections = SplitRecords(SafeString(variantSelections));
        if (!selections.empty())
        {
            context->stage->SetEditTarget(UsdEditTarget(context->stage->GetSessionLayer()));
            for (const std::vector<std::string>& record : selections)
            {
                if (record.size() < 3 || !SdfPath::IsValidPathString(record[0]))
                {
                    AppendImportWarning(context.get(), "ignored a malformed variant selection.");
                    continue;
                }

                UsdPrim prim = context->stage->GetPrimAtPath(SdfPath(record[0]));
                if (!prim || prim.IsInstanceProxy())
                {
                    AppendImportWarning(context.get(), "variant selection skipped: no editable prim at " + record[0] + ".");
                    continue;
                }

                UsdVariantSet variantSet = prim.GetVariantSet(record[1]);
                if (!prim.GetVariantSets().HasVariantSet(record[1]) || !variantSet.HasAuthoredVariant(record[2]))
                {
                    AppendImportWarning(context.get(), "variant selection skipped: " + record[0] + " has no variant " +
                        record[1] + "=" + record[2] + ".");
                    continue;
                }

                variantSet.SetVariantSelection(record[2]);
            }
        }

        if (loadNone)
        {
            for (const std::vector<std::string>& record : SplitRecords(SafeString(loadPaths)))
            {
                if (!SdfPath::IsValidPathString(record[0]))
                {
                    AppendImportWarning(context.get(), "ignored a malformed payload path.");
                    continue;
                }

                context->stage->Load(SdfPath(record[0]));
            }
        }

        ReadImportedStage(context.get());
        *outContext = context.release();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context.get(), MakeError("Failed to open USD stage", exception));
        DisableDiagnostics(context.get());
        return kFailure;
    }
    catch (...)
    {
        SetError(context.get(), "Failed to open USD stage.");
        DisableDiagnostics(context.get());
        return kFailure;
    }
}

int RUsd_GetImportNodeFlags(RUsdContext* context, int nodeIndex, int* flags)
{
    if (context == nullptr || flags == nullptr || nodeIndex < 0 ||
        nodeIndex >= static_cast<int>(context->importedNodes.size()))
    {
        SetError(context, "USD import node index is invalid.");
        return kFailure;
    }

    *flags = context->importedNodes[static_cast<size_t>(nodeIndex)].compositionFlags;
    return kSuccess;
}

int RUsd_GetImportInstancedMeshCount(RUsdContext* context, int* count)
{
    if (context == nullptr || count == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportInstancedMeshCount.");
        return kFailure;
    }

    *count = static_cast<int>(context->importedInstancedMeshes.size());
    return kSuccess;
}

int RUsd_GetImportInstancedMesh(RUsdContext* context, int index, RUsdImportInstancedMesh* instanced)
{
    if (context == nullptr || instanced == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedInstancedMeshes.size())
    {
        SetError(context, "Invalid arguments for RUsd_GetImportInstancedMesh.");
        return kFailure;
    }

    const RUsdContext::ImportedInstancedMesh& record =
        context->importedInstancedMeshes[static_cast<size_t>(index)];
    instanced->instancerNodeIndex = record.instancerNodeIndex;
    instanced->meshIndex = record.meshIndex;
    instanced->instanceCount = static_cast<int>(record.matrices.size() / 16);
    return kSuccess;
}

int RUsd_CopyImportInstanceMatrices(RUsdContext* context, int index, float* matrices, int capacity)
{
    if (context == nullptr || matrices == nullptr || capacity < 0 || index < 0 ||
        static_cast<size_t>(index) >= context->importedInstancedMeshes.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportInstanceMatrices.");
        return kFailure;
    }

    const std::vector<float>& source =
        context->importedInstancedMeshes[static_cast<size_t>(index)].matrices;
    if (static_cast<size_t>(capacity) < source.size())
    {
        SetError(context, "Buffer too small for RUsd_CopyImportInstanceMatrices.");
        return kFailure;
    }

    std::copy(source.begin(), source.end(), matrices);
    return kSuccess;
}

int RUsd_GetImportLightCount(RUsdContext* context, int* lightCount)
{
    if (context == nullptr || lightCount == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportLightCount.");
        return kFailure;
    }

    *lightCount = static_cast<int>(context->importedLights.size());
    return kSuccess;
}

int RUsd_GetImportLight(
    RUsdContext* context,
    int lightIndex,
    RUsdImportLight* light,
    char* texturePathBuffer,
    int texturePathBufferCapacity)
{
    if (context == nullptr || light == nullptr || lightIndex < 0 ||
        lightIndex >= static_cast<int>(context->importedLights.size()))
    {
        SetError(context, "USD import light index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedLight& entry = context->importedLights[static_cast<size_t>(lightIndex)];
    *light = entry.light;
    if (texturePathBuffer != nullptr && texturePathBufferCapacity > 0)
    {
        CopyStringToBuffer(entry.texturePath, texturePathBuffer, static_cast<size_t>(texturePathBufferCapacity));
    }
    return kSuccess;
}

int RUsd_GetImportCameraCount(RUsdContext* context, int* cameraCount)
{
    if (context == nullptr || cameraCount == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportCameraCount.");
        return kFailure;
    }

    *cameraCount = static_cast<int>(context->importedCameras.size());
    return kSuccess;
}

int RUsd_GetImportCamera(RUsdContext* context, int cameraIndex, RUsdImportCamera* camera)
{
    if (context == nullptr || camera == nullptr || cameraIndex < 0 ||
        cameraIndex >= static_cast<int>(context->importedCameras.size()))
    {
        SetError(context, "USD import camera index is invalid.");
        return kFailure;
    }

    *camera = context->importedCameras[static_cast<size_t>(cameraIndex)];
    return kSuccess;
}

int RUsd_GetImportAudioCount(RUsdContext* context, int* audioCount)
{
    if (context == nullptr || audioCount == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportAudioCount.");
        return kFailure;
    }

    *audioCount = static_cast<int>(context->importedAudio.size());
    return kSuccess;
}

int RUsd_GetImportAudio(
    RUsdContext* context,
    int audioIndex,
    RUsdImportAudio* audio,
    char* filePathBuffer,
    int filePathBufferCapacity)
{
    if (context == nullptr || audio == nullptr || audioIndex < 0 ||
        audioIndex >= static_cast<int>(context->importedAudio.size()))
    {
        SetError(context, "USD import audio index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedAudio& entry = context->importedAudio[static_cast<size_t>(audioIndex)];
    *audio = entry.audio;
    if (filePathBuffer != nullptr && filePathBufferCapacity > 0)
    {
        CopyStringToBuffer(entry.filePath, filePathBuffer, static_cast<size_t>(filePathBufferCapacity));
    }
    return kSuccess;
}

int RUsd_GetImportComposition(
    RUsdContext* context,
    char* buffer,
    int bufferCapacity,
    int* requiredLength)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD import context is invalid.");
        return kFailure;
    }

    try
    {
        if (!context->compositionReportBuilt)
        {
            context->compositionReport = BuildCompositionReport(context);
            context->compositionReportBuilt = true;
        }

        const int required = static_cast<int>(context->compositionReport.size()) + 1;
        if (requiredLength != nullptr)
        {
            *requiredLength = required;
        }

        if (buffer == nullptr)
        {
            return kSuccess; // size query
        }

        if (bufferCapacity < required)
        {
            SetError(context, "USD composition report buffer is too small.");
            return kFailure;
        }

        return CopyStringToBuffer(context->compositionReport, buffer, bufferCapacity);
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to read the USD composition", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to read the USD composition.");
        return kFailure;
    }
}
