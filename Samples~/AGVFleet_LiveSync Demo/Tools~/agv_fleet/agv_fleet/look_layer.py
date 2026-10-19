"""Environment "look" layer: UsdPreviewSurface translations of the environment's MDL materials.

The Explorer Factory (and the Isaac warehouse) use MDL materials -- OmniPBR, OmniSurface, OmniGlass,
OmniGridExplorer -- which Unity's usd-toolkit importer cannot read; it imports UsdPreviewSurface
reached through a material's universal `outputs:surface`. This module writes, once per environment,
a small generated layer that adds to each MDL material:

    over Material "X"
        token outputs:surface.connect = </.../X/AGV_Preview.outputs:surface>   (universal context)
        def Shader "AGV_Preview"     UsdPreviewSurface: diffuseColor / roughness / metallic / opacity / normal
        def Shader "AGV_Diffuse" ... UsdUVTexture (+ AGV_Normal, AGV_ORM, AGV_st primvar reader)

The MDL network and `outputs:mdl:surface` are untouched, so Isaac's RTX renderer keeps using MDL;
Unity's importer picks up the preview network. The layer is sublayered by root.usda (weakest
sublayer, still stronger than the environment reference) and written by neither runtime app -- it is
derived data, regenerated when the environment changes (env_look_<env>.usda).
"""

from __future__ import annotations

import os
import time

import config as cfg


def look_layer_path():
    return cfg.STAGE_DIR / f"env_look_{cfg.ENV}.usda"


def _val(shader, name):
    """Input value, following connections to the producing attribute (material interface inputs)."""
    inp = shader.GetInput(name)
    if not inp:
        return None
    attrs = inp.GetValueProducingAttributes()
    if attrs:
        v = attrs[0].Get()
        if v is not None:
            return v
    return inp.Get()


def _tex(shader, name):
    from pxr import Sdf

    from .layers import layer_relative

    v = _val(shader, name)
    if isinstance(v, Sdf.AssetPath) and v.path:
        path = v.resolvedPath
        if path and os.path.exists(path):
            return layer_relative(path)   # relative inside a package, so it can be moved / shared
    return None


def _translate(shader, kind):
    """MDL shader -> dict(diffuse, diffuse_tex, roughness, metallic, opacity, normal_tex, orm_tex)."""
    out = {"diffuse": (0.6, 0.6, 0.6), "diffuse_tex": None, "roughness": 0.5, "metallic": 0.0,
           "opacity": 1.0, "normal_tex": None, "orm_tex": None, "emissive": None}

    def col(v, default):
        return tuple(float(c) for c in v) if v is not None else default

    if kind == "OmniPBR":
        c = col(_val(shader, "diffuse_color_constant"), (0.2, 0.2, 0.2))
        tint = col(_val(shader, "diffuse_tint"), (1.0, 1.0, 1.0))
        out["diffuse"] = tuple(a * b for a, b in zip(c, tint))
        out["diffuse_tex"] = _tex(shader, "diffuse_texture")
        out["roughness"] = float(_val(shader, "reflection_roughness_constant") or 0.5)
        out["metallic"] = float(_val(shader, "metallic_constant") or 0.0)
        out["normal_tex"] = _tex(shader, "normalmap_texture")
        if _val(shader, "enable_ORM_texture"):
            out["orm_tex"] = _tex(shader, "ORM_texture")
        if _val(shader, "enable_opacity"):
            out["opacity"] = float(_val(shader, "opacity_constant") or 1.0)
        if _val(shader, "enable_emission"):
            e = col(_val(shader, "emissive_color"), (1.0, 1.0, 1.0))
            out["emissive"] = tuple(min(x * 0.5, 1.0) for x in e)
    elif kind == "OmniSurface":
        c = col(_val(shader, "diffuse_reflection_color"), (0.8, 0.8, 0.8))
        w = float(_val(shader, "diffuse_reflection_weight") or 0.8)
        out["diffuse"] = tuple(x * w for x in c)
        out["diffuse_tex"] = _tex(shader, "diffuse_reflection_color_image")
        out["roughness"] = float(_val(shader, "specular_reflection_roughness") or 0.3)
        out["metallic"] = float(_val(shader, "metalness") or 0.0)
        out["normal_tex"] = _tex(shader, "geometry_normal_image")
    elif kind == "OmniGlass":
        out["diffuse"] = col(_val(shader, "glass_color"), (0.85, 0.9, 0.95))
        out["roughness"] = float(_val(shader, "frosting_roughness") or 0.05)
        out["opacity"] = 0.25
    elif kind == "OmniGridExplorer":
        # Explorer's procedural floor grid: use its background colour, ignore the lines
        out["diffuse"] = col(_val(shader, "background_color_01") or _val(shader, "background_color"), (0.35, 0.36, 0.38))
        out["roughness"] = float(_val(shader, "roughness") or 0.6)
    else:
        return None
    return out


