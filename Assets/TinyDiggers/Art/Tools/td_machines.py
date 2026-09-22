"""Turns the Meshy scans of the two machines into game-ready, rigged units.

Ronan, 2026-09-22: "these need to be finished cleaned up and rigged in blender they are missing or
not complete they need animations for the legs walking and scooping and anything else they might
need". His rulings that day: the machines stand **1.8 m** to the top of the shell (the crew robot
is 0.43 m, and sets human scale); keep the scans' baked look but **ease the rust**; and they need
walk, idle, turn in place, the digger's scoop and the hauler's tip.

The two scans are single meshes of about 10k triangles with one baked material, no rig, no parts,
and roughly 750 disconnected shells each (600 of them shards of a few vertices). So this runs in
steps, each of which can be re-run when a scan is replaced:

    stage 1  clean     weld the shell, drop the shards, fix the normals, stand it on the floor
                       facing Blender +Y at its true size
    stage 2  parts     cut it into the pieces a rig needs (body, turret, boom, stick, bucket,
                       tray, and four legs of three segments)
    stage 3  rig       an armature per machine, rigid weights, feet on IK
    stage 4  clips     walk, idle, turn, scoop, tip
    stage 5  export    FBX for Unity (see the gotchas in the crew notes: bake_space_transform
                       off, model facing +Y)

Run it inside Blender (the MCP addon or `blender --python`):

    import td_machines; td_machines.clean_all()
"""
import math
import os

import bmesh
import bpy
import mathutils

# The scans, as Meshy delivered them.
DOWNLOADS = os.path.join(os.path.expanduser("~"), "Downloads")
SCANS = {
    "digger": os.path.join(DOWNLOADS, "Meshy_AI_Orbital_Excavator_0922004840_texture.glb"),
    "hauler": os.path.join(DOWNLOADS, "Meshy_AI__0922175014_texture.glb"),
}

# Ronan's scale ruling: 1.8 m to the top of the shell, which is the body ball, not the boom or
# the raised tray. The crew robot is 0.43 m, so a machine stands about four robots high.
SHELL_TOP = 1.8

# Vertices closer than this (in the scan's own unit-box space) are welded together. The scans are
# shells of separate panels laid against each other; below about 1 mm of a unit box nothing joins,
# above about 5 mm the thin boom pinches shut.
WELD = 0.0025

# Shells smaller than this share of the whole are shards from the scan and are dropped.
SHARD_VERTS = 20


def _log(message):
    print(f"[td_machines] {message}")


def _shells(bm):
    """Connected vertex groups, biggest first."""
    seen = set()
    groups = []
    for vert in bm.verts:
        if vert.index in seen:
            continue
        stack = [vert]
        seen.add(vert.index)
        group = []
        while stack:
            current = stack.pop()
            group.append(current)
            for edge in current.link_edges:
                other = edge.other_vert(current)
                if other.index not in seen:
                    seen.add(other.index)
                    stack.append(other)
        groups.append(group)
    groups.sort(key=len, reverse=True)
    return groups


def _ball_top(obj):
    """
    How far the top of the body ball stands over the feet, in object space, and how many vertices
    the ball has. After welding, the ball is the biggest shell by a long way: the boom, the tray
    and the legs are separate pieces resting against it.

    It is measured over the feet, not over the object's origin, because that is what Ronan's
    1.8 m means: what the machine stands, the way you would measure it in the yard.
    """
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    groups = _shells(bm)
    floor = min(v.co.z for v in bm.verts) if bm.verts else 0.0
    top = max(v.co.z for v in groups[0]) if groups else 0.0
    verts = len(groups[0]) if groups else 0
    bm.free()
    return top - floor, verts


