using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Records the spectator stand as a numbered PNG sequence, for turning into a clip with
/// ffmpeg. A development tool, not part of the app: it lives in an Editor folder, so it
/// is compiled out of every build, and it writes to quest-app/Captures/, which .gitignore
/// already keeps out of the repository.
///
/// WHY NOT JUST SCREEN-GRAB. The crowd's motion is deliberately slow -- breathing, weight
/// shifts, a head turning to a neighbour -- and a recorder that drops frames whenever the
/// editor is busy turns that into a stutter that looks like a bug. Time.captureFramerate
/// pins the simulation to a fixed step, so each rendered frame is exactly 1/FPS of game
/// time no matter how long the encode takes. The clip comes out smooth and the wall-clock
/// cost is irrelevant.
///
/// THE CAMERA SITS WHERE THE PLAYER DOES, at the firing point, turned round to face the
/// stand. In play that view is the one thing the spectators are built for; it is also the
/// only time they are animating at all, since Spectator culls them completely while they
/// are off-screen and the player is normally facing downrange.
/// </summary>
public static class CrowdCapture
{
    const string ArmedKey = "CrowdCapture.Armed";

    [MenuItem("Quest/14. Record the Crowd")]
    public static void Record()
    {
        // Entering play mode reloads the domain, so the intent has to survive it.
        SessionState.SetBool(ArmedKey, true);
        EditorApplication.isPlaying = true;
    }

    const string StillsKey = "CrowdCapture.Stills";

    /// <summary>
    /// A set of stills from around the stand, for looking at the crowd rather than
    /// watching it. Taken in play mode on purpose: in edit mode every rigged spectator
    /// sits at frame zero of the same clip, which is the one arrangement guaranteed to
    /// make the crowd look repetitive in exactly the way the photographs are meant to
    /// check. The editor's own screenshot tool is no use for this -- it saves at the
    /// game view's size, 586x326 here, which is too small to judge a posture.
    /// </summary>
    [MenuItem("Quest/15. Photograph the Crowd")]
    public static void Photograph()
    {
        SessionState.SetBool(StillsKey, true);
        EditorApplication.isPlaying = true;
    }

    [InitializeOnLoadMethod]
    static void Hook()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            if (SessionState.GetBool(ArmedKey, false))
            {
                SessionState.SetBool(ArmedKey, false);
                new GameObject("__crowdcapture").AddComponent<CrowdRecorder>();
            }
            else if (SessionState.GetBool(StillsKey, false))
            {
                SessionState.SetBool(StillsKey, false);
                new GameObject("__crowdstills").AddComponent<CrowdStills>();
            }
        };
    }
}

/// <summary>
/// Renders a fixed list of viewpoints to PNGs at a size worth looking at.
/// </summary>
public class CrowdStills : MonoBehaviour
{
    public const int Width = 1600;
    public const int Height = 900;

    /// <summary>
    /// Frames to let run before the first shot. The rigs scatter themselves through
    /// their clips in Start and the rigid tier's lean is driven off Time.time, so a
    /// photograph taken on frame one catches the whole crowd at the bottom of its
    /// breath -- which is the one pose they genuinely do share.
    /// </summary>
    const int Settle = 90;

    struct Shot
    {
        public string Name;
        public Vector3 Pos;
        public Vector3 Rot;
        public float Fov;

        public Shot(string name, Vector3 pos, Vector3 rot, float fov)
        {
            Name = name; Pos = pos; Rot = rot; Fov = fov;
        }
    }

