"""Single source of truth for the Composed Gripper Physics gripper (original design).

Both variants share the flange and housing styling:
  v1_prismatic : two prismatic fingers, each with its own linear drive.
  v2_linkage   : per side a four-bar parallelogram (driver + follower link), loop closed by a
                 joint with excludeFromArticulation; tree joints carry coordinated drives.

Conventions
  * Units: meters, kilograms, seconds. Z up. +Z is the approach direction (flange at z = 0).
  * X is the stroke axis; the left finger sits at -X, the right finger at +X.
  * Every link frame sits on its joint pivot. Joint value 0 == fully open, positive == closing.
  * Right-side geometry is the exact mirror (x -> -x) of the left side; its joint frames are
    rotated 180 deg about Z so the same positive target closes both fingers.
"""
import math

# ----------------------------------------------------------------------------- point sets
def box(lo, hi):
    (x0, y0, z0), (x1, y1, z1) = lo, hi
    return [(x, y, z) for x in (x0, x1) for y in (y0, y1) for z in (z0, z1)]


def cyl(axis, center, r, half, n):
    cx, cy, cz = center
    pts = []
    for i in range(n):
        a = 2.0 * math.pi * (i + 0.5) / n
        u, v = r * math.cos(a), r * math.sin(a)
        for h in (-half, half):
            if axis == "X":
                pts.append((cx + h, cy + u, cz + v))
            elif axis == "Y":
                pts.append((cx + u, cy + h, cz + v))
            else:
                pts.append((cx + u, cy + v, cz + h))
    return pts


def stadium_y(p0, p1, r, y0, y1, n):
    """Convex hull points of two Y-axis cylinders (a rounded bar lying in the XZ plane)."""
    c = (y0 + y1) / 2.0
    h = (y1 - y0) / 2.0
    return cyl("Y", (p0[0], c, p0[1]), r, h, n) + cyl("Y", (p1[0], c, p1[1]), r, h, n)


def mirror_pts(pts):
    return [(-x, y, z) for (x, y, z) in pts]


def part(pts, material, bevel=0.0, segments=2):
    return {"points": pts, "material": material, "bevel": bevel, "segments": segments}


# ----------------------------------------------------------------------------- materials
MATERIALS = {
    # name: (base color, metallic, roughness)
    "housing_light": ((0.62, 0.64, 0.66), 0.55, 0.38),
    "flange_dark": ((0.07, 0.07, 0.075), 0.70, 0.35),
    "link_graphite": ((0.17, 0.18, 0.19), 0.50, 0.45),
    "finger_slate": ((0.26, 0.31, 0.37), 0.40, 0.42),
    "pad_rubber": ((0.025, 0.025, 0.025), 0.00, 0.90),
    "pin_steel": ((0.80, 0.80, 0.82), 1.00, 0.25),
}

# Physics material bound to the fingertip pad colliders (core PhysicsMaterialAPI only).
PAD_MATERIAL = {"staticFriction": 1.0, "dynamicFriction": 0.9, "restitution": 0.0}


# ----------------------------------------------------------------------------- shared base parts
def _flange_parts():
    parts = [
        part(cyl("Z", (0, 0, 0.005), 0.032, 0.005, 48), "flange_dark", 0.0008),
        part(cyl("Z", (0, 0, 0.0145), 0.026, 0.0045, 48), "flange_dark", 0.0005),
    ]
    for i in range(4):  # bolt heads on the flange shoulder
        a = math.radians(45 + 90 * i)
        parts.append(part(cyl("Z", (0.029 * math.cos(a), 0.029 * math.sin(a), 0.0106), 0.0024, 0.0008, 12),
                          "pin_steel", 0.0003, 1))
    return parts


FLANGE_COLLIDER = cyl("Z", (0, 0, 0.009), 0.032, 0.009, 16)  # spans z 0 .. 0.018


# ============================================================================= V1 prismatic
V1_FINGER_ORIGIN = (-0.058, 0.0, 0.094)  # left carriage, fully open
V1_STROKE = 0.040                          # per finger (80 mm total)
V1_PAD_LOCAL = (0.012, 0.0, 0.084)         # pad frame on the blade's inner face