def clean(name, path=None, weld=WELD):
    """
    Stage 1. Imports one scan and leaves a single clean mesh object called <name>: welded, free of
    shards, normals out, standing on z = 0, facing +Y, at its true size. Returns what it did.
    """
    path = path or SCANS[name]
    if not os.path.exists(path):
        raise FileNotFoundError(path)

    for old in [o for o in bpy.data.objects if o.name == name]:
        bpy.data.objects.remove(old, do_unlink=True)

    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    fresh = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in fresh if o.type == 'MESH']
    if not meshes:
        raise RuntimeError(f"{path} holds no mesh")

    obj = meshes[0]
    for extra in fresh:
        if extra is not obj:
            bpy.data.objects.remove(extra, do_unlink=True)
    obj.name = name
    obj.data.name = f"{name}_mesh"

    report = {"before": {"verts": len(obj.data.vertices), "tris": len(obj.data.polygons)}}

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=weld)
    bm.verts.ensure_lookup_table()
    report["shells_after_weld"] = len(_shells(bm))

    # Shards: the scan leaves hundreds of loose slivers that no rig part wants.
    shards = [v for group in _shells(bm) if len(group) < SHARD_VERTS for v in group]
    if shards:
        bmesh.ops.delete(bm, geom=shards, context='VERTS')
    bm.verts.ensure_lookup_table()

    bmesh.ops.dissolve_degenerate(bm, dist=weld * 0.5, edges=bm.edges)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(obj.data)
    report["shards_dropped"] = len(shards)
    report["shells_after_shards"] = len(_shells(bm))
    bm.free()

    obj.data.update()
    for polygon in obj.data.polygons:
        polygon.use_smooth = True

    # Stand it up: the scans come Y-up out of glTF, so the import already turned them Z-up.
    #
    # The transform is baked into the mesh by hand rather than with object.transform_apply, which
    # needs a window and a selection and does nothing at all when it is driven over the MCP
    # bridge (it left the machines at scale 3.2 on 2026-09-22, and every measurement after that
    # was in scan units).
    obj.rotation_euler = (0.0, 0.0, 0.0)
    obj.location = (0.0, 0.0, 0.0)
    obj.scale = (1.0, 1.0, 1.0)

    top, ball_verts = _ball_top(obj)
    scale = SHELL_TOP / top if top > 1e-6 else 1.0
    obj.data.transform(mathutils.Matrix.Scale(scale, 4))

    # Feet on the floor, body centred over the origin.
    lo = mathutils.Vector((min(v.co.x for v in obj.data.vertices),
                           min(v.co.y for v in obj.data.vertices),
                           min(v.co.z for v in obj.data.vertices)))
    hi = mathutils.Vector((max(v.co.x for v in obj.data.vertices),
                           max(v.co.y for v in obj.data.vertices),
                           max(v.co.z for v in obj.data.vertices)))
    obj.data.transform(mathutils.Matrix.Translation((-(lo.x + hi.x) * 0.5, -(lo.y + hi.y) * 0.5, -lo.z)))
    obj.data.update()

    report["after"] = {
        "verts": len(obj.data.vertices),
        "tris": sum(len(p.vertices) - 2 for p in obj.data.polygons),
        "ball_verts": ball_verts,
        "scale": round(scale, 4),
        "dims_m": [round(v, 3) for v in obj.dimensions],
        "shell_top_m": round(SHELL_TOP, 3),
    }
    _log(f"{name}: {report['before']['tris']} tris -> {report['after']['tris']}, "
         f"{report['shells_after_weld']} shells, {report['shards_dropped']} shard verts dropped, "
         f"{report['after']['dims_m']} m")
    return report


def clean_all():
    """Stage 1 for both machines, into an empty scene."""
    bpy.ops.wm.read_homefile(use_empty=True)
    return {name: clean(name) for name in SCANS}


# --- stage 2: the skeleton --------------------------------------------------------------------

# Joints, in metres, measured off the cleaned scans on 2026-09-22 (see JOINTS.md notes in the
# commit). They are in the scan's own facing: the digger carries its arm along -X, the hauler
# looks along -Y. FACING turns each machine to look along +Y, which is what Unity's +Z wants
# (crew notes: a front on Blender -Y imports facing Unity -Z).
FACING = {"digger": math.radians(-90.0), "hauler": math.radians(180.0)}

