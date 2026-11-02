"""Build v3_composed from the v1_prismatic and v2_linkage file sets (pip install usd-core).

  python export_composed.py

v3 is the same gripper re-authored so that every OpenUSD composition arc carries part of the
physics description, while the composed result stays identical to v1 / v2:

  sublayers    physics payload is split into classes / bodies / joints / materials layers;
               scene.usda sublayers a scene-wide tuning layer
  inherits     class prims hold everything both sides share: rigid-body + collider templates,
               joint axis, limits and drive gains (one edit tunes both fingers, in every gripper)
  variants     mechanism = prismatic | linkage (one selection drives geometry AND physics),
               physics = usdphysics | none, pads = grip | smooth
  references   per-link render geometry and materials
  payloads     all physics, inside physics = usdphysics
  specializes  pad physics materials specialize a base elastomer material
  relocates    scene.usda renames gripper_a's pad links to fingertip_left/right

Variant selections are composed opinions too. In scene.usda each gripper gets its selections
through a different arc (local, sublayer, inherits, reference, variant, payload); see SCENE_GRIPPERS.

Output (<repo>/v3_composed/):
  gripper.usda                      asset root: variants, references, payload, PhysicsScene
  geometry/<mechanism>/<link>.usd   render geometry (copied from v1 / v2)
  geometry/materials.usd            UsdPreviewSurface materials
  physics/gripper_physics.usda      payload root: ArticulationRootAPI + subLayers below
  physics/{pads,joints,bodies,classes}.usda
  physics/physics_materials.usda    physics material library (referenced next to the payload)
  scene.usda                        demo scene: four grippers, variant selections via different arcs
  layers/scene_tuning.usda          scene sublayer: class, specializes-base and selection overrides
  presets/linkage_smooth.usda       preset asset: references gripper.usda and pins its selections
"""
import os
import shutil

from pxr import Gf, Sdf, Usd, UsdGeom, UsdPhysics, UsdShade

SOURCE_DIR = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SOURCE_DIR)
OUT = os.path.join(REPO, "v3_composed")
MECHANISMS = {"prismatic": "v1_prismatic", "linkage": "v2_linkage"}
ROOT = "composed_gripper"
ROOT_PATH = Sdf.Path("/" + ROOT)
LICENSE = "CC-BY-4.0. Original work; contains no third-party geometry, names or branding."

RIGID_CLASS = "_class_rigid_link"
COLLIDER_CLASS = "_class_convex_collider"
ELASTOMER_BASE = {"staticFriction": 0.8, "dynamicFriction": 0.7, "restitution": 0.0}
PAD_VARIANTS = {"grip": {"staticFriction": 1.0, "dynamicFriction": 0.9},
                "smooth": {"staticFriction": 0.35, "dynamicFriction": 0.3}}
# Joint properties that are identical on both sides move into a class prim.
SHARED_JOINT_PROPS = ("physics:axis", "physics:lowerLimit", "physics:upperLimit", "physics:excludeFromArticulation")

# variantSet preset: each variant authors selections for the other sets (variant-driven selection).
PRESETS = {"prismatic": {"mechanism": "prismatic"},
           "linkage": {"mechanism": "linkage"},
           "linkage_smooth": {"mechanism": "linkage", "pads": "smooth"}}
DEFAULT_PRESET = "prismatic"
DEFAULT_PADS = "grip"  # authored in the payload, next to the pads variantSet it selects
PRESET_FILE = {"name": "linkage_smooth", "selections": {"mechanism": "linkage", "pads": "smooth"}}
SCENE_CLASS = {"name": "_class_linkage_grippers", "selections": {"mechanism": "linkage"}}

