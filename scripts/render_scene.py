"""Rasterise a low-poly textured GLB, to check a built room actually looks like the spec.

render_glb.py splats *vertices*, which works for a MoGe relief (one vertex per pixel) and
renders exactly nothing for a hall made of 4-vertex quads. So this is a real triangle
rasteriser: near-plane clipping, perspective-correct attributes, a z-buffer, backface
culling (a hole in the render therefore means a wound-backwards face, which is the point),
and shading from the GLB's own KHR_lights_punctual lamps.

Raster runs per-triangle in numpy -- a few thousand triangles is nothing -- into a G-buffer
of uv / normal / world position, then one vectorised torch pass shades every pixel against
every lamp at once. 2x supersampled.

W/H/FOV are module globals so a caller (flythrough.py) can retarget the camera and frame
size without threading them through every call.

usage: render_scene.py MODEL.glb OUTDIR [--views a,b] [--exposure 0.016] [--no-cull]
"""

import io
import json
import os
import struct
import sys

import numpy as np
import torch
from PIL import Image

W, H = 1600, 900
SS = 2                     # supersampling factor
FOV_DEG = 70.0
NEAR = 0.05
AMBIENT = 6.0              # flat bounce term, in the same units as the lamps
DEFAULT_EXPOSURE = 0.016


# ---------------------------------------------------------------- GLB in

def load(path):
    data = open(path, "rb").read()
    assert struct.unpack("<I", data[:4])[0] == 0x46546C67, "not a GLB"
    off, chunks = 12, {}
    while off < len(data):
        clen, ctype = struct.unpack("<II", data[off:off + 8])
        chunks[ctype] = data[off + 8: off + 8 + clen]
        off += 8 + clen + (-clen % 4)
    g = json.loads(chunks[0x4E4F534A])
    buf = chunks.get(0x004E4942, b"")

    def accessor(i):
        acc = g["accessors"][i]
        bv = g["bufferViews"][acc["bufferView"]]
        ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[acc["type"]]
        dt = {5120: "<i1", 5121: "<u1", 5122: "<i2", 5123: "<u2",
              5125: "<u4", 5126: "<f4"}[acc["componentType"]]
        start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
        arr = np.frombuffer(buf, dtype=dt, count=acc["count"] * ncomp, offset=start)
        return arr.reshape(acc["count"], ncomp)

    def image(i):
        bv = g["bufferViews"][g["images"][i]["bufferView"]]
        s = bv.get("byteOffset", 0)
        raw = buf[s:s + bv["byteLength"]]
        return np.asarray(Image.open(io.BytesIO(raw)).convert("RGB"))

    prims = []
    for node in g["nodes"]:
        if "mesh" not in node:
            continue
        T = np.array(node.get("translation", [0, 0, 0]), float)
        for prim in g["meshes"][node["mesh"]]["primitives"]:
            a = prim["attributes"]
            pos = accessor(a["POSITION"]).astype(np.float64) + T
            nrm = (accessor(a["NORMAL"]).astype(np.float64) if "NORMAL" in a
                   else np.zeros_like(pos))
            uv = (accessor(a["TEXCOORD_0"]).astype(np.float64) if "TEXCOORD_0" in a
                  else np.zeros((len(pos), 2)))
            idx = accessor(prim["indices"]).reshape(-1).astype(np.int64)
            m = g["materials"][prim.get("material", 0)]
            pbr = m.get("pbrMetallicRoughness", {})
            tex = None
            if "baseColorTexture" in pbr:
                tex = image(g["textures"][pbr["baseColorTexture"]["index"]]["source"])
            base = pbr.get("baseColorFactor", [1, 1, 1, 1])
            prims.append(dict(name=m.get("name", "mat"), pos=pos, nrm=nrm, uv=uv, idx=idx,
                              color=np.array(base[:3]), alpha=float(base[3]) if len(base) > 3
                              else 1.0,
                              rough=float(pbr.get("roughnessFactor", 1.0)),
                              tex=tex, emissive=np.array(m.get("emissiveFactor", [0, 0, 0])),
                              double=bool(m.get("doubleSided", False))))

    lamps = []
    defs = g.get("extensions", {}).get("KHR_lights_punctual", {}).get("lights", [])
    for node in g["nodes"]:
        ref = node.get("extensions", {}).get("KHR_lights_punctual")
        if ref is not None:
            d = defs[ref["light"]]
            lamps.append((np.array(node.get("translation", [0, 0, 0]), float),
                          d.get("intensity", 100.0), np.array(d.get("color", [1, 1, 1]))))
    return prims, lamps


