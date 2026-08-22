using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SpatialTracking;

/// <summary>
/// Brings output/range_10m into the Quest app: URP (which RangeSetup's material
/// fix-up requires), the model import settings the range's own README specifies,
/// and a scene with the player standing at firing point 23.
/// </summary>
public static class ArcheryRangeSetup
{
    const string FbxPath = "Assets/Models/range_10m.fbx";
    const string SettingsFolder = "Assets/Settings";
    const string UrpAssetPath = SettingsFolder + "/QuestURP.asset";
    const string RendererPath = SettingsFolder + "/QuestRenderer.asset";
    const string LightingPath = SettingsFolder + "/RangeLighting.lighting";
    const string ScenePath = "Assets/Scenes/Range.unity";

    // Firing point 23. The hall runs Z 0..30 with the targets at Z=0 and the
    // firing bench at Z=10.5, so the shooter stands at +11.75 and looks down -Z.
    // This was -11.75 with identity rotation, which put the player ~12 m behind
    // the back wall facing away from the room — nothing to see but empty space.
    // X is unchanged: lane 23 of 40 across a 39.5 m spread lands at +2.5.
    static readonly Vector3 SpawnPosition = new Vector3(2.5f, 0f, 11.75f);

    // Identity faces +Z, i.e. away from the targets, so the rig is turned round.
    static readonly Quaternion SpawnRotation = Quaternion.Euler(0f, 180f, 0f);

    // ------------------------------------------------------------------ URP

    [MenuItem("Quest/2. Set Up URP (Mobile)")]
    public static void SetupUrp()
    {
        EnsureFolder(SettingsFolder);

        var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (existing != null)
        {
            AssignPipeline(existing);
            Debug.Log("[Range] URP asset already present; re-assigned.");
            return;
        }

        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(rendererData, RendererPath);

        var urp = UniversalRenderPipelineAsset.Create(rendererData);

        // Mobile-shaped defaults. The glass wall needs opaque texture off but
        // depth on; shadow cascades cost more than they buy in a lit-by-bake room.
        urp.msaaSampleCount = 4;
        urp.supportsHDR = false;
        urp.supportsCameraDepthTexture = true;
        urp.supportsCameraOpaqueTexture = false;
        urp.shadowDistance = 25f;
        urp.shadowCascadeCount = 1;

        AssetDatabase.CreateAsset(urp, UrpAssetPath);
        AssetDatabase.SaveAssets();

        AssignPipeline(urp);
        Debug.Log("[Range] URP asset created and assigned (MSAA 4x, HDR off, 1 shadow cascade).");
    }

    static void AssignPipeline(UniversalRenderPipelineAsset urp)
    {
        GraphicsSettings.defaultRenderPipeline = urp;
        QualitySettings.renderPipeline = urp;
        AssetDatabase.SaveAssets();
    }

    // --------------------------------------------------------------- Import

    [MenuItem("Quest/3. Import Range Model")]
    public static void ConfigureModelImport()
    {
        var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[Range] No model importer at {FbxPath}. Is the FBX in Assets/Models/?");
            return;
        }

        // These four come straight from output/range_10m/unity/README.md.
        importer.globalScale = 1f;
        importer.useFileUnits = true;                                  // Convert Units
        importer.generateSecondaryUV = true;                           // required before any bake
        importer.importNormals = ModelImporterNormals.Import;          // the hall is deliberately faceted

        importer.importCameras = false;
        importer.importLights = false;                                 // FBX lights are unusable; RangeSetup builds them
        importer.isReadable = false;                                   // saves memory; nothing reads the mesh at runtime

        // External material location makes Unity write real .mat assets next to
        // the model, which is what RangeSetup's fix-up needs to edit.
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.materialLocation = ModelImporterMaterialLocation.External;

        importer.SaveAndReimport();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (model == null)
        {
            Debug.LogError("[Range] Model failed to import.");
            return;
        }

