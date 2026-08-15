using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Player settings Quest 3 requires. Everything here is also reachable by hand
/// in Project Settings; this just removes the clicking and the forgetting.
/// </summary>
public static class QuestProjectSetup
{
    [MenuItem("Quest/1. Configure Player Settings for Quest 3")]
    public static void Configure()
    {
        // Quest runs Android; IL2CPP + ARM64 is the only combination Meta accepts.
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Vulkan is the recommended backend on Quest 3; stripping OpenGLES stops
        // the editor silently falling back to it.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.companyName = "IshanXR";
        PlayerSettings.productName = "VR Shooting Archery";
        PlayerSettings.applicationIdentifier = "com.ishanxr.vrshootingarchery";

        // Single Pass Instanced: one draw call feeding both eyes. On a stereo
        // renderer that is close to a free halving of CPU cost.
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

        // Quest headsets are landscape-only and must never rotate.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

        AssetDatabase.SaveAssets();
        Debug.Log("[Quest] Player settings: Android / IL2CPP / ARM64 / Vulkan / minSdk 32 / Single Pass Instanced.");
    }
}
