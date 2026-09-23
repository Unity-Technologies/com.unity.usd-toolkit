// Security regression coverage for SECURITY-282834 (importer findings).
//
// 1. Untrusted mesh topology. A stage can author faceVertexCounts that do not describe
//    faceVertexIndices — in the extreme, counts summing past INT_MAX, which wrapped the 32-bit
//    face-start accumulator and then defeated TriangulateFace's own bounds check, reading far
//    outside the corner buffer. The fix rejects any mesh whose counts do not sum to exactly the
//    faceVertexIndices length, which is the general form of that inconsistency.
// 2. Authored asset paths. RUsd_ReadImportAsset resolves through OpenUSD, which honours absolute
//    paths and `..` climbs, so a crafted `inputs:file` could read any local file. The fix confines
//    the resolved path to the folders of the layers that compose the stage.
//
// Build and run (macOS; adjust the payload directory for Windows/Linux):
//   PLUGINS=../../Runtime/Plugins/macOS
//   clang++ -std=c++17 -Wl,-headerpad_max_install_names security_test.cpp \
//       -I ../include "$PLUGINS/UnityUSDToolkitNative.dylib" -o security_test
//   install_name_tool -change "@loader_path/UnityUSDToolkitNative.dylib" \
//       "$(cd "$PLUGINS" && pwd)/UnityUSDToolkitNative.dylib" security_test
//   PXR_PLUGINPATH_NAME="$PLUGINS/lib/usd:$PLUGINS/plugin/usd" \
//       ./security_test security_fixture.usda

#include "unity_usd_toolkit_native.h"

#include <cstdio>
#include <filesystem>
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

std::string Diagnostics(RUsdContext* context)
{
    std::vector<char> buffer(8192, '\0');
    RUsd_GetDiagnostics(context, buffer.data(), static_cast<int>(buffer.size()));
    return std::string(buffer.data());
}

// Reads one asset through the importer's resolver. Returns the native status code.
int ReadAsset(RUsdContext* context, const char* assetPath)
{
    std::vector<char> resolved(4096, '\0');
    long long size = 0;
    return RUsd_ReadImportAsset(
        context, assetPath, resolved.data(), static_cast<int>(resolved.size()), nullptr, 0, &size);
}

} // namespace

int main(int argc, char** argv)
{
    const char* fixture = argc > 1 ? argv[1] : "security_fixture.usda";

    RUsdContext* context = nullptr;
    if (RUsd_OpenStage(fixture, 1, &context) != 0 || context == nullptr)
    {
        std::vector<char> error(4096, '\0');
        RUsd_GetLastError(context, error.data(), static_cast<int>(error.size()));
        std::printf("FAIL could not open %s: %s\n", fixture, error.data());
        return 1;
    }

    char primPath[512] = {0};
    double metersPerUnit = 0.0;
    int upAxis = 0, meshCount = -1, materialCount = -1;
    RUsd_GetImportInfo(
        context, primPath, sizeof(primPath), &metersPerUnit, &upAxis, &meshCount, &materialCount);

    std::printf("Topology consistency (CWE-190 / OOB read)\n");
    // GoodQuad imports; OverCount and UnderCount are refused, so exactly one mesh survives.
    Check(meshCount == 1, "only the well-formed mesh is imported (meshCount == 1), got " +
        std::to_string(meshCount));

    const std::string diagnostics = Diagnostics(context);
    Check(diagnostics.find("OverCount") != std::string::npos,
        "OverCount is reported as inconsistent topology");
    Check(diagnostics.find("UnderCount") != std::string::npos,
        "UnderCount is reported as inconsistent topology");
    Check(diagnostics.find("GoodQuad") == std::string::npos,
        "the well-formed mesh produces no warning");

    std::printf("Asset path confinement (CWE-22 / arbitrary file read)\n");
    Check(ReadAsset(context, "security_inside.txt") == 0,
        "a relative asset inside the stage folder is still readable");
    Check(ReadAsset(context, "/etc/hosts") != 0,
        "an absolute path outside the stage folder is refused");
    Check(ReadAsset(context, "../../../../../../etc/hosts") != 0,
        "a `..` climb out of the stage folder is refused");
    // Exists and resolves cleanly, but sits one folder above the stage: refused on its
    // *location*, not for being absent. A substring scan for ".." would have let this through
    // once normalization removed it. The probe file is created here rather than relying on a
    // checked-in file one level up, so the check holds wherever the fixture is run from --
    // depending on the repository layout made this fail on a healthy payload for the wrong
    // reason as soon as the fixture was copied elsewhere.
    const std::filesystem::path fixturePath = std::filesystem::absolute(fixture);
    const std::filesystem::path outsideProbe =
        fixturePath.parent_path().parent_path() / "security_outside_probe.tmp";
    bool probeCreated = false;
    {
        std::FILE* handle = std::fopen(outsideProbe.string().c_str(), "wb");
        if (handle != nullptr)
        {
            std::fputs("outside the stage folder\n", handle);
            std::fclose(handle);
            probeCreated = true;
        }
    }

    if (probeCreated)
    {
        const std::string relativeToProbe = "../" + outsideProbe.filename().string();
        Check(ReadAsset(context, relativeToProbe.c_str()) != 0,
            "an existing file just outside the stage folder is refused");

        std::error_code ec;
        std::filesystem::remove(outsideProbe, ec);
    }
    else
    {
        std::printf("  skip %s\n",
            "an existing file just outside the stage folder (could not create the probe)");
    }

    // The refusal names the authored spelling, not where the file sits on this machine.
    std::vector<char> error(4096, '\0');
    RUsd_GetLastError(context, error.data(), static_cast<int>(error.size()));
    const std::string lastError(error.data());
    Check(lastError.find("resolves outside the stage's own folders") != std::string::npos,
        "the refusal is reported as an out-of-folder rejection");

    RUsd_Destroy(context);

    std::printf("%s (%d failure%s)\n", failures == 0 ? "PASS" : "FAIL", failures,
        failures == 1 ? "" : "s");
    return failures == 0 ? 0 : 1;
}
