"""Merge several DA3 multi-view GLBs into one world file.

DA3's multiview mode returns every view in a shared coordinate frame, but ComfyUI's
SaveGLB writes one file per view. Merging is therefore just a matter of packing them into
a single glTF -- no transforms needed. Each view keeps its own image and material, so the
result stays textured (a single shared atlas would mean re-baking UVs).

Rather than remapping arbitrary glTF index spaces, this rebuilds the document from the
parsed arrays; SaveGLB's output shape is known and fixed (one primitive, POSITION +
TEXCOORD_0 + indices, one image, one material).

usage: merge_glb.py OUT.glb IN1.glb IN2.glb ...
"""

import json
import struct
import sys

import numpy as np


def read_glb(path):
    data = open(path, "rb").read()
    assert struct.unpack("<I", data[:4])[0] == 0x46546C67, "not a GLB: " + path

    off, chunks = 12, {}
    while off < len(data):
        clen, ctype = struct.unpack("<II", data[off:off + 8])
        chunks[ctype] = data[off + 8: off + 8 + clen]
        off += 8 + clen + (-clen % 4)

    g = json.loads(chunks[0x4E4F534A])
    buf = chunks.get(0x004E4942, b"")

    def acc(i):
        a = g["accessors"][i]
        bv = g["bufferViews"][a["bufferView"]]
        ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        dt = {5120: "<i1", 5121: "<u1", 5122: "<i2",
              5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]]
        start = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
        return np.frombuffer(buf, dtype=dt, count=a["count"] * ncomp,
                             offset=start).reshape(a["count"], ncomp)

    prim = g["meshes"][0]["primitives"][0]
    pos = acc(prim["attributes"]["POSITION"]).astype("<f4")
    uv = acc(prim["attributes"]["TEXCOORD_0"]).astype("<f4")
    idx = acc(prim["indices"]).astype("<u4").reshape(-1)

    img = g["images"][0]
    bv = g["bufferViews"][img["bufferView"]]
    s = bv.get("byteOffset", 0)
    png = buf[s:s + bv["byteLength"]]
    return pos, uv, idx, png, img.get("mimeType", "image/png")


def main():
    out_path, inputs = sys.argv[1], sys.argv[2:]

    blobs, views = [], []
    offset = 0

    def add(raw):
        """Append to the BIN chunk, 4-byte aligned, and return (byteOffset, length)."""
        nonlocal offset
        pad = (-len(raw)) % 4
        blobs.append(raw + b"\x00" * pad)
        o = offset
        offset += len(raw) + pad
        return o, len(raw)

    for p in inputs:
        pos, uv, idx, png, mime = read_glb(p)
        # Drop non-finite vertices; they would blow up the accessor min/max.
        good = np.isfinite(pos).all(1)
        if not good.all():
            remap = -np.ones(len(pos), dtype=np.int64)
            remap[good] = np.arange(good.sum())
            keep = good[idx.reshape(-1, 3)].all(1)
            idx = remap[idx.reshape(-1, 3)[keep]].reshape(-1).astype("<u4")
            pos, uv = pos[good], uv[good]
        views.append({
            "pos": add(pos.tobytes()), "npos": len(pos),
            "min": pos.min(0).tolist(), "max": pos.max(0).tolist(),
            "uv": add(uv.tobytes()),
            "idx": add(idx.astype("<u4").tobytes()), "nidx": len(idx),
            "png": add(png), "mime": mime,
            "name": p.split("/")[-1].replace(".glb", ""),
        })
        print("  + {:<22} {:>9} verts {:>9} tris".format(views[-1]["name"], len(pos), len(idx) // 3))

    bufviews, accessors, images, textures, materials, meshes, nodes = [], [], [], [], [], [], []

    def bv(entry, target=None):
        d = {"buffer": 0, "byteOffset": entry[0], "byteLength": entry[1]}
        if target: d["target"] = target
        bufviews.append(d)
        return len(bufviews) - 1

    for i, v in enumerate(views):
        a_pos = len(accessors)
        accessors.append({"bufferView": bv(v["pos"], 34962), "componentType": 5126,
                          "count": v["npos"], "type": "VEC3",
                          "min": v["min"], "max": v["max"]})
        a_uv = len(accessors)
        accessors.append({"bufferView": bv(v["uv"], 34962), "componentType": 5126,
                          "count": v["npos"], "type": "VEC2"})
        a_idx = len(accessors)
        accessors.append({"bufferView": bv(v["idx"], 34963), "componentType": 5125,
                          "count": v["nidx"], "type": "SCALAR"})

        images.append({"bufferView": bv(v["png"]), "mimeType": v["mime"]})
        textures.append({"source": i})
        materials.append({
            "name": v["name"],
            "pbrMetallicRoughness": {
                "baseColorTexture": {"index": i},
                "metallicFactor": 0.0, "roughnessFactor": 1.0,
            },
            "doubleSided": True,
            # Unlit: these meshes carry no NORMAL attribute and the texture is already a
            # photograph with real lighting baked in. Shading it again would double up the
            # lighting and render flat-normal facets as visible banding.
            "extensions": {"KHR_materials_unlit": {}},
        })
        meshes.append({"name": v["name"], "primitives": [{
            "attributes": {"POSITION": a_pos, "TEXCOORD_0": a_uv},
            "indices": a_idx, "material": i,
        }]})
        nodes.append({"mesh": i, "name": v["name"]})

    gltf = {
        "asset": {"version": "2.0", "generator": "merge_glb.py (DA3 multiview)"},
        "extensionsUsed": ["KHR_materials_unlit"],
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes, "meshes": meshes, "materials": materials,
        "textures": textures, "images": images,
        "accessors": accessors, "bufferViews": bufviews,
        "buffers": [{"byteLength": offset}],
    }

    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((-len(js)) % 4)
    binc = b"".join(blobs)

    total = 12 + 8 + len(js) + 8 + len(binc)
    with open(out_path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack("<II", len(binc), 0x004E4942)); f.write(binc)

    print("wrote {} ({:.1f} MB, {} views)".format(out_path, total / 1e6, len(views)))


if __name__ == "__main__":
    main()
