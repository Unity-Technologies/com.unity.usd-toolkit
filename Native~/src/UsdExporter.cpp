#include "unity_usd_toolkit_native.h"
#include "UsdPhysicsImport.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <exception>
#include <fstream>
#include <map>
#include <ios>
#include <limits>
#include <memory>
#include <mutex>
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
#if defined(__linux__)
#include <tbb/tbb_stddef.h> // TBB_INTERFACE_VERSION only; nothing of TBB is linked from here
#endif
#include <pxr/base/tf/fileUtils.h>
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
#include <pxr/usd/sdf/fileFormat.h>
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
#include <pxr/usd/usdLux/shadowAPI.h>
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
#include <pxr/usd/usdSkel/animation.h>
#include <pxr/usd/usdSkel/animMapper.h>
#include <pxr/usd/usdSkel/animQuery.h>
#include <pxr/usd/usdSkel/binding.h>
#include <pxr/usd/usdSkel/bindingAPI.h>
#include <pxr/usd/usdSkel/blendShape.h>
#include <pxr/usd/usdSkel/cache.h>
#include <pxr/usd/usdSkel/inbetweenShape.h>
#include <pxr/usd/usdSkel/root.h>
#include <pxr/usd/usdSkel/skeleton.h>
#include <pxr/usd/usdSkel/skeletonQuery.h>
#include <pxr/usd/usdSkel/skinningQuery.h>
#include <pxr/usd/usdSkel/tokens.h>
#include <pxr/usd/usdSkel/topology.h>
#include <pxr/usd/usdSkel/utils.h>
#include <pxr/usd/usdUtils/usdzPackage.h>
// Stage API (API v13)
#include <pxr/base/gf/half.h>
#include <pxr/base/gf/matrix2d.h>
#include <pxr/base/gf/matrix3d.h>
#include <pxr/base/gf/quatd.h>
#include <pxr/base/gf/quatf.h>
#include <pxr/base/gf/quath.h>
#include <pxr/base/gf/vec2i.h>
#include <pxr/base/gf/vec3h.h>
#include <pxr/base/gf/vec3i.h>
#include <pxr/base/gf/vec4d.h>
#include <pxr/base/gf/vec4h.h>
#include <pxr/base/gf/vec4i.h>
#include <pxr/base/tf/notice.h>
#include <pxr/base/tf/weakBase.h>
#include <pxr/usd/sdf/schema.h>
#include <pxr/usd/sdf/timeCode.h>
#include <pxr/usd/usd/editTarget.h>
#include <pxr/usd/usd/inherits.h>
#include <pxr/usd/usd/notice.h>
#include <pxr/usd/usd/payloads.h>
#include <pxr/usd/usd/references.h>
#include <pxr/usd/usd/relationship.h>
#include <pxr/usd/usd/specializes.h>
#include <pxr/base/arch/export.h>

PXR_NAMESPACE_OPEN_SCOPE
// usdDraco's writer (pxr/usd/plugin/usdDraco/writer.h, which OpenUSD does not install), exported
// from the OpenUSD library by Native~/patches/openusd-26.05-windows-draco.patch.
ARCH_IMPORT bool UsdDraco_WriteDraco(const UsdGeomMesh& mesh,
                                     const std::string& fileName,
                                     int qp,
                                     int qt,
                                     int qn,
                                     int cl,
                                     int preservePolygons,
                                     int preservePositionOrder,
                                     int preserveHoles);
PXR_NAMESPACE_CLOSE_SCOPE

PXR_NAMESPACE_USING_DIRECTIVE

// Collects what a stage recomposes or edits (API v13) for RUSD_QUERY_CHANGES: the prim of every
// resynced and changed-info path of each UsdNotice::ObjectsChanged the stage sends.
class RUsdChangeListener final : public TfWeakBase
{
public:
    RUsdChangeListener(const UsdStageRefPtr& stage, std::set<SdfPath>* changed)
        : _changed(changed)
    {
        _key = TfNotice::Register(TfCreateWeakPtr(this), &RUsdChangeListener::OnObjectsChanged,
                                  UsdStageWeakPtr(stage));
    }

    ~RUsdChangeListener()
    {
        TfNotice::Revoke(_key);
    }

    void OnObjectsChanged(const UsdNotice::ObjectsChanged& notice, const UsdStageWeakPtr&)
    {
        for (const SdfPath& path : notice.GetResyncedPaths())
        {
            _changed->insert(path.GetPrimPath());
        }

        for (const SdfPath& path : notice.GetChangedInfoOnlyPaths())
        {
            _changed->insert(path.GetPrimPath());
        }
    }

private:
    std::set<SdfPath>* _changed;
    TfNotice::Key _key;
};

struct RUsdContext
{
    UsdStageRefPtr stage;
    SdfPath rootPath;
    // Set when the output's format can be written but not edited in place (Alembic: usdAbc's
    // layers refuse every edit): the stage is built in memory and exported here at the end.
    std::string exportTo;
    // API v13: `exportTo` is a Draco (.drc) file, which no file format writes: EndExport encodes
    // the stage's meshes into it through usdDraco's writer instead (WriteDracoFile).
    bool exportDraco = false;
    // Where generated materials go. Empty means <rootPath>/Looks (the original layout); a layer
    // written over another stage sets its own so it cannot collide with that stage's materials.
    SdfPath materialScopePath;
    std::string lastError;
    std::string diagnostics;
    // OpenUSD posts diagnostics from TBB worker threads, and the delegate appends them while the
    // importer adds its own warnings on the calling thread: every access to `diagnostics` while
    // the delegate is installed takes this.
    std::mutex diagnosticsMutex;
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
        // The normal map's tangent frame is flipped along U / V: OmniPBR's flip_tangent_u
        // (default false) and flip_tangent_v (default true). Isaac Sim reads an OmniPBR normal map
        // with V flipped (DirectX-style green), so a host on Unity's OpenGL-style convention
        // flips that channel (RUsd_GetImportMaterialNormalFlip).
        bool normalFlipU = false;
        bool normalFlipV = false;
        // A roughness map in a file of its own (API 10). Unity reads smoothness from the metallic
        // map's alpha, so the host folds 1 - roughness into that alpha while it has the pixels.
        std::string roughnessTexturePath;
        // An albedo blended with a constant colour by a mask (API 13): the base colour is
        // lerp(maskColor, albedo, mask), as Unreal-connector MDL writes it. The host bakes it.
        std::string maskTexturePath;
        float maskColor[3] = {0.0f, 0.0f, 0.0f};
        // API 13. How the mask is used: -1 none, 0 lerp(maskColor, albedo, mask.r), 1 the
        // per-channel form lerp(lerp(lerp(lerp(0, c0, m.r), c1, m.g), c2, m.b), albedo, m.a),
        // 2 Unreal's BlendOverlay of the mask (read linear) over the albedo, per channel.
        int maskMode = -1;
        float channelColors[9] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};
        // Roughness = lerp(roughnessMin, roughnessMax, ORM.g) when hasRoughnessRange.
        bool hasRoughnessRange = false;
        float roughnessMin = 0.0f;
        float roughnessMax = 1.0f;
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

        // The USD point each imported vertex was made from. Joint weights and blend-shape
        // offsets are authored per point, so a skinned mesh records it through the vertex split;
        // empty means vertex i is point i (always true when nothing split).
        std::vector<int> vertexPoints;
        size_t sourcePointCount = 0; // the USD points vertexPoints indexes

        // Animated points (a point cache, API v13): every imported vertex at every sample of the
        // points, through the same split and basis as `points`, and the normals likewise -- empty
        // when the mesh keeps the normals it was imported with. vertexNormalElements is the
        // authored normal each vertex took, recorded for a mesh with animated points and normals.
        std::vector<double> pointCacheTimes;
        std::vector<RUsdVec3> pointCachePoints;
        std::vector<RUsdVec3> pointCacheNormals;
        std::vector<int> vertexNormalElements;

        // UsdSkel binding (API v13). skeletonIndex is -1 for a mesh that is not skinned.
        struct BlendShapeFrame
        {
            float weight = 1.0f;
            std::vector<RUsdVec3> deltaVertices; // one per imported vertex, Unity basis
            std::vector<RUsdVec3> deltaNormals;  // empty when the shape authors no normal offsets
        };
        struct BlendShape
        {
            std::string name;
            std::vector<BlendShapeFrame> frames; // inbetweens by ascending weight, then the shape at 1
        };
        struct Skin
        {
            int skeletonIndex = -1;
            int influences = 0;                 // joint/weight pairs per vertex
            std::vector<int> jointIndices;      // into the skeleton's joint order
            std::vector<float> jointWeights;
            RUsdMatrix4x4 geomBindTransform = {};
            int dualQuaternion = 0;             // skel:skinningMethod is dualQuaternion
            std::vector<BlendShape> blendShapes;
            std::vector<float> blendShapeWeights;       // at importTime, one per blend shape
            std::vector<double> blendShapeWeightTimes;
            std::vector<float> blendShapeWeightSamples; // times x blend shapes
        };
        Skin skin;
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

    // Time (API v13). Every static value the import reads -- points, primvars, transforms,
    // visibility, implicit surfaces, lights, cameras -- is read at importTime; see
    // ResolveImportTime. importAtDefaultTime is set from RUSD_OPEN_DEFAULT_TIME before the walk.
    UsdTimeCode importTime = UsdTimeCode::Default();
    bool importAtDefaultTime = false;
    RUsdImportTimeInfo timeInfo = {};

    // Animated transforms and visibility (API v13): one track per node and kind.
    struct ImportedNodeTrack
    {
        int nodeIndex = -1;
        int kind = 0;                // RUSD_TRACK_*
        std::vector<double> times;
        std::vector<float> values;   // 16 floats a sample for transforms, 1 for visibility
    };
    std::vector<ImportedNodeTrack> importedNodeTracks;

    // Skeletons (API v13), in the order their SkelRoots were reached. Matrices are row-major
    // floats in USD's row-vector convention, 16 per joint (and per sample for `samples`).
    struct ImportedSkeleton
    {
        std::string path;
        int nodeIndex = -1;          // the node the Skeleton prim became; -1 until the walk reaches it
        std::vector<std::string> joints;
        std::vector<int> parents;
        std::vector<float> bindTransforms;  // skeleton space
        std::vector<float> restTransforms;  // joint local
        std::vector<float> poseTransforms;  // joint local, at importTime
        std::vector<double> times;
        std::vector<float> samples;         // joint local, times x joints
    };
    std::vector<ImportedSkeleton> importedSkeletons;
    size_t animationValues = 0;  // floats held by tracks, skeleton samples and blend shapes

    // Import budgets, as RUsd_OpenStageWithLimits set them; these defaults are the kMaxImport*
    // constants below (static_assert there). With skipWhenExceeded the walk leaves out what does
    // not fit, counted here and reported once at the end, instead of failing the import.
    struct ImportLimits
    {
        size_t nodes = 2000000;
        size_t meshes = 1000000;
        size_t instancePlacements = 4000000;
        size_t geometryElements = 256u * 1024u * 1024u;
        size_t trackSamples = 1000000;
        size_t animationValues = 64u * 1024u * 1024u;
        size_t joints = 65536;
        bool skipWhenExceeded = false;
    } importLimits;
    size_t skippedNodes = 0;
    std::string firstSkippedNode;
    size_t skippedMeshes = 0;
    size_t skippedPlacements = 0;

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
    // API v13: what RUsd_ReadPhysics last returned; its pointers stay valid while this holds it.
    rusd_physics::StorePtr physics;

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

    // Export: RUSD_MATERIAL_FLAVOR_* written beside UsdPreviewSurface (API 13).
    int materialFlavors = 0;

    // Authored asset path -> the path OpenUSD resolved it to, recorded while reading materials.
    // The authored path is all the managed side gets, and for a stage inside a .usdz package it
    // resolves to a file no file-system read can reach, so RUsd_ReadImportAsset resolves through
    // this table (per-layer correct) before falling back to anchoring on the root layer.
    std::unordered_map<std::string, std::string> assetResolutions;
    // Texture path -> the colour space its file input authors ("sRGB", "raw", ...): the
    // colorSpace metadata on the attribute that holds the asset, or a UsdUVTexture's
    // sourceColorSpace. Absent when nothing is authored ("auto"). See RUsd_GetImportTextureColorSpace.
    std::unordered_map<std::string, std::string> assetColorSpaces;
    // Remote MDL modules (http/https sourceAsset) the import met and found no cached copy of,
    // in the order met. See RUsd_GetImportRemoteRequest.
    std::vector<std::string> remoteRequests;

    // RUsd_GetImportComposition's report, built on first request.
    std::string compositionReport;
    bool compositionReportBuilt = false;

    // Stage API (API v13). The prims recomposed or edited since the last RUSD_QUERY_CHANGES, and
    // what collects them; declared after `stage`, so the listener goes first.
    std::set<SdfPath> changedPaths;
    std::unique_ptr<RUsdChangeListener> changeListener;
    // A value RUsd_GetAttributeValue read for a size query, held for the call that copies it.
    std::string pendingValueKey;
    VtValue pendingValue;
};

namespace
{
constexpr int kSuccess = 0;
constexpr int kFailure = 1;
constexpr int kApiVersion = 13;
thread_local std::string g_lastError;
thread_local std::string g_lastDiagnostics;

class RUsdDiagnosticDelegate final : public TfDiagnosticMgr::Delegate
{
public:
    RUsdDiagnosticDelegate(std::string* diagnostics, std::mutex* mutex)
        : _diagnostics(diagnostics)
        , _mutex(mutex)
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

    // TfDiagnosticMgr calls delegates on whichever thread posted the diagnostic, several at once
    // when a stage is read in parallel; an unguarded append corrupted the heap.
    void AppendLine(const std::string& line)
    {
        std::lock_guard<std::mutex> lock(*_mutex);
        if (!_diagnostics->empty())
        {
            *_diagnostics += "\n";
        }

        *_diagnostics += line;
    }

    std::string* _diagnostics;
    std::mutex* _mutex;
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
        std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
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

// Animation budgets (API v13). Time samples are untrusted input too -- a line of usda can author
// a million samples on one joint array -- but losing an animation is no reason to lose the stage,
// so these drop the one track, skeleton animation or blend shape with a warning and carry on.
constexpr size_t kMaxImportTrackSamples = 1000000;                // per track
constexpr size_t kMaxImportAnimationValues = 64u * 1024u * 1024u; // floats, every track and shape
constexpr size_t kMaxImportJoints = 65536;                        // per skeleton
constexpr int kMaxImportInfluences = 255;                         // Unity's bones-per-vertex limit

// The context's budgets start as these; RUsd_OpenStageWithLimits replaces them for one import.
static_assert(RUsdContext::ImportLimits{}.nodes == kMaxImportNodes &&
              RUsdContext::ImportLimits{}.meshes == kMaxImportMeshes &&
              RUsdContext::ImportLimits{}.instancePlacements == kMaxImportInstancePlacements &&
              RUsdContext::ImportLimits{}.geometryElements == kMaxImportGeometryElements &&
              RUsdContext::ImportLimits{}.trackSamples == kMaxImportTrackSamples &&
              RUsdContext::ImportLimits{}.animationValues == kMaxImportAnimationValues &&
              RUsdContext::ImportLimits{}.joints == kMaxImportJoints,
              "RUsdContext::ImportLimits defaults must be the kMaxImport* constants");

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
    std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
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

