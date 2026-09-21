"""
TinyDiggers crew unit, modelled from scratch in Blender (Ronan, 2026-09-21: "the smallest ball
will be our smallest unit, these represent our human scale ... remake that model completely in
blender so it's much better and more detailed").

    blender -b --factory-startup -P td_crew_model.py -- --out <folder/name> --blend <file>
            [--radius 0.1425] [--hover 0.125] [--shots <folder>]

Built at true size: the ball is 0.285 m across and floats 0.125 m up, so in the game it is
scale 1. The front is +Y, which imports as Unity's +Z. After the concept `crew_0_lens_8000`:
    shell     a cream ball with recessed panel seams: two meridians and two latitude rings
    band      a raised sunny-yellow band round the middle, with rounded rims
    eye       a socket cut through shell and band, a dark bezel ring, a domed glass lens with a
              darker iris ring, and a small highlight; the lens has planar UVs so the engine can
              put a radial glow on it
    shoulders dark sockets on each side with four bolts each
    arms      egg-shaped paddles with dark tips, on a shoulder hub, hanging down and a little
              forward
    pad       a dark ring under the belly round a glowing pad
It is rigged and animated with td_crew's bones and clips (Root, Body, Arm.L, Arm.R; Idle, Move,
Work, Carry), gets td_crew's hover-glow disc, and is exported the same way. The materials are
flat colours, named so the Unity import can map them: CrewShell, CrewBand, CrewTrim, CrewLens,
CrewIris, CrewHighlight, CrewPad.
"""
import argparse
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import td_crew  # noqa: E402
import td_pipeline as td  # noqa: E402

COLOURS = {
    "CrewShell": ((0.93, 0.89, 0.80), None),
    "CrewBand": ((0.98, 0.70, 0.12), None),
    "CrewTrim": ((0.10, 0.10, 0.12), None),
    "CrewLens": ((0.04, 0.42, 0.48), (0.30, 1.00, 0.95)),
    "CrewIris": ((0.02, 0.22, 0.28), (0.10, 0.55, 0.60)),
    "CrewHighlight": ((1.0, 1.0, 1.0), (1.0, 1.0, 1.0)),
    "CrewPad": ((1.0, 0.80, 0.40), (1.0, 0.75, 0.30)),
}


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--blend", required=True)
    p.add_argument("--radius", type=float, default=0.1425)
    p.add_argument("--hover", type=float, default=0.125)
    p.add_argument("--shots", default="")
    return p.parse_args(argv)


# --- helpers -----------------------------------------------------------------------------------

def material(name):
    existing = bpy.data.materials.get(name)
    if existing:
        return existing
    colour, emission = COLOURS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*colour, 1.0)
    if emission:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = 2.0
    m.diffuse_color = (*colour, 1.0)  # what the Workbench contact sheet shows
    return m


def finish_object(obj, name, mat):
    """Applies the object's transform, names it, gives it one material, smooth-shaded."""
    obj.name = name
    obj.data.name = name
    obj.data.transform(obj.matrix_world)
    obj.matrix_world = Matrix.Identity(4)
    obj.data.materials.clear()
    obj.data.materials.append(material(mat))
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    return obj


def sphere(name, mat, radius, centre, scale=(1, 1, 1), segments=48, rings=24):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, radius=radius,
                                         location=centre, scale=scale)
    return finish_object(bpy.context.active_object, name, mat)


def cylinder(name, mat, radius, depth, centre, rotation=(0, 0, 0), vertices=48):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth,
                                        location=centre, rotation=rotation)
    return finish_object(bpy.context.active_object, name, mat)


def torus(name, mat, major, minor, centre, rotation=(0, 0, 0), segments=48, sides=12):
    bpy.ops.mesh.primitive_torus_add(major_segments=segments, minor_segments=sides,
                                     major_radius=major, minor_radius=minor,
                                     location=centre, rotation=rotation)
    return finish_object(bpy.context.active_object, name, mat)


def box(name, size, centre, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=centre, rotation=rotation, scale=size)
    return finish_object(bpy.context.active_object, name, "CrewTrim")


def boolean(target, cutter, operation, keep=False):
    """Applies an exact boolean of `cutter` onto `target`; removes the cutter unless `keep`."""
    modifier = target.modifiers.new("Boolean", "BOOLEAN")
    modifier.operation = operation
    modifier.object = cutter
    modifier.solver = "EXACT"
    apply(target, modifier)
    if not keep:
        bpy.data.objects.remove(cutter, do_unlink=True)


def apply(obj, modifier):
    td.select_only(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=modifier.name)


def bevel(obj, width, segments=3, angle=30.0):
    modifier = obj.modifiers.new("Bevel", "BEVEL")
    modifier.width = width
    modifier.segments = segments
    modifier.limit_method = "ANGLE"
    modifier.angle_limit = math.radians(angle)
    apply(obj, modifier)


