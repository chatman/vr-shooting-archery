using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders the baked scene to PNGs from the player's spawn point, so the hall can
/// be inspected without a headset. Run WITHOUT -nographics or the render is blank.
/// </summary>
public static class RangeRender
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    public static void RenderViews()
    {
        EditorSceneManager.OpenScene(ScenePath);

        var outDir = System.Environment.GetEnvironmentVariable("RANGE_RENDER_DIR");
        if (string.IsNullOrEmpty(outDir)) outDir = "Builds/Renders";
        Directory.CreateDirectory(outDir);

        var rig = GameObject.Find("OVRCameraRig");
        var origin = rig != null ? rig.transform.position : new Vector3(0f, 0f, 15f);

        // Eye height above the floor-level tracking origin, so the render matches
        // roughly what a standing player sees.
        var eye = origin + new Vector3(0f, 1.65f, 0f);

        var camGo = new GameObject("__RenderCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 90f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 1000f;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.transform.position = eye;

        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        var shot = new Texture2D(1280, 720, TextureFormat.RGB24, false);

        var angles = new[] { 0f, 90f, 180f, 270f };
        var names = new[] { "forward_+Z", "right_+X", "back_-Z", "left_-X" };

        for (int i = 0; i < angles.Length; i++)
        {
            cam.transform.rotation = Quaternion.Euler(0f, angles[i], 0f);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            shot.Apply();
            RenderTexture.active = null;

            var path = Path.Combine(outDir, $"view_{i}_{names[i]}.png");
            File.WriteAllBytes(path, shot.EncodeToPNG());

            // Report mean brightness so a blown-out or black render is obvious
            // from the log alone, without opening the file.
            var px = shot.GetPixels();
            float sum = 0f, max = 0f;
            foreach (var p in px) { var v = (p.r + p.g + p.b) / 3f; sum += v; if (v > max) max = v; }
            Debug.Log($"[Render] {names[i]}: mean={(sum / px.Length):F3} max={max:F3} -> {path}");
        }

        cam.targetTexture = null;
        Object.DestroyImmediate(camGo);
        Debug.Log($"[Render] Done. Camera at {eye}, 90 deg FOV.");
    }
}
