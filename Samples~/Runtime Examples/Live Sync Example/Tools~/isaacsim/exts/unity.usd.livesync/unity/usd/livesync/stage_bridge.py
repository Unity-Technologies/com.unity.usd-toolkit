"""
The `pxr`-dependent half of the Unity -> Isaac Sim live transform bridge.

Import this ONLY after Kit is up (i.e. after `SimulationApp(...)` in a standalone script, or from
inside a Kit extension) — `pxr` is not importable from Isaac's bundled interpreter before that.

Two responsibilities:

  1. `mount_unity_stage()` — get Unity's `base_stage.usda` geometry into the Isaac stage once, with
     the up-axis / units mismatch handled, and report the prim-path prefix the live stream must use.
  2. `StageBridge` — apply the streamed transforms onto those prims every update, cheaply.

Why this authors a matrix op instead of translate/orient/scale (which is what
`Tools/usd_live_sync.py --checkpoint` writes): the baseline already authors exactly one
`xformOp:transform` per Xform, so reusing that op means every frame is a single attribute write with
NO change to `xformOpOrder`. Rewriting the op stack 60x/second would churn composition for no gain.
The checkpoint writer has the opposite constraint (it is authoring a standalone override layer from
scratch), so the two differ on purpose.
"""

from __future__ import annotations

from pxr import Gf, Sdf, Usd, UsdGeom

from .core import (
    unity_to_usd_quat,
    unity_to_usd_translate,
    usd_to_unity_quat,
    usd_to_unity_translate,
)


# ==================================================================================================
# Matrix construction
# ==================================================================================================

def usd_matrix_from_unity_trs(trs):
    """
    Build the `matrix4d xformOp:transform` value for one streamed prim.

    Gf uses the row-vector convention (points transform as p * M), so the composition order that
    yields "scale, then rotate, then translate" is S * R * T.
    """
    t = unity_to_usd_translate(trs.get("t", (0.0, 0.0, 0.0)))
    w, qx, qy, qz = unity_to_usd_quat(trs.get("r", (0.0, 0.0, 0.0, 1.0)))
    s = trs.get("s", (1.0, 1.0, 1.0))

    sm = Gf.Matrix4d().SetScale(Gf.Vec3d(float(s[0]), float(s[1]), float(s[2])))
    rm = Gf.Matrix4d().SetRotate(Gf.Quatd(w, qx, qy, qz))
    tm = Gf.Matrix4d().SetTranslate(Gf.Vec3d(t[0], t[1], t[2]))
    return sm * rm * tm


def unity_trs_from_usd_matrix(matrix):
    """Inverse of `usd_matrix_from_unity_trs`: decompose a local matrix back to RAW Unity T/R/S."""
    xf = Gf.Transform(matrix)
    tr = xf.GetTranslation()
    quat = xf.GetRotation().GetQuat()
    sc = xf.GetScale()

    imag = quat.GetImaginary()
    t = usd_to_unity_translate((tr[0], tr[1], tr[2]))
    r = usd_to_unity_quat((quat.GetReal(), imag[0], imag[1], imag[2]))
    return {
        "t": [t[0], t[1], t[2]],
        "r": [r[0], r[1], r[2], r[3]],
        "s": [float(sc[0]), float(sc[1]), float(sc[2])],
    }


# ==================================================================================================
# Mounting Unity's baseline into an Isaac stage
# ==================================================================================================

def inspect_stage_metadata(usd_path):
    """Read (upAxis, metersPerUnit) out of a USD file without composing its payloads."""
    stage = Usd.Stage.Open(str(usd_path), load=Usd.Stage.LoadNone)
    if stage is None:
        raise RuntimeError("could not open USD file: {}".format(usd_path))
    return UsdGeom.GetStageUpAxis(stage), UsdGeom.GetStageMetersPerUnit(stage)