def _v1():
    base_render = _flange_parts() + [
        part(box((-0.072, -0.034, 0.018), (0.072, 0.034, 0.088)), "housing_light", 0.004, 3),
        part(box((-0.0725, -0.0345, 0.050), (0.0725, 0.0345, 0.056)), "link_graphite", 0.0005, 1),
        part(box((-0.070, -0.010, 0.087), (0.070, 0.010, 0.0935)), "flange_dark", 0.0008, 1),
    ]
    base_cols = [FLANGE_COLLIDER, box((-0.072, -0.034, 0.018), (0.072, 0.034, 0.0935))]

    blade = [(0.000, -0.016, 0.0155), (0.012, -0.016, 0.0155), (0.000, 0.016, 0.0155), (0.012, 0.016, 0.0155),
             (0.005, -0.016, 0.110), (0.012, -0.016, 0.110), (0.005, 0.016, 0.110), (0.012, 0.016, 0.110)]
    carriage = box((-0.014, -0.030, 0.0005), (0.014, 0.030, 0.016))
    finger_render = [part(carriage, "link_graphite", 0.0015), part(blade, "finger_slate", 0.0015)]
    for z in (0.004, 0.012):  # cap screws on the carriage outer face
        finger_render.append(part(cyl("X", (-0.0142, 0.0, z + 0.0005), 0.0025, 0.0006, 12), "pin_steel", 0.0002, 1))
    finger_cols = [carriage, blade]

    pad_render = [part(box((0.0, -0.014, -0.022), (0.0025, 0.014, 0.022)), "pad_rubber", 0.0006)]
    for i in range(5):
        zc = -0.018 + 0.009 * i
        pad_render.append(part(box((0.002, -0.0135, zc - 0.0025), (0.004, 0.0135, zc + 0.0025)),
                               "pad_rubber", 0.0005, 1))
    pad_cols = [box((0.0, -0.014, -0.022), (0.004, 0.014, 0.022))]

    fo = V1_FINGER_ORIGIN
    pad_origin = (fo[0] + V1_PAD_LOCAL[0], 0.0, fo[2] + V1_PAD_LOCAL[2])
    links = [
        {"name": "base_link", "origin": (0, 0, 0), "rot_y": 0.0, "mass": 0.95,
         "render": base_render, "colliders": base_cols},
        {"name": "left_finger_link", "origin": fo, "rot_y": 0.0, "mass": 0.080,
         "render": finger_render, "colliders": finger_cols},
        {"name": "left_pad_link", "origin": pad_origin, "rot_y": 0.0, "mass": 0.012,
         "render": pad_render, "colliders": pad_cols, "pad": True},
    ]
    linear_drive = {"kind": "linear", "stiffness": 2000.0, "damping": 200.0, "maxForce": 100.0}
    joints = [
        {"name": "root_joint", "type": "fixed", "body0": None, "body1": "base_link", "pivot": (0, 0, 0)},
        {"name": "left_finger_joint", "type": "prismatic", "body0": "base_link", "body1": "left_finger_link",
         "pivot": fo, "axis": "X", "limits": (0.0, V1_STROKE), "drive": linear_drive},
        {"name": "left_pad_joint", "type": "fixed", "body0": "left_finger_link", "body1": "left_pad_link",
         "pivot": pad_origin},
    ]
    return links, joints


# ============================================================================= V2 linkage
V2_PIVOT_Z = 0.100
V2_OUTER_X = -0.042        # driver-link base pivot (left)
V2_INNER_X = -0.022        # follower-link base pivot (left)
V2_LINK_LEN = 0.055
V2_OPEN_TILT = 28.0        # deg, links lean outward when open
V2_TRAVEL = 40.0           # deg, open -> closed
V2_PAD_LOCAL = (0.0246, 0.0, 0.067)


def v2_link_dir(tilt_deg):
    t = math.radians(tilt_deg)
    return (-math.sin(t), 0.0, math.cos(t))


