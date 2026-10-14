// Import regressions found by the conformance survey (2026-10-10), one block per finding:
//
// 1. Teapot colour: UsdPreviewSurface.diffuseColor fed by a UsdPrimvarReader_float3 reading
//    displayColor imported white. Each bound mesh must get its own primvar colour, and a mesh
//    without the primvar the reader's fallback.
// 2. Teapot shading: a mesh with no authored normals and faceVarying UVs was split per corner and
//    then flat-shaded by RecalculateNormals. It must arrive with smooth normals.
// 3. OpenChessSet: MaterialX standard_surface (mtlx render context) imported as "no surface
//    shader source". Constants, nodedef defaults and nodegraph textures must come through.
// 4. ALab: typeless and unknown-schema prims were dropped from the node table.
// 5. Vehicle Variants: a texture authored as @../textures/...@ in a material layer kept in a
//    sub-folder was handed to the host relative to that layer; resolved against the stage folder
//    it pointed outside it and was refused. It must come back relative to the stage folder.
//
// Needs a payload built with MaterialX (usdMtlx and its standard library under
// lib/usd/usdMtlx/resources). Build and run like import_uv_test (see REBUILD_WINDOWS_LINUX.md
// Gate 6), with material_import_fixture.usda and material_import_fixture.mtlx copied next to it:
//   material_import_test material_import_fixture.usda
// Prints PASS and exits 0, or lists the failed checks.

#include "unity_usd_toolkit_native.h"

