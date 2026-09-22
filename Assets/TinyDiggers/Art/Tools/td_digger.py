"""Finishes the digger: the four-legged crab excavator.

Ronan, 2026-09-22: "we will knock out the next unit so we have the new digger as well, it will be
same size as the dumper ... also the bucket arm does not have the correct rigging to work like a
real excavator, it will walk and move like the dumptruck but you may have to fix its legs as well
for rigging".

It walks on `td_walker`, the same tool the dumper uses. What is different is the arm:

- The auto-rigger gave the whole arm **one** long bone and a handful of stubs at the bucket, so it
  could only wave about in one piece. A real excavator needs four joints: a turret that swings the
  arm round, a boom off the body, a stick, and a bucket that curls.
- The arm is sixty-odd separate pieces in the scan, so there is nothing to find by connectivity.
  Instead its **spine** is measured: a graph over the arm's own vertices, walked out from the
  shoulder, gives a distance along the arm; the spine is the run of centroids at each distance,
  and the joints are where that spine bends hardest.
- The legs are re-found the same way, because the auto-rig had the arm down as a leg and folded a
  real leg into it.

    import td_digger; td_digger.deliver()
"""
import math
import os

import bmesh
import bpy
import mathutils

import td_walker
from td_walker import _log, _shells_of, _window_override, harden, weld, face_forward

NAME = "digger"

# Ronan's ruling: the same size as the dumper, measured to the top of the body shell.
HEIGHT = 0.7

# How far apart two arm vertices can be and still count as joined when the spine is walked out.
# The arm's pieces stand a few millimetres clear of each other in the scan.
STEP = 0.075

# Where the joints sit along the arm, as a share of its length, when the bends are not clear.
FALLBACK = (0.38, 0.72)

# How much of the front limb's height the bucket takes up. The bucket is the short link at the
# bottom; the stick is the long run down to it.
BUCKET_SHARE = 0.34

# The digger's scan carries its arm along -X. An excavator faces the way it digs, so it is turned
# a quarter the other way from the dumper to look along +Y (Unity's +Z) with the bucket out front.
FACING = -90.0


def _ball(mesh):
    """The body shell: the biggest welded piece, with its centre and its top."""
    shell = _shells_of(mesh)[0]
    points = [co for _, co in shell]
    lo = mathutils.Vector((min(c.x for c in points), min(c.y for c in points), min(c.z for c in points)))
    hi = mathutils.Vector((max(c.x for c in points), max(c.y for c in points), max(c.z for c in points)))
    return {"indices": {index for index, _ in shell}, "lo": lo, "hi": hi,
            "centre": (lo + hi) * 0.5, "top": hi.z}


def _spine(points, shoulder, step=STEP, bands=14):
    """
    Walks out from the shoulder through the arm's own vertices and returns the run of centroids,
    from the shoulder to the far end.

    The arm folds back over the body, so distance through the air says nothing about how far along
    the arm a point is; distance *through the arm* does.
    """
    from mathutils import kdtree

    tree = kdtree.KDTree(len(points))
    for index, point in enumerate(points):
        tree.insert(point, index)
    tree.balance()

    start = min(range(len(points)), key=lambda i: (points[i] - shoulder).length)

    # Reach far enough to join the arm's separate pieces. The scan's spacing is not known in
    # advance, so the step grows until most of the arm has been walked.
    reach = []
    for attempt in range(6):
        reach = [None] * len(points)
        reach[start] = 0.0
        front = [start]
        while front:
            nxt = []
            for index in front:
                for _, other, distance in tree.find_range(points[index], step):
                    if reach[other] is None or reach[index] + distance < reach[other] - 1e-6:
                        reach[other] = reach[index] + distance
                        nxt.append(other)
            front = nxt
        if sum(1 for r in reach if r is not None) > len(points) * 0.8:
            break
        step *= 1.6

    walked = [(r, points[i]) for i, r in enumerate(reach) if r is not None]
    if not walked:
        return [], {}
    longest = max(r for r, _ in walked)

    spine = []
    for band in range(bands):
        low = longest * band / bands
        high = longest * (band + 1) / bands
        inside = [p for r, p in walked if low <= r <= high]
        if inside:
            spine.append(sum(inside, mathutils.Vector()) / len(inside))

    along = {i: r for i, r in enumerate(reach) if r is not None}
    _log(f"arm spine: {len(spine)} points over {longest:.2f} m, "
         f"{len(along)} of {len(points)} vertices reached at a step of {step:.3f} m")
    return spine, along


