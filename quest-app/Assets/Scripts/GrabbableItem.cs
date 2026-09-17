using UnityEngine;

/// <summary>
/// Marks an object <see cref="PinchGrab"/> may pick up, carries the designed grip, and
/// brings the object home if it ends up out of reach.
///
/// The grip is stored as the WRIST pose in this object's local space: where the wrist
/// must sit, and how it must be oriented, for the object to be held correctly. Placing
/// the object is then exact arithmetic rather than a search:
///
///     rotation = wristWorldRotation * inverse(wristLocalRotation)
///     position = wristWorldPosition - rotation * wristLocalPosition
///
/// These values are lifted from the HandGrabPoses that PistolHandGrab authored, which
/// measured correct on device -- wrist a few centimetres off the grip, barrel within 5
/// degrees of lying along the forearm. Only the Interaction SDK's runtime resolution of
/// those poses was broken, teleporting the pistol hundreds of metres on the first frame
/// of a grab. The geometry was always right, so it is reused here directly.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class GrabbableItem : MonoBehaviour
{
    [Header("Designed grip (wrist pose in this object's local space)")]
    public bool hasGrips;
    public Vector3 rightWristLocalPosition;
    public Quaternion rightWristLocalRotation = Quaternion.identity;
    public Vector3 leftWristLocalPosition;
    public Quaternion leftWristLocalRotation = Quaternion.identity;

    [Header("Fallback anchor when no grip is designed")]
    public Transform grabAnchor;

    [Header("Return home when lost")]
    public float fallLimit = 0.6f;
    public float strayLimit = 3.0f;
    public float returnDelay = 2.0f;

    Vector3 _home;
    Quaternion _homeRot;
    Rigidbody _body;
    float _outSince = -1f;

    /// <summary>Set by PinchGrab so a held object is never pulled out of the hand.</summary>
    public bool IsHeld { get; set; }

    public Vector3 AnchorWorldPosition => grabAnchor != null ? grabAnchor.position : transform.position;

    void Awake()
    {
        _home = transform.position;
        _homeRot = transform.rotation;
        _body = GetComponent<Rigidbody>();
    }

    /// <summary>Where the object must sit for the given wrist to be holding it properly.</summary>
    public void PoseForWrist(bool isLeft, Vector3 wristPos, Quaternion wristRot,
                             out Vector3 position, out Quaternion rotation)
    {
        var localPos = isLeft ? leftWristLocalPosition : rightWristLocalPosition;
        var localRot = isLeft ? leftWristLocalRotation : rightWristLocalRotation;

        rotation = wristRot * Quaternion.Inverse(localRot);
        position = wristPos - rotation * localPos;
    }

    void Update()
    {
        if (IsHeld) { _outSince = -1f; return; }

        bool fell = transform.position.y < _home.y - fallLimit;
        bool strayed = Vector3.Distance(transform.position, _home) > strayLimit;
        if (!fell && !strayed) { _outSince = -1f; return; }

        if (_outSince < 0f) _outSince = Time.unscaledTime;
        if (Time.unscaledTime - _outSince < returnDelay) return;

        ReturnHome(fell ? "fell" : "strayed");
    }

    public void ReturnHome(string why)
    {
        if (_body != null)
        {
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }
        transform.SetPositionAndRotation(_home, _homeRot);
        _outSince = -1f;
        Debug.Log($"[Grab] {name} returned to {_home} ({why}).");
    }
}