# ---------------------------------------------------------------- raster

def look_at(eye, at, up=(0, 1, 0)):
    eye, at, up = (np.array(v, float) for v in (eye, at, up))
    f = at - eye; f /= np.linalg.norm(f)
    r = np.cross(f, up); r /= np.linalg.norm(r)
    u = np.cross(r, f)
    return np.stack([r, u, f]), eye          # rows: right, up, forward


def clip_near(verts, attrs):
    """Sutherland-Hodgman against z >= NEAR, carrying attributes.

    Without this, a floor quad that passes behind the camera projects to garbage -- and in
    a 15 m room with a 70 deg lens the floor is behind the camera in every interior shot.
    """
    out_v, out_a = [], []
    n = len(verts)
    for i in range(n):
        a, b = verts[i], verts[(i + 1) % n]
        aa, ba = attrs[i], attrs[(i + 1) % n]
        ain, bin_ = a[2] >= NEAR, b[2] >= NEAR
        if ain:
            out_v.append(a); out_a.append(aa)
        if ain != bin_:
            t = (NEAR - a[2]) / (b[2] - a[2])
            out_v.append(a + t * (b - a))
            out_a.append(aa + t * (ba - aa))
    return out_v, out_a


def rasterize(prims, eye, at, cull=True, depth0=None, fov=None):
    """-> (depth, matid, attrs) G-buffer at supersampled resolution.

    `depth0` seeds the z-buffer from an earlier pass, so a transparent layer is occluded
    by opaque geometry in front of it without being able to overwrite it."""
    w, h = W * SS, H * SS
    focal = 0.5 * w / np.tan(np.radians(FOV_DEG if fov is None else fov) / 2)
    R, eye = look_at(eye, at)

    depth = np.full((h, w), np.inf) if depth0 is None else depth0.copy()
    matid = np.full((h, w), -1, np.int32)
    # attrs: u v nx ny nz wx wy wz
    gbuf = np.zeros((h, w, 8))

    for pid, p in enumerate(prims):
        cam = (p["pos"] - eye) @ R.T
        att = np.concatenate([p["uv"], p["nrm"], p["pos"]], 1)
        tris = p["idx"].reshape(-1, 3)
        for tri in tris:
            V = [cam[i] for i in tri]
            A = [att[i] for i in tri]
            if max(v[2] for v in V) < NEAR:
                continue
            if min(v[2] for v in V) < NEAR:
                V, A = clip_near(V, A)
                if len(V) < 3:
                    continue
            for k in range(1, len(V) - 1):
                _draw(depth, matid, gbuf, pid, [V[0], V[k], V[k + 1]],
                      [A[0], A[k], A[k + 1]], focal, w, h,
                      cull and not p["double"])
    return depth, matid, gbuf


