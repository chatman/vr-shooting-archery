using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Puts visible hands on the player, and moves the spawn behind a chair so the
/// table has to be walked to.
///
/// Hand rendering needs three things, and missing any one of them shows nothing:
///   1. OVRProjectConfig.handTrackingSupport — this is what injects the
///      oculus.software.handtracking feature and permission into the manifest.
///      Without it the runtime never reports hand data at all.
///   2. OVRManager.handTrackingSupport on the rig, so the SDK polls for it.
///   3. An OVRHandPrefab under each hand anchor, with hand/skeleton/mesh type
///      set per side. Those fields are private and serialized, so they are set
///      through SerializedObject rather than by assignment.
///
/// Controller models go on too: with controllers in hand the runtime reports
/// controller poses rather than hands, so without them the player would see
/// nothing whenever they pick the controllers up.
/// </summary>
public static class RangeHands
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string HandPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRHandPrefab.prefab";
    const string ControllerPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRControllerPrefab.prefab";

    // Chairs sit at Z=13.0 (0.45 m deep, so 12.78..13.22) and the firing bench at
    // Z=10.5. Standing at 14.0 puts the player about 0.8 m behind the chair with
    // roughly 3 m to walk before reaching the table.
    static readonly Vector3 SpawnBehindChair = new Vector3(2.5f, 0f, 14.0f);

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        EnableHandTrackingInProjectConfig();

        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        if (rig == null) { Debug.LogError("[Hands] no OVRCameraRig in the scene."); return; }

        var mgr = Object.FindFirstObjectByType<OVRManager>();
        if (mgr != null)
        {
            var so = new SerializedObject(mgr);
            var p = so.FindProperty("handTrackingSupport");
            // Index 1 = ControllersAndHands (0 = ControllersOnly, 2 = HandsOnly).
            if (p != null) { p.enumValueIndex = 1; Debug.Log("[Hands] OVRManager.handTrackingSupport = ControllersAndHands"); }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mgr);
        }

        AttachHand(rig.leftHandAnchor, isLeft: true);
        AttachHand(rig.rightHandAnchor, isLeft: false);

        AttachController(rig.leftControllerAnchor, isLeft: true);
        AttachController(rig.rightControllerAnchor, isLeft: false);

        // ---- spawn behind the chair ----
        rig.transform.position = SpawnBehindChair;
        rig.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        Debug.Log($"[Hands] spawn moved to {SpawnBehindChair} facing -Z " +
                  "(chair at Z=13.0, bench at Z=10.5 — walk forward to reach the table)");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[Hands] done.");
    }

    static void EnableHandTrackingInProjectConfig()
    {
        try
        {
            var cfg = OVRProjectConfig.CachedProjectConfig;
            if (cfg == null) { Debug.LogWarning("[Hands] no OVRProjectConfig"); return; }

            var so = new SerializedObject(cfg);
            var hts = so.FindProperty("handTrackingSupport");
            if (hts != null) { hts.enumValueIndex = 2; Debug.Log("[Hands] ProjectConfig.handTrackingSupport = ControllersAndHands"); }
            var freq = so.FindProperty("handTrackingFrequency");
            if (freq != null) { freq.enumValueIndex = 1; Debug.Log("[Hands] ProjectConfig.handTrackingFrequency = HIGH"); }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Hands] could not set project config: {e.Message}");
        }
    }

    static void AttachHand(Transform anchor, bool isLeft)
    {
        var side = isLeft ? "L" : "R";
        if (anchor == null) { Debug.LogWarning($"[Hands] no {side} hand anchor"); return; }

        var name = $"OVRHand{side}";
        var existing = anchor.Find(name);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandPrefab);
        if (prefab == null) { Debug.LogError($"[Hands] prefab not found: {HandPrefab}"); return; }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
        go.name = name;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        // Index 1 = HandLeft, 2 = HandRight in each enum (None is index 0).
        // OVRHand names its field HandType; OVRSkeleton and OVRMesh use the
        // underscore-prefixed convention.
        int v = isLeft ? 1 : 2;
        SetPrivateEnum(go, "OVRHand", "HandType", v);
        SetPrivateEnum(go, "OVRSkeleton", "_skeletonType", v);
        SetPrivateEnum(go, "OVRMesh", "_meshType", v);

        // OVRMesh pulls the hand mesh from the runtime's legacy API. This device
        // reports "hand skeleton version is OpenXR", where that mesh may never
        // arrive — and OVRMeshRenderer then draws nothing, with no error. Enable
        // OVRSkeletonRenderer as well: it draws the joints and bones directly
        // from tracking data, so something visible appears either way.
        foreach (var c in go.GetComponents<MonoBehaviour>())
        {
            if (c == null) continue;
            var n = c.GetType().Name;
            if (n == "OVRSkeletonRenderer" || n == "OVRMeshRenderer")
            {
                c.enabled = true;
                EditorUtility.SetDirty(c);
                Debug.Log($"[Hands]   enabled {n} on {name}");
            }
        }

        Debug.Log($"[Hands] attached {name} under {anchor.name}");
    }

    static void AttachController(Transform anchor, bool isLeft)
    {
        var side = isLeft ? "L" : "R";
        if (anchor == null) { Debug.LogWarning($"[Hands] no {side} controller anchor"); return; }

        var name = $"OVRController{side}";
        var existing = anchor.Find(name);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ControllerPrefab);
        if (prefab == null) { Debug.LogWarning($"[Hands] no controller prefab at {ControllerPrefab}"); return; }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
        go.name = name;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        // OVRControllerHelper.m_controller: 4 = LTouch, 5 = RTouch in OVRInput.Controller.
        foreach (var c in go.GetComponents<MonoBehaviour>())
        {
            if (c == null || c.GetType().Name != "OVRControllerHelper") continue;
            var so = new SerializedObject(c);
            var p = so.FindProperty("m_controller");
            if (p != null) { p.intValue = isLeft ? 0x00000004 : 0x00000008; so.ApplyModifiedPropertiesWithoutUndo(); }
        }

        Debug.Log($"[Hands] attached {name} under {anchor.name}");
    }

    static void SetPrivateEnum(GameObject go, string componentName, string field, int value)
    {
        foreach (var c in go.GetComponents<MonoBehaviour>())
        {
            if (c == null || c.GetType().Name != componentName) continue;
            var so = new SerializedObject(c);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[Hands] {componentName}.{field} not found"); return; }
            p.enumValueIndex = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(c);
            return;
        }
        Debug.LogWarning($"[Hands] no {componentName} on {go.name}");
    }
}
