using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SpatialTracking;

/// <summary>
/// Drives the centre-eye camera from the XR head pose.
///
/// OVRCameraRig is supposed to pose its own anchors from OVRPlugin, but on this
/// build the stereo display comes up (XR_SESSION_STATE_FOCUSED, correct
/// framebuffer) while the view stays locked — the anchors never move. A
/// TrackedPoseDriver reads the pose from Unity's XR input subsystem instead,
/// which works whichever provider actually owns the session.
/// </summary>
public static class RangeTrackingFix
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        // Report what the rig looks like before touching it, so the log explains
        // itself if this turns out not to be the cause.
        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        Debug.Log(rig == null
            ? "[Fix] No OVRCameraRig component."
            : $"[Fix] OVRCameraRig enabled={rig.enabled} activeInHierarchy={rig.gameObject.activeInHierarchy}");

        var mgr = Object.FindFirstObjectByType<OVRManager>();
        Debug.Log(mgr == null
            ? "[Fix] No OVRManager component."
            : $"[Fix] OVRManager enabled={mgr.enabled} activeInHierarchy={mgr.gameObject.activeInHierarchy} " +
              $"trackingOrigin={mgr.trackingOriginType}");

        var anchor = GameObject.Find("CenterEyeAnchor");
        if (anchor == null)
        {
            Debug.LogError("[Fix] No CenterEyeAnchor in the scene.");
            return;
        }

        var tpd = anchor.GetComponent<TrackedPoseDriver>();
        if (tpd == null)
        {
            tpd = anchor.AddComponent<TrackedPoseDriver>();
            Debug.Log("[Fix] Added TrackedPoseDriver to CenterEyeAnchor.");
        }
        else
        {
            Debug.Log("[Fix] TrackedPoseDriver already present; reconfiguring.");
        }

        tpd.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice,
                          TrackedPoseDriver.TrackedPose.Center);
        tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

        EditorUtility.SetDirty(anchor);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log($"[Fix] TrackedPoseDriver configured: source=Center, " +
                  $"tracking={tpd.trackingType}, update={tpd.updateType}. Scene saved.");
    }
}
