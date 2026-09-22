"""Finishes the dumper: the four-legged tipper Ronan brought already rigged.

Ronan, 2026-09-22: "lets remove them and do one at a time the animations are poor i have a dumper
thats mostly rigged already lets check it and i also want to see a video clip of it moving".

What the scan is: 15 456 triangles, no UVs, no materials, and a UniRig auto-rig of 37 bones with
generic names — a tray bone that is also the root, a body, four legs of three segments, one leg
driven by two identical chains, and a dozen one-centimetre stubs that hold a few hundred vertices
each.

What this does, in stages:

    stage 1  read     work out what each bone actually drives, from its weights
    stage 2  name     rename to body/tray/leg_<corner>_<segment>, reparent so the body is the
                      root and the tray hangs off it, and fold the stubs and the duplicate
                      chain into the bone they sit on
    stage 3  clips    walk, idle, turn and tip, with the **feet planted**: a foot on the ground
                      stays on the ground while the body travels over it, which is what was wrong
                      with the first pair of machines
    stage 4  video    an MP4 of it moving, to look at before anything goes near Unity

The legs are solved analytically rather than with IK constraints and a bake, so the whole thing
re-runs headlessly and gives the same result every time.
"""
import math
import os

import bpy
import mathutils

SCAN = os.path.join(os.path.expanduser("~"), "Downloads", "Meshy_AI_Character_output.glb")

# Ronan's scale ruling for the machines: 1.8 m to the top of the shell, the crew robot being
# 0.43 m. The shell here is the body ball, not the tray standing over it.
SHELL_TOP = 1.8

# A bone shorter than this is a stub the auto-rigger left behind; its weights go to its parent.
STUB = 0.05

FPS = 30


def _log(message):
    print(f"[td_dumper] {message}")


def _window_override():
    """
    The bits of context Blender's importers and mode switches want, taken from the running
    window. Over the MCP bridge there is no window in context and the glTF importer dies on
    `bpy.context.window.scene`.
    """
    wm = bpy.data.window_managers[0]
    window = wm.windows[0] if len(wm.windows) else None
    if window is None:
        return {}
    override = {"window": window, "screen": window.screen,
                "scene": bpy.context.scene, "view_layer": bpy.context.view_layer}
    area = next((a for a in window.screen.areas if a.type == 'VIEW_3D'), None)
    if area is not None:
        override["area"] = area
        region = next((r for r in area.regions if r.type == 'WINDOW'), None)
        if region is not None:
            override["region"] = region
    return override


def load(path=SCAN):
    """Stage 1. Brings the scan in on its own, and returns the mesh and the rig."""
    bpy.ops.wm.read_homefile(use_empty=True)
    with bpy.context.temp_override(**_window_override()):
        bpy.ops.import_scene.gltf(filepath=path)

    mesh = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name != "Icosphere")
    rig = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    for spare in [o for o in bpy.data.objects if o not in (mesh, rig)]:
        bpy.data.objects.remove(spare, do_unlink=True)

    mesh.name = "dumper"
    mesh.data.name = "dumper_mesh"
    rig.name = "dumper_rig"
    rig.data.name = "dumper_rig"
    _log(f"loaded {len(mesh.data.vertices)} vertices, "
         f"{sum(len(p.vertices) - 2 for p in mesh.data.polygons)} triangles, "
         f"{len(rig.data.bones)} bones")
    return mesh, rig


def weighted_centres(mesh, floor=0.4):
    """Where each bone's weight actually sits: {bone: (centre, vertex count, lowest z)}."""
    names = {group.index: group.name for group in mesh.vertex_groups}
    totals = {}
    for vertex in mesh.data.vertices:
        for item in vertex.groups:
            if item.weight < floor:
                continue
            name = names[item.group]
            entry = totals.setdefault(name, [mathutils.Vector(), 0, 1e9])
            entry[0] += vertex.co
            entry[1] += 1
            entry[2] = min(entry[2], vertex.co.z)
    return {name: (total / count, count, low) for name, (total, count, low) in totals.items() if count}


# The scan is built looking down -Y. Everything here — the gait, the corner names, and Unity's
# +Z — wants a machine that looks along +Y, so it is turned once on the way in. Ronan spotted it
# from the first walk video: "the walk looks backwards".
FACING = 180.0


