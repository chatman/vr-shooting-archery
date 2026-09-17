using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Clears Grabbable._targetTransform on the air pistol.
///
/// PistolHandGrab sets it with InjectOptionalTargetTransform(pistol.transform) -- the
/// object's own transform. That is self-referential: the grab computes a correction
/// relative to the target and then moves the target, so the next frame reads its own
/// output. On device the pistol diverged from centimetres to hundreds of metres per frame
/// and ended up ~12,265 m from the bench the instant it was lifted, still held, still
/// visible, still at scale 1 -- the signature of a feedback loop rather than a bad pose.
///
/// Meta's own [BB] Grabbable Cube leaves _targetTransform null, and Grabbable falls back
/// to its own transform internally, which is what breaks the loop. The authored
/// HandGrabPoses are fine (local offsets of a few centimetres) and are left untouched.
/// </summary>
public static class FixGrabbableTarget
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";

    [MenuItem("Quest/10d. Clear Grabbable Target Transform")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[GrabFix] no {PistolName} in the scene."); return; }

        int cleared = 0;
        foreach (var c in pistol.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null || c.GetType().Name != "Grabbable") continue;

            var so = new SerializedObject(c);
            var p = so.FindProperty("_targetTransform");
            if (p == null) { Debug.LogError("[GrabFix] _targetTransform not found on Grabbable."); continue; }

            var before = p.objectReferenceValue;
            if (before == null) { Debug.Log("[GrabFix] already null; not the cause."); continue; }

            p.objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(c);
            cleared++;
            Debug.Log($"[GrabFix] {c.gameObject.name}: _targetTransform {before.name} -> null");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Read back rather than trust the write.
        foreach (var c in pistol.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null || c.GetType().Name != "Grabbable") continue;
            var p = new SerializedObject(c).FindProperty("_targetTransform");
            Debug.Log($"[GrabFix] VERIFY _targetTransform=" +
                      $"{(p?.objectReferenceValue == null ? "null OK" : p.objectReferenceValue.name + " STILL SET")}");
        }

        Debug.Log($"[GrabFix] cleared {cleared} target transform(s).");
    }
}
