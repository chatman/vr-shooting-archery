"""Build the 10 m air pistol hall to spec, procedurally.

Everything else in this repo *infers* geometry from an image, so the numbers come out
approximate and the room comes out as a shell. This one goes the other way: the hall is
written down as measurements and emitted directly, so 10.00 m is 10.00 m, the walls are
closed, and every surface has an outward normal an engine can light.

Layout (metres, Y up, floor at y=0, downrange is -Z as everywhere else in this repo):

      x = -21                                                        x = +21
        +--------------------------------------------------------------+  z = 0
        |  target wall (wood) - 40 SIUS housings, 1 m pitch, cards y=1.5 |
        |     yellow lane number 1..40, 1 m above each target face      |
        |                  dark green floor  (0 .. 10 m)               |
        |                                                              |
        |=============== firing line / table front face ===============|  z = 10
        |############# bench 40.0 x 1.0 x 1.0 m (1 m clear ends) #######|  z = 11
        |   [chair] [chair] [chair] ... 40 chairs, 1 m pitch           |  z = 13
        |                                                              |
        |                  black matte floor (10 .. 30 m)              |
        |                                                              |
        +==[door]===========[door]==[door]===========[door]============+  z = 30
                     toughened glass curtain wall, 14 bays

Reading the brief: "30 m wide, 42 m long" plus "40 targets at 1 m spacing on the back
wall" plus "a 40 m table along the entire length" only closes one way -- the 42 m axis is
the one the target wall spans, and 30 m is the downrange depth. That leaves 10 m wall to
firing line, 1 m of bench, 2 m to the chairs and 17 m of hall behind them for audience and
circulation. The table is given as 40 m, so in a 42 m hall it stops 1 m short at each end.

Geometry is quads merged per material, so the whole hall is one draw call per surface
type. Textures are generated here (tileable, no assets on disk) rather than sampled from
a photo, because there is no photo -- this hall is specified, not observed.

usage: build_range.py [OUT.glb]
"""

import io
import json
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# ---------------------------------------------------------------- the spec

HALL_X      = 42.0    # target wall span ("length")
HALL_Z      = 30.0    # target wall -> glazed front wall ("width")
HALL_Y      = 6.0     # floor -> ceiling

N_LANES     = 40
LANE_PITCH  = 1.0
TARGET_Y    = 1.5     # centre height of the target faces
RANGE_DIST  = 10.0    # target wall -> firing line

# SIUS target housing, proportioned from sius.jpg: a green box with the card set into a
# recessed opening low on the front face and the wordmark above it. The reference gives
# card:face width 0.56, face height:width 1.58, card centre 65 % of the way down the face
# and the wordmark 36 % down -- those ratios are what is reproduced here, scaled so the
# 170 mm card sits in a housing of a believable size.
BOX_W, BOX_H, BOX_D  = 0.34, 0.54, 0.18
BOX_CARD_DOWN        = 0.653         # card centre, as a fraction of face height from top
BOX_TEXT_DOWN        = 0.364         # wordmark centre, same measure
BOX_TEXT_CAP         = 0.070         # wordmark cap height, as a fraction of face height
CARD_M               = 0.17          # printed card, 170 mm square

# Lane signs. Sized by legibility, not by taste: the back of the hall is HALL_Z away,
# and a sign is comfortably readable at roughly 200x its cap height, so the digits on
# the target signs must clear HALL_Z / 200 = 150 mm. At 72 % of the plate height that
# sets the plate. The bench plates are read from a few metres and can be smaller.
SIGN_W, SIGN_H       = 0.50, 0.30    # target lane sign, above each target
SIGN_ABOVE           = 1.00          # target centre -> sign centre
BENCH_SIGN_W, BENCH_SIGN_H = 0.40, 0.24
# The firing point plates sit high on the bench face, just under the top edge. Lower down
# they are hidden: the chairs share the lanes' 1 m pitch, so every chair back stands
# directly in front of its own number. Up here the sight line from the back of the hall
# passes over a (realistically proportioned) chair back with room to spare.
BENCH_SIGN_Y         = 0.74          # bottom edge, on the bench's audience-facing face
DIGIT_FRAC           = 0.72          # glyph cap height as a fraction of plate height

TABLE_LEN   = 40.0    # bench is 40 m in a 42 m hall: 1 m clear at each end
TABLE_D     = 1.0     # bench depth
TABLE_Y     = 1.0     # bench height
CHAIR_GAP   = 2.0     # bench front face -> chair centre

# Front wall is a toughened-glass curtain wall: 14 bays of 3 m, doors in four of them.
GLASS_BAYS  = 14
TRANSOM_Y   = (2.4, 4.2)
DOOR_W, DOOR_H = 1.2, 2.2
DOOR_X      = (-10.5, -4.5, 4.5, 10.5)   # bay centres 3, 5, 8, 10

LIGHT_ROWS_Z = tuple(1.5 + 3.0 * i for i in range(10))     # 10 rows across 30 m
LIGHT_COLS   = 14                                          # 3 m pitch across 42 m
LIGHT_W, LIGHT_D = 1.20, 0.60
# Every other panel in both axes is lit, so lamps sit on a 6 x 6 m grid under a 6 m
# ceiling -- a spacing-to-height ratio of 1, which is what a real hall of this size uses.
# Intensity is candela, per the glTF spec; 6 m of drop needs far more than a 3.5 m ceiling.
LIGHT_CD     = 2200.0