def _joints(spine):
    """
    Where the arm's two hinges are: boom to stick, and stick to bucket.

    The hardest bends in the spine, but each of the three segments has to be a real part of the
    arm — at least `LEAST` of its length — or the fit hands the whole arm to the boom and leaves
    a stub for the bucket, which is what happened first time out.
    """
    if len(spine) < 5:
        return None

    bends = []
    for i in range(1, len(spine) - 1):
        back = (spine[i] - spine[i - 1]).normalized()
        on = (spine[i + 1] - spine[i]).normalized()
        bends.append((back.angle(on), i))

    least = max(1, int(len(spine) * LEAST))
    best = None
    for first in range(least, len(spine) - 2 * least + 1):
        for second in range(first + least, len(spine) - least + 1):
            score = sum(angle for angle, index in bends if index in (first, second))
            if best is None or score > best[0]:
                best = (score, [first, second])

    if best is None:
        return [max(1, int(len(spine) * FALLBACK[0])),
                min(len(spine) - 2, int(len(spine) * FALLBACK[1]))]
    return best[1]


def _pinch(points, axis_lo, axis_hi, bands=24):
    """
    Where a limb pinches: the narrowest slice across it between two heights.

    A machine's joints are pins, and a pin is the thinnest thing on the limb — on this scan the
    bucket's pivot narrows to twelve millimetres where the arm around it is ninety. Looking for
    the pinch finds the joint the model actually has, instead of guessing a share of its length.
    """
    inside = [c for c in points if axis_lo <= c.z <= axis_hi]
    if len(inside) < 12:
        return None

    low = min(c.z for c in inside)
    high = max(c.z for c in inside)
    if high - low < 1e-4:
        return None

    best = None
    for band in range(bands):
        a = low + (high - low) * band / bands
        b = low + (high - low) * (band + 1) / bands
        slice_ = [c for c in inside if a <= c.z <= b]
        if len(slice_) < 4:
            continue
        width = max(c.x for c in slice_) - min(c.x for c in slice_)
        depth = max(c.y for c in slice_) - min(c.y for c in slice_)
        girth = max(width, depth)
        if best is None or girth < best[0]:
            best = (girth, sum(slice_, mathutils.Vector()) / len(slice_))

    return None if best is None else best[1]


def _arm_joints(points, shoulder):
    """
    The excavator's own joints, off the machine's landmarks rather than a share of its length.

    Reading the layout Ronan sent: the boom pivots low on the body (O1) and rises to the top of
    the fold (O2); the stick runs from there down and forward to the bucket's pivot (O3); the
    bucket curls about that and ends in its teeth (O4).

    O2 is the top of the arm. O3 is the **pinch** in the limb below it — the bucket's pin, which
    is the thinnest part of the arm. O4 is the far end of the bucket below the pin.
    """
    top = max(points, key=lambda c: c.z)
    front = [c for c in points if c.y > top.y - 0.06]
    if len(front) < 20:
        front = [c for c in points if (c - shoulder).length > (top - shoulder).length * 0.8]

    low = min(c.z for c in front)
    span = top.z - low

    # The pin is in the lower half of the descending limb, clear of its end.
    wrist = _pinch(front, low + span * 0.08, low + span * 0.55)
    if wrist is None:
        wrist = mathutils.Vector((top.x, top.y, low + span * BUCKET_SHARE))

    bucket = [c for c in front if c.z < wrist.z]
    teeth = max(bucket, key=lambda c: (c - wrist).length) if bucket else         mathutils.Vector((wrist.x, wrist.y, low))
    return [shoulder.copy(), top.copy(), wrist, teeth]


def _near_segment(point, head, tail):
    """How far a point lies off a bone's own line."""
    span = tail - head
    length = span.length
    if length < 1e-6:
        return (point - head).length
    share = max(0.0, min(1.0, (point - head).dot(span) / (length * length)))
    return (point - (head + span * share)).length


def _set_bone(rig, name, head, tail):
    """Moves one bone in edit mode, leaving the rig back in object mode."""
    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        bone = rig.data.edit_bones.get(name)
        if bone is not None:
            bone.head = head
            bone.tail = tail
            bone.roll = 0.0
        bpy.ops.object.mode_set(mode='OBJECT')


