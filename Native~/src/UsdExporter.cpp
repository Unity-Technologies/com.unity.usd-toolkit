#include "unity_usd_toolkit_native.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <exception>
#include <ios>
#include <memory>
#include <sstream>
#include <string>
#include <unordered_map>
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
#include <pxr/usd/sdf/valueTypeName.h>
#include <pxr/usd/usd/primRange.h>
#include <pxr/usd/usd/stage.h>
#include <pxr/usd/usdGeom/mesh.h>
#include <pxr/usd/usdGeom/imageable.h>
#include <pxr/usd/usdGeom/metrics.h>
#include <pxr/usd/usdGeom/primvarsAPI.h>
#include <pxr/usd/usdGeom/subset.h>
#include <pxr/usd/usdGeom/tokens.h>
#include <pxr/usd/usdGeom/xform.h>
#include <pxr/usd/usdGeom/xformable.h>
#include <pxr/usd/usdGeom/xformCache.h>
#include <pxr/usd/usdShade/connectableAPI.h>
#include <pxr/usd/usdShade/input.h>
#include <pxr/usd/usdShade/material.h>
#include <pxr/usd/usdShade/materialBindingAPI.h>
#include <pxr/usd/usdShade/output.h>
#include <pxr/usd/usdShade/shader.h>
#include <pxr/usd/usdUtils/usdzPackage.h>

PXR_NAMESPACE_USING_DIRECTIVE

struct RUsdContext
{
    UsdStageRefPtr stage;
    SdfPath rootPath;
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
    };

    struct ImportedMesh
    {
        std::string primPath;
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
    };

    std::string defaultPrimPath;
    double importMetersPerUnit = 1.0;
    int importUpAxis = 1;
    std::vector<ImportedMaterial> importedMaterials;
    std::vector<ImportedMesh> importedMeshes;
    std::vector<ImportedNode> importedNodes;

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
};

