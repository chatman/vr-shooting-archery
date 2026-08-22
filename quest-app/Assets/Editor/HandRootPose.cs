using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Lets the tracked hands move through the world, not just flex their fingers.
///
/// OVRSkeleton applies the finger bone rotations every frame regardless, but the hand's
/// own position and orientation are gated:
///
///     if (_updateRootPose) {
///         transform.localPosition = data.RootPose.Position...;
///         transform.localRotation = data.RootPose.Orientation...;
///     }
///     if (_updateRootScale) { transform.localScale = ...RootScale...; }
///
/// Both fields ship false on OVRHandPrefab. The result on device is a hand pinned to
/// wherever its anchor sits, with fingers that animate correctly -- which is what this
/// project showed: a stationary hand on the floor responding to finger movement.
///
/// Root scale is enabled alongside so the mesh matches the user's actual hand size;
/// without it every hand renders at the SDK's default scale.
/// </summary>
public static class HandRootPose
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var skeletons = Object.FindObjectsByType<OVRSkeleton>(FindObjectsInactive.Include,
                                                              FindObjectsSortMode.None);
        if (skeletons.Length == 0)
        {
            Debug.LogError("[RootPose] no OVRSkeleton components in the scene.");
            return;
        }

        foreach (var skel in skeletons)
        {
            var so = new SerializedObject(skel);
            SetBool(so, "_updateRootPose", true, skel.gameObject.name);
            SetBool(so, "_updateRootScale", true, skel.gameObject.name);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(skel);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Read back from the saved scene rather than trusting the writes.
        foreach (var skel in Object.FindObjectsByType<OVRSkeleton>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None))
        {
            var so = new SerializedObject(skel);
            var pose = so.FindProperty("_updateRootPose");
            var scale = so.FindProperty("_updateRootScale");
            bool ok = pose != null && pose.boolValue && scale != null && scale.boolValue;
            Debug.Log($"[RootPose] VERIFY {skel.gameObject.name}: " +
                      $"updateRootPose={(pose == null ? "MISSING" : pose.boolValue.ToString())} " +
                      $"updateRootScale={(scale == null ? "MISSING" : scale.boolValue.ToString())} " +
                      $"{(ok ? "OK" : "STILL WRONG")}");
        }
    }

    static void SetBool(SerializedObject so, string field, bool value, string goName)
    {
        var p = so.FindProperty(field);
        if (p == null)
        {
            Debug.LogError($"[RootPose] {goName}: '{field}' not found -- SDK field name changed.");
            return;
        }
        Debug.Log($"[RootPose] {goName}: {field} {p.boolValue} -> {value}");
        p.boolValue = value;
    }
}
