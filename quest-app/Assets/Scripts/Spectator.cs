using UnityEngine;

/// <summary>
/// Keeps one spectator moving.
///
/// WHY THE CROWD IS IN TWO TIERS. The stand holds roughly two hundred people. A
/// SkinnedMeshRenderer driven by an Animator is skinned on the CPU every frame, and a
/// Quest 3 runs out of that budget somewhere in the low dozens of characters -- the
/// figure is only 1,604 triangles, but the cost is the forty bones and the skinning
/// pass, not the triangles. So only the front rows get a rig. Everything behind them is
/// a rigid baked mesh moved by its transform instead, which costs one matrix write per
/// spectator per frame and, on the GPU, one instanced draw for the lot of them.
///
/// That split is only honest because of where the stand is. ISSF puts the barrier 7 m
/// behind the firing line and the rows run back from there, so the nearest spectator is
/// about 5.5 m from the shooter and the back row is 16 m away. Joint-level articulation
/// is legible in the first two or three rows and gone well before the back of the stand.
///
/// WHY MOVE THEM AT ALL. A still crowd at this fidelity is the uncanny valley's exact
/// trigger: a figure that looks approximately like a person and does not behave like one
/// reads worse than an obvious abstraction does. The literature on VR crowds is
/// consistent that VARIETY of motion matters more than quality of motion, and a stand
/// where everyone breathes on the same beat is as dead as one where nobody moves. So
/// every interval below is per-spectator random, drawn from a seed the builder hands
/// over, which keeps the crowd reproducible without making it uniform.
/// </summary>
public class Spectator : MonoBehaviour
{
    /// <summary>Seated idles the controller carries: three idles plus applause.</summary>
    public const int SeatedActs = 4;

    static readonly int ActHash = Animator.StringToHash("Act");
    static readonly int StandHash = Animator.StringToHash("Stand");
    static readonly int SitHash = Animator.StringToHash("Sit");

    [Tooltip("Set by the builder. Drives every random interval here, so the crowd is the "
             + "same on every run but no two spectators share a rhythm.")]
    public int Seed;

    [Tooltip("Left empty on the rigid tier, which sways instead of animating.")]
    public Animator Rig;

    [Header("Animated tier")]
    [Tooltip("Seconds between changes of seated idle.")]
    public Vector2 ActInterval = new Vector2(7f, 16f);

    [Tooltip("Seconds seated before the spectator may get up.")]
    public Vector2 SitSpell = new Vector2(40f, 150f);

    [Tooltip("Seconds on its feet once it does.")]
    public Vector2 StandSpell = new Vector2(8f, 30f);

    [Tooltip("Chance a seated spell ends by standing up rather than simply carrying on.")]
    [Range(0f, 1f)] public float StandChance = 0.35f;

    [Header("Rigid tier")]
    [Tooltip("Degrees of lean the rigid figures rock through. Deliberately small: this is "
             + "weight shifting on a plastic seat, not swaying.")]
    public float SwayAngle;

    public float SwayPeriod = 6f;

    System.Random _rng;
    float _nextAct;
    float _nextPose;
    bool _standing;
    float _phase;
    float _rate;
    Quaternion _rest;
    Vector3 _home;
    Vector3 _pivot;

    void Awake()
    {
        _rng = new System.Random(Seed);
        _rest = transform.localRotation;
        _home = transform.localPosition;
        _pivot = new Vector3(0f, 0.45f, 0f);          // the hips: sitting height
        _phase = Range(0f, Mathf.PI * 2f);
        _rate = Mathf.PI * 2f / (SwayPeriod * Range(0.75f, 1.25f));

        if (Rig == null) return;
        _nextAct = Range(ActInterval);
        _nextPose = Range(SitSpell);
    }

    /// <summary>
    /// Animator setup belongs here and not in Awake. A parameter set before the Animator
    /// has initialised is silently dropped, which is why the whole front row sat on
    /// Act 0 -- 49 spectators all picking zero out of four, which is the kind of odds
    /// that should always be read as a bug rather than as luck.
    /// </summary>
    void Start()
    {
        if (Rig == null || Rig.runtimeAnimatorController == null) return;

        Rig.SetInteger(ActHash, _rng.Next(SeatedActs));

        // Scatter the rigs through their clips. Without this the whole front row breathes
        // in unison, which is a worse tell than not breathing at all.
        var s = Rig.GetCurrentAnimatorStateInfo(0);
        Rig.Play(s.fullPathHash, 0, Range(0f, 1f));

        // Off-screen spectators cost nothing at all. The player spends almost the whole
        // session facing downrange, with the entire stand behind them. Set last: a culled
        // Animator will not accept the calls above.
        Rig.cullingMode = AnimatorCullingMode.CullCompletely;
    }

    float Range(float a, float b) { return a + (b - a) * (float)_rng.NextDouble(); }

    float Range(Vector2 r) { return Range(r.x, r.y); }

    void Update()
    {
        if (Rig != null) Tick(Time.deltaTime);
        else if (SwayAngle > 0f) Sway();
    }

    void Tick(float dt)
    {
        _nextAct -= dt;
        _nextPose -= dt;

        if (!_standing && _nextAct <= 0f)
        {
            Rig.SetInteger(ActHash, _rng.Next(SeatedActs));
            _nextAct = Range(ActInterval);
        }

        if (_nextPose > 0f) return;

        if (_standing)
        {
            Rig.SetTrigger(SitHash);
            _standing = false;
            _nextPose = Range(SitSpell);
            _nextAct = Range(ActInterval);
        }
        else if (_rng.NextDouble() < StandChance)
        {
            Rig.SetTrigger(StandHash);
            _standing = true;
            _nextPose = Range(StandSpell);
        }
        else
        {
            _nextPose = Range(SitSpell);
        }
    }

    /// <summary>
    /// The rigid tier's whole animation: a slow lean about the hips, out of phase with
    /// every neighbour, with a second slower beat across it so the period is not
    /// readable. Leaning about the seat rather than about the origin is what keeps it
    /// from looking like a hinged cut-out -- the feet stay put and the head travels.
    /// </summary>
    void Sway()
    {
        float t = Time.time;
        float a = Mathf.Sin(t * _rate + _phase) * SwayAngle;
        float b = Mathf.Sin(t * _rate * 0.59f + _phase * 2f) * SwayAngle * 0.6f;
        var delta = Quaternion.Euler(a, b, 0f);
        transform.localRotation = _rest * delta;
        transform.localPosition = _home + _rest * (_pivot - delta * _pivot);
    }
}