def face_forward(mesh, rig, yaw=FACING):
    """Turns mesh and rest skeleton together so the machine looks along +Y."""
    turn = mathutils.Matrix.Rotation(math.radians(yaw), 4, 'Z')
    mesh.data.transform(turn)
    mesh.data.update()

    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        for bone in rig.data.edit_bones:
            bone.head = turn @ bone.head
            bone.tail = turn @ bone.tail
        bpy.ops.object.mode_set(mode='OBJECT')

    _log(f"turned {yaw:.0f} degrees: the machine looks along +Y")
    return yaw


# --- stage 2: make sense of the rig ------------------------------------------------------------

def _chain_from(bone):
    """A bone and its descendants, following the longest child each time."""
    chain = [bone]
    current = bone
    while current.children:
        current = max(current.children, key=lambda b: b.length)
        chain.append(current)
    return chain


def _fold(mesh, source, target):
    """Adds the source vertex group's weights into the target's, then drops the source."""
    if source == target:
        return 0
    group = mesh.vertex_groups.get(source)
    into = mesh.vertex_groups.get(target) or mesh.vertex_groups.new(name=target)
    if group is None:
        return 0

    moved = 0
    for vertex in mesh.data.vertices:
        weight = next((g.weight for g in vertex.groups if g.group == group.index), 0.0)
        if weight <= 0.0:
            continue
        held = next((g.weight for g in vertex.groups if g.group == into.index), 0.0)
        into.add([vertex.index], min(1.0, held + weight), 'REPLACE')
        moved += 1

    mesh.vertex_groups.remove(group)
    return moved


def tidy(mesh, rig):
    """
    Stage 2. Gives the auto-rig names and a shape that keeps every joint the machine actually has.

    - The body becomes the root and the tray hangs off it (the auto-rigger made the tray the root
      and hung the whole machine under it).
    - Each corner's longest chain is the leg. A second chain in the same corner is the claw the
      rigger left dangling off the body: it is re-parented onto the end of the leg, so the claw
      hinges instead of floating.
    - The one-centimetre stubs are the hinge knuckles — two thousand vertices apiece on the back
      legs — and they are folded into the **nearest leg bone**, not into their parent. Folding
      them into the parent left the knuckle welded to the shell, which is the missing hinge Ronan
      spotted on every leg.
    """
    centres = weighted_centres(mesh)

    body = max(rig.data.bones, key=lambda b: len(b.children))
    tray = body.parent or next((b for b in rig.data.bones if b.parent is None and b != body), None)

    starts = [b for b in body.children if b.length > STUB and b != tray]
    chains = [_chain_from(b) for b in starts]

    # Sort into corners, by where the chain's far end sits.
    corners = {}
    for chain in chains:
        tip = centres.get(chain[-1].name, (chain[-1].tail_local, 0, 0))[0]
        corner = ("front" if tip.y > 0 else "back") + ("_left" if tip.x < 0 else "_right")
        corners.setdefault(corner, []).append(chain)

    legs = {}
    folded = 0
    extras = []
    for corner, found in corners.items():
        found.sort(key=lambda c: (-len(c), -sum(b.length for b in c)))
        leg = found[0]
        for spare in found[1:]:
            # Two chains from the same hip are the same leg cut up differently: keep the one with
            # more joints and give the other's weights to the matching segment.
            if (spare[0].head_local - leg[0].head_local).length < 1e-3:
                for i, bone in enumerate(spare):
                    folded += _fold(mesh, bone.name, leg[min(i, len(leg) - 1)].name)
            else:
                # A claw hanging off the body at the far end of this leg: it belongs on the end.
                extras.append((corner, spare))
        legs[corner] = leg

    renames = {body.name: "body"}
    if tray is not None:
        renames[tray.name] = "tray"

    labels = ["upper", "lower", "foot", "toe", "claw", "tip"]
    reparent = {}
    for corner, chain in legs.items():
        full = list(chain)
        for other_corner, spare in extras:
            if other_corner == corner:
                reparent[spare[0].name] = chain[-1].name
                full.extend(spare)
        for i, bone in enumerate(full):
            renames[bone.name] = f"leg_{corner}_{labels[min(i, len(labels) - 1)]}"

    # The knuckles: fold each stub into whichever named bone it sits closest to.
    anchors = [(name, rig.data.bones[old].head_local.copy())
               for old, name in renames.items() if old in rig.data.bones]
    stubs = [b for b in rig.data.bones if b.name not in renames]
    for bone in stubs:
        nearest = min(anchors, key=lambda a: (a[1] - bone.head_local).length)
        folded += _fold(mesh, bone.name, nearest[0])

    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        bones = rig.data.edit_bones

        for old, new_parent in reparent.items():
            if old in bones and new_parent in bones:
                bones[old].parent = bones[new_parent]

        for old, name in renames.items():
            if old in bones:
                bones[old].name = name

        for bone in [b for b in list(bones) if b.name not in renames.values()]:
            bones.remove(bone)

        if "tray" in bones and "body" in bones:
            bones["body"].parent = None
            bones["tray"].parent = bones["body"]

        bpy.ops.object.mode_set(mode='OBJECT')

    for group in [g for g in mesh.vertex_groups if g.name not in {b.name for b in rig.data.bones}]:
        _fold(mesh, group.name, "body")

    segments = {corner: sum(1 for b in rig.data.bones if b.name.startswith(f"leg_{corner}_"))
                for corner in legs}
    _log(f"named {len(renames)} bones, folded {len(stubs)} knuckles into the leg they sit on "
         f"({folded} weights moved); segments per leg {segments}")
    return {"bones": [b.name for b in rig.data.bones], "segments": segments}


