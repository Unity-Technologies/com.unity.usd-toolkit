#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
OPENUSD_ROOT=""
CONFIGURATION="Release"
ARCH="universal"
CMAKE_BIN="cmake"
CODESIGN_ID="-"
SKIP_CODESIGN=0

usage() {
    cat <<'USAGE'
Build and install the Unity USD Toolkit native plugin for macOS.

Usage:
  Native~/build_macos.sh --openusd-root <path> [options]

Options:
  --openusd-root <path>      Pixar OpenUSD install root.
  --configuration <value>    CMake configuration. Defaults to Release.
  --arch <value>             native, x86_64, arm64, or universal. Defaults to universal.
  --cmake <path>             CMake executable. Defaults to cmake.
  --codesign-id <identity>   Codesign identity. Defaults to ad-hoc '-'.
  --skip-codesign            Do not codesign patched dylibs.
  -h, --help                 Show this help.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --openusd-root|-OpenUsdRoot)
            OPENUSD_ROOT="$2"
            shift 2
            ;;
        --configuration|-Configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --arch)
            ARCH="$2"
            shift 2
            ;;
        --cmake|-CMakePath)
            CMAKE_BIN="$2"
            shift 2
            ;;
        --codesign-id)
            CODESIGN_ID="$2"
            shift 2
            ;;
        --skip-codesign)
            SKIP_CODESIGN=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

case "${ARCH}" in
    native)
        CMAKE_ARCHS=""
        BUILD_SUFFIX="native"
        ;;
    x64|x86_64)
        CMAKE_ARCHS="x86_64"
        BUILD_SUFFIX="x86_64"
        ;;
    arm64)
        CMAKE_ARCHS="arm64"
        BUILD_SUFFIX="arm64"
        ;;
    universal)
        CMAKE_ARCHS="x86_64;arm64"
        BUILD_SUFFIX="universal"
        ;;
    *)
        echo "Unsupported --arch '${ARCH}'. Use native, x86_64, arm64, or universal." >&2
        exit 2
        ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script must be run on macOS." >&2
    exit 1
fi

if [[ -z "${OPENUSD_ROOT}" ]]; then
    echo "--openusd-root is required." >&2
    usage >&2
    exit 2
fi

if [[ ! -d "${OPENUSD_ROOT}" ]]; then
    echo "OpenUSD root does not exist: ${OPENUSD_ROOT}" >&2
    exit 1
fi

if ! command -v "${CMAKE_BIN}" >/dev/null 2>&1; then
    echo "CMake was not found. Install CMake or pass --cmake." >&2
    exit 1
fi

OPENUSD_ROOT="$(cd "${OPENUSD_ROOT}" && pwd)"
USD_STAGE_HEADER="${OPENUSD_ROOT}/include/pxr/usd/usd/stage.h"
if [[ ! -f "${USD_STAGE_HEADER}" ]]; then
    echo "OpenUSD headers were not found under ${OPENUSD_ROOT}. Expected: ${USD_STAGE_HEADER}" >&2
    exit 1
fi

USD_MONOLITHIC_DYLIB="${OPENUSD_ROOT}/lib/libusd_ms.dylib"
if [[ ! -f "${USD_MONOLITHIC_DYLIB}" && -f "${OPENUSD_ROOT}/build/OpenUSD-dev/libusd_ms.dylib" ]]; then
    mkdir -p "${OPENUSD_ROOT}/lib"
    cp -p "${OPENUSD_ROOT}/build/OpenUSD-dev/libusd_ms.dylib" "${USD_MONOLITHIC_DYLIB}"
fi

if [[ -f "${USD_MONOLITHIC_DYLIB}" ]] && command -v codesign >/dev/null 2>&1; then
    codesign --force --sign "${CODESIGN_ID}" --timestamp=none "${USD_MONOLITHIC_DYLIB}" >/dev/null
fi

BUILD_DIR="${SCRIPT_DIR}/build~/macos-${BUILD_SUFFIX}"
INSTALL_DIR="${PACKAGE_DIR}/Runtime/Plugins/macOS"

mkdir -p "${BUILD_DIR}" "${INSTALL_DIR}"

CMAKE_CONFIGURE_ARGS=(
    -S "${SCRIPT_DIR}"
    -B "${BUILD_DIR}"
    -DOPENUSD_ROOT="${OPENUSD_ROOT}"
    -DCMAKE_INSTALL_PREFIX="${INSTALL_DIR}"
)

if [[ -n "${CMAKE_ARCHS}" ]]; then
    CMAKE_CONFIGURE_ARGS+=("-DCMAKE_OSX_ARCHITECTURES=${CMAKE_ARCHS}")
