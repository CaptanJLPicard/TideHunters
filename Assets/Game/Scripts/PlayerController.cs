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

    [Header("Netcode / Prediction")]
    [Tooltip("Position error (metres) above which the owning client corrects toward the server.")]
    [SerializeField] private float reconcileThreshold = 0.2f;
    [Tooltip("How many ticks behind to render players seen from another machine.")]
    [SerializeField] private int interpolationTicks = 2;
    [SerializeField] private float animDampTime = 0.15f;

    // Cached components / children.
    private CharacterController _cc;
    private Transform _cameraTarget;
    private Transform _skeletonRoot;
    private Vector3 _skeletonBaseLocal;
    private PlayerAnimator _anim;
    private PlayerCameraRig _cameraRig;

    // Owner input actions.
    private InputActionMap _playerMap;
    private InputAction _move, _look, _sprint, _jump, _freeLook, _emote1, _emote2, _emote3;

    // Replicated authoritative state (server writes, everyone reads).
    private readonly NetworkVariable<StatePayload> _netState = new NetworkVariable<StatePayload>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private StatePayload _current;

    // Owner per-frame input latches.
    private bool _jumpLatched;
    private int _emoteLatched;
    private int _activeEmote;
    private InputCommand _lastOwnerInput;

    // Client-owner prediction history (predicted position per tick, for reconciliation).
    private const int BufferSize = 1024;
    private readonly Vector3[] _predHistory = new Vector3[BufferSize];
    private readonly int[] _predHistoryTick = new int[BufferSize];
    private int _lastReconciledTick = -1;
    private Vector3 _visualCorrection;   // decaying offset that hides reconciliation pops

    // Server input queue for a remote client's player.
    private readonly Queue<InputCommand> _serverInputs = new Queue<InputCommand>();
    private InputCommand _lastServerInput;

    // Snapshot interpolation buffer for players seen from another machine.
    private struct Snap { public double time; public StatePayload state; }
    private readonly List<Snap> _snaps = new List<Snap>(64);

    // Roles.
    private bool _ownerSim;          // this machine controls this player (host owner or client owner)
    private bool _clientOwner;       // pure-client owner (predicts + reconciles)
    private bool _serverRemote;      // host simulating another client's player
    private bool _clientRemote;      // client viewing another player
    private uint _tickRate = 60;
    private float _dt = 1f / 60f;

    public override void OnNetworkSpawn()
    {
        _cc = GetComponent<CharacterController>();
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
        _clientOwner = IsOwner && !IsServer;
        _serverRemote = IsServer && !IsOwner;
        _clientRemote = IsClient && !IsServer && !IsOwner;
        _cc.enabled = IsServer || IsOwner;

        _current = new StatePayload
        {
            Tick = NetworkManager.LocalTime.Tick,
            LastProcessedInputTick = NetworkManager.LocalTime.Tick,
            Position = transform.position,
            Yaw = transform.eulerAngles.y,
            Grounded = true,
        };
        if (IsServer) _netState.Value = _current;

        _netState.OnValueChanged += OnServerStateChanged;

        if (IsOwner) SetupOwner();

        NetworkManager.NetworkTickSystem.Tick += OnTick;
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
            NetworkManager.NetworkTickSystem.Tick -= OnTick;
        _netState.OnValueChanged -= OnServerStateChanged;

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
            _emote1 = _playerMap.FindAction("Emote1", true);
            _emote2 = _playerMap.FindAction("Emote2", true);
            _emote3 = _playerMap.FindAction("Emote3", true);
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
        int e = ReadEmote();
        if (e != 0) _emoteLatched = e;
        _cameraRig?.UpdateAngles(Time.deltaTime);

        // Simulate this frame with real delta time -> frame-aligned, perfectly smooth motion.
        InputCommand cmd = SampleInput();
        cmd.Tick = NetworkManager.LocalTime.Tick;
        _lastOwnerInput = cmd;

        StatePayload next = PlayerMotor.Simulate(_cc, _current, cmd, Time.deltaTime, motor);
        next.Tick = cmd.Tick;
        next.LastProcessedInputTick = cmd.Tick;
        _current = next; // transform is now at next.Position (set by CharacterController.Move)
    }

    private int ReadEmote()
    {
        if (_emote1 != null && _emote1.WasPressedThisFrame()) return 1;
        if (_emote2 != null && _emote2.WasPressedThisFrame()) return 2;
        if (_emote3 != null && _emote3.WasPressedThisFrame()) return 3;
        return 0;
    }

    private InputCommand SampleInput()
    {
        Vector2 mv = _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
        bool sprint = _sprint != null && _sprint.IsPressed();
        float yaw = _cameraRig != null ? _cameraRig.BodyYaw : transform.eulerAngles.y;

        if (_emoteLatched != 0) _activeEmote = _emoteLatched;
        if (mv.sqrMagnitude > 0.02f || _jumpLatched) _activeEmote = 0;

        var cmd = new InputCommand
        {
            MoveX = mv.x,
            MoveY = mv.y,
            Yaw = yaw,
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
        int tick = NetworkManager.LocalTime.Tick;

        if (IsServer && IsOwner)
        {
            // Host's own player: already simulated per-frame and authoritative — just replicate.
            _current.Tick = tick;
            _netState.Value = _current;
        }
        else if (_serverRemote)
        {
            ServerTickRemote(tick);
        }
        else if (_clientOwner)
        {
            // Record where prediction says we are this tick, then send input to the server.
            _predHistory[tick % BufferSize] = _current.Position;
            _predHistoryTick[tick % BufferSize] = tick;
            SubmitInputRpc(_lastOwnerInput);
        }
    }

    private void ServerTickRemote(int tick)
    {
        if (_serverInputs.Count > 0) _lastServerInput = _serverInputs.Dequeue();
        InputCommand cmd = _lastServerInput;

        if ((transform.position - _current.Position).sqrMagnitude > 1e-4f) Teleport(_current.Position);
        StatePayload next = PlayerMotor.Simulate(_cc, _current, cmd, _dt, motor);
        next.Tick = tick;
        next.LastProcessedInputTick = cmd.Tick;
        _current = next;

        _snaps.Add(new Snap { time = tick * (double)_dt, state = next });
        while (_snaps.Count > 32) _snaps.RemoveAt(0);

        _netState.Value = next;
    }

    [Rpc(SendTo.Server)]
    private void SubmitInputRpc(InputCommand cmd, RpcParams rpc = default)
    {
        _serverInputs.Enqueue(cmd);
        while (_serverInputs.Count > 12) _serverInputs.Dequeue();
    }

    // ---- Reconciliation (client owner) -----------------------------------------------------

    private void OnServerStateChanged(StatePayload previous, StatePayload next)
    {
        if (_clientRemote)
        {
            _snaps.Add(new Snap { time = next.Tick * (double)_dt, state = next });
            while (_snaps.Count > 64) _snaps.RemoveAt(0);
        }
        else if (_clientOwner)
        {
            ReconcileClient(next);
        }
        // host owner: authoritative locally → ignore
    }

    private void ReconcileClient(StatePayload server)
    {
        int t = server.LastProcessedInputTick;
        if (t <= _lastReconciledTick || _predHistoryTick[t % BufferSize] != t) return;
        _lastReconciledTick = t;

        // Compare the server's authoritative position at tick t with what we predicted then.
        Vector3 error = server.Position - _predHistory[t % BufferSize];
        if (error.sqrMagnitude < reconcileThreshold * reconcileThreshold) return;

        // Shift our current prediction by the error (keeps us near the server without rubber-banding),
        // and add the visible jump to a decaying offset so the correction isn't a hard pop.
        _visualCorrection += error;
        _current.Position += error;
        Teleport(_current.Position);
    }

    // ---- Rendering -------------------------------------------------------------------------

    private void LateUpdate()
    {
        if (_clientRemote)
        {
            InterpolateRemote();
        }
        else if (_serverRemote)
        {
            InterpolateLocalSnaps();
        }
        // owner: transform already holds the per-frame sim result — nothing to do.

        ApplyVisual();
        if (_cameraRig != null)
        {
            // Widen FOV when actually sprinting-and-moving (running on land or sprint-swimming).
            bool fast = _current.Sprinting && (_current.IsSwimming ? _current.Speed > 0.05f : _current.Speed > 0.6f);
            _cameraRig.SetSpeedFactor(fast ? 1f : 0f);
        }
        _cameraRig?.ApplyToTarget();
        _anim.Apply(_current, Time.deltaTime);
    }

    // Skeleton-only visual offset: ocean wave bob while swimming + decaying reconciliation smoothing.
    private void ApplyVisual()
    {
        if (_skeletonRoot == null) return;
        float bob = 0f;
        if (_current.IsSwimming)
        {
            float timeX = Time.timeSinceLevelLoad / 20f;
            bob = OceanWaves.SurfaceOffsetY(transform.position, timeX, waveCount, waveLength, waveSteepness, waveSpeed, waveAmplitude);
        }
        // ease the reconciliation offset back to zero
        if (_visualCorrection.sqrMagnitude > 1e-6f)
            _visualCorrection = Vector3.Lerp(_visualCorrection, Vector3.zero, 1f - Mathf.Exp(-12f * Time.deltaTime));
        else
            _visualCorrection = Vector3.zero;

        _skeletonRoot.localPosition = _skeletonBaseLocal + Vector3.up * bob
            + transform.InverseTransformVector(-_visualCorrection);
    }

    private void InterpolateRemote()
    {
        Interpolate(NetworkManager.ServerTime.Time - interpolationTicks * _dt);
    }

    private void InterpolateLocalSnaps()
    {
        Interpolate(NetworkManager.ServerTime.Time - interpolationTicks * _dt);
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
}
