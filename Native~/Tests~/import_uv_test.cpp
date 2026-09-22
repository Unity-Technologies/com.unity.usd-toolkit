// Import-side UV/normal primvar and material-opacity coverage.
//
// Regression test for meshes whose texture coordinates are authored per face-corner or through
// an index table (what most DCC exporters write): those used to be dropped silently, so imports
// arrived with no UVs at all. Each case below is one mesh in import_uv_fixture.usda. The last
// section covers authored `opacity` (connected to a texture's alpha plus an `opacityThreshold`),
// which was likewise ignored, so cut-out foliage and glass imported fully opaque.
//
// Build and run (macOS; adjust the payload directory for Windows/Linux):
//   PLUGINS=../../Runtime/Plugins/macOS
//   clang++ -std=c++17 -Wl,-headerpad_max_install_names import_uv_test.cpp \
//       -I ../include "$PLUGINS/UnityUSDToolkitNative.dylib" -o import_uv_test
//   install_name_tool -change "@loader_path/UnityUSDToolkitNative.dylib" \
//       "$(cd "$PLUGINS" && pwd)/UnityUSDToolkitNative.dylib" import_uv_test
//   # OpenUSD needs its plugin manifests; Unity sets this itself at runtime.
//   PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd" \
//       ./import_uv_test import_uv_fixture.usda

#include "unity_usd_toolkit_native.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace
{

int failures = 0;

void Check(bool condition, const std::string& what)
{
    if (!condition)
    {
        std::printf("  FAIL %s\n", what.c_str());
        ++failures;
    }
}

void CheckEqual(int actual, int expected, const std::string& what)
{
    if (actual != expected)
    {
        std::printf("  FAIL %s: expected %d, got %d\n", what.c_str(), expected, actual);
        ++failures;
    }
}

struct Mesh
{
    std::string name;
    int pointCount = 0;
    int indexCount = 0;
    std::vector<RUsdVec3> points;
    std::vector<RUsdVec3> normals;
    std::vector<RUsdVec2> uvSets[RUSD_MAX_UV_SETS];
};

bool ReadMeshes(RUsdContext* context, std::vector<Mesh>* meshes)
{
    char primPath[1024] = {0};
    double metersPerUnit = 0.0;
    int upAxis = 0;
    int meshCount = 0;
    int materialCount = 0;
    if (RUsd_GetImportInfo(context, primPath, sizeof(primPath), &metersPerUnit, &upAxis,
                           &meshCount, &materialCount) != 0)
    {
        return false;
    }

    for (int i = 0; i < meshCount; ++i)
    {
        char name[256] = {0};
        int pointCount = 0, indexCount = 0, normalCount = 0, uv0Count = 0, materialIndex = 0, visible = 0;
        if (RUsd_GetImportMeshInfo(context, i, primPath, sizeof(primPath), name, sizeof(name),
                                   &pointCount, &indexCount, &normalCount, &uv0Count,
                                   &materialIndex, &visible) != 0)
        {
            return false;
        }

        Mesh mesh;
        mesh.name = name;
        mesh.pointCount = pointCount;
        mesh.indexCount = indexCount;
        mesh.points.resize(static_cast<size_t>(pointCount));
        mesh.normals.resize(static_cast<size_t>(normalCount));
        mesh.uvSets[0].resize(static_cast<size_t>(uv0Count));

        std::vector<int> indices(static_cast<size_t>(indexCount));
        if (RUsd_CopyImportMesh(context, i,
                                mesh.points.data(), pointCount,
                                indices.data(), indexCount,
                                mesh.normals.data(), normalCount,
                                mesh.uvSets[0].data(), uv0Count) != 0)
        {
            return false;
        }

        for (int set = 1; set < RUSD_MAX_UV_SETS; ++set)
        {
            int uvCount = 0;
            if (RUsd_GetImportMeshUvSetInfo(context, i, set, &uvCount) != 0)
            {
                return false;
            }

            mesh.uvSets[set].resize(static_cast<size_t>(uvCount));
            if (uvCount > 0 &&
                RUsd_CopyImportMeshUvSet(context, i, set, mesh.uvSets[set].data(), uvCount) != 0)
            {
                return false;
            }
        }

        meshes->push_back(mesh);
    }

    return true;
}

const Mesh* Find(const std::vector<Mesh>& meshes, const std::string& name)
{
    for (const Mesh& mesh : meshes)
    {
        if (mesh.name == name)
        {
            return &mesh;
        }
    }

    return nullptr;
}

// Every non-empty attribute must be exactly one value per vertex, otherwise the managed side
// drops it (Mesh.SetUVs/SetNormals are 1:1 with the vertex buffer).
void CheckPerVertex(const Mesh& mesh)
{
    if (!mesh.normals.empty())
    {
        CheckEqual(static_cast<int>(mesh.normals.size()), mesh.pointCount, mesh.name + " normal count");
    }

    for (int set = 0; set < RUSD_MAX_UV_SETS; ++set)
    {
        if (!mesh.uvSets[set].empty())
        {
            CheckEqual(static_cast<int>(mesh.uvSets[set].size()), mesh.pointCount,
                       mesh.name + " uv" + std::to_string(set) + " count");
        }
    }
}

bool UvEquals(const RUsdVec2& uv, float u, float v)
{
    return std::fabs(uv.x - u) < 1e-5f && std::fabs(uv.y - v) < 1e-5f;
}

// Finds the vertex at a point position (X is negated by the USD->Unity basis flip) that carries
// the given UV, so assertions do not depend on the order vertices were emitted in.
bool HasVertexWithUv(const Mesh& mesh, float x, float y, float u, float v)
{
    for (size_t i = 0; i < mesh.points.size(); ++i)
    {
        if (std::fabs(mesh.points[i].x + x) < 1e-5f &&
            std::fabs(mesh.points[i].y - y) < 1e-5f &&
            i < mesh.uvSets[0].size() &&
            UvEquals(mesh.uvSets[0][i], u, v))
        {
            return true;
        }
    }

    return false;
}

} // namespace

