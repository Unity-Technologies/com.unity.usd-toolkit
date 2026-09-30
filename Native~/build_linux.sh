#!/usr/bin/env bash
set -euo pipefail

# Unity USD Toolkit - Linux native build
# Builds libUnityUSDToolkitNative.so with CMake against the monolithic, --no-python OpenUSD 26.05
# install that Native~/build_openusd.py produces, and assembles the shipped payload under
# Runtime/Plugins/x86_64/Linux/:
#
#   libUnityUSDToolkitNative.so        rpath $ORIGIN:$ORIGIN/lib
#   lib/libusd_ms.so                   rpath $ORIGIN
#   lib/libtbb.so.2
#   lib/usd/**                         (without usd/resources/codegenTemplates)
#   plugin/usd/**
#
# Usage:
#   Native~/build_linux.sh --openusd-root /opt/usd-26.05/install [options]
#
# This is the path the shipped Linux payload is built with. It used to be a sequence of manual
# commands in Native~/REBUILD_WINDOWS_LINUX.md, which left the source-provenance check to whoever
# ran them; as a script it enforces the same gates build_macos.sh and build_windows.ps1 do
# (SECURITY-282834). It no longer builds the packman + Python layout it once targeted.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

OPENUSD_ROOT=""
CMAKE_BIN="cmake"
RECORD_DEPENDENCY_DIGESTS=0
SKIP_SOURCE_PROVENANCE=0

usage() {
    cat <<'USAGE'
Build and assemble the Unity USD Toolkit native payload for Linux x86_64.

Usage:
  Native~/build_linux.sh --openusd-root <path> [options]

Options:
  --openusd-root <path>      OpenUSD install root built by Native~/build_openusd.py.
  --cmake <path>             CMake executable. Defaults to cmake.
  --record-dependency-digests
                             Accept the current OpenUSD/TBB tree and rewrite
                             Native~/dependency-digests/linux.sha256 instead of verifying
                             against it. Requires a valid provenance stamp. Commit the result so
                             the change is reviewable.
  --skip-source-provenance   Build against an OpenUSD install that was never verified against
                             Native~/dependency-sources/linux.tsv. For local experiments only:
                             the resulting payload has no chain back to a published revision
                             and must not be committed or shipped; the build marks the
                             payload so generate_native_hashes.py refuses it.
  -h, --help                 Show this help.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --openusd-root)
            OPENUSD_ROOT="$2"
            shift 2
            ;;
        --cmake)
            CMAKE_BIN="$2"
            shift 2
            ;;
        --record-dependency-digests)
            RECORD_DEPENDENCY_DIGESTS=1
            shift
            ;;
        --skip-source-provenance)
            SKIP_SOURCE_PROVENANCE=1
            shift
            ;;
        --python-root)
            echo "--python-root: this script no longer builds the packman + Python layout. The" >&2
            echo "shipped payload is a --no-python build; see Native~/REBUILD_WINDOWS_LINUX.md." >&2
            exit 2
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

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "This script must be run on Linux." >&2
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
OPENUSD_ROOT="$(cd "${OPENUSD_ROOT}" && pwd)"

for tool in "${CMAKE_BIN}" python3 patchelf strip strings ldd; do
    command -v "${tool}" >/dev/null 2>&1 || { echo "${tool} not found." >&2; exit 1; }
done

for required in include/pxr/usd/usd/stage.h lib/libusd_ms.so lib/libtbb.so.2; do
    if [[ ! -f "${OPENUSD_ROOT}/${required}" ]]; then
        echo "Not a monolithic OpenUSD install: ${OPENUSD_ROOT}/${required} is missing." >&2
        exit 1
    fi
done

if [[ ${SKIP_SOURCE_PROVENANCE} -eq 1 && ${RECORD_DEPENDENCY_DIGESTS} -eq 1 ]]; then
    echo "--record-dependency-digests with --skip-source-provenance would record a tree of unknown" >&2
    echo "origin as the reviewed baseline. Build the install with Native~/build_openusd.py instead." >&2
    exit 2
fi

DST="${PACKAGE_DIR}/Runtime/Plugins/x86_64/Linux"
LIBDST="${DST}/lib"
BUILD_DIR="${SCRIPT_DIR}/build~/linux-x64"

# --- Source provenance gate (SECURITY-282834, trust chain) ---------------------------------
# The digest gate below establishes that the tree being copied is the tree that was reviewed. It
# cannot establish where that tree came from -- by this point OpenUSD has already been fetched and
# compiled. That earlier link is what the stamp carries: Native~/build_openusd.py writes it into
# the install root only after checking the clone and the TBB archive against
# Native~/dependency-sources/linux.tsv, so requiring it here is what ties the payload to a
# published revision rather than to whatever happened to be on this machine. Native~/CMakeLists.txt
# checks it again; this copy fails before the digest gate, with the clearer message.
if [[ ${SKIP_SOURCE_PROVENANCE} -eq 1 ]]; then
    echo "WARNING: --skip-source-provenance. This payload has no chain back to a published" >&2
    echo "         OpenUSD revision. Do not commit or ship what this build produces." >&2
elif ! python3 "${SCRIPT_DIR}/verify_upstream_sources.py" --platform linux \
    --check-stamp "${OPENUSD_ROOT}"; then
    echo "Refusing to build against an OpenUSD install of unverified origin." >&2
    exit 1
