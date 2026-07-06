using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Aim-driven spine deformation. On top of the animated pose (runs after the Animator), it:
///  - bends the torso up/down toward the look pitch (aim up = lean back, aim down = hunch forward), and
///  - twists the torso left/right toward the camera/aim yaw, clamped, so the character can aim to the
///    side of its body facing (essential for an aim system) without turning the whole body — past the
///    clamp it simply stops turning.
///
/// Both pitch and the aim-yaw offset come from <see cref="PlayerController"/> authoritative/replicated
/// state, so every client (owner and remotes) deforms the spine in sync. The bend is weighted toward
/// the lower spine so it originates low in the back and reads as a full-torso lean.
/// </summary>
[DefaultExecutionOrder(200)]
[RequireComponent(typeof(Animator))]
public class PlayerSpineAim : MonoBehaviour
{
    [Header("Pitch (up/down)")]
    [Tooltip("Torso bend (deg) per degree of look pitch. 1 = the torso matches the camera pitch 1:1. " +
             "Positive: look down hunches forward.")]
    [SerializeField] private float pitchGain = 1f;
    [Tooltip("Clamp on the torso pitch bend. 70 matches the camera's downward pitch limit so it never clamps early.")]
    [SerializeField] private float maxPitchBend = 70f;

    [Header("Yaw (left/right aim twist)")]
    [Tooltip("Max horizontal twist (deg) the torso aims off the body facing before it stops.")]
    [SerializeField] private float maxYaw = 70f;
    [Tooltip("While walking (and not aiming) the yaw twist is scaled down to this, so the upper body does " +
             "not sway/lean as the body turns. 1 = no reduction. Full twist returns when aiming or standing still.")]
    [Range(0f, 1f)][SerializeField] private float walkYawScale = 0.35f;

    [SerializeField] private float smoothSharpness = 14f;
    [Tooltip("Bend share per spine bone, low→high. Favors the lower spine so the bend starts low.")]
    [SerializeField] private float[] boneWeights = { 0.45f, 0.35f, 0.20f };

    [Header("Gun aim (arm)")]
    [Tooltip("While aiming a gun, the right upper arm is rotated so the forearm/gun points along the " +
             "camera's view — this holds true at any pitch (unlike a fixed spine twist). This is a small " +
             "fine-tune (deg: x=pitch, y=yaw) added to that target direction, in case the muzzle sits off " +
             "the forearm. Public — set it live from the Inspector or from any script.")]
    public Vector3 aimOffsetEuler = Vector3.zero;
    [Tooltip("Clamp (deg) on how far off the body facing the arm will aim horizontally, so it never reaches " +
             "around backwards when the camera looks behind the character (mirrors the torso yaw clamp).")]
    [SerializeField] private float armMaxYaw = 92f;
    [SerializeField] private float aimBlendSpeed = 6f;

    private PlayerController _pc;
    private PlayerCombat _combat;
    private Animator _animator;
    private Transform[] _spine;
    private Transform _rUpperArm, _rLowerArm, _rHand;
    private float _smoothPitch, _smoothYaw, _aimBlend;

    private void Start()
    {
        _pc = GetComponent<PlayerController>();
        _combat = GetComponent<PlayerCombat>();
        _animator = GetComponent<Animator>();

        var bones = new List<Transform>();
        foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
        {
            var t = _animator.GetBoneTransform(b);
            if (t != null) bones.Add(t);
        }
        _spine = bones.ToArray();

        _rUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        _rLowerArm = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        _rHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
    }

    /// <summary>
    /// Editor debug read-out: horizontal angle (deg) between the aiming forearm and the camera forward
    /// (the crosshair). Tune <see cref="aimOffsetEuler"/>.y until this reaches ~0 → the gun points at the
    /// crosshair. Returns 0 outside play mode / when bones or camera are unavailable.
    /// </summary>
    public float DebugForearmYawError()
    {
        if (_animator == null) return 0f;
        var cam = Camera.main;
        var hand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
        var elbow = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        if (cam == null || hand == null || elbow == null) return 0f;
        Vector3 forearmH = Vector3.ProjectOnPlane(hand.position - elbow.position, Vector3.up).normalized;
        Vector3 camH = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
        return Mathf.DeltaAngle(Mathf.Atan2(forearmH.x, forearmH.z) * Mathf.Rad2Deg,
                                Mathf.Atan2(camH.x, camH.z) * Mathf.Rad2Deg);
    }