def shell_only(cutter, centre, inner):
    """Keeps only the part of `cutter` outside a sphere of radius `inner`: a cut that far deep."""
    core = sphere("Core", "CrewTrim", inner, centre)
    boolean(cutter, core, "DIFFERENCE")
    return cutter


# --- the parts ---------------------------------------------------------------------------------

def build(radius, hover):
    """Every part, joined into one mesh. Returns it and the arm vertex masks."""
    R = radius
    C = Vector((0.0, 0.0, hover + R))
    seam_width = 0.018 * R
    seam_depth = 0.022 * R

    # Shell, with seams cut into it.
    shell = sphere("Shell", "CrewShell", R, C, segments=64, rings=32)
    for yaw in (0.0, 90.0):
        slab = box("Seam", (seam_width, 2.6 * R, 2.6 * R), C, rotation=(0, 0, math.radians(yaw)))
        boolean(shell, shell_only(slab, C, R - seam_depth), "DIFFERENCE")
    for height in (0.62, -0.58):
        ring = cylinder("Seam", "CrewTrim", 1.3 * R, seam_width, C + Vector((0, 0, height * R)))
        boolean(shell, shell_only(ring, C, R - seam_depth), "DIFFERENCE")

    # Band: a thin raised shell round the middle, a little below the equator.
    band = sphere("Band", "CrewBand", 1.03 * R, C, segments=64, rings=32)
    slab = box("BandSlab", (3 * R, 3 * R, 0.38 * R), C + Vector((0, 0, -0.15 * R)))
    boolean(band, slab, "INTERSECT")
    boolean(band, sphere("BandCore", "CrewTrim", 0.97 * R, C), "DIFFERENCE")
    bevel(band, 0.012 * R)

    # Eye: tilted up a little, a socket through shell and band, then bezel, lens and highlight.
    tilt = Matrix.Rotation(math.radians(8.0), 4, "X")  # turns +Y up toward +Z
    along = (tilt @ Vector((0, 1, 0))).normalized()

    def on_axis(distance):
        return C + along * distance

    socket_rotation = (math.radians(90.0 + 8.0), 0.0, 0.0)  # cylinder and torus axes on `along`
    hole = cylinder("EyeHole", "CrewTrim", 0.40 * R, 0.6 * R, on_axis(1.08 * R), socket_rotation)
    boolean(shell, hole, "DIFFERENCE", keep=True)
    boolean(band, hole, "DIFFERENCE")
    bezel = torus("Bezel", "CrewTrim", 0.40 * R, 0.055 * R, on_axis(0.917 * R), socket_rotation)

    lens_radius = 0.5 * R
    lens_centre = 0.504 * R
    base = 0.84 * R
    lens = sphere("Lens", "CrewLens", lens_radius, (0, 0, 0), segments=48, rings=24)
    bm = bmesh.new()
    bm.from_mesh(lens.data)
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6,
                           plane_co=(0, 0, base - lens_centre), plane_no=(0, 0, 1), clear_inner=True)
    bm.to_mesh(lens.data)
    bm.free()
    # Planar UVs across the lens face, 0..1 over its base circle.
    half = math.sqrt(lens_radius ** 2 - (base - lens_centre) ** 2)
    # Into the sphere's own UV layer: a second one would not be the one exported.
    layer = lens.data.uv_layers.active or lens.data.uv_layers.new(name="UVMap")
    for loop in lens.data.loops:
        co = lens.data.vertices[loop.vertex_index].co
        layer.data[loop.index].uv = (0.5 + co.x / (2 * half), 0.5 + co.y / (2 * half))
    # An iris: a darker ring of the glass, between 55% and 72% of the way out.
    lens.data.materials.append(material("CrewIris"))
    for polygon in lens.data.polygons:
        out = math.hypot(polygon.center.x, polygon.center.y) / half
        if 0.55 < out < 0.72:
            polygon.material_index = 1
    # Its local +Z onto the eye axis, its centre `lens_centre` out from the ball's centre.
    place = Matrix.Translation(C) @ tilt @ Matrix.Rotation(math.radians(-90.0), 4, "X") \
        @ Matrix.Translation((0, 0, lens_centre))
    lens.data.transform(place)
    lens.data.update()

    # Upper left as the viewer sees it; facing the robot, the viewer's left is +X.
    glint_dir = (tilt @ Vector((0.32, 1.0, 0.36))).normalized()
    highlight = sphere("Highlight", "CrewHighlight", 1.0,
                       on_axis(lens_centre) + glint_dir * (lens_radius - 0.01 * R),
                       scale=(0.07 * R, 0.07 * R, 0.07 * R), segments=16, rings=8)

    # Shoulders: dark sockets with four bolts each, at band height on either side.
    parts = [shell, band, bezel, lens, highlight]
    shoulder_z = -0.14 * R
    for side in (1, -1):
        socket = cylinder("Socket", "CrewTrim", 0.27 * R, 0.16 * R,
                          C + Vector((side * 0.99 * R, 0, shoulder_z)), (0, math.radians(90), 0))
        bevel(socket, 0.02 * R)
        parts.append(socket)
        axis = Vector((side, 0, shoulder_z / R)).normalized()
        for k in range(4):
            spin = Matrix.Rotation(math.radians(45 + 90 * k), 3, axis)
            tilt_out = Matrix.Rotation(math.radians(24), 3, Vector((0, 1, 0)).cross(axis).normalized())
            direction = (spin @ (tilt_out @ axis)).normalized()
            bolt = cylinder("Bolt", "CrewTrim", 0.035 * R, 0.05 * R, C + direction * R, vertices=12)
            bolt.data.transform(Matrix.Translation(C + direction * R)
                                @ direction.to_track_quat("Z", "Y").to_matrix().to_4x4()
                                @ Matrix.Translation(-(C + direction * R)))
            parts.append(bolt)

    # Pad under the belly: a dark ring round a glowing dome.
    pad_ring = torus("PadRing", "CrewTrim", 0.30 * R, 0.07 * R, C + Vector((0, 0, -0.955 * R)))
    pad = sphere("Pad", "CrewPad", 1.0, C + Vector((0, 0, -0.975 * R)),
                 scale=(0.29 * R, 0.29 * R, 0.07 * R), segments=32, rings=12)
    parts += [pad_ring, pad]

    # Arms: a shoulder hub and an egg-shaped paddle with a dark tip, hanging down and forward.
    arms = []
    for side, group in ((1, "Arm.L_part"), (-1, "Arm.R_part")):
        # Front is +Y, so the robot's left is -X.
        x = -side
        hub = cylinder("Hub", "CrewShell", 0.15 * R, 0.22 * R,
                       C + Vector((x * 1.06 * R, 0, shoulder_z)), (0, math.radians(90), 0), vertices=32)
        paddle = sphere("Paddle", "CrewShell", 1.0, (0, 0, 0), scale=(0.17 * R, 0.20 * R, 0.40 * R),
                        segments=32, rings=20)
        paddle.data.materials.append(material("CrewTrim"))
        for polygon in paddle.data.polygons:
            if polygon.center.z < -0.30 * R:
                polygon.material_index = 1
        hang = Matrix.Translation(C + Vector((x * 1.26 * R, 0.04 * R, shoulder_z - 0.30 * R))) \
            @ Matrix.Rotation(math.radians(15), 4, "X") @ Matrix.Rotation(math.radians(-x * 10), 4, "Y")
        paddle.data.transform(hang)
        for piece in (hub, paddle):
            piece.vertex_groups.new(name=group).add(list(range(len(piece.data.vertices))), 1.0, "REPLACE")
        arms += [hub, paddle]

    td.select_only(*(parts + arms))
    bpy.context.view_layer.objects.active = shell
    bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = obj.data.name = "crew_unit"
    obj.data.set_sharp_from_angle(angle=math.radians(40))

    n = len(obj.data.vertices)
    masks = {}
    for group, key in (("Arm.L_part", "arm_l"), ("Arm.R_part", "arm_r")):
        g = obj.vertex_groups[group]
        mask = np.zeros(n, dtype=bool)
        for v in obj.data.vertices:
            if any(e.group == g.index for e in v.groups):
                mask[v.index] = True
        masks[key] = mask
        obj.vertex_groups.remove(g)
    return obj, masks


