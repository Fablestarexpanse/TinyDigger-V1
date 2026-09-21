"""
Trees as leaf cards and branch tubes, built from Trellis2's own shape (no voxel remesh: only the
terrain is cells). Ronan chose leaf cards on 2026-09-21: Trellis2 canopies are thin leaf shells
that shatter when decimated to game budgets (tried at 1.5k, 4k and 15k faces).

    cards, info = leaf_cards(parts["foliage"])    square cards on the leaf surface, atlas UVs
    tubes, info = branch_tubes(parts["wood"])     trunk and branches as tapered tubes

The leaf atlas comes from leaf_atlas.py (Krea2 sprites, ComfyUI workflow TinyDiggers_Sprite_Krea2).
"""
import math

import bpy
import numpy as np

from td_pipeline import tris


def _kmeans(points, weights, count, iterations=16, seed=7):
    rng = np.random.default_rng(seed)
    centres = points[rng.choice(len(points), count, replace=False, p=weights / weights.sum())].copy()
    labels = np.zeros(len(points), dtype=np.int32)
    for _ in range(iterations):
        for start in range(0, len(points), 20000):
            chunk = points[start:start + 20000]
            labels[start:start + 20000] = ((chunk[:, None, :] - centres[None]) ** 2).sum(-1).argmin(1)
        for k in range(count):
            member = labels == k
            if member.any():
                centres[k] = np.average(points[member], axis=0, weights=weights[member])
    return centres, labels


