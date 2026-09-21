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
    kit.hide_render = True

    # An arc: bays either side of a spillway, a tower on one bay.
    step = td_dam.BAY_WIDTH / RADIUS
    angle = -6 * step
    arc = []
    for i in range(-6, 7):
        if i == 0:
            centre = angle + (td_dam.SPILL_WIDTH / 2) / RADIUS
            arc.append(placed(spill, centre, "arc_spill"))
            arc.append(placed(water, centre, "arc_water"))
            angle += td_dam.SPILL_WIDTH / RADIUS
            continue
        centre = angle + step / 2
        arc.append(placed(bay, centre, f"arc_bay_{i}"))
        if i == 2:
            arc.append(placed(tower, centre, "arc_tower"))
        angle += step

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

    mid = Vector((0, RADIUS + 10, -6))
    # Outside, three-quarter, looking back at the downstream face and the spillway.
    shoot("dam_outside", (70, RADIUS + 95, 28), mid)
    # Close on the spillway, from outside and below the crest.
    shoot("dam_spillway_close", (22, RADIUS + 48, 4), (0, RADIUS + 12, -8))
    # From inside, over the sea, as the player mostly sees it.
    shoot("dam_inside", (-10, RADIUS - 75, 32), (8, RADIUS + 5, 2))
    shoot("dam_gates_close", (-6, RADIUS - 26, 9), (0, RADIUS + 2, 2))
    tower_at = 4.5 * step
    tx, ty = math.sin(tower_at) * (RADIUS - 10), math.cos(tower_at) * (RADIUS - 10)
    shoot("dam_tower", (tx - 30, ty - 40, 22), (tx, ty, 4))
    shoot("dam_arc_high", (0, RADIUS - 150, 120), (0, RADIUS, -5))
    # Straight pieces, for the kit sheet.
    for obj in arc + [sea]:
        obj.hide_render = True
    kit.hide_render = False
    bay.location = (-40, 0, 0)
    tower.location = (-40, 0, 0)
    spill.location = water.location = (0, 0, 0)
    shoot("dam_kit_sheet", (-20, -70, 45), (-18, 8, -8))
    print("DAMSHOTS", shots)


main()
