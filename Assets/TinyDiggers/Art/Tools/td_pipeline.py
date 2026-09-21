"""
TinyDiggers prop pipeline: the stages that take a Trellis2 mesh to a game prop, as functions, so
the same code runs headless (clean_export.py) and inside a live Blender session, where each stage
is looked at before the next one runs (Ronan, 2026-09-21: "bring Trellis items into Blender to
clean, improve, convert them").

**Props keep Trellis2's own shape** (Ronan, 2026-09-21: "trees and other props don't need to be
voxels, only our terrain"). No voxel remesh and no blobbing. The triangle budget and the
texture are reached inside ComfyUI instead: the TinyDiggers_Trellis2 workflow's own DecimateMesh
(GPU QEM, after its distance-field cleanup) cuts to the budget, and BakeTextureFromVoxel bakes the
colour onto that low mesh from Trellis2's colour field, back-projected from the dense mesh. That
gives exact colours, with no ray-bake flecks. Blender then does only what Blender is for:

    prop    = load_raw(path, height)          scaled to metres; transforms baked into the mesh
    report  = inspect(prop)                   triangles, islands, non-manifold, dimensions
    tidy(prop)                                weld, drop floaters and loose bits, fix normals
    palette_by_part(prop, category)           albedo onto STYLE swatches, per part (wood/foliage)
    finish(prop, out)                         pivot at base, matte material, export FBX + GLB
    save_blend(out)                           the .blend stays as the asset's source file

The voxel functions below (canopy_hull, wood_hull, trunk_tube) are kept only as a record of
Phase 1; they are not used for props.

Geometry is Blender Z-up; the exporters turn it into +Y up for Unity. 1 unit = 1 m.
"""
import json
import math
import os

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

# STYLE.md section 1. These are *lit* colours, sampled from rendered references; the albedo that
# reads as them under our sun is ALBEDO_VALUE darker (measured on tree_a, Phase 1 loop 3).
SWATCH = {
    "Meadow Sun": "#86B83A",
    "Leaf Mid": "#4F9A3E",
    "Canopy Teal": "#5E9F8C",
    "Bark": "#9C5A3C",
    "Stone": "#A3A6A4",
    "Path Earth": "#A65A30",
    "Machine Yellow": "#E8B33A",
}
ALBEDO_VALUE = 0.72

# Per-part albedo targets. Foliage leans 30% from Leaf Mid toward Canopy Teal (tree_a loop 5: our
# warm sun reads a pure Leaf Mid albedo as lime). Wood is darker still (loop 5: Bark x 0.72 read as
# saturated orange).
FOLIAGE = [("Leaf Mid", 0.30, "Canopy Teal", 1.0), ("Meadow Sun", 0.0, None, 1.0)]
WOOD = [("Bark", 0.0, None, 0.78)]


def hex_rgb(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])


def target_colour(entry):
    name, mix, other, value = entry
    colour = hex_rgb(SWATCH[name])
    if other:
        colour = colour + (hex_rgb(SWATCH[other]) - colour) * mix
    return colour * ALBEDO_VALUE * value