def rebuild_arm(mesh, rig, parts=None):
    """
    Replaces the auto-rig's arm bones with the three an excavator actually has: a boom off the
    body, a stick, and a bucket, each a hinge across the machine so the arm works in its own plane
    and never swings sideways.

    Two things have to be right, and they come from different places. **Which** geometry is the
    arm comes from the scan's own rig (`parts`, from `name_parts`): taking "everything standing
    above the hull" instead swept in the antennae and the boxes on the machine's back, and the
    boom flung them about. **Where** the joints are has to be measured, because UniRig put the
    bones themselves low on the body, well below everything they hold.
    """
    ball = _ball(mesh)
    owner = {v.index: max(((g.weight, g.group) for g in v.groups), default=(0.0, -1))[1]
             for v in mesh.data.vertices}
    name_of = {g.index: g.name for g in mesh.vertex_groups}

    def not_a_leg(index):
        return not name_of.get(owner[index], "").startswith("leg_")

    if parts and len(parts.get("main", ())) >= 50:
        arm_indices = [i for i in parts["main"] + parts["bucket"] if not_a_leg(i)]
    else:
        # Fallback only, for a scan whose rig says nothing: whatever stands above the hull.
        parts = None
        arm_indices = [v.index for v in mesh.data.vertices
                       if v.co.z > ball["top"] - 0.02 and not_a_leg(v.index)]
    if len(arm_indices) < 50:
        _log("no arm found")
        return None

    shells = _shells_of(mesh)
    points = [mesh.data.vertices[i].co.copy() for i in arm_indices]
    if parts:
        # O1 is the mount on the crown of the hull, where this machine carries its arm. The
        # middle of the hull puts the pivot inside the sphere and the boom then swings its base
        # out through the shell.
        crown = max((co for _, co in shells[0]), key=lambda c: c.z) if shells else ball["centre"]
        shoulder = min(points, key=lambda c: (c - crown).length)
    else:
        shoulder = min(points, key=lambda c: (c - ball["centre"]).length)
    heads = _arm_joints(points, shoulder)
    heads[0] = shoulder.copy()

    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        bones = rig.data.edit_bones

        for bone in [b for b in list(bones) if b.name in ("turret", "boom", "stick", "bucket")]:
            bones.remove(bone)

        parent = bones.get("body")
        for label, bone_head, bone_tail in (("boom", heads[0], heads[1]),
                                            ("stick", heads[1], heads[2]),
                                            ("bucket", heads[2], heads[3])):
            bone = bones.new(label)
            bone.head = bone_head
            bone.tail = bone_tail
            bone.roll = 0.0
            bone.parent = parent
            bone.use_connect = label != "boom"
            parent = bone

        bpy.ops.object.mode_set(mode='OBJECT')

    # The bucket is cut at the pin, vertex by vertex, and only the bucket is. The scoop is welded
    # into the same piece as the stick on this scan, so no piece-level rule separates them; and
    # a real bucket pivot makes exactly this cut, everything past the pin turning with the
    # bucket. Boom and stick stay piece-level, where welded parts must not be torn apart.
    links = [("boom", heads[0], heads[1]), ("stick", heads[1], heads[2]),
             ("bucket", heads[2], heads[3])]
    wanted = set(arm_indices)
    pieces = []
    for shell in shells:
        inside = [(index, co) for index, co in shell if index in wanted]
        if inside:
            pieces.append(inside)

    down = (heads[2] - heads[1]).normalized()
    # Past the pin **and** within reach of it. The scan's arm group also holds odd fittings low
    # down the front of the machine, and "everything below the pin" swept those in, stretching
    # the bucket a metre down to take them with it. Measured on this scan, the arm below the pin
    # runs a stick's length and no further; the strays are far beyond that.

    claimed = {"boom": [], "stick": [], "bucket": []}
    for piece in pieces:
        bucket_side = {index for index, co in piece
                       if (co - heads[2]).dot(down) > 0.0
                       and (co - heads[2]).length <= (heads[2] - heads[1]).length}
        claimed["bucket"].extend(bucket_side)
        rest = [index for index, _ in piece if index not in bucket_side]
        if not rest:
            continue
        centre = sum((mesh.data.vertices[i].co for i in rest), mathutils.Vector()) / len(rest)
        label = min(links[:2], key=lambda link: _near_segment(centre, link[1], link[2]))[0]
        claimed[label].extend(rest)

    # Now the scoop is known, the bucket bone is aimed down it: along the line from the pin to the
    # middle of the scoop, reaching its farthest corner, and kept in the arm's own plane because a
    # scoop is symmetrical. Aimed straight at that farthest corner it pointed backwards into the
    # machine, since the bucket's linkage reaches back past the pin.
    if claimed["bucket"]:
        scoop = [mesh.data.vertices[i].co for i in claimed["bucket"]]
        along = (sum(scoop, mathutils.Vector()) / len(scoop)) - heads[2]
        along.x = 0.0
        if along.length < 1e-4:
            along = mathutils.Vector((0.0, 0.0, -1.0))
        heads[3] = heads[2] + along.normalized() * max((co - heads[2]).length for co in scoop)
        heads[3].x = heads[2].x
        _set_bone(rig, "bucket", heads[2], heads[3])
        links = [links[0], links[1], ("bucket", heads[2], heads[3])]

    vertex_groups = {label: (mesh.vertex_groups.get(label) or mesh.vertex_groups.new(name=label))
                     for label in claimed}
    counts = {}
    for label, indices in claimed.items():
        for group in mesh.vertex_groups:
            if group.name != label:
                group.remove(indices)
        vertex_groups[label].add(indices, 1.0, 'REPLACE')
        counts[label] = len(indices)

    lengths = {label: round((tail - head).length, 3) for label, head, tail in links}
    _log("arm rebuilt: " + ", ".join(f"{label} {count} verts, {lengths[label]} m"
                                     for label, count in counts.items()))
    return {"heads": [[round(v, 3) for v in head] for head in heads],
            "weights": counts, "lengths": lengths}



