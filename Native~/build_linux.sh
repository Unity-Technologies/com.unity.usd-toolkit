#!/usr/bin/env bash
set -euo pipefail

# Unity USD Toolkit - Linux native build
# libUnityUSDToolkitNative.so 를 OpenUSD(component) + Python3.11 로 빌드하고,
# 의존 .so 와 USD schema 플러그인을 Runtime/Plugins/x86_64/Linux/ 에 self-contained 로 구성한다.
#
# Usage:
#   Native~/build_linux.sh [--openusd-root PATH] [--python-root PATH]
#
# 기본값은 Isaac/Omniverse(packman)가 받아둔 OpenUSD/Python 을 가리킨다. 다른 환경이면 인자로 덮어쓴다.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

OPENUSD_ROOT="${OPENUSD_ROOT:-$HOME/.cache/packman/chk/usd.py311.manylinux_2_35_x86_64.stock.release/0.24.05.kit.7-gl.16400+05f48f24}"
PYTHON_ROOT="${PYTHON_ROOT:-$HOME/.cache/packman/chk/python/3.11.13+nv1-linux-x86_64}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --openusd-root) OPENUSD_ROOT="$2"; shift 2 ;;
        --python-root)  PYTHON_ROOT="$2";  shift 2 ;;
        -h|--help)
            echo "Usage: build_linux.sh [--openusd-root PATH] [--python-root PATH]"
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

USD_INC="${OPENUSD_ROOT}/include"
USD_LIB="${OPENUSD_ROOT}/lib"
PY_INC="${PYTHON_ROOT}/include/python3.11"
PY_LIB="${PYTHON_ROOT}/lib"
DST="${PACKAGE_DIR}/Runtime/Plugins/x86_64/Linux"
LIBDST="${DST}/lib"

if [[ ! -f "${USD_INC}/pxr/usd/usd/stage.h" ]]; then
    echo "OpenUSD headers not found: ${USD_INC}/pxr/usd/usd/stage.h" >&2
    echo "Pass --openusd-root to a valid OpenUSD install." >&2
    exit 1
fi
if [[ ! -f "${PY_INC}/pyconfig.h" ]]; then
    echo "Python headers not found: ${PY_INC}/pyconfig.h (Python-enabled OpenUSD needs them)" >&2
    echo "Pass --python-root to a Python 3.11 install matching the OpenUSD build." >&2
    exit 1
fi
command -v g++ >/dev/null 2>&1 || { echo "g++ not found." >&2; exit 1; }

mkdir -p "${LIBDST}/usd"

echo "[1/3] Compiling libUnityUSDToolkitNative.so ..."
g++ -std=c++17 -fPIC -shared -O2 \
    -DUNITY_USD_TOOLKIT_NATIVE_EXPORTS -fvisibility=hidden -Wno-deprecated \
    -I"${SCRIPT_DIR}/include" -I"${USD_INC}" -I"${PY_INC}" \
    "${SCRIPT_DIR}/src/UsdExporter.cpp" \
    -L"${USD_LIB}" -L"${PY_LIB}" \
    -lusd_usd -lusd_usdGeom -lusd_usdShade -lusd_sdf -lusd_tf -lusd_vt -lusd_gf \
    -lusd_work -lusd_plug -lusd_arch -lusd_ar -lusd_trace \
    -Wl,--disable-new-dtags -Wl,-rpath,'$ORIGIN/lib' \
    -o "${DST}/libUnityUSDToolkitNative.so"

echo "[2/3] Copying dependency closure (.so) into lib/ ..."
LD_LIBRARY_PATH="${USD_LIB}:${PY_LIB}" ldd "${DST}/libUnityUSDToolkitNative.so" \
    | awk -v u="${USD_LIB}/" -v p="${PY_LIB}/" '/=>/ { if (index($3, u) == 1 || index($3, p) == 1) print $3 }' \
    | sort -u \
    | while read -r f; do cp -L "$f" "${LIBDST}/$(basename "$f")"; done

echo "[3/3] Copying USD schema plugins (lib/usd) ..."
[[ -f "${USD_LIB}/usd/plugInfo.json" ]] && cp "${USD_LIB}/usd/plugInfo.json" "${LIBDST}/usd/"
for plugin in usd usdGeom usdShade sdf ar ndr sdr; do
    [[ -d "${USD_LIB}/usd/${plugin}" ]] && cp -R "${USD_LIB}/usd/${plugin}" "${LIBDST}/usd/"
done

echo
echo "Done. Output: ${DST}/libUnityUSDToolkitNative.so"
echo "Payload size: $(du -sh "${DST}" | cut -f1)"
echo "Restart the Unity Editor to load the rebuilt plugin (no hot-reload for native libs)."
