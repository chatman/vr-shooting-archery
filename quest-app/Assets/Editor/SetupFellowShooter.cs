using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch: import Shooter.fbx as Humanoid, split PracticeShot into Ready(1-50 loop) +
// Shoot(50-300 once), build an Animator, and drop the character on the adjacent lane
// facing the targets. Run: -executeMethod SetupFellowShooter.Run
public static class SetupFellowShooter
{
    const string Fbx      = "Assets/Models/FellowShooter/Shooter.fbx";
    const string CtrlPath = "Assets/Models/FellowShooter/FellowShooter.controller";
    const string Scene    = "Assets/Scenes/Range.unity";

    public static void Run()
    {
        // ---- 1. import settings (per shooter-model README) ----
        var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
        if (imp == null) { Debug.LogError("[FS] FBX not found at " + Fbx); EditorApplication.Exit(2); return; }
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        imp.materialLocation = ModelImporterMaterialLocation.InPrefab;

        ModelImporterClipAnimation Clip(string name, int a, int b, bool loop) =>
            new ModelImporterClipAnimation {
                name = name, firstFrame = a, lastFrame = b, loopTime = loop,
                lockRootRotation = true, keepOriginalOrientation = true,
                lockRootHeightY = true, keepOriginalPositionY = true,
                lockRootPositionXZ = true, keepOriginalPositionXZ = true,
            };
        imp.clipAnimations = new[] {
            Clip("FS_Ready", 1, 50, true),     // ready / breathing, loops
            Clip("FS_Shoot", 50, 300, false),  // raise -> aim -> shot(160) -> lower -> recover
        };
        AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceSynchronousImport);

        AnimationClip ready = null, shoot = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Fbx))
        {
            var c = o as AnimationClip; if (c == null) continue;
            if (c.name == "FS_Ready") ready = c; else if (c.name == "FS_Shoot") shoot = c;
        }
        Debug.Log("[FS] clips ready=" + (ready != null) + " shoot=" + (shoot != null));

        // ---- 2. animator controller: Ready <-> Shoot (trigger) ----
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(CtrlPath);
        ctrl.AddParameter("Shoot", AnimatorControllerParameterType.Trigger);
        var sm = ctrl.layers[0].stateMachine;
        var sReady = sm.AddState("Ready"); sReady.motion = ready;
        var sShoot = sm.AddState("Shoot"); sShoot.motion = shoot;
        sm.defaultState = sReady;
        var toShoot = sReady.AddTransition(sShoot);
        toShoot.hasExitTime = false; toShoot.duration = 0.2f;
        toShoot.AddCondition(AnimatorConditionMode.If, 0, "Shoot");
        var toReady = sShoot.AddTransition(sReady);
        toReady.hasExitTime = true; toReady.exitTime = 0.96f; toReady.duration = 0.25f;
        AssetDatabase.SaveAssets();

        // ---- 3. place in the scene, adjacent lane, facing the targets (-Z) ----
        var sc = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);

        var gun = GameObject.Find("AirPistol_Lane19");
        float x19 = gun != null ? gun.transform.position.x : 0.5f;
        // firing-line Z: prefer the player rig, else the gun.
        float zLine = 11.45f;
        foreach (var n in new[] { "OVRCameraRig", "XR Origin", "XROrigin", "CameraRig", "Player" })
        { var g = GameObject.Find(n); if (g != null) { zLine = g.transform.position.z; break; } }
        if (gun != null && zLine == 11.45f) zLine = gun.transform.position.z + 1.0f;

        var fbxObj = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbxObj);
        inst.name = "FellowShooter";
        // one lane to -X (the side the established POV looks down); feet on the floor (y=0).
        inst.transform.position = new Vector3(x19 - 1.0f, 0f, zLine);
        inst.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // model forward +Z -> scene -Z (targets)

        var anim = inst.GetComponent<Animator>();
        if (anim == null) anim = inst.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var fs = inst.GetComponent<FellowShooter>();
        if (fs == null) fs = inst.AddComponent<FellowShooter>();
        fs.Rig = anim;

        var b = GetWorldBounds(inst);
        Debug.Log("[FS] placed at " + inst.transform.position.ToString("F2") +
                  " lane19x=" + x19.ToString("F2") + " zLine=" + zLine.ToString("F2") +
                  " height=" + b.size.y.ToString("F2") + " center=" + b.center.ToString("F2"));

        EditorSceneManager.MarkSceneDirty(sc);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[FS] DONE - scene saved");
    }

    static Bounds GetWorldBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }
}
