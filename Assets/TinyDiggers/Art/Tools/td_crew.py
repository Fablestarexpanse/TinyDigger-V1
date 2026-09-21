"""
TinyDiggers crew unit: the Trellis2 ball robot, cleaned, rigged and animated in Blender (Ronan,
2026-09-21: "make sure to animate and rig it in blender as well").

    blender -b --factory-startup -P td_crew.py -- --in <trellis.glb> --out <folder/name>
            [--height 0.6] [--hover 0.25] [--tris 6000] [--blend <file>] [--shots <folder>]

Stages:
    load      Trellis2 GLB, scaled so the robot is `height` metres tall, tidied, decimated to budget
    parts     the ball fitted as a sphere; the lens found by its teal texture (it gives the front);
              the arms are the mesh pieces reaching past the ball, one each side
    cap       the lens Trellis2 copied onto the back is pushed flush and painted shell colour
    place     front turned to +Y (which Unity reads as +Z forward), the ball `hover` metres up
    rig       Root on the ground, Body at the ball's centre, Arm.L and Arm.R at the shoulders;
              every vertex rigid to one bone
    glow      a downward disc under the belly pad, riding Body, for the hover glow
    animate   Idle, Move, Work and Carry: looping clips at 30 fps
    export    FBX with the armature and every clip, the albedo, and the .blend as the source
              (outside Assets, in Art/Blender~, or Unity imports it as a second model)
    shots     a contact sheet: four frames from each clip

There is no eye bone. Trellis2 makes one surface, with no shell behind the lens, so turning the
lens would open a hole; the robot looks about by turning its whole body, which suits a ball.
"""
import argparse
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import td_pipeline as td  # noqa: E402

FPS = 30


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--in", dest="source", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--height", type=float, default=0.6)
    p.add_argument("--hover", type=float, default=0.25)
    p.add_argument("--tris", type=int, default=6000)
    p.add_argument("--blend", default="")
    p.add_argument("--shots", default="")
    return p.parse_args(argv)


# --- parts -------------------------------------------------------------------------------------

def vertex_colours(obj):
    """The base-colour texel under each vertex (averaged over its corners), as N x 3 in 0..1."""
    image = td.base_colour_image(obj)
    count = len(obj.data.vertices)
    if image is None or not obj.data.uv_layers:
        return np.full((count, 3), 0.8)
    w, h = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(h, w, 4)
    uv = obj.data.uv_layers.active.data
    total = np.zeros((count, 3))
    seen = np.zeros(count)
    for loop in obj.data.loops:
        u, v = uv[loop.index].uv
        x = min(w - 1, max(0, int(u % 1.0 * w)))
        y = min(h - 1, max(0, int(v % 1.0 * h)))
        total[loop.vertex_index] += pixels[y, x, :3]
        seen[loop.vertex_index] += 1
    return total / np.maximum(seen, 1)[:, None]


def face_colours(obj, samples=4):
    """The base-colour texel under each face, averaged over a few points inside it, as F x 3.

    A low mesh has big faces across the lens and few vertices on it, so the lens is found by face.
    """
    image = td.base_colour_image(obj)
    mesh = obj.data
    if image is None or not mesh.uv_layers:
        return np.full((len(mesh.polygons), 3), 0.8)
    w, h = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(h, w, 4)
    uv = mesh.uv_layers.active.data
    out = np.zeros((len(mesh.polygons), 3))
    for face in mesh.polygons:
        corners = np.array([uv[i].uv for i in face.loop_indices])
        middle = corners.mean(axis=0)
        points = [middle] + [middle + (c - middle) * 0.5 for c in corners[:samples]]
        texels = [pixels[min(h - 1, max(0, int(p[1] % 1.0 * h))), min(w - 1, max(0, int(p[0] % 1.0 * w))), :3]
                  for p in points]
        out[face.index] = np.mean(texels, axis=0)
    return out