# Each gripper receives its variant selections through a different composition arc:
#   gripper_a  nothing authored: preset from the asset (reference), mechanism from the preset
#              variant (variant), pads from the payload default (payload)
#   gripper_b  preset = linkage authored locally (local), pads = smooth in scene_tuning (sublayer)
#   gripper_c  inherits _class_linkage_grippers, which selects mechanism = linkage (inherits beat the
#              asset's preset, because the class lives in the stronger scene layer stack)
#   gripper_d  references presets/linkage_smooth.usda, which pins mechanism + pads (reference)
SCENE_GRIPPERS = [  # name, x offset (m), local selections, inherits, referenced asset
    ("gripper_a", -0.375, {}, None, "./gripper.usda"),
    ("gripper_b", -0.125, {"preset": "linkage"}, None, "./gripper.usda"),
    ("gripper_c", 0.125, {}, SCENE_CLASS["name"], "./gripper.usda"),
    ("gripper_d", 0.375, {}, None, f"./presets/{PRESET_FILE['name']}.usda"),
]
SCENE_SUBLAYER_SELECTIONS = {"gripper_b": {"pads": "smooth"}}
SCENE_RELOCATES = {"left_pad_link": "fingertip_left", "right_pad_link": "fingertip_right"}  # on gripper_a
SCENE_TUNING = {"class": "_class_prismatic_finger_joint", "drive:linear:physics:stiffness": 3000.0,
                "elastomer_base": {"staticFriction": 0.2, "dynamicFriction": 0.15, "restitution": 0.05}}


# ----------------------------------------------------------------------------- helpers
def new_layer(path, doc, default_prim=ROOT):
    if os.path.exists(path):
        os.remove(path)
    layer = Sdf.Layer.CreateNew(path)
    layer.documentation = doc
    if default_prim:
        layer.defaultPrim = default_prim
    return layer


def stage_metadata(layer):
    stage = Usd.Stage.Open(layer, Usd.Stage.LoadNone)
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.z)
    UsdGeom.SetStageMetersPerUnit(stage, 1.0)
    UsdPhysics.SetStageKilogramsPerUnit(stage, 1.0)


def vpath(mechanism, rel=""):
    """Path inside the mechanism variant of the root prim, e.g. /composed_gripper{mechanism=linkage}Geometry."""
    p = ROOT_PATH.AppendVariantSelection("mechanism", mechanism)
    return p.AppendPath(rel) if rel else p


def variant_parent(layer, mechanism, rel):
    """Create the over specs (and variant set) down to rel inside the mechanism variant."""
    spec = Sdf.CreatePrimInLayer(layer, vpath(mechanism, rel))
    root = layer.GetPrimAtPath(ROOT_PATH)
    if "mechanism" not in root.variantSetNameList.prependedItems:
        root.variantSetNameList.prependedItems.append("mechanism")
    return spec


def set_api_schemas(spec, schemas):
    if schemas:
        spec.SetInfo("apiSchemas", Sdf.TokenListOp.Create(prependedItems=schemas))
    elif spec.HasInfo("apiSchemas"):
        spec.ClearInfo("apiSchemas")


def add_inherit(spec, cls):
    spec.inheritPathList.prependedItems.append(Sdf.Path("/" + cls))


def remove_props(spec, names):
    for name in names:
        if name in spec.properties:
            spec.RemoveProperty(spec.properties[name])


def class_spec(layer, name, schemas=()):
    spec = layer.GetPrimAtPath("/" + name) or Sdf.PrimSpec(layer.pseudoRoot, name, Sdf.SpecifierClass)
    set_api_schemas(spec, list(schemas))
    return spec


def set_attr(spec, name, type_name, value, variability=Sdf.VariabilityVarying):
    attr = spec.attributes[name] if name in spec.attributes else Sdf.AttributeSpec(spec, name, type_name, variability)
    attr.default = value
    return attr


def src_physics(mechanism):
    return Sdf.Layer.FindOrOpen(os.path.join(REPO, MECHANISMS[mechanism], "physics", "gripper_physics.usd"))


def src_root(mechanism):
    return Sdf.Layer.FindOrOpen(os.path.join(REPO, MECHANISMS[mechanism], "gripper.usda"))


def children(layer, path):
    spec = layer.GetPrimAtPath(path)
    return list(spec.nameChildren) if spec else []


