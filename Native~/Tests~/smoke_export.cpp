#include "unity_usd_toolkit_native.h"

#include <cstdio>

int main()
{
    RUsdContext* context = nullptr;
    const char* outputPath = "D:/OpenUSDExporter/Native~/build~/windows-x64/smoke_cube.usda";

    if (RUsd_BeginExport(outputPath, "Smoke", 1.0f, &context) != 0)
    {
        char error[4096];
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("Begin failed: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    const RUsdVec3 points[] = {
        {-0.5f, -0.5f, -0.5f},
        { 0.5f, -0.5f, -0.5f},
        { 0.5f,  0.5f, -0.5f},
        {-0.5f,  0.5f, -0.5f},
        {-0.5f, -0.5f,  0.5f},
        { 0.5f, -0.5f,  0.5f},
        { 0.5f,  0.5f,  0.5f},
        {-0.5f,  0.5f,  0.5f},
    };

    const int indices[] = {
        0, 2, 1, 0, 3, 2,
        4, 5, 6, 4, 6, 7,
        0, 1, 5, 0, 5, 4,
        3, 6, 2, 3, 7, 6,
        1, 2, 6, 1, 6, 5,
        0, 4, 7, 0, 7, 3,
    };

    const RUsdMaterial material = {
        0.2f,
        0.55f,
        1.0f,
        1.0f,
        0.0f,
        0.35f,
    };

    if (RUsd_AddMesh(
        context,
        "/Smoke/Cube_Mesh",
        points,
        static_cast<int>(sizeof(points) / sizeof(points[0])),
        indices,
        static_cast<int>(sizeof(indices) / sizeof(indices[0])),
        nullptr,
        0,
        nullptr,
        0,
        &material) != 0)
    {
        char error[4096];
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("AddMesh failed: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    if (RUsd_EndExport(context) != 0)
    {
        char error[4096];
        RUsd_GetLastError(context, error, sizeof(error));
        std::printf("End failed: %s\n", error);
        RUsd_Destroy(context);
        return 1;
    }

    RUsd_Destroy(context);
    std::printf("Wrote %s\n", outputPath);
    return 0;
}
