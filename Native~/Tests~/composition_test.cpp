// Composition coverage (API v6): reading a composed stage with variant and payload control, and
// writing layers that keep composition -- a sparse override layer over an existing stage, internal
// and external references, instanceable prototypes and variant selections.
//
// Build and run (macOS; adjust the payload directory for Windows/Linux):
//   PLUGINS=../../Runtime/Plugins/macOS
//   clang++ -std=c++17 -Wl,-headerpad_max_install_names composition_test.cpp \
//       -I ../include "$PLUGINS/UnityUSDToolkitNative.dylib" -o composition_test
//   install_name_tool -change "@loader_path/UnityUSDToolkitNative.dylib" \
//       "$(cd "$PLUGINS" && pwd)/UnityUSDToolkitNative.dylib" composition_test
//   PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd" ./composition_test <work-dir>
//
// The work directory receives the fixture and every layer the test writes; it is left in place
// so the output can be inspected with usdcat.

#include "unity_usd_toolkit_native.h"

#include <cmath>
#include <cstdio>
#include <fstream>
#include <map>
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

std::string LastError(RUsdContext* context)
{
    char buffer[4096] = {};
    RUsd_GetLastError(context, buffer, sizeof(buffer));
    return buffer;
}

bool Ok(RUsdContext* context, int result, const std::string& what)
{
    if (result != 0)
    {
        std::printf("  FAIL %s: %s\n", what.c_str(), LastError(context).c_str());
        ++failures;
        return false;
    }

    return true;
}

// The source stage: a variant set that swaps a child, an instanced part used twice, a payload,
// and materials under the default prim.
const char* kFixture = R"(#usda 1.0
(
    defaultPrim = "World"
    metersPerUnit = 0.01
    upAxis = "Z"
)

def Xform "World"
{
    def Xform "Robot" (
        variants = {
            string color = "blue"
        }
        prepend variantSets = "color"
    )
    {
        double3 xformOp:translate = (1, 2, 3)
        uniform token[] xformOpOrder = ["xformOp:translate"]

        variantSet "color" = {
            "blue" {
                def Xform "BlueArm" {}
            }
            "red" {
                def Xform "RedArm" {}
            }
        }
    }

    def Xform "PartA" (
        instanceable = true
        prepend references = </Parts/Bolt>
    )
    {
        double3 xformOp:translate = (5, 0, 0)
        uniform token[] xformOpOrder = ["xformOp:translate"]
    }

    def Xform "PartB" (
        instanceable = true
        prepend references = </Parts/Bolt>
    )
    {
    }

    def Xform "Heavy" (
        prepend payload = @./heavy.usda@
    )
    {
    }
}

class "Parts"
{
    def Xform "Bolt"
    {
        def Xform "Head" {}
    }
}
)";

const char* kHeavy = R"(#usda 1.0
(
    defaultPrim = "Heavy"
)

def Xform "Heavy"
{
    def Xform "Inside" {}
}
)";

void WriteFile(const std::string& path, const char* text)
{
    std::ofstream file(path, std::ios::binary);
    file << text;
}

