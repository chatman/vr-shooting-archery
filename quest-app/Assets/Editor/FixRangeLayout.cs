using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Corrects the lane mapping and the player's start, and reports the state of the hands.
///
/// Lane numbering: build_range.py prints number i+1 at LANE_X[i] = i - 19.5, so lane N is
/// at generator x = N - 20.5. The FBX import mirrors X, which was confirmed on device --
/// the pistol placed at world x = -1.5 reads as firing point 22, and the generator puts
/// lane 22 at x = +1.5. So in world space:
///
///     lane N  ->  x = 20.5 - N          (lane 19 -> x = +1.5)
///
/// Start position: between the chairs (z = 13) and the bench (z = 10..11), so z = 12,
/// on lane 19, facing the targets at z = 0 (yaw 180, since identity faces +Z).
///
/// The hand report is diagnosis, not a fix: the hand meshes stopped rendering after the
/// interaction rig went in, and grepping the scene file has proved unreliable for this
/// kind of question, so the values are read through Unity here.
/// </summary>
public static class FixRangeLayout
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";

    const int Lane = 19;
    const float BenchTopY = 1.0f;
    const float BenchMidZ = 10.5f;

    // Chairs sit at z = 13, the bench back face at z = 11; midway is z = 12.
    const float StandZ = 12.0f;

    static float LaneX(int lane) => 20.5f - lane;

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        ReportHands();
        MovePistol();
        MoveSpawn();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Layout] saved.");
    }

    static void ReportHands()
    {
        var hands = Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Layout] OVRHand components in scene: {hands.Length}");

        foreach (var hand in hands)
        {
            var go = hand.gameObject;
            var smr = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault();
            var skel = go.GetComponent<OVRSkeleton>();
            var mesh = go.GetComponent<OVRMesh>();

            Debug.Log($"[Layout]   {Path(go)} active={go.activeInHierarchy} handEnabled={hand.enabled} " +
                      $"skeleton={(skel == null ? "none" : skel.enabled.ToString())} " +
                      $"mesh={(mesh == null ? "none" : mesh.enabled.ToString())} " +
                      $"smr={(smr == null ? "none" : $"enabled={smr.enabled} obj={smr.gameObject.activeInHierarchy}")}");
        }

        // Anything the interaction rig added that could be driving or hiding the hands.
        foreach (var name in new[] { "HandGrabInteractor", "SyntheticHand", "HandVisual", "OVRCameraRigRef" })
        {
            var found = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                              .Where(c => c != null && c.GetType().Name == name).ToArray();
            Debug.Log($"[Layout]   {name}: {found.Length}" +
                      (found.Length > 0 ? $" (first: {Path(found[0].gameObject)}, enabled={found[0].enabled})" : ""));
        }
    }

    static void MovePistol()
    {
        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[Layout] {PistolName} not found."); return; }

        var before = pistol.transform.position;
        pistol.transform.position = new Vector3(LaneX(Lane), BenchTopY, BenchMidZ);
        EditorUtility.SetDirty(pistol);

        Debug.Log($"[Layout] pistol {before} -> {pistol.transform.position} " +
                  $"(lane {Lane} is world x={LaneX(Lane)}; it was reading as firing point 22 at x=-1.5)");
    }

    static void MoveSpawn()
    {
        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        if (rig == null) { Debug.LogError("[Layout] no OVRCameraRig."); return; }

        var before = rig.transform.position;
        var beforeYaw = rig.transform.rotation.eulerAngles.y;

        rig.transform.position = new Vector3(LaneX(Lane), 0f, StandZ);
        // Identity faces +Z; the targets are at z = 0, so face -Z.
        rig.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        EditorUtility.SetDirty(rig);

        Debug.Log($"[Layout] spawn {before} yaw={beforeYaw:0} -> {rig.transform.position} yaw=180 " +
                  $"(lane {Lane}, between chairs z=13 and bench z=11, facing the targets at z=0)");
    }

    static string Path(GameObject go)
    {
        var path = go.name;
        for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path;
    }
}
