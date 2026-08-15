"""Convert the LayerPano3D FLUX panorama LoRA (XLabs/x-flux key format) to ComfyUI's
native flux LoRA naming.

ComfyUI already knows this key layout (comfy/lora_convert.py:convert_uso_lora), but its
auto-detection in convert_lora() requires `single_blocks.37.*`, and this LoRA only trains
single blocks 1-4. Without the rename every key is silently dropped and the LoRA becomes a
no-op, so we apply the same mapping ahead of time and write a pre-converted file.
"""

import sys
from safetensors.torch import load_file, save_file

SRC, DST = sys.argv[1], sys.argv[2]

# Same replacements as comfy/lora_convert.py:convert_uso_lora, order preserved.
REPLACEMENTS = [
    (".down.weight", ".lora_down.weight"),
    (".up.weight", ".lora_up.weight"),
    (".qkv_lora2.", ".txt_attn.qkv."),
    (".qkv_lora1.", ".img_attn.qkv."),
    (".proj_lora1.", ".img_attn.proj."),
    (".proj_lora2.", ".txt_attn.proj."),
    (".qkv_lora.", ".linear1_qkv."),
    (".proj_lora.", ".linear2."),
    (".processor.", "."),
]

sd = load_file(SRC)
out = {}
for k, v in sd.items():
    new = k
    for old, rep in REPLACEMENTS:
        new = new.replace(old, rep)
    out["diffusion_model.{}".format(new)] = v

save_file(out, DST)
print("converted {} keys -> {}".format(len(out), DST))
for k in sorted(out)[:4]:
    print("  ", k)