def generate(force: bool = False) -> str | None:
    """Write env_look_<env>.usda for the active environment. Returns its path (None if nothing to do)."""
    from pxr import Sdf, Usd, UsdShade

    path = look_layer_path()
    from .layers import layer_relative

    # location-independent stamp: the env path as written (relative inside a package) + file size
    stamp = f"{layer_relative(cfg.ENV_USD)}|{os.path.getsize(cfg.ENV_USD) if os.path.exists(cfg.ENV_USD) else 0}|v2"
    if path.exists() and not force:
        try:
            if Sdf.Layer.OpenAsAnonymous(str(path)).customLayerData.get("agvLookStamp") == stamp:
                return str(path)
        except Exception:  # noqa: BLE001 -- unreadable: regenerate
            pass
    t0 = time.perf_counter()
    env = Usd.Stage.Open(cfg.ENV_USD)
    default = env.GetDefaultPrim()
    if not default:
        return None
    src_root = str(default.GetPath())               # e.g. /World  (mapped onto /World/Environment)
    layer = Sdf.Layer.CreateAnonymous(".usda")
    layer.comment = ("Generated by Tools/agv_fleet (look_layer.py): UsdPreviewSurface translations of the "
                     "environment's MDL materials for the Unity importer. Derived data - do not edit.")
    layer.customLayerData = {"agvLookStamp": stamp}
    counts = {}
    with Sdf.ChangeBlock():
        for prim in Usd.PrimRange(default):
            if not prim.IsA(UsdShade.Material) or prim.IsInstanceProxy():
                continue
            mat = UsdShade.Material(prim)
            shader = mat.ComputeSurfaceSource("mdl")[0]
            if not shader:
                continue
            src = shader.GetSourceAsset("mdl")
            kind = os.path.splitext(os.path.basename(src.path))[0] if src else ""
            look = _translate(shader, kind)
            if look is None:
                continue
            counts[kind] = counts.get(kind, 0) + 1
            mpath = Sdf.Path("/World/Environment" + str(prim.GetPath())[len(src_root):])
            _author(layer, mpath, look)
    layer.Export(str(path))
    print(f"[agv_fleet] look layer: {sum(counts.values())} materials translated {counts} in {time.perf_counter() - t0:.1f}s -> {path.name}")
    return str(path)


def _author(layer, mpath, look):
    from pxr import Sdf

    T = Sdf.ValueTypeNames
    mspec = Sdf.CreatePrimInLayer(layer, mpath)          # an `over` on the referenced material
    out = Sdf.AttributeSpec(mspec, "outputs:surface", T.Token)
    out.connectionPathList.explicitItems = [mpath.AppendChild("AGV_Preview").AppendProperty("outputs:surface")]

    def shader(name, sid):
        s = Sdf.PrimSpec(mspec, name, Sdf.SpecifierDef, "Shader")
        Sdf.AttributeSpec(s, "info:id", T.Token, Sdf.VariabilityUniform).default = sid
        return s

    def inp(spec, name, tn, value=None, connect=None):
        a = Sdf.AttributeSpec(spec, f"inputs:{name}", tn)
        if value is not None:
            a.default = value
        if connect is not None:
            a.connectionPathList.explicitItems = [connect]
        return a

    ps = shader("AGV_Preview", "UsdPreviewSurface")
    Sdf.AttributeSpec(ps, "outputs:surface", T.Token)
    base = mpath
    st = None
    if look["diffuse_tex"] or look["normal_tex"] or look["orm_tex"]:
        rd = shader("AGV_st", "UsdPrimvarReader_float2")
        inp(rd, "varname", T.Token, "st")
        Sdf.AttributeSpec(rd, "outputs:result", T.Float2)
        st = base.AppendChild("AGV_st").AppendProperty("outputs:result")

    def texture(name, file, outputs, scale=None, bias=None, raw=False):
        tx = shader(name, "UsdUVTexture")
        inp(tx, "file", T.Asset, Sdf.AssetPath(file))
        inp(tx, "st", T.Float2, connect=st)
        inp(tx, "wrapS", T.Token, "repeat")
        inp(tx, "wrapT", T.Token, "repeat")
        if raw:
            inp(tx, "sourceColorSpace", T.Token, "raw")
        if scale:
            inp(tx, "scale", T.Float4, scale)
        if bias:
            inp(tx, "bias", T.Float4, bias)
        for o, tn in outputs:
            Sdf.AttributeSpec(tx, f"outputs:{o}", tn)
        return base.AppendChild(name)

    if look["diffuse_tex"]:
        d = texture("AGV_Diffuse", look["diffuse_tex"], [("rgb", T.Float3)])
        inp(ps, "diffuseColor", T.Color3f, connect=d.AppendProperty("outputs:rgb"))
    else:
        inp(ps, "diffuseColor", T.Color3f, look["diffuse"])
    if look["orm_tex"]:
        o = texture("AGV_ORM", look["orm_tex"], [("g", T.Float), ("b", T.Float)], raw=True)
        inp(ps, "roughness", T.Float, connect=o.AppendProperty("outputs:g"))
        inp(ps, "metallic", T.Float, connect=o.AppendProperty("outputs:b"))
    else:
        inp(ps, "roughness", T.Float, float(look["roughness"]))
        inp(ps, "metallic", T.Float, float(look["metallic"]))
    if look["normal_tex"]:
        n = texture("AGV_Normal", look["normal_tex"], [("rgb", T.Float3)], scale=(2, 2, 2, 1), bias=(-1, -1, -1, 0), raw=True)
        inp(ps, "normal", T.Normal3f, connect=n.AppendProperty("outputs:rgb"))
    if look["opacity"] < 1.0:
        inp(ps, "opacity", T.Float, float(look["opacity"]))
    if look["emissive"]:
        inp(ps, "emissiveColor", T.Color3f, look["emissive"])
