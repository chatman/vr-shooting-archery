"""Score how room-like a MoGe-reconstructed world is.

A panorama that MoGe reads well becomes an enclosed box: floor below, ceiling above,
walls a few metres out on all sides. A panorama it reads badly collapses into a shell
close around the capture point, or a shallow trough. These stats separate the two
without having to eyeball every render.

usage: mesh_stats.py WORLD.glb [WORLD.glb ...]
"""

import sys

import numpy as np

sys.path.insert(0, "/home/ishan/code/vrshooting/scripts")
from render_glb import load_glb

# Horizontal directions are binned into sectors; a real room has walls in every sector,
# a collapsed reconstruction has most sectors hugging the camera.
SECTORS = 12


def stats(path):
    pos, _, _ = load_glb(path)
    finite = np.isfinite(pos).all(1)
    pos = pos[finite]
    # Convention confirmed by sampling the mesh's own UV->position pairs: Y is up
    # (+Y ceiling, -Y floor), the horizontal plane is XZ, and the middle of the
    # panorama (u=0.5, where the targets are) points along -Z.
    y = pos[:, 1]

    horiz = np.linalg.norm(pos[:, [0, 2]], axis=1)
    ang = np.arctan2(pos[:, 2], pos[:, 0])
    sector = ((ang + np.pi) / (2 * np.pi) * SECTORS).astype(int) % SECTORS

    # Per-sector wall distance = far end of that direction's points.
    walls = np.array([
        np.percentile(horiz[sector == s], 95) if (sector == s).any() else 0.0
        for s in range(SECTORS)
    ])

    return {
        "verts": len(pos),
        "median_r": float(np.median(np.linalg.norm(pos, axis=1))),
        "floor_below": float(-np.percentile(y, 2)),
        "ceil_above": float(np.percentile(y, 98)),
        "downrange": float(-np.percentile(pos[:, 2], 1)),
        "wall_min": float(walls.min()),
        "wall_med": float(np.median(walls)),
        "wall_max": float(walls.max()),
        "frac_beyond_3m": float((horiz > 3).mean()),
    }


def main():
    rows = []
    for p in sys.argv[1:]:
        s = stats(p)
        s["name"] = p.split("/")[-1].replace(".glb", "")
        rows.append(s)

    hdr = ("name", "floor", "ceil", "downrng", "wall_min", "wall_med", "wall_max", ">3m")
    print("{:<22}{:>8}{:>8}{:>9}{:>10}{:>10}{:>10}{:>8}".format(*hdr))
    for s in rows:
        print("{:<22}{:>8.2f}{:>8.2f}{:>9.2f}{:>10.2f}{:>10.2f}{:>10.2f}{:>7.1f}%".format(
            s["name"], s["floor_below"], s["ceil_above"], s["downrange"],
            s["wall_min"], s["wall_med"], s["wall_max"], 100 * s["frac_beyond_3m"]))

    print("\nfloor/ceil are metres below/above the camera; downrng is how far the -Z")
    print("(target) wall sits. A believable range wants floor ~1.2-1.7, ceil ~1-1.5,")
    print("and downrng as large as possible -- 10 m is the brief.")


if __name__ == "__main__":
    main()
