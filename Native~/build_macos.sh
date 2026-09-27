#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
OPENUSD_ROOT=""
CONFIGURATION="Release"
ARCH="universal"
CMAKE_BIN="cmake"
# Without this the SDK default applies, which is the build machine's own macOS version: the
# 2026-09-23 payload shipped with `minos 26.0` and dyld refused it on anything older. 12.0 is
# Unity 6.3's minimum for a macOS player; the Editor's own minimum (13.0) is above it, so one
# value covers both.
DEPLOYMENT_TARGET="12.0"
# Deliberately NOT defaulted to the ad-hoc "-" identity. An ad-hoc signature carries no
# publisher identity, so it cannot establish trust, and defaulting to it made every build
# silently produce an unattributable binary (SECURITY-282834, CWE-347). Pass --codesign-id for a
# real Developer ID, or --adhoc-codesign to accept an unattributable build on purpose.
CODESIGN_ID=""
ADHOC_CODESIGN=0
SKIP_CODESIGN=0
RECORD_DEPENDENCY_DIGESTS=0
SKIP_SOURCE_PROVENANCE=0

usage() {
    cat <<'USAGE'
Build and install the Unity USD Toolkit native plugin for macOS.

Usage:
  Native~/build_macos.sh --openusd-root <path> [options]

Options:
  --openusd-root <path>      Pixar OpenUSD install root.
  --configuration <value>    CMake configuration. Defaults to Release.
  --arch <value>             native, x86_64, arm64, or universal. Defaults to universal.
  --deployment-target <ver>  Minimum macOS version the plugin runs on. Defaults to 12.0, Unity
                             6.3's minimum for a macOS player. Build OpenUSD with the same
                             value (MACOSX_DEPLOYMENT_TARGET) or its dylibs keep their own.
  --cmake <path>             CMake executable. Defaults to cmake.
  --codesign-id <identity>   Developer ID to sign the patched dylibs with. Required unless
                             --adhoc-codesign or --skip-codesign is given.
  --adhoc-codesign           Sign ad-hoc ('-'). Produces a binary with no publisher identity;
                             acceptable for local work, not for a release.
  --skip-codesign            Do not codesign patched dylibs.
  --record-dependency-digests
                             Accept the current OpenUSD/TBB tree and rewrite
                             Native~/dependency-digests/macos.sha256 instead of verifying
                             against it. Commit the result so the change is reviewable.
  --skip-source-provenance   Build against an OpenUSD install that was never verified against
                             Native~/dependency-sources/macos.tsv. For local experiments only:
                             the resulting payload has no chain back to a published revision
                             and must not be committed or shipped.
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
        --deployment-target)
            DEPLOYMENT_TARGET="$2"
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
        --adhoc-codesign)
            ADHOC_CODESIGN=1
            shift
            ;;
        --skip-codesign)
            SKIP_CODESIGN=1
            shift
            ;;
        --record-dependency-digests)
            RECORD_DEPENDENCY_DIGESTS=1
            shift
            ;;
        --skip-source-provenance)
            SKIP_SOURCE_PROVENANCE=1
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

# Fail rather than fall back to an unattributable signature: a silent ad-hoc default is how
# unsigned artifacts end up shipped.
if [[ ${SKIP_CODESIGN} -eq 0 ]]; then
    if [[ -z "${CODESIGN_ID}" && ${ADHOC_CODESIGN} -eq 0 ]]; then
        echo "No code signing identity given." >&2
        echo "  --codesign-id <identity>   sign with a real Developer ID (use this for a release)" >&2
        echo "  --adhoc-codesign           accept an ad-hoc, unattributable signature" >&2
        echo "  --skip-codesign            do not sign at all" >&2
        exit 1
    fi

    if [[ -z "${CODESIGN_ID}" ]]; then
        CODESIGN_ID="-"
        echo "WARNING: signing ad-hoc. The result carries no publisher identity and must not be released."
        CODESIGN_TIMESTAMP_ARGS=(--timestamp=none)
    else
        # A real identity gets a secure timestamp, so the signature outlives the certificate.
        CODESIGN_TIMESTAMP_ARGS=(--timestamp)
    fi

    if [[ -f "${USD_MONOLITHIC_DYLIB}" ]] && command -v codesign >/dev/null 2>&1; then
        codesign --force --sign "${CODESIGN_ID}" "${CODESIGN_TIMESTAMP_ARGS[@]}" "${USD_MONOLITHIC_DYLIB}" >/dev/null
    fi
fi

# --- Source provenance gate (SECURITY-282834, trust chain) ---------------------------------
# The digest gate below establishes that the tree being copied is the tree that was reviewed. It
# cannot establish where that tree came from -- by this point OpenUSD has already been fetched and
# compiled. That earlier link is what the stamp carries: verify_upstream_sources.py writes it into
# the install root only after checking the clone against the commit pinned in
# Native~/dependency-sources/macos.tsv, so requiring it here is what ties the payload to a
# published revision rather than to whatever happened to be on this machine.
if [[ ${SKIP_SOURCE_PROVENANCE} -eq 1 ]]; then
    echo "WARNING: --skip-source-provenance. This payload has no chain back to a published" >&2
    echo "         OpenUSD revision. Do not commit or ship what this build produces." >&2
elif ! python3 "${SCRIPT_DIR}/verify_upstream_sources.py" --platform macos \
    --check-stamp "${OPENUSD_ROOT}"; then
    echo "Refusing to build against an OpenUSD install of unverified origin." >&2
    exit 1
fi

# --- Dependency digest gate (SECURITY-282834, CWE-347) -------------------------------------
# Everything below is copied verbatim off this machine into the shipped package, so a tampered
# local OpenUSD/TBB tree would ride in unnoticed. Check what is about to be copied against the
# checked-in record first; --record-dependency-digests updates that record deliberately.
collect_dependency_files() {
    local dir
    for dir in "${OPENUSD_ROOT}/lib" "${OPENUSD_ROOT}/bin"; do
        [[ -d "${dir}" ]] || continue
        find "${dir}" -maxdepth 1 -type f -name "*.dylib*" ! -name "*.meta" ! -name "*debug*" -print0
    done

    for dir in "${OPENUSD_ROOT}/lib/usd" "${OPENUSD_ROOT}/plugin" "${OPENUSD_ROOT}/share" \
        "${OPENUSD_ROOT}/resources"; do
        [[ -d "${dir}" ]] || continue
        find "${dir}" -type f ! -name "*.meta" -print0
    done
}

DEPENDENCY_MODE="--verify"
if [[ ${RECORD_DEPENDENCY_DIGESTS} -eq 1 ]]; then
    DEPENDENCY_MODE="--record"
fi

if ! collect_dependency_files | xargs -0 python3 "${SCRIPT_DIR}/verify_dependency_digests.py" \
    --platform macos --root "${OPENUSD_ROOT}" "${DEPENDENCY_MODE}"; then
    echo "Refusing to copy an unverified dependency tree into the package." >&2
    exit 1
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

if [[ -n "${DEPLOYMENT_TARGET}" ]]; then
    CMAKE_CONFIGURE_ARGS+=("-DCMAKE_OSX_DEPLOYMENT_TARGET=${DEPLOYMENT_TARGET}")
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
