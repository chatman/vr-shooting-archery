using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Read-only dump of the scene's camera / XR rig setup. Nothing here changes
/// anything; it exists so a headless run can answer "what is actually in the
/// scene" instead of guessing from the runtime symptoms.
/// </summary>
public static class RangeDiagnostics
{
    const string ScenePath = "Assets/Scenes/Range.unity";

    [MenuItem("Quest/9. Diagnose Scene")]
    public static void Diagnose()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var scene = SceneManager.GetActiveScene();

        Debug.Log("[Diag] ===== ROOT OBJECTS =====");
        foreach (var go in scene.GetRootGameObjects())
            Debug.Log($"[Diag] root: {go.name}  active={go.activeSelf}");

        Debug.Log("[Diag] ===== CAMERAS =====");
        var cams = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Diag] camera count = {cams.Length}");
        foreach (var c in cams)
        {
            var path = c.name;
            for (var t = c.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            Debug.Log($"[Diag] cam '{path}' enabled={c.enabled} activeInHierarchy={c.gameObject.activeInHierarchy} " +
                      $"tag={c.tag} depth={c.depth} pos={c.transform.position} rot={c.transform.eulerAngles} " +
                      $"clear={c.clearFlags} cull={c.cullingMask} near={c.nearClipPlane} far={c.farClipPlane} " +
                      $"target={(c.targetTexture == null ? "none" : c.targetTexture.name)}");

            var td = c.GetComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
            Debug.Log($"[Diag]     TrackedPoseDriver: {(td == null ? "MISSING" : "present, tracking=" + td.trackingType)}");
        }

        Debug.Log("[Diag] ===== OVR RIG =====");
        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        if (rig == null)
        {
            Debug.LogError("[Diag] No OVRCameraRig component in the scene.");
        }
        else
        {
            Debug.Log($"[Diag] OVRCameraRig on '{rig.name}' pos={rig.transform.position}");
            Debug.Log($"[Diag]   trackingSpace = {(rig.trackingSpace == null ? "NULL" : rig.trackingSpace.name)}");
            Debug.Log($"[Diag]   centerEyeAnchor = {(rig.centerEyeAnchor == null ? "NULL" : rig.centerEyeAnchor.name)}");
            Debug.Log($"[Diag]   usePerEyeCameras = {rig.usePerEyeCameras}");
        }

        var mgr = Object.FindFirstObjectByType<OVRManager>();
        Debug.Log(mgr == null
            ? "[Diag] No OVRManager in the scene — no tracking will be applied."
            : $"[Diag] OVRManager on '{mgr.name}' trackingOrigin={mgr.trackingOriginType}");

        Debug.Log("[Diag] ===== GEOMETRY =====");
        var rends = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Diag] MeshRenderer count = {rends.Length}");
        if (rends.Length > 0)
        {
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Debug.Log($"[Diag] combined bounds centre={b.center} size={b.size}");
            var noLightmap = rends.Count(r => r.lightmapIndex < 0);
            Debug.Log($"[Diag] renderers with NO lightmap index = {noLightmap} / {rends.Length}");
            foreach (var r in rends.Take(8))
            {
                var m = r.sharedMaterial;
                Debug.Log($"[Diag]   '{r.name}' mat={(m == null ? "NULL" : m.name)} " +
                          $"shader={(m == null || m.shader == null ? "NULL" : m.shader.name)} lm={r.lightmapIndex}");
            }
        }

        Debug.Log($"[Diag] lightmaps in LightmapSettings = {LightmapSettings.lightmaps.Length}");

        Debug.Log("[Diag] ===== LIGHTS =====");
        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Diag] Light count = {lights.Length}");
        foreach (var l in lights.Take(6))
        {
            Debug.Log($"[Diag]   '{l.name}' type={l.type} mode={l.lightmapBakeType} " +
                      $"intensity={l.intensity} range={l.range} enabled={l.enabled} " +
                      $"activeInHierarchy={l.gameObject.activeInHierarchy} colour={l.color}");
        }
        var realtime = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
        var baked = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked);
        var mixed = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed);
        Debug.Log($"[Diag] light modes: baked={baked} mixed={mixed} realtime={realtime}");

        Debug.Log("[Diag] ===== AMBIENT / ENV =====");
        Debug.Log($"[Diag] ambientMode={RenderSettings.ambientMode} ambientLight={RenderSettings.ambientLight} " +
                  $"ambientIntensity={RenderSettings.ambientIntensity}");
        Debug.Log($"[Diag] skybox={(RenderSettings.skybox == null ? "NULL" : RenderSettings.skybox.name)} " +
                  $"sun={(RenderSettings.sun == null ? "NULL" : RenderSettings.sun.name)}");
        Debug.Log($"[Diag] lightmapsMode={LightmapSettings.lightmapsMode}");

        // A lightmap that exists but is black is the failure the bake's own
        // count-based guard cannot see. Sample the EXR to find out which it is.
        if (LightmapSettings.lightmaps.Length > 0)
        {
            var tex = LightmapSettings.lightmaps[0].lightmapColor;
            if (tex == null)
            {
                Debug.LogError("[Diag] lightmapColor is NULL.");
            }
            else
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null && !imp.isReadable) { imp.isReadable = true; imp.SaveAndReimport(); }
                tex = LightmapSettings.lightmaps[0].lightmapColor;
                Debug.Log($"[Diag] lightmap '{path}' {tex.width}x{tex.height} format={tex.format}");
                try
                {
                    var px = tex.GetPixels();
                    float max = 0f, sum = 0f;
                    foreach (var p in px) { var v = p.r + p.g + p.b; sum += v; if (v > max) max = v; }
                    Debug.Log($"[Diag] lightmap luminance: max={max:F4} mean={(sum / px.Length):F4} samples={px.Length}");
                    if (max < 0.001f) Debug.LogError("[Diag] LIGHTMAP IS BLACK — the bake captured no light.");
                }
                catch (System.Exception e) { Debug.Log($"[Diag] could not sample lightmap: {e.Message}"); }
            }
        }

        Debug.Log("[Diag] ===== END =====");
    }
}
