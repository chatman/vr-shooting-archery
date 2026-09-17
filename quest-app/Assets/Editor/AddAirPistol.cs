using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Puts a 10 m match air pistol on the firing bench at lane 19.
///
/// Built from primitives rather than imported, matching how the hall itself is made:
/// the repo carries no binary art and everything is reproducible from source.
///
/// Shaped to the ISSF 10 m Air Pistol rules, because the rules are what make the thing
/// hold and aim like a match pistol rather than like a prop:
///
///   - the whole pistol fits a 420 x 200 x 50 mm box
///   - it weighs under 1500 g (a real one is around 950-1000 g, which is what MassKg uses)
///   - the heel rest sits at not less than 90 degrees to the grip
///
/// The grip is RAKED, and that is the part that matters for VR. The first version of this
/// script hung a vertical box under the frame, 90 degrees to the bore. No pistol is built
/// that way: a vertical grip forces the wrist into flexion to bring the sights level, so
/// in the headset it reads as holding a brick. Real grip-to-bore angles run about 100-115
/// degrees, and match air pistols sit at the top of that range with a palm shelf under the
/// heel of the hand. GripRakeDeg below is the tilt off vertical that produces it.
///
/// The bore axis is also deliberately LOW -- BarrelY is only about 45 mm above the top of
/// the grip. That is what lets the barrel sit roughly along the forearm when the arm is
/// extended, which is the whole point of a match pistol's silhouette and the reason it can
/// be aimed with a relaxed wrist.
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
/// Grabbing is a separate step: see PistolHandGrab.
/// </summary>
public static class AddAirPistol
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string MaterialDir = "Assets/Models/Materials";
    const string ObjectName = "AirPistol_Lane19";

    const int Lane = 19;
    const float BenchTopY = 1.0f;      // TABLE_Y
    const float BenchMidZ = 10.85f;    // near the shooter's edge of the bench (spans 10..11)

    /// <summary>Tilt of the grip off vertical. 17 degrees puts the grip-to-bore angle at 107.</summary>
    public const float GripRakeDeg = 22f;

    /// <summary>
    /// The grip's rotation. NEGATIVE about X on purpose: a positive X euler tilts local +Y
    /// toward +Z, which would lean the grip's top rearward and push the heel forward -- a
    /// grip raked the wrong way. Negative leans the top forward over the trigger and puts
    /// the heel at the back, which is how a pistol is actually built. Everything that needs
    /// to reason about the grip axis (PistolHandGrab included) goes through this.
    /// </summary>
    public static Quaternion GripRotation => Quaternion.Euler(-GripRakeDeg, 0f, 0f);
    /// <summary>Height of the bore axis above the bench top, in local metres.</summary>
    public const float BarrelY = 0.150f;
    /// <summary>Centre of the grip, local. PistolHandGrab anchors the hand off this.</summary>
    public static readonly Vector3 GripCentre = new Vector3(0f, 0.060f, 0.064f);
    public static readonly Vector3 GripSize = new Vector3(0.034f, 0.105f, 0.046f);

    const float MassKg = 0.96f;

    // ISSF limits, metres. Checked in Verify rather than assumed.
    const float BoxLength = 0.420f, BoxHeight = 0.200f, BoxWidth = 0.050f;

    static float LaneX(int lane) => 20.5f - lane;

    [MenuItem("Quest/9b. Build the Air Pistol")]
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
        var walnut = MakeMaterial("pistol_grip", new Color(0.19f, 0.13f, 0.09f), 0f, 0.20f);

        var root = new GameObject(ObjectName);
        root.transform.position = new Vector3(LaneX(Lane), BenchTopY, BenchMidZ);
        root.transform.rotation = Quaternion.identity;

        // Local Y is measured from the bench top, so every part sits above the surface.
        // Local -Z is downrange. The rake tilts the grip's top forward of its heel.
        var rake = GripRotation.eulerAngles;

        Part(root, "Grip", walnut, GripCentre, GripSize, rake);
        // The heel rest -- the shelf the base of the palm sits on. ISSF requires it at 90
        // degrees or more to the grip; sharing the grip's rake keeps it exactly at 90.
        Part(root, "HeelRest", walnut,
             centre: new Vector3(0f, 0.020f, 0.098f), size: new Vector3(0.046f, 0.026f, 0.050f),
             euler: rake);

        Part(root, "Frame", gunmetal,
             centre: new Vector3(0f, 0.120f, -0.012f), size: new Vector3(0.030f, 0.048f, 0.152f));
        Part(root, "Barrel", gunmetal,
             centre: new Vector3(0f, BarrelY, -0.152f), size: new Vector3(0.024f, 0.024f, 0.232f));
        // Counterweight rail under the barrel: the match-pistol silhouette, and the reason
        // the muzzle stays down rather than the whole thing pivoting in the hand.
        Part(root, "WeightRail", gunmetal,
             centre: new Vector3(0f, 0.118f, -0.150f), size: new Vector3(0.020f, 0.018f, 0.200f));

        Part(root, "TriggerGuard", gunmetal,
             centre: new Vector3(0f, 0.078f, 0.020f), size: new Vector3(0.026f, 0.044f, 0.009f));
        Part(root, "TriggerGuardBase", gunmetal,
             centre: new Vector3(0f, 0.058f, 0.042f), size: new Vector3(0.026f, 0.009f, 0.053f));
        Part(root, "Trigger", gunmetal,
             centre: new Vector3(0f, 0.084f, 0.040f), size: new Vector3(0.009f, 0.026f, 0.007f));

        // Sight radius is the distance between these two. Long is the point.
        Part(root, "RearSight", gunmetal,
             centre: new Vector3(0f, 0.170f, 0.016f), size: new Vector3(0.032f, 0.016f, 0.010f));
        Part(root, "FrontSight", gunmetal,
             centre: new Vector3(0f, 0.170f, -0.260f), size: new Vector3(0.012f, 0.016f, 0.010f));

        // One box rather than a compound of nine: a compound collider on an object this
        // small jitters against the bench, and a posed grab does not need precise contact.
        var box = root.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.090f, -0.075f);
        box.size = new Vector3(0.046f, 0.180f, 0.390f);

        var body = root.AddComponent<Rigidbody>();
        body.mass = MassKg;
        body.linearDamping = 0f;
        body.angularDamping = 0.15f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // No grab components are added here. Grabbing is the Interaction SDK's job, and it
        // is a separate step because this one rebuilds the pistol from scratch and would
        // throw the grab components away every time it ran. PistolHandGrab.Apply does that
        // side, so run it after this.
        Selection.activeGameObject = root;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Verify(root);
    }

    static void Part(GameObject root, string name, Material material, Vector3 centre, Vector3 size)
        => Part(root, name, material, centre, size, Vector3.zero);

    static void Part(GameObject root, string name, Material material, Vector3 centre, Vector3 size,
                     Vector3 euler)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = centre;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = size;

        // The parts are visual only; the root carries the single collider.
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.GetComponent<MeshRenderer>().sharedMaterial = material;

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

    /// <summary>Check the build against the ISSF box and the hall actually in the scene.</summary>
    static void Verify(GameObject root)
    {
        var local = new Bounds();
        bool first = true;
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
        {
            // Renderer bounds are world-space; the root is unrotated, so subtracting its
            // position is enough to get the local extent.
            var b = r.bounds;
            b.center -= root.transform.position;
            if (first) { local = b; first = false; }
            else local.Encapsulate(b);
        }

        Debug.Log($"[Pistol] extent (l x h x w) = {local.size.z * 1000f:F0} x {local.size.y * 1000f:F0} " +
                  $"x {local.size.x * 1000f:F0} mm  (ISSF box 420 x 200 x 50)");

        bool fits = local.size.z <= BoxLength && local.size.y <= BoxHeight && local.size.x <= BoxWidth;
        Debug.Log(fits
            ? "[Pistol] fits the ISSF measuring box."
            : "[Pistol] WARNING: does NOT fit the ISSF measuring box.");

        var rear = root.transform.Find("RearSight");
        var front = root.transform.Find("FrontSight");
        if (rear != null && front != null)
        {
            float radius = Mathf.Abs(rear.localPosition.z - front.localPosition.z);
            Debug.Log($"[Pistol] sight radius {radius * 1000f:F0} mm, bore axis {BarrelY * 1000f:F0} mm " +
                      $"above the bench, grip-to-bore angle {90f + GripRakeDeg:F0} deg");
        }

        Debug.Log($"[Pistol] mass {MassKg * 1000f:F0} g (ISSF limit 1500 g)");
        Debug.Log($"[Pistol] placed at world {root.transform.position} " +
                  $"(lane {Lane} -> x={LaneX(Lane)}, bench top y={BenchTopY}, z={BenchMidZ})");
    }
}