def find_parts(obj):
    co = np.array([v.co for v in obj.data.vertices])
    rgb = face_colours(obj)
    faces = obj.data.polygons
    face_centre = np.array([f.center for f in faces])
    face_area = np.array([f.area for f in faces])

    # The ball: a least-squares sphere through the points, refitted on the ones that lie close to
    # it, so the arms, the lens recess and any inner shell do not drag it.
    keep = np.ones(len(co), dtype=bool)
    for _ in range(8):
        p = co[keep]
        a = np.c_[2.0 * p, np.ones(len(p))]
        solution = np.linalg.lstsq(a, (p * p).sum(axis=1), rcond=None)[0]
        centre = solution[:3]
        radius = math.sqrt(solution[3] + centre @ centre)
        keep = np.abs(np.linalg.norm(co - centre, axis=1) - radius) < 0.12 * radius
    d = np.linalg.norm(co - centre, axis=1)
    offset = co - centre

    # The lens: teal to cyan (green and blue clearly above red), on the side, not the glow
    # beneath. Trellis2 may paint a second lens on the back it never saw, so the faces are grouped
    # by direction and only the biggest group is the lens.
    cyan = (rgb[:, 1] - rgb[:, 0] > 0.08) & (rgb[:, 2] - rgb[:, 0] > 0.05)
    cyan &= face_centre[:, 2] - centre[2] > -0.45 * radius
    heading = face_centre - centre
    heading /= np.maximum(np.linalg.norm(heading, axis=1), 1e-9)[:, None]
    lens_faces = np.zeros(len(faces), dtype=bool)
    candidates = np.flatnonzero(cyan)
    if len(candidates):
        close = heading[candidates] @ heading[candidates].T > math.cos(math.radians(40))
        best = candidates[np.argmax(close @ face_area[candidates])]
        lens_faces = cyan & (heading @ heading[best] > math.cos(math.radians(50)))
    lens_area = face_area[lens_faces].sum()
    ball_area = 4.0 * math.pi * radius * radius
    if lens_area < 0.01 * ball_area:
        raise RuntimeError(f"found no lens: {int(cyan.sum())} cyan faces, "
                           f"{lens_area / ball_area:.3%} of the ball on the side")
    front = ((face_centre[lens_faces] - centre) * face_area[lens_faces, None]).sum(axis=0)
    lens = np.zeros(len(co), dtype=bool)
    for i in np.flatnonzero(lens_faces):
        lens[list(faces[i].vertices)] = True
    front[2] = 0.0
    front /= np.linalg.norm(front)
    side = np.cross([0.0, 0.0, 1.0], front)

    # The arms: Trellis2 makes each one its own piece of mesh, reaching well past the ball on one
    # side. Taking whole pieces keeps the shoulder end with its arm, so it cannot tear off in a
    # swing. If the arms are not separate, fall back to "well past the ball's surface".
    along = offset @ side
    labels = islands(obj.data)
    arm_l = np.zeros(len(co), dtype=bool)
    arm_r = np.zeros(len(co), dtype=bool)
    for label in np.unique(labels):
        piece = labels == label
        reach = d[piece].max() / radius
        lean = along[piece].mean() / radius
        if reach > 1.25 and abs(lean) > 0.5:
            (arm_l if lean > 0 else arm_r)[piece] = True
    arm_source = "pieces"
    if not arm_l.any() or not arm_r.any():
        out = (d > radius * 1.12) & ~lens
        arm_l = out & (along > 0)
        arm_r = out & (along < 0)
        arm_source = "distance"
    return {
        "centre": centre, "radius": float(radius), "front": front, "side": side,
        "lens": lens, "arm_l": arm_l, "arm_r": arm_r, "d": d, "arm_source": arm_source,
    }


def islands(mesh):
    """A label per vertex: vertices joined by edges share one."""
    parent = np.arange(len(mesh.vertices))

    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for edge in mesh.edges:
        a, b = root(edge.vertices[0]), root(edge.vertices[1])
        if a != b:
            parent[a] = b
    return np.array([root(i) for i in range(len(parent))])