namespace
{
constexpr int kSuccess = 0;
constexpr int kFailure = 1;
constexpr int kApiVersion = 5;
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

// Writes an authored texture asset path into `buffer` and remembers what OpenUSD resolved it
// to. USD anchored it against the layer that authored it, which is more than
// RUsd_ReadImportAsset could work out on its own. Returns false for an empty path.
bool StoreAssetPath(RUsdContext* context, const SdfAssetPath& asset, char* buffer, size_t capacity)
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
        const std::string& resolved = asset.GetResolvedPath();
        if (!resolved.empty())
        {
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

    return StoreAssetPath(context, vtValue.UncheckedGet<SdfAssetPath>(), buffer, capacity)
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

RUsdMaterial ExtractPreviewMaterial(RUsdContext* context, const UsdShadeMaterial& material)
{
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
    bool hasAlbedo = ReadConnectedTextureFileReported(
        context, shader, TfToken("diffuseColor"), materialPath,
        result.albedoTexturePath, sizeof(result.albedoTexturePath));
    ReadConnectedTextureFileReported(
        context, shader, TfToken("normal"), materialPath,
        result.normalTexturePath, sizeof(result.normalTexturePath));
    bool hasMetallic = ReadConnectedTextureFileReported(
        context, shader, TfToken("metallic"), materialPath,
        result.metallicTexturePath, sizeof(result.metallicTexturePath));
    if (!hasMetallic)
    {
        hasMetallic = ReadConnectedTextureFileReported(
            context, shader, TfToken("roughness"), materialPath,
            result.metallicTexturePath, sizeof(result.metallicTexturePath));
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

    return StoreAssetPath(context, value.UncheckedGet<SdfAssetPath>(), buffer, capacity);
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
    char unused[1024] = {0};
    if (GetMdlTexture(nullptr, shader, "metallic_texture", unused, sizeof(unused)) ||
        GetMdlTexture(nullptr, shader, "reflectionroughness_texture", unused, sizeof(unused)) ||
        GetMdlTexture(nullptr, shader, "ORM_texture", unused, sizeof(unused)))
    {
        AppendImportWarning(context,
            "material '" + materialPath + "' uses OmniPBR metallic/roughness/ORM textures; they "
            "are not imported and the constant metallic and roughness values are used.");
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

void ReadOmniSurface(const UsdShadeShader& shader, RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

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

void ReadOmniGlass(const UsdShadeShader& shader, RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    GfVec3f color(1.0f, 1.0f, 1.0f);
    GetMdlColor(shader, "glass_color", &color);
    SetColor(&m, color);
    m.a = 0.25f;
    m.metallic = 0.0f;
    m.roughness = 0.0f;
    GetMdlFloat(shader, "frosting_roughness", &m.roughness);
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
        ReadOmniSurface(shader, out);
        return true;
    }

    if (StartsWith(name, "OmniGlass"))
    {
        ReadOmniGlass(shader, out);
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
        out->material = ExtractPreviewMaterial(context, material);
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
};

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

int AddImportedMaterial(
    RUsdContext* context,
    const UsdShadeMaterial& material,
    const DisplayColor& displayColor)
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

    if (found->second.recognized || !displayColor.valid)
    {
        return found->second.index;
    }

    return AddDisplayColorMaterial(context, key, name, displayColor);
}

// Triangulates one polygon face (fan) into `indices` with the import winding flip.
void TriangulateFace(
    const int* cornerVertices,
    int cornerCount,
    int faceStart,
    int faceVertexCount,
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
        indices->push_back(cornerVertices[static_cast<size_t>(faceStart)]);
        indices->push_back(cornerVertices[static_cast<size_t>(faceStart + i + 1)]);
        indices->push_back(cornerVertices[static_cast<size_t>(faceStart + i)]);
    }
}

// Triangulates the mesh grouped by material binding. Faces bound by `materialBind`-family
// UsdGeomSubsets get the subset's material; the rest use the mesh's direct binding. Produces
// a single `indices` buffer with one contiguous RUsdSubmesh range per material (so each maps
// to a Unity submesh). Returns the mesh's default (whole-mesh) material index.
int BuildImportedSubmeshes(
    RUsdContext* context,
    const UsdGeomMesh& mesh,
    const VtIntArray& faceVertexCounts,
    const int* cornerVertices,
    int cornerCount,
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
            "mesh '" + mesh.GetPath().GetString() +
            "' has inconsistent topology (faceVertexCounts do not sum to the faceVertexIndices "
            "length); mesh skipped.");
        return -1;
    }

    UsdShadeMaterial meshMaterial = UsdShadeMaterialBindingAPI(mesh.GetPrim()).ComputeBoundMaterial();
    int defaultMaterialIndex = AddImportedMaterial(context, meshMaterial, displayColor);

    std::vector<int> faceMaterial(static_cast<size_t>(numFaces), defaultMaterialIndex);

    std::vector<UsdGeomSubset> subsets =
        UsdShadeMaterialBindingAPI(mesh.GetPrim()).GetMaterialBindSubsets();
    for (const UsdGeomSubset& subset : subsets)
    {
        UsdShadeMaterial subsetMaterial =
            UsdShadeMaterialBindingAPI(subset.GetPrim()).ComputeBoundMaterial();
        // An unbound subset keeps the mesh's material rather than becoming a displayColor stand-in.
        int subsetMaterialIndex = subsetMaterial
            ? AddImportedMaterial(context, subsetMaterial, displayColor)
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

    // Distinct material indices, in order of first face appearance (stable, deterministic).
    std::vector<int> materialOrder;
    for (int f = 0; f < numFaces; ++f)
    {
        int matIndex = faceMaterial[static_cast<size_t>(f)];
        if (std::find(materialOrder.begin(), materialOrder.end(), matIndex) == materialOrder.end())
        {
            materialOrder.push_back(matIndex);
        }
    }

    for (int matIndex : materialOrder)
    {
        int start = static_cast<int>(indices->size());
        for (int f = 0; f < numFaces; ++f)
        {
            if (faceMaterial[static_cast<size_t>(f)] != matIndex)
            {
                continue;
            }

            TriangulateFace(cornerVertices, cornerCount, faceStart[static_cast<size_t>(f)],
                faceVertexCounts[static_cast<size_t>(f)], indices);
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

DisplayColor ReadDisplayColor(const UsdGeomMesh& mesh)
{
    DisplayColor result;
    UsdGeomPrimvarsAPI primvars(mesh.GetPrim());

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

    // Points/normals are kept in mesh-local space; the prim's transform is reconstructed
    // per node on the Unity side (see importedNodes). ToUnityVector still applies the
    // USD→Unity basis flip (negate X) so local coordinates match the converted transforms.
    importedMesh->primPath = mesh.GetPath().GetString();
    importedMesh->name = mesh.GetPrim().GetName().GetString();

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

        std::vector<int> uvElements[RUSD_MAX_UV_SETS];
        for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
        {
            ResolveCornerElements(
                &uvSources[set].layout, faceVertexIndices, cornerFace, pointCount, &uvElements[set]);
        }

        std::unordered_map<SplitVertexKey, int, SplitVertexKeyHash> vertexMap;
        vertexMap.reserve(static_cast<size_t>(cornerCount));

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

            auto existing = vertexMap.find(key);
            if (existing != vertexMap.end())
            {
                splitCornerVertices[static_cast<size_t>(corner)] = existing->second;
                continue;
            }

            const int vertex = static_cast<int>(importedMesh->points.size());
            vertexMap.emplace(key, vertex);
            splitCornerVertices[static_cast<size_t>(corner)] = vertex;

            importedMesh->points.push_back(ToUnityVector(points[static_cast<size_t>(point)]));
            if (normalSource.layout.valid)
            {
                importedMesh->normals.push_back(
                    ToUnityVector(normalSource.values[static_cast<size_t>(key[1])]));
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
        context, mesh, faceVertexCounts, cornerVertices, cornerCount, ReadDisplayColor(mesh),
        &importedMesh->indices, &importedMesh->submeshes);

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

    SdfPath looksPath = context->rootPath.AppendChild(TfToken("Looks"));
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

int RUsd_GetApiVersion(void)
{
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

        UsdPrim defaultPrim = context->stage->GetDefaultPrim();
        context->defaultPrimPath = defaultPrim ? defaultPrim.GetPath().GetString() : std::string();
        context->importMetersPerUnit = UsdGeomGetStageMetersPerUnit(context->stage);
        TfToken upAxis = UsdGeomGetStageUpAxis(context->stage);
        context->importUpAxis = upAxis == UsdGeomTokens->z ? 2 : 1;

        UsdGeomXformCache xformCache;
        // Instance proxies included: instanceable prims (Omniverse and most CAD exports build
        // assemblies this way) keep all their geometry in prototypes, and the default
        // traversal stops at the instance, which imported such stages as empty.
        for (const UsdPrim& prim : context->stage->Traverse(UsdTraverseInstanceProxies()))
        {
            // Record every transformable prim (Xform, Mesh, ...) so the Unity side can
            // rebuild the hierarchy with per-node local transforms instead of baking.
            if (prim.IsA<UsdGeomXformable>())
            {
                bool resetsXformStack = false;
                GfMatrix4d local = xformCache.GetLocalTransformation(prim, &resetsXformStack);

                RUsdContext::ImportedNode node;
                node.path = prim.GetPath().GetString();
                node.localMatrix = FromGfMatrix(local);
                node.visible = UsdGeomImageable(prim).ComputeVisibility() == UsdGeomTokens->invisible ? 0 : 1;
                context->importedNodes.push_back(node);
            }

            if (!prim.IsA<UsdGeomMesh>())
            {
                continue;
            }

            RUsdContext::ImportedMesh importedMesh;
            if (BuildImportedMesh(context.get(), UsdGeomMesh(prim), &xformCache, &importedMesh))
            {
                context->importedMeshes.push_back(importedMesh);
            }
        }

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
    if (submeshes == nullptr || submeshCapacity < static_cast<int>(source.size()))
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
    if (points == nullptr || pointCapacity < static_cast<int>(mesh.points.size()) ||
        indices == nullptr || indexCapacity < static_cast<int>(mesh.indices.size()))
    {
        SetError(context, "USD import mesh destination buffers are too small.");
        return kFailure;
    }

    std::copy(mesh.points.begin(), mesh.points.end(), points);
    std::copy(mesh.indices.begin(), mesh.indices.end(), indices);

    if (!mesh.normals.empty())
    {
        if (normals == nullptr || normalCapacity < static_cast<int>(mesh.normals.size()))
        {
            SetError(context, "USD import normal destination buffer is too small.");
            return kFailure;
        }

        std::copy(mesh.normals.begin(), mesh.normals.end(), normals);
    }

    if (!mesh.uvSets[0].empty())
    {
        if (uv0 == nullptr || uv0Capacity < static_cast<int>(mesh.uvSets[0].size()))
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

    if (uv == nullptr || uvCapacity < static_cast<int>(source.size()))
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
