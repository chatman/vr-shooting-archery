# 3D world generation (ComfyUI)

Three pipelines:

1. **[Text → 3D world](#text--3d-world-comfyui)** — FLUX + LayerPano3D panorama → MoGe-2 → mesh.
2. **[Photos → 3D world](#photos--3d-world-real-venue)** — real photographs of Dr. Karni Singh
   Shooting Range → Depth Anything 3 multi-view → merged mesh.
3. **[Spec → 3D world](#spec--3d-world-40-lane-10-m-hall)** — a written brief → procedural
   geometry. No model in the loop; every dimension is exact and checked.

---

# Text → 3D world (ComfyUI)

Generates a textured 3D world from a text prompt, entirely with ComfyUI native nodes.

Prompt used:

> 10 meter shooting range with 5 targets, a table 10 meters away, good lighting,
> in a closed room with air conditioners installed

## Pipeline

```
text ──> FLUX.1-dev + LayerPano3D LoRA ──> 360° equirectangular panorama (1440×720)
                                              │
                                              ▼
                             MoGe-2 ViT-L panorama inference
                        (12 perspective splits, Poisson-merged depth)
                                              │
                                              ▼
                              textured triangle mesh ──> .glb
```

Two stages, one graph: `workflows/text_to_3d_world.json`.

There is no single "text → 3D world" checkpoint here. Panorama-plus-monocular-geometry is
the approach LayerPano3D itself uses, and both halves are supported natively by this
ComfyUI build (`MoGePanoramaInference` / `MoGePointMapToMesh`), so no custom nodes are needed.

## Models downloaded

| Model | Where it went | Why |
|---|---|---|
| [MoGe-2 ViT-L (normal)](https://huggingface.co/Ruicheng/moge-2-vitl-normal) `model.pt`, 1.3 GB | `models/geometry_estimation/moge-2-vitl-normal.pt` | Panorama → 3D geometry. ComfyUI's loader already unwraps the official `{model, model_config}` checkpoint and remaps DINOv2 keys, so the upstream file works as-is. |
| [LayerPano3D FLUX panorama LoRA](https://huggingface.co/ysmikey/Layerpano3D-FLUX-Panorama-LoRA) `pano_lora_720*1440_v1`, 26 MB | `models/loras/layerpano3d_flux_pano_comfy.safetensors` | Makes FLUX emit true equirectangular panoramas. Needed conversion — see below. |

FLUX.1-dev fp8 was already installed and is used unchanged.

### The LoRA needed converting

The LayerPano3D LoRA ships in XLabs/x-flux key format
(`double_blocks.N.processor.qkv_lora1.down.weight`). ComfyUI knows this layout
(`comfy/lora_convert.py:convert_uso_lora`) but only auto-detects it when
`single_blocks.37.*` is present — this LoRA trains only single blocks 1–4, so detection
misses and **every key is silently dropped, making the LoRA a no-op with no error**.

`scripts/convert_pano_lora.py` applies the same mapping ahead of time. Verify a run
loaded it by checking for the absence of "lora key not loaded" in the ComfyUI log.

## Usage

```bash
cd /home/ishan/comfy-workspace   # scripts use ComfyUI's own venv, no extra deps

# full pipeline
./comfy-venv/bin/python /home/ishan/code/vrshooting/scripts/run_workflow.py \
    /home/ishan/code/vrshooting/workflows/text_to_3d_world.json

# override any node input; --drop skips meshing for fast seed sweeps
... run_workflow.py workflows/text_to_3d_world.json \
      --drop=10,11,12,13 --set=7.seed=4242 --set="3.text=your prompt"

# mesh an existing panorama
... run_workflow.py workflows/pano_to_3d.json --set=1.image=panoeval/g21.png

# inspect / render results
... scripts/mesh_stats.py output/*.glb
... scripts/render_glb.py output/world_closed_shell.glb output/renders
```

Runtime end-to-end: ~75 s on an RTX A6000 (41 s FLUX sampling, ~27 s MoGe).

## Output

| File | Notes |
|---|---|
| `output/panorama.png` | 1440×720 equirectangular source image |
| `output/world_closed_shell.glb` | 1,036,800 verts / ~2.07 M tris. Full grid, no culling — closed shell, but stretched triangles across depth jumps |
| `output/world_culled.glb` | `discontinuity_threshold=0.04`. Cleaner silhouettes, but holes where near and far surfaces meet |
| `output/renders/` | Novel viewpoints, proving real parallax |

Seed 21 with the prompt in the workflow is the kept result: it is the one seed of ~15
that produced exactly five targets *and* clear air-conditioner units.

## Coordinate convention (worth knowing)

The mesh is **Y-up**, and the middle of the panorama (`u=0.5`) points along **−Z** —
so −Z is downrange. This is *not* the convention in
`comfy/ldm/moge/panorama.py:spherical_uv_to_directions` (which is written Z-up with the
centre at −X); the mesh node reorients. Confirmed by sampling the GLB's own UV↔position
pairs. Get this wrong and renders come out upside down and aimed at the ceiling.

## Limitations

- **Depth is compressed.** MoGe puts the target wall ~1.6 m away, not 10 m, while floor
  and ceiling land at believable heights (0.7 m / 1.5 m). Monocular geometry
  under-estimates distance to large flat textureless surfaces, and panorama mode
  discards MoGe's metric scale outright, so absolute scale is arbitrary. A uniform
  rescale cannot fix it — it would stretch the ceiling too. `scripts/mesh_stats.py`
  reports `downrng` per candidate; seeds `g11`/`g33` reach ~3.3 m if depth matters more
  than the exact target count.
- **It is a shell, not a room.** One panorama only sees surfaces from one point. The
  mesh holds up under head-motion parallax (the VR 6DoF-panorama use case), not walking
  across the room. Everything occluded from the capture point is missing.
- **Counting is unreliable.** FLUX hit "5 targets" by seed search, not by instruction.
  Asking for numbered targets actively backfired — it produced lane-number placards
  instead of five bullseyes.

---

# Photos → 3D world (real venue)

Reconstructs the **10 m range hall at Dr. Karni Singh Shooting Range**, Tughlakabad, New
Delhi, from real photographs rather than a generated panorama.

```
10 photographs (same hall) ──> Depth Anything 3 (base), multiview mode
                                 │   joint geometry + estimated camera poses,
                                 │   all views in one shared coordinate frame
                                 ▼
                         per-view textured meshes ──> merged .glb
```

Workflow: `workflows/photos_to_3d.json`, generated by `scripts/build_multiview_workflow.py`
(10 LoadImage + a 9-deep ImageBatch chain is too repetitive to hand-write).

## Source photographs

All ten are from the **Asian Rifle/Pistol Championship 2026** at this venue, via
`asia-shooting.org` — deliberately one event, so the decor, lighting and layout are
consistent. Originals are 3245×2443 up to 6000×4000; they are centre-cropped to 3:2 and
resized to 1512×1008, because DA3 multiview needs every view in one batch at one size.

Full URL list: `output/karni_photos/SOURCES.md`. Photos are in `output/karni_photos/`.

Verified as the 10 m hall by content: the numbered 10 m target line, overhead target
monitors, blue firing-point flooring, SAI/NRAI/Walther/SIUS boards and spectator seating.

## Model downloaded

| Model | Path | Notes |
|---|---|---|
| [Depth Anything 3 base](https://huggingface.co/Comfy-Org/Depth-Anything-3) 541 MB | `models/geometry_estimation/depth_anything_3_base.safetensors` | Multiview mode requires the Small or Base variant; Mono/Metric cannot do it. |

Two traps worth recording:

- **Load it as `fp32`.** With the default dtype the camera decoder raises
  `mat1 and mat2 must have the same dtype, but got Float and Half` —
  `comfy/ldm/depth_anything_3/camera.py` casts activations with `feat.float()` while the
  weights stay half.
- **`mode` is a dynamic combo.** Its sub-inputs are namespaced in the API graph:
  `mode.ref_view_strategy`, `mode.pose_method` — not bare names.

## Output

| File | Notes |
|---|---|
| `output/karni_world/world_merged.glb` | 2.67 M verts / 5.23 M tris, 5 best views, each keeping its own texture |
| `output/karni_world/views/*.glb` | All 10 per-view reconstructions |
| `output/karni_world/renders/` | Novel viewpoints of the merged world |
| `output/karni_photos/` | The 10 source photographs + `SOURCES.md` |

Inference for all 10 views takes ~9 s.

## How well it worked

**The alignment is real.** DA3 recovered a consistent shared frame: across views the
sponsor wall stays one plane, the floor stays one plane, and the 10 m target line lands in
the same place. Relative depth of shooters standing down the firing line is correct.

**Confidence splits the views sharply.** Five registered densely (0.87–1.38 M verts before
thresholding: views 00, 01, 02, 07, 08); four were almost entirely culled as low-confidence
(view06 kept 1,622 verts). Only the five good ones are merged. `scripts/merge_glb.py`
concatenates them — multiview already puts them in one frame, so no transform is needed.

## Limitations

- **It is 2.5 D per view, not a watertight room.** Each view is a depth-map relief, so
  everything occluded from that camera is a hole. Merging five views fills some of it, but
  this is not a closed, walkable room.
- **Ghosting from transient content.** The photos are different moments — different
  shooters standing in different places. The *room* fuses correctly; the *people* stack up
  as overlapping duplicates. Photos from a single continuous capture would avoid this;
  press photos of an event cannot.
- **Scale is relative.** DA3 base returns no metric scale, so the world is
  correctly-proportioned but arbitrarily sized.
- **Thresholds are a trade-off.** `discontinuity_threshold=0.015`,
  `confidence_threshold=0.35` cut the stretched triangles that span depth jumps, at the
  cost of roughly half the vertices. The looser default (0.04 / 0.1) is in git history if
  coverage matters more than cleanliness.

---

# Solid room proxy (game-ready)

`scripts/build_room.py` converts a single-view depth relief into engine-usable geometry.

The relief straight out of MoGe is not usable in a game: the floor is a torn ribbon that
stops wherever the counter occluded it, the firing bench is a hollow floating slab, and
every depth jump leaves a ragged edge. Occluded surfaces are simply absent, and no amount
of threshold tuning invents them.

So the room is *fitted* rather than carved:

1. Fit the floor and ceiling planes; their average normal defines "up".
2. Take the room's yaw from the **far edge of the floor** — the line where floor meets
   target wall. Fitting that distant wall directly is badly conditioned; its floor edge
   is not.
3. Build a closed box: floor, ceiling, two side walls, target wall, back wall.
4. Fit the firing counter from the near, below-eye horizontal slab and extrude it to the
   floor as a solid box.
5. Bake each surface's texture by projecting it back through the recovered pinhole camera
   into the source photograph.

The camera is recovered from the mesh itself — it is an unprojected depth map, so a
pinhole fits to ~1e-7. That makes the reprojection exact.

## Handling what the camera never saw

A texel counts as observed only if the captured depth at that pixel *matches* the plane
(`|scene - d| < tol`). Merely rejecting nearer occluders lets a texel sample whatever sits
behind the plane, which is how bench timber smeared across the floor.

Unobserved texels are filled by tiling a real observed patch, chosen as the
**lowest-variance** well-observed window. That matters: tiling an arbitrary patch stamped
copies of the range's KSA banner across the target wall, while the flat-window rule picks
plain turf, ceiling tile or bare timber, which repeats without reading as duplication.
The patch is full-height where possible, so repetition is horizontal only — a
160px-tall patch put a second row of targets halfway up the wall.

Two further guards:

- **Colour-outlier rejection**, applied only to faces that are genuinely one material
  (median deviation < 30). On turf a stray dark texel is obviously wrong; on the signed
  target wall the spread is wide, the test is skipped, and the logos survive.
- **The counter's vertical faces share one timber texture.** Only its top and shooter-side
  were photographed; the downrange face and both ends filled themselves with turf from
  behind the box and came out green. The donor face is picked by red-over-green.

## Output

`output/kalyani_world/range_solid.glb` — **22 triangles, 0.8 MB**, 11 textured quads.
Room 11.1 m wide x 14.3 m long x 3.08 m high; counter 6.6 m x 1.5 m, top 1.30 m above the
floor. Metric, from MoGe's scale.

## Limitations

- **The back wall was never photographed** (it is behind the camera) and is flat grey.
- **The roof pillar is not modelled.** It is a prominent feature of the real room; fitting
  it needs a clustering pass that is not written yet.
- **Fitted, not measured.** Everything is planar by construction, so real detail — the
  booth dividers, target frames, skirting — lives in the texture rather than the geometry.
  The full-detail relief is still available as `range_main.glb` for reference.

---

# Spec → 3D world (40-lane 10 m hall)

`scripts/build_range.py` builds a 40-lane 10 m air pistol hall from a written brief. The
other two pipelines *infer* a world from an image and inherit its errors — compressed
depth, arbitrary scale, holes where the camera saw nothing. This one inverts that: the
room is a list of measurements, so 10.00 m is 10.00 m, the shell is closed and watertight,
and every face carries a correct outward normal.

```
brief ──> parametric quads (merged per material) ──┬──> range_10m.glb
          + procedural tileable textures           └──> measured back out of the file
```

## Reading the brief

The brief says 30 m wide, 42 m long, with 40 targets at 1 m pitch on the back wall and a
40 m table along the entire length. That closes exactly one way: **the 42 m axis is the one
the target wall spans, and 30 m is the downrange depth.** A 40 m table cannot stand across
a 30 m width without leaving the targets nowhere to go, and 40 targets at 1 m pitch need
39 m of wall. What falls out is a real 10 m hall — 10 m of wall to firing line, 1 m of
bench, 2 m to the chairs, and 17 m of hall behind them for audience and circulation. The
table is given as 40 m, so in a 42 m hall it stops 1 m short at each end.

Frame: Y up, floor at y=0, **−Z is downrange** (same convention as the panorama meshes).
Target wall at z=0, glazed front wall at z=30, lanes at x = −19.5 … +19.5.

| | |
|---|---|
| Hall | 42.0 × 30.0 × 6.0 m |
| Targets | 40 ISSF 10 m air pistol faces, 1 m pitch, centres 1.5 m |
| Target housing | Green SIUS unit, 0.34 × 0.54 × 0.18 m, card recessed into the front face and the wordmark above it — proportions taken off `sius.jpg` |
| Target card | 170 × 170 mm; 10-ring 11.5 mm, inner ten 5.0 mm, +16 mm diameter per ring out to 155.5 mm; rings 7–10 inside the 59.5 mm black; 0.15 mm ring lines |
| Lane signs | 1–40, black on yellow, 500 × 300 mm, centred 1 m above each target — 216 mm digits, sized so they read from the back wall 30 m away |
| Firing points | the same 1–40, 400 × 240 mm, high on the bench's audience-facing face, one per lane; both sets share one atlas texture |
| Firing line | bench's downrange face, exactly 10 m from the target wall |
| Bench | one boxy volume 40 × 1 × 1 m, no legs, timber |
| Chairs | 40, 1 m pitch, seat centres 2 m in front of the bench |
| Floor | dark green downrange of the firing line, black matte behind it |
| Ceiling | 140 emissive panels (10 rows × 14), 35 of them carrying KHR_lights_punctual lamps at 2200 cd |
| Front wall | toughened-glass curtain wall: 14 bays of 3 m, aluminium mullions, two transoms |
| Doors | 4 glass doors set into four of the bays, as **real openings** in the glazing |

## Usage

```bash
cd /home/ishan/code/vrshooting
PY=/home/ishan/comfy-workspace/comfy-venv/bin/python   # numpy + PIL + torch, no extra deps

$PY scripts/build_range.py                        # ~2 s, builds and self-checks
$PY scripts/render_scene.py output/range_10m/range_10m.glb output/range_10m/renders
$PY scripts/build_viewer.py                       # relist models in viewer/index.html
$PY scripts/flythrough.py --jobs 14                # walkthrough video, ~7 min

scripts/view_range.sh                             # native viewer, no browser (f3d)
scripts/view_range.sh wide shot.png               # ...or render a view straight to a file
```

`view_range.sh` opens the hall in [f3d](https://f3d.app) (`apt install f3d`) with five
preset viewpoints — `line`, `shooter`, `wide`, `door`, `targets`. One flag in it is load
bearing: **`--light-intensity=0.02`**. The GLB's 40 lamps carry glTF-spec intensities in
candela (300 cd each); VTK, which f3d renders through, treats that number as a plain
multiplier, so at face value the hall comes back pure white. Viewers that read candela
properly — three.js/model-viewer, Blender's importer — need no such fudge, so this belongs
in the launcher, not in the model.

Edit the constants at the top of `build_range.py` to change the hall — lane count, pitch,
range distance, bench size, door positions and the light grid are all parameters.

## Output

| File | Notes |
|---|---|
| `output/range_10m/range_10m.glb` | **6,120 triangles, 0.82 MB.** 14 materials, one primitive each, 35 punctual lights |
| `output/range_10m/renders/` | Eight viewpoints: shooter, along the firing line, from a door, downrange, at the target wall, high wide, glass wall, and from the back of the hall — the legibility case |

Textures are generated in the script, not loaded — tileable by construction, because all
the noise is built from integer-frequency sinusoids. Sample a normal noise field instead
and a seam appears every repeat, which on a 42 m wall is unmissable.

The SIUS housing textures only its front face; the other five take a flat green material.
One texture wrapped around the box would print the wordmark down its sides as well.

The target card is drawn 4× oversize and filtered down. Its ring lines are specified at
0.1–0.2 mm on a 170 mm card — 0.9 px at the card's 1024 px, which PIL can only draw as a
hard 1 px line, aliased into a dashed ellipse on the small rings. Supersampling puts a
real sub-pixel hairline on the card, which is what a ring line is.

## The build checks itself

`build_range.py` re-opens the GLB it just wrote and measures it — 35 assertions covering
lane pitch, target height, wall-to-firing-line distance, bench size, chair spacing, the
floor colour split, hall extents, glazed area, sign legibility and placement, the printed
target card, the housings enclosing it, and the door, panel and lamp counts. Intent
is not evidence: a swapped axis or a wrong `tiles=` still builds, still renders plausibly,
and still ships a 9 m range. Two caught real mistakes — end mullions centred on the wall
edge that put the hall 100 mm over its 42 m width, and a glazed area that only balances if
every bay is cut and every door opening subtracted.

```
[ok] wall -> firing line                  10.000 m  (want 10)
[ok] target pitch                          1.000 m  (want 1)
[ok] bench face -> chair centre            2.000 m  (want 2)
[ok] glazed area                         241.440 m2 (want 241.44)
[ok] sign columns aligned                  0.000 m  (want 0)
[ok] target sign digit height              0.216 m  (want >= 0.15)
[ok] black aiming mark (ink)              59.106 mm (want 59.2)
```

Winding is checked the same way, at build time. `Scene.rect` takes the direction the face
is supposed to look and refuses the quad if `cross(u, v)` disagrees. Backwards winding is
invisible in the data and shows up only as a hole in a backface-culled render — the first
build had the entire shell inside-out, and with culling misread as well the two errors
cancelled and the room looked fine.

## Rendering

`render_glb.py` splats vertices, which is right for a MoGe relief (one vertex per pixel)
and renders *nothing* for a hall made of 4-vertex quads. `scripts/render_scene.py` is a
real triangle rasteriser for that case: near-plane clipping, perspective-correct
attributes, z-buffer, backface culling, and shading from the GLB's own punctual lamps.
Raster is per-triangle numpy into a uv/normal/position G-buffer; one vectorised torch pass
then shades every pixel against all 35 lamps. 2× supersampled, ~3 s a frame.

Transparency is a second pass: opaque geometry first, then each glass layer rasterised
against — but never into — that depth buffer, and composited back to front. Shading stays
linear until the composite is done, because tone-mapping each layer separately
double-compresses whatever shows through the glass. Smooth materials also get a
Blinn-Phong lobe, which is what makes the glazing read as glass at all: with only a
diffuse term, a 16 %-opacity sheet in front of a dark exterior is nearly invisible. The
lamp reflections in the panes are that lobe.

## Walkthrough video

`scripts/flythrough.py` renders `output/range_10m/walkthrough.mp4` — 34 s, 24 fps,
1280×720. The shot list: in through the door nearest lane 23, down the hall to firing
point 23, turn and look back at the doors, sweep left and right along the firing line,
then back downrange and zoom in on target 23.

Camera keyframes are written as look-**at** points, because that is how a shot is
described — "aim at the target" — but they are converted to yaw/pitch immediately and
interpolated as angles. Interpolating the aim point instead swings the view at wildly
uneven angular speed whenever the target is close to the camera, and a 180° turn through
an aim point is undefined at the halfway mark. The turn-and-look-back keyframe is aimed
1 m to one side for the same reason: at exactly 180° the direction of the spin is
arbitrary. Zoom interpolates geometrically, so 70°→35° looks like 20°→10°.

Frames render in parallel — the rasteriser is a per-triangle numpy loop, so it is CPU
bound and scales across processes while the shading passes share one GPU. 821 frames take
about 7 minutes on 14 workers. Frames are cached by index, so a re-run only renders what
is missing; delete the ones you want redone.

**The zoom is what forced mipmapping.** At the end of the push-in the 1024 px card texture
lands on ~540 px of screen, and a point sampler skips half of every 0.9 mm-wide ring line,
so the rings arrive on screen as dashes — the card was correct, the sampler was not.
`render_scene.py` now picks a mip level per pixel from log2(texels per pixel), which fixes
the rings and also stops the timber wall stippling at the far end of the hall. Texture
sampling moved to the GPU with it — eight gathers over every textured pixel cost more in
numpy than the entire rest of the renderer.

One frame in the walkthrough then killed a worker with a CUDA device-side assert: frame
71, where the camera passes through the plane of the glass door. A triangle clipped at the
near plane and seen almost edge-on yields pixels at absurd depth whose perspective-correct
uv comes back at 1e30, which indexes a texture out of bounds. The fix is at the source —
require `iz > 1e-6` in the rasteriser rather than merely positive — with a clamp in the
sampler behind it, because an out-of-range index on the GPU is not an exception you can
catch per pixel.

## Limitations

- **Lambert plus one Blinn-Phong lobe, no shadows.** The bench casts none, and the lamps are point lights
  standing in for area panels, so the lighting reads flatter than a real hall. That is the
  preview renderer's limit, not the model's — the GLB carries roughness and emissive, so
  an engine will do better with it.
- **Nothing is instanced.** All 40 chairs are baked into one merged buffer. Fine at 5 k
  triangles; if the chairs ever get detailed, they should become a node reference instead.
- **No target carriers or booth dividers.** Real 10 m ranges have a moving carrier per
  lane and dividers between firing points. The brief did not ask for them and they are the
  obvious next parts to add.
- **Doors are closed leaves in real openings.** Hide or animate the `door_glass` node and
  the doorway is walkable; there is no hinge rig.
- **There is nothing outside the glass.** The curtain wall is glazed at 16 % opacity over
  an empty exterior, so it reads the way real glass reads at night from a lit room — a
  grey mirror rather than a window. Drop in a ground plane and a sky and it becomes a
  window; that is a level-dressing decision, not a model one.
- **Chair backs are 0.82 m, not taller.** They share the lanes' 1 m pitch, so a taller
  back stands directly in front of its own firing point number. The plates sit high on the
  bench face and the backs are a realistic height; both were needed for the numbers to
  survive the view from the back of the hall.
- **The hall is 30 m deep and the shooting only uses 13 m of it.** That is what the brief
  asks for. The 17 m behind the chairs is empty floor waiting for spectator seating.

---

# Quest 3 app (Unity)

`quest-app/` puts the hall on a Meta Quest 3. It consumes `output/range_10m/unity/` — the
FBX, its nine textures and the generated `RangeSetup.cs` — and adds only what a headset
needs: URP, the XR plumbing, a lightmap, and a scene with the player standing at firing
point 23 facing downrange.

Unity 6000.0.81f1, Meta XR SDK 205.0.0. Built and verified: 48.2 MB APK, `arm64-v8a`,
`android.hardware.vr.headtracking`, min SDK 32.

## Usage

```bash
cd quest-app
./deploy.ps1                # build, install to a tethered Quest, launch
./deploy.ps1 -SkipBuild     # reinstall the last APK without rebuilding
```

Developer Mode must be on and the in-headset "Allow USB debugging" prompt accepted, or
`adb devices` reports `unauthorized` and the install fails. The app appears under
**Library ▸ Unknown Sources**, not with the store apps.

Everything the deploy script depends on is reproducible from a clean clone via the
**Quest** menu in the editor, in order: player settings, URP, model import, scene, bake.
`ArcheryRangeSetup.ConfigureAll` runs the first four in one headless editor launch.

## URP is not optional

`RangeSetup.FixMaterials` looks up `Universal Render Pipeline/Lit` and aborts if it is
missing, so the hall's materials — both glass materials especially — only import correctly
on URP. A default Unity project is not on URP, so `Quest ▸ 2` creates the pipeline asset
and assigns it to both Graphics and Quality settings before the import runs. MSAA 4×, HDR
off, one shadow cascade, no opaque texture: the room is lit by a bake, so the shadow and
HDR budget buys nothing.

The import settings are the ones this repo's own `output/range_10m/unity/README.md`
specifies, applied through `ModelImporter` rather than by hand — scale 1 with Convert
Units, lightmap UVs on, normals imported rather than recalculated. The hall lands at
**42.0 × 6.0 × 30.0 m**, which matches the build's own round-trip assertion.

## The bake is the whole lighting

The 35 lamps and the emissive ceiling panels are baked-only, by design — 35 realtime point
lights is not a Quest budget. Until the bake runs, the hall renders black.

**A headless bake with the GPU lightmapper silently does nothing.** Progressive GPU needs
an OpenCL device; a batch-mode editor has none, and `Lightmapping.Bake()` returns normally
having baked nothing at all, so the run exits 0 and looks like a success. The first bake
here did exactly that. `ArcheryRangeSetup` now uses Progressive CPU and, more importantly,
counts `LightmapSettings.lightmaps` afterwards and fails loudly on zero — a bake that
cannot report its own failure is worse than one that never ran.

At the README's recommended 3 texels per unit the result is one lightmap, 395 KB of EXR
plus a 44 KB directionality map. The default 40 texels/unit over ~2,500 m² of surface is
the trap that warning exists for.

## Limitations

- **The FBX is copied into `quest-app/Assets/Models/`, not referenced.** Unity will only
  import assets under `Assets/`, so rebuilding the hall means re-running the copy — the
  Unity project can silently drift from `output/`. The clean fix is for
  `scripts/export_unity.py` to write straight into `quest-app/Assets/Models/`.
- **No interaction yet.** The player stands at firing point 23 and can look around. There
  is no bow, no arrow, no scoring — the Interaction SDK is installed but unused.
- **The bake is low resolution.** 3 texels/unit is the right starting point for iteration
  speed, not a shipping value. Raise it once the scene stops changing.
- **Baked on CPU, on this machine, with no OpenCL device.** A workstation with a working
  GPU lightmapper will bake the same scene far faster and can afford more texels.
- **Colliders are mesh colliders, from `RangeSetup`.** Fine for a static room; if physics
  props get added, the floor wants a box collider instead.
