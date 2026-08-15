"""Turn a single-view depth relief into solid, game-ready room geometry.

The raw MoGe mesh is a per-pixel relief: the floor is a torn ribbon that stops wherever
the shooting bench occluded it, the bench is a hollow floating slab, and every depth jump
leaves a ragged edge. None of that is usable in an engine.

This fits the room as planes instead, and rebuilds it as a handful of clean quads:

  * floor / ceiling / two side walls / target wall / back wall  -> a closed box
  * the firing-point counter and the roof pillar                 -> solid boxes

Each surface gets its own texture, baked by projecting the surface back through the
recovered pinhole camera into the source photograph. Where a surface was hidden at capture
time (floor behind the counter, wall behind the pillar) the texel has no observation, and
is filled from the nearest observed texel -- so the floor reads as continuous turf rather
than stopping at the occlusion boundary.

Geometry is quads, not grids: the bake already carries the perspective, so 4 verts per
face is enough. The result is ~50 triangles instead of 3 million.

usage: build_room.py IN.glb OUT.glb [--debug-dir DIR]
"""

import json
import struct
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

sys.path.insert(0, "/home/ishan/code/vrshooting/scripts")
import render_glb as R

DEPTH_W, DEPTH_H = 1280, 960   # z-buffer resolution for the occlusion test
TEXELS_PER_M = 110             # bake density
MAX_TEX = 2048


# ---------------------------------------------------------------- camera

def recover_intrinsics(pos, uv):
    """The mesh is an unprojected depth map, so a pinhole fits it to ~1e-7."""
    d = -pos[:, 2]
    m = d > 0.2
    fx, cx = np.polyfit(pos[m, 0] / d[m], uv[m, 0], 1)
    fy, cy = np.polyfit(pos[m, 1] / d[m], uv[m, 1], 1)
    return float(fx), float(cx), float(fy), float(cy)


def project(P, K):
    """Camera-frame points -> (u, v, depth). Camera at origin looking -Z."""
    fx, cx, fy, cy = K
    d = -P[..., 2]
    with np.errstate(divide="ignore", invalid="ignore"):
        u = fx * (P[..., 0] / d) + cx
        v = fy * (P[..., 1] / d) + cy
    return u, v, d


def build_depth_buffer(pos, K):
    """Nearest-depth buffer of the original cloud, used to test surface visibility."""
    u, v, d = project(pos, K)
    ok = np.isfinite(d) & (d > 0.05) & (u >= 0) & (u < 1) & (v >= 0) & (v < 1)
    xi = (u[ok] * (DEPTH_W - 1)).astype(np.int32)
    yi = (v[ok] * (DEPTH_H - 1)).astype(np.int32)
    buf = np.full(DEPTH_W * DEPTH_H, np.inf, np.float32)
    np.minimum.at(buf, yi * DEPTH_W + xi, d[ok].astype(np.float32))
    buf = buf.reshape(DEPTH_H, DEPTH_W)
    # The cloud is sparse at distance; a small min-filter closes pinholes that would
    # otherwise read as "nothing in front", wrongly marking hidden texels visible.
    return ndimage.minimum_filter(buf, size=3)


# ---------------------------------------------------------------- planes

def fit_plane(pts, tol=0.08, iters=10):
    """Total-least-squares plane, refit on inliers to shake off outliers."""
    n, d = np.array([0.0, 1.0, 0.0]), 0.0
    q = pts
    for _ in range(iters):
        c = q.mean(0)
        n = np.linalg.svd(q - c, full_matrices=False)[2][-1]
        d = -n @ c
        inl = np.abs(pts @ n + d) < tol
        if inl.sum() < 200:
            break
        q = pts[inl]
    return n, d, int((np.abs(pts @ n + d) < tol).sum())


def orient(n, d, want):
    """Flip a plane so its normal points along `want` (into the room)."""
    return (n, d) if n @ want > 0 else (-n, -d)


# ---------------------------------------------------------------- baking

