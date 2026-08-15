"""Straighten the firing line in a MoGe relief reconstruction.

The reconstruction is kept as-is -- full per-pixel detail, original texture -- because
that is what reads best. The one thing wrong with it is the firing bench: monocular depth
bows it, so the shooting points sit at depths differing by over a metre and the bays look
irregular.

Measured on the Kalyani range, the bench's best-fit line is parallel to the target wall to
within 0.2 degrees, so the bench really is straight in the room; the wobble is
reconstruction error, not architecture. This removes it.

Method: work in the room frame, take the front surface depth per lateral column, fit a
robust straight line, and shift each column by its residual. Bays keep their own recesses
and clutter -- only the low-frequency bow is taken out. The shift is weighted so it fades
to zero away from the bench, which stops the floor and back wall tearing at the seam.

usage: regularize_bench.py IN.glb OUT.glb
"""

import json
import struct
import sys

import numpy as np

sys.path.insert(0, "/home/ishan/code/vrshooting/scripts")
from build_room import fit_plane, orient
from merge_glb import read_glb

COL = 0.10          # lateral column width, m
SMOOTH_M = 0.35     # residual smoothing window, m
PASSES = 2          # measure-and-shift iterations


def room_frame(P):
    """Room axes: up from floor+ceiling, yaw from the floor's far edge."""
    fn, fd, _ = fit_plane(P[P[:, 1] < -1.2]); fn, fd = orient(fn, fd, [0, 1, 0])
    cn, cd, _ = fit_plane(P[P[:, 1] > 1.0]);  cn, cd = orient(cn, cd, [0, -1, 0])
    up = fn - cn
    up /= np.linalg.norm(up)

    fl = P[np.abs(P @ fn + fd) < 0.12]
    e1 = np.cross(up, [0.0, 0.0, 1.0]); e1 /= np.linalg.norm(e1)
    e2 = np.cross(up, e1)
    fe = fl[fl[:, 2] < np.percentile(fl[:, 2], 6)]
    A = np.stack([fe @ e1, fe @ e2], 1)
    A = A - A.mean(0)
    lat = np.linalg.svd(A, full_matrices=False)[2][0]
    right = lat[0] * e1 + lat[1] * e2; right /= np.linalg.norm(right)
    fwd = np.cross(up, right); fwd /= np.linalg.norm(fwd)
    if fwd[2] > 0:
        fwd, right = -fwd, -right
    return np.stack([right, up, -fwd]), float(-fd)


def _straighten(x, z):
    """Robust straight-line fit; returns slope, intercept and residuals."""
    keep = np.ones(len(x), bool)
    for _ in range(6):
        a, b = np.polyfit(x[keep], z[keep], 1)
        r = z - (a * x + b)
        keep = np.abs(r) < max(0.15, 2.0 * np.std(r[keep]))
    a, b = np.polyfit(x[keep], z[keep], 1)
    return a, b, z - (a * x + b)


def smooth(y, win):
    if win < 3:
        return y
    k = np.ones(win) / win
    pad = win // 2
    return np.convolve(np.pad(y, pad, mode="edge"), k, mode="valid")[:len(y)]


