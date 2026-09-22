// USDZ coverage: packaging an exported stage, and reading an asset out of a package.
//
// usdz is a read-only zip, so it can never be written as a stage - an export writes an ordinary
// .usdc/.usda first and RUsd_CreateUsdzPackage packages it. Textures then live *inside* the
// package, where no file-system read can reach them, which is what RUsd_ReadImportAsset is for.
// Both are API v5.
//
// The test writes its own source asset (a usda plus a real 2x2 PNG) into a temp directory, so it
// needs no binary fixture in the repo.
//
// Build and run (macOS; adjust the payload directory for Windows/Linux):
//   PLUGINS=../../Runtime/Plugins/macOS
//   clang++ -std=c++17 -Wl,-headerpad_max_install_names usdz_test.cpp \
//       -I ../include "$PLUGINS/UnityUSDToolkitNative.dylib" -o usdz_test
//   install_name_tool -change "@loader_path/UnityUSDToolkitNative.dylib" \
//       "$(cd "$PLUGINS" && pwd)/UnityUSDToolkitNative.dylib" usdz_test
//   # OpenUSD needs its plugin manifests; Unity sets this itself at runtime.
//   PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd" ./usdz_test

#include "unity_usd_toolkit_native.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
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

// A valid 2x2 RGBA PNG, so the packaged asset is a file type usdz actually allows.
const unsigned char kPng[] = {
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
    0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02,
    0x08, 0x06, 0x00, 0x00, 0x00, 0x72, 0xb6, 0x0d, 0x24, 0x00, 0x00, 0x00,
    0x14, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0xf8, 0xcf, 0xc0, 0xf0,
    0x1f, 0x0c, 0x81, 0x34, 0x10, 0x30, 0x34, 0x00, 0x00, 0x47, 0x4b, 0x08,
    0x79, 0xc3, 0x25, 0x87, 0xeb, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e,
    0x44, 0xae, 0x42, 0x60, 0x82
};

const char* kSourceStage =
    "#usda 1.0\n"
    "(\n"
    "    defaultPrim = \"Root\"\n"
    "    metersPerUnit = 1\n"
    "    upAxis = \"Y\"\n"
    ")\n"
    "\n"
    "def Xform \"Root\"\n"
    "{\n"
    "    def Mesh \"Quad\" (\n"
    "        prepend apiSchemas = [\"MaterialBindingAPI\"]\n"
    "    )\n"
    "    {\n"
    "        int[] faceVertexCounts = [4]\n"
    "        int[] faceVertexIndices = [0, 1, 2, 3]\n"
    "        point3f[] points = [(-1, 0, -1), (1, 0, -1), (1, 0, 1), (-1, 0, 1)]\n"
    "        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)] (\n"
    "            interpolation = \"vertex\"\n"
    "        )\n"
    "        rel material:binding = </Root/Looks/QuadMat>\n"
    "    }\n"
    "\n"
    "    def Scope \"Looks\"\n"
    "    {\n"
    "        def Material \"QuadMat\"\n"
    "        {\n"
    "            token outputs:surface.connect = </Root/Looks/QuadMat/Surface.outputs:surface>\n"
    "\n"
    "            def Shader \"Surface\"\n"
    "            {\n"
    "                uniform token info:id = \"UsdPreviewSurface\"\n"
    "                color3f inputs:diffuseColor.connect = </Root/Looks/QuadMat/Albedo.outputs:rgb>\n"
    "                token outputs:surface\n"
    "            }\n"
    "\n"
    "            def Shader \"Albedo\"\n"
    "            {\n"
    "                uniform token info:id = \"UsdUVTexture\"\n"
    "                asset inputs:file = @./textures/dot.png@\n"
    "                float3 outputs:rgb\n"
    "            }\n"
    "        }\n"
    "    }\n"
    "}\n";

bool WriteFile(const std::string& path, const void* data, size_t size)
{
    std::FILE* file = std::fopen(path.c_str(), "wb");
    if (file == nullptr)
    {
        return false;
    }

    const bool written = size == 0 || std::fwrite(data, 1, size, file) == size;
    std::fclose(file);
    return written;
}

std::string TempDirectory()
{
    const char* base = std::getenv("TMPDIR");
    std::string directory = (base != nullptr && base[0] != '\0') ? base : "/tmp/";
    if (directory.back() != '/')
    {
        directory += '/';
    }

    directory += "usd_toolkit_usdz_test";
    return directory;
}

} // namespace