def _draw(depth, matid, gbuf, pid, V, A, focal, w, h, cull):
    V = np.array(V); A = np.array(A)
    invz = 1.0 / V[:, 2]
    sx = V[:, 0] * invz * focal + w / 2
    sy = -V[:, 1] * invz * focal + h / 2

    area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0])
    # look_at's (right, up, forward) basis is left-handed, and screen y is flipped again
    # on top of that, so a face whose normal points back at the camera lands with a
    # NEGATIVE signed area here. Verified against a quad of known normal, not guessed.
    if area == 0 or (cull and area >= 0):
        return

    x0 = max(int(np.floor(sx.min())), 0); x1 = min(int(np.ceil(sx.max())) + 1, w)
    y0 = max(int(np.floor(sy.min())), 0); y1 = min(int(np.ceil(sy.max())) + 1, h)
    if x0 >= x1 or y0 >= y1:
        return

    px = np.arange(x0, x1) + 0.5
    py = np.arange(y0, y1) + 0.5
    gx, gy = np.meshgrid(px, py)

    def edge(i, j):
        return ((sx[j] - sx[i]) * (gy - sy[i]) - (sy[j] - sy[i]) * (gx - sx[i])) / area

    w0, w1 = edge(1, 2), edge(2, 0)
    w2 = 1.0 - w0 - w1
    inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
    if not inside.any():
        return

    iz = w0 * invz[0] + w1 * invz[1] + w2 * invz[2]
    # 1e-6 rather than 0: a triangle clipped at the near plane and seen almost edge-on
    # produces pixels at absurd depth, whose perspective-correct attributes come back as
    # 1e30-scale uv. Those then index a texture, and the answer is a CUDA out-of-bounds.
    inside &= iz > 1e-6
    z = np.where(inside, 1.0 / np.where(iz == 0, 1e-9, iz), np.inf)

    sub = depth[y0:y1, x0:x1]
    win = inside & (z < sub)
    if not win.any():
        return
    sub[win] = z[win]
    matid[y0:y1, x0:x1][win] = pid
    persp = np.stack([w0 * invz[0], w1 * invz[1], w2 * invz[2]], -1)[win] / iz[win, None]
    gbuf[y0:y1, x0:x1][win] = persp @ A


def bilinear(t, uv):
    """Bilinear, wrapping. `t` is a device tensor of float32 in 0..1.

    On the GPU deliberately: this is eight gathers over every textured pixel in the frame,
    and in numpy it cost more than the whole rest of the renderer put together.
    """
    th, tw = t.shape[0], t.shape[1]
    nz = lambda a: torch.nan_to_num(a, nan=0.0, posinf=0.0, neginf=0.0)
    x = torch.remainder(nz(uv[:, 0] * tw - 0.5), tw)
    y = torch.remainder(nz(uv[:, 1] * th - 0.5), th)
    x0, y0 = x.floor(), y.floor()
    fx, fy = (x - x0).unsqueeze(1), (y - y0).unsqueeze(1)
    # Belt and braces: a non-finite uv that slipped through would index out of bounds,
    # which on the GPU is a device-side assert that kills the whole worker.
    x0 = x0.long().clamp(0, tw - 1)
    y0 = y0.long().clamp(0, th - 1)
    x1, y1 = (x0 + 1) % tw, (y0 + 1) % th
    top = t[y0, x0] * (1 - fx) + t[y0, x1] * fx
    bot = t[y1, x0] * (1 - fx) + t[y1, x1] * fx
    return top * (1 - fy) + bot * fy


