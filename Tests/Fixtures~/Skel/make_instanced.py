"""Write the expected points of every instance of instanced_arm.usda's skinned prototype.

For each requested time: the prototype mesh skinned by UsdSkel (blend shapes none, skinning by
UsdSkelSkinningQuery.ComputeSkinnedPoints, in skeleton space), carried into the prototype root's
space, then placed by each of its instances (UsdGeomPointInstancer.ComputeInstanceTransformsAtTime
with the prototype's own transform included) and the instancer's world transform -- how OpenUSD
renders a skinned prototype. Points are flat x,y,z in stage coordinates, instance by instance.

    /Applications/Blender.app/Contents/Resources/4.4/python/bin/python3.11 -I make_instanced.py \\
        instanced_arm.usda instanced_arm.expected.json --times 0 5 10
"""

import argparse
import json

from pxr import Gf, Usd, UsdGeom, UsdSkel, Vt


def rounded(value):
    return float("%.7g" % value)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("stage")
    parser.add_argument("output")
    parser.add_argument("--times", type=float, nargs="+", required=True)
    args = parser.parse_args()

    stage = Usd.Stage.Open(args.stage)
    instancer = UsdGeom.PointInstancer(stage.GetPrimAtPath("/Root/Crowd"))
    proto_root = stage.GetPrimAtPath("/Root/Crowd/Prototypes/Arm")
    mesh_prim = stage.GetPrimAtPath("/Root/Crowd/Prototypes/Arm/Strip")
    proto_indices = instancer.GetProtoIndicesAttr().Get()
    instances = [i for i, p in enumerate(proto_indices) if p == 0]

    cache = UsdSkel.Cache()
    root = UsdSkel.Root(proto_root)
    cache.Populate(root, Usd.TraverseInstanceProxies())
    binding = cache.ComputeSkelBindings(root, Usd.TraverseInstanceProxies())[0]
    skel_query = cache.GetSkelQuery(binding.GetSkeleton())
    skinning = [q for q in binding.GetSkinningTargets() if q.GetPrim() == mesh_prim][0]

    frames = []
    for time in args.times:
        xform_cache = UsdGeom.XformCache(time)
        points = Vt.Vec3fArray(UsdGeom.Mesh(mesh_prim).GetPointsAttr().Get(time))
        if not skinning.ComputeSkinnedPoints(skel_query.ComputeSkinningTransforms(time), points, time):
            raise SystemExit("skinning failed")

        skel_to_proto, _ = xform_cache.ComputeRelativeTransform(skel_query.GetPrim(), proto_root)
        placements = instancer.ComputeInstanceTransformsAtTime(
            time, time, UsdGeom.PointInstancer.IncludeProtoXform, UsdGeom.PointInstancer.IgnoreMask)
        to_world = xform_cache.GetLocalToWorldTransform(instancer.GetPrim())
        flat = []
        for i in instances:
            matrix = skel_to_proto * placements[i] * to_world
            for p in points:
                q = matrix.Transform(Gf.Vec3d(p))
                flat += [rounded(q[0]), rounded(q[1]), rounded(q[2])]

        frames.append({"time": time, "points": flat})

    rest = UsdGeom.Mesh(mesh_prim).GetPointsAttr().Get()
    result = {
        "source": args.stage,
        "generator": "make_instanced.py",
        "instances": len(instances),
        "restPoints": [rounded(c) for p in rest for c in (p[0], p[1], p[2])],
        "frames": frames,
    }

    with open(args.output, "w") as out:
        json.dump(result, out, indent=1)
        out.write("\n")
    print("wrote %s: %d instances, %d frames" % (args.output, len(instances), len(frames)))


if __name__ == "__main__":
    main()
