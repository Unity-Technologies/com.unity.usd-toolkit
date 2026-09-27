cmake_minimum_required(VERSION 3.23)

project(UnityUSDToolkitNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(APPLE)
    set(CMAKE_MACOSX_RPATH ON)
endif()

if(NOT DEFINED OPENUSD_ROOT)
    message(FATAL_ERROR "Set OPENUSD_ROOT to the Pixar OpenUSD install directory.")
endif()

list(APPEND CMAKE_PREFIX_PATH
    "${OPENUSD_ROOT}"
    "${OPENUSD_ROOT}/lib/cmake"
    "${OPENUSD_ROOT}/lib/cmake/pxr"
    "${OPENUSD_ROOT}/cmake"
)
find_package(pxr CONFIG REQUIRED)

add_library(UnityUSDToolkitNative SHARED
    include/unity_usd_toolkit_native.h
    src/UsdExporter.cpp
)

if(APPLE)
    set_target_properties(UnityUSDToolkitNative PROPERTIES
        PREFIX ""
        MACOSX_RPATH ON
        BUILD_WITH_INSTALL_RPATH ON
        INSTALL_NAME_DIR "@rpath"
        INSTALL_RPATH "@loader_path;@loader_path/lib"
        XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED "NO"
    )
elseif(UNIX)
    set_target_properties(UnityUSDToolkitNative PROPERTIES
        BUILD_WITH_INSTALL_RPATH ON
        INSTALL_RPATH "$ORIGIN;$ORIGIN/lib"
    )
endif()

target_include_directories(UnityUSDToolkitNative
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
        "${OPENUSD_ROOT}/include"
)

target_compile_definitions(UnityUSDToolkitNative
    PRIVATE
        UNITY_USD_TOOLKIT_NATIVE_EXPORTS
)

# Keep build-machine paths out of the shipped binary. `__FILE__` (OpenUSD's TF_AXIOM / TF_VERIFY
# macros expand it) and `__PRETTY_FUNCTION__` (a lambda passed as a template argument prints the
# file it was written in) both embed the absolute path of whatever is being compiled, so a build
# from a home directory ships the developer's user name inside the plugin. Both come from the
# compiler's presumed filename, so one flag rewrites both, as well as debug info.
if(NOT MSVC)
    include(CheckCXXCompilerFlag)
    check_cxx_compiler_flag("-ffile-prefix-map=/a=/b" RUSD_HAS_FILE_PREFIX_MAP)
    if(RUSD_HAS_FILE_PREFIX_MAP)
        # The package root, so this file's own sources map to /usd-toolkit/..., and the OpenUSD
        # install, whose headers are the ones that actually reach the binary through those macros.
        get_filename_component(_RUSD_PACKAGE_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
        target_compile_options(UnityUSDToolkitNative
            PRIVATE
                "-ffile-prefix-map=${_RUSD_PACKAGE_ROOT}=/usd-toolkit"
        )
        if(OPENUSD_ROOT)
            get_filename_component(_RUSD_OPENUSD_ROOT "${OPENUSD_ROOT}" ABSOLUTE)
            target_compile_options(UnityUSDToolkitNative
                PRIVATE
                    "-ffile-prefix-map=${_RUSD_OPENUSD_ROOT}=/openusd"
            )
        endif()
    endif()
endif()

if(WIN32)
    target_compile_definitions(UnityUSDToolkitNative
        PRIVATE
            NOMINMAX
            WIN32_LEAN_AND_MEAN
    )
else()
    set_target_properties(UnityUSDToolkitNative PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN YES
    )
endif()

set(_RUSD_LINKED_OPENUSD_TARGET FALSE)
foreach(_target_name IN ITEMS
    pxr::usd_ms
    usd_ms
    pxr::usd_m
    usd_m
)
    if(TARGET "${_target_name}")
        target_link_libraries(UnityUSDToolkitNative PRIVATE "${_target_name}")
        set(_RUSD_LINKED_OPENUSD_TARGET TRUE)
        message(STATUS "Linking UnityUSDToolkitNative with OpenUSD target: ${_target_name}")
        break()
    endif()
endforeach()

if(NOT _RUSD_LINKED_OPENUSD_TARGET)
    find_library(_RUSD_MONOLITHIC_LIBRARY
        NAMES usd_ms usd_m
        HINTS
            "${OPENUSD_ROOT}/lib"
            "${OPENUSD_ROOT}/bin"
        NO_DEFAULT_PATH
    )

    if(_RUSD_MONOLITHIC_LIBRARY)
        target_link_libraries(UnityUSDToolkitNative PRIVATE "${_RUSD_MONOLITHIC_LIBRARY}")
        set(_RUSD_LINKED_OPENUSD_TARGET TRUE)
        message(STATUS "Linking UnityUSDToolkitNative with OpenUSD library: ${_RUSD_MONOLITHIC_LIBRARY}")
    endif()
endif()

if(NOT _RUSD_LINKED_OPENUSD_TARGET)
    set(_RUSD_COMPONENT_TARGET_COUNT 0)
    foreach(_target_name IN ITEMS
        pxr::usd
        usd
        pxr::usdGeom
        usdGeom
        pxr::usdShade
        usdShade
        pxr::usdUtils
        usdUtils
        pxr::ar
        ar
        pxr::sdf
        sdf
        pxr::tf
        tf
        pxr::vt
        vt
        pxr::gf
        gf
        pxr::plug
        plug
        pxr::work
        work
    )
        if(TARGET "${_target_name}")
            target_link_libraries(UnityUSDToolkitNative PRIVATE "${_target_name}")
            math(EXPR _RUSD_COMPONENT_TARGET_COUNT "${_RUSD_COMPONENT_TARGET_COUNT} + 1")
        endif()
    endforeach()

    if(_RUSD_COMPONENT_TARGET_COUNT GREATER 0)
        set(_RUSD_LINKED_OPENUSD_TARGET TRUE)
        message(STATUS "Linking UnityUSDToolkitNative with OpenUSD component CMake targets.")
    else()
        target_link_directories(UnityUSDToolkitNative
            PRIVATE
                "${OPENUSD_ROOT}/lib"
                "${OPENUSD_ROOT}/bin"
        )
        target_link_libraries(UnityUSDToolkitNative
            PRIVATE
                usd
                usdGeom
                usdShade
                usdUtils
                ar
                sdf
                tf
                vt
                gf
                plug
                work
        )
        message(STATUS "Linking UnityUSDToolkitNative with OpenUSD component library names.")
    endif()
endif()

install(TARGETS UnityUSDToolkitNative
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib
)