def name_parts(mesh, rig):
    """
    Names the digger's bones. The generic tidy had the arm down as a fourth leg and folded a real
    leg into it, because the arm hangs off the body like any other chain: here a chain that ends
    above the shell is the arm, and only the rest are legs.
    """
    ball = _ball(mesh)
    centres = td_walker.weighted_centres(mesh)
    body = max(rig.data.bones, key=lambda b: len(b.children))
    base = body.parent

    chains = []
    for start in body.children:
        if start is base or start.length <= td_walker.STUB:
            continue
        chains.append(td_walker._chain_from(start))

    legs = {}
    arm_chains = []
    for chain in chains:
        # The arm is the chain whose *geometry* climbs above the shell. Judged on what the bones
        # actually hold, not on where they sit: the auto-rigger's arm bones start low on the body
        # and its last one is a bucket stub, so their own positions say nothing.
        held = [centres[bone.name][0].z for bone in chain if bone.name in centres]
        if held and max(held) > ball["top"]:
            arm_chains.append(chain)
            continue
        tip = centres.get(chain[-1].name, (chain[-1].tail_local, 0, 0))[0]
        corner = ("front" if tip.y > ball["centre"].y else "back") \
            + ("_left" if tip.x < ball["centre"].x else "_right")
        legs.setdefault(corner, []).append(chain)

    renames = {body.name: "body"}
    if base is not None:
        renames[base.name] = "base"

    folded = 0
    labels = ["upper", "lower", "foot", "toe", "claw", "tip"]
    for corner, found in legs.items():
        found.sort(key=lambda c: -sum(b.length for b in c))
        keep = found[0]
        for spare in found[1:]:
            for bone in spare:
                folded += td_walker._fold(mesh, bone.name, keep[-1].name)
        for i, bone in enumerate(keep):
            renames[bone.name] = f"leg_{corner}_{labels[min(i, len(labels) - 1)]}"

    # The arm's own bones go; rebuild_arm puts proper ones in their place.
    doomed = [bone.name for chain in arm_chains for bone in chain]

    # Before they go, take what they hold. The auto-rig already knew which geometry is the arm,
    # and nothing else does: taking "everything standing above the hull" instead swept in the
    # antennae and the boxes on the machine's back, and the boom then flung them about.
    group_name = {g.index: g.name for g in mesh.vertex_groups}
    held = {}
    for vertex in mesh.data.vertices:
        best = max(vertex.groups, key=lambda g: g.weight, default=None)
        if best is not None:
            held.setdefault(group_name.get(best.group), []).append(vertex.index)

    # And it knew where the bucket's pin is, which no amount of measuring the geometry gets
    # reliably right. The chain's big bone is the arm itself; whatever hangs off the end of it is
    # the bucket, and the joint between them is the pin the scan was rigged with.
    arm_parts = None
    for chain in arm_chains:
        main = max(chain, key=lambda b: len(held.get(b.name, ())))
        after = chain[chain.index(main) + 1:]
        arm_parts = {
            "main": [i for b in chain[:chain.index(main) + 1] for i in held.get(b.name, ())],
            "bucket": [i for b in after for i in held.get(b.name, ())],
            "pin": (after[0].head_local if after else main.tail_local).copy(),
            "tip": (after[-1].tail_local if after else main.tail_local).copy(),
        }
        break
    arm_indices = (arm_parts["main"] + arm_parts["bucket"]) if arm_parts else []

    anchors = [(name, rig.data.bones[old].head_local.copy())
               for old, name in renames.items() if old in rig.data.bones]
    for bone in rig.data.bones:
        if bone.name in renames or bone.name in doomed:
            continue
        nearest = min(anchors, key=lambda a: (a[1] - bone.head_local).length)
        folded += td_walker._fold(mesh, bone.name, nearest[0])

    with bpy.context.temp_override(**_window_override(), active_object=rig, object=rig,
                                   selected_objects=[rig], selected_editable_objects=[rig]):
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        bones = rig.data.edit_bones
        for old, new in renames.items():
            if old in bones:
                bones[old].name = new
        for name in doomed:
            if name in bones:
                bones.remove(bones[name])
        for bone in [b for b in list(bones) if b.name not in renames.values()]:
            bones.remove(bone)
        if "base" in bones and "body" in bones:
            bones["body"].parent = None
            bones["base"].parent = bones["body"]
        bpy.ops.object.mode_set(mode='OBJECT')

    _log(f"named {len(renames)} bones, {len(doomed)} arm bones dropped, {folded} weights moved; "
         f"legs {sorted(legs)}")
    return {"legs": sorted(legs), "arm_bones_dropped": len(doomed),
            "arm_indices": arm_indices, "arm_parts": arm_parts}


