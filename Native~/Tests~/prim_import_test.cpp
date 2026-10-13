// Prim kinds the conformance survey found missing, doubled or split (2026-10-10), one block per
// roadmap item:
//
//  2  purpose: proxy and guide geometry was imported next to the render geometry (ALab).
//  6  PointInstancer: instances were not expanded (OpenChessSet's pawns, Moore Lane's forest).
// 24  ...and its prototypes were imported where they stand, overlapping the instances (AGV Fleet).
//  7  Cube/Sphere/Cylinder/Cone/Capsule/Plane arrived as empty GameObjects.
// 25  a non-indexed faceVarying primvar split every corner even where the values agree.
// 21  a subdivision surface is imported as its cage; that is now said once, when authored.
//
// Build and run like import_uv_test (REBUILD_WINDOWS_LINUX.md Gate 6), with
// prim_import_fixture.usda next to it:  prim_import_test prim_import_fixture.usda
// Prints PASS and exits 0, or lists the failed checks.

#include "unity_usd_toolkit_native.h"

#include <cfloat>
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

struct Mesh
{
    std::vector<RUsdVec3> points;
    std::vector<RUsdVec3> normals;
    int indexCount = 0;
    int index = -1;
    int visible = 1;
};

struct Bounds
{
    float min[3] = {FLT_MAX, FLT_MAX, FLT_MAX};
    float max[3] = {-FLT_MAX, -FLT_MAX, -FLT_MAX};
    float Size(int axis) const { return max[axis] - min[axis]; }
};

Bounds BoundsOf(const Mesh& mesh)
{
    Bounds b;
    for (const RUsdVec3& p : mesh.points)
    {
        const float v[3] = {p.x, p.y, p.z};
        for (int a = 0; a < 3; ++a)
        {
            b.min[a] = std::fmin(b.min[a], v[a]);
            b.max[a] = std::fmax(b.max[a], v[a]);
        }
    }

    return b;
}