def bake_face(corners, pos_all, tex, K, depthbuf, fill=True, fallback=(160, 160, 155)):
    """Bake a photo-projected texture for a planar quad.

    corners: 4 camera-frame points, counter-clockwise, starting at the UV origin.
    Returns (RGB texture, observed-fraction).
    """
    c0, c1, c2, c3 = [np.asarray(c, np.float64) for c in corners]
    e_u, e_v = c1 - c0, c3 - c0
    wu, wv = np.linalg.norm(e_u), np.linalg.norm(e_v)
    tw = int(np.clip(wu * TEXELS_PER_M, 16, MAX_TEX))
    th = int(np.clip(wv * TEXELS_PER_M, 16, MAX_TEX))

    su = (np.arange(tw) + 0.5) / tw
    sv = (np.arange(th) + 0.5) / th
    SU, SV = np.meshgrid(su, sv)
    Q = c0 + SU[..., None] * e_u + SV[..., None] * e_v      # (th, tw, 3)

    u, v, d = project(Q, K)
    vis = np.isfinite(d) & (d > 0.05) & (u >= 0) & (u < 1) & (v >= 0) & (v < 1)

    xi = np.clip((u * (DEPTH_W - 1)), 0, DEPTH_W - 1).astype(np.int32)
    yi = np.clip((v * (DEPTH_H - 1)), 0, DEPTH_H - 1).astype(np.int32)
    scene = depthbuf[yi, xi]
    # Hidden if the captured surface sits clearly nearer than this face.
    # Require the captured surface at that pixel to BE this plane. Only rejecting nearer
    # occluders still lets a texel sample whatever happens to sit behind the plane, which
    # is how bench timber ended up smeared across the floor.
    tol = np.maximum(0.50, 0.06 * d)
    vis &= np.isfinite(scene) & (np.abs(scene - d) < tol)

    th_i, tw_i = tex.shape[:2]
    px = np.clip((u * (tw_i - 1)), 0, tw_i - 1).astype(np.int32)
    py = np.clip((v * (th_i - 1)), 0, th_i - 1).astype(np.int32)
    img = tex[py, px]

    if vis.any():
        med = np.median(img[vis].astype(np.float32), axis=0)
        dev = np.abs(img.astype(np.float32) - med).sum(2)
        d50 = float(np.percentile(dev[vis], 50))
        # Only police faces that are genuinely one material. On turf a stray dark texel
        # (the shadowed gap under the counter, sampled through a depth-test near-miss)
        # is an obvious outlier; on the signed target wall the spread is wide and this
        # test is skipped entirely, so the logos survive.
        if d50 < 30.0:
            vis = vis & (dev <= d50 * 4.0 + 20.0)

    frac = float(vis.mean())
    if frac == 0:
        return (np.zeros_like(img) + np.asarray(fallback, np.uint8)).astype(np.uint8), 0.0
    if fill and frac < 1:
        img = tile_fill(img, vis)
    return img.astype(np.uint8), frac


