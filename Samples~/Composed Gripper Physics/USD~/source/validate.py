"""Validate the gripper file sets with stock OpenUSD (pip install usd-core numpy).

  python validate.py            -> runs every check on ../v1_prismatic, ../v2_linkage, ../v3_composed

Checks: usdchecker-equivalent compliance, payload loaded/unloaded composition, articulation is a
tree (loop joints excluded), joint frames coincide at rest, colliders are convex, and no
unfiltered collider pair interpenetrates anywhere over the full stroke (driven forward kinematics).
v3_composed additionally: both mechanism variants, every composition arc present in scene.usda,
and that inherits / specializes / relocates resolve the values they are meant to.
"""
import itertools
import math
import os
import sys

import numpy as np
from pxr import Gf, Pcp, Usd, UsdGeom, UsdPhysics, UsdShade, UsdUtils

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOL = 1e-5


def compliance(path):
    """usdchecker equivalent. OpenUSD >= 25.x usdchecker runs the UsdValidation framework:
    every registered validator except the usdz/ARKit-only ones. Older builds: ComplianceChecker."""
    if not hasattr(UsdUtils, "ComplianceChecker"):
        from pxr import UsdValidation
        validators = [v for v in UsdValidation.ValidationRegistry().GetOrLoadAllValidators()
                      if "UsdzValidators" not in v.GetMetadata().GetKeywords()]
        stage = Usd.Stage.Open(path, Usd.Stage.LoadAll)
        found = UsdValidation.ValidationContext(validators).Validate(stage)
        fmt = lambda e: f"[{e.GetValidator().GetMetadata().name if e.GetValidator() else '?'}] {e.GetMessage()}"
        errors = [fmt(e) for e in found if e.GetType() == UsdValidation.ValidationErrorType.Error]
        warnings = [fmt(e) for e in found if e.GetType() != UsdValidation.ValidationErrorType.Error]
        return errors, [], warnings
    checker = UsdUtils.ComplianceChecker(arkit=False, skipARKitRootLayerCheck=False,
                                         rootPackageOnly=False, skipVariants=False, verbose=False)
    checker.CheckCompliance(path)
    return checker.GetErrors(), checker.GetFailedChecks(), checker.GetWarnings()


def mat(xf):
    """Gf.Matrix4d (row-vector convention) -> numpy column-vector 4x4."""
    return np.array(xf).T


def pose_matrix(pos, quat):
    m = Gf.Matrix4d().SetRotate(Gf.Rotation(Gf.Quatd(quat))).SetTranslateOnly(Gf.Vec3d(pos))
    return mat(m)


def under(path, prefix):
    path = str(path)
    return path == prefix or path.startswith(prefix + "/")


def joint_info(stage, prefix):
    joints = []
    for p in stage.Traverse():
        if not p.IsA(UsdPhysics.Joint) or not under(p.GetPath(), prefix):
            continue
        j = UsdPhysics.Joint(p)
        b0 = j.GetBody0Rel().GetTargets()
        b1 = j.GetBody1Rel().GetTargets()
        info = {"name": p.GetName(), "type": p.GetTypeName(), "prim": p,
                "body0": str(b0[0]) if b0 else None, "body1": str(b1[0]) if b1 else None,
                "L0": pose_matrix(j.GetLocalPos0Attr().Get(), j.GetLocalRot0Attr().Get()),
                "L1": pose_matrix(j.GetLocalPos1Attr().Get(), j.GetLocalRot1Attr().Get()),
                "exclude": bool(j.GetExcludeFromArticulationAttr().Get())}
        if p.IsA(UsdPhysics.RevoluteJoint) or p.IsA(UsdPhysics.PrismaticJoint):
            info["axis"] = p.GetAttribute("physics:axis").Get()
            info["limits"] = (p.GetAttribute("physics:lowerLimit").Get(), p.GetAttribute("physics:upperLimit").Get())
        joints.append(info)
    return joints


def motion(j, q):
    a = {"X": 0, "Y": 1, "Z": 2}[j["axis"]]
    m = np.eye(4)
    if j["type"] == "PhysicsPrismaticJoint":
        m[a, 3] = q
    else:
        c, s = math.cos(math.radians(q)), math.sin(math.radians(q))
        i, k = [(1, 2), (2, 0), (0, 1)][a]
        m[i, i], m[i, k], m[k, i], m[k, k] = c, -s, s, c
    return m