def _v2():
    u = v2_link_dir(V2_OPEN_TILT)
    po = (V2_OUTER_X, 0.0, V2_PIVOT_Z)
    pi_ = (V2_INNER_X, 0.0, V2_PIVOT_Z)
    qo = (po[0] + V2_LINK_LEN * u[0], 0.0, po[2] + V2_LINK_LEN * u[2])
    qi = (pi_[0] + V2_LINK_LEN * u[0], 0.0, pi_[2] + V2_LINK_LEN * u[2])
    finger_dx = qi[0] - qo[0]  # 0.020, knuckle pin spacing

    base_render = _flange_parts() + [
        part(box((-0.050, -0.032, 0.018), (0.050, 0.032, 0.080)), "housing_light", 0.004, 3),
        part(box((-0.0505, -0.0325, 0.044), (0.0505, 0.0325, 0.050)), "link_graphite", 0.0005, 1),
    ]
    base_cols = [FLANGE_COLLIDER, box((-0.050, -0.032, 0.018), (0.050, 0.032, 0.080))]
    for s in (-1, 1):  # clevis plates either side of the links
        y0, y1 = sorted((s * 0.016, s * 0.022))
        plate = box((-0.052, y0, 0.076), (0.052, y1, 0.100))
        for x in (V2_OUTER_X, V2_INNER_X, -V2_INNER_X, -V2_OUTER_X):
            plate += cyl("Y", (x, (y0 + y1) / 2, V2_PIVOT_Z), 0.009, (y1 - y0) / 2, 24)
        base_render.append(part(plate, "flange_dark", 0.0008))
        col = box((-0.052, y0, 0.080), (0.052, y1, 0.100))
        for x in (V2_OUTER_X, V2_INNER_X, -V2_INNER_X, -V2_OUTER_X):
            col += cyl("Y", (x, (y0 + y1) / 2, V2_PIVOT_Z), 0.009, (y1 - y0) / 2, 8)
        base_cols.append(col)
    for x in (V2_OUTER_X, V2_INNER_X, -V2_INNER_X, -V2_OUTER_X):  # base pivot pins
        base_render.append(part(cyl("Y", (x, 0, V2_PIVOT_Z), 0.003, 0.0235, 16), "pin_steel", 0.0003, 1))

    L = V2_LINK_LEN
    driver = stadium_y((0, 0), (0, L), 0.006, -0.012, 0.012, 24)
    follower = stadium_y((0, 0), (0, L), 0.005, -0.010, 0.010, 24)

    finger_render, finger_cols = [], []
    knuckle_col = []
    for s in (-1, 1):
        y0, y1 = sorted((s * 0.013, s * 0.019))
        plate = stadium_y((0, 0), (finger_dx, 0), 0.0075, y0, y1, 24)
        finger_render.append(part(plate, "finger_slate", 0.0008))
        knuckle_col += stadium_y((0, 0), (finger_dx, 0), 0.0075, y0, y1, 8)
    bridge = box((-0.004, -0.019, 0.006), (0.024, 0.019, 0.0165))
    blade = [(0.0126, -0.013, 0.0160), (0.0246, -0.013, 0.0160), (0.0126, 0.013, 0.0160), (0.0246, 0.013, 0.0160),
             (0.0180, -0.013, 0.0910), (0.0246, -0.013, 0.0910), (0.0180, 0.013, 0.0910), (0.0246, 0.013, 0.0910)]
    finger_render += [part(bridge, "finger_slate", 0.0015), part(blade, "finger_slate", 0.0015)]
    for x in (0.0, finger_dx):  # knuckle pins
        finger_render.append(part(cyl("Y", (x, 0, 0), 0.003, 0.0205, 16), "pin_steel", 0.0003, 1))
    finger_cols = [knuckle_col + bridge, blade]

    pad_render = [part(box((0.0, -0.012, -0.020), (0.0025, 0.012, 0.020)), "pad_rubber", 0.0006)]
    for i in range(4):
        zc = -0.0135 + 0.009 * i
        pad_render.append(part(box((0.002, -0.0115, zc - 0.0025), (0.004, 0.0115, zc + 0.0025)),
                               "pad_rubber", 0.0005, 1))
    pad_cols = [box((0.0, -0.012, -0.020), (0.004, 0.012, 0.020))]
    pad_origin = (qo[0] + V2_PAD_LOCAL[0], 0.0, qo[2] + V2_PAD_LOCAL[2])

    links = [
        {"name": "base_link", "origin": (0, 0, 0), "rot_y": 0.0, "mass": 0.85,
         "render": base_render, "colliders": base_cols},
        {"name": "left_driver_link", "origin": po, "rot_y": -V2_OPEN_TILT, "mass": 0.040,
         "render": [part(driver, "link_graphite", 0.0008)], "colliders": [stadium_y((0, 0), (0, L), 0.006, -0.012, 0.012, 8)]},
        {"name": "left_follower_link", "origin": pi_, "rot_y": -V2_OPEN_TILT, "mass": 0.025,
         "render": [part(follower, "link_graphite", 0.0008)], "colliders": [stadium_y((0, 0), (0, L), 0.005, -0.010, 0.010, 8)]},
        {"name": "left_finger_link", "origin": qo, "rot_y": 0.0, "mass": 0.060,
         "render": finger_render, "colliders": finger_cols},
        {"name": "left_pad_link", "origin": pad_origin, "rot_y": 0.0, "mass": 0.010,
         "render": pad_render, "colliders": pad_cols, "pad": True},
    ]
    # Angular drive gains are per degree (UsdPhysics convention).
    joints = [
        {"name": "root_joint", "type": "fixed", "body0": None, "body1": "base_link", "pivot": (0, 0, 0)},
        {"name": "left_driver_joint", "type": "revolute", "body0": "base_link", "body1": "left_driver_link",
         "pivot": po, "axis": "Y", "limits": (0.0, V2_TRAVEL),
         "drive": {"kind": "angular", "stiffness": 0.5, "damping": 0.05, "maxForce": 4.0}},
        {"name": "left_follower_joint", "type": "revolute", "body0": "base_link", "body1": "left_follower_link",
         "pivot": pi_, "axis": "Y", "limits": (0.0, V2_TRAVEL),
         "drive": {"kind": "angular", "stiffness": 0.2, "damping": 0.02, "maxForce": 2.0}},
        {"name": "left_finger_joint", "type": "revolute", "body0": "left_driver_link", "body1": "left_finger_link",
         "pivot": qo, "axis": "Y", "limits": (-V2_TRAVEL, 0.0),
         "drive": {"kind": "angular", "stiffness": 1.0, "damping": 0.1, "maxForce": 5.0}},
        {"name": "left_loop_joint", "type": "revolute", "body0": "left_follower_link", "body1": "left_finger_link",
         "pivot": qi, "axis": "Y", "exclude": True},
        {"name": "left_pad_joint", "type": "fixed", "body0": "left_finger_link", "body1": "left_pad_link",
         "pivot": pad_origin},
    ]
    return links, joints