def tile_fill(img, vis):
    """Fill unobserved texels by tiling a genuinely observed patch.

    Nearest-neighbour fill smears whatever sits at the occlusion boundary into long
    streaks. These surfaces (turf, painted wall, ceiling tile) are near-homogeneous, so
    repeating a real patch of them reads as continuous material instead.
    """
    h, w = vis.shape
    v = vis.astype(np.float32)
    ph = h
    while ph > 32 and ndimage.uniform_filter(
            v, size=(ph, min(160, w)), mode="constant").max() < 0.9:
        ph //= 2                       # no fully-observed full-height column; relax
    pw = min(160, w)
    dens = ndimage.uniform_filter(v, size=(ph, pw), mode="constant")
    # Among well-observed windows take the FLATTEST one. Tiling a patch that happens to
    # contain the range's signage stamps copies of the logo across the wall; the
    # low-variance window is plain material -- turf, ceiling tile, bare timber -- which
    # repeats without reading as duplication.
    g = img.astype(np.float32).mean(2) * v
    m1 = ndimage.uniform_filter(g, size=(ph, pw), mode="constant")
    m2 = ndimage.uniform_filter(g * g, size=(ph, pw), mode="constant")
    var = np.maximum(m2 - m1 * m1, 0.0)
    score = np.where(dens > 0.97 * dens.max(), -var, -np.inf)
    cy, cx = np.unravel_index(np.argmax(score), score.shape)
    y0 = int(np.clip(cy - ph // 2, 0, h - ph))
    x0 = int(np.clip(cx - pw // 2, 0, w - pw))
    patch = img[y0:y0 + ph, x0:x0 + pw]
    pvis = vis[y0:y0 + ph, x0:x0 + pw]
    if pvis.mean() < 0.5:                      # no clean patch: fall back to flat median
        med = np.median(img[vis], axis=0)
        out = img.copy(); out[~vis] = med
        return out
    if not pvis.all():                         # patch off-pixels take the patch median
        patch = patch.copy()
        patch[~pvis] = np.median(patch[pvis], axis=0)
    reps = (int(np.ceil(h / ph)), int(np.ceil(w / pw)), 1)
    tiled = np.tile(patch, reps)[:h, :w]
    out = img.copy(); out[~vis] = tiled[~vis]
    return out


# ---------------------------------------------------------------- glTF out

class GLB:
    def __init__(self):
        self.blobs, self.off = [], 0
        self.bv, self.acc, self.img, self.texs, self.mats, self.meshes, self.nodes = \
            [], [], [], [], [], [], []

    def _add(self, raw):
        pad = (-len(raw)) % 4
        self.blobs.append(raw + b"\x00" * pad)
        o = self.off
        self.off += len(raw) + pad
        return o, len(raw)

    def _bufview(self, entry, target=None):
        d = {"buffer": 0, "byteOffset": entry[0], "byteLength": entry[1]}
        if target:
            d["target"] = target
        self.bv.append(d)
        return len(self.bv) - 1

    def add_quad(self, name, corners, texture):
        pos = np.asarray(corners, "<f4")
        uv = np.array([[0, 1], [1, 1], [1, 0], [0, 0]], "<f4")   # v flipped for glTF
        idx = np.array([0, 1, 2, 0, 2, 3], "<u4")

        a_p = len(self.acc)
        self.acc.append({"bufferView": self._bufview(self._add(pos.tobytes()), 34962),
                         "componentType": 5126, "count": 4, "type": "VEC3",
                         "min": pos.min(0).tolist(), "max": pos.max(0).tolist()})
        a_t = len(self.acc)
        self.acc.append({"bufferView": self._bufview(self._add(uv.tobytes()), 34962),
                         "componentType": 5126, "count": 4, "type": "VEC2"})
        a_i = len(self.acc)
        self.acc.append({"bufferView": self._bufview(self._add(idx.tobytes()), 34963),
                         "componentType": 5125, "count": 6, "type": "SCALAR"})

        buf = __import__("io").BytesIO()
        Image.fromarray(texture).save(buf, format="JPEG", quality=92)
        i = len(self.img)
        self.img.append({"bufferView": self._bufview(self._add(buf.getvalue())),
                         "mimeType": "image/jpeg"})
        self.texs.append({"source": i})
        self.mats.append({"name": name,
                          "pbrMetallicRoughness": {"baseColorTexture": {"index": i},
                                                   "metallicFactor": 0.0,
                                                   "roughnessFactor": 1.0},
                          "doubleSided": True,
                          "extensions": {"KHR_materials_unlit": {}}})
        self.meshes.append({"name": name, "primitives": [
            {"attributes": {"POSITION": a_p, "TEXCOORD_0": a_t},
             "indices": a_i, "material": i}]})
        self.nodes.append({"mesh": len(self.meshes) - 1, "name": name})

    def write(self, path):
        g = {"asset": {"version": "2.0", "generator": "build_room.py"},
             "extensionsUsed": ["KHR_materials_unlit"],
             "scene": 0, "scenes": [{"nodes": list(range(len(self.nodes)))}],
             "nodes": self.nodes, "meshes": self.meshes, "materials": self.mats,
             "textures": self.texs, "images": self.img,
             "accessors": self.acc, "bufferViews": self.bv,
             "buffers": [{"byteLength": self.off}]}
        js = json.dumps(g, separators=(",", ":")).encode()
        js += b" " * ((-len(js)) % 4)
        binc = b"".join(self.blobs)
        total = 12 + 8 + len(js) + 8 + len(binc)
        with open(path, "wb") as f:
            f.write(struct.pack("<III", 0x46546C67, 2, total))
            f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
            f.write(struct.pack("<II", len(binc), 0x004E4942)); f.write(binc)
        return total


# ---------------------------------------------------------------- main

def main():
    src, dst = sys.argv[1], sys.argv[2]

    pos, uv, tex = R.load_glb(src)
    ok = np.isfinite(pos).all(1)
    pos, uv = pos[ok], uv[ok]
    K = recover_intrinsics(pos, uv)
    print("intrinsics fx={:.4f} cx={:.4f} fy={:.4f} cy={:.4f}".format(*K))

    rng = np.random.default_rng(0)
    P = pos[rng.choice(len(pos), min(500000, len(pos)), replace=False)]

    # --- floor and ceiling give the vertical axis -------------------------------
    fn, fd, fc = fit_plane(P[P[:, 1] < -1.2]); fn, fd = orient(fn, fd, [0, 1, 0])
    cn, cd, cc = fit_plane(P[P[:, 1] > 1.0]);  cn, cd = orient(cn, cd, [0, -1, 0])
    print("floor   n={} d={:.3f} inliers={}".format(fn.round(3), fd, fc))
    print("ceiling n={} d={:.3f} inliers={}".format(cn.round(3), cd, cc))

    up = fn - cn                     # cn points down, so this averages the two
    up /= np.linalg.norm(up)
    # Camera sits at the origin, so its signed distance to each plane is just d.
    floor_h, ceil_h = fd, cd
    print("camera {:.2f} m above floor, {:.2f} m below ceiling (room {:.2f} m)".format(
        floor_h, ceil_h, floor_h + ceil_h))

    # --- room yaw from the far edge of the floor --------------------------------
    # Fitting the target wall directly is unreliable: it is distant, so it carries few
    # points, and the far floor/ceiling bleed into the selection. The line where the
    # floor meets that wall is much better conditioned, and it is by definition the
    # room's lateral axis.
    on_floor = np.abs(P @ fn + fd) < 0.12
    fl = P[on_floor]
    e1 = np.cross(up, [0.0, 0.0, 1.0]); e1 /= np.linalg.norm(e1)
    e2 = np.cross(up, e1)
    far_edge = fl[fl[:, 2] < np.percentile(fl[:, 2], 6)]
    A = np.stack([far_edge @ e1, far_edge @ e2], 1)
    A = A - A.mean(0)
    lat2 = np.linalg.svd(A, full_matrices=False)[2][0]       # principal direction
    right = lat2[0] * e1 + lat2[1] * e2
    right /= np.linalg.norm(right)
    fwd = np.cross(up, right); fwd /= np.linalg.norm(fwd)
    if fwd[2] > 0:                                           # must point away from camera
        fwd, right = -fwd, -right
    print("floor far-edge pts={} -> yaw {:.1f} deg off camera axis".format(
        len(far_edge), np.degrees(np.arctan2(fwd[0], -fwd[2]))))
    Rw = np.stack([right, up, -fwd])            # world->room: x right, y up, z back

    Qa = P @ Rw.T
    Q = Qa[~on_floor]
    x0, x1 = np.percentile(Q[:, 0], 1.0), np.percentile(Q[:, 0], 99.0)
    z_far = np.percentile(Q[:, 2], 1.0)
    z_back = 2.0                                # behind the camera: never observed
    y0 = -floor_h
    on_ceil_pts = P[np.abs(P @ cn + cd) < 0.12]
    y1 = float(np.median((on_ceil_pts @ Rw.T)[:, 1])) if len(on_ceil_pts) > 500 else ceil_h
    print("room extents  x [{:.2f} {:.2f}]  y [{:.2f} {:.2f}]  z [{:.2f} {:.2f}]".format(
        x0, x1, y0, y1, z_far, z_back))

    def W(x, y, z):                              # room frame -> camera frame
        return (np.array([x, y, z], float) @ Rw)

    depthbuf = build_depth_buffer(pos, K)
    glb = GLB()

    faces = {
        "floor":   [W(x0, y0, z_back), W(x1, y0, z_back), W(x1, y0, z_far), W(x0, y0, z_far)],
        "ceiling": [W(x0, y1, z_far),  W(x1, y1, z_far),  W(x1, y1, z_back), W(x0, y1, z_back)],
        "target_wall": [W(x0, y1, z_far), W(x1, y1, z_far), W(x1, y0, z_far), W(x0, y0, z_far)],
        "wall_left":   [W(x0, y1, z_back), W(x0, y1, z_far), W(x0, y0, z_far), W(x0, y0, z_back)],
        "wall_right":  [W(x1, y1, z_far), W(x1, y1, z_back), W(x1, y0, z_back), W(x1, y0, z_far)],
        "wall_back":   [W(x1, y1, z_back), W(x0, y1, z_back), W(x0, y0, z_back), W(x1, y0, z_back)],
    }
    for name, corners in faces.items():
        img, frac = bake_face(corners, pos, tex, K, depthbuf)
        glb.add_quad(name, corners, img)
        print("  baked {:12s} {:>4}x{:<4} observed {:5.1f}%".format(
            name, img.shape[1], img.shape[0], 100 * frac))

    # --- firing-point counter ----------------------------------------------------
    # The counter is the big near, below-eye, roughly horizontal slab in front of the
    # camera. Fit its top, then extrude down to the floor as a solid box.
    near = Qa[(Qa[:, 2] > -4.0) & (Qa[:, 2] < 1.5) & (Qa[:, 1] < 0.3) & (Qa[:, 1] > y0 + 0.3)]
    if len(near) > 5000:
        top_y = float(np.percentile(near[:, 1], 92))
        slab = near[np.abs(near[:, 1] - top_y) < 0.12]
        # Keep the dominant depth band; stray points at counter height must not stretch it.
        zc = np.median(slab[:, 2])
        slab = slab[np.abs(slab[:, 2] - zc) < 1.2]
        bz0, bz1 = float(np.percentile(slab[:, 2], 3)), float(np.percentile(slab[:, 2], 97))
        bx0, bx1 = float(np.percentile(slab[:, 0], 2)), float(np.percentile(slab[:, 0], 98))
        print("counter: top y={:.2f}  x [{:.2f} {:.2f}]  z [{:.2f} {:.2f}]".format(
            top_y, bx0, bx1, bz0, bz1))
        bench = {
            "counter_top":   [W(bx0, top_y, bz1), W(bx1, top_y, bz1),
                              W(bx1, top_y, bz0), W(bx0, top_y, bz0)],
            "counter_front": [W(bx0, top_y, bz0), W(bx1, top_y, bz0),
                              W(bx1, y0, bz0),   W(bx0, y0, bz0)],
            "counter_back":  [W(bx1, top_y, bz1), W(bx0, top_y, bz1),
                              W(bx0, y0, bz1),   W(bx1, y0, bz1)],
            "counter_end_l": [W(bx0, top_y, bz1), W(bx0, top_y, bz0),
                              W(bx0, y0, bz0),   W(bx0, y0, bz1)],
            "counter_end_r": [W(bx1, top_y, bz0), W(bx1, top_y, bz1),
                              W(bx1, y0, bz1),   W(bx1, y0, bz0)],
        }
        baked = {n: bake_face(c, pos, tex, K, depthbuf) for n, c in bench.items()}

        # Only the shooter-facing side and the top of the counter were photographed; the
        # downrange side and both ends were not. Left to fill themselves they pull in turf
        # from behind the box and come out green. Give every vertical face the most
        # timber-like texture we did observe -- judged by red-over-green, which separates
        # wood from the green matting.
        verticals = [n for n in bench if n != "counter_top"]
        def woodiness(n):
            im, fr = baked[n]
            med = np.median(im.reshape(-1, 3), axis=0).astype(float)
            return (med[0] - med[1]) + 40.0 * fr
        donor = max(verticals, key=woodiness)
        print("  counter timber donor: {}".format(donor))
        for n in verticals:
            if n != donor:
                src_img = baked[donor][0]
                h, w = baked[n][0].shape[:2]
                reps = (int(np.ceil(h / src_img.shape[0])), int(np.ceil(w / src_img.shape[1])), 1)
                baked[n] = (np.tile(src_img, reps)[:h, :w], baked[n][1])

        for name, corners in bench.items():
            img, frac = baked[name]
            glb.add_quad(name, corners, img)
            print("  baked {:12s} {:>4}x{:<4} observed {:5.1f}%".format(
                name, img.shape[1], img.shape[0], 100 * frac))

    total = glb.write(dst)
    print("\nwrote {} ({:.1f} MB, {} faces, {} triangles)".format(
        dst, total / 1e6, len(glb.meshes), 2 * len(glb.meshes)))


if __name__ == "__main__":
    main()