def paint_faces(image, mesh, faces, colour):
    """Fills the UV area of `faces` in `image` with `colour`, a little past the edges so no seam."""
    w, h = image.size
    pixels = np.empty(w * h * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    pixels = pixels.reshape(h, w, 4)
    uv = mesh.uv_layers.active.data
    for index in faces:
        corners = np.array([uv[i].uv for i in mesh.polygons[index].loop_indices]) * (w, h)
        for k in range(1, len(corners) - 1):  # fan into triangles
            tri = corners[[0, k, k + 1]]
            lo = np.floor(tri.min(axis=0)).astype(int) - 1
            hi = np.ceil(tri.max(axis=0)).astype(int) + 1
            xs, ys = np.meshgrid(np.arange(max(lo[0], 0), min(hi[0], w - 1) + 1),
                                 np.arange(max(lo[1], 0), min(hi[1], h - 1) + 1))
            p = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], axis=1)
            a, b, c = tri
            m = np.array([b - a, c - a]).T
            if abs(np.linalg.det(m)) < 1e-9:
                continue
            s, t = np.linalg.solve(m, (p - a).T)
            inside = (s > -0.05) & (t > -0.05) & (s + t < 1.05)
            pixels[ys.ravel()[inside], xs.ravel()[inside], :3] = colour
    image.pixels.foreach_set(pixels.ravel())
    image.update()


def cap_back_lens(obj, parts):
    """
    Trellis2 paints a copy of the lens on the back it never saw. Pushes that socket out flush
    with the ball and paints it the colour of the shell around it. Returns how many faces it
    painted (0 if there is no back lens).
    """
    mesh = obj.data
    image = td.base_colour_image(obj)
    if image is None or not mesh.uv_layers:
        return 0
    centre, radius = parts["centre"], parts["radius"]
    rgb = face_colours(obj)
    face_centre = np.array([f.center for f in mesh.polygons])
    face_area = np.array([f.area for f in mesh.polygons])
    heading = face_centre - centre
    heading /= np.maximum(np.linalg.norm(heading, axis=1), 1e-9)[:, None]
    cyan = (rgb[:, 1] - rgb[:, 0] > 0.08) & (rgb[:, 2] - rgb[:, 0] > 0.05)
    back = cyan & (heading @ parts["front"] < -0.3)
    if face_area[back].sum() < 0.005 * 4.0 * math.pi * radius * radius:
        return 0
    # Only the densest group of teal faces is the lens; stray teal flecks must not widen the cap.
    candidates = np.flatnonzero(back)
    close = heading[candidates] @ heading[candidates].T > math.cos(math.radians(30))
    seed = candidates[np.argmax(close @ face_area[candidates])]
    back &= heading @ heading[seed] > math.cos(math.radians(40))
    back_dir = (heading[back] * face_area[back, None]).sum(axis=0)
    back_dir /= np.linalg.norm(back_dir)
    angle = np.degrees(np.arccos(np.clip(heading @ back_dir, -1.0, 1.0)))
    cone = float(np.percentile(angle[back], 95)) + 6.0
    parts["back_lens_cone_deg"] = round(cone, 1)

    # The shell colour: the plain light faces in a ring just outside the cone.
    ring = (angle > cone + 5) & (angle < cone + 25) & ~cyan & (rgb.mean(axis=1) > 0.5)
    shell = np.median(rgb[ring], axis=0) if ring.any() else np.array([0.85, 0.82, 0.74])

    co = np.array([v.co for v in mesh.vertices])
    offset = co - centre
    dist = np.linalg.norm(offset, axis=1)
    towards = offset / np.maximum(dist, 1e-9)[:, None]
    vertex_angle = np.degrees(np.arccos(np.clip(towards @ back_dir, -1.0, 1.0)))
    sunk = (vertex_angle < cone) & (dist < radius) & ~parts["arm_l"] & ~parts["arm_r"]
    co[sunk] = centre + towards[sunk] * radius
    mesh.vertices.foreach_set("co", co.ravel())
    mesh.update()

    painted = np.flatnonzero(angle < cone)
    paint_faces(image, mesh, painted, shell)
    return int(len(painted))


