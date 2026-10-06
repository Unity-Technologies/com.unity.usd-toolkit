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

# --- Source provenance gate (SECURITY-282834, trust chain) ---------------------------------
# The payload is this wrapper plus the OpenUSD install it links against, copied verbatim. What
# ties that install to a published OpenUSD revision is the stamp Native~/build_openusd.py writes
# into it after checking the clone and every downloaded archive against
# Native~/dependency-sources/<platform>.tsv. Requiring it here rather than only in the build
# scripts means no way of building the wrapper -- a script, or plain CMake as the Linux payload
# used to be built -- can skip it by accident. It runs at configure time and again on every build,
# so an install changed after configuring is still caught.
#
# -DUSD_TOOLKIT_SKIP_SOURCE_PROVENANCE=ON skips it for local experiments. It is dropped from the
# cache straight away, so it applies to one configure only and cannot linger in a build tree, and
# the install step leaves a marker in the payload that makes generate_native_hashes.py refuse it.
set(_RUSD_SKIP_SOURCE_PROVENANCE OFF)
if(DEFINED USD_TOOLKIT_SKIP_SOURCE_PROVENANCE)
    set(_RUSD_SKIP_SOURCE_PROVENANCE "${USD_TOOLKIT_SKIP_SOURCE_PROVENANCE}")
    unset(USD_TOOLKIT_SKIP_SOURCE_PROVENANCE CACHE)
endif()

if(APPLE)
    set(_RUSD_PROVENANCE_PLATFORM macos)
elseif(WIN32)
    set(_RUSD_PROVENANCE_PLATFORM windows)
else()
    set(_RUSD_PROVENANCE_PLATFORM linux)
endif()

set(_RUSD_UNVERIFIED_MARKER ".unverified-build")
set(_RUSD_UNSCANNED_MARKER ".unscanned-build")
set(_RUSD_SCANNED OFF)

if(_RUSD_SKIP_SOURCE_PROVENANCE)
    message(WARNING
        "USD_TOOLKIT_SKIP_SOURCE_PROVENANCE: building against an OpenUSD install that was never "
        "verified against Native~/dependency-sources/${_RUSD_PROVENANCE_PLATFORM}.tsv. The payload "
        "has no chain back to a published revision; the install step marks it "
        "(${_RUSD_UNVERIFIED_MARKER}) so no manifest can be generated for it.")
else()
    find_package(Python3 COMPONENTS Interpreter REQUIRED)
    set(_RUSD_PROVENANCE_COMMAND
        "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/verify_upstream_sources.py"
        --platform "${_RUSD_PROVENANCE_PLATFORM}" --check-stamp "${OPENUSD_ROOT}")
    execute_process(COMMAND ${_RUSD_PROVENANCE_COMMAND} RESULT_VARIABLE _RUSD_PROVENANCE_RESULT)
    if(NOT _RUSD_PROVENANCE_RESULT EQUAL 0)
        message(FATAL_ERROR
            "Refusing to build against an OpenUSD install of unverified origin: ${OPENUSD_ROOT}")
    endif()
    add_custom_target(UsdToolkitSourceProvenance
        COMMAND ${_RUSD_PROVENANCE_COMMAND}
        COMMENT "Checking the OpenUSD install's source-provenance stamp"
        VERBATIM
    )
    # Whether the install was also built from scanned source (build_openusd.py --require-scan, with a
    # passing Native~/security-scans record per version). Not required for a local build, but the
    # install step marks a payload that was not, and the release pack refuses a marked payload.
    execute_process(COMMAND ${_RUSD_PROVENANCE_COMMAND} --require-scan
        RESULT_VARIABLE _RUSD_SCAN_RESULT OUTPUT_QUIET ERROR_QUIET)
    if(_RUSD_SCAN_RESULT EQUAL 0)
        set(_RUSD_SCANNED ON)
    endif()
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

if(TARGET UsdToolkitSourceProvenance)
    add_dependencies(UnityUSDToolkitNative UsdToolkitSourceProvenance)
endif()

install(TARGETS UnityUSDToolkitNative
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib
)

# The payload root is the install prefix on every platform. A skipped gate marks it; a build that
# passed the gate clears a marker a previous skipped build left behind.
if(_RUSD_SKIP_SOURCE_PROVENANCE)
    install(CODE "file(WRITE \"\${CMAKE_INSTALL_PREFIX}/${_RUSD_UNVERIFIED_MARKER}\" \"Built with USD_TOOLKIT_SKIP_SOURCE_PROVENANCE against ${OPENUSD_ROOT}.\\nNot for commit or release: Native~/generate_native_hashes.py refuses this payload.\\n\")")
else()
    install(CODE "file(REMOVE \"\${CMAKE_INSTALL_PREFIX}/${_RUSD_UNVERIFIED_MARKER}\")")
endif()

# Likewise for the source scan: a payload whose OpenUSD install was not built from scanned source
# is fine to commit and test, and Native~/generate_native_hashes.py --release -- the pack job --
# refuses it (SECURITY-282834, CWE-1104).
if(_RUSD_SCANNED)
    install(CODE "file(REMOVE \"\${CMAKE_INSTALL_PREFIX}/${_RUSD_UNSCANNED_MARKER}\")")
else()
    # Forward slashes: a Windows root's backslashes would be read as escapes in the install script.
    file(TO_CMAKE_PATH "${OPENUSD_ROOT}" _RUSD_OPENUSD_ROOT_DISPLAY)
    install(CODE "file(WRITE \"\${CMAKE_INSTALL_PREFIX}/${_RUSD_UNSCANNED_MARKER}\" \"Built against ${_RUSD_OPENUSD_ROOT_DISPLAY}, which build_openusd.py did not build with --require-scan.\\nNot for release: Native~/generate_native_hashes.py --release refuses this payload.\\n\")")
endif()