def forward_kinematics(joints, rest, q):
    """World pose of every body for joint values q, walking the articulation tree from the root."""
    world = {}
    tree = [j for j in joints if not j["exclude"]]
    root = next(j for j in tree if j["body0"] is None)
    world[root["body1"]] = root["L0"] @ np.linalg.inv(root["L1"])
    pending = [j for j in tree if j is not root]
    while pending:
        for j in list(pending):
            if j["body0"] in world:
                m = motion(j, q.get(j["name"], 0.0)) if "axis" in j else np.eye(4)
                world[j["body1"]] = world[j["body0"]] @ j["L0"] @ m @ np.linalg.inv(j["L1"])
                pending.remove(j)
    return world


def hull_planes(pts, faces):
    planes = []
    c = pts.mean(axis=0)
    for f in faces:
        n = np.cross(pts[f[1]] - pts[f[0]], pts[f[2]] - pts[f[0]])
        if np.linalg.norm(n) < 1e-12:
            continue
        n /= np.linalg.norm(n)
        d = np.dot(n, pts[f[0]])
        if np.dot(n, c) > d:
            n, d = -n, -d
        planes.append((n, d))
    return planes


def separated(pa, pb, axes):
    for ax in axes:
        n = np.linalg.norm(ax)
        if n < 1e-9:
            continue
        ax = ax / n
        a, b = pa @ ax, pb @ ax
        if a.max() < b.min() - 1e-6 or b.max() < a.min() - 1e-6:
            return True
    return False


def colliders(stage, prefix):
    out = {}
    for p in stage.Traverse():
        if p.HasAPI(UsdPhysics.CollisionAPI) and under(p.GetPath(), prefix):
            m = UsdGeom.Mesh(p)
            pts = np.array(m.GetPointsAttr().Get(), dtype=float)
            counts, idx = m.GetFaceVertexCountsAttr().Get(), m.GetFaceVertexIndicesAttr().Get()
            faces, k = [], 0
            for c in counts:
                faces.append(list(idx[k:k + c]))
                k += c
            body = str(p.GetParent().GetPath())
            approx = UsdPhysics.MeshCollisionAPI(p).GetApproximationAttr().Get()
            material = UsdShade.MaterialBindingAPI(p).ComputeBoundMaterial("physics")[0]
            out.setdefault(body, []).append({"path": str(p.GetPath()), "pts": pts, "faces": faces, "approx": approx,
                                             "material": str(material.GetPath()) if material else None})
    return out


def composition_checks(root):
    """Payload unloaded / physics=none / payload loaded behaviour of an asset root layer."""
    count = lambda s, pred: sum(1 for p in s.Traverse() if pred(p))
    is_phys = lambda p: p.HasAPI(UsdPhysics.RigidBodyAPI) or p.IsA(UsdPhysics.Joint)
    is_mesh = lambda p: p.IsA(UsdGeom.Mesh)

    # Unloaded payload: USD keeps the payload prim but composes none of its descendants.
    unloaded = Usd.Stage.Open(root, Usd.Stage.LoadNone)
    dp = unloaded.GetDefaultPrim()
    print(f"payload unloaded: default prim {dp.GetPath()} valid={dp.IsValid()} loaded={dp.IsLoaded()}, "
          f"{count(unloaded, is_phys)} physics prims")
    ok = dp.IsValid() and not dp.IsLoaded() and count(unloaded, is_phys) == 0

    visual = Usd.Stage.Open(root, Usd.Stage.LoadAll)
    visual.SetEditTarget(visual.GetSessionLayer())  # don't dirty the shared root layer
    visual.GetDefaultPrim().GetVariantSet("physics").SetVariantSelection("none")
    print(f"variant physics=none: {count(visual, is_mesh)} render meshes, {count(visual, is_phys)} physics prims")
    return ok and count(visual, is_mesh) > 0 and count(visual, is_phys) == 0