        var bounds = MeasureBounds(model);
        Debug.Log($"[Range] Imported. Bounds {bounds.size.x:F1} x {bounds.size.y:F1} x {bounds.size.z:F1} m " +
                  "(the README's round-trip figure is 42 x 30 x 6).");
    }

    static Bounds MeasureBounds(GameObject model)
    {
        var renderers = model.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0) return new Bounds();

        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    // ---------------------------------------------------------------- Scene

    [MenuItem("Quest/4. Build Range Scene")]
    public static void BuildRangeScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (model == null)
        {
            Debug.LogError($"[Range] {FbxPath} not found. Run 'Import Range Model' first.");
            return;
        }

        var hall = (GameObject)PrefabUtility.InstantiatePrefab(model);
        hall.name = "Range10m";
        hall.transform.position = Vector3.zero;

        // Static flags are what make the meshes eligible for the lightmap bake
        // and for static batching. Without ContributeGI the bake produces nothing.
        foreach (var renderer in hall.GetComponentsInChildren<MeshRenderer>())
        {
            GameObjectUtility.SetStaticEditorFlags(renderer.gameObject,
                StaticEditorFlags.ContributeGI |
                StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.OccludeeStatic);
        }

        AddCameraRig();

        EnsureFolder("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Materials, lamps and colliders come from the model's own generated
        // script, so they cannot drift from the hall geometry.
        EditorApplication.ExecuteMenuItem("Tools/Range/Fix Materials (URP)");
        EditorApplication.ExecuteMenuItem("Tools/Range/Build Ceiling Lights");
        EditorApplication.ExecuteMenuItem("Tools/Range/Add Colliders");

        ApplyLightingSettings();

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        SetAsOnlyBuildScene();
        Debug.Log($"[Range] Scene built and saved to {ScenePath}.");
    }

    static void AddCameraRig()
    {
        var guids = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
        if (guids.Length == 0)
        {
            Debug.LogWarning("[Range] OVRCameraRig prefab not found; add one via Meta > Tools > Building Blocks.");
            return;
        }

        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        rig.transform.position = SpawnPosition;
        rig.transform.rotation = SpawnRotation;

        // OVRCameraRig is supposed to pose its own anchors from OVRPlugin, but on
        // this build the stereo display comes up while the view stays locked — the
        // anchors never move. A TrackedPoseDriver on the centre-eye camera reads
        // the pose from Unity's XR input subsystem instead, which works whichever
        // provider owns the session. Without this, head tracking does not work at
        // all, and a scene rebuilt through this method loses it silently.
        var centreEye = rig.GetComponentsInChildren<Transform>(true)
                           .FirstOrDefault(t => t.name == "CenterEyeAnchor");
        if (centreEye == null)
        {
            Debug.LogWarning("[Range] No CenterEyeAnchor on the rig; head tracking will not work.");
        }
        else if (centreEye.GetComponent<TrackedPoseDriver>() == null)
        {
            var tpd = centreEye.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice,
                              TrackedPoseDriver.TrackedPose.Center);
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            Debug.Log("[Range] TrackedPoseDriver added to CenterEyeAnchor.");
        }

        // Floor Level means the tracked origin sits on the physical floor and the
        // headset lands at the player's real standing height against the model.
        var manager = rig.GetComponentInChildren<OVRManager>();
        if (manager != null)
        {
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
        }
        else
        {
            Debug.LogWarning("[Range] No OVRManager on the rig; set Tracking Origin Type to Floor Level by hand.");
        }

        Debug.Log($"[Range] Player placed at firing point 23 {SpawnPosition}, facing downrange.");
    }

    static void ApplyLightingSettings()
    {
        // The README is emphatic here: the default resolution of 40 texels/unit
        // over ~2,500 m2 of surface bakes for hours and blows the memory budget.
        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingPath);
        if (settings == null)
        {
            settings = new LightingSettings { name = "RangeLighting" };
            EnsureFolder(SettingsFolder);
            AssetDatabase.CreateAsset(settings, LightingPath);
        }

        settings.bakedGI = true;
        settings.realtimeGI = false;

        // CPU, not GPU. The GPU lightmapper needs an OpenCL device, which is not
        // available in a headless batch-mode run -- and when it is missing,
        // Lightmapping.Bake() returns silently having baked nothing.
        settings.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
        settings.lightmapResolution = 3f;
        settings.lightmapMaxSize = 2048;
        settings.lightmapCompression = LightmapCompression.NormalQuality;
        settings.ao = true;

        Lightmapping.lightingSettings = settings;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        Debug.Log("[Range] Lighting settings: Progressive CPU, 3 texels/unit, max 2048, AO on.");
    }

    static void SetAsOnlyBuildScene()
    {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    // ------------------------------------------------------------------ Bake

    [MenuItem("Quest/5. Bake Lighting (slow)")]
    public static void BakeLighting()
    {
        EditorSceneManager.OpenScene(ScenePath);
        ApplyLightingSettings();

        Debug.Log("[Range] Baking. The lamps and the emissive ceiling panels are baked-only, " +
                  "so the hall renders dark until this finishes.");
        Lightmapping.Bake();

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());

        // Bake() reports nothing on failure, so confirm lightmaps actually exist
        // rather than trusting that it ran.
        int maps = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;
        if (maps == 0)
        {
            Debug.LogError("[Range] Bake produced NO lightmaps. The hall will render dark. " +
                           "Check the log above for the lightmapper's reason.");
            return;
        }

        var first = LightmapSettings.lightmaps[0].lightmapColor;
        string dims = first != null ? $"{first.width}x{first.height}" : "unknown size";
        Debug.Log($"[Range] Bake complete: {maps} lightmap(s), {dims}.");
    }

    // ------------------------------------------------------------- Orchestrate

    /// <summary>Whole setup in one editor launch, for headless runs.</summary>
    public static void ConfigureAll()
    {
        QuestProjectSetup.Configure();
        QuestXRSetup.EnableOculusLoader();
        SetupUrp();
        ConfigureModelImport();
        BuildRangeScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Range] ConfigureAll complete.");
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/');
        var leaf = Path.GetFileName(path);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