def mount_unity_stage(stage, base_stage_path, root_prim_name="SyncRoot",
                      wrapper_path="/World/UnityScene", use_payload=False, log=print):
    """
    Reference Unity's `base_stage.usda` into `stage` and return the prim-path prefix that maps a
    wire path onto a real stage path.

    Layout::

        /World/UnityScene            <- wrapper Xform: owns the up-axis / units fix-up ONLY
        /World/UnityScene/SyncRoot   <- reference to base_stage.usda (its defaultPrim is SyncRoot)
        /World/UnityScene/SyncRoot/… <- the Unity hierarchy, driven by the live stream

    Two details that are easy to get wrong and are the reason this is a function and not two lines:

      * A USD reference maps the referenced layer's `defaultPrim` ONTO the referencing prim — it does
        not nest under it. `base_stage.usda` declares `defaultPrim = "SyncRoot"`, so the reference
        must be attached to a prim *named* `SyncRoot`. Do that, and a wire path `/SyncRoot/Box` is
        simply `prefix + wire_path`; attach it to a prim named anything else and every streamed path
        silently fails to resolve.
      * `upAxis` and `metersPerUnit` are ROOT-LAYER metadata. A referenced layer's values are ignored
        by composition, so Unity's Y-up baseline dropped into Isaac's default Z-up stage arrives
        lying on its side. The wrapper Xform carries the correcting rotation (and a units scale if
        needed) so the fix-up is applied exactly once, in one place.

    Keeping the fix-up on a *separate* wrapper prim rather than on `SyncRoot` itself is what makes
    the whole thing composable: the live stream owns `SyncRoot`'s transform (Unity tracks it in
    `AllDescendants` mode), so anything we authored there would be overwritten on the first delta.
    And because the stream only ever authors *local* transforms on descendants, the wrapper's basis
    change composes through automatically — no per-prim coordinate math changes.
    """
    base_stage_path = str(base_stage_path).replace("\\", "/")
    base_up, base_mpu = inspect_stage_metadata(base_stage_path)
    stage_up = UsdGeom.GetStageUpAxis(stage)
    stage_mpu = UsdGeom.GetStageMetersPerUnit(stage)

    # Make sure every ancestor of the wrapper exists as an Xform (Isaac convention: /World root).
    for ancestor in Sdf.Path(wrapper_path).GetPrefixes():
        if not stage.GetPrimAtPath(ancestor).IsValid():
            UsdGeom.Xform.Define(stage, ancestor)

    wrapper = UsdGeom.Xform.Define(stage, wrapper_path)
    wrapper.ClearXformOpOrder()

    # --- up-axis fix-up -------------------------------------------------------------------------
    # A +90 deg rotation about X sends +Y to +Z; -90 sends +Z to +Y.
    if base_up != stage_up:
        angle = 90.0 if (base_up == UsdGeom.Tokens.y and stage_up == UsdGeom.Tokens.z) else -90.0
        wrapper.AddRotateXOp(UsdGeom.XformOp.PrecisionDouble).Set(angle)
        log("[unity-live-sync] up-axis fix-up: baseline is {}-up, stage is {}-up -> rotateX {:+.0f} "
            "on {}".format(base_up, stage_up, angle, wrapper_path))

    # --- units fix-up ---------------------------------------------------------------------------
    # Uniform scale, so it commutes with the rotation above and op order is irrelevant.
    if base_mpu > 0.0 and stage_mpu > 0.0 and abs(base_mpu - stage_mpu) > 1e-12:
        factor = base_mpu / stage_mpu
        wrapper.AddScaleOp(UsdGeom.XformOp.PrecisionDouble).Set(Gf.Vec3d(factor, factor, factor))
        log("[unity-live-sync] units fix-up: baseline {} m/unit vs stage {} m/unit -> scale x{:.6g}"
            .format(base_mpu, stage_mpu, factor))

    # --- the reference itself -------------------------------------------------------------------
    mount_path = "{}/{}".format(wrapper_path.rstrip("/"), root_prim_name)
    mount = UsdGeom.Xform.Define(stage, mount_path)
    mount_prim = mount.GetPrim()

    # Idempotent: mounting twice (the panel has a button, and buttons get pressed twice) must not
    # stack a second reference onto the same prim. Clearing both arc types also lets a re-mount
    # switch between reference and payload cleanly.
    mount_prim.GetReferences().ClearReferences()
    mount_prim.GetPayloads().ClearPayloads()

    if use_payload:
        mount_prim.GetPayloads().AddPayload(base_stage_path)
    else:
        mount_prim.GetReferences().AddReference(base_stage_path)

    log("[unity-live-sync] mounted {} at {} (wire prefix '{}')"
        .format(base_stage_path, mount_path, wrapper_path))
    return wrapper_path


# ==================================================================================================
# Per-update transform applier
# ==================================================================================================

