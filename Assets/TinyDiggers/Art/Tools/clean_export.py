"""
SUPERSEDED (2026-09-21): this is the Phase 1 record. Props are no longer voxel-remeshed (Ronan:
only the terrain is cells). The current path is td_pipeline.py + td_cards.py, run in live Blender.

TinyDiggers prop export: every generated mesh goes through this, so scale, pivot, triangle budget
and palette are identical across the set (STYLE.md).

    blender --background --factory-startup --python clean_export.py -- \
        --in raw.glb --out Assets/TinyDiggers/Art/Props/tree_a --height 10 --category plant

Steps, in order:
 1. Import the raw mesh (GLB from Trellis2) and join it into one object.
 2. Clean: merge by distance, delete loose parts, dissolve degenerate faces, recalculate normals.
 3. Split wood from foliage by the base colour (a trunk is too thin to share the canopy's
    voxel size). Voxel-remesh each into a closed hull (--voxel, a fraction of the height), smooth it, drop
    floaters, and decimate it to --tris (default 1500). Trellis2 builds a canopy out of a thousand
    loose leaf shells; the remesh fuses them into the few soft clumps STYLE.md asks for, and a
    closed hull is what collapse decimation needs to reach the budget without tearing.
 4. Unwrap the low mesh and bake the raw mesh's base colour onto it at --tex (default 512).
 5. Quantise toward the palette: every texel is grouped by its nearest STYLE.md albedo swatch for
    the category, each group's mean is moved onto its swatch, and the painted variation around the
    mean is kept at (1 - --pull) of its strength. The hues and values are ours whatever Trellis2
    or the bake did to them (a canopy bake reads a little dark: rays slip between leaves and find
    their shaded backs), and the brushwork survives.
 6. Pivot at the base centre, +Y up (the glTF/FBX exporters convert from Blender's +Z), scaled so
    the object is --height metres tall. 1 unit = 1 m.
 7. Matte material (roughness 1, metallic 0) and export <out>.glb and <out>.fbx, plus a JSON
    report beside them.
"""
import argparse
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np

# Albedo swatches per category, from STYLE.md section 1. Only the colours a surface *is*: the
# light and shade tones (Leaf Light, Leaf Deep) come from Unity's lighting, not from the albedo.
SWATCHES = {
    "plant": {
        "Leaf Mid": "#4F9A3E",
        "Meadow Sun": "#86B83A",
        "Canopy Teal": "#5E9F8C",
        "Bark": "#9C5A3C",
    },
    "stone": {
        "Stone": "#A3A6A4",
        "Leaf Mid": "#4F9A3E",
        "Meadow Sun": "#86B83A",
        "Path Earth": "#A65A30",
    },
    "vehicle": {
        "Machine Yellow": "#E8B33A",
        "Stone": "#A3A6A4",
        "Bark": "#9C5A3C",
    },
}


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="source", required=True)
    parser.add_argument("--out", required=True, help="output path without extension")
    parser.add_argument("--height", type=float, required=True, help="metres from base to top")
    parser.add_argument("--category", choices=sorted(SWATCHES), required=True)
    parser.add_argument("--tris", type=int, default=1500)
    parser.add_argument("--tex", type=int, default=512)
    parser.add_argument("--pull", type=float, default=0.5, help="0 keeps the bake, 1 flattens to swatches")
    parser.add_argument("--albedo-value", dest="albedo_value", type=float, default=0.72,
                        help="swatches were sampled from lit reference renders; the albedo that "
                             "looks like them under our sun is this much darker")
    parser.add_argument("--wood-voxel", dest="wood_voxel", type=float, default=0.0,
                        help="remesh voxel size for trunk and branches, as a fraction of the height; 0 keeps the "
                             "raw wood surface (a fine remesh made collapse stall at ~1800 tris)")
    parser.add_argument("--wood-share", dest="wood_share", type=float, default=0.2,
                        help="share of the triangle budget kept for wood")
    parser.add_argument("--foliage-cool", dest="foliage_cool", type=float, default=0.3,
                        help="plants: move the foliage target this far from Leaf Mid toward Canopy "
                             "Teal; our warm sun reads a pure Leaf Mid albedo as lime")
    parser.add_argument("--voxel", type=float, default=0.02, help="remesh voxel size, as a fraction of the height")
    parser.add_argument("--smooth", type=int, default=12, help="smoothing iterations on the hull before decimation")
    return parser.parse_args(argv)


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def hex_to_rgb(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])


def triangle_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def clean(obj, merge_distance):
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    bmesh.ops.remove_doubles(mesh, verts=mesh.verts, dist=merge_distance)
    bmesh.ops.dissolve_degenerate(mesh, edges=mesh.edges, dist=merge_distance * 0.1)
    loose = [v for v in mesh.verts if not v.link_faces]
    bmesh.ops.delete(mesh, geom=loose, context="VERTS")
    bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
    mesh.to_mesh(obj.data)
    mesh.free()


