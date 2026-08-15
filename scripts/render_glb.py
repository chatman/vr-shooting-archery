"""Render novel viewpoints of a textured GLB, to check the generated world holds up
when the camera moves away from the panorama's capture point.

The venv has no trimesh/pyrender/open3d, so this parses the GLB directly and splats
vertices through a z-buffer on the GPU with torch. Point splatting rather than triangle
rasterization is enough here: the MoGe mesh is a dense per-pixel grid, so vertices alone
cover the frame once each is drawn as a small square.

usage: render_glb.py WORLD.glb OUTDIR
"""

import base64
import io
import json
import struct
import sys

import numpy as np
import torch
from PIL import Image

W, H = 1280, 720
FOV_DEG = 75.0
SPLAT = 2  # half-width in pixels; fills the gaps left between projected vertices


def load_glb_parts(path):
    data = open(path, "rb").read()
    magic, _, _ = struct.unpack("<III", data[:12])
    assert magic == 0x46546C67, "not a GLB"

    off, chunks = 12, {}
    while off < len(data):
        clen, ctype = struct.unpack("<II", data[off:off + 8])
        chunks[ctype] = data[off + 8: off + 8 + clen]
        off += 8 + clen + (-clen % 4)

    gltf = json.loads(chunks[0x4E4F534A])
    buf = chunks.get(0x004E4942, b"")

    def read_accessor(i):
        acc = gltf["accessors"][i]
        bv = gltf["bufferViews"][acc["bufferView"]]
        ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[acc["type"]]
        dtype = {5120: "<i1", 5121: "<u1", 5122: "<i2",
                 5123: "<u2", 5125: "<u4", 5126: "<f4"}[acc["componentType"]]
        start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
        count = acc["count"] * ncomp
        arr = np.frombuffer(buf, dtype=dtype, count=count, offset=start)
        return arr.reshape(acc["count"], ncomp)

    def load_image(i):
        img = gltf["images"][i]
        if "bufferView" in img:
            bv = gltf["bufferViews"][img["bufferView"]]
            s = bv.get("byteOffset", 0)
            raw = buf[s:s + bv["byteLength"]]
        else:
            raw = base64.b64decode(img["uri"].split(",", 1)[1])
        return np.asarray(Image.open(io.BytesIO(raw)).convert("RGB"))

    # Merged multi-view worlds carry one primitive (and one texture) per source view.
    parts = []
    for mesh in gltf["meshes"]:
        for prim in mesh["primitives"]:
            pos = read_accessor(prim["attributes"]["POSITION"]).astype(np.float32)
            uv = read_accessor(prim["attributes"]["TEXCOORD_0"]).astype(np.float32)
            tex_i = 0
            if "material" in prim:
                mat = gltf["materials"][prim["material"]]
                tex_i = mat["pbrMetallicRoughness"]["baseColorTexture"]["index"]
            parts.append((pos, uv, load_image(gltf["textures"][tex_i]["source"])))
    return parts


def load_glb(path):
    """Single-primitive view: positions, uvs and texture of part 0."""
    return load_glb_parts(path)[0]


def sample_texture(tex, uv):
    th, tw = tex.shape[:2]
    x = np.clip((uv[:, 0] * (tw - 1)).round().astype(np.int64), 0, tw - 1)
    y = np.clip((uv[:, 1] * (th - 1)).round().astype(np.int64), 0, th - 1)
    return tex[y, x]


def look_at(eye, target, up=(0.0, 1.0, 0.0)):
    """World->camera rotation.

    The panorama mesh is Y-up (+Y ceiling, -Y floor). With world up passed in,
    cross(forward, right) comes out as image-space *down*, so the returned basis is
    (right, down, forward) -- the pixel convention render() expects.
    """
    eye, target, up = np.array(eye, "f4"), np.array(target, "f4"), np.array(up, "f4")
    f = target - eye
    f /= np.linalg.norm(f)
    r = np.cross(f, up)
    r /= np.linalg.norm(r)
    u = np.cross(f, r)
    return np.stack([r, u, f]), eye


