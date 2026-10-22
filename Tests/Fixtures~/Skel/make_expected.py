"""Write the expected values for a UsdSkel fixture, as OpenUSD itself evaluates them.

The import tests compare what Unity produces (SkinnedMeshRenderer.BakeMesh, transforms, blend
shapes, visibility) with these numbers, so the reference is OpenUSD's own UsdSkel code rather than
a second implementation of the same maths. Everything is in the stage's coordinates (USD's
right-handed frame, stage up axis and units); the tests convert to Unity's frame themselves.

For every skinned mesh and every requested time the script applies the blend shapes
(UsdSkelBlendShapeQuery), skins the result (UsdSkelSkinningQuery.ComputeSkinnedPoints, which
returns skeleton-space points) and moves it to world space with the skeleton's local-to-world
transform. It then cross-checks those numbers against UsdSkelBakeSkinning at the times the bake
writes, and fails if they disagree, so a misuse of the query API cannot produce a wrong reference.

The output is shaped for UnityEngine.JsonUtility: lists of records, points as flat x,y,z arrays,
matrices as 16 row-major values (USD's row-vector convention, translation in the last row).

Run it with any Python that has OpenUSD (`pip install usd-core`, or the copy inside Blender):
    python3 make_expected.py skel_arm.usda skel_arm.expected.json --times 0 2.5 4 5 7.5 8 10
    /Applications/Blender.app/Contents/Resources/4.4/python/bin/python3.11 -I make_expected.py ...
"""

import argparse
import json
import pathlib
import sys

from pxr import Gf, Usd, UsdGeom, UsdSkel, Vt

# Query and bake both skin in float32, in a different order of operations, so they agree to a
# few ulps of the coordinates involved: an absolute floor plus a part relative to the point.
BAKE_TOLERANCE = 1e-4
BAKE_RELATIVE_TOLERANCE = 1e-5


def rounded(value):
    return float("%.7g" % value)


def flat_points(points):
    return [rounded(c) for p in points for c in (p[0], p[1], p[2])]


def flat_matrix(matrix):
    return [rounded(matrix[r][c]) for r in range(4) for c in range(4)]


def skinned_world_points(skel_query, skinning_query, time):
    """Blend shapes, then skinning, then the skeleton's world transform, all at `time`."""
    prim = skinning_query.GetPrim()
    points = Vt.Vec3fArray(UsdGeom.Mesh(prim).GetPointsAttr().Get(time))

    anim_query = skel_query.GetAnimQuery()
    if skinning_query.HasBlendShapes() and anim_query:
        weights = anim_query.ComputeBlendShapeWeights(time)
        mapper = skinning_query.GetBlendShapeMapper()
        if mapper:
            weights = mapper.Remap(weights)
        shapes = UsdSkel.BlendShapeQuery(UsdSkel.BindingAPI(prim))
        sub_weights, shape_indices, sub_indices = shapes.ComputeSubShapeWeights(weights)
        if not shapes.ComputeDeformedPoints(sub_weights, shape_indices, sub_indices,
                                            shapes.ComputeBlendShapePointIndices(),
                                            shapes.ComputeSubShapePointOffsets(), points):
            raise RuntimeError(f"{prim.GetPath()}: blend shapes could not be applied at {time}")

    transforms = skel_query.ComputeSkinningTransforms(time)
    if not skinning_query.ComputeSkinnedPoints(transforms, points, time):
        raise RuntimeError(f"{prim.GetPath()}: skinning failed at {time}")

    skel_to_world = UsdGeom.XformCache(time).GetLocalToWorldTransform(skel_query.GetPrim())
    return [skel_to_world.Transform(Gf.Vec3d(p)) for p in points]


