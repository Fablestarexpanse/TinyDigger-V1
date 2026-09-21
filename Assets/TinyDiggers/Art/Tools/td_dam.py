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

    build_bay()        12 m: vertical inner wall, a deep crest cantilevered out over a
                       terraced downstream face, solid chamfered crest walls, lit slots, and a
                       wedge fin on its +x joint rising 4 m above the crest
    build_spillway()   36 m: four chamfered gate towers under one monolithic lintel with a
                       control cabin, three slab lift gates, a stepped chute with angular
                       training walls, and the water (its own object: sheet down the steps,
                       curtain off the lip)
    build_tower()      the intake tower standing in the sea: a shaft under a corbelled,
                       cantilevered head with lit bands and a beacon mast, and a slab bridge to
                       the crest; placed on top of a bay
    build_kit(out)     all of them, bevelled, exported (FBX) and saved (.blend)

Materials are slots only; Unity assigns the shaders. Concrete, ConcreteDark (recesses and
slots), Steel (gates and hoists), SpillWater (the water, whose vertex alpha fades the falling
curtain out into the void), Light (emissive strips at the back of the slots).

Style (Ronan, 2026-09-21, after the first kit): sci-fi brutalism. Monolithic mass, repetition,
deep shadow, chamfers instead of rails, lit slots.
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

MATERIALS = ("Concrete", "ConcreteDark", "Steel", "SpillWater", "Light")


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
               "Steel": (0.30, 0.32, 0.34, 1), "SpillWater": (0.75, 0.88, 0.92, 1),
               "Light": (0.55, 0.95, 1.0, 1)}
    existing = bpy.data.materials.get("Dam" + name)
    if existing:
        return existing
    made = bpy.data.materials.new("Dam" + name)
    made.diffuse_color = colours[name]
    return made


CONCRETE, DARK, STEEL, WATER, LIGHT = range(5)


# --- sci-fi brutalist geometry (Ronan, 2026-09-21: "more of a scifi brutalism look") -------
#
# Monolithic mass, repetition and deep shadow: a crest that cantilevers out over a stepped,
# terraced downstream face; a wedge fin on every joint rising above the crest; recessed slots
# with light strips at their backs; spillways framed by chamfered gate towers under one
# monolithic lintel, with slab lift gates and a chute that steps down to its lip.

DECK_OUT = 9.0           # the crest deck, from the inner face out to its cantilevered edge
FIN_TOP = 6.0            # the fins rise this far above the sea, 4 m over the crest
FIN_THICK = 2.0
TERRACES = [             # the downstream face: (face y, top z, bottom z) for each step
    (6.5, 1.0, -4.0),
    (9.0, -4.0, -10.0),
    (11.5, -10.0, -16.0),
    (14.0, -16.0, FOOTING),
]
LINTEL = 10.0            # underside of the spillway lintel
LINTEL_TOP = 13.5


def slot(piece, x0, x1, y, z0, z1, outward=True):
    """A recessed band on a face at depth y: a dark reveal with a light strip along its middle."""
    d = 0.06 if outward else -0.06
    with piece.trim():
        piece.box(x0, x1, min(y, y + d), max(y, y + d), z0, z1, DARK)
        m = (z0 + z1) / 2
        piece.box(x0 + 0.3, x1 - 0.3, min(y + d, y + 2 * d), max(y + d, y + 2 * d), m - 0.12, m + 0.12, LIGHT)


def _dedupe(points):
    out = []
    for p in points:
        if not out or abs(out[-1][0] - p[0]) > 1e-6 or abs(out[-1][1] - p[1]) > 1e-6:
            out.append(p)
    return out


def wall_section(piece, x0, x1):
    """The monolith: inner wall, deep deck cantilevered out over a terraced face."""
    profile = [(0.0, INNER_BOTTOM), (0.0, CREST), (DECK_OUT, CREST), (DECK_OUT, CREST - 1.0)]
    for y, top, bottom in TERRACES:
        profile += [(y, top), (y, bottom)]
    profile += [(TERRACES[-1][0], FOOTING - 1.5), (10.0, UNDERSIDE), (3.0, UNDERSIDE - 0.5)]
    piece.prism(x0, x1, _dedupe(profile))


def crest_walls(piece, x0, x1, crest=CREST):
    """Solid, chamfered crest walls: no rails, just mass."""
    piece.prism(x0, x1, [(0.0, crest), (0.9, crest), (0.9, crest + 0.9), (0.5, crest + 1.3), (0.0, crest + 1.3)])
    piece.prism(x0, x1, [(DECK_OUT - 1.1, crest), (DECK_OUT, crest), (DECK_OUT, crest + 1.6),
                         (DECK_OUT - 0.6, crest + 1.6), (DECK_OUT - 1.1, crest + 1.1)])