def check(variant):
    root = os.path.join(REPO, variant, "gripper.usda")
    print(f"\n=== {variant}")
    ok = compliance_report(root)
    ok &= composition_checks(root)
    ok &= articulation_checks(Usd.Stage.Open(root, Usd.Stage.LoadAll), "/composed_gripper")
    print("RESULT:", "PASS" if ok else "FAIL")
    return ok


def compliance_report(path):
    errors, failed, warnings = compliance(path)
    print(f"compliance {os.path.basename(path)}: {len(errors)} errors, {len(failed)} failed checks, "
          f"{len(warnings)} warnings")
    for e in errors + failed + warnings:
        print("   ", e)
    return not errors and not failed


def articulation_checks(stage, prefix):
    """Kinematic checks for the one articulation whose root prim is prefix."""
    ok = True
    bodies = [str(p.GetPath()) for p in stage.Traverse()
              if p.HasAPI(UsdPhysics.RigidBodyAPI) and under(p.GetPath(), prefix)]
    massless = [b for b in bodies if not stage.GetPrimAtPath(b).HasAPI(UsdPhysics.MassAPI)]
    art_roots = [str(p.GetPath()) for p in stage.Traverse()
                 if p.HasAPI(UsdPhysics.ArticulationRootAPI) and under(p.GetPath(), prefix)]
    joints = joint_info(stage, prefix)
    cols = colliders(stage, prefix)
    print(f"{prefix}: {len(bodies)} rigid bodies, {len(joints)} joints, "
          f"{sum(len(v) for v in cols.values())} colliders, articulation roots {art_roots}")
    ok &= not massless and len(art_roots) == 1 and all(b in cols for b in bodies)

    # Articulation must be a spanning tree over the bodies (plus one fixed joint to world).
    tree = [j for j in joints if not j["exclude"]]
    edges = [(j["body0"], j["body1"]) for j in tree if j["body0"]]
    parents = {}
    for b0, b1 in edges:
        ok &= b1 not in parents
        parents[b1] = b0
    world_joints = [j for j in tree if j["body0"] is None]
    is_tree = len(edges) == len(bodies) - 1 and len(world_joints) == 1 and len(parents) == len(edges)
    print(f"articulation tree: {is_tree} ({len(edges)} tree joints, "
          f"{len(joints) - len(tree)} excluded loop joints, root fixed to world: {len(world_joints) == 1})")
    ok &= is_tree

    # Rest pose: each joint's two frames coincide in world space.
    xc = UsdGeom.XformCache()
    worst = 0.0
    for j in joints:
        w0 = mat(xc.GetLocalToWorldTransform(stage.GetPrimAtPath(j["body0"]))) if j["body0"] else np.eye(4)
        w1 = mat(xc.GetLocalToWorldTransform(stage.GetPrimAtPath(j["body1"])))
        worst = max(worst, np.abs(w0 @ j["L0"] - w1 @ j["L1"]).max())
    print(f"joint frames coincide at rest: max deviation {worst:.2e}")
    ok &= worst < TOL

    # Colliders: convexHull approximation and actually convex.
    nonconvex = []
    for b, cs in cols.items():
        for c in cs:
            planes = hull_planes(c["pts"], c["faces"])
            if c["approx"] != "convexHull" or any((c["pts"] @ n - d).max() > 1e-6 for n, d in planes):
                nonconvex.append(c["path"])
    print(f"colliders convexHull + convex: {not nonconvex} {nonconvex or ''}")
    ok &= not nonconvex

    # Unfiltered pairs must never interpenetrate over the stroke (fingers driven symmetrically).
    filtered = set()
    for b in bodies:
        for t in UsdPhysics.FilteredPairsAPI(stage.GetPrimAtPath(b)).GetFilteredPairsRel().GetTargets():
            filtered.add(frozenset((b, str(t))))
    active = [p for p in itertools.combinations(bodies, 2) if frozenset(p) not in filtered]
    driven = {j["name"]: j for j in tree if "limits" in j}
    steps = np.linspace(0.0, 1.0, 21)
    worst_gap, hits = math.inf, []
    for s in steps:
        q = {}
        for name, j in driven.items():
            lo, hi = j["limits"]
            q[name] = s * (hi if abs(hi) > abs(lo) else lo)
        world = forward_kinematics(joints, None, q)
        for a, b in active:
            for ca in cols[a]:
                for cb in cols[b]:
                    pa = (world[a] @ np.c_[ca["pts"], np.ones(len(ca["pts"]))].T).T[:, :3]
                    pb = (world[b] @ np.c_[cb["pts"], np.ones(len(cb["pts"]))].T).T[:, :3]
                    na = [world[a][:3, :3] @ n for n, _ in hull_planes(ca["pts"], ca["faces"])]
                    nb = [world[b][:3, :3] @ n for n, _ in hull_planes(cb["pts"], cb["faces"])]
                    ea = [pa[f[(i + 1) % len(f)]] - pa[f[i]] for f in ca["faces"] for i in range(len(f))]
                    eb = [pb[f[(i + 1) % len(f)]] - pb[f[i]] for f in cb["faces"] for i in range(len(f))]
                    axes = na + nb + [np.cross(x, y) for x in ea[::3] for y in eb[::3]]
                    if not separated(pa, pb, axes):
                        hits.append((round(float(s), 2), ca["path"].split("/")[-2], cb["path"].split("/")[-2]))
        if s == 1.0:
            pads = [b for b in bodies if any(c["material"] for c in cols[b])]
            xs = [(world[p] @ np.c_[cols[p][0]["pts"], np.ones(len(cols[p][0]["pts"]))].T)[0] for p in pads]
            worst_gap = min(xs[1]) - max(xs[0]) if xs[0].mean() < xs[1].mean() else min(xs[0]) - max(xs[1])
    print(f"unfiltered pairs: {len(active)}; interpenetrations over stroke: {len(hits)} {hits[:6] or ''}")
    print(f"pad gap at full close: {worst_gap * 1000:.2f} mm")
    ok &= not hits and worst_gap > 0

    # Loop closure: the excluded joint's frames must still coincide when the tree is driven.
    loops = [j for j in joints if j["exclude"]]
    if loops:
        dev = 0.0
        for s in steps:
            q = {n: s * (j["limits"][1] if abs(j["limits"][1]) > abs(j["limits"][0]) else j["limits"][0])
                 for n, j in driven.items()}
            world = forward_kinematics(joints, None, q)
            for j in loops:
                dev = max(dev, np.abs((world[j["body0"]] @ j["L0"])[:3, 3] - (world[j["body1"]] @ j["L1"])[:3, 3]).max())
        print(f"loop joints stay closed under coordinated drive targets: max gap {dev:.2e} m")
        ok &= dev < TOL
    return ok


