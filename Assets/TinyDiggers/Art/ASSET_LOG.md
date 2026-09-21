# Asset log

One entry per asset: the seed that won, why, and every evaluation loop with its scores against
STYLE.md (1–5 per section) and the one thing that changed. The shots are in `Screenshots/Art/`,
taken in Unity under our lighting by `TinyDiggers/Art Capture`: 20 seeded instances on a grassy
patch of Continent seed 11, then the home preset, an RTS zoom (80 m) and a close look (32 m).

## tree_a — broadleaf, Phase 1 (2026-09-21)

**Concept.** Three candidates from the de-lit 3D-input variant (seeds 7200, 7201, 7202). The
foliage clause says "even mid spring green" instead of two-tone, because two-tone is light and
Unity supplies it. Picked **7200**: its clumps overlap into one full canopy mass on a sturdy trunk,
so it reads as a lumpy blob at 44 px (STYLE §2), and it leaves Trellis2 no see-through gaps to
guess at. 7201 had gaps between clumps; 7202 was lopsided.

**Trellis2.** The ComfyUI workflow `TinyDiggers_Trellis2` (the bundled Pixal3D/TRELLIS.2 template,
switched to TRELLIS.2, 2048 textures) gave 674k triangles. The canopy is 1,225 separate leaf
shells, and the base-colour texture averages `#436113`.

**Blender.** `Art/Tools/clean_export.py`, run as
`blender --background --factory-startup --python clean_export.py -- --in <raw.glb> --out
Assets/TinyDiggers/Art/Props/tree_a --height 9 --category plant`. Result: 1,455 triangles,
8.85 m tall, 11 non-manifold edges (small canopy pinches), 512² albedo, pivot at the base centre.

| Loop | Changed (one thing) | Palette | Form | Surface | Light | Scale | Worst miss |
|------|---------------------|---------|------|---------|-------|-------|------------|
| 1 | first pass: fine voxel hull (0.012 h), plain smooth | 2 | 1 | 2 | 2 | 4 | trunk shattered into floating slivers; canopy crumpled with holes; neon lime |
| 2 | Blender hull: Laplacian volume-preserving smooth ×12, voxel 0.02 h | 2 | 3 | 3 | 2 | 4 | soft clumps now; trunk still a broken stick; neon lime |
| 3 | palette: albedo = swatch × 0.72 (swatches were sampled from *lit* renders) | 3 | 3 | 3 | 3 | 4 | trunk |
| 4 | Blender wood: split wood from foliage by texture colour; trunk rebuilt as a tapered 8-sided tube through the wood's slice centres | 3 | 4 | 4 | 3 | 4 | canopy reads warm lime; no teal shade |
| 5 | palette: foliage target moved 30% from Leaf Mid toward Canopy Teal | 3 | 4 | 3 | 3 | 4 | orange/ochre flecks on the canopy; trunk saturated orange |

Internal Blender-only checks between loops (not scored) found two dead ends:
- A fine wood remesh made collapse decimation stall at about 1,800 triangles.
- Decimating the raw trunk to a few hundred triangles turned it into spikes.

The trunk rebuild in loop 4 is what came out of them.

**Best: loop 5** (current files). The canopy colour sits best with the terrain and the references;
form and surface come from loop 4.

**Not right yet, and why:**
- **Flecks on the canopy (a loop-5 regression in look, a bake defect in fact).** The canopy bake
  sometimes hits branches inside the leaves; the palette step then groups those texels with Bark
  or Meadow Sun and pushes them there. Fix: bake the canopy only from the foliage faces (the wood
  is already split off), or snap the canopy's texels to foliage swatches only.
- **Lighting 3/5.** No cool teal on the shaded side. That is the scene lighting (STYLE §4), which
  the brief puts in the ground-and-water pass; it can't be fixed per asset.
- **Trunk colour.** Bark × 0.72 still reads as saturated orange under the 4800 K sun. The
  references' trunks are terracotta but darker. Try bark at × 0.55, or desaturate it slightly.
- **11 non-manifold edges** on the canopy hull (pinches where the smoothing met); invisible at RTS
  distance, not zero.
- **GLB into Unity needs a package.** The project has no glTF importer, so Unity uses the FBX that
  the same script writes beside the GLB. Adding `com.unity.cloud.gltfast` is Ronan's call.

### tree_a, workflow v2: leaf cards (2026-09-21) — **current**

Ronan changed the workflow after Phase 1:
- Blender is the hub for cleaning, improving and converting Trellis items, and adds animation
  where needed.
