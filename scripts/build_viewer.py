"""Generate viewer/index.html listing every .glb under output/.

The model list is generated rather than hand-written so newly produced worlds show up
without editing HTML. model-viewer is vendored in viewer/lib so the page works with no
network access.

usage: build_viewer.py
"""

import os

ROOT = "/home/ishan/code/vrshooting"
OUT = os.path.join(ROOT, "viewer", "index.html")

LABELS = {
    "output/range_10m/range_10m.glb":
        ("Specified 10 m hall - built to a brief (game-ready)",
         "40 lanes, 42 m x 30 m x 6 m, glazed front wall; every dimension exact"),
    "output/kalyani_world/range_symmetric.glb":
        ("Kalyani 10 m range - firing line straightened",
         "Full relief, bench de-warped so the shooting points align"),
    "output/kalyani_world/range_solid.glb":
        ("Kalyani 10 m range - solid (game-ready)",
         "Plane-fitted room + firing counter, 22 triangles, continuous floor"),
    "output/kalyani_world/range_main.glb":
        ("Kalyani 10 m range - main",
         "MoGe-2 metric mono from the 4096x3072 photo; target wall at 12.1 m"),
    "output/kalyani_world/range_alt.glb":
        ("Kalyani 10 m range - second angle",
         "MoGe-2 metric mono from the second photo; target wall at 11.7 m"),
    "output/bhopal_world/hall_view1.glb":
        ("Bhopal 10 m hall - down the firing line",
         "DA3 metric mono; ~20 m of hall, real-world scale"),
    "output/bhopal_world/hall_view2.glb":
        ("Bhopal 10 m hall - elevated along the line",
         "DA3 metric mono; ~16 m of hall, real-world scale"),
    "output/karni_world/world_merged.glb":
        ("Karni Singh 10 m hall - merged world", "5 photo views fused by DA3 multiview"),
    "output/world_closed_shell.glb":
        ("Generated range - closed shell", "text -> panorama -> MoGe, no culling"),
    "output/world_culled.glb":
        ("Generated range - culled", "text -> panorama -> MoGe, discontinuities dropped"),
}


def find_models():
    found = []
    for dirpath, _, files in os.walk(os.path.join(ROOT, "output")):
        for f in sorted(files):
            if f.endswith(".glb"):
                rel = os.path.relpath(os.path.join(dirpath, f), ROOT)
                found.append((rel, os.path.getsize(os.path.join(dirpath, f))))
    # Labelled headline worlds first, then everything else, per-view dumps last.
    order = list(LABELS)
    found.sort(key=lambda r: (order.index(r[0]) if r[0] in order else len(order),
                              "views/" in r[0], r[0]))
    return found


def main():
    models = find_models()
    opts = []
    for rel, size in models:
        label, note = LABELS.get(rel, (os.path.basename(rel).replace(".glb", ""), ""))
        mb = size / 1e6
        text = "{} - {:.0f} MB".format(label, mb)
        opts.append('<option value="/{rel}" data-note="{note}">{text}</option>'.format(
            rel=rel, note=note, text=text))

    html = """<!doctype html>
<meta charset="utf-8">
<title>vrshooting - 3D world viewer</title>
<script type="module" src="lib/model-viewer.min.js"></script>
<style>
  :root { color-scheme: dark; }
  body { margin:0; font:14px/1.5 system-ui,sans-serif; background:#111; color:#eee;
         display:flex; flex-direction:column; height:100vh; }
  header { padding:10px 14px; background:#1b1b1b; border-bottom:1px solid #333;
           display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
  select { background:#222; color:#eee; border:1px solid #444; padding:6px; font-size:14px;
           max-width:60vw; }
  #note { color:#9aa; }
  model-viewer { flex:1; width:100%; background:#0d0d0d; }
  #bar { height:3px; background:#2a7; width:0; transition:width .2s; }
  kbd { background:#222; border:1px solid #444; border-radius:3px; padding:1px 5px; }
</style>

<header>
  <strong>3D world viewer</strong>
  <select id="pick">%OPTIONS%</select>
  <span id="note"></span>
  <span style="margin-left:auto; color:#888">
    drag = orbit &middot; <kbd>scroll</kbd> = zoom &middot; two-finger / right-drag = pan
  </span>
</header>
<div id="bar"></div>
<model-viewer id="mv" camera-controls interaction-prompt="none"
              exposure="1.0" shadow-intensity="0" min-field-of-view="5deg"
              max-camera-orbit="Infinity 180deg Infinity" style="--poster-color:transparent">
</model-viewer>

<script>
  const pick = document.getElementById('pick');
  const mv   = document.getElementById('mv');
  const bar  = document.getElementById('bar');
  const note = document.getElementById('note');

  function load() {
    const opt = pick.selectedOptions[0];
    note.textContent = opt.dataset.note || '';
    bar.style.width = '0%';
    mv.src = opt.value;
  }
  mv.addEventListener('progress', e => {
    bar.style.width = (e.detail.totalProgress * 100).toFixed(0) + '%';
  });
  mv.addEventListener('load', () => {
    bar.style.width = '100%';
    // Frame the model from a slight elevation rather than dead-on.
    mv.cameraOrbit = '15deg 72deg auto';
    setTimeout(() => bar.style.width = '0%', 600);
  });
  pick.addEventListener('change', load);
  load();
</script>
"""
    html = html.replace("%OPTIONS%", "\n    ".join(opts))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, "w").write(html)
    print("wrote {} listing {} models".format(OUT, len(models)))
    for rel, size in models:
        print("  {:>7.0f} MB  {}".format(size / 1e6, rel))


if __name__ == "__main__":
    main()