def orient_outward(obj, parts):
    """Turn every face to face away from its part's centre. Returns how many were flipped.

    Trellis2's reduced mesh has patches wound the wrong way; with back faces drawn they show as
    dark mirror-like flecks in a lit engine. The ball and the arms are convex, so "away from the
    centre" is the right way out for nearly every face.
    """
    import bmesh
    mesh = obj.data
    if mesh.has_custom_normals:
        bpy.context.view_layer.objects.active = obj
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    co = np.array([v.co for v in mesh.vertices])
    centres = {"ball": parts["centre"]}
    for side in ("arm_l", "arm_r"):
        if parts[side].any():
            centres[side] = co[parts[side]].mean(axis=0)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    flip = []
    for face in bm.faces:
        ids = [v.index for v in face.verts]
        owner = "ball"
        for side in ("arm_l", "arm_r"):
            if side in centres and parts[side][ids].sum() * 2 > len(ids):
                owner = side
        out = np.array(face.calc_center_median()) - centres[owner]
        if np.dot(np.array(face.normal), out) < 0:
            flip.append(face)
    bmesh.ops.reverse_faces(bm, faces=flip)
    bm.to_mesh(mesh)
    bm.free()
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.update()
    return len(flip)


# --- place -------------------------------------------------------------------------------------

def place(obj, parts, hover):
    """Front to +Y, the ball's centre `hover + radius` above the origin. Returns the transform.

    +Y, not Blender's usual -Y: measured in Unity, a front on -Y imports facing -Z, and Unity
    units face +Z. The FBX axis settings cannot fix it; Unity reads them and undoes them.
    """
    front = Vector(parts["front"])
    angle = math.atan2(front.x, -front.y) + math.pi  # rotation about Z taking front onto +Y
    rotate = Matrix.Rotation(angle, 4, "Z")
    centre = Vector(parts["centre"])
    move = Matrix.Translation(Vector((0.0, 0.0, hover + parts["radius"]))) @ rotate @ Matrix.Translation(-centre)
    obj.data.transform(move)
    obj.data.update()
    return move


def arm_joint_and_tip(obj, mask, radius, centre):
    co = np.array([v.co for v in obj.data.vertices])[mask]
    d = np.linalg.norm(co - centre, axis=1)
    near = co[d < np.percentile(d, 25)]
    far = co[d > np.percentile(d, 90)]
    return Vector(near.mean(axis=0)), Vector(far.mean(axis=0))


# --- rig ---------------------------------------------------------------------------------------

def build_rig(obj, parts, hover):
    radius = parts["radius"]
    centre = np.array([0.0, 0.0, hover + radius])
    joint_l, tip_l = arm_joint_and_tip(obj, parts["arm_l"], radius, centre)
    joint_r, tip_r = arm_joint_and_tip(obj, parts["arm_r"], radius, centre)

    data = bpy.data.armatures.new("CrewRig")
    rig = bpy.data.objects.new("CrewRig", data)
    bpy.context.scene.collection.objects.link(rig)
    td.select_only(rig)
    td.run(bpy.ops.object.mode_set, mode="EDIT")
    root = data.edit_bones.new("Root")
    root.head, root.tail = (0, 0, 0), (0, 0.15, 0)
    body = data.edit_bones.new("Body")
    body.head = Vector(centre)
    body.tail = Vector(centre) + Vector((0, 0, radius))
    body.parent = root
    for name, joint, tip in (("Arm.L", joint_l, tip_l), ("Arm.R", joint_r, tip_r)):
        arm = data.edit_bones.new(name)
        arm.head, arm.tail = joint, tip
        if (tip - joint).length < 1e-3:
            arm.tail = joint + Vector((0, 0, -0.05))
        arm.parent = body
    td.run(bpy.ops.object.mode_set, mode="OBJECT")

    # Rigid weights: the robot is hard panels, nothing bends.
    groups = {name: obj.vertex_groups.new(name=name) for name in ("Body", "Arm.L", "Arm.R")}
    arm_l = np.flatnonzero(parts["arm_l"]).tolist()
    arm_r = np.flatnonzero(parts["arm_r"]).tolist()
    body_idx = np.flatnonzero(~(parts["arm_l"] | parts["arm_r"])).tolist()
    groups["Body"].add(body_idx, 1.0, "REPLACE")
    groups["Arm.L"].add(arm_l, 1.0, "REPLACE")
    groups["Arm.R"].add(arm_r, 1.0, "REPLACE")
    modifier = obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = rig
    obj.parent = rig
    return rig, {"arm_l_vertices": len(arm_l), "arm_r_vertices": len(arm_r), "body_vertices": len(body_idx)}