def main():
    src, dst = sys.argv[1], sys.argv[2]
    pos, uv, idx, png, mime = read_glb(src)
    finite = np.isfinite(pos).all(1)

    rng = np.random.default_rng(0)
    sample = pos[finite][rng.choice(int(finite.sum()), min(400000, int(finite.sum())), replace=False)]
    Rw, floor_y = room_frame(sample)

    Q = pos @ Rw.T
    y, z = Q[:, 1], Q[:, 2]

    # Counter top: the dense height band above the floor in the near field.
    near = Q[finite & (Q[:, 2] > -4.0) & (Q[:, 2] < 1.5)]
    hist, edges = np.histogram(near[:, 1], bins=120, range=(floor_y + 0.2, 0.6))
    top_y = float(edges[int(np.argmax(hist))])
    print("floor y {:.2f}  counter top y {:.2f}".format(floor_y, top_y))

    # Apron = the vertical face under the counter; its 85th-percentile depth per column
    # is the front surface.
    apron = finite & (y < top_y - 0.06) & (y > floor_y + 0.08) & (z > -4.0) & (z < 1.0)
    A = Q[apron]
    lo, hi = np.percentile(A[:, 0], 1), np.percentile(A[:, 0], 99)
    xs = np.arange(lo, hi, COL)
    cx, cz = [], []
    for x in xs:
        s = A[(A[:, 0] >= x) & (A[:, 0] < x + COL)]
        if len(s) > 400:
            cx.append(x + COL / 2)
            cz.append(np.percentile(s[:, 2], 85))
    cx = np.array(cx)

    total_shift = np.zeros(len(Q))
    for it in range(PASSES):
        A = Q[apron]
        cz = []
        for x in cx:
            sl = A[(A[:, 0] >= x - COL / 2) & (A[:, 0] < x + COL / 2)]
            cz.append(np.percentile(sl[:, 2], 85) if len(sl) > 200 else np.nan)
        cz = np.array(cz)
        good_c = np.isfinite(cz)
        a, b, resid = _straighten(cx[good_c], cz[good_c])
        rs = np.full(len(cx), np.nan); rs[good_c] = resid
        rs = np.interp(cx, cx[good_c], resid)
        rs = smooth(rs, max(3, int(SMOOTH_M / COL) | 1))
        if it == 0:
            print("bench line z = {:+.4f}x {:+.3f}  ({:.1f} deg off lateral)".format(
                a, b, np.degrees(np.arctan(a))))
            print("warp before: rms {:.3f} m  max {:.3f} m".format(
                np.sqrt(np.mean(resid ** 2)), np.abs(resid).max()))
            line_z = a * Q[:, 0] + b
            dz = Q[:, 2] - line_z
            w_depth = np.clip((1.5 - np.abs(dz - 0.15)) / 0.6, 0.0, 1.0)
            w_up = np.clip((top_y + 0.35 - y) / 0.25, 0.0, 1.0)
            w_dn = np.clip((y - (floor_y + 0.02)) / 0.18, 0.0, 1.0)
            w_side = np.clip(np.minimum(Q[:, 0] - cx[0], cx[-1] - Q[:, 0]) / 0.30 + 1.0, 0.0, 1.0)

            # The roof pillar stands in the bench's depth band but is not part of the
            # bench. Shifting its base while its top stays put shears it into a smear, so
            # find the columns it occupies -- ones with geometry well above counter
            # height near the firing line -- and leave them alone.
            # A mid-height band, bounded above so the ceiling itself -- which of course
            # sits directly over the bench at the same depth -- is not mistaken for a
            # pillar, and bounded below so the booth dividers (part of the bench) are not
            # either. Only a real column occupies the space between.
            ceil_y = float(np.percentile(Q[finite, 1], 99.5))
            tall = (finite & (y > top_y + 0.8) & (y < ceil_y - 0.35)
                    & (np.abs(dz) < 1.0))
            occupied = np.zeros(len(cx), bool)
            keepw = np.ones(len(cx))
            if tall.any():
                bins = np.clip(((Q[tall, 0] - cx[0]) / COL).astype(int), 0, len(cx) - 1)
                cnt = np.bincount(bins, minlength=len(cx))
                occupied = cnt > 500
                # Widen the no-go zone so the pillar is wholly inside it, then taper the
                # edges. A hard 0/1 boundary means neighbouring columns slide up to 1.5 m
                # while the pillar stays put, which shears it into a smear -- exactly the
                # artefact this is meant to avoid.
                occupied = np.convolve(occupied.astype(float),
                                       np.ones(7), mode="same") > 0.5
                keepw = 1.0 - occupied.astype(float)
                # A wide taper (~1.7 m) spreads the unavoidable shear over enough
                # distance that it stops reading as a crease at the pillar's foot.
                for _ in range(3):
                    keepw = np.convolve(np.pad(keepw, 8, mode="edge"),
                                        np.ones(17) / 17.0, mode="valid")[:len(cx)]
                keepw[occupied] = 0.0
                print("  pillar columns excluded: {} of {} (tapered)".format(
                    int(occupied.sum()), len(cx)))
            else:
                keepw = np.ones(len(cx))
            w_col = np.interp(Q[:, 0], cx, keepw, left=1.0, right=1.0)
            w = w_depth * w_up * w_dn * w_side * w_col
            w[~finite] = 0.0
        step = w * np.interp(Q[:, 0], cx, -rs, left=0.0, right=0.0)
        Q[:, 2] = Q[:, 2] + step
        total_shift += step
        print("  pass {}: residual rms {:.3f} m".format(it + 1, np.sqrt(np.mean(resid ** 2))))

    print("shifted {} of {} verts (max {:.3f} m)".format(
        int((np.abs(total_shift) > 0.01).sum()), len(Q), float(np.abs(total_shift).max())))

    # Re-measure on the corrected cloud.
    A2 = Q[apron]
    cz2 = []
    for x in cx:
        s = A2[(A2[:, 0] >= x - COL / 2) & (A2[:, 0] < x + COL / 2)]
        cz2.append(np.percentile(s[:, 2], 85) if len(s) > 200 else np.nan)
    cz2 = np.array(cz2)
    ok = np.isfinite(cz2)
    r2 = cz2[ok] - (a * cx[ok] + b)
    print("warp after : rms {:.3f} m  max {:.3f} m".format(
        np.sqrt(np.mean(r2 ** 2)), np.abs(r2).max()))

    out = (Q @ Rw).astype("<f4")
    out[~finite] = pos[~finite]
    write_glb(dst, out, uv.astype("<f4"), idx.astype("<u4"), png, mime)