std::string ReadFile(const std::string& path)
{
    std::ifstream file(path, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
}

struct Node
{
    RUsdMatrix4x4 matrix {};
    int visible = 0;
    int flags = 0;
};

std::map<std::string, Node> ReadNodes(RUsdContext* context)
{
    std::map<std::string, Node> nodes;
    int count = 0;
    RUsd_GetImportNodeCount(context, &count);
    for (int i = 0; i < count; ++i)
    {
        char path[1024] = {};
        Node node;
        RUsd_GetImportNodeInfo(context, i, path, sizeof(path), &node.matrix, &node.visible);
        RUsd_GetImportNodeFlags(context, i, &node.flags);
        nodes[path] = node;
    }

    return nodes;
}

std::string ReadComposition(RUsdContext* context)
{
    int required = 0;
    if (RUsd_GetImportComposition(context, nullptr, 0, &required) != 0 || required <= 0)
    {
        return std::string();
    }

    std::vector<char> buffer(static_cast<size_t>(required));
    if (RUsd_GetImportComposition(context, buffer.data(), required, &required) != 0)
    {
        return std::string();
    }

    return std::string(buffer.data());
}

RUsdContext* Open(const std::string& path, int flags, const char* variants, const char* loads)
{
    RUsdContext* context = nullptr;
    if (RUsd_OpenStageEx(path.c_str(), flags, variants, loads, &context) != 0)
    {
        std::printf("  FAIL open %s: %s\n", path.c_str(), LastError(context).c_str());
        ++failures;
        RUsd_Destroy(context);
        return nullptr;
    }

    return context;
}

RUsdMatrix4x4 Translation(double x, double y, double z)
{
    RUsdMatrix4x4 m {};
    m.m00 = m.m11 = m.m22 = m.m33 = 1.0;
    m.m30 = x;
    m.m31 = y;
    m.m32 = z;
    return m;
}

bool IsTranslation(const RUsdMatrix4x4& m, double x, double y, double z)
{
    return std::fabs(m.m30 - x) < 1e-9 && std::fabs(m.m31 - y) < 1e-9 && std::fabs(m.m32 - z) < 1e-9;
}

void TestImport(const std::string& dir)
{
    std::printf("import: variants, payloads, flags, report\n");
    const std::string fixture = dir + "/scene.usda";

    RUsdContext* context = Open(fixture, 0, "", "");
    if (context == nullptr)
    {
        return;
    }

    std::map<std::string, Node> nodes = ReadNodes(context);
    Check(nodes.count("/World/Robot/BlueArm") == 1, "default variant composes BlueArm");
    Check(nodes.count("/World/Robot/RedArm") == 0, "default variant leaves RedArm out");
    Check(nodes.count("/World/Heavy/Inside") == 1, "payload loads by default");
    Check(nodes.count("/World/PartA/Head") == 1, "instance proxies are traversed");
    Check((nodes["/World/PartA"].flags & RUSD_NODE_INSTANCE) != 0, "PartA is flagged as an instance");
    Check((nodes["/World/PartA/Head"].flags & RUSD_NODE_INSTANCE_PROXY) != 0, "PartA/Head is flagged as a proxy");
    Check((nodes["/World/Robot"].flags & RUSD_NODE_VARIANT_SETS) != 0, "Robot is flagged with variant sets");
    Check((nodes["/World/Heavy"].flags & RUSD_NODE_PAYLOAD) != 0, "Heavy is flagged with a payload");
    Check((nodes["/World/Heavy"].flags & RUSD_NODE_UNLOADED) == 0, "Heavy is loaded");
    Check(IsTranslation(nodes["/World/Robot"].matrix, 1, 2, 3), "Robot keeps its translate");

    const std::string report = ReadComposition(context);
    Check(report.find("V\t/World/Robot\tcolor\tblue\tblue\tred\n") != std::string::npos, "report lists the color variant set");
    Check(report.find("P\t/World/Heavy\t1\n") != std::string::npos, "report lists the loaded payload");
    Check(report.find("I\t/World/PartA\n") != std::string::npos, "report lists the instance");
    Check(report.find("A\t/World/PartA\treference\t") != std::string::npos, "report lists PartA's reference");
    Check(report.find("A\t/World/Heavy\tpayload\t") != std::string::npos, "report lists Heavy's payload");
    RUsd_Destroy(context);

    context = Open(fixture, RUSD_OPEN_LOAD_NO_PAYLOADS, "/World/Robot\tcolor\tred\n", "");
    if (context == nullptr)
    {
        return;
    }

    nodes = ReadNodes(context);
    Check(nodes.count("/World/Robot/RedArm") == 1, "selected variant composes RedArm");
    Check(nodes.count("/World/Robot/BlueArm") == 0, "selected variant drops BlueArm");
    Check(nodes.count("/World/Heavy") == 1, "unloaded payload keeps its placeholder node");
    Check(nodes.count("/World/Heavy/Inside") == 0, "unloaded payload brings no children");
    Check((nodes["/World/Heavy"].flags & RUSD_NODE_UNLOADED) != 0, "Heavy is flagged unloaded");
    Check(ReadComposition(context).find("P\t/World/Heavy\t0\n") != std::string::npos, "report shows the payload unloaded");
    RUsd_Destroy(context);

    context = Open(fixture, RUSD_OPEN_LOAD_NO_PAYLOADS, "", "/World/Heavy\n");
    if (context == nullptr)
    {
        return;
    }

    nodes = ReadNodes(context);
    Check(nodes.count("/World/Heavy/Inside") == 1, "listed payload is loaded");
    RUsd_Destroy(context);

    context = Open(fixture, 0, "/World/Robot\tcolor\tgreen\n/World/Nope\tx\ty\n", "");
    if (context != nullptr)
    {
        nodes = ReadNodes(context);
        Check(nodes.count("/World/Robot/BlueArm") == 1, "an unknown variant falls back to the authored one");
        char diagnostics[8192] = {};
        RUsd_GetDiagnostics(context, diagnostics, sizeof(diagnostics));
        Check(std::string(diagnostics).find("variant selection skipped") != std::string::npos, "unknown variants are reported");
        RUsd_Destroy(context);
    }
}

// Writes a sparse layer over the fixture, then reopens it and checks the composed result.
void TestOverrideLayer(const std::string& dir)
{
    std::printf("export: override layer over the source stage\n");
    const std::string output = dir + "/edited.usda";
    RUsdContext* context = nullptr;
    if (!Ok(context, RUsd_BeginLayerExport(output.c_str(), "/World", "/World/UnityLooks", 0.01, 2,
            "./scene.usda\n", 0, &context), "begin override layer"))
    {
        RUsd_Destroy(context);
        return;
    }

    RUsdMatrix4x4 moved = Translation(10, 20, 30);
    Ok(context, RUsd_SetLocalTransform(context, "/World/Robot", &moved), "move Robot");
    Ok(context, RUsd_SetVariantSelection(context, "/World/Robot", "color", "red"), "select red");
    Ok(context, RUsd_SetVisibility(context, "/World/PartB", 0), "hide PartB");
    // Editing inside an instance: clear instanceable first, then override the proxy's prim.
    Ok(context, RUsd_SetInstanceable(context, "/World/PartA", 0), "uninstance PartA");
    RUsdMatrix4x4 headMove = Translation(0, 0, 7);
    Ok(context, RUsd_SetLocalTransform(context, "/World/PartA/Head", &headMove), "move PartA/Head");
    Ok(context, RUsd_SetActive(context, "/World/Heavy", 0), "deactivate Heavy");

    int exists = 0;
    Ok(context, RUsd_PrimExists(context, "/World/Robot", &exists), "query Robot");
    Check(exists == 1, "the sublayer's prims are visible to the export stage");
    Ok(context, RUsd_PrimExists(context, "/World/Nothing", &exists), "query a missing prim");
    Check(exists == 0, "a missing prim is reported missing");

    // A new object under an existing prim, with geometry, so materials land in the scope given.
    RUsdMatrix4x4 identity = Translation(0, 0, 0);
    Ok(context, RUsd_AddXform(context, "/World/Robot/NewThing", &identity, 1), "add NewThing");
    const RUsdVec3 points[] = {{0, 0, 0}, {1, 0, 0}, {0, 1, 0}};
    const int indices[] = {0, 1, 2};
    RUsdMaterial material {};
    material.r = 1.0f;
    material.a = 1.0f;
    material.roughness = 0.5f;
    material.uvScale[0] = material.uvScale[1] = 1.0f;
    Ok(context, RUsd_AddMeshEx(context, "/World/Robot/NewThing/NewThing_Mesh", points, 3, indices, 3, nullptr, 0,
        nullptr, 0, nullptr, 0, &material, 1, 1, 1), "add NewThing mesh");
    Ok(context, RUsd_EndExport(context), "save override layer");
    RUsd_Destroy(context);

    const std::string text = ReadFile(output);
    Check(text.find("subLayers = [") != std::string::npos && text.find("@./scene.usda@") != std::string::npos,
        "the layer sublayers the source with the path as given");
    Check(text.find("def Xform \"World\"") == std::string::npos, "the default prim is not redefined");
    Check(text.find("over \"Robot\"") != std::string::npos, "Robot is an over");
    Check(text.find("BlueArm") == std::string::npos, "untouched prims are not written");
    Check(text.find("metersPerUnit = 0.01") != std::string::npos, "metersPerUnit follows the source");
    Check(text.find("upAxis = \"Z\"") != std::string::npos, "upAxis follows the source");

    context = Open(output, 0, "", "");
    if (context == nullptr)
    {
        return;
    }

    std::map<std::string, Node> nodes = ReadNodes(context);
    Check(IsTranslation(nodes["/World/Robot"].matrix, 10, 20, 30), "Robot's override transform wins");
    Check(nodes.count("/World/Robot/RedArm") == 1 && nodes.count("/World/Robot/BlueArm") == 0, "variant selection override applies");
    Check(nodes["/World/PartB"].visible == 0, "PartB is hidden");
    Check(nodes["/World/PartB/Head"].visible == 0, "PartB's children inherit invisibility");
    Check((nodes["/World/PartA"].flags & RUSD_NODE_INSTANCE) == 0, "PartA is no longer an instance");
    Check(IsTranslation(nodes["/World/PartA/Head"].matrix, 0, 0, 7), "the edit inside the former instance applies");
    Check((nodes["/World/PartB"].flags & RUSD_NODE_INSTANCE) != 0, "PartB stays instanced");
    Check(IsTranslation(nodes["/World/PartB/Head"].matrix, 0, 0, 0), "PartB's head is untouched");
    Check(nodes.count("/World/Heavy") == 0, "Heavy is deactivated");
    Check(nodes.count("/World/Robot/NewThing/NewThing_Mesh") == 1, "the new mesh composes under Robot");

    int meshCount = 0;
    int materialCount = 0;
    RUsd_GetImportInfo(context, nullptr, 0, nullptr, nullptr, &meshCount, &materialCount);
    Check(meshCount == 1, "exactly the new mesh is a mesh");
    RUsdMaterial imported {};
    char materialName[1024] = {};
    if (materialCount == 1 && RUsd_GetImportMaterial(context, 0, &imported, materialName, sizeof(materialName)) == 0)
    {
        Check(std::string(materialName).rfind("/World/UnityLooks/", 0) == 0, "materials go to the scope given");
    }
    else
    {
        Check(false, "the new mesh has its material");
    }

    RUsd_Destroy(context);
}

// Writes a scene whose objects reference shared content: an internal class prototype that two
// instances reference, and an external asset file referenced by default prim.
void TestReferences(const std::string& dir)
{
    std::printf("export: prototypes and asset references\n");
    const RUsdVec3 points[] = {{0, 0, 0}, {1, 0, 0}, {0, 1, 0}};
    const int indices[] = {0, 1, 2};
    RUsdMatrix4x4 identity = Translation(0, 0, 0);

    const std::string assetPath = dir + "/scene_assets/Chair.usda";
    RUsdContext* asset = nullptr;
    if (Ok(asset, RUsd_BeginLayerExport(assetPath.c_str(), "/Chair", "", 1.0, 1, "",
            RUSD_LAYER_DEFINE_DEFAULT_PRIM, &asset), "begin asset"))
    {
        Ok(asset, RUsd_AddXform(asset, "/Chair/Seat", &identity, 1), "asset seat");
        Ok(asset, RUsd_AddMeshEx(asset, "/Chair/Seat/Seat_Mesh", points, 3, indices, 3, nullptr, 0, nullptr, 0,
            nullptr, 0, nullptr, 0, 1, 1), "asset seat mesh");
        Ok(asset, RUsd_EndExport(asset), "save asset");
    }

    RUsd_Destroy(asset);

    const std::string output = dir + "/assembled.usda";
    RUsdContext* context = nullptr;
    if (!Ok(context, RUsd_BeginLayerExport(output.c_str(), "/Root", "", 1.0, 1, "",
            RUSD_LAYER_DEFINE_DEFAULT_PRIM, &context), "begin assembled"))
    {
        RUsd_Destroy(context);
        return;
    }

    Ok(context, RUsd_DefinePrim(context, "/UnityPrototypes", "", RUSD_SPECIFIER_CLASS), "prototype scope");
    Ok(context, RUsd_AddXform(context, "/UnityPrototypes/Box", &identity, 1), "prototype xform");
    Ok(context, RUsd_AddMeshEx(context, "/UnityPrototypes/Box/Geom", points, 3, indices, 3, nullptr, 0, nullptr, 0,
        nullptr, 0, nullptr, 0, 1, 1), "prototype mesh");

    for (const char* name : {"/Root/BoxA", "/Root/BoxB"})
    {
        RUsdMatrix4x4 placed = Translation(name[9] == 'A' ? 1 : 2, 0, 0);
        Ok(context, RUsd_AddXform(context, name, &placed, 1), std::string("add ") + name);
        Ok(context, RUsd_AddReference(context, name, "", "/UnityPrototypes/Box", RUSD_ARC_REFERENCE), "reference box");
        Ok(context, RUsd_SetInstanceable(context, name, 1), "instance box");
    }

    Ok(context, RUsd_AddXform(context, "/Root/Chair_1", &identity, 1), "add chair");
    Ok(context, RUsd_AddReference(context, "/Root/Chair_1", "./scene_assets/Chair.usda", "", RUSD_ARC_REFERENCE),
        "reference chair");
    // A prefab instance override: hide the seat that comes through the reference.
    Ok(context, RUsd_SetVisibility(context, "/Root/Chair_1/Seat", 0), "override the chair seat");
    Ok(context, RUsd_AddXform(context, "/Root/Chair_2", &identity, 1), "add chair 2");
    Ok(context, RUsd_AddReference(context, "/Root/Chair_2", "./scene_assets/Chair.usda", "", RUSD_ARC_PAYLOAD),
        "payload chair");

    Check(RUsd_AddReference(context, "/Root/Bad", "", "", RUSD_ARC_REFERENCE) != 0, "an internal reference with no target is refused");
    Check(RUsd_DefinePrim(context, "Relative/Path", "", RUSD_SPECIFIER_DEF) != 0, "a relative prim path is refused");
    Ok(context, RUsd_EndExport(context), "save assembled");
    RUsd_Destroy(context);

    context = Open(output, 0, "", "");
    if (context == nullptr)
    {
        return;
    }

    std::map<std::string, Node> nodes = ReadNodes(context);
    Check(nodes.count("/UnityPrototypes/Box") == 0, "class prototypes are not imported themselves");
    Check(nodes.count("/Root/BoxA/Geom") == 1 && nodes.count("/Root/BoxB/Geom") == 1, "both instances bring the geometry");
    Check((nodes["/Root/BoxA"].flags & RUSD_NODE_INSTANCE) != 0, "BoxA is an instance");
    Check(IsTranslation(nodes["/Root/BoxB"].matrix, 2, 0, 0), "BoxB keeps its placement");
    Check(nodes.count("/Root/Chair_1/Seat/Seat_Mesh") == 1, "the referenced asset composes");
    Check(nodes["/Root/Chair_1/Seat"].visible == 0, "the instance override hides the seat");
    Check(nodes.count("/Root/Chair_2/Seat/Seat_Mesh") == 1 && nodes["/Root/Chair_2/Seat"].visible == 1, "the payload composes, unaltered");
    // Invisible geometry is not read since b48013a (Hydra does not populate it either), so the
    // hidden seat of Chair_1 keeps its node but contributes no mesh.
    int meshCount = 0;
    RUsd_GetImportInfo(context, nullptr, 0, nullptr, nullptr, &meshCount, nullptr);
    Check(meshCount == 3, "three meshes compose (two boxes, one visible chair seat)");
    RUsd_Destroy(context);

    // The usdz path has to carry referenced asset layers along.
    const std::string package = dir + "/assembled.usdz";
    Ok(nullptr, RUsd_CreateUsdzPackage(nullptr, output.c_str(), package.c_str(), "", 0), "package assembled");
    context = Open(package, 0, "", "");
    if (context != nullptr)
    {
        RUsd_GetImportInfo(context, nullptr, 0, nullptr, nullptr, &meshCount, nullptr);
        Check(meshCount == 3, "the usdz package carries the referenced asset");
        RUsd_Destroy(context);
    }
}

}

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::printf("usage: composition_test <work-dir>\n");
        return 2;
    }

    if (RUsd_GetApiVersion() < 6)
    {
        std::printf("FAIL the plugin reports API %d; composition needs 6\n", RUsd_GetApiVersion());
        return 1;
    }

    const std::string dir = argv[1];
    WriteFile(dir + "/scene.usda", kFixture);
    WriteFile(dir + "/heavy.usda", kHeavy);

    TestImport(dir);
    TestOverrideLayer(dir);
    TestReferences(dir);

    if (failures != 0)
    {
        std::printf("%d check(s) failed\n", failures);
        return 1;
    }

    std::printf("all composition checks passed\n");
    return 0;
}