def leaf_cards(foliage, count=220, scale=1.35, sphere=0.65, seed=7, cells=2, min_half=0.35):
    """
    Replaces Trellis2's leaf shells with `count` square cards carrying the leaf atlas.

    The leaf surface is clustered (k-means on face centres, weighted by area). Each cluster gets
    a card on its best-fit plane (the two widest principal axes), sized to cover the cluster,
    turned to face out of the canopy, spun at random, with a random atlas cell. Normals lean
    `sphere` of the way toward "out from the canopy centre", so the canopy shades as soft clumps
    (warm on the sun side, cool on the shade side) rather than as flat cards.

    Vertex colour R holds the wind weight (about 0.3 near the trunk, 1 at the outer tips), for a
    sway shader.
    """
    mesh = foliage.data
    centres = np.zeros(len(mesh.polygons) * 3, dtype=np.float32)
    areas = np.zeros(len(mesh.polygons), dtype=np.float32)
    mesh.polygons.foreach_get("center", centres)
    mesh.polygons.foreach_get("area", areas)
    centres = centres.reshape(-1, 3)
    keep = areas > 0
    centres, areas = centres[keep], areas[keep]
    rng = np.random.default_rng(seed)
    sample = rng.choice(len(centres), min(60000, len(centres)), replace=False, p=areas / areas.sum())
    points, weights = centres[sample], areas[sample]
    _, labels = _kmeans(points, weights, count, seed=seed)

    canopy_centre = np.average(points, axis=0, weights=weights)
    radius = float(np.percentile(np.linalg.norm(points - canopy_centre, axis=1), 90))
    trunk_axis = canopy_centre[:2]

    verts, faces, uvs, normals, wind = [], [], [], [], []
    cell = 1.0 / cells
    for k in range(count):
        member = points[labels == k]
        if len(member) < 8:
            continue
        centre = member.mean(0)
        offsets = member - centre
        _, _, axes = np.linalg.svd(offsets, full_matrices=False)
        u, v, n = axes[0], axes[1], axes[2]
        if np.dot(n, centre - canopy_centre) < 0:
            n, v = -n, -v
        half = max(np.percentile(np.abs(offsets @ u), 90), np.percentile(np.abs(offsets @ v), 90)) * scale
        half = max(half, min_half)
        spin = rng.uniform(0, 2 * math.pi)
        cu = math.cos(spin) * u + math.sin(spin) * v
        cv = -math.sin(spin) * u + math.cos(spin) * v
        pick = int(rng.integers(0, cells * cells))
        x0, y0 = (pick % cells) * cell, 1.0 - (pick // cells + 1) * cell
        base = len(verts)
        for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = centre + cu * a * half + cv * b * half
            verts.append(tuple(float(x) for x in p))
            uvs.append((x0 + (a + 1) * 0.5 * cell, y0 + (b + 1) * 0.5 * cell))
            out = p - canopy_centre
            out = out / (np.linalg.norm(out) + 1e-6)
            blended = n * (1 - sphere) + out * sphere
            normals.append(tuple(float(x) for x in blended / (np.linalg.norm(blended) + 1e-6)))
            reach = np.linalg.norm(p[:2] - trunk_axis) / max(radius, 1e-3)
            wind.append(float(np.clip(0.3 + 0.7 * reach, 0.0, 1.0)))
        faces.append((base, base + 1, base + 2, base + 3))

    cards_mesh = bpy.data.meshes.new("leaf_cards")
    cards_mesh.from_pydata(verts, [], faces)
    cards_mesh.update()
    uv = cards_mesh.uv_layers.new(name="UVMap")
    for loop in cards_mesh.loops:
        uv.data[loop.index].uv = uvs[loop.vertex_index]
    colours = cards_mesh.color_attributes.new("wind", "BYTE_COLOR", "POINT")
    for i, w in enumerate(wind):
        colours.data[i].color = (w, 0.0, 0.0, 1.0)
    for polygon in cards_mesh.polygons:
        polygon.use_smooth = True
    cards_mesh.normals_split_custom_set_from_vertices(normals)
    cards = bpy.data.objects.new("leaf_cards", cards_mesh)
    bpy.context.scene.collection.objects.link(cards)
    return cards, {"cards": len(faces), "canopy_radius_m": round(radius, 2)}


def branch_tubes(wood, sides=6, step=0.4, link=0.8, min_rings=3, root_scale=1.3, grab=0.55):
    """
    Trunk and branches as tapered tubes along Trellis2's own wood, without remeshing. The wood's
    points are sliced by height, each slice is clustered (greedy, `grab` m), clusters are chained
    upward to the nearest cluster in the next slice, and each chain becomes a tube whose rings sit
    on the cluster centres with the clusters' median radii, never widening going up. A chain that
    starts above the ground starts inside its nearest lower ring, so every branch is joined on.
    """
    points = np.array([v.co[:] for v in wood.data.vertices], dtype=np.float32)
    bottom, top = float(points[:, 2].min()), float(points[:, 2].max())
    slices = []
    z = bottom
    while z <= top:
        remaining = points[np.abs(points[:, 2] - z) < step * 0.6][:, :2].copy()
        clusters = []
        while len(remaining) >= 6:
            near = np.linalg.norm(remaining - remaining[0], axis=1) < grab
            centre = remaining[near].mean(0)
            near = np.linalg.norm(remaining - centre, axis=1) < grab
            group = remaining[near]
            remaining = remaining[~near]
            if len(group) >= 6:
                centre = group.mean(0)
                radius = float(np.median(np.linalg.norm(group - centre, axis=1)))
                clusters.append((np.array([centre[0], centre[1], z]), radius))
        slices.append(clusters)
        z += step

    chains, open_chains = [], []
    for clusters in slices:
        used, still_open = set(), []
        for chain in open_chains:
            last = chain[-1][0]
            best, best_d = None, link
            for i, (c, _) in enumerate(clusters):
                d = float(np.linalg.norm(c[:2] - last[:2]))
                if i not in used and d < best_d:
                    best, best_d = i, d
            if best is None:
                chains.append(chain)
            else:
                used.add(best)
                chain.append(clusters[best])
                still_open.append(chain)
        for i, cl in enumerate(clusters):
            if i not in used:
                still_open.append([cl])
        open_chains = still_open
    chains.extend(open_chains)
    chains = sorted((c for c in chains if len(c) >= min_rings), key=lambda c: c[0][0][2])

    verts, faces, placed = [], [], []
    for chain in chains:
        rings = [[c.copy(), float(np.clip(r, 0.05, 0.45))] for c, r in chain]
        for i in range(1, len(rings)):
            rings[i][1] = min(rings[i][1], rings[i - 1][1])
        if rings[0][0][2] <= bottom + step:
            rings[0][1] *= root_scale
        else:
            below = [p for p in placed if p[0][2] <= rings[0][0][2]]
            if below:
                nearest = min(below, key=lambda p: float(np.linalg.norm(p[0] - rings[0][0])))
                rings.insert(0, [nearest[0].copy(), min(nearest[1], rings[0][1] * 1.2)])
        placed.extend(rings)
        base = len(verts)
        for c, r in rings:
            for k in range(sides):
                a = 2 * math.pi * k / sides
                verts.append((float(c[0] + math.cos(a) * r), float(c[1] + math.sin(a) * r), float(c[2])))
        for ri in range(len(rings) - 1):
            for k in range(sides):
                a0 = base + ri * sides + k
                b0 = base + ri * sides + (k + 1) % sides
                faces.append((a0, b0, b0 + sides, a0 + sides))
        faces.append(tuple(base + (len(rings) - 1) * sides + k for k in range(sides)))

    tube_mesh = bpy.data.meshes.new("branches")
    tube_mesh.from_pydata(verts, [], faces)
    tube_mesh.update()
    for polygon in tube_mesh.polygons:
        polygon.use_smooth = True
    tubes = bpy.data.objects.new("branches", tube_mesh)
    bpy.context.scene.collection.objects.link(tubes)
    return tubes, {"chains": len(chains), "tris": tris(tubes)}


def finish_tree(cards, tubes, atlas_path, out, name, bark_rgb=(0.35, 0.20, 0.13)):
    """
    Joins cards and tubes into one prop with two materials, 'leaves' (the atlas, alpha-clipped;
    Unity renders it double-sided) and 'bark' (flat bark albedo), puts the pivot at the base
    centre, and exports FBX + GLB with the custom normals and the wind vertex colours.
    """
    import os
    from mathutils import Matrix, Vector
    from td_pipeline import run, select_only

    atlas = bpy.data.images.load(atlas_path, check_existing=True)
    leaves = bpy.data.materials.new("leaves")
    leaves.use_nodes = True
    nodes = leaves.node_tree.nodes
    bsdf = nodes.get("Principled BSDF")
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = atlas
    leaves.node_tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    leaves.node_tree.links.new(texture.outputs["Alpha"], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = 1.0
    bark = bpy.data.materials.new("bark")
    bark.use_nodes = True
    bark_bsdf = bark.node_tree.nodes.get("Principled BSDF")
    bark_bsdf.inputs["Base Color"].default_value = (*[c ** 2.2 for c in bark_rgb], 1.0)
    bark_bsdf.inputs["Roughness"].default_value = 1.0

    cards.data.materials.clear()
    cards.data.materials.append(leaves)
    tubes.data.materials.clear()
    tubes.data.materials.append(bark)
    if "wind" not in tubes.data.color_attributes:
        colours = tubes.data.color_attributes.new("wind", "BYTE_COLOR", "POINT")
        top = max(v.co.z for v in tubes.data.vertices) or 1.0
        for i, v in enumerate(tubes.data.vertices):
            w = max(0.0, v.co.z / top) ** 2 * 0.3
            colours.data[i].color = (w, 0.0, 0.0, 1.0)

    select_only(cards, tubes)
    run(bpy.ops.object.join)
    prop = bpy.context.view_layer.objects.active
    prop.name = name
    xs = [v.co.x for v in prop.data.vertices]
    ys = [v.co.y for v in prop.data.vertices]
    zs = [v.co.z for v in prop.data.vertices]
    base = Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, min(zs)))
    prop.data.transform(Matrix.Translation(-base))
    prop.location = (0, 0, 0)

    os.makedirs(os.path.dirname(out), exist_ok=True)
    select_only(prop)
    run(bpy.ops.export_scene.gltf, filepath=out + ".glb", export_format="GLB", use_selection=True,
        export_yup=True, export_apply=True)
    run(bpy.ops.export_scene.fbx, filepath=out + ".fbx", use_selection=True,
        apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
        bake_space_transform=True, path_mode="STRIP", mesh_smooth_type="OFF", colors_type="LINEAR")
    return prop, {"tris": tris(prop), "dimensions_m": [round(d, 2) for d in prop.dimensions]}
