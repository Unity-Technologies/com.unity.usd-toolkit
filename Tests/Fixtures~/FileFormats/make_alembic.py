"""Writes alembic_box.abc with Blender's own Alembic exporter: a file made by a DCC, not by us.

    Blender -b --factory-startup --python make_alembic.py -- alembic_box.abc

Two objects, eleven frames at 24 fps:
  Box   a 1 m cube whose location moves from the origin to (2, 0, 0) -- an animated transform;
  Wave  a 2 x 2 quad whose shape key lifts one corner by 1 m over the same frames -- animated
        points (a point cache), which Alembic stores as one sample of positions a frame.
Blender writes Alembic Y-up; nothing else is set beyond Blender's defaults.
"""

import sys

import bpy

output = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.fps = 24
scene.frame_start = 1
scene.frame_end = 11

bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0.0, 0.0, 0.0))
box = bpy.context.active_object
box.name = "Box"
box.data.name = "BoxMesh"
box.location = (0.0, 0.0, 0.0)
box.keyframe_insert("location", frame=1)
box.location = (2.0, 0.0, 0.0)
box.keyframe_insert("location", frame=11)
for curve in box.animation_data.action.fcurves:
    for key in curve.keyframe_points:
        key.interpolation = "LINEAR"

bpy.ops.mesh.primitive_plane_add(size=2.0, location=(0.0, 3.0, 0.0))
wave = bpy.context.active_object
wave.name = "Wave"
wave.data.name = "WaveMesh"
wave.shape_key_add(name="Basis")
lift = wave.shape_key_add(name="Lift")
lift.data[0].co.z += 1.0
lift.value = 0.0
lift.keyframe_insert("value", frame=1)
lift.value = 1.0
lift.keyframe_insert("value", frame=11)
for curve in wave.data.shape_keys.animation_data.action.fcurves:
    for key in curve.keyframe_points:
        key.interpolation = "LINEAR"

bpy.ops.wm.alembic_export(filepath=output, start=1, end=11, selected=False, uvs=True, normals=True,
                          face_sets=False, evaluation_mode="RENDER")
print("wrote", output)