# ----------------------------------------------------------------------------- mirroring
def _mirror(name):
    return name.replace("left_", "right_") if name else name


def _with_right_side(links, joints):
    out_l, out_j = [], []
    for l in links:
        out_l.append(dict(l))
        if l["name"].startswith("left_"):
            r = dict(l)
            r["name"] = _mirror(l["name"])
            r["origin"] = (-l["origin"][0], l["origin"][1], l["origin"][2])
            r["rot_y"] = -l["rot_y"]
            r["render"] = [dict(p, points=mirror_pts(p["points"])) for p in l["render"]]
            r["colliders"] = [mirror_pts(c) for c in l["colliders"]]
            out_l.append(r)
    for j in joints:
        out_j.append(dict(j, flip=False))
        if j["name"].startswith("left_"):
            r = dict(j, flip=True)
            r["name"] = _mirror(j["name"])
            r["body0"], r["body1"] = _mirror(j["body0"]), _mirror(j["body1"])
            r["pivot"] = (-j["pivot"][0], j["pivot"][1], j["pivot"][2])
            out_j.append(r)
    return out_l, out_j


def get_variant(variant):
    links, joints = {"v1_prismatic": _v1, "v2_linkage": _v2}[variant]()
    return _with_right_side(links, joints)


VARIANTS = ("v1_prismatic", "v2_linkage")
