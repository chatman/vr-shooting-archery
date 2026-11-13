// Editor-only batch screenshot of the Range scene from the shooter's POV, looking left.
// Run: Unity.exe -batchmode -projectPath <proj> -executeMethod ScreenshotTool.Capture -logFile - -quit
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ScreenshotTool
{
    const int W = 1920, H = 1080;
    const string OUT = @"D:\Unity\range_left.png";

    public static void Capture()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/Range.unity", OpenSceneMode.Single);
        Debug.Log("[SHOT] opened scene: " + scene.name);

        // Anchor: the active firing point (gun) marks the shooter's lane; fall back to a camera.
        Vector3 fp;
        var gun = GameObject.Find("AirPistol_Lane19");
        Camera mainCam = Camera.main;
        if (mainCam == null)
        {
            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            if (cams != null && cams.Length > 0) mainCam = cams[0];
        }
        if (gun != null) { fp = gun.transform.position; Debug.Log("[SHOT] gun@ " + fp.ToString("F2")); }
        else if (mainCam != null) { fp = mainCam.transform.position; Debug.Log("[SHOT] cam anchor@ " + fp.ToString("F2")); }
        else { fp = new Vector3(0.5f, 1.6f, -10.8f); Debug.Log("[SHOT] FALLBACK anchor"); }

        // Shooter stands at the firing line (eye ~1.6 m), faces downrange (+Z toward targets at z~0).
        // His LEFT is -X. Look down the row of firing points, tilted slightly down to show benches/lane numbers.
        Vector3 eye = new Vector3(fp.x, 1.6f, fp.z);
        Vector3 lookDir = new Vector3(-1f, -0.12f, 0f).normalized;

        var go = new GameObject("BatchShotCam");
        var cam = go.AddComponent<Camera>();
        if (mainCam != null) { cam.clearFlags = mainCam.clearFlags; cam.backgroundColor = mainCam.backgroundColor; }
        cam.transform.position = eye;
        cam.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        cam.fieldOfView = 75f;            // vertical; ~104 deg horizontal at 16:9 (Quest-like wide)
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 140f;
        cam.aspect = (float)W / H;
        cam.allowHDR = true;

        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 4;
        rt.Create();
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;

        File.WriteAllBytes(OUT, tex.EncodeToPNG());
        Debug.Log("[SHOT] eye=" + eye.ToString("F2") + " look=" + lookDir.ToString("F2") + " wrote " + OUT);
    }
}
