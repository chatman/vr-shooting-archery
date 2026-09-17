using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Temporarily removes the authored HandGrabPoses, turning the pistol into a pose-less
/// grab, to find out whether the poses are what teleports it.
///
/// The evidence so far: on grab the pistol jumps 146 m in the first frame, compounds to
/// about 1500 m, then tracks the hand with roughly 80x amplification -- all while
/// held=True, scale=1 and visible. Clearing Grabbable._targetTransform (which pointed at
/// the pistol's own transform) cut the runaway from 12,265 m to 1,514 m but did not stop
/// it. The authored poses themselves serialize as sane local offsets of a few
/// centimetres, and the only sub-unit scale nearby turned out to be an SDK hand-joint
/// visualisation sphere.
///
/// So rather than guess again: strip the poses. Meta's own [BB] Grabbable Cube is
/// pose-less and works. If the pistol then picks up cleanly, the poses are at fault and
/// can be rebuilt. If it still teleports, the fault is in the interactor rig or the
/// interactable wiring and the poses were never the issue.
///
/// This is a diagnostic step: a pose-less grab can be carried but not reliably aimed,
/// which is the thing the posed grab exists to provide. Quest > 10 rebuilds the poses.
/// </summary>
public static class BisectGrabPoses
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";

    [MenuItem("Quest/10e. Bisect - strip grab poses")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var pistol = GameObject.Find(PistolName);
        if (pistol == null) { Debug.LogError($"[Bisect] no {PistolName}."); return; }

        // HandGrabPose components live on child objects of the interactable.
        var poses = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(c => c != null && c.GetType().Name == "HandGrabPose")
            .ToArray();

        Debug.Log($"[Bisect] found {poses.Length} HandGrabPose component(s)");

        foreach (var p in poses)
        {
            Debug.Log($"[Bisect]   removing {Path(p.gameObject)} " +
                      $"localPos={p.transform.localPosition} localScale={p.transform.localScale}");
            Object.DestroyImmediate(p.gameObject);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Read back: the interactable should survive, with no poses left.
        var interactable = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .FirstOrDefault(c => c != null && c.GetType().Name == "HandGrabInteractable");
        int left = pistol.GetComponentsInChildren<MonoBehaviour>(true)
            .Count(c => c != null && c.GetType().Name == "HandGrabPose");

        Debug.Log($"[Bisect] VERIFY HandGrabInteractable={(interactable != null ? "present" : "MISSING")} " +
                  $"posesRemaining={left}");
    }

    static string Path(GameObject go)
    {
        var path = go.name;
        for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path;
    }
}