int main(int argc, char** argv)
{
    const char* fixture = argc > 1 ? argv[1] : "import_uv_fixture.usda";
    std::printf("API %d, fixture %s\n", RUsd_GetApiVersion(), fixture);

    RUsdContext* context = nullptr;
    if (RUsd_OpenStage(fixture, 0, &context) != 0)
    {
        char error[4096] = {0};
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("Open failed: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    std::vector<Mesh> meshes;
    if (!ReadMeshes(context, &meshes))
    {
        char error[4096] = {0};
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("Read failed: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    CheckEqual(static_cast<int>(meshes.size()), 8, "mesh count");
    for (const Mesh& mesh : meshes)
    {
        CheckPerVertex(mesh);
    }

    // faceVarying + indexed: two quads share points 1 and 2, but the shared corners disagree on
    // their UV, so exactly those two points split (6 points -> 8 vertices).
    if (const Mesh* mesh = Find(meshes, "A_faceVarying_indexed"))
    {
        CheckEqual(mesh->pointCount, 8, "A vertex count (split)");
        CheckEqual(mesh->indexCount, 12, "A index count");
        Check(HasVertexWithUv(*mesh, 1.0f, 0.0f, 1.0f, 0.0f), "A point1 keeps uv (1,0)");
        Check(HasVertexWithUv(*mesh, 1.0f, 0.0f, 0.5f, 0.5f), "A point1 also carries uv (0.5,0.5)");
        Check(HasVertexWithUv(*mesh, 1.0f, 1.0f, 1.0f, 1.0f), "A point2 keeps uv (1,1)");
        Check(HasVertexWithUv(*mesh, 1.0f, 1.0f, 0.5f, 0.5f), "A point2 also carries uv (0.5,0.5)");
    }
    else
    {
        Check(false, "A_faceVarying_indexed present");
    }

    // Per-point data must stay on the unsplit path: same vertex count as USD points.
    if (const Mesh* mesh = Find(meshes, "B_vertex_plain"))
    {
        CheckEqual(mesh->pointCount, 4, "B vertex count (no split)");
        Check(HasVertexWithUv(*mesh, 1.0f, 1.0f, 1.0f, 1.0f), "B uv follows the point");
    }

    // Name aliases: "st" is the convention, but DCC round-trips emit "UVMap"/"uv"/"map1"/...
    if (const Mesh* mesh = Find(meshes, "C_alias_UVMap"))
    {
        CheckEqual(static_cast<int>(mesh->uvSets[0].size()), 4, "C uv0 from primvars:UVMap");
        Check(HasVertexWithUv(*mesh, 0.0f, 0.0f, 0.1f, 0.1f), "C uv value");
    }

    // st/st1/st2 -> Unity uv0/uv1/uv2 (API 3+).
    if (const Mesh* mesh = Find(meshes, "D_three_sets"))
    {
        CheckEqual(static_cast<int>(mesh->uvSets[0].size()), 4, "D uv0 count");
        CheckEqual(static_cast<int>(mesh->uvSets[1].size()), 4, "D uv1 count");
        CheckEqual(static_cast<int>(mesh->uvSets[2].size()), 4, "D uv2 count");
        Check(UvEquals(mesh->uvSets[1][1], 0.5f, 0.0f) || UvEquals(mesh->uvSets[1][2], 0.5f, 0.5f),
              "D uv1 values come from primvars:st1");
    }

    // Normals authored as a primvar rather than the schema attribute.
    if (const Mesh* mesh = Find(meshes, "E_primvar_normals"))
    {
        CheckEqual(static_cast<int>(mesh->normals.size()), 4, "E normals from primvars:normals");
    }

    // double2 values, and a set-0 alias that is not "st".
    if (const Mesh* mesh = Find(meshes, "F_double2_uv"))
    {
        CheckEqual(static_cast<int>(mesh->uvSets[0].size()), 4, "F uv0 from double2 primvars:uv");
        Check(HasVertexWithUv(*mesh, 1.0f, 1.0f, 1.0f, 1.0f), "F uv value converted to float");
    }

    // Indexed but vertex-interpolated: resolvable per point, so no split, values de-indexed.
    if (const Mesh* mesh = Find(meshes, "G_vertex_indexed"))
    {
        CheckEqual(mesh->pointCount, 4, "G vertex count (no split)");
        Check(HasVertexWithUv(*mesh, 1.0f, 0.0f, 1.0f, 1.0f), "G index table applied");
        Check(HasVertexWithUv(*mesh, 0.0f, 1.0f, 0.0f, 0.0f), "G index table applied (second)");
    }

    // No UVs at all: stays empty, and the mesh still imports.
    if (const Mesh* mesh = Find(meshes, "H_no_uv"))
    {
        CheckEqual(mesh->pointCount, 4, "H vertex count");
        CheckEqual(static_cast<int>(mesh->uvSets[0].size()), 0, "H has no uv0");
    }

    // Authored opacity: CutoutMat asks for a hard cutout at 0.5, BlendMat for alpha blending.
    // Both connect `opacity` to a texture's alpha channel; the file itself is never read here.
    int materialCount = 0;
    {
        char primPath[1024] = {0};
        double metersPerUnit = 0.0;
        int upAxis = 0, meshCount = 0;
        RUsd_GetImportInfo(context, primPath, sizeof(primPath), &metersPerUnit, &upAxis,
                           &meshCount, &materialCount);
    }

    bool sawCutout = false, sawBlend = false;
    for (int i = 0; i < materialCount; ++i)
    {
        RUsdMaterial material;
        char name[1024] = {0};
        if (RUsd_GetImportMaterial(context, i, &material, name, sizeof(name)) != 0)
        {
            continue;
        }

        float threshold = -1.0f;
        char opacityTexture[1024] = {0};
        CheckEqual(
            RUsd_GetImportMaterialOpacity(context, i, &threshold, opacityTexture, sizeof(opacityTexture)),
            0, "RUsd_GetImportMaterialOpacity succeeds");

        const std::string materialName = name;
        if (materialName.find("CutoutMat") != std::string::npos)
        {
            sawCutout = true;
            Check(std::string(opacityTexture) == "./fixture_cutout.png",
                  std::string("CutoutMat opacity texture path (got '") + opacityTexture + "')");
            Check(std::fabs(threshold - 0.5f) < 1e-5f,
                  "CutoutMat opacityThreshold is 0.5 (alpha cutout)");
        }
        else if (materialName.find("BlendMat") != std::string::npos)
        {
            sawBlend = true;
            Check(std::string(opacityTexture) == "./fixture_blend.png",
                  std::string("BlendMat opacity texture path (got '") + opacityTexture + "')");
            Check(threshold == 0.0f, "BlendMat opacityThreshold is 0 (alpha blend)");
        }
        else
        {
            Check(opacityTexture[0] == '\0',
                  materialName + " has no opacity texture");
        }
    }

    Check(sawCutout, "CutoutMat was imported");
    Check(sawBlend, "BlendMat was imported");

    // A connected texture input that yields no file path must be reported, not dropped in
    // silence: that silence is what made a mismatched plugin binary (every texture path empty)
    // look like a file problem.
    {
        std::vector<char> diagnostics(16384, 0);
        RUsd_GetDiagnostics(context, diagnostics.data(), static_cast<int>(diagnostics.size()));
        const std::string text = diagnostics.data();
        Check(text.find("USD import: material") != std::string::npos,
              "import warnings are reported through RUsd_GetDiagnostics");
        Check(text.find("BadConnectionMat") != std::string::npos &&
              text.find("not a UsdUVTexture") != std::string::npos,
              "the non-UsdUVTexture connection on BadConnectionMat is named in the warning");
        if (text.find("BadConnectionMat") == std::string::npos)
        {
            std::printf("  (diagnostics were: %s)\n", text.c_str());
        }
    }

    RUsd_Destroy(context);

    std::printf(failures == 0 ? "PASS\n" : "%d check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
