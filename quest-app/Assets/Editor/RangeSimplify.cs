using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Strips the scene back to bare, functional lighting.
///
/// The baked pipeline has been the source of every visual problem so far: at
/// 256x256 it saturated to white, and at 1024x1024 with tuned ambient it still
/// reads dim on the headset while the emissive ceiling blows out. None of that is
/// necessary to walk around and confirm the hall is right.
///
/// So: no lightmaps, no baked GI, no 35 point lights. One directional light plus
/// bright flat ambient, which is cheap on a Quest and cannot fail silently.
/// </summary>
public static class RangeSimplify
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string LightingPath = "Assets/Settings/RangeLighting.lighting";

    public static void Simplify()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        // 1. Throw away the bake. Renderers fall back to ambient + realtime lights.
        Lightmapping.Clear();
        Lightmapping.ClearDiskCache();
        Lightmapping.ClearLightingDataAsset();
        Debug.Log("[Simple] cleared baked lightmaps and lighting data");

        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingPath);
        if (settings != null)
        {
            settings.bakedGI = false;
            settings.realtimeGI = false;
            Lightmapping.lightingSettings = settings;
            EditorUtility.SetDirty(settings);
            Debug.Log("[Simple] baked GI off, realtime GI off");
        }

        // 2. The 35 lamps were baked-only. With no bake they contribute nothing,
        //    and making them realtime would blow the Quest's light budget.
        var existing = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int disabled = 0;
        foreach (var l in existing)
        {
            if (l.type == LightType.Directional) continue;
            l.gameObject.SetActive(false);
            disabled++;
        }
        Debug.Log($"[Simple] disabled {disabled} baked point lamps");

        // 3. One directional light gives shape to the geometry so surfaces read as
        //    surfaces rather than flat colour.
        var sunGo = GameObject.Find("Simple Sun");
        if (sunGo == null) sunGo = new GameObject("Simple Sun");
        var sun = sunGo.GetComponent<Light>();
        if (sun == null) sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.98f, 0.95f);
        sun.intensity = 1.1f;
        sun.shadows = LightShadows.None;      // no shadow cost on mobile
        sun.lightmapBakeType = LightmapBakeType.Realtime;
        sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        Debug.Log($"[Simple] directional light intensity {sun.intensity}");

        // 4. Bright, flat ambient so nothing is ever black, whatever it faces.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.62f, 0.63f, 0.65f);
        RenderSettings.sun = sun;
        RenderSettings.fog = false;
        Debug.Log("[Simple] ambient = flat 0.62 grey, fog off");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Simple] done. lightmaps now = {LightmapSettings.lightmaps.Length}");

        RangeRender.RenderViews();
    }
}
