using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.GrabAPI;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.HandGrab.Visuals;
using Oculus.Interaction.Input;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Makes the air pistol pickable AND aimable, using a posed Hand Grab.
///
/// Why posed, and why the previous two attempts could not work:
///
///   Touch Hand Grab (and a HandGrabInteractable with no HandGrabPose) is a POSE-LESS
///   grab. Meta's own wording: "If no HandGrabPose is provided, the grab will be a
///   pose-less grab anchored at the interactable pose." The object keeps whatever offset
///   it happened to have when the fingers closed. For a mug that is fine. For a pistol it
///   is fatal: there is no repeatable relationship between where the hand points and where
///   the barrel points, so the thing cannot be aimed, only carried.
///
///   A posed grab snaps the pistol to a designed grip every time. That is what makes the
///   sights land in the same place relative to the hand on every pickup, which is the
///   whole requirement.
///
/// Poses normally come from the Hand Grab Pose Recorder, which needs Link, Play-Mode and a
/// live tracked hand -- it cannot be scripted. But a pose is only data: a wrist Pose plus
/// finger joint rotations. HandGrabUtils exposes the whole thing (CreateHandGrabInteractable,
/// LoadData, HandGrabPoseData), so the pose can be authored here instead, as long as the
/// finger rotations come from somewhere credible.
///
/// They come from the SDK's own Torch prop. A torch is held exactly the way a pistol grip
/// is -- a fist wrapped round a column, thumb up the side -- so its recorded finger curl
/// transfers directly. Only the wrist Pose is ours; the fingers are Meta's, recorded off a
/// real hand.
///
/// The wrist Pose is derived from the pistol's own geometry rather than typed in, so it
/// keeps working if AddAirPistol's grip moves: see WristPose below.
///
/// Two halves have to meet for a grab to happen. The interactor half comes from the
/// Interactions Rig already in the scene (4 HandGrabInteractors: two tracked hands, two
/// controller-driven). This script does the interactable half and refuses to run if the
/// interactors are missing.
///
/// Run after AddAirPistol, and again whenever AddAirPistol re-runs.
///
/// "Quest > 10b. Preview Grip (ghost hands)" drops non-functional ghost hands into the
/// scene at the authored poses so the grip can be eyeballed in the editor without a
/// headset. "10c" removes them again. They are editor-only scenery -- delete before build.
/// </summary>
public static class PistolHandGrab
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";
    const string InteractionChild = "HandGrab";
    const string GhostName = "__GripPreviewGhost";

    const string TorchPath =
        "Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/Props/Torch/Torch.prefab";
    // OpenXR, not the OVR one next to it. HandGrabPose.HandPose returns the 19-joint
    // OpenXR pose in this SDK build; the OVR ghost is a 17-joint skeleton, and feeding it
    // OpenXR rotations splays the fingers instead of curling them.
    const string GhostProviderPath =
        "Packages/com.meta.xr.sdk.interaction/Runtime/Prefabs/HandGrab/OpenXRGhostProvider.asset";

    /// <summary>
    /// The three numbers that describe a closed fist, in the hand's own space with the
    /// wrist at the origin. Measured off the ghost rather than typed in, so the numbers
    /// stay honest if the SDK's skeleton changes under us.
    /// </summary>
    public struct Grasp
    {
        /// <summary>Centre of the tube the curled fingers enclose.</summary>
        public Vector3 centre;
        /// <summary>Axis of that tube: little-finger knuckle to index knuckle.</summary>
        public Vector3 column;
        /// <summary>Which way the palm faces.</summary>
        public Vector3 palmNormal;
        /// <summary>Wrist to palm: the direction the forearm runs into the hand.</summary>
        public Vector3 forearm;
        /// <summary>Half the gap between the middle finger and the thumb.</summary>
        public float radius;
    }

    /// <summary>
    /// Instantiates a throwaway ghost, applies the reference grip to it, and reads the
    /// fist's geometry back out.
    ///
    /// The whole approach turns on this. An earlier attempt anchored the wrist a fixed
    /// palm-length below the knuckles and let the fingers fall where they may; the result
    /// was a fist sitting beside the grip rather than around it, because a hand does not
    /// hold a column by its knuckles -- it holds it in the tube its curled fingers make.
    /// Measuring that tube and putting the grip down the middle of it is what actually
    /// produces a hold.
    /// </summary>
    public static Grasp MeasureGrasp(Handedness handedness, HandPose pose)
    {
        var provider = AssetDatabase.LoadAssetAtPath<HandGhostProvider>(GhostProviderPath);
        var probe = new GameObject("__graspProbe");
        var ghost = Object.Instantiate(provider.GetHand(handedness), probe.transform);
        ghost.SetPose(pose, Pose.identity);

        System.Func<string, Vector3> at = delegate (string n)
        {
            foreach (Transform t in ghost.GetComponentsInChildren<Transform>())
                if (t.name == n) return probe.transform.InverseTransformPoint(t.position);
            return Vector3.zero;
        };

        Vector3 wrist = at("XRHand_Wrist");
        Vector3 indexKnuckle = at("XRHand_IndexProximal");
        Vector3 littleKnuckle = at("XRHand_LittleProximal");
        Vector3 middleMid = at("XRHand_MiddleIntermediate");
        Vector3 thumbTip = at("XRHand_ThumbDistal");
        Vector3 middleKnuckle = at("XRHand_MiddleProximal");

        var g = new Grasp
        {
            // The SDK's own palm joint, not the midpoint of the middle finger and thumb.
            // That midpoint sits ~86 mm forward of the wrist -- far deeper than a fist
            // actually closes -- and threw the whole hand out to the side of the grip.
            centre = at("XRHand_Palm"),
            column = (indexKnuckle - littleKnuckle).normalized,
            palmNormal = Vector3.Cross((middleKnuckle - wrist).normalized,
                                       (littleKnuckle - indexKnuckle).normalized).normalized,
            radius = (middleMid - thumbTip).magnitude * 0.5f,
            forearm = (at("XRHand_Palm") - wrist).normalized
        };

        // That cross product gives the palm plane's normal but not reliably its sign -- it
        // comes out palm-side for the left hand and back-of-hand for the right. Rather than
        // special-case handedness, take the side the held column is on: whatever the hand
        // is gripping is by definition in front of the palm.
        if (Vector3.Dot(g.palmNormal, g.centre - wrist) < 0f)
        {
            g.palmNormal = -g.palmNormal;
        }

        Object.DestroyImmediate(probe);
        return g;
    }

    /// <summary>
    /// How far the forearm sits below the bore line, degrees. A shooting hand does not hold
    /// a match pistol with the arm dead level with the barrel; the bore rides slightly above
    /// the forearm, which is what the low bore axis and the rake are both for.
    /// </summary>
    public const float ForearmDroopDeg = 25f;

    /// <summary>Rotation whose +X is a1, +Y is a2 orthogonalised against it.</summary>
    static Quaternion Frame(Vector3 a1, Vector3 a2)
    {
        a1 = a1.normalized;
        Vector3 a2o = (a2 - Vector3.Project(a2, a1)).normalized;
        return Quaternion.LookRotation(Vector3.Cross(a1, a2o), a2o);
    }

    /// <summary>
    /// The wrist Pose, in the pistol's local space, that drops the grip down the middle of
    /// the fist.
    ///
    /// Two axes are matched. The fist's tube axis goes onto the grip's own axis, so the
    /// index knuckle ends up high on the front strap and the little knuckle low, which is
    /// how a hand sits on a raked grip. The palm goes onto the backstrap. Position then
    /// falls out: put the tube's centre on the grip's centre and solve back for the wrist.
    /// </summary>
    public static Pose WristPose(Handedness handedness, HandPose reference)
    {
        Grasp g = MeasureGrasp(handedness, reference);

        Vector3 gripUp = AddAirPistol.GripRotation * Vector3.up;

        // The forearm, NOT the palm, is the second constraint. Constraining the palm looks
        // reasonable and does not work: the palm normal and the grip axis both lie in the
        // pistol's YZ plane, so the frame's third axis is forced to lie along X and the
        // forearm gets thrown out sideways -- 80 degrees off the bore, unaimable. Pinning
        // the forearm instead is also the honest constraint, because forearm-to-bore is
        // exactly what a match pistol's rake exists to control. The palm orientation then
        // falls out, correctly, slightly turned rather than square to the backstrap.
        Vector3 forearmTarget = Quaternion.Euler(-ForearmDroopDeg, 0f, 0f) * Vector3.back;

        Quaternion rotation = Frame(gripUp, forearmTarget) * Quaternion.Inverse(Frame(g.column, g.forearm));

        // g.centre is the palm joint, which sits ON the palm surface. The grip's axis is
        // half a grip-depth further out, in front of the palm.
        Vector3 gripAxisInHand = g.centre + g.palmNormal * (AddAirPistol.GripSize.z * 0.5f);
        Vector3 wrist = GripAnchor - rotation * gripAxisInHand;

        return new Pose(wrist, rotation);
    }

    /// <summary>
    /// Where the fist closes on the grip. Slightly below the grip's centre: the web of the
    /// hand goes up under the frame and the heel rest catches the bottom of the palm, so
    /// the fingers close lower than half way.
    /// </summary>
    static Vector3 GripAnchor => AddAirPistol.GripCentre
        + AddAirPistol.GripRotation * new Vector3(0f, -0.008f, 0f);

    [MenuItem("Quest/10. Grab the Pistol (posed Hand Grab)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var pistol = GameObject.Find(PistolName);
        if (pistol == null)
        {
            Debug.LogError($"[Grab] no {PistolName} in the scene. Run AddAirPistol.Apply first.");
            return;
        }

        var body = pistol.GetComponent<Rigidbody>();
        if (body == null || pistol.GetComponent<Collider>() == null)
        {
            Debug.LogError($"[Grab] {PistolName} needs a Rigidbody and a Collider. Re-run AddAirPistol.Apply.");
            return;
        }

        if (!InteractorsPresent()) return;

        var handPoses = LoadReferenceHandPoses();
        if (handPoses == null) return;

        ClearPreviousInteraction(pistol);
        ClearGhosts(pistol);

        var grabbable = pistol.GetComponent<Grabbable>() ?? pistol.AddComponent<Grabbable>();
        grabbable.InjectOptionalRigidbody(body);
        grabbable.InjectOptionalTargetTransform(pistol.transform);
        grabbable.InjectOptionalKinematicWhileSelected(true);
        grabbable.InjectOptionalThrowWhenUnselected(true);

        var interactable = HandGrabUtils.CreateHandGrabInteractable(pistol.transform, InteractionChild);

        var poses = new List<HandGrabUtils.HandGrabPoseData>();
        foreach (var handedness in new[] { Handedness.Right, Handedness.Left })
        {
            if (!handPoses.TryGetValue(handedness, out var reference)) continue;
            poses.Add(new HandGrabUtils.HandGrabPoseData
            {
                gripPose = WristPose(handedness, reference),
                handPose = new HandPose(reference),
                scale = 1f
            });
        }

        HandGrabUtils.LoadData(interactable, new HandGrabUtils.HandGrabInteractableData
        {
            poses = poses,
            grabType = GrabTypeFlags.All,
            // Snap the visual hand onto the authored grip the instant the grab starts.
            // Without this the fingers stay wherever tracking last put them and the grip
            // reads as approximate, which is the thing being fixed.
            handAlignment = HandAlignType.AlignOnGrab,
            pinchGrabRules = GrabbingRule.DefaultPinchRule,
            palmGrabRules = GrabbingRule.DefaultPalmRule
        });

        EditorUtility.SetDirty(grabbable);
        EditorUtility.SetDirty(interactable);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();

        Verify(pistol);
    }

    [MenuItem("Quest/10b. Preview Grip (ghost hands)")]
    public static void PreviewGhosts()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[Grab] no {PistolName}."); return; }

        var provider = AssetDatabase.LoadAssetAtPath<HandGhostProvider>(GhostProviderPath);
        if (provider == null) { Debug.LogError($"[Grab] no ghost provider at {GhostProviderPath}."); return; }

        ClearGhosts(pistol);

        var interactable = pistol.GetComponentInChildren<HandGrabInteractable>(true);
        if (interactable == null) { Debug.LogError("[Grab] no HandGrabInteractable; run Quest > 10 first."); return; }

        int made = 0;
        foreach (var pose in interactable.HandGrabPoses)
        {
            if (pose.HandPose == null) continue;
            var ghost = Object.Instantiate(provider.GetHand(pose.HandPose.Handedness), pistol.transform);
            ghost.name = $"{GhostName}_{pose.HandPose.Handedness}";
            // SetPose already does both halves -- joint rotations AND placing the puppet via
            // the pose's RelativePose. Moving ghost.transform afterwards fights it, because
            // the puppet hangs off an internal root that is offset from the GameObject.
            ghost.SetPose(pose);
            made++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[Grab] {made} preview ghost(s) added. Quest > 10c removes them.");
    }

    [MenuItem("Quest/10c. Clear Grip Preview")]
    public static void ClearGhostsFromMenu()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var pistol = GameObject.Find(PistolName);
        if (pistol == null) return;
        ClearGhosts(pistol);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Grab] preview ghosts cleared.");
    }

    static void ClearGhosts(GameObject pistol)
    {
        var stale = pistol.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && t.name.StartsWith(GhostName))
            .Select(t => t.gameObject).ToList();
        foreach (var go in stale) Object.DestroyImmediate(go);
    }

    /// <summary>
    /// Pulls the recorded finger curl off the SDK's Torch prop, one HandPose per hand.
    /// </summary>
    static Dictionary<Handedness, HandPose> LoadReferenceHandPoses()
    {
        var torch = AssetDatabase.LoadAssetAtPath<GameObject>(TorchPath);
        if (torch == null)
        {
            Debug.LogError($"[Grab] reference prop not found at {TorchPath}. " +
                           "It ships with com.meta.xr.sdk.interaction; has the package moved?");
            return null;
        }

        var byHand = new Dictionary<Handedness, HandPose>();
        foreach (var pose in torch.GetComponentsInChildren<HandGrabPose>(true))
        {
            if (pose.HandPose == null) continue;
            if (!byHand.ContainsKey(pose.HandPose.Handedness))
            {
                byHand[pose.HandPose.Handedness] = pose.HandPose;
            }
        }

        if (byHand.Count == 0)
        {
            Debug.LogError("[Grab] the reference prop carries no HandPoses.");
            return null;
        }
        return byHand;
    }

    static bool InteractorsPresent()
    {
        var interactors = Object.FindObjectsByType<HandGrabInteractor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (interactors.Length > 0) return true;

        Debug.LogError("[Grab] no HandGrabInteractor in the scene. The Interactions Rig is missing — " +
                       "install a hand-grab Building Block (Meta > Tools > Building Blocks).");
        return false;
    }

    static void ClearPreviousInteraction(GameObject pistol)
    {
        var stale = pistol.transform.Cast<Transform>()
            .Where(c => c.GetComponent<HandGrabInteractable>() != null ||
                        c.GetComponent<TouchHandGrabInteractable>() != null ||
                        c.name == InteractionChild || c.name == "TouchHandGrab")
            .Select(c => c.gameObject).ToList();

        foreach (var go in stale)
        {
            Debug.Log($"[Grab] removed previous {go.name}");
            Object.DestroyImmediate(go);
        }
    }

    static void Verify(GameObject pistol)
    {
        var grabbable = pistol.GetComponent<Grabbable>();
        var interactable = pistol.GetComponentInChildren<HandGrabInteractable>(true);
        var interactors = Object.FindObjectsByType<HandGrabInteractor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int posed = interactable == null ? 0 : interactable.HandGrabPoses.Count(p => p.HandPose != null);
        Debug.Log($"[Grab] pistol: Grabbable={grabbable != null} HandGrabInteractable={interactable != null} " +
                  $"posed grips={posed}");
        Debug.Log($"[Grab] interactors in the scene: {interactors.Length} hand grab");

        // Report off what actually landed in the scene, not off a recomputation.
        if (interactable != null)
        {
            foreach (var pose in interactable.HandGrabPoses)
            {
                if (pose.HandPose == null) continue;
                var local = pose.RelativePose;
                // The number that decides whether it can be aimed. The hand's local +Z runs
                // wrist-to-palm, so through the rotation it becomes the forearm's direction
                // in pistol space; the bore is the pistol's -Z. Small angle = the barrel
                // lies along the arm and the thing points where the arm points.
                Vector3 forearm = local.rotation * Vector3.forward;
                float boreToForearm = Vector3.Angle(Vector3.back, forearm);
                Debug.Log($"[Grab] {pose.HandPose.Handedness} wrist local={local.position:F3} " +
                          $"bore-to-forearm {boreToForearm:F0} deg " +
                          "(0 = barrel straight along the arm)");
            }
        }

        bool ok = grabbable != null && interactable != null && posed >= 2 && interactors.Length > 0;
        Debug.Log(ok
            ? "[Grab] posed grip is wired -- the pistol will snap to the same grip every pickup."
            : "[Grab] WARNING: incomplete; the grab will fall back to pose-less and will not aim.");
    }
}