def main():
    a = parse()
    a.out = os.path.abspath(a.out)
    a.blend = os.path.abspath(a.blend)
    a.shots = os.path.abspath(a.shots) if a.shots else None
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0
    td_crew.LENGTH = a.radius / 0.283
    td_crew.SHEET_COLOUR = "MATERIAL"  # flat materials, no texture

    obj, masks = build(a.radius, a.hover)
    parts = {"radius": a.radius, "arm_l": masks["arm_l"], "arm_r": masks["arm_r"]}
    report = {"triangles": sum(len(p.vertices) - 2 for p in obj.data.polygons),
              "vertices": len(obj.data.vertices),
              "materials": [m.name for m in obj.data.materials],
              "arm_l_vertices": int(masks["arm_l"].sum()), "arm_r_vertices": int(masks["arm_r"].sum()),
              "size_m": [round(v, 3) for v in obj.dimensions]}
    rig, weights = td_crew.build_rig(obj, parts, a.hover)
    glow = td_crew.add_glow(obj, rig, parts)
    report["clips"] = td_crew.animate(rig)
    td_crew.finish(obj, rig, a.out, a.blend, glow)
    if a.shots:
        report["sheet"] = td_crew.shots(rig, a.shots)
    print("TD_CREW_MODEL " + json.dumps(report))


if __name__ == "__main__":
    main()