def fin(piece, x):
    """The wedge fin on a joint: rises above the crest, runs down and out to the footing."""
    f0, f1 = x - FIN_THICK / 2, x + FIN_THICK / 2
    piece.prism(f0, f1, [
        (2.5, CREST), (2.5, FIN_TOP - 1.2), (3.7, FIN_TOP), (DECK_OUT + 1.0, FIN_TOP),
        (TERRACES[-1][0] + 5.5, FOOTING), (TERRACES[-1][0] - 0.5, FOOTING), (5.0, -2.0)])
    # A lit slot down each side of the fin, near its outer edge.
    with piece.trim():
        for side, xs in ((-1, f0), (1, f1)):
            a, b = sorted((xs, xs + side * 0.06))
            piece.box(a, b, DECK_OUT - 1.0, DECK_OUT + 0.2, -6.0, FIN_TOP - 1.0, DARK)
            a, b = sorted((xs + side * 0.06, xs + side * 0.1))
            piece.box(a, b, DECK_OUT - 0.55, DECK_OUT - 0.25, -5.5, FIN_TOP - 1.5, LIGHT)


def footing(piece, x0, x1):
    piece.box(x0, x1, TERRACES[-1][0] - 1.0, TERRACES[-1][0] + 6.5, FOOTING - 1.5, FOOTING)


# --- pieces -------------------------------------------------------------------------------

def build_bay(collection):
    piece = Piece("dam_bay")
    half = BAY_WIDTH / 2
    wall_section(piece, -half, half)
    footing(piece, -half, half)
    crest_walls(piece, -half, half)
    fin(piece, half)
    # A lit slot in each terrace face, and one along the inner face above the waterline.
    for y, top, bottom in TERRACES[:3]:
        slot(piece, -half + 1.2, half - 1.2, y, bottom + 1.8, bottom + 2.6)
    slot(piece, -half, half, 0.0, 0.6, 1.3, outward=False)
    return piece.to_object(collection, BEVEL)


