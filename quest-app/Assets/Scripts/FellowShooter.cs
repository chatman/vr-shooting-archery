using System.Collections;
using UnityEngine;

/// <summary>
/// A fellow competitor on an adjacent firing point. Air-pistol match pace is slow and
/// irregular: a shooter fires one shot, then takes a long, variable pause to reset, breathe
/// and re-aim. So this fires a single PracticeShot roughly once every few dozen minutes, each
/// gap drawn uniformly and independently from a fixed per-shooter seed (so a session is
/// reproducible without being metronomic). Between shots the Animator idles in Ready.
///
/// When the shot actually breaks (a fixed delay into the Shoot clip) a 3-D positioned
/// AudioSource plays the air-pistol crack, so a shot on a nearby lane is heard loud and a
/// distant one faint. The AudioSource is auto-configured for 3-D if not supplied.
/// </summary>
public class FellowShooter : MonoBehaviour
{
    [Tooltip("Minutes between shots. Each wait is drawn uniformly from [x, y].")]
    public Vector2 IntervalMinutes = new Vector2(45f, 60f);

    [Tooltip("The character's Animator (Ready idle + Shoot trigger). Auto-filled if empty.")]
    public Animator Rig;

    [Tooltip("Fixed seed -> same rhythm every run; change it for a different one.")]
    public int Seed = 7;

    [Tooltip("The air-pistol crack. Auto-loaded from Assets/Audio if left empty.")]
    public AudioClip ShotSound;

    [Tooltip("3-D AudioSource for this shooter. Auto-created if empty.")]
    public AudioSource Audio;

    [Tooltip("Seconds from the Shoot trigger to the moment the shot breaks (when the crack plays).")]
    public float ShotDelay = 3.6f;

    [Tooltip("This firing point's SIUS monitor; the shot registers here when it breaks.")]
    public ShotMonitor Monitor;

    static readonly int ShootHash = Animator.StringToHash("Shoot");
    System.Random _rng;

    void Start()
    {
        if (Rig == null) Rig = GetComponent<Animator>();
        if (ShotSound == null)
            ShotSound = Resources.Load<AudioClip>("airpistol_shot"); // optional fallback
        if (Audio == null) Audio = GetComponent<AudioSource>();
        if (Audio == null) Audio = gameObject.AddComponent<AudioSource>();
        Audio.playOnAwake = false;
        Audio.spatialBlend = 1f;                 // fully 3-D: distance + panning
        Audio.rolloffMode = AudioRolloffMode.Logarithmic;
        Audio.minDistance = 3.0f;                // full volume within ~3 m (a shot is LOUD up close)
        Audio.maxDistance = 45f;                 // carries across the hall
        Audio.volume = 1f;
        Audio.dopplerLevel = 0f;

        _rng = new System.Random(Seed);
        StartCoroutine(Loop());
    }

    IEnumerator Loop()
    {
        // Stagger the very first shot so a whole hall of shooters never fires in lockstep.
        yield return new WaitForSeconds((float)_rng.NextDouble() * 60f);
        while (true)
        {
            float mins = Mathf.Lerp(IntervalMinutes.x, IntervalMinutes.y, (float)_rng.NextDouble());
            yield return new WaitForSeconds(mins * 60f);
            Fire();
        }
    }

    void Fire()
    {
        if (Rig != null) Rig.SetTrigger(ShootHash);
        StartCoroutine(PlayShotAfterDelay());
    }

    IEnumerator PlayShotAfterDelay()
    {
        yield return new WaitForSeconds(ShotDelay);
        if (Audio != null && ShotSound != null) Audio.PlayOneShot(ShotSound);
        if (Monitor != null) Monitor.AddLiveShot();   // register the shot on the SIUS screen
    }

    /// <summary>Fire immediately (used to stage a shot for demos/recording).</summary>
    public void FireNow() { Fire(); }
}