# ----------------------------------------------------------------------------- geometry
def copy_geometry():
    for mechanism, folder in MECHANISMS.items():
        src = os.path.join(REPO, folder, "geometry")
        dst = os.path.join(OUT, "geometry", mechanism)
        os.makedirs(dst, exist_ok=True)
        for f in os.listdir(src):
            if f != "materials.usd":
                shutil.copyfile(os.path.join(src, f), os.path.join(dst, f))
    shutil.copyfile(os.path.join(REPO, "v1_prismatic", "geometry", "materials.usd"),
                    os.path.join(OUT, "geometry", "materials.usd"))


# ----------------------------------------------------------------------------- physics payload
def write_classes(path):
    layer = new_layer(path, "Class prims (inherits targets). Everything the left and right side share lives "
                            "here once; override a class in a stronger layer to retune every inheriting prim.",
                      default_prim=None)
    rigid = class_spec(layer, RIGID_CLASS, ["PhysicsRigidBodyAPI", "PhysicsMassAPI", "PhysicsFilteredPairsAPI"])
    rigid.documentation = "Template for every link: rigid body with explicit mass and collision filtering."
    col = class_spec(layer, COLLIDER_CLASS, ["PhysicsCollisionAPI", "PhysicsMeshCollisionAPI"])
    col.documentation = "Template for every collider part: guide-purpose convex hull."
    set_attr(col, "physics:approximation", Sdf.ValueTypeNames.Token, UsdPhysics.Tokens.convexHull, Sdf.VariabilityUniform)
    set_attr(col, "purpose", Sdf.ValueTypeNames.Token, UsdGeom.Tokens.guide, Sdf.VariabilityUniform)
    return layer


def write_bodies(path):
    layer = new_layer(path, "Rigid bodies: per-link mass properties, filtered pairs and collider hulls, "
                            "one variant per mechanism. Shared schemas come from the class prims.")
    for mechanism in MECHANISMS:
        src = src_physics(mechanism)
        variant_parent(layer, mechanism, "Geometry")
        for link in children(src, ROOT_PATH.AppendChild("Geometry")):
            dst = vpath(mechanism, f"Geometry/{link.name}")
            assert Sdf.CopySpec(src, link.path, layer, dst)
            spec = layer.GetPrimAtPath(dst)
            spec.specifier = Sdf.SpecifierOver
            set_api_schemas(spec, [])
            add_inherit(spec, RIGID_CLASS)
            for col in spec.nameChildren:
                set_api_schemas(col, [])
                remove_props(col, ["physics:approximation", "purpose", "material:binding:physics"])
                add_inherit(col, COLLIDER_CLASS)
    return layer


def write_joints(path, classes):
    layer = new_layer(path, "Joints, one variant per mechanism. Each driven / loop joint keeps only its bodies "
                            "and frames; axis, limits, excludeFromArticulation and drives are inherited from "
                            "_class_<mechanism>_<role> (e.g. _class_linkage_driver_joint).")
    Sdf.PrimSpec(Sdf.CreatePrimInLayer(layer, ROOT_PATH), "Physics", Sdf.SpecifierDef, "Scope")
    for mechanism in MECHANISMS:
        src = src_physics(mechanism)
        variant_parent(layer, mechanism, "Physics")
        for joint in children(src, ROOT_PATH.AppendChild("Physics")):
            if joint.typeName == "Material":
                continue  # physics materials are re-authored in materials.usda
            dst = vpath(mechanism, f"Physics/{joint.name}")
            assert Sdf.CopySpec(src, joint.path, layer, dst)
            if joint.typeName == "PhysicsFixedJoint":
                continue
            spec = layer.GetPrimAtPath(dst)
            role = joint.name.split("_", 1)[1] if joint.name.startswith(("left_", "right_")) else joint.name
            cls_name = f"_class_{mechanism}_{role}"
            shared = [a for a in spec.attributes if a.name in SHARED_JOINT_PROPS or a.name.startswith("drive:")]
            schemas = list(spec.GetInfo("apiSchemas").prependedItems) if spec.HasInfo("apiSchemas") else []
            cls = classes.GetPrimAtPath("/" + cls_name)
            if cls is None:
                cls = class_spec(classes, cls_name, schemas)
                cls.documentation = f"Shared by left_{role} and right_{role} ({mechanism})."
                for a in shared:
                    set_attr(cls, a.name, a.typeName, a.default, a.variability)
            else:  # the other side must agree exactly, otherwise it could not inherit
                assert list(cls.GetInfo("apiSchemas").prependedItems) == schemas, joint.name
                for a in shared:
                    assert cls.attributes[a.name].default == a.default, (joint.name, a.name)
            remove_props(spec, [a.name for a in shared])
            set_api_schemas(spec, [])
            add_inherit(spec, cls_name)
    return layer


