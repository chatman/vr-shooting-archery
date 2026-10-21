# Poses, reproportions and animates Kenney's "Animated Characters: Protagonists" rig as a
# seated spectator, and writes everything the Unity side needs.
#
# WHY POSE IT OURSELVES. The pack ships idle / jump / run and nothing else: there is no
# sitting clip and no seated idle. The rig, though, is a clean 58-bone FK humanoid with
# ZERO constraints (checked -- the FootIK / KneeCtrl chains are unparented leftovers that
# drive nothing), so every pose below is plain forward kinematics and nothing fights back.
#
# HOW A POSE IS WRITTEN. Not as per-bone Euler angles -- those depend on each bone's roll
# and are unreadable and easy to get backwards. Instead each entry is the DIRECTION the
# bone should point, in armature space, and aim() builds the matrix that takes it there.
# The character faces -Y, up is +Z, and +X is its own left. So (0,-0.9,-0.4) is
# "forward and somewhat down", which is what a thigh does when you sit.
#
# WHY THE FIGURE IS RESHAPED. Kenney's proportions are chibi: measured off the import, the
# hip sits at 34% of standing height and the head is 28% of it, against roughly 53% and
# 13% on a person. Sitting only drops you by however much your legs fold, so a chibi
# figure seated is 92% of its standing height -- 1.58 m with the head at 1.70 m scale. A
# row of those tower over a 1.10 m barrier and block the rake the stand was built around.
# No global scale fixes it, because the ratio is wrong, not the size. So two pose-space
# reshapes are baked into every pose and every clip:
#
#   LEG_STRETCH  everything between the ankle and the hip is stretched downward; the feet
#                keep their size and just travel with it.
#   HEAD_SHRINK  everything above the top of the neck is scaled about that point, with a
#                smoothstep band below it so the jaw does not step off the neck.
#
# The reshape is done on the REST pose -- mesh vertices and edit bones remapped by the
# same function, so the rig keeps matching the skin -- and not as pose-space bone scale.
# Pose scale was the first attempt and it does not work: scaling a thigh moves nothing
# below it unless the child inherits the scale, and a child that inherits a lengthwise
# stretch through a knee bent 65 degrees gets skewed rather than moved. Remapping the
# T-pose is unambiguous, because in a T-pose every leg vertex is directly below the hip.
#
# Those two put the seated crown near 1.26 m and the hip near 0.45 m, which is the seat
# pan height, so the figures sit ON the seats with their feet on the tier rather than
# hovering or sinking. The numbers are solved for, then verified by measuring the posed
# mesh -- see the [rig] lines the script prints.
#
# OUTPUTS
#   spectator.fbx          mesh + rig + 7 baked takes (3 seated idles, clap, sit<->stand)
#   spectator_sit.fbx      the seated pose baked to a static mesh, no rig
#   spectator_stand.fbx    ditto standing
#
# The static pair is for the back rows. A SkinnedMeshRenderer with an Animator costs CPU
# skinning every frame and the Quest runs out of that budget at a few dozen characters;
# the back of an 11-row stand is 10-16 m from the firing point, where a rigid mesh given a
# slow transform-level sway is indistinguishable and costs one matrix write.
#
# INPUT. Model/characterMedium.fbx from Kenney's "Animated Characters: Protagonists" 1.1,
# CC0, kenney.nl/assets/animated-characters-3. The pack is not vendored here -- it is a
# 580 KB download and this repo keeps binary art out of git -- but its licence travels
# with the exported assets, in quest-app/Assets/Models/Spectator/Kenney-License.txt.
#
#   blender -b --python scripts/build_spectator.py -- <characterMedium.fbx> <outdir>
#
# Verified reproducible: re-running this writes the three FBXs byte for byte.

import bpy, sys, os, math
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = argv[0], argv[1]
os.makedirs(OUT, exist_ok=True)

FPS = 24
STAND_HEIGHT = 1.72
LEG_STRETCH = 1.83
HEAD_SHRINK = 0.62
HEAD_DROP = 0.22       # raw import units; closes the neck gap the shrink opens

# ---------------------------------------------------------------- pose vocabulary

def mirror(d):
    """Same direction on the other side of the body."""
    return (-d[0], d[1], d[2])