    context->diagnosticDelegate.reset(
        new RUsdDiagnosticDelegate(&context->diagnostics, &context->diagnosticsMutex));
    TfDiagnosticMgr::GetInstance().AddDelegate(context->diagnosticDelegate.get());
}

void DisableDiagnostics(RUsdContext* context)
{
    if (context == nullptr || !context->diagnosticDelegate)
    {
        return;
    }

    // RemoveDelegate waits for calls already in the delegate, so none can follow this.
    TfDiagnosticMgr::GetInstance().RemoveDelegate(context->diagnosticDelegate.get());
    context->diagnosticDelegate.reset();
    std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
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

// The colour space authored for the file an attribute supplies: a UsdUVTexture's
// sourceColorSpace, else the colorSpace metadata on the attribute or, through connections, on the
// interface input that holds the value (Isaac Sim's OmniPBR materials connect each shader input to
// a Material input). Empty when nothing is authored, which USD reads as "auto".
static std::string AuthoredColorSpace(const UsdAttribute& attribute, int depth = 0)
{
    if (!attribute || depth > 8)
    {
        return std::string();
    }

    UsdShadeShader shader(attribute.GetPrim());
    if (shader)
    {
        UsdShadeInput space = shader.GetInput(TfToken("sourceColorSpace"));
        TfToken token;
        if (space && space.Get(&token) && !token.IsEmpty() && token != TfToken("auto"))
        {
            return token.GetString();
        }
    }

    if (attribute.HasColorSpace())
    {
        const TfToken space = attribute.GetColorSpace();
        if (!space.IsEmpty() && space != TfToken("auto"))
        {
            return space.GetString();
        }
    }

    UsdShadeInput input(attribute);
    if (input)
    {
        UsdShadeSourceInfoVector sources = input.GetConnectedSources();
        for (const UsdShadeConnectionSourceInfo& source : sources)
        {
            UsdAttribute next = source.sourceType == UsdShadeAttributeType::Output
                ? source.source.GetOutput(source.sourceName).GetAttr()
                : source.source.GetInput(source.sourceName).GetAttr();
            std::string found = AuthoredColorSpace(next, depth + 1);
            if (!found.empty())
            {
                return found;
            }
        }
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

    if (context != nullptr && source)
    {
        std::string space = AuthoredColorSpace(source);
        if (!space.empty())
        {
            context->assetColorSpaces[path] = space;
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

    // varname is a string in the current schema and was a token before USD 21.11; both occur.
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

// UsdPreviewSurface's specular workflow (useSpecularWorkflow = 1): the reflectance comes from
// specularColor and `metallic` is ignored. Unity's Lit is set up for the metallic workflow, so the
// pair is converted the way Khronos converts glTF's specular-glossiness to metallic-roughness
// (KHR_materials_pbrSpecularGlossiness converter): metallic solved from the diffuse and specular
// brightness, the base colour blended from both. Ignoring the workflow imported a specular metal
// as a dielectric with its diffuse colour (bench sphere: CIEDE2000 45 against Isaac Sim). Only a
// constant specularColor converts; a textured one keeps the metallic reading, with a warning.
static void ConvertSpecularWorkflow(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const std::string& materialPath,
    bool hasAlbedo,
    bool hasMetallicTexture,
    RUsdMaterial* result)
{
    UsdShadeInput workflow = shader.GetInput(TfToken("useSpecularWorkflow"));
    int useSpecular = 0;
    VtValue value;
    if (!workflow || !workflow.Get(&value) || !value.IsHolding<int>())
    {
        return;
    }
    useSpecular = value.UncheckedGet<int>();
    if (useSpecular != 1)
    {
        return;
    }

    UsdShadeInput specularInput = shader.GetInput(TfToken("specularColor"));
    if (specularInput && specularInput.HasConnectedSource())
    {
        AppendImportWarning(context,
            "material '" + materialPath + "' uses the specular workflow with a textured specularColor, "
            "which Unity's metallic Lit cannot take; it is read as the metallic workflow.");
        return;
    }

    GfVec3f specular(0.0f, 0.0f, 0.0f); // the UsdPreviewSurface default
    if (specularInput && specularInput.Get(&value) && value.IsHolding<GfVec3f>())
    {
        specular = value.UncheckedGet<GfVec3f>();
    }

    const float dielectric = 0.04f;
    const float epsilon = 1e-6f;
    auto brightness = [](const GfVec3f& c)
    {
        return std::sqrt(0.299f * c[0] * c[0] + 0.587f * c[1] * c[1] + 0.114f * c[2] * c[2]);
    };

    const GfVec3f diffuse(result->r, result->g, result->b);
    const float specularStrength = std::max(specular[0], std::max(specular[1], specular[2]));
    const float oneMinusSpecular = 1.0f - specularStrength;
    const float diffuseBrightness = brightness(diffuse);
    const float specularBrightness = brightness(specular);

    float metallic = 0.0f;
    if (specularBrightness >= dielectric)
    {
        const float b = diffuseBrightness * oneMinusSpecular / (1.0f - dielectric) + specularBrightness - 2.0f * dielectric;
        const float c = dielectric - specularBrightness;
        const float d = std::max(b * b - 4.0f * dielectric * c, 0.0f);
        metallic = std::min(std::max((-b + std::sqrt(d)) / (2.0f * dielectric), 0.0f), 1.0f);
    }

    if (!hasAlbedo)
    {
        const GfVec3f fromDiffuse = diffuse * (oneMinusSpecular / (1.0f - dielectric) / std::max(1.0f - metallic, epsilon));
        const GfVec3f fromSpecular =
            (specular - GfVec3f(dielectric * (1.0f - metallic))) / std::max(metallic, epsilon);
        const float t = metallic * metallic;
        GfVec3f base = fromDiffuse * (1.0f - t) + fromSpecular * t;
        for (int i = 0; i < 3; ++i)
        {
            base[i] = std::min(std::max(base[i], 0.0f), 1.0f);
        }
        result->r = base[0];
        result->g = base[1];
        result->b = base[2];
    }

    if (!hasMetallicTexture)
    {
        result->metallic = metallic;
    }
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

    // An input the material leaves out takes UsdPreviewSurface's own fallback. Opacity 1,
    // metallic 0 and roughness 0.5 are the defaults above already; diffuseColor's is 0.18 grey,
    // where white stays for a mesh with no material at all.
    result.r = result.g = result.b = 0.18f;

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
        if (!hasAlbedo)
        {
            // A UsdUVTexture that names no file (usd-wg's StandardShaderBall "neutral" material
            // wires diffuseColor to one) still carries the colour the spec says to show in that
            // case: its 'fallback' input. Read it when it is authored; otherwise the shader's own
            // diffuseColor value above stands.
            UsdShadeShader texture = GetConnectedShader(shader, TfToken("diffuseColor"));
            TfToken textureId;
            if (texture && texture.GetShaderId(&textureId) && textureId == TfToken("UsdUVTexture"))
            {
                GetUsdShadeInputColor(texture, TfToken("fallback"), &result);
            }
        }
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

    ConvertSpecularWorkflow(context, shader, materialPath, hasAlbedo, hasMetallic, &result);

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
// ---- Remote MDL modules -----------------------------------------------------------------
// NVIDIA's material library is referenced by URL (info:mdl:sourceAsset =
// @https://omniverse-content-production.s3.../Base/Plastics/Rubber_Smooth.mdl@): each module is
// one OmniPBR call with literal arguments and textures beside it. The resolver cannot open a URL,
// so the import read nothing of it and the SO-101 lightbox's mat (Paper) and rubber (Rubber_Smooth)
// came in default grey. The host downloads the module and its textures into a cache folder
// (RUsd_SetRemoteAssetCache); a module found there is read as the OmniPBR it calls, its arguments
// standing in for every input the stage does not author. Nothing is downloaded here, and the stage
// is not edited: the arguments live in an overlay the MDL readers consult while one material is
// extracted.

static std::mutex g_remoteCacheMutex;
static std::string g_remoteCache;

struct RemoteMdlOverlay
{
    std::string module;                          // the module the call names ("OmniPBR")
    std::map<std::string, VtValue> values;       // argument -> value
    std::map<std::string, std::string> spaces;   // texture argument -> "sRGB" / "raw"
};

static thread_local const std::map<std::string, RemoteMdlOverlay>* t_remoteOverlays = nullptr;

static const RemoteMdlOverlay* FindRemoteOverlay(const UsdShadeShader& shader)
{
    if (t_remoteOverlays == nullptr || !shader)
    {
        return nullptr;
    }
    auto found = t_remoteOverlays->find(shader.GetPath().GetString());
    return found == t_remoteOverlays->end() ? nullptr : &found->second;
}

static bool IsRemoteUrl(const std::string& path)
{
    return path.rfind("https://", 0) == 0 || path.rfind("http://", 0) == 0;
}

// The cache file for a URL: <cache>/<host>/<path>, or empty when the URL climbs (".." segments)
// or carries characters a file name cannot. The host writes downloads to the same place.
static std::string RemoteCachePath(const std::string& url)
{
    std::string cache;
    {
        std::lock_guard<std::mutex> lock(g_remoteCacheMutex);
        cache = g_remoteCache;
    }
    if (cache.empty())
    {
        return std::string();
    }
    const size_t scheme = url.find("://");
    std::string rest = url.substr(scheme + 3);
    const size_t query = rest.find_first_of("?#");
    if (query != std::string::npos)
    {
        rest.resize(query);
    }
    for (const std::string& part : TfStringSplit(rest, "/"))
    {
        if (part == ".." || part == "." || part.find_first_of(":*?\"<>|\\") != std::string::npos)
        {
            return std::string();
        }
    }
    return TfNormPath(TfAbsPath(cache) + "/" + rest);
}

static std::string TrimMdl(const std::string& text)
{
    size_t a = 0, b = text.size();
    while (a < b && std::isspace(static_cast<unsigned char>(text[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(text[b - 1]))) --b;
    return text.substr(a, b - a);
}

// Splits "a: x, b: f(y, z)" at the commas that are not inside brackets or quotes.
static std::vector<std::string> SplitMdlArguments(const std::string& text)
{
    std::vector<std::string> parts;
    int depth = 0;
    bool quoted = false;
    size_t start = 0;
    for (size_t i = 0; i < text.size(); ++i)
    {
        const char c = text[i];
        if (c == '"')
        {
            quoted = !quoted;
        }
        else if (!quoted && (c == '(' || c == '['))
        {
            ++depth;
        }
        else if (!quoted && (c == ')' || c == ']'))
        {
            --depth;
        }
        else if (!quoted && c == ',' && depth == 0)
        {
            parts.push_back(text.substr(start, i - start));
            start = i + 1;
        }
    }
    parts.push_back(text.substr(start));
    return parts;
}

static std::vector<float> RemoteMdlNumbers(const std::string& text)
{
    std::vector<float> numbers;
    const char* p = text.c_str();
    while (*p)
    {
        if (std::isdigit(static_cast<unsigned char>(*p)) || *p == '-' || *p == '+' || *p == '.')
        {
            char* end = nullptr;
            const float value = std::strtof(p, &end);
            if (end != p)
            {
                numbers.push_back(value);
                p = end;
                continue;
            }
        }
        ++p;
    }
    return numbers;
}

// One argument expression as a USD value: color(r, g, b), float2(x, y), a number (with or
// without an f suffix), true / false, texture_2d("file", ::tex::gamma_*). Anything else
// (an enum, a call, an empty texture_2d()) is left out and the input keeps its default.
static bool ParseMdlArgument(const std::string& expression, const std::string& folder, VtValue* value,
                             std::string* space)
{
    const std::string e = TrimMdl(expression);
    if (e.rfind("color(", 0) == 0)
    {
        std::vector<float> n = RemoteMdlNumbers(e.substr(5));
        if (n.size() == 1)
        {
            n = {n[0], n[0], n[0]};
        }
        if (n.size() < 3)
        {
            return false;
        }
        *value = VtValue(GfVec3f(n[0], n[1], n[2]));
        return true;
    }
    if (e.rfind("float2(", 0) == 0)
    {
        std::vector<float> n = RemoteMdlNumbers(e.substr(6));
        if (n.size() == 1)
        {
            n = {n[0], n[0]};
        }
        if (n.size() < 2)
        {
            return false;
        }
        *value = VtValue(GfVec2f(n[0], n[1]));
        return true;
    }
    if (e == "true" || e == "false")
    {
        *value = VtValue(e == "true");
        return true;
    }
    if (e.rfind("texture_2d(", 0) == 0)
    {
        const size_t open = e.find('"');
        const size_t close = open == std::string::npos ? std::string::npos : e.find('"', open + 1);
        if (close == std::string::npos)
        {
            return false;
        }
        std::string file = e.substr(open + 1, close - open - 1);
        if (file.empty() || IsRemoteUrl(file) || file.find("..") != std::string::npos)
        {
            return false;
        }
        if (file.rfind("./", 0) == 0)
        {
            file = file.substr(2);
        }
        *value = VtValue(SdfAssetPath(TfNormPath(folder + "/" + file)));
        if (e.find("gamma_srgb") != std::string::npos)
        {
            *space = "sRGB";
        }
        else if (e.find("gamma_linear") != std::string::npos)
        {
            *space = "raw";
        }
        return true;
    }
    if (e.find('(') != std::string::npos || e.find("::") != std::string::npos)
    {
        return false;
    }
    std::vector<float> n = RemoteMdlNumbers(e);
    if (n.size() != 1)
    {
        return false;
    }
    *value = VtValue(n[0]);
    return true;
}

// Reads a cached remote module that is one call to a material this import knows (OmniPBR):
// `export material Name(*) = OmniPBR(arg: value, ...)`. False when the file is missing, larger
// than 1 MB, or not that shape.
static bool ReadRemoteModule(const std::string& file, RemoteMdlOverlay* overlay)
{
    std::ifstream stream(file, std::ios::binary);
    if (!stream)
    {
        return false;
    }
    std::stringstream contents;
    contents << stream.rdbuf();
    const std::string text = contents.str();
    if (text.size() > 1024 * 1024)
    {
        return false;
    }
    const size_t material = text.find("export material ");
    const size_t equals = material == std::string::npos ? std::string::npos : text.find('=', material);
    if (equals == std::string::npos)
    {
        return false;
    }
    size_t call = equals + 1;
    while (call < text.size() && std::isspace(static_cast<unsigned char>(text[call]))) ++call;
    const size_t open = text.find('(', call);
    if (open == std::string::npos)
    {
        return false;
    }
    std::string callee = TrimMdl(text.substr(call, open - call));
    const size_t scope = callee.rfind("::");
    if (scope != std::string::npos)
    {
        callee = callee.substr(scope + 2);
    }
    if (callee.rfind("OmniPBR", 0) != 0)
    {
        return false;
    }
    int depth = 0;
    size_t close = std::string::npos;
    for (size_t i = open; i < text.size(); ++i)
    {
        if (text[i] == '(') ++depth;
        else if (text[i] == ')' && --depth == 0)
        {
            close = i;
            break;
        }
    }
    if (close == std::string::npos)
    {
        return false;
    }
    overlay->module = callee;
    const std::string folder = TfGetPathName(file);
    for (const std::string& argument : SplitMdlArguments(text.substr(open + 1, close - open - 1)))
    {
        const size_t colon = argument.find(':');
        if (colon == std::string::npos || argument.compare(colon, 2, "::") == 0)
        {
            continue;
        }
        const std::string name = TrimMdl(argument.substr(0, colon));
        VtValue value;
        std::string space;
        if (!name.empty() && ParseMdlArgument(argument.substr(colon + 1), folder, &value, &space))
        {
            overlay->values[name] = value;
            if (!space.empty())
            {
                overlay->spaces[name] = space;
            }
        }
    }
    return true;
}

// Builds the overlay for one MDL shader whose module is a URL: from the cache when it is there,
// otherwise the URL is recorded for the host to fetch.
static void CollectRemoteOverlay(RUsdContext* context, const UsdShadeShader& shader,
                                 std::map<std::string, RemoteMdlOverlay>* overlays)
{
    SdfAssetPath asset;
    if (!shader || !shader.GetSourceAsset(&asset, TfToken("mdl")) || !IsRemoteUrl(asset.GetAssetPath()))
    {
        return;
    }
    const std::string url = asset.GetAssetPath();
    const std::string file = RemoteCachePath(url);
    RemoteMdlOverlay overlay;
    if (!file.empty() && TfIsFile(file) && ReadRemoteModule(file, &overlay))
    {
        (*overlays)[shader.GetPath().GetString()] = overlay;
        return;
    }
    if (context != nullptr && (file.empty() || !TfIsFile(file)) &&
        std::find(context->remoteRequests.begin(), context->remoteRequests.end(), url) ==
            context->remoteRequests.end())
    {
        context->remoteRequests.push_back(url);
    }
}

bool GetMdlInputValue(const UsdShadeShader& shader, const char* name, VtValue* value)
{
    if (const RemoteMdlOverlay* overlay = FindRemoteOverlay(shader))
    {
        UsdShadeInput authored = shader.GetInput(TfToken(name));
        VtValue own;
        bool hasOwn = false;
        if (authored)
        {
            for (const UsdAttribute& attribute : authored.GetValueProducingAttributes())
            {
                if (!UsdShadeOutput::IsOutput(attribute) && attribute.Get(&own) && !own.IsEmpty())
                {
                    hasOwn = true;
                    break;
                }
            }
        }
        if (hasOwn)
        {
            *value = own;
            return true;
        }
        auto found = overlay->values.find(name);
        if (found != overlay->values.end())
        {
            *value = found->second;
            return true;
        }
        return false;
    }

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
    if (!StoreAssetPath(context, value.UncheckedGet<SdfAssetPath>(), buffer, capacity,
                        input ? input.GetAttr() : UsdAttribute()))
    {
        return false;
    }

    const RemoteMdlOverlay* overlay = FindRemoteOverlay(shader);
    if (overlay != nullptr && context != nullptr)
    {
        auto space = overlay->spaces.find(name);
        if (space != overlay->spaces.end() && context->assetColorSpaces.find(buffer) == context->assetColorSpaces.end())
        {
            context->assetColorSpaces[buffer] = space->second;
        }
    }
    return true;
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
    if (const RemoteMdlOverlay* overlay = FindRemoteOverlay(shader))
    {
        return overlay->module;
    }

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

// Nits per unit of Unity emission: Kit's UsdPreviewSurface.mdl scales emissiveColor by
// pi * 1133.6 to reach nits, and emissiveColor matches Unity emission 1:1 on the look bench.
constexpr float kNitsPerUnitEmission = 3.14159265f * 1133.6f;

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

    if (GetMdlTexture(context, shader, "normalmap_texture", m.normalTexturePath, sizeof(m.normalTexturePath)))
    {
        out->normalFlipU = GetMdlBool(shader, "flip_tangent_u", false);
        out->normalFlipV = GetMdlBool(shader, "flip_tangent_v", true);
    }

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
        // emissive_intensity is in nits (default 40). Kit renders a UsdPreviewSurface emissiveColor
        // of E as E * pi * 1133.6 nits (UsdPreviewSurface.mdl, emissiveScale: "the same value as the
        // exposed pixel"), and that emissiveColor imports 1:1 into Unity's emission, so nits divide
        // by the same constant. Normalising by the 40-nit default instead put Franka's 29,000-nit
        // status light at 725x its colour and bleached the robot around it; it is 8.1x now, and the
        // untouched 40-nit default is the near-invisible glow Kit shows for it.
        float intensity = 40.0f;
        GetMdlFloat(shader, "emissive_intensity", &intensity);
        const float scale = std::max(0.0f, intensity / kNitsPerUnitEmission);
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
    // The transmitted share of the light is not diffused, so the diffuse colour keeps only
    // (1 - weight): see the MaterialX path.
    float transmission = 0.0f;
    GetMdlFloat(shader, "specular_transmission_weight", &transmission);
    if (transmission > 0.0f)
    {
        transmission = std::min(transmission, 1.0f);
        m.r *= 1.0f - transmission;
        m.g *= 1.0f - transmission;
        m.b *= 1.0f - transmission;
        m.a = std::min(m.a, 1.0f - 0.75f * transmission);
    }
}

void ReadOmniGlass(RUsdContext* context, const UsdShadeShader& shader,
                   RUsdContext::ImportedMaterial* out)
{
    RUsdMaterial& m = out->material;

    GfVec3f color(1.0f, 1.0f, 1.0f);
    GetMdlColor(shader, "glass_color", &color);
    // A blended Lit surface is a diffuse reflector lit from the front, while OmniGlass is a
    // transmitter that only sends back its Fresnel reflection and (when frosted) a thin haze.
    // Rendering glass_color as the diffuse colour made every pane a lit white sheet: on NVIDIA's
    // Factory the cell fence (glass_color 0.94) measured 179 against Isaac's 107 on the same
    // pixels, and the clear roof the same. Scaling the diffuse colour by kGlassDiffuse puts the
    // pane at Isaac's level and leaves the specular (smoothness from frosting) to carry the look.
    // 0.35 still read as a lit sheet next to Isaac's pane once the lights were taken out of the
    // comparison; 0.1 leaves the pane to its reflection and transmission.
    constexpr float kGlassDiffuse = 0.1f;
    SetColor(&m, color * kGlassDiffuse);
    m.metallic = 0.0f;
    float frosting = 0.0f;
    GetMdlFloat(shader, "frosting_roughness", &frosting);
    frosting = std::max(0.0f, frosting);
    // Frosting blurs both the reflection and what shows through. The reflection blur maps onto
    // roughness directly; 2.5x because Isaac's frosting 0.1 pane reads like a roughness 0.25
    // mirror side by side (Factory cell fence, lights off, uniform dome).
    m.roughness = std::min(1.0f, 2.5f * frosting);

    // Opacity. A real-time blend has no rough refraction, so a frosted pane cannot blur what is
    // behind it; the closest a blend gets is to show less of it. Clear glass (frosting 0) stays
    // at 0.25. Frosting raises the opacity -- Factory's Glass_Tinted_Mid (0.1) rendered in Isaac
    // as a milky, mosaic-like pane while the 0.25 blend was see-through -- up to 0.85. A
    // thin-walled pane (a single surface, no volume to frost) stays clear.
    // What shows through cannot be blurred by a blended surface (no rough refraction in URP or
    // HDRP's Lit), so it is veiled instead: a uniform haze by alpha. Known limitation: Isaac's
    // frosted pane blurs the view more with distance; this veil is the same at every depth.
    const bool thinWalled = GetMdlBool(shader, "thin_walled", false);
    m.a = thinWalled ? 0.25f : std::min(0.85f, 0.25f + 2.5f * frosting);

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

static std::string LowerAscii(std::string text)
{
    for (char& c : text)
    {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return text;
}

static bool EndsWithAny(const std::string& text, std::initializer_list<const char*> suffixes)
{
    for (const char* suffix : suffixes)
    {
        const size_t n = std::strlen(suffix);
        if (text.size() >= n && text.compare(text.size() - n, n, suffix) == 0)
        {
            return true;
        }
    }
    return false;
}

// What an MDL module file says that USD does not: the default of every parameter the stage left
// unauthored, and the textures its body reads as literals. The Unreal connector writes both --
// RoughnessMin/RoughnessMax and MaskSelection often stay at their module defaults, and modules
// such as M_AisleSign take no parameters at all and name their textures inside the body.
struct MdlModuleSource
{
    std::map<std::string, std::string> defaults;   // parameter -> default expression, as written
    std::vector<std::string> bodyTextures;         // absolute paths of texture_2d("...") literals in the body
    std::string folder;                            // folder of the module file, for relative paths
    // Texture parameters the body blends over the base colour with Unreal's BlendOverlay, which
    // the connector writes out as a per-channel ">= 0.5 ?" select on the parameter's lookup
    // (M_AisleSign: the aisle number and "BOXES G - I" over the sign's diffuse map).
    std::set<std::string> overlayParameters;
};

static std::string TrimAscii(const std::string& text)
{
    size_t a = 0, b = text.size();
    while (a < b && std::isspace(static_cast<unsigned char>(text[a]))) ++a;
    while (b > a && std::isspace(static_cast<unsigned char>(text[b - 1]))) --b;
    return text.substr(a, b - a);
}

// texture_2d("./Textures/x.png", ...) -> "./Textures/x.png", or empty.
static std::string MdlTextureLiteral(const std::string& expression)
{
    const size_t at = expression.find("texture_2d(\"");
    if (at == std::string::npos)
    {
        return std::string();
    }
    const size_t begin = at + 12;
    const size_t end = expression.find('"', begin);
    return end == std::string::npos ? std::string() : expression.substr(begin, end - begin);
}

static const MdlModuleSource* LoadMdlModuleSource(const UsdShadeShader& shader)
{
    static std::map<std::string, std::unique_ptr<MdlModuleSource>> cache;
    SdfAssetPath asset;
    if (!shader.GetSourceAsset(&asset, TfToken("mdl")))
    {
        return nullptr;
    }
    const std::string file = asset.GetResolvedPath();
    if (file.empty() || ArIsPackageRelativePath(file))
    {
        return nullptr;
    }
    auto found = cache.find(file);
    if (found != cache.end())
    {
        return found->second.get();
    }

    auto module = std::make_unique<MdlModuleSource>();
    module->folder = TfGetPathName(file);
    std::ifstream stream(file, std::ios::binary);
    std::stringstream contents;
    contents << stream.rdbuf();
    const std::string text = contents.str();
    if (text.size() > 4 * 1024 * 1024)
    {
        cache[file] = nullptr;
        return nullptr;
    }

    const size_t material = text.find("export material ");
    const size_t open = material == std::string::npos ? std::string::npos : text.find('(', material);
    size_t close = std::string::npos;
    if (open != std::string::npos)
    {
        int depth = 0;
        size_t start = open + 1;
        for (size_t i = open; i < text.size(); ++i)
        {
            const char c = text[i];
            if (c == '(' || c == '[')
            {
                ++depth;
            }
            else if (c == ')' || c == ']')
            {
                --depth;
            }
            const bool last = depth == 0;
            if ((c == ',' && depth == 1) || last)
            {
                // One parameter: "uniform? type name = expression [[annotations]]".
                std::string param = text.substr(start, i - start);
                const size_t annotations = param.find("[[");
                if (annotations != std::string::npos)
                {
                    param = param.substr(0, annotations);
                }
                const size_t equals = param.find('=');
                if (equals != std::string::npos)
                {
                    std::string left = TrimAscii(param.substr(0, equals));
                    const size_t space = left.find_last_of(" \t\r\n");
                    const std::string name = space == std::string::npos ? left : left.substr(space + 1);
                    module->defaults[name] = TrimAscii(param.substr(equals + 1));
                }
                start = i + 1;
            }
            if (last)
            {
                close = i;
                break;
            }
        }
    }

    const size_t body = close == std::string::npos ? 0 : close;
    for (size_t at = text.find("texture_2d(\"", body); at != std::string::npos; at = text.find("texture_2d(\"", at + 1))
    {
        const std::string relative = MdlTextureLiteral(text.substr(at, 1024));
        if (!relative.empty())
        {
            module->bodyTextures.push_back(TfNormPath(module->folder + "/" + relative));
        }
    }

    // BlendOverlay: "float4 LocalN = tex::lookup_float4(Param, ...)" followed by a select on
    // "(float3(LocalN.x,LocalN.y,LocalN.z).x >= 0.5) ? ..." for each channel.
    for (const auto& entry : module->defaults)
    {
        if (MdlTextureLiteral(entry.second).empty())
        {
            continue;
        }

        const std::string lookup = "tex::lookup_float4(" + entry.first + ",";
        for (size_t at = text.find(lookup, body); at != std::string::npos; at = text.find(lookup, at + 1))
        {
            const size_t equals = text.rfind('=', at);
            const size_t lineStart = text.rfind('\n', at);
            if (equals == std::string::npos || (lineStart != std::string::npos && equals < lineStart))
            {
                continue;
            }

            std::string left = TrimAscii(text.substr(lineStart == std::string::npos ? 0 : lineStart + 1,
                                                     equals - (lineStart == std::string::npos ? 0 : lineStart + 1)));
            const size_t space = left.find_last_of(" \t");
            const std::string local = space == std::string::npos ? left : left.substr(space + 1);
            if (local.empty())
            {
                continue;
            }

            const std::string select = local + ".x," + local + ".y," + local + ".z).x >= 0.5)";
            if (text.find(select, at) != std::string::npos)
            {
                module->overlayParameters.insert(entry.first);
                break;
            }
        }
    }

    const MdlModuleSource* result = module.get();
    cache[file] = std::move(module);
    return result;
}

// Numbers inside "float4(0.1,0.2,0.3,1.0)", "color(...)", "0.5".
static std::vector<float> MdlNumbers(const std::string& expression)
{
    std::vector<float> values;
    const char* p = expression.c_str();
    while (*p)
    {
        if ((*p >= '0' && *p <= '9') || *p == '-' || *p == '.')
        {
            char* end = nullptr;
            const float v = std::strtof(p, &end);
            if (end != p)
            {
                values.push_back(v);
                p = end;
                continue;
            }
        }
        ++p;
    }
    return values;
}

static bool UnrealColor(const UsdShadeShader& shader, const MdlModuleSource* module, const char* name, GfVec3f* out)
{
    if (GetMdlColor(shader, name, out))
    {
        return true;
    }
    if (module == nullptr)
    {
        return false;
    }
    auto found = module->defaults.find(name);
    if (found == module->defaults.end())
    {
        return false;
    }
    // Skip the leading type token's digits ("float4"): numbers start after the first '('.
    const size_t open = found->second.find('(');
    const std::vector<float> v = MdlNumbers(open == std::string::npos ? found->second : found->second.substr(open));
    if (v.size() < 3)
    {
        return false;
    }
    *out = GfVec3f(v[0], v[1], v[2]);
    return true;
}

static bool UnrealFloat(const UsdShadeShader& shader, const MdlModuleSource* module, const char* name, float* out)
{
    if (GetMdlFloat(shader, name, out))
    {
        return true;
    }
    if (module == nullptr)
    {
        return false;
    }
    auto found = module->defaults.find(name);
    if (found == module->defaults.end())
    {
        return false;
    }
    const std::vector<float> v = MdlNumbers(found->second);
    if (v.size() != 1)
    {
        return false;
    }
    *out = v[0];
    return true;
}

// Unreal-connector MDL (see ExtractMdlMaterial): picks the albedo, normal and ORM maps by
// parameter name, then by the Unreal texture suffixes (_D, _N, _ORM), takes a tint only from a
// parameter named *Tint (ColorAlbedo-style parameters are mask colours in these graphs, not
// multipliers), and the constant Roughness/Metallic and U/V tiling where they exist. Returns
// false when the shader carries no texture a connector material would have.
bool ReadUnrealConnectorMdl(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const std::string& materialPath,
    RUsdContext::ImportedMaterial* out)
{
    const MdlModuleSource* module = LoadMdlModuleSource(shader);

    // Texture candidates: authored inputs, then module defaults the stage left unauthored, then
    // literals in the module body. Each is a parameter name (empty for a literal) and a path.
    struct Candidate { std::string name; std::string path; bool authored; };
    std::vector<Candidate> candidates;
    std::set<std::string> authoredNames;
    for (const UsdShadeInput& input : shader.GetInputs())
    {
        VtValue value;
        const std::string name = input.GetBaseName().GetString();
        if (GetMdlInputValue(shader, name.c_str(), &value) && VtValueHoldsType(value, "SdfAssetPath"))
        {
            candidates.push_back({name, value.UncheckedGet<SdfAssetPath>().GetAssetPath(), true});
            authoredNames.insert(name);
        }
    }
    if (module != nullptr)
    {
        for (const auto& entry : module->defaults)
        {
            const std::string relative = MdlTextureLiteral(entry.second);
            if (!relative.empty() && authoredNames.count(entry.first) == 0)
            {
                candidates.push_back({entry.first, TfNormPath(module->folder + "/" + relative), false});
            }
        }
        for (const std::string& path : module->bodyTextures)
        {
            candidates.push_back({std::string(), path, false});
        }
    }

    int albedo = -1, normal = -1, orm = -1, mask = -1;
    int albedoByName = -1, normalByName = -1, ormByName = -1;
    for (int i = 0; i < static_cast<int>(candidates.size()); ++i)
    {
        const std::string lname = LowerAscii(candidates[i].name);
        const std::string stem = LowerAscii(TfStringGetBeforeSuffix(TfGetBaseName(candidates[i].path)));
        if (albedoByName < 0 && (lname == "albedotexture" || lname == "basecolor_texture" || lname == "basecolortexture" ||
                                 lname == "basecolor" || lname == "diffuse_texture" || lname == "texture_basecolor"))
        {
            albedoByName = i;
        }
        else if (normalByName < 0 && lname.find("normal") != std::string::npos)
        {
            normalByName = i;
        }
        else if (ormByName < 0 && (lname.find("mergemap") != std::string::npos || lname.find("orm") != std::string::npos ||
                                   lname.find("multimap") != std::string::npos))
        {
            ormByName = i;
        }
        else if (mask < 0 && (lname.find("mask") != std::string::npos || (lname.empty() && EndsWithAny(stem, {"_m"}))))
        {
            mask = i;
        }
        if (albedo < 0 && EndsWithAny(stem, {"_d", "_basecolor", "_albedo", "_diffuse"}))
        {
            albedo = i;
        }
        else if (normal < 0 && EndsWithAny(stem, {"_n", "_normal"}))
        {
            normal = i;
        }
        else if (orm < 0 && EndsWithAny(stem, {"_orm"}))
        {
            orm = i;
        }
    }
    if (albedoByName >= 0) albedo = albedoByName;
    if (normalByName >= 0) normal = normalByName;
    if (ormByName >= 0) orm = ormByName;
    if (albedo < 0 && normal < 0 && orm < 0)
    {
        return false;
    }

    auto store = [&](int index, char* buffer, size_t capacity) -> bool
    {
        if (index < 0)
        {
            return false;
        }
        const Candidate& c = candidates[static_cast<size_t>(index)];
        if (c.authored)
        {
            return GetMdlTexture(context, shader, c.name.c_str(), buffer, capacity);
        }
        return StoreAssetPath(context, SdfAssetPath(c.path, c.path), buffer, capacity);
    };

    RUsdMaterial& m = out->material;
    GfVec3f tint(1.0f, 1.0f, 1.0f);
    for (const UsdShadeInput& input : shader.GetInputs())
    {
        const std::string lname = LowerAscii(input.GetBaseName().GetString());
        if (lname.size() >= 4 && lname.compare(lname.size() - 4, 4, "tint") == 0 &&
            lname.find("plastic") == std::string::npos)
        {
            GetMdlColor(shader, input.GetBaseName().GetString().c_str(), &tint);
            break;
        }
    }
    SetColor(&m, albedo < 0 ? GfVec3f(0.5f, 0.5f, 0.5f) : tint);
    store(albedo, m.albedoTexturePath, sizeof(m.albedoTexturePath));
    store(normal, m.normalTexturePath, sizeof(m.normalTexturePath));
    if (store(orm, m.metallicTexturePath, sizeof(m.metallicTexturePath)))
    {
        out->metallicIsOrm = true;
    }
    m.metallic = 0.0f;
    UnrealFloat(shader, module, "Metallic", &m.metallic);
    m.roughness = 0.5f;
    UnrealFloat(shader, module, "Roughness", &m.roughness);
    float u = 1.0f, v = 1.0f;
    const bool hasU = UnrealFloat(shader, module, "U_Tiling", &u);
    const bool hasV = UnrealFloat(shader, module, "V_Tiling", &v);
    if (hasU || hasV)
    {
        m.uvScale[0] = u;
        m.uvScale[1] = v;
    }

    // The colour mask: a single colour where the mask is 0 (ColorAlbedo, MI_Floor_01), or three
    // colours painted by the mask's red, green and blue with the albedo in alpha (Body, Handle,
    // Cap: MI_PushcartA_01, barrels and crates).
    char maskPath[1024] = {0};
    if (albedo >= 0 && store(mask, maskPath, sizeof(maskPath)))
    {
        GfVec3f body, handle, cap, single;
        if (UnrealColor(shader, module, "Body", &body))
        {
            UnrealColor(shader, module, "Handle", &handle);
            UnrealColor(shader, module, "Cap", &cap);
            out->maskMode = 1;
            out->maskTexturePath = maskPath;
            const GfVec3f colours[3] = {body, handle, cap};
            for (int i = 0; i < 3; ++i)
            {
                out->channelColors[i * 3] = colours[i][0];
                out->channelColors[i * 3 + 1] = colours[i][1];
                out->channelColors[i * 3 + 2] = colours[i][2];
            }
        }
        else if (UnrealColor(shader, module, "ColorAlbedo", &single))
        {
            out->maskMode = 0;
            out->maskTexturePath = maskPath;
            out->maskColor[0] = single[0];
            out->maskColor[1] = single[1];
            out->maskColor[2] = single[2];
        }
    }

    // An overlay texture (BlendOverlay over the albedo, mode 2): the host bakes
    // t >= 0.5 ? 1 - 2(1 - t)(1 - albedo) : 2 t albedo per channel into the albedo.
    if (out->maskMode < 0 && albedo >= 0 && module != nullptr && !module->overlayParameters.empty())
    {
        for (int i = 0; i < static_cast<int>(candidates.size()); ++i)
        {
            if (candidates[static_cast<size_t>(i)].name.empty() ||
                module->overlayParameters.count(candidates[static_cast<size_t>(i)].name) == 0)
            {
                continue;
            }

            char overlayPath[1024] = {0};
            if (store(i, overlayPath, sizeof(overlayPath)))
            {
                out->maskMode = 2;
                out->maskTexturePath = overlayPath;
            }

            break;
        }
    }

    float rmin = 0.0f, rmax = 1.0f;
    if (out->metallicIsOrm && UnrealFloat(shader, module, "RoughnessMin", &rmin) &&
        UnrealFloat(shader, module, "RoughnessMax", &rmax))
    {
        out->hasRoughnessRange = true;
        out->roughnessMin = rmin;
        out->roughnessMax = rmax;
    }

    AppendImportWarning(context,
        "material '" + materialPath + "' uses Unreal-connector MDL module '" + GetMdlShaderName(shader) +
        "'; its maps, mask colours and roughness range are read by name, and the rest of its graph is not evaluated.");
    return true;
}

// Returns false when the MDL module is not one this reader understands.
bool ExtractMdlMaterial(
    RUsdContext* context,
    const UsdShadeShader& shader,
    const std::string& materialPath,
    RUsdContext::ImportedMaterial* out,
    bool allowPartial = true)
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

    // Materials exported by the Omniverse Unreal Engine connector: each Unreal material instance
    // becomes its own MDL module built on OmniUe4Base, and its inputs are the Unreal parameter
    // names (AlbedoTexture, BaseColor_Texture, MainNormalInput, MergeMapInput, ...). No two
    // modules share a signature, so nothing above matched them and NVIDIA's Simple_Warehouse
    // (5,914 materials) imported untextured. The parameters follow the connector's conventions
    // closely enough to read by name and by texture suffix.
    if (ReadUnrealConnectorMdl(context, shader, materialPath, out))
    {
        return true;
    }

    // Unknown module: take a base colour if it carries one under a common name. ExtractMaterial
    // asks for this only when the material has nothing better (no supported MaterialX or
    // UsdPreviewSurface network), since the renderer draws the whole module and a full preview
    // network is closer to that than one colour.
    if (!allowPartial)
    {
        return false;
    }

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
    const char* specularWeight;
};

const MtlxSurfaceInputs* FindMtlxSurfaceInputs(const std::string& id)
{
    static const MtlxSurfaceInputs standardSurface = {
        "base_color", "base", "metalness", "specular_roughness", "normal",
        "emission_color", "emission", "opacity", "transmission", "transmission_color", "specular"};
    static const MtlxSurfaceInputs openPbr = {
        "base_color", "base_weight", "base_metalness", "specular_roughness", "geometry_normal",
        "emission_color", "emission_luminance", "geometry_opacity", "transmission_weight", "transmission_color",
        "specular_weight"};
    static const MtlxSurfaceInputs previewSurface = {
        "diffuseColor", "", "metallic", "roughness", "normal", "emissiveColor", "", "opacity", "", "", ""};

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

    // A dielectric with its specular layer weighted off has no reflection at all, whatever its
    // roughness says (the Standard Shader Ball's neutral: specular_weight 0, roughness 0, which
    // Isaac Sim renders matte). Unity's Lit has no specular weight, so the closest is a fully
    // rough surface rather than the mirror roughness 0 would give.
    float specularWeight = 1.0f;
    if (GetMtlxFloat(shader, shaderId, inputs.specularWeight, &specularWeight) && specularWeight <= 0.0f &&
        m.metallic <= 0.0f)
    {
        m.roughness = 1.0f;
    }
    // A MaterialX normal image is a vector3: MaterialX colour-manages only colour types, so with
    // no colorspace authored it is data, not "auto". Recorded as raw so the host does not read it
    // as sRGB the way an unauthored 8-bit OmniPBR or UsdUVTexture normal map is read.
    if (ReadMtlxTexture(context, shader, inputs.normal, m.normalTexturePath, sizeof(m.normalTexturePath)) &&
        context != nullptr && context->assetColorSpaces.find(m.normalTexturePath) == context->assetColorSpaces.end())
    {
        context->assetColorSpaces[m.normalTexturePath] = "raw";
    }

    GfVec3f opacity(1.0f, 1.0f, 1.0f);
    if (GetMtlxColor(shader, shaderId, inputs.opacity, &opacity))
    {
        m.a = (opacity[0] + opacity[1] + opacity[2]) / 3.0f;
    }

    // Transmission (glass: OpenChessSet's pawn tops) has no real-time equivalent. Left out, a
    // fully transmissive surface with base_color 1 imported as an opaque white ball. It becomes
    // partly transparent, and its diffuse colour keeps only the (1 - weight) share standard_surface
    // gives the diffuse lobe: the transmitted light is not scattered back. The stand-in used to
    // paint the transmission colour on as diffuse instead, and under the Reference Look's bright uniform
    // dome every clear glass still read as a white ball; Isaac Sim, refracting the board below,
    // draws them dark. Alpha blending cannot tint what is behind, so the colour is not kept.
    float transmission = 0.0f;
    if (!hasAlbedo && GetMtlxFloat(shader, shaderId, inputs.transmission, &transmission) && transmission > 0.0f)
    {
        transmission = std::min(transmission, 1.0f);
        const GfVec3f surface(m.r, m.g, m.b);
        SetColor(&m, surface * (1.0f - transmission));
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
    // metalness and roughness as separate greyscale images, the layout OmniPBR's
    // metallic_texture + reflectionroughness_texture has: they are reported as a plain metallic
    // map and a roughness map, and the host packs them while it holds the pixels (API 10), the
    // constants above standing in for whichever is not a texture. They used to be dropped with a
    // warning, which left OpenChessSet's gold crowns and borders flat cream and its marble one
    // uniform 0.5 smoothness.
    if (ReadMtlxTexture(context, shader, inputs.metallic, m.metallicTexturePath, sizeof(m.metallicTexturePath)))
    {
        out->metallicIsPlain = true;
    }

    // A specular layer weighted off made the surface fully rough above; a roughness map would
    // undo that, so it is only taken while the layer reflects.
    char roughnessPath[1024] = {0};
    if (!(specularWeight <= 0.0f && m.metallic <= 0.0f && !out->metallicIsPlain) &&
        ReadMtlxTexture(context, shader, inputs.roughness, roughnessPath, sizeof(roughnessPath)))
    {
        out->roughnessTexturePath = roughnessPath;
    }
}

// The packed metallic(.r) + smoothness(.a) map this exporter writes into a UsdPreviewSurface:
// `metallic` reads .r and `roughness` reads .a of one file (ExtractPreviewMaterial's packing
// test, plus the channels). Writes that file into `buffer`; false for any other layout.
bool ReadPreviewPackedMetallicMap(
    RUsdContext* context,
    const UsdShadeShader& preview,
    char* buffer,
    size_t capacity)
{
    auto sourceOutput = [&preview](const char* name)
    {
        UsdShadeInput input = preview.GetInput(TfToken(name));
        UsdShadeSourceInfoVector sources = input ? input.GetConnectedSources() : UsdShadeSourceInfoVector();
        return sources.empty() ? TfToken() : sources[0].sourceName;
    };
    if (sourceOutput("metallic") != TfToken("r") || sourceOutput("roughness") != TfToken("a"))
    {
        return false;
    }

    char metallicPath[1024] = {0};
    char roughnessPath[1024] = {0};
    if (!ReadConnectedTextureFile(preview, TfToken("metallic"), metallicPath, sizeof(metallicPath)) ||
        !ReadConnectedTextureFile(preview, TfToken("roughness"), roughnessPath, sizeof(roughnessPath)) ||
        std::strcmp(metallicPath, roughnessPath) != 0)
    {
        return false;
    }

    return ReadConnectedTexture(context, preview, TfToken("metallic"), buffer, capacity, nullptr) ==
        TextureReadStatus::Ok;
}

// An OmniPBR or MaterialX flavour exported beside a UsdPreviewSurface (MaterialFlavors) cannot
// carry Unity's packed metallic/smoothness map, which neither has an input for, so that map
// lives only in the UsdPreviewSurface. Reading the MDL or MaterialX network first, as Isaac Sim
// renders it, dropped it on the way back in. When the network read has no metallic or roughness
// map of its own and `preview` has this exporter's packed map, that one map is taken from it.
// Every value the network sets, which is what Isaac Sim renders, stays as read; a network with
// any other UsdPreviewSurface beside it imports exactly as before.
void TakePreviewPackedMetallicMap(
    RUsdContext* context,
    const UsdShadeShader& preview,
    RUsdContext::ImportedMaterial* out)
{
    if (preview && out->material.metallicTexturePath[0] == '\0' && out->roughnessTexturePath.empty())
    {
        ReadPreviewPackedMetallicMap(context, preview, out->material.metallicTexturePath,
                                     sizeof(out->material.metallicTexturePath));
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

    // The render contexts in Isaac Sim's order (Omniverse RTX, measured on Isaac Sim 6.2): an
    // MDL network wins, then MaterialX, then the universal UsdPreviewSurface. A material that
    // carries more than one renders from the first, so the import reads the same one: the
    // Standard Shader Ball's `neutral` has a UsdPreviewSurface with an 18% grey texture and a
    // MaterialX OpenPBR with no base colour, and Isaac Sim draws it white (OpenPBR's default).
    // A known MDL module and a supported MaterialX surface are read in full; an unknown MDL
    // module yields only a base colour, so it is used only when nothing else is there.
    UsdShadeShader universal = material.ComputeSurfaceSource();
    TfToken shaderId;
    const bool hasPreview =
        universal && universal.GetShaderId(&shaderId) && shaderId == TfToken("UsdPreviewSurface");

    // ComputeSurfaceSource falls back to the universal output when a context has none of its own.
    UsdShadeShader mdl = material.ComputeSurfaceSource(TfTokenVector{TfToken("mdl")});
    if (mdl && universal && mdl.GetPath() == universal.GetPath())
    {
        mdl = UsdShadeShader();
    }

    // A module named by URL is read from the host's download cache, if it is there.
    std::map<std::string, RemoteMdlOverlay> overlays;
    CollectRemoteOverlay(context, mdl, &overlays);
    struct OverlayScope
    {
        const std::map<std::string, RemoteMdlOverlay>* previous;
        explicit OverlayScope(const std::map<std::string, RemoteMdlOverlay>* current) : previous(t_remoteOverlays)
        {
            t_remoteOverlays = current;
        }
        ~OverlayScope() { t_remoteOverlays = previous; }
    } overlayScope(overlays.empty() ? t_remoteOverlays : &overlays);

    if (mdl && ExtractMdlMaterial(context, mdl, materialPath, out, false))
    {
        if (StartsWith(GetMdlShaderName(mdl), "OmniPBR"))
        {
            TakePreviewPackedMetallicMap(context, hasPreview ? universal : UsdShadeShader(), out);
        }

        return true;
    }

    out->material = DefaultImportMaterial();
    UsdShadeShader mtlx = material.ComputeSurfaceSource(TfTokenVector{TfToken("mtlx")});
    TfToken mtlxId;
    const MtlxSurfaceInputs* mtlxInputs = nullptr;
    if (mtlx && !(universal && mtlx.GetPath() == universal.GetPath()) && mtlx.GetShaderId(&mtlxId))
    {
        mtlxInputs = FindMtlxSurfaceInputs(mtlxId.GetString());
        if (mtlxInputs != nullptr)
        {
            ExtractMtlxMaterial(context, mtlx, mtlxId, *mtlxInputs, materialPath, out);

            // The MaterialX flavour is a standard_surface with metalness and roughness as
            // constants. One whose metalness or roughness is a texture was not written that
            // way, and ExtractMtlxMaterial has already said those maps are not used.
            char unused[1024] = {0};
            if (mtlxId == TfToken("ND_standard_surface_surfaceshader") &&
                !ReadMtlxTexture(nullptr, mtlx, mtlxInputs->metallic, unused, sizeof(unused)) &&
                !ReadMtlxTexture(nullptr, mtlx, mtlxInputs->roughness, unused, sizeof(unused)))
            {
                TakePreviewPackedMetallicMap(context, hasPreview ? universal : UsdShadeShader(), out);
            }

            return true;
        }
    }

    if (hasPreview)
    {
        out->material = ExtractPreviewMaterial(context, material, out);
        ExtractOpacityInfo(context, material, &out->opacityTexturePath, &out->opacityThreshold);
        return true;
    }

    if (mdl && ExtractMdlMaterial(context, mdl, materialPath, out, true))
    {
        return true;
    }

    if (mtlx && !(universal && mtlx.GetPath() == universal.GetPath()) && !mtlxId.IsEmpty())
    {
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

bool ReadVec3Array(const UsdAttribute& attribute, VtArray<GfVec3f>* out, UsdTimeCode time = UsdTimeCode::Default());

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

    if (triangleIndices > static_cast<int64_t>(context->importLimits.geometryElements))
    {
        AppendImportWarning(context,
            "mesh '" + meshPrim.GetPath().GetString() + "' triangulates to " +
            std::to_string(triangleIndices) + " indices, over the import limit of " +
            std::to_string(context->importLimits.geometryElements) + "; mesh skipped.");
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
        subset.GetIndicesAttr().Get(&subsetFaces, context->importTime);
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
// plugin/monolithic-OpenUSD boundary (hidden visibility), so the held value's registered type
// name decides which UncheckedGet is safe. See BUILD_NOTES.md. It has to be the value's: the
// attribute's declared type name is only what the layer claims, and a layer whose value is of
// another type (a crafted usdc, a damaged Alembic file read through usdAbc) had UncheckedGet
// reinterpret that value's storage as the declared type.
bool ReadVec2Array(const UsdAttribute& attribute, VtArray<GfVec2f>* out, UsdTimeCode time = UsdTimeCode::Default())
{
    if (!attribute || out == nullptr)
    {
        return false;
    }

    VtValue value;
    if (!attribute.Get(&value, time) || value.IsEmpty())
    {
        return false;
    }

    if (VtValueHoldsType(value, "VtArray<GfVec2f>"))
    {
        *out = value.UncheckedGet<VtArray<GfVec2f>>();
    }
    else if (VtValueHoldsType(value, "VtArray<GfVec2d>"))
    {
        const VtArray<GfVec2d>& source = value.UncheckedGet<VtArray<GfVec2d>>();
        out->resize(source.size());
        for (size_t i = 0; i < source.size(); ++i)
        {
            (*out)[i] = GfVec2f(static_cast<float>(source[i][0]), static_cast<float>(source[i][1]));
        }
    }
    else if (VtValueHoldsType(value, "VtArray<GfVec2h>"))
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

bool ReadVec3Array(const UsdAttribute& attribute, VtArray<GfVec3f>* out, UsdTimeCode time)
{
    if (!attribute || out == nullptr)
    {
        return false;
    }

    VtValue value;
    if (!attribute.Get(&value, time) || value.IsEmpty())
    {
        return false;
    }

    // The held value's type, as in ReadVec2Array: every float3 role (point, normal, vector,
    // color) holds VtArray<GfVec3f>, and the double ones VtArray<GfVec3d>.
    if (VtValueHoldsType(value, "VtArray<GfVec3f>"))
    {
        *out = value.UncheckedGet<VtArray<GfVec3f>>();
    }
    else if (VtValueHoldsType(value, "VtArray<GfVec3d>"))
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

void ReadAttrLayout(const UsdGeomPrimvar& primvar, int valueCount, ImportAttrLayout* layout,
                    UsdTimeCode time = UsdTimeCode::Default())
{
    layout->interpolation = primvar.GetInterpolation();
    layout->indices.clear();
    if (primvar.IsIndexed())
    {
        primvar.GetIndices(&layout->indices, time);
    }

    layout->valueCount = valueCount;
    layout->valid = valueCount > 0;
}

bool LoadUvSource(const UsdGeomPrimvar& primvar, ImportUvSource* source, UsdTimeCode time)
{
    if (!primvar || !IsUvPrimvarType(primvar) || !ReadVec2Array(primvar.GetAttr(), &source->values, time))
    {
        return false;
    }

    ReadAttrLayout(primvar, static_cast<int>(source->values.size()), &source->layout, time);
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

void ResolveUvSources(const UsdGeomMesh& mesh, ImportUvSource sources[RUSD_MAX_UV_SETS], UsdTimeCode time)
{
    UsdGeomPrimvarsAPI primvars(mesh.GetPrim());

    const std::vector<std::vector<TfToken>>& aliases = UvSetAliasTokens();
    std::vector<std::string> claimed;
    for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
    {
        for (const TfToken& name : aliases[static_cast<size_t>(set)])
        {
            if (LoadUvSource(primvars.GetPrimvar(name), &sources[set], time))
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
            if (!sources[set].layout.valid && LoadUvSource(primvar, &sources[set], time))
            {
                claimed.push_back(name);
                break;
            }
        }
    }
}

void ResolveNormalSource(const UsdGeomMesh& mesh, ImportNormalSource* source, UsdTimeCode time)
{
    if (ReadVec3Array(mesh.GetNormalsAttr(), &source->values, time))
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
    if (primvar && ReadVec3Array(primvar.GetAttr(), &source->values, time))
    {
        ReadAttrLayout(primvar, static_cast<int>(source->values.size()), &source->layout, time);
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

DisplayColor ReadDisplayColor(const UsdPrim& prim, UsdTimeCode time)
{
    DisplayColor result;
    UsdGeomPrimvarsAPI primvars(prim);

    // With inheritance: a colour authored once on a parent Xform applies to every mesh below it.
    UsdGeomPrimvar color = primvars.FindPrimvarWithInheritance(UsdGeomTokens->primvarsDisplayColor);
    VtArray<GfVec3f> colors;
    if (color && ReadVec3Array(color.GetAttr(), &colors, time))
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
    if (opacity && opacity.GetAttr().Get(&value, time) && VtValueHoldsType(value, "VtArray<float>"))
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
    RUsdContext::ImportedMesh* importedMesh,
    bool recordVertexPoints)
{
    if (context == nullptr || !mesh || xformCache == nullptr || importedMesh == nullptr)
    {
        return false;
    }

    const UsdTimeCode time = context->importTime;
    VtArray<GfVec3f> points;
    if (!mesh.GetPointsAttr().Get(&points, time) || points.empty())
    {
        return false;
    }

    importedMesh->sourcePointCount = points.size();

    VtIntArray faceVertexCounts;
    VtIntArray faceVertexIndices;
    if (!mesh.GetFaceVertexCountsAttr().Get(&faceVertexCounts, time) ||
        !mesh.GetFaceVertexIndicesAttr().Get(&faceVertexIndices, time) ||
        faceVertexCounts.empty() ||
        faceVertexIndices.empty())
    {
        return false;
    }

    // Everything below allocates per corner (split tables, normals, UVs) before triangulation
    // counts its output, so an oversized corner list is refused here, before any of it.
    if (faceVertexIndices.size() > context->importLimits.geometryElements)
    {
        AppendImportWarning(context,
            "mesh '" + mesh.GetPath().GetString() + "' has " +
            std::to_string(faceVertexIndices.size()) + " face-vertex indices, over the import limit of " +
            std::to_string(context->importLimits.geometryElements) + "; mesh skipped.");
        return false;
    }

    // Every corner has to name one of the mesh's points. The unsplit path hands
    // faceVertexIndices over as the triangle table itself, so an index past the points (or a
    // negative one) reached the host's mesh as it stood; the split path quietly drew such a
    // corner at point 0. Both now refuse the mesh, as inconsistent face counts are refused.
    for (const int point : faceVertexIndices)
    {
        if (point < 0 || static_cast<size_t>(point) >= points.size())
        {
            AppendImportWarning(context,
                "mesh '" + mesh.GetPath().GetString() + "' has a face-vertex index (" +
                std::to_string(point) + ") outside its " + std::to_string(points.size()) +
                " points; mesh skipped.");
            return false;
        }
    }

    // Points/normals are kept in mesh-local space; the prim's transform is reconstructed
    // per node on the Unity side (see importedNodes). ToUnityVector still applies the
    // USD→Unity basis flip (negate X) so local coordinates match the converted transforms.
    importedMesh->primPath = mesh.GetPath().GetString();
    importedMesh->name = mesh.GetPrim().GetName().GetString();

    // Face winding. Only consulted when normals have to be computed; authored normals are used
    // as they stand.
    TfToken orientation = UsdGeomTokens->rightHanded;
    mesh.GetOrientationAttr().Get(&orientation, time);
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
    ResolveUvSources(mesh, uvSources, time);

    ImportNormalSource normalSource;
    ResolveNormalSource(mesh, &normalSource, time);

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
                if (recordVertexPoints)
                {
                    importedMesh->vertexNormalElements.push_back(point);
                }
            }
        }
        else if (normalSource.layout.valid)
        {
            std::vector<RUsdVec3> normals;
            std::vector<int> elements;
            normals.reserve(static_cast<size_t>(pointCount));
            for (int point = 0; point < pointCount; ++point)
            {
                const int element = normalSource.layout.Element(point, point, 0);
                if (element < 0)
                {
                    normals.clear();
                    elements.clear();
                    break;
                }

                normals.push_back(ToUnityVector(normalSource.values[static_cast<size_t>(element)]));
                elements.push_back(element);
            }

            importedMesh->normals = std::move(normals);
            if (recordVertexPoints)
            {
                importedMesh->vertexNormalElements = std::move(elements);
            }
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
            if (recordVertexPoints)
            {
                importedMesh->vertexPoints.push_back(point);
            }

            if (normalSource.layout.valid)
            {
                importedMesh->normals.push_back(
                    ToUnityVector(normalSource.values[static_cast<size_t>(key[1])]));
                if (recordVertexPoints)
                {
                    importedMesh->vertexNormalElements.push_back(key[1]);
                }
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
        ReadDisplayColor(mesh.GetPrim(), time), &importedMesh->indices, &importedMesh->submeshes);

    importedMesh->visible = UsdGeomImageable(mesh.GetPrim()).ComputeVisibility(time) == UsdGeomTokens->invisible ? 0 : 1;
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

bool HasUvTransform(const RUsdMaterial& source)
{
    return source.uvScale[0] != 1.0f || source.uvScale[1] != 1.0f ||
        source.uvOffset[0] != 0.0f || source.uvOffset[1] != 0.0f;
}

// OmniPBR, Omniverse's default MDL material, beside the UsdPreviewSurface (API 13): Kit and
// Isaac Sim render it natively and its inputs are the ones a user edits there. The input names
// are the ones ReadOmniPbr reads back, so an exported OmniPBR imports to the same values. The
// importer reads it in preference to the UsdPreviewSurface, as Isaac Sim renders it. Unity's
// packed metallic(.r) + smoothness(.a) map has no OmniPBR input -- metallic_texture is greyscale
// and roughness a map of its own -- so metallic and roughness stay constants here, the map stays
// in the UsdPreviewSurface only, and ExtractMaterial takes it from there on import.
void WriteOmniPbrFlavor(
    RUsdContext* context,
    const SdfPath& materialPath,
    const UsdShadeMaterial& usdMaterial,
    const RUsdMaterial& source)
{
    UsdShadeShader omni = UsdShadeShader::Define(
        context->stage, materialPath.AppendChild(TfToken("OmniPBR")));
    omni.SetSourceAsset(SdfAssetPath("OmniPBR.mdl"), TfToken("mdl"));
    omni.SetSourceAssetSubIdentifier(TfToken("OmniPBR"), TfToken("mdl"));

    auto input = [&omni](const char* name, const SdfValueTypeName& type)
    {
        return omni.CreateInput(TfToken(name), type);
    };
    auto texture = [&input](const char* name, const char* path, const char* colorSpace)
    {
        UsdShadeInput file = input(name, SdfValueTypeNames->Asset);
        file.Set(SdfAssetPath(path));
        file.GetAttr().SetColorSpace(TfToken(colorSpace));
    };

    const GfVec3f color(source.r, source.g, source.b);
    if (source.albedoTexturePath[0] != '\0')
    {
        // OmniPBR multiplies diffuse_texture by diffuse_tint, as Unity multiplies its base map by
        // the base colour; the constant is the untextured colour only.
        texture("diffuse_texture", source.albedoTexturePath, "sRGB");
        input("diffuse_color_constant", SdfValueTypeNames->Color3f).Set(GfVec3f(1.0f));
        input("diffuse_tint", SdfValueTypeNames->Color3f).Set(color);
    }
    else
    {
        input("diffuse_color_constant", SdfValueTypeNames->Color3f).Set(color);
    }

    input("metallic_constant", SdfValueTypeNames->Float).Set(source.metallic);
    input("reflection_roughness_constant", SdfValueTypeNames->Float).Set(source.roughness);

    if (source.normalTexturePath[0] != '\0')
    {
        texture("normalmap_texture", source.normalTexturePath, "raw");
        // Unity's normal maps are OpenGL-style (green up); OmniPBR flips V unless told not to,
        // which turned every exported bump inside out in Isaac Sim.
        input("flip_tangent_v", SdfValueTypeNames->Bool).Set(false);
    }

    // The inverse of ReadOmniPbr: emissive_intensity in nits, kNitsPerUnitEmission per unit of
    // UsdPreviewSurface emissiveColor (what Unity's emission imports from and exports to).
    const float peak = std::max({source.emissive[0], source.emissive[1], source.emissive[2]});
    const bool hasEmissiveTexture = source.emissiveTexturePath[0] != '\0';
    if (peak > 0.0f || hasEmissiveTexture)
    {
        input("enable_emission", SdfValueTypeNames->Bool).Set(true);
        const float scale = peak > 0.0f ? peak : 1.0f;
        const GfVec3f emissive = peak > 0.0f
            ? GfVec3f(source.emissive[0], source.emissive[1], source.emissive[2]) / scale
            : GfVec3f(1.0f);
        input("emissive_color", SdfValueTypeNames->Color3f).Set(emissive);
        input("emissive_intensity", SdfValueTypeNames->Float).Set(scale * kNitsPerUnitEmission);
        if (hasEmissiveTexture)
        {
            texture("emissive_color_texture", source.emissiveTexturePath, "sRGB");
        }
    }

    if (source.a < 1.0f)
    {
        input("enable_opacity", SdfValueTypeNames->Bool).Set(true);
        input("opacity_constant", SdfValueTypeNames->Float).Set(source.a);
    }

    if (HasUvTransform(source))
    {
        input("texture_scale", SdfValueTypeNames->Float2).Set(GfVec2f(source.uvScale[0], source.uvScale[1]));
        input("texture_translate", SdfValueTypeNames->Float2).Set(GfVec2f(source.uvOffset[0], source.uvOffset[1]));
    }

    // Kit connects all three MDL terminals to the one material output.
    UsdShadeOutput out = omni.CreateOutput(TfToken("out"), SdfValueTypeNames->Token);
    const TfToken mdl("mdl");
    usdMaterial.CreateSurfaceOutput(mdl).ConnectToSource(out);
    usdMaterial.CreateDisplacementOutput(mdl).ConnectToSource(out);
    usdMaterial.CreateVolumeOutput(mdl).ConnectToSource(out);
}

// A MaterialX standard_surface beside the UsdPreviewSurface (API 13), under the mtlx render
// context that Hydra Storm, Omniverse and MaterialX-aware renderers pick. Node definitions are
// MaterialX 1.39's, as shipped in the payload's usdMtlx library. Textures are sampled through
// texcoord * scale + offset, which is UsdTransform2d's order and Unity's tiling/offset.
// Metalness and roughness stay constants, for the same reason as OmniPBR, and because the
// importer's MaterialX path reads them that way; the packed map stays in the UsdPreviewSurface
// and ExtractMaterial takes it from there on import.
void WriteMaterialXFlavor(
    RUsdContext* context,
    const SdfPath& materialPath,
    const UsdShadeMaterial& usdMaterial,
    const RUsdMaterial& source)
{
    auto node = [&context, &materialPath](const char* name, const char* id)
    {
        UsdShadeShader shader = UsdShadeShader::Define(
            context->stage, materialPath.AppendChild(TfToken(name)));
        shader.CreateIdAttr().Set(TfToken(id));
        return shader;
    };

    UsdShadeShader surface = node("mtlx_surface", "ND_standard_surface_surfaceshader");

    UsdShadeOutput uv;
    auto texcoord = [&]()
    {
        if (uv)
        {
            return uv;
        }

        UsdShadeShader reader = node("mtlx_texcoord", "ND_texcoord_vector2");
        uv = reader.CreateOutput(TfToken("out"), SdfValueTypeNames->Float2);
        if (HasUvTransform(source))
        {
            UsdShadeShader scale = node("mtlx_uv_scale", "ND_multiply_vector2");
            scale.CreateInput(TfToken("in1"), SdfValueTypeNames->Float2).ConnectToSource(uv);
            scale.CreateInput(TfToken("in2"), SdfValueTypeNames->Float2)
                .Set(GfVec2f(source.uvScale[0], source.uvScale[1]));
            UsdShadeShader offset = node("mtlx_uv_offset", "ND_add_vector2");
            offset.CreateInput(TfToken("in1"), SdfValueTypeNames->Float2)
                .ConnectToSource(scale.CreateOutput(TfToken("out"), SdfValueTypeNames->Float2));
            offset.CreateInput(TfToken("in2"), SdfValueTypeNames->Float2)
                .Set(GfVec2f(source.uvOffset[0], source.uvOffset[1]));
            uv = offset.CreateOutput(TfToken("out"), SdfValueTypeNames->Float2);
        }

        return uv;
    };

    auto image = [&](const char* name, const char* id, const char* path, const char* colorSpace,
                     const SdfValueTypeName& type)
    {
        UsdShadeShader shader = node(name, id);
        UsdShadeInput file = shader.CreateInput(TfToken("file"), SdfValueTypeNames->Asset);
        file.Set(SdfAssetPath(path));
        file.GetAttr().SetColorSpace(TfToken(colorSpace));
        shader.CreateInput(TfToken("texcoord"), SdfValueTypeNames->Float2).ConnectToSource(texcoord());
        return shader.CreateOutput(TfToken("out"), type);
    };

    const GfVec3f color(source.r, source.g, source.b);
    if (source.albedoTexturePath[0] != '\0')
    {
        // Unity's base colour multiplies the map, which standard_surface can only express
        // through a multiply node; base stays 1.
        UsdShadeOutput albedo = image("mtlx_base_color", "ND_image_color3",
            source.albedoTexturePath, "srgb_texture", SdfValueTypeNames->Color3f);
        if (color != GfVec3f(1.0f))
        {
            UsdShadeShader tint = node("mtlx_base_tint", "ND_multiply_color3");
            tint.CreateInput(TfToken("in1"), SdfValueTypeNames->Color3f).ConnectToSource(albedo);
            tint.CreateInput(TfToken("in2"), SdfValueTypeNames->Color3f).Set(color);
            albedo = tint.CreateOutput(TfToken("out"), SdfValueTypeNames->Color3f);
        }

        surface.CreateInput(TfToken("base_color"), SdfValueTypeNames->Color3f).ConnectToSource(albedo);
    }
    else
    {
        surface.CreateInput(TfToken("base_color"), SdfValueTypeNames->Color3f).Set(color);
    }

    surface.CreateInput(TfToken("base"), SdfValueTypeNames->Float).Set(1.0f);
    surface.CreateInput(TfToken("metalness"), SdfValueTypeNames->Float).Set(source.metallic);
    surface.CreateInput(TfToken("specular_roughness"), SdfValueTypeNames->Float).Set(source.roughness);

    if (source.normalTexturePath[0] != '\0')
    {
        UsdShadeOutput raw = image("mtlx_normal_image", "ND_image_vector3",
            source.normalTexturePath, "raw", SdfValueTypeNames->Float3);
        UsdShadeShader normalMap = node("mtlx_normalmap", "ND_normalmap_float");
        normalMap.CreateInput(TfToken("in"), SdfValueTypeNames->Float3).ConnectToSource(raw);
        surface.CreateInput(TfToken("normal"), SdfValueTypeNames->Float3)
            .ConnectToSource(normalMap.CreateOutput(TfToken("out"), SdfValueTypeNames->Float3));
    }

    const GfVec3f emissive(source.emissive[0], source.emissive[1], source.emissive[2]);
    if (source.emissiveTexturePath[0] != '\0')
    {
        UsdShadeOutput map = image("mtlx_emission_image", "ND_image_color3",
            source.emissiveTexturePath, "srgb_texture", SdfValueTypeNames->Color3f);
        surface.CreateInput(TfToken("emission_color"), SdfValueTypeNames->Color3f).ConnectToSource(map);
        const float peak = std::max({emissive[0], emissive[1], emissive[2]});
        surface.CreateInput(TfToken("emission"), SdfValueTypeNames->Float).Set(peak > 0.0f ? peak : 1.0f);
    }
    else if (emissive != GfVec3f(0.0f))
    {
        surface.CreateInput(TfToken("emission_color"), SdfValueTypeNames->Color3f).Set(emissive);
        surface.CreateInput(TfToken("emission"), SdfValueTypeNames->Float).Set(1.0f);
    }

    if (source.a < 1.0f)
    {
        surface.CreateInput(TfToken("opacity"), SdfValueTypeNames->Color3f).Set(GfVec3f(source.a));
    }

    usdMaterial.CreateSurfaceOutput(TfToken("mtlx"))
        .ConnectToSource(surface.CreateOutput(TfToken("out"), SdfValueTypeNames->Token));
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
        // varname is a string since USD 21.11; a token fails OpenUSD 26's validator.
        stReader.CreateInput(TfToken("varname"), SdfValueTypeNames->String).Set(std::string("st"));
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

    if ((context->materialFlavors & RUSD_MATERIAL_FLAVOR_OMNIPBR) != 0)
    {
        WriteOmniPbrFlavor(context, materialPath, usdMaterial, source);
    }

    if ((context->materialFlavors & RUSD_MATERIAL_FLAVOR_MATERIALX) != 0)
    {
        WriteMaterialXFlavor(context, materialPath, usdMaterial, source);
    }

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
bool GenerateImplicitSurface(const UsdPrim& prim, ProceduralMesh* m, UsdTimeCode time)
{
    TfToken axis = UsdGeomTokens->z;
    if (prim.IsA<UsdGeomCube>())
    {
        double size = 2.0;
        UsdGeomCube(prim).GetSizeAttr().Get(&size, time);
        AddCube(m, size);
    }
    else if (prim.IsA<UsdGeomSphere>())
    {
        double radius = 1.0;
        UsdGeomSphere(prim).GetRadiusAttr().Get(&radius, time);
        AddRevolution(m, CapsuleRows(static_cast<float>(radius), 0.0f));
    }
    else if (prim.IsA<UsdGeomCylinder>())
    {
        UsdGeomCylinder cylinder(prim);
        double radius = 1.0, height = 2.0;
        cylinder.GetRadiusAttr().Get(&radius, time);
        cylinder.GetHeightAttr().Get(&height, time);
        cylinder.GetAxisAttr().Get(&axis, time);
        const float r = static_cast<float>(radius), h = static_cast<float>(height) * 0.5f;
        AddRevolution(m, {{r, h, 1, 0, 1}, {r, -h, 1, 0, 0}});
        AddDisk(m, r, h, true);
        AddDisk(m, r, -h, false);
    }
    else if (prim.IsA<UsdGeomCone>())
    {
        UsdGeomCone cone(prim);
        double radius = 1.0, height = 2.0;
        cone.GetRadiusAttr().Get(&radius, time);
        cone.GetHeightAttr().Get(&height, time);
        cone.GetAxisAttr().Get(&axis, time);
        const float r = static_cast<float>(radius), h = static_cast<float>(height);
        // Side normal leans up by the slope: radial h, axial r.
        AddRevolution(m, {{0.0f, h * 0.5f, h, r, 1}, {r, -h * 0.5f, h, r, 0}});
        AddDisk(m, r, -h * 0.5f, false);
    }
    else if (prim.IsA<UsdGeomCapsule>())
    {
        UsdGeomCapsule capsule(prim);
        double radius = 0.5, height = 1.0;
        capsule.GetRadiusAttr().Get(&radius, time);
        capsule.GetHeightAttr().Get(&height, time);
        capsule.GetAxisAttr().Get(&axis, time);
        AddRevolution(m, CapsuleRows(static_cast<float>(radius), static_cast<float>(height) * 0.5f));
    }
    else if (prim.IsA<UsdGeomPlane>())
    {
        UsdGeomPlane plane(prim);
        double width = 2.0, length = 2.0;
        plane.GetWidthAttr().Get(&width, time);
        plane.GetLengthAttr().Get(&length, time);
        plane.GetAxisAttr().Get(&axis, time);
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
    if (!GenerateImplicitSurface(prim, &m, context->importTime))
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
        ReadDisplayColor(prim, context->importTime), &out->indices, &out->submeshes);
    out->visible = UsdGeomImageable(prim).ComputeVisibility(context->importTime) == UsdGeomTokens->invisible ? 0 : 1;
    return !out->indices.empty();
}

// ---- Stage walk ---------------------------------------------------------------------------

struct ImportWalk
{
    ImportWalk(RUsdContext* c, const Usd_PrimFlagsPredicate& p)
        : context(c), predicate(p), xformCache(c->importTime) {}

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

    // UsdSkel (API v13): filled when the walk enters a SkelRoot, read by the Skeleton and
    // skinned-mesh prims below it.
    UsdSkelCache skelCache;
    struct SkinTarget
    {
        UsdSkelSkinningQuery query;
        UsdSkelAnimQuery animation;
        int skeletonIndex = -1;
    };
    std::unordered_map<SdfPath, SkinTarget, SdfPath::Hash> skinningTargets;
    std::unordered_map<SdfPath, int, SdfPath::Hash> skeletonIndices; // -1: refused

    // How many prims above the current one animate their visibility (see ImportSubtree).
    int animatedVisibilityScopes = 0;
};

[[noreturn]] void ThrowImportBudget(const char* what, size_t limit)
{
    throw std::runtime_error(std::string("the stage exceeds the import limit of ") +
        std::to_string(limit) + " " + what + "; it was not imported");
}

// Claims a mesh against the import's mesh and geometry budgets. Over either, the import fails,
// or, when it skips past its limits, the mesh is left out (false) and counted.
bool CountGeometry(ImportWalk* walk, const RUsdContext::ImportedMesh& mesh)
{
    RUsdContext* context = walk->context;
    const RUsdContext::ImportLimits& limits = context->importLimits;
    const size_t elements = mesh.points.size() + mesh.indices.size();
    const bool tooMany = context->importedMeshes.size() >= limits.meshes;
    const bool tooLarge = elements > limits.geometryElements - walk->geometryElements;
    if (tooMany || tooLarge)
    {
        if (!limits.skipWhenExceeded)
        {
            if (tooMany)
            {
                ThrowImportBudget("meshes", limits.meshes);
            }

            ThrowImportBudget("points and triangle indices", limits.geometryElements);
        }

        ++context->skippedMeshes;
        return false;
    }

    walk->geometryElements += elements;
    return true;
}

// ---- Time, animation and skinning (API v13) -----------------------------------------------

// Claims `values` floats of the animation budget for `what`, or says why not and refuses.
bool ReserveAnimation(RUsdContext* context, const std::string& what, size_t samples, size_t values,
                      const char* consequence)
{
    const RUsdContext::ImportLimits& limits = context->importLimits;
    if (samples > limits.trackSamples)
    {
        AppendImportWarning(context, what + " has " + std::to_string(samples) +
            " time samples, over the import limit of " + std::to_string(limits.trackSamples) +
            "; " + consequence + ".");
        return false;
    }

    if (values > limits.animationValues - context->animationValues)
    {
        AppendImportWarning(context, what + " would take the stage past the import limit of " +
            std::to_string(limits.animationValues) + " animation values; " + consequence + ".");
        return false;
    }

    context->animationValues += values;
    return true;
}

void AppendMatrix(std::vector<float>* out, const GfMatrix4d& matrix)
{
    for (int row = 0; row < 4; ++row)
    {
        for (int column = 0; column < 4; ++column)
        {
            out->push_back(static_cast<float>(matrix[row][column]));
        }
    }
}

// A node's own animated transform and visibility, sampled where the stage authors samples.
void EmitNodeTracks(ImportWalk* walk, const UsdPrim& prim, int nodeIndex)
{
    RUsdContext* context = walk->context;

    // The walk's cache already holds this prim's xformOps; asking the schema instead rebuilds them
    // for every node, which cost 4% on a 3,384-mesh stage with no animation at all.
    const UsdGeomXformable xformable(prim);
    std::vector<double> times;
    if (xformable && walk->xformCache.TransformMightBeTimeVarying(prim) && xformable.GetTimeSamples(&times) &&
        times.size() > 1 &&
        ReserveAnimation(context, "the transform of '" + prim.GetPath().GetString() + "'", times.size(),
                         times.size() * 16, "it imports at the read time"))
    {
        RUsdContext::ImportedNodeTrack track;
        track.nodeIndex = nodeIndex;
        track.kind = RUSD_TRACK_TRANSFORM;
        track.times = times;
        track.values.reserve(times.size() * 16);
        for (double time : times)
        {
            GfMatrix4d local(1.0);
            bool resetsXformStack = false;
            xformable.GetLocalTransformation(&local, &resetsXformStack, UsdTimeCode(time));
            AppendMatrix(&track.values, local);
        }

        context->importedNodeTracks.push_back(std::move(track));
    }

    const UsdAttribute visibility = UsdGeomImageable(prim).GetVisibilityAttr();
    times.clear();
    if (visibility && visibility.ValueMightBeTimeVarying() && visibility.GetTimeSamples(&times) &&
        times.size() > 1 &&
        ReserveAnimation(context, "the visibility of '" + prim.GetPath().GetString() + "'", times.size(),
                         times.size(), "it imports at the read time"))
    {
        RUsdContext::ImportedNodeTrack track;
        track.nodeIndex = nodeIndex;
        track.kind = RUSD_TRACK_VISIBILITY;
        track.times = times;
        track.values.reserve(times.size());
        for (double time : times)
        {
            TfToken value;
            visibility.Get(&value, UsdTimeCode(time));
            track.values.push_back(value == UsdGeomTokens->invisible ? 0.0f : 1.0f);
        }

        context->importedNodeTracks.push_back(std::move(track));
    }
}

// Records a skeleton the first time a SkelRoot binds it; returns its index, or -1 when it cannot
// skin anything (its meshes then import as they are authored). The node index is filled in when
// the walk reaches the Skeleton prim itself.
int EmitSkeleton(ImportWalk* walk, const UsdSkelSkeletonQuery& query)
{
    RUsdContext* context = walk->context;
    const SdfPath path = query.GetSkeleton().GetPrim().GetPath();
    const auto known = walk->skeletonIndices.find(path);
    if (known != walk->skeletonIndices.end())
    {
        return known->second;
    }

    walk->skeletonIndices[path] = -1; // refused once, not once per binding
    const std::string name = path.GetString();
    const VtTokenArray joints = query.GetJointOrder();
    if (joints.empty())
    {
        return -1;
    }

    if (joints.size() > context->importLimits.joints)
    {
        AppendImportWarning(context, "skeleton '" + name + "' has " + std::to_string(joints.size()) +
            " joints, over the import limit of " + std::to_string(context->importLimits.joints) +
            "; its meshes import unskinned.");
        return -1;
    }

    VtMatrix4dArray bind;
    if (!query.HasBindPose() || !query.GetJointWorldBindTransforms(&bind) || bind.size() != joints.size())
    {
        AppendImportWarning(context, "skeleton '" + name +
            "' has no usable bindTransforms; its meshes import unskinned.");
        return -1;
    }

    // Without authored restTransforms the rest pose is the bind pose, made joint-local.
    VtMatrix4dArray rest;
    if (!query.HasRestPose() ||
        !query.ComputeJointLocalTransforms(&rest, UsdTimeCode::Default(), /*atRest=*/true) ||
        rest.size() != joints.size())
    {
        std::vector<GfMatrix4d> local(bind.size());
        if (!UsdSkelComputeJointLocalTransforms(query.GetTopology(), TfSpan<const GfMatrix4d>(bind.cdata(), bind.size()),
                                                TfSpan<GfMatrix4d>(local)))
        {
            AppendImportWarning(context, "skeleton '" + name +
                "' has no usable rest pose; its meshes import unskinned.");
            return -1;
        }

        rest.assign(local.begin(), local.end());
    }

    VtMatrix4dArray pose;
    if (!query.ComputeJointLocalTransforms(&pose, context->importTime) || pose.size() != joints.size())
    {
        pose = rest;
    }

    RUsdContext::ImportedSkeleton skeleton;
    skeleton.path = name;
    for (const TfToken& joint : joints)
    {
        skeleton.joints.push_back(joint.GetString());
    }

    const VtIntArray& parents = query.GetTopology().GetParentIndices();
    skeleton.parents.assign(parents.begin(), parents.end());
    for (size_t joint = 0; joint < joints.size(); ++joint)
    {
        AppendMatrix(&skeleton.bindTransforms, bind[joint]);
        AppendMatrix(&skeleton.restTransforms, rest[joint]);
        AppendMatrix(&skeleton.poseTransforms, pose[joint]);
    }

    const UsdSkelAnimQuery& animation = query.GetAnimQuery();
    std::vector<double> times;
    if (animation && animation.GetJointTransformTimeSamples(&times) && times.size() > 1 &&
        ReserveAnimation(context, "the animation of skeleton '" + name + "'", times.size(),
                         times.size() * joints.size() * 16, "it imports in its read-time pose"))
    {
        skeleton.samples.reserve(times.size() * joints.size() * 16);
        VtMatrix4dArray local;
        for (double time : times)
        {
            if (!query.ComputeJointLocalTransforms(&local, UsdTimeCode(time)) || local.size() != joints.size())
            {
                AppendImportWarning(context, "the animation of skeleton '" + name +
                    "' could not be read; it imports in its read-time pose.");
                skeleton.samples.clear();
                times.clear();
                break;
            }

            for (const GfMatrix4d& matrix : local)
            {
                AppendMatrix(&skeleton.samples, matrix);
            }
        }

        skeleton.times = times;
    }

    const int index = static_cast<int>(context->importedSkeletons.size());
    context->importedSkeletons.push_back(std::move(skeleton));
    walk->skeletonIndices[path] = index;
    return index;
}

// Binds what a SkelRoot skins. The walk is pre-order, so this runs before the Skeleton and mesh
// prims below the root are reached.
void PopulateSkelRoot(ImportWalk* walk, const UsdPrim& prim)
{
    const UsdSkelRoot root(prim);
    std::vector<UsdSkelBinding> bindings;
    if (!walk->skelCache.Populate(root, walk->predicate) ||
        !walk->skelCache.ComputeSkelBindings(root, &bindings, walk->predicate))
    {
        AppendImportWarning(walk->context, "SkelRoot '" + prim.GetPath().GetString() +
            "' could not be read; its meshes import unskinned.");
        return;
    }

    for (const UsdSkelBinding& binding : bindings)
    {
        const UsdSkelSkeletonQuery query = walk->skelCache.GetSkelQuery(binding.GetSkeleton());
        if (!query)
        {
            AppendImportWarning(walk->context, "skeleton '" + binding.GetSkeleton().GetPath().GetString() +
                "' is invalid; its meshes import unskinned.");
            continue;
        }

        const int skeletonIndex = EmitSkeleton(walk, query);
        if (skeletonIndex < 0)
        {
            continue;
        }

        for (const UsdSkelSkinningQuery& target : binding.GetSkinningTargets())
        {
            ImportWalk::SkinTarget entry;
            entry.query = target;
            entry.animation = query.GetAnimQuery();
            entry.skeletonIndex = skeletonIndex;
            walk->skinningTargets.emplace(target.GetPrim().GetPath(), entry);
        }
    }
}

void LinkSkeletonNode(ImportWalk* walk, const UsdPrim& prim, int nodeIndex)
{
    const auto found = walk->skeletonIndices.find(prim.GetPath());
    if (found != walk->skeletonIndices.end() && found->second >= 0)
    {
        RUsdContext::ImportedSkeleton& skeleton =
            walk->context->importedSkeletons[static_cast<size_t>(found->second)];
        if (skeleton.nodeIndex < 0)
        {
            skeleton.nodeIndex = nodeIndex;
        }
    }
}

// Blend shapes of a skinned mesh as dense per-vertex frames, plus their weights from the
// skeleton's animation. Shapes keep the mesh's skel:blendShapes order even when one cannot be
// read (it then has no frames), so weight i always belongs to shape i.
void BuildMeshBlendShapes(ImportWalk* walk, const UsdPrim& prim, const ImportWalk::SkinTarget& target,
                          RUsdContext::ImportedMesh* mesh)
{
    RUsdContext* context = walk->context;
    const std::string meshName = prim.GetPath().GetString();
    const UsdSkelBindingAPI binding(prim);
    VtTokenArray names;
    SdfPathVector targets;
    binding.GetBlendShapesAttr().Get(&names);
    binding.GetBlendShapeTargetsRel().GetTargets(&targets);
    if (names.empty())
    {
        return;
    }

    if (names.size() != targets.size())
    {
        AppendImportWarning(context, "mesh '" + meshName +
            "' authors different numbers of skel:blendShapes and skel:blendShapeTargets; its blend shapes are ignored.");
        return;
    }

    const size_t pointCount = mesh->sourcePointCount;
    const size_t vertexCount = mesh->points.size();
    auto pointOf = [mesh](size_t vertex)
    {
        return mesh->vertexPoints.empty() ? vertex : static_cast<size_t>(mesh->vertexPoints[vertex]);
    };

    struct PendingFrame
    {
        float weight = 1.0f;
        VtVec3fArray offsets;
        VtVec3fArray normalOffsets;
    };

    for (size_t shapeIndex = 0; shapeIndex < names.size(); ++shapeIndex)
    {
        RUsdContext::ImportedMesh::BlendShape shape;
        shape.name = names[shapeIndex].GetString();
        const std::string what = "blend shape '" + shape.name + "' of mesh '" + meshName + "'";
        const UsdSkelBlendShape source(prim.GetStage()->GetPrimAtPath(targets[shapeIndex]));
        if (!source)
        {
            AppendImportWarning(context, what + " targets no BlendShape prim; it is ignored.");
            mesh->skin.blendShapes.push_back(std::move(shape));
            continue;
        }

        std::vector<PendingFrame> frames;
        for (const UsdSkelInbetweenShape& inbetween : source.GetInbetweens())
        {
            PendingFrame frame;
            if (!inbetween.GetWeight(&frame.weight))
            {
                continue;
            }

            if (!(frame.weight > 0.0f && frame.weight < 1.0f))
            {
                AppendImportWarning(context, what + " has an inbetween at weight " +
                    std::to_string(frame.weight) + "; Unity places frames between 0 and 1 only, so it is ignored.");
                continue;
            }

            inbetween.GetOffsets(&frame.offsets);
            inbetween.GetNormalOffsets(&frame.normalOffsets);
            frames.push_back(std::move(frame));
        }

        std::sort(frames.begin(), frames.end(),
                  [](const PendingFrame& a, const PendingFrame& b) { return a.weight < b.weight; });
        PendingFrame full;
        source.GetOffsetsAttr().Get(&full.offsets);
        source.GetNormalOffsetsAttr().Get(&full.normalOffsets);
        frames.push_back(std::move(full));

        VtIntArray pointIndices;
        source.GetPointIndicesAttr().Get(&pointIndices);
        const size_t authored = pointIndices.empty() ? pointCount : pointIndices.size();

        size_t values = 0;
        for (const PendingFrame& frame : frames)
        {
            values += vertexCount * 3 * (frame.normalOffsets.size() == authored ? 2 : 1);
        }

        if (!ReserveAnimation(context, what, 0, values, "it is ignored"))
        {
            mesh->skin.blendShapes.push_back(std::move(shape));
            continue;
        }

        for (const PendingFrame& pending : frames)
        {
            if (pending.offsets.size() != authored)
            {
                AppendImportWarning(context, what + " has " + std::to_string(pending.offsets.size()) +
                    " offsets for " + std::to_string(authored) + " points; that frame is ignored.");
                continue;
            }

            const bool hasNormals = pending.normalOffsets.size() == authored;
            std::vector<GfVec3f> pointDelta(pointCount, GfVec3f(0.0f));
            std::vector<GfVec3f> pointNormalDelta(hasNormals ? pointCount : 0, GfVec3f(0.0f));
            for (size_t i = 0; i < authored; ++i)
            {
                const int point = pointIndices.empty() ? static_cast<int>(i) : pointIndices[i];
                if (point < 0 || static_cast<size_t>(point) >= pointCount)
                {
                    continue;
                }

                pointDelta[static_cast<size_t>(point)] = pending.offsets[i];
                if (hasNormals)
                {
                    pointNormalDelta[static_cast<size_t>(point)] = pending.normalOffsets[i];
                }
            }

            RUsdContext::ImportedMesh::BlendShapeFrame frame;
            frame.weight = pending.weight;
            frame.deltaVertices.reserve(vertexCount);
            for (size_t vertex = 0; vertex < vertexCount; ++vertex)
            {
                frame.deltaVertices.push_back(ToUnityVector(pointDelta[pointOf(vertex)]));
            }

            if (hasNormals)
            {
                frame.deltaNormals.reserve(vertexCount);
                for (size_t vertex = 0; vertex < vertexCount; ++vertex)
                {
                    frame.deltaNormals.push_back(ToUnityVector(pointNormalDelta[pointOf(vertex)]));
                }
            }

            shape.frames.push_back(std::move(frame));
        }

        mesh->skin.blendShapes.push_back(std::move(shape));
    }

    // Weights come from the skeleton's animation, in its blendShapes order; the skinning query's
    // mapper moves them into the mesh's order.
    RUsdContext::ImportedMesh::Skin& skin = mesh->skin;
    skin.blendShapeWeights.assign(names.size(), 0.0f);
    const UsdSkelAnimQuery& animation = target.animation;
    if (!animation)
    {
        return;
    }

    const UsdSkelAnimMapperRefPtr& mapper = target.query.GetBlendShapeMapper();
    auto remap = [&](const VtFloatArray& animationWeights, std::vector<float>* out)
    {
        VtFloatArray meshWeights = animationWeights;
        if (mapper && !mapper->Remap(animationWeights, &meshWeights))
        {
            return false;
        }

        if (meshWeights.size() != names.size())
        {
            return false;
        }

        out->insert(out->end(), meshWeights.begin(), meshWeights.end());
        return true;
    };

    VtFloatArray weights;
    std::vector<float> atReadTime;
    if (animation.ComputeBlendShapeWeights(&weights, context->importTime) && remap(weights, &atReadTime))
    {
        skin.blendShapeWeights = atReadTime;
    }

    std::vector<double> times;
    if (animation.GetBlendShapeWeightTimeSamples(&times) && times.size() > 1 &&
        ReserveAnimation(context, "the blend-shape weights of mesh '" + meshName + "'", times.size(),
                         times.size() * names.size(), "it imports with its read-time weights"))
    {
        for (double time : times)
        {
            if (!animation.ComputeBlendShapeWeights(&weights, UsdTimeCode(time)) ||
                !remap(weights, &skin.blendShapeWeightSamples))
            {
                AppendImportWarning(context, "the blend-shape weights of mesh '" + meshName +
                    "' could not be read; it imports with its read-time weights.");
                skin.blendShapeWeightSamples.clear();
                times.clear();
                break;
            }
        }

        skin.blendShapeWeightTimes = times;
    }
}

// Joint weights and the bind transform of a skinned mesh, carried through the vertex split onto
// the imported vertices, with joint indices moved into the skeleton's order.
void BuildMeshSkin(ImportWalk* walk, const UsdPrim& prim, const ImportWalk::SkinTarget& target,
                   RUsdContext::ImportedMesh* mesh)
{
    RUsdContext* context = walk->context;
    const UsdSkelSkinningQuery& query = target.query;
    const RUsdContext::ImportedSkeleton& skeleton =
        context->importedSkeletons[static_cast<size_t>(target.skeletonIndex)];
    const std::string name = prim.GetPath().GetString();
    const size_t pointCount = mesh->sourcePointCount;
    const size_t vertexCount = mesh->points.size();

    RUsdContext::ImportedMesh::Skin& skin = mesh->skin;
    skin = RUsdContext::ImportedMesh::Skin();
    skin.skeletonIndex = target.skeletonIndex;
    skin.geomBindTransform = FromGfMatrix(query.GetGeomBindTransform(context->importTime));

    // Unity's SkinnedMeshRenderer blends linearly; the host decides how to honour this.
    skin.dualQuaternion = query.GetSkinningMethod() == UsdSkelTokens->dualQuaternion ? 1 : 0;

    if (query.HasJointInfluences())
    {
        const int influences = query.GetNumInfluencesPerComponent();
        VtIntArray indices;
        VtFloatArray weights;
        if (influences <= 0 || influences > kMaxImportInfluences)
        {
            AppendImportWarning(context, "mesh '" + name + "' has " + std::to_string(influences) +
                " joint influences per point, outside Unity's 1 to " + std::to_string(kMaxImportInfluences) +
                "; it imports unskinned.");
            skin = RUsdContext::ImportedMesh::Skin();
            return;
        }

        const size_t perPoint = static_cast<size_t>(influences);
        if (!query.ComputeVaryingJointInfluences(pointCount, &indices, &weights, context->importTime) ||
            indices.size() != pointCount * perPoint || weights.size() != indices.size())
        {
            AppendImportWarning(context, "mesh '" + name +
                "' has joint influences that could not be read; it imports unskinned.");
            skin = RUsdContext::ImportedMesh::Skin();
            return;
        }

        // A mesh can author its own joint order (skel:joints); its indices then count in that order.
        std::vector<int> toSkeleton;
        VtTokenArray meshJoints;
        if (query.GetJointOrder(&meshJoints))
        {
            std::unordered_map<std::string, int> skeletonJoint;
            for (size_t joint = 0; joint < skeleton.joints.size(); ++joint)
            {
                skeletonJoint.emplace(skeleton.joints[joint], static_cast<int>(joint));
            }

            for (const TfToken& joint : meshJoints)
            {
                const auto found = skeletonJoint.find(joint.GetString());
                toSkeleton.push_back(found == skeletonJoint.end() ? -1 : found->second);
            }
        }

        const int jointCount = static_cast<int>(skeleton.joints.size());
        size_t ignored = 0;
        size_t unweighted = 0;
        skin.influences = influences;
        skin.jointIndices.assign(vertexCount * perPoint, 0);
        skin.jointWeights.assign(vertexCount * perPoint, 0.0f);
        for (size_t vertex = 0; vertex < vertexCount; ++vertex)
        {
            const size_t point = mesh->vertexPoints.empty() ? vertex : static_cast<size_t>(mesh->vertexPoints[vertex]);
            float sum = 0.0f;
            for (size_t i = 0; i < perPoint; ++i)
            {
                int joint = indices[point * perPoint + i];
                const float weight = weights[point * perPoint + i];
                if (!toSkeleton.empty())
                {
                    joint = joint >= 0 && static_cast<size_t>(joint) < toSkeleton.size() ? toSkeleton[static_cast<size_t>(joint)] : -1;
                }

                if (joint < 0 || joint >= jointCount || !(weight > 0.0f))
                {
                    ignored += weight > 0.0f ? 1 : 0;
                    continue;
                }

                skin.jointIndices[vertex * perPoint + i] = joint;
                skin.jointWeights[vertex * perPoint + i] = weight;
                sum += weight;
            }

            if (sum > 0.0f)
            {
                for (size_t i = 0; i < perPoint; ++i)
                {
                    skin.jointWeights[vertex * perPoint + i] /= sum;
                }
            }
            else
            {
                ++unweighted;
            }
        }

        if (ignored > 0)
        {
            AppendImportWarning(context, "mesh '" + name + "' has " + std::to_string(ignored) +
                " joint influences on joints its skeleton does not have; they are ignored.");
        }

        if (unweighted > 0)
        {
            AppendImportWarning(context, "mesh '" + name + "' has " + std::to_string(unweighted) +
                " vertices with no joint weight; Unity draws them at the skeleton's origin.");
        }
    }

    if (query.HasBlendShapes())
    {
        BuildMeshBlendShapes(walk, prim, target, mesh);
    }
}

// Whether a mesh's normals -- the schema attribute, or the normals primvar some exporters write
// instead -- might change over time.
bool NormalsMightBeTimeVarying(const UsdGeomMesh& mesh)
{
    if (mesh.GetNormalsAttr().HasAuthoredValue())
    {
        return mesh.GetNormalsAttr().ValueMightBeTimeVarying();
    }

    static const TfToken normalsToken("normals");
    const UsdGeomPrimvar primvar = UsdGeomPrimvarsAPI(mesh.GetPrim()).GetPrimvar(normalsToken);
    return primvar && (primvar.GetAttr().ValueMightBeTimeVarying() ||
                       (primvar.GetIndicesAttr() && primvar.GetIndicesAttr().ValueMightBeTimeVarying()));
}

// Time-sampled points (a point cache: simulations, Alembic conversions, baked deformers) as every
// sample's points on the imported vertices. Normals go with them: recomputed for each sample when
// the mesh authors none, as the import computed them for the read time; read for each sample when
// authored ones animate too; left to the mesh when authored ones hold still. The topology has to
// hold still and every sample has to have every point; otherwise the mesh stays at the read time,
// with a warning saying why.
void BuildMeshPointCache(ImportWalk* walk, const UsdGeomMesh& mesh, RUsdContext::ImportedMesh* imported)
{
    RUsdContext* context = walk->context;
    const std::string name = mesh.GetPath().GetString();
    const UsdAttribute pointsAttr = mesh.GetPointsAttr();
    std::vector<double> times;
    if (!pointsAttr.GetTimeSamples(&times) || times.size() < 2)
    {
        return;
    }

    if (mesh.GetFaceVertexCountsAttr().ValueMightBeTimeVarying() ||
        mesh.GetFaceVertexIndicesAttr().ValueMightBeTimeVarying())
    {
        AppendImportWarning(context, "mesh '" + name +
            "' changes its topology over time; it imports as the frame at the read time.");
        return;
    }

    const size_t vertexCount = imported->points.size();
    ImportNormalSource readTimeNormals;
    ResolveNormalSource(mesh, &readTimeNormals, context->importTime);
    const bool authoredNormals = readTimeNormals.layout.valid;
    bool sampledNormals = !authoredNormals ||
        (NormalsMightBeTimeVarying(mesh) && imported->vertexNormalElements.size() == vertexCount);
    if (!ReserveAnimation(context, "the points of mesh '" + name + "'", times.size(),
                          times.size() * vertexCount * 3 * (sampledNormals ? 2 : 1),
                          "it imports as the frame at the read time"))
    {
        return;
    }

    VtIntArray faceVertexCounts;
    VtIntArray faceVertexIndices;
    mesh.GetFaceVertexCountsAttr().Get(&faceVertexCounts, context->importTime);
    mesh.GetFaceVertexIndicesAttr().Get(&faceVertexIndices, context->importTime);
    TfToken orientation = UsdGeomTokens->rightHanded;
    mesh.GetOrientationAttr().Get(&orientation);
    const bool leftHanded = orientation == UsdGeomTokens->leftHanded;
    auto pointOf = [imported](size_t vertex)
    {
        return imported->vertexPoints.empty() ? vertex : static_cast<size_t>(imported->vertexPoints[vertex]);
    };

    std::vector<RUsdVec3> points;
    std::vector<RUsdVec3> normals;
    points.reserve(times.size() * vertexCount);
    normals.reserve(sampledNormals ? times.size() * vertexCount : 0);
    for (double time : times)
    {
        VtArray<GfVec3f> sample;
        if (!pointsAttr.Get(&sample, UsdTimeCode(time)) || sample.size() != imported->sourcePointCount)
        {
            AppendImportWarning(context, "mesh '" + name + "' has " + std::to_string(sample.size()) +
                " points at time " + TfStringify(time) + " where it has " +
                std::to_string(imported->sourcePointCount) + " at the read time; it imports as the frame at the read time.");
            return;
        }

        for (size_t vertex = 0; vertex < vertexCount; ++vertex)
        {
            points.push_back(ToUnityVector(sample[pointOf(vertex)]));
        }

        if (!sampledNormals)
        {
            continue;
        }

        if (authoredNormals)
        {
            ImportNormalSource source;
            ResolveNormalSource(mesh, &source, UsdTimeCode(time));
            if (!source.layout.valid || source.values.size() != readTimeNormals.values.size())
            {
                AppendImportWarning(context, "the normals of mesh '" + name +
                    "' change their layout over time; its points animate with the normals of the read time.");
                sampledNormals = false;
                normals.clear();
                continue;
            }

            for (size_t vertex = 0; vertex < vertexCount; ++vertex)
            {
                normals.push_back(ToUnityVector(
                    source.values[static_cast<size_t>(imported->vertexNormalElements[vertex])]));
            }
        }
        else
        {
            const std::vector<GfVec3f> smooth =
                ComputeSmoothPointNormals(sample, faceVertexCounts, faceVertexIndices, leftHanded);
            for (size_t vertex = 0; vertex < vertexCount; ++vertex)
            {
                normals.push_back(ToUnityVector(smooth[pointOf(vertex)]));
            }
        }
    }

    imported->pointCacheTimes = times;
    imported->pointCachePoints = std::move(points);
    imported->pointCacheNormals = std::move(normals);
}

// `inheritedPurpose` and `parentVisible` carry down what purpose and visibility resolve to above
// this prim. Both attributes inherit, and computing them from the prim alone walks back up the
// ancestor chain on every call -- which the recursion has already walked on the way down.
// UsdImaging keeps the same two as memoised caches for the same reason.
void ImportSubtree(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, int depth,
                   const UsdGeomImageable::PurposeInfo& inheritedPurpose, int parentVisible);

// False when the node budget is spent and the import skips past its limits: the node, and
// everything the walk would reach after it, is left out.
bool EmitNode(ImportWalk* walk, const UsdPrim& prim, const std::string& outPath, const GfMatrix4d& local, int visible)
{
    RUsdContext::ImportedNode node;
    node.path = outPath;
    node.localMatrix = FromGfMatrix(local);
    node.visible = visible;
    // A copy inside a point-instancer instance is not that prim; it carries no composition of
    // its own to report.
    node.compositionFlags = outPath == prim.GetPath().GetString() ? ComputeCompositionFlags(prim) : 0;
    RUsdContext* context = walk->context;
    if (context->importedNodes.size() >= context->importLimits.nodes)
    {
        if (!context->importLimits.skipWhenExceeded)
        {
            ThrowImportBudget("nodes", context->importLimits.nodes);
        }

        if (context->skippedNodes++ == 0)
        {
            context->firstSkippedNode = outPath;
        }

        return false;
    }

    context->importedNodes.push_back(node);
    return true;
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
            if (!CountGeometry(walk, context->importedMeshes[static_cast<size_t>(built->second)]))
            {
                return;
            }

            RUsdContext::ImportedMesh copy = context->importedMeshes[static_cast<size_t>(built->second)];
            copy.primPath = outPath;
            copy.sourcePath = sourcePath;
            // A copy is drawn from the original's arrays; the animated points stay with it.
            copy.pointCacheTimes.clear();
            copy.pointCachePoints.clear();
            copy.pointCacheNormals.clear();
            context->importedMeshes.push_back(std::move(copy));
        }

        return;
    }

    const auto skinTarget = walk->skinningTargets.find(prim.GetPath());
    const bool skinned = skinTarget != walk->skinningTargets.end();

    RUsdContext::ImportedMesh mesh;
    bool ok = false;
    if (prim.IsA<UsdGeomMesh>())
    {
        const UsdGeomMesh geomMesh(prim);
        const bool animatedPoints = geomMesh.GetPointsAttr().ValueMightBeTimeVarying();
        ok = BuildImportedMesh(context, geomMesh, &walk->xformCache, &mesh, skinned || animatedPoints);
        if (ok && skinned)
        {
            BuildMeshSkin(walk, prim, skinTarget->second, &mesh);
        }

        if (ok && animatedPoints)
        {
            if (skinned)
            {
                AppendImportWarning(context, "mesh '" + sourcePath +
                    "' is skinned and has animated points; its points import as the frame at the read time.");
            }
            else if (outPath != sourcePath)
            {
                AppendImportWarning(context, "mesh '" + sourcePath +
                    "' has animated points inside a point-instancer prototype, which is drawn from one mesh for "
                    "every instance; it imports as the frame at the read time.");
            }
            else
            {
                BuildMeshPointCache(walk, geomMesh, &mesh);
            }
        }
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

    if (!CountGeometry(walk, mesh))
    {
        walk->builtMeshes[sourcePath] = -1; // and its instance copies with it
        return;
    }

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
    UsdTimeCode time = walk->context->importTime;
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
    const int visible = UsdGeomImageable(prim).ComputeVisibility(walk->context->importTime) == UsdGeomTokens->invisible ? 0 : 1;
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
        if (!EmitNode(walk, prim, protoPath, GfMatrix4d(1.0), 0))
        {
            return; // the node budget is spent; nothing more of the instancer fits
        }

        walk->expandingInstancers.push_back(prim.GetPath());
        ImportSubtree(walk, prototype, protoPath + "/" + prototype.GetName().GetString(), depth + 1,
                      UsdGeomImageable::PurposeInfo(), 1);
        walk->expandingInstancers.pop_back();

        // A skinned prototype mesh is drawn posed by its prototype's own copy of the skeleton. One
        // bound to a skeleton outside this copy -- above the instancer, or in a prototype another
        // instancer imported first -- has no pose of its own here, so it is drawn as authored.
        for (size_t m = firstMesh; m < walk->context->importedMeshes.size(); ++m)
        {
            RUsdContext::ImportedMesh& mesh = walk->context->importedMeshes[m];
            const int skeletonIndex = mesh.skin.skeletonIndex;
            if (skeletonIndex < 0)
            {
                continue;
            }

            const int skeletonNode = walk->context->importedSkeletons[static_cast<size_t>(skeletonIndex)].nodeIndex;
            const bool inside = skeletonNode >= 0 &&
                TfStringStartsWith(walk->context->importedNodes[static_cast<size_t>(skeletonNode)].path, protoPath + "/");
            if (!inside)
            {
                AppendImportWarning(walk->context, "mesh '" + mesh.sourcePath + "' in prototype '" +
                    prototypePath.GetString() + "' of point instancer '" + prim.GetPath().GetString() +
                    "' is bound to a skeleton outside that prototype's import; its instances are drawn unskinned.");
                mesh.skin = RUsdContext::ImportedMesh::Skin();
            }
        }

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
            const RUsdContext::ImportLimits& limits = walk->context->importLimits;
            if (placements > limits.instancePlacements - walk->instancePlacements)
            {
                if (!limits.skipWhenExceeded)
                {
                    ThrowImportBudget("point-instancer placements (instances times prototype meshes)",
                        limits.instancePlacements);
                }

                walk->context->skippedPlacements += placements;
                continue;
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
T GetAttrOr(const UsdAttribute& attribute, T fallback, UsdTimeCode time)
{
    T value = fallback;
    if (attribute)
    {
        attribute.Get(&value, time);
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
    const UsdTimeCode time = context->importTime;

    if (prim.HasAPI<UsdLuxLightAPI>())
    {
        RUsdContext::ImportedLight entry = {};
        RUsdImportLight& light = entry.light;
        light.nodeIndex = nodeIndex;

        UsdLuxLightAPI api(prim);
        const GfVec3f color = GetAttrOr(api.GetColorAttr(), GfVec3f(1.0f), time);
        GfVec3f tint(1.0f);
        if (GetAttrOr(api.GetEnableColorTemperatureAttr(), false, time))
        {
            tint = UsdLuxBlackbodyTemperatureAsRgb(GetAttrOr(api.GetColorTemperatureAttr(), 6500.0f, time));
        }
        light.color[0] = color[0] * tint[0];
        light.color[1] = color[1] * tint[1];
        light.color[2] = color[2] * tint[2];
        light.intensity = GetAttrOr(api.GetIntensityAttr(), 1.0f, time) *
            std::pow(2.0f, GetAttrOr(api.GetExposureAttr(), 0.0f, time));
        light.normalize = GetAttrOr(api.GetNormalizeAttr(), false, time) ? 1 : 0;

        if (prim.IsA<UsdLuxDistantLight>())
        {
            light.type = RUSD_LIGHT_DISTANT;
            light.angle = GetAttrOr(UsdLuxDistantLight(prim).GetAngleAttr(), 0.53f, time);
        }
        else if (prim.IsA<UsdLuxSphereLight>())
        {
            light.type = RUSD_LIGHT_SPHERE;
            light.radius = GetAttrOr(UsdLuxSphereLight(prim).GetRadiusAttr(), 0.5f, time);
        }
        else if (prim.IsA<UsdLuxRectLight>())
        {
            UsdLuxRectLight rect(prim);
            light.type = RUSD_LIGHT_RECT;
            light.width = GetAttrOr(rect.GetWidthAttr(), 1.0f, time);
            light.height = GetAttrOr(rect.GetHeightAttr(), 1.0f, time);
            entry.texturePath = ReadAssetAttr(context, rect.GetTextureFileAttr());
        }
        else if (prim.IsA<UsdLuxDiskLight>())
        {
            light.type = RUSD_LIGHT_DISK;
            light.radius = GetAttrOr(UsdLuxDiskLight(prim).GetRadiusAttr(), 0.5f, time);
        }
        else if (prim.IsA<UsdLuxCylinderLight>())
        {
            UsdLuxCylinderLight cylinder(prim);
            light.type = RUSD_LIGHT_CYLINDER;
            light.radius = GetAttrOr(cylinder.GetRadiusAttr(), 0.5f, time);
            light.length = GetAttrOr(cylinder.GetLengthAttr(), 1.0f, time);
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
            light.coneAngle = GetAttrOr(shaping.GetShapingConeAngleAttr(), 90.0f, time);
            light.coneSoftness = GetAttrOr(shaping.GetShapingConeSoftnessAttr(), 0.0f, time);
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
        entry.projection = GetAttrOr(camera.GetProjectionAttr(), UsdGeomTokens->perspective, time) ==
                UsdGeomTokens->orthographic
            ? RUSD_CAMERA_ORTHOGRAPHIC
            : RUSD_CAMERA_PERSPECTIVE;
        entry.focalLength = GetAttrOr(camera.GetFocalLengthAttr(), 50.0f, time);
        entry.horizontalAperture = GetAttrOr(camera.GetHorizontalApertureAttr(), 20.955f, time);
        entry.verticalAperture = GetAttrOr(camera.GetVerticalApertureAttr(), 15.2908f, time);
        const GfVec2f clipping = GetAttrOr(camera.GetClippingRangeAttr(), GfVec2f(1.0f, 1000000.0f), time);
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
            GetAttrOr(audio.GetAuralModeAttr(), TfToken("spatial"), time) == TfToken("nonSpatial")
            ? RUSD_AUDIO_NONSPATIAL
            : RUSD_AUDIO_SPATIAL;
        const std::string playback = GetAttrOr(audio.GetPlaybackModeAttr(), TfToken("onceFromStart"), time).GetString();
        entry.audio.loop = playback.rfind("loop", 0) == 0 ? 1 : 0;
        entry.audio.gain = static_cast<float>(GetAttrOr(audio.GetGainAttr(), 1.0, time));
        entry.audio.startTime = GetAttrOr(audio.GetStartTimeAttr(), SdfTimeCode(0.0), time).GetValue();
        entry.audio.endTime = GetAttrOr(audio.GetEndTimeAttr(), SdfTimeCode(0.0), time).GetValue();
        entry.filePath = ReadAssetAttr(context, audio.GetFilePathAttr());
        context->importedAudio.push_back(entry);
    }
}

// What a prim passes down the walk: the purpose and visibility its ancestors and its own opinions
// give it, updated in place. False when its purpose keeps it, and everything below, out of the
// import. ImportSubtree applies it to every prim it enters; RUsd_ReadSubtrees replays it down a
// subtree's ancestors, so the subtree arrives as the full walk would have read it.
static bool InheritState(ImportWalk* walk, const UsdPrim& prim, UsdGeomImageable::PurposeInfo* purposeInfo,
                         int* visible, bool* animatesVisibility)
{
    *animatesVisibility = false;

    // proxy and guide geometry is for viewports and tools, not the render: ALab carries a proxy
    // copy of most assets and imported 24.6% of its triangles twice. Purpose inherits, so the
    // whole subtree goes.
    if (prim.IsA<UsdGeomImageable>())
    {
        const UsdGeomImageable imageable(prim);
        *purposeInfo = imageable.ComputePurposeInfo(*purposeInfo);
        // A Skeleton is not geometry: its purpose only says whether a viewport draws its bones.
        // DCC exporters often author it `guide` (the Elephant with Monochord sample does), and
        // dropping it left its node unlinked, so the meshes that skin to it imported unskinned in
        // their bind shape (arms down) instead of the pose Isaac Sim shows. It has no prim
        // children to carry the purpose further.
        if ((purposeInfo->purpose == UsdGeomTokens->proxy || purposeInfo->purpose == UsdGeomTokens->guide) &&
            !prim.IsA<UsdSkelSkeleton>())
        {
            return false;
        }

        // Visibility inherits the same way: invisible anywhere above wins, and otherwise the
        // prim's own opinion decides. At and below a prim whose visibility is animated that is
        // left to the host's hierarchy instead -- an inactive parent hides its children, which is
        // USD's inheritance -- so every prim there reports its own opinion and keeps its geometry:
        // what the animation reveals later has to be there to show.
        const UsdAttribute visibilityAttr = imageable.GetVisibilityAttr();
        *animatesVisibility = visibilityAttr.ValueMightBeTimeVarying();
        const bool ownOpinionOnly = *animatesVisibility || walk->animatedVisibilityScopes > 0;
        if (*visible != 0 || ownOpinionOnly)
        {
            TfToken own;
            const bool ownInvisible = visibilityAttr.Get(&own, walk->context->importTime) &&
                own == UsdGeomTokens->invisible;
            *visible = ownInvisible ? 0 : (ownOpinionOnly ? 1 : *visible);
        }
    }
    else if (walk->animatedVisibilityScopes > 0)
    {
        *visible = 1; // no opinion of its own: the hierarchy above decides
    }

    return true;
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

    // Once the node budget is spent (and the import skips past it), nothing after fits either.
    if (walk->context->skippedNodes > 0)
    {
        return;
    }

    UsdGeomImageable::PurposeInfo purposeInfo = inheritedPurpose;
    int visible = parentVisible;
    bool animatesVisibility = false;
    if (!InheritState(walk, prim, &purposeInfo, &visible, &animatesVisibility))
    {
        return;
    }

    struct VisibilityScope
    {
        int* scopes;
        bool entered;
        ~VisibilityScope() { if (entered) --*scopes; }
    } visibilityScope{&walk->animatedVisibilityScopes, animatesVisibility};
    if (animatesVisibility)
    {
        ++walk->animatedVisibilityScopes;
    }

    if (prim.IsA<UsdSkelRoot>())
    {
        PopulateSkelRoot(walk, prim);
    }

    // Record every transformable prim (Xform, Mesh, ...) so the Unity side can rebuild the
    // hierarchy with per-node local transforms instead of baking.
    if (prim.IsA<UsdGeomXformable>())
    {
        bool resetsXformStack = false;
        GfMatrix4d local = walk->xformCache.GetLocalTransformation(prim, &resetsXformStack);
        if (!EmitNode(walk, prim, outPath, local, visible))
        {
            return;
        }

        const int nodeIndex = static_cast<int>(walk->context->importedNodes.size()) - 1;
        EmitSceneComponents(walk, prim, nodeIndex);
        if (outPath == prim.GetPath().GetString())
        {
            // Copies inside a point-instancer instance share the prototype's samples; they are
            // not animated nodes of their own.
            EmitNodeTracks(walk, prim, nodeIndex);
        }

        // A Skeleton inside a point-instancer prototype is the one its prototype's meshes skin to:
        // the prototype is imported once, hidden, and drawn per instance from its posed meshes.
        if (prim.IsA<UsdSkelSkeleton>())
        {
            LinkSkeletonNode(walk, prim, nodeIndex);
        }
    }
    else if (!prim.IsA<UsdTyped>() && !prim.IsPseudoRoot() && !IsShadingOnlyPrim(prim))
    {
        // Typeless prims (`def "rig"`) and prims of a schema this build does not know (a
        // studio's custom types) were dropped, and everything they carried with them: ALab
        // lost 19, among them typeless light-rig prims that exist to hold a reference. They
        // have no transform of their own, so they become identity nodes; a known
        // non-transformable type (Material, Shader, GeomSubset, ...) stays out as before.
        // A typeless prim has no visibility of its own, so it takes what it inherited.
        if (!EmitNode(walk, prim, outPath, GfMatrix4d(1.0), visible))
        {
            return;
        }
    }

    // Invisible geometry is not read: Hydra does not populate it, and the importer used to
    // read, split and triangulate it only to mark the renderer disabled -- 1,274 of Factory's
    // 13,843 meshes, 102 of Moore Lane's. The node stays, with its visibility, so the
    // hierarchy is unchanged; only the mesh behind it is absent.
    if (prim.IsLoaded() && (visible != 0 || walk->animatedVisibilityScopes > 0))
    {
        const size_t meshesBefore = walk->context->importedMeshes.size();
        EmitGeometry(walk, prim, outPath);
        if (walk->animatedVisibilityScopes > 0 && walk->context->importedMeshes.size() > meshesBefore)
        {
            walk->context->importedMeshes.back().visible = visible;
        }
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

// Static values are read where a viewer opens the stage: at startTimeCode when the stage authors
// a time range, else at the earliest time, which gives an attribute its first sample when it has
// samples and its default when it has none. Reading at UsdTimeCode::Default() ignores samples
// altogether, so stages that author transforms only as samples (Blender's animated exports)
// arrived with every object at its parent's origin.
static void ResolveImportTime(RUsdContext* context)
{
    const UsdStageRefPtr& stage = context->stage;
    RUsdImportTimeInfo& info = context->timeInfo;
    info.startTimeCode = stage->GetStartTimeCode();
    info.endTimeCode = stage->GetEndTimeCode();
    info.timeCodesPerSecond = stage->GetTimeCodesPerSecond();
    info.framesPerSecond = stage->GetFramesPerSecond();
    info.hasTimeRange = stage->HasAuthoredTimeCodeRange() ? 1 : 0;
    if (context->importAtDefaultTime)
    {
        info.readTimeKind = RUSD_READ_TIME_DEFAULT;
        context->importTime = UsdTimeCode::Default();
    }
    else if (info.hasTimeRange != 0)
    {
        info.readTimeKind = RUSD_READ_TIME_START;
        context->importTime = UsdTimeCode(info.startTimeCode);
    }
    else
    {
        info.readTimeKind = RUSD_READ_TIME_EARLIEST;
        context->importTime = UsdTimeCode::EarliestTime();
    }
}

// The stage-wide facts every read of a stage starts from: the time static values are read at,
// the defaultPrim, the units and the up axis.
static void ReadStageMetrics(RUsdContext* context)
{
    ResolveImportTime(context);
    UsdPrim defaultPrim = context->stage->GetDefaultPrim();
    context->defaultPrimPath = defaultPrim ? defaultPrim.GetPath().GetString() : std::string();
    context->importMetersPerUnit = UsdGeomGetStageMetersPerUnit(context->stage);
    TfToken upAxis = UsdGeomGetStageUpAxis(context->stage);
    context->importUpAxis = upAxis == UsdGeomTokens->z ? 2 : 1;
}

// Instance proxies included: instanceable prims (Omniverse and most CAD exports build
// assemblies this way) keep all their geometry in prototypes, and the default
// traversal stops at the instance, which imported such stages as empty.
// The default predicate minus IsLoaded: with every payload loaded (RUsd_OpenStage) the two
// are the same, and with payloads left unloaded the payload's own prim still comes through
// as an empty placeholder node, so the hierarchy keeps its place for it.
static Usd_PrimFlagsPredicate ImportPredicate()
{
    return UsdTraverseInstanceProxies(UsdPrimIsActive && UsdPrimIsDefined && !UsdPrimIsAbstract);
}

static void AppendSkipSummary(RUsdContext* context);

// Walks an opened stage into the context's node and mesh tables. Shared by RUsd_OpenStage and
// RUsd_OpenStageEx so both import paths read a stage identically.
static void ReadImportedStage(RUsdContext* context)
{
    ReadStageMetrics(context);
    ImportWalk walk(context, ImportPredicate());
    for (const UsdPrim& prim : context->stage->GetPseudoRoot().GetFilteredChildren(walk.predicate))
    {
        ImportSubtree(&walk, prim, prim.GetPath().GetString(), 0, UsdGeomImageable::PurposeInfo(), 1);
    }

    AppendSkipSummary(context);
}

// What an import that skips past its limits left out. Put first, past the warning cap: these
// say the result is partial, which is the one thing the host must not miss, and a host logs
// the first few warnings only.
static void AppendSkipSummary(RUsdContext* context)
{
    const RUsdContext::ImportLimits& limits = context->importLimits;
    std::string partial;
    if (context->skippedNodes > 0)
    {
        partial += "USD import: the stage exceeds the import limit of " + std::to_string(limits.nodes) +
            " nodes; the walk stopped at '" + context->firstSkippedNode +
            "', and the prims after it were not imported.\n";
    }

    if (context->skippedMeshes > 0)
    {
        partial += "USD import: " + std::to_string(context->skippedMeshes) +
            " meshes were not imported: over the import limit of " + std::to_string(limits.meshes) +
            " meshes or " + std::to_string(limits.geometryElements) + " points and triangle indices.\n";
    }

    if (context->skippedPlacements > 0)
    {
        partial += "USD import: " + std::to_string(context->skippedPlacements) +
            " point-instancer placements were not drawn: over the import limit of " +
            std::to_string(limits.instancePlacements) + ".\n";
    }

    std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
    context->diagnostics.insert(0, partial);
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

// Linux: which TBB OpenUSD actually runs on. The dynamic linker looks a symbol up in the process's
// global scope before a library's own dependencies, so libusd_ms's TBB calls go to whatever
// libtbb.so.2 the host process links -- in the Editor, the Editor's Data/Tools/libtbb.so.2 -- and
// not to the payload's. Windows renames its TBB (tbb_usdrt.dll) to keep the Editor's out; on Linux
// a rename does not, because the lookup is by symbol, not by library: a renamed copy was tested
// and the two TBBs ended up serving one process between them. Running on the host's TBB is fine
// while it is the same ABI (TBB 2020.3, interface 11103, in the Editors so far). What is refused is
// an older one, which TBB's compatibility rules do not cover. dlsym from here searches the scope
// libusd_ms's calls are bound in: the global scope, then this library's own dependencies.
bool TbbRuntimeIsUsable()
{
#if defined(__linux__)
    static const std::string problem = []() -> std::string {
        using VersionFunction = int (*)();
        void* symbol = dlsym(RTLD_DEFAULT, "TBB_runtime_interface_version");
        if (symbol == nullptr)
        {
            return std::string();
        }

        const int runtime = reinterpret_cast<VersionFunction>(symbol)();
        if (runtime >= TBB_INTERFACE_VERSION)
        {
            return std::string();
        }

        Dl_info info = {};
        const char* file = dladdr(symbol, &info) != 0 && info.dli_fname != nullptr ? info.dli_fname : "an unknown library";
        std::ostringstream stream;
        stream << "OpenUSD is running on TBB interface version " << runtime << " from " << file
               << ", older than the " << TBB_INTERFACE_VERSION
               << " this payload was built against. The host process loaded that TBB before the"
                  " package's own lib/libtbb.so.2, and the package cannot run on it.";
        return stream.str();
    }();

    if (!problem.empty())
    {
        SetError(nullptr, problem);
        return false;
    }
#endif
    return true;
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

namespace
{
bool IsDracoPath(const std::string& path)
{
    return TfStringToLower(TfGetExtension(path)) == "drc";
}
} // namespace

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
    if (!TbbRuntimeIsUsable())
    {
        return kFailure;
    }

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

        // CreateNew on such a format gives a layer whose first edit fails (and the next one throws),
        // so the stage is a usda one in memory, written out in the output's format by EndExport.
        const SdfFileFormatConstPtr format = SdfFileFormat::FindByExtension(output);
        if (IsDracoPath(output))
        {
            context->stage = UsdStage::CreateInMemory();
            context->exportTo = output;
            context->exportDraco = true;
        }
        else if (format && format->SupportsWriting() && !format->SupportsEditing())
        {
            context->stage = UsdStage::CreateInMemory();
            context->exportTo = output;
        }
        else
        {
            context->stage = UsdStage::CreateNew(output);
        }

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

namespace
{
// Draco's quantization bits for positions, texture coordinates and normals, and its compression
// level (0-10): OpenUSD's usdcompress defaults but for the level (7, Draco's own default speed),
// within a few micrometers for a model a meter across.
constexpr int kDracoPositionBits = 14;
constexpr int kDracoTexCoordBits = 12;
constexpr int kDracoNormalBits = 10;
constexpr int kDracoCompressionLevel = 7;

// The value at every face corner, in face order, from values of any interpolation (and indices,
// for an indexed primvar). False when a value is missing or an index is out of range.
template <typename T>
bool ExpandToCorners(
    const VtArray<T>& values,
    const VtIntArray& valueIndices,
    const TfToken& interpolation,
    const VtIntArray& faceCounts,
    const VtIntArray& faceIndices,
    std::vector<T>& corners)
{
    corners.clear();
    corners.reserve(faceIndices.size());
    size_t corner = 0;
    for (size_t face = 0; face < faceCounts.size(); ++face)
    {
        for (int k = 0; k < faceCounts[face]; ++k, ++corner)
        {
            size_t element;
            if (interpolation == UsdGeomTokens->vertex || interpolation == UsdGeomTokens->varying)
            {
                element = static_cast<size_t>(faceIndices[corner]);
            }
            else if (interpolation == UsdGeomTokens->faceVarying)
            {
                element = corner;
            }
            else if (interpolation == UsdGeomTokens->uniform)
            {
                element = face;
            }
            else if (interpolation == UsdGeomTokens->constant)
            {
                element = 0;
            }
            else
            {
                return false;
            }

            if (!valueIndices.empty())
            {
                if (element >= valueIndices.size() || valueIndices[element] < 0)
                {
                    return false;
                }

                element = static_cast<size_t>(valueIndices[element]);
            }

            if (element >= values.size())
            {
                return false;
            }

            corners.push_back(values[element]);
        }
    }

    return true;
}

// A primvar's values at every face corner; false when it has none or they do not fit the faces.
template <typename T>
bool PrimvarCorners(
    const UsdGeomPrimvar& primvar,
    UsdTimeCode time,
    const VtIntArray& faceCounts,
    const VtIntArray& faceIndices,
    std::vector<T>& corners)
{
    VtArray<T> values;
    if (!primvar || !primvar.Get(&values, time) || values.empty())
    {
        return false;
    }

    VtIntArray valueIndices;
    primvar.GetIndices(&valueIndices, time);
    return ExpandToCorners(values, valueIndices, primvar.GetInterpolation(), faceCounts, faceIndices, corners);
}

// The mesh's primary texture coordinates: the first UV set alias with float2 values.
UsdGeomPrimvar PrimaryUvPrimvar(const UsdGeomMesh& mesh)
{
    const UsdGeomPrimvarsAPI primvars(mesh.GetPrim());
    for (const TfToken& name : UvSetAliasTokens()[0])
    {
        UsdGeomPrimvar primvar = primvars.GetPrimvar(name);
        if (primvar && primvar.HasAuthoredValue() &&
            (primvar.GetTypeName() == SdfValueTypeNames->TexCoord2fArray ||
             primvar.GetTypeName() == SdfValueTypeNames->Float2Array))
        {
            return primvar;
        }
    }

    return UsdGeomPrimvar();
}

// A .drc holds one mesh, with no hierarchy, transform or material. Every visible mesh of the
// export stage, at its first time, is merged into one in the stage's space: points, faces (their
// winding kept facing out through mirroring transforms and left-handed meshes) and, where every
// mesh has them, normals and the primary texture coordinates, as face-varying values Draco
// deduplicates. usdDraco's writer then encodes it, keeping polygons (Draco triangulates and
// records the edges it added, which its reader undoes).
bool WriteDracoFile(RUsdContext* context, const std::string& output)
{
    const UsdTimeCode time = UsdTimeCode::EarliestTime();
    UsdGeomXformCache xformCache(time);
    VtVec3fArray points;
    VtIntArray faceCounts;
    VtIntArray faceIndices;
    std::vector<GfVec3f> normals;
    std::vector<GfVec2f> uvs;
    bool everyNormal = true;
    bool everyUv = true;
    int meshCount = 0;

    for (const UsdPrim& prim : UsdPrimRange::Stage(context->stage, UsdTraverseInstanceProxies()))
    {
        const UsdGeomMesh mesh(prim);
        if (!mesh || mesh.ComputeVisibility(time) == UsdGeomTokens->invisible)
        {
            continue;
        }

        const TfToken purpose = mesh.ComputePurpose();
        if (purpose == UsdGeomTokens->guide || purpose == UsdGeomTokens->proxy)
        {
            continue;
        }

        VtVec3fArray meshPoints;
        VtIntArray meshCounts;
        VtIntArray meshIndices;
        mesh.GetPointsAttr().Get(&meshPoints, time);
        mesh.GetFaceVertexCountsAttr().Get(&meshCounts, time);
        mesh.GetFaceVertexIndicesAttr().Get(&meshIndices, time);
        if (meshPoints.empty() || meshCounts.empty())
        {
            continue;
        }

        size_t cornerCount = 0;
        for (int count : meshCounts)
        {
            if (count < 3)
            {
                SetError(context, "Mesh " + prim.GetPath().GetString() +
                    " has a face with fewer than three corners; Draco cannot hold it.");
                return false;
            }

            cornerCount += static_cast<size_t>(count);
        }

        if (cornerCount != meshIndices.size())
        {
            SetError(context, "Mesh " + prim.GetPath().GetString() +
                " has face vertex counts that do not match its indices.");
            return false;
        }

        for (int index : meshIndices)
        {
            if (index < 0 || static_cast<size_t>(index) >= meshPoints.size())
            {
                SetError(context, "Mesh " + prim.GetPath().GetString() +
                    " has a face vertex index outside its points.");
                return false;
            }
        }

        std::vector<GfVec3f> meshNormals;
        const UsdGeomPrimvar normalsPrimvar = UsdGeomPrimvarsAPI(prim).GetPrimvar(UsdGeomTokens->normals);
        bool hasNormals = false;
        if (normalsPrimvar && normalsPrimvar.HasAuthoredValue())
        {
            hasNormals = PrimvarCorners(normalsPrimvar, time, meshCounts, meshIndices, meshNormals);
        }
        else
        {
            VtVec3fArray values;
            hasNormals = mesh.GetNormalsAttr().Get(&values, time) && !values.empty() &&
                ExpandToCorners(values, VtIntArray(), mesh.GetNormalsInterpolation(), meshCounts, meshIndices,
                                meshNormals);
        }

        std::vector<GfVec2f> meshUvs;
        const bool hasUvs = PrimvarCorners(PrimaryUvPrimvar(mesh), time, meshCounts, meshIndices, meshUvs);

        const GfMatrix4d toStage = xformCache.GetLocalToWorldTransform(prim);
        const GfMatrix4d toStageNormal = toStage.GetInverse().GetTranspose();
        TfToken orientation;
        mesh.GetOrientationAttr().Get(&orientation);
        const bool reverse = (toStage.GetDeterminant() < 0.0) != (orientation == UsdGeomTokens->leftHanded);

        const int base = static_cast<int>(points.size());
        for (const GfVec3f& point : meshPoints)
        {
            points.push_back(GfVec3f(toStage.Transform(GfVec3d(point))));
        }

        everyNormal = everyNormal && hasNormals;
        everyUv = everyUv && hasUvs;
        size_t corner = 0;
        for (int count : meshCounts)
        {
            faceCounts.push_back(count);
            for (int k = 0; k < count; ++k)
            {
                const size_t source = corner + static_cast<size_t>(reverse ? count - 1 - k : k);
                faceIndices.push_back(base + meshIndices[source]);
                if (everyNormal)
                {
                    GfVec3f normal(toStageNormal.TransformDir(GfVec3d(meshNormals[source])));
                    const float length = normal.GetLength();
                    normals.push_back(length > 0.0f ? normal / length : normal);
                }

                if (everyUv)
                {
                    uvs.push_back(meshUvs[source]);
                }
            }

            corner += static_cast<size_t>(count);
        }

        ++meshCount;
    }

    if (meshCount == 0)
    {
        SetError(context, "There is no visible mesh to write to the Draco file.");
        return false;
    }

    UsdStageRefPtr merged = UsdStage::CreateInMemory();
    UsdGeomMesh mesh = UsdGeomMesh::Define(merged, SdfPath("/DracoMesh"));
    mesh.CreatePointsAttr().Set(points);
    mesh.CreateFaceVertexCountsAttr().Set(faceCounts);
    mesh.CreateFaceVertexIndicesAttr().Set(faceIndices);
    mesh.CreateSubdivisionSchemeAttr().Set(UsdGeomTokens->none);
    // usdDraco's writer takes face-varying values only with indices, one per corner.
    VtIntArray cornerIndices(faceIndices.size());
    for (size_t i = 0; i < cornerIndices.size(); ++i)
    {
        cornerIndices[i] = static_cast<int>(i);
    }

    const UsdGeomPrimvarsAPI primvars(mesh.GetPrim());
    if (everyNormal && !normals.empty())
    {
        UsdGeomPrimvar primvar = primvars.CreatePrimvar(
            UsdGeomTokens->normals, SdfValueTypeNames->Normal3fArray, UsdGeomTokens->faceVarying);
        primvar.Set(VtVec3fArray(normals.begin(), normals.end()));
        primvar.SetIndices(cornerIndices);
    }

    if (everyUv && !uvs.empty())
    {
        UsdGeomPrimvar primvar = primvars.CreatePrimvar(
            TfToken("st"), SdfValueTypeNames->TexCoord2fArray, UsdGeomTokens->faceVarying);
        primvar.Set(VtVec2fArray(uvs.begin(), uvs.end()));
        primvar.SetIndices(cornerIndices);
    }

    if (!UsdDraco_WriteDraco(mesh, output, kDracoPositionBits, kDracoTexCoordBits, kDracoNormalBits,
                             kDracoCompressionLevel, 1, 0, 0))
    {
        SetError(context, "OpenUSD's Draco writer failed to encode or write " + output + ".");
        return false;
    }

    return true;
}
} // namespace

int RUsd_EndExport(RUsdContext* context)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    try
    {
        if (context->exportDraco)
        {
            if (!WriteDracoFile(context, context->exportTo))
            {
                return kFailure;
            }

            context->stage.Reset();
            return kSuccess;
        }

        bool saved = context->exportTo.empty()
            ? context->stage->GetRootLayer()->Save()
            : context->stage->GetRootLayer()->Export(context->exportTo);
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

namespace
{
// The mesh RUsd_AddMesh(Ex) wrote at primPath, for the per-mesh setters below.
bool FindExportMesh(RUsdContext* context, const char* primPath, int count, UsdGeomMesh* mesh)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return false;
    }

    SdfPath path(SafeString(primPath));
    if (!path.IsAbsolutePath() || path.IsAbsoluteRootPath())
    {
        SetError(context, "USD mesh prim path must be absolute.");
        return false;
    }

    *mesh = UsdGeomMesh(context->stage->GetPrimAtPath(path));
    if (!*mesh)
    {
        SetError(context, "No exported mesh at '" + path.GetString() + "'.");
        return false;
    }

    VtVec3fArray points;
    mesh->GetPointsAttr().Get(&points);
    if (count <= 0 || static_cast<size_t>(count) != points.size())
    {
        SetError(context, "Per-vertex data for '" + path.GetString() + "' must have one value per point.");
        return false;
    }

    return true;
}

// Defines a light or camera prim (any UsdGeomXformable schema) with its parents, local matrix
// and visibility.
template <typename Schema>
Schema DefineXformable(RUsdContext* context, const char* primPath, const RUsdMatrix4x4* matrix, int visible)
{
    SdfPath path(SafeString(primPath));
    if (!path.IsAbsolutePath() || path.IsAbsoluteRootPath())
    {
        SetError(context, "USD prim path must be absolute.");
        return Schema();
    }

    EnsureParentXforms(context->stage, path);
    Schema schema = Schema::Define(context->stage, path);
    UsdGeomXformable xformable(schema.GetPrim());
    xformable.ClearXformOpOrder();
    if (matrix != nullptr)
    {
        xformable.MakeMatrixXform().Set(ToGfMatrix(*matrix));
    }

    UsdGeomImageable(schema.GetPrim()).CreateVisibilityAttr().Set(
        visible != 0 ? UsdGeomTokens->inherited : UsdGeomTokens->invisible);
    return schema;
}
}  // namespace

int RUsd_SetMeshUvSet(RUsdContext* context, const char* primPath, int uvSet, const RUsdVec2* uv, int count)
{
    try
    {
        if (uvSet < 1 || uvSet > 2 || uv == nullptr)
        {
            SetError(context, "UV set must be 1 or 2 (uv0 is written by RUsd_AddMesh).");
            return kFailure;
        }

        UsdGeomMesh mesh;
        if (!FindExportMesh(context, primPath, count, &mesh))
        {
            return kFailure;
        }

        // st1 / st2: the first names the importer's UV-set table looks for.
        UsdGeomPrimvar primvar = UsdGeomPrimvarsAPI(mesh.GetPrim()).CreatePrimvar(
            TfToken(uvSet == 1 ? "st1" : "st2"), SdfValueTypeNames->TexCoord2fArray, UsdGeomTokens->vertex);
        primvar.Set(CopyUv0(uv, count));
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to write a UV set", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to write a UV set.");
        return kFailure;
    }
}

int RUsd_SetMeshVertexColors(RUsdContext* context, const char* primPath, const RUsdVec4* colors, int count)
{
    try
    {
        UsdGeomMesh mesh;
        if (colors == nullptr || !FindExportMesh(context, primPath, count, &mesh))
        {
            if (colors == nullptr)
            {
                SetError(context, "Vertex colours are null.");
            }

            return kFailure;
        }

        VtArray<GfVec3f> rgb(static_cast<size_t>(count));
        VtArray<float> alpha(static_cast<size_t>(count));
        bool translucent = false;
        for (int i = 0; i < count; ++i)
        {
            rgb[static_cast<size_t>(i)] = GfVec3f(colors[i].x, colors[i].y, colors[i].z);
            alpha[static_cast<size_t>(i)] = colors[i].w;
            translucent = translucent || colors[i].w < 1.0f;
        }

        mesh.CreateDisplayColorPrimvar(UsdGeomTokens->vertex).Set(rgb);
        // The material mirror may have authored a uniform/constant opacity; keep the two
        // primvars at one interpolation.
        if (translucent || mesh.GetDisplayOpacityPrimvar().HasAuthoredValue())
        {
            mesh.CreateDisplayOpacityPrimvar(UsdGeomTokens->vertex).Set(alpha);
        }

        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to write vertex colours", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to write vertex colours.");
        return kFailure;
    }
}

int RUsd_SetExportMaterialFlavors(RUsdContext* context, int flags)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return kFailure;
    }

    context->materialFlavors = flags & (RUSD_MATERIAL_FLAVOR_OMNIPBR | RUSD_MATERIAL_FLAVOR_MATERIALX);
    return kSuccess;
}

int RUsd_AddLight(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    const RUsdExportLight* light,
    int visible)
{
    if (context == nullptr || !context->stage || light == nullptr)
    {
        SetError(context, "USD export context or light is invalid.");
        return kFailure;
    }

    try
    {
        UsdPrim prim;
        switch (light->type)
        {
            case RUSD_LIGHT_DISTANT:
            {
                UsdLuxDistantLight distant = DefineXformable<UsdLuxDistantLight>(context, primPath, matrix, visible);
                if (distant)
                {
                    distant.CreateAngleAttr().Set(light->angle);
                    prim = distant.GetPrim();
                }

                break;
            }
            case RUSD_LIGHT_SPHERE:
            {
                UsdLuxSphereLight sphere = DefineXformable<UsdLuxSphereLight>(context, primPath, matrix, visible);
                if (sphere)
                {
                    sphere.CreateRadiusAttr().Set(light->radius);
                    prim = sphere.GetPrim();
                }

                break;
            }
            case RUSD_LIGHT_RECT:
            {
                UsdLuxRectLight rect = DefineXformable<UsdLuxRectLight>(context, primPath, matrix, visible);
                if (rect)
                {
                    rect.CreateWidthAttr().Set(light->width);
                    rect.CreateHeightAttr().Set(light->height);
                    prim = rect.GetPrim();
                }

                break;
            }
            case RUSD_LIGHT_DISK:
            {
                UsdLuxDiskLight disk = DefineXformable<UsdLuxDiskLight>(context, primPath, matrix, visible);
                if (disk)
                {
                    disk.CreateRadiusAttr().Set(light->radius);
                    prim = disk.GetPrim();
                }

                break;
            }
            default:
                SetError(context, "Unsupported light type for export.");
                return kFailure;
        }

        if (!prim)
        {
            return kFailure;
        }

        UsdLuxLightAPI api(prim);
        api.CreateColorAttr().Set(GfVec3f(light->color[0], light->color[1], light->color[2]));
        api.CreateIntensityAttr().Set(light->intensity);
        if (light->exposure != 0.0f)
        {
            api.CreateExposureAttr().Set(light->exposure);
        }

        if (light->normalize != 0)
        {
            api.CreateNormalizeAttr().Set(true);
        }

        if (light->colorTemperature > 0.0f)
        {
            api.CreateEnableColorTemperatureAttr().Set(true);
            api.CreateColorTemperatureAttr().Set(light->colorTemperature);
        }

        if (light->coneAngle > 0.0f && light->type != RUSD_LIGHT_DISTANT)
        {
            UsdLuxShapingAPI shaping = UsdLuxShapingAPI::Apply(prim);
            shaping.CreateShapingConeAngleAttr().Set(light->coneAngle);
            shaping.CreateShapingConeSoftnessAttr().Set(light->coneSoftness);
        }

        UsdLuxShadowAPI::Apply(prim).CreateShadowEnableAttr().Set(light->castShadows != 0);
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to add light", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to add light.");
        return kFailure;
    }
}

int RUsd_AddCamera(
    RUsdContext* context,
    const char* primPath,
    const RUsdMatrix4x4* matrix,
    const RUsdExportCamera* camera,
    int visible)
{
    if (context == nullptr || !context->stage || camera == nullptr)
    {
        SetError(context, "USD export context or camera is invalid.");
        return kFailure;
    }

    try
    {
        UsdGeomCamera usdCamera = DefineXformable<UsdGeomCamera>(context, primPath, matrix, visible);
        if (!usdCamera)
        {
            return kFailure;
        }

        usdCamera.CreateProjectionAttr().Set(camera->projection == RUSD_CAMERA_ORTHOGRAPHIC
            ? UsdGeomTokens->orthographic
            : UsdGeomTokens->perspective);
        usdCamera.CreateFocalLengthAttr().Set(camera->focalLength);
        usdCamera.CreateHorizontalApertureAttr().Set(camera->horizontalAperture);
        usdCamera.CreateVerticalApertureAttr().Set(camera->verticalAperture);
        if (camera->horizontalApertureOffset != 0.0f || camera->verticalApertureOffset != 0.0f)
        {
            usdCamera.CreateHorizontalApertureOffsetAttr().Set(camera->horizontalApertureOffset);
            usdCamera.CreateVerticalApertureOffsetAttr().Set(camera->verticalApertureOffset);
        }

        usdCamera.CreateClippingRangeAttr().Set(GfVec2f(camera->nearClip, camera->farClip));
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to add camera", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to add camera.");
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
    std::string diagnostics;
    if (context != nullptr)
    {
        std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
        diagnostics = context->diagnostics;
    }
    else
    {
        diagnostics = g_lastDiagnostics;
    }
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
    if (!TbbRuntimeIsUsable())
    {
        return kFailure;
    }

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

int RUsd_SetRemoteAssetCache(const char* folder)
{
    std::lock_guard<std::mutex> lock(g_remoteCacheMutex);
    g_remoteCache = folder != nullptr ? std::string(folder) : std::string();
    return kSuccess;
}

int RUsd_GetImportRemoteRequestCount(RUsdContext* context, int* count)
{
    if (context == nullptr || count == nullptr)
    {
        SetError(context, "USD import remote request count needs a context and an output.");
        return kFailure;
    }
    *count = static_cast<int>(context->remoteRequests.size());
    return kSuccess;
}

int RUsd_GetImportRemoteRequest(RUsdContext* context, int index, char* buffer, int bufferCapacity)
{
    if (context == nullptr || index < 0 || index >= static_cast<int>(context->remoteRequests.size()))
    {
        SetError(context, "USD import remote request index is invalid.");
        return kFailure;
    }
    CopyStringToBuffer(context->remoteRequests[static_cast<size_t>(index)], buffer, bufferCapacity);
    return kSuccess;
}

int RUsd_GetImportMaterialNormalFlip(RUsdContext* context, int materialIndex, int* flipU, int* flipV)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material = context->importedMaterials[static_cast<size_t>(materialIndex)];
    if (flipU != nullptr)
    {
        *flipU = material.normalFlipU ? 1 : 0;
    }
    if (flipV != nullptr)
    {
        *flipV = material.normalFlipV ? 1 : 0;
    }
    return kSuccess;
}

int RUsd_GetImportTextureColorSpace(
    RUsdContext* context,
    const char* texturePath,
    char* buffer,
    int bufferCapacity)
{
    if (context == nullptr || texturePath == nullptr)
    {
        SetError(context, "USD import texture colour space query needs a context and a path.");
        return kFailure;
    }

    auto found = context->assetColorSpaces.find(texturePath);
    CopyStringToBuffer(found == context->assetColorSpaces.end() ? std::string() : found->second, buffer,
                       bufferCapacity);
    return kSuccess;
}

int RUsd_GetImportMaterialAlbedoMask(
    RUsdContext* context,
    int materialIndex,
    char* maskTexturePathBuffer,
    int maskTexturePathBufferCapacity,
    float* maskColorRgb)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()))
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material =
        context->importedMaterials[static_cast<size_t>(materialIndex)];
    if (maskTexturePathBuffer != nullptr && maskTexturePathBufferCapacity > 0)
    {
        CopyStringToBuffer(material.maskTexturePath, maskTexturePathBuffer, maskTexturePathBufferCapacity);
    }

    if (maskColorRgb != nullptr)
    {
        maskColorRgb[0] = material.maskColor[0];
        maskColorRgb[1] = material.maskColor[1];
        maskColorRgb[2] = material.maskColor[2];
    }

    return kSuccess;
}

int RUsd_GetImportMaterialUnrealBlend(
    RUsdContext* context,
    int materialIndex,
    float* values)
{
    if (context == nullptr || materialIndex < 0 ||
        materialIndex >= static_cast<int>(context->importedMaterials.size()) || values == nullptr)
    {
        SetError(context, "USD import material index is invalid.");
        return kFailure;
    }

    const RUsdContext::ImportedMaterial& material =
        context->importedMaterials[static_cast<size_t>(materialIndex)];
    values[0] = static_cast<float>(material.maskMode);
    for (int i = 0; i < 9; ++i)
    {
        values[1 + i] = material.channelColors[i];
    }
    values[10] = material.hasRoughnessRange ? 1.0f : 0.0f;
    values[11] = material.roughnessMin;
    values[12] = material.roughnessMax;
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
    if (!TbbRuntimeIsUsable())
    {
        return kFailure;
    }

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

int RUsd_AddRelocate(RUsdContext* context, const char* sourcePath, const char* targetPath)
{
    return RunExportEdit(context, "add a relocate", [&]() {
        SdfPath source;
        SdfPath target;
        if (!ParseAbsolutePrimPath(context, sourcePath, &source) ||
            !ParseAbsolutePrimPath(context, targetPath, &target))
        {
            return kFailure;
        }

        // What Pcp would refuse anyway, refused here with a reason: a root prim has no parent to
        // leave, and a prim cannot move into its own subtree.
        if (source.GetPathElementCount() < 2 || target.GetPathElementCount() < 2)
        {
            SetError(context, "A relocate cannot move a root prim, or make one.");
            return kFailure;
        }

        if (source == target || target.HasPrefix(source))
        {
            SetError(context, "A relocate cannot move '" + source.GetString() + "' to itself or into its own subtree.");
            return kFailure;
        }

        SdfLayerHandle layer = context->stage->GetRootLayer();
        SdfRelocates relocates = layer->GetRelocates();
        for (const SdfRelocate& existing : relocates)
        {
            if (existing.first == source || existing.second == target)
            {
                SetError(context, "The layer already relocates '" + existing.first.GetString() + "' to '" +
                    existing.second.GetString() + "'.");
                return kFailure;
            }
        }

        relocates.emplace_back(source, target);
        layer->SetRelocates(relocates);
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

// RUsd_OpenStageEx and RUsd_OpenStageWithLimits: `limits` null imports with the defaults.
// RUsdImportLimits into the walk's budgets: a field of 0 or less keeps the default.
static bool TakeImportLimits(RUsdContext* context, const RUsdImportLimits* limits, RUsdContext::ImportLimits* to)
{
    // A larger struct is a newer caller's; its extra fields are not this build's to read.
    if (limits->structSize < static_cast<int>(sizeof(RUsdImportLimits)))
    {
        SetError(context, "RUsdImportLimits.structSize is smaller than this plugin's RUsdImportLimits.");
        return false;
    }

    const auto take = [](long long value, size_t* field) {
        if (value > 0)
        {
            *field = static_cast<size_t>(value);
        }
    };
    take(limits->maxNodes, &to->nodes);
    take(limits->maxMeshes, &to->meshes);
    take(limits->maxInstancePlacements, &to->instancePlacements);
    take(limits->maxGeometryElements, &to->geometryElements);
    take(limits->maxTrackSamples, &to->trackSamples);
    take(limits->maxAnimationValues, &to->animationValues);
    take(limits->maxJoints, &to->joints);
    to->skipWhenExceeded = limits->skipWhenExceeded != 0;
    return true;
}

static int OpenStageImpl(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    const RUsdImportLimits* limits,
    RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    if (!TbbRuntimeIsUsable())
    {
        return kFailure;
    }

    std::unique_ptr<RUsdContext> context(new RUsdContext());

    if (limits != nullptr && !TakeImportLimits(context.get(), limits, &context->importLimits))
    {
        *outContext = context.release();
        return kFailure;
    }

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
        context->importAtDefaultTime = (flags & RUSD_OPEN_DEFAULT_TIME) != 0;
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

        // From here on what the stage recomposes is recorded (RUSD_QUERY_CHANGES); the choices
        // above are what it opened with.
        context->changeListener.reset(new RUsdChangeListener(context->stage, &context->changedPaths));
        if ((flags & RUSD_OPEN_NO_READ) == 0)
        {
            ReadImportedStage(context.get());
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

int RUsd_OpenStageEx(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    RUsdContext** outContext)
{
    return OpenStageImpl(inputPath, flags, variantSelections, loadPaths, nullptr, outContext);
}

int RUsd_OpenStageWithLimits(
    const char* inputPath,
    int flags,
    const char* variantSelections,
    const char* loadPaths,
    const RUsdImportLimits* limits,
    RUsdContext** outContext)
{
    return OpenStageImpl(inputPath, flags, variantSelections, loadPaths, limits, outContext);
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

// ---- Time, animation and skinning (API v13) -----------------------------------------------

int RUsd_GetImportLayers(RUsdContext* context, char* buffer, int bufferCapacity, int* requiredLength)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD import context is invalid.");
        return kFailure;
    }

    try
    {
        std::string report;
        std::set<std::string> seen;
        for (const SdfLayerHandle& layer : context->stage->GetUsedLayers())
        {
            if (!layer || layer->IsAnonymous())
            {
                continue;
            }

            // A layer inside a .usdz (inside a .usdz...) is the outermost package's file.
            std::string path = layer->GetRealPath();
            for (int depth = 0; depth < 8 && ArIsPackageRelativePath(path); ++depth)
            {
                path = ArSplitPackageRelativePathOuter(path).first;
            }

            if (path.empty() || !seen.insert(path).second)
            {
                continue;
            }

            report += path;
            report += '\n';
        }

        const int required = static_cast<int>(report.size()) + 1;
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
            SetError(context, "Buffer too small for RUsd_GetImportLayers.");
            return kFailure;
        }

        std::memcpy(buffer, report.c_str(), report.size() + 1);
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context, MakeError("Failed to list the stage's layers", exception));
        return kFailure;
    }
    catch (...)
    {
        SetError(context, "Failed to list the stage's layers.");
        return kFailure;
    }
}

int RUsd_GetImportTimeInfo(RUsdContext* context, RUsdImportTimeInfo* info)
{
    if (context == nullptr || info == nullptr || !context->stage)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportTimeInfo.");
        return kFailure;
    }

    *info = context->timeInfo;
    return kSuccess;
}

int RUsd_GetImportNodeTrackCount(RUsdContext* context, int* count)
{
    if (context == nullptr || count == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportNodeTrackCount.");
        return kFailure;
    }

    *count = static_cast<int>(context->importedNodeTracks.size());
    return kSuccess;
}

int RUsd_GetImportNodeTrack(RUsdContext* context, int index, RUsdImportNodeTrack* track)
{
    if (context == nullptr || track == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedNodeTracks.size())
    {
        SetError(context, "Invalid arguments for RUsd_GetImportNodeTrack.");
        return kFailure;
    }

    const RUsdContext::ImportedNodeTrack& source = context->importedNodeTracks[static_cast<size_t>(index)];
    track->nodeIndex = source.nodeIndex;
    track->kind = source.kind;
    track->sampleCount = static_cast<int>(source.times.size());
    return kSuccess;
}

int RUsd_CopyImportNodeTrack(
    RUsdContext* context,
    int index,
    double* times,
    int timeCapacity,
    float* values,
    int valueCapacity)
{
    if (context == nullptr || times == nullptr || values == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedNodeTracks.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportNodeTrack.");
        return kFailure;
    }

    const RUsdContext::ImportedNodeTrack& source = context->importedNodeTracks[static_cast<size_t>(index)];
    if (!FitsCapacity(timeCapacity, source.times.size()) || !FitsCapacity(valueCapacity, source.values.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportNodeTrack.");
        return kFailure;
    }

    std::copy(source.times.begin(), source.times.end(), times);
    std::copy(source.values.begin(), source.values.end(), values);
    return kSuccess;
}

namespace
{
std::string JoinJointNames(const RUsdContext::ImportedSkeleton& skeleton)
{
    std::string names;
    for (size_t joint = 0; joint < skeleton.joints.size(); ++joint)
    {
        if (joint > 0)
        {
            names += '\n';
        }

        names += skeleton.joints[joint];
    }

    return names;
}
}

int RUsd_GetImportSkeletonCount(RUsdContext* context, int* count)
{
    if (context == nullptr || count == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportSkeletonCount.");
        return kFailure;
    }

    *count = static_cast<int>(context->importedSkeletons.size());
    return kSuccess;
}

int RUsd_GetImportSkeleton(RUsdContext* context, int index, RUsdImportSkeleton* skeleton)
{
    if (context == nullptr || skeleton == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedSkeletons.size())
    {
        SetError(context, "Invalid arguments for RUsd_GetImportSkeleton.");
        return kFailure;
    }

    const RUsdContext::ImportedSkeleton& source = context->importedSkeletons[static_cast<size_t>(index)];
    skeleton->nodeIndex = source.nodeIndex;
    skeleton->jointCount = static_cast<int>(source.joints.size());
    skeleton->sampleCount = static_cast<int>(source.times.size());
    skeleton->jointNamesLength = static_cast<int>(JoinJointNames(source).size() + 1);
    return kSuccess;
}

int RUsd_CopyImportSkeletonJoints(
    RUsdContext* context,
    int index,
    char* jointNames,
    int jointNamesCapacity,
    int* parents,
    float* bindTransforms,
    float* restTransforms,
    float* poseTransforms,
    int jointCapacity)
{
    if (context == nullptr || jointNames == nullptr || parents == nullptr || bindTransforms == nullptr ||
        restTransforms == nullptr || poseTransforms == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedSkeletons.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportSkeletonJoints.");
        return kFailure;
    }

    const RUsdContext::ImportedSkeleton& source = context->importedSkeletons[static_cast<size_t>(index)];
    const std::string names = JoinJointNames(source);
    if (!FitsCapacity(jointNamesCapacity, names.size() + 1) || !FitsCapacity(jointCapacity, source.joints.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportSkeletonJoints.");
        return kFailure;
    }

    std::memcpy(jointNames, names.c_str(), names.size() + 1);
    std::copy(source.parents.begin(), source.parents.end(), parents);
    std::copy(source.bindTransforms.begin(), source.bindTransforms.end(), bindTransforms);
    std::copy(source.restTransforms.begin(), source.restTransforms.end(), restTransforms);
    std::copy(source.poseTransforms.begin(), source.poseTransforms.end(), poseTransforms);
    return kSuccess;
}

int RUsd_CopyImportSkeletonAnimation(
    RUsdContext* context,
    int index,
    double* times,
    int timeCapacity,
    float* transforms,
    int transformCapacity)
{
    if (context == nullptr || times == nullptr || transforms == nullptr || index < 0 ||
        static_cast<size_t>(index) >= context->importedSkeletons.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportSkeletonAnimation.");
        return kFailure;
    }

    const RUsdContext::ImportedSkeleton& source = context->importedSkeletons[static_cast<size_t>(index)];
    if (!FitsCapacity(timeCapacity, source.times.size()) || !FitsCapacity(transformCapacity, source.samples.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportSkeletonAnimation.");
        return kFailure;
    }

    std::copy(source.times.begin(), source.times.end(), times);
    std::copy(source.samples.begin(), source.samples.end(), transforms);
    return kSuccess;
}

namespace
{
const RUsdContext::ImportedMesh* FindImportMesh(RUsdContext* context, int meshIndex)
{
    if (context == nullptr || meshIndex < 0 || static_cast<size_t>(meshIndex) >= context->importedMeshes.size())
    {
        return nullptr;
    }

    return &context->importedMeshes[static_cast<size_t>(meshIndex)];
}
}

int RUsd_GetImportMeshSkin(RUsdContext* context, int meshIndex, RUsdImportMeshSkin* skin)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || skin == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportMeshSkin.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::Skin& source = mesh->skin;
    skin->skeletonIndex = source.skeletonIndex;
    skin->influencesPerVertex = source.influences;
    skin->blendShapeCount = static_cast<int>(source.blendShapes.size());
    skin->blendShapeSampleCount = static_cast<int>(source.blendShapeWeightTimes.size());
    skin->geomBindTransform = source.geomBindTransform;
    skin->dualQuaternion = source.dualQuaternion;
    return kSuccess;
}

int RUsd_CopyImportMeshSkinWeights(
    RUsdContext* context,
    int meshIndex,
    int* jointIndices,
    float* jointWeights,
    int capacity)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || jointIndices == nullptr || jointWeights == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportMeshSkinWeights.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::Skin& source = mesh->skin;
    if (!FitsCapacity(capacity, source.jointIndices.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportMeshSkinWeights.");
        return kFailure;
    }

    std::copy(source.jointIndices.begin(), source.jointIndices.end(), jointIndices);
    std::copy(source.jointWeights.begin(), source.jointWeights.end(), jointWeights);
    return kSuccess;
}

int RUsd_GetImportBlendShape(
    RUsdContext* context,
    int meshIndex,
    int shapeIndex,
    char* nameBuffer,
    int nameBufferCapacity,
    int* frameCount,
    int* hasNormals)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || frameCount == nullptr || hasNormals == nullptr || shapeIndex < 0 ||
        static_cast<size_t>(shapeIndex) >= mesh->skin.blendShapes.size())
    {
        SetError(context, "Invalid arguments for RUsd_GetImportBlendShape.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::BlendShape& shape = mesh->skin.blendShapes[static_cast<size_t>(shapeIndex)];
    if (nameBuffer != nullptr)
    {
        CopyStringToBuffer(shape.name, nameBuffer, nameBufferCapacity);
    }

    *frameCount = static_cast<int>(shape.frames.size());
    *hasNormals = !shape.frames.empty() && !shape.frames.front().deltaNormals.empty() ? 1 : 0;
    return kSuccess;
}

int RUsd_CopyImportBlendShapeFrame(
    RUsdContext* context,
    int meshIndex,
    int shapeIndex,
    int frame,
    float* weight,
    RUsdVec3* deltaVertices,
    RUsdVec3* deltaNormals,
    int vertexCapacity)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || weight == nullptr || deltaVertices == nullptr || shapeIndex < 0 ||
        static_cast<size_t>(shapeIndex) >= mesh->skin.blendShapes.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportBlendShapeFrame.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::BlendShape& shape = mesh->skin.blendShapes[static_cast<size_t>(shapeIndex)];
    if (frame < 0 || static_cast<size_t>(frame) >= shape.frames.size())
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportBlendShapeFrame.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::BlendShapeFrame& source = shape.frames[static_cast<size_t>(frame)];
    if (!FitsCapacity(vertexCapacity, source.deltaVertices.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportBlendShapeFrame.");
        return kFailure;
    }

    *weight = source.weight;
    std::copy(source.deltaVertices.begin(), source.deltaVertices.end(), deltaVertices);
    if (deltaNormals != nullptr && !source.deltaNormals.empty())
    {
        std::copy(source.deltaNormals.begin(), source.deltaNormals.end(), deltaNormals);
    }

    return kSuccess;
}

int RUsd_CopyImportMeshBlendShapeWeights(
    RUsdContext* context,
    int meshIndex,
    float* weights,
    int weightCapacity,
    double* times,
    int timeCapacity,
    float* samples,
    int sampleCapacity)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || weights == nullptr || times == nullptr || samples == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportMeshBlendShapeWeights.");
        return kFailure;
    }

    const RUsdContext::ImportedMesh::Skin& source = mesh->skin;
    if (!FitsCapacity(weightCapacity, source.blendShapeWeights.size()) ||
        !FitsCapacity(timeCapacity, source.blendShapeWeightTimes.size()) ||
        !FitsCapacity(sampleCapacity, source.blendShapeWeightSamples.size()))
    {
        SetError(context, "Buffer too small for RUsd_CopyImportMeshBlendShapeWeights.");
        return kFailure;
    }

    std::copy(source.blendShapeWeights.begin(), source.blendShapeWeights.end(), weights);
    std::copy(source.blendShapeWeightTimes.begin(), source.blendShapeWeightTimes.end(), times);
    std::copy(source.blendShapeWeightSamples.begin(), source.blendShapeWeightSamples.end(), samples);
    return kSuccess;
}

int RUsd_GetImportMeshPointCache(RUsdContext* context, int meshIndex, RUsdImportPointCache* pointCache)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || pointCache == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_GetImportMeshPointCache.");
        return kFailure;
    }

    pointCache->sampleCount = static_cast<int>(mesh->pointCacheTimes.size());
    pointCache->vertexCount = mesh->pointCacheTimes.empty() ? 0 : static_cast<int>(mesh->points.size());
    pointCache->hasNormals = mesh->pointCacheNormals.empty() ? 0 : 1;
    return kSuccess;
}

int RUsd_CopyImportMeshPointCache(
    RUsdContext* context,
    int meshIndex,
    double* times,
    int timeCapacity,
    RUsdVec3* points,
    RUsdVec3* normals,
    int valueCapacity)
{
    const RUsdContext::ImportedMesh* mesh = FindImportMesh(context, meshIndex);
    if (mesh == nullptr || times == nullptr || points == nullptr ||
        !FitsCapacity(timeCapacity, mesh->pointCacheTimes.size()) ||
        !FitsCapacity(valueCapacity, mesh->pointCachePoints.size()) ||
        (normals != nullptr && !FitsCapacity(valueCapacity, mesh->pointCacheNormals.size())))
    {
        SetError(context, "Invalid arguments for RUsd_CopyImportMeshPointCache.");
        return kFailure;
    }

    std::copy(mesh->pointCacheTimes.begin(), mesh->pointCacheTimes.end(), times);
    std::copy(mesh->pointCachePoints.begin(), mesh->pointCachePoints.end(), points);
    if (normals != nullptr)
    {
        std::copy(mesh->pointCacheNormals.begin(), mesh->pointCacheNormals.end(), normals);
    }

    return kSuccess;
}

// ---- Animation and skinning on export (API v13) -------------------------------------------

namespace
{
bool ExportPrim(RUsdContext* context, const char* primPath, const char* what, UsdPrim* prim)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD export context is invalid.");
        return false;
    }

    const SdfPath path(SafeString(primPath));
    if (!path.IsAbsolutePath() || !path.IsPrimPath())
    {
        SetError(context, std::string(what) + ": the prim path must be absolute.");
        return false;
    }

    *prim = context->stage->GetPrimAtPath(path);
    if (!*prim)
    {
        SetError(context, std::string(what) + ": no prim at " + path.GetString() + ".");
        return false;
    }

    return true;
}

GfMatrix4d MatrixFromFloats(const float* values)
{
    GfMatrix4d matrix;
    for (int i = 0; i < 16; ++i)
    {
        matrix[i / 4][i % 4] = values[i];
    }

    return matrix;
}

VtTokenArray TokensFromLines(const std::string& text)
{
    VtTokenArray tokens;
    std::string line;
    std::istringstream stream(text);
    while (std::getline(stream, line))
    {
        if (!line.empty())
        {
            tokens.push_back(TfToken(line));
        }
    }

    return tokens;
}

bool SamplesValid(RUsdContext* context, const char* what, const void* times, const void* values, int count)
{
    if (count < 0 || static_cast<size_t>(count) > kMaxImportTrackSamples || (count > 0 && (times == nullptr || values == nullptr)))
    {
        SetError(context, std::string("Invalid samples for ") + what + ".");
        return false;
    }

    return true;
}
}

#define RUSD_EXPORT_GUARD(what)                                                     \
    catch (const std::exception& exception)                                         \
    {                                                                               \
        SetError(context, MakeError(what, exception));                              \
        return kFailure;                                                            \
    }                                                                               \
    catch (...)                                                                     \
    {                                                                               \
        SetError(context, std::string(what) + ".");                                 \
        return kFailure;                                                            \
    }

int RUsd_SetExportTimeRange(RUsdContext* context, double startTimeCode, double endTimeCode,
                            double timeCodesPerSecond, double framesPerSecond)
{
    if (context == nullptr || !context->stage || !(endTimeCode >= startTimeCode) ||
        !(timeCodesPerSecond > 0.0) || !(framesPerSecond > 0.0))
    {
        SetError(context, "Invalid arguments for RUsd_SetExportTimeRange.");
        return kFailure;
    }

    try
    {
        context->stage->SetStartTimeCode(startTimeCode);
        context->stage->SetEndTimeCode(endTimeCode);
        context->stage->SetTimeCodesPerSecond(timeCodesPerSecond);
        context->stage->SetFramesPerSecond(framesPerSecond);
        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to set the stage's time range")
}

int RUsd_SetXformSamples(RUsdContext* context, const char* primPath, const double* times,
                         const float* matrices, int sampleCount)
{
    UsdPrim prim;
    if (!ExportPrim(context, primPath, "RUsd_SetXformSamples", &prim) ||
        !SamplesValid(context, "RUsd_SetXformSamples", times, matrices, sampleCount))
    {
        return kFailure;
    }

    try
    {
        UsdGeomXformable xformable(prim);
        bool resets = false;
        std::vector<UsdGeomXformOp> ops = xformable.GetOrderedXformOps(&resets);
        UsdGeomXformOp op = ops.size() == 1 && ops[0].GetOpType() == UsdGeomXformOp::TypeTransform
            ? ops[0]
            : xformable.MakeMatrixXform();
        for (int i = 0; i < sampleCount; ++i)
        {
            op.Set(MatrixFromFloats(matrices + 16 * static_cast<size_t>(i)), UsdTimeCode(times[i]));
        }

        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to write transform samples")
}

int RUsd_SetVisibilitySamples(RUsdContext* context, const char* primPath, const double* times,
                              const int* visible, int sampleCount)
{
    UsdPrim prim;
    if (!ExportPrim(context, primPath, "RUsd_SetVisibilitySamples", &prim) ||
        !SamplesValid(context, "RUsd_SetVisibilitySamples", times, visible, sampleCount))
    {
        return kFailure;
    }

    try
    {
        UsdAttribute attribute = UsdGeomImageable(prim).CreateVisibilityAttr();
        for (int i = 0; i < sampleCount; ++i)
        {
            attribute.Set(visible[i] != 0 ? UsdGeomTokens->inherited : UsdGeomTokens->invisible,
                          UsdTimeCode(times[i]));
        }

        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to write visibility samples")
}

int RUsd_SetPointsSamples(RUsdContext* context, const char* meshPath, const double* times,
                          const RUsdVec3* points, const RUsdVec3* normals, int pointCount, int sampleCount)
{
    UsdPrim prim;
    if (!ExportPrim(context, meshPath, "RUsd_SetPointsSamples", &prim) ||
        !SamplesValid(context, "RUsd_SetPointsSamples", times, points, sampleCount))
    {
        return kFailure;
    }

    if (!prim.IsA<UsdGeomMesh>() || pointCount <= 0 ||
        static_cast<size_t>(pointCount) * static_cast<size_t>(sampleCount) > kMaxImportGeometryElements)
    {
        SetError(context, "Invalid arguments for RUsd_SetPointsSamples.");
        return kFailure;
    }

    try
    {
        UsdGeomMesh mesh(prim);
        UsdAttribute pointsAttr = mesh.CreatePointsAttr();
        UsdAttribute extentAttr = mesh.CreateExtentAttr();
        UsdAttribute normalsAttr = normals != nullptr ? mesh.CreateNormalsAttr() : UsdAttribute();
        for (int i = 0; i < sampleCount; ++i)
        {
            const size_t offset = static_cast<size_t>(i) * static_cast<size_t>(pointCount);
            const UsdTimeCode time(times[i]);
            pointsAttr.Set(CopyPoints(points + offset, pointCount), time);
            extentAttr.Set(ComputeExtent(points + offset, pointCount), time);
            if (normalsAttr)
            {
                normalsAttr.Set(CopyPoints(normals + offset, pointCount), time);
            }
        }

        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to write point samples")
}

int RUsd_SetSkinningMethod(RUsdContext* context, const char* meshPath, int dualQuaternion)
{
    UsdPrim prim;
    if (!ExportPrim(context, meshPath, "RUsd_SetSkinningMethod", &prim))
    {
        return kFailure;
    }

    try
    {
        UsdSkelBindingAPI::Apply(prim).CreateSkinningMethodAttr().Set(
            dualQuaternion != 0 ? UsdSkelTokens->dualQuaternion : UsdSkelTokens->classicLinear);
        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to set the skinning method")
}

int RUsd_DefineSkelRoot(RUsdContext* context, const char* primPath)
{
    UsdPrim prim;
    if (!ExportPrim(context, primPath, "RUsd_DefineSkelRoot", &prim))
    {
        return kFailure;
    }

    try
    {
        UsdSkelRoot::Define(context->stage, prim.GetPath());
        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to define a SkelRoot")
}

int RUsd_AddSkeleton(RUsdContext* context, const char* skeletonPath, const char* jointPaths, int jointCount,
                     const float* bindTransforms, const float* restTransforms)
{
    if (context == nullptr || !context->stage || jointCount <= 0 ||
        static_cast<size_t>(jointCount) > kMaxImportJoints || bindTransforms == nullptr || restTransforms == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_AddSkeleton.");
        return kFailure;
    }

    try
    {
        const SdfPath path(SafeString(skeletonPath));
        const VtTokenArray joints = TokensFromLines(SafeString(jointPaths));
        if (!path.IsAbsolutePath() || !path.IsPrimPath() || joints.size() != static_cast<size_t>(jointCount))
        {
            SetError(context, "RUsd_AddSkeleton: the path must be absolute and name one joint path per joint.");
            return kFailure;
        }

        EnsureParentXforms(context->stage, path);
        UsdSkelSkeleton skeleton = UsdSkelSkeleton::Define(context->stage, path);
        VtMatrix4dArray bind(joints.size());
        VtMatrix4dArray rest(joints.size());
        for (size_t joint = 0; joint < joints.size(); ++joint)
        {
            bind[joint] = MatrixFromFloats(bindTransforms + 16 * joint);
            rest[joint] = MatrixFromFloats(restTransforms + 16 * joint);
        }

        skeleton.CreateJointsAttr().Set(joints);
        skeleton.CreateBindTransformsAttr().Set(bind);
        skeleton.CreateRestTransformsAttr().Set(rest);
        UsdSkelBindingAPI::Apply(skeleton.GetPrim());
        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to add a skeleton")
}

int RUsd_BindSkinnedMesh(RUsdContext* context, const char* meshPath, const char* skeletonPath,
                         const int* jointIndices, const float* jointWeights, int pointCount, int influences,
                         const RUsdMatrix4x4* geomBindTransform)
{
    UsdPrim prim;
    if (!ExportPrim(context, meshPath, "RUsd_BindSkinnedMesh", &prim))
    {
        return kFailure;
    }

    if (pointCount <= 0 || influences <= 0 || influences > kMaxImportInfluences ||
        jointIndices == nullptr || jointWeights == nullptr)
    {
        SetError(context, "Invalid arguments for RUsd_BindSkinnedMesh.");
        return kFailure;
    }

    try
    {
        const size_t count = static_cast<size_t>(pointCount) * static_cast<size_t>(influences);
        VtIntArray indices(jointIndices, jointIndices + count);
        VtFloatArray weights(jointWeights, jointWeights + count);
        UsdSkelBindingAPI binding = UsdSkelBindingAPI::Apply(prim);
        binding.CreateSkeletonRel().SetTargets({SdfPath(SafeString(skeletonPath))});
        binding.CreateJointIndicesPrimvar(false, influences).Set(indices);
        binding.CreateJointWeightsPrimvar(false, influences).Set(weights);
        if (geomBindTransform != nullptr)
        {
            binding.CreateGeomBindTransformAttr().Set(ToGfMatrix(*geomBindTransform));
        }

        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to bind a skinned mesh")
}

int RUsd_AddBlendShapeFrame(RUsdContext* context, const char* meshPath, const char* shapeName, float weight,
                            const int* pointIndices, int pointCount, const RUsdVec3* offsets,
                            const RUsdVec3* normalOffsets)
{
    UsdPrim prim;
    if (!ExportPrim(context, meshPath, "RUsd_AddBlendShapeFrame", &prim))
    {
        return kFailure;
    }

    if (pointCount < 0 || (pointCount > 0 && (pointIndices == nullptr || offsets == nullptr)) ||
        !(weight > 0.0f && weight <= 1.0f))
    {
        SetError(context, "Invalid arguments for RUsd_AddBlendShapeFrame.");
        return kFailure;
    }

    try
    {
        const TfToken name(TfMakeValidIdentifier(SafeString(shapeName)));
        const SdfPath shapePath = prim.GetPath().AppendChild(name);
        UsdSkelBlendShape shape = UsdSkelBlendShape::Define(context->stage, shapePath);
        VtVec3fArray points(static_cast<size_t>(pointCount));
        VtVec3fArray normals(normalOffsets != nullptr ? static_cast<size_t>(pointCount) : 0);
        for (int i = 0; i < pointCount; ++i)
        {
            points[static_cast<size_t>(i)] = GfVec3f(offsets[i].x, offsets[i].y, offsets[i].z);
            if (normalOffsets != nullptr)
            {
                normals[static_cast<size_t>(i)] = GfVec3f(normalOffsets[i].x, normalOffsets[i].y, normalOffsets[i].z);
            }
        }

        shape.CreatePointIndicesAttr().Set(VtIntArray(pointIndices, pointIndices + pointCount));
        if (weight >= 1.0f)
        {
            shape.CreateOffsetsAttr().Set(points);
            if (normalOffsets != nullptr)
            {
                shape.CreateNormalOffsetsAttr().Set(normals);
            }
        }
        else
        {
            const TfToken inbetweenName("w" + std::to_string(static_cast<int>(std::lround(weight * 1000.0f))));
            UsdSkelInbetweenShape inbetween = shape.CreateInbetween(inbetweenName);
            inbetween.SetWeight(weight);
            inbetween.SetOffsets(points);
            if (normalOffsets != nullptr)
            {
                inbetween.SetNormalOffsets(normals);
            }
        }

        UsdSkelBindingAPI binding = UsdSkelBindingAPI::Apply(prim);
        VtTokenArray names;
        binding.GetBlendShapesAttr().Get(&names);
        if (std::find(names.begin(), names.end(), name) == names.end())
        {
            names.push_back(name);
            binding.CreateBlendShapesAttr().Set(names);
            binding.CreateBlendShapeTargetsRel().AddTarget(shapePath);
        }

        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to add a blend shape")
}

int RUsd_SetSkeletonAnimation(RUsdContext* context, const char* skeletonPath, const char* blendShapeNames,
                              int shapeCount, const double* times, int sampleCount, const float* jointTransforms,
                              const float* blendShapeWeights)
{
    UsdPrim prim;
    if (!ExportPrim(context, skeletonPath, "RUsd_SetSkeletonAnimation", &prim))
    {
        return kFailure;
    }

    const int samples = times == nullptr ? 1 : sampleCount;
    if (samples < 1 || static_cast<size_t>(samples) > kMaxImportTrackSamples || shapeCount < 0 ||
        jointTransforms == nullptr || (shapeCount > 0 && blendShapeWeights == nullptr))
    {
        SetError(context, "Invalid arguments for RUsd_SetSkeletonAnimation.");
        return kFailure;
    }

    try
    {
        UsdSkelSkeleton skeleton(prim);
        VtTokenArray joints;
        skeleton.GetJointsAttr().Get(&joints);
        const VtTokenArray shapes = TokensFromLines(SafeString(blendShapeNames));
        if (joints.empty() || shapes.size() != static_cast<size_t>(shapeCount))
        {
            SetError(context, "RUsd_SetSkeletonAnimation: the skeleton has no joints, or the blend-shape names do not match their count.");
            return kFailure;
        }

        const SdfPath animationPath = prim.GetPath().AppendChild(TfToken("Animation"));
        UsdSkelAnimation animation = UsdSkelAnimation::Define(context->stage, animationPath);
        animation.CreateJointsAttr().Set(joints);
        if (shapeCount > 0)
        {
            animation.CreateBlendShapesAttr().Set(shapes);
        }

        for (int sample = 0; sample < samples; ++sample)
        {
            const UsdTimeCode time = times == nullptr ? UsdTimeCode::Default() : UsdTimeCode(times[sample]);
            VtMatrix4dArray local(joints.size());
            for (size_t joint = 0; joint < joints.size(); ++joint)
            {
                local[joint] = MatrixFromFloats(jointTransforms + 16 * (static_cast<size_t>(sample) * joints.size() + joint));
            }

            animation.SetTransforms(local, time);
            if (shapeCount > 0)
            {
                const float* weights = blendShapeWeights + static_cast<size_t>(sample) * static_cast<size_t>(shapeCount);
                animation.CreateBlendShapeWeightsAttr().Set(VtFloatArray(weights, weights + shapeCount), time);
            }
        }

        UsdSkelBindingAPI::Apply(prim).CreateAnimationSourceRel().SetTargets({animationPath});
        return kSuccess;
    }
    RUSD_EXPORT_GUARD("Failed to write a skeleton animation")
}

#undef RUSD_EXPORT_GUARD

// ---- Stage API (API v13) ------------------------------------------------------------------------
//
// A stage kept open for the host to read and edit. Every call takes the context's stage as it is
// now; reads compose through the Usd API and edits go to the stage's edit target. Values cross as
// the type the attribute holds, checked against the held value's registered type name (never the
// attribute's declared type: a stage can author any value under any declaration).

namespace
{

template <typename TBody>
int RunStageCall(RUsdContext* context, const char* what, TBody body)
{
    if (context == nullptr || !context->stage)
    {
        SetError(context, "USD stage context is invalid.");
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

// An absolute prim path, or "/" (the pseudo-root) when `allowRoot`.
bool ParseStagePath(RUsdContext* context, const char* primPath, bool allowRoot, SdfPath* out)
{
    const std::string text = SafeString(primPath);
    if (allowRoot && text == "/")
    {
        *out = SdfPath::AbsoluteRootPath();
        return true;
    }

    return ParseAbsolutePrimPath(context, primPath, out);
}

// The composed prim at `primPath` ("/" the pseudo-root when `allowRoot`); an error when there is none.
bool FindStagePrim(RUsdContext* context, const char* primPath, bool allowRoot, UsdPrim* out)
{
    SdfPath path;
    if (!ParseStagePath(context, primPath, allowRoot, &path))
    {
        return false;
    }

    *out = context->stage->GetPrimAtPath(path);
    if (!*out)
    {
        SetError(context, "The stage has no prim at " + path.GetString() + ".");
        return false;
    }

    return true;
}

bool ParsePropertyName(RUsdContext* context, const char* name, TfToken* out)
{
    const std::string text = SafeString(name);
    if (!SdfPath::IsValidNamespacedIdentifier(text))
    {
        SetError(context, "'" + text + "' is not a valid property name.");
        return false;
    }

    *out = TfToken(text);
    return true;
}

// Edits through the Usd API cannot reach below an instance, and a layer whose format OpenUSD can
// write but not edit (usdAbc's) refuses an edit by aborting, so both are turned away first.
bool CheckEditable(RUsdContext* context, const SdfPath& path)
{
    const SdfLayerHandle layer = context->stage->GetEditTarget().GetLayer();
    const SdfFileFormatConstPtr format = layer ? layer->GetFileFormat() : SdfFileFormatConstPtr();
    if (!layer || (format && !format->SupportsEditing()))
    {
        SetError(context, "The edit target layer " + (layer ? "'" + layer->GetIdentifier() + "' " : std::string()) +
            "cannot be edited; set the edit target to another layer (the session layer), or export the stage.");
        return false;
    }

    for (SdfPath at = path; !at.IsEmpty() && at != SdfPath::AbsoluteRootPath(); at = at.GetParentPath())
    {
        const UsdPrim prim = context->stage->GetPrimAtPath(at);
        if (!prim)
        {
            continue;
        }

        if (prim.IsInstanceProxy())
        {
            SetError(context, path.GetString() + " is below an instance, which is edited through its prototype "
                "only; clear the instance's instanceable first.");
            return false;
        }

        break;
    }

    return true;
}

bool ParseTime(RUsdContext* context, int timeKind, double time, bool writing, UsdTimeCode* out)
{
    switch (timeKind)
    {
        case RUSD_TIME_DEFAULT:
            *out = UsdTimeCode::Default();
            return true;
        case RUSD_TIME_CODE:
            if (!std::isfinite(time))
            {
                SetError(context, "The time code is not a finite number.");
                return false;
            }

            *out = UsdTimeCode(time);
            return true;
        case RUSD_TIME_EARLIEST:
            if (!writing)
            {
                *out = UsdTimeCode::EarliestTime();
                return true;
            }

            break;
        default:
            break;
    }

    SetError(context, "Unknown time kind " + std::to_string(timeKind) + ".");
    return false;
}

// The text an RUsd_* query returns, the RUsd_GetImportComposition way: a size query, then a copy.
int ReturnText(RUsdContext* context, const std::string& text, char* buffer, int bufferCapacity, int* requiredLength)
{
    if (text.size() >= static_cast<size_t>(std::numeric_limits<int>::max()))
    {
        SetError(context, "The text is too long to return.");
        return kFailure;
    }

    const int required = static_cast<int>(text.size()) + 1;
    if (requiredLength != nullptr)
    {
        *requiredLength = required;
    }

    if (buffer == nullptr)
    {
        return kSuccess;
    }

    if (bufferCapacity < required)
    {
        SetError(context, "The text buffer is too small.");
        return kFailure;
    }

    return CopyStringToBuffer(text, buffer, bufferCapacity);
}

// ---- Values -------------------------------------------------------------------------------------

// The value type an RUSD_VALUE_* code names (an array type with RUSD_VALUE_ARRAY); invalid for an
// unknown code.
SdfValueTypeName PlainTypeName(int valueType)
{
    const bool array = (valueType & RUSD_VALUE_ARRAY) != 0;
    switch (valueType & ~(RUSD_VALUE_ARRAY | RUSD_VALUE_RESOLVED))
    {
        case RUSD_VALUE_BOOL: return array ? SdfValueTypeNames->BoolArray : SdfValueTypeNames->Bool;
        case RUSD_VALUE_UCHAR: return array ? SdfValueTypeNames->UCharArray : SdfValueTypeNames->UChar;
        case RUSD_VALUE_INT: return array ? SdfValueTypeNames->IntArray : SdfValueTypeNames->Int;
        case RUSD_VALUE_UINT: return array ? SdfValueTypeNames->UIntArray : SdfValueTypeNames->UInt;
        case RUSD_VALUE_INT64: return array ? SdfValueTypeNames->Int64Array : SdfValueTypeNames->Int64;
        case RUSD_VALUE_UINT64: return array ? SdfValueTypeNames->UInt64Array : SdfValueTypeNames->UInt64;
        case RUSD_VALUE_HALF: return array ? SdfValueTypeNames->HalfArray : SdfValueTypeNames->Half;
        case RUSD_VALUE_FLOAT: return array ? SdfValueTypeNames->FloatArray : SdfValueTypeNames->Float;
        case RUSD_VALUE_DOUBLE: return array ? SdfValueTypeNames->DoubleArray : SdfValueTypeNames->Double;
        case RUSD_VALUE_TIMECODE: return array ? SdfValueTypeNames->TimeCodeArray : SdfValueTypeNames->TimeCode;
        case RUSD_VALUE_STRING: return array ? SdfValueTypeNames->StringArray : SdfValueTypeNames->String;
        case RUSD_VALUE_TOKEN: return array ? SdfValueTypeNames->TokenArray : SdfValueTypeNames->Token;
        case RUSD_VALUE_ASSET: return array ? SdfValueTypeNames->AssetArray : SdfValueTypeNames->Asset;
        case RUSD_VALUE_INT2: return array ? SdfValueTypeNames->Int2Array : SdfValueTypeNames->Int2;
        case RUSD_VALUE_INT3: return array ? SdfValueTypeNames->Int3Array : SdfValueTypeNames->Int3;
        case RUSD_VALUE_INT4: return array ? SdfValueTypeNames->Int4Array : SdfValueTypeNames->Int4;
        case RUSD_VALUE_HALF2: return array ? SdfValueTypeNames->Half2Array : SdfValueTypeNames->Half2;
        case RUSD_VALUE_HALF3: return array ? SdfValueTypeNames->Half3Array : SdfValueTypeNames->Half3;
        case RUSD_VALUE_HALF4: return array ? SdfValueTypeNames->Half4Array : SdfValueTypeNames->Half4;
        case RUSD_VALUE_FLOAT2: return array ? SdfValueTypeNames->Float2Array : SdfValueTypeNames->Float2;
        case RUSD_VALUE_FLOAT3: return array ? SdfValueTypeNames->Float3Array : SdfValueTypeNames->Float3;
        case RUSD_VALUE_FLOAT4: return array ? SdfValueTypeNames->Float4Array : SdfValueTypeNames->Float4;
        case RUSD_VALUE_DOUBLE2: return array ? SdfValueTypeNames->Double2Array : SdfValueTypeNames->Double2;
        case RUSD_VALUE_DOUBLE3: return array ? SdfValueTypeNames->Double3Array : SdfValueTypeNames->Double3;
        case RUSD_VALUE_DOUBLE4: return array ? SdfValueTypeNames->Double4Array : SdfValueTypeNames->Double4;
        case RUSD_VALUE_QUATH: return array ? SdfValueTypeNames->QuathArray : SdfValueTypeNames->Quath;
        case RUSD_VALUE_QUATF: return array ? SdfValueTypeNames->QuatfArray : SdfValueTypeNames->Quatf;
        case RUSD_VALUE_QUATD: return array ? SdfValueTypeNames->QuatdArray : SdfValueTypeNames->Quatd;
        case RUSD_VALUE_MATRIX2D: return array ? SdfValueTypeNames->Matrix2dArray : SdfValueTypeNames->Matrix2d;
        case RUSD_VALUE_MATRIX3D: return array ? SdfValueTypeNames->Matrix3dArray : SdfValueTypeNames->Matrix3d;
        case RUSD_VALUE_MATRIX4D: return array ? SdfValueTypeNames->Matrix4dArray : SdfValueTypeNames->Matrix4d;
        default: return SdfValueTypeName();
    }
}

// The RUSD_VALUE_* code for a declared type, roles ignored (point3f[] is float3[]); 0 when none.
int ValueTypeOf(const SdfValueTypeName& typeName)
{
    if (!typeName)
    {
        return 0;
    }

    const TfType type = typeName.GetType();
    for (int code = RUSD_VALUE_BOOL; code <= RUSD_VALUE_MATRIX4D; ++code)
    {
        if (PlainTypeName(code).GetType() == type)
        {
            return code;
        }

        if (PlainTypeName(code | RUSD_VALUE_ARRAY).GetType() == type)
        {
            return code | RUSD_VALUE_ARRAY;
        }
    }

    return 0;
}

// How one element crosses: N components of type C.
template <typename T, typename TComponent, int TCount>
struct RawForm
{
    using C = TComponent;
    static constexpr int N = TCount;
    static_assert(sizeof(T) == sizeof(C) * N, "the type must be its components, packed");
    static void Put(const T& value, C* out) { std::memcpy(out, &value, sizeof(T)); }
    static T Take(const C* in)
    {
        T value;
        std::memcpy(&value, in, sizeof(T));
        return value;
    }
};

struct BoolForm
{
    using C = unsigned char;
    static constexpr int N = 1;
    static void Put(const bool& value, C* out) { *out = value ? 1 : 0; }
    static bool Take(const C* in) { return *in != 0; }
};

struct TimeCodeForm
{
    using C = double;
    static constexpr int N = 1;
    static void Put(const SdfTimeCode& value, C* out) { *out = value.GetValue(); }
    static SdfTimeCode Take(const C* in) { return SdfTimeCode(*in); }
};

struct HalfForm
{
    using C = float;
    static constexpr int N = 1;
    static void Put(const GfHalf& value, C* out) { *out = static_cast<float>(value); }
    static GfHalf Take(const C* in) { return GfHalf(*in); }
};

template <typename T, int TCount>
struct HalfVecForm
{
    using C = float;
    static constexpr int N = TCount;
    static void Put(const T& value, C* out)
    {
        for (int i = 0; i < N; ++i)
        {
            out[i] = static_cast<float>(value[i]);
        }
    }
    static T Take(const C* in)
    {
        T value;
        for (int i = 0; i < N; ++i)
        {
            value[i] = GfHalf(in[i]);
        }

        return value;
    }
};

// x, y, z, w: the imaginary part first, as Unity and most hosts keep a quaternion.
template <typename T, typename TComponent>
struct QuatForm
{
    using C = TComponent;
    static constexpr int N = 4;
    static void Put(const T& value, C* out)
    {
        const auto& imaginary = value.GetImaginary();
        out[0] = static_cast<C>(imaginary[0]);
        out[1] = static_cast<C>(imaginary[1]);
        out[2] = static_cast<C>(imaginary[2]);
        out[3] = static_cast<C>(value.GetReal());
    }
    static T Take(const C* in)
    {
        using Real = typename T::ScalarType;
        using Imaginary = typename T::ImaginaryType;
        return T(Real(in[3]), Imaginary(Real(in[0]), Real(in[1]), Real(in[2])));
    }
};

using FormUChar = RawForm<unsigned char, unsigned char, 1>;
using FormInt = RawForm<int, int, 1>;
using FormUInt = RawForm<unsigned int, unsigned int, 1>;
using FormInt64 = RawForm<int64_t, int64_t, 1>;
using FormUInt64 = RawForm<uint64_t, uint64_t, 1>;
using FormFloat = RawForm<float, float, 1>;
using FormDouble = RawForm<double, double, 1>;
using FormInt2 = RawForm<GfVec2i, int, 2>;
using FormInt3 = RawForm<GfVec3i, int, 3>;
using FormInt4 = RawForm<GfVec4i, int, 4>;
using FormHalf2 = HalfVecForm<GfVec2h, 2>;
using FormHalf3 = HalfVecForm<GfVec3h, 3>;
using FormHalf4 = HalfVecForm<GfVec4h, 4>;
using FormFloat2 = RawForm<GfVec2f, float, 2>;
using FormFloat3 = RawForm<GfVec3f, float, 3>;
using FormFloat4 = RawForm<GfVec4f, float, 4>;
using FormDouble2 = RawForm<GfVec2d, double, 2>;
using FormDouble3 = RawForm<GfVec3d, double, 3>;
using FormDouble4 = RawForm<GfVec4d, double, 4>;
using FormQuath = QuatForm<GfQuath, float>;
using FormQuatf = QuatForm<GfQuatf, float>;
using FormQuatd = QuatForm<GfQuatd, double>;
using FormMatrix2d = RawForm<GfMatrix2d, double, 4>;
using FormMatrix3d = RawForm<GfMatrix3d, double, 9>;
using FormMatrix4d = RawForm<GfMatrix4d, double, 16>;

// code, held type, form
#define RUSD_NUMERIC_FORMS(X) \
    X(RUSD_VALUE_BOOL, bool, BoolForm) \
    X(RUSD_VALUE_UCHAR, unsigned char, FormUChar) \
    X(RUSD_VALUE_INT, int, FormInt) \
    X(RUSD_VALUE_UINT, unsigned int, FormUInt) \
    X(RUSD_VALUE_INT64, int64_t, FormInt64) \
    X(RUSD_VALUE_UINT64, uint64_t, FormUInt64) \
    X(RUSD_VALUE_HALF, GfHalf, HalfForm) \
    X(RUSD_VALUE_FLOAT, float, FormFloat) \
    X(RUSD_VALUE_DOUBLE, double, FormDouble) \
    X(RUSD_VALUE_TIMECODE, SdfTimeCode, TimeCodeForm) \
    X(RUSD_VALUE_INT2, GfVec2i, FormInt2) \
    X(RUSD_VALUE_INT3, GfVec3i, FormInt3) \
    X(RUSD_VALUE_INT4, GfVec4i, FormInt4) \
    X(RUSD_VALUE_HALF2, GfVec2h, FormHalf2) \
    X(RUSD_VALUE_HALF3, GfVec3h, FormHalf3) \
    X(RUSD_VALUE_HALF4, GfVec4h, FormHalf4) \
    X(RUSD_VALUE_FLOAT2, GfVec2f, FormFloat2) \
    X(RUSD_VALUE_FLOAT3, GfVec3f, FormFloat3) \
    X(RUSD_VALUE_FLOAT4, GfVec4f, FormFloat4) \
    X(RUSD_VALUE_DOUBLE2, GfVec2d, FormDouble2) \
    X(RUSD_VALUE_DOUBLE3, GfVec3d, FormDouble3) \
    X(RUSD_VALUE_DOUBLE4, GfVec4d, FormDouble4) \
    X(RUSD_VALUE_QUATH, GfQuath, FormQuath) \
    X(RUSD_VALUE_QUATF, GfQuatf, FormQuatf) \
    X(RUSD_VALUE_QUATD, GfQuatd, FormQuatd) \
    X(RUSD_VALUE_MATRIX2D, GfMatrix2d, FormMatrix2d) \
    X(RUSD_VALUE_MATRIX3D, GfMatrix3d, FormMatrix3d) \
    X(RUSD_VALUE_MATRIX4D, GfMatrix4d, FormMatrix4d)

// Copies a held value of type T (or VtArray<T>) out through Form. With `out` null only the size.
template <typename T, typename Form>
long long PutValue(const VtValue& value, bool array, void* out, long long* count)
{
    constexpr long long element = static_cast<long long>(sizeof(typename Form::C)) * Form::N;
    typename Form::C* to = static_cast<typename Form::C*>(out);
    if (array)
    {
        const VtArray<T>& items = value.UncheckedGet<VtArray<T>>();
        *count = static_cast<long long>(items.size());
        if (to != nullptr)
        {
            for (const T& item : items)
            {
                Form::Put(item, to);
                to += Form::N;
            }
        }

        return element * static_cast<long long>(items.size());
    }

    *count = 1;
    if (to != nullptr)
    {
        Form::Put(value.UncheckedGet<T>(), to);
    }

    return element;
}

// Writes data crossing in through Form as T (or VtArray<T>) at `at`. Typed, not through a VtValue:
// a VtValue built in this plugin can fail OpenUSD's own type check (see VtValueHoldsType), while
// UsdAttribute::Set<T> is what the export side writes everything with.
template <typename T, typename Form>
bool SetTyped(const UsdAttribute& attribute, UsdTimeCode at, const void* data, bool array, long long count,
              long long bytes, std::string* error)
{
    constexpr long long element = static_cast<long long>(sizeof(typename Form::C)) * Form::N;
    if (!array && count != 1)
    {
        *error = "a single value has an element count of 1";
        return false;
    }

    if (count < 0 || count > std::numeric_limits<long long>::max() / element || count * element != bytes ||
        (count > 0 && data == nullptr))
    {
        *error = "the data is not " + std::to_string(count) + " elements of " + std::to_string(element) + " bytes";
        return false;
    }

    const typename Form::C* from = static_cast<const typename Form::C*>(data);
    if (!array)
    {
        return attribute.Set(Form::Take(from), at);
    }

    VtArray<T> items(static_cast<size_t>(count));
    T* write = items.data();
    for (long long i = 0; i < count; ++i)
    {
        write[i] = Form::Take(from);
        from += Form::N;
    }

    return attribute.Set(items, at);
}

// A string as it crosses: cut at its first NUL, which would otherwise end it early on the far side.
std::string CrossingText(const std::string& text)
{
    return std::string(text.c_str());
}

std::string AssetText(const SdfAssetPath& asset, bool resolved)
{
    return CrossingText(resolved ? asset.GetResolvedPath() : asset.GetAssetPath());
}

long long PutStrings(const std::vector<std::string>& items, void* out)
{
    long long bytes = 0;
    char* to = static_cast<char*>(out);
    for (const std::string& item : items)
    {
        const size_t length = item.size() + 1;
        if (to != nullptr)
        {
            std::memcpy(to, item.c_str(), length);
            to += length;
        }

        bytes += static_cast<long long>(length);
    }

    return bytes;
}

// The texts of a string, token or asset value (one, or an array's), as they cross.
std::vector<std::string> TextsOf(const VtValue& value, int valueType)
{
    const bool array = (valueType & RUSD_VALUE_ARRAY) != 0;
    const bool resolved = (valueType & RUSD_VALUE_RESOLVED) != 0;
    std::vector<std::string> texts;
    switch (valueType & ~(RUSD_VALUE_ARRAY | RUSD_VALUE_RESOLVED))
    {
        case RUSD_VALUE_STRING:
            if (array)
            {
                for (const std::string& item : value.UncheckedGet<VtStringArray>())
                {
                    texts.push_back(CrossingText(item));
                }
            }
            else
            {
                texts.push_back(CrossingText(value.UncheckedGet<std::string>()));
            }

            break;
        case RUSD_VALUE_TOKEN:
            if (array)
            {
                for (const TfToken& item : value.UncheckedGet<VtTokenArray>())
                {
                    texts.push_back(CrossingText(item.GetString()));
                }
            }
            else
            {
                texts.push_back(CrossingText(value.UncheckedGet<TfToken>().GetString()));
            }

            break;
        case RUSD_VALUE_ASSET:
            if (array)
            {
                for (const SdfAssetPath& item : value.UncheckedGet<VtArray<SdfAssetPath>>())
                {
                    texts.push_back(AssetText(item, resolved));
                }
            }
            else
            {
                texts.push_back(AssetText(value.UncheckedGet<SdfAssetPath>(), resolved));
            }

            break;
        default:
            break;
    }

    return texts;
}

bool IsTextType(int valueType)
{
    const int plain = valueType & ~(RUSD_VALUE_ARRAY | RUSD_VALUE_RESOLVED);
    return plain == RUSD_VALUE_STRING || plain == RUSD_VALUE_TOKEN || plain == RUSD_VALUE_ASSET;
}

// Copies a value already checked to hold `valueType` out; with `out` null only the size.
long long PutAnyValue(const VtValue& value, int valueType, void* out, long long* count)
{
    if (IsTextType(valueType))
    {
        const std::vector<std::string> texts = TextsOf(value, valueType);
        *count = static_cast<long long>(texts.size());
        return PutStrings(texts, out);
    }

    const bool array = (valueType & RUSD_VALUE_ARRAY) != 0;
    switch (valueType & ~RUSD_VALUE_ARRAY)
    {
#define RUSD_PUT_CASE(code, T, Form) case code: return PutValue<T, Form>(value, array, out, count);
        RUSD_NUMERIC_FORMS(RUSD_PUT_CASE)
#undef RUSD_PUT_CASE
        default:
            *count = 0;
            return 0;
    }
}

// NUL-terminated strings back to back, exactly `count` of them.
bool SplitStrings(const void* data, long long count, long long bytes, std::vector<std::string>* out, std::string* error)
{
    if (count < 0 || bytes < 0 || (bytes > 0 && data == nullptr))
    {
        *error = "the string data is malformed";
        return false;
    }

    const char* from = static_cast<const char*>(data);
    const char* end = from + bytes;
    while (from < end)
    {
        const void* nul = std::memchr(from, '\0', static_cast<size_t>(end - from));
        if (nul == nullptr)
        {
            *error = "the last string is not NUL-terminated";
            return false;
        }

        const char* stop = static_cast<const char*>(nul);
        out->emplace_back(from, stop);
        from = stop + 1;
    }

    if (static_cast<long long>(out->size()) != count)
    {
        *error = "the data holds " + std::to_string(out->size()) + " strings, not " + std::to_string(count);
        return false;
    }

    return true;
}

// Writes a value crossing in as `valueType`; false with `error` set when the data is malformed, and
// false with `error` empty when OpenUSD refuses it.
bool SetAnyValue(const UsdAttribute& attribute, UsdTimeCode at, int valueType, const void* data, long long count,
                 long long bytes, std::string* error)
{
    const bool array = (valueType & RUSD_VALUE_ARRAY) != 0;
    const int plain = valueType & ~RUSD_VALUE_ARRAY;
    if (IsTextType(valueType))
    {
        std::vector<std::string> texts;
        if (!SplitStrings(data, count, bytes, &texts, error))
        {
            return false;
        }

        if (!array && texts.size() != 1)
        {
            *error = "a single value has an element count of 1";
            return false;
        }

        if (plain == RUSD_VALUE_STRING)
        {
            return array ? attribute.Set(VtStringArray(texts.begin(), texts.end()), at) : attribute.Set(texts[0], at);
        }

        if (plain == RUSD_VALUE_TOKEN)
        {
            VtTokenArray tokens;
            for (const std::string& text : texts)
            {
                tokens.push_back(TfToken(text));
            }

            return array ? attribute.Set(tokens, at) : attribute.Set(tokens[0], at);
        }

        VtArray<SdfAssetPath> assets;
        for (const std::string& text : texts)
        {
            assets.push_back(SdfAssetPath(text));
        }

        return array ? attribute.Set(assets, at) : attribute.Set(assets[0], at);
    }

    switch (plain)
    {
#define RUSD_SET_CASE(code, T, Form) case code: return SetTyped<T, Form>(attribute, at, data, array, count, bytes, error);
        RUSD_NUMERIC_FORMS(RUSD_SET_CASE)
#undef RUSD_SET_CASE
        default:
            *error = "unknown value type " + std::to_string(valueType);
            return false;
    }
}

// A value's type, said as a stage would spell it ("float3[]"), for errors.
std::string DescribeHeldType(const VtValue& value)
{
    const SdfValueTypeName name = SdfSchema::GetInstance().FindType(value);
    return name ? name.GetAsToken().GetString() : value.GetTypeName();
}

bool FindAttribute(RUsdContext* context, const char* primPath, const char* name, UsdAttribute* out)
{
    UsdPrim prim;
    TfToken property;
    if (!FindStagePrim(context, primPath, false, &prim) || !ParsePropertyName(context, name, &property))
    {
        return false;
    }

    *out = prim.GetAttribute(property);
    if (!*out)
    {
        SetError(context, prim.GetPath().GetString() + " has no attribute '" + property.GetString() + "'.");
        return false;
    }

    return true;
}

// ---- Changes and rereads ------------------------------------------------------------------------

bool IsPrototypeRoot(const SdfPath& root)
{
    return TfStringStartsWith(root.GetName(), "__Prototype");
}

SdfPath RootPrimOf(const SdfPath& path)
{
    SdfPath root = path;
    while (root.GetPathElementCount() > 1)
    {
        root = root.GetParentPath();
    }

    return root;
}

// RUSD_QUERY_CHANGES: the changed prims, each one in an instance prototype given as the instances
// that prototype serves (which may sit in another prototype, whose instances are then given in
// turn). A prototype that is gone served instances that were recomposed themselves.
std::string DescribeChanges(RUsdContext* context)
{
    std::set<SdfPath> changed;
    std::vector<SdfPath> pending(context->changedPaths.begin(), context->changedPaths.end());
    for (int round = 0; round < 16 && !pending.empty(); ++round)
    {
        std::vector<SdfPath> next;
        for (const SdfPath& path : pending)
        {
            if (path.IsEmpty() || path == SdfPath::AbsoluteRootPath())
            {
                changed.insert(SdfPath::AbsoluteRootPath());
                continue;
            }

            const SdfPath root = RootPrimOf(path);
            if (!IsPrototypeRoot(root))
            {
                changed.insert(path);
                continue;
            }

            const UsdPrim prototype = context->stage->GetPrimAtPath(root);
            if (prototype && prototype.IsPrototype())
            {
                for (const UsdPrim& instance : prototype.GetInstances())
                {
                    next.push_back(instance.GetPath());
                }
            }
        }

        pending.swap(next);
    }

    std::string text;
    for (const SdfPath& path : changed)
    {
        text += path.GetString();
        text += '\n';
    }

    return text;
}

// RUSD_QUERY_REBUILD_ROOT: see the header.
std::string RebuildRoot(RUsdContext* context, const SdfPath& changed)
{
    SdfPath path = changed.GetPrimPath();
    if (path.IsEmpty() || !path.IsAbsolutePath() || path == SdfPath::AbsoluteRootPath() ||
        IsPrototypeRoot(RootPrimOf(path)))
    {
        return std::string();
    }

    // A removed prim is widened from the nearest ancestor that still composes.
    UsdPrim existing = context->stage->GetPrimAtPath(path);
    for (SdfPath at = path; !existing && at.GetPathElementCount() > 1;)
    {
        at = at.GetParentPath();
        existing = context->stage->GetPrimAtPath(at);
    }

    // A geometry subset belongs to its mesh.
    while (existing && existing.IsA<UsdGeomSubset>())
    {
        existing = existing.GetParent();
        path = existing.GetPath();
    }

    for (UsdPrim at = existing; at && !at.IsPseudoRoot(); at = at.GetParent())
    {
        // Shading and skeleton animation reach the prims bound to them, wherever those are.
        if (at.IsA<UsdShadeNodeGraph>() || at.IsA<UsdShadeShader>() || at.IsA<UsdSkelAnimation>())
        {
            return std::string();
        }

        // A skeleton binds, and an instancer copies, across its whole subtree.
        if (at.IsA<UsdSkelRoot>() || at.IsA<UsdGeomPointInstancer>())
        {
            path = at.GetPath();
        }
    }

    return path.GetString();
}

// The first opinion a layer holds on a prim brings `over` specs for the prim and its ancestors with
// it, and OpenUSD resyncs the topmost new one: a variant switch on one prim would be recorded as a
// change to its parent's whole subtree. Those specs are inert, so they are made first and what
// they record is dropped, leaving the edit's own change.
void EnsureSpecQuietly(RUsdContext* context, const SdfPath& path)
{
    const UsdEditTarget& target = context->stage->GetEditTarget();
    const SdfLayerHandle layer = target.GetLayer();
    const SdfPath specPath = target.MapToSpecPath(path);
    if (!layer || specPath.IsEmpty() || specPath.ContainsPrimVariantSelection() || layer->GetPrimAtPath(specPath))
    {
        return;
    }

    const std::set<SdfPath> before = context->changedPaths;
    SdfCreatePrimInLayer(layer, specPath);
    context->changedPaths = before;
}

void ResetImportTables(RUsdContext* context)
{
    std::vector<RUsdContext::ImportedMaterial>().swap(context->importedMaterials);
    std::vector<RUsdContext::ImportedMesh>().swap(context->importedMeshes);
    std::vector<RUsdContext::ImportedNode>().swap(context->importedNodes);
    std::vector<RUsdContext::ImportedNodeTrack>().swap(context->importedNodeTracks);
    std::vector<RUsdContext::ImportedSkeleton>().swap(context->importedSkeletons);
    std::vector<RUsdContext::ImportedInstancedMesh>().swap(context->importedInstancedMeshes);
    std::vector<RUsdContext::ImportedLight>().swap(context->importedLights);
    std::vector<RUsdImportCamera>().swap(context->importedCameras);
    std::vector<RUsdContext::ImportedAudio>().swap(context->importedAudio);
    std::unordered_map<std::string, RUsdContext::MaterialCacheEntry>().swap(context->importedMaterialCache);
    std::unordered_map<std::string, std::string>().swap(context->assetResolutions);
    std::unordered_map<std::string, std::string>().swap(context->assetColorSpaces);
    std::vector<std::string>().swap(context->remoteRequests);
    context->animationValues = 0;
    context->skippedNodes = 0;
    context->firstSkippedNode.clear();
    context->skippedMeshes = 0;
    context->skippedPlacements = 0;
    context->importWarningCount = 0;
    {
        std::lock_guard<std::mutex> lock(context->diagnosticsMutex);
        context->diagnostics.clear();
    }
    context->compositionReport.clear();
    context->compositionReportBuilt = false;
    context->physics.reset();
}

} // namespace

int RUsd_CreateStage(const char* path, int inMemory, RUsdContext** outContext)
{
    if (outContext == nullptr)
    {
        return kFailure;
    }

    *outContext = nullptr;
    if (!TbbRuntimeIsUsable())
    {
        return kFailure;
    }

    std::unique_ptr<RUsdContext> context(new RUsdContext());
    try
    {
        const std::string output = SafeString(path);
        if (inMemory != 0)
        {
            context->stage = UsdStage::CreateInMemory();
        }
        else if (output.empty())
        {
            SetError(context.get(), "The stage path is empty.");
            *outContext = context.release();
            return kFailure;
        }
        else
        {
            // A format OpenUSD writes but cannot edit (usdAbc) is built in memory and exported on save.
            const SdfFileFormatConstPtr format = SdfFileFormat::FindByExtension(output);
            if (format && format->SupportsWriting() && !format->SupportsEditing())
            {
                context->stage = UsdStage::CreateInMemory();
                context->exportTo = output;
            }
            else
            {
                context->stage = UsdStage::CreateNew(output);
            }
        }

        if (!context->stage)
        {
            SetError(context.get(), "OpenUSD could not create a stage at '" + output + "'.");
            *outContext = context.release();
            return kFailure;
        }

        context->changeListener.reset(new RUsdChangeListener(context->stage, &context->changedPaths));
        *outContext = context.release();
        return kSuccess;
    }
    catch (const std::exception& exception)
    {
        SetError(context.get(), MakeError("Failed to create a USD stage", exception));
    }
    catch (...)
    {
        SetError(context.get(), "Failed to create a USD stage.");
    }

    *outContext = context.release();
    return kFailure;
}

int RUsd_SaveStage(RUsdContext* context)
{
    return RunStageCall(context, "save the stage", [&]() {
        if (!context->exportTo.empty())
        {
            if (!context->stage->Export(context->exportTo))
            {
                SetError(context, "OpenUSD could not write '" + context->exportTo + "'.");
                return kFailure;
            }

            return kSuccess;
        }

        if (context->stage->GetRootLayer()->IsAnonymous())
        {
            SetError(context, "An in-memory stage has no file to save to; export it instead.");
            return kFailure;
        }

        context->stage->Save();
        const SdfLayerHandle session = context->stage->GetSessionLayer();
        for (const SdfLayerHandle& layer : context->stage->GetUsedLayers())
        {
            if (layer && layer != session && !layer->IsAnonymous() && layer->IsDirty())
            {
                SetError(context, "OpenUSD could not save '" + layer->GetIdentifier() + "'.");
                return kFailure;
            }
        }

        return kSuccess;
    });
}

int RUsd_ExportStage(RUsdContext* context, const char* path, int flatten)
{
    return RunStageCall(context, "export the stage", [&]() {
        const std::string output = SafeString(path);
        if (output.empty())
        {
            SetError(context, "The export path is empty.");
            return kFailure;
        }

        const bool written = flatten != 0 ? context->stage->Export(output)
                                          : context->stage->GetRootLayer()->Export(output);
        if (!written)
        {
            SetError(context, "OpenUSD could not write '" + output + "'.");
            return kFailure;
        }

        return kSuccess;
    });
}

int RUsd_ReloadStage(RUsdContext* context)
{
    return RunStageCall(context, "reload the stage", [&]() {
        context->stage->Reload();
        return kSuccess;
    });
}

int RUsd_GetStageInfo(RUsdContext* context, RUsdStageInfo* info)
{
    return RunStageCall(context, "read the stage metadata", [&]() {
        if (info == nullptr || info->structSize < static_cast<int>(sizeof(RUsdStageInfo)))
        {
            SetError(context, "RUsdStageInfo is null, or its structSize is smaller than this plugin's.");
            return kFailure;
        }

        const UsdStageRefPtr& stage = context->stage;
        info->hasTimeRange = stage->HasAuthoredTimeCodeRange() ? 1 : 0;
        info->startTimeCode = stage->GetStartTimeCode();
        info->endTimeCode = stage->GetEndTimeCode();
        info->timeCodesPerSecond = stage->GetTimeCodesPerSecond();
        info->framesPerSecond = stage->GetFramesPerSecond();
        info->metersPerUnit = UsdGeomGetStageMetersPerUnit(stage);
        info->upAxis = UsdGeomGetStageUpAxis(stage) == UsdGeomTokens->z ? 2 : 1;
        info->dirty = stage->GetSessionLayer()->IsDirty() ? 1 : 0;
        for (const SdfLayerHandle& layer : stage->GetUsedLayers())
        {
            if (layer && layer->IsDirty())
            {
                info->dirty = 1;
            }
        }

        return kSuccess;
    });
}

int RUsd_SetStageMetadata(RUsdContext* context, int field, double value)
{
    return RunStageCall(context, "set stage metadata", [&]() {
        if (!CheckEditable(context, SdfPath::AbsoluteRootPath()))
        {
            return kFailure;
        }

        if (!std::isfinite(value))
        {
            SetError(context, "The metadata value is not a finite number.");
            return kFailure;
        }

        const UsdStageRefPtr& stage = context->stage;
        bool written = true;
        switch (field)
        {
            case RUSD_STAGE_START_TIME_CODE:
                stage->SetStartTimeCode(value);
                break;
            case RUSD_STAGE_END_TIME_CODE:
                stage->SetEndTimeCode(value);
                break;
            case RUSD_STAGE_TIME_CODES_PER_SECOND:
            case RUSD_STAGE_FRAMES_PER_SECOND:
                if (value <= 0.0)
                {
                    SetError(context, "A rate must be above 0.");
                    return kFailure;
                }

                if (field == RUSD_STAGE_TIME_CODES_PER_SECOND)
                {
                    stage->SetTimeCodesPerSecond(value);
                }
                else
                {
                    stage->SetFramesPerSecond(value);
                }

                break;
            case RUSD_STAGE_METERS_PER_UNIT:
                if (value <= 0.0)
                {
                    SetError(context, "metersPerUnit must be above 0.");
                    return kFailure;
                }

                written = UsdGeomSetStageMetersPerUnit(stage, value);
                break;
            case RUSD_STAGE_UP_AXIS:
                if (value != 1.0 && value != 2.0)
                {
                    SetError(context, "The up axis is 1 (Y) or 2 (Z).");
                    return kFailure;
                }

                written = UsdGeomSetStageUpAxis(stage, value == 2.0 ? UsdGeomTokens->z : UsdGeomTokens->y);
                break;
            default:
                SetError(context, "Unknown stage metadata field " + std::to_string(field) + ".");
                return kFailure;
        }

        if (!written)
        {
            SetError(context, "OpenUSD refused the stage metadata; the edit target must be the root or session layer.");
            return kFailure;
        }

        return kSuccess;
    });
}

int RUsd_SetEditTarget(
    RUsdContext* context,
    int target,
    const char* layer,
    const char* variantPrimPath,
    const char* variantSet,
    const char* variant)
{
    return RunStageCall(context, "set the edit target", [&]() {
        const UsdStageRefPtr& stage = context->stage;
        SdfLayerHandle chosen;
        switch (target)
        {
            case RUSD_EDIT_TARGET_ROOT:
                chosen = stage->GetRootLayer();
                break;
            case RUSD_EDIT_TARGET_SESSION:
                chosen = stage->GetSessionLayer();
                break;
            case RUSD_EDIT_TARGET_LAYER:
            {
                const std::string wanted = SafeString(layer);
                for (const SdfLayerHandle& candidate : stage->GetLayerStack(false))
                {
                    if (candidate && !wanted.empty() &&
                        (candidate->GetIdentifier() == wanted || candidate->GetRealPath() == wanted))
                    {
                        chosen = candidate;
                        break;
                    }
                }

                if (!chosen)
                {
                    SetError(context, "'" + wanted + "' is not a layer of the stage's root layer stack.");
                    return kFailure;
                }

                break;
            }
            default:
                SetError(context, "Unknown edit target " + std::to_string(target) + ".");
                return kFailure;
        }

        const std::string primText = SafeString(variantPrimPath);
        const std::string setName = SafeString(variantSet);
        const std::string variantName = SafeString(variant);
        if (primText.empty() && setName.empty() && variantName.empty())
        {
            stage->SetEditTarget(stage->GetEditTargetForLocalLayer(chosen));
            return kSuccess;
        }

        UsdPrim prim;
        if (setName.empty() || variantName.empty() || !FindStagePrim(context, variantPrimPath, false, &prim))
        {
            if (setName.empty() || variantName.empty())
            {
                SetError(context, "A variant edit target names a prim, a variant set and a variant.");
            }

            return kFailure;
        }

        if (prim.IsInstanceProxy() || !prim.GetVariantSets().HasVariantSet(setName) ||
            !prim.GetVariantSet(setName).HasAuthoredVariant(variantName))
        {
            SetError(context, prim.GetPath().GetString() + " has no variant " + setName + "=" + variantName + " to edit.");
            return kFailure;
        }

        // OpenUSD makes specs inside a variant only while the prim composes it.
        UsdVariantSet set = prim.GetVariantSet(setName);
        if (set.GetVariantSelection() != variantName)
        {
            SetError(context, prim.GetPath().GetString() + " composes " + setName + "=" + set.GetVariantSelection() +
                "; select " + variantName + " before editing inside it.");
            return kFailure;
        }

        stage->SetEditTarget(set.GetVariantEditTarget(chosen));
        return kSuccess;
    });
}

int RUsd_GetPrimInfo(RUsdContext* context, const char* primPath, int* flags, int* specifier)
{
    return RunStageCall(context, "look up a prim", [&]() {
        SdfPath path;
        if (flags == nullptr || specifier == nullptr || !ParseStagePath(context, primPath, false, &path))
        {
            if (flags == nullptr || specifier == nullptr)
            {
                SetError(context, "The prim info outputs are null.");
            }

            return kFailure;
        }

        *flags = 0;
        *specifier = -1;
        const UsdPrim prim = context->stage->GetPrimAtPath(path);
        if (!prim)
        {
            return kSuccess;
        }

        int bits = RUSD_PRIM_VALID;
        bits |= prim.IsActive() ? RUSD_PRIM_ACTIVE : 0;
        bits |= prim.IsDefined() ? RUSD_PRIM_DEFINED : 0;
        bits |= prim.IsAbstract() ? RUSD_PRIM_ABSTRACT : 0;
        bits |= prim.IsLoaded() ? RUSD_PRIM_LOADED : 0;
        bits |= prim.HasAuthoredPayloads() ? RUSD_PRIM_HAS_PAYLOAD : 0;
        bits |= prim.IsInstance() ? RUSD_PRIM_INSTANCE : 0;
        bits |= prim.IsInstanceProxy() ? RUSD_PRIM_INSTANCE_PROXY : 0;
        bits |= prim.HasVariantSets() ? RUSD_PRIM_VARIANT_SETS : 0;
        bits |= prim.IsModel() ? RUSD_PRIM_MODEL : 0;
        bits |= prim.HasAuthoredReferences() ? RUSD_PRIM_HAS_REFERENCES : 0;
        *flags = bits;
        switch (prim.GetSpecifier())
        {
            case SdfSpecifierDef: *specifier = RUSD_SPECIFIER_DEF; break;
            case SdfSpecifierOver: *specifier = RUSD_SPECIFIER_OVER; break;
            case SdfSpecifierClass: *specifier = RUSD_SPECIFIER_CLASS; break;
            default: break;
        }

        return kSuccess;
    });
}

int RUsd_QueryText(
    RUsdContext* context,
    int query,
    const char* primPath,
    const char* name,
    char* buffer,
    int bufferCapacity,
    int* requiredLength)
{
    return RunStageCall(context, "query the stage", [&]() {
        const UsdStageRefPtr& stage = context->stage;
        std::string text;
        const auto line = [&text](const std::string& item) {
            text += item;
            text += '\n';
        };

        switch (query)
        {
            case RUSD_QUERY_DEFAULT_PRIM:
            {
                const UsdPrim defaultPrim = stage->GetDefaultPrim();
                text = defaultPrim ? defaultPrim.GetPath().GetString() : std::string();
                return ReturnText(context, text, buffer, bufferCapacity, requiredLength);
            }
            case RUSD_QUERY_LAYERS:
                for (const SdfLayerHandle& layer : stage->GetLayerStack(true))
                {
                    if (layer)
                    {
                        line(layer->GetIdentifier());
                    }
                }

                return ReturnText(context, text, buffer, bufferCapacity, requiredLength);
            case RUSD_QUERY_CHANGES:
            {
                text = DescribeChanges(context);
                const int result = ReturnText(context, text, buffer, bufferCapacity, requiredLength);
                if (result == kSuccess && buffer != nullptr)
                {
                    context->changedPaths.clear();
                }

                return result;
            }
            case RUSD_QUERY_REBUILD_ROOT:
            {
                SdfPath path;
                if (!ParseStagePath(context, primPath, true, &path))
                {
                    return kFailure;
                }

                return ReturnText(context, RebuildRoot(context, path), buffer, bufferCapacity, requiredLength);
            }
            default:
                break;
        }

        SdfPath path;
        if (!ParseStagePath(context, primPath, true, &path))
        {
            return kFailure;
        }

        const UsdPrim prim = stage->GetPrimAtPath(path);
        const std::string field = SafeString(name);
        if (prim)
        {
            switch (query)
            {
                case RUSD_QUERY_CHILDREN:
                case RUSD_QUERY_ALL_CHILDREN:
                {
                    const Usd_PrimFlagsPredicate predicate = query == RUSD_QUERY_CHILDREN
                        ? UsdTraverseInstanceProxies(UsdPrimDefaultPredicate)
                        : UsdTraverseInstanceProxies(UsdPrimAllPrimsPredicate);
                    for (const UsdPrim& child : prim.GetFilteredChildren(predicate))
                    {
                        line(child.GetName().GetString());
                    }

                    break;
                }
                case RUSD_QUERY_TYPE_NAME:
                    text = prim.GetTypeName().GetString();
                    break;
                case RUSD_QUERY_PROPERTIES:
                    for (const UsdProperty& property : prim.GetProperties())
                    {
                        if (const UsdAttribute attribute = property.As<UsdAttribute>())
                        {
                            line(property.GetName().GetString() + "\ta\t" + attribute.GetTypeName().GetAsToken().GetString());
                        }
                        else if (property.Is<UsdRelationship>())
                        {
                            line(property.GetName().GetString() + "\tr\t");
                        }
                    }

                    break;
                case RUSD_QUERY_VARIANT_SETS:
                {
                    const UsdVariantSets sets = prim.GetVariantSets();
                    for (const std::string& setName : sets.GetNames())
                    {
                        const UsdVariantSet set = sets.GetVariantSet(setName);
                        std::string record = setName + "\t" + set.GetVariantSelection();
                        for (const std::string& variantName : set.GetVariantNames())
                        {
                            record += "\t" + variantName;
                        }

                        line(record);
                    }

                    break;
                }
                case RUSD_QUERY_TARGETS:
                    if (SdfPath::IsValidNamespacedIdentifier(field))
                    {
                        if (const UsdRelationship relationship = prim.GetRelationship(TfToken(field)))
                        {
                            SdfPathVector targets;
                            relationship.GetForwardedTargets(&targets);
                            for (const SdfPath& target : targets)
                            {
                                line(target.GetString());
                            }
                        }
                    }

                    break;
                case RUSD_QUERY_METADATA:
                    if (!field.empty())
                    {
                        VtValue value;
                        if (prim.GetMetadata(TfToken(field), &value))
                        {
                            if (VtValueHoldsType(value, "TfToken"))
                            {
                                text = value.UncheckedGet<TfToken>().GetString();
                            }
                            else if (VtValueHoldsType(value, SdfValueTypeNames->String.GetType().GetTypeName().c_str()))
                            {
                                text = value.UncheckedGet<std::string>();
                            }
                        }
                    }

                    break;
                default:
                    SetError(context, "Unknown stage query " + std::to_string(query) + ".");
                    return kFailure;
            }
        }
        else if (query < RUSD_QUERY_CHILDREN || query > RUSD_QUERY_METADATA)
        {
            SetError(context, "Unknown stage query " + std::to_string(query) + ".");
            return kFailure;
        }

        return ReturnText(context, text, buffer, bufferCapacity, requiredLength);
    });
}

int RUsd_EditPrim(RUsdContext* context, const char* primPath, int edit, const char* name, const char* value)
{
    return RunStageCall(context, "edit a prim", [&]() {
        const UsdStageRefPtr& stage = context->stage;
        SdfPath path;
        if (!ParseStagePath(context, primPath, false, &path))
        {
            return kFailure;
        }

        const std::string field = SafeString(name);
        const std::string text = SafeString(value);
        const auto refused = [&](const char* what) {
            SetError(context, std::string("OpenUSD refused to ") + what + " at " + path.GetString() + ".");
            return kFailure;
        };

        // Loading changes what the stage composes, not what any layer says.
        if (edit == RUSD_EDIT_LOAD || edit == RUSD_EDIT_UNLOAD)
        {
            if (!stage->GetPrimAtPath(path))
            {
                SetError(context, "The stage has no prim at " + path.GetString() + ".");
                return kFailure;
            }

            if (edit == RUSD_EDIT_LOAD)
            {
                stage->Load(path);
            }
            else
            {
                stage->Unload(path);
            }

            return kSuccess;
        }

        if (!CheckEditable(context, path))
        {
            return kFailure;
        }

        switch (edit)
        {
            case RUSD_EDIT_DEFINE:
                return stage->DefinePrim(path, TfToken(text)) ? kSuccess : refused("define a prim");
            case RUSD_EDIT_OVERRIDE:
                return stage->OverridePrim(path) ? kSuccess : refused("override a prim");
            case RUSD_EDIT_CLASS:
                return stage->CreateClassPrim(path) ? kSuccess : refused("create a class (a root prim path)");
            case RUSD_EDIT_REMOVE:
                return stage->RemovePrim(path) ? kSuccess : refused("remove the prim's spec");
            default:
                break;
        }

        UsdPrim prim = stage->GetPrimAtPath(path);
        if (!prim)
        {
            SetError(context, "The stage has no prim at " + path.GetString() + ".");
            return kFailure;
        }

        const auto parseBool = [&](bool* out) {
            if (text == "1" || text == "0")
            {
                *out = text == "1";
                return true;
            }

            SetError(context, "The value is \"1\" or \"0\", or empty to clear the opinion.");
            return false;
        };
        const auto parseTarget = [&](bool allowEmpty, SdfPath* out) {
            if (allowEmpty && text.empty())
            {
                *out = SdfPath();
                return true;
            }

            return ParseAbsolutePrimPath(context, value, out);
        };

        if (edit == RUSD_EDIT_ACTIVE || edit == RUSD_EDIT_SELECT_VARIANT)
        {
            EnsureSpecQuietly(context, path);
        }

        switch (edit)
        {
            case RUSD_EDIT_ACTIVE:
            {
                if (text.empty())
                {
                    return prim.ClearActive() ? kSuccess : refused("clear active");
                }

                bool active = true;
                return !parseBool(&active) ? kFailure : (prim.SetActive(active) ? kSuccess : refused("set active"));
            }
            case RUSD_EDIT_INSTANCEABLE:
            {
                if (text.empty())
                {
                    return prim.ClearInstanceable() ? kSuccess : refused("clear instanceable");
                }

                bool instanceable = false;
                return !parseBool(&instanceable)
                    ? kFailure
                    : (prim.SetInstanceable(instanceable) ? kSuccess : refused("set instanceable"));
            }
            case RUSD_EDIT_TYPE_NAME:
                return prim.SetTypeName(TfToken(text)) ? kSuccess : refused("set the type name");
            case RUSD_EDIT_METADATA:
            {
                if (field.empty())
                {
                    SetError(context, "The metadata field name is empty.");
                    return kFailure;
                }

                // Only a string or token field: anything else would need its own type. The value
                // is the schema's own fallback with the text swapped in, so it carries OpenUSD's type
                // info: one built here can fail OpenUSD's later type checks (a flatten dropped a
                // kind set from a TfToken made in this plugin; see VtValueHoldsType).
                const TfToken key(field);
                VtValue value = SdfSchema::GetInstance().GetFallback(key);
                if (VtValueHoldsType(value, "TfToken"))
                {
                    TfToken token(text);
                    value.UncheckedSwap(token);
                    return prim.SetMetadata(key, value) ? kSuccess : refused("set metadata");
                }

                if (VtValueHoldsType(value, SdfValueTypeNames->String.GetType().GetTypeName().c_str()))
                {
                    std::string copy = text;
                    value.UncheckedSwap(copy);
                    return prim.SetMetadata(key, value) ? kSuccess : refused("set metadata");
                }

                SetError(context, "'" + field + "' is not a string or token metadata field.");
                return kFailure;
            }
            case RUSD_EDIT_DEFAULT_PRIM:
                if (!path.IsRootPrimPath())
                {
                    SetError(context, "The defaultPrim must be a root prim; " + path.GetString() + " is not.");
                    return kFailure;
                }

                stage->SetDefaultPrim(prim);
                return kSuccess;
            case RUSD_EDIT_SELECT_VARIANT:
            case RUSD_EDIT_ADD_VARIANT:
            {
                if (field.empty())
                {
                    SetError(context, "The variant set name is empty.");
                    return kFailure;
                }

                if (edit == RUSD_EDIT_ADD_VARIANT)
                {
                    if (text.empty())
                    {
                        SetError(context, "The variant name is empty.");
                        return kFailure;
                    }

                    return prim.GetVariantSets().AddVariantSet(field).AddVariant(text) ? kSuccess : refused("add a variant");
                }

                UsdVariantSet set = prim.GetVariantSet(field);
                const bool done = text.empty() ? set.ClearVariantSelection() : set.SetVariantSelection(text);
                return done ? kSuccess : refused("select a variant");
            }
            case RUSD_EDIT_ADD_REFERENCE:
            case RUSD_EDIT_ADD_PAYLOAD:
            {
                SdfPath target;
                if (!parseTarget(true, &target))
                {
                    return kFailure;
                }

                if (edit == RUSD_EDIT_ADD_REFERENCE)
                {
                    const bool done = field.empty()
                        ? prim.GetReferences().AddInternalReference(target)
                        : prim.GetReferences().AddReference(SdfReference(field, target));
                    return done ? kSuccess : refused("add a reference");
                }

                const bool done = field.empty()
                    ? prim.GetPayloads().AddInternalPayload(target)
                    : prim.GetPayloads().AddPayload(SdfPayload(field, target));
                return done ? kSuccess : refused("add a payload");
            }
            case RUSD_EDIT_CLEAR_REFERENCES:
                return prim.GetReferences().ClearReferences() ? kSuccess : refused("clear references");
            case RUSD_EDIT_CLEAR_PAYLOADS:
                return prim.GetPayloads().ClearPayloads() ? kSuccess : refused("clear payloads");
            case RUSD_EDIT_ADD_INHERIT:
            case RUSD_EDIT_ADD_SPECIALIZE:
            {
                SdfPath target;
                if (!parseTarget(false, &target))
                {
                    return kFailure;
                }

                const bool done = edit == RUSD_EDIT_ADD_INHERIT ? prim.GetInherits().AddInherit(target)
                                                                : prim.GetSpecializes().AddSpecialize(target);
                return done ? kSuccess : refused("add the arc");
            }
            case RUSD_EDIT_SET_TARGETS:
            {
                TfToken property;
                if (!ParsePropertyName(context, name, &property))
                {
                    return kFailure;
                }

                SdfPathVector targets;
                for (const std::vector<std::string>& record : SplitRecords(text))
                {
                    if (!SdfPath::IsValidPathString(record[0]) || !SdfPath(record[0]).IsAbsolutePath())
                    {
                        SetError(context, "'" + record[0] + "' is not an absolute path.");
                        return kFailure;
                    }

                    targets.push_back(SdfPath(record[0]));
                }

                UsdRelationship relationship = prim.GetRelationship(property);
                if (!relationship)
                {
                    relationship = prim.CreateRelationship(property, false);
                }

                return relationship && relationship.SetTargets(targets) ? kSuccess : refused("set targets");
            }
            case RUSD_EDIT_REMOVE_PROPERTY:
            {
                TfToken property;
                if (!ParsePropertyName(context, name, &property))
                {
                    return kFailure;
                }

                return prim.RemoveProperty(property) ? kSuccess : refused("remove the property's spec");
            }
            case RUSD_EDIT_CLEAR_VALUE:
            {
                UsdAttribute attribute;
                if (!FindAttribute(context, primPath, name, &attribute))
                {
                    return kFailure;
                }

                return attribute.Clear() ? kSuccess : refused("clear the attribute");
            }
            default:
                SetError(context, "Unknown prim edit " + std::to_string(edit) + ".");
                return kFailure;
        }
    });
}

int RUsd_GetAttributeInfo(RUsdContext* context, const char* primPath, const char* name, RUsdAttributeInfo* info)
{
    return RunStageCall(context, "read an attribute", [&]() {
        if (info == nullptr || info->structSize < static_cast<int>(sizeof(RUsdAttributeInfo)))
        {
            SetError(context, "RUsdAttributeInfo is null, or its structSize is smaller than this plugin's.");
            return kFailure;
        }

        UsdPrim prim;
        TfToken property;
        if (!FindStagePrim(context, primPath, false, &prim) || !ParsePropertyName(context, name, &property))
        {
            return kFailure;
        }

        const int structSize = info->structSize;
        std::memset(info, 0, sizeof(RUsdAttributeInfo));
        info->structSize = structSize;
        const UsdAttribute attribute = prim.GetAttribute(property);
        if (!attribute)
        {
            return kSuccess;
        }

        info->exists = 1;
        info->valueType = ValueTypeOf(attribute.GetTypeName());
        info->hasValue = attribute.HasValue() ? 1 : 0;
        info->hasAuthoredValue = attribute.HasAuthoredValue() ? 1 : 0;
        info->timeSampleCount = static_cast<int>(std::min<size_t>(attribute.GetNumTimeSamples(),
                                                                  static_cast<size_t>(std::numeric_limits<int>::max())));
        info->uniform = attribute.GetVariability() == SdfVariabilityUniform ? 1 : 0;
        info->custom = attribute.IsCustom() ? 1 : 0;
        return kSuccess;
    });
}

int RUsd_GetAttributeValue(
    RUsdContext* context,
    const char* primPath,
    const char* name,
    int timeKind,
    double time,
    int valueType,
    void* buffer,
    long long bufferBytes,
    long long* elementCount,
    long long* requiredBytes)
{
    return RunStageCall(context, "read an attribute value", [&]() {
        if (elementCount == nullptr || requiredBytes == nullptr)
        {
            SetError(context, "The value size outputs are null.");
            return kFailure;
        }

        *elementCount = 0;
        *requiredBytes = -1;
        const SdfValueTypeName expected = PlainTypeName(valueType);
        const int plain = valueType & ~(RUSD_VALUE_ARRAY | RUSD_VALUE_RESOLVED);
        if (!expected || ((valueType & RUSD_VALUE_RESOLVED) != 0 && plain != RUSD_VALUE_ASSET) ||
            (valueType & ~(0xff | RUSD_VALUE_ARRAY | RUSD_VALUE_RESOLVED)) != 0)
        {
            SetError(context, "Unknown value type " + std::to_string(valueType) + ".");
            return kFailure;
        }

        UsdAttribute attribute;
        UsdTimeCode at;
        if (!FindAttribute(context, primPath, name, &attribute) || !ParseTime(context, timeKind, time, false, &at))
        {
            return kFailure;
        }

        // The value a size query read is held for the copy that follows it; any other call drops it.
        std::ostringstream key;
        key.precision(17);
        key << attribute.GetPath().GetString() << '\n' << timeKind << '\n' << time << '\n' << valueType;
        VtValue value;
        if (context->pendingValueKey == key.str())
        {
            value.Swap(context->pendingValue);
        }
        else if (!attribute.Get(&value, at))
        {
            value = VtValue();
        }

        context->pendingValueKey.clear();
        context->pendingValue = VtValue();
        if (value.IsEmpty())
        {
            return kSuccess; // no value
        }

        if (value.GetTypeName() != expected.GetType().GetTypeName())
        {
            SetError(context, attribute.GetPath().GetString() + " holds " + DescribeHeldType(value) + ", not " +
                expected.GetAsToken().GetString() + ".");
            return kFailure;
        }

        long long count = 0;
        const long long bytes = PutAnyValue(value, valueType, nullptr, &count);
        *elementCount = count;
        *requiredBytes = bytes;
        if (buffer == nullptr || bufferBytes < bytes)
        {
            context->pendingValueKey = key.str();
            context->pendingValue.Swap(value);
            return kSuccess;
        }

        PutAnyValue(value, valueType, buffer, &count);
        return kSuccess;
    });
}

int RUsd_SetAttributeValue(
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
    long long dataBytes)
{
    return RunStageCall(context, "write an attribute value", [&]() {
        UsdPrim prim;
        TfToken property;
        UsdTimeCode at;
        if (!FindStagePrim(context, primPath, false, &prim) || !ParsePropertyName(context, name, &property) ||
            !ParseTime(context, timeKind, time, true, &at) || !CheckEditable(context, prim.GetPath()))
        {
            return kFailure;
        }

        context->pendingValueKey.clear();
        context->pendingValue = VtValue();
        UsdAttribute attribute = prim.GetAttribute(property);

        // A clear or a block takes no value, so it needs no type: only an attribute.
        if ((flags & (RUSD_SET_CLEAR | RUSD_SET_BLOCK)) != 0)
        {
            if (!attribute)
            {
                SetError(context, prim.GetPath().GetString() + " has no attribute '" + property.GetString() + "'.");
                return kFailure;
            }

            bool done = true;
            if ((flags & RUSD_SET_CLEAR) != 0)
            {
                done = at.IsDefault() ? attribute.ClearDefault() : attribute.ClearAtTime(at);
            }
            else if (at.IsDefault())
            {
                attribute.Block(); // the default and every sample
            }
            else
            {
                done = attribute.Set(SdfValueBlock(), at);
            }

            if (!done)
            {
                SetError(context, "OpenUSD refused to " + std::string((flags & RUSD_SET_CLEAR) != 0 ? "clear " : "block ") +
                    attribute.GetPath().GetString() + ".");
                return kFailure;
            }

            return kSuccess;
        }

        const SdfValueTypeName expected = PlainTypeName(valueType);
        if (!expected || (valueType & ~(0xff | RUSD_VALUE_ARRAY)) != 0)
        {
            SetError(context, "Unknown value type " + std::to_string(valueType) + ".");
            return kFailure;
        }

        if (!attribute)
        {
            const std::string declared = SafeString(typeName);
            const SdfValueTypeName type = declared.empty() ? expected : SdfSchema::GetInstance().FindType(declared);
            if (!type)
            {
                SetError(context, "'" + declared + "' is not a value type name.");
                return kFailure;
            }

            if (type.GetType() != expected.GetType())
            {
                SetError(context, "An attribute of type " + type.GetAsToken().GetString() + " does not hold " +
                    expected.GetAsToken().GetString() + ".");
                return kFailure;
            }

            attribute = prim.CreateAttribute(property, type, (flags & RUSD_SET_CUSTOM) != 0,
                (flags & RUSD_SET_UNIFORM) != 0 ? SdfVariabilityUniform : SdfVariabilityVarying);
            if (!attribute)
            {
                SetError(context, "OpenUSD refused to make " + prim.GetPath().GetString() + "." + property.GetString() + ".");
                return kFailure;
            }
        }
        else if (attribute.GetTypeName().GetType() != expected.GetType())
        {
            SetError(context, attribute.GetPath().GetString() + " is declared " +
                attribute.GetTypeName().GetAsToken().GetString() + ", not " + expected.GetAsToken().GetString() + ".");
            return kFailure;
        }

        std::string error;
        const bool written = SetAnyValue(attribute, at, valueType, data, elementCount, dataBytes, &error);
        if (!error.empty())
        {
            SetError(context, "The value for " + attribute.GetPath().GetString() + " is malformed: " + error + ".");
            return kFailure;
        }

        if (!written)
        {
            SetError(context, "OpenUSD refused the value for " + attribute.GetPath().GetString() + ".");
            return kFailure;
        }

        return kSuccess;
    });
}

int RUsd_GetAttributeTimeSamples(
    RUsdContext* context,
    const char* primPath,
    const char* name,
    double* times,
    int capacity,
    int* count)
{
    return RunStageCall(context, "read time samples", [&]() {
        UsdAttribute attribute;
        if (count == nullptr || !FindAttribute(context, primPath, name, &attribute))
        {
            if (count == nullptr)
            {
                SetError(context, "The sample count output is null.");
            }

            return kFailure;
        }

        std::vector<double> samples;
        attribute.GetTimeSamples(&samples);
        if (samples.size() > static_cast<size_t>(std::numeric_limits<int>::max()))
        {
            SetError(context, "The attribute has too many time samples to return.");
            return kFailure;
        }

        *count = static_cast<int>(samples.size());
        if (times == nullptr)
        {
            return kSuccess;
        }

        if (!FitsCapacity(capacity, samples.size()))
        {
            SetError(context, "The time sample buffer is too small.");
            return kFailure;
        }

        std::copy(samples.begin(), samples.end(), times);
        return kSuccess;
    });
}

int RUsd_GetPrimTransform(
    RUsdContext* context,
    const char* primPath,
    int space,
    int timeKind,
    double time,
    RUsdMatrix4x4* matrix,
    int* resetsXformStack)
{
    return RunStageCall(context, "read a transform", [&]() {
        UsdPrim prim;
        UsdTimeCode at;
        if (matrix == nullptr || !FindStagePrim(context, primPath, false, &prim) ||
            !ParseTime(context, timeKind, time, false, &at))
        {
            if (matrix == nullptr)
            {
                SetError(context, "The matrix output is null.");
            }

            return kFailure;
        }

        UsdGeomXformCache cache(at);
        bool resets = false;
        GfMatrix4d result(1.0);
        if (space == RUSD_TRANSFORM_WORLD)
        {
            result = cache.GetLocalToWorldTransform(prim);
        }
        else if (space == RUSD_TRANSFORM_LOCAL)
        {
            if (prim.IsA<UsdGeomXformable>())
            {
                result = cache.GetLocalTransformation(prim, &resets);
            }
        }
        else
        {
            SetError(context, "Unknown transform space " + std::to_string(space) + ".");
            return kFailure;
        }

        *matrix = FromGfMatrix(result);
        if (resetsXformStack != nullptr)
        {
            *resetsXformStack = resets ? 1 : 0;
        }

        return kSuccess;
    });
}

int RUsd_SetPrimTransform(
    RUsdContext* context,
    const char* primPath,
    int timeKind,
    double time,
    const RUsdMatrix4x4* matrix)
{
    return RunStageCall(context, "write a transform", [&]() {
        UsdPrim prim;
        UsdTimeCode at;
        if (matrix == nullptr || !FindStagePrim(context, primPath, false, &prim) ||
            !ParseTime(context, timeKind, time, true, &at) || !CheckEditable(context, prim.GetPath()))
        {
            if (matrix == nullptr)
            {
                SetError(context, "The matrix is null.");
            }

            return kFailure;
        }

        if (!prim.IsA<UsdGeomXformable>())
        {
            SetError(context, prim.GetPath().GetString() + " is not Xformable.");
            return kFailure;
        }

        UsdGeomXformOp op = UsdGeomXformable(prim).MakeMatrixXform();
        if (!op || !op.Set(ToGfMatrix(*matrix), at))
        {
            SetError(context, "OpenUSD refused the transform for " + prim.GetPath().GetString() + ".");
            return kFailure;
        }

        return kSuccess;
    });
}

int RUsd_ReadSubtrees(RUsdContext* context, const char* rootPaths, int flags, const RUsdImportLimits* limits)
{
    return RunStageCall(context, "reread part of the stage", [&]() {
        if (flags != 0)
        {
            SetError(context, "RUsd_ReadSubtrees flags are reserved; pass 0.");
            return kFailure;
        }

        std::vector<SdfPath> roots;
        for (const std::vector<std::string>& record : SplitRecords(SafeString(rootPaths)))
        {
            SdfPath path;
            if (!ParseAbsolutePrimPath(context, record[0].c_str(), &path))
            {
                return kFailure;
            }

            roots.push_back(path);
        }

        RUsdContext::ImportLimits budgets;
        if (limits != nullptr && !TakeImportLimits(context, limits, &budgets))
        {
            return kFailure;
        }

        ResetImportTables(context);
        context->importLimits = budgets;
        ReadStageMetrics(context);
        ImportWalk walk(context, ImportPredicate());
        for (const SdfPath& root : roots)
        {
            const UsdPrim prim = context->stage->GetPrimAtPath(root);
            if (!prim || !walk.predicate(prim))
            {
                continue; // gone: nothing to read
            }

            // The ancestors' purpose, visibility and animated-visibility scopes, as the full walk
            // would have carried them down to this prim.
            std::vector<UsdPrim> ancestors;
            for (UsdPrim at = prim.GetParent(); at && !at.IsPseudoRoot(); at = at.GetParent())
            {
                ancestors.push_back(at);
            }

            UsdGeomImageable::PurposeInfo purposeInfo;
            int visible = 1;
            int scopes = 0;
            bool excluded = false;
            for (auto at = ancestors.rbegin(); at != ancestors.rend(); ++at)
            {
                bool animatesVisibility = false;
                if (!InheritState(&walk, *at, &purposeInfo, &visible, &animatesVisibility))
                {
                    excluded = true;
                    break;
                }

                if (animatesVisibility)
                {
                    ++walk.animatedVisibilityScopes;
                    ++scopes;
                }
            }

            if (!excluded)
            {
                ImportSubtree(&walk, prim, root.GetString(), static_cast<int>(ancestors.size()), purposeInfo, visible);
            }

            walk.animatedVisibilityScopes -= scopes;
        }

        AppendSkipSummary(context);
        return kSuccess;
    });
}

int RUsd_ReleaseImportData(RUsdContext* context)
{
    return RunStageCall(context, "release the import data", [&]() {
        ResetImportTables(context);
        return kSuccess;
    });
}

int RUsd_ReadPhysics(RUsdContext* context, const RUsdPhysicsReadOptions* options, const RUsdPhysicsData** outData)
{
    return RunStageCall(context, "read the stage's physics", [&]() {
        if (outData == nullptr)
        {
            SetError(context, "RUsd_ReadPhysics: outData is null.");
            return kFailure;
        }
        *outData = nullptr;

        // A larger struct is a newer caller's; its extra fields are not this build's to read.
        RUsdPhysicsReadOptions read{};
        read.structSize = static_cast<int>(sizeof(RUsdPhysicsReadOptions));
        read.flags = RUSD_PHYSICS_READ_PHYSX;
        if (options != nullptr)
        {
            if (options->structSize < static_cast<int>(sizeof(RUsdPhysicsReadOptions)))
            {
                SetError(context, "RUsdPhysicsReadOptions.structSize is smaller than this plugin's RUsdPhysicsReadOptions.");
                return kFailure;
            }
            read.flags = options->flags;
            read.skipWhenExceeded = options->skipWhenExceeded;
            read.maxObjects = options->maxObjects;
            read.maxMeshElements = options->maxMeshElements;
            read.rootPaths = options->rootPaths;
        }
        if (read.maxObjects <= 0)
        {
            read.maxObjects = 1000000;
        }
        if (read.maxMeshElements <= 0)
        {
            read.maxMeshElements = 268435456;
        }

        // The previous block goes first: a failed read leaves nothing behind that looks current.
        context->physics.reset();

        // Prim path -> import index. A mesh is found by where it was placed, then by the prim it
        // came from (a point-instancer prototype's placement differs from its source).
        std::unordered_map<std::string, int> nodes;
        for (size_t i = 0; i < context->importedNodes.size(); ++i)
        {
            nodes.emplace(context->importedNodes[i].path, static_cast<int>(i));
        }
        std::unordered_map<std::string, int> meshes;
        for (size_t i = 0; i < context->importedMeshes.size(); ++i)
        {
            meshes.emplace(context->importedMeshes[i].primPath, static_cast<int>(i));
        }
        for (size_t i = 0; i < context->importedMeshes.size(); ++i)
        {
            meshes.emplace(context->importedMeshes[i].sourcePath, static_cast<int>(i));
        }

        rusd_physics::ImportLookups lookups;
        lookups.node = [&](const std::string& path) {
            const auto found = nodes.find(path);
            return found != nodes.end() ? found->second : -1;
        };
        lookups.mesh = [&](const std::string& path) {
            const auto found = meshes.find(path);
            return found != meshes.end() ? found->second : -1;
        };

        context->physics = rusd_physics::Read(context->stage, read, lookups);
        *outData = rusd_physics::Data(*context->physics);
        return kSuccess;
    });
}

int RUsd_ReleasePhysics(RUsdContext* context)
{
    return RunStageCall(context, "release the physics data", [&]() {
        context->physics.reset();
        return kSuccess;
    });
}