# ----------------------------------------------------------------------------- v3_composed
ARC_NAMES = {Pcp.ArcTypeReference: "reference", Pcp.ArcTypePayload: "payload", Pcp.ArcTypeInherit: "inherit",
             Pcp.ArcTypeSpecialize: "specialize", Pcp.ArcTypeVariant: "variant", Pcp.ArcTypeRelocate: "relocate"}


def arc_census(stage):
    """Number of prims whose prim index contains each arc type, plus the sublayer counts."""
    found = {}
    payload_stack = 0

    def walk(node, seen):
        nonlocal payload_stack
        name = ARC_NAMES.get(node.arcType)
        if name:
            seen.add(name)
        if node.arcType == Pcp.ArcTypePayload:
            payload_stack = max(payload_stack, len(node.layerStack.layers))
        for c in node.children:
            walk(c, seen)

    for p in stage.Traverse():
        seen = set()
        walk(p.GetPrimIndex().rootNode, seen)
        for name in seen:
            found[name] = found.get(name, 0) + 1
    found["sublayer (root stack)"] = len(stage.GetLayerStack(includeSessionLayers=False)) - 1
    found["sublayer (payload stack)"] = max(payload_stack - 1, 0)
    return found


def resolved(stage, path, attr):
    """(value, arc type of the node that supplied it) for a composed attribute."""
    a = stage.GetPrimAtPath(path).GetAttribute(attr)
    node = a.GetResolveInfo().GetNode()
    return a.Get(), ARC_NAMES.get(node.arcType, "local") if node else None


