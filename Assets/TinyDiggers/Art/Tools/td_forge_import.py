"""
td_forge_import.py - brings a machine-forge machine into TinyDiggers as FBX.

    blender --background --factory-startup <forge>/out/<asset>/<asset>.blend \
        --python Assets/TinyDiggers/Art/Tools/td_forge_import.py -- <out folder>

The forge (F:/machine-forge) builds each machine as a hierarchy of empties, one mesh under each,
coloured per face by vertex colour, with one NLA track per clip (Dig, Tip, Drive, ...). Unity reads
none of that as it stands: URP Lit ignores vertex colour, and Blender's FBX exporter writes NLA
strips per object rather than per clip. So:

 1. Faces are split into one material per colour, named forge_<hex> (sRGB). The same colour is the
    same material on every machine, and Unity gives each a URP Lit material of that colour.
 2. <asset>.fbx is the rest pose with no animation.
 3. <asset>@<Clip>.fbx is the scene evaluated with only that clip's tracks playing, baked into an
    action on every object (the exporter reads actions, not the NLA), one file per NLA track name.
    Unity merges the @ files' clips onto the base model's hierarchy.

Geometry stays 1:1 with the forge: size rulings are applied by the prefab (Ronan, 2026-09-24:
keep the game's size rulings), so a re-export never changes a unit's size.
"""
import bpy, os, sys, json

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = os.path.abspath(argv[0])
os.makedirs(OUT, exist_ok=True)
sc = bpy.context.scene
root = next(o for o in sc.objects if o.get("forge_root"))
asset = os.path.splitext(os.path.basename(bpy.data.filepath))[0]

# --- 1: a material per face colour -----------------------------------------------------------
materials = {}
def material(hexname):
    if hexname not in materials:
        m = bpy.data.materials.get(hexname) or bpy.data.materials.new(hexname)
        r, g, b = (int(hexname[6 + 2 * i:8 + 2 * i], 16) / 255.0 for i in range(3))
        m.diffuse_color = (r ** 2.2, g ** 2.2, b ** 2.2, 1.0)
        materials[hexname] = m
    return materials[hexname]

for o in sc.objects:
    if o.type != "MESH":
        continue
    me = o.data
    attr = me.color_attributes.get("Col") or (me.color_attributes[0] if len(me.color_attributes) else None)
    if attr is None:
        continue
    slots = {}
    me.materials.clear()
    for poly in me.polygons:
        if attr.domain == "CORNER":
            c = attr.data[poly.loop_indices[0]].color_srgb
        else:
            c = attr.data[poly.vertices[0]].color_srgb
        hexname = "forge_%02x%02x%02x" % tuple(max(0, min(255, round(v * 255))) for v in c[:3])
        if hexname not in slots:
            slots[hexname] = len(me.materials)
            me.materials.append(material(hexname))
        poly.material_index = slots[hexname]

# --- 2 and 3: the rest pose, then one file per clip -------------------------------------------
tracks = {}
for o in sc.objects:
    if o.animation_data:
        o.animation_data.action = None
        for t in o.animation_data.nla_tracks:
            tracks.setdefault(t.name, []).append(t)

def play_only(name):
    for o in sc.objects:
        if o.animation_data:
            for t in o.animation_data.nla_tracks:
                t.mute = t.name != name
                t.is_solo = False

def export(path, animated):
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=False, object_types={"EMPTY", "MESH"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
        use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False,
        bake_anim=animated, bake_anim_use_all_bones=False, bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False, bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0)

# The rest pose, taken once. A part a clip does not move keeps whatever the last evaluated frame
# left it at, so without this the dumper's wheels sat frozen a quarter turn round through Tip.
play_only(None)
sc.frame_set(1)
rest = {o.name: (o.location.copy(), o.rotation_euler.copy(), o.scale.copy()) for o in sc.objects}

def to_rest():
    for o in sc.objects:
        o.location, o.rotation_euler, o.scale = (v.copy() for v in rest[o.name])

report = {"asset": asset, "materials": sorted(materials), "clips": {}}
# The first animated export of a Blender session comes out constant, whichever clip it is and
# whatever was exported before it (dumper Drive, digger Dig, dozer BladeCycle, boat Bob, every
# time), and every one after it is right. So the first clip goes through twice and the first
# file is thrown away. Check with a re-import: every clip should have curves that move.
names = sorted(tracks)
for name in names[:1] + names:
    ts = tracks[name]
    play_only(name)
    to_rest()
    ends = [s.frame_end for t in ts for s in t.strips]
    starts = [s.frame_start for t in ts for s in t.strips]
    if not ends:
        continue
    sc.frame_start, sc.frame_end = int(min(starts)), int(round(max(ends)))
    # Blender 5.2's FBX exporter bakes only an object's active action and ignores what its NLA
    # plays: exported straight, every clip came out constant (the dumper's bed never tipped).
    # So the clip is baked into an action on every object first, then those are thrown away.
    # Evaluated at the clip's own start before baking, from the rest pose.
    sc.frame_set(sc.frame_start)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = root
    bpy.ops.nla.bake(frame_start=sc.frame_start, frame_end=sc.frame_end, step=1, only_selected=True,
                     visual_keying=True, clear_constraints=False, clear_parents=False,
                     use_current_action=False, clean_curves=False, bake_types={"OBJECT"})
    warm_up = name not in report["clips"] and not report.get("warmed")
    target = os.path.join(OUT, f"{asset}@{name}.fbx")
    export(target, animated=True)
    if warm_up:
        report["warmed"] = True
    for o in sc.objects:
        if o.animation_data and o.animation_data.action:
            baked = o.animation_data.action
            o.animation_data.action = None
            bpy.data.actions.remove(baked)
    if not warm_up:
        report["clips"][name] = [sc.frame_start, sc.frame_end]

# The rest pose, with nothing playing.
play_only(None)
to_rest()
sc.frame_set(1)
export(os.path.join(OUT, f"{asset}.fbx"), animated=False)

report.pop("warmed", None)
json.dump(report, open(os.path.join(OUT, f"{asset}.forge_import.json"), "w"), indent=1)
print("FORGE_IMPORT", json.dumps(report))
