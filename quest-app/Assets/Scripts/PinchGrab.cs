using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Pick up, aim and drop a <see cref="GrabbableItem"/> by pinching, driven off the
/// Interaction SDK's hand joints.
///
/// The earlier version looked for OVRHand plus OVRSkeleton. Neither exists in this scene
/// any more: the OVRHandPrefab hands were replaced by the SDK's own rig, so the grab
/// found no hands and silently did nothing. What the scene does have is
///
///     OVRComprehensiveInteractionRig/OVRHandVisual{Left,Right}/OpenXR{Left,Right}Hand/XRHand_Wrist
///
/// with the full joint chain beneath, driven at runtime by HandVisual. Those two are the
/// hands the player sees; the other eight XRHand_* sets in the scene belong to interactor
/// reticles and synthetic ghosts and must not be used.
///
/// Pinch is measured as thumb-tip to index-tip distance rather than through an SDK call,
/// because it needs only the joints and cannot go stale if the rig changes again.
///
/// Placement is exact arithmetic against the grip harvested from the authored
/// HandGrabPoses, so the pistol snaps to the same grip every time and can be aimed.
/// </summary>
public class PinchGrab : MonoBehaviour
{
    // Thumb-to-index distance that counts as a pinch, with hysteresis so a held grip
    // does not flicker at the threshold.
    const float PinchClose = 0.025f;
    const float PinchOpen = 0.040f;