JOINTS = {
    "digger": {
        # The body shell: its centre and how far out it reaches. Everything inside belongs to the
        # body whatever else is near, or posing a leg tears a bite out of the ball.
        "ball": [(0.46, 0.0, 1.01), 0.84],
        "body": [(0.46, 0.0, 1.01), (0.46, 0.0, 1.31)],
        "turret": [(0.46, 0.0, 1.80), (0.46, 0.0, 2.15)],
        "boom": [(0.20, 0.0, 2.45), (-1.15, 0.0, 3.15)],
        "stick": [(-1.15, 0.0, 3.15), (-1.48, 0.0, 1.95)],
        "bucket": [(-1.48, 0.0, 1.95), (-1.05, 0.0, 1.30)],
        "legs": {
            "fl": [(-0.02, -0.54, 0.81), (-0.11, -0.49, 0.48), (-0.12, -0.50, 0.19)],
            "fr": [(0.97, -0.52, 0.81), (1.01, -0.46, 0.43), (1.42, -0.78, 0.08)],
            "bl": [(-0.06, 0.47, 0.82), (-0.10, 0.47, 0.48), (-0.13, 0.50, 0.19)],
            "br": [(1.00, 0.47, 0.82), (1.02, 0.46, 0.43), (1.43, 0.77, 0.08)],
        },
    },
    "hauler": {
        "ball": [(0.0, -0.71, 1.15), 0.69],
        "body": [(0.0, -0.71, 1.15), (0.0, -0.71, 1.45)],
        "tray": [(0.0, 1.35, 1.55), (0.0, -0.45, 1.75)],
        "legs": {
            "fl": [(-0.63, -1.13, 0.89), (-0.61, -1.03, 0.51), (-0.97, -1.24, 0.03)],
            "fr": [(0.65, -1.11, 0.89), (0.62, -1.03, 0.51), (1.00, -1.23, 0.05)],
            "bl": [(-0.48, 0.17, 0.90), (-0.57, 0.05, 0.50), (-0.98, 0.37, 0.03)],
            "br": [(0.49, 0.11, 0.90), (0.56, 0.05, 0.51), (0.98, 0.38, 0.04)],
        },
    },
}

# How far past the ankle the foot bone points, so a foot has a direction to plant.
TOE = 0.22


def _turn(point, yaw):
    return mathutils.Matrix.Rotation(yaw, 4, 'Z') @ mathutils.Vector(point)


def face_forward(name):
    """Turns a cleaned machine so it looks along +Y, in place. Returns the yaw it used."""
    yaw = FACING[name]
    obj = bpy.data.objects[name]
    obj.data.transform(mathutils.Matrix.Rotation(yaw, 4, 'Z'))
    obj.data.update()
    return yaw


def _bone_chain(edit_bones, names, points, parent=None, connected=True):
    made = []
    for i, bone_name in enumerate(names):
        bone = edit_bones.new(bone_name)
        bone.head = points[i]
        bone.tail = points[i + 1]
        bone.parent = parent if i == 0 else made[-1]
        bone.use_connect = connected and i > 0
        made.append(bone)
        parent = parent if i == 0 else parent
    return made


def skeleton(name):
    """
    Stage 2. Turns the machine to face +Y and builds its armature: a body bone under a root, four
    legs of upper, lower and foot, and then the machine's own working parts — the digger's turret,
    boom, stick and bucket, the hauler's tray.
    """
    joints = JOINTS[name]
    yaw = face_forward(name)
    obj = bpy.data.objects[name]

    rig_name = f"{name}_rig"
    old = bpy.data.objects.get(rig_name)
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)

    armature = bpy.data.armatures.new(rig_name)
    rig = bpy.data.objects.new(rig_name, armature)
    bpy.context.scene.collection.objects.link(rig)
    # Edit mode needs a context the MCP bridge does not hand us, so it is overridden explicitly.
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    with bpy.context.temp_override(active_object=rig, object=rig, selected_objects=[rig],
                                   selected_editable_objects=[rig]):
        bpy.ops.object.mode_set(mode='EDIT')
    bones = armature.edit_bones

    root = bones.new("root")
    root.head = (0.0, 0.0, 0.0)
    root.tail = (0.0, 0.0, 0.35)

    body = bones.new("body")
    body.head = _turn(joints["body"][0], yaw)
    body.tail = _turn(joints["body"][1], yaw)
    body.parent = root

    made = ["root", "body"]
    for quad, points in joints["legs"].items():
        hip, knee, foot = (_turn(p, yaw) for p in points)
        toe = foot + mathutils.Vector((0.0, TOE, 0.0))
        parent = body
        for label, head, tail in (("upper", hip, knee), ("lower", knee, foot), ("foot", foot, toe)):
            bone = bones.new(f"leg_{quad}_{label}")
            bone.head = head
            bone.tail = tail
            bone.parent = parent
            bone.use_connect = label != "upper"
            parent = bone
            made.append(bone.name)

    if name == "digger":
        parent = body
        for label in ("turret", "boom", "stick", "bucket"):
            bone = bones.new(label)
            bone.head = _turn(joints[label][0], yaw)
            bone.tail = _turn(joints[label][1], yaw)
            bone.parent = parent
            parent = bone
            made.append(label)
    else:
        bone = bones.new("tray")
        bone.head = _turn(joints["tray"][0], yaw)
        bone.tail = _turn(joints["tray"][1], yaw)
        bone.parent = body
        made.append("tray")

    with bpy.context.temp_override(active_object=rig, object=rig, selected_objects=[rig],
                                   selected_editable_objects=[rig]):
        bpy.ops.object.mode_set(mode='OBJECT')
    rig.select_set(False)
    return {"rig": rig_name, "bones": made, "yaw_deg": round(math.degrees(yaw), 1)}


