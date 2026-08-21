using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates a real bitmap app icon and assigns it for Android.
///
/// Unity ships an adaptive icon (res/Qu.xml, an anydpi XML). Horizon builds its
/// Library tiles from a rasterised bitmap, and when it cannot resolve one the
/// tile renders with no art and no name — which is what "App name unavailable"
/// with no visible entry looks like.
///
/// The icon is drawn rather than imported so the repo needs no binary asset: a
/// 10 m air-rifle target, black rings on white, which is what the hall is for.
/// </summary>
public static class QuestAppIcon
{
    const string IconDir = "Assets/Icons";
    const string IconPath = IconDir + "/app_icon.png";

    public static void Apply()
    {
        GenerateIcon();
        AssignIcon();
    }

    static void GenerateIcon()
    {
        const int size = 512;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var centre = new Vector2(size / 2f, size / 2f);

        // Ring radii as a fraction of the icon, outermost first.
        float[] rings = { 0.46f, 0.40f, 0.34f, 0.28f, 0.22f, 0.16f, 0.10f, 0.05f };

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), centre) / size;

                // Outside the face: a dark surround so the tile has a solid edge.
                Color c;
                if (d > 0.48f) c = new Color(0.12f, 0.14f, 0.13f, 1f);
                else
                {
                    // White face; the inner rings of a 10 m target are black.
                    c = Color.white;
                    if (d < rings[3]) c = new Color(0.09f, 0.09f, 0.09f, 1f);

                    // Ring lines.
                    foreach (var r in rings)
                    {
                        if (Mathf.Abs(d - r) < 0.006f)
                        {
                            c = d < rings[3] ? Color.white : new Color(0.09f, 0.09f, 0.09f, 1f);
                            break;
                        }
                    }
                }
                tex.SetPixel(x, y, c);
            }
        }

        tex.Apply();
        Directory.CreateDirectory(IconDir);
        File.WriteAllBytes(IconPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceUpdate);

        // Icons must be uncompressed and readable to be assigned.
        if (AssetImporter.GetAtPath(IconPath) is TextureImporter ti)
        {
            ti.textureType = TextureImporterType.Default;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
        }

        Debug.Log($"[Icon] generated {IconPath} ({size}x{size})");
    }

    static void AssignIcon()
    {
        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (icon == null) { Debug.LogError($"[Icon] could not load {IconPath}"); return; }

        // The per-kind PlatformIcon API moved namespaces across Unity versions;
        // this older call is stable and sets the legacy bitmap icons, which are
        // the ones Horizon can actually rasterise for a Library tile.
        var sizes = PlayerSettings.GetIconSizesForTargetGroup(BuildTargetGroup.Android);
        var textures = new Texture2D[sizes.Length];
        for (int i = 0; i < sizes.Length; i++) textures[i] = icon;
        PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Android, textures);
        Debug.Log($"[Icon] assigned icon to {sizes.Length} Android slot(s): {string.Join(",", sizes)}");

        AssetDatabase.SaveAssets();
        Debug.Log("[Icon] done.");
    }
}
