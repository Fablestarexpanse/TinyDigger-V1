"""
TinyDiggers dam kit: the modular pieces of the concrete dam that rings the world (Ronan,
2026-09-21: "a cement shell around the world that looks like a huge dam containing our world").

Pieces are modelled straight and Unity bends them onto the circle, so they are built in a
fixed frame:
    X  along the wall; the piece spans -width/2 .. +width/2
    Y  outward (downstream); 0 is the inner face, where the sea laps the wall
    Z  up; 0 is sea level
1 unit = 1 m. Every piece's ends are plain cuts at x = +-width/2, so neighbours meet exactly
once bent.

    build_bay()        12 m: vertical inner wall, crest walkway with parapets, a battered
                       downstream face and one triangular buttress on its +x joint
    build_spillway()   36 m: three gated openings between four piers, a hoist house on each
                       pier, an ogee chute with training walls and a flip-bucket lip, and the
                       water (its own object: sheet down the chute, curtain off the lip)
    build_tower()      the intake tower standing in the sea, with its bridge to the crest;
                       placed on top of a bay
    build_kit(out)     all of them, bevelled, exported (FBX) and saved (.blend)

Materials are slots only; Unity assigns the shaders. Concrete, ConcreteDark (recesses and
slots), Steel (gates and hoists), SpillWater (the water, whose vertex alpha fades the falling
curtain out into the void).
"""
import math
import os
from contextlib import contextmanager

import bmesh
import bpy
from mathutils import Vector

import td_pipeline as td

# The wall's dimensions, metres. Face 20 m (Ronan's "modest" choice): crest 2 m above the sea,
# downstream face down to -18, footing to -20.
BAY_WIDTH = 12.0
CREST = 2.0
PARAPET = 1.1
DECK_DEPTH = 6.0
FACE_BOTTOM = -18.0
FACE_OUT = 12.0          # how far out the downstream face reaches at its foot
FOOTING = -20.0
FOOTING_OUT = 24.0
UNDERSIDE = -24.0
INNER_BOTTOM = -22.0     # the inner face runs below the seabed at the rim
BUTTRESS_OUT = 22.0
BUTTRESS_THICK = 1.6

SPILL_WIDTH = 3 * BAY_WIDTH
WEIR = -2.5              # weir crest: below the sea, so the spillways always run
GATE_BOTTOM = -1.0       # radial gates raised this far: water runs under them
PIER_WIDTH = 2.4
LIP_OUT = 25.0
GANTRY = 8.0             # top of the gate piers and the service bridge across them

BEVEL = 0.12

MATERIALS = ("Concrete", "ConcreteDark", "Steel", "SpillWater")


# --- building blocks -----------------------------------------------------------------------