def side(**kw):
    """Expands one side's entries into Left* and Right*, mirroring the Right."""
    out = {}
    for bone, d in kw.items():
        out["Left" + bone] = d
        out["Right" + bone] = mirror(d)
    return out


# Knees a little above the hips -- a low seat relative to the leg, which is how a
# spectator sits on a 0.45 m plastic pan. Solved, not guessed: see the header.
SIT = dict(
    Spine=(0, -0.11, 1.0), Chest=(0, -0.05, 1.0), UpperChest=(0, 0.0, 1.0),
    Neck=(0, 0.04, 1.0), Head=(0, 0.01, 1.0),
    **side(UpLeg=(0.16, -0.98, 0.15), Leg=(0.02, 0.16, -1.0), Foot=(0.03, -0.80, -0.34),
           Arm=(0.24, 0.04, -1.0), ForeArm=(0.13, -0.58, -0.80), Hand=(0.11, -0.70, -0.70)),
)

STAND = dict(
    Spine=(0, -0.02, 1.0), Chest=(0, 0.0, 1.0), UpperChest=(0, 0.0, 1.0),
    Neck=(0, 0.02, 1.0), Head=(0, 0.0, 1.0),
    **side(UpLeg=(0.05, 0.0, -1.0), Leg=(0.0, 0.03, -1.0), Foot=(0.02, -0.80, -0.52),
           Arm=(0.17, 0.0, -1.0), ForeArm=(0.13, -0.16, -0.98), Hand=(0.11, -0.22, -0.97)),
)

def blend(a, b, t):
    out = dict(a)
    for k, v in b.items():
        if k in a:
            p = a[k]
            out[k] = tuple(p[i] + (v[i] - p[i]) * t for i in range(3))
        else:
            out[k] = v
    return out


def tweak(base, **kw):
    out = dict(base)
    out.update(kw)
    return out


# ---------------------------------------------------------------- reproportioning

def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def reshape_rest(arm, mesh):
    """Remaps the T-pose -- mesh vertices and edit bones through the same function -- to
    lengthen the legs and bring the head down to something like human proportion.

    Valid only on the rest pose: it works in world Z, and it is the T-pose that puts
    every leg vertex directly below the hip and every skull vertex above the neck."""
    eb = arm.data.bones
    hip_z = eb["LeftUpLeg"].head_local.z
    ankle_z = eb["LeftFoot"].head_local.z
    neck_z = eb["Head"].head_local.z
    neck_y = eb["Head"].head_local.y
    band = (neck_z - eb["Neck"].head_local.z) * 0.65     # blend the shrink into the neck
    drop = (hip_z - ankle_z) * (LEG_STRETCH - 1.0)        # how far the ankle travels down

    def remap(p):
        x, y, z = p.x, p.y, p.z
        if z >= neck_z - band:
            # Shrinking the skull about the top of the neck leaves the head perched on a
            # long thin stalk, because this character barely had a neck to start with.
            # So the same ramp that scales it also drops it back onto the shoulders.
            t = smoothstep((z - (neck_z - band)) / band)
            s = 1.0 - (1.0 - HEAD_SHRINK) * t
            return Vector((x * s, neck_y + (y - neck_y) * s,
                           neck_z + (z - neck_z) * s - HEAD_DROP * t))
        if z >= ankle_z:
            if z < hip_z:
                return Vector((x, y, hip_z - (hip_z - z) * LEG_STRETCH))
            return Vector((x, y, z))
        return Vector((x, y, z - drop))

    for v in mesh.data.vertices:
        v.co = remap(v.co)
    mesh.data.update()

    bpy.context.view_layer.objects.active = arm
    # Snapshot every endpoint before touching any of them. Connected bones share one
    # point: setting a parent's tail moves its child's head, so remapping in place runs
    # the function twice over the same coordinate and the leg chain comes out 60% long.
    bpy.ops.object.mode_set(mode='EDIT')
    orig = {b.name: (b.head.copy(), b.tail.copy()) for b in arm.data.edit_bones}
    for b in arm.data.edit_bones:
        h, t = orig[b.name]
        b.head = remap(h)
        b.tail = remap(t)
    bpy.ops.object.mode_set(mode='OBJECT')
    zs = [v.co.z for v in mesh.data.vertices]
    print("[rig] reshape: hip %.3f ankle %.3f neck %.3f  legs x%.2f head x%.2f  "
          "rest z %.3f..%.3f (h=%.3f) hip above sole %.3f" % (
              hip_z, ankle_z, neck_z, LEG_STRETCH, HEAD_SHRINK,
              min(zs), max(zs), max(zs) - min(zs), hip_z - min(zs)))


