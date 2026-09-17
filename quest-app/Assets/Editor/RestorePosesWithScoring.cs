using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Rebuilds the authored grab poses and sets the pose scoring weight Meta's own
/// grabbable uses.
///
/// Field-by-field against [BB] Grabbable Cube, the interactable differed in exactly one
/// meaningful place:
///
///     ours  _scoringModifier._positionRotationWeight: 0
///     Meta  _scoringModifier._positionRotationWeight: 0.8
///
/// That weight balances position against rotation when scoring candidate grab poses and
/// settling on one. At 0 the position term drops out of the comparison entirely, which
/// is a credible way for a pose to resolve somewhere absurd -- and absurd is what the
/// device showed: a 146 m jump on the first frame of the grab, compounding to ~1500 m.
/// PistolHandGrab's LoadData call never sets it, so it defaulted to zero.
///
/// The bisect established the rest: with poses the grab engages (so the interactor rig,
/// colliders and wiring are sound) and without them it does not engage at all (because
/// AlignOnGrab has nothing to align to). The fault is in how the pose resolves, not in
/// whether the grab can fire.
/// </summary>
public static class RestorePosesWithScoring
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";
    const float MetaWeight = 0.8f;

    [MenuItem("Quest/10f. Restore poses with Meta's scoring weight")]
    public static void Apply()
    {
        // Rebuild the poses first; this reopens and saves the scene itself.
        PistolHandGrab.Apply();

        var scene = EditorSceneManager.OpenScene(ScenePath);
        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[Scoring] no {PistolName}."); return; }

        var interactable = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .FirstOrDefault(c => c != null && c.GetType().Name == "HandGrabInteractable");
        if (interactable == null) { Debug.LogError("[Scoring] no HandGrabInteractable after rebuild."); return; }

        var so = new SerializedObject(interactable);
        var p = so.FindProperty("_scoringModifier._positionRotationWeight");
        if (p == null) { Debug.LogError("[Scoring] _positionRotationWeight not found."); return; }

        float before = p.floatValue;
        p.floatValue = MetaWeight;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(interactable);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        int poses = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .Count(c => c != null && c.GetType().Name == "HandGrabPose");
        var check = new SerializedObject(interactable).FindProperty("_scoringModifier._positionRotationWeight");

        Debug.Log($"[Scoring] _positionRotationWeight {before} -> {check?.floatValue}");
        Debug.Log($"[Scoring] VERIFY poses={poses} (expect 2)");
    }
}
