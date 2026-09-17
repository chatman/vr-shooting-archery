using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Lists every hand joint the scene actually contains, with full paths and handedness.
///
/// PinchGrab drove off OVRHand plus OVRSkeleton, and the scene now has neither: the
/// OVRHandPrefab hands are gone and the Interaction SDK's own representation is in their
/// place. The scene file shows XRHand_Wrist and XRHand_IndexTip twice each, but every
/// hand root name visible in the YAML reads as right-handed, so it cannot be told from
/// the text whether those are a left/right pair or two right hands.
///
/// Which transforms are live and which hand each belongs to decides what a rewritten
/// grab can attach to, so it is worth reading from Unity rather than inferring.
/// </summary>
public static class DumpHandHierarchy
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    [MenuItem("Quest/12. Dump hand hierarchy")]
    public static void Apply()
    {
        EditorSceneManager.OpenScene(ScenePath);

        foreach (var name in new[] { "XRHand_Wrist", "XRHand_IndexTip", "XRHand_Palm" })
        {
            var hits = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                             .Where(t => t.name == name)
                             .ToArray();

            Debug.Log($"[Hands] === {name}: {hits.Length} ===");
            foreach (var t in hits)
                Debug.Log($"[Hands]   {Path(t.gameObject)}  active={t.gameObject.activeInHierarchy}");
        }

        // Whatever drives those joints is what a grab must follow.
        foreach (var typeName in new[] { "SyntheticHand", "HandVisual", "Hand", "HandRef", "FromOVRHandDataSource" })
        {
            var comps = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                              .Where(c => c != null && c.GetType().Name == typeName)
                              .ToArray();
            Debug.Log($"[Hands] === {typeName}: {comps.Length} ===");
            foreach (var c in comps.Take(6))
                Debug.Log($"[Hands]   {Path(c.gameObject)} enabled={c.enabled} active={c.gameObject.activeInHierarchy}");
        }

        // Anything still exposing a pinch is a candidate input source.
        var ovrHands = Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Hands] === OVRHand: {ovrHands.Length} ===");
        foreach (var h in ovrHands)
            Debug.Log($"[Hands]   {Path(h.gameObject)} skeleton={(h.GetComponent<OVRSkeleton>() != null)}");
    }

    static string Path(GameObject go)
    {
        var path = go.name;
        for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path;
    }
}