# --- stage 3: rigid skinning -------------------------------------------------------------------

# Bones a vertex may never be given: the root carries the whole machine, and nothing is weighted
# to it directly.
NEVER = {"root"}

# Share of the ball's radius the body claims outright. Just inside the shell: at 1.0 the hip
# housings and the boom's foot come with it, which locks the legs.
BALL_CAPTURE = 0.96


def _closest_bone(point, segments):
    """The bone whose line segment the point lies nearest, and how far away it is."""
    best_name = None
    best_distance = float("inf")
    for bone_name, head, tail in segments:
        span = tail - head
        length = span.length
        if length < 1e-6:
            distance = (point - head).length
        else:
            t = max(0.0, min(1.0, (point - head).dot(span) / (length * length)))
            distance = (point - (head + span * t)).length
        if distance < best_distance:
            best_distance = distance
            best_name = bone_name
    return best_name, best_distance


def skin(name):
    """
    Stage 3. Weights every vertex rigidly to one bone, deciding **per welded shell** rather than
    per vertex. A shell is one panel of the machine: it belongs to one part and turns with it.

    Deciding per vertex tore the machines apart on 2026-09-22 — the hauler's tray split into
    slabs, half of it following the tray and half staying on the body, because the nearest bone
    changes across a long panel. The body shell is claimed first by the ball's own radius, so a
    leg bone can never take a bite out of the shell.
    """
    obj = bpy.data.objects[name]
    rig = bpy.data.objects[f"{name}_rig"]
    centre, radius = JOINTS[name]["ball"]
    centre = _turn(centre, FACING[name])

    segments = [(b.name, b.head_local.copy(), b.tail_local.copy())
                for b in rig.data.bones if b.name not in NEVER and b.name != "body"]

    for group in list(obj.vertex_groups):
        obj.vertex_groups.remove(group)
    groups = {bone_name: obj.vertex_groups.new(name=bone_name) for bone_name, _, _ in segments}
    groups["body"] = obj.vertex_groups.new(name="body")

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    shells = _shells(bm)

    counts = {bone_name: 0 for bone_name in groups}
    for shell in shells:
        middle = mathutils.Vector((
            sum(v.co.x for v in shell) / len(shell),
            sum(v.co.y for v in shell) / len(shell),
            sum(v.co.z for v in shell) / len(shell),
        ))
        if (middle - centre).length <= radius * BALL_CAPTURE:
            bone_name = "body"
        else:
            bone_name, _ = _closest_bone(middle, segments)
        groups[bone_name].add([v.index for v in shell], 1.0, 'REPLACE')
        counts[bone_name] += len(shell)
    bm.free()

    if obj.parent is not rig:
        obj.parent = rig
        obj.matrix_parent_inverse = rig.matrix_world.inverted()
    if not any(m.type == 'ARMATURE' for m in obj.modifiers):
        modifier = obj.modifiers.new("Armature", 'ARMATURE')
        modifier.object = rig

    _log(f"{name}: weighted {len(shells)} shells ({len(obj.data.vertices)} vertices) to "
         f"{sum(1 for c in counts.values() if c)} bones")
    return counts


