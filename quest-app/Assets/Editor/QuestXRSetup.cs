using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// Enables the Oculus XR loader for Android. Doing this by hand means walking
/// into Project Settings > XR Plug-in Management > Android and ticking Oculus;
/// forgetting it is the usual reason a build runs but shows a black screen.
/// </summary>
public static class QuestXRSetup
{
    const string SettingsFolder = "Assets/XR";
    const string SettingsAsset = SettingsFolder + "/XRGeneralSettingsPerBuildTarget.asset";
    const string OculusLoader = "Unity.XR.Oculus.OculusLoader";

    [MenuItem("Quest/1b. Enable Oculus XR Loader (Android)")]
    public static void EnableOculusLoader()
    {
        var perBuildTarget = GetOrCreateSettings();

        perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        var settings = perBuildTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (settings == null || settings.Manager == null)
        {
            Debug.LogError("[Quest] Could not create XR settings for Android.");
            return;
        }

        if (XRPackageMetadataStore.IsLoaderAssigned(OculusLoader, BuildTargetGroup.Android))
        {
            Debug.Log("[Quest] Oculus loader already enabled for Android.");
            return;
        }

        if (XRPackageMetadataStore.AssignLoader(settings.Manager, OculusLoader, BuildTargetGroup.Android))
        {
            EditorUtility.SetDirty(settings.Manager);
            AssetDatabase.SaveAssets();
            Debug.Log("[Quest] Oculus XR loader enabled for Android.");
        }
        else
        {
            Debug.LogError("[Quest] Failed to assign the Oculus loader. Enable it manually in " +
                           "Project Settings > XR Plug-in Management > Android.");
        }
    }

    static XRGeneralSettingsPerBuildTarget GetOrCreateSettings()
    {
        EditorBuildSettings.TryGetConfigObject(
            XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perBuildTarget);

        if (perBuildTarget != null) return perBuildTarget;

        if (!AssetDatabase.IsValidFolder(SettingsFolder))
        {
            AssetDatabase.CreateFolder("Assets", "XR");
        }

        perBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
        AssetDatabase.CreateAsset(perBuildTarget, SettingsAsset);
        AssetDatabase.SaveAssets();

        // Registering as a config object is what makes Unity actually read these
        // settings at build time, rather than just leaving an orphaned asset.
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perBuildTarget, true);
        return perBuildTarget;
    }

}
