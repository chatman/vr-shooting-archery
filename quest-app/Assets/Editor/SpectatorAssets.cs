using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Turns the three spectator FBXs and the twelve crowd textures into something the
/// audience builder can instance: import settings, materials, an animator controller and
/// three prefabs. Idempotent -- run it again after re-exporting from Blender.
///
/// THE SOURCE. Kenney's CC0 "Animated Characters: Protagonists" pack, reproportioned and
/// re-posed by scripts/build_spectator.py, which also authors the seven clips: the pack
/// itself ships idle, jump and run and nothing that resembles sitting down. The textures
/// are the pack's four skins recoloured twelve ways by scripts/recolour_spectator_skins.py,
/// because four outfits across two hundred spectators reads as a clone army.
///
/// WHY THE MATERIALS ARE "Standard". This project is on the BUILT-IN render pipeline --
/// RenderPipelineManager.currentPipeline is null and the hall's own materials are
/// Standard. A URP/Lit material here renders magenta.
///
/// WHY optimizeGameObjects IS ON. It strips the bone GameObjects out of the imported
/// hierarchy and animates the skeleton internally. Forty transforms per spectator times
/// the front rows is the single biggest cost in this crowd, and nothing in the project
/// needs to reach into a spectator's bones.
/// </summary>
public static class SpectatorAssets
{
    public const string Dir = "Assets/Models/Spectator";
    public const string RigFbx = Dir + "/spectator.fbx";
    public const string TextureDir = Dir + "/Textures";
    public const string MaterialDir = Dir + "/Materials";
    public const string ControllerPath = Dir + "/Spectator.controller";
    public const string RigPrefab = Dir + "/Spectator_Rigged.prefab";

    /// <summary>Clips that loop. The two transitions are the ones that must not.</summary>
    static readonly string[] Looping =
        { "SitIdleA", "SitIdleB", "SitIdleC", "SitIdleD", "SitIdleE", "SitClap", "StandIdle" };

    /// <summary>Act 0..5 pick these; the Spectator component sets Act at random.</summary>
    static readonly string[] SeatedStates =
        { "SitIdleA", "SitIdleB", "SitIdleC", "SitIdleD", "SitIdleE", "SitClap" };

    /// <summary>
    /// The baked poses, discovered rather than listed, so adding one to
    /// scripts/build_spectator.py and re-exporting is the whole of the change. Seated and
    /// standing are told apart by the filename, which is also how the audience builder
    /// decides which list to draw from.
    /// </summary>
    public static string[] PosePrefabs(bool seated)
    {
        var want = seated ? "spectator_sit_" : "spectator_stand_";
        var found = new List<string>();
        foreach (var path in AssetDatabase.FindAssets("t:Prefab", new[] { Dir }))
        {
            var p = AssetDatabase.GUIDToAssetPath(path);
            if (System.IO.Path.GetFileName(p).StartsWith(PrefabName(want))) found.Add(p);
        }
        found.Sort(string.CompareOrdinal);
        return found.ToArray();
    }

    static string PrefabName(string fbxStem)
    {
        // spectator_sit_folded -> Spectator_sit_folded, which is what MakePrefab writes.
        return "Spectator_" + fbxStem.Substring("spectator_".Length);
    }