# derived
X0, X1 = -HALL_X / 2, HALL_X / 2
LANE_X = np.arange(N_LANES) * LANE_PITCH - (N_LANES - 1) * LANE_PITCH / 2
TABLE_X0, TABLE_X1 = -TABLE_LEN / 2, TABLE_LEN / 2
TABLE_Z0, TABLE_Z1 = RANGE_DIST, RANGE_DIST + TABLE_D     # 10 .. 11
CHAIR_Z = TABLE_Z1 + CHAIR_GAP                            # 13
INSIDE = (0.0, 1.75, HALL_Z / 2)                          # any point in the room's air

OUT = "/home/ishan/code/vrshooting/output/range_10m/range_10m.glb"


# ---------------------------------------------------------------- textures
#
# All noise is built from integer-frequency sinusoids so it tiles exactly -- a 40 m wall
# repeats its texture ~26 times, and a seam every 1.5 m would be the first thing you see.

def noise(size, freqs, rng, seed_phase=True):
    y, x = np.mgrid[0:size, 0:size] / size
    out = np.zeros((size, size))
    for f in freqs:
        for _ in range(4):
            ax, ay = rng.integers(-f, f + 1, 2)
            ph = rng.uniform(0, 2 * np.pi) if seed_phase else 0.0
            out += np.sin(2 * np.pi * (ax * x + ay * y) + ph) / f
    out -= out.min()
    return out / max(out.max(), 1e-6)


def wood(size=512, light=(168, 124, 74), dark=(96, 60, 30), planks=4, grain=26, seed=0):
    """Sawn boards: hard plank seams across v, grain lines running along v.

    The grain is a sinusoid in u pushed sideways by *low frequency* noise only. Warping it
    with the full noise stack instead reads as watered silk -- timber wanders slowly and
    is otherwise straight, so the waviness has to stay long-wavelength while the detail
    goes into thin dark lines (the ** below) and fibre speckle.
    """
    rng = np.random.default_rng(seed)
    v, u = np.mgrid[0:size, 0:size] / size
    warp = noise(size, (1, 3), rng) - 0.5

    # One dark line per band, each band a different strength. An even sinusoid gives
    # corduroy; timber's irregularity is almost entirely in how strong each line is.
    t = grain * u + 0.9 * warp
    band = np.floor(t)
    frac = t - band
    strength = (np.sin(band * 12.9898 + seed) * 43758.5453) % 1.0
    line = np.exp(-((frac - 0.5) ** 2) / (2 * 0.16 ** 2))
    g = 1.0 - line * (0.15 + 0.75 * strength)
    g *= 0.85 + 0.15 * noise(size, (2, 6), rng)     # broad tonal drift
    fibre = noise(size, (11, 37, 97), rng)
    g = np.clip(0.88 * g + 0.12 * fibre, 0, 1)

    # per-plank tone variation, then a dark seam line at each plank boundary
    pid = np.floor(v * planks)
    tone = (np.sin(pid * 12.9898) * 43758.5453) % 1.0
    g = np.clip(g * (0.86 + 0.26 * tone), 0, 1)
    seam = np.abs((v * planks) % 1.0 - 0.5) > (0.5 - 1.5 / (size / planks))
    g[seam] = 0.05

    lo, hi = np.array(dark, float), np.array(light, float)
    img = lo + (hi - lo) * g[..., None]
    return img.astype(np.uint8)


def matte(size=256, color=(24, 24, 24), speck=10, seed=0):
    """Flat paint / rubber matting: a base colour with just enough speckle to break banding."""
    rng = np.random.default_rng(seed)
    n = noise(size, (16, 48, 96), rng) - 0.5
    img = np.array(color, float) + n[..., None] * speck
    return np.clip(img, 0, 255).astype(np.uint8)


CARD_MM   = 170.0        # card is at least 170 x 170 mm
TEN_MM    = 11.5         # 10-ring diameter
STEP_MM   = 16.0         # each ring out adds 8 mm of radius
INNER_MM  = 5.0          # inner-ten circle, the tie-breaker
BLACK_MM  = 59.5         # 7-ring: the aiming mark runs from here inwards
RING_MM   = 0.15         # ring line thickness; the spec allows 0.1 .. 0.2


def ring_dia(ring):
    """Ring 10 is 11.5 mm and every ring outwards adds 16 mm of diameter, so ring 7 lands
    on 59.5 mm (the edge of the black) and ring 1 on 155.5 mm, inside the 170 mm card."""
    return TEN_MM + (10 - ring) * STEP_MM


def target_face(px=1024, ss=4):
    """ISSF 10 m air pistol face, drawn to the dimensioned spec.

    Rings 1-6 print black on white, 7-10 white inside the black aiming mark.

    `ss` is why this is not drawn at final size. A 0.15 mm ring line on a 170 mm card is
    1/1133 of the width -- 0.9 px at 1024, which PIL can only draw as a hard 1 px line
    (0.17 mm at best, and aliased into a dashed ellipse at worst). Drawing 4x oversize and
    filtering down puts a real sub-pixel hairline on the card, which is what a ring line is.
    """
    assert 0.1 <= RING_MM <= 0.2, "ring line thickness is specified as 0.1-0.2 mm"
    big = px * ss
    s = big / CARD_MM                                   # px per mm while drawing
    img = Image.new("RGB", (big, big), (246, 245, 241))
    d = ImageDraw.Draw(img)
    c = big / 2

    def circle(dia_mm, fill=None, outline=None, w=1):
        r = dia_mm * s / 2
        d.ellipse([c - r, c - r, c + r, c + r], fill=fill, outline=outline, width=w)

    # PIL draws an outline inboard of the bounding box, which is what we want: ring
    # dimensions are to the outside edge of the ring line.
    circle(BLACK_MM, fill=(16, 16, 16))                 # aiming mark = ring 7 outwards
    lw = max(1, int(round(RING_MM * s)))
    for ring in range(1, 11):
        circle(ring_dia(ring), outline=(250, 250, 250) if ring >= 7 else (20, 20, 20), w=lw)
    circle(INNER_MM, fill=(250, 250, 250))              # inner ten

    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                                  int(6.5 * s))
    except OSError:
        font = ImageFont.load_default()
    for ring in range(1, 9):
        r_mm = ring_dia(ring) / 2 - 4.0                 # mid-band of the ring
        col = (250, 250, 250) if ring >= 7 else (20, 20, 20)
        for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            d.text((c + dx * r_mm * s, c + dy * r_mm * s), str(ring),
                   fill=col, font=font, anchor="mm")
    return np.asarray(img.resize((px, px), Image.LANCZOS))


