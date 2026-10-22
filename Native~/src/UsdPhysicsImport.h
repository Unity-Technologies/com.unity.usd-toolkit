#pragma once

// Physics on import (API v13): what RUsd_ReadPhysics runs, kept out of UsdExporter.cpp, which owns
// the context and passes in what this needs from it.

#include "unity_usd_toolkit_native.h"

#include <functional>
#include <memory>
#include <string>

#include <pxr/pxr.h>
#include <pxr/usd/usd/stage.h>

namespace rusd_physics
{

// The block RUsd_ReadPhysics hands out, with the storage its pointers point into.
struct Store;

struct StoreDeleter
{
    void operator()(Store* store) const;
};

using StorePtr = std::unique_ptr<Store, StoreDeleter>;

struct ImportLookups
{
    // Prim path -> RUsd_GetImportNodeInfo's / RUsd_GetImportMeshInfo's index, -1 when absent.
    std::function<int(const std::string&)> node;
    std::function<int(const std::string&)> mesh;
};

// Reads the stage's physics into a new store. `options` is already validated and defaulted.
// Throws std::exception with a message the caller reports when the read fails.
StorePtr Read(
    const PXR_NS::UsdStageRefPtr& stage,
    const RUsdPhysicsReadOptions& options,
    const ImportLookups& lookups);

const RUsdPhysicsData* Data(const Store& store);

} // namespace rusd_physics