    // The hall, for reading these: targets at z = 0, firing line z = 10, ISSF barrier
    // z = 17, stand z = 17.6..27.5, glazed wall z = 30, ceiling y = 6, and the player
    // standing at (1.50, 1.62, 11.45) facing downrange, i.e. away from all of this.
    static readonly Shot[] Shots =
    {
        new Shot("01_firing_point",   new Vector3(1.5f, 1.62f, 11.45f), new Vector3(-4f, -7f, 0f), 44f),
        new Shot("02_firing_point_wide", new Vector3(1.5f, 1.62f, 11.45f), new Vector3(-3f, 0f, 0f), 75f),
        new Shot("03_at_the_barrier", new Vector3(0f, 1.55f, 16.2f),   new Vector3(-2f, 10f, 0f), 50f),
        new Shot("04_front_row_low",  new Vector3(-2.0f, 1.00f, 16.9f), new Vector3(4f, 28f, 0f), 38f),
        new Shot("05_along_the_rows", new Vector3(-13.0f, 2.20f, 18.6f), new Vector3(0f, 62f, 0f), 55f),
        new Shot("06_three_quarter",  new Vector3(-8.0f, 4.60f, 14.5f), new Vector3(12f, 28f, 0f), 60f),
        new Shot("07_from_above",     new Vector3(0f, 5.40f, 20.5f),   new Vector3(52f, 0f, 0f), 55f),
        new Shot("08_up_the_aisle",   new Vector3(6.5f, 1.60f, 17.2f), new Vector3(9f, 0f, 0f), 58f),
        new Shot("09_spectators_view", new Vector3(0f, 3.60f, 28.2f),  new Vector3(7f, 180f, 0f), 70f),
    };

    string _dir;
    Camera _cam;
    RenderTexture _rt;
    Texture2D _read;
    int _frame;

    void Awake()
    {
        Application.runInBackground = true;

        _dir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "..", "Captures", "stills"));
        if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        System.IO.Directory.CreateDirectory(_dir);

        _rt = new RenderTexture(Width, Height, 24);
        _read = new Texture2D(Width, Height, TextureFormat.RGB24, false);

        var go = new GameObject("__stillcam");
        go.transform.SetParent(transform, false);
        _cam = go.AddComponent<Camera>();
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = 90f;
        _cam.targetTexture = _rt;
        _cam.depth = 100;

        // Frame one of the shots immediately, so the Animators count the stand as
        // on-screen from the first frame and start running during the settle.
        Aim(Shots[0]);
        Debug.Log("[Stills] " + Shots.Length + " views into " + _dir);
    }

    void Aim(Shot s)
    {
        _cam.transform.position = s.Pos;
        _cam.transform.rotation = Quaternion.Euler(s.Rot);
        _cam.fieldOfView = s.Fov;
    }

    void LateUpdate()
    {
        if (_frame++ < Settle) return;

        int i = _frame - Settle - 1;
        if (i >= Shots.Length)
        {
            Debug.Log("[Stills] wrote " + Shots.Length + " views to " + _dir);
            EditorApplication.isPlaying = false;
            return;
        }

        Aim(Shots[i]);
        _cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        _read.Apply(false);
        RenderTexture.active = prev;

        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(_dir, Shots[i].Name + ".png"), _read.EncodeToPNG());
    }

    void OnDestroy()
    {
        if (_cam != null) _cam.targetTexture = null;
        if (_rt != null) _rt.Release();
    }
}

/// <summary>
/// The recorder itself. An Editor-assembly MonoBehaviour: it cannot be attached to a
/// saved scene object and it does not exist in a build, but the editor loads the assembly
/// during play, so AddComponent works for exactly as long as this session lasts.
/// </summary>
public class CrowdRecorder : MonoBehaviour
{
    public const int Fps = 30;
    public const int Width = 1280;
    public const int Height = 720;
    public const float Seconds = 18f;

    /// <summary>Firing point: the OVRCameraRig's own position, at standing eye height.</summary>
    static readonly Vector3 Eye = new Vector3(1.5f, 1.62f, 11.45f);

    string _dir;
    Camera _cam;
    RenderTexture _rt;
    Texture2D _read;
    int _frame;
    int _total;

    void Awake()
    {
        // Without this the player loop stops the moment the editor loses focus, and the
        // recording sits at two frames until someone clicks on Unity again. Set here, at
        // runtime, rather than in Player Settings: a recording tool has no business
        // changing how the shipped app behaves.
        Application.runInBackground = true;

        _total = Mathf.RoundToInt(Fps * Seconds);
        _dir = System.IO.Path.Combine(Application.dataPath, "..", "Captures", "crowd");
        _dir = System.IO.Path.GetFullPath(_dir);
        if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
        System.IO.Directory.CreateDirectory(_dir);

        Time.captureFramerate = Fps;

        _rt = new RenderTexture(Width, Height, 24);
        _read = new Texture2D(Width, Height, TextureFormat.RGB24, false);

        var go = new GameObject("__capturecam");
        go.transform.SetParent(transform, false);
        _cam = go.AddComponent<Camera>();
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = 80f;
        _cam.targetTexture = _rt;
        // A camera with a target texture still takes part in culling, which is what keeps
        // the Animators awake: rendering this view is what makes the stand "seen".
        _cam.depth = 100;

        Aim(0f);
        Debug.Log("[Capture] " + _total + " frames at " + Fps + " fps into " + _dir);
    }