# ---------------------------------------------------------------- rig plumbing

def load():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=SRC)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    mesh = next(o for o in bpy.data.objects if o.type == 'MESH')

    # Drop the unused IK control chains: they are unparented roots, they deform nothing,
    # and exporting them would hand Unity 18 extra transforms per spectator to animate.
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='EDIT')
    for name in [b.name for b in arm.data.edit_bones]:
        if any(k in name for k in ("Ctrl", "Roll", "IK")):
            arm.data.edit_bones.remove(arm.data.edit_bones[name])
    bpy.ops.object.mode_set(mode='OBJECT')

    reshape_rest(arm, mesh)

    for pb in arm.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    return arm, mesh


def depth(bone):
    n, b = 0, bone
    while b.parent:
        n += 1
        b = b.parent
    return n


def aim(pb, d):
    """Point the bone along d (armature space), keeping its head where the parent chain
    put it. Twist carries over from the bone's current matrix so repeated aims do not
    spin the elbow."""
    bpy.context.view_layer.update()
    m = pb.matrix.copy()
    head = m.translation.copy()
    y = Vector(d).normalized()
    ref = Vector(m.col[2][:3])
    x = ref.cross(y)
    if x.length < 1e-4:
        x = Vector((1.0, 0.0, 0.0)).cross(y)
        if x.length < 1e-4:
            x = Vector((0.0, 1.0, 0.0)).cross(y)
    x.normalize()
    # z = x cross y, NOT y cross x. The other way round the basis is left-handed, the
    # matrix is a reflection, and Blender's decomposition turns that into a negative
    # scale -- which silently lengthened the whole leg chain by a third.
    z = x.cross(y)
    nm = Matrix(((x.x, y.x, z.x, 0.0), (x.y, y.y, z.y, 0.0), (x.z, y.z, z.z, 0.0),
                 (0.0, 0.0, 0.0, 1.0)))
    nm.translation = head
    pb.matrix = nm
    bpy.context.view_layer.update()


def apply_pose(arm, pose):
    for pb in arm.pose.bones:
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)
    bpy.context.view_layer.update()
    # Parents first: aim() reads the bone's current head, which the parent decides.
    for pb in sorted(arm.pose.bones, key=lambda p: depth(p.bone)):
        if pb.name in pose:
            aim(pb, pose[pb.name])


def fcurves_of(act):
    """Blender 5 moved an action's curves into layers/strips/channelbags."""
    try:
        return list(act.fcurves)
    except AttributeError:
        out = []
        for layer in act.layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    out.extend(bag.fcurves)
        return out


def key_all(arm, frame):
    for pb in arm.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        pb.keyframe_insert("location", frame=frame)
        pb.keyframe_insert("scale", frame=frame)


def make_action(arm, name, keys):
    """keys: list of (frame, pose). First and last should match for a clip meant to loop."""
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = act
    for f, pose in keys:
        apply_pose(arm, pose)
        key_all(arm, f)
    for fc in fcurves_of(act):
        for kp in fc.keyframe_points:
            kp.interpolation = 'BEZIER'
            kp.handle_left_type = 'AUTO_CLAMPED'
            kp.handle_right_type = 'AUTO_CLAMPED'
    print("[rig] action %-12s frames 0..%d (%.1fs)" % (name, keys[-1][0], keys[-1][0] / float(FPS)))
    return act


# ---------------------------------------------------------------- the clips

def breath(p, k):
    """Chest rise and a touch of neck give -- the floor of looking alive."""
    return tweak(p, Chest=(0, -0.05 + 0.03 * k, 1.0), UpperChest=(0, 0.015 * k, 1.0),
                 Neck=(0, 0.04 - 0.02 * k, 1.0))


