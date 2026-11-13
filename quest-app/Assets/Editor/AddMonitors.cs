using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds a small SIUS-style tablet monitor on every firing point (lanes 1..40), standing on the
// bench and angled toward the shooter, and wires each occupied lane's FellowShooter to its
// monitor so a fired shot registers on screen. ProbeOne() renders one close-up to verify the
// screen orientation/content before BuildAll().
public static class AddMonitors
{
    const string Scene = "Assets/Scenes/Range.unity";
    // Deep on the bench toward the far (downrange) edge, offset left of the lane centre, as on a
    // real firing point. Bench spans z=10 (firing line) .. z=11 (shooter side); shooter at z=11.45.
    const float Z = 10.40f;         // deeper toward the far/back edge
    const float LEFT_OFF = 0.22f;   // slightly left of centre (facing downrange, left = +X)
    const float BaseY = 1.00f;      // bench top

    static (ShotMonitor mon, GameObject root) BuildTablet(Transform parent, int lane, float x)
    {
        var root = new GameObject("Monitor_Lane" + lane);
        root.transform.SetParent(parent, false);
        root.transform.position = new Vector3(x + LEFT_OFF, BaseY, Z);

        const float tilt = -18f;                 // top leans downrange so the screen faces the shooter+up
        var tiltRot = Quaternion.Euler(tilt, 0f, 0f);
        Vector3 faceN = tiltRot * Vector3.forward; // monitor's shooter-facing normal (+Z, up)

        // dark body (bezel), tilted back
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body"; body.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.transform.localScale = new Vector3(0.235f, 0.15f, 0.02f);
        body.transform.localPosition = new Vector3(0f, 0.17f, 0f);
        body.transform.localRotation = tiltRot;
        var bm = new Material(Shader.Find("Standard")); bm.color = new Color(0.07f, 0.075f, 0.085f);
        bm.SetFloat("_Glossiness", 0.35f); body.GetComponent<Renderer>().sharedMaterial = bm;

        // a little stand/leg
        var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leg.name = "Leg"; leg.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(leg.GetComponent<Collider>());
        leg.transform.localScale = new Vector3(0.05f, 0.09f, 0.05f);
        leg.transform.localPosition = new Vector3(0f, 0.045f, 0.01f);
        leg.GetComponent<Renderer>().sharedMaterial = bm;

        // screen: a thin cube (texture shows on its shooter-facing face; no backface-cull issue),
        // sitting just proud of the body toward the shooter.
        var screen = GameObject.CreatePrimitive(PrimitiveType.Cube);
        screen.name = "Screen"; screen.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(screen.GetComponent<Collider>());
        screen.transform.localScale = new Vector3(0.205f, 0.125f, 0.006f);
        screen.transform.localPosition = new Vector3(0f, 0.17f, 0f) + faceN * 0.012f;
        screen.transform.localRotation = tiltRot;
        var sm = new Material(Shader.Find("Unlit/Texture")); screen.GetComponent<Renderer>().sharedMaterial = sm;

        var mon = root.AddComponent<ShotMonitor>();
        mon.Lane = lane; mon.Screen = screen.GetComponent<Renderer>();
        mon.EnsureInit();
        return (mon, root);
    }

    public static void ProbeOne()
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            if (go.name == "POVCam" || go.name == "MonProbe") Object.DestroyImmediate(go);

        var probe = new GameObject("MonProbe");
        var t = BuildTablet(probe.transform, 19, 1.5f);
        // seed some shots
        for (int i = 0; i < 6; i++) t.mon.GenShot(-100 + i);
        t.mon.RenderAtTime(0f);

        var cgo = new GameObject("POVCam"); var cam = cgo.AddComponent<Camera>();
        cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f; cam.fieldOfView = 40f; cam.aspect = 16f / 9f;
        // shooter stands at +Z; the screen faces +Z. Look at it from the shooter side.
        Vector3 scenter = new Vector3(1.5f, 1.17f, Z);
        Vector3 cpos = scenter + new Vector3(0.0f, 0.06f, 0.55f);
        cam.transform.position = cpos;
        cam.transform.rotation = Quaternion.LookRotation((scenter - cpos).normalized, Vector3.up);

        var rt = new RenderTexture(960, 540, 24); rt.Create(); cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt; var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply(); RenderTexture.active = null; cam.targetTexture = null;
        File.WriteAllBytes(@"D:\Unity\mon_probe.png", tex.EncodeToPNG());
        Debug.Log("[MON] PROBE DONE lane19 shots=6");
    }

    [MenuItem("Tools/Range/Build Firing-Point Monitors")]
    public static void BuildAll()
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        foreach (var go in scene.GetRootGameObjects())
            if (go.name == "FiringPointMonitors" || go.name == "POVCam" || go.name == "MonProbe") Object.DestroyImmediate(go);

        var parent = new GameObject("FiringPointMonitors");
        int wired = 0;
        for (int n = 1; n <= 40; n++)
        {
            float x = 20.5f - n;
            var t = BuildTablet(parent.transform, n, x);
            // wire shooter if present
            var fs = GameObject.Find("FS_Lane" + n);
            if (fs != null)
            {
                var comp = fs.GetComponent<FellowShooter>();
                if (comp != null) { comp.Monitor = t.mon; wired++; }
                // seed a short history so screens aren't empty
                int seed = 3 + (n % 4);
                for (int i = 0; i < seed; i++) t.mon.GenShot(-300 + i * 20);
            }
            t.mon.RenderAtTime(0f);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[MON] built 40 monitors, wired " + wired + " shooters");
        Debug.Log("[MON] BUILD DONE");
    }
}