def render(pos, colors, eye, target, device):
    R, eye = look_at(eye, target)
    R = torch.as_tensor(R, device=device)
    p = torch.as_tensor(pos, device=device) - torch.as_tensor(eye, device=device)
    cam = p @ R.T  # (N,3): x right, y down, z forward

    z = cam[:, 2]
    valid = z > 0.05
    focal = 0.5 * W / np.tan(np.radians(FOV_DEG) / 2)
    u = (cam[:, 0] / z * focal + W / 2)
    v = (cam[:, 1] / z * focal + H / 2)

    ui, vi = u.round().long(), v.round().long()
    valid &= (ui >= 0) & (ui < W) & (vi >= 0) & (vi < H) & torch.isfinite(z)

    depth = torch.full((H * W,), float("inf"), device=device)
    rgb = torch.zeros((H * W, 3), device=device)

    # Splat each vertex over a small square, nearest-depth wins.
    for dy in range(-SPLAT, SPLAT + 1):
        for dx in range(-SPLAT, SPLAT + 1):
            uu, vv = ui + dx, vi + dy
            m = valid & (uu >= 0) & (uu < W) & (vv >= 0) & (vv < H)
            idx = (vv[m] * W + uu[m])
            zz = z[m]
            # scatter_reduce amin resolves occlusion; then keep colors that match the winner.
            depth.scatter_reduce_(0, idx, zz, reduce="amin")
            keep = zz <= depth[idx] + 1e-6
            rgb[idx[keep]] = colors[m][keep]

    out = (rgb.reshape(H, W, 3).clamp(0, 1) * 255).byte().cpu().numpy()
    return out


def main():
    glb, outdir = sys.argv[1], sys.argv[2]
    device = "cuda" if torch.cuda.is_available() else "cpu"

    da3 = "--da3" in sys.argv

    parts = load_glb_parts(glb)
    pos = np.concatenate([p for p, _, _ in parts])
    colors = torch.as_tensor(
        np.concatenate([sample_texture(t, u) for _, u, t in parts]).astype(np.float32) / 255.0,
        device=device)
    print("loaded {} parts, {} verts".format(len(parts), len(pos)))

    lo, hi = np.nanmin(pos, 0), np.nanmax(pos, 0)
    print("bbox {} .. {}".format(lo.round(2), hi.round(2)))

    if da3:
        # DA3 multiview puts the reference camera at the origin looking down its own axis,
        # and the world frame is arbitrary -- so aim at the scene centroid rather than
        # assuming an axis, and orbit around it.
        finite = np.isfinite(pos).all(1)
        c = pos[finite].mean(0)
        r = float(np.linalg.norm(pos[finite] - c, axis=1).mean())
        print("centroid {} mean-radius {:.2f}".format(c.round(2), r))
        aim = tuple(c.tolist())
        views = {
            "01_ref_camera":   ((0.0, 0.0, 0.0), aim),
            "02_orbit_left":   tuple([(c[0] - r, c[1], c[2] * 0.2), aim]),
            "03_orbit_right":  tuple([(c[0] + r, c[1], c[2] * 0.2), aim]),
            "04_above":        tuple([(c[0], c[1] + r * 0.8, c[2] * 0.2), aim]),
        }
    else:
        # Equirect u=0.5 -- the middle of the panorama, where the targets are -- maps to -Z,
        # so -Z is "downrange". The panorama camera itself sits at the origin.
        downrange = float(np.percentile(pos[:, 2], 1.0))
        print("downrange aim (-Z): {:.2f} m".format(downrange))
        aim = (0.0, 0.0, downrange)

        # Offsets are deliberately small: a single-panorama mesh is a shell around the
        # capture point, so it holds up under head-motion parallax, not room-scale walking.
        views = {
            "01_origin_downrange": ((0.0, 0.0, 0.0), aim),
            "02_step_left":        ((-0.6, 0.0, 0.0), aim),
            "03_step_right_back":  ((0.6, 0.0, 0.5), aim),
            "04_crouched":         ((0.0, -0.5, 0.0), aim),
        }

    for name, (eye, target) in views.items():
        img = render(pos, colors, eye, target, device)
        path = "{}/{}.png".format(outdir, name)
        Image.fromarray(img).save(path)
        print("wrote", path)


if __name__ == "__main__":
    main()