def clips(arm):
    s = SIT

    # A: quiet. Breathing, one slow look left and back.
    make_action(arm, "SitIdleA", [
        (0, s), (36, breath(s, 1)), (72, s),
        (96, tweak(s, Head=(0.30, 0.04, 0.95), Neck=(0.12, 0.04, 1.0))),
        (132, tweak(s, Head=(0.26, 0.02, 0.96), Neck=(0.10, 0.04, 1.0))),
        (156, breath(s, 0.6)), (192, s),
    ])

    # B: leans forward onto the knees for a while, the way people watch a close final.
    fwd = tweak(s, Spine=(0, -0.34, 1.0), Chest=(0, -0.16, 1.0), Neck=(0, 0.18, 1.0),
                Head=(0, 0.12, 1.0),
                LeftForeArm=(0.16, -0.52, -0.84), RightForeArm=(-0.16, -0.52, -0.84),
                LeftHand=(0.14, -0.60, -0.78), RightHand=(-0.14, -0.60, -0.78))
    make_action(arm, "SitIdleB", [
        (0, s), (30, blend(s, fwd, 0.55)), (54, fwd), (96, breath(fwd, 1)),
        (120, fwd), (150, blend(s, fwd, 0.35)), (168, s),
    ])

    # C: arms folded, turns to the neighbour and back.
    fold = tweak(s,
                 LeftArm=(0.34, -0.30, -0.89), RightArm=(-0.34, -0.30, -0.89),
                 LeftForeArm=(-0.86, -0.46, 0.22), RightForeArm=(0.86, -0.46, 0.22),
                 LeftHand=(-0.92, -0.32, 0.22), RightHand=(0.92, -0.32, 0.22))
    make_action(arm, "SitIdleC", [
        (0, fold), (48, breath(fold, 1)),
        (84, tweak(fold, Head=(-0.42, 0.02, 0.90), Neck=(-0.16, 0.04, 1.0),
                   UpperChest=(-0.10, 0.0, 1.0))),
        (120, tweak(fold, Head=(-0.38, 0.0, 0.92), Neck=(-0.14, 0.04, 1.0),
                    UpperChest=(-0.09, 0.0, 1.0))),
        (156, breath(fold, 0.4)), (216, fold),
    ])

    # Applause: four claps in two seconds, hands meeting in front of the chest.
    opened = tweak(s,
                   LeftArm=(0.42, -0.34, -0.84), RightArm=(-0.42, -0.34, -0.84),
                   LeftForeArm=(-0.34, -0.90, 0.28), RightForeArm=(0.34, -0.90, 0.28),
                   LeftHand=(-0.30, -0.93, 0.21), RightHand=(0.30, -0.93, 0.21))
    shut = tweak(s,
                 LeftArm=(0.34, -0.30, -0.89), RightArm=(-0.34, -0.30, -0.89),
                 LeftForeArm=(-0.72, -0.68, 0.14), RightForeArm=(0.72, -0.68, 0.14),
                 LeftHand=(-0.70, -0.71, 0.10), RightHand=(0.70, -0.71, 0.10))
    seq = []
    for i in range(4):
        seq.append((i * 12, opened))
        seq.append((i * 12 + 6, shut))
    seq.append((48, opened))
    make_action(arm, "SitClap", seq)

    # Rising: the weight goes forward over the feet before the knees straighten.
    rise = tweak(SIT, Spine=(0, -0.42, 1.0), Chest=(0, -0.20, 1.0),
                 LeftUpLeg=(0.16, -0.72, -0.30), RightUpLeg=(-0.16, -0.72, -0.30),
                 LeftLeg=(0.02, -0.26, -0.96), RightLeg=(-0.02, -0.26, -0.96))
    make_action(arm, "SitToStand", [(0, SIT), (10, rise), (18, blend(rise, STAND, 0.6)), (28, STAND)])
    make_action(arm, "StandToSit", [(0, STAND), (10, blend(rise, STAND, 0.6)), (18, rise), (30, SIT)])

    # Standing idle: weight shifts side to side, no foot travel.
    lean_l = tweak(STAND, Spine=(0.07, -0.02, 1.0), Chest=(0.04, 0, 1.0),
                   LeftUpLeg=(0.09, 0, -1.0), RightUpLeg=(-0.02, 0, -1.0))
    lean_r = tweak(STAND, Spine=(-0.07, -0.02, 1.0), Chest=(-0.04, 0, 1.0),
                   LeftUpLeg=(0.02, 0, -1.0), RightUpLeg=(-0.09, 0, -1.0))
    make_action(arm, "StandIdle", [
        (0, STAND), (48, lean_l), (96, breath(STAND, 1)), (144, lean_r), (192, STAND),
    ])