def mips(prim, device):
    """Box-filtered halving pyramid, built once per primitive and cached on it."""
    key = "mips_" + str(device)
    if key not in prim:
        t = torch.as_tensor(prim["tex"].astype(np.float32) / 255.0, device=device)
        levels = [t]
        while min(levels[-1].shape[0], levels[-1].shape[1]) > 4:
            h, w = levels[-1].shape[0], levels[-1].shape[1]
            levels.append(levels[-1][:h // 2 * 2, :w // 2 * 2]
                          .reshape(h // 2, 2, w // 2, 2, 3).mean((1, 3)))
        prim[key] = levels
    return prim[key]


def texel_density(prim):
    """Texels per metre on the surface, from the first triangle's world and uv areas.

    Scale invariant, so it works for a 42 m wall tiling its texture 30 times and for a
    170 mm card mapped 0..1, without either needing to know about the other.
    """
    if "density" not in prim:
        i = prim["idx"][:3]
        p, t = prim["pos"][i], prim["uv"][i]
        th, tw = prim["tex"].shape[:2]
        wa = 0.5 * np.linalg.norm(np.cross(p[1] - p[0], p[2] - p[0]))
        ua = 0.5 * abs(np.cross(t[1] - t[0], t[2] - t[0])) * tw * th
        prim["density"] = float(np.sqrt(ua / max(wa, 1e-12)))
    return prim["density"]


def sample(prim, uv, dist, focal, device):
    """Mip-mapped, level chosen per pixel from how many texels land in one pixel.

    Bilinear alone only fixes magnification. The target card is the opposite case -- a
    1024 px texture landing on ~540 px of screen at the end of the walkthrough zoom -- so
    point sampling skips half of every 0.9 px ring line and the rings come out dashed.
    Level = log2(texels per pixel) fixes that, and it also stops the timber wall
    stippling at the far end of the hall.
    """
    levels = mips(prim, device)
    lod = torch.nan_to_num(torch.log2((texel_density(prim) * dist / focal).clamp_min(1e-6)))
    l0 = lod.floor().clamp(0, len(levels) - 1)
    frac = (lod - l0).clamp(0, 1).unsqueeze(1)
    l0 = l0.long()
    out = torch.zeros((uv.shape[0], 3), device=device, dtype=torch.float32)
    for L in torch.unique(l0).tolist():
        m = l0 == L
        a = bilinear(levels[L], uv[m])
        b = bilinear(levels[min(L + 1, len(levels) - 1)], uv[m])
        out[m] = a * (1 - frac[m]) + b * frac[m]
    return out


# ---------------------------------------------------------------- shade

def shade(prims, lamps, matid, gbuf, eye, device, focal=None):
    """-> (diffuse, specular) linear radiance per pixel, flat (h*w, 3).

    Kept linear and un-tone-mapped so transparent layers can be composited on top before
    the curve is applied; tone mapping each layer separately double-compresses whatever
    shows through the glass."""
    h, w = matid.shape
    flat = matid.reshape(-1)
    t = lambda a: torch.as_tensor(a, device=device, dtype=torch.float32)
    g = t(gbuf.reshape(-1, 8))
    ids = torch.as_tensor(flat, device=device)
    eye_t = t(np.asarray(eye, float))

    albedo = torch.zeros((h * w, 3), device=device)
    emis = torch.zeros((h * w, 3), device=device)
    ks = torch.zeros((h * w, 1), device=device)
    shine = torch.full((h * w, 1), 8.0, device=device)
    for pid, p in enumerate(prims):
        m = ids == pid
        if not bool(m.any()):
            continue
        c = t(p["color"])
        if p["tex"] is not None:
            gm = g[m]
            d = (gm[:, 5:8] - eye_t).norm(dim=1)
            c = sample(p, gm[:, :2], d, focal, device) * c
        albedo[m] = c
        emis[m] = t(p["emissive"])
        # Glass and aluminium are only readable through their highlights, so smooth
        # materials get a Blinn-Phong lobe; matte ones effectively get none.
        r = max(p.get("rough", 1.0), 0.02)
        ks[m] = 0.6 * (1.0 - r) ** 2
        shine[m] = 2.0 + 900.0 * (1.0 - r) ** 3

    N, P = g[:, 2:5], g[:, 5:8]
    N = N / N.norm(dim=1, keepdim=True).clamp_min(1e-9)
    V = eye_t - P
    V = V / V.norm(dim=1, keepdim=True).clamp_min(1e-9)
    KS, SH = ks, shine

    irr = torch.full_like(N, AMBIENT)
    spec = torch.zeros_like(N)
    for pos, cd, col in lamps:
        L = t(pos) - P
        d2 = (L * L).sum(1, keepdim=True).clamp_min(0.04)
        Lh = L / d2.sqrt()
        ndl = (N * Lh).sum(1, keepdim=True).clamp_min(0.0)
        e = t(col) * (cd / d2)
        irr += e * ndl
        Hh = Lh + V
        Hh = Hh / Hh.norm(dim=1, keepdim=True).clamp_min(1e-9)
        spec += e * KS * (N * Hh).sum(1, keepdim=True).clamp_min(0.0) ** SH

    alb = albedo ** 2.2                                        # sRGB texels -> linear
    diffuse = alb * irr + emis * 70.0
    bg = (ids < 0).to(torch.float32).unsqueeze(1)
    return diffuse * (1 - bg), spec * (1 - bg)


def tonemap(linear, exposure, device):
    srgb = (1.0 - torch.exp(-linear * exposure)).clamp(0, 1) ** (1 / 2.2)
    img = (srgb.reshape(H * SS, W * SS, 3) * 255).byte().cpu().numpy()
    return np.asarray(Image.fromarray(img).resize((W, H), Image.LANCZOS))


def render_view(prims, lamps, eye, at, exposure, device, cull=True, fov=None):
    """Opaque pass, then each transparent layer composited back to front."""
    opaque = [p for p in prims if p["alpha"] >= 0.999]
    glassy = [p for p in prims if p["alpha"] < 0.999]

    focal = 0.5 * (W * SS) / np.tan(np.radians(FOV_DEG if fov is None else fov) / 2)
    depth, matid, gbuf = rasterize(opaque, eye, at, cull, fov=fov)
    lin, sp = shade(opaque, lamps, matid, gbuf, eye, device, focal)
    lin = lin + sp

    def far(p):
        return -np.linalg.norm(p["pos"].mean(0) - np.asarray(eye, float))

    for p in sorted(glassy, key=far):
        # Depth-tested against the opaque pass but never written back: glass in front of
        # the room tints it, glass behind it is hidden.
        _, m2, g2 = rasterize([p], eye, at, cull=False, depth0=depth, fov=fov)
        d2, s2 = shade([p], lamps, m2, g2, eye, device, focal)
        a = float(p["alpha"]) * torch.as_tensor(
            (m2.reshape(-1) >= 0).astype(np.float32), device=device).unsqueeze(1)
        lin = d2 * a + s2 * (a > 0) + lin * (1 - a)      # reflections sit on top of the tint
    return tonemap(lin, exposure, device), matid, depth


# ---------------------------------------------------------------- views

VIEWS = {
    # (eye, aim). Eye heights are real: 1.65 m standing, 1.60 m over the bench.
    "01_shooter":     ((0.5, 1.60, 10.8), (0.5, 1.50, 0.0)),
    "02_firing_line": ((-9.0, 1.70, 12.6), (6.0, 1.30, 2.0)),
    "03_from_door":   ((-10.5, 1.65, 28.8), (-3.0, 1.40, 0.0)),
    "04_downrange":   ((-1.0, 1.55, 1.2), (4.0, 1.60, 29.0)),
    "05_target_wall": ((3.0, 1.55, 3.0), (-2.5, 1.50, 0.0)),
    "06_high_wide":   ((-16.0, 5.20, 27.5), (4.0, 1.00, 4.0)),
    "07_glass_wall":  ((0.0, 1.70, 21.0), (1.5, 2.20, 30.0)),
    # The legibility case: standing at the back wall, 30 m off the targets.
    "08_from_back":   ((0.0, 1.65, 29.4), (0.0, 1.62, 0.0)),
}


def main():
    glb, outdir = sys.argv[1], sys.argv[2]
    args = sys.argv[3:]
    exposure = DEFAULT_EXPOSURE
    if "--exposure" in args:
        exposure = float(args[args.index("--exposure") + 1])
    cull = "--no-cull" not in args
    want = VIEWS
    if "--views" in args:
        keys = args[args.index("--views") + 1].split(",")
        want = {k: v for k, v in VIEWS.items() if any(s in k for s in keys)}

    device = "cuda" if torch.cuda.is_available() else "cpu"
    prims, lamps = load(glb)
    tris = sum(len(p["idx"]) // 3 for p in prims)
    print("{}: {} prims / {} tris / {} lamps".format(
        os.path.basename(glb), len(prims), tris, len(lamps)))

    os.makedirs(outdir, exist_ok=True)
    for name, (eye, at) in want.items():
        img, matid, depth = render_view(prims, lamps, eye, at, exposure, device, cull)
        path = os.path.join(outdir, name + ".png")
        Image.fromarray(img).save(path)
        cov = float((matid >= 0).mean())
        print("  {}  coverage {:.1%}  nearest {:.2f} m".format(
            path, cov, float(np.min(depth)) if np.isfinite(depth).any() else -1))


if __name__ == "__main__":
    main()
