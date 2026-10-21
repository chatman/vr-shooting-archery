# Widens Kenney's four protagonist skins into a crowd's worth of people.
#
# The pack ships 4 textures. A stand of two hundred spectators wearing 4 outfits between
# them reads as a clone army, and the figures are too simple to carry that. The textures
# are flat-colour atlases -- 400-750 unique RGB values over a 1024 map -- so they recolour
# cleanly by classifying each pixel and remapping it, with no UV knowledge needed.
#
# Three classes, in this order:
#   skin     near the peach ramp (245,140..155,106..125) -- remapped MULTIPLICATIVELY so
#            the baked shading survives into darker tones
#   hair     dark and saturated (V < 0.42, S > 0.25) -- gets its own tint targets, since
#            anything hue-based turns a brown head green
#   clothing everything else -- hue, saturation and VALUE all under the variant's control
#
# WHY VALUE AND NOT JUST HUE. Two earlier attempts failed in the same direction. Rotating
# the hue produced neon, because these are skate and street outfits already at full
# saturation and rotating saturated orange just gives saturated lime. Mapping onto one
# hue with a saturation cap fixed that but produced a stand dressed in pastels -- mint,
# candy pink, powder blue -- which reads as a toy aisle rather than a sports crowd. The
# missing control was brightness: a navy jacket is not a blue one at lower saturation, it
# is a blue one at a third of the value. Each variant now sets a hue, a saturation
# ceiling AND a value multiplier, and the palette below is mostly black, charcoal, navy,
# denim, grey, olive and white with two bright team colours to break it up -- which is
# what a photograph of a 10 m qualification crowd actually looks like.
#
# Near-neutral pixels (S <= 0.14: the whites, greys and blacks of the source) are not
# left alone either. They are a third of some of these atlases, and leaving them put made
# every other spectator wear a white shirt whatever the variant said. They keep their
# neutrality and take the variant's value, so a charcoal variant really is charcoal.
#
#   uv run --with pillow python -I scripts/recolour_spectator_skins.py <Skins dir> <outdir>
#
# Input is the Skins/ folder of Kenney's "Animated Characters: Protagonists" 1.1 (CC0);
# output goes to quest-app/Assets/Models/Spectator/Textures.
import sys, os, math, colorsys
from PIL import Image

SRC, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)

SKIN = [(1.00, 1.00, 1.00), (0.95, 0.86, 0.76), (0.86, 0.73, 0.59),
        (0.71, 0.56, 0.43), (0.55, 0.40, 0.31)]

# Black, dark brown, mid brown, grey, and the source's own. No platinum: one blond head
# in a crowd is fine, a sixteenth of the crowd blond is not.
HAIR = [(0.26, 0.22, 0.20), (0.52, 0.42, 0.34), (0.78, 0.64, 0.48),
        (1.15, 1.10, 1.02), (1.00, 1.00, 1.00)]

HUE_SPREAD = 0.045      # how far two garments on one figure drift from the chosen hue
NEUTRAL = 0.14          # at or below this saturation a source colour counts as neutral

# base texture, skin, hair, clothing hue (deg), saturation ceiling, value multiplier
VARIANTS = [
    ("skaterMaleA",   0, 0, 222, 0.38, 0.55),   # navy
    ("skaterMaleA",   2, 1, 215, 0.10, 0.42),   # charcoal
    ("skaterMaleA",   4, 0, 205, 0.07, 0.95),   # light grey
    ("skaterMaleA",   1, 2, 212, 0.32, 0.78),   # denim
    ("skaterFemaleA", 0, 3, 230, 0.06, 0.30),   # black
    ("skaterFemaleA", 3, 0,  75, 0.30, 0.55),   # olive
    ("skaterFemaleA", 1, 1, 355, 0.36, 0.50),   # maroon
    ("skaterFemaleA", 2, 2, 200, 0.06, 0.65),   # mid grey
    ("criminalMaleA", 1, 0,  20, 0.40, 0.70),   # rust
    ("criminalMaleA", 3, 1, 185, 0.30, 0.58),   # teal
    ("criminalMaleA", 0, 3,  40, 0.05, 1.00),   # white
    ("criminalMaleA", 4, 2,  45, 0.24, 0.84),   # khaki
    ("cyborgFemaleA", 2, 1, 215, 0.50, 0.72),   # a team's blue
    ("cyborgFemaleA", 4, 0,  25, 0.55, 0.95),   # a team's orange
    ("cyborgFemaleA", 1, 2, 140, 0.28, 0.45),   # forest
    ("cyborgFemaleA", 3, 3, 300, 0.22, 0.45),   # plum
]


def is_skin(r, g, b):
    return r > 205 and 115 < g < 200 and 85 < b < 175 and r > g > b and (r - b) > 90


def build(base, skin_i, hair_i, hue_deg, cap, val):
    im = Image.open(os.path.join(SRC, base + ".png")).convert("RGBA")
    px = im.load()
    sk, hr = SKIN[skin_i], HAIR[hair_i]
    hue = (hue_deg % 360) / 360.0
    cache = {}
    w, h = im.size
    for y in range(h):
        for x in range(w):
            c = px[x, y]
            if c[3] == 0:
                continue
            out = cache.get(c)
            if out is None:
                r, g, b, a = c
                if is_skin(r, g, b):
                    out = (min(255, int(r * sk[0])), min(255, int(g * sk[1])),
                           min(255, int(b * sk[2])), a)
                else:
                    hh, ss, vv = colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
                    if vv < 0.42 and ss > 0.25:
                        out = (min(255, int(r * hr[0])), min(255, int(g * hr[1])),
                               min(255, int(b * hr[2])), a)
                    else:
                        nv = min(1.0, vv * val)
                        if ss > NEUTRAL:
                            # The sine keeps two differently-coloured garments on one
                            # figure distinguishable without either leaving the family.
                            nh = (hue + HUE_SPREAD * math.sin(2.0 * math.pi * hh)) % 1.0
                            ns = min(ss, cap)
                        else:
                            nh, ns = hue, min(ss, 0.05)
                        nr, ng, nb = colorsys.hsv_to_rgb(nh, ns, nv)
                        out = (int(nr * 255), int(ng * 255), int(nb * 255), a)
                cache[c] = out
            px[x, y] = out
    name = "spec_%s_%d%d%03d" % (base[:6].lower(), skin_i, hair_i, hue_deg)
    im.save(os.path.join(OUT, name + ".png"))
    print("[tex] %-22s hue=%3d cap=%.2f val=%.2f colours=%d"
          % (name, hue_deg, cap, val, len(cache)))


for v in VARIANTS:
    build(*v)
print("[tex] DONE %d variants" % len(VARIANTS))