def harden(mesh):
    """
    Gives every vertex to one bone: whichever holds most of it already.

    The auto-rigger blends weights the way it would for flesh, so the tray came up at half the
    angle its bone turned and the legs bent like rubber. These are machines — a panel belongs to
    one part and turns with it.
    """
    changed = 0
    for vertex in mesh.data.vertices:
        held = [(g.weight, g.group) for g in vertex.groups if g.weight > 0.0]
        if not held:
            continue
        best = max(held)[1]
        for weight, group in held:
            if group != best:
                mesh.vertex_groups[group].remove([vertex.index])
                changed += 1
        mesh.vertex_groups[best].add([vertex.index], 1.0, 'REPLACE')

    mesh.data.update()
    _log(f"hardened weights: {changed} shared weights dropped, one bone a vertex")
    return changed


def hinge_tray(mesh, rig):
    """
    Puts the tray's hinge where a dump truck has it: the **rear bottom edge** of the tray, with
    the bone pointing forward, so tipping lifts the front and the load runs off over the tail.

    The auto-rigger hung the tray off a bone at its front, which tipped the wrong end down and
    swung the tray through the shell (Ronan, 2026-09-22: "the dump goes the other direction it
    should tilt backwards the eyes are the front").
    """
    group = mesh.vertex_groups.get("tray")
    if group is None or "tray" not in rig.data.bones:
        return None

    tray = [v.co for v in mesh.data.vertices
            if any(g.group == group.index and g.weight > 0.5 for g in v.groups)]
    if not tray:
        return None

    rear = min(c.y for c in tray)
    front = max(c.y for c in tray)
    floor = min(c.z for c in tray if c.y < rear + 0.15)
    middle = sum(c.x for c in tray) / len(tray)

    head = mathutils.Vector((middle, rear, floor))
    tail = mathutils.Vector((middle, front, floor + 0.05))

    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        bone = rig.data.edit_bones["tray"]
        bone.head = head
        bone.tail = tail
        bone.roll = 0.0
        bpy.ops.object.mode_set(mode='OBJECT')

    _log(f"tray hinged at its rear edge {[round(v, 2) for v in head]}, pointing forward")
    return {"head": [round(v, 3) for v in head], "tail": [round(v, 3) for v in tail]}


# --- stage 3: clips with the feet planted -------------------------------------------------------

CORNERS = ("front_left", "front_right", "back_left", "back_right")

# A crab mech walks its diagonals: one pair holds the ground while the other swings.
PAIRS = (("front_left", "back_right"), ("front_right", "back_left"))

# How far a foot travels over one stride, how high it lifts, how far the shell sinks and sways.
STRIDE = 0.42
LIFT = 0.16
BOB = 0.045
SWAY = 0.05

# How far a foot swings round for the turn-on-the-spot, in degrees.
TURN = 11.0