def _rim_tilt(tray):
    """
    How far the tray's rim slopes front to back, in radians. The rim is what the eye reads as
    level; fitting the floor instead gives nonsense, because the floor of a hopper is a V.
    """
    front = [v.co for v in tray if v.co.y > 0.55]
    rear = [v.co for v in tray if v.co.y < -0.85]
    if not front or not rear:
        return 0.0

    def lip(band):
        top = sorted(band, key=lambda c: -c.z)[:max(3, len(band) // 8)]
        return (sum(c.y for c in top) / len(top), sum(c.z for c in top) / len(top))

    fy, fz = lip(front)
    ry, rz = lip(rear)
    return math.atan((fz - rz) / (fy - ry)) if abs(fy - ry) > 1e-6 else 0.0


def level_tray(name="hauler"):
    """
    Rests the hauler's tray level. The scan was made with the tray tipped up, which is a fine
    picture and a poor rest pose: a machine standing about with its tray in the air looks like it
    is about to drop something. The tray's floor is measured and turned back down about its hinge,
    so rest is level and the tip clip is the thing that raises it.
    """
    obj = bpy.data.objects[name]
    group = obj.vertex_groups.get("tray")
    if group is None:
        return None
    index = group.index
    tray = [v for v in obj.data.vertices if any(g.group == index for g in v.groups)]
    if not tray:
        return None

    tilt = _rim_tilt(tray)
    hinge = bpy.data.objects[f"{name}_rig"].data.bones["tray"].head_local.copy()
    turn = (mathutils.Matrix.Translation(hinge)
            @ mathutils.Matrix.Rotation(-tilt, 4, 'X')
            @ mathutils.Matrix.Translation(-hinge))
    for vertex in tray:
        vertex.co = turn @ vertex.co
    obj.data.update()

    left = _rim_tilt(tray)
    _log(f"{name}: tray rested level, turned back {math.degrees(tilt):.1f} degrees "
         f"({math.degrees(left):.1f} left)")
    return {"tilt_deg": round(math.degrees(tilt), 1),
            "left_deg": round(math.degrees(left), 1),
            "verts": len(tray)}


def build(name, animate=True):
    """Stages 1 to 4 for one machine: clean, skeleton, skin, and the clips."""
    report = clean(name)
    report["skeleton"] = skeleton(name)
    report["weights"] = skin(name)
    if name == "hauler":
        report["tray"] = level_tray(name)
    if animate:
        report["clips"] = clips(name)
    return report


def build_all(animate=True):
    """Every stage for both machines, into an empty scene."""
    bpy.ops.wm.read_homefile(use_empty=True)
    return {name: build(name, animate=animate) for name in SCANS}


# --- stage 4: the clips ------------------------------------------------------------------------

# Frames a second the clips are authored at. Unity resamples, but whole frames keep the poses
# where they were put.
FPS = 30

# The four legs in the order they step: opposite corners together, the way a four-legged machine
# keeps two feet down at all times.
GAITS = {
    "digger": [("fl", "br"), ("fr", "bl")],
    "hauler": [("fl", "br"), ("fr", "bl")],
}

# How far a leg swings, in degrees, and how much the knee folds under it.
STRIDE = 22.0
KNEE = 30.0

# How far the body sinks and rises over a stride, in metres.
BOB = 0.05


def _fcurves(action):
    """
    An action's curves, whichever way this Blender keeps them: 4.x hangs them off the action,
    5.x puts them in a channelbag inside a layer's strip (slotted actions).
    """
    if hasattr(action, "fcurves") and len(action.fcurves):
        return list(action.fcurves)
    curves = []
    for layer in getattr(action, "layers", []):
        for strip in getattr(layer, "strips", []):
            for bag in getattr(strip, "channelbags", []):
                curves.extend(bag.fcurves)
    return curves


def _clip(rig, name, frames):
    """
    Lays one clip on the rig. `frames` is {frame: {bone: (rx, ry, rz)}} in degrees, with the bone
    name "body" also taking a (0, 0, dz) lift through a fourth number.
    """
    if rig.animation_data is None:
        rig.animation_data_create()
    action = bpy.data.actions.new(f"{rig.name}|{name}")
    action.use_fake_user = True
    rig.animation_data.action = action

    for bone in rig.pose.bones:
        bone.rotation_mode = 'XYZ'

    for frame, poses in sorted(frames.items()):
        for bone_name, values in poses.items():
            bone = rig.pose.bones.get(bone_name)
            if bone is None:
                continue
            bone.rotation_euler = [math.radians(v) for v in values[:3]]
            bone.keyframe_insert("rotation_euler", frame=frame)
            if len(values) > 3:
                bone.location = (0.0, 0.0, values[3])
                bone.keyframe_insert("location", frame=frame)

    for curve in _fcurves(action):
        for point in curve.keyframe_points:
            point.interpolation = 'BEZIER'
    return action


def _walk_frames(name, length=24, stride=STRIDE, knee=KNEE, bob=BOB, turn=0.0):
    """
    A four-leg walk: opposite corners swing together, so two feet are always down. `turn` swings
    the legs sideways instead of forward, which walks the machine round on the spot.
    """
    pairs = GAITS[name]
    frames = {}
    for step in range(5):
        frame = 1 + round(step * length / 4.0)
        phase = step / 4.0
        poses = {}
        for i, pair in enumerate(pairs):
            # One pair is half a cycle behind the other.
            angle = math.sin((phase + i * 0.5) * math.tau)
            lift = max(0.0, math.cos((phase + i * 0.5) * math.tau))
            for quad in pair:
                swing = angle * stride
                poses[f"leg_{quad}_upper"] = (swing * (1.0 - abs(turn)), 0.0, turn * swing)
                poses[f"leg_{quad}_lower"] = (-lift * knee, 0.0, 0.0)
                poses[f"leg_{quad}_foot"] = (lift * knee * 0.45, 0.0, 0.0)
        poses["body"] = (0.0, 0.0, 0.0, -bob * abs(math.sin(phase * math.tau)))
        frames[frame] = poses
    return frames


def _idle_frames(name, length=48, sink=0.02):
    """A machine at rest: it settles on its legs and breathes, so it never looks frozen."""
    frames = {}
    for step, share in ((0, 0.0), (1, 1.0), (2, 0.0)):
        frame = 1 + round(step * length / 2.0)
        poses = {"body": (0.0, 0.0, 0.0, -sink * share)}
        for pair in GAITS[name]:
            for quad in pair:
                poses[f"leg_{quad}_upper"] = (share * 2.0, 0.0, 0.0)
                poses[f"leg_{quad}_lower"] = (-share * 3.0, 0.0, 0.0)
        frames[frame] = poses
    return frames


def _scoop_frames(length=60):
    """
    The digger's cut: reach out and down, curl the bucket through the ground, lift it clear, swing
    round to the side and open it, then come back. The angles are the rig's own, in degrees.
    """
    reach = {"boom": (-28.0, 0.0, 0.0), "stick": (34.0, 0.0, 0.0), "bucket": (10.0, 0.0, 0.0)}
    cut = {"boom": (-18.0, 0.0, 0.0), "stick": (46.0, 0.0, 0.0), "bucket": (55.0, 0.0, 0.0)}
    lift = {"boom": (-40.0, 0.0, 0.0), "stick": (20.0, 0.0, 0.0), "bucket": (60.0, 0.0, 0.0)}
    swing = {"turret": (0.0, 0.0, 55.0), "boom": (-40.0, 0.0, 0.0), "stick": (20.0, 0.0, 0.0),
             "bucket": (60.0, 0.0, 0.0)}
    drop = {"turret": (0.0, 0.0, 55.0), "boom": (-34.0, 0.0, 0.0), "stick": (26.0, 0.0, 0.0),
            "bucket": (-15.0, 0.0, 0.0)}
    rest = {"turret": (0.0, 0.0, 0.0), "boom": (0.0, 0.0, 0.0), "stick": (0.0, 0.0, 0.0),
            "bucket": (0.0, 0.0, 0.0)}
    marks = [(1, rest), (10, reach), (20, cut), (30, lift), (40, swing), (48, drop), (length, rest)]
    return {frame: dict(pose) for frame, pose in marks}


def _tip_frames(length=48, angle=48.0):
    """The hauler's tip: the tray rises, holds while the load runs out, and settles back."""
    return {
        1: {"tray": (0.0, 0.0, 0.0)},
        14: {"tray": (angle, 0.0, 0.0)},
        30: {"tray": (angle, 0.0, 0.0)},
        length: {"tray": (0.0, 0.0, 0.0)},
    }


def clips(name):
    """
    Stage 4. Lays the clips Ronan asked for on one machine: walk, idle and turn for both, the
    scoop for the digger, the tip for the hauler. Each is its own action, kept by a fake user so
    it survives a save, and the FBX export writes them as Unity clips.
    """
    rig = bpy.data.objects[f"{name}_rig"]
    bpy.context.scene.render.fps = FPS

    made = {}
    made["walk"] = _clip(rig, "walk", _walk_frames(name))
    made["idle"] = _clip(rig, "idle", _idle_frames(name))
    made["turn"] = _clip(rig, "turn", _walk_frames(name, stride=STRIDE * 0.8, turn=0.8))
    if name == "digger":
        made["scoop"] = _clip(rig, "scoop", _scoop_frames())
    else:
        made["tip"] = _clip(rig, "tip", _tip_frames())

    rig.animation_data.action = None
    for bone in rig.pose.bones:
        bone.rotation_euler = (0.0, 0.0, 0.0)
        bone.location = (0.0, 0.0, 0.0)

    _log(f"{name}: {len(made)} clips ({', '.join(sorted(made))})")
    return {label: {"name": action.name, "frames": int(action.frame_range[1])}
            for label, action in made.items()}


# --- stage 5: out to Unity ---------------------------------------------------------------------

UNITY_UNITS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Units")


def _stash_clips(rig):
    """
    Puts each of this rig's actions on its own NLA track, muted, and clears the active action.

    The FBX exporter's "all actions" mode writes *every* action in the file onto *every* armature,
    so the digger came into Unity carrying the hauler's walk (2026-09-22). One strip per clip per
    rig is what actually makes one Unity clip per clip.
    """
    if rig.animation_data is None:
        rig.animation_data_create()
    data = rig.animation_data
    data.action = None
    for track in list(data.nla_tracks):
        data.nla_tracks.remove(track)

    mine = [a for a in bpy.data.actions if a.name.startswith(f"{rig.name}|")]
    for action in mine:
        track = data.nla_tracks.new()
        track.name = action.name.split("|", 1)[1]
        strip = track.strips.new(track.name, 1, action)
        strip.name = track.name
        track.mute = True
    return [a.name for a in mine]


def export(name, folder=None):
    """
    Stage 5. Writes <name>.fbx with its rig and its own clips, at true metres.

    The crew rig's notes apply: `bake_space_transform` must stay **off** (with it on the armature
    clips came out flattened in Unity) and the model must already face Blender +Y, because the FBX
    axis options are undone on the way in. The Unity importer wants `bakeAxisConversion` on.

    Scale is written with FBX_SCALE_UNITS: FBX_SCALE_NONE brought the machines into Unity a
    hundredth of their size. Textures are written beside the file rather than embedded, because an
    embedded .fbm put Unity into an endless re-import loop.
    """
    folder = folder or UNITY_UNITS
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, f"{name}.fbx")

    mesh = bpy.data.objects[name]
    rig = bpy.data.objects[f"{name}_rig"]
    clips_out = [a.name for a in bpy.data.actions if a.name.startswith(f"{rig.name}|")]
    rig.animation_data_create()
    rig.animation_data.action = None

    for obj in bpy.data.objects:
        obj.select_set(obj in (mesh, rig))
    bpy.context.view_layer.objects.active = rig

    with bpy.context.temp_override(active_object=rig, object=rig,
                                   selected_objects=[mesh, rig],
                                   selected_editable_objects=[mesh, rig]):
        bpy.ops.export_scene.fbx(
            filepath=path,
            use_selection=True,
            apply_scale_options='FBX_SCALE_UNITS',
            bake_space_transform=False,
            object_types={'ARMATURE', 'MESH'},
            use_mesh_modifiers=False,
            add_leaf_bones=False,
            primary_bone_axis='Y',
            secondary_bone_axis='X',
            # Every action in the file, which is why a machine is exported from a scene of its
            # own: "all actions" means all of them, so a shared scene gave the digger the
            # hauler's walk. Muted NLA strips, tried first, gave Unity no takes at all.
            bake_anim=True,
            bake_anim_use_all_actions=True,
            bake_anim_use_nla_strips=False,
            bake_anim_simplify_factor=0.0,
            # The maps already live in the project (Units/Textures), so the FBX points at them
            # rather than carrying copies: an embedded .fbm put Unity into an endless re-import.
            path_mode='AUTO',
            embed_textures=False,
        )

    for obj in bpy.data.objects:
        obj.select_set(False)
    size = os.path.getsize(path)
    _log(f"{name}: exported {path} ({size / 1e6:.1f} MB, clips {len(clips_out)})")
    return {"path": path, "bytes": size, "clips": clips_out}


def export_all(folder=None):
    """
    Builds each machine in a scene of its own and exports it, so its FBX carries its own clips and
    nothing else. Leaves both machines in the scene afterwards, ready to look at.
    """
    out = {}
    for name in SCANS:
        bpy.ops.wm.read_homefile(use_empty=True)
        build(name)
        out[name] = export(name, folder)
    return out


# --- looking at what came out ----------------------------------------------------------------

CREW_BLEND = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                          "Blender~", "crew_unit.blend")


