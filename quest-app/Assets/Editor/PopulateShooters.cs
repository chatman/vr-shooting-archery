using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Clones the fellow shooter across the firing line: ~25 of the 40 lanes (never the player's
// lane 19), each with its own randomised shirt colour, shot rhythm and a 3-D shot AudioSource.
// Lane geometry (verified): lane N stands at x = 20.5 - N, z = 11.45, facing downrange (rotY 180).
public static class PopulateShooters
{
    const string Scene   = "Assets/Scenes/Range.unity";
    const string Fbx     = "Assets/Models/FellowShooter/Shooter.fbx";
    const string Ctrl    = "Assets/Models/FellowShooter/FellowShooter.controller";
    const string ShotWav = "Assets/Audio/airpistol_shot.wav";
    const string VarDir  = "Assets/Models/FellowShooter/ShirtVariants";
    const int    PlayerLane = 19;
    const int    Count      = 25;
    const int    MasterSeed = 20261113;
    const float  Z = 11.45f;

    [MenuItem("Tools/Range/Populate Fellow Shooters")]
    public static void Run()
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        // ---- clean slate: old single shooter, prior populate, stray render cameras ----
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name == "FellowShooter" || go.name == "FellowShooters" || go.name == "POVCam")
                Object.DestroyImmediate(go);
        }

        var fbx  = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Ctrl);
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ShotWav);
        if (fbx == null || ctrl == null) { Debug.LogError("[POP] missing fbx/controller"); return; }
        if (clip == null) Debug.LogWarning("[POP] shot clip not found at " + ShotWav);

        if (!AssetDatabase.IsValidFolder(VarDir))
            AssetDatabase.CreateFolder("Assets/Models/FellowShooter", "ShirtVariants");

        // ---- pick ~Count lanes at random, excluding the player's ----
        var pool = new List<int>();
        for (int n = 1; n <= 40; n++) if (n != PlayerLane) pool.Add(n);
        var rng = new System.Random(MasterSeed);
        for (int i = pool.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = pool[i]; pool[i] = pool[j]; pool[j] = t; }
        var lanes = pool.GetRange(0, Mathf.Min(Count, pool.Count));
        lanes.Sort();

        var parent = new GameObject("FellowShooters");
        var log = new StringBuilder();

        foreach (int n in lanes)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            go.name = "FS_Lane" + n;
            go.transform.SetParent(parent.transform, false);
            go.transform.position = new Vector3(20.5f - n, 0f, Z);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            go.transform.localScale = Vector3.one;

            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // ---- randomised shirt colour (saved material asset per lane) ----
            Color col = RandomShirt(rng);
            var polo = FindPolo(go, out int slot, out Material baseMat);
            if (polo != null && baseMat != null)
            {
                var mat = new Material(baseMat) { name = "M_Polo_Lane" + n };
                // The stock polo albedo is blue, which would swamp any tint; drop it so the
                // jersey takes a true solid colour, keeping the normal map for fabric folds.
                if (mat.HasProperty("_MainTex"))   mat.SetTexture("_MainTex", null);
                if (mat.HasProperty("_BaseMap"))   mat.SetTexture("_BaseMap", null);
                if (mat.HasProperty("_Color"))     mat.SetColor("_Color", col);
                if (mat.HasProperty("_BaseColor"))  mat.SetColor("_BaseColor", col);
                string path = VarDir + "/M_Polo_Lane" + n + ".mat";
                AssetDatabase.CreateAsset(mat, path);
                var mats = polo.sharedMaterials;
                mats[slot] = mat;
                polo.sharedMaterials = mats;
            }

            // ---- randomised shot rhythm + seed ----
            var fs = go.AddComponent<FellowShooter>();
            float lo = 15f + (float)rng.NextDouble() * 25f;          // 15..40 min
            float hi = lo + 8f + (float)rng.NextDouble() * 20f;       // +8..28 min
            fs.IntervalMinutes = new Vector2(lo, hi);
            fs.Seed = n * 101 + rng.Next(1000);
            fs.ShotSound = clip;

            var au = go.AddComponent<AudioSource>();
            au.playOnAwake = false; au.spatialBlend = 1f;
            au.rolloffMode = AudioRolloffMode.Logarithmic;
            au.minDistance = 1.5f; au.maxDistance = 30f; au.dopplerLevel = 0f;
            au.pitch = 0.92f + (float)rng.NextDouble() * 0.18f;        // slight per-gun variation
            au.clip = clip;
            fs.Audio = au;

            log.Append("lane ").Append(n).Append(" x=").Append((20.5f - n).ToString("F1"))
               .Append(" rgb=").Append(ColFmt(col))
               .Append(" interval=[").Append(lo.ToString("F0")).Append(",").Append(hi.ToString("F0")).Append("]min")
               .Append(" seed=").Append(fs.Seed).Append(" pitch=").Append(au.pitch.ToString("F2")).Append('\n');
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[POP] placed " + lanes.Count + " shooters on lanes: " + string.Join(",", lanes));
        Debug.Log("[POP] detail:\n" + log.ToString());
        Debug.Log("[POP] DONE");
    }

    static Color RandomShirt(System.Random rng)
    {
        // 20% neutral (white/grey/charcoal), else a saturated jersey colour.
        if (rng.NextDouble() < 0.20)
        {
            float g = 0.15f + (float)rng.NextDouble() * 0.85f;
            return new Color(g, g, g);
        }
        float h = (float)rng.NextDouble();
        float s = 0.55f + (float)rng.NextDouble() * 0.35f;
        float v = 0.60f + (float)rng.NextDouble() * 0.35f;
        return Color.HSVToRGB(h, s, v);
    }

    static string ColFmt(Color c) =>
        ((int)(c.r * 255)) + "/" + ((int)(c.g * 255)) + "/" + ((int)(c.b * 255));

    // The polo renderer carries a material whose name contains "SportPolo"; return it and its slot.
    static Renderer FindPolo(GameObject go, out int slot, out Material baseMat)
    {
        slot = -1; baseMat = null;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var ms = r.sharedMaterials;
            for (int i = 0; i < ms.Length; i++)
                if (ms[i] != null && ms[i].name.Contains("SportPolo"))
                { slot = i; baseMat = ms[i]; return r; }
        }
        return null;
    }
}