# The tray tips like a dump truck: hinged at the back, the front lifts and the load runs off.
TIP = 52.0


def _rest(rig):
    """
    Everything the leg solver needs, per corner: the whole chain of segments, the hip and knee for
    the two-bone solve, and the contact point at the far end of the last segment.

    Every joint the machine has is driven. Leaving the ankle and the claw out left them welded,
    which is what Ronan meant by "the legs are missing joints".
    """
    legs = {}
    for corner in CORNERS:
        chain = []
        for label in ("upper", "lower", "foot", "toe", "claw", "tip"):
            bone = rig.data.bones.get(f"leg_{corner}_{label}")
            if bone is not None:
                chain.append(bone)
        if len(chain) < 2:
            continue

        upper, lower = chain[0], chain[1]
        hip = upper.head_local.copy()
        knee = lower.head_local.copy()
        ankle = lower.tail_local.copy()
        contact = chain[-1].tail_local.copy()

        straight = (ankle - hip).normalized()
        out = (knee - hip) - straight * (knee - hip).dot(straight)
        legs[corner] = {
            "names": [b.name for b in chain],
            "rests": [b.matrix_local.copy() for b in chain],
            "heads": [b.head_local.copy() for b in chain],
            "hip": hip,
            "knee": knee,
            "ankle": ankle,
            "contact": contact,
            "l1": (knee - hip).length,
            "l2": (ankle - knee).length,
            "bend": out.normalized() if out.length > 1e-5 else mathutils.Vector((0.0, 0.0, 1.0)),
        }
    return legs


def _aim(pose_bone, rest_matrix, head, direction):
    """Puts a bone's head at `head` and points it down `direction`, keeping its rest twist."""
    rest_direction = (rest_matrix.to_3x3() @ mathutils.Vector((0.0, 1.0, 0.0))).normalized()
    turn = rest_direction.rotation_difference(direction.normalized()).to_matrix().to_4x4()
    matrix = turn @ rest_matrix
    matrix.translation = head
    pose_bone.matrix = matrix


def _solve_leg(rig, leg, contact_target, body_offset):
    """
    Puts the far end of the leg on `contact_target`.

    The hip and knee are solved as a two-bone chain onto the ankle, with the knee breaking the way
    it breaks at rest so it keeps splaying outwards like a crab's. Everything past the ankle — the
    foot, the toe, the claw — is then carried along and aimed at the contact, so each of those
    joints bends as the machine rolls over its own foot.
    """
    names, rests, heads = leg["names"], leg["rests"], leg["heads"]

    # Where the ankle has to be for the far end to land on the target.
    ankle_target = contact_target + (leg["ankle"] - leg["contact"])
    hip = leg["hip"] + body_offset
    span = ankle_target - hip
    distance = min(max(span.length, abs(leg["l1"] - leg["l2"]) + 1e-3), leg["l1"] + leg["l2"] - 1e-3)
    along = span.normalized()

    reach = (distance * distance + leg["l1"] * leg["l1"] - leg["l2"] * leg["l2"]) / (2.0 * distance)
    height = math.sqrt(max(0.0, leg["l1"] * leg["l1"] - reach * reach))
    sideways = leg["bend"] - along * leg["bend"].dot(along)
    if sideways.length < 1e-5:
        sideways = mathutils.Vector((0.0, 0.0, 1.0)) - along * along.z
    knee = hip + along * reach + sideways.normalized() * height

    _aim(rig.pose.bones[names[0]], rests[0], hip, knee - hip)
    bpy.context.view_layer.update()
    _aim(rig.pose.bones[names[1]], rests[1], knee, ankle_target - knee)
    bpy.context.view_layer.update()

    # Past the ankle: each segment starts where the last one ended and points at what is left of
    # the way to the contact.
    here = ankle_target
    for index in range(2, len(names)):
        rest_length = (heads[index + 1] - heads[index]).length if index + 1 < len(heads) else             (leg["contact"] - heads[index]).length
        remaining = contact_target - here
        if remaining.length < 1e-5:
            break
        _aim(rig.pose.bones[names[index]], rests[index], here, remaining)
        bpy.context.view_layer.update()
        here = here + remaining.normalized() * rest_length


