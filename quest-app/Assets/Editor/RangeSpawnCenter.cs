using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Moves the player rig to the middle of the hall without rebuilding the scene.
///
/// Quest > 4 builds the scene from an empty one, which throws away the lightmap,
/// so repositioning through it would cost another bake. This edits the saved
/// scene in place instead: the bake, the materials and the colliders all survive.
/// </summary>
public static class RangeSpawnCenter
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    /// <summary>
    /// Stands the player at firing point 23, facing the targets.
    ///
    /// The repo's SpawnPosition is (2.5, 0, -11.75). The X is right — lane 23 of
    /// 40 across a 39.5 m spread lands at +2.5 — but the Z sign is inverted: the
    /// hall runs Z 0..30, so -11.75 is nearly 12 m outside the back wall. The
    /// targets sit at Z=0 and the firing bench at Z=10.5, so the shooter belongs
    /// at +11.75 looking down -Z. Identity rotation faces +Z, i.e. away from the
    /// targets, so the rig needs turning 180 degrees as well.
    /// </summary>
    [MenuItem("Quest/7. Stand at Firing Point 23")]
    public static void MoveToFiringPoint()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var rig = GameObject.Find("OVRCameraRig");
        if (rig == null) { Debug.LogError("[Range] No OVRCameraRig in the scene."); return; }

        var pos = new Vector3(2.5f, 0f, 11.75f);
        rig.transform.position = pos;
        rig.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log($"[Range] Player at firing point 23 {pos}, facing -Z: " +
                  "targets at Z=0 are 11.75 m downrange, bench at Z=10.5 just ahead.");
    }

    [MenuItem("Quest/6. Move Player to Hall Centre")]
    public static void MoveToCentre()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var hall = GameObject.Find("Range10m");
        if (hall == null)
        {
            Debug.LogError("[Range] No 'Range10m' object in the scene. Run Quest > 4 first.");
            return;
        }

        // The FBX pivot is not necessarily the middle of the room, so take the
        // centre from the renderers themselves rather than trusting the transform.
        var renderers = hall.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0)
        {
            Debug.LogError("[Range] Range10m has no MeshRenderers to measure.");
            return;
        }

        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);

        var rig = GameObject.Find("OVRCameraRig");
        if (rig == null)
        {
            Debug.LogError("[Range] No OVRCameraRig in the scene.");
            return;
        }

        // Floor-level tracking origin means the rig sits on the floor and the
        // headset rises to the player's real standing height from there. Placing
        // the rig at bounds.min.y rather than centre.y keeps that true.
        var centre = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        rig.transform.position = centre;
        rig.transform.rotation = Quaternion.identity;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log($"[Range] Hall bounds: centre {bounds.center}, size {bounds.size}, " +
                  $"floor y={bounds.min.y:F2}.");
        Debug.Log($"[Range] Player moved to hall centre {centre}, facing +Z (downrange).");
    }
}
