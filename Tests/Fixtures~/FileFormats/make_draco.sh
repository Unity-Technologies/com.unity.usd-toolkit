#!/bin/sh
# Builds make_draco.cpp against an OpenUSD install built with --draco and writes draco_quad.drc.
#   ./make_draco.sh /Users/Shared/usd-26.05-abc/install
set -e
install="$1"
tool="$(mktemp -d)/make_draco"
c++ -std=c++17 -O1 make_draco.cpp -I"${install}/include" -L"${install}/lib" -lusd_ms -ltbb \
    -Wl,-rpath,"${install}/lib" -o "${tool}"
"${tool}" draco_quad.drc
rm -f "${tool}"

# draco_polygons.drc is not made here. It is Blender's Suzanne (the Monkey of a kitchen-sink Alembic
# file Blender 4.4 wrote, read at frame 1) written by UsdDraco_WriteDraco with qp 0, qt 0, qn 0,
# cl 7, polygons and position order preserved: 467 quads and 34 triangles with boundaries. OpenUSD
# 26.05's usdDraco crashed reading it until Native~/patches/openusd-26.05-file-format-hardening.patch.