    const float GrabRadius = 0.18f;
    const int VelocitySamples = 5;
    const float MaxThrowSpeed = 6f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var go = new GameObject("~PinchGrab");
        go.AddComponent<PinchGrab>();
        DontDestroyOnLoad(go);
    }

    class Hand
    {
        public bool isLeft;
        public Transform wrist;
        public Transform indexTip;
        public Transform thumbTip;
        public GrabbableItem held;
        public Rigidbody body;
        public bool wasPinching;
        public Vector3 fallbackOffset;
        public Quaternion fallbackRotation;
        public readonly Queue<(Vector3 pos, float time)> trail = new Queue<(Vector3, float)>();

        public bool Valid => wrist != null && indexTip != null && thumbTip != null;
        public bool Tracked => Valid && wrist.gameObject.activeInHierarchy;
    }

    readonly List<Hand> _hands = new List<Hand>();
    GrabbableItem[] _items = System.Array.Empty<GrabbableItem>();
    float _nextScan;
    bool _announced;

    void Update()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            Scan();
        }

        foreach (var h in _hands) Step(h);
    }

    void Scan()
    {
        _items = FindObjectsByType<GrabbableItem>(FindObjectsSortMode.None);

        if (_hands.Count < 2)
        {
            _hands.Clear();
            TryAddHand("OVRHandVisualLeft", true);
            TryAddHand("OVRHandVisualRight", false);
        }

        if (!_announced && _hands.Count > 0 && _items.Length > 0)
        {
            _announced = true;
            Debug.Log($"[Grab] ready: {_hands.Count} hand(s), {_items.Length} item(s), " +
                      $"{_items.Count(i => i.hasGrips)} with a designed grip");
            foreach (var h in _hands)
                Debug.Log($"[Grab]   {(h.isLeft ? "LEFT" : "RIGHT")} wrist={Path(h.wrist)}");
        }
    }

    /// <summary>
    /// Find the joints under one of the two primary hand visuals. Searched by path so the
    /// reticle and ghost copies of the same joint names are never picked up.
    /// </summary>
    void TryAddHand(string visualName, bool isLeft)
    {
        var all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        Transform Find(string joint) => all.FirstOrDefault(t =>
            t.name == joint && InsideVisual(t, visualName));

        var wrist = Find("XRHand_Wrist");
        var index = Find("XRHand_IndexTip");
        var thumb = Find("XRHand_ThumbTip");

        if (wrist == null || index == null || thumb == null)
        {
            Debug.Log($"[Grab] {visualName}: wrist={wrist != null} index={index != null} thumb={thumb != null} " +
                      "-- not ready yet");
            return;
        }

        _hands.Add(new Hand { isLeft = isLeft, wrist = wrist, indexTip = index, thumbTip = thumb });
    }

    static bool InsideVisual(Transform t, string visualName)
    {
        for (var p = t; p != null; p = p.parent)
            if (p.name == visualName) return true;
        return false;
    }

    void Step(Hand h)
    {
        if (!h.Valid) return;

        if (!h.Tracked)
        {
            if (h.held != null) Release(h);
            h.wasPinching = false;
            return;
        }

        h.trail.Enqueue((h.wrist.position, Time.unscaledTime));
        while (h.trail.Count > VelocitySamples) h.trail.Dequeue();

        float gap = Vector3.Distance(h.indexTip.position, h.thumbTip.position);
        bool pinching = h.wasPinching ? gap < PinchOpen : gap < PinchClose;

        if (pinching && !h.wasPinching && h.held == null) TryGrab(h);
        else if (!pinching && h.held != null) Release(h);

        h.wasPinching = pinching;
    }

    void TryGrab(Hand h)
    {
        GrabbableItem best = null;
        float bestDistance = GrabRadius;

        foreach (var item in _items)
        {
            if (item == null) continue;
            if (_hands.Any(o => o.held == item)) continue;

            float d = Vector3.Distance(h.indexTip.position, item.AnchorWorldPosition);
            if (d < bestDistance) { bestDistance = d; best = item; }
        }

        if (best == null)
        {
            Debug.Log($"[Grab] {(h.isLeft ? "LEFT" : "RIGHT")} pinched but nothing within {GrabRadius} m " +
                      $"(nearest item {Nearest(h):0.00} m)");
            return;
        }

        var body = best.GetComponent<Rigidbody>();
        if (body == null) return;

        h.held = best;
        h.body = body;
        best.IsHeld = true;
        body.isKinematic = true;

        if (!best.hasGrips)
        {
            h.fallbackOffset = h.wrist.InverseTransformPoint(best.transform.position);
            h.fallbackRotation = Quaternion.Inverse(h.wrist.rotation) * best.transform.rotation;
        }

        Debug.Log($"[Grab] {(h.isLeft ? "LEFT" : "RIGHT")} picked up {best.name} at {bestDistance:0.000} m " +
                  $"({(best.hasGrips ? "designed grip" : "held where caught")})");
    }

    float Nearest(Hand h)
    {
        float best = 99f;
        foreach (var i in _items)
            if (i != null) best = Mathf.Min(best, Vector3.Distance(h.indexTip.position, i.AnchorWorldPosition));
        return best;
    }

    void LateUpdate()
    {
        foreach (var h in _hands)
        {
            if (h.held == null || !h.Valid) continue;

            if (h.held.hasGrips)
            {
                h.held.PoseForWrist(h.isLeft, h.wrist.position, h.wrist.rotation,
                                    out var pos, out var rot);
                h.held.transform.SetPositionAndRotation(pos, rot);
            }
            else
            {
                h.held.transform.SetPositionAndRotation(
                    h.wrist.TransformPoint(h.fallbackOffset),
                    h.wrist.rotation * h.fallbackRotation);
            }
        }
    }

    void Release(Hand h)
    {
        var item = h.held;
        var body = h.body;
        h.held = null;
        h.body = null;
        if (item == null || body == null) return;

        item.IsHeld = false;
        body.isKinematic = false;
        body.linearVelocity = Vector3.ClampMagnitude(TrailVelocity(h), MaxThrowSpeed);

        Debug.Log($"[Grab] {(h.isLeft ? "LEFT" : "RIGHT")} released {item.name} at " +
                  $"{body.linearVelocity.magnitude:0.00} m/s");
    }

    static Vector3 TrailVelocity(Hand h)
    {
        if (h.trail.Count < 2) return Vector3.zero;
        var s = h.trail.ToArray();
        float dt = s[s.Length - 1].time - s[0].time;
        return dt > 0.0001f ? (s[s.Length - 1].pos - s[0].pos) / dt : Vector3.zero;
    }

    static string Path(Transform t)
    {
        var p = t.name;
        for (var q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
        return p;
    }
}
