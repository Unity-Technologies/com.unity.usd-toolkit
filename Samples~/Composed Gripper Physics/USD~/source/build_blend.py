"""Build the Composed Gripper Physics .blend files from gripper_spec.py.

Run inside Blender:  blender --background --python build_blend.py
One .blend per variant. Each rigid link is one object whose origin is its joint pivot;
its convex collision proxies are child objects named <link>_collider_<n> (wire display).
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

SOURCE_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SOURCE_DIR)
import gripper_spec as spec  # noqa: E402


def reset_scene():
    # Purge data in place (keeps add-ons such as an MCP bridge alive, unlike factory reset).
    for coll in (bpy.data.objects, bpy.data.meshes, bpy.data.materials, bpy.data.cameras,
                 bpy.data.lights, bpy.data.collections):
        for block in list(coll):
            coll.remove(block)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def make_materials():
    mats = {}
    for name, (color, metallic, roughness) in spec.MATERIALS.items():
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        bsdf = m.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Metallic"].default_value = metallic
        bsdf.inputs["Roughness"].default_value = roughness
        m.diffuse_color = (*color, 1.0)
        mats[name] = m
    return mats


def hull_into(bm, points):
    """Append the convex hull of points to bm; return the new faces."""
    verts = [bm.verts.new(p) for p in points]
    res = bmesh.ops.convex_hull(bm, input=verts)
    loose = {g for g in res["geom_interior"] + res["geom_unused"] if isinstance(g, bmesh.types.BMVert)}
    bmesh.ops.delete(bm, geom=list(loose), context="VERTS")
    faces = [g for g in res["geom"] if isinstance(g, bmesh.types.BMFace)]
    res2 = bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5),
                                    verts=list({v for f in faces for v in f.verts}),
                                    edges=list({e for f in faces for e in f.edges}))
    return [f for f in res2["region"] if isinstance(f, bmesh.types.BMFace)]


def build_part(points, bevel, segments):
    bm = bmesh.new()
    hull_into(bm, points)
    if bevel > 0:
        sharp = [e for e in bm.edges if e.is_manifold and e.calc_face_angle(0) > math.radians(30)]
        if sharp:
            bmesh.ops.bevel(bm, geom=sharp, offset=bevel, offset_type="OFFSET", segments=segments,
                            profile=0.5, affect="EDGES", clamp_overlap=True)
    return bm


def link_object(link, mats, collection):
    mesh = bpy.data.meshes.new(link["name"])
    bm = bmesh.new()
    slot_names = []
    for p in link["render"]:
        if p["material"] not in slot_names:
            slot_names.append(p["material"])
        part_bm = build_part(p["points"], p["bevel"], p["segments"])
        tmp = bpy.data.meshes.new("tmp")
        part_bm.to_mesh(tmp)
        part_bm.free()
        n_before = len(bm.faces)
        bm.from_mesh(tmp)
        bm.faces.ensure_lookup_table()
        for f in bm.faces[n_before:]:
            f.material_index = slot_names.index(p["material"])
        bpy.data.meshes.remove(tmp)
    bm.to_mesh(mesh)
    bm.free()
    for s in slot_names:
        mesh.materials.append(mats[s])
    mesh.shade_smooth()
    mesh.set_sharp_from_angle(angle=math.radians(35))

    obj = bpy.data.objects.new(link["name"], mesh)
    collection.objects.link(obj)
    obj.matrix_world = Matrix.Translation(Vector(link["origin"])) @ Matrix.Rotation(
        math.radians(link["rot_y"]), 4, "Y")
    obj["mass_kg"] = link["mass"]

    for i, pts in enumerate(link["colliders"]):
        cm = bpy.data.meshes.new(f"{link['name']}_collider_{i}")
        cbm = bmesh.new()
        hull_into(cbm, pts)
        bmesh.ops.triangulate(cbm, faces=cbm.faces)
        cbm.to_mesh(cm)
        cbm.free()
        col = bpy.data.objects.new(cm.name, cm)
        collection.objects.link(col)
        col.parent = obj
        col.matrix_parent_inverse = Matrix.Identity(4)
        col.display_type = "WIRE"
        col.hide_render = True
        col["collider"] = True
    return obj


def strip_local_paths():
    """Keep machine-specific absolute paths (render output, file browser folders) out of the shipped .blend."""
    for scene in bpy.data.scenes:
        scene.render.filepath = "//"
    for ws in bpy.data.workspaces:
        for screen in ws.screens:
            for area in screen.areas:
                for sp in area.spaces:
                    if sp.type == "FILE_BROWSER" and sp.params:
                        sp.params.directory = b"/" * 1000  # the buffer is saved whole: overwrite it first
                        sp.params.directory = b"//"


def build(variant, out_dir):
    reset_scene()
    mats = make_materials()
    coll = bpy.data.collections.new(variant)
    bpy.context.scene.collection.children.link(coll)
    links, _ = spec.get_variant(variant)
    for link in links:
        link_object(link, mats, coll)
    strip_local_paths()
    path = os.path.join(out_dir, f"composed_gripper_{variant}.blend")
    bpy.ops.wm.save_as_mainfile(filepath=path)
    return path


if __name__ == "__main__":
    for v in spec.VARIANTS:
        print("saved", build(v, SOURCE_DIR))
