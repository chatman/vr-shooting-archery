using UnityEngine;

/// <summary>
/// Watches the air pistol, reports where it goes, and brings it back when it leaves.
///
/// On device the pistol vanishes the moment it is lifted. The Grabbable is configured
/// correctly (_kinematicWhileSelected and _throwWhenUnselected both on, rigidbody and
/// target transform bound), so this is not physics fighting the pose snap. The remaining
/// candidate is the posed grab snapping the pistol to a wrong offset -- a HandGrabPose
/// whose wrist transform is off will teleport it, which looks exactly like disappearing.
///
/// Logging the position across the grab is the only way to tell a teleport from a fall
/// from a scale collapse, and each needs a different fix. The auto-return is here so a
/// bad grab costs a couple of seconds rather than ending the session.
///
/// Held state is inferred from Rigidbody.isKinematic, which Grabbable toggles on select.
/// That avoids a compile-time dependency on the Interaction SDK assemblies.
/// </summary>
public class PistolWatchdog : MonoBehaviour
{
    const string PistolName = "AirPistol_Lane19";

    // Anything beyond this is gone, not merely dropped on the floor.
    const float StrayLimit = 4f;
    const float FallLimit = 1.5f;

    // A single frame moving further than this is a teleport, not hand movement.
    const float JumpPerFrame = 0.75f;

    const float ReturnDelay = 1.5f;
    const float Heartbeat = 5f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var go = new GameObject("~PistolWatchdog");
        go.AddComponent<PistolWatchdog>();
        DontDestroyOnLoad(go);
    }

    Transform _pistol;
    Rigidbody _body;
    Vector3 _home;
    Quaternion _homeRot;
    Vector3 _lastPos;
    bool _lastHeld;
    float _outSince = -1f;
    float _nextBeat;
    float _nextScan;

    void Update()
    {
        if (_pistol == null)
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;

            var go = GameObject.Find(PistolName);
            if (go == null) return;

            _pistol = go.transform;
            _body = go.GetComponent<Rigidbody>();
            _home = _pistol.position;
            _homeRot = _pistol.rotation;
            _lastPos = _home;
            Debug.Log($"[Watchdog] tracking {PistolName} home={_home} scale={_pistol.lossyScale}");
            return;
        }

        bool held = _body != null && _body.isKinematic;
        var pos = _pistol.position;

        if (held != _lastHeld)
        {
            Debug.Log($"[Watchdog] {(held ? "GRABBED" : "RELEASED")} at {pos} " +
                      $"(dist from home {Vector3.Distance(pos, _home):0.00} m)");
            _lastHeld = held;
        }

        // A teleport shows up as one enormous frame delta; that is the signature that
        // separates a bad grab pose from the object simply falling.
        float delta = Vector3.Distance(pos, _lastPos);
        if (delta > JumpPerFrame)
        {
            Debug.Log($"[Watchdog] JUMP {delta:0.00} m in one frame: {_lastPos} -> {pos} " +
                      $"held={held} scale={_pistol.lossyScale} active={_pistol.gameObject.activeInHierarchy}");
        }
        _lastPos = pos;

        if (Time.unscaledTime >= _nextBeat)
        {
            _nextBeat = Time.unscaledTime + Heartbeat;
            Debug.Log($"[Watchdog] pos={pos} held={held} dist={Vector3.Distance(pos, _home):0.00} " +
                      $"visible={IsVisible()} scale={_pistol.lossyScale}");
        }

        if (held) { _outSince = -1f; return; }

        bool strayed = Vector3.Distance(pos, _home) > StrayLimit;
        bool fell = pos.y < _home.y - FallLimit;
        if (!strayed && !fell) { _outSince = -1f; return; }

        if (_outSince < 0f) _outSince = Time.unscaledTime;
        if (Time.unscaledTime - _outSince < ReturnDelay) return;

        if (_body != null) { _body.linearVelocity = Vector3.zero; _body.angularVelocity = Vector3.zero; }
        _pistol.SetPositionAndRotation(_home, _homeRot);
        _lastPos = _home;
        _outSince = -1f;
        Debug.Log($"[Watchdog] returned to {_home} ({(fell ? "fell" : "strayed")}).");
    }

    bool IsVisible()
    {
        foreach (var r in _pistol.GetComponentsInChildren<Renderer>(true))
            if (r.enabled && r.gameObject.activeInHierarchy) return true;
        return false;
    }
}
