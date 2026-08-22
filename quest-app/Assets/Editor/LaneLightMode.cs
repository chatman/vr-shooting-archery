using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Splits the 35 ceiling lamps into a small realtime set over the middle lanes and a
/// baked remainder.
///
/// Why bother: the bake is one 256x256 lightmap over ~2,500 m2 of surface, so baked
/// lighting resolves at roughly one texel per 33 cm. Realtime additional lights are
/// evaluated per pixel instead, so the lamps switched here render at display resolution
/// rather than lightmap resolution.
///
/// The count is not a taste decision. URP culls additional lights per renderer, and this
/// hall is 15 enormous meshes (one per material) -- the floor alone is a single 42 x 30 m
/// renderer. Any light beyond m_AdditionalLightsPerObjectLimit silently stops affecting
/// that mesh, and URP's mobile ceiling for that limit is 8. Marking more than 8 realtime
/// would leave the surplus doing nothing.
/// </summary>
public static class LaneLightMode
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string UrpAssetPath = "Assets/Settings/QuestURP.asset";

    // Lane n sits at x = -19.5 + (n - 1), so lanes 10..25 span x = -10.5 .. +4.5.
    // The lit lamp columns are at x = -19.5, -13.5, -7.5, -1.5, +4.5, +10.5, +16.5,
    // three of which (-7.5, -1.5, +4.5) fall inside that band.
    const float BandMinX = -10.5f;
    const float BandMaxX = 4.5f;

    // URP's per-object additional light ceiling on mobile. Anything past this is wasted.
    const int RealtimeCount = 8;

    // Firing point 23, where the player actually stands; the realtime budget is spent
    // on the lamps nearest the eye rather than on whichever ones sort first.
    static readonly Vector3 FiringPoint = new Vector3(2.5f, 0f, 11.75f);

    [MenuItem("Quest/8. Realtime Lamps Over Middle Lanes")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var lamps = Object.FindObjectsByType<Light>(FindObjectsInactive.Include,
                                                    FindObjectsSortMode.None)
                          .Where(l => l.gameObject.name == "Lamp")
                          .ToArray();

        if (lamps.Length == 0)
        {
            Debug.LogError("[Lights] No lamps named 'Lamp' in the scene; nothing changed.");
            return;
        }

        // World space, so a parent transform on the lamp container cannot skew the band.
        var band = lamps
            .Where(l => l.transform.position.x >= BandMinX - 0.01f
                     && l.transform.position.x <= BandMaxX + 0.01f)
            .OrderBy(l => Vector3.Distance(
                new Vector3(l.transform.position.x, 0f, l.transform.position.z), FiringPoint))
            .ToArray();

        Debug.Log($"[Lights] {lamps.Length} lamps total; {band.Length} inside the lanes 10-25 " +
                  $"band (world x {BandMinX} .. {BandMaxX}).");

        var realtime = band.Take(RealtimeCount).ToArray();

        foreach (var l in lamps)
        {
            l.lightmapBakeType = LightmapBakeType.Baked;
            EditorUtility.SetDirty(l);
        }

        foreach (var l in realtime)
        {
            l.lightmapBakeType = LightmapBakeType.Realtime;
            EditorUtility.SetDirty(l);
            var p = l.transform.position;
            Debug.Log($"[Lights]   realtime lamp at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})");
        }

        if (band.Length > RealtimeCount)
        {
            Debug.LogWarning($"[Lights] {band.Length - RealtimeCount} lamps in the band stay baked: " +
                             $"URP only applies {RealtimeCount} additional lights per renderer, and the " +
                             "hall's meshes are too large for per-object culling to help.");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        RaisePerObjectLimit();

        Debug.Log($"[Lights] Done: {realtime.Length} realtime, {lamps.Length - realtime.Length} baked. " +
                  "Re-bake so the realtime lamps stop contributing to the lightmap.");
    }

    /// <summary>
    /// The asset ships at 4. Every realtime lamp past that silently stops lighting a given
    /// mesh, so the limit has to come up with the count.
    /// </summary>
    static void RaisePerObjectLimit()
    {
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp == null)
        {
            Debug.LogError($"[Lights] No URP asset at {UrpAssetPath}; per-object limit left alone.");
            return;
        }

        var so = new SerializedObject(urp);
        var prop = so.FindProperty("m_AdditionalLightsPerObjectLimit");
        if (prop == null)
        {
            Debug.LogError("[Lights] m_AdditionalLightsPerObjectLimit not found on the URP asset.");
            return;
        }

        int before = prop.intValue;
        prop.intValue = RealtimeCount;
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();

        Debug.Log($"[Lights] URP additional lights per object: {before} -> {prop.intValue}.");
    }
}