def cross_check_with_bake(path, skinned):
    """Bake the same stage with UsdSkelBakeSkinning and compare world points at baked times."""
    stage = Usd.Stage.Open(str(path))
    stage.SetEditTarget(stage.GetSessionLayer())
    interval = Gf.Interval(stage.GetStartTimeCode(), stage.GetEndTimeCode())
    for prim in stage.Traverse():
        if prim.IsA(UsdSkel.Root) and not UsdSkel.BakeSkinning(UsdSkel.Root(prim), interval):
            raise RuntimeError(f"UsdSkelBakeSkinning failed on {prim.GetPath()}")

    checked = 0
    for entry in skinned:
        mesh = UsdGeom.Mesh(stage.GetPrimAtPath(entry["path"]))
        # A rigidly bound mesh is baked as a transform on the mesh, not as points.
        baked_times = set(mesh.GetPointsAttr().GetTimeSamples())
        for op in mesh.GetOrderedXformOps():
            baked_times.update(op.GetAttr().GetTimeSamples())
        for frame in entry["frames"]:
            time = frame["time"]
            # Nothing animated: the bake writes the skinned pose as default values instead of
            # samples, and that pose holds at every time.
            if baked_times and time not in baked_times:
                continue
            to_world = UsdGeom.XformCache(time).GetLocalToWorldTransform(mesh.GetPrim())
            got = [to_world.Transform(Gf.Vec3d(p)) for p in mesh.GetPointsAttr().Get(time)]
            want = frame["points"]
            for i, g in enumerate(got):
                w = Gf.Vec3d(want[3 * i], want[3 * i + 1], want[3 * i + 2])
                error = (w - g).GetLength()
                if error > BAKE_TOLERANCE + BAKE_RELATIVE_TOLERANCE * w.GetLength():
                    raise RuntimeError(f"{entry['path']} at {time}: query and bake differ by {error} at point {i}")
            checked += 1
    return checked


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("fixture", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--times", type=float, nargs="+", required=True)
    args = parser.parse_args()

    stage = Usd.Stage.Open(str(args.fixture))
    if not stage:
        return f"cannot open {args.fixture}"

    result = {
        "source": args.fixture.name,
        "generator": "make_expected.py",
        "openusd": ".".join(str(v) for v in Usd.GetVersion()),
        "stage": {
            "startTimeCode": stage.GetStartTimeCode(),
            "endTimeCode": stage.GetEndTimeCode(),
            "timeCodesPerSecond": stage.GetTimeCodesPerSecond(),
            "framesPerSecond": stage.GetFramesPerSecond(),
            "upAxis": str(UsdGeom.GetStageUpAxis(stage)),
            "metersPerUnit": UsdGeom.GetStageMetersPerUnit(stage),
        },
        "times": args.times,
        "skinned": [],
        "xforms": [],
        "visibility": [],
        "points": [],
    }

    cache = UsdSkel.Cache()
    skinned_paths = set()
    for prim in stage.Traverse():
        if not prim.IsA(UsdSkel.Root):
            continue
        root = UsdSkel.Root(prim)
        cache.Populate(root, Usd.PrimDefaultPredicate)
        for binding in cache.ComputeSkelBindings(root, Usd.PrimDefaultPredicate):
            skel_query = cache.GetSkelQuery(binding.GetSkeleton())
            for skinning_query in binding.GetSkinningTargets():
                mesh_prim = skinning_query.GetPrim()
                skinned_paths.add(mesh_prim.GetPath())
                result["skinned"].append({
                    "path": str(mesh_prim.GetPath()),
                    "skeleton": str(binding.GetSkeleton().GetPath()),
                    "restPoints": flat_points(UsdGeom.Mesh(mesh_prim).GetPointsAttr().Get()),
                    "frames": [
                        {"time": t,
                         "points": flat_points(skinned_world_points(skel_query, skinning_query, t))}
                        for t in args.times
                    ],
                })

    for prim in stage.Traverse():
        if prim.GetPath() in skinned_paths or prim.IsA(UsdSkel.Root) or prim.IsA(UsdSkel.Skeleton):
            continue
        xformable = UsdGeom.Xformable(prim)
        if xformable and xformable.TransformMightBeTimeVarying():
            result["xforms"].append({
                "path": str(prim.GetPath()),
                "frames": [
                    {"time": t,
                     "matrix": flat_matrix(UsdGeom.XformCache(t).GetLocalToWorldTransform(prim))}
                    for t in args.times
                ],
            })
        imageable = UsdGeom.Imageable(prim)
        if imageable and imageable.GetVisibilityAttr().ValueMightBeTimeVarying():
            for target in [prim] + list(prim.GetChildren()):
                result["visibility"].append({
                    "path": str(target.GetPath()),
                    "frames": [
                        {"time": t, "value": str(UsdGeom.Imageable(target).ComputeVisibility(t))}
                        for t in args.times
                    ],
                })
        if prim.IsA(UsdGeom.Mesh):
            attr = UsdGeom.Mesh(prim).GetPointsAttr()
            if attr.ValueMightBeTimeVarying():
                result["points"].append({
                    "path": str(prim.GetPath()),
                    "hasDefault": attr.Get() is not None,
                    "timeSamples": list(attr.GetTimeSamples()),
                    "frames": [{"time": t, "points": flat_points(attr.Get(t))} for t in args.times],
                })

    checked = cross_check_with_bake(args.fixture, result["skinned"])
    if result["skinned"] and checked == 0:
        return "no baked time matched a requested time; nothing was cross-checked"

    args.output.write_text(json.dumps(result, indent=1) + "\n")
    print(f"wrote {args.output}: {len(result['skinned'])} skinned meshes, {len(result['xforms'])} "
          f"animated xforms, {len(result['visibility'])} visibility tracks, "
          f"{checked} frames cross-checked against UsdSkelBakeSkinning")
    return 0


if __name__ == "__main__":
    sys.exit(main())