def _crew_stand_in(at):
    """
    The crew robot beside the machines for scale: the real model when its blend file is there,
    otherwise a 0.285 m ball floating 0.125 m up, which is what the ruling says it is.
    """
    if os.path.exists(CREW_BLEND):
        with bpy.data.libraries.load(CREW_BLEND, link=False) as (source, target):
            wanted = [n for n in source.objects if "crew" in n.lower() or "body" in n.lower()]
            target.objects = wanted or source.objects[:1]
        brought = [o for o in target.objects if o is not None]
        for obj in brought:
            bpy.context.scene.collection.objects.link(obj)
            obj.location = (at[0], at[1], obj.location.z)
        if brought:
            return brought

    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.1425, location=(at[0], at[1], 0.125 + 0.1425))
    ball = bpy.context.object
    ball.name = "crew_scale_reference"
    for polygon in ball.data.polygons:
        polygon.use_smooth = True
    return [ball]


def check_sheet(folder, crew=True, only=None):
    """
    Renders the cleaned machines side on, face on and from above, with the crew robot for scale,
    and returns where the shots went. Evidence for Ronan, and a way to see what a re-scan did.
    """
    scene = bpy.context.scene
    os.makedirs(folder, exist_ok=True)

    wanted = [only] if only else list(SCANS)
    for name in SCANS:
        for obj in (bpy.data.objects.get(name), bpy.data.objects.get(f"{name}_rig")):
            if obj is not None:
                obj.hide_render = name not in wanted
    machines = [bpy.data.objects[n] for n in wanted if n in bpy.data.objects]
    span = 0.0
    for i, obj in enumerate(machines):
        # Once a machine is rigged its mesh hangs off the rig, so the rig is what moves: sliding
        # the mesh instead drags it out of its own skeleton.
        mover = obj.parent or obj
        mover.location.x = (i - (len(machines) - 1) * 0.5) * 4.0
        span = max(span, max(obj.dimensions))
    if crew:
        _crew_stand_in((len(machines) * 2.0, 0.0))

    if not any(o.type == 'LIGHT' for o in bpy.data.objects):
        light_data = bpy.data.lights.new("Key", type='SUN')
        light_data.energy = 4.0
        light = bpy.data.objects.new("Key", light_data)
        scene.collection.objects.link(light)
        light.rotation_euler = (math.radians(55.0), 0.0, math.radians(35.0))

    camera = bpy.data.objects.get("Camera")
    if camera is None:
        camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
        scene.collection.objects.link(camera)
    scene.camera = camera
    camera.data.type = 'ORTHO'

    if scene.world is None:
        scene.world = bpy.data.worlds.new("World")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.13, 0.14, 0.16, 1.0)
    scene.render.resolution_x = 1400
    scene.render.resolution_y = 900
    scene.render.film_transparent = False

    wide = 4.0 * len(machines) + 3.0
    centre = mathutils.Vector((0.0, 0.0, span * 0.45))
    shots = {}
    # to_track_quat's second axis is the camera's own up, not a world axis: 'Y' is what keeps the
    # shot upright. Passing 'Z' lays every standing view on its side.
    for label, offset, scale in (
        ("side", (12.0, 0.0, 0.0), wide),
        ("front", (0.0, -12.0, 0.0), wide),
        ("top", (0.0, 0.0, 12.0), wide),
    ):
        camera.data.ortho_scale = scale
        camera.location = centre + mathutils.Vector(offset)
        camera.rotation_euler = (centre - camera.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(folder, f"machines_{label}.png")
        bpy.ops.render.render(write_still=True)
        shots[label] = scene.render.filepath
        _log(f"shot {label} -> {scene.render.filepath}")
    return shots
