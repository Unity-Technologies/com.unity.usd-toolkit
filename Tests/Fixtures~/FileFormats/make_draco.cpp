// Writes draco_quad.drc the way OpenUSD writes Draco: usdDraco reads only meshes its own writer
// encoded (every attribute carries the USD name it came from), so a .drc from draco_encoder is
// refused ("Draco mesh has no valid positions"). This builds a USD quad in memory and encodes it
// with UsdDraco_WriteDraco, positions unquantized (qp 0) so they decode to these exact values.
//
//   ./make_draco.sh /Users/Shared/usd-26.05-abc/install
//
// The quad: a 1 x 1 square in the XY plane at z = -1, shifted by (0.5, 0.25), one polygon, with a
// vertex texture coordinate per corner.

#include <pxr/usd/usd/stage.h>
#include <pxr/usd/usdGeom/mesh.h>
#include <pxr/usd/usdGeom/primvarsAPI.h>
#include <pxr/usd/sdf/types.h>

#include <cstdio>
#include <string>

PXR_NAMESPACE_OPEN_SCOPE
bool UsdDraco_WriteDraco(const UsdGeomMesh& mesh, const std::string& fileName, int qp, int qt, int qn,
                         int cl, int preservePolygons, int preservePositionOrder, int preserveHoles);
PXR_NAMESPACE_CLOSE_SCOPE

PXR_NAMESPACE_USING_DIRECTIVE

int main(int argc, char** argv)
{
    const std::string output = argc > 1 ? argv[1] : "draco_quad.drc";
    UsdStageRefPtr stage = UsdStage::CreateInMemory();
    UsdGeomMesh mesh = UsdGeomMesh::Define(stage, SdfPath("/Quad"));
    mesh.CreatePointsAttr(VtValue(VtVec3fArray{
        GfVec3f(0.5f, 0.25f, -1.0f), GfVec3f(1.5f, 0.25f, -1.0f),
        GfVec3f(1.5f, 1.25f, -1.0f), GfVec3f(0.5f, 1.25f, -1.0f)}));
    mesh.CreateFaceVertexCountsAttr(VtValue(VtIntArray{4}));
    mesh.CreateFaceVertexIndicesAttr(VtValue(VtIntArray{0, 1, 2, 3}));
    UsdGeomPrimvar st = UsdGeomPrimvarsAPI(mesh).CreatePrimvar(
        TfToken("st"), SdfValueTypeNames->TexCoord2fArray, UsdGeomTokens->vertex);
    st.Set(VtVec2fArray{GfVec2f(0, 0), GfVec2f(1, 0), GfVec2f(1, 1), GfVec2f(0, 1)});

    // qp 0: positions unquantized; texture coordinates at 12 bits, normals default; compression
    // level 7; keep the polygon and the point order, no holes.
    if (!UsdDraco_WriteDraco(mesh, output, 0, 12, 10, 7, 1, 1, 0))
    {
        std::fprintf(stderr, "UsdDraco_WriteDraco failed\n");
        return 1;
    }

    std::printf("wrote %s\n", output.c_str());
    return 0;
}