def drop_small_islands(obj, keep_fraction=0.002):
    """Deletes disconnected shells smaller than keep_fraction of the faces: floaters Trellis leaves."""
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    mesh.faces.ensure_lookup_table()
    seen = set()
    islands = []
    for face in mesh.faces:
        if face.index in seen:
            continue
        stack = [face]
        island = []
        seen.add(face.index)
        while stack:
            current = stack.pop()
            island.append(current)
            for edge in current.edges:
                for other in edge.link_faces:
                    if other.index not in seen:
                        seen.add(other.index)
                        stack.append(other)
        islands.append(island)
    limit = max(1, int(len(mesh.faces) * keep_fraction))
    doomed = [f for island in islands if len(island) < limit for f in island]
    bmesh.ops.delete(mesh, geom=doomed, context="FACES")
    mesh.to_mesh(obj.data)
    mesh.free()
    return len(islands), sum(1 for island in islands if len(island) < limit)


def non_manifold_edges(obj):
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    count = sum(1 for e in mesh.edges if not e.is_manifold)
    mesh.free()
    return count


def split_wood(obj):
    """
    Splits the faces whose base colour is wood (redder than green) into their own object. A trunk
    is far thinner than a canopy clump: one hull for both either loses the trunk or keeps the
    canopy's leaf noise, so each gets its own voxel size and triangle share. Returns the wood
    object, or None when there is no wood (a rock, a machine).
    """
    material = obj.data.materials[0] if obj.data.materials else None
    image = None
    if material and material.node_tree:
        principled = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if principled and principled.inputs["Base Color"].links:
            source = principled.inputs["Base Color"].links[0].from_node
            image = getattr(source, "image", None)
    if image is None or not obj.data.uv_layers:
        return None
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)

    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    uv = mesh.loops.layers.uv.active
    wood = []
    for face in mesh.faces:
        u = sum(loop[uv].uv.x for loop in face.loops) / len(face.loops)
        v = sum(loop[uv].uv.y for loop in face.loops) / len(face.loops)
        px = pixels[min(height - 1, max(0, int(v * height))), min(width - 1, max(0, int((u % 1.0) * width)))]
        if px[0] > px[1] * 1.05:
            wood.append(face.index)
    mesh.free()
    if len(wood) < 50:
        return None

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.object.mode_set(mode="OBJECT")
    for index in wood:
        obj.data.polygons[index].select = True
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    parts = [o for o in bpy.context.selected_objects if o is not obj]
    return parts[0] if parts else None


def hull(obj, voxel, smooth_iterations, keep_fraction):
    """Voxel remesh into one closed hull, volume-preserving smooth, drop floaters."""
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    if voxel > 0:
        remesh = obj.modifiers.new("remesh", "REMESH")
        remesh.mode = "VOXEL"
        remesh.voxel_size = voxel
        remesh.adaptivity = 0.0
        bpy.ops.object.modifier_apply(modifier=remesh.name)
    if smooth_iterations > 0:
        # Laplacian with volume preservation: a plain smooth shrinks thin parts, and the first
        # tree lost its trunk to it.
        smooth = obj.modifiers.new("smooth", "LAPLACIANSMOOTH")
        smooth.lambda_factor = 1.0
        smooth.iterations = smooth_iterations
        smooth.use_volume_preserve = True
        smooth.use_normalized = True
        bpy.ops.object.modifier_apply(modifier=smooth.name)
    result = drop_small_islands(obj, keep_fraction=keep_fraction)
    if voxel <= 0:
        # No remesh: close the open ends Trellis leaves on trunks and branches.
        mesh = bmesh.new()
        mesh.from_mesh(obj.data)
        bmesh.ops.holes_fill(mesh, edges=[e for e in mesh.edges if e.is_boundary], sides=16)
        bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
        mesh.to_mesh(obj.data)
        mesh.free()
    return result