def selection_source(stage, path, vset):
    """(selection, arc that supplied it): the strongest opinion in the prim index, walked in strength order.
    'local' = the stage's root layer, 'sublayer' = another layer of the root layer stack."""
    prim = stage.GetPrimAtPath(path)
    root_layer = stage.GetRootLayer()

    def walk(node):
        if not node.isInert:
            for layer in node.layerStack.layers:
                spec = layer.GetPrimAtPath(node.path)
                if spec and vset in spec.variantSelections:
                    if node.arcType == Pcp.ArcTypeRoot:
                        arc = "local" if layer == root_layer else "sublayer"
                    else:
                        arc = ARC_NAMES.get(node.arcType, str(node.arcType))
                    return spec.variantSelections[vset], arc
        for child in node.children:
            found = walk(child)
            if found:
                return found
        return None

    found = walk(prim.GetPrimIndex().rootNode)
    applied = prim.GetVariantSet(vset).GetVariantSelection()
    return found if found and found[0] == applied else (applied, "?")


# gripper -> variant set -> (selection, arc that supplies it); see SCENE_GRIPPERS in export_composed.py
SCENE_SELECTIONS = {
    "gripper_a": {"preset": ("prismatic", "reference"), "mechanism": ("prismatic", "variant"),
                  "pads": ("grip", "payload")},
    "gripper_b": {"preset": ("linkage", "local"), "mechanism": ("linkage", "variant"),
                  "pads": ("smooth", "sublayer")},
    "gripper_c": {"mechanism": ("linkage", "inherit"), "pads": ("grip", "payload")},
    "gripper_d": {"mechanism": ("linkage", "reference"), "pads": ("smooth", "reference")},
}
SCENE_BODIES = {"gripper_a": 5, "gripper_b": 9, "gripper_c": 9, "gripper_d": 9}
PRESETS = {"prismatic": ("prismatic", "grip", 5), "linkage": ("linkage", "grip", 9),
           "linkage_smooth": ("linkage", "smooth", 9)}


def same(got, want):
    if isinstance(want, float):
        return got is not None and abs(got - want) < 1e-6
    if isinstance(want, tuple):
        return isinstance(got, tuple) and len(got) == len(want) and all(map(same, got, want))
    return got == want


def expect(label, got, want):
    good = same(got, want)
    print(f"  {'ok ' if good else 'BAD'} {label}: {got}" + ("" if good else f" (expected {want})"))
    return good