# --- animate -----------------------------------------------------------------------------------

def bone_rotation(rig, bone, axis, angle):
    """A rotation of `angle` about world `axis`, as the bone's local pose quaternion."""
    rest = rig.data.bones[bone].matrix_local.to_3x3()
    world = Matrix.Rotation(angle, 3, Vector(axis))
    return (rest.inverted() @ world @ rest).to_quaternion()


def bone_offset(rig, bone, offset):
    """A world offset as the bone's local pose location."""
    rest = rig.data.bones[bone].matrix_local.to_3x3()
    return rest.inverted() @ Vector(offset)


SIDE = (-1.0, 0.0, 0.0)  # front is +Y: -X is the robot's left
UP = (0.0, 0.0, 1.0)
FORWARD = (0.0, 1.0, 0.0)


def square_axis(along, toward):
    """The axis a turn of positive angle about which moves a tip at `along` toward `toward`."""
    axis = along.cross(Vector(toward))
    if axis.length < 1e-4:  # the arm already points that way: any square axis will do
        axis = along.cross(Vector(SIDE)) if abs(along.x) < 0.9 else along.cross(Vector(UP))
    return tuple(axis.normalized())


def pose_key(rig, frame, body_pitch=0.0, body_yaw=0.0, body_roll=0.0, body_lift=0.0,
             arm_swing_l=0.0, arm_swing_r=0.0, arm_out_l=0.0, arm_out_r=0.0):
    """
    Keys one frame. Pitch leans the front down, yaw turns to the robot's left, roll tips its
    left side up. An arm's swing brings it forward, `out` lifts it away from the body.
    """
    pb = rig.pose.bones
    body = (bone_rotation(rig, "Body", UP, math.radians(body_yaw))
            @ bone_rotation(rig, "Body", SIDE, math.radians(body_pitch))
            @ bone_rotation(rig, "Body", FORWARD, math.radians(body_roll)))
    pb["Body"].rotation_quaternion = body
    pb["Body"].location = bone_offset(rig, "Body", (0, 0, body_lift))
    # The arms can point any way (Trellis2 gives them sticking out sideways), so the turns are
    # about axes square to the arm: swing sweeps the tip forward, out lifts it up. A turn about a
    # fixed side axis would only twist a sideways arm about its own length.
    for name, swing, out in (("Arm.L", arm_swing_l, arm_out_l), ("Arm.R", arm_swing_r, arm_out_r)):
        bone = rig.data.bones[name]
        along = (bone.tail_local - bone.head_local).normalized()
        pb[name].rotation_quaternion = (bone_rotation(rig, name, square_axis(along, FORWARD), math.radians(swing))
                                        @ bone_rotation(rig, name, square_axis(along, UP), math.radians(out)))
    for name in ("Body", "Arm.L", "Arm.R"):
        pb[name].keyframe_insert("rotation_quaternion", frame=frame)
    pb["Body"].keyframe_insert("location", frame=frame)


def make_clip(rig, name, frames, pose, step=3):
    """One looping action: `pose(phase)` in 0..2pi gives the pose_key arguments."""
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = action
    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"
    for frame in range(0, frames + 1, step):
        pose_key(rig, frame, **pose(2.0 * math.pi * frame / frames))
    action.frame_range = (0, frames)
    for fcurve in getattr(action, "fcurves", []) or []:
        for key in fcurve.keyframe_points:
            key.interpolation = "BEZIER"
    return action