fi

# --- Dependency digest gate (SECURITY-282834, CWE-347) -------------------------------------
# Everything below is copied verbatim off this machine into the shipped package, so a tampered
# local OpenUSD/TBB tree would ride in unnoticed. --scan enumerates exactly what the assembly step
# copies (SCAN_RULES["linux"] in the verifier); --record-dependency-digests updates the record
# deliberately.
DEPENDENCY_MODE="--verify"
if [[ ${RECORD_DEPENDENCY_DIGESTS} -eq 1 ]]; then
    DEPENDENCY_MODE="--record"
fi

if ! python3 "${SCRIPT_DIR}/verify_dependency_digests.py" --platform linux \
    --root "${OPENUSD_ROOT}" --scan "${DEPENDENCY_MODE}"; then
    echo "Refusing to copy an unverified dependency tree into the package." >&2
    exit 1
fi

# --- Build the wrapper -- clean, every time ------------------------------------------------
# A stale CMake cache is the most common cause of "I rebuilt and nothing changed".
if [[ ${SKIP_SOURCE_PROVENANCE} -eq 1 ]]; then
    SKIP_PROVENANCE_VALUE=ON
else
    SKIP_PROVENANCE_VALUE=OFF
fi

echo "[1/3] Building libUnityUSDToolkitNative.so ..."
rm -rf "${BUILD_DIR}"
mkdir -p "${DST}"
"${CMAKE_BIN}" -S "${SCRIPT_DIR}" -B "${BUILD_DIR}" \
    -DCMAKE_BUILD_TYPE=Release \
    -DOPENUSD_ROOT="${OPENUSD_ROOT}" \
    -DCMAKE_INSTALL_PREFIX="${DST}" \
    -DUSD_TOOLKIT_SKIP_SOURCE_PROVENANCE="${SKIP_PROVENANCE_VALUE}"
"${CMAKE_BIN}" --build "${BUILD_DIR}" --parallel
"${CMAKE_BIN}" --install "${BUILD_DIR}"

# --- Assemble the payload ------------------------------------------------------------------
# Old files go first so nothing from a previous OpenUSD survives into this payload. Unity .meta
# files stay: deleting them would make Unity regenerate them with new GUIDs.
echo "[2/3] Copying OpenUSD runtime into lib/ and plugin/ ..."
for dir in "${LIBDST}" "${DST}/plugin"; do
    if [[ -d "${dir}" ]]; then
        find "${dir}" -type f ! -name "*.meta" -delete
    fi
done
mkdir -p "${LIBDST}/usd" "${DST}/plugin/usd"

cp -L "${OPENUSD_ROOT}/lib/libusd_ms.so" "${LIBDST}/libusd_ms.so"
cp -L "${OPENUSD_ROOT}/lib/libtbb.so.2" "${LIBDST}/libtbb.so.2"
cp -R "${OPENUSD_ROOT}/lib/usd/." "${LIBDST}/usd/"
if [[ -d "${OPENUSD_ROOT}/plugin/usd" ]]; then
    cp -R "${OPENUSD_ROOT}/plugin/usd/." "${DST}/plugin/usd/"
fi
rm -rf "${LIBDST}/usd/usd/resources/codegenTemplates" "${LIBDST}/usd/usd/resources/codegenTemplates.meta"

patchelf --set-rpath '$ORIGIN:$ORIGIN/lib' "${DST}/libUnityUSDToolkitNative.so"
patchelf --set-rpath '$ORIGIN' "${LIBDST}/libusd_ms.so"
strip --strip-unneeded "${DST}/libUnityUSDToolkitNative.so" "${LIBDST}/libusd_ms.so" "${LIBDST}/libtbb.so.2"

# --- Check the result ----------------------------------------------------------------------
echo "[3/3] Checking the payload ..."
SHIPPED=("${DST}/libUnityUSDToolkitNative.so" "${LIBDST}/libusd_ms.so" "${LIBDST}/libtbb.so.2")

# grep without -q throughout: -q exits on the first match, the producer dies of SIGPIPE, and under
# pipefail the whole condition then reads as false -- the check would pass exactly when it matched.
if ldd "${DST}/libUnityUSDToolkitNative.so" | grep "not found" >/dev/null; then
    ldd "${DST}/libUnityUSDToolkitNative.so" >&2
    echo "Unresolved dependencies in the payload." >&2
    exit 1
fi

# __FILE__ and __PRETTY_FUNCTION__ bake absolute paths into the binaries; a build under a home
# directory ships the developer's user name to everyone who unpacks the package.
for file in "${SHIPPED[@]}"; do
    if strings -a "${file}" | grep '/home/' >/dev/null; then
        echo "${file} contains a /home/ path. Rebuild OpenUSD with build_openusd.py outside the" >&2
        echo "home directory, e.g. /opt/usd-26.05." >&2
        exit 1
    fi
done

echo
echo "Done. Output: ${DST}"
echo "Payload size: $(du -sh "${DST}" | cut -f1)"
if [[ -f "${DST}/.unverified-build" ]]; then
    echo "WARNING: this payload is marked unverified and cannot be committed or released." >&2
fi
echo "Next: python3 Native~/generate_native_hashes.py && python3 Native~/generate_sbom.py"
echo "Restart the Unity Editor to load the rebuilt plugin (no hot-reload for native libs)."
