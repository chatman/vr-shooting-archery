"""Render a walkthrough of the hall to an mp4.

The shot list: enter through the door nearest lane 23, walk down the hall to firing point
23, turn and look back at the doors, sweep left and right along the firing line, then turn
downrange and zoom in on target 23.

Camera state is (eye, yaw, pitch, fov). Keyframes are written as look-AT points because
that is how you think about a shot -- "aim at the target" -- but they are converted to
yaw/pitch immediately and interpolated as angles. Interpolating the aim point instead
swings the view at wildly uneven angular speed whenever the target is near the camera, and
a 180 degree turn through an aim point is undefined at the halfway mark.

Frames are rendered by render_scene.py, several at once: the rasteriser is a per-triangle
numpy loop, so it is CPU bound and scales across processes, while each worker's shading
pass is small enough to share one GPU.

usage: flythrough.py [OUT.mp4] [--fps 24] [--width 1280] [--jobs 6] [--frames a:b]
"""

import math
import multiprocessing as mp
import os
import subprocess
import sys
import tempfile

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))

MODEL = os.path.join(ROOT, "output", "range_10m", "range_10m.glb")
OUT = os.path.join(ROOT, "output", "range_10m", "walkthrough.mp4")
FRAMEDIR = os.path.join(tempfile.gettempdir(), "vrshooting_flythrough_frames")

LANE = 23                       # the lane this walkthrough is about
LANE_X = -19.5 + (LANE - 1)     # = 2.5; lanes run 1..40 at 1 m pitch, centred on 0
DOOR_X = 4.5                    # the front door nearest that lane
EYE_H = 1.65                    # standing eye height
GLASS_Z = 30.0

TARGET = (LANE_X, 1.5, 0.19)    # the card in its housing, 190 mm off the wall

# Each move interpolates to its (eye, aim, fov) over dur seconds. `ease` shapes the
# timing; `bob` adds a small vertical oscillation, which is what makes a moving camera
# read as a person walking rather than a drone.
SHOTS = [
    dict(dur=0.0, eye=(DOOR_X, EYE_H, GLASS_Z + 1.8), aim=(DOOR_X, 1.55, 20.0), fov=70),
    # 1. through the doorway
    dict(dur=3.2, eye=(DOOR_X, EYE_H, GLASS_Z - 1.2), aim=(DOOR_X - 0.3, 1.55, 16.0),
         fov=70, ease="in", bob=True),
    # 2. down the hall, drifting across to line up with lane 23's chair gap
    dict(dur=4.4, eye=(3.0, EYE_H, 16.5), aim=(LANE_X, 1.5, 2.0), fov=70,
         ease="linear", bob=True),
    # 3. arrive at firing point 23 and settle
    dict(dur=3.4, eye=(LANE_X, EYE_H, 11.75), aim=TARGET, fov=70, ease="out", bob=True),
    dict(dur=1.2, eye=(LANE_X, EYE_H, 11.75), aim=TARGET, fov=70, ease="linear"),
    # 4. turn around and look back at the doors. The aim is offset in x so the turn is
    #    not an exact 180 -- at exactly 180 the direction of the spin is arbitrary.
    dict(dur=3.6, eye=(LANE_X, EYE_H, 11.75), aim=(DOOR_X + 1.0, 1.25, GLASS_Z), fov=70,
         ease="inout"),
    dict(dur=1.6, eye=(LANE_X, EYE_H, 11.75), aim=(DOOR_X + 1.0, 1.25, GLASS_Z), fov=70,
         ease="linear"),
    # 5. sweep left along the hall, then all the way right
    dict(dur=3.4, eye=(LANE_X, EYE_H, 11.75), aim=(-20.0, 1.45, 14.0), fov=70,
         ease="inout"),
    dict(dur=4.6, eye=(LANE_X, EYE_H, 11.75), aim=(20.0, 1.45, 14.0), fov=70,
         ease="inout"),
    # 6. back downrange onto lane 23, then zoom in on the target
    dict(dur=2.8, eye=(LANE_X, EYE_H, 11.75), aim=TARGET, fov=70, ease="inout"),
    dict(dur=4.2, eye=(LANE_X, 1.58, 11.75), aim=TARGET, fov=4.0, ease="inout"),
    dict(dur=1.8, eye=(LANE_X, 1.58, 11.75), aim=TARGET, fov=4.0, ease="linear"),
]


# ---------------------------------------------------------------- camera path

def yaw_pitch(eye, aim):
    d = np.asarray(aim, float) - np.asarray(eye, float)
    return math.atan2(d[0], -d[2]), math.asin(d[1] / max(np.linalg.norm(d), 1e-9))