def trunk_from_wood(wood, canopy, sides=8, step=0.5):
    """
    Replaces the wood with a tapered tube that follows it: the wood's points are sliced every
    `step` metres from the ground to a metre inside the canopy, and each ring sits on its slice's
    centre with its slice's median radius. Decimating Trellis2's own trunk to a few hundred
    triangles turns it into spikes; this is the trunk STYLE.md describes (slender, slightly
    curved, clearly visible) at about 16 triangles a metre. Branches inside the canopy are not
    seen at RTS distance and are dropped.
    """
    points = np.array([wood.matrix_world @ v.co for v in wood.data.vertices])
    canopy_points = np.array([canopy.matrix_world @ v.co for v in canopy.data.vertices])
    bottom = points[:, 2].min()
    top = canopy_points[:, 2].min() + 1.0
    rings = []
    z = bottom
    while z <= top:
        band = points[np.abs(points[:, 2] - z) < step * 0.75]
        if len(band) >= 6:
            # The trunk is the densest cluster in the slice, not every branch that crosses it.
            centre = np.median(band[:, :2], axis=0)
            radius = np.median(np.linalg.norm(band[:, :2] - centre, axis=1))
            if radius > 1.2:
                close = band[np.linalg.norm(band[:, :2] - centre, axis=1) < 1.0]
                if len(close) >= 6:
                    centre = close[:, :2].mean(0)
                    radius = np.median(np.linalg.norm(close[:, :2] - centre, axis=1))
            rings.append((z, centre, float(np.clip(radius, 0.12, 0.6))))
        z += step
    if len(rings) < 2:
        return None

    # Radii never grow going up: a trunk tapers.
    for i in range(1, len(rings)):
        z, centre, radius = rings[i]
        rings[i] = (z, centre, min(radius, rings[i - 1][2]))

    mesh = bpy.data.meshes.new("trunk")
    verts = []
    faces = []
    for z, centre, radius in rings:
        for k in range(sides):
            angle = 2 * math.pi * k / sides
            verts.append((centre[0] + math.cos(angle) * radius, centre[1] + math.sin(angle) * radius, z))
    for r in range(len(rings) - 1):
        for k in range(sides):
            a = r * sides + k
            b = r * sides + (k + 1) % sides
            faces.append((a, b, b + sides, a + sides))
    faces.append(tuple(reversed(range(sides))))
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    trunk = bpy.data.objects.new("trunk", mesh)
    bpy.context.scene.collection.objects.link(trunk)
    bpy.data.objects.remove(wood, do_unlink=True)
    return trunk


