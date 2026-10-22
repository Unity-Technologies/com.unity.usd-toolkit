"""Build a small rigged, animated arm in Blender and export it with Blender's own USD exporter.

skel_arm.usda is hand-written, so it only contains what its author thought of. This one is what a
DCC actually writes: Z-up, automatic weights (up to four influences a point), a shape key, UV
seams, per-frame time samples and whatever naming and layout Blender's exporter chooses. The
generated blender_arm.usda is committed, so the tests do not need Blender; rerun this only to
refresh it, then regenerate blender_arm.expected.json with make_expected.py.

    /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
        --python make_blender_arm.py -- blender_arm.usda
"""

import math
import sys

import bmesh
import bpy

RINGS = 7          # rings along the arm, 0.5 apart
SEGMENTS = 8       # points around each ring
RADIUS = 0.25
LENGTH = 3.0
FRAME_END = 24


def clear_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def build_mesh():
    mesh = bpy.data.meshes.new("ArmMesh")
    bm = bmesh.new()
    rings = []
    for r in range(RINGS):
        z = LENGTH * r / (RINGS - 1)
        ring = []
        for s in range(SEGMENTS):
            a = 2.0 * math.pi * s / SEGMENTS
            ring.append(bm.verts.new((RADIUS * math.cos(a), RADIUS * math.sin(a), z)))
        rings.append(ring)
    uv = bm.loops.layers.uv.new("UVMap")
    for r in range(RINGS - 1):
        for s in range(SEGMENTS):
            n = (s + 1) % SEGMENTS
            face = bm.faces.new((rings[r][s], rings[r][n], rings[r + 1][n], rings[r + 1][s]))
            # One u range per segment, so the seam between the last and the first segment splits.
            corners = ((s, r), (s + 1, r), (s + 1, r + 1), (s, r + 1))
            for loop, (u, v) in zip(face.loops, corners):
                loop[uv].uv = (u / SEGMENTS, v / (RINGS - 1))
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new("Arm", mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def build_armature():
    data = bpy.data.armatures.new("ArmRig")
    rig = bpy.data.objects.new("Rig", data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    parent = None
    for i, name in enumerate(("Shoulder", "Elbow", "Wrist")):
        bone = data.edit_bones.new(name)
        bone.head = (0.0, 0.0, i * 1.0)
        bone.tail = (0.0, 0.0, (i + 1) * 1.0)
        if parent:
            bone.parent = parent
            bone.use_connect = True
        parent = bone
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def bind(mesh_obj, rig):
    bpy.ops.object.select_all(action="DESELECT")
    mesh_obj.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def add_shape_key(mesh_obj):
    mesh_obj.shape_key_add(name="Basis")
    bulge = mesh_obj.shape_key_add(name="Bulge")
    middle = LENGTH / 2.0
    for point in bulge.data:
        x, y, z = point.co
        if abs(z - middle) < 0.6:
            point.co = (x * 1.6, y * 1.6, z)
    bulge.value = 0.0
    bulge.keyframe_insert("value", frame=1)
    bulge.value = 1.0
    bulge.keyframe_insert("value", frame=FRAME_END)


def animate(rig):
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    for name, axis, degrees in (("Elbow", "X", 90.0), ("Wrist", "Y", 45.0)):
        bone = rig.pose.bones[name]
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = (0.0, 0.0, 0.0)
        bone.keyframe_insert("rotation_euler", frame=1)
        angles = [0.0, 0.0, 0.0]
        angles["XYZ".index(axis)] = math.radians(degrees)
        bone.rotation_euler = angles
        bone.keyframe_insert("rotation_euler", frame=FRAME_END)
    bpy.ops.object.mode_set(mode="OBJECT")


def main():
    output = sys.argv[sys.argv.index("--") + 1]
    clear_scene()
    scene = bpy.context.scene
    scene.frame_start = 1
    scene.frame_end = FRAME_END
    scene.render.fps = 24

    mesh_obj = build_mesh()
    rig = build_armature()
    bind(mesh_obj, rig)
    add_shape_key(mesh_obj)
    animate(rig)

    bpy.ops.wm.usd_export(
        filepath=output,
        export_animation=True,
        export_armatures=True,
        export_shapekeys=True,
        only_deform_bones=False,
        export_materials=False,
        export_textures=False,
        convert_world_material=False,
    )
    print("exported", output)


main()
