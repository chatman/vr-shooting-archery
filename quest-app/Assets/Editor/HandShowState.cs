using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Makes the hand meshes visible regardless of controller state.
///
/// OVRHandPrefab ships with m_showState = ControllerNotInHand. In OVRHand.GetHandState
/// the hand pose is read successfully and IsDataValid is set true, and then this runs:
///
///     case InputDeviceShowState.ControllerNotInHand:
///         if (controllerInHandState != ControllerInHandState.ControllerNotInHand)
///             IsDataValid = false;
///
/// With the controllers switched off entirely, GetControllerIsInHandState() returns
/// NoHand rather than ControllerNotInHand, so the comparison fails and IsDataValid is
/// forced back to false. OVRMeshRenderer keys the SkinnedMeshRenderer off that flag, so
/// the hand mesh is never drawn -- even though tracking is working, the mesh has loaded
/// and the shader is fine. On device: IsTracked=True, confidence=High, IsDataValid=False.
///
/// Always shows the hands whenever the runtime is tracking them, which is what this
/// project wants: it is a hands-first app and the lamps/targets are not held.
/// </summary>
public static class HandShowState
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var hands = Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include,
                                                      FindObjectsSortMode.None);
        if (hands.Length == 0)
        {
            Debug.LogError("[ShowState] no OVRHand components in the scene.");
            return;
        }

        foreach (var hand in hands)
        {
            var so = new SerializedObject(hand);
            var p = so.FindProperty("m_showState");
            if (p == null)
            {
                Debug.LogError($"[ShowState] {hand.gameObject.name}: m_showState not found " +
                               "-- the SDK field name has changed.");
                continue;
            }

            int before = p.intValue;
            p.intValue = (int)OVRInput.InputDeviceShowState.Always;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(hand);

            Debug.Log($"[ShowState] {hand.gameObject.name}: m_showState " +
                      $"{(OVRInput.InputDeviceShowState)before} -> " +
                      $"{(OVRInput.InputDeviceShowState)p.intValue}");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Read back rather than trusting the write.
        foreach (var hand in Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include,
                                                              FindObjectsSortMode.None))
        {
            var p = new SerializedObject(hand).FindProperty("m_showState");
            var v = p == null ? -1 : p.intValue;
            Debug.Log($"[ShowState] VERIFY {hand.gameObject.name}: m_showState={v} " +
                      $"{(v == 0 ? "(Always) OK" : "STILL WRONG")}");
        }
    }
}