def write_material_library(path):
    """Physics material library, referenced (not payloaded) into /composed_gripper/Physics/Materials.

    OpenUSD 26.08 drops a referencing layer's override of a specializes base when the specializes
    arc comes through a payload and the asset is referenced below the root (/World/gripper_b);
    through a reference it composes as documented. So the library rides next to the payload,
    inside physics = usdphysics, rather than inside it.
    """
    layer = new_layer(path, "Physics material library. pad_grip / pad_smooth specialize elastomer_base: "
                            "overriding the base in a stronger layer changes what the pads do not author "
                            "themselves (restitution), never their own friction.", default_prim="Materials")
    mats = Sdf.PrimSpec(layer.pseudoRoot, "Materials", Sdf.SpecifierDef, "Scope")
    base = Sdf.PrimSpec(mats, "elastomer_base", Sdf.SpecifierDef, "Material")
    set_api_schemas(base, ["PhysicsMaterialAPI"])
    for k, v in ELASTOMER_BASE.items():
        set_attr(base, f"physics:{k}", Sdf.ValueTypeNames.Float, v)
    for name, values in PAD_VARIANTS.items():
        pad = Sdf.PrimSpec(mats, f"pad_{name}", Sdf.SpecifierDef, "Material")
        pad.specializesList.prependedItems.append(base.path)
        for k, v in values.items():
            set_attr(pad, f"physics:{k}", Sdf.ValueTypeNames.Float, v)
    layer.Save()


def write_pad_bindings(path):
    layer = new_layer(path, "variantSet pads (grip | smooth): binds the pad colliders to a physics material "
                            "from the library referenced at /composed_gripper/Physics/Materials.")
    mats = ROOT_PATH.AppendPath("Physics/Materials")
    Sdf.CreatePrimInLayer(layer, ROOT_PATH)

    # Pad colliders exist in both mechanism variants under the same names.
    pad_cols = None
    for mechanism in MECHANISMS:
        src = src_physics(mechanism)
        cols = sorted(f"{l.name}/{c.name}" for l in children(src, ROOT_PATH.AppendChild("Geometry"))
                      for c in l.nameChildren if "material:binding:physics" in c.relationships)
        assert pad_cols in (None, cols), "pad colliders differ between mechanisms"
        pad_cols = cols
    root = layer.GetPrimAtPath(ROOT_PATH)
    root.variantSetNameList.prependedItems.append("pads")
    root.variantSelections["pads"] = DEFAULT_PADS
    for name in PAD_VARIANTS:
        for col in pad_cols:
            spec = Sdf.CreatePrimInLayer(layer, ROOT_PATH.AppendVariantSelection("pads", name).AppendPath(f"Geometry/{col}"))
            set_api_schemas(spec, ["MaterialBindingAPI"])
            rel = Sdf.RelationshipSpec(spec, "material:binding:physics", custom=False)
            rel.targetPathList.explicitItems = [mats.AppendChild(f"pad_{name}")]
    return layer