fi

"${CMAKE_BIN}" "${CMAKE_CONFIGURE_ARGS[@]}"
"${CMAKE_BIN}" --build "${BUILD_DIR}" --config "${CONFIGURATION}" --target install --parallel

copy_dylibs_from_directory() {
    local source_dir="$1"
    if [[ ! -d "${source_dir}" ]]; then
        return
    fi

    find "${source_dir}" -maxdepth 1 -type f -name "*.dylib*" ! -name "*.meta" -print0 |
        while IFS= read -r -d '' file; do
            local name
            name="$(basename "${file}")"
            if [[ "${name}" == *"_debug"* || "${name}" == *"debug"* ]]; then
                continue
            fi

            cp -L "${file}" "${INSTALL_DIR}/$(basename "${file}")"
        done
}

copy_runtime_directory() {
    local source_dir="$1"
    local destination_name="$2"
    if [[ ! -d "${source_dir}" ]]; then
        return
    fi

    local destination="${INSTALL_DIR}/${destination_name}"
    rm -rf "${destination}"
    mkdir -p "$(dirname "${destination}")"
    cp -R "${source_dir}" "${destination}"
}

copy_dylibs_from_directory "${OPENUSD_ROOT}/lib"
copy_dylibs_from_directory "${OPENUSD_ROOT}/bin"
copy_runtime_directory "${OPENUSD_ROOT}/lib/usd" "lib/usd"
copy_runtime_directory "${OPENUSD_ROOT}/plugin" "plugin"
copy_runtime_directory "${OPENUSD_ROOT}/share" "share"
copy_runtime_directory "${OPENUSD_ROOT}/resources" "resources"

USD_CODEGEN_TEMPLATES="${INSTALL_DIR}/lib/usd/usd/resources/codegenTemplates"
rm -rf "${USD_CODEGEN_TEMPLATES}" "${USD_CODEGEN_TEMPLATES}.meta"
find "${INSTALL_DIR}" -maxdepth 1 -type f \( -name "*_debug*.dylib*" -o -name "*debug*.dylib*" \) -delete

TOOLKIT_DYLIB="${INSTALL_DIR}/UnityUSDToolkitNative.dylib"
if [[ ! -f "${TOOLKIT_DYLIB}" ]]; then
    echo "UnityUSDToolkitNative.dylib was not installed to ${INSTALL_DIR}" >&2
    exit 1
fi

patch_macho_dependencies() {
    local file="$1"
    local base
    base="$(basename "${file}")"
    chmod u+w "${file}"

    install_name_tool -id "@loader_path/${base}" "${file}" 2>/dev/null || true

    otool -L "${file}" | awk 'NR > 1 { print $1 }' |
        while IFS= read -r dependency; do
            local dependency_base
            dependency_base="$(basename "${dependency}")"
            if [[ -f "${INSTALL_DIR}/${dependency_base}" &&
                  "${dependency}" != @loader_path/* &&
                  "${dependency}" != /usr/lib/* &&
                  "${dependency}" != /System/Library/* ]]; then
                install_name_tool -change "${dependency}" "@loader_path/${dependency_base}" "${file}" 2>/dev/null || true
            fi
        done

    install_name_tool -add_rpath "@loader_path" "${file}" 2>/dev/null || true
}

find "${INSTALL_DIR}" -maxdepth 1 -type f \( -name "UnityUSDToolkitNative.dylib" -o -name "*.dylib*" \) ! -name "*.meta" -print0 |
    while IFS= read -r -d '' file; do
        patch_macho_dependencies "${file}"
    done

if [[ "${SKIP_CODESIGN}" -eq 0 ]] && command -v codesign >/dev/null 2>&1; then
    find "${INSTALL_DIR}" -maxdepth 1 -type f \( -name "UnityUSDToolkitNative.dylib" -o -name "*.dylib*" \) ! -name "*.meta" -print0 |
        while IFS= read -r -d '' file; do
            codesign --force --sign "${CODESIGN_ID}" --timestamp=none "${file}" >/dev/null
        done
fi

if [[ ! -f "${INSTALL_DIR}/libusd_ms.dylib" && ! -f "${INSTALL_DIR}/libusd_m.dylib" ]]; then
    echo "Warning: no OpenUSD monolithic dylib was found in ${INSTALL_DIR}." >&2
fi

echo
echo "Installed UnityUSDToolkitNative and OpenUSD runtime files to:"
echo "${INSTALL_DIR}"
echo
echo "Dependency check:"
otool -L "${TOOLKIT_DYLIB}"
