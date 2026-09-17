using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Switches the pistol from the Interaction SDK's posed grab to a fixed-offset grab,
/// keeping the grip geometry that was already proven correct.
///
/// The authored HandGrabPoses measured right on device -- wrist at (-0.023, 0.055, 0.115)
/// for the right hand, mirrored for the left, barrel within 5 degrees of the forearm.
/// What failed was the SDK resolving them at runtime: every grab teleported the pistol
/// hundreds of metres on its first frame, and six candidate causes were eliminated
/// without finding it.
///
/// So the poses are HARVESTED rather than discarded: their transforms, relative to the
/// pistol, become the wrist poses on GrabbableItem, and PinchGrab places the pistol by
/// arithmetic. The grip a shooter feels is identical; only the machinery differs.
///
/// The SDK grab components are then removed. They have to go: the interactors are still
/// live in the scene and would keep grabbing the pistol and flinging it away.
/// </summary>
public static class UseFixedOffsetGrab
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";

    [MenuItem("Quest/11. Fixed-offset grab (harvest SDK poses)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[Fixed] no {PistolName}."); return; }

        var item = pistol.GetComponent<GrabbableItem>() ?? pistol.AddComponent<GrabbableItem>();
        Harvest(pistol, item);
        StripSdkGrab(pistol);

        EditorUtility.SetDirty(item);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Verify(pistol);
    }

    /// <summary>Lift each HandGrabPose's transform, relative to the pistol, onto the item.</summary>
    static void Harvest(GameObject pistol, GrabbableItem item)
    {
        var poses = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(c => c != null && c.GetType().Name == "HandGrabPose")
            .ToArray();

        if (poses.Length == 0)
        {
            Debug.LogError("[Fixed] no HandGrabPoses to harvest. Run Quest > 10 first.");
            return;
        }

        foreach (var pose in poses)
        {
            // Pose transforms are children of the pistol, so their pose relative to the
            // pistol root is exactly the wrist pose we need.
            var local = pistol.transform.InverseTransformPoint(pose.transform.position);
            var localRot = Quaternion.Inverse(pistol.transform.rotation) * pose.transform.rotation;

            // Right hand sits at negative local x, left at positive -- confirmed by the
            // pose rebuild log ("Right wrist local=(-0.023, ...)").
            bool isLeft = local.x > 0f;

            if (isLeft) { item.leftWristLocalPosition = local; item.leftWristLocalRotation = localRot; }
            else { item.rightWristLocalPosition = local; item.rightWristLocalRotation = localRot; }

            Debug.Log($"[Fixed] harvested {(isLeft ? "LEFT" : "RIGHT")} wrist local={local} " +
                      $"euler={localRot.eulerAngles}");
        }

        item.hasGrips = true;
    }

    /// <summary>
    /// Remove Grabbable, HandGrabInteractable and the HandGrab child. The scene's four
    /// HandGrabInteractors are still active and would otherwise keep seizing the pistol.
    /// </summary>
    static void StripSdkGrab(GameObject pistol)
    {
        int removed = 0;

        foreach (var c in pistol.GetComponentsInChildren<MonoBehaviour>(true).ToArray())
        {
            if (c == null) continue;
            var n = c.GetType().Name;
            if (n != "Grabbable" && n != "HandGrabInteractable" && n != "GrabInteractable") continue;
            Debug.Log($"[Fixed] removing {n} from {c.gameObject.name}");
            Object.DestroyImmediate(c);
            removed++;
        }

        var child = pistol.transform.Find("HandGrab");
        if (child != null)
        {
            Debug.Log("[Fixed] removing the HandGrab child (pose holders)");
            Object.DestroyImmediate(child.gameObject);
        }

        Debug.Log($"[Fixed] removed {removed} SDK grab component(s).");
    }

    static void Verify(GameObject pistol)
    {
        var item = pistol.GetComponent<GrabbableItem>();
        var body = pistol.GetComponent<Rigidbody>();
        var col = pistol.GetComponent<Collider>();

        int sdkLeft = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .Count(c => c != null && (c.GetType().Name == "Grabbable" ||
                                      c.GetType().Name == "HandGrabInteractable" ||
                                      c.GetType().Name == "HandGrabPose"));

        Debug.Log($"[Fixed] VERIFY GrabbableItem={item != null} hasGrips={item?.hasGrips} " +
                  $"rigidbody={body != null} collider={col != null} sdkGrabComponentsLeft={sdkLeft}");
        if (item != null)
        {
            Debug.Log($"[Fixed] VERIFY right wrist={item.rightWristLocalPosition} " +
                      $"left wrist={item.leftWristLocalPosition}");
        }
        if (sdkLeft > 0) Debug.LogError("[Fixed] SDK grab components remain -- they will fight the fixed grab.");
    }
}