CLIPS = {
    # A slow bob and sway; once a loop it glances left and back.
    "Idle": (60, lambda p: dict(body_lift=0.03 * math.sin(p), body_pitch=3 * math.sin(p + 1.0),
                                body_roll=2 * math.sin(p * 0.5) ** 2,
                                body_yaw=14 * math.sin(p) * max(0.0, math.sin(p)),
                                arm_swing_l=8 * math.sin(p + 0.6), arm_swing_r=8 * math.sin(p + 0.2),
                                arm_out_l=6 + 4 * math.sin(p), arm_out_r=6 + 4 * math.sin(p + 0.4))),
    # Leaning into it, arms swept back, a quick bob.
    "Move": (30, lambda p: dict(body_lift=0.015 * math.sin(2 * p), body_pitch=12 + 2 * math.sin(2 * p),
                                body_roll=3 * math.sin(p),
                                arm_swing_l=-30 + 5 * math.sin(p), arm_swing_r=-30 - 5 * math.sin(p),
                                arm_out_l=10, arm_out_r=10)),
    # A two-arm scoop: dip, sweep both arms down and forward, come back up.
    "Work": (36, lambda p: (lambda s: dict(body_lift=-0.05 * s, body_pitch=14 * s,
                                           arm_swing_l=-10 + 80 * s, arm_swing_r=-10 + 80 * s,
                                           arm_out_l=4, arm_out_r=4))((1 - math.cos(p)) / 2)),
    # Arms held out in front, leaning back against the load, a heavier bob.
    "Carry": (30, lambda p: dict(body_lift=0.02 * math.sin(p), body_pitch=-5,
                                 arm_swing_l=50 + 3 * math.sin(p), arm_swing_r=50 + 3 * math.sin(p),
                                 arm_out_l=-4, arm_out_r=-4)),
}


def animate(rig):
    made = []
    for name, (frames, pose) in CLIPS.items():
        make_clip(rig, name, frames, pose)
        made.append(name)
    rig.animation_data.action = bpy.data.actions["Idle"]
    bpy.context.scene.render.fps = FPS
    return made


# --- export ------------------------------------------------------------------------------------

def add_glow(obj, rig, parts, segments=24):
    """
    The warm hover glow under the belly (the concept's): a flat disc facing down, riding the
    Body bone, just under the lowest point of the mesh near the axis. That point is Trellis2's
    dark hover pad, which hangs a little below the sphere. UVs run 0..1 across the disc, so the
    engine can put a soft radial falloff on it. It is its own object, so it gets its own
    glowing material.
    """
    radius = parts["radius"] * 0.42
    co = np.array([v.co for v in obj.data.vertices])
    near_axis = np.hypot(co[:, 0], co[:, 1]) < radius
    z = float(co[near_axis, 2].min()) - 0.003
    verts = [(0.0, 0.0, z)] + [(radius * math.cos(2 * math.pi * i / segments),
                                 radius * math.sin(2 * math.pi * i / segments), z) for i in range(segments)]
    faces = [(0, 1 + (i + 1) % segments, 1 + i) for i in range(segments)]  # wound to face down
    mesh = bpy.data.meshes.new("CrewGlow")
    mesh.from_pydata(verts, [], faces)
    layer = mesh.uv_layers.new(name="UVMap")
    for loop in mesh.loops:
        x, y, _ = verts[loop.vertex_index]
        layer.data[loop.index].uv = (0.5 + x / (2 * radius), 0.5 + y / (2 * radius))
    mesh.update()
    glow = bpy.data.objects.new("CrewGlow", mesh)
    bpy.context.scene.collection.objects.link(glow)
    material = bpy.data.materials.new("CrewGlow")
    material.use_nodes = True
    principled = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    principled.inputs["Base Color"].default_value = (1.0, 0.85, 0.5, 1.0)
    principled.inputs["Emission Color"].default_value = (1.0, 0.85, 0.5, 1.0)
    principled.inputs["Emission Strength"].default_value = 3.0
    mesh.materials.append(material)
    glow.vertex_groups.new(name="Body").add(list(range(len(verts))), 1.0, "REPLACE")
    glow.modifiers.new("Armature", "ARMATURE").object = rig
    glow.parent = rig
    return glow