def check_composed():
    folder = os.path.join(REPO, "v3_composed")
    asset, scene_path = os.path.join(folder, "gripper.usda"), os.path.join(folder, "scene.usda")
    print("\n=== v3_composed")
    ok = compliance_report(asset) & compliance_report(scene_path)
    ok &= composition_checks(asset)

    for mechanism, n_bodies in (("prismatic", 5), ("linkage", 9)):
        stage = Usd.Stage.Open(asset, Usd.Stage.LoadAll)
        stage.SetEditTarget(stage.GetSessionLayer())
        stage.GetDefaultPrim().GetVariantSet("mechanism").SetVariantSelection(mechanism)
        print(f"--- asset, mechanism={mechanism}")
        ok &= articulation_checks(stage, "/composed_gripper")
        ok &= expect("rigid bodies", sum(1 for p in stage.Traverse() if p.HasAPI(UsdPhysics.RigidBodyAPI)), n_bodies)
        # Every non-fixed joint gets its axis from a class prim, not from its own spec.
        sources = {resolved(stage, j.GetPath(), "physics:axis")[1] for j in stage.Traverse()
                   if j.IsA(UsdPhysics.Joint) and not j.IsA(UsdPhysics.FixedJoint)}
        ok &= expect("joint axis/limits/drives resolved via", sources, {"inherit"})

    print("--- asset, variantSet preset drives the other selections (variant arc)")
    for preset, (mechanism, pads, n_bodies) in PRESETS.items():
        stage = Usd.Stage.Open(asset, Usd.Stage.LoadAll)
        stage.SetEditTarget(stage.GetSessionLayer())
        stage.GetDefaultPrim().GetVariantSet("preset").SetVariantSelection(preset)
        n = sum(1 for p in stage.Traverse() if p.HasAPI(UsdPhysics.RigidBodyAPI))
        sel = lambda vs: stage.GetDefaultPrim().GetVariantSet(vs).GetVariantSelection()
        ok &= expect(f"preset={preset}: mechanism, pads, rigid bodies", (sel("mechanism"), sel("pads"), n),
                     (mechanism, pads, n_bodies))

    stage = Usd.Stage.Open(scene_path, Usd.Stage.LoadAll)
    print("--- scene.usda")
    census = arc_census(stage)
    print("  arcs (prims affected):", ", ".join(f"{k} {v}" for k, v in sorted(census.items())))
    missing = [k for k in list(ARC_NAMES.values()) + ["sublayer (root stack)", "sublayer (payload stack)"]
               if not census.get(k)]
    ok &= expect("composition arcs missing", missing, [])
    for g, n_bodies in SCENE_BODIES.items():
        ok &= articulation_checks(stage, f"/World/{g}")
        ok &= expect(f"{g} rigid bodies", sum(1 for p in stage.Traverse() if p.HasAPI(UsdPhysics.RigidBodyAPI)
                                              and under(p.GetPath(), f"/World/{g}")), n_bodies)

    print("  variant selections, and the arc each one came from")
    for g, sets in SCENE_SELECTIONS.items():
        for vs, want in sets.items():
            ok &= expect(f"{g} {vs}", selection_source(stage, f"/World/{g}", vs), want)

    a, b = "/World/gripper_a", "/World/gripper_b"
    print("  inherits: scene_tuning overrides _class_prismatic_finger_joint once, both sides follow")
    for side in ("left", "right"):
        ok &= expect(f"{side}_finger_joint stiffness (gripper_a)",
                     resolved(stage, f"{a}/Physics/{side}_finger_joint", "drive:linear:physics:stiffness"),
                     (3000.0, "inherit"))
    ok &= expect("left_driver_joint stiffness (gripper_b, class untouched)",
                 resolved(stage, f"{b}/Physics/left_driver_joint", "drive:angular:physics:stiffness"),
                 (0.5, "inherit"))

    print("  variants + specializes: gripper_b pads=smooth; scene overrides elastomer_base")
    pad_mat = lambda g, link: UsdShade.MaterialBindingAPI(stage.GetPrimAtPath(f"{g}/Geometry/{link}/collision_0")) \
        .ComputeBoundMaterial("physics")[0].GetPath()
    mb, ma = pad_mat(b, "left_pad_link"), pad_mat(a, "fingertip_left")
    ok &= expect("gripper_b pad material", mb.name, "pad_smooth")
    ok &= expect("  staticFriction (own opinion beats base override)", resolved(stage, mb, "physics:staticFriction"),
                 (0.35, "reference"))
    ok &= expect("  restitution (not authored, follows overridden base)", resolved(stage, mb, "physics:restitution"),
                 (0.05, "specialize"))
    ok &= expect("gripper_a pad material", ma.name, "pad_grip")
    ok &= expect("  staticFriction", resolved(stage, ma, "physics:staticFriction")[0], 1.0)
    ok &= expect("  restitution (base not overridden for gripper_a)", resolved(stage, ma, "physics:restitution")[0], 0.0)

    print("  relocates: gripper_a pad links renamed to fingertip_left/right")
    ok &= expect("old path left_pad_link exists", stage.GetPrimAtPath(f"{a}/Geometry/left_pad_link").IsValid(), False)
    ok &= expect("fingertip_left is a rigid body",
                 stage.GetPrimAtPath(f"{a}/Geometry/fingertip_left").HasAPI(UsdPhysics.RigidBodyAPI), True)
    ok &= expect("left_pad_joint body1", str(UsdPhysics.Joint(stage.GetPrimAtPath(f"{a}/Physics/left_pad_joint"))
                                             .GetBody1Rel().GetTargets()[0]), f"{a}/Geometry/fingertip_left")
    print("RESULT:", "PASS" if ok else "FAIL")
    return ok


if __name__ == "__main__":
    variants = sys.argv[1:] or ["v1_prismatic", "v2_linkage", "v3_composed"]
    sys.exit(0 if all([check_composed() if v == "v3_composed" else check(v) for v in variants]) else 1)