    /// <summary>Where the first shot cuts to the second.</summary>
    public const float Cut = 9f;

    /// <summary>
    /// Two shots, both from the firing point, both facing +Z -- the stand is BEHIND the
    /// shooter, so this is the view you get by turning round.
    ///
    /// The first is locked off and fairly tight. Motion this slow is only legible against
    /// something that is not itself moving, so a camera that pans the whole time hides
    /// the one thing the clip exists to show. The second pans, once the point is made, to
    /// put the near rows in the context of the other nine.
    ///
    /// Pitch is NEGATIVE to look up. The stand's mass sits around y = 2.0 at z = 22,
    /// which from a 1.62 m eye at z = 11.45 is about three degrees above the horizon; the
    /// first attempt had the sign the other way and framed half a shot of empty floor.
    /// </summary>
    void Aim(float t)
    {
        if (t < Cut)
        {
            // Dead still, not even a slow drift. Nine seconds of a camera that does not
            // move is what makes a spectator's shoulders rising readable at all, and it
            // also makes the clip checkable: difference two frames of this shot and
            // everything that lights up is a spectator. An earlier take drifted a degree
            // and a half and the ceiling lights showed up in the difference as brightly
            // as the crowd did.
            _cam.fieldOfView = 44f;
            _cam.transform.position = Eye;
            _cam.transform.rotation = Quaternion.Euler(-4.5f, -7f, 0f);
            return;
        }

        float v = Mathf.Clamp01((t - Cut) / (Seconds - Cut));
        float ease = v * v * (3f - 2f * v);
        _cam.fieldOfView = 72f;
        _cam.transform.position = Eye;
        _cam.transform.rotation = Quaternion.Euler(-3f, Mathf.Lerp(-15f, 15f, ease), 0f);
    }

    /// <summary>
    /// Brings a few stand-ups forward so an 18 second clip can show them at all. Left to
    /// itself a spectator sits for 40 to 150 seconds before it considers getting up,
    /// which is right for the app and useless for a demo, so the private countdown is
    /// shortened on a handful of them. Nothing else about their behaviour is touched.
    ///
    /// They are chosen by where they are sitting, not by whatever the scene hands over
    /// first. The locked-off shot is 44 degrees wide and only covers about five and a
    /// half metres of the front row; picking at random put every nudged spectator out of
    /// frame and the first take showed nobody getting up at all.
    /// </summary>
    void Nudge(int count)
    {
        var field = typeof(Spectator).GetField("_nextPose",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) return;

        var inShot = new System.Collections.Generic.List<Spectator>();
        foreach (var s in FindObjectsByType<Spectator>(FindObjectsSortMode.None))
        {
            if (s.Rig == null) continue;
            if ((float)field.GetValue(s) < 5f) continue;       // already on its way up
            if (Mathf.Abs(s.transform.position.x - 0.7f) > 2.0f) continue;
            inShot.Add(s);
        }
        // Spread them across the frame rather than clustering on whichever end was first.
        inShot.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

        for (int i = 0; i < count && inShot.Count > 0; i++)
        {
            var s = inShot[(i * inShot.Count) / Mathf.Max(1, count)];
            s.StandChance = 1f;
            field.SetValue(s, 0.1f);
        }
    }

    void LateUpdate()
    {
        float t = _frame / (float)Fps;
        if (_frame == 2 * Fps) Nudge(4);
        if (_frame == 8 * Fps) Nudge(3);

        Aim(t);
        _cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        _read.Apply(false);
        RenderTexture.active = prev;

        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(_dir, "f" + _frame.ToString("D5") + ".png"),
            _read.EncodeToPNG());

        _frame++;
        if (_frame < _total) return;

        Debug.Log("[Capture] wrote " + _frame + " frames to " + _dir);
        Time.captureFramerate = 0;
        EditorApplication.isPlaying = false;
    }

    void OnDestroy()
    {
        Time.captureFramerate = 0;
        if (_cam != null) _cam.targetTexture = null;
        if (_rt != null) _rt.Release();
    }
}