class StageBridge:
    """
    Applies a coalesced `{wire_prim_path: {'t','r','s'}}` batch onto the Isaac stage.

    Caches the resolved `xformOp:transform` attribute per wire path, so steady-state cost is one
    `Sdf` value write per changed prim per tick — no prim lookups, no op-stack inspection, and the
    whole batch lands inside a single `Sdf.ChangeBlock` so downstream (Hydra / Fabric) sees one
    change round instead of N.
    """

    def __init__(self, stage, prim_prefix="", log=print, max_missing_logs=8):
        self._stage = stage
        self._prefix = (prim_prefix or "").rstrip("/")
        self._log = log
        self._max_missing_logs = max_missing_logs

        self._attrs = {}            # wire path -> Usd.Attribute (matrix4d xformOp:transform)
        self._unresolvable = set()  # wire paths already reported as absent; do not retry/spam
        self.applied_total = 0
        self.missing_total = 0

    # -- configuration ----------------------------------------------------------------------------

    @property
    def stage(self):
        return self._stage

    @property
    def prim_prefix(self):
        return self._prefix

    def set_stage(self, stage):
        self._stage = stage
        self.invalidate()

    def set_prim_prefix(self, prefix):
        self._prefix = (prefix or "").rstrip("/")
        self.invalidate()

    def invalidate(self):
        """Drop all cached handles. Call after the stage or prefix changes, or after a re-mount."""
        self._attrs.clear()
        self._unresolvable.clear()

    def stage_path(self, wire_path):
        """Map a Unity wire prim path (e.g. '/SyncRoot/Box') onto this stage's prim path."""
        if not wire_path.startswith("/"):
            wire_path = "/" + wire_path
        return self._prefix + wire_path if self._prefix else wire_path

    # -- the hot path -----------------------------------------------------------------------------

    def apply(self, prims):
        """
        Author every entry in `prims`. Returns (applied, missing).

        Handle resolution happens BEFORE the change block on purpose: resolving reads composed state
        (`GetOrderedXformOps`) and may create specs, neither of which is safe to interleave with a
        `Sdf.ChangeBlock` — inside a block you must not depend on composed values being up to date.
        """
        if not prims:
            return 0, 0

        resolved = []
        missing = 0
        for wire_path, trs in prims.items():
            attr = self._matrix_attr(wire_path)
            if attr is None:
                missing += 1
                continue
            resolved.append((attr, usd_matrix_from_unity_trs(trs)))

        if resolved:
            with Sdf.ChangeBlock():
                for attr, matrix in resolved:
                    attr.Set(matrix)

        self.applied_total += len(resolved)
        self.missing_total += missing
        return len(resolved), missing

    def _matrix_attr(self, wire_path):
        """Resolve (and cache) the single matrix xformOp we drive for this prim."""
        attr = self._attrs.get(wire_path)
        if attr is not None:
            return attr
        if wire_path in self._unresolvable:
            return None

        prim = self._stage.GetPrimAtPath(self.stage_path(wire_path))
        if not prim or not prim.IsValid():
            self._unresolvable.add(wire_path)
            if len(self._unresolvable) <= self._max_missing_logs:
                self._log("[unity-live-sync] no prim at '{}' for wire path '{}' — not synced"
                          .format(self.stage_path(wire_path), wire_path))
            elif len(self._unresolvable) == self._max_missing_logs + 1:
                self._log("[unity-live-sync] (further missing-prim warnings suppressed)")
            return None

        xformable = UsdGeom.Xformable(prim)
        ops = xformable.GetOrderedXformOps()

        # Reuse the baseline's single matrix op when that is exactly what is there (the common case,
        # and the cheap one). Anything else — a translate/orient/scale stack, extra ops, an inverse
        # op — gets replaced, because we own this prim's local transform and must not leave a
        # leftover op silently composing on top of the streamed pose.
        if (len(ops) == 1
                and ops[0].GetOpType() == UsdGeom.XformOp.TypeTransform
                and not ops[0].IsInverseOp()):
            op = ops[0]
        else:
            xformable.ClearXformOpOrder()
            op = xformable.AddTransformOp(UsdGeom.XformOp.PrecisionDouble)

        attr = op.GetAttr()
        self._attrs[wire_path] = attr
        return attr

    # -- Isaac -> Unity direction ------------------------------------------------------------------

    def read_unity_trs(self, wire_path):
        """
        Read a prim's CURRENT local transform back out as RAW Unity T/R/S, ready to hand to
        `SyncClient.send_set_transform`. Returns None if the prim is absent.

        Local (not world) is deliberate: the wire protocol is local-space throughout, and reading
        local keeps the wrapper's up-axis fix-up out of the value — a world-space read would send
        Unity a pose polluted by the Z-up correction.
        """
        prim = self._stage.GetPrimAtPath(self.stage_path(wire_path))
        if not prim or not prim.IsValid():
            return None
        xformable = UsdGeom.Xformable(prim)
        matrix = xformable.GetLocalTransformation(Usd.TimeCode.Default())
        return unity_trs_from_usd_matrix(matrix)

    def stats_line(self):
        return ("applied {} prim writes | {} unresolved path(s) | prefix '{}'"
                .format(self.applied_total, len(self._unresolvable), self._prefix or "<none>"))