def write_payload(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    d = os.path.dirname(path)
    classes = write_classes(os.path.join(d, "classes.usda"))
    bodies = write_bodies(os.path.join(d, "bodies.usda"))
    joints = write_joints(os.path.join(d, "joints.usda"), classes)
    pads = write_pad_bindings(os.path.join(d, "pads.usda"))
    for layer in (classes, bodies, joints, pads):
        layer.Save()
    write_material_library(os.path.join(d, "physics_materials.usda"))

    layer = new_layer(path, "Physics payload root. Strongest sublayer first: pads, joints, bodies, classes.")
    layer.subLayerPaths = ["./pads.usda", "./joints.usda", "./bodies.usda", "./classes.usda"]
    stage_metadata(layer)
    root = Sdf.PrimSpec(layer.pseudoRoot, ROOT, Sdf.SpecifierDef)
    set_api_schemas(root, ["PhysicsArticulationRootAPI"])
    layer.Save()


# ----------------------------------------------------------------------------- asset root
def write_root(path):
    layer = new_layer(path, "Composed Gripper Physics. variantSet preset (prismatic | linkage | linkage_smooth) "
                            "authors the selections of the other sets; variantSet mechanism (prismatic | "
                            "linkage) selects geometry here and physics in the payload; variantSet physics "
                            "(usdphysics | none) carries the payload; variantSet pads (grip | smooth) and its "
                            "default selection live inside the payload.")
    layer.customLayerData = {"creator": "composed_gripper export_composed.py", "license": LICENSE,
                             "variant": "v3_composed"}
    stage = Usd.Stage.Open(layer, Usd.Stage.LoadNone)
    stage_metadata(layer)
    root = UsdGeom.Xform.Define(stage, ROOT_PATH).GetPrim()
    Usd.ModelAPI(root).SetKind("component")
    Usd.ModelAPI(root).SetAssetName(ROOT)
    UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Materials")).GetPrim().GetReferences().AddReference(
        "./geometry/materials.usd")
    UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Geometry"))

    # preset comes first: its variants author the selections of the sets evaluated after it.
    # Its variants are filled once the referenced geometry paths are final (see below).
    preset = root.GetVariantSets().AddVariantSet("preset")

    mech = root.GetVariantSets().AddVariantSet("mechanism")
    for mechanism in MECHANISMS:
        mech.AddVariant(mechanism)
        src = src_root(mechanism)
        variant_parent(layer, mechanism, "Geometry")
        for link in children(src, ROOT_PATH.AppendChild("Geometry")):
            dst = vpath(mechanism, f"Geometry/{link.name}")
            assert Sdf.CopySpec(src, link.path, layer, dst)
            layer.GetPrimAtPath(dst).referenceList.prependedItems = [
                Sdf.Reference(f"./geometry/{mechanism}/{link.name}.usd")]

    for name, selections in PRESETS.items():
        preset.AddVariant(name)
        preset.SetVariantSelection(name)
        with preset.GetVariantEditContext():
            for vs, sel in selections.items():
                root.GetVariantSet(vs).SetVariantSelection(sel)
    preset.SetVariantSelection(DEFAULT_PRESET)

    phys = root.GetVariantSets().AddVariantSet("physics")
    phys.AddVariant("none")
    phys.AddVariant("usdphysics")
    phys.SetVariantSelection("usdphysics")
    with phys.GetVariantEditContext():
        root.GetPayloads().AddPayload("./physics/gripper_physics.usda")
        UsdGeom.Scope.Define(stage, ROOT_PATH.AppendChild("Physics"))
        UsdGeom.Scope.Define(stage, ROOT_PATH.AppendPath("Physics/Materials")).GetPrim().GetReferences().AddReference(
            "./physics/physics_materials.usda")

    scene = UsdPhysics.Scene.Define(stage, "/PhysicsScene")
    scene.CreateGravityDirectionAttr(Gf.Vec3f(0, 0, -1))
    scene.CreateGravityMagnitudeAttr(9.81)
    layer.Save()


