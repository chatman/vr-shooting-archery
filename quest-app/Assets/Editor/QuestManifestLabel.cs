using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Gives the launcher activity a name and an icon.
///
/// Unity emits the activity with android:label and android:icon empty, putting
/// both only on &lt;application&gt;. Stock Android launchers fall back to the
/// application's values; Horizon OS does not, so the Library shows "App name
/// unavailable" and no usable tile.
///
/// Verified against the built APK before this ran:
///     application:         label='VR Shooting Archery'  icon='res/Qu.xml'
///     launchable-activity: label=''                     icon=''
///
/// Only the activity carrying the LAUNCHER intent is touched. An earlier version
/// of this script rewrote every activity in the manifest, including the Meta
/// SDK's own; that was needlessly broad, so it is scoped here.
/// </summary>
public class QuestManifestLabel : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 100;   // after the Meta SDK's manifest work

    const string Ns = "http://schemas.android.com/apk/res/android";

    public void OnPostGenerateGradleAndroidProject(string projectPath)
    {
        var manifestPath = Path.Combine(projectPath, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[Manifest] not found: {manifestPath}");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);

        var label = PlayerSettings.productName;
        const string icon = "@mipmap/app_icon";
        var patched = 0;

        foreach (XmlElement activity in doc.GetElementsByTagName("activity"))
        {
            if (!IsLauncher(activity)) continue;

            if (string.IsNullOrEmpty(activity.GetAttribute("label", Ns)))
            {
                activity.SetAttribute("label", Ns, label);
                Debug.Log($"[Manifest] android:label=\"{label}\" on launcher activity");
            }

            if (string.IsNullOrEmpty(activity.GetAttribute("icon", Ns)))
            {
                activity.SetAttribute("icon", Ns, icon);
                Debug.Log($"[Manifest] android:icon=\"{icon}\" on launcher activity");
            }

            patched++;
        }

        if (patched == 0)
        {
            Debug.LogWarning("[Manifest] no LAUNCHER activity found — Library tile will stay blank");
            return;
        }

        doc.Save(manifestPath);
        Debug.Log($"[Manifest] patched {patched} launcher activity in {manifestPath}");
    }

    static bool IsLauncher(XmlElement activity)
    {
        foreach (XmlElement filter in activity.GetElementsByTagName("intent-filter"))
            foreach (XmlElement category in filter.GetElementsByTagName("category"))
                if (category.GetAttribute("name", Ns) == "android.intent.category.LAUNCHER")
                    return true;
        return false;
    }
}
