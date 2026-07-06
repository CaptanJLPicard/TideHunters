using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner-only third-person camera controller. Binds the scene <see cref="CinemachineCamera"/>
/// (ThirdPersonFollow body) to this player's CameraTarget pivot and drives yaw/pitch from the
/// mouse. Holding the FreeLook button (Alt) freezes the body facing and lets the camera orbit
/// freely around the character; on release the camera eases back behind the character.
///
/// Angle updates (mouse) happen in Update via <see cref="UpdateAngles"/>; the pivot rotation is
/// written in LateUpdate via <see cref="ApplyToTarget"/> — after the body has been posed — so the
/// camera never inherits the body rotation. Purely local; nothing here is networked.
/// </summary>
public class PlayerCameraRig
{
    private Transform _target;            // pivot the camera orbits (player CameraTarget, or a ship pivot while sailing)
    private readonly InputAction _look;
    private readonly InputAction _freeLook;

    private readonly float _sensitivity;
    private readonly float _pitchMin;
    private readonly float _pitchMax;
    private readonly float _freeReturnSharpness;

    private float _yaw;        // body + camera yaw (degrees)
    private float _pitch;      // camera pitch (degrees)
    private float _freeYaw;    // free-look yaw offset
    private float _freePitch;  // free-look pitch offset
    private float _shake;      // current camera-shake magnitude (deg), decays each frame
    private const float ShakeDecay = 11f;

    private CinemachineCamera _vcam;
    private CinemachineThirdPersonFollow _body;

    // Ship mode: while sailing, the ThirdPersonFollow body is disabled and the vcam is placed by hand at a
    // fixed distance from the ship's camera anchor — so the camera can never collapse onto a fast-moving
    // target the way an over-shoulder rig can, and always frames the whole ship from outside.
    private bool _shipMode;
    private ShipController _shipCtrl;
    private float _shipDistance;

    // Speed FOV: widen the lens a little while sprinting so acceleration reads on screen.
    private const float FovDelta = 10f;
    private const float FovSharpness = 8f;
    private float _baseFov;
    private float _fovBoost;        // eased 0..1
    private float _fovBoostTarget;  // 0 or 1

    /// <summary>Desired character facing (degrees). Frozen while free-look is held.</summary>
    public float BodyYaw => _yaw;

    /// <summary>Look pitch (degrees) used to bend the spine toward the aim direction.</summary>
    public float Pitch => _pitch;

    public PlayerCameraRig(Transform target, InputAction look, InputAction freeLook, float startYaw,
        float sensitivity = 0.15f, float pitchMin = -35f, float pitchMax = 70f, float freeReturnSharpness = 12f)
    {
        _target = target;
        _look = look;
        _freeLook = freeLook;
        _yaw = startYaw;
        _pitch = 10f;
        _sensitivity = sensitivity;
        _pitchMin = pitchMin;
        _pitchMax = pitchMax;
        _freeReturnSharpness = freeReturnSharpness;
    }

    public void BindSceneCamera()
    {
        _vcam = Object.FindFirstObjectByType<CinemachineCamera>();
        if (_vcam != null)
        {
            _vcam.Follow = _target;
            _vcam.LookAt = null;
            _baseFov = _vcam.Lens.FieldOfView;
            _body = _vcam.GetComponent<CinemachineThirdPersonFollow>();
        }
        else
        {
            Debug.LogWarning("[PlayerCameraRig] No CinemachineCamera found in the scene.");
        }
    }

    /// <summary>Call every frame on the owner (Update). Reads the mouse and updates the angles.</summary>
    public void UpdateAngles(float dt)
    {
        Vector2 look = _look != null ? _look.ReadValue<Vector2>() : Vector2.zero;
        bool free = _freeLook != null && _freeLook.IsPressed();

        float dx = look.x * _sensitivity;
        float dy = look.y * _sensitivity;

        if (free)
        {
            // Orbit the camera without turning the body.
            _freeYaw += dx;
            _freePitch = Mathf.Clamp(_freePitch - dy, _pitchMin - _pitch, _pitchMax - _pitch);
        }
        else
        {
            _yaw += dx;
            _pitch = Mathf.Clamp(_pitch - dy, _pitchMin, _pitchMax);

            // Ease the free-look offset back to zero so the camera returns behind the character.
            float k = 1f - Mathf.Exp(-_freeReturnSharpness * dt);
            _freeYaw = Mathf.Lerp(_freeYaw, 0f, k);
            _freePitch = Mathf.Lerp(_freePitch, 0f, k);
        }
    }