def tris(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def _context():
    """
    A window and 3D view for operators. The MCP bridge runs code with no window context (the
    screen is None), where many operators fail their poll or act on the GUI's selection instead;
    headless Blender has no windows and needs no override.
    """
    wm = bpy.context.window_manager
    if not wm or not wm.windows:
        return {}
    window = wm.windows[0]
    area = next((a for a in window.screen.areas if a.type == "VIEW_3D"), None)
    override = {"window": window, "screen": window.screen}
    if area:
        override["area"] = area
        override["region"] = next((r for r in area.regions if r.type == "WINDOW"), None)
    return override


def run(operator, *args, **kwargs):
    with bpy.context.temp_override(**_context()):
        return operator(*args, **kwargs)


def select_only(*objects):
    run(bpy.ops.object.select_all, action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def apply_modifier(obj, kind, **settings):
    """Applies one modifier by evaluating it, which needs no operator context."""
    for existing in list(obj.modifiers):
        obj.modifiers.remove(existing)
    modifier = obj.modifiers.new(kind.lower(), kind)
    for key, value in settings.items():
        setattr(modifier, key, value)
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=True,
                                           depsgraph=bpy.context.evaluated_depsgraph_get())
    obj.modifiers.remove(modifier)
    old = obj.data
    materials = list(old.materials)
    obj.data = mesh
    if not mesh.materials:
        for m in materials:
            mesh.materials.append(m)
    if old.users == 0:
        bpy.data.meshes.remove(old)


def clean(obj, merge=0.002):
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    bmesh.ops.remove_doubles(mesh, verts=mesh.verts, dist=merge)
    bmesh.ops.dissolve_degenerate(mesh, edges=mesh.edges, dist=merge * 0.1)
    bmesh.ops.delete(mesh, geom=[v for v in mesh.verts if not v.link_faces], context="VERTS")
    bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
    mesh.to_mesh(obj.data)
    mesh.free()


def islands(obj):
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    mesh.faces.ensure_lookup_table()
    seen, groups = set(), []
    for face in mesh.faces:
        if face.index in seen:
            continue
        stack, group = [face], []
        seen.add(face.index)
        while stack:
            current = stack.pop()
            group.append(current.index)
            for edge in current.edges:
                for other in edge.link_faces:
                    if other.index not in seen:
                        seen.add(other.index)
                        stack.append(other)
        groups.append(group)
    mesh.free()
    return groups


def keep_large_islands(obj, fraction):
    groups = islands(obj)
    total = sum(len(g) for g in groups)
    doomed = {i for g in groups if len(g) < total * fraction for i in g}
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    mesh.faces.ensure_lookup_table()
    bmesh.ops.delete(mesh, geom=[mesh.faces[i] for i in doomed], context="FACES")
    mesh.to_mesh(obj.data)
    mesh.free()
    return len(groups), len(groups) - sum(1 for g in groups if len(g) >= total * fraction)


def non_manifold(obj):
    mesh = bmesh.new()
    mesh.from_mesh(obj.data)
    count = sum(1 for e in mesh.edges if not e.is_manifold)
    mesh.free()
    return count


# --- stages -------------------------------------------------------------------------------------

def load_raw(path, height):
    """Imports the Trellis2 GLB as one object named 'raw', scaled so it is `height` metres tall."""
    before = set(bpy.data.objects)
    run(bpy.ops.import_scene.gltf, filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if o.type != "MESH":
            bpy.data.objects.remove(o, do_unlink=True)
    meshes = [o for o in new if o.name in bpy.data.objects and o.type == "MESH"]
    select_only(*meshes)
    if len(meshes) > 1:
        run(bpy.ops.object.join)
    raw = bpy.context.view_layer.objects.active
    raw.name = "raw"
    # Transforms are baked into the mesh directly, not with transform_apply: in a live session
    # that operator acts on the GUI's context and silently left a x9 scale on the object, which made
    # every metre below (voxel sizes, radii) wrong by that factor.
    raw.data.transform(raw.matrix_world)
    raw.matrix_world = Matrix.Identity(4)
    factor = height / max(raw.dimensions.z, 1e-6)
    raw.data.transform(Matrix.Scale(factor, 4))
    raw.data.update()
    clean(raw)
    return raw


def base_colour_image(obj):
    material = obj.data.materials[0] if obj.data.materials else None
    if not material or not material.node_tree:
        return None
    principled = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if principled is None or not principled.inputs["Base Color"].links:
        return None
    return getattr(principled.inputs["Base Color"].links[0].from_node, "image", None)


def inspect(obj):
    return {
        "tris": tris(obj),
        "islands": len(islands(obj)),
        "non_manifold_edges": non_manifold(obj),
        "dimensions_m": [round(d, 3) for d in obj.dimensions],
    }


def tidy(obj, merge=0.002, floater=0.002):
    """Welds seams, drops floaters smaller than `floater` of the faces, recomputes normals."""
    clean(obj, merge)
    total, dropped = keep_large_islands(obj, floater)
    clean(obj, merge)
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    return {"islands": total, "islands_dropped": dropped}


# Albedo groups per category: (targets, test) where test picks the texels the group owns.
PLANT_PARTS = [("wood", WOOD, lambda rgb: rgb[:, 0] > rgb[:, 1] * 1.05),
               ("foliage", FOLIAGE, lambda rgb: rgb[:, 0] <= rgb[:, 1] * 1.05)]


def palette_by_part(image, parts=PLANT_PARTS, keep=0.5):
    """
    Moves each part's texels onto that part's swatches: texels are first split by part (wood is
    redder than green), then grouped by nearest swatch within the part, and each group's mean is
    moved onto its target, keeping `keep` of the painted variation. A branch texel can therefore
    never be pushed toward a leaf colour, or a leaf toward bark.
    """
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(-1, 4)
    rgb = pixels[:, :3].copy()
    out = rgb.copy()
    stats = {}
    for name, targets, test in parts:
        mask = test(rgb)
        if not mask.any():
            continue
        colours = np.stack([target_colour(t) for t in targets])
        values = rgb[mask]
        nearest = ((values[:, None, :] - (colours / ALBEDO_VALUE)[None]) ** 2).sum(-1).argmin(1)
        result = values.copy()
        for i in range(len(targets)):
            group = nearest == i
            if group.any():
                result[group] = colours[i] + (values[group] - values[group].mean(0)) * keep
        out[mask] = result
        stats[name] = {"share": round(float(mask.mean()), 3),
                       "mean_before": "#%02X%02X%02X" % tuple(int(round(c * 255)) for c in values.mean(0))}
    pixels[:, :3] = np.clip(out, 0.0, 1.0)
    image.pixels[:] = pixels.ravel()
    return stats


def finish_prop(prop, image, out, name):
    """Pivot at the base centre, one matte material with the palette-corrected albedo, exports."""
    prop.name = name
    coords = np.array([v.co for v in prop.data.vertices])
    base = Vector(((coords[:, 0].min() + coords[:, 0].max()) / 2,
                   (coords[:, 1].min() + coords[:, 1].max()) / 2, coords[:, 2].min()))
    prop.data.transform(Matrix.Translation(-base))
    prop.location = (0, 0, 0)

    os.makedirs(os.path.dirname(out), exist_ok=True)
    image.filepath_raw = out + "_albedo.png"
    image.file_format = "PNG"
    image.save()
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    principled = nodes.get("Principled BSDF")
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    material.node_tree.links.new(texture.outputs["Color"], principled.inputs["Base Color"])
    principled.inputs["Roughness"].default_value = 1.0
    principled.inputs["Metallic"].default_value = 0.0
    prop.data.materials.clear()
    prop.data.materials.append(material)

    select_only(prop)
    run(bpy.ops.export_scene.gltf, filepath=out + ".glb", export_format="GLB", use_selection=True,
        export_yup=True, export_apply=True)
    run(bpy.ops.export_scene.fbx, filepath=out + ".fbx", use_selection=True,
        apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
        bake_space_transform=True, path_mode="STRIP", mesh_smooth_type="OFF")
    return prop


def split_parts(raw):
    """
    Copies raw into 'foliage' and 'wood' by the base colour under each face (wood: red above
    green). The raw object is kept: each part is baked from its own source later, which is what
    keeps branch colour out of the canopy.
    """
    image = base_colour_image(raw)
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)
    mesh = bmesh.new()
    mesh.from_mesh(raw.data)
    uv = mesh.loops.layers.uv.active
    wood = set()
    for face in mesh.faces:
        u = sum(l[uv].uv.x for l in face.loops) / len(face.loops)
        v = sum(l[uv].uv.y for l in face.loops) / len(face.loops)
        px = pixels[min(height - 1, max(0, int(v * height))), min(width - 1, max(0, int((u % 1.0) * width)))]
        if px[0] > px[1] * 1.05:
            wood.add(face.index)
    mesh.free()

    parts = {}
    for name, keep_wood in (("foliage", False), ("wood", True)):
        part = raw.copy()
        part.data = raw.data.copy()
        part.name = name
        bpy.context.scene.collection.objects.link(part)
        mesh = bmesh.new()
        mesh.from_mesh(part.data)
        mesh.faces.ensure_lookup_table()
        doomed = [f for f in mesh.faces if (f.index in wood) != keep_wood]
        bmesh.ops.delete(mesh, geom=doomed, context="FACES")
        mesh.to_mesh(part.data)
        mesh.free()
        parts[name] = part
    return parts


def canopy_hull(foliage, height, voxel=0.02, smooth=12, thickness=0.06):
    """
    Fuses a canopy of loose leaf shells into a few soft closed clumps. The leaves are single-sided
    cards, so they are solidified first: a voxel remesh needs volume to fill.
    """
    canopy = foliage.copy()
    canopy.data = foliage.data.copy()
    canopy.name = "canopy"
    bpy.context.scene.collection.objects.link(canopy)
    canopy.data.materials.clear()
    apply_modifier(canopy, "SOLIDIFY", thickness=thickness, offset=0.0)
    apply_modifier(canopy, "REMESH", mode="VOXEL", voxel_size=voxel * height, adaptivity=0.0)
    apply_modifier(canopy, "LAPLACIANSMOOTH", lambda_factor=1.0, iterations=smooth,
                   use_volume_preserve=True, use_normalized=True)
    keep_large_islands(canopy, 0.002)
    return canopy


def wood_hull(wood, target=450, thickness=0.08, voxel=0.05, smooth=4):
    """
    Keeps Trellis2's own trunk and forks. Its wood is open ribbons, which neither a voxel remesh
    (no volume to fill) nor collapse decimation (stalls near 10k triangles) can use; solidified
    first, it remeshes into one watertight branching trunk that decimates cleanly.
    """
    hull = wood.copy()
    hull.data = wood.data.copy()
    hull.name = "trunk"
    bpy.context.scene.collection.objects.link(hull)
    hull.data.materials.clear()
    keep_large_islands(hull, 0.01)
    apply_modifier(hull, "SOLIDIFY", thickness=thickness, offset=0.0)
    apply_modifier(hull, "REMESH", mode="VOXEL", voxel_size=voxel, adaptivity=0.0)
    apply_modifier(hull, "LAPLACIANSMOOTH", lambda_factor=1.0, iterations=smooth,
                   use_volume_preserve=True, use_normalized=True)
    keep_large_islands(hull, 0.02)
    decimate(hull, target)
    for polygon in hull.data.polygons:
        polygon.use_smooth = True
    return hull


def trunk_tube(wood, canopy, sides=8, step=0.5, darken_top=True):
    """
    A tapered tube through the wood: sliced every `step` m from the ground to 1 m inside the
    canopy, each ring on its slice's centre with its slice's median radius, never widening going
    up. Decimating Trellis2's own trunk to a few hundred triangles turns it into spikes.
    """
    points = np.array([wood.matrix_world @ v.co for v in wood.data.vertices])
    canopy_points = np.array([canopy.matrix_world @ v.co for v in canopy.data.vertices])
    top = canopy_points[:, 2].min() + 1.0
    rings, z = [], points[:, 2].min()
    while z <= top:
        band = points[np.abs(points[:, 2] - z) < step * 0.75]
        if len(band) >= 6:
            centre = np.median(band[:, :2], axis=0)
            radius = np.median(np.linalg.norm(band[:, :2] - centre, axis=1))
            if radius > 1.2:
                close = band[np.linalg.norm(band[:, :2] - centre, axis=1) < 1.0]
                if len(close) >= 6:
                    centre = close[:, :2].mean(0)
                    radius = np.median(np.linalg.norm(close[:, :2] - centre, axis=1))
            rings.append([z, centre, float(np.clip(radius, 0.12, 0.6))])
        z += step
    for i in range(1, len(rings)):
        rings[i][2] = min(rings[i][2], rings[i - 1][2])
    # A flare at the foot, so the tree stands on the ground rather than being stuck into it.
    if rings:
        rings[0][2] *= 1.35

    verts, faces = [], []
    for z, centre, radius in rings:
        for k in range(sides):
            a = 2 * math.pi * k / sides
            verts.append((centre[0] + math.cos(a) * radius, centre[1] + math.sin(a) * radius, z))
    for r in range(len(rings) - 1):
        for k in range(sides):
            a, b = r * sides + k, r * sides + (k + 1) % sides
            faces.append((a, b, b + sides, a + sides))
    faces.append(tuple(reversed(range(sides))))
    faces.append(tuple((len(rings) - 1) * sides + k for k in range(sides)))
    mesh = bpy.data.meshes.new("trunk")
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    trunk = bpy.data.objects.new("trunk", mesh)
    bpy.context.scene.collection.objects.link(trunk)
    for polygon in trunk.data.polygons:
        polygon.use_smooth = True
    return trunk


def make_manifold(obj):
    """Removes the pinches smoothing leaves where two clumps nearly touch."""
    before = non_manifold(obj)
    for _ in range(3):
        if non_manifold(obj) == 0:
            break
        mesh = bmesh.new()
        mesh.from_mesh(obj.data)
        bad = {v for e in mesh.edges if not e.is_manifold for v in e.verts}
        bmesh.ops.delete(mesh, geom=list(bad), context="VERTS")
        bmesh.ops.holes_fill(mesh, edges=[e for e in mesh.edges if e.is_boundary], sides=12)
        bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
        mesh.to_mesh(obj.data)
        mesh.free()
        clean(obj, 0.001)
    return before, non_manifold(obj)


def decimate(obj, target):
    for _ in range(6):
        current = tris(obj)
        if current <= target:
            break
        apply_modifier(obj, "DECIMATE", decimate_type="COLLAPSE",
                       ratio=max(0.0005, target / current * 0.97), use_collapse_triangulate=True)
        clean(obj, 0.001)


def fit_budget(parts, total):
    """Everything but the first part keeps its triangles; the first gets what is left."""
    fixed = sum(tris(p) for p in parts[1:])
    decimate(parts[0], max(total // 2, total - fixed))
    for p in parts:
        for polygon in p.data.polygons:
            polygon.use_smooth = True


def soft_normals(canopy, inflate=0.35, iterations=40):
    """
    Gives the canopy the normals of an inflated, heavily smoothed copy of itself, so each clump
    shades as one soft ball: warm on the sun side, cool on the shade side, the two-tone the
    references have. The shape does not change; only how light falls on it does.
    """
    proxy = canopy.copy()
    proxy.data = canopy.data.copy()
    proxy.name = "canopy_normals"
    bpy.context.scene.collection.objects.link(proxy)
    apply_modifier(proxy, "LAPLACIANSMOOTH", lambda_factor=1.0, iterations=iterations,
                   use_volume_preserve=True, use_normalized=True)
    apply_modifier(proxy, "DISPLACE", strength=inflate, mid_level=0.0, direction="NORMAL")
    select_only(canopy)
    transfer = canopy.modifiers.new("soft_normals", "DATA_TRANSFER")
    transfer.object = proxy
    transfer.use_loop_data = True
    transfer.data_types_loops = {"CUSTOM_NORMAL"}
    transfer.loop_mapping = "POLYINTERP_NEAREST"
    run(bpy.ops.object.modifier_apply, modifier=transfer.name)
    bpy.data.objects.remove(proxy, do_unlink=True)


def unwrap_atlas(layout):
    """Unwraps each object into its own band of one UV space: [(obj, u0, u1), ...]."""
    for obj, u0, u1 in layout:
        select_only(obj)
        for layer in list(obj.data.uv_layers):
            obj.data.uv_layers.remove(layer)
        obj.data.uv_layers.new(name="UVMap")
        run(bpy.ops.object.mode_set, mode="EDIT")
        run(bpy.ops.mesh.select_all, action="SELECT")
        run(bpy.ops.uv.smart_project, angle_limit=math.radians(66), island_margin=0.02)
        run(bpy.ops.object.mode_set, mode="OBJECT")
        uv = obj.data.uv_layers.active.data
        for loop in uv:
            loop.uv.x = u0 + loop.uv.x * (u1 - u0)


def bake_parts(pairs, size, name):
    """Bakes each low part from its own raw source into one image: [(low, source), ...]."""
    image = bpy.data.images.new(name, size, size, alpha=False)
    image.generated_color = (1.0, 0.0, 1.0, 1.0)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 4
    bake = scene.render.bake
    bake.use_selected_to_active = True
    bake.use_clear = False
    bake.margin = 6
    for low, source in pairs:
        height = max(low.dimensions)
        bake.cage_extrusion = 0.03 * height
        bake.max_ray_distance = 0.08 * height
        material = bpy.data.materials.new(low.name + "_bake")
        material.use_nodes = True
        node = material.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        material.node_tree.nodes.active = node
        low.data.materials.clear()
        low.data.materials.append(material)
        select_only(low, source)
        bpy.context.view_layer.objects.active = low
        run(bpy.ops.object.bake, type="DIFFUSE", pass_filter={"COLOR"})
    return image


def palette_parts(image, bands, keep=0.5):
    """
    Per band of the atlas, groups texels by their nearest target and moves each group's mean onto
    it, keeping (keep) of the painted variation. Bands are [(u0, u1, targets), ...]. Returns stats.
    """
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)
    rgb = pixels[:, :, :3]
    unused = (rgb[:, :, 0] > 0.98) & (rgb[:, :, 1] < 0.02) & (rgb[:, :, 2] > 0.98)
    stats = {}
    columns = np.arange(width) / width
    for u0, u1, targets in bands:
        band = (columns >= u0) & (columns < u1)
        mask = ~unused & band[None, :]
        if not mask.any():
            continue
        colours = np.stack([target_colour(t) for t in targets])
        values = rgb[mask]
        nearest = ((values[:, None, :] - (colours / ALBEDO_VALUE)[None]) ** 2).sum(-1).argmin(1)
        out = values.copy()
        for i in range(len(targets)):
            group = nearest == i
            if group.any():
                out[group] = colours[i] + (values[group] - values[group].mean(0)) * keep
        rgb[mask] = np.clip(out, 0.0, 1.0)
        stats["%.2f-%.2f" % (u0, u1)] = {targets[i][0]: round(float((nearest == i).mean()), 3) for i in range(len(targets))}
    if (~unused).any():
        rgb[unused] = rgb[~unused].mean(0)
    pixels[:, :, :3] = rgb
    image.pixels[:] = pixels.ravel()
    return stats


def finish(parts, image, out, name):
    """Joins the parts, puts the pivot at the base centre, gives it one matte material, exports."""
    select_only(*parts)
    run(bpy.ops.object.join)
    prop = bpy.context.view_layer.objects.active
    prop.name = name
    coords = np.array([v.co for v in prop.data.vertices])
    base = Vector(((coords[:, 0].min() + coords[:, 0].max()) / 2,
                   (coords[:, 1].min() + coords[:, 1].max()) / 2, coords[:, 2].min()))
    for v in prop.data.vertices:
        v.co -= base
    prop.location = (0, 0, 0)

    image.filepath_raw = out + "_albedo.png"
    image.file_format = "PNG"
    image.save()
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    principled = nodes.get("Principled BSDF")
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    material.node_tree.links.new(texture.outputs["Color"], principled.inputs["Base Color"])
    principled.inputs["Roughness"].default_value = 1.0
    principled.inputs["Metallic"].default_value = 0.0
    prop.data.materials.clear()
    prop.data.materials.append(material)

    os.makedirs(os.path.dirname(out), exist_ok=True)
    select_only(prop)
    run(bpy.ops.export_scene.gltf, filepath=out + ".glb", export_format="GLB", use_selection=True,
                              export_yup=True, export_apply=True)
    run(bpy.ops.export_scene.fbx, filepath=out + ".fbx", use_selection=True, apply_scale_options="FBX_SCALE_UNITS",
                             axis_forward="-Z", axis_up="Y", bake_space_transform=True, path_mode="STRIP",
                             mesh_smooth_type="OFF", use_mesh_modifiers=True)
    return prop


def snapshot(objects, path, size=720):
    """A quick textured preview from the three-quarter view, for looking at a stage."""
    scene = bpy.context.scene
    hidden = {}
    for o in scene.objects:
        hidden[o.name] = o.hide_render
        o.hide_render = o not in objects
    points = np.array([o.matrix_world @ Vector(c) for o in objects for c in o.bound_box])
    centre = Vector(points.mean(0))
    extent = float(np.ptp(points, axis=0).max())
    camera = scene.camera
    if camera is None:
        camera = bpy.data.objects.new("preview_camera", bpy.data.cameras.new("preview_camera"))
        scene.collection.objects.link(camera)
        scene.camera = camera
    camera.location = centre + Vector((extent * 1.3, -extent * 1.3, extent * 0.8))
    camera.rotation_euler = (centre - camera.location).to_track_quat("-Z", "Y").to_euler()
    engine = scene.render.engine
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.filepath = path
    run(bpy.ops.render.render, write_still=True)
    scene.render.engine = engine
    for o in scene.objects:
        o.hide_render = hidden.get(o.name, False)
    return path


def save_blend(out):
    folder = os.path.join(os.path.dirname(os.path.dirname(out)), "Blender~")
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, os.path.basename(out) + ".blend")
    run(bpy.ops.wm.save_as_mainfile, filepath=path, copy=True)
    return path


def report(out, data):
    with open(out + "_report.json", "w") as handle:
        json.dump(data, handle, indent=2)