def ease(u, kind):
    if kind == "in":
        return u * u
    if kind == "out":
        return 1.0 - (1.0 - u) ** 2
    if kind == "inout":
        return u * u * (3 - 2 * u)
    return u


def build_path(fps):
    """-> list of (eye, aim, fov), one per frame."""
    keys = []
    for sh in SHOTS:
        y, p = yaw_pitch(sh["eye"], sh["aim"])
        keys.append(dict(eye=np.array(sh["eye"], float), yaw=y, pitch=p,
                         fov=float(sh["fov"]), dur=sh["dur"],
                         ease=sh.get("ease", "inout"), bob=sh.get("bob", False)))

    # Unwrap yaw so each turn takes the short way round and never spins through +-pi.
    for i in range(1, len(keys)):
        while keys[i]["yaw"] - keys[i - 1]["yaw"] > math.pi:
            keys[i]["yaw"] -= 2 * math.pi
        while keys[i]["yaw"] - keys[i - 1]["yaw"] < -math.pi:
            keys[i]["yaw"] += 2 * math.pi

    path, t_total = [], 0.0
    for i in range(1, len(keys)):
        a, b = keys[i - 1], keys[i]
        n = max(1, int(round(b["dur"] * fps)))
        for f in range(n):
            u = ease((f + 1) / n, b["ease"])
            eye = a["eye"] + (b["eye"] - a["eye"]) * u
            yaw = a["yaw"] + (b["yaw"] - a["yaw"]) * u
            pitch = a["pitch"] + (b["pitch"] - a["pitch"]) * u
            # Zoom reads as even when it is geometric: halving the field of view should
            # look the same whether it runs 70->35 or 20->10.
            fov = math.exp(math.log(a["fov"]) + (math.log(b["fov"]) - math.log(a["fov"])) * u)
            e = eye.copy()
            if b["bob"]:
                e[1] += 0.012 * math.sin(2 * math.pi * 1.9 * (t_total + (f + 1) / fps))
            d = np.array([math.sin(yaw) * math.cos(pitch), math.sin(pitch),
                          -math.cos(yaw) * math.cos(pitch)])
            path.append((tuple(e), tuple(e + d * 10.0), fov))
        t_total += b["dur"]
    return path


# ---------------------------------------------------------------- rendering

_STATE = {}


def _init(width, height):
    import torch
    import render_scene as R
    R.W, R.H = width, height
    _STATE["R"] = R
    _STATE["scene"] = R.load(MODEL)
    _STATE["device"] = "cuda" if torch.cuda.is_available() else "cpu"


def _frame(job):
    i, eye, at, fov = job
    path = os.path.join(FRAMEDIR, "f{:05d}.png".format(i))
    if os.path.exists(path):
        return path
    R = _STATE["R"]
    prims, lamps = _STATE["scene"]
    img, _, _ = R.render_view(prims, lamps, eye, at, R.DEFAULT_EXPOSURE,
                              _STATE["device"], fov=fov)
    from PIL import Image
    Image.fromarray(img).save(path)
    return path


def main():
    args = sys.argv[1:]

    def opt(name, default, cast=int):
        return cast(args[args.index(name) + 1]) if name in args else default

    out = next((a for a in args if a.endswith(".mp4")), OUT)
    fps = opt("--fps", 24)
    width = opt("--width", 1280)
    height = width * 9 // 16
    jobs = opt("--jobs", 6)

    path = build_path(fps)
    lo, hi = 0, len(path)
    if "--frames" in args:
        lo, hi = (int(v) for v in args[args.index("--frames") + 1].split(":"))
    todo = [(i, e, a, f) for i, (e, a, f) in enumerate(path)][lo:hi]

    os.makedirs(FRAMEDIR, exist_ok=True)
    print("{} frames at {} fps = {:.1f} s, {}x{}, {} workers".format(
        len(path), fps, len(path) / fps, width, height, jobs))

    ctx = mp.get_context("spawn")           # fork + CUDA in the parent is a deadlock
    with ctx.Pool(jobs, initializer=_init, initargs=(width, height)) as pool:
        for n, _ in enumerate(pool.imap_unordered(_frame, todo, chunksize=1), 1):
            if n % 20 == 0 or n == len(todo):
                print("  {}/{} frames".format(n, len(todo)), flush=True)

    if "--frames" in args:      # a partial render has no continuous sequence to encode
        print("rendered frames {}:{}, skipping encode".format(lo, hi))
        return

    os.makedirs(os.path.dirname(out), exist_ok=True)
    cmd = ["ffmpeg", "-y", "-framerate", str(fps), "-i", os.path.join(FRAMEDIR, "f%05d.png"),
           "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p",
           "-movflags", "+faststart", out]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("wrote {} ({:.1f} MB)".format(out, os.path.getsize(out) / 1e6))


if __name__ == "__main__":
    main()