    private void LateUpdate()
    {
        if (_pc == null || _spine == null || _spine.Length == 0) return;

        float pitchTarget = Mathf.Clamp(_pc.AimPitch * pitchGain, -maxPitchBend, maxPitchBend);
        // While walking (and not aiming) fade the yaw twist down so the torso stops swaying as the body turns.
        float moving = Mathf.Clamp01(_pc.PlanarSpeed / 1.5f);
        float yawScale = Mathf.Lerp(1f, walkYawScale, moving * (1f - _aimBlend));
        float yawTarget = Mathf.Clamp(_pc.AimYawOffset, -maxYaw, maxYaw) * yawScale;

        float k = 1f - Mathf.Exp(-smoothSharpness * Time.deltaTime);
        _smoothPitch = Mathf.Lerp(_smoothPitch, pitchTarget, k);
        _smoothYaw = Mathf.Lerp(_smoothYaw, yawTarget, k);

        float aimTarget = (_combat != null && _combat.AimingGun) ? 1f : 0f;
        _aimBlend = Mathf.MoveTowards(_aimBlend, aimTarget, aimBlendSpeed * Time.deltaTime);

        Vector3 right = transform.right, up = transform.up;
        for (int i = 0; i < _spine.Length; i++)
        {
            float w = i < boneWeights.Length ? boneWeights[i] : 1f / _spine.Length;
            Quaternion add = Quaternion.AngleAxis(_smoothYaw * w, up) * Quaternion.AngleAxis(_smoothPitch * w, right);
            _spine[i].rotation = add * _spine[i].rotation;
        }

        AimArm();
    }

    /// <summary>
    /// While aiming a gun, rotates the right upper arm so the forearm (≈ the gun barrel) points along the
    /// camera's view — i.e. straight at the crosshair — regardless of pitch. Runs after the spine bend, so
    /// it corrects the residual and always lands the gun on the crosshair (down, level or up).
    /// </summary>
    private void AimArm()
    {
        if (_aimBlend <= 0.001f || _rUpperArm == null || _rLowerArm == null || _rHand == null || _pc == null) return;

        Vector3 gunDir = _rHand.position - _rLowerArm.position;               // forearm ≈ gun barrel
        if (gunDir.sqrMagnitude < 1e-6f) return;

        // Aim along the REPLICATED aim direction (not the live camera). This is correct for every player and
        // — because it stays frozen while free-look (Alt) orbits the camera — the arm stops tracking the
        // crosshair until free-look is released.
        Vector3 aimDir = _pc.AimDirection;
        Vector3 right = Vector3.Cross(Vector3.up, aimDir);
        right = right.sqrMagnitude > 1e-4f ? right.normalized : transform.right;
        Vector3 target = Quaternion.AngleAxis(aimOffsetEuler.y, Vector3.up)
                       * Quaternion.AngleAxis(aimOffsetEuler.x, right)
                       * aimDir;

        // Clamp the horizontal aim relative to the body facing so the arm never wraps around backwards.
        Vector3 flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        Vector3 flatTarget = Vector3.ProjectOnPlane(target, Vector3.up);
        if (flatFwd.sqrMagnitude > 1e-6f && flatTarget.sqrMagnitude > 1e-6f)
        {
            float yaw = Vector3.SignedAngle(flatFwd, flatTarget, Vector3.up);
            float clamped = Mathf.Clamp(yaw, -armMaxYaw, armMaxYaw);
            if (Mathf.Abs(clamped - yaw) > 0.01f)
                target = Quaternion.AngleAxis(clamped - yaw, Vector3.up) * target;
        }

        Quaternion aim = Quaternion.FromToRotation(gunDir, target);
        aim = Quaternion.Slerp(Quaternion.identity, aim, _aimBlend);
        _rUpperArm.rotation = aim * _rUpperArm.rotation;
    }
}