def finish(obj, rig, out, blend, glow=None):
    name = os.path.basename(out)
    obj.name = name
    os.makedirs(os.path.dirname(out), exist_ok=True)
    image = td.base_colour_image(obj)
    if image is not None:
        image.filepath_raw = out + "_albedo.png"
        image.file_format = "PNG"
        image.save()
    for material in obj.data.materials:
        principled = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if principled:
            principled.inputs["Roughness"].default_value = 0.8
            principled.inputs["Metallic"].default_value = 0.0
    for polygon in obj.data.polygons:
        polygon.use_smooth = True

    td.select_only(rig, obj, *([glow] if glow else []))
    td.run(bpy.ops.export_scene.fbx, filepath=out + ".fbx", use_selection=True,
           object_types={"ARMATURE", "MESH"}, apply_scale_options="FBX_SCALE_UNITS",
           # No bake_space_transform: Blender marks it broken with armatures and it flattened the
           # clips. Unity's importer bakes the axis conversion instead (bakeAxisConversion).
           axis_forward="-Z", axis_up="Y", bake_space_transform=False, path_mode="STRIP",
           mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=True,
           bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
           bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0)
    os.makedirs(os.path.dirname(blend), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=blend)


# --- shots -------------------------------------------------------------------------------------

def shots(rig, folder):
    """Four frames from each clip in a row, clips top to bottom, one contact sheet."""
    os.makedirs(folder, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = scene.render.resolution_y = 384
    scene.render.film_transparent = False
    camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    scene.collection.objects.link(camera)
    scene.camera = camera
    target = Vector((0, 0, 0.45))
    camera.location = target + Vector((-1.1, 1.5, 0.75))
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.lens = 50

    tiles = []
    for name, (frames, _) in CLIPS.items():
        rig.animation_data.action = bpy.data.actions[name]
        row = []
        for k in range(4):
            scene.frame_set(int(frames * k / 4))
            path = os.path.join(folder, f"{name}_{k}.png")
            scene.render.filepath = path
            bpy.ops.render.render(write_still=True)
            img = bpy.data.images.load(path)
            px = np.array(img.pixels[:], dtype=np.float32).reshape(img.size[1], img.size[0], 4)
            row.append(px)
            bpy.data.images.remove(img)
        tiles.append(np.concatenate(row, axis=1))
    sheet = np.concatenate(tiles[::-1], axis=0)  # images are bottom-up: first clip on top
    h, w = sheet.shape[:2]
    out = bpy.data.images.new("crew_sheet", w, h, alpha=True)
    out.pixels = sheet.ravel()
    out.filepath_raw = os.path.join(folder, "crew_contact_sheet.png")
    out.file_format = "PNG"
    out.save()
    return out.filepath_raw


def main():
    a = parse()
    # Absolute paths: once the .blend is saved, Blender reads relative ones against its folder.
    a.out = os.path.abspath(a.out)
    a.shots = os.path.abspath(a.shots) if a.shots else None
    a.blend = os.path.abspath(a.blend) if a.blend else a.out + ".blend"
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0
    report = {}
    obj = td.load_raw(a.source, a.height)
    report["raw"] = td.inspect(obj)
    report["tidy"] = td.tidy(obj)
    td.decimate(obj, a.tris)
    report["decimated"] = td.inspect(obj)
    parts = find_parts(obj)
    report["parts"] = {"radius_m": round(parts["radius"], 3), "lens_vertices": int(parts["lens"].sum()),
                       "arm_l_vertices": int(parts["arm_l"].sum()), "arm_r_vertices": int(parts["arm_r"].sum())}
    report["arm_source"] = parts["arm_source"]
    report["back_lens_faces_painted"] = cap_back_lens(obj, parts)
    report["back_lens_cone_deg"] = parts.get("back_lens_cone_deg")
    report["faces_flipped"] = orient_outward(obj, parts)
    place(obj, parts, a.hover)
    co = np.array([v.co for v in obj.data.vertices])
    report["lens_offset"] = [round(float(x), 2) for x in (co[parts["lens"]].mean(axis=0) - co.mean(axis=0))]
    report["arm_l_offset"] = [round(float(x), 2) for x in (co[parts["arm_l"]].mean(axis=0) - co.mean(axis=0))]
    rig, weights = build_rig(obj, parts, a.hover)
    report["weights"] = weights
    glow = add_glow(obj, rig, parts)
    report["clips"] = animate(rig)
    finish(obj, rig, a.out, a.blend, glow)
    if a.shots:
        report["sheet"] = shots(rig, a.shots)
    print("TD_CREW " + json.dumps(report))


if __name__ == "__main__":
    main()