class Piece:
    """
    Two bmeshes with a material index per face, turned into one object at the end: the body,
    whose edges are bevelled, and the trim (gate skins, rails, slots, arms), which is added after
    the bevel. Bevelling the thin and zero-thickness trim threw long spikes across the scene.
    """

    def __init__(self, name):
        self.name = name
        self.body = bmesh.new()
        self.detail = bmesh.new()
        for mesh in (self.body, self.detail):
            mesh.loops.layers.color.new("Col")
        self.mesh = self.body

    @property
    def colour(self):
        return self.mesh.loops.layers.color["Col"]

    @contextmanager
    def trim(self):
        """Geometry added inside this block is trim: not bevelled."""
        self.mesh = self.detail
        try:
            yield
        finally:
            self.mesh = self.body

    def box(self, x0, x1, y0, y1, z0, z1, material=0):
        verts = [self.mesh.verts.new(v) for v in (
            (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
            (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1))]
        for face in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            self._face([verts[i] for i in face], material)

    def prism(self, x0, x1, profile, material=0):
        """Extrudes a closed (y, z) profile, counter-clockwise seen from +x, from x0 to x1."""
        left = [self.mesh.verts.new((x0, y, z)) for y, z in profile]
        right = [self.mesh.verts.new((x1, y, z)) for y, z in profile]
        self._face(list(reversed(left)), material)
        self._face(right, material)
        n = len(profile)
        for i in range(n):
            j = (i + 1) % n
            self._face([left[i], left[j], right[j], right[i]], material)

    def cylinder_x(self, x0, x1, centre, radius, start, end, segments, bottom, material=0):
        """A part-cylinder about an x axis at (y, z) = centre, closed down to z = bottom."""
        points = []
        for i in range(segments + 1):
            a = math.radians(start + (end - start) * i / segments)
            points.append((centre[0] + math.cos(a) * radius, centre[1] + math.sin(a) * radius))
        profile = [(points[0][0], bottom)] + points + [(points[-1][0], bottom)]
        self.prism(x0, x1, profile, material)

    def column_z(self, x, y, radius, z0, z1, segments=10, start=0.0, end=360.0, material=0):
        """A vertical (part-)cylinder: the rounded upstream nose of a pier."""
        ring0, ring1 = [], []
        closed = end - start >= 359.9
        count = segments if closed else segments + 1
        for i in range(count):
            a = math.radians(start + (end - start) * i / segments)
            ring0.append(self.mesh.verts.new((x + math.cos(a) * radius, y + math.sin(a) * radius, z0)))
            ring1.append(self.mesh.verts.new((x + math.cos(a) * radius, y + math.sin(a) * radius, z1)))
        if not closed:
            ring0.append(self.mesh.verts.new((x, y, z0)))
            ring1.append(self.mesh.verts.new((x, y, z1)))
        n = len(ring0)
        self._face(list(reversed(ring0)), material)
        self._face(ring1, material)
        for i in range(n):
            j = (i + 1) % n
            self._face([ring0[i], ring0[j], ring1[j], ring1[i]], material)

    def sheet(self, rows, material, alpha=None):
        """A grid of quads from rows of points (each row the same length), one-sided."""
        grid = [[self.mesh.verts.new(p) for p in row] for row in rows]
        for r in range(len(grid) - 1):
            for c in range(len(grid[r]) - 1):
                face = self._face([grid[r][c], grid[r][c + 1], grid[r + 1][c + 1], grid[r + 1][c]], material)
                if alpha is not None:
                    for loop in face.loops:
                        row = r if loop.vert in (grid[r][c], grid[r][c + 1]) else r + 1
                        loop[self.colour] = (1.0, 1.0, 1.0, alpha[row])

    def _face(self, verts, material):
        face = self.mesh.faces.new(verts)
        face.material_index = material
        for loop in face.loops:
            loop[self.colour] = (1.0, 1.0, 1.0, 1.0)
        return face

    def to_object(self, collection, bevel_width=None):
        obj = self._object(self.body, self.name, collection)
        if bevel_width:
            bevel(obj, bevel_width)
        if self.detail.faces:
            trim = self._object(self.detail, self.name + "_trim", collection)
            td.select_only(obj, trim)
            bpy.context.view_layer.objects.active = obj
            td.run(bpy.ops.object.join)
        else:
            self.detail.free()
        return obj

    @staticmethod
    def _object(mesh, name, collection):
        bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
        data = bpy.data.meshes.new(name)
        mesh.to_mesh(data)
        mesh.free()
        for m in MATERIALS:
            data.materials.append(material(m))
        obj = bpy.data.objects.new(name, data)
        collection.objects.link(obj)
        return obj


def material(name):
    colours = {"Concrete": (0.62, 0.62, 0.60, 1), "ConcreteDark": (0.25, 0.25, 0.25, 1),
               "Steel": (0.91, 0.70, 0.23, 1), "SpillWater": (0.75, 0.88, 0.92, 1)}
    existing = bpy.data.materials.get("Dam" + name)
    if existing:
        return existing
    made = bpy.data.materials.new("Dam" + name)
    made.diffuse_color = colours[name]
    return made


CONCRETE, DARK, STEEL, WATER = range(4)


# --- the wall section shared by bays -------------------------------------------------------

def wall_section(piece, x0, x1, crest=CREST):
    """Inner wall, deck and battered downstream face, as one solid from x0 to x1."""
    piece.prism(x0, x1, [
        (0.0, INNER_BOTTOM), (0.0, crest), (DECK_DEPTH, crest),
        (FACE_OUT, FACE_BOTTOM), (FACE_OUT, FOOTING), (4.0, UNDERSIDE)])


def footing(piece, x0, x1):
    """The stepped foundation the wall and its buttresses stand on."""
    piece.prism(x0, x1, [
        (FACE_OUT - 1.0, FOOTING), (FACE_OUT - 1.0, FACE_BOTTOM), (FOOTING_OUT, FACE_BOTTOM),
        (FOOTING_OUT, FOOTING - 1.5), (FOOTING_OUT - 3.0, FOOTING - 1.5), (FOOTING_OUT - 3.0, FOOTING - 3.0),
        (FACE_OUT, UNDERSIDE + 0.5)])


def parapets(piece, x0, x1, crest=CREST):
    piece.box(x0, x1, 0.0, 0.35, crest, crest + PARAPET)
    piece.box(x0, x1, DECK_DEPTH - 0.35, DECK_DEPTH, crest, crest + PARAPET)


# --- pieces -------------------------------------------------------------------------------

def build_bay(collection):
    piece = Piece("dam_bay")
    half = BAY_WIDTH / 2
    wall_section(piece, -half, half)
    footing(piece, -half, half)
    parapets(piece, -half, half)
    # Formwork recess: a shallow panel on the downstream face, so each bay reads as cast.
    piece.prism(-half + 1.4, half - 1.4, [
        (DECK_DEPTH + 0.9, -1.5), (FACE_OUT - 0.6, FACE_BOTTOM + 1.2),
        (FACE_OUT - 0.45, FACE_BOTTOM + 1.2), (DECK_DEPTH + 1.05, -1.5)])
    # The buttress on the +x joint, half in this bay and half in the next.
    b0, b1 = half - BUTTRESS_THICK / 2, half + BUTTRESS_THICK / 2
    piece.prism(b0, b1, [
        (DECK_DEPTH - 0.2, CREST - 0.4), (BUTTRESS_OUT, FACE_BOTTOM),
        (FACE_OUT - 1.0, FACE_BOTTOM), (DECK_DEPTH - 0.6, CREST - 1.5)])
    # A capped post over the buttress, on the outer parapet.
    piece.box(b0 - 0.1, b1 + 0.1, DECK_DEPTH - 0.5, DECK_DEPTH + 0.3, CREST, CREST + PARAPET + 0.6)
    return piece.to_object(collection, BEVEL)


def build_spillway(collection):
    piece = Piece("dam_spillway")
    water = Piece("dam_spillway_water")
    half = SPILL_WIDTH / 2
    piers = [-half, -BAY_WIDTH / 2, BAY_WIDTH / 2, half]

    # The body under the weir: inner face down to the footing, weir crest, ogee into the chute.
    ogee = [(1.0 + i * 0.6, WEIR - 0.02 * (i * 0.6) ** 2 * 3.0) for i in range(8)]
    chute_end = (LIP_OUT - 3.0, FACE_BOTTOM + 1.5)
    lip = [(LIP_OUT - 1.5, FACE_BOTTOM + 1.2), (LIP_OUT, FACE_BOTTOM + 2.4)]
    profile = [(0.0, INNER_BOTTOM), (0.0, WEIR), (0.6, WEIR + 0.2)] + ogee + [chute_end] + lip + [
        (LIP_OUT, FACE_BOTTOM - 1.0), (FOOTING_OUT - 3.0, FOOTING - 3.0), (FACE_OUT, UNDERSIDE + 0.5),
        (4.0, UNDERSIDE)]
    piece.prism(-half, half, profile)

    for i, x in enumerate(piers):
        # Outer piers are half-width, so two spillways never double up against a bay.
        p0 = x - (0 if i == 0 else PIER_WIDTH / 2)
        p1 = x + (0 if i == len(piers) - 1 else PIER_WIDTH / 2)
        # Pier: from upstream nose to the downstream end of the gate structure, up to the deck,
        # and its upstream half on up to the gantry, so the gates stand clear of the sea.
        piece.box(p0, p1, -2.0, 8.0, INNER_BOTTOM, CREST)
        piece.box(p0, p1, -2.0, 2.5, CREST, GANTRY)
        if 0 < i < len(piers) - 1:
            piece.column_z(x, -2.0, PIER_WIDTH / 2, INNER_BOTTOM, GANTRY, segments=8, start=180.0, end=360.0)
        # Training wall (outer piers) or divider (inner) down the chute.
        top = 3.0 if i in (0, len(piers) - 1) else 1.6
        reach = LIP_OUT if i in (0, len(piers) - 1) else 14.0
        piece.prism(p0, p1, [(8.0, CREST), (8.0, -6.0), (reach, FACE_BOTTOM + 1.0),
                             (reach, FACE_BOTTOM + 1.0 + top), (8.5, CREST)])
        # Hoist house on the gantry over each pier: the silhouette that says "gates".
        piece.box(p0 - 0.5, p1 + 0.5, -2.2, 2.7, GANTRY + 1.0, GANTRY + 4.0)
        piece.box(p0 - 0.8, p1 + 0.8, -2.5, 3.0, GANTRY + 4.0, GANTRY + 4.5)
        with piece.trim():
            piece.box(p0 - 0.51, p1 + 0.51, -0.6, 1.1, GANTRY + 1.8, GANTRY + 3.2, DARK)

    # The deck bridging the piers, at the crest, with its parapets, and the service bridge along
    # the tops of the piers, with rails.
    piece.box(-half, half, 0.0, DECK_DEPTH, CREST - 1.0, CREST)
    parapets(piece, -half, half)
    piece.box(-half, half, -1.6, 2.4, GANTRY, GANTRY + 1.0)
    with piece.trim():
        piece.box(-half, half, -1.6, -1.35, GANTRY + 1.0, GANTRY + 2.0)
        piece.box(-half, half, 2.15, 2.4, GANTRY + 1.0, GANTRY + 2.0)

    # Radial gates between the piers, raised: a curved steel skin with its arms.
    for g in range(3):
        g0 = piers[g] + (PIER_WIDTH / 2 if g > 0 else 0.0) + 0.05
        g1 = piers[g + 1] - (PIER_WIDTH / 2 if g + 1 < 3 else 0.0) - 0.05
        # Radial (Tainter) gate: a skin curved about a trunnion downstream of it, raised so its
        # bottom edge is just under the sea and water runs out beneath it.
        trunnion, radius = (3.5, 1.5), 5.6
        rows = []
        for k in range(9):
            a = math.radians(-26 + k * 7)
            rows.append([(g0, trunnion[0] - math.cos(a) * radius, trunnion[1] + math.sin(a) * radius),
                         (g1, trunnion[0] - math.cos(a) * radius, trunnion[1] + math.sin(a) * radius)])
        with piece.trim():
            piece.sheet(rows, STEEL)
            piece.sheet([list(reversed(r)) for r in rows], STEEL)
            # Ribs across the skin, and the two arms back to the trunnion.
            for k in (1, 4, 7):
                (ya, za), = [(rows[k][0][1], rows[k][0][2])]
                piece.box(g0, g1, ya + 0.05, ya + 0.35, za - 0.15, za + 0.15, STEEL)
            for gx in (g0 + 0.3, g1 - 0.6):
                for k in (0, 8):
                    ya, za = rows[k][0][1], rows[k][0][2]
                    steps = 6
                    for t in range(steps):
                        f0, f1 = t / steps, (t + 1) / steps
                        piece.box(gx, gx + 0.3,
                                  min(ya + (trunnion[0] - ya) * f0, ya + (trunnion[0] - ya) * f1),
                                  max(ya + (trunnion[0] - ya) * f0, ya + (trunnion[0] - ya) * f1),
                                  min(za + (trunnion[1] - za) * f0, za + (trunnion[1] - za) * f1) - 0.12,
                                  max(za + (trunnion[1] - za) * f0, za + (trunnion[1] - za) * f1) + 0.12, STEEL)
        # Water running under the gate, down the chute, and off the lip into the void.
        w0, w1 = g0 + 0.1, g1 - 0.1
        path = [(-1.5, 0.0 - 0.05), (0.5, WEIR + 0.9), (1.0, WEIR + 0.55)]
        path += [(y, z + 0.35) for y, z in ogee] + [(chute_end[0], chute_end[1] + 0.35)]
        path += [(lip[0][0], lip[0][1] + 0.4), (lip[1][0], lip[1][1] + 0.4)]
        # The curtain: thrown out off the lip and falling, fading out below the footing.
        speed, drop = 6.0, 0.0
        for step in range(1, 13):
            t = step * 0.28
            path.append((LIP_OUT + speed * t, lip[1][1] + 0.4 + 2.0 * t - 4.9 * t * t))
        alpha = []
        for y, z in path:
            alpha.append(1.0 if z > FOOTING else max(0.0, 1.0 - (FOOTING - z) / 22.0))
        water.sheet([[(w0, y, z), ((w0 + w1) / 2, y, z), (w1, y, z)] for y, z in path], WATER, alpha)

    return piece.to_object(collection, BEVEL), water.to_object(collection)


def build_tower(collection):
    """The intake tower: stands in the sea off a bay, joined to the crest by a bridge."""
    piece = Piece("dam_tower")
    piece.box(-4.0, 4.0, -14.0, -6.0, INNER_BOTTOM, 14.0)
    piece.box(-4.6, 4.6, -14.6, -5.4, 14.0, 15.0)
    piece.box(-3.2, 3.2, -13.2, -6.8, 15.0, 17.5)
    piece.box(-3.6, 3.6, -13.6, -6.4, 17.5, 18.0)
    with piece.trim():
        # Window slots, one tall slit per face per storey.
        for z in (3.0, 8.0):
            piece.box(-0.5, 0.5, -14.05, -13.9, z, z + 3.0, DARK)
            piece.box(-0.5, 0.5, -6.1, -5.95, z, z + 3.0, DARK)
            piece.box(-4.05, -3.9, -10.5, -9.5, z, z + 3.0, DARK)
            piece.box(3.9, 4.05, -10.5, -9.5, z, z + 3.0, DARK)
        # Bridge to the crest, with rails.
        piece.box(-1.6, 1.6, -6.0, 0.0, CREST - 0.9, CREST)
        piece.box(-1.6, -1.35, -6.0, 0.0, CREST, CREST + PARAPET)
        piece.box(1.35, 1.6, -6.0, 0.0, CREST, CREST + PARAPET)
    return piece.to_object(collection, BEVEL)


# --- finishing -----------------------------------------------------------------------------

def bevel(obj, width=0.12):
    """Chamfered edges: at this scale a hard 90-degree edge reads as a model, not concrete."""
    td.apply_modifier(obj, "BEVEL", width=width, segments=1, limit_method="ANGLE",
                      angle_limit=math.radians(35), harden_normals=False)


def export(obj, out):
    td.select_only(obj)
    td.run(bpy.ops.export_scene.fbx, filepath=out, use_selection=True,
           apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
           bake_space_transform=True, path_mode="STRIP", mesh_smooth_type="FACE",
           colors_type="LINEAR")


def build_kit(out_dir):
    collection = bpy.data.collections.get("DamKit")
    if collection is None:
        collection = bpy.data.collections.new("DamKit")
        bpy.context.scene.collection.children.link(collection)
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    bay = build_bay(collection)
    spillway, water = build_spillway(collection)
    tower = build_tower(collection)

    os.makedirs(out_dir, exist_ok=True)
    report = {}
    for obj in (bay, spillway, water, tower):
        export(obj, os.path.join(out_dir, obj.name + ".fbx"))
        report[obj.name] = {"triangles": td.tris(obj),
                            "size": [round(d, 2) for d in obj.dimensions]}
    return report