    static string[] PoseFbxs()
    {
        var found = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Dir }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid);
            var stem = System.IO.Path.GetFileNameWithoutExtension(p);
            if (stem.StartsWith("spectator_sit_") || stem.StartsWith("spectator_stand_"))
            {
                found.Add(p);
            }
        }
        found.Sort(string.CompareOrdinal);
        return found.ToArray();
    }

    [MenuItem("Quest/12. Prepare the Spectators")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(MaterialDir))
        {
            AssetDatabase.CreateFolder(Dir, "Materials");
        }

        ImportRig();
        var poses = PoseFbxs();
        foreach (var p in poses) ImportStatic(p);

        var mats = BuildMaterials();
        var controller = BuildController();

        MakePrefab(RigFbx, RigPrefab, controller, mats[0]);
        foreach (var p in poses)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(p);
            MakePrefab(p, Dir + "/" + PrefabName(stem) + ".prefab", null, mats[0]);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[Spectator] " + mats.Count + " skins, " + poses.Length + " baked poses, " +
                  SeatedStates.Length + " seated acts, controller at " + ControllerPath);
    }

    // ------------------------------------------------------------------ import

    static void ImportRig()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(RigFbx);
        imp.globalScale = 1f;
        imp.useFileScale = true;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.importVisibility = false;
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        imp.meshCompression = ModelImporterMeshCompression.Medium;
        imp.isReadable = false;
        imp.animationType = ModelImporterAnimationType.Generic;
        // Setting animationType alone is not enough: avatarSetup defaults to NoAvatar
        // when the importer is driven from script, and with no avatar Unity creates no
        // Animator, ignores optimizeGameObjects and leaves all 39 bones as GameObjects.
        imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        imp.optimizeGameObjects = true;
        imp.importAnimation = true;
        imp.animationCompression = ModelImporterAnimationCompression.KeyframeReductionAndCompression;

        // defaultClipAnimations is the takes as the FBX carries them; clipAnimations is
        // the override list, and it starts empty, so it has to be seeded from the first.
        var clips = imp.defaultClipAnimations;
        foreach (var c in clips)
        {
            // Blender writes each take as "<armature>|<action>", so the clips arrive
            // called "Root|SitIdleA". Rename to the action, which is what the controller
            // and anything reading the console expect.
            int bar = c.name.LastIndexOf('|');
            if (bar >= 0) c.name = c.name.Substring(bar + 1);
            c.loopTime = System.Array.IndexOf(Looping, c.name) >= 0;
            c.loopPose = c.loopTime;
        }
        imp.clipAnimations = clips;

        EditorUtility.SetDirty(imp);
        imp.SaveAndReimport();
        Debug.Log("[Spectator] rig: " + clips.Length + " clips (" +
                  string.Join(", ", System.Array.ConvertAll(clips, c => c.name)) + ")");
    }

    static void ImportStatic(string path)
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(path);
        imp.globalScale = 1f;
        imp.useFileScale = true;
        imp.importBlendShapes = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.importVisibility = false;
        imp.importAnimation = false;
        imp.animationType = ModelImporterAnimationType.None;
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        // Not Medium here. These two come in with a x100 on the prefab root and vertex
        // coordinates a hundredth of their real size, because Blender declares the file
        // in centimetres; quantising a mesh whose bounds are 6 mm across is asking for
        // visible stepping. They are 1,030 vertices each, so uncompressed costs nothing.
        imp.meshCompression = ModelImporterMeshCompression.Off;
        imp.isReadable = false;
        EditorUtility.SetDirty(imp);
        imp.SaveAndReimport();
    }

    // ------------------------------------------------------------------ materials

    public static List<Material> BuildMaterials()
    {
        var made = new List<Material>();
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TextureDir }))
        {
            string texPath = AssetDatabase.GUIDToAssetPath(guid);

            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            // Flat-colour atlases: 512 keeps the hard edges clean and costs a quarter of
            // what the source 1024 would, twelve times over.
            if (ti.maxTextureSize != 512 || ti.alphaSource != TextureImporterAlphaSource.None)
            {
                ti.maxTextureSize = 512;
                ti.alphaSource = TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = false;
                ti.mipmapEnabled = true;
                ti.SaveAndReimport();
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            string name = System.IO.Path.GetFileNameWithoutExtension(texPath);
            string matPath = MaterialDir + "/" + name + ".mat";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.shader = Shader.Find("Standard");
            mat.mainTexture = tex;
            mat.SetFloat("_Glossiness", 0.08f);
            mat.SetFloat("_Metallic", 0f);
            // The rigid tier shares one mesh across a hundred-odd spectators; without
            // this they are a hundred-odd draw calls instead of one per material.
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            made.Add(mat);
        }
        made.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return made;
    }

    // ------------------------------------------------------------------ controller

    /// <summary>
    /// Four seated acts chosen by the int Act, with two triggers taking the spectator to
    /// its feet and back. The seated acts hand over at exitTime rather than immediately,
    /// so a spectator finishes the loop it is in rather than snapping mid-breath.
    /// </summary>
    static AnimatorController BuildController()
    {
        AssetDatabase.DeleteAsset(ControllerPath);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("Act", AnimatorControllerParameterType.Int);
        ctrl.AddParameter("Stand", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Sit", AnimatorControllerParameterType.Trigger);

        var clips = new Dictionary<string, AnimationClip>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(RigFbx))
        {
            var clip = o as AnimationClip;
            if (clip != null && !clip.name.StartsWith("__")) clips[clip.name] = clip;
        }

        var sm = ctrl.layers[0].stateMachine;
        var seated = new AnimatorState[SeatedStates.Length];
        for (int i = 0; i < SeatedStates.Length; i++)
        {
            seated[i] = sm.AddState(SeatedStates[i]);
            seated[i].motion = Get(clips, SeatedStates[i]);
        }
        var toStand = sm.AddState("SitToStand");
        toStand.motion = Get(clips, "SitToStand");
        var standing = sm.AddState("StandIdle");
        standing.motion = Get(clips, "StandIdle");
        var toSit = sm.AddState("StandToSit");
        toSit.motion = Get(clips, "StandToSit");
        sm.defaultState = seated[0];

        for (int from = 0; from < seated.Length; from++)
        {
            for (int to = 0; to < seated.Length; to++)
            {
                if (from == to) continue;
                var t = seated[from].AddTransition(seated[to]);
                t.hasExitTime = true;
                t.exitTime = 0.9f;
                t.duration = 0.4f;
                t.AddCondition(AnimatorConditionMode.Equals, to, "Act");
            }

            var s = seated[from].AddTransition(toStand);
            s.hasExitTime = false;
            s.duration = 0.2f;
            s.AddCondition(AnimatorConditionMode.If, 0f, "Stand");
        }

        var a = toStand.AddTransition(standing);
        a.hasExitTime = true;
        a.exitTime = 0.95f;
        a.duration = 0.1f;

        var b = standing.AddTransition(toSit);
        b.hasExitTime = false;
        b.duration = 0.2f;
        b.AddCondition(AnimatorConditionMode.If, 0f, "Sit");

        var c = toSit.AddTransition(seated[0]);
        c.hasExitTime = true;
        c.exitTime = 0.95f;
        c.duration = 0.15f;

        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static Motion Get(Dictionary<string, AnimationClip> clips, string name)
    {
        AnimationClip clip;
        if (clips.TryGetValue(name, out clip)) return clip;
        Debug.LogError("[Spectator] missing clip " + name + " in " + RigFbx);
        return null;
    }

    // ------------------------------------------------------------------ prefabs

    /// <summary>
    /// Wraps the imported model in a clean root and normalises it there: upright, facing
    /// its own -Z, feet on y = 0, root transform identity.
    ///
    /// This exists because the three FBXs do NOT import alike, although they came out of
    /// one Blender script through one exporter. Blender declares the files in centimetres,
    /// so Unity compensates with a x100 somewhere -- on the mesh child for the rigged
    /// model, on the ROOT for the two static ones. And the static pair arrives Z-up while
    /// the rigged one arrives Y-up. Both differences are invisible until something is
    /// a hundred times too small or lying on its back in the stand, and both of them are
    /// easy to "fix" at the point of use by assigning a scale or a rotation, which simply
    /// discards the compensation and causes the next bug. Normalising here, once,
    /// measured rather than assumed, means the placement code can set position, rotation
    /// and scale freely and never think about any of it.
    /// </summary>
    static void MakePrefab(string fbx, string prefab, AnimatorController controller, Material mat)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        if (model == null) { Debug.LogError("[Spectator] no model at " + fbx); return; }

        var inner = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(inner, PrefabUnpackMode.Completely,
                                           InteractionMode.AutomatedAction);

        var inst = new GameObject(System.IO.Path.GetFileNameWithoutExtension(prefab));
        inner.transform.SetParent(inst.transform, true);

        // A figure is much taller than it is wide, so whichever axis the bounds are
        // longest on is the one pointing at the ceiling.
        var size = LocalBounds(inst).size;
        if (size.z > size.y)
        {
            inner.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f) * inner.transform.localRotation;
        }

        // Feet on the floor plane, hips over the origin.
        var b = LocalBounds(inst);
        inner.transform.localPosition -= new Vector3(b.center.x, b.min.y, b.center.z);

        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // Probe lighting would need light probes placed through the stand; the scene
            // is lit by one realtime directional, so a flat ambient term is honest here
            // and saves the per-renderer probe lookup.
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        var spec = inst.AddComponent<Spectator>();

        if (controller != null)
        {
            // The Animator belongs on the imported model, not on the wrapper: that is the
            // transform the avatar and the clips were authored against.
            var anim = inner.GetComponent<Animator>();
            if (anim == null) anim = inner.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            spec.Rig = anim;
        }
        else
        {
            spec.Rig = null;
            spec.SwayAngle = 1.6f;
        }

        var final = LocalBounds(inst);
        PrefabUtility.SaveAsPrefabAsset(inst, prefab);
        Object.DestroyImmediate(inst);

        Debug.Log("[Spectator] " + System.IO.Path.GetFileName(prefab) + "  size " +
                  final.size.ToString("F2") + "  feet at y=" + final.min.y.ToString("F3"));
    }

    /// <summary>Renderer bounds of a hierarchy in its own root's local space.</summary>
    static Bounds LocalBounds(GameObject root)
    {
        var b = new Bounds();
        bool first = true;
        var toLocal = root.transform.worldToLocalMatrix;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var w = r.bounds;
            // Eight corners rather than the centre and extents: the world box of a rotated
            // child does not map to a local box of the same size.
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3(
                    (i & 1) == 0 ? w.min.x : w.max.x,
                    (i & 2) == 0 ? w.min.y : w.max.y,
                    (i & 4) == 0 ? w.min.z : w.max.z);
                var p = toLocal.MultiplyPoint3x4(c);
                if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }
}
