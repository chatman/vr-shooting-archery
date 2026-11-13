> **This project is part of Deveshi Khanna's Ph.D. work.**

# VR Air Pistol Shooting Range (Quest 3)

A 40-lane, 10 m ISSF air-pistol range for Meta Quest 3. The hall is built procedurally
from a written spec — every dimension exact and self-checked — then populated with fellow
competitors, electronic scoring and an animated crowd, and shipped as a Unity/URP app.

---

## The range (`scripts/build_range.py`)

The hall is generated from a measured brief, with no model in the loop, so 10.00 m is
10.00 m, the shell is closed and watertight, and every face has a correct outward normal.
Parametric quads plus procedural tileable textures export to
`output/range_10m/range_10m.glb` (6,120 triangles, 0.82 MB, 14 materials, 35 lights).

- **Frame:** Y up, floor at y=0, **−Z downrange**; target wall at z=0, lanes at x = −19.5 … +19.5.
- **Layout:** hall 42 × 30 × 6 m; 40 ISSF target faces at 1 m pitch in green SIUS housings;
  a 40 m timber bench with its downrange face exactly 10 m from the targets; 40 chairs; a
  glazed front wall with four doors; 140 ceiling panels (35 baked lamps).
- **Self-checking:** the build re-opens the GLB it just wrote and verifies it with ~35
  assertions — lane pitch, distances, extents, sign legibility, the printed target card,
  and door/panel/lamp counts. Intent is not evidence; a swapped axis still renders plausibly.

```bash
PY=/home/ishan/comfy-workspace/comfy-venv/bin/python   # numpy + PIL + torch
$PY scripts/build_range.py                              # ~2 s, builds and self-checks
$PY scripts/render_scene.py output/range_10m/range_10m.glb output/range_10m/renders
```

Edit the constants at the top of `build_range.py` to change lane count, pitch, range
distance, bench size, doors or the light grid.

---

## Quest 3 app (`quest-app/`)

Puts the hall on a Meta Quest 3 — URP, XR plumbing, a baked lightmap, and the player at a
firing point facing downrange. Unity 6000.5.10f1, Meta XR SDK.

Everything is reproducible from a clean clone via the editor **Quest** menu (player
settings, URP, model import, scene, bake); `ArcheryRangeSetup.ConfigureAll` runs them in
one headless launch, then `quest-app/deploy.ps1` builds, installs to a tethered Quest and
launches.

Two things that are not optional:

- **URP** — the materials, the two glass materials especially, only import correctly on URP.
- **The bake is the lighting** — 35 lamps and the emissive ceiling are baked (realtime is
  off the Quest budget). A headless GPU bake silently does nothing, so the setup uses
  Progressive CPU and fails loudly if it produces zero lightmaps.

### Shooters, scoring and crowd

- **Fellow competitors** — 25 of the 40 lanes carry a rigged air-pistol shooter, each with
  a randomized shirt colour and an independent, slow shot rhythm (a shot every few dozen
  minutes). Firing plays a 3-D shot report — loud on an adjacent lane, faint down the hall.
  (`PopulateShooters.cs`, `FellowShooter.cs`)
- **SIUS scoring monitors** — a screen on every firing point draws a ringed target with the
  plotted shots, a shot-value list, the series total and the big current score; a fired
  shot registers live. (`ShotMonitor.cs`, `AddMonitors.cs`)
- **Animated audience** — a two-tier grandstand: rigged front rows that idle and applaud,
  rigid rear rows that sway, each on its own random rhythm. (`Spectator.cs`, `AddAudience.cs`)
- **POV render tooling** — editor harnesses render POV walkthrough videos headlessly,
  driving the shooter and crowd animators frame by frame. (`RenderPovTour.cs`, `RenderFellow.cs`)

---

## Also in the repo

A generative **text → 3D world** pipeline (ComfyUI: FLUX + LayerPano3D panorama → MoGe-2
mesh) lives under `workflows/` and `scripts/` — an alternative route that produces a world
from a text prompt. The procedural hall above is what the Quest app ships.

## Limitations

- The hall is lit by a low-resolution bake; raise the texel density once the scene settles.
- Rear-audience fidelity is deliberately low (distance plus the Quest skinning budget).
- No moving target carriers or booth dividers yet — the obvious next parts to add.