# --- the dig -------------------------------------------------------------------------------------

# The cut, as how far each joint has turned in its digging direction — which way that is comes
# from the geometry, not from here (see `_turn_signs`): the boom lowers, the stick pushes out,
# the bucket curls in. Negative is the other way: boom up, stick in, bucket open.
#
# This is how an excavator is actually dug, from Cat's and SANY's operator guides rather than
# invented: the stick works between about 40 degrees out and vertical, the bucket floor goes in
# at about 45 degrees to grade, and the bucket is full by the time the stick stands upright.
#   reach out   — stick out, bucket open, teeth presented to grade
#   penetrate   — boom down so the bucket floor sits about 45 degrees into the surface
#   drag / fill — the long phase: the stick crowds back to vertical while the bucket curls
#                 through the cut, the boom easing up to hold grade rather than drag the teeth
#                 under it
#   lift        — bucket shut on the load, boom up clear of the trench
#   carry       — nothing moves in the arm; the machine swings its body
#   dump        — stick out, bucket opens, boom holding its height
#   return      — back to rest
# The bucket never opens between the fill and the dump: that is what spills the load.
DIG = (
    (1,  {"boom":   0.0, "stick":   0.0, "bucket":   0.0}),
    (14, {"boom":  20.0, "stick":  40.0, "bucket": -25.0}),
    (26, {"boom":  34.0, "stick":  38.0, "bucket": -10.0}),
    (50, {"boom":  22.0, "stick":   0.0, "bucket":  55.0}),
    (62, {"boom": -18.0, "stick":  -8.0, "bucket":  75.0}),
    (70, {"boom": -18.0, "stick":  -8.0, "bucket":  75.0}),
    (80, {"boom": -16.0, "stick":  25.0, "bucket": -35.0}),
    (90, {"boom":   0.0, "stick":   0.0, "bucket":   0.0}),
)

# Every joint is a hinge in the arm's own plane. There is no swing: Ronan, 2026-09-22, "it should
# not move side to side it should move like an excavator". The machine turns its whole body to
# dump, the way a crab would.
AXES = {"boom": (1.0, 0.0, 0.0), "stick": (1.0, 0.0, 0.0), "bucket": (1.0, 0.0, 0.0)}