def _foot_offset(phase, stride, lift, turn=0.0, anchor=None):
    """
    Where a foot is within its cycle. The first half is stance — the foot holds the ground and
    slides back under the machine, which is what makes it look like it is walking rather than
    paddling. The second half is the swing, lifted and carried forward.
    """
    if turn:
        angle = math.radians(turn) * (0.5 - phase * 2.0 if phase < 0.5 else (phase - 0.5) * 2.0 - 0.5)
        turned = mathutils.Matrix.Rotation(angle, 4, 'Z') @ anchor
        offset = turned - anchor
    else:
        travel = stride * (0.5 - phase * 2.0) if phase < 0.5 else stride * ((phase - 0.5) * 4.0 - 1.0) * 0.5
        offset = mathutils.Vector((0.0, travel, 0.0))

    if phase >= 0.5:
        offset.z += lift * math.sin((phase - 0.5) * 2.0 * math.pi * 0.5)
    return offset


def _new_action(rig, name):
    if rig.animation_data is None:
        rig.animation_data_create()
    action = bpy.data.actions.new(f"dumper|{name}")
    action.use_fake_user = True
    rig.animation_data.action = action
    if getattr(rig.animation_data, "action_slot", None) is None and action.slots:
        rig.animation_data.action_slot = action.slots[0]
    return action


def _key(rig, legs, frame, keyed_tray=False):
    body = rig.pose.bones["body"]
    body.keyframe_insert("location", frame=frame)
    body.keyframe_insert("rotation_quaternion", frame=frame)
    for leg in legs.values():
        for name in leg["names"]:
            bone = rig.pose.bones[name]
            bone.keyframe_insert("location", frame=frame)
            bone.keyframe_insert("rotation_quaternion", frame=frame)
    if keyed_tray and "tray" in rig.pose.bones:
        rig.pose.bones["tray"].keyframe_insert("rotation_quaternion", frame=frame)


def _rest_pose(rig):
    for bone in rig.pose.bones:
        bone.rotation_mode = 'QUATERNION'
        bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        bone.location = (0.0, 0.0, 0.0)
    bpy.context.view_layer.update()


def walk(rig, legs, name="walk", length=32, stride=STRIDE, lift=LIFT, turn=0.0):
    """
    A crab's walk: the diagonals take turns, two feet always on the ground, the shell sinking and
    swaying towards whichever pair is carrying it. Nothing travels — the feet slide back under the
    machine — so the game can drive it at whatever speed it likes.
    """
    action = _new_action(rig, name)
    body = rig.pose.bones["body"]

    for step in range(length + 1):
        frame = 1 + step
        phase0 = (step % length) / length
        _rest_pose(rig)

        sway = math.sin(phase0 * math.tau) * SWAY
        sink = -abs(math.sin(phase0 * math.tau)) * BOB
        offset = mathutils.Vector((sway, 0.0, sink))
        body.location = (rig.pose.bones["body"].bone.matrix_local.to_3x3().inverted()
                         @ offset)
        bpy.context.view_layer.update()

        for index, pair in enumerate(PAIRS):
            phase = (phase0 + index * 0.5) % 1.0
            for corner in pair:
                leg = legs.get(corner)
                if leg is None:
                    continue
                anchor = leg["contact"]
                step_offset = _foot_offset(phase, stride, lift, turn=turn, anchor=anchor)
                _solve_leg(rig, leg, anchor + step_offset, offset)

        _key(rig, legs, frame)

    _log(f"{name}: {length} frames")
    return action


def idle(rig, legs, name="idle", length=60):
    """At rest the machine settles on its legs and breathes, so it never looks switched off."""
    action = _new_action(rig, name)
    body = rig.pose.bones["body"]

    for step in range(length + 1):
        frame = 1 + step
        phase = (step % length) / length
        _rest_pose(rig)

        settle = -0.5 * BOB * (1.0 - math.cos(phase * math.tau))
        offset = mathutils.Vector((0.0, 0.0, settle))
        body.location = body.bone.matrix_local.to_3x3().inverted() @ offset
        bpy.context.view_layer.update()

        for leg in legs.values():
            _solve_leg(rig, leg, leg["contact"], offset)

        _key(rig, legs, frame)

    _log(f"{name}: {length} frames")
    return action