#include <cmath>
#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace
{

int failures = 0;

void Check(bool condition, const std::string& what)
{
    std::printf("  %s %s\n", condition ? "ok  " : "FAIL", what.c_str());
    if (!condition)
    {
        ++failures;
    }
}

bool Near(float a, float b, float tolerance = 1e-3f)
{
    return std::fabs(a - b) <= tolerance;
}

bool EndsWith(const std::string& value, const std::string& suffix)
{
    return value.size() >= suffix.size() &&
        value.compare(value.size() - suffix.size(), suffix.size(), suffix) == 0;
}

struct Mesh
{
    int materialIndex = -1;
    std::vector<RUsdVec3> points;
    std::vector<RUsdVec3> normals;
};

std::map<std::string, Mesh> ReadMeshes(RUsdContext* context, int meshCount)
{
    std::map<std::string, Mesh> meshes;
    for (int i = 0; i < meshCount; ++i)
    {
        char path[1024] = {};
        char name[256] = {};
        int pointCount = 0, indexCount = 0, normalCount = 0, uvCount = 0, materialIndex = -1, visible = 0;
        if (RUsd_GetImportMeshInfo(context, i, path, sizeof(path), name, sizeof(name), &pointCount,
                                   &indexCount, &normalCount, &uvCount, &materialIndex, &visible) != 0)
        {
            continue;
        }

        Mesh mesh;
        mesh.materialIndex = materialIndex;
        mesh.points.resize(static_cast<size_t>(pointCount));
        mesh.normals.resize(static_cast<size_t>(normalCount));
        std::vector<int> indices(static_cast<size_t>(indexCount));
        std::vector<RUsdVec2> uv(static_cast<size_t>(uvCount));
        RUsd_CopyImportMesh(context, i, mesh.points.data(), pointCount, indices.data(), indexCount,
                            mesh.normals.empty() ? nullptr : mesh.normals.data(), normalCount,
                            uv.empty() ? nullptr : uv.data(), uvCount);
        meshes[path] = mesh;
    }

    return meshes;
}

bool ReadMaterial(RUsdContext* context, int index, RUsdMaterial* material)
{
    char name[1024] = {};
    return index >= 0 && RUsd_GetImportMaterial(context, index, material, name, sizeof(name)) == 0;
}

}  // namespace

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::printf("usage: material_import_test <material_import_fixture.usda>\n");
        return 2;
    }

    // The host's first call, as in Unity: it also points usdMtlx at the bundled standard library.
    std::printf("API %d, fixture %s\n", RUsd_GetApiVersion(), argv[1]);

    RUsdContext* context = nullptr;
    if (RUsd_OpenStage(argv[1], 1, &context) != 0)
    {
        char error[4096] = {};
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("FAIL RUsd_OpenStage: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    char defaultPrim[256] = {};
    double metersPerUnit = 0.0;
    int upAxis = 0, meshCount = 0, materialCount = 0;
    RUsd_GetImportInfo(context, defaultPrim, sizeof(defaultPrim), &metersPerUnit, &upAxis, &meshCount,
                       &materialCount);
    std::map<std::string, Mesh> meshes = ReadMeshes(context, meshCount);

    std::vector<char> diagnosticBuffer(32768, 0);
    RUsd_GetDiagnostics(context, diagnosticBuffer.data(), static_cast<int>(diagnosticBuffer.size()));
    const std::string diagnostics = diagnosticBuffer.data();

    std::printf("Primvar reader colour (Teapot)\n");
    {
        RUsdMaterial green = {}, blue = {}, fallback = {};
        const bool haveAll = meshes.count("/Fixture/GreenQuad") && meshes.count("/Fixture/BlueQuad") &&
            meshes.count("/Fixture/FallbackQuad");
        Check(haveAll, "the three primvar-colour meshes are imported");
        if (haveAll)
        {
            const int greenIndex = meshes["/Fixture/GreenQuad"].materialIndex;
            const int blueIndex = meshes["/Fixture/BlueQuad"].materialIndex;
            Check(ReadMaterial(context, greenIndex, &green) &&
                      Near(green.r, 0.325f) && Near(green.g, 0.825f) && Near(green.b, 0.0f),
                  "GreenQuad takes its displayColor (0.325, 0.825, 0)");
            Check(ReadMaterial(context, blueIndex, &blue) &&
                      Near(blue.r, 0.1f) && Near(blue.g, 0.2f) && Near(blue.b, 0.9f),
                  "BlueQuad takes its own displayColor (0.1, 0.2, 0.9)");
            Check(greenIndex != blueIndex, "the two colours are two materials");
            Check(Near(green.roughness, 0.4f), "the rest of the material is kept (roughness 0.4)");
            Check(ReadMaterial(context, meshes["/Fixture/FallbackQuad"].materialIndex, &fallback) &&
                      Near(fallback.r, 0.7f) && Near(fallback.g, 0.7f) && Near(fallback.b, 0.7f),
                  "a mesh without displayColor gets the reader's fallback (0.7)");
        }

        Check(diagnostics.find("PrimvarColorMat") == std::string::npos,
              "no warning for the primvar reader connection");
    }

    std::printf("Smooth normals for split vertices (Teapot)\n");
    {
        const Mesh& strip = meshes["/Fixture/FoldedStrip"];
        Check(!strip.normals.empty() && strip.normals.size() == strip.points.size(),
              "a mesh with no authored normals and faceVarying UVs gets normals");

        // The fold edge is USD x = 1, which is Unity x = -1 after the handedness flip. Its two
        // faces have normals +Y and -X (USD), so the smooth normal is (-0.707, 0.707, 0) in USD and
        // (0.707, 0.707, 0) in Unity, on every split copy of those points.
        int foldVertices = 0;
        bool allSmooth = !strip.normals.empty();
        for (size_t i = 0; i < strip.points.size() && i < strip.normals.size(); ++i)
        {
            if (Near(strip.points[i].x, -1.0f) && Near(strip.points[i].y, 0.0f))
            {
                ++foldVertices;
                allSmooth = allSmooth && Near(strip.normals[i].x, 0.7071f) &&
                    Near(strip.normals[i].y, 0.7071f) && Near(strip.normals[i].z, 0.0f);
            }
        }

        Check(foldVertices >= 4, "the fold edge is split into one vertex per corner (" +
                                     std::to_string(foldVertices) + ")");
        Check(allSmooth, "every split copy on the fold carries the same smooth normal");
    }

    std::printf("MaterialX standard_surface (OpenChessSet)\n");
    {
        RUsdMaterial piece = {}, board = {};
        const bool havePiece = meshes.count("/Fixture/Piece") &&
            ReadMaterial(context, meshes["/Fixture/Piece"].materialIndex, &piece);
        const bool haveBoard = meshes.count("/Fixture/Board") &&
            ReadMaterial(context, meshes["/Fixture/Board"].materialIndex, &board);
        Check(havePiece && haveBoard, "both MaterialX materials are imported");
        Check(diagnostics.find("has no surface shader source") == std::string::npos,
              "no material is reported as having no surface shader");

        Check(EndsWith(piece.albedoTexturePath, "piece_base_color.jpg"),
              std::string("base_color comes from the nodegraph image (") + piece.albedoTexturePath + ")");
        Check(EndsWith(piece.normalTexturePath, "piece_normal.jpg"),
              std::string("normal is found through the normalmap node (") + piece.normalTexturePath + ")");
        Check(piece.metallicTexturePath[0] == '\0',
              "a separate metalness image is not put in the packed metallic slot");
        Check(diagnostics.find("metalness/roughness textures") != std::string::npos,
              "that omission is reported");
        std::printf("       piece colour (nodedef base) = %.3f %.3f %.3f\n", piece.r, piece.g, piece.b);
        Check(piece.r > 0.0f && Near(piece.r, piece.g) && Near(piece.g, piece.b),
              "an unauthored base weight comes from the nodedef default");

        Check(Near(board.r, 0.4f) && Near(board.g, 0.2f) && Near(board.b, 0.1f),
              "Board colour is base_color x base = (0.4, 0.2, 0.1)");
        Check(Near(board.metallic, 0.3f) && Near(board.roughness, 0.6f),
              "Board metalness 0.3 and specular_roughness 0.6");
        Check(Near(board.emissive[0], 0.2f) && Near(board.emissive[1], 0.4f) && Near(board.emissive[2], 0.6f),
              "Board emission is emission_color x emission = (0.2, 0.4, 0.6)");
    }

    std::printf("Texture in a sub-folder material layer (Vehicle Variants)\n");
    {
        RUsdMaterial body = {};
        const bool haveBody = meshes.count("/Fixture/VehicleBody") &&
            ReadMaterial(context, meshes["/Fixture/VehicleBody"].materialIndex, &body);
        Check(haveBody && std::string(body.albedoTexturePath) == "material_import_assets/textures/body.png",
              std::string("@../textures@ from materials/ comes back relative to the stage folder (") +
                  body.albedoTexturePath + ")");
    }

    std::printf("Typeless and unknown-schema prims (ALab)\n");
    {
        std::map<std::string, int> nodes;
        int nodeCount = 0;
        RUsd_GetImportNodeCount(context, &nodeCount);
        for (int i = 0; i < nodeCount; ++i)
        {
            char path[1024] = {};
            RUsdMatrix4x4 matrix = {};
            int visible = 0;
            RUsd_GetImportNodeInfo(context, i, path, sizeof(path), &matrix, &visible);
            nodes[path] = i;
        }

        Check(nodes.count("/Fixture/TypelessRig") == 1, "a typeless prim is a node");
        Check(nodes.count("/Fixture/TypelessRig/Lamp") == 1, "its Xform child is a node");
        Check(nodes.count("/Fixture/UnknownSchemaPrim") == 1, "a prim of an unknown schema is a node");
        Check(nodes.count("/Fixture/Mtlx/Materials") == 0,
              "usdMtlx's typeless Materials container is not a node");
        Check(nodes.count("/Fixture/Looks/PrimvarColorMat") == 0, "a Material is not a node");
    }

    if (failures != 0)
    {
        std::printf("  (diagnostics were: %s)\n", diagnostics.c_str());
    }

    RUsd_Destroy(context);
    std::printf(failures == 0 ? "PASS\n" : "%d check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
