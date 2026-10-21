using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the spectator stand behind the firing line: a raked seating deck, the ISSF
/// barrier, individual seats, and a seated crowd.
///
/// Built from boxes and combined into one mesh per material, matching how the rest of the
/// hall is made -- the repo carries no binary art and everything is reproducible from
/// source.
///
/// WHERE IT GOES. ISSF General Technical Rule 6.4.3.4: "There must also be a space
/// provided for spectators. This area must be separated from the area for athletes and
/// officials by a suitable barrier located at least 7.0m behind the firing line." The
/// firing line here is the bench front at z = 10, so the barrier sits at z = 17 and the
/// spectators get z = 17..30, the 13 m of empty floor the hall was always holding for them.
///
/// AXIS WARNING. output/range_10m/unity/README.md describes the GLB: downrange +Z, firing
/// line at z = -10. The FBX import MIRRORS Z, so the scene is the other way round --
/// targets at z = 0, firing line at z = +10, glazed wall at z = +30, downrange -Z. Every
/// number below is measured off the imported hall, not read from that README. The
/// OVRCameraRig confirms it: it sits at z = 11.45 yawed 180 degrees, facing the targets.
///
/// THE RAKE IS DERIVED, NOT TYPED. Each tier height comes from a C-value recurrence, the
/// standard sightline method: C is the vertical gap between a spectator's sightline to the
/// action and the top of the head in front. Given row n-1 at height y and horizontal
/// distance d from the focal point, row n must sit at
///
///     y_n = Fy + (y_(n-1) + eye + C - Fy) * (d_n / d_(n-1)) - eye
///
/// which is why the risers grow toward the back instead of being constant. A constant
/// riser gives the back rows a view of the heads in front. C = 60 mm is the low end of the
/// usual band, and is only honest because the seats are STAGGERED half a pitch row to row,
/// so you look between two heads rather than over one -- the same trick real venues use to
/// keep the rake walkable.
///
/// The focal point is the athlete at the firing line, taken at bench-top height (y = 1.0)
/// rather than at the floor. Focusing on the floor would guarantee a view of the athlete's
/// feet and need roughly half a metre more rise than this 6 m ceiling can spare.
///
/// THE FIGURES ARE STYLISED AND THEY MOVE. Four earlier crowds were tried here and all
/// four were rejected for the same reason: box figures, cut-down CC0 sculpt bases,
/// MakeHuman bodies, and MakeHuman bodies with photographic skins and real clothing. The
/// last of those was by far the most detailed and by far the worst -- "still very creepy".
/// That is the uncanny valley doing what it does, and the trigger is MISMATCHED FIDELITY:
/// something that looks approximately like a person and does not behave like one. The two
/// fixes are to stop approximating a person and to make them behave. This crowd does both.
///
/// They are Kenney's CC0 "Animated Characters: Protagonists" figure, reproportioned off
/// its chibi original and posed and animated by scripts/build_spectator.py -- that pack ships
/// idle, jump and run and nothing that resembles sitting down. SpectatorAssets.cs turns
/// the result into prefabs and Spectator.cs drives them. Variety comes from twelve
/// recoloured skins, per-figure scale and yaw, and most of all from every spectator
/// running its own clock, because a stand that breathes in unison is as dead as one that
/// does not breathe at all.
///
/// THE CROWD IS TIERED. The front rows carry a rig and an Animator; everything behind them
/// is a rigid baked mesh that leans about its own hips instead. See Spectator.cs for why,
/// and for the distances that make that an honest trade.
///
/// OCCUPANCY IS DELIBERATELY NOT 100%. Every reference photograph of a 10 m qualification
/// shows a half-empty stand with people in clumps: team staff together, families together,
/// whole blocks bare. A full stand is the thing that reads as fake. Seats fill at ~45%,
/// clustered, from a fixed seed so the crowd is the same on every rebuild.
///
/// LIGHTING. The audience is marked batching-static but NOT ContributeGI. The scene's
/// lightmap is already baked; adding GI-static geometry to it renders black until someone
/// re-bakes. "Simple Sun" is a realtime directional, so non-GI geometry just lights.
/// If you do re-bake later, turn ContributeGI on and they will improve.
/// </summary>
public static class AddAudience
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string MaterialDir = "Assets/Models/Materials";
    const string MeshDir = "Assets/Models/Generated";
    const string RootName = "Audience";

    // ---- measured off the imported hall (see AXIS WARNING above) ----
    const float FiringLineZ = 10f;     // bench front
    const float GlassZ = 30f;          // glazed rear wall

    /// <summary>ISSF 6.4.3.4: barrier at least 7.0 m behind the firing line.</summary>
    const float BarrierSetback = 7.0f;
    const float BarrierZ = FiringLineZ + BarrierSetback;   // 17.0
    const float BarrierHeight = 1.10f;

    // ---- deck ----
    const float FrontRowZ = 17.6f;     // 0.6 m walkway between barrier and first row
    const float RowDepth = 0.90f;      // tread; 750-800 standard, 850-950 generous
    const int RowCount = 11;
    const float FirstTierY = 0.45f;    // front row already lifted clear of the officials

    // ---- sightline ----
    const float FocalZ = 11.5f;        // athlete standing behind the bench
    const float FocalY = 1.00f;        // bench top
    const float SeatHeight = 0.45f;
    const float SittingEyeHeight = 0.80f;   // above the seat, 50th percentile male
    const float EyeAboveTier = SeatHeight + SittingEyeHeight;
    const float CValue = 0.06f;

    // ---- block ----
    const float BlockHalfWidth = 16f;
    const float AisleCentre = 6.5f;
    const float AisleHalf = 0.6f;      // 1.2 m aisles
    const float SeatPitch = 0.52f;

    const float Occupancy = 0.45f;
    const int Seed = 1023;

    /// <summary>
    /// How many front rows get a rig and an Animator. Two rows is about 45 people, which
    /// is the upper end of what a Quest will skin per frame without complaint. The rows
    /// behind start 8 m from the firing point, where joint articulation has stopped being
    /// legible and a leaning rigid mesh reads the same for a fraction of the CPU.
    /// Spectator.cs has the reasoning in full. Raise it if the profiler says there is
    /// room; it is the one number in here worth tuning on the headset.
    /// </summary>
    const int AnimatedRows = 3;

    /// <summary>
    /// Rows up to this one sway. Rows at or beyond it hold still and are batching-static,
    /// which is what buys one draw call per skin for the back of the stand. Two rows of
    /// slow leaning between the animated front and the still back is enough that the eye
    /// never finds the line where motion stops.
    /// </summary>
    const int SwayRows = 6;

    /// <summary>Share of the rigid tier placed on its feet rather than seated.</summary>
    const float StandingShare = 0.08f;

    /// <summary>
    /// The spectators face the firing line. Nothing else: the FBXs come out of Blender's
    /// exporter with its default axis conversion and import Y-up at 1:1, which the
    /// measured prefab bounds in SpectatorAssets.Build confirm. The MakeHuman figures
    /// this replaces needed a -90 about X; these do not.
    /// </summary>
    static readonly Quaternion Facing = Quaternion.Euler(0f, 180f, 0f);

    struct Finish
    {
        public string Name;
        public Color Col;
        public float Smooth;
        public Finish(string name, Color col, float smooth) { Name = name; Col = col; Smooth = smooth; }
    }

    static readonly Finish[] Palette =
    {
        new Finish("audience_deck",   new Color(0.34f, 0.34f, 0.36f), 0.15f),
        new Finish("audience_rail",   new Color(0.55f, 0.57f, 0.60f), 0.70f),
        new Finish("audience_seat",   new Color(0.11f, 0.22f, 0.52f), 0.35f),  // the blue stand at Bhopal
        new Finish("audience_coat",   new Color(0.16f, 0.17f, 0.20f), 0.12f),  // left over a seat back
        new Finish("audience_bag",    new Color(0.24f, 0.21f, 0.18f), 0.14f),  // on the deck underfoot
    };

    [MenuItem("Quest/13. Build the Audience")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            EditorSceneManager.OpenScene(ScenePath);
        }

        if (!AssetDatabase.IsValidFolder(MeshDir))
        {
            AssetDatabase.CreateFolder("Assets/Models", "Generated");
        }

        // Every root named Audience, not just the first. GameObject.Find returns one, and
        // rebuilding with it left three stands stacked on top of each other in the scene.
        foreach (var g in EditorSceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (g.name == RootName || g.name == "__crowdtest") Object.DestroyImmediate(g);
        }

        float[] tierY = TierHeights();
        var buckets = new Dictionary<string, List<CombineInstance>>();
        foreach (var p in Palette) buckets[p.Name] = new List<CombineInstance>();

        // The root has to exist before the crowd is built: the spectators are instantiated
        // prefabs and need somewhere to be parented as they are placed.
        var root = new GameObject(RootName);
        root.transform.position = Vector3.zero;
        var peopleRoot = new GameObject("People");
        peopleRoot.transform.SetParent(root.transform, false);
        _people = peopleRoot.transform;

        _buckets = buckets;
        LoadPrefabs();
        BuildDeck(buckets, tierY);
        BuildBarrier(buckets);
        int seats = 0, people = 0;
        BuildSeatsAndCrowd(buckets, tierY, ref seats, ref people);

        int tris = 0, drawCalls = 0;
        foreach (var p in Palette)
        {
            var list = buckets[p.Name];
            if (list.Count == 0) continue;

            var mesh = new Mesh { name = p.Name, indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(list.ToArray(), true, true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, MeshDir + "/" + p.Name + ".asset");

            var go = new GameObject(p.Name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = MakeMaterial(p.Name, p.Col, p.Smooth);

            // Batching only. ContributeGI would need a re-bake; see the summary above.
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);

            tris += mesh.triangles.Length / 3;
            drawCalls++;
        }

        // One collider, on the deck, so the stand is solid if anyone ever walks back there.
        var deck = root.transform.Find("audience_deck");
        if (deck != null) deck.gameObject.AddComponent<MeshCollider>();

        AssetDatabase.SaveAssets();
        var active = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(active);
        EditorSceneManager.SaveScene(active);

        float frontRiser = tierY[1] - tierY[0];
        float backRiser = tierY[RowCount - 1] - tierY[RowCount - 2];
        float headroom = 6f - (tierY[RowCount - 1] + EyeAboveTier + 0.15f);

        Debug.Log("[Audience] barrier z=" + BarrierZ.ToString("F1") + " (ISSF 6.4.3.4 minimum " +
                  BarrierSetback.ToString("F1") + " m behind firing line z=" + FiringLineZ.ToString("F1") + ")");
        Debug.Log("[Audience] " + RowCount + " rows, risers " + frontRiser.ToString("F3") + ".." +
                  backRiser.ToString("F3") + " m, back row y=" + tierY[RowCount - 1].ToString("F2") + " m");
        int animated = 0, standing = 0, crowdTris = 0;
        foreach (var s in peopleRoot.GetComponentsInChildren<Spectator>())
        {
            if (s.Rig != null) animated++;
            var smr = s.GetComponentInChildren<SkinnedMeshRenderer>();
            var mf = s.GetComponentInChildren<MeshFilter>();
            var m = smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
            if (m != null) crowdTris += (int)(m.GetIndexCount(0) / 3);
            if (s.name.Contains("stand_")) standing++;
        }

        Debug.Log("[Audience] " + seats + " seats, " + people + " occupied (" +
                  (100f * people / Mathf.Max(1, seats)).ToString("F0") + "%), " + tris +
                  " triangles of structure in " + drawCalls + " draw calls");
        Debug.Log("[Audience] crowd: " + animated + " animated, " + (people - animated) +
                  " rigid (" + standing + " of them on their feet), " + crowdTris +
                  " triangles, " + _skins.Count + " skins");
        Debug.Log("[Audience] headroom above the back row's heads: " + headroom.ToString("F2") + " m");
    }

    /// <summary>C-value recurrence. See the class summary for the formula and why.</summary>
    static float[] TierHeights()
    {
        var y = new float[RowCount];
        y[0] = FirstTierY;
        for (int n = 1; n < RowCount; n++)
        {
            float dPrev = (FrontRowZ + (n - 1) * RowDepth) - FocalZ;
            float dThis = (FrontRowZ + n * RowDepth) - FocalZ;
            y[n] = FocalY + (y[n - 1] + EyeAboveTier + CValue - FocalY) * (dThis / dPrev) - EyeAboveTier;
        }
        return y;
    }

    static void BuildDeck(Dictionary<string, List<CombineInstance>> b, float[] tierY)
    {
        for (int n = 0; n < RowCount; n++)
        {
            float z0 = FrontRowZ + n * RowDepth;
            Box(b["audience_deck"],
                new Vector3(0f, tierY[n] * 0.5f, z0 + RowDepth * 0.5f),
                new Vector3(BlockHalfWidth * 2f, tierY[n], RowDepth),
                Quaternion.identity);
        }

        // Rear walkway, level with the back row, out to just short of the glass.
        float backZ = FrontRowZ + RowCount * RowDepth;
        float walk = (GlassZ - 1f) - backZ;
        if (walk > 0.1f)
        {
            Box(b["audience_deck"],
                new Vector3(0f, tierY[RowCount - 1] * 0.5f, backZ + walk * 0.5f),
                new Vector3(BlockHalfWidth * 2f, tierY[RowCount - 1], walk),
                Quaternion.identity);
        }

        // Aisle steps: half a tier each, so the climb is walkable rather than a vault.
        float[] aisles = { -AisleCentre, AisleCentre };
        foreach (float ax in aisles)
        {
            for (int n = 1; n < RowCount; n++)
            {
                float rise = tierY[n] - tierY[n - 1];
                float z0 = FrontRowZ + n * RowDepth;
                Box(b["audience_deck"],
                    new Vector3(ax, tierY[n - 1] + rise * 0.25f, z0 + RowDepth * 0.25f),
                    new Vector3(AisleHalf * 2f, rise * 0.5f, RowDepth * 0.5f),
                    Quaternion.identity);
            }
        }
    }

    static void BuildBarrier(Dictionary<string, List<CombineInstance>> b)
    {
        float w = BlockHalfWidth * 2f;
        Box(b["audience_rail"], new Vector3(0f, BarrierHeight - 0.03f, BarrierZ), new Vector3(w, 0.06f, 0.06f), Quaternion.identity);
        Box(b["audience_rail"], new Vector3(0f, BarrierHeight * 0.5f, BarrierZ), new Vector3(w, 0.05f, 0.05f), Quaternion.identity);
        for (float x = -BlockHalfWidth; x <= BlockHalfWidth + 0.01f; x += 2.5f)
        {
            Box(b["audience_rail"], new Vector3(x, BarrierHeight * 0.5f, BarrierZ), new Vector3(0.06f, BarrierHeight, 0.06f), Quaternion.identity);
        }
    }

    static void BuildSeatsAndCrowd(Dictionary<string, List<CombineInstance>> b, float[] tierY, ref int seats, ref int people)
    {
        var rng = new System.Random(Seed);

        for (int n = 0; n < RowCount; n++)
        {
            float tier = tierY[n];
            float panY = tier + SeatHeight;
            // 0.70 of the tread, not 0.55 as it started. The seat has to sit far enough
            // back that the DEEPEST pose still has its feet on the deck: a slouched
            // spectator reaches 0.55 m forward of its hip against the neutral pose's
            // 0.37, and at 0.55 of the tread those feet hung over the edge of the tier
            // with the next row's seats under them. 0.70 puts the hip 0.63 m from the
            // front edge, which leaves 80 mm of deck in front of the longest legs and
            // still clears the back of the tread by 50 mm.
            float z = FrontRowZ + n * RowDepth + RowDepth * 0.70f;

            // Stagger alternate rows half a pitch: this is what makes C = 60 mm honest.
            float stagger = (n % 2 == 0) ? 0f : SeatPitch * 0.5f;

            // Clustering. Two or three groups per row rather than one, and a floor of
            // 0.06 rather than 0.35, so there are real bare stretches between them. With
            // the old single wide cluster and a high floor the stand came out evenly
            // sprinkled from end to end, which is the arrangement no photograph of a
            // qualification ever shows.
            int groups = 2 + rng.Next(2);
            var centre = new float[groups];
            var width = new float[groups];
            for (int g = 0; g < groups; g++)
            {
                centre[g] = (float)(rng.NextDouble() * 2.0 - 1.0) * BlockHalfWidth * 0.85f;
                width[g] = 1.6f + (float)rng.NextDouble() * 4.5f;
            }

            for (float x = -BlockHalfWidth + SeatPitch * 0.5f + stagger; x <= BlockHalfWidth; x += SeatPitch)
            {
                if (Mathf.Abs(Mathf.Abs(x) - AisleCentre) < AisleHalf) continue;      // aisles
                if (n == 0 && x > 8.4f && x < 11.0f) continue;                        // wheelchair spaces (ISSF 6.4.1.6)

                seats++;
                Seat(b["audience_seat"], new Vector3(x, panY, z), rng);

                float near = 0f;
                for (int g = 0; g < groups; g++)
                {
                    near = Mathf.Max(near, Mathf.Exp(-Mathf.Pow((x - centre[g]) / width[g], 2f)));
                }
                float chance = Occupancy * (0.06f + 1.7f * near);
                if (rng.NextDouble() > chance) continue;

                people++;
                Person(new Vector3(x, panY, z), n, rng);
            }
        }
    }

    static Transform _people;
    static Dictionary<string, List<CombineInstance>> _buckets;
    static List<Material> _skins;
    static GameObject _rigged;
    static GameObject[] _seated, _standingPoses;
    static RuntimeAnimatorController _controller;

    static void LoadPrefabs()
    {
        // Force the rig prefab off disk. SpectatorAssets may have rewritten it moments
        // ago and Unity will happily hand back the copy it already had in memory, which
        // is how 49 spectators ended up with a null animator controller although the
        // prefab file on disk plainly referenced one.
        AssetDatabase.ImportAsset(SpectatorAssets.RigPrefab, ImportAssetOptions.ForceUpdate);
        _rigged = AssetDatabase.LoadAssetAtPath<GameObject>(SpectatorAssets.RigPrefab);
        _controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
            SpectatorAssets.ControllerPath);
        _seated = Load(SpectatorAssets.PosePrefabs(true));
        _standingPoses = Load(SpectatorAssets.PosePrefabs(false));
        _skins = SpectatorAssets.BuildMaterials();

        if (_rigged == null || _seated.Length == 0 || _standingPoses.Length == 0 || _skins.Count == 0)
        {
            Debug.LogError("[Audience] spectator prefabs missing -- run " +
                           "Quest/12. Prepare the Spectators first");
        }
    }

    static GameObject[] Load(string[] paths)
    {
        var list = new List<GameObject>();
        foreach (var p in paths)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            if (go != null) list.Add(go);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Places one spectator on a seat.
    ///
    /// EVERYTHING HERE IS A VARIETY KNOB, and that is the point. The first version of
    /// this crowd put two hundred copies of one mesh in one posture on a perfect grid,
    /// and the verdict was "not realistic". Looking at a native-resolution frame, the
    /// loudest tell by a long way was not the stylised proportions -- it was that every
    /// figure sat identically, in dead-straight rows, at identical spacing. So: eleven
    /// baked seated poses and three standing ones, sixteen skins, a yaw spread wide
    /// enough that people face slightly different ways, a few centimetres of positional
    /// jitter so the rows are ragged, and a height spread that includes children.
    ///
    /// THREE PERFORMANCE TIERS. The front rows carry a rig and an Animator. The next
    /// couple sway, which means they cannot be batched. Everything behind that is marked
    /// batching-static, which lets Unity merge it into one draw call per skin -- and that
    /// is what makes eleven poses free: batching groups by material, not by mesh, so the
    /// pose count does not cost a thing.
    /// </summary>
    static void Person(Vector3 pan, int row, System.Random rng)
    {
        if (_people == null || _rigged == null || _seated.Length == 0) return;

        bool animated = row < AnimatedRows;
        bool standing = !animated && rng.NextDouble() < StandingShare;
        var src = animated ? _rigged
                : standing ? _standingPoses[rng.Next(_standingPoses.Length)]
                : _seated[rng.Next(_seated.Length)];

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(src, _people);

        // Wider than it looks: at a 0.52 m seat pitch, 16 degrees of yaw is most of the
        // difference between "facing the range" and "facing the person next to you".
        inst.transform.rotation =
            Quaternion.Euler(0f, (float)(rng.NextDouble() * 32.0 - 16.0), 0f) * Facing;

        // Height. Adults scatter about 7% either side, and roughly one in eleven
        // spectators at a qualification is a child brought along by a parent.
        float size = 0.93f + (float)rng.NextDouble() * 0.14f;
        if (rng.NextDouble() < 0.09) size *= 0.76f + (float)rng.NextDouble() * 0.07f;
        // Safe to assign outright: SpectatorAssets wraps each model in a clean root and
        // keeps the import's x100 and its axis conversion on the child, precisely so this
        // line does not have to know about either. Assigning scale straight onto a raw
        // imported model is what left 132 spectators one centimetre tall and invisible.
        inst.transform.localScale = Vector3.one * size;

        // Feet on the tier, not the seat pan. A standing spectator steps forward off its
        // own seat so it is not occupying the chair it is no longer sitting on. The
        // jitter is what breaks the grid: nobody sits dead centre on a plastic seat.
        float floorY = pan.y - SeatHeight;
        float forward = standing ? 0.22f : 0f;
        inst.transform.position = new Vector3(
            pan.x + (float)(rng.NextDouble() * 0.09 - 0.045),
            floorY,
            pan.z - forward + (float)(rng.NextDouble() * 0.10 - 0.05));

        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            r.sharedMaterial = _skins[rng.Next(_skins.Count)];
        }

        bool sways = !animated && row < SwayRows;

        var spec = inst.GetComponent<Spectator>();
        if (spec != null)
        {
            spec.Seed = rng.Next();
            // Re-assert the controller on the instance rather than trusting the prefab
            // to have carried it: one line, and it cannot be defeated by asset caching.
            if (spec.Rig != null && _controller != null)
            {
                spec.Rig.runtimeAnimatorController = _controller;
            }
            spec.SwayAngle = sways ? 1.1f + (float)rng.NextDouble() * 1.1f : 0f;
        }

        // Batching-static ONLY for the figures that never move. A batching-static
        // renderer that moves is both wrong and slower; but for the ones that hold
        // still, this is what collapses 130-odd spectators into one draw call per skin
        // and makes the eleven baked poses cost nothing at all, since Unity batches by
        // material rather than by mesh.
        var flags = StaticEditorFlags.OccludeeStatic;
        if (!animated && !sways) flags |= StaticEditorFlags.BatchingStatic;
        GameObjectUtility.SetStaticEditorFlags(inst, flags);
    }


    /// <summary>
    /// One seat, and occasionally a coat left over its back or a bag on the deck in front
    /// of it. The props cost two boxes each and go through the same combine as everything
    /// else, so the whole lot is two extra draw calls -- cheap for something that reads
    /// as "people have been sitting here" rather than "seats have been instantiated".
    /// </summary>
    static void Seat(List<CombineInstance> b, Vector3 pan, System.Random rng)
    {
        var rot = Quaternion.Euler(0f, 180f, 0f);
        Local(b, pan, rot, new Vector3(0f, -0.03f, 0f), new Vector3(0.46f, 0.06f, 0.44f));
        // A 0.38 m back, not 0.48. These figures carry a shorter torso than a person --
        // reproportioning them gave the height to the legs -- so their shoulder sits only
        // 0.55 m above the pan. Against a 0.48 m back that left every spectator buried to
        // the chin and the stand reading as a rack of blue panels with heads on top.
        // Low-backed plastic seats are what a qualification hall has anyway.
        Local(b, pan, rot, new Vector3(0f, 0.19f, -0.22f), new Vector3(0.46f, 0.38f, 0.06f));

        if (_buckets == null) return;

        if (rng.NextDouble() < 0.06)
        {
            float w = 0.34f + (float)rng.NextDouble() * 0.10f;
            Local(_buckets["audience_coat"], pan, rot,
                  new Vector3((float)(rng.NextDouble() * 0.08 - 0.04), 0.30f, -0.25f),
                  new Vector3(w, 0.42f, 0.09f));
        }
        if (rng.NextDouble() < 0.05)
        {
            Local(_buckets["audience_bag"], pan, rot,
                  new Vector3((float)(rng.NextDouble() * 0.16 - 0.08), -0.33f, 0.30f),
                  new Vector3(0.26f, 0.22f, 0.17f));
        }
    }

    static void Local(List<CombineInstance> b, Vector3 origin, Quaternion rot, Vector3 localPos, Vector3 size)
    {
        Box(b, origin + rot * localPos, size, rot);
    }

    static Mesh _cube;

    static Mesh Cube()
    {
        if (_cube != null) return _cube;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cube = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
        Object.DestroyImmediate(go);
        return _cube;
    }


    static void Box(List<CombineInstance> b, Vector3 centre, Vector3 size, Quaternion rot)
    {
        var ci = new CombineInstance();
        ci.mesh = Cube();
        ci.transform = Matrix4x4.TRS(centre, rot, size);
        b.Add(ci);
    }

    static Material MakeMaterial(string name, Color col, float smoothness)
    {
        string path = MaterialDir + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            // Standard, not URP/Lit: this project runs the built-in pipeline (see ToBuiltIn).
            mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_Color", col);
        mat.SetFloat("_Glossiness", smoothness);
        mat.SetFloat("_Metallic", name == "audience_rail" ? 0.8f : 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