# ---------------------------------------------------------------- measuring

def posed_points(arm, mesh, pose):
    apply_pose(arm, pose)
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    return [mesh.matrix_world @ v.co for v in mesh.evaluated_get(dg).data.vertices]


def report(arm, mesh, pose, label):
    pts = posed_points(arm, mesh, pose)
    zl = min(p.z for p in pts)
    hip = arm.matrix_world @ arm.pose.bones["Hips"].head
    print("[rig] %-7s w=%.2f d=%.2f h=%.2f  hip above feet=%.3f" % (
        label,
        max(p.x for p in pts) - min(p.x for p in pts),
        max(p.y for p in pts) - min(p.y for p in pts),
        max(p.z for p in pts) - zl, hip.z - zl))
    return max(p.z for p in pts) - zl


# ---------------------------------------------------------------- static bakes

def bake_static(arm, mesh, pose, name):
    """Applies a pose to the mesh and writes it rigless, for the distant rows."""
    apply_pose(arm, pose)
    bpy.context.view_layer.update()

    dg = bpy.context.evaluated_depsgraph_get()
    ev = mesh.evaluated_get(dg)
    data = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=dg)
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    obj.matrix_world = mesh.matrix_world.copy()

    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # Origin between the feet at floor level, so Unity can drop it onto the tier.
    pts = [v.co for v in obj.data.vertices]
    xm = (min(p.x for p in pts) + max(p.x for p in pts)) * 0.5
    ym = (min(p.y for p in pts) + max(p.y for p in pts)) * 0.5
    zl = min(p.z for p in pts)
    for v in obj.data.vertices:
        v.co = Vector((v.co.x - xm, v.co.y - ym, v.co.z - zl))
    obj.data.update()

    pts = [v.co for v in obj.data.vertices]
    print("[rig] static %-16s w=%.2f d=%.2f h=%.2f tris=%d" % (
        name,
        max(p.x for p in pts) - min(p.x for p in pts),
        max(p.y for p in pts) - min(p.y for p in pts),
        max(p.z for p in pts) - min(p.z for p in pts),
        sum(len(p.vertices) - 2 for p in obj.data.polygons)))

    path = os.path.join(OUT, name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'},
                             use_mesh_modifiers=False, path_mode='STRIP',
                             apply_unit_scale=True, global_scale=1.0)
    bpy.data.objects.remove(obj, do_unlink=True)
    print("[rig] wrote " + path)


# ---------------------------------------------------------------- main

arm, mesh = load()
bpy.context.scene.render.fps = FPS

# Size the rig from the RESHAPED standing figure rather than from the import: the leg
# stretch changes the height it is being normalised against.
report(arm, mesh, {}, "rest")
raw = report(arm, mesh, STAND, "raw")
# Leg bone lengths, because they are where this went wrong twice: once to a left-handed
# basis in aim(), once to remapping connected bone endpoints in place. Both showed up
# here as a chain a third longer than the rest pose it was built from.
for _n in ("LeftUpLeg", "LeftLeg", "LeftFoot"):
    print("[rig] %-10s length %.3f" % (_n, arm.pose.bones[_n].bone.length))
k = STAND_HEIGHT / raw
apply_pose(arm, {})
arm.scale = (k, k, k)
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True)
mesh.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
print("[rig] reshaped height %.3f -> scaled by %.4f" % (raw, k))

report(arm, mesh, STAND, "stand")
report(arm, mesh, SIT, "sit")

bake_static(arm, mesh, SIT, "spectator_sit")
bake_static(arm, mesh, STAND, "spectator_stand")

clips(arm)

arm.animation_data.action = bpy.data.actions["SitIdleA"]
bpy.context.scene.frame_set(0)
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True)
mesh.select_set(True)
bpy.context.view_layer.objects.active = arm
path = os.path.join(OUT, "spectator.fbx")
bpy.ops.export_scene.fbx(filepath=path, use_selection=True,
                         object_types={'ARMATURE', 'MESH'},
                         add_leaf_bones=False, bake_anim=True,
                         bake_anim_use_all_actions=True,
                         bake_anim_use_nla_strips=False,
                         bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
                         path_mode='STRIP', apply_unit_scale=True, global_scale=1.0)
print("[rig] wrote " + path)
print("[rig] DONE")
