"""Export the open composed-gripper .blend to the USD file set (uses Blender's bundled pxr).

  blender --background composed_gripper_v1_prismatic.blend --python export_usd.py -- v1_prismatic

Output (<repo>/<variant>/):
  gripper.usda                   root layer: defaultPrim, references + physics payload, PhysicsScene
  geometry/<link>.usd            render geometry per link, link-local coordinates (referenced)
  geometry/materials.usd         UsdPreviewSurface materials (referenced)
  physics/gripper_physics.usd    payload: articulation root, bodies, mass, colliders, joints, drives
Geometry comes from the Blender objects; joints, masses and drives come from gripper_spec.py.
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector
from pxr import Gf, Sdf, Usd, UsdGeom, UsdPhysics, UsdShade, Vt

SOURCE_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SOURCE_DIR)
import gripper_spec as spec  # noqa: E402

ROOT = "composed_gripper"
ROOT_PATH = Sdf.Path("/" + ROOT)
LICENSE = "CC-BY-4.0. Original work; contains no third-party geometry, names or branding."


def stage_metadata(stage, default_prim=None):
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.z)
    UsdGeom.SetStageMetersPerUnit(stage, 1.0)
    UsdPhysics.SetStageKilogramsPerUnit(stage, 1.0)
    if default_prim:
        stage.SetDefaultPrim(stage.GetPrimAtPath("/" + default_prim))


def clean(v, eps=1e-7):
    """Snap float noise (e.g. 4e-8 from a 180 deg rotation) to exact values."""
    return [0.0 if abs(x) < eps else round(x, 9) for x in v]


def gf_quat(q):
    q = q.normalized()
    w, x, y, z = clean((q.w, q.x, q.y, q.z))
    return Gf.Quatf(w, Gf.Vec3f(x, y, z))


# ----------------------------------------------------------------------------- geometry
def mesh_arrays(mesh, poly_filter=None):
    polys = [p for p in mesh.polygons if poly_filter is None or poly_filter(p)]
    used = sorted({v for p in polys for v in p.vertices})
    remap = {v: i for i, v in enumerate(used)}
    points = [tuple(mesh.vertices[v].co) for v in used]
    counts = [p.loop_total for p in polys]
    indices = [remap[mesh.loops[li].vertex_index] for p in polys for li in p.loop_indices]
    normals = [tuple(mesh.corner_normals[li].vector) for p in polys for li in p.loop_indices]
    return points, counts, indices, normals


def define_mesh(stage, path, points, counts, indices, normals=None):
    m = UsdGeom.Mesh.Define(stage, path)
    m.CreatePointsAttr(Vt.Vec3fArray([Gf.Vec3f(*p) for p in points]))
    m.CreateFaceVertexCountsAttr(Vt.IntArray(counts))
    m.CreateFaceVertexIndicesAttr(Vt.IntArray(indices))
    m.CreateSubdivisionSchemeAttr(UsdGeom.Tokens.none)
    m.CreateExtentAttr(UsdGeom.Mesh.ComputeExtent(m.GetPointsAttr().Get()))
    if normals:
        m.CreateNormalsAttr(Vt.Vec3fArray([Gf.Vec3f(*n) for n in normals]))
        m.SetNormalsInterpolation(UsdGeom.Tokens.faceVarying)
    return m


def write_link_geometry(obj, path):
    stage = Usd.Stage.CreateNew(path)
    UsdGeom.Xform.Define(stage, "/" + obj.name)
    stage_metadata(stage, obj.name)
    mesh = obj.data
    meshes = []
    for idx, mat in enumerate(mesh.materials):
        pts, counts, inds, nrm = mesh_arrays(mesh, lambda p, i=idx: p.material_index == i)
        name = f"visual_{mat.name}"
        define_mesh(stage, f"/{obj.name}/{name}", pts, counts, inds, nrm)
        meshes.append((name, mat.name))
    stage.GetRootLayer().Save()
    return meshes


def write_materials(path):
    stage = Usd.Stage.CreateNew(path)
    UsdGeom.Scope.Define(stage, "/Materials")
    stage.SetDefaultPrim(stage.GetPrimAtPath("/Materials"))
    for name, (color, metallic, roughness) in spec.MATERIALS.items():
        mat = UsdShade.Material.Define(stage, f"/Materials/{name}")
        sh = UsdShade.Shader.Define(stage, f"/Materials/{name}/PreviewSurface")
        sh.CreateIdAttr("UsdPreviewSurface")
        sh.CreateInput("diffuseColor", Sdf.ValueTypeNames.Color3f).Set(Gf.Vec3f(*color))
        sh.CreateInput("metallic", Sdf.ValueTypeNames.Float).Set(metallic)
        sh.CreateInput("roughness", Sdf.ValueTypeNames.Float).Set(roughness)
        mat.CreateSurfaceOutput().ConnectToSource(sh.ConnectableAPI(), "surface")
    stage.GetRootLayer().Save()


# ----------------------------------------------------------------------------- mass properties
def mass_properties(collider_meshes, mass):
    """Uniform-density mass properties of a union of closed triangle meshes (link-local)."""
    vol = 0.0
    first = np.zeros(3)
    second = np.zeros((3, 3))
    for mesh in collider_meshes:
        co = np.array([tuple(v.co) for v in mesh.vertices])
        for p in mesh.polygons:
            vs = [co[i] for i in p.vertices]
            for k in range(1, len(vs) - 1):
                a, b, c = vs[0], vs[k], vs[k + 1]
                d = float(np.dot(a, np.cross(b, c)))
                vol += d / 6.0
                s = a + b + c
                first += d / 24.0 * s
                second += d / 120.0 * (np.outer(a, a) + np.outer(b, b) + np.outer(c, c) + np.outer(s, s))
    rho = mass / vol
    com = first / vol
    cov = rho * second - mass * np.outer(com, com)
    inertia = np.trace(cov) * np.eye(3) - cov
    evals, evecs = np.linalg.eigh(inertia)
    if np.linalg.det(evecs) < 0:
        evecs[:, 2] *= -1
    rot = Matrix([list(r) for r in evecs]).to_quaternion()
    return {"volume_m3": vol, "density": rho, "com": com.tolist(), "diag": evals.tolist(), "axes": rot}


# ----------------------------------------------------------------------------- physics payload
def side(name):
    return "left" if name.startswith("left_") else "right" if name.startswith("right_") else None


def write_physics(path, links, joints, objs):
    stage = Usd.Stage.Open(Sdf.Layer.CreateNew(path, args={"format": "usda"}))  # readable .usd
    stage_metadata(stage)
    root = stage.OverridePrim(ROOT_PATH)
    stage.SetDefaultPrim(root)
    UsdPhysics.ArticulationRootAPI.Apply(root)
    stage.OverridePrim(ROOT_PATH.AppendChild("Geometry"))
    UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Physics"))

    pad_mat_path = ROOT_PATH.AppendPath("Physics/pad_physics_material")
    pad_mat = UsdShade.Material.Define(stage, pad_mat_path)
    pm = UsdPhysics.MaterialAPI.Apply(pad_mat.GetPrim())
    pm.CreateStaticFrictionAttr(spec.PAD_MATERIAL["staticFriction"])
    pm.CreateDynamicFrictionAttr(spec.PAD_MATERIAL["dynamicFriction"])
    pm.CreateRestitutionAttr(spec.PAD_MATERIAL["restitution"])

    names = [l["name"] for l in links]
    summary = {"links": {}, "joints": []}
    for link in links:
        name = link["name"]
        lp = ROOT_PATH.AppendPath(f"Geometry/{name}")
        prim = stage.OverridePrim(lp)
        UsdPhysics.RigidBodyAPI.Apply(prim)

        cols = [c for c in objs[name].children if c.get("collider")]
        mp = mass_properties([c.data for c in cols], link["mass"])
        mapi = UsdPhysics.MassAPI.Apply(prim)
        mapi.CreateMassAttr(link["mass"])
        mapi.CreateCenterOfMassAttr(Gf.Vec3f(*mp["com"]))
        mapi.CreateDiagonalInertiaAttr(Gf.Vec3f(*mp["diag"]))
        mapi.CreatePrincipalAxesAttr(gf_quat(mp["axes"]))

        # Filter self-collision for every pair on the same side and against the base;
        # left vs right stays enabled so the jaws cannot pass through each other.
        partners = [n for n in names if n != name and
                    (side(n) is None or side(name) is None or side(n) == side(name))]
        UsdPhysics.FilteredPairsAPI.Apply(prim).CreateFilteredPairsRel().SetTargets(
            [ROOT_PATH.AppendPath(f"Geometry/{n}") for n in partners])

        for i, c in enumerate(sorted(cols, key=lambda o: o.name)):
            pts, counts, inds, _ = mesh_arrays(c.data)
            m = define_mesh(stage, lp.AppendChild(f"collision_{i}"), pts, counts, inds)
            m.CreatePurposeAttr(UsdGeom.Tokens.guide)
            UsdPhysics.CollisionAPI.Apply(m.GetPrim())
            UsdPhysics.MeshCollisionAPI.Apply(m.GetPrim()).CreateApproximationAttr(UsdPhysics.Tokens.convexHull)
            if link.get("pad"):
                UsdShade.MaterialBindingAPI.Apply(m.GetPrim()).Bind(
                    pad_mat, UsdShade.Tokens.weakerThanDescendants, "physics")
        summary["links"][name] = {"mass": link["mass"], "com": mp["com"], "diag": mp["diag"],
                                  "colliders": len(cols), "volume_cm3": mp["volume_m3"] * 1e6}

    flip = Matrix.Rotation(math.pi, 4, "Z")
    for j in joints:
        jp = ROOT_PATH.AppendPath(f"Physics/{j['name']}")
        kind = j["type"]
        if kind == "fixed":
            joint = UsdPhysics.FixedJoint.Define(stage, jp)
        elif kind == "prismatic":
            joint = UsdPhysics.PrismaticJoint.Define(stage, jp)
        else:
            joint = UsdPhysics.RevoluteJoint.Define(stage, jp)
        jw = Matrix.Translation(Vector(j["pivot"])) @ (flip if j.get("flip") else Matrix.Identity(4))
        for k, body in ((0, j["body0"]), (1, j["body1"])):
            local = (objs[body].matrix_world.inverted() @ jw) if body else jw
            if body:
                rel = joint.CreateBody0Rel() if k == 0 else joint.CreateBody1Rel()
                rel.SetTargets([ROOT_PATH.AppendPath(f"Geometry/{body}")])
            pos = Gf.Vec3f(*clean(local.to_translation()))
            rot = gf_quat(local.to_quaternion())
            if k == 0:
                joint.CreateLocalPos0Attr(pos)
                joint.CreateLocalRot0Attr(rot)
            else:
                joint.CreateLocalPos1Attr(pos)
                joint.CreateLocalRot1Attr(rot)
        if kind in ("prismatic", "revolute"):
            joint.CreateAxisAttr(j["axis"])
            if j.get("limits"):
                joint.CreateLowerLimitAttr(j["limits"][0])
                joint.CreateUpperLimitAttr(j["limits"][1])
        if j.get("exclude"):
            joint.CreateExcludeFromArticulationAttr(True)
        d = j.get("drive")
        if d:
            drive = UsdPhysics.DriveAPI.Apply(joint.GetPrim(), d["kind"])
            drive.CreateTypeAttr("force")
            drive.CreateStiffnessAttr(d["stiffness"])
            drive.CreateDampingAttr(d["damping"])
            drive.CreateMaxForceAttr(d["maxForce"])
            drive.CreateTargetPositionAttr(0.0)
            drive.CreateTargetVelocityAttr(0.0)
        summary["joints"].append({k: v for k, v in j.items() if k != "pivot"} | {"pivot": list(j["pivot"])})
    stage.GetRootLayer().Save()
    return summary


# ----------------------------------------------------------------------------- root layer
def write_root(path, variant, links, objs, link_meshes):
    stage = Usd.Stage.CreateNew(path)
    root = UsdGeom.Xform.Define(stage, ROOT_PATH).GetPrim()
    stage_metadata(stage, ROOT)
    layer = stage.GetRootLayer()
    layer.customLayerData = {"creator": "composed_gripper export_usd.py", "license": LICENSE, "variant": variant}
    layer.documentation = ("Composed Gripper Physics: two-finger parallel gripper (%s). Physics lives in the payload "
                           "physics/gripper_physics.usd, selected by variantSet physics (usdphysics | none)." % variant)
    Usd.ModelAPI(root).SetKind("component")
    Usd.ModelAPI(root).SetAssetName(ROOT)
    # Like the reference: a variantSet picks the physics payload. "none" gives a visual-only asset
    # (unloading the payload instead hides every descendant of the root, per USD load rules).
    vset = root.GetVariantSets().AddVariantSet("physics")
    vset.AddVariant("none")
    vset.AddVariant("usdphysics")
    vset.SetVariantSelection("usdphysics")
    with vset.GetVariantEditContext():
        root.GetPayloads().AddPayload("./physics/gripper_physics.usd")

    UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Materials")).GetPrim().GetReferences().AddReference(
        "./geometry/materials.usd")
    UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Geometry"))
    for link in links:
        name = link["name"]
        lp = ROOT_PATH.AppendPath(f"Geometry/{name}")
        x = UsdGeom.Xform.Define(stage, lp)
        x.GetPrim().GetReferences().AddReference(f"./geometry/{name}.usd")
        mw = objs[name].matrix_world
        x.AddTranslateOp().Set(Gf.Vec3d(*mw.to_translation()))
        x.AddOrientOp().Set(gf_quat(mw.to_quaternion()))
        for mesh_name, mat_name in link_meshes[name]:
            mp = stage.OverridePrim(lp.AppendChild(mesh_name))
            UsdShade.MaterialBindingAPI.Apply(mp).Bind(
                UsdShade.Material(stage.GetPrimAtPath(ROOT_PATH.AppendPath(f"Materials/{mat_name}"))))

    scene = UsdPhysics.Scene.Define(stage, "/PhysicsScene")
    scene.CreateGravityDirectionAttr(Gf.Vec3f(0, 0, -1))
    scene.CreateGravityMagnitudeAttr(9.81)
    layer.Save()


def export(variant, repo_dir):
    out = os.path.join(repo_dir, variant)
    os.makedirs(os.path.join(out, "geometry"), exist_ok=True)
    os.makedirs(os.path.join(out, "physics"), exist_ok=True)
    links, joints = spec.get_variant(variant)
    objs = {o.name: o for o in bpy.data.objects if o.type == "MESH" and not o.get("collider")}
    missing = [l["name"] for l in links if l["name"] not in objs]
    assert not missing, f"blend is missing link objects: {missing}"

    link_meshes = {l["name"]: write_link_geometry(objs[l["name"]], os.path.join(out, "geometry", l["name"] + ".usd"))
                   for l in links}
    write_materials(os.path.join(out, "geometry", "materials.usd"))
    summary = write_physics(os.path.join(out, "physics", "gripper_physics.usd"), links, joints, objs)
    write_root(os.path.join(out, "gripper.usda"), variant, links, objs, link_meshes)
    summary["triangles"] = {n: sum(p.loop_total - 2 for p in objs[n].data.polygons) for n in objs}
    return out, summary


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    print(export(argv[0], os.path.dirname(SOURCE_DIR))[0])
