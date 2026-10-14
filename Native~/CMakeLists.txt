cmake_minimum_required(VERSION 3.23)

project(UnityUSDToolkitNative LANGUAGES C CXX)

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

# --- The inflater the image codecs compile in ----------------------------------------------
# src/UsdDeflate.c amalgamates the decompressor of the libdeflate that OpenUSD vendors for its
# OpenEXR reader (MIT, v1.18), so EXR tiles and PNGs inflate the same way on every platform with
# no zlib to find at run time. The source is not in the install, only in the tree it was built
# from, which Native~/build_openusd.py leaves beside it as <install>/openusd-src; OPENUSD_SRC
# points elsewhere when the tree lives elsewhere. Each file is checked against the digest
# recorded here (bytes with CRLF folded to LF, so a Windows clone hashes like a Linux one) before
# anything is compiled: these are the files OpenUSD v26.05 (2095faf) ships, and a tree that
# differs is not the tree the dependency record pins.
if(NOT DEFINED OPENUSD_SRC)
    set(OPENUSD_SRC "${OPENUSD_ROOT}/openusd-src")
endif()
set(_RUSD_DEFLATE_DIR "${OPENUSD_SRC}/pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate")
set(_RUSD_DEFLATE_FILES
    "COPYING=5d246dd2537307b80fed9ba5fdddf5d0fe7a8f023393a071182b9daed56b40fe"
    "common_defs.h=354e64c01a4083c0d1e7dc111c7d42d2adc2b06ab2efa7681473d93dac9e3f76"
    "libdeflate.h=9aabfa2f90070765968e8055610240fe15fe03395b9355ebe6fc8372271e20bb"
    "lib/adler32.c=1d0d46e30292c06da9472bdb599c434dda54dc0a709ce25fbc8bf2c057c26ca3"
    "lib/adler32_vec_template.h=d84c6f88473189dd6f2ffaf9a47a238bec212ddfe371ec0ff55dd8954b41a185"
    "lib/bt_matchfinder.h=e786b8bda13eca2cc02ef0c3e7dbce3e5a08be906c851d5d220347a5e9bdb616"
    "lib/cpu_features_common.h=f4eb0800558e9116dd14446ccfb9b1896f970aec6de42c18366a29bde73fd0da"
    "lib/decompress_template.h=06cd528ce5eb4c8dd58bc4fd42e882aede4387196cf3705a2a7d890327835f3d"
    "lib/deflate_compress.c=ab506b374c46e70ec2e915217607918e08c5370fd90bb79002fdb415cffae5b5"
    "lib/deflate_compress.h=0633294ff9e7dae04047184a26072af3626cc795caf333626010c147dff8c09d"
    "lib/deflate_constants.h=72ea6e5f0abe53e781203e3f383603a765a768b860a09b3356be36bfd87c8af3"
    "lib/deflate_decompress.c=45104ae7c8307e15b778aee79c797e0ba3d763fd1f6c34aa4f1efca73ad7fcd0"
    "lib/hc_matchfinder.h=50c15b1144910d409cb4e26888d52f8fccb78dc5b0ab61fe3ac4d41f017b1555"
    "lib/ht_matchfinder.h=a4170100c9530dd36797e4c50f34ab6c85eaf069bfeb6337ce9e142ba4f60657"
    "lib/lib_common.h=daff929df91867ec101727d9021549c3139f3ae175a5a5ef845199659003b945"
    "lib/matchfinder_common.h=9a6fca6e558d19d6ff50111e03f6f56ee315058bd079dfafda20f95fb6f981a5"
    "lib/utils.c=8a983d7774f2a7369b322907e0a2861c3eed913947ee796b69e24a4b9964c5b5"
    "lib/zlib_compress.c=a6d572bb3f6771a17a54e1aed194ba9148d69eea090c23f39d1370b561c85bd5"
    "lib/zlib_constants.h=562e16b83ac33ac6d351cdfdf1962750576080d65c22ab66cad0aaa33695bebb"
    "lib/zlib_decompress.c=f87b9716cb3b507b6d9f80c6ec4688a370a8518ead4e607a092a56c1019438e9"
    "lib/arm/adler32_impl.h=e7d99be2e7b2a40a5df2b03b381bb3bc72e148f15f53488e3812acbc151791e5"
    "lib/arm/cpu_features.c=051ba4649c1b0f38bf9eef2fd74c1fee6ef82e1049879ebac274886694a11e43"
    "lib/arm/cpu_features.h=96eef31325896eb70dfc2f996a1fe2c12faa25cdf20303b544e96d6032029b37"
    "lib/arm/matchfinder_impl.h=e479785e936e3adf7b621b7314cb09365ea65bd350cfc7fc51480752178ba998"
    "lib/x86/adler32_impl.h=6d6da312a2e9cd52af93fea0cbe12212be3c8f7f2c38127b39aab204312b4d0f"
    "lib/x86/cpu_features.c=06ad5cf41ab2270d7001f736673c256d5e3fb1dc0f1f702769df67252b064e16"
    "lib/x86/cpu_features.h=09b84ac7e3daf52f8a70f7c03ca8ae78aa7cb842d5d893b836f753276b82c2a2"
    "lib/x86/decompress_impl.h=582688894e9d218daddc7b175b4f02762494e5ee32f670bfba63af862b0b5598"
    "lib/x86/matchfinder_impl.h=815cae8221ef667123a62497896e6f032ae67be81b47b9d3cacf5b9cd95a760a"
)
foreach(_RUSD_DEFLATE_ENTRY IN LISTS _RUSD_DEFLATE_FILES)
    string(REPLACE "=" ";" _RUSD_DEFLATE_PARTS "${_RUSD_DEFLATE_ENTRY}")
    list(GET _RUSD_DEFLATE_PARTS 0 _RUSD_DEFLATE_RELATIVE)
    list(GET _RUSD_DEFLATE_PARTS 1 _RUSD_DEFLATE_EXPECTED)
    set(_RUSD_DEFLATE_FILE "${_RUSD_DEFLATE_DIR}/${_RUSD_DEFLATE_RELATIVE}")
    if(NOT EXISTS "${_RUSD_DEFLATE_FILE}")
        message(FATAL_ERROR
            "The OpenUSD source tree is needed for the inflater and ${_RUSD_DEFLATE_FILE} is not "
            "there. Pass -DOPENUSD_SRC=<the clone build_openusd.py built from>; it leaves one at "
            "<install>/openusd-src.")
    endif()
    file(READ "${_RUSD_DEFLATE_FILE}" _RUSD_DEFLATE_TEXT)
    string(REPLACE "\r\n" "\n" _RUSD_DEFLATE_TEXT "${_RUSD_DEFLATE_TEXT}")
    string(SHA256 _RUSD_DEFLATE_ACTUAL "${_RUSD_DEFLATE_TEXT}")
    if(NOT _RUSD_DEFLATE_ACTUAL STREQUAL _RUSD_DEFLATE_EXPECTED)
        message(FATAL_ERROR
            "${_RUSD_DEFLATE_FILE} is not the file OpenUSD v26.05 ships (sha256 "
            "${_RUSD_DEFLATE_ACTUAL}, expected ${_RUSD_DEFLATE_EXPECTED}). Refusing to compile an "
            "inflater of unknown origin into the payload.")
    endif()
endforeach()
message(STATUS "Inflater: libdeflate from ${_RUSD_DEFLATE_DIR} (29 files verified)")

add_library(UnityUSDToolkitNative SHARED
    include/unity_usd_toolkit_native.h
    src/UsdExporter.cpp
    src/UsdImageCodecs.cpp
    src/UsdDeflate.c
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
        "${_RUSD_DEFLATE_DIR}"
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
        C_VISIBILITY_PRESET hidden
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