- **Props are never voxel-remeshed; only the terrain is cells.**
- He chose leaf cards over a higher triangle budget with LODs.

**Why cards.** A Trellis2 canopy is thin leaf shells. Decimated directly (Blender collapse stalls
at about 32k faces; the ComfyUI workflow's own GPU decimation was tried at 1.5k, 4k and 15k faces),
it shatters into spikes at every budget (`Screenshots/Art/tree_a_direct_decimation.png`, against
the raw `tree_a_trellis_raw.png`).

**Build.**
- The same Trellis2 mesh as Phase 1, split into foliage and wood by base colour
  (`td_pipeline.split_parts`).
- **Canopy:** 280 square cards (`td_cards.leaf_cards`). The leaf surface is clustered by k-means,
  and each cluster gets a card on its best-fit plane at 2.0× its extent, facing out, spun at
  random.
  - UVs point into a 2×2 leaf-cluster atlas: `leaf_atlas_a.png`, four Krea2 sprites (seeds
    7210–7213, workflow `TinyDiggers_Sprite_Krea2`), cut out by birefnet, palette-pulled, and
    colour-bled so the mips don't fringe.
  - Normals lean 65% toward "out from the canopy centre", so the canopy shades as soft clumps.
  - Vertex colour R is the wind weight.
- **Trunk and branches:** 8 tapered 6-sided tubes chained through height slices of Trellis's wood
  (`td_cards.branch_tubes`). No remesh.
- **Result:** 1,240 triangles (560 of them cards), 9.6 m tall, two materials:
  - `tree_a_leaves`: URP Lit, alpha clip 0.5, double-sided, mip coverage preserved.
  - `tree_a_bark`: flat bark albedo.
- The source is `Art/Blender~/tree_a.blend`, which keeps the raw mesh, the parts and the prop.

| Loop | Changed (one thing) | Palette | Form | Surface | Light | Scale | Worst miss |
|------|---------------------|---------|------|---------|-------|-------|------------|
| c1 | first card build: 280 cards at 2.0×, 8 branch tubes | 4 | 4 | 3 | 4 | 4 | cream fringe round every leaf (the cut-out's anti-aliased edge kept the white background) |
| c2 | atlas colour only from fully opaque texels (alpha > 0.95), bled outward | 4 | 4 | 4 | 4 | 4 | small: red-brown twig spots on some cards; blunt branch-tube ends poking through the canopy at close zoom |

**Every section is 4 or better: this passes.**

**Open:**
- **Wind.** The weights are in the mesh (vertex colour R), but URP Lit ignores them. Sway needs a
  small foliage shader.
- **Branch ends.** Tube ends sometimes show through gaps in the canopy; cap the chains a metre
  inside the canopy.
- **Bark.** It is a flat colour, with no painted texture yet.
- **One atlas for every card.** At close zoom the repetition is faint but visible; a second atlas
  row would add variety.

## crew_unit (2026-09-21)
- **Source:** concept `crew_0_lens_8000`, re-rendered de-lit
  (`Concepts~/crew_lens_delit_8000_00001_.png`).
- **Trellis2:** `TinyDiggers_Trellis2`, with `DecimateMesh` at 20,000 faces in qem mode and a
  2048 bake. Output: `Blender~/crew_lens_q20k.glb`.
- **Blender:**
  `blender -b --factory-startup -P Art/Tools/td_crew.py -- --in Blender~/crew_lens_q20k.glb --out Props/Crew/crew_unit --blend Blender~/crew_unit.blend --tris 20000 --shots Screenshots/Art/Crew`
- **Result:**
  - 14,301 triangles, 4 bones (rigid), clips Idle, Move, Work and Carry at 30 fps;
  - URP Lit material with back-face culling and smoothness 0.35;
  - a prefab with an Animator.
- **Open:** the band is orange rather than yellow; Trellis2 added a back lens; there is no glow
  underneath yet.

## crew_unit, remade (2026-09-21)
- **Built:** procedurally in Blender (`Art/Tools/td_crew_model.py`), at human scale: a ball
  0.285 m across, hovering 0.125 m up.
  `blender -b --factory-startup -P Art/Tools/td_crew_model.py -- --out Props/Crew/crew_unit --blend Blender~/crew_unit.blend --shots Screenshots/Art/Crew`
- **Result:**
  - 17,720 triangles, 7 flat materials, and a radial emission map on the lens;
  - bones Root, Body, Arm.L and Arm.R; clips Idle, Move, Work and Carry;
  - a hover-glow disc.
- **Replaces:** the Trellis2 build. Its GLB is kept in `Blender~/crew_lens_q20k.glb`.