def _tray_front(mesh, rig):
    """The tray's own vertices, and which of them sit at its front (the eyes' end)."""
    group = mesh.vertex_groups.get("tray")
    if group is None:
        return []
    tray = [v for v in mesh.data.vertices
            if any(g.group == group.index and g.weight > 0.5 for g in v.groups)]
    if not tray:
        return []
    hinge = rig.data.bones["tray"].head_local
    return [v for v in tray if v.co.y > hinge.y + 0.2]


def _tip_sign(mesh, rig):
    """
    Which way round the tray's rotation has to go so its **front** lifts and the load runs off
    over the tail — a dump truck, Ronan's words, not a skip turning over. Worked out by trying it
    and watching the front of the tray, because the bone's own axes are whatever the auto-rigger
    left behind.
    """
    front = _tray_front(mesh, rig)
    if not front:
        return 1.0

    tray = rig.pose.bones["tray"]
    tray.rotation_mode = 'QUATERNION'
    heights = {}
    for sign in (1.0, -1.0):
        tray.rotation_quaternion = mathutils.Quaternion((1.0, 0.0, 0.0), math.radians(20.0 * sign))
        bpy.context.view_layer.update()
        graph = bpy.context.evaluated_depsgraph_get()
        posed = mesh.evaluated_get(graph).to_mesh()
        heights[sign] = sum(posed.vertices[v.index].co.z for v in front) / len(front)
        mesh.evaluated_get(graph).to_mesh_clear()

    tray.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
    bpy.context.view_layer.update()
    return 1.0 if heights[1.0] >= heights[-1.0] else -1.0


def tip(rig, legs, name="tip", length=54, angle=TIP, mesh=None):
    """
    The dump: the tray hinges at the back and its front lifts, the load runs off over the tail,
    then it comes back down — a dump truck, as Ronan put it, not a hopper turning over.
    """
    action = _new_action(rig, name)
    tray = rig.pose.bones["tray"]
    _rest_pose(rig)
    for leg in legs.values():
        _solve_leg(rig, leg, leg["contact"], mathutils.Vector())

    sign = _tip_sign(mesh, rig) if mesh is not None else 1.0
    marks = ((1, 0.0), (16, angle * sign), (34, angle * sign), (length, 0.0))
    for frame, lift in marks:
        tray.rotation_quaternion = mathutils.Quaternion((1.0, 0.0, 0.0), math.radians(lift))
        bpy.context.view_layer.update()
        _key(rig, legs, frame, keyed_tray=True)

    _log(f"{name}: tray front lifts {angle:.0f} degrees (sign {sign:+.0f}) over {length} frames")
    return action


def clips(rig, legs, mesh=None):
    """Stage 3. Every clip the dumper needs, each its own action."""
    bpy.context.scene.render.fps = FPS
    made = {
        "walk": walk(rig, legs),
        "idle": idle(rig, legs),
        "turn": walk(rig, legs, name="turn", stride=0.0, lift=LIFT * 0.8, turn=TURN),
        "tip": tip(rig, legs, mesh=mesh),
    }
    rig.animation_data.action = None
    _rest_pose(rig)
    return {name: {"action": action.name, "frames": int(action.frame_range[1])}
            for name, action in made.items()}


# --- stage 4: a video to look at ----------------------------------------------------------------

def stage(mesh, distance=5.0, height=2.0):
    """A floor, a key light and a camera that frames the machine. Returns the camera."""
    scene = bpy.context.scene
    if scene.world is None:
        scene.world = bpy.data.worlds.new("World")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.09, 0.10, 0.12, 1.0)

    if bpy.data.objects.get("floor") is None:
        floor_mesh = bpy.data.meshes.new("floor")
        size = 12.0
        floor_mesh.from_pydata([(-size, -size, 0.0), (size, -size, 0.0), (size, size, 0.0), (-size, size, 0.0)],
                               [], [(0, 1, 2, 3)])
        floor_mesh.update()
        floor = bpy.data.objects.new("floor", floor_mesh)
        scene.collection.objects.link(floor)
        material = bpy.data.materials.new("floor")
        material.use_nodes = True
        material.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.20, 0.21, 0.23, 1.0)
        material.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.95
        floor.data.materials.append(material)

    if bpy.data.objects.get("Key") is None:
        data = bpy.data.lights.new("Key", type='SUN')
        data.energy = 4.5
        data.angle = math.radians(3.0)
        light = bpy.data.objects.new("Key", data)
        scene.collection.objects.link(light)
        light.rotation_euler = (math.radians(52.0), 0.0, math.radians(38.0))

    camera = bpy.data.objects.get("Camera")
    if camera is None:
        camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
        scene.collection.objects.link(camera)
    scene.camera = camera
    camera.data.type = 'PERSP'
    camera.data.lens = 50.0
    target = mathutils.Vector((0.0, 0.0, mesh.dimensions.z * 0.45))
    camera.location = target + mathutils.Vector((distance * 0.75, -distance, height))
    camera.rotation_euler = (target - camera.location).to_track_quat('-Z', 'Y').to_euler()
    return camera


