"""Write the stage-warning fixtures as text .usda layers.

    python3 -I make_stage_warnings.py      (usd-core; writes next to this script)

  mesh_and_unknown   /Root with a one-quad Mesh and a prim of the made-up type UnknownSolid, which
                     authors a translate of its own the way a placed part would
  unknown_only       a single prim of the made-up type UnknownSolid: nothing to draw
  mesh_only          /Root with a one-quad Mesh

No schema defines UnknownSolid, so USD composes it as a prim of an unknown type.
"""

import os

from pxr import Gf, Usd, UsdGeom

UNKNOWN_TYPE = "UnknownSolid"


def define_quad(stage, path):
    mesh = UsdGeom.Mesh.Define(stage, path)
    mesh.CreatePointsAttr([Gf.Vec3f(0, 0, 0), Gf.Vec3f(1, 0, 0), Gf.Vec3f(1, 1, 0), Gf.Vec3f(0, 1, 0)])
    mesh.CreateFaceVertexCountsAttr([4])
    mesh.CreateFaceVertexIndicesAttr([0, 1, 2, 3])
    return mesh


def mesh_and_unknown(stage):
    root = UsdGeom.Xform.Define(stage, "/Root")
    define_quad(stage, "/Root/Quad")
    part = stage.DefinePrim("/Root/Part", UNKNOWN_TYPE)
    # Authored xform ops, as a placed part carries them; its type still defines none of them.
    UsdGeom.Xformable(part).AddTranslateOp().Set(Gf.Vec3d(2, 0, 0))
    return root.GetPrim()


def unknown_only(stage):
    return stage.DefinePrim("/Part", UNKNOWN_TYPE)


def mesh_only(stage):
    root = UsdGeom.Xform.Define(stage, "/Root")
    define_quad(stage, "/Root/Quad")
    return root.GetPrim()


FIXTURES = {
    "mesh_and_unknown": mesh_and_unknown,
    "unknown_only": unknown_only,
    "mesh_only": mesh_only,
}


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    for name, build in FIXTURES.items():
        stage = Usd.Stage.CreateNew(os.path.join(here, name + ".usda"))
        layer = stage.GetRootLayer()
        layer.documentation = "Stage-warning fixture %s, written by make_stage_warnings.py." % name
        stage.SetMetadata("metersPerUnit", 1.0)
        stage.SetMetadata("upAxis", "Y")
        stage.SetDefaultPrim(build(stage))
        layer.Save()


if __name__ == "__main__":
    main()
