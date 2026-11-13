using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Player-POV tour of the populated range (60 s). Looks down the line left & right (neighbours
// fire -> recoil, crack, their SIUS monitor registers the shot), glances at the player's own
// monitor, turns to the AUDIENCE and pans across the clapping crowd, then returns to the line
// and takes two aimed shots (muzzle kick + own monitor ticks over). Head-bob throughout. The
// rigged front-row spectators are driven here (Animator doesn't run in batch) so the crowd moves.
public static class RenderPovTour
{
    const string Scene = "Assets/Scenes/Range.unity";
    const string FbxFS = "Assets/Models/FellowShooter/Shooter.fbx";
    const int W = 1280, H = 720;
    const float REST_WX = 48f;

    const int fps = 24;
    const float T = 60.0f;

    // camera keyframes: time, yaw(deg; 0/360=+Z behind=audience, 180=-Z downrange, 90=+X left, 270=-X right), pitch(+down)
    static readonly float[,] KEY = {
        {0.0f,180f, 4f},{2.0f,180f, 4f},
        {4.5f,100f, 2f},{8.0f, 92f, 3f},           // look left down the line
        {11.0f,268f,2f},{14.8f,262f,3f},           // sweep right (through downrange)
        {17.0f,165f,24f},{19.0f,165f,24f},         // glance down at my own monitor
        {22.5f,335f,-3f},                          // turn round to the audience (right stands)
        {26.0f,350f,-8f},{30.0f,372f,-8f},         // pan across the crowd, up at the back rows
        {34.0f,388f,-2f},{37.5f,360f,-5f},         // front rows (clapping), settle on centre
        {40.5f,180f, 8f},{42.5f,180f,11f},         // turn back downrange to my target
        {44.0f,180f,12f},{46.5f,180f,11f},         // aim + fire 1
        {48.5f, 95f, 3f},{50.5f, 95f, 3f},         // quick look left (a neighbour fires)
        {52.5f,180f,11f},{54.0f,180f,12f},         // back on target, aim + fire 2
        {56.5f,180f,11f},{58.0f,330f,-5f},         // glance to the applauding crowd
        {60.0f,348f,-6f},
    };

    static readonly float[] PLAYER_FIRES = { 44.6f, 54.6f };

    // neighbour shots: lane, time-of-report. Visible ones show recoil; all log a crack + monitor hit.
    static readonly (int lane, float t)[] SHOTS = {
        (18, 5.5f), (17, 7.3f), (20, 12.0f), (21, 13.5f), (30, 12.8f),
        (32, 24.0f), (5, 26.0f), (18, 49.2f), (20, 55.5f), (22, 33.0f),
    };

    static readonly string[] IDLE = { "SitIdleA", "SitIdleB", "SitIdleC", "SitIdleD", "SitIdleE" };

    static List<GameObject> shooters = new List<GameObject>();
    static List<ShotMonitor> monitors = new List<ShotMonitor>();
    static List<Animator> specs = new List<Animator>();
    static List<float> specPhase = new List<float>();
    static Dictionary<int, GameObject> byLane = new Dictionary<int, GameObject>();
    static Dictionary<GameObject, AnimationClip> shootClip = new Dictionary<GameObject, AnimationClip>();
    static Camera cam;
    static Vector3 headPos;
    static Transform pistol;
    static Vector3 pistolBenchPos; static Quaternion pistolBenchRot;

    static void Prime()
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        shooters.Clear(); shootClip.Clear(); byLane.Clear();
        var parent = GameObject.Find("FellowShooters");
        AnimationClip shoot = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(FbxFS))
        { var c = o as AnimationClip; if (c != null && c.name == "FS_Shoot") shoot = c; }
        if (parent != null)
            foreach (Transform t in parent.transform)
            {
                var go = t.gameObject; shooters.Add(go);
                int ln; if (int.TryParse(go.name.Replace("FS_Lane", ""), out ln)) byLane[ln] = go;
                var an = go.GetComponent<Animator>();
                if (an != null) { an.cullingMode = AnimatorCullingMode.AlwaysAnimate; an.Rebind(); }
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                { smr.updateWhenOffscreen = true; smr.forceMatrixRecalculationPerRender = true; }
                shootClip[go] = shoot;
            }

        // rigged front-row spectators: drive their animators ourselves (batch won't run Update)
        specs.Clear(); specPhase.Clear();
        var aud = GameObject.Find("Audience");
        if (aud != null)
            foreach (var an in aud.GetComponentsInChildren<Animator>(true))
            {
                if (an.runtimeAnimatorController == null) continue;
                an.cullingMode = AnimatorCullingMode.AlwaysAnimate; an.Rebind();
                foreach (var smr in an.GetComponentsInChildren<SkinnedMeshRenderer>())
                { smr.updateWhenOffscreen = true; smr.forceMatrixRecalculationPerRender = true; }
                specs.Add(an);
                specPhase.Add((specs.Count * 0.61803f) % 1f);
            }

        // monitors: seed each lane's display (shot list not serialized, rebuild here)
        monitors.Clear();
        var monParent = GameObject.Find("FiringPointMonitors");
        if (monParent != null)
            foreach (Transform t in monParent.transform)
            {
                var m = t.GetComponent<ShotMonitor>(); if (m == null) continue;
                monitors.Add(m); m.EnsureInit(); m.ClearShots();
                SeedMonitor(m);
            }

        var gun = GameObject.Find("AirPistol_Lane19");
        float x19 = gun != null ? gun.transform.position.x : 1.5f;
        headPos = new Vector3(x19, 1.60f, 11.45f);
        if (gun != null) { pistol = gun.transform; pistolBenchPos = pistol.position; pistolBenchRot = pistol.rotation; }
        var cgo = new GameObject("POVCam"); cam = cgo.AddComponent<Camera>();
        cam.nearClipPlane = 0.02f; cam.farClipPlane = 200f; cam.fieldOfView = 82f; cam.aspect = (float)W / H; cam.allowHDR = true;
    }

    static void SeedMonitor(ShotMonitor m)
    {
        bool occupied = byLane.ContainsKey(m.Lane) || m.Lane == 19;
        if (occupied)
        {
            int k = 4 + (m.Lane * 7) % 4;
            for (int i = 0; i < k; i++) m.GenShot(-600f + i * 25f);
        }
        foreach (var s in SHOTS) if (s.lane == m.Lane) m.GenShot(s.t);
        if (m.Lane == 19) foreach (var tf in PLAYER_FIRES) m.GenShot(tf);
    }

    static void DrawMonitors(float t) { for (int i = 0; i < monitors.Count; i++) monitors[i].RenderAtTime(t); }

    static bool Applause(float t) =>
        (t >= 21.5f && t <= 40.5f) || (t >= 44.6f && t <= 47.5f) || (t >= 54.6f && t <= 60f);

    static void PoseSpectators(float t)
    {
        bool clapAll = Applause(t);
        for (int i = 0; i < specs.Count; i++)
        {
            bool clap = clapAll && ((i * 7 + 3) % 10 < 7);
            string st = clap ? "SitClap" : IDLE[i % 5];
            float speed = clap ? 1.7f : 0.55f;
            float nt = Mathf.Repeat(t * speed + specPhase[i], 1f);
            var a = specs[i]; a.Play(st, 0, nt); a.Update(0f);
        }
    }

    static void RestPose(GameObject go, float headPhase)
    {
        var an = go.GetComponent<Animator>();
        if (an == null || !shootClip.ContainsKey(go) || shootClip[go] == null) return;
        an.Play("Shoot", 0, 0f); an.Update(0f);
        var hand = an.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand != null) hand.rotation = Quaternion.AngleAxis(REST_WX, Vector3.right) * hand.rotation;
        float pitch = 20f + 12f * Mathf.PerlinNoise(headPhase * 0.45f, 2.3f);
        float yaw = (Mathf.PerlinNoise(headPhase * 0.40f, 7.1f) - 0.5f) * 36f;
        var neck = an.GetBoneTransform(HumanBodyBones.Neck);
        if (neck != null) neck.rotation = Quaternion.AngleAxis(pitch * 0.4f, neck.right) * Quaternion.AngleAxis(yaw * 0.4f, Vector3.up) * neck.rotation;
        var head = an.GetBoneTransform(HumanBodyBones.Head);
        if (head != null) head.rotation = Quaternion.AngleAxis(pitch * 0.6f, head.right) * Quaternion.AngleAxis(yaw * 0.6f, Vector3.up) * head.rotation;
    }

    static void FirePose(GameObject go, float t, float tShot)
    {
        var an = go.GetComponent<Animator>();
        if (an == null || shootClip[go] == null) return;
        float len = shootClip[go].length;
        float ct = Mathf.Clamp(t - (tShot - 3.67f), 0f, len);
        an.Play("Shoot", 0, ct / len); an.Update(0f);
    }

    static void PoseAll(float t)
    {
        for (int i = 0; i < shooters.Count; i++)
        {
            var go = shooters[i];
            float best = -99f; bool firing = false;
            foreach (var s in SHOTS)
                if (byLane.TryGetValue(s.lane, out var fg) && fg == go)
                {
                    float c0 = s.t - 3.67f, c1 = c0 + shootClip[go].length;
                    if (t >= c0 && t <= c1) { firing = true; best = s.t; }
                }
            if (firing) FirePose(go, t, best);
            else RestPose(go, i * 3.1f + t * 0.15f);
        }
    }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    static void CamAngles(float t, out float yaw, out float pitch)
    {
        int rows = KEY.GetLength(0);
        if (t <= KEY[0, 0]) { yaw = KEY[0, 1]; pitch = KEY[0, 2]; return; }
        if (t >= KEY[rows - 1, 0]) { yaw = KEY[rows - 1, 1]; pitch = KEY[rows - 1, 2]; return; }
        for (int i = 0; i < rows - 1; i++)
            if (t >= KEY[i, 0] && t <= KEY[i + 1, 0])
            {
                float u = Smooth((t - KEY[i, 0]) / (KEY[i + 1, 0] - KEY[i, 0]));
                yaw = Mathf.Lerp(KEY[i, 1], KEY[i + 1, 1], u);
                pitch = Mathf.Lerp(KEY[i, 2], KEY[i + 1, 2], u);
                return;
            }
        yaw = 180f; pitch = 0f;
    }

    static Vector3 BobPos(float t) => new Vector3(
        (Mathf.PerlinNoise(t * 0.7f, 0.3f) - 0.5f) * 0.05f,
        (Mathf.PerlinNoise(t * 1.1f, 5.1f) - 0.5f) * 0.03f + Mathf.Sin(t * 1.6f) * 0.012f,
        (Mathf.PerlinNoise(t * 0.6f, 9.7f) - 0.5f) * 0.04f);
    static Quaternion BobRot(float t) => Quaternion.Euler(
        (Mathf.PerlinNoise(t * 0.6f, 30f) - 0.5f) * 2.4f,
        (Mathf.PerlinNoise(t * 0.5f, 20f) - 0.5f) * 3.2f,
        (Mathf.PerlinNoise(t * 0.4f, 40f) - 0.5f) * 1.3f);

    static void PlacePistol(float t)
    {
        if (pistol == null) return;
        Vector3 aimPos = new Vector3(headPos.x - 0.20f, 1.26f, 10.98f);
        Quaternion aimRot = Quaternion.Euler(-8f, 48f, -28f);     // held 3/4 hero pose (readable)
        Vector3 p = pistolBenchPos; Quaternion r = pistolBenchRot;
        foreach (float tF in PLAYER_FIRES)
        {
            float ls = tF - 3.0f, le = tF - 1.0f, dn0 = tF + 2.5f, dn1 = tF + 4.0f;
            if (t < ls || t > dn1) continue;
            float u = (t < le) ? Smooth((t - ls) / (le - ls))
                   : (t < dn0) ? 1f : 1f - Smooth((t - dn0) / (dn1 - dn0));
            p = Vector3.Lerp(pistolBenchPos, aimPos, u);
            p.y += Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI) * 0.05f;
            r = Quaternion.Slerp(pistolBenchRot, aimRot, u);
            if (u > 0.98f)
            { float s = t - le; p += new Vector3((Mathf.PerlinNoise(s * 1.3f, 2f) - 0.5f) * 0.012f, (Mathf.PerlinNoise(s * 1.1f, 8f) - 0.5f) * 0.010f, 0f); }
            if (t >= tF && t < tF + 0.5f)                          // muzzle kick
            { float a = Mathf.Exp(-(t - tF) * 13f); p += new Vector3(0f, 0.018f, 0.035f) * a; r = Quaternion.Euler(-14f * a, 0f, 0f) * r; }
            break;
        }
        pistol.position = p; pistol.rotation = r;
    }

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

    static void Frame(float t)
    {
        PoseAll(t);
        if (Mathf.Abs(Mathf.DeltaAngle(CamYaw(t), 180f)) > 65f) PoseSpectators(t); // only when crowd may be in view
        DrawMonitors(t);
        PlacePistol(t);
        CamAngles(t, out float yaw, out float pitch);
        cam.transform.position = headPos + BobPos(t);
        cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f) * BobRot(t);
    }

    static float CamYaw(float t) { CamAngles(t, out float y, out float _); return y; }

    public static void TourStills()
    {
        Prime();
        float[] ts = { 6.0f, 12.5f, 30.0f, 45.0f, 55.0f };
        string[] nm = { "left", "right", "audience", "aim", "aim2" };
        for (int k = 0; k < ts.Length; k++) { Frame(ts[k]); RenderTo(@"D:\Unity\tour_" + nm[k] + ".png"); }
        Debug.Log("[PT] STILLS DONE");
    }

    public static void Tour()
    {
        Prime();
        int N = Mathf.RoundToInt(fps * T);
        Directory.CreateDirectory(@"D:\Unity\tframes");
        foreach (var f in Directory.GetFiles(@"D:\Unity\tframes", "*.png")) File.Delete(f);
        for (int i = 0; i < N; i++)
        {
            float t = (float)i / fps;
            Frame(t);
            RenderTo(string.Format(@"D:\Unity\tframes\f{0:D4}.png", i));
            if (i % 24 == 0) Debug.Log("[PT] frame " + i + "/" + N);
        }
        Debug.Log("[PT] TOUR DONE frames=" + N);
    }
}