def write_glb(path, pos, uv, idx, png, mime):
    blobs, off = [], 0

    def add(raw):
        nonlocal off
        pad = (-len(raw)) % 4
        blobs.append(raw + b"\x00" * pad)
        o = off
        off += len(raw) + pad
        return o, len(raw)

    good = np.isfinite(pos).all(1)
    p_e, u_e, i_e, t_e = add(pos.tobytes()), add(uv.tobytes()), add(idx.tobytes()), add(png)
    bv, acc = [], []

    def view(entry, target=None):
        d = {"buffer": 0, "byteOffset": entry[0], "byteLength": entry[1]}
        if target:
            d["target"] = target
        bv.append(d)
        return len(bv) - 1

    acc.append({"bufferView": view(p_e, 34962), "componentType": 5126, "count": len(pos),
                "type": "VEC3", "min": pos[good].min(0).tolist(),
                "max": pos[good].max(0).tolist()})
    acc.append({"bufferView": view(u_e, 34962), "componentType": 5126, "count": len(uv),
                "type": "VEC2"})
    acc.append({"bufferView": view(i_e, 34963), "componentType": 5125, "count": len(idx),
                "type": "SCALAR"})
    img_bv = view(t_e)

    g = {"asset": {"version": "2.0", "generator": "regularize_bench.py"},
         "extensionsUsed": ["KHR_materials_unlit"],
         "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}],
         "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1},
                                     "indices": 2, "material": 0}]}],
         "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0},
                                                 "metallicFactor": 0.0,
                                                 "roughnessFactor": 1.0},
                        "doubleSided": True,
                        "extensions": {"KHR_materials_unlit": {}}}],
         "textures": [{"source": 0}], "images": [{"bufferView": img_bv, "mimeType": mime}],
         "accessors": acc, "bufferViews": bv, "buffers": [{"byteLength": off}]}

    js = json.dumps(g, separators=(",", ":")).encode()
    js += b" " * ((-len(js)) % 4)
    binc = b"".join(blobs)
    total = 12 + 8 + len(js) + 8 + len(binc)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack("<II", len(binc), 0x004E4942)); f.write(binc)
    print("wrote {} ({:.1f} MB)".format(path, total / 1e6))


if __name__ == "__main__":
    main()