def video(rig, mesh, clip="walk", folder=None, repeats=2, size=(960, 540)):
    """
    Stage 4. Renders one clip to an MP4 to look at before anything goes near Unity. Loops the
    walk a couple of times so the gait can be judged rather than guessed at.
    """
    # Tools sit at <project>/Assets/TinyDiggers/Art/Tools, and the videos belong beside the other
    # evidence at <project>/Screenshots, which is four levels up, not three.
    folder = folder or os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                    "..", "..", "..", "..", "Screenshots", "Units")
    folder = os.path.abspath(folder)
    os.makedirs(folder, exist_ok=True)

    scene = bpy.context.scene
    action = bpy.data.actions[f"dumper|{clip}"]
    if rig.animation_data is None:
        rig.animation_data_create()
    rig.animation_data.action = action
    if getattr(rig.animation_data, "action_slot", None) is None and action.slots:
        rig.animation_data.action_slot = action.slots[0]

    start, end = (int(v) for v in action.frame_range)
    scene.frame_start = start
    scene.frame_end = start + (end - start) * max(1, repeats) if clip in ("walk", "turn") else end
    if scene.frame_end > end:
        # Looping clips are keyed one cycle long: repeat by cycling the action.
        for curve in _fcurves(action):
            modifier = next((m for m in curve.modifiers if m.type == 'CYCLES'), None)
            if modifier is None:
                curve.modifiers.new('CYCLES')

    stage(mesh)
    scene.render.fps = FPS
    scene.render.resolution_x, scene.render.resolution_y = size
    # Blender 5 splits stills from video: FFMPEG only appears as a file format once the output
    # is told it is a video.
    if hasattr(scene.render.image_settings, "media_type"):
        scene.render.image_settings.media_type = 'VIDEO'
    scene.render.image_settings.file_format = 'FFMPEG'
    scene.render.ffmpeg.format = 'MPEG4'
    scene.render.ffmpeg.codec = 'H264'
    scene.render.ffmpeg.constant_rate_factor = 'HIGH'
    scene.render.ffmpeg.ffmpeg_preset = 'REALTIME'
    scene.render.filepath = os.path.join(folder, f"dumper_{clip}")

    with bpy.context.temp_override(**_window_override()):
        bpy.ops.render.render(animation=True)

    path = scene.render.filepath + f"{scene.frame_start:04d}-{scene.frame_end:04d}.mp4"
    if not os.path.exists(path):
        made = [f for f in os.listdir(folder) if f.startswith(f"dumper_{clip}")]
        path = os.path.join(folder, made[0]) if made else path
    _log(f"{clip}: video {path}")
    return path


def _fcurves(action):
    """An action's curves, 4.x style or 5.x channelbag style."""
    if hasattr(action, "fcurves") and len(action.fcurves):
        return list(action.fcurves)
    curves = []
    for layer in getattr(action, "layers", []):
        for strip in getattr(layer, "strips", []):
            for bag in getattr(strip, "channelbags", []):
                curves.extend(bag.fcurves)
    return curves


def build():
    """Everything: load the scan, tidy the rig, lay the clips, and leave it ready to render."""
    mesh, rig = load()
    face_forward(mesh, rig)
    tidied = tidy(mesh, rig)
    harden(mesh)
    hinge_tray(mesh, rig)
    legs = _rest(rig)
    made = clips(rig, legs, mesh=mesh)
    return {"bones": tidied["bones"], "legs": sorted(legs), "clips": made}
