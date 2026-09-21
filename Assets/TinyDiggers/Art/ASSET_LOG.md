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
