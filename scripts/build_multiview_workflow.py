"""Emit the DA3 multi-view workflow for an arbitrary number of photos.

Ten LoadImage nodes plus a nine-deep ImageBatch chain is too repetitive to hand-write and
too easy to mis-wire, so the graph is generated. DA3's multiview mode needs every view in
one IMAGE batch, which is also why the photos must all share a resolution.

usage: build_multiview_workflow.py N_VIEWS OUT.json [subfolder]
"""

import json
import sys

n = int(sys.argv[1])
out_path = sys.argv[2]
subfolder = sys.argv[3] if len(sys.argv) > 3 else "karni10m"

wf = {}

# LoadImage per view: 100, 101, ...
for i in range(n):
    wf[str(100 + i)] = {
        "class_type": "LoadImage",
        "_meta": {"title": "view {:02d}".format(i)},
        "inputs": {"image": "{}/view{:02d}.png".format(subfolder, i)},
    }

# Left-fold the views into a single batch: 200, 201, ...
batch_ref = ["100", 0]
for i in range(1, n):
    nid = str(200 + i)
    wf[nid] = {
        "class_type": "ImageBatch",
        "_meta": {"title": "batch +view{:02d}".format(i)},
        "inputs": {"image1": batch_ref, "image2": [str(100 + i), 0]},
    }
    batch_ref = [nid, 0]

wf["10"] = {
    "class_type": "LoadDA3Model",
    "_meta": {"title": "Depth Anything 3 (base)"},
    # fp32, not default: the camera decoder does feat.float() before its Linear, so
    # half-precision weights raise "mat1 and mat2 must have the same dtype".
    "inputs": {"model_name": "depth_anything_3_base.safetensors", "weight_dtype": "fp32"},
}

wf["11"] = {
    "class_type": "DA3Inference",
    "_meta": {"title": "DA3 multi-view: joint geometry + camera poses"},
    "inputs": {
        "da3_model": ["10", 0],
        "image": batch_ref,
        "resolution": 1008,
        "resize_method": "upper_bound_resize",
        # mode is a dynamic combo: its sub-inputs are namespaced under the mode key.
        "mode": "multiview",
        "mode.ref_view_strategy": "saddle_balanced",
        "mode.pose_method": "cam_dec",
    },
}

# One mesh per view. Multiview puts them all in a shared frame, so they can be
# concatenated afterwards into a single world.
for i in range(n):
    wf[str(300 + i)] = {
        "class_type": "DA3GeometryToMesh",
        "_meta": {"title": "mesh view{:02d}".format(i)},
        "inputs": {
            "da3_geometry": ["11", 0],
            "batch_index": i,
            "decimation": 1,
            "discontinuity_threshold": 0.04,
            "confidence_threshold": 0.1,
            "use_sky_mask": False,
            "texture": True,
        },
    }
    wf[str(400 + i)] = {
        "class_type": "SaveGLB",
        "_meta": {"title": "save view{:02d}".format(i)},
        "inputs": {"mesh": [str(300 + i), 0], "filename_prefix": "karni/view{:02d}".format(i)},
    }

json.dump(wf, open(out_path, "w"), indent=2)
print("wrote {} with {} nodes for {} views".format(out_path, len(wf), n))
