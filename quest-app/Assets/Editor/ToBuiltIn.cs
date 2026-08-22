using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Moves the project off URP and back onto the built-in render pipeline.
///
/// Why: a minimal control project (one cube, engine defaults) renders crisply on this
/// Quest 3 under built-in RP and softly under URP, at an identical 1680x1760 eye buffer.
/// Built-in renders straight into the XR eye swapchain; URP goes through an intermediate
/// texture and blits, and it takes that path whenever HDR, MSAA, render scale != 1,
/// post-processing or the depth/opaque textures are on. This project had MSAA 4 and
/// render scale 1.5, so it always blitted. That is the softness, not resolution.
///
/// Two things have to happen together, or the hall turns magenta: unassign the pipeline
/// asset, and remap every URP shader to its built-in equivalent.
/// </summary>
public static class ToBuiltIn
{
    public static void Apply()
    {
        UnassignPipeline();
        ConvertMaterials();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BiRP] Conversion complete. Re-bake lighting before building.");
    }

    static void UnassignPipeline()
    {
        var before = GraphicsSettings.defaultRenderPipeline != null
            ? GraphicsSettings.defaultRenderPipeline.name : "(none)";

        GraphicsSettings.defaultRenderPipeline = null;

        // Quality levels can each override the pipeline; a single leftover reference is
        // enough to keep URP active on Android and make this look like it did nothing.
        int current = QualitySettings.GetQualityLevel();
        var names = QualitySettings.names;
        for (int i = 0; i < names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            if (QualitySettings.renderPipeline != null)
            {
                Debug.Log($"[BiRP] Clearing pipeline override on quality level '{names[i]}'.");
                QualitySettings.renderPipeline = null;
            }
        }
        QualitySettings.SetQualityLevel(current, false);

        Debug.Log($"[BiRP] Render pipeline: {before} -> built-in.");
    }

    static void ConvertMaterials()
    {
        var standard = Shader.Find("Standard");
        if (standard == null)
        {
            Debug.LogError("[BiRP] Built-in 'Standard' shader not found; aborting material conversion.");
            return;
        }

        var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        int converted = 0, skipped = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null) continue;

            if (!mat.shader.name.StartsWith("Universal Render Pipeline/"))
            {
                skipped++;
                continue;
            }

            // Read the URP values before swapping the shader; the properties disappear
            // with it.
            Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            Texture baseMap = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            Vector2 scale = mat.HasProperty("_BaseMap") ? mat.GetTextureScale("_BaseMap") : Vector2.one;
            Vector2 offset = mat.HasProperty("_BaseMap") ? mat.GetTextureOffset("_BaseMap") : Vector2.zero;
            float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
            float smooth = mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.5f;
            Color emission = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
            bool transparent = mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f;
            var giFlags = mat.globalIlluminationFlags;

            mat.shader = standard;

            mat.SetColor("_Color", baseColor);
            if (baseMap != null)
            {
                mat.SetTexture("_MainTex", baseMap);
                mat.SetTextureScale("_MainTex", scale);
                mat.SetTextureOffset("_MainTex", offset);
            }
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Glossiness", smooth);

            if (emission.maxColorComponent > 0f)
            {
                mat.SetColor("_EmissionColor", emission);
                mat.EnableKeyword("_EMISSION");
            }
            else
            {
                mat.DisableKeyword("_EMISSION");
            }

            // Preserve BakedEmissive so the ceiling panels keep lighting the hall.
            mat.globalIlluminationFlags = giFlags;

            SetMode(mat, transparent);
            EditorUtility.SetDirty(mat);
            converted++;

            Debug.Log($"[BiRP]   {System.IO.Path.GetFileNameWithoutExtension(path)}: " +
                      $"{(transparent ? "transparent" : "opaque")}, metallic={metallic:0.00}, " +
                      $"smoothness={smooth:0.00}{(emission.maxColorComponent > 0f ? ", emissive" : "")}");
        }

        Debug.Log($"[BiRP] Materials: {converted} converted to Standard, {skipped} left alone.");
    }

    /// <summary>Standard's rendering mode is a set of blend states and keywords, not one field.</summary>
    static void SetMode(Material mat, bool transparent)
    {
        if (transparent)
        {
            // Fade, which is what the glazing wants: alpha scales the whole surface.
            mat.SetFloat("_Mode", 2f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
        else
        {
            mat.SetFloat("_Mode", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.One);
            mat.SetInt("_DstBlend", (int)BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = -1;
        }
    }
}