# ----------------------------------------------------------------------------- demo scene
def write_scene(path):
    tuning_path = os.path.join(OUT, "layers", "scene_tuning.usda")
    os.makedirs(os.path.dirname(tuning_path), exist_ok=True)
    tuning = new_layer(tuning_path, "Scene-wide tuning (sublayer of scene.usda). The class override reaches every "
                                    "prismatic finger joint in every gripper through inherits; the elastomer_base "
                                    "override reaches gripper_b's pads only through specializes.", default_prim=None)
    cls = class_spec(tuning, SCENE_TUNING["class"])
    set_attr(cls, "drive:linear:physics:stiffness", Sdf.ValueTypeNames.Float,
             SCENE_TUNING["drive:linear:physics:stiffness"])
    base = Sdf.CreatePrimInLayer(tuning, "/World/gripper_b/Physics/Materials/elastomer_base")
    for k, v in SCENE_TUNING["elastomer_base"].items():
        set_attr(base, f"physics:{k}", Sdf.ValueTypeNames.Float, v)
    for name, selections in SCENE_SUBLAYER_SELECTIONS.items():
        spec = Sdf.CreatePrimInLayer(tuning, f"/World/{name}")
        for vs, sel in selections.items():
            spec.variantSelections[vs] = sel
    tuning.Save()

    preset_path = os.path.join(OUT, "presets", f"{PRESET_FILE['name']}.usda")
    os.makedirs(os.path.dirname(preset_path), exist_ok=True)
    preset = new_layer(preset_path, "Preset asset: references the gripper and pins its variant selections. "
                                    "Reference this file instead of gripper.usda to get the preset.")
    stage_metadata(preset)
    spec = Sdf.PrimSpec(preset.pseudoRoot, ROOT, Sdf.SpecifierDef, "Xform")
    spec.referenceList.prependedItems = [Sdf.Reference("../gripper.usda")]
    for vs, sel in PRESET_FILE["selections"].items():
        spec.variantSelections[vs] = sel
    preset.Save()

    layer = new_layer(path, "Composed Gripper Physics demo scene: four grippers whose variant selections arrive "
                            "through different arcs (reference/variant/payload, local, sublayer, inherits, "
                            "preset reference), a tuning sublayer, and relocates on gripper_a's pad links.",
                      default_prim="World")
    layer.customLayerData = {"creator": "composed_gripper export_composed.py", "license": LICENSE}
    layer.subLayerPaths = ["./layers/scene_tuning.usda"]
    layer.relocates = [(Sdf.Path(f"/World/gripper_a/Geometry/{s}"), Sdf.Path(f"/World/gripper_a/Geometry/{d}"))
                       for s, d in SCENE_RELOCATES.items()]
    stage_metadata(layer)
    stage = Usd.Stage.Open(layer, Usd.Stage.LoadAll)
    stage.SetEditTarget(layer)
    world = UsdGeom.Xform.Define(stage, "/World").GetPrim()
    Usd.ModelAPI(world).SetKind("assembly")
    cls = stage.CreateClassPrim("/" + SCENE_CLASS["name"])
    for vs, sel in SCENE_CLASS["selections"].items():
        cls.GetVariantSet(vs).SetVariantSelection(sel)
    for name, x, selections, inherits, asset in SCENE_GRIPPERS:
        g = UsdGeom.Xform.Define(stage, f"/World/{name}")
        if inherits:
            g.GetPrim().GetInherits().AddInherit("/" + inherits)
        g.GetPrim().GetReferences().AddReference(asset)
        g.AddTranslateOp().Set(Gf.Vec3d(x, 0, 0))
        for vs, sel in selections.items():
            g.GetPrim().GetVariantSet(vs).SetVariantSelection(sel)
        # body0 of root_joint is the world, so its frame 0 is in world space: shift it with the instance.
        joint = UsdPhysics.Joint(stage.GetPrimAtPath(f"/World/{name}/Physics/root_joint"))
        pos = joint.GetLocalPos0Attr().Get()
        joint.GetLocalPos0Attr().Set(Gf.Vec3f(pos[0] + x, pos[1], pos[2]))
    scene = UsdPhysics.Scene.Define(stage, "/PhysicsScene")
    scene.CreateGravityDirectionAttr(Gf.Vec3f(0, 0, -1))
    scene.CreateGravityMagnitudeAttr(9.81)
    layer.Save()


def export():
    for sub in ("geometry", "physics", "layers", "presets"):  # clear generated content, keep the folder itself
        shutil.rmtree(os.path.join(OUT, sub), ignore_errors=True)
    copy_geometry()
    write_payload(os.path.join(OUT, "physics", "gripper_physics.usda"))
    write_root(os.path.join(OUT, "gripper.usda"))
    write_scene(os.path.join(OUT, "scene.usda"))
    return OUT


if __name__ == "__main__":
    print(export())
