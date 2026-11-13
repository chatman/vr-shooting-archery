using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Shooter POV render. Rest pose = lowered body + arm posed so the pistol lies flat on the
// bench (tunable via -sx/-wx/-wy/-wz/-lift args for RestCheck; baked into constants for Video2).
public static class RenderFellow
{
    const string Scene = "Assets/Scenes/Range.unity";
    const string Fbx   = "Assets/Models/FellowShooter/Shooter.fbx";
    const int W = 1280, H = 720;
    const float TableTop = 1.0f;

    // ---- baked rest-pose tuning (set from RestCheck results) ----
    const float REST_SX = 0f, REST_WX = 48f, REST_WY = 0f, REST_WZ = 0f, Y_LIFT = 0f, Z_STAND = 11.45f;

    static GameObject fellow;
    static Animator anim;
    static AnimationClip shootClip;
    static Camera cam;
    static Vector3 headPos;
    static Renderer[] pistolRends;

    static float Arg(string k, float d)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == k) { float v; if (float.TryParse(a[i + 1], out v)) return v; }
        return d;
    }

    static void Load(float fov)
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        fellow = GameObject.Find("FellowShooter");
        anim = fellow.GetComponent<Animator>();
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; anim.Rebind();
        foreach (var smr in fellow.GetComponentsInChildren<SkinnedMeshRenderer>())
        { smr.updateWhenOffscreen = true; smr.forceMatrixRecalculationPerRender = true; }
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Fbx))
        { var c = o as AnimationClip; if (c != null && c.name == "FS_Shoot") shootClip = c; }

        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        foreach (Transform ch in hand)
        {
            bool has = false;
            foreach (var r in ch.GetComponentsInChildren<Renderer>())
                foreach (var m in r.sharedMaterials) if (m != null && m.name.Contains("Pistol")) { has = true; break; }
            if (has) { pistolRends = ch.GetComponentsInChildren<Renderer>(); break; }
        }

        var gun = GameObject.Find("AirPistol_Lane19"); float x19 = gun != null ? gun.transform.position.x : 1.5f;
        headPos = new Vector3(x19, 1.60f, 11.45f);
        var go = new GameObject("POVCam"); cam = go.AddComponent<Camera>();
        cam.nearClipPlane = 0.03f; cam.farClipPlane = 180f; cam.fieldOfView = fov; cam.aspect = (float)W / H; cam.allowHDR = true;
    }

    static void SetLift(float lift)
    { var p = fellow.transform.position; fellow.transform.position = new Vector3(p.x, lift, p.z); }

    static void Pose(float clipTimeSec)
    { anim.Play("Shoot", 0, Mathf.Clamp01(clipTimeSec / shootClip.length)); anim.Update(0f); }

    // Rest: lowered body, then drop the hand (shoulder pitch about world X) and pitch the
    // pistol up to horizontal (hand rotation about world X, yaw about world Y).
    static void PoseRest(float sx, float wpitch, float wyaw, float unused)
    {
        Pose(0f);
        var up = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (up != null) up.rotation = Quaternion.AngleAxis(sx, Vector3.right) * up.rotation;
        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand != null) hand.rotation = Quaternion.AngleAxis(wyaw, Vector3.up) * Quaternion.AngleAxis(wpitch, Vector3.right) * hand.rotation;
    }

    // Idle head motion for the rest phases: mostly glancing down at the resting pistol, with
    // slow look-around drift. Split across neck+head so it doesn't look like a bobblehead.
    static void ApplyHeadIdle(float t)
    {
        float pitch = 20f + 12f * Mathf.PerlinNoise(t * 0.45f, 2.3f);   // look down 20..32 deg
        float yaw = (Mathf.PerlinNoise(t * 0.40f, 7.1f) - 0.5f) * 36f;  // +-18 deg look around
        var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
        if (neck != null) neck.rotation = Quaternion.AngleAxis(pitch * 0.4f, neck.right) * Quaternion.AngleAxis(yaw * 0.4f, Vector3.up) * neck.rotation;
        var head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head != null) head.rotation = Quaternion.AngleAxis(pitch * 0.6f, head.right) * Quaternion.AngleAxis(yaw * 0.6f, Vector3.up) * head.rotation;
    }

    static float PistolMinY() { float m = 9f; if (pistolRends != null) foreach (var r in pistolRends) m = Mathf.Min(m, r.bounds.min.y); return m; }
    static float PistolMaxY() { float m = -9f; if (pistolRends != null) foreach (var r in pistolRends) m = Mathf.Max(m, r.bounds.max.y); return m; }

    static void RenderTo(string path)
    {
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.antiAliasing = 4; rt.Create();
        cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
        RenderTexture.active = null; cam.targetTexture = null;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex); rt.Release();
    }

    static Quaternion LookAt(Vector3 target, Vector3 from) => Quaternion.LookRotation((target - from).normalized, Vector3.up);
    static Vector3 FellowLook() => fellow.transform.position + new Vector3(0f, 1.2f, 0f);
    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // Tunable rest-pose check: -sx -wx -wy -wz -lift -z (bench back edge is z=11; feet must stay z>11)
    public static void RestCheck()
    {
        Load(60f);
        float sx = Arg("-sx", 0), wx = Arg("-wx", 0), wy = Arg("-wy", 0), wz = Arg("-wz", 0), lift = Arg("-lift", 0f);
        float z = Arg("-z", fellow.transform.position.z);
        var p0 = fellow.transform.position; fellow.transform.position = new Vector3(p0.x, lift, z);
        PoseRest(sx, wx, wy, wz);
        ApplyHeadIdle(Arg("-t", 0.5f));

        // body (non-pistol) extents = where the feet/toes reach
        float bodyMinZ = 99f, bodyMinY = 99f, pistolMinZ = 99f, pistolMaxZ = -99f;
        foreach (var r in fellow.GetComponentsInChildren<Renderer>())
        {
            bool isP = false; foreach (var m in r.sharedMaterials) if (m != null && m.name.Contains("Pistol")) isP = true;
            if (isP) { pistolMinZ = Mathf.Min(pistolMinZ, r.bounds.min.z); pistolMaxZ = Mathf.Max(pistolMaxZ, r.bounds.max.z); }
            else { bodyMinZ = Mathf.Min(bodyMinZ, r.bounds.min.z); bodyMinY = Mathf.Min(bodyMinY, r.bounds.min.y); }
        }
        Debug.Log("[RF] REST z=" + z + " lift=" + lift + " wx=" + wx +
                  " feet_minZ=" + bodyMinZ.ToString("F3") + " (bench back=11; want >11)" +
                  " feet_minY=" + bodyMinY.ToString("F3") + " (floor=0)" +
                  " pistolMinY=" + PistolMinY().ToString("F3") + " (table=1.0)" +
                  " pistol_Z=[" + pistolMinZ.ToString("F2") + "," + pistolMaxZ.ToString("F2") + "] (bench z 10..11)");
        // low side view showing feet + bench
        Vector3 c = new Vector3(fellow.transform.position.x + 2.0f, 0.65f, fellow.transform.position.z - 1.3f);
        cam.transform.position = c; cam.fieldOfView = 55f;
        cam.transform.rotation = LookAt(new Vector3(fellow.transform.position.x, 0.4f, fellow.transform.position.z), c);
        RenderTo(@"D:\Unity\fs_rest_side.png");
        cam.transform.position = headPos; cam.fieldOfView = 82f;
        cam.transform.rotation = LookAt(FellowLook(), headPos);
        RenderTo(@"D:\Unity\fs_rest_pov.png");
        Debug.Log("[RF] restcheck done");
    }

    static Vector3 BobPos(float t) => new Vector3(
        (Mathf.PerlinNoise(t * 0.7f, 0.3f) - 0.5f) * 0.05f,
        (Mathf.PerlinNoise(t * 1.1f, 5.1f) - 0.5f) * 0.03f + Mathf.Sin(t * 1.6f) * 0.012f,
        (Mathf.PerlinNoise(t * 0.6f, 9.7f) - 0.5f) * 0.04f);
    static Quaternion BobRot(float t) => Quaternion.Euler(
        (Mathf.PerlinNoise(t * 0.6f, 30f) - 0.5f) * 2.4f,
        (Mathf.PerlinNoise(t * 0.5f, 20f) - 0.5f) * 3.2f,
        (Mathf.PerlinNoise(t * 0.4f, 40f) - 0.5f) * 1.3f);

    public static void Video2()
    {
        Load(82f);
        { var fp = fellow.transform.position; fellow.transform.position = new Vector3(fp.x, Y_LIFT, Z_STAND); }
        EditorSceneManager.MarkSceneDirty(fellow.scene);
        EditorSceneManager.SaveOpenScenes();

        const int fps = 24;
        float s1 = 3.0f, s1end = 10.0f, panOut0 = 10.0f, panOut1 = 11.5f, audHoldEnd = 13.0f,
              panBack0 = 13.0f, panBack1 = 14.5f, s2 = 16.5f, s2end = 23.5f, T = 25.0f;
        int N = Mathf.RoundToInt(fps * T);
        Quaternion rFellow = LookAt(FellowLook(), headPos);
        Quaternion rAud = LookAt(new Vector3(6.5f, 1.6f, 22f), headPos);

        Directory.CreateDirectory(@"D:\Unity\frames");
        foreach (var f in Directory.GetFiles(@"D:\Unity\frames", "*.png")) File.Delete(f);

        for (int i = 0; i < N; i++)
        {
            float tv = (float)i / fps;
            if (tv >= s1 && tv < s1end) Pose(tv - s1);
            else if (tv >= s2 && tv < s2end) Pose(tv - s2);
            else { PoseRest(REST_SX, REST_WX, REST_WY, REST_WZ); ApplyHeadIdle(tv); }

            Quaternion baseRot;
            if (tv < panOut0) baseRot = rFellow;
            else if (tv < panOut1) baseRot = Quaternion.Slerp(rFellow, rAud, Smooth((tv - panOut0) / (panOut1 - panOut0)));
            else if (tv < audHoldEnd) baseRot = rAud;
            else if (tv < panBack1) baseRot = Quaternion.Slerp(rAud, rFellow, Smooth((tv - panBack0) / (panBack1 - panBack0)));
            else baseRot = rFellow;

            cam.transform.position = headPos + BobPos(tv);
            cam.transform.rotation = baseRot * BobRot(tv);
            RenderTo(string.Format(@"D:\Unity\frames\f{0:D4}.png", i));
            if (i % 24 == 0) Debug.Log("[RF] frame " + i + "/" + N);
        }
        Debug.Log("[RF] VIDEO2 DONE frames=" + N);
    }
}