def sius_front(px=512):
    """Front face of the target housing: green, wordmark, and the recessed card opening.

    Only the front is textured. Wrapping this image around the whole box would print SIUS
    down its sides too, so the other five faces take the flat green material instead.
    """
    h = int(round(px * BOX_H / BOX_W))
    img = Image.new("RGB", (px, h), (128, 170, 128))
    d = ImageDraw.Draw(img)

    # Recessed opening: a darker green well a little larger than the card, with a light
    # top edge and dark bottom edge so the card reads as set into the box, not stuck on.
    ow = CARD_M / BOX_W * px * 1.12
    cy = BOX_CARD_DOWN * h
    box = [px / 2 - ow / 2, cy - ow / 2, px / 2 + ow / 2, cy + ow / 2]
    d.rectangle(box, fill=(104, 142, 104))
    d.line([box[0], box[1], box[2], box[1]], fill=(88, 122, 88), width=max(1, px // 128))
    d.line([box[0], box[3], box[2], box[3]], fill=(150, 190, 150), width=max(1, px // 170))

    cap = BOX_TEXT_CAP * h
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                  int(cap * 1.35))
    except OSError:
        font = ImageFont.load_default()
    # Letter-spaced, the way the reference sets it -- PIL has no tracking, so step by hand.
    text, track = "SIUS", cap * 0.18
    widths = [d.textlength(ch, font=font) for ch in text]
    x = px / 2 - (sum(widths) + track * (len(text) - 1)) / 2
    for ch, w in zip(text, widths):
        d.text((x, BOX_TEXT_DOWN * h), ch, fill=(28, 32, 28), font=font, anchor="lm")
        x += w + track
    return np.asarray(img)


def lane_atlas(cols=8, rows=5, cw=200, ch=120):
    """One texture holding all 40 lane serials, black on yellow; each placard samples its
    own cell. Forty separate 1-target textures would be forty draw calls and forty images
    in the buffer, for what is really one sign repeated."""
    img = Image.new("RGB", (cols * cw, rows * ch), (250, 202, 20))
    d = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                                  int(ch * DIGIT_FRAC))
    except OSError:
        font = ImageFont.load_default()
    for i in range(cols * rows):
        x0, y0 = (i % cols) * cw, (i // cols) * ch
        d.rectangle([x0 + 3, y0 + 3, x0 + cw - 4, y0 + ch - 4], outline=(20, 18, 10), width=5)
        d.text((x0 + cw / 2, y0 + ch / 2), str(i + 1), fill=(16, 14, 8), font=font, anchor="mm")
    return np.asarray(img)


def lane_cell_uv(i, cols=8, rows=5):
    return (i % cols) / cols, (i // cols) / rows, (i % cols + 1) / cols, (i // cols + 1) / rows


# ---------------------------------------------------------------- mesh building

class Scene:
    """Quads accumulated per material; one glTF primitive per material at write time."""

    def __init__(self):
        self.mats = {}                     # name -> material dict
        self.geo = {}                      # name -> [pos, nrm, uv, idx]
        self.lights = []                   # (position, intensity)

    def material(self, name, texture=None, color=(1, 1, 1), rough=0.85, metal=0.0,
                 emissive=None, double_sided=False, alpha=1.0):
        self.mats[name] = dict(texture=texture, color=color, rough=rough, metal=metal,
                               emissive=emissive, double_sided=double_sided, alpha=alpha)
        self.geo[name] = [[], [], [], []]
        return name

    def rect(self, mat, origin, uvec, vvec, tiles=1.0, uv=None, facing=None, outward=None):
        """Quad at origin spanned by uvec, vvec. Normal is cross(uvec, vvec) -- the order
        of the two vectors is what decides which way the face looks. `tiles` is repeats
        per metre.

        `facing` is a point the normal must point towards. Backwards winding is invisible
        in the data and only shows up as a hole in a backface-culled render, so every
        surface that has an obvious "front" declares it and gets checked here.
        """
        o, U, V = (np.asarray(v, float) for v in (origin, uvec, vvec))
        n = np.cross(U, V)
        n = n / max(np.linalg.norm(n), 1e-12)
        want = None
        if facing is not None:
            want = np.asarray(facing, float) - (o + (U + V) / 2)
        elif outward is not None:
            want = np.asarray(outward, float)
        if want is not None and float(n @ want) <= 0:
            raise AssertionError(
                "{}: quad at {} faces {} but should face {} -- swap uvec/vvec"
                .format(mat, (o + (U + V) / 2).round(2), n.round(2), np.round(want, 2)))
        p = np.stack([o, o + U, o + U + V, o + V])
        if uv is None:
            su, sv = np.linalg.norm(U) * tiles, np.linalg.norm(V) * tiles
            t = np.array([[0, sv], [su, sv], [su, 0], [0, 0]], float)
        else:
            u0, v0, u1, v1 = uv
            t = np.array([[u0, v1], [u1, v1], [u1, v0], [u0, v0]], float)

        pos, nrm, uvs, idx = self.geo[mat]
        base = len(pos)
        pos.extend(p.tolist())
        nrm.extend([n.tolist()] * 4)
        uvs.extend(t.tolist())
        idx.extend([base, base + 1, base + 2, base, base + 2, base + 3])

    def box(self, mat, lo, hi, tiles=1.0, skip=()):
        """Axis-aligned box, faces pointing out. `skip` drops faces you will never see
        (a light panel's top, a wall-mounted board's back) -- they cost triangles and,
        for a doorway lining, produce z-fighting with the wall they sit in."""
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        dx, dy, dz = x1 - x0, y1 - y0, z1 - z0
        faces = {
            "+x": ((x1, y0, z1), (0, 0, -dz), (0, dy, 0), (1, 0, 0)),
            "-x": ((x0, y0, z0), (0, 0, dz), (0, dy, 0), (-1, 0, 0)),
            "+y": ((x0, y1, z1), (dx, 0, 0), (0, 0, -dz), (0, 1, 0)),
            "-y": ((x0, y0, z0), (dx, 0, 0), (0, 0, dz), (0, -1, 0)),
            "+z": ((x0, y0, z1), (dx, 0, 0), (0, dy, 0), (0, 0, 1)),
            "-z": ((x1, y0, z0), (-dx, 0, 0), (0, dy, 0), (0, 0, -1)),
        }
        for k, (o, u, v, out) in faces.items():
            if k not in skip:
                self.rect(mat, o, u, v, tiles, outward=out)

    def light(self, pos, intensity):
        self.lights.append((list(pos), intensity))

    def stats(self):
        tris = sum(len(g[3]) // 3 for g in self.geo.values())
        verts = sum(len(g[0]) for g in self.geo.values())
        return verts, tris


# ---------------------------------------------------------------- the hall

def build():
    s = Scene()

    M_WOOD_WALL = s.material("wall_wood", wood(512, (140, 98, 56), (70, 42, 20),
                                               planks=5, grain=40, seed=3), rough=0.72)
    M_WOOD_TBL  = s.material("bench_wood", wood(512, (186, 141, 88), (112, 72, 38),
                                                planks=3, grain=34, seed=11), rough=0.55)
    M_PLASTER   = s.material("wall_plaster", matte(256, (206, 208, 203), 6, seed=5))
    M_CEIL      = s.material("ceiling", matte(256, (232, 233, 230), 4, seed=7))
    M_GREEN     = s.material("floor_green", matte(256, (26, 58, 36), 9, seed=1), rough=0.95)
    M_BLACK     = s.material("floor_black", matte(256, (17, 17, 18), 7, seed=2), rough=0.98)
    M_FACE      = s.material("target_face", target_face(1024), rough=0.9)
    M_BOX       = s.material("sius_box", None, color=(0.50, 0.665, 0.50), rough=0.6)
    M_BOXFRONT  = s.material("sius_box_front", sius_front(512), rough=0.6)
    M_LANE      = s.material("lane_number", lane_atlas(), rough=0.7)
    M_LAMP      = s.material("light_panel", None, color=(1, 1, 0.97), rough=0.4,
                             emissive=(1.0, 0.98, 0.92))
    M_FRAME     = s.material("metal_frame", None, color=(0.30, 0.32, 0.35),
                             rough=0.35, metal=0.9)
    M_GLASS     = s.material("glass", None, color=(0.72, 0.82, 0.80), rough=0.04,
                             metal=0.0, alpha=0.16, double_sided=True)
    M_DOORGLASS = s.material("door_glass", None, color=(0.72, 0.82, 0.80), rough=0.04,
                             metal=0.0, alpha=0.20, double_sided=True)
    M_SEAT      = s.material("chair_seat", None, color=(0.10, 0.20, 0.36), rough=0.75)

    # --- shell -----------------------------------------------------------------
    # Floor splits at the firing line: dark green downrange, black matte behind it, so
    # the bench stands on the black. Every shell face is declared as facing INSIDE -- see
    # Scene.rect; get one backwards and it turns into a hole from within the room.
    s.rect(M_GREEN,  (X0, 0, 0), (0, 0, RANGE_DIST), (HALL_X, 0, 0), tiles=0.5, facing=INSIDE)
    s.rect(M_BLACK,  (X0, 0, RANGE_DIST), (0, 0, HALL_Z - RANGE_DIST), (HALL_X, 0, 0),
           tiles=0.5, facing=INSIDE)
    s.rect(M_CEIL,   (X0, HALL_Y, 0), (HALL_X, 0, 0), (0, 0, HALL_Z), tiles=0.5, facing=INSIDE)
    s.rect(M_WOOD_WALL, (X0, 0, 0), (HALL_X, 0, 0), (0, HALL_Y, 0), tiles=0.8, facing=INSIDE)
    s.rect(M_PLASTER, (X0, 0, 0), (0, HALL_Y, 0), (0, 0, HALL_Z), tiles=0.5, facing=INSIDE)
    s.rect(M_PLASTER, (X1, 0, 0), (0, 0, HALL_Z), (0, HALL_Y, 0), tiles=0.5, facing=INSIDE)

    # --- glazed front wall -----------------------------------------------------
    # A toughened-glass curtain wall: 14 bays of 3 m between mullions, two transoms, and
    # four glass doors set into four of the bays. The glass is one double-sided plane per
    # pane at z = HALL_Z, alpha 0.16; the aluminium carries the wall's readability, since
    # a bare transparent sheet is invisible from most angles.
    bay = HALL_X / GLASS_BAYS
    mull_w, mull_d = 0.10, 0.18            # mullion section, sitting inboard of the glass
    door_bay = {round(x, 3) for x in DOOR_X}

    for i in range(GLASS_BAYS):
        a, b = X0 + i * bay, X0 + (i + 1) * bay
        cx = (a + b) / 2
        if round(cx, 3) in door_bay:        # pane over the door, plus a strip either side
            da, db = cx - DOOR_W / 2, cx + DOOR_W / 2
            s.rect(M_GLASS, (a, 0, HALL_Z), (0, HALL_Y, 0), (da - a, 0, 0), facing=INSIDE)
            s.rect(M_GLASS, (db, 0, HALL_Z), (0, HALL_Y, 0), (b - db, 0, 0), facing=INSIDE)
            s.rect(M_GLASS, (da, DOOR_H, HALL_Z), (0, HALL_Y - DOOR_H, 0), (DOOR_W, 0, 0),
                   facing=INSIDE)
        else:
            s.rect(M_GLASS, (a, 0, HALL_Z), (0, HALL_Y, 0), (bay, 0, 0), facing=INSIDE)

    for i in range(GLASS_BAYS + 1):        # mullions, including both end posts
        x = X0 + i * bay
        # The end posts are clamped to the side walls; centred on the edge they would
        # stick 50 mm outside the hall, which is exactly what the extents check catches.
        s.box(M_FRAME, (max(x - mull_w / 2, X0), 0, HALL_Z - mull_d),
              (min(x + mull_w / 2, X1), HALL_Y, HALL_Z), tiles=2, skip=("+z",))
    for y in TRANSOM_Y:
        # Above the door head a transom crosses the whole wall; below it, it has to stop
        # at each opening (both defaults are above, but the constants are meant to be moved)
        run = [X0, X1] if y > DOOR_H else (
            [X0] + [v for x in DOOR_X for v in (x - DOOR_W / 2, x + DOOR_W / 2)] + [X1])
        for j in range(0, len(run) - 1, 2):
            s.box(M_FRAME, (run[j], y - 0.05, HALL_Z - mull_d + 0.02),
                  (run[j + 1], y + 0.05, HALL_Z - 0.02), tiles=2)
    s.box(M_FRAME, (X0, HALL_Y - 0.12, HALL_Z - mull_d), (X1, HALL_Y, HALL_Z), tiles=1)
    s.box(M_FRAME, (X0, 0, HALL_Z - mull_d), (X1, 0.12, HALL_Z), tiles=1)   # cill

    for i, x in enumerate(DOOR_X):
        a, b = x - DOOR_W / 2, x + DOOR_W / 2
        s.box(M_FRAME, (a - 0.05, 0, HALL_Z - 0.06), (a, DOOR_H + 0.06, HALL_Z), tiles=2)
        s.box(M_FRAME, (b, 0, HALL_Z - 0.06), (b + 0.05, DOOR_H + 0.06, HALL_Z), tiles=2)
        s.box(M_FRAME, (a - 0.05, DOOR_H, HALL_Z - 0.06), (b + 0.05, DOOR_H + 0.06, HALL_Z),
              tiles=2)
        # Glass leaf in an aluminium rail top and bottom -- what a glazed entrance is.
        s.rect(M_DOORGLASS, (a + 0.02, 0.12, HALL_Z - 0.03), (0, DOOR_H - 0.26, 0),
               (DOOR_W - 0.04, 0, 0), facing=INSIDE)
        for yy in (0.02, DOOR_H - 0.14):
            s.box(M_FRAME, (a + 0.02, yy, HALL_Z - 0.05), (b - 0.02, yy + 0.12, HALL_Z - 0.01),
                  tiles=3)
        hx = b - 0.20 if i % 2 == 0 else a + 0.12   # push bar, hinge side alternating
        s.box(M_FRAME, (hx, 0.90, HALL_Z - 0.10), (hx + 0.08, 1.14, HALL_Z - 0.04), tiles=4)

    # --- targets in their SIUS housings -----------------------------------------
    # The card keeps its 1.50 m centre; the box is positioned around it, which is why the
    # box bottom is a derived number and not a round one.
    y0 = TARGET_Y - (1.0 - BOX_CARD_DOWN) * BOX_H          # box bottom
    for i, x in enumerate(LANE_X):
        # Five plain green faces; the sixth is the textured front, hence the +z skip.
        s.box(M_BOX, (x - BOX_W / 2, y0, 0), (x + BOX_W / 2, y0 + BOX_H, BOX_D),
              tiles=3, skip=("-z", "+z"))
        s.rect(M_BOXFRONT, (x - BOX_W / 2, y0, BOX_D), (BOX_W, 0, 0), (0, BOX_H, 0),
               uv=(0, 0, 1, 1), facing=INSIDE)
        s.rect(M_FACE, (x - CARD_M / 2, TARGET_Y - CARD_M / 2, BOX_D + 0.002),
               (CARD_M, 0, 0), (0, CARD_M, 0), uv=(0, 0, 1, 1), facing=INSIDE)
        s.rect(M_LANE, (x - SIGN_W / 2, TARGET_Y + SIGN_ABOVE - SIGN_H / 2, 0.002),
               (SIGN_W, 0, 0), (0, SIGN_H, 0), uv=lane_cell_uv(i), facing=INSIDE)

    # --- bench -----------------------------------------------------------------
    # Boxy by request: a solid volume, no legs. Its downrange face IS the firing line,
    # which is what puts the targets at exactly 10.00 m.
    s.box(M_WOOD_TBL, (TABLE_X0, 0, TABLE_Z0), (TABLE_X1, TABLE_Y, TABLE_Z1),
          tiles=0.8, skip=("-y",))
    # Firing point numbers on the bench's front (audience-facing) face, same atlas as the
    # target signs so lane N reads identically at both ends of the shot.
    for i, x in enumerate(LANE_X):
        s.rect(M_LANE, (x - BENCH_SIGN_W / 2, BENCH_SIGN_Y, TABLE_Z1 + 0.002),
               (BENCH_SIGN_W, 0, 0), (0, BENCH_SIGN_H, 0),
               uv=lane_cell_uv(i), facing=INSIDE)

    # --- chairs ----------------------------------------------------------------
    for x in LANE_X:
        chair(s, M_SEAT, M_FRAME, x, CHAIR_Z)

    # --- ceiling lights --------------------------------------------------------
    step = HALL_X / LIGHT_COLS
    for r, z in enumerate(LIGHT_ROWS_Z):
        for c in range(LIGHT_COLS):
            x = X0 + step * (c + 0.5)
            s.box(M_LAMP, (x - LIGHT_W / 2, HALL_Y - 0.06, z - LIGHT_D / 2),
                  (x + LIGHT_W / 2, HALL_Y, z + LIGHT_D / 2), tiles=1, skip=("+y",))
            if r % 2 == 0 and c % 2 == 0:    # lit panels on a 6 x 6 m grid; see LIGHT_CD
                s.light((x, HALL_Y - 0.10, z), LIGHT_CD)
    return s


def chair(s, seat_mat, frame_mat, x, z):
    """Stacking chair, facing downrange (-Z): seat, backrest and four legs.

    Back top lands at 0.82 m -- a real stacking chair, and low enough that it does not
    mask the firing point number on the bench behind it."""
    w, d, sh = 0.45, 0.45, 0.45
    s.box(seat_mat, (x - w / 2, sh - 0.05, z - d / 2), (x + w / 2, sh, z + d / 2), tiles=2)
    s.box(seat_mat, (x - w / 2, sh + 0.05, z + d / 2 - 0.05),
          (x + w / 2, sh + 0.37, z + d / 2), tiles=2)
    for sx in (-1, 1):
        for sz in (-1, 1):
            lx = x + sx * (w / 2 - 0.05)
            lz = z + sz * (d / 2 - 0.05)
            s.box(frame_mat, (lx - 0.02, 0, lz - 0.02), (lx + 0.02, sh - 0.05, lz + 0.02),
                  tiles=4, skip=("-y",))
    # backrest posts
    for sx in (-1, 1):
        lx = x + sx * (w / 2 - 0.05)
        s.box(frame_mat, (lx - 0.02, sh, z + d / 2 - 0.05),
              (lx + 0.02, sh + 0.39, z + d / 2 - 0.01), tiles=4)


# ---------------------------------------------------------------- glTF out

class GLB:
    def __init__(self, generator="build_range.py"):
        self.blobs, self.off = [], 0
        self.bv, self.acc, self.img, self.texs = [], [], [], []
        self.mats, self.meshes, self.nodes = [], [], []
        self.generator = generator
        self.lights = []

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

    def _accessor(self, arr, ctype, atype, target, minmax=False):
        a = {"bufferView": self._bufview(self._add(arr.tobytes()), target),
             "componentType": ctype, "count": len(arr), "type": atype}
        if minmax:
            a["min"] = arr.min(0).tolist()
            a["max"] = arr.max(0).tolist()
        self.acc.append(a)
        return len(self.acc) - 1

    def _texture(self, rgb):
        buf = io.BytesIO()
        # PNG for the ring targets and the numerals -- JPEG rings them badly at this size.
        Image.fromarray(rgb).save(buf, format="PNG", optimize=True)
        self.img.append({"bufferView": self._bufview(self._add(buf.getvalue())),
                         "mimeType": "image/png"})
        self.texs.append({"source": len(self.img) - 1})
        return len(self.texs) - 1

    def add_group(self, name, spec, pos, nrm, uv, idx):
        pbr = {"baseColorFactor": list(spec["color"]) + [spec.get("alpha", 1.0)],
               "metallicFactor": spec["metal"], "roughnessFactor": spec["rough"]}
        if spec["texture"] is not None:
            pbr["baseColorTexture"] = {"index": self._texture(spec["texture"])}
        mat = {"name": name, "pbrMetallicRoughness": pbr,
               "doubleSided": bool(spec["double_sided"])}
        if spec.get("alpha", 1.0) < 1.0:
            # BLEND rather than KHR_materials_transmission: every importer understands it,
            # and the glazing only has to read as glass, not refract what is behind it.
            mat["alphaMode"] = "BLEND"
        if spec["emissive"]:
            mat["emissiveFactor"] = list(spec["emissive"])
        self.mats.append(mat)

        a_p = self._accessor(pos.astype("<f4"), 5126, "VEC3", 34962, minmax=True)
        a_n = self._accessor(nrm.astype("<f4"), 5126, "VEC3", 34962)
        a_t = self._accessor(uv.astype("<f4"), 5126, "VEC2", 34962)
        a_i = self._accessor(idx.astype("<u4"), 5125, "SCALAR", 34963)
        self.meshes.append({"name": name, "primitives": [
            {"attributes": {"POSITION": a_p, "NORMAL": a_n, "TEXCOORD_0": a_t},
             "indices": a_i, "material": len(self.mats) - 1}]})
        self.nodes.append({"mesh": len(self.meshes) - 1, "name": name})

    def add_light(self, pos, intensity):
        if not self.lights:
            self.lights = []
        self.lights.append({"type": "point", "intensity": intensity,
                            "color": [1.0, 0.97, 0.92], "range": 12.0})
        self.nodes.append({"name": "lamp_{}".format(len(self.lights)),
                           "translation": [float(v) for v in pos],
                           "extensions": {"KHR_lights_punctual": {"light": len(self.lights) - 1}}})

    def write(self, path):
        g = {"asset": {"version": "2.0", "generator": self.generator},
             "scene": 0, "scenes": [{"nodes": list(range(len(self.nodes)))}],
             "nodes": self.nodes, "meshes": self.meshes, "materials": self.mats,
             "textures": self.texs, "images": self.img,
             "accessors": self.acc, "bufferViews": self.bv,
             "buffers": [{"byteLength": self.off}]}
        if self.lights:
            g["extensionsUsed"] = ["KHR_lights_punctual"]
            g["extensions"] = {"KHR_lights_punctual": {"lights": self.lights}}
        js = json.dumps(g, separators=(",", ":")).encode()
        js += b" " * ((-len(js)) % 4)
        binc = b"".join(self.blobs)
        total = 12 + 8 + len(js) + 8 + len(binc)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(struct.pack("<III", 0x46546C67, 2, total))
            f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
            f.write(struct.pack("<II", len(binc), 0x004E4942)); f.write(binc)
        return total


# ---------------------------------------------------------------- verify

def lamps_expected():
    return sum(1 for r in range(len(LIGHT_ROWS_Z)) for c in range(LIGHT_COLS)
               if r % 2 == 0 and c % 2 == 0)


def verify(path):
    """Re-open the written GLB and measure it. The point of a specified room is that the
    numbers survive into the file, so check the file rather than the intent -- a wrong
    tiles= or a swapped axis would still build and still look plausible in a render."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import render_scene as R

    loaded, lamps = R.load(path)
    prims = {p["name"]: p for p in loaded}
    ok = True

    def check(label, got, want, tol=1e-6, unit="m"):
        nonlocal ok
        good = abs(got - want) <= tol
        ok &= good
        print("  [{}] {:<34} {:8.3f} {}  (want {:g})".format(
            "ok" if good else "FAIL", label, got, unit, want))

    def least(label, got, want, unit="m"):
        """One-sided: the value has to reach a floor, not hit a target."""
        nonlocal ok
        good = got >= want - 1e-9
        ok &= good
        print("  [{}] {:<34} {:8.3f} {}  (want >= {:g})".format(
            "ok" if good else "FAIL", label, got, unit, want))

    def quads(name):
        """Per-quad centres, in the order they were emitted (4 verts each)."""
        p = prims[name]["pos"]
        return p.reshape(-1, 4, 3).mean(1)

    faces = quads("target_face")
    xs = np.sort(faces[:, 0])
    check("targets", len(faces), N_LANES, unit="  ")
    check("target pitch", float(np.diff(xs).mean()), LANE_PITCH)
    check("target pitch spread", float(np.ptp(np.diff(xs))), 0.0, tol=1e-4)
    check("target centre height", float(faces[:, 1].mean()), TARGET_Y, tol=1e-6)
    check("target row width", float(xs[-1] - xs[0]), (N_LANES - 1) * LANE_PITCH)

    bench = prims["bench_wood"]["pos"]
    check("wall -> firing line", float(bench[:, 2].min()), RANGE_DIST, tol=1e-6)
    check("bench depth", float(np.ptp(bench[:, 2])), TABLE_D)
    check("bench height", float(bench[:, 1].max()), TABLE_Y)
    check("bench length", float(np.ptp(bench[:, 0])), TABLE_LEN)

    # 12 quads per chair in the seat material (seat box + backrest box), in chair() order.
    seats = quads("chair_seat").reshape(-1, 12, 3)
    cx = np.sort(seats[:, :, 0].mean(1))
    seat_z = seats[:, :6, 2].mean(1)          # seat pan only; the backrest sits behind it
    check("chairs", len(cx), N_LANES, unit="  ")
    check("chair pitch", float(np.diff(cx).mean()), LANE_PITCH, tol=1e-5)
    check("bench face -> chair centre", float(seat_z.mean() - TABLE_Z1), CHAIR_GAP, tol=1e-5)

    # Both sign sets share one atlas material, so split them by which plane they sit on.
    signs = quads("lane_number")
    tgt = signs[signs[:, 2] < RANGE_DIST / 2]
    bench_sign = signs[signs[:, 2] >= RANGE_DIST / 2]
    check("target lane signs", len(tgt), N_LANES, unit="  ")
    check("firing point signs", len(bench_sign), N_LANES, unit="  ")
    check("firing point signs on bench face", float(bench_sign[:, 2].mean()),
          TABLE_Z1, tol=0.005)
    # Lane N on the wall and firing point N on the bench have to be the same lane.
    check("sign columns aligned",
          float(np.abs(np.sort(tgt[:, 0]) - np.sort(bench_sign[:, 0])).max()), 0.0, tol=1e-4)
    sign_h = float(np.ptp(prims["lane_number"]["pos"].reshape(-1, 4, 3)[0][:, 1]))
    fronts = prims["sius_box_front"]["pos"].reshape(-1, 4, 3)
    faces4 = prims["target_face"]["pos"].reshape(-1, 4, 3)
    check("SIUS housings", len(fronts), N_LANES, unit="  ")
    check("housing front face at", float(fronts[:, :, 2].mean()), BOX_D, tol=1e-6)
    least("card proud of housing", float(faces4[:, :, 2].mean() - BOX_D), 0.001)
    # The card has to sit inside the housing on every side, not just look like it does.
    margin = min(float(np.ptp(fronts[0][:, 0]) - np.ptp(faces4[0][:, 0])) / 2,
                 float(fronts[0][:, 1].max() - faces4[0][:, 1].max()),
                 float(faces4[0][:, 1].min() - fronts[0][:, 1].min()))
    least("card margin inside housing", margin, 0.02)
    check("sign above target centre", float(tgt[:, 1].mean() - TARGET_Y), SIGN_ABOVE,
          tol=1e-6)
    # Legibility from the back of the hall: cap height must clear distance / 200.
    least("target sign digit height", sign_h * DIGIT_FRAC, HALL_Z / 200.0)

    # The card is a texture, not geometry, so check the pixels. Sample along a 45 degree
    # ray from the centre: the ring numerals are printed on the horizontal and vertical
    # axes, and a straight row through the middle measures them instead of the rings --
    # first-to-last dark pixel reads the aiming mark as 149 mm, and stopping at the first
    # bright run reads it as 32 mm (the white "8" inside the black). The diagonal crosses
    # nothing but ring lines.
    card = prims["target_face"]["tex"]
    n = card.shape[0]
    mm_per_px = CARD_MM / n
    k = 1 / np.sqrt(2)
    t = np.arange(0, int(n / 2 * k))
    ray = card[(n // 2 + np.rint(t * k)).astype(int),
               (n // 2 + np.rint(t * k)).astype(int)].astype(float).mean(1)

    THR = 131.0                      # halfway between the black ink and the paper

    def sub_px(i):
        """Sub-pixel radius where the ray crosses THR between samples i and i+1.

        Taking the last dark pixel instead reads ~0.5 mm small: the edge is anti-aliased
        by the 4x downsample, and the black is bounded by the white 7-ring hairline drawn
        centred on 59.5 mm, so the ink genuinely stops just inside the nominal diameter.
        """
        a, b = ray[i], ray[min(i + 1, len(ray) - 1)]
        f = 0.0 if b == a else np.clip((THR - a) / (b - a), 0.0, 1.0)
        return i + f + 0.5

    edge, gap, seen = 0, 0, False
    for i, v in enumerate(ray):
        if v < THR:
            edge, gap, seen = i, 0, True
        elif seen:                       # the ray starts inside the white inner ten
            gap += 1
            if gap >= 6:
                break
    # Ring lines are drawn with their OUTER edge on the nominal diameter, the usual
    # reading of ring dimensions. So the last black ink sits one line thickness inside
    # 59.5 mm on each side -- outboard of that is the white 7-ring line, which a
    # luminance scan cannot tell from paper. Expect the ink edge, not the nominal.
    check("black aiming mark (ink)", 2 * sub_px(edge) * mm_per_px, BLACK_MM - 2 * RING_MM,
          tol=mm_per_px, unit="mm")
    lit = 0
    while lit + 1 < len(ray) and ray[lit + 1] > THR:
        lit += 1
    check("inner ten circle", 2 * sub_px(lit) * mm_per_px, INNER_MM,
          tol=mm_per_px, unit="mm")

    green, black = prims["floor_green"]["pos"], prims["floor_black"]["pos"]
    check("green floor to", float(green[:, 2].max()), RANGE_DIST)
    check("black floor from", float(black[:, 2].min()), RANGE_DIST)
    check("black floor to", float(black[:, 2].max()), HALL_Z)

    allpos = np.concatenate([p["pos"] for p in prims.values()])
    check("hall width (x)", float(np.ptp(allpos[:, 0])), HALL_X, tol=0.05)
    check("hall height (y)", float(np.ptp(allpos[:, 1])), HALL_Y, tol=0.01)
    check("hall depth (z)", float(np.ptp(allpos[:, 2])), HALL_Z, tol=0.01)
    check("doors", len(quads("door_glass")), len(DOOR_X), unit="  ")
    check("ceiling panels", len(quads("light_panel")) // 5,
          len(LIGHT_ROWS_Z) * LIGHT_COLS, unit="  ")
    check("ceiling lamps", len(lamps), lamps_expected(), unit="  ")

    # The glazing is cut into panes bay by bay, so its total area is the one number that
    # catches a bay left out or a door opening not subtracted.
    p = prims["glass"]["pos"].reshape(-1, 4, 3)
    area = float(np.linalg.norm(np.cross(p[:, 1] - p[:, 0], p[:, 3] - p[:, 0]), axis=1).sum())
    check("glazed area", area, HALL_X * HALL_Y - len(DOOR_X) * DOOR_W * DOOR_H,
          tol=0.01, unit="m2")
    check("glass plane at z", float(prims["glass"]["pos"][:, 2].mean()), HALL_Z)
    return ok


def main():
    dst = sys.argv[1] if len(sys.argv) > 1 else OUT
    s = build()

    glb = GLB()
    for name, spec in s.mats.items():
        pos, nrm, uv, idx = s.geo[name]
        if not idx:
            continue
        glb.add_group(name, spec, np.array(pos), np.array(nrm), np.array(uv), np.array(idx))
    for pos, cd in s.lights:
        glb.add_light(pos, cd)
    size = glb.write(dst)

    verts, tris = s.stats()
    print("wrote {}  ({:.2f} MB)".format(dst, size / 1e6))
    print("  {} verts / {} tris / {} materials / {} punctual lights".format(
        verts, tris, len(glb.mats), len(glb.lights)))
    print("  hall {:.1f} x {:.1f} x {:.1f} m, {} lanes at {:.2f} m pitch".format(
        HALL_X, HALL_Z, HALL_Y, N_LANES, LANE_PITCH))
    print("  target wall z=0, faces at y={:.2f}; firing line z={:.2f}; bench {:.2f}..{:.2f};"
          " chairs z={:.2f}".format(TARGET_Y, RANGE_DIST, TABLE_Z0, TABLE_Z1, CHAIR_Z))
    print("  lanes x={:.1f} .. {:.1f}".format(LANE_X[0], LANE_X[-1]))
    print("measured back out of the file:")
    if not verify(dst):
        sys.exit("spec check failed")


if __name__ == "__main__":
    main()
