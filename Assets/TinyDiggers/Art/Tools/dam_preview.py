"""
Headless: builds the dam kit, saves the .blend, and renders review shots of a short arc of the
ring (the pieces bent onto the circle the way Unity will bend them).

    blender -b --factory-startup -P dam_preview.py -- <out_dir> <shots_dir>
"""
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import td_dam  # noqa: E402
import td_pipeline as td  # noqa: E402

RADIUS = 258.0


def bend(obj, start_angle):
    """Polar bend, as Unity will do it: angle from x over the inner radius, radius = inner + y."""
    for v in obj.data.vertices:
        x, y, z = v.co
        a = start_angle + x / RADIUS
        r = RADIUS + y
        v.co = Vector((math.sin(a) * r, math.cos(a) * r, z))


def placed(source, start_angle, name):
    obj = source.copy()
    obj.data = source.data.copy()
    obj.name = name
    bpy.context.scene.collection.objects.link(obj)
    bend(obj, start_angle)
    return obj


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    out_dir, shots = argv[0], argv[1]
    os.makedirs(shots, exist_ok=True)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    report = td_dam.build_kit(out_dir)
    print("DAMKIT", report)
    kit = bpy.data.collections["DamKit"]
    blend = os.path.join(os.path.dirname(os.path.dirname(out_dir)), "Blender~", "dam_kit.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)

    bay = kit.objects["dam_bay"]
    spill = kit.objects["dam_spillway"]
    water = kit.objects["dam_spillway_water"]
    tower = kit.objects["dam_tower"]
    terminal = kit.objects["dam_terminal"]
    pad = kit.objects["dam_pad"]
    kit.hide_render = True

    # An arc, left to right: bays, a pad, bays, a spillway, bays with a tower, a terminal, bays.
    step = td_dam.BAY_WIDTH / RADIUS
    sequence = ["bay", "bay", "pad", "bay", "spill", "bay", "tower", "bay", "terminal", "bay", "bay"]
    widths = {"bay": td_dam.BAY_WIDTH, "tower": td_dam.BAY_WIDTH, "pad": td_dam.PAD_WIDTH,
              "spill": td_dam.SPILL_WIDTH, "terminal": td_dam.TERMINAL_WIDTH}
    total = sum(widths[k] for k in sequence)
    angle = -(total / 2) / RADIUS
    arc, centres = [], {}
    for i, kind in enumerate(sequence):
        width = widths[kind] / RADIUS
        centre = angle + width / 2
        centres.setdefault(kind, centre)
        if kind == "spill":
            arc += [placed(spill, centre, "arc_spill"), placed(water, centre, "arc_water")]
        elif kind == "pad":
            arc.append(placed(pad, centre, "arc_pad"))
        elif kind == "terminal":
            arc.append(placed(terminal, centre, "arc_terminal"))
        else:
            arc.append(placed(bay, centre, f"arc_bay_{i}"))
            if kind == "tower":
                arc.append(placed(tower, centre, "arc_tower"))
        angle += width

    # The sea inside, and a scrap of island for scale.
    bpy.ops.mesh.primitive_circle_add(vertices=256, radius=RADIUS + 0.5, fill_type="NGON", location=(0, 0, 0))
    sea = bpy.context.active_object
    sea.name = "sea"
    sea_mat = bpy.data.materials.new("Sea")
    sea_mat.diffuse_color = (0.12, 0.42, 0.50, 1)
    sea.data.materials.append(sea_mat)

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = "MATERIAL"
    shading.show_cavity = True
    shading.cavity_type = "BOTH"
    shading.show_shadows = True
    scene.display.shadow_focus = 0.5
    scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
    world = bpy.data.worlds.new("dark") if not scene.world else scene.world
    scene.world = world
    world.color = (0.07, 0.08, 0.11)
    shading.background_type = "WORLD"

    camera = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.data.lens = 35

    def shoot(name, location, target):
        camera.location = Vector(location)
        camera.rotation_euler = (Vector(target) - camera.location).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(shots, name + ".png")
        bpy.ops.render.render(write_still=True)

    def polar(a, r, z):
        return (math.sin(a) * r, math.cos(a) * r, z)

    t, p, sp = centres["terminal"], centres["pad"], centres["spill"]
    # The terminal from out in the void, three-quarter, and from above the berth.
    shoot("dock_terminal", polar(t - 0.42, RADIUS + 190, 55), polar(t, RADIUS + 50, -4))
    shoot("dock_terminal_high", polar(t + 0.16, RADIUS + 160, 140), polar(t, RADIUS + 45, 0))
    # A pad, close.
    shoot("dock_pad", polar(p + 0.14, RADIUS + 60, 18), polar(p, RADIUS + 28, -2))
    # The whole arc from outside and above, and from inside over the sea.
    shoot("dam_arc_outside", polar(0.0, RADIUS + 300, 160), polar(0.0, RADIUS + 30, -10))
    shoot("dam_inside", polar(t - 0.05, RADIUS - 110, 45), polar(t, RADIUS + 10, 4))
    shoot("dam_spillway_close", polar(sp + 0.1, RADIUS + 48, 4), polar(sp, RADIUS + 12, -8))
    # Straight pieces, for the kit sheet.
    for obj in arc + [sea]:
        obj.hide_render = True
    kit.hide_render = False
    for obj in kit.objects:
        obj.hide_render = True
    print("DAMSHOTS-done")
    print("DAMSHOTS", shots)


main()
