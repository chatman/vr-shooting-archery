#!/usr/bin/env bash
# Open a hall GLB in f3d, a native viewer -- no browser, no server.
#
#   scripts/view_range.sh                 # walk the 10 m hall from the firing line
#   scripts/view_range.sh shooter         # start behind a firing point, aimed downrange
#   scripts/view_range.sh wide            # elevated three-quarter view of the whole hall
#   scripts/view_range.sh door            # standing in a front doorway
#   scripts/view_range.sh glass           # facing the glazed front wall
#   scripts/view_range.sh wide shot.png   # render that view to a file instead of a window
#   scripts/view_range.sh line other.glb  # any other model in output/
#
# --light-intensity is doing real work here. The GLB's ceiling lamps carry glTF-spec
# intensities in candela (35 lamps at 2200 cd, for a 6 m ceiling); VTK, which f3d renders
# with, treats that number as a plain multiplier and ignores distance falloff entirely, so
# at face value the room comes back pure white. Scaling by 0.005 puts it back where the
# spec intends. Engines that read candela properly (three.js, Blender's importer) need no
# such fudge -- do not "fix" the model to suit this viewer. Retune this if LIGHT_CD moves.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODEL="$ROOT/output/range_10m/range_10m.glb"
VIEW="${1:-line}"
OUT=""

for arg in "${@:2}"; do
    case "$arg" in
        *.glb) MODEL="$arg" ;;
        *.png) OUT="$arg" ;;
    esac
done

# eye -> aim, in the model's own frame: Y up, -Z downrange, target wall at z=0.
case "$VIEW" in
    line)    POS="-9,1.7,12.6";   AIM="6,1.3,2"    ;;  # along the firing line
    shooter) POS="0.5,1.6,10.8";  AIM="0.5,1.5,0"  ;;  # behind lane 21, aimed at its target
    wide)    POS="-16,5.2,27.5";  AIM="4,1,4"      ;;  # elevated three-quarter of the hall
    door)    POS="-10.5,1.65,28.8"; AIM="-3,1.4,0" ;;  # entering by the second glass door
    targets) POS="3,1.55,3";      AIM="-2.5,1.5,0" ;;  # up close at the target wall
    glass)   POS="0,1.7,21";      AIM="1.5,2.2,30" ;;  # facing the glazed front wall
    *) echo "unknown view '$VIEW' (line|shooter|wide|door|targets|glass)" >&2; exit 2 ;;
esac

ARGS=(
    "$MODEL"
    --light-intensity=0.005       # see note above
    --tone-mapping
    --camera-position="$POS"
    --camera-focal-point="$AIM"
    --camera-view-up=0,1,0
    --camera-view-angle=65        # f3d defaults to a 30 deg lens; the hall needs a wide one
)

if [ -n "$OUT" ]; then
    exec f3d "${ARGS[@]}" --resolution=1600,900 --output="$OUT"
fi

cat <<'KEYS'
f3d controls
  left drag    orbit            right drag / wheel   zoom
  middle drag  pan              S / T                toggle textures / tone mapping
  Q            ambient occlusion         E           edges
  R            raytracing (if built)     H           on-screen help
  ESC / Q-uit  close window
KEYS

exec f3d "${ARGS[@]}" --resolution=1600,900
