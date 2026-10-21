# Widens Kenney's four protagonist skins into a crowd's worth of people.
#
# The pack ships 4 textures. A stand of 200 spectators wearing 4 outfits between them
# reads as a clone army, and the figures are too simple to carry that. The textures are
# flat-colour atlases -- 400-750 unique RGB values over a 1024 map -- so they recolour
# cleanly by classifying each pixel and remapping it, with no UV knowledge needed.
#
# Three classes, in this order:
#   skin     near the peach ramp (245,140..155,106..125) -- remapped MULTIPLICATIVELY so
#            the baked shading survives into darker tones
#   hair     dark and saturated (V < 0.42, S > 0.25) -- gets its own tint targets, since
#            anything hue-based turns a brown head green
#   clothing S > 0.14 -- mapped onto the variant's one clothing hue; greys and whites fall
#            through untouched, which keeps white shirts white
#
# WHY CLOTHING IS MAPPED AND NOT ROTATED. Rotating the hue was the first attempt and the
# stand came out in neon: these are skate and street outfits whose source colours are
# already at full saturation, so rotating them just moved saturated orange to saturated
# lime. Mapping instead pins every garment on one figure to a narrow band around a chosen
# hue and caps the saturation, which is both how people dress and what a sports crowd
# looks like -- mostly muted, a few team colours, white and grey throughout.
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
HAIR = [(0.30, 0.26, 0.24), (1.00, 1.00, 1.00), (0.62, 0.52, 0.40),
        (1.45, 1.30, 0.95), (1.60, 1.60, 1.55)]          # black, as-is, brown, fair, grey

SAT_CAP = 0.46          # above this a flat colour reads as hi-vis, not as a shirt
HUE_SPREAD = 0.055      # how far garments on one figure drift from the chosen hue

# base texture, skin tone, hair tone, clothing hue (degrees on the wheel)
VARIANTS = [
    ("skaterMaleA",   0, 1, 222),   # navy
    ("skaterMaleA",   2, 0, 352),   # maroon
    ("skaterMaleA",   4, 0,  72),   # olive
    ("skaterFemaleA", 0, 3, 174),   # teal
    ("skaterFemaleA", 3, 0, 210),   # slate blue
    ("skaterFemaleA", 1, 2,  42),   # mustard
    ("criminalMaleA", 1, 0, 120),   # forest
    ("criminalMaleA", 3, 2,  18),   # rust
    ("criminalMaleA", 0, 4, 288),   # plum
    ("cyborgFemaleA", 2, 2, 200),   # steel
    ("cyborgFemaleA", 4, 0,  96),   # moss
    ("cyborgFemaleA", 1, 3, 336),   # wine
]


def is_skin(r, g, b):
    return r > 205 and 115 < g < 200 and 85 < b < 175 and r > g > b and (r - b) > 90


def build(base, skin_i, hair_i, hue_deg):
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
                    elif ss > 0.14:
                        # The sine keeps two differently-coloured garments on the same
                        # figure distinguishable without letting either leave the family.
                        nh = (hue + HUE_SPREAD * math.sin(2.0 * math.pi * hh)) % 1.0
                        ns = min(ss, SAT_CAP)
                        nr, ng, nb = colorsys.hsv_to_rgb(nh, ns, vv)
                        out = (int(nr * 255), int(ng * 255), int(nb * 255), a)
                    else:
                        out = c
                cache[c] = out
            px[x, y] = out
    name = "spec_%s_%d%d%03d" % (base[:6].lower(), skin_i, hair_i, hue_deg)
    im.save(os.path.join(OUT, name + ".png"))
    print("[tex]", name, "colours=", len(cache))


for v in VARIANTS:
    build(*v)
print("[tex] DONE %d variants" % len(VARIANTS))
