using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Server-authoritative third-person (shooter/strafe) player controller.
///
/// The locally-controlled player (owner — whether host or client) is simulated EVERY FRAME in
/// Update using Time.deltaTime, so its motion is perfectly frame-aligned and smooth at any frame
/// rate (no interpolation, no tick stepping). The host's owner is authoritative; a client's owner
/// predicts and reconciles against the server. Players seen from another machine are interpolated
/// from the replicated authoritative snapshots.
///
///  - Owner (host or client)          : per-frame sim in Update; transform is the result.
///  - Host viewing a remote player    : tick sim from that client's input + snapshot interpolation.
///  - Client viewing a remote player  : snapshot interpolation of the replicated state.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class PlayerController : NetworkBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputAsset;

    [Header("Movement (tunable)")]
    [SerializeField] private MotorConfig motor = MotorConfig.Default;

    [Header("Camera")]
    [SerializeField] private float mouseSensitivity = 0.15f;
    [SerializeField] private float pitchMin = -35f;
    [SerializeField] private float pitchMax = 70f;

    [Header("Swim waves (visual bob — matches the scene water's Gerstner surface)")]
    [SerializeField] private int waveCount = 4;
    [SerializeField] private float waveLength = 4.22f;
    [SerializeField] private float waveSteepness = 0.105f;
    [SerializeField] private float waveSpeed = 20f;
    [SerializeField] private float waveAmplitude = 0.431f;

    [Header("Netcode")]
    [Tooltip("How many ticks behind to render players seen from another machine.")]
    [SerializeField] private int interpolationTicks = 2;
    [SerializeField] private float animDampTime = 0.15f;

    [Header("Carrying")]
    [Tooltip("Walk-speed multiplier applied while carrying a chest (sprint is also disabled).")]
    [SerializeField] private float carrySpeedMultiplier = 0.8f;

    // Cached components / children.
    private CharacterController _cc;
    private Transform _cameraTarget;
    private Transform _skeletonRoot;
    private Vector3 _skeletonBaseLocal;
    private PlayerAnimator _anim;
    private PlayerCameraRig _cameraRig;
    private PlayerInventory _inv;

    // Owner input actions.
    private InputActionMap _playerMap;
    private InputAction _move, _look, _sprint, _jump, _freeLook;

    // Replicated authoritative state. Movement is client-authoritative: the player's OWNER simulates
    // locally and its state is exactly what everyone else sees — the server does not re-simulate or
    // reconcile, so the owner never rubber-bands against a second simulation. A non-host owner can't write
    // a NetworkVariable directly, so it ships its state via SubmitStateRpc; the server re-stamps it with
    // the server tick and publishes it here, keeping every viewer on one interpolation clock.
    private readonly NetworkVariable<StatePayload> _netState = new NetworkVariable<StatePayload>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private StatePayload _current;

    // Owner per-frame input latches.
    private bool _jumpLatched;
    private int _emoteLatched;
    private int _activeEmote;

    // Snapshot interpolation buffer for players seen from another machine.
    private struct Snap { public double time; public StatePayload state; }
    private readonly List<Snap> _snaps = new List<Snap>(64);

    // Roles.
    private bool _ownerSim;   // this machine owns + simulates this player (host owner or client owner)
    private uint _tickRate = 60;
    private float _dt = 1f / 60f;

    /// <summary>Current look pitch (deg), authoritative/predicted — drives the spine aim on every client.</summary>
    public float AimPitch => _current.Pitch;

    /// <summary>Horizontal aim offset (deg) between where the camera aims and the body faces — spine twist.</summary>
    public float AimYawOffset => Mathf.DeltaAngle(_current.Yaw, _current.AimYaw);

    /// <summary>Current planar movement speed (m/s), authoritative/predicted.</summary>
    public float PlanarSpeed => _current.Speed;

    /// <summary>True while this player is in the water (swimming / treading), replicated to every client.</summary>
    public bool IsSwimming => _current.IsSwimming;

    /// <summary>Owner camera yaw (deg). Its recent change = how the player is sweeping the view left/right.</summary>
    public float CameraYaw => _cameraRig != null ? _cameraRig.BodyYaw : 0f;

    /// <summary>World aim direction (where the crosshair points), from replicated yaw+pitch — valid on every client.</summary>
    public Vector3 AimDirection => Quaternion.Euler(_current.Pitch, _current.AimYaw, 0f) * Vector3.forward;

    /// <summary>Kick the owner's camera (e.g. on a gun shot). No-op on non-owners.</summary>
    public void ShakeCamera(float degrees) => _cameraRig?.AddShake(degrees);

    public override void OnNetworkSpawn()
    {
        _cc = GetComponent<CharacterController>();
        _inv = GetComponent<PlayerInventory>();
        _cameraTarget = transform.Find("CameraTarget");
        _skeletonRoot = transform.Find("Root");
        if (_skeletonRoot != null) _skeletonBaseLocal = _skeletonRoot.localPosition;
        var animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        _anim = new PlayerAnimator(animator, animDampTime, motor.walkSpeed, motor.runSpeed,
            motor.swimSpeed, motor.swimSprintSpeed);

        _tickRate = NetworkManager.NetworkConfig.TickRate == 0 ? 60u : NetworkManager.NetworkConfig.TickRate;
        _dt = 1f / _tickRate;

        _ownerSim = IsOwner;
        _cc.enabled = IsOwner; // only the owner simulates + collides; everyone else interpolates its state

        _current = new StatePayload
        {
            Tick = NetworkManager.LocalTime.Tick,
            LastProcessedInputTick = NetworkManager.LocalTime.Tick,
            Position = transform.position,
            Yaw = transform.eulerAngles.y,
            Grounded = true,
        };
        if (IsServer) _netState.Value = _current;

        _netState.OnValueChanged += OnNetStateChanged;

        if (IsOwner) SetupOwner();

        NetworkManager.NetworkTickSystem.Tick += OnTick;
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
            NetworkManager.NetworkTickSystem.Tick -= OnTick;
        _netState.OnValueChanged -= OnNetStateChanged;

        if (IsOwner)
        {
            _playerMap?.Disable();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void SetupOwner()
    {
        if (inputAsset != null)
        {
            _playerMap = inputAsset.FindActionMap("Player", true);
            _playerMap.Enable();
            _move = _playerMap.FindAction("Move", true);
            _look = _playerMap.FindAction("Look", true);
            _sprint = _playerMap.FindAction("Sprint", true);
            _jump = _playerMap.FindAction("Jump", true);
            _freeLook = _playerMap.FindAction("FreeLook", true);
        }
        else
        {
            Debug.LogWarning("[PlayerController] inputAsset not assigned; owner input is disabled.");
        }

        float startYaw = transform.eulerAngles.y;
        _cameraRig = new PlayerCameraRig(_cameraTarget, _look, _freeLook, startYaw,
            mouseSensitivity, pitchMin, pitchMax);
        _cameraRig.BindSceneCamera();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ---- Per-frame owner simulation --------------------------------------------------------

    private void Update()
    {
        if (!_ownerSim) return;

        if (_jump != null && _jump.WasPressedThisFrame()) _jumpLatched = true;
        if (!EmoteWheel.IsOpen) _cameraRig?.UpdateAngles(Time.deltaTime); // freeze look while aiming the emote wheel

        // Simulate this frame with real delta time -> frame-aligned, perfectly smooth motion.
        InputCommand cmd = SampleInput();
        cmd.Tick = NetworkManager.LocalTime.Tick;

        // Carrying a chest: hands full, so no sprint and a heavier, slower walk. The animator's locomotion
        // playback is scaled to match (see LateUpdate) so the reduced walk still plants its feet.
        MotorConfig m = motor;
        if (_inv != null && _inv.IsCarryingChest)
        {
            cmd.Sprint = false;
            m.walkSpeed *= carrySpeedMultiplier;
            m.runSpeed = m.walkSpeed;
        }

        StatePayload next = PlayerMotor.Simulate(_cc, _current, cmd, Time.deltaTime, m);
        next.Tick = cmd.Tick;
        next.LastProcessedInputTick = cmd.Tick;
        if (IsFinite(next.Position)) _current = next; // transform is now at next.Position (set by CharacterController.Move)
        else Teleport(_current.Position);             // sim produced a non-finite pose — hold the last good one
    }

    /// <summary>Play an emote (1..3) — called by the emote wheel. Owner only; movement/jump cancels it.</summary>
    public void TriggerEmote(int id) { if (_ownerSim && id >= 1 && id <= 3) _emoteLatched = id; }

    private InputCommand SampleInput()
    {
        Vector2 mv = _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
        bool sprint = _sprint != null && _sprint.IsPressed();
        float yaw = _cameraRig != null ? _cameraRig.BodyYaw : transform.eulerAngles.y;
        float pitch = _cameraRig != null ? _cameraRig.Pitch : 0f;

        if (_emoteLatched != 0) _activeEmote = _emoteLatched;
        if (mv.sqrMagnitude > 0.02f || _jumpLatched) _activeEmote = 0;

        var cmd = new InputCommand
        {
            MoveX = mv.x,
            MoveY = mv.y,
            Yaw = yaw,
            Pitch = pitch,
            Sprint = sprint,
            Jump = _jumpLatched,
            Emote = _activeEmote,
        };
        _jumpLatched = false;
        _emoteLatched = 0;
        return cmd;
    }

    // ---- Network tick ----------------------------------------------------------------------

    private void OnTick()
    {
        if (!_ownerSim) return; // only the owner advances + publishes this player's authoritative state

        _current.Tick = NetworkManager.LocalTime.Tick;
        if (IsServer) _netState.Value = _current; // host owner writes the NetworkVariable directly
        else SubmitStateRpc(_current);            // client owner ships it to the server to publish
    }

    // Client owner → server: hand the server this player's authoritative state so it can publish it to
    // everyone (a non-host owner may not write the NetworkVariable itself).
    [Rpc(SendTo.Server)]
    private void SubmitStateRpc(StatePayload state)
    {
        state.Tick = NetworkManager.LocalTime.Tick; // re-stamp onto the server clock so all viewers share it
        _netState.Value = state;
    }

    private void OnNetStateChanged(StatePayload previous, StatePayload next)
    {
        if (_ownerSim) return; // our own local simulation is authoritative — never fight it

        _snaps.Add(new Snap { time = next.Tick * (double)_dt, state = next });
        while (_snaps.Count > 64) _snaps.RemoveAt(0);
    }

    // ---- Rendering -------------------------------------------------------------------------

    private void LateUpdate()
    {
        // Anyone who doesn't own this player renders it from interpolated snapshots of its authoritative
        // state. The owner already holds the live per-frame sim result on its transform — nothing to do.
        if (!_ownerSim)
            Interpolate(NetworkManager.ServerTime.Time - interpolationTicks * _dt);

        ApplyVisual();
        if (_cameraRig != null)
        {
            // Widen FOV when actually sprinting-and-moving (running on land or sprint-swimming).
            bool fast = _current.Sprinting && (_current.IsSwimming ? _current.Speed > 0.05f : _current.Speed > 0.6f);
            _cameraRig.SetSpeedFactor(fast ? 1f : 0f);
        }
        _cameraRig?.ApplyToTarget();
        // Match the animator's locomotion playback to the (reduced) ground speed while carrying, so the
        // walk clip's feet stay planted instead of skating.
        if (_anim != null) _anim.LocomotionScale = (_inv != null && _inv.IsCarryingChest) ? carrySpeedMultiplier : 1f;
        _anim?.Apply(_current, Time.deltaTime);
    }

    // Skeleton-only visual offset: ocean wave bob while swimming.
    private void ApplyVisual()
    {
        if (_skeletonRoot == null) return;
        float bob = 0f;
        if (_current.IsSwimming)
        {
            float timeX = Time.timeSinceLevelLoad / 20f;
            bob = OceanWaves.SurfaceOffsetY(transform.position, timeX, waveCount, waveLength, waveSteepness, waveSpeed, waveAmplitude);
        }
        _skeletonRoot.localPosition = _skeletonBaseLocal + Vector3.up * bob;
    }

    private void Interpolate(double renderTime)
    {
        if (_snaps.Count == 0) return;
        if (_snaps.Count == 1 || renderTime <= _snaps[0].time) { ApplySnap(_snaps[0].state, _snaps[0].state, 0f); return; }
        for (int i = 0; i < _snaps.Count - 1; i++)
        {
            if (renderTime >= _snaps[i].time && renderTime <= _snaps[i + 1].time)
            {
                double span = _snaps[i + 1].time - _snaps[i].time;
                float f = span > 0.0 ? (float)((renderTime - _snaps[i].time) / span) : 0f;
                ApplySnap(_snaps[i].state, _snaps[i + 1].state, f);
                if (i > 1) _snaps.RemoveRange(0, i - 1);
                return;
            }
        }
        ApplySnap(_snaps[_snaps.Count - 1].state, _snaps[_snaps.Count - 1].state, 0f);
    }

    private void ApplySnap(in StatePayload a, in StatePayload b, float f)
    {
        Vector3 pos = Vector3.Lerp(a.Position, b.Position, f);
        if (!IsFinite(pos)) return; // ignore a corrupt snapshot rather than snap the transform to NaN
        float yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, f);
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        _current = b;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private void Teleport(Vector3 pos)
    {
        bool was = _cc.enabled;
        _cc.enabled = false;
        transform.position = pos;
        _cc.enabled = was;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
}
