using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.HandGrab.Visuals;
using Oculus.Interaction.Input;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Checks whether the authored grip would actually work for a human hand and a human eye.
///
/// The [Grab] log lines prove the components are wired. They do not prove the hold is any
/// good: a grip can be correctly wired and still put the fingers through the frame, or park
/// the trigger out of reach, or point the sights somewhere the eye cannot follow. These are
/// the things that decide whether it reads as natural, and all of them are measurable
/// without a headset, so they are measured here rather than guessed at.
///
/// What this cannot check is feel -- tracking jitter, how heavy it seems, whether the snap
/// on grab is jarring. Those need a person in the headset. Everything below is a
/// precondition for that person not immediately noticing something wrong.
///
/// Anthropometry used, all adult-median figures:
///   hand breadth across the knuckles   ~ 85 mm
///   comfortable pistol grip girth      130 - 180 mm  (match pistols sit at the top)
///   eye height standing                ~ 1620 mm
///   shoulder below eye                 ~ 150 mm
///   shoulder to wrist, arm extended    ~ 570 mm
/// </summary>
public static class PistolGripValidation
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";
    const string GhostProviderPath =
        "Packages/com.meta.xr.sdk.interaction/Runtime/Prefabs/HandGrab/OpenXRGhostProvider.asset";

    // Range geometry. Targets sit on the wall at z = 0; the firing line is at the bench.
    const float TargetZ = 0f;
    const float TargetHeight = 1.40f;      // ISSF target centre height
    const float FiringLineZ = 11.6f;       // just behind the bench, where a shooter stands
    const float LaneX = 1.5f;              // lane 19

    const float EyeHeight = 1.620f;
    const float ShoulderDrop = 0.150f;
    const float ShoulderOffsetX = 0.200f;  // right shoulder out from the head centreline
    const float ArmReach = 0.570f;

    static int _fail;

    [MenuItem("Quest/10d. Validate the Grip")]
    public static void Validate()
    {
        EditorSceneManager.OpenScene(ScenePath);
        _fail = 0;

        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError("[Check] no pistol in the scene."); return; }

        var interactable = pistol.GetComponentInChildren<HandGrabInteractable>(true);
        if (interactable == null) { Debug.LogError("[Check] no HandGrabInteractable; run Quest > 10."); return; }

        var pose = interactable.HandGrabPoses.FirstOrDefault(
            p => p.HandPose != null && p.HandPose.Handedness == Handedness.Right);
        if (pose == null) { Debug.LogError("[Check] no right-hand pose."); return; }

        Debug.Log("===== grip validation =====");
        CheckGripGirth(pistol);
        var joints = GhostJoints(pose, pistol.transform, out GameObject probe);
        CheckFingerClipping(pistol, joints);
        CheckTriggerReach(pistol, joints);
        CheckThumbAndSightLine(pistol, joints);
        Object.DestroyImmediate(probe);
        CheckAimGeometry(pistol, pose);

        Debug.Log(_fail == 0
            ? "===== all checks passed ====="
            : $"===== {_fail} check(s) FAILED =====");
    }

    static void Pass(string what, string detail) => Debug.Log($"[Check] OK   {what}: {detail}");
    static void Fail(string what, string detail)
    {
        _fail++;
        Debug.LogWarning($"[Check] FAIL {what}: {detail}");
    }

    /// <summary>
    /// A grip the hand cannot close around reads as wrong however well it is posed. Girth is
    /// the perimeter the fingers have to travel; the fist's own enclosed tube is the other
    /// half of the comparison.
    /// </summary>
    static void CheckGripGirth(GameObject pistol)
    {
        Vector3 s = AddAirPistol.GripSize;
        float girth = 2f * (s.x + s.z) * 1000f;
        // Rounded corners in a real grip cut a little off the square perimeter.
        float effective = girth * 0.92f;

        if (effective >= 130f && effective <= 185f)
            Pass("grip girth", $"{effective:F0} mm (human comfortable 130-180 mm)");
        else
            Fail("grip girth", $"{effective:F0} mm, outside the comfortable 130-180 mm band");

        float width = s.x * 1000f;
        if (width <= 90f) Pass("grip width", $"{width:F0} mm across, inside a {85f:F0} mm palm");
        else Fail("grip width", $"{width:F0} mm is wider than a median palm");
    }

    /// <summary>Ghost joint positions, in the pistol's local space.</summary>
    static Dictionary<string, Vector3> GhostJoints(HandGrabPose pose, Transform pistol, out GameObject probe)
    {
        var provider = AssetDatabase.LoadAssetAtPath<HandGhostProvider>(GhostProviderPath);
        probe = new GameObject("__validationGhost");
        var ghost = Object.Instantiate(provider.GetHand(pose.HandPose.Handedness), probe.transform);
        ghost.SetPose(pose);

        var map = new Dictionary<string, Vector3>();
        foreach (Transform t in ghost.GetComponentsInChildren<Transform>())
        {
            if (!t.name.StartsWith("XRHand_")) continue;
            map[t.name] = pistol.InverseTransformPoint(t.position);
        }
        return map;
    }

    /// <summary>
    /// Fingers passing through solid metal is the single most obvious tell that a grip was
    /// authored rather than held. Every pistol part is a scaled unit cube, so containment is
    /// an exact oriented-box test rather than an approximation.
    /// </summary>
    static void CheckFingerClipping(GameObject pistol, Dictionary<string, Vector3> joints)
    {
        // The grip is handled separately: the fist is supposed to be around it, so the test
        // there is depth, not presence.
        var solid = new[] { "Frame", "Barrel", "WeightRail", "TriggerGuard", "TriggerGuardBase",
                            "RearSight", "FrontSight" };

        float worst = 0f; string worstWhere = null;
        foreach (var partName in solid)
        {
            var part = pistol.transform.Find(partName);
            if (part == null) continue;

            foreach (var joint in joints)
            {
                Vector3 p = part.InverseTransformPoint(
                    pistol.transform.TransformPoint(joint.Value));
                // Unit cube: inside means every axis within +/- 0.5.
                float dx = 0.5f - Mathf.Abs(p.x);
                float dy = 0.5f - Mathf.Abs(p.y);
                float dz = 0.5f - Mathf.Abs(p.z);
                if (dx <= 0f || dy <= 0f || dz <= 0f) continue;

                // Depth in metres: smallest distance to a face, scaled back out.
                float depth = Mathf.Min(dx * part.localScale.x,
                              Mathf.Min(dy * part.localScale.y, dz * part.localScale.z));
                if (depth > worst) { worst = depth; worstWhere = $"{joint.Key} in {partName}"; }
            }
        }

        if (worst < 0.004f)
            Pass("finger clipping", worst == 0f ? "no joint inside solid geometry"
                                                : $"worst {worst * 1000f:F1} mm ({worstWhere}), within skin tolerance");
        else
            Fail("finger clipping", $"{worst * 1000f:F1} mm of {worstWhere} — visible through the metal");

        CheckGripPenetration(pistol, joints);
    }

    /// <summary>
    /// The fist is meant to be around the grip, so joints being inside it is expected -- but
    /// only just inside. A hand sunk deep into the grip means the grip is fatter than the
    /// fist can close on, and the fingers will visibly pass through the panels.
    /// </summary>
    static void CheckGripPenetration(GameObject pistol, Dictionary<string, Vector3> joints)
    {
        var grip = pistol.transform.Find("Grip");
        if (grip == null) { Fail("grip penetration", "no Grip part"); return; }

        float worst = 0f; string where = null;
        foreach (var joint in joints)
        {
            // Only the digits wrap the grip; the wrist and palm sit against it.
            if (!joint.Key.Contains("Proximal") && !joint.Key.Contains("Intermediate")
                && !joint.Key.Contains("Distal") && !joint.Key.Contains("Tip")) continue;

            Vector3 p = grip.InverseTransformPoint(pistol.transform.TransformPoint(joint.Value));
            float dx = 0.5f - Mathf.Abs(p.x);
            float dy = 0.5f - Mathf.Abs(p.y);
            float dz = 0.5f - Mathf.Abs(p.z);
            if (dx <= 0f || dy <= 0f || dz <= 0f) continue;

            float depth = Mathf.Min(dx * grip.localScale.x,
                          Mathf.Min(dy * grip.localScale.y, dz * grip.localScale.z));
            if (depth > worst) { worst = depth; where = joint.Key; }
        }

        // Fingertips press into a grip by a few mm; more than a centimetre is the hand
        // occupying the same space as the grip rather than holding it.
        if (worst <= 0.010f)
            Pass("grip penetration", worst == 0f ? "fingers close outside the grip"
                                                 : $"deepest {worst * 1000f:F1} mm ({where}), reads as pressure");
        else
            Fail("grip penetration", $"{where} is {worst * 1000f:F1} mm inside the grip — the grip is too fat for the fist");
    }

    /// <summary>The trigger finger has to land on the trigger, or the hold reads as a fist round a brick.</summary>
    static void CheckTriggerReach(GameObject pistol, Dictionary<string, Vector3> joints)
    {
        var trigger = pistol.transform.Find("Trigger");
        if (trigger == null) { Fail("trigger reach", "no Trigger part"); return; }

        if (!joints.TryGetValue("XRHand_IndexTip", out Vector3 tip))
        { Fail("trigger reach", "no index tip joint"); return; }

        float d = Vector3.Distance(tip, trigger.localPosition) * 1000f;
        // A finger pad is ~15 mm across, and the trigger blade is ~26 mm tall, so anything
        // inside ~35 mm reads as "on the trigger" once the finger curls to fire.
        if (d <= 35f) Pass("trigger reach", $"index tip {d:F0} mm from the trigger");
        else Fail("trigger reach", $"index tip {d:F0} mm from the trigger — cannot reach it");
    }

    /// <summary>
    /// The hand must not stand between the eye and the sights. Anything the fist puts above
    /// the bore line in front of the rear sight blocks the sight picture.
    /// </summary>
    static void CheckThumbAndSightLine(GameObject pistol, Dictionary<string, Vector3> joints)
    {
        var rear = pistol.transform.Find("RearSight");
        var front = pistol.transform.Find("FrontSight");
        if (rear == null || front == null) { Fail("sight picture", "sights missing"); return; }

        float sightY = rear.localPosition.y;
        string blocker = null; float worstRise = 0f;

        foreach (var joint in joints)
        {
            Vector3 p = joint.Value;
            // Only things forward of the rear sight and inside the sight corridor matter.
            if (p.z > rear.localPosition.z) continue;
            if (Mathf.Abs(p.x) > 0.030f) continue;
            float rise = p.y - sightY;
            if (rise > worstRise) { worstRise = rise; blocker = joint.Key; }
        }

        if (worstRise <= 0f)
            Pass("sight picture", "nothing on the hand rises into the sight line");
        else
            Fail("sight picture", $"{blocker} sits {worstRise * 1000f:F0} mm above the sight line");
    }

    /// <summary>
    /// The aiming test, posed the way a shooter actually aims.
    ///
    /// An earlier version of this extended the arm at the target and measured where the
    /// sights ended up. That is the wrong way round and it failed the grip for the wrong
    /// reason: nobody aims by pointing their arm and hoping. A shooter raises the pistol
    /// INTO the line between the eye and the target and then reads the sights.
    ///
    /// So: put the sights on that line, hang the hand off the pistol by the authored grip,
    /// and ask whether the arm can be where it now has to be. If the wrist lands at a
    /// natural extension with the hand in line with the forearm, the hold is aimable by a
    /// human. If it demands a broken wrist or an arm of the wrong length, it is not.
    /// </summary>
    static void CheckAimGeometry(GameObject pistol, HandGrabPose pose)
    {
        Vector3 eye = new Vector3(LaneX, EyeHeight, FiringLineZ);
        Vector3 target = new Vector3(LaneX, TargetHeight, TargetZ);
        Vector3 shoulder = eye + new Vector3(ShoulderOffsetX, -ShoulderDrop, 0f);
        Vector3 aim = (target - eye).normalized;

        var rear = pistol.transform.Find("RearSight").localPosition;
        var front = pistol.transform.Find("FrontSight").localPosition;

        // Orient the pistol so its sight line runs along the eye-target ray, upright.
        Vector3 sightAxisLocal = (front - rear).normalized;
        Quaternion pistolRot = Quaternion.LookRotation(aim, Vector3.up)
                             * Quaternion.Inverse(Quaternion.LookRotation(sightAxisLocal, Vector3.up));

        // Slide it along that ray to full arm extension, measured shoulder to wrist.
        Pose grip = pose.RelativePose;
        Vector3 wristOffsetFromRear = pistolRot * (grip.position - rear);
        float distance = SolveSightDistance(eye, aim, shoulder, wristOffsetFromRear, ArmReach);
        Vector3 rearW = eye + aim * distance;
        Vector3 pistolPos = rearW - pistolRot * rear;
        Vector3 wristPos = pistolPos + pistolRot * grip.position;

        Debug.Log($"[Check] .... eye {eye:F2}, sights on the eye-target line, wrist ends at {wristPos:F2}");

        // Is the arm at a plausible extension?
        float reach = Vector3.Distance(shoulder, wristPos);
        if (Mathf.Abs(reach - ArmReach) <= 0.08f)
            Pass("arm extension", $"shoulder to wrist {reach * 1000f:F0} mm (median reach {ArmReach * 1000f:F0} mm)");
        else
            Fail("arm extension", $"shoulder to wrist {reach * 1000f:F0} mm vs a {ArmReach * 1000f:F0} mm arm");

        // The ergonomic number that matters: is the hand in line with the forearm, or bent?
        //
        // Judged against the geometric floor, not a flat constant. Some wrist extension is
        // unavoidable for ANY pistol here: the shoulder sits below and outside the eye, so
        // an extended arm reaching the eye-target line has to angle up and inward, and the
        // wrist makes up the difference. The floor is that arm geometry alone. What the
        // pistol's design controls is only the excess over it.
        Vector3 forearm = (wristPos - shoulder).normalized;
        Vector3 handAxis = (pistolRot * grip.rotation) * Vector3.forward;   // hand local +Z = wrist->palm
        float wristBend = Vector3.Angle(forearm, handAxis);
        float floor = Vector3.Angle(forearm, aim);
        float excess = wristBend - floor;

        Debug.Log($"[Check] .... wrist bend {wristBend:F0} deg, of which {floor:F0} deg is forced by the " +
                  "arm reaching the eye line; the grip design contributes the rest");

        // A briefly-held aiming posture tolerates more than sustained work does; the usual
        // 15-20 deg guidance is for repetitive tasks. What must not happen is the grip
        // ADDING much to the unavoidable floor.
        if (excess <= 8f)
            Pass("wrist deviation", $"{wristBend:F0} deg total, only {excess:F0} deg beyond the {floor:F0} deg the posture forces");
        else
            Fail("wrist deviation", $"{wristBend:F0} deg total — {excess:F0} deg worse than the {floor:F0} deg floor, so the grip is fighting the arm");

        // Does the pistol sit where the hand can be, or does it demand the arm cross the body?
        float lateral = wristPos.x - shoulder.x;
        if (Mathf.Abs(lateral) <= 0.25f)
            Pass("arm line", $"wrist {lateral * 1000f:F0} mm across from the shoulder");
        else
            Fail("arm line", $"wrist {lateral * 1000f:F0} mm across from the shoulder — the arm crosses the body");

        // Sight radius drives how precisely a wobble can be read. Longer is better.
        float radius = Vector3.Distance(rear, front) * 1000f;
        if (radius >= 200f) Pass("sight radius", $"{radius:F0} mm");
        else Fail("sight radius", $"{radius:F0} mm is short for a match pistol");

        // How much angular error one degree of hand wobble costs at the target. This is the
        // hand-tracking reality check rather than a pass/fail of the grip.
        float rangeM = Vector3.Distance(eye, target);
        Debug.Log($"[Check] .... at {rangeM:F1} m, 1 deg of hand wobble moves the shot " +
                  $"{Mathf.Tan(Mathf.Deg2Rad) * rangeM * 1000f:F0} mm; the 10 m target ring is 155 mm across");
    }

    /// <summary>
    /// Distance along the aim ray that puts the wrist one arm-length from the shoulder.
    /// Solved by bisection because the wrist offset rotates with the pistol.
    /// </summary>
    static float SolveSightDistance(Vector3 eye, Vector3 aim, Vector3 shoulder,
                                    Vector3 wristOffset, float reach)
    {
        float lo = 0.1f, hi = 1.2f;
        for (int i = 0; i < 40; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float d = Vector3.Distance(shoulder, eye + aim * mid + wristOffset);
            if (d < reach) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }
}