def decimate_to(obj, target):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    for _ in range(6):
        current = triangle_count(obj)
        if current <= target:
            break
        modifier = obj.modifiers.new("decimate", "DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = max(0.0005, target / current * 0.97)
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        clean(obj, 0.001)
        print("DECIMATE %s %d -> %d (target %d)" % (obj.name, current, triangle_count(obj), target))


def join_meshes():
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return obj


def main():
    args = parse_args()
    report = {"source": args.source, "category": args.category}

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=args.source)
    for o in list(bpy.context.scene.objects):
        if o.type != "MESH":
            bpy.data.objects.remove(o, do_unlink=True)
    raw = join_meshes()
    raw.name = "raw"
    report["raw_tris"] = triangle_count(raw)

    # Scale first, so every distance below is in metres.
    height = raw.dimensions.z
    factor = args.height / max(height, 1e-6)
    raw.scale = (factor, factor, factor)
    bpy.ops.object.transform_apply(scale=True)
    clean(raw, 0.002)

    # The low mesh is a closed hull round the raw one, smoothed and decimated. Wood gets its own,
    # finer hull and a fixed share of the triangles, so a trunk survives beside a canopy.
    low = raw.copy()
    low.data = raw.data.copy()
    low.name = os.path.basename(args.out)
    bpy.context.scene.collection.objects.link(low)
    wood = split_wood(low)
    report["wood_split"] = wood is not None

    islands, dropped = hull(low, args.voxel * args.height, args.smooth, 0.002)
    report["hull_islands"] = islands
    report["hull_islands_dropped"] = dropped
    report["hull_tris"] = triangle_count(low)
    if wood is not None:
        wood = trunk_from_wood(wood, low)
    if wood is not None:
        report["wood_tris"] = triangle_count(wood)
        decimate_to(low, max(args.tris // 2, args.tris - triangle_count(wood)))
        bpy.ops.object.select_all(action="DESELECT")
        wood.select_set(True)
        low.select_set(True)
        bpy.context.view_layer.objects.active = low
        bpy.ops.object.join()
        low = bpy.context.view_layer.objects.active
        low.name = os.path.basename(args.out)
        # Thin wood tubes resist collapse; the whole tree still has to fit the budget.
        decimate_to(low, args.tris)
    else:
        decimate_to(low, args.tris)
    report["tris"] = triangle_count(low)
    report["non_manifold_edges"] = non_manifold_edges(low)

    # Soft forms: smooth shading everywhere (STYLE.md: rounded, not faceted).
    for polygon in low.data.polygons:
        polygon.use_smooth = True

    # Unwrap and bake the raw base colour onto the low mesh.
    bpy.ops.object.select_all(action="DESELECT")
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    for layer in list(low.data.uv_layers):
        low.data.uv_layers.remove(layer)
    low.data.uv_layers.new(name="UVMap")
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")

    image = bpy.data.images.new(low.name + "_albedo", args.tex, args.tex, alpha=False)
    # Magenta marks texels no island covers, so the statistics and the fill below can skip them.
    image.generated_color = (1.0, 0.0, 1.0, 1.0)
    material = bpy.data.materials.new(low.name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    principled = nodes.get("Principled BSDF")
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    nodes.active = texture
    low.data.materials.clear()
    low.data.materials.append(material)

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 4
    scene.render.bake.use_selected_to_active = True
    scene.render.bake.cage_extrusion = 0.02 * args.height
    scene.render.bake.max_ray_distance = 0.06 * args.height
    scene.render.bake.margin = 8
    raw.hide_render = False
    bpy.ops.object.select_all(action="DESELECT")
    raw.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"})

    # Palette pull, in sRGB where the swatches are defined.
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(-1, 4)
    rgb = linear_to_srgb(pixels[:, :3]) if image.colorspace_settings.name != "sRGB" else pixels[:, :3]
    names = list(SWATCHES[args.category])
    swatches = np.stack([hex_to_rgb(SWATCHES[args.category][n]) for n in names])
    report["albedo_value"] = args.albedo_value
    if args.category == "plant":
        mid = names.index("Leaf Mid")
        swatches[mid] = swatches[mid] + (hex_to_rgb(SWATCHES["plant"]["Canopy Teal"]) - swatches[mid]) * args.foliage_cool
        report["foliage_cool"] = args.foliage_cool
    unused = (rgb[:, 0] > 0.98) & (rgb[:, 1] < 0.02) & (rgb[:, 2] > 0.98)
    report["uv_coverage"] = round(float(1.0 - unused.mean()), 3)
    distance = ((rgb[:, None, :] - swatches[None, :, :]) ** 2).sum(-1)
    nearest = distance.argmin(1)
    pulled = rgb.copy()
    keep = 1.0 - np.clip(args.pull, 0.0, 1.0)
    for i in range(len(names)):
        group = (nearest == i) & ~((rgb[:, 0] > 0.98) & (rgb[:, 1] < 0.02) & (rgb[:, 2] > 0.98))
        if group.any():
            mean = rgb[group].mean(0)
            pulled[group] = swatches[i] * args.albedo_value + (rgb[group] - mean) * keep
    pulled = np.clip(pulled, 0.0, 1.0)
    used = ~unused
    report["swatch_share"] = {names[i]: round(float((nearest[used] == i).mean()), 3) for i in range(len(names))}
    report["mean_distance_before"] = round(float(np.sqrt(distance.min(1))[used].mean()), 4)
    report["mean_colour"] = "#%02X%02X%02X" % tuple(int(round(c * 255)) for c in rgb[used].mean(0))
    # Unused texels take the mean colour, so mip levels never bleed magenta into the model.
    pulled[unused] = pulled[used].mean(0)
    out_rgb = pulled if image.colorspace_settings.name == "sRGB" else srgb_to_linear(pulled)
    pixels[:, :3] = out_rgb
    image.pixels[:] = pixels.ravel()
    image.filepath_raw = args.out + "_albedo.png"
    image.file_format = "PNG"
    image.save()

    texture.image = image
    material.node_tree.links.new(texture.outputs["Color"], principled.inputs["Base Color"])
    principled.inputs["Roughness"].default_value = 1.0
    principled.inputs["Metallic"].default_value = 0.0

    # Pivot at the base centre, then drop the raw mesh.
    bpy.data.objects.remove(raw, do_unlink=True)
    coords = np.array([v.co for v in low.data.vertices])
    base = np.array([(coords[:, 0].min() + coords[:, 0].max()) / 2,
                     (coords[:, 1].min() + coords[:, 1].max()) / 2,
                     coords[:, 2].min()])
    for v in low.data.vertices:
        v.co.x -= base[0]
        v.co.y -= base[1]
        v.co.z -= base[2]
    low.location = (0, 0, 0)
    report["dimensions_m"] = [round(d, 3) for d in low.dimensions]

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    low.select_set(True)
    bpy.ops.export_scene.gltf(filepath=args.out + ".glb", export_format="GLB", use_selection=True,
                              export_yup=True, export_apply=True)
    bpy.ops.export_scene.fbx(filepath=args.out + ".fbx", use_selection=True, apply_scale_options="FBX_SCALE_UNITS",
                             axis_forward="-Z", axis_up="Y", bake_space_transform=True, path_mode="STRIP",
                             mesh_smooth_type="FACE")
    with open(args.out + "_report.json", "w") as handle:
        json.dump(report, handle, indent=2)
    print("CLEAN_EXPORT " + json.dumps(report))


main()