int main()
{
    const std::string directory = TempDirectory();
    const std::string textureDirectory = directory + "/textures";
    std::string command = "rm -rf '" + directory + "' && mkdir -p '" + textureDirectory + "'";
    if (std::system(command.c_str()) != 0)
    {
        std::printf("could not prepare %s\n", directory.c_str());
        return 1;
    }

    const std::string stagePath = directory + "/asset.usda";
    const std::string texturePath = textureDirectory + "/dot.png";
    const std::string packagePath = directory + "/asset.usdz";
    if (!WriteFile(stagePath, kSourceStage, std::strlen(kSourceStage)) ||
        !WriteFile(texturePath, kPng, sizeof(kPng)))
    {
        std::printf("could not write the source asset\n");
        return 1;
    }

    std::printf("RUsd_CreateUsdzPackage\n");
    CheckEqual(RUsd_CreateUsdzPackage(nullptr, stagePath.c_str(), packagePath.c_str(), "asset.usdc", 0),
               0, "packaging succeeds");

    // The result must be a real zip, and the source stage must be left alone.
    {
        std::vector<char> magic(4, 0);
        std::FILE* file = std::fopen(packagePath.c_str(), "rb");
        Check(file != nullptr, "the package was written");
        if (file != nullptr)
        {
            const size_t read = std::fread(magic.data(), 1, magic.size(), file);
            std::fclose(file);
            Check(read == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4,
                  "the package starts with the zip magic");
        }

        std::FILE* source = std::fopen(stagePath.c_str(), "rb");
        Check(source != nullptr, "the source stage still exists");
        if (source != nullptr)
        {
            std::fclose(source);
        }
    }

    std::printf("RUsd_OpenStage on the package\n");
    RUsdContext* context = nullptr;
    if (RUsd_OpenStage(packagePath.c_str(), 1, &context) != 0 || context == nullptr)
    {
        std::printf("  FAIL could not open %s\n", packagePath.c_str());
        return 1;
    }

    std::vector<char> defaultPrim(512, 0);
    double metersPerUnit = 0.0;
    int upAxis = 0;
    int meshCount = 0;
    int materialCount = 0;
    CheckEqual(RUsd_GetImportInfo(context, defaultPrim.data(), static_cast<int>(defaultPrim.size()),
                                  &metersPerUnit, &upAxis, &meshCount, &materialCount),
               0, "RUsd_GetImportInfo on the package");
    CheckEqual(meshCount, 1, "the packaged mesh is found");
    CheckEqual(materialCount, 1, "the packaged material is found");

    // The texture path is authored relative to the layer, which now lives inside the package:
    // nothing on the file system answers to it, which is the whole point of RUsd_ReadImportAsset.
    RUsdMaterial material = {};
    std::vector<char> materialName(512, 0);
    CheckEqual(RUsd_GetImportMaterial(context, 0, &material, materialName.data(),
                                      static_cast<int>(materialName.size())),
               0, "RUsd_GetImportMaterial on the package");
    Check(material.albedoTexturePath[0] != '\0', "the packaged material reports an albedo texture");

    std::printf("RUsd_ReadImportAsset\n");
    std::vector<char> resolved(1024, 0);
    long long byteCount = 0;
    CheckEqual(RUsd_ReadImportAsset(context, material.albedoTexturePath, resolved.data(),
                                    static_cast<int>(resolved.size()), nullptr, 0, &byteCount),
               0, "size query succeeds");
    CheckEqual(static_cast<int>(byteCount), static_cast<int>(sizeof(kPng)),
               "the reported size is the PNG's size");
    Check(std::string(resolved.data()).find(".usdz[") != std::string::npos,
          std::string("the resolved path points inside the package (got '") + resolved.data() + "')");

    std::vector<unsigned char> bytes(static_cast<size_t>(byteCount > 0 ? byteCount : 1), 0);
    long long readCount = 0;
    CheckEqual(RUsd_ReadImportAsset(context, material.albedoTexturePath, nullptr, 0, bytes.data(),
                                    static_cast<long long>(bytes.size()), &readCount),
               0, "byte read succeeds");
    Check(byteCount == static_cast<long long>(sizeof(kPng)) &&
          std::memcmp(bytes.data(), kPng, sizeof(kPng)) == 0,
          "the bytes read back are the PNG that was packaged");

    // A buffer that is too small must fail rather than truncate, and a path that resolves to
    // nothing must fail instead of returning an empty asset.
    long long ignored = 0;
    unsigned char tiny[4] = {0};
    Check(RUsd_ReadImportAsset(context, material.albedoTexturePath, nullptr, 0, tiny, sizeof(tiny), &ignored) != 0,
          "a too-small buffer is rejected");
    Check(RUsd_ReadImportAsset(context, "./textures/missing.png", nullptr, 0, nullptr, 0, &ignored) != 0,
          "an unresolvable asset path fails");

    RUsd_Destroy(context);

    std::printf(failures == 0 ? "PASS\n" : "%d check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
