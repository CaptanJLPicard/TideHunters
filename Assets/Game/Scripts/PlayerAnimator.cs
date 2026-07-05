using UnityEngine;

/// <summary>
/// Drives the Animator from a movement snapshot, using damping on the blend-tree floats
/// so idle/walk/run and 8-directional strafe transitions stay smooth. Owner, host and
/// remote instances all feed their current <see cref="StatePayload"/> through here, so the
/// visible animation always matches the (authoritative or predicted) movement state.
/// </summary>
public class PlayerAnimator
{
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int MoveXHash = Animator.StringToHash("MoveX");
    private static readonly int MoveYHash = Animator.StringToHash("MoveY");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");
    private static readonly int SwimHash = Animator.StringToHash("IsSwimming");
    private static readonly int EmoteHash = Animator.StringToHash("EmoteId");
    private static readonly int JumpHash = Animator.StringToHash("Jump");
    private static readonly int FallingHash = Animator.StringToHash("Falling");

    // Measured horizontal stride speed of the source locomotion clips (m/s). Used to scale playback so
    // the feet plant at the ground speed instead of skating.
    private const float WalkStride = 1.41f;
    private const float RunStride = 3.6f;

    private readonly Animator _animator;
    private readonly float _damp;
    private readonly float _walkSpeed;
    private readonly float _runSpeed;
    private readonly float _swimSprintMult;
    private int _lastJumpStamp;
    private bool _initialised;

    public PlayerAnimator(Animator animator, float dampTime, float walkSpeed, float runSpeed,
        float swimSpeed, float swimSprintSpeed)
    {
        _animator = animator;
        _damp = dampTime;
        _walkSpeed = walkSpeed;
        _runSpeed = runSpeed;
        _swimSprintMult = swimSpeed > 0.01f ? swimSprintSpeed / swimSpeed : 1.6f;
    }

    public void Apply(in StatePayload s, float dt)
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        _animator.SetFloat(SpeedHash, s.Speed, _damp, dt);
        _animator.SetFloat(MoveXHash, s.MoveX, _damp, dt);
        _animator.SetFloat(MoveYHash, s.MoveY, _damp, dt);
        _animator.SetBool(GroundedHash, s.Grounded);
        _animator.SetBool(SwimHash, s.IsSwimming);
        _animator.SetBool(FallingHash, !s.Grounded && !s.IsSwimming && s.VerticalVelocity < -0.5f);
        _animator.SetInteger(EmoteHash, s.Emote);

        // Anti-slide: play the locomotion clips at the speed that matches the actual ground speed, so
        // the planted foot doesn't skate. Only while running on the ground (jump/fall/swim/idle = 1x).
        float animSpeed = 1f;
        if (s.Grounded && !s.IsSwimming && s.Speed > 0.05f)
        {
            float walkMult = _walkSpeed / WalkStride;
            float runMult = _runSpeed / RunStride;
            animSpeed = Mathf.Lerp(walkMult, runMult, Mathf.InverseLerp(0.5f, 1f, s.Speed));
        }
        else if (s.IsSwimming && s.Speed > 0.05f && s.Sprinting)
        {
            animSpeed = _swimSprintMult; // faster swim strokes while sprinting
        }
        _animator.speed = animSpeed;

        if (!_initialised)
        {
            _lastJumpStamp = s.JumpStamp;
            _initialised = true;
        }
        else if (s.JumpStamp != _lastJumpStamp)
        {
            _lastJumpStamp = s.JumpStamp;
            _animator.SetTrigger(JumpHash);
        }
    }
}
