using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Puts a 10 m air pistol on the firing bench at lane 19, grabbable by the tracked hands.
///
/// Built from primitives rather than imported, matching how the hall itself is made:
/// the repo carries no binary art and everything is reproducible from source.
///
/// Placement comes from the range's own dimensions (scripts/build_range.py):
///   bench spans z = 10 .. 11 and stands 1.0 m tall, so the top is y = 1.0
///   downrange is -Z, since the player stands facing the targets at z = 0
///
/// The generator prints lane number i+1 at LANE_X[i] = i - 19.5, i.e. lane N at
/// generator x = N - 20.5. The FBX import mirrors X, confirmed on device: a pistol
/// placed at world x = -1.5 read as firing point 22, and the generator puts lane 22 at
/// +1.5. So in world space lane N is at x = 20.5 - N, and lane 19 is x = +1.5.
///
/// The pistol therefore points -Z, grip toward the shooter, resting on the bench top.
/// Hall bounds are logged so the placement can be checked against the actual scene
/// rather than trusted.
/// </summary>
public static class AddAirPistol
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string MaterialDir = "Assets/Models/Materials";
    const string ObjectName = "AirPistol_Lane19";

    const int Lane = 19;
    const float BenchTopY = 1.0f;      // TABLE_Y
    const float BenchMidZ = 10.5f;     // between TABLE_Z0 = 10 and TABLE_Z1 = 11

    static float LaneX(int lane) => 20.5f - lane;

    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var existing = GameObject.Find(ObjectName);
        if (existing != null)
        {
            Object.DestroyImmediate(existing);
            Debug.Log("[Pistol] removed previous instance");
        }

        var gunmetal = MakeMaterial("pistol_gunmetal", new Color(0.16f, 0.17f, 0.19f), 0.85f, 0.55f);
        var grip = MakeMaterial("pistol_grip", new Color(0.19f, 0.13f, 0.09f), 0f, 0.20f);

        var root = new GameObject(ObjectName);
        root.transform.position = new Vector3(LaneX(Lane), BenchTopY, BenchMidZ);
        root.transform.rotation = Quaternion.identity;

        // Local Y is measured from the bench top, so every part sits above the surface.
        // Local -Z is downrange.
        Part(root, "Grip", gunmetal: grip,
             centre: new Vector3(0f, 0.070f, 0.060f), size: new Vector3(0.038f, 0.140f, 0.055f));
        Part(root, "Frame", gunmetal,
             centre: new Vector3(0f, 0.155f, 0.000f), size: new Vector3(0.040f, 0.050f, 0.200f));
        Part(root, "Barrel", gunmetal,
             centre: new Vector3(0f, 0.166f, -0.160f), size: new Vector3(0.028f, 0.028f, 0.220f));
        Part(root, "TriggerGuard", gunmetal,
             centre: new Vector3(0f, 0.104f, 0.014f), size: new Vector3(0.028f, 0.046f, 0.012f));
        Part(root, "RearSight", gunmetal,
             centre: new Vector3(0f, 0.188f, -0.030f), size: new Vector3(0.032f, 0.014f, 0.012f));
        Part(root, "FrontSight", gunmetal,
             centre: new Vector3(0f, 0.190f, -0.258f), size: new Vector3(0.018f, 0.018f, 0.012f));

        // One box rather than a compound of six: a compound collider on an object this
        // small jitters against the bench, and the grab does not need precise contact.
        var box = root.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.100f, -0.030f);
        box.size = new Vector3(0.050f, 0.200f, 0.480f);

        var body = root.AddComponent<Rigidbody>();
        body.mass = 0.95f;                     // a 10 m air pistol is about a kilo
        body.linearDamping = 0f;
        body.angularDamping = 0.15f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // No grab components are added here. Grabbing is the Interaction SDK's job, and it
        // is a separate step because this one rebuilds the pistol from scratch and would
        // throw the grab components away every time it ran. PistolTouchGrab.Apply does
        // that side, so run it after this.
        //
        // The rigidbody and collider above are the only prerequisites it needs.
        Selection.activeGameObject = root;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Verify(root);
    }

    static void Part(GameObject root, string name, Material gunmetal, Vector3 centre, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = centre;
        go.transform.localScale = size;

        // The parts are visual only; the root carries the single collider.
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.GetComponent<MeshRenderer>().sharedMaterial = gunmetal;

        // Dynamic object: contributing to the lightmap would bake it in place.
        GameObjectUtility.SetStaticEditorFlags(go, 0);
    }

    static Material MakeMaterial(string name, Color colour, float metallic, float smoothness)
    {
        var path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Directory.CreateDirectory(MaterialDir);
            // Standard, not URP/Lit: this project moved to the built-in pipeline.
            mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_Color", colour);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", smoothness);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    /// <summary>Check the placement against the hall actually in the scene.</summary>
    static void Verify(GameObject root)
    {
        var bounds = new Bounds();
        bool first = true;
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (r.gameObject == root || r.transform.IsChildOf(root.transform)) continue;
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }

        Debug.Log($"[Pistol] hall bounds centre={bounds.center} size={bounds.size}");
        Debug.Log($"[Pistol] placed at world {root.transform.position} " +
                  $"(lane {Lane} -> x={LaneX(Lane)}, bench top y={BenchTopY}, z={BenchMidZ})");

        bool inside = bounds.Contains(root.transform.position);
        Debug.Log(inside
            ? "[Pistol] position is inside the hall bounds."
            : "[Pistol] WARNING: position is OUTSIDE the hall bounds -- check the coordinate frame.");
    }
}