bool Near(float a, float b, float tolerance = 1e-3f)
{
    return std::fabs(a - b) <= tolerance;
}

}  // namespace

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::printf("usage: prim_import_test <prim_import_fixture.usda>\n");
        return 2;
    }

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

    std::map<std::string, Mesh> meshes;
    for (int i = 0; i < meshCount; ++i)
    {
        char path[1024] = {};
        char name[256] = {};
        int pointCount = 0, indexCount = 0, normalCount = 0, uvCount = 0, materialIndex = -1, visible = 0;
        RUsd_GetImportMeshInfo(context, i, path, sizeof(path), name, sizeof(name), &pointCount, &indexCount,
                               &normalCount, &uvCount, &materialIndex, &visible);
        Mesh mesh;
        mesh.index = i;
        mesh.visible = visible;
        mesh.points.resize(static_cast<size_t>(pointCount));
        mesh.normals.resize(static_cast<size_t>(normalCount));
        mesh.indexCount = indexCount;
        std::vector<int> indices(static_cast<size_t>(indexCount));
        std::vector<RUsdVec2> uv(static_cast<size_t>(uvCount));
        RUsd_CopyImportMesh(context, i, mesh.points.data(), pointCount, indices.data(), indexCount,
                            mesh.normals.empty() ? nullptr : mesh.normals.data(), normalCount,
                            uv.empty() ? nullptr : uv.data(), uvCount);
        meshes[path] = mesh;
    }

    std::map<std::string, RUsdMatrix4x4> nodes;
    std::map<std::string, int> nodeIndices;
    std::map<std::string, int> nodeVisible;
    int nodeCount = 0;
    RUsd_GetImportNodeCount(context, &nodeCount);
    for (int i = 0; i < nodeCount; ++i)
    {
        char path[1024] = {};
        RUsdMatrix4x4 matrix = {};
        int visible = 0;
        RUsd_GetImportNodeInfo(context, i, path, sizeof(path), &matrix, &visible);
        nodes[path] = matrix;
        nodeIndices[path] = i;
        nodeVisible[path] = visible;
    }

    std::vector<char> diagnosticBuffer(32768, 0);
    RUsd_GetDiagnostics(context, diagnosticBuffer.data(), static_cast<int>(diagnosticBuffer.size()));
    const std::string diagnostics = diagnosticBuffer.data();

    std::printf("Purpose (item 2)\n");
    Check(meshes.count("/Fixture/Purpose/RenderMesh") == 1, "render-purpose mesh is imported");
    Check(meshes.count("/Fixture/Purpose/ProxyMesh") == 0, "proxy-purpose mesh is not");
    Check(meshes.count("/Fixture/Purpose/Guides/GuideChild") == 0, "a mesh under a guide Xform is not");

    std::printf("PointInstancer (items 6, 24; API 8)\n");
    // Since API 8 the instances are not expanded into prims: the prototype is imported once,
    // hidden, and its meshes come with one matrix per instance (RUsd_GetImportInstancedMesh).
    Check(meshes.count("/Fixture/Pawns/instance_0/Pawn/Body") == 0, "instances are not expanded into prims");
    Check(meshes.count("/Fixture/Pawns/Prototypes/Pawn/Body") == 0, "the prototype is not imported in place");
    const auto proto = meshes.find("/Fixture/Pawns/prototype_0_Pawn/Pawn/Body");
    Check(proto != meshes.end(), "the prototype is imported once under the instancer");
    // The copy's mesh stays readable (invisible meshes are not read since b48013a); it is the
    // node carrying the copy that is hidden, so nothing draws in place.
    const auto protoNode = nodeVisible.find("/Fixture/Pawns/prototype_0_Pawn");
    Check(protoNode != nodeVisible.end() && protoNode->second == 0, "the prototype copy's node is hidden");
    int instancedCount = 0;
    RUsd_GetImportInstancedMeshCount(context, &instancedCount);
    Check(instancedCount == 1, "one instanced record: the prototype's one mesh");
    RUsdImportInstancedMesh instanced = {};
    if (instancedCount >= 1)
    {
        RUsd_GetImportInstancedMesh(context, 0, &instanced);
    }
    Check(proto != meshes.end() && instanced.meshIndex == proto->second.index, "the record points at the prototype mesh");
    Check(instanced.instanceCount == 2, "invisibleIds hides instance 1 (two of three remain)");
    const auto instancer = nodeIndices.find("/Fixture/Pawns");
    Check(instancer != nodeIndices.end() && instanced.instancerNodeIndex == instancer->second,
          "the matrices are relative to the instancer node");
    // Row-major, USD row-vector (translation in row 3) before the handedness conversion. The
    // surviving instances are 0 and 2, at x = 10 and x = 30.
    std::vector<float> matrices(static_cast<size_t>(instanced.instanceCount) * 16, 0.0f);
    if (!matrices.empty())
    {
        RUsd_CopyImportInstanceMatrices(context, 0, matrices.data(), static_cast<int>(matrices.size()));
    }
    Check(matrices.size() == 32 && Near(matrices[12], 10.0f) && Near(matrices[28], 30.0f),
          "instances 0 and 2 sit at their positions x = 10 and x = 30");
    Check(matrices.size() == 32 && Near(matrices[13], 0.5f) && Near(matrices[29], 0.5f),
          "the prototype root keeps its own transform below the instance");

    std::printf("Implicit surfaces (item 7)\n");
    for (const char* name : {"Cube", "Sphere", "CylinderX", "Cone", "CapsuleY", "Plane"})
    {
        const std::string path = std::string("/Fixture/Shapes/") + name;
        const auto found = meshes.find(path);
        Check(found != meshes.end() && found->second.indexCount > 0 &&
                  found->second.normals.size() == found->second.points.size(),
              std::string(name) + " is a mesh with normals");
    }

    if (meshes.count("/Fixture/Shapes/Cube"))
    {
        const Bounds b = BoundsOf(meshes["/Fixture/Shapes/Cube"]);
        Check(Near(b.Size(0), 2) && Near(b.Size(1), 2) && Near(b.Size(2), 2), "Cube size 2 is 2 wide on every axis");
    }

    if (meshes.count("/Fixture/Shapes/CylinderX"))
    {
        const Bounds b = BoundsOf(meshes["/Fixture/Shapes/CylinderX"]);
        Check(Near(b.Size(0), 4) && Near(b.Size(1), 1, 0.01f), "CylinderX runs along X (height 4, diameter 1)");
    }

    if (meshes.count("/Fixture/Shapes/CapsuleY"))
    {
        const Bounds b = BoundsOf(meshes["/Fixture/Shapes/CapsuleY"]);
        Check(Near(b.Size(1), 2, 0.01f) && Near(b.Size(0), 1, 0.01f), "CapsuleY spans height + 2r = 2 along Y");
    }

    if (meshes.count("/Fixture/Shapes/Sphere"))
    {
        // Outward normals: every normal points away from the centre.
        const Mesh& sphere = meshes["/Fixture/Shapes/Sphere"];
        bool outward = true;
        for (size_t i = 0; i < sphere.points.size(); ++i)
        {
            const RUsdVec3& p = sphere.points[i];
            const RUsdVec3& n = sphere.normals[i];
            if (p.x * n.x + p.y * n.y + p.z * n.z < 0.5f)
            {
                outward = false;
            }
        }

        Check(outward, "Sphere normals point outward");
    }

    std::printf("faceVarying split (item 25)\n");
    if (meshes.count("/Fixture/SharedUvStrip"))
    {
        Check(meshes["/Fixture/SharedUvStrip"].points.size() == 6,
              "two quads with matching UVs on their shared edge keep 6 vertices (got " +
                  std::to_string(meshes["/Fixture/SharedUvStrip"].points.size()) + ")");
    }
    else
    {
        Check(false, "SharedUvStrip is imported");
    }

    std::printf("Lights, cameras, spatial audio (items 9, 23; API 7)\n");
    {
        std::map<std::string, int> nodeIndexByPath;
        for (int i = 0; i < nodeCount; ++i)
        {
            char path[1024] = {};
            RUsdMatrix4x4 matrix = {};
            int visible = 0;
            RUsd_GetImportNodeInfo(context, i, path, sizeof(path), &matrix, &visible);
            nodeIndexByPath[path] = i;
        }

        int lightCount = 0, cameraCount = 0, audioCount = 0;
        RUsd_GetImportLightCount(context, &lightCount);
        RUsd_GetImportCameraCount(context, &cameraCount);
        RUsd_GetImportAudioCount(context, &audioCount);
        Check(lightCount == 3 && cameraCount == 1 && audioCount == 1,
              "3 lights, 1 camera, 1 spatial audio (got " + std::to_string(lightCount) + ", " +
                  std::to_string(cameraCount) + ", " + std::to_string(audioCount) + ")");

        for (int i = 0; i < lightCount; ++i)
        {
            RUsdImportLight light = {};
            char texture[512] = {};
            RUsd_GetImportLight(context, i, &light, texture, sizeof(texture));
            if (light.nodeIndex == nodeIndexByPath["/Fixture/Components/Sun"])
            {
                Check(light.type == RUSD_LIGHT_DISTANT && Near(light.intensity, 1000.0f) &&
                          Near(light.color[1], 0.9f) && Near(light.angle, 1.0f),
                      "Sun: distant, intensity 500 x 2^1 = 1000, its colour and angle");
            }
            else if (light.nodeIndex == nodeIndexByPath["/Fixture/Components/Spot"])
            {
                Check(light.type == RUSD_LIGHT_SPHERE && Near(light.radius, 0.1f) &&
                          Near(light.coneAngle, 30.0f) && Near(light.coneSoftness, 0.2f),
                      "Spot: sphere light radius 0.1 shaped to a 30 degree cone");
            }
            else if (light.nodeIndex == nodeIndexByPath["/Fixture/Components/Sky"])
            {
                // Authored relative to the root layer, so it is passed on as authored.
                Check(light.type == RUSD_LIGHT_DOME &&
                          std::string(texture) == "./material_import_assets/textures/body.png",
                      std::string("Sky: dome light with its texture (") + texture + ")");
            }
            else
            {
                Check(false, "light " + std::to_string(i) + " belongs to a known node");
            }
        }

        if (cameraCount == 1)
        {
            RUsdImportCamera camera = {};
            RUsd_GetImportCamera(context, 0, &camera);
            Check(camera.nodeIndex == nodeIndexByPath["/Fixture/Components/Shot"] &&
                      camera.projection == RUSD_CAMERA_PERSPECTIVE && Near(camera.focalLength, 24.0f) &&
                      Near(camera.verticalAperture, 20.0f) && Near(camera.nearClip, 0.1f) &&
                      Near(camera.farClip, 500.0f),
                  "Shot: perspective, focal 24, vertical aperture 20, clipping 0.1 to 500");
        }

        if (audioCount == 1)
        {
            RUsdImportAudio audio = {};
            char file[512] = {};
            RUsd_GetImportAudio(context, 0, &audio, file, sizeof(file));
            Check(audio.nodeIndex == nodeIndexByPath["/Fixture/Components/Hum"] &&
                      audio.auralMode == RUSD_AUDIO_SPATIAL && audio.loop == 1 && Near(audio.gain, 0.5f) &&
                      std::string(file).find("hum.wav") != std::string::npos,
                  std::string("Hum: spatial, looping, gain 0.5, ") + file);
        }
    }

    std::printf("Subdivision (item 21)\n");
    Check(diagnostics.find("SubdivMesh' is a catmullClark subdivision surface") != std::string::npos,
          "an authored catmullClark scheme is reported");
    Check(diagnostics.find("PolygonMesh' is a") == std::string::npos &&
              diagnostics.find("RenderMesh' is a") == std::string::npos,
          "scheme none and an unauthored scheme are not");

    if (failures != 0)
    {
        std::printf("  (diagnostics were: %s)\n", diagnostics.c_str());
    }

    RUsd_Destroy(context);
    std::printf(failures == 0 ? "PASS\n" : "%d check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