def _bucket_tip(rig):
    """Where the bucket's far end is now, in the rig's space."""
    bone = rig.pose.bones.get("bucket")
    return None if bone is None else bone.tail.copy()


def _turn_signs(rig):
    """
    Which way each joint has to turn to dig.

    The bones are fitted to the scan's own spine, so their axes point wherever that geometry
    happened to lie: the boom might lift on a positive angle or drop on one. Each is tried both
    ways and judged on what it does to the bucket — the boom should lower it, the stick should
    push it away from the machine, the bucket should curl it back in.
    """
    signs = {}
    rest = _bucket_tip(rig)
    if rest is None:
        return {name: 1.0 for name in AXES}

    turret = rig.pose.bones["boom"].head.copy()
    wants = {
        "boom": lambda tip: -tip.z,                                  # lower it
        "stick": lambda tip: (tip - turret).length,                  # push it out
        "bucket": lambda tip: -(tip - turret).length,                # curl it in
    }

    for joint, better in wants.items():
        bone = rig.pose.bones.get(joint)
        if bone is None:
            signs[joint] = 1.0
            continue
        scores = {}
        for sign in (1.0, -1.0):
            bone.rotation_mode = 'QUATERNION'
            bone.rotation_quaternion = mathutils.Quaternion(AXES[joint], math.radians(25.0 * sign))
            bpy.context.view_layer.update()
            scores[sign] = better(_bucket_tip(rig))
        bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        bpy.context.view_layer.update()
        signs[joint] = 1.0 if scores[1.0] >= scores[-1.0] else -1.0

    _log("dig turns: " + ", ".join(f"{joint} {sign:+.0f}" for joint, sign in signs.items()))
    return signs


def dig(rig, legs, name="dig"):
    """
    The digging cycle: reach, cut, lift, swing, dump, come back. The legs stay planted throughout —
    an excavator digs with its feet still.
    """
    action = td_walker._new_action(rig, name)
    td_walker._rest_pose(rig)
    for leg in legs.values():
        td_walker._solve_leg(rig, leg, leg["contact"], mathutils.Vector())

    signs = _turn_signs(rig)
    for frame, pose in DIG:
        for joint, angle in pose.items():
            bone = rig.pose.bones.get(joint)
            if bone is None:
                continue
            bone.rotation_mode = 'QUATERNION'
            bone.rotation_quaternion = mathutils.Quaternion(
                AXES[joint], math.radians(angle * signs.get(joint, 1.0)))
        bpy.context.view_layer.update()
        td_walker._key(rig, legs, frame)
        for joint in pose:
            bone = rig.pose.bones.get(joint)
            if bone is not None:
                bone.keyframe_insert("rotation_quaternion", frame=frame)

    _log(f"{name}: {DIG[-1][0]} frames")
    return action


def clips(rig, legs, scale=1.0):
    """The walker's clip set, plus the dig. No tip, no loaded walk: this one carries nothing."""
    made = td_walker.clips(rig, legs, mesh=None, scale=scale)
    action = dig(rig, legs)
    made["dig"] = {"action": action.name, "frames": int(action.frame_range[1])}
    rig.animation_data.action = None
    td_walker._rest_pose(rig)
    return made


def build(height=HEIGHT, crew=False):
    """Scan to rigged, sized, painted machine with its clips."""
    mesh, rig = td_walker.load(NAME)
    weld(mesh)
    face_forward(mesh, rig, yaw=FACING)
    named = name_parts(mesh, rig)
    harden(mesh)
    # Before the arm: the hull and everything tucked inside it goes to the body bone, so the walk
    # cannot drag the underside about. The arm is its own welded shell, well clear of the hull.
    td_walker.shell_to_body(mesh)
    arm = rebuild_arm(mesh, rig, named.get("arm_parts"))

    scale = 1.0
    if height:
        scale = td_walker.set_height(height) or 1.0

    td_walker.settle_weights(mesh, rig)
    td_walker.paint(mesh, rig)
    legs = td_walker._rest(rig)
    made = clips(rig, legs, scale=scale)
    if crew:
        td_walker.bring_crew()

    return {"legs": named["legs"], "arm": arm, "scale": round(scale, 3), "clips": made}


def deliver(height=HEIGHT):
    """Build it and write the FBX for Unity."""
    report = build(height=height, crew=False)
    report["export"] = td_walker.export(name=NAME)
    return report