    /// <summary>Snap the look back behind a given facing (used when the owner respawns on a restart).</summary>
    public void ResetYaw(float yaw)
    {
        _yaw = yaw; _pitch = 10f; _freeYaw = 0f; _freePitch = 0f;
    }

    /// <summary>Re-point the camera at a new pivot (e.g. a ship's camera target while sailing, then back to
    /// the player). The vcam follows this pivot, so the same look/orbit rig now frames the new target.</summary>
    public void SetTarget(Transform target, float yaw)
    {
        _target = target;
        if (_vcam != null && target != null) _vcam.Follow = target;
        _yaw = yaw; _freeYaw = 0f; _freePitch = 0f;
    }

    /// <summary>Directly set the look pitch (deg). Used to frame a ship from a lower, level-ish angle.</summary>
    public void SetPitch(float pitch) => _pitch = pitch;

    /// <summary>Enter sailing mode: disable the over-shoulder body and hand-place the vcam a fixed distance from
    /// the ship's camera anchor (see <see cref="ApplyToTarget"/>). Robust for a fast-moving hull.</summary>
    public void EnterShipMode(ShipController ship, float distance)
    {
        if (_body == null && _vcam != null) _body = _vcam.GetComponent<CinemachineThirdPersonFollow>();
        _shipMode = true; _shipCtrl = ship; _shipDistance = distance;
        if (_body != null) _body.enabled = false;
    }

    /// <summary>Leave sailing mode: hand the vcam back to the over-shoulder body following the player.</summary>
    public void ExitShipMode()
    {
        _shipMode = false; _shipCtrl = null;
        if (_body != null) _body.enabled = true;
    }

    /// <summary>0 = normal FOV, 1 = full speed-boost FOV. Eased toward each frame.</summary>
    public void SetSpeedFactor(float t)
    {
        _fovBoostTarget = Mathf.Clamp01(t);
    }

    /// <summary>Kick the camera by up to <paramref name="degrees"/> of jitter (e.g. on a gun shot); decays quickly.</summary>
    public void AddShake(float degrees) => _shake = Mathf.Max(_shake, degrees);

    /// <summary>Call in LateUpdate after the body has been posed. Writes the pivot world rotation.</summary>
    public void ApplyToTarget()
    {
        // Re-acquire the scene camera if it went away (a scene reload destroys the old vcam and spawns a
        // fresh one with no Follow target — without this the camera would stop following after a restart).
        if (_vcam == null) BindSceneCamera();

        // Sailing: place the vcam ourselves at a fixed distance from the ship's (steady, un-rocked) anchor,
        // along the freelook direction. Because the offset is an explicit `distance`, the camera can never
        // slide onto the target, and it holds the same framing whether the ship is moving, turning, or still.
        if (_shipMode)
        {
            if (_shipCtrl != null && _vcam != null)
            {
                Quaternion look = Quaternion.Euler(_pitch + _freePitch, _yaw + _freeYaw, 0f);
                if (_shake > 0.01f)
                {
                    look *= Quaternion.Euler(Random.Range(-_shake, _shake), Random.Range(-_shake, _shake), 0f);
                    _shake = Mathf.Lerp(_shake, 0f, 1f - Mathf.Exp(-ShakeDecay * Time.deltaTime));
                }
                Vector3 anchor = _shipCtrl.CamAnchor;
                _vcam.transform.SetPositionAndRotation(anchor - look * Vector3.forward * _shipDistance, look);
                if (_baseFov > 0f) _vcam.Lens.FieldOfView = _baseFov;
            }
            return;
        }

        if (_target != null)
        {
            Quaternion rot = Quaternion.Euler(_pitch + _freePitch, _yaw + _freeYaw, 0f);
            if (_shake > 0.01f)
            {
                rot *= Quaternion.Euler(Random.Range(-_shake, _shake), Random.Range(-_shake, _shake), 0f);
                _shake = Mathf.Lerp(_shake, 0f, 1f - Mathf.Exp(-ShakeDecay * Time.deltaTime));
            }
            _target.rotation = rot;
        }

        if (_vcam != null && _baseFov > 0f)
        {
            _fovBoost = Mathf.Lerp(_fovBoost, _fovBoostTarget, 1f - Mathf.Exp(-FovSharpness * Time.deltaTime));
            _vcam.Lens.FieldOfView = _baseFov + FovDelta * _fovBoost;
        }
    }
}