def build_spillway(collection):
    piece = Piece("dam_spillway")
    water = Piece("dam_spillway_water")
    half = SPILL_WIDTH / 2
    piers = [-half, -BAY_WIDTH / 2, BAY_WIDTH / 2, half]
    pier_w = 3.0

    # The body under the weir: a stepped chute, 2 m treads and 1.5 m risers, to a lip.
    chute = [(0.0, WEIR), (2.5, WEIR)]
    y, z = 2.5, WEIR
    while z > FACE_BOTTOM + 1.0:
        z -= 1.5
        chute.append((y, z))
        y += 2.0
        chute.append((y, z))
    lip_y, lip_z = y + 1.5, z + 1.0
    chute.append((lip_y, lip_z))
    profile = [(0.0, INNER_BOTTOM)] + chute + [(lip_y, z - 2.0), (TERRACES[-1][0], FOOTING - 1.5),
                                               (10.0, UNDERSIDE), (3.0, UNDERSIDE - 0.5)]
    piece.prism(-half, half, _dedupe(profile))

    for i, x in enumerate(piers):
        outer = i in (0, len(piers) - 1)
        p0 = x - (0 if i == 0 else pier_w / 2)
        p1 = x + (0 if i == len(piers) - 1 else pier_w / 2)
        # The gate tower: chamfered at the nose and the head, up into the lintel.
        piece.prism(p0, p1, [(-4.0, INNER_BOTTOM), (-4.0, LINTEL - 1.0), (-2.5, LINTEL + 0.5),
                             (7.0, LINTEL + 0.5), (9.0, CREST), (9.0, INNER_BOTTOM)])
        # Training wall (outer) or divider (inner) down the steps, angular.
        reach = lip_y if outer else 14.0
        top = 3.2 if outer else 1.8
        piece.prism(p0, p1, [(9.0, CREST), (9.0, -6.0), (reach, lip_z - 1.0), (reach, lip_z - 1.0 + top),
                             (10.5, CREST + 0.5)])
        if not outer:
            slot(piece, p0 + 0.05, p1 - 0.05, -4.0, -1.0, LINTEL - 3.0, outward=False)

    # The deck across the spillway at the crest, its walls, and the lintel over the gates.
    piece.box(-half, half, 0.0, DECK_OUT, CREST - 1.0, CREST)
    crest_walls(piece, -half, half)
    piece.prism(-half, half, [(-3.5, LINTEL + 0.8), (-2.7, LINTEL), (3.5, LINTEL), (4.3, LINTEL + 0.8),
                              (4.3, LINTEL_TOP), (-3.5, LINTEL_TOP)])
    slot(piece, -half + 1.0, half - 1.0, -3.5, LINTEL + 1.4, LINTEL + 2.6, outward=False)
    # The control cabin, cantilevered off the lintel's middle over the sea.
    piece.box(-6.0, 6.0, -7.5, 3.0, LINTEL_TOP, LINTEL_TOP + 3.5)
    piece.box(-6.5, 6.5, -8.0, 3.5, LINTEL_TOP + 3.5, LINTEL_TOP + 4.1)
    slot(piece, -5.0, 5.0, -7.5, LINTEL_TOP + 1.0, LINTEL_TOP + 2.6, outward=False)

    # Slab lift gates between the towers, raised so water runs out beneath them.
    for g in range(3):
        g0 = piers[g] + (pier_w / 2 if g > 0 else 0.0) + 0.05
        g1 = piers[g + 1] - (pier_w / 2 if g + 1 < 3 else 0.0) - 0.05
        with piece.trim():
            piece.box(g0, g1, -1.4, -0.7, GATE_BOTTOM, LINTEL - 0.2, STEEL)
            for rib in range(4):
                zr = GATE_BOTTOM + 1.2 + rib * 2.2
                piece.box(g0, g1, -1.7, -1.4, zr, zr + 0.35, STEEL)
        # Water: out under the gate, down the steps (skimming their noses), off the lip.
        w0, w1 = g0 + 0.1, g1 - 0.1
        path = [(-2.5, -0.05), (-0.9, GATE_BOTTOM + 0.1), (1.5, WEIR + 0.6)]
        noses = [p for k, p in enumerate(chute) if k % 2 == 1 and k > 1]
        path += [(yy - 0.5, zz + 0.9) for yy, zz in noses]
        path += [(lip_y, lip_z + 0.5)]
        for step in range(1, 13):
            t = step * 0.28
            path.append((lip_y + 6.0 * t, lip_z + 0.5 + 2.0 * t - 4.9 * t * t))
        alpha = [1.0 if zz > FOOTING else max(0.0, 1.0 - (FOOTING - zz) / 22.0) for yy, zz in path]
        water.sheet([[(w0, yy, zz), ((w0 + w1) / 2, yy, zz), (w1, yy, zz)] for yy, zz in path], WATER, alpha)

    return piece.to_object(collection, BEVEL), water.to_object(collection)


def build_tower(collection):
    """The intake tower: a shaft in the sea under a cantilevered head, and a slab bridge."""
    piece = Piece("dam_tower")
    piece.box(-3.5, 3.5, -13.5, -6.5, INNER_BOTTOM, 12.0)
    # Corbelled head: three stepped courses out to the cantilevered block.
    for k, grow in enumerate((0.7, 1.4, 2.1)):
        piece.box(-3.5 - grow, 3.5 + grow, -13.5 - grow, -6.5 + grow, 12.0 + k * 0.7, 12.7 + k * 0.7)
    piece.box(-6.0, 6.0, -16.0, -4.0, 14.1, 20.5)
    piece.box(-4.5, 4.5, -14.5, -5.5, 20.5, 22.0)
    # Lit window bands round the head, and a lit slot up the shaft.
    slot(piece, -5.0, 5.0, -16.0, 16.2, 18.2, outward=False)
    slot(piece, -5.0, 5.0, -4.0, 16.2, 18.2)
    with piece.trim():
        piece.box(-0.5, 0.5, -13.56, -13.5, -1.0, 11.5, DARK)
        piece.box(-0.15, 0.15, -13.6, -13.56, -0.5, 11.0, LIGHT)
        # The mast, with a beacon.
        piece.box(-0.25, 0.25, -10.25, -9.75, 22.0, 32.0, STEEL)
        piece.box(-0.5, 0.5, -10.5, -9.5, 32.0, 32.8, LIGHT)
    # The slab bridge to the crest.
    piece.box(-2.2, 2.2, -6.5, 0.0, CREST - 1.2, CREST)
    piece.prism(-2.2, -1.6, [(-6.5, CREST), (0.0, CREST), (0.0, CREST + 1.1), (-6.5, CREST + 1.1)])
    piece.prism(1.6, 2.2, [(-6.5, CREST), (0.0, CREST), (0.0, CREST + 1.1), (-6.5, CREST + 1.1)])
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
