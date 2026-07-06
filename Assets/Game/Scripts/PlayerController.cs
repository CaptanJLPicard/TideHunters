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
    private Vector3 _spawnPos;
    private float _spawnYaw;
    private ShipController _drivingShip;   // the ship this player is currently driving (null = on foot)
    private ShipController _platform;      // ship we're STANDING on as a passenger (moving-platform carry)
    private Matrix4x4 _platformLastW2L;    // its world->local last frame, to carry our exact deck spot forward

    // Server-set on board; replicated so every client parks the driver at the ship's wheel + plays the pose.
    private readonly NetworkVariable<NetworkObjectReference> _shipRef = new(default,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>True while this player is steering a ship — other player scripts freeze their input / show the pose.</summary>
    public bool IsDriving => _drivingShip != null;
    public ShipController DrivingShip => _drivingShip;
    /// <summary>True while driving OR standing on a ship's deck (so we don't offer to climb aboard again).</summary>
    public bool IsRidingShip => _drivingShip != null || _platform != null;

    [Header("Ship camera")]
    [SerializeField] private float shipCamDistance = 15f; // how far the camera sits from the ship while sailing (whole ship in view)
    [SerializeField] private float shipCamPitch = 8f;     // a low, level-ish look at the ship (not from high above)

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

    /// <summary>True while in the water at all — swimming/treading OR standing with feet below the (wavy)
    /// surface. Owner-side check (uses this machine's live transform); used to forbid dropping items into the sea.</summary>
    public bool InWater
    {
        get
        {
            if (_current.IsSwimming) return true;
            float timeX = Time.timeSinceLevelLoad / 20f;
            float surface = motor.waterLevelY + OceanWaves.SurfaceOffsetY(transform.position, timeX,
                waveCount, waveLength, waveSteepness, waveSpeed, waveAmplitude);
            return transform.position.y < surface;
        }
    }

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
        _spawnPos = transform.position;
        _spawnYaw = transform.eulerAngles.y;
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
        _shipRef.OnValueChanged += OnShipRefChanged;

        if (IsOwner) SetupOwner();
        ResolveShip();

        NetworkManager.NetworkTickSystem.Tick += OnTick;
        if (NetworkManager.SceneManager != null)
            NetworkManager.SceneManager.OnLoadComplete += OnSceneLoadComplete;
    }

    // A scene reload (host restart) keeps player objects alive, so OnNetworkSpawn does NOT re-run: put the
    // owner back at its spawn here. (The camera rig re-binds itself to the new scene camera in ApplyToTarget.)
    private void OnSceneLoadComplete(ulong clientId, string sceneName, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        if (!IsOwner || clientId != NetworkManager.LocalClientId) return;
        Teleport(_spawnPos);
        _current.Position = _spawnPos;
        _current.Yaw = _spawnYaw;
        _current.VerticalVelocity = 0f;
        transform.rotation = Quaternion.Euler(0f, _spawnYaw, 0f);
        _cameraRig?.ResetYaw(_spawnYaw);
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
            NetworkManager.NetworkTickSystem.Tick -= OnTick;
        if (NetworkManager != null && NetworkManager.SceneManager != null)
            NetworkManager.SceneManager.OnLoadComplete -= OnSceneLoadComplete;
        _netState.OnValueChanged -= OnNetStateChanged;
        _shipRef.OnValueChanged -= OnShipRefChanged;

        if (IsOwner)
        {
            _playerMap?.Disable();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    // ---- Ship driving ----------------------------------------------------------------------

    private void OnShipRefChanged(NetworkObjectReference _, NetworkObjectReference __) => ResolveShip();

    private void ResolveShip()
    {
        ShipController ship = null;
        if (_shipRef.Value.TryGet(out var no)) ship = no.GetComponent<ShipController>();
        if (ship == _drivingShip) return;
        ShipController previous = _drivingShip;
        _drivingShip = ship;

        if (IsOwner)
        {
            if (_cc != null) _cc.enabled = _drivingShip == null; // detach the character controller while aboard
            if (_drivingShip != null)
            {
                _cameraRig?.ResetYaw(_drivingShip.transform.eulerAngles.y);
                _cameraRig?.SetPitch(shipCamPitch);
                _cameraRig?.EnterShipMode(_drivingShip, shipCamDistance);
            }
            else
            {
                // Step off ONTO the ship's CURRENT PlayerPoint (the ship may have sailed far since we took the
                // wheel) so we land on the deck — never in the sea where the moving ship used to be — and start
                // riding as a passenger immediately so the carry keeps us aboard.
                if (previous != null && previous.PlayerPoint != null)
                {
                    Vector3 spot = previous.PlayerPoint.position;
                    Teleport(spot);
                    _current.Position = spot;
                    _current.Yaw = previous.PlayerPoint.eulerAngles.y;
                    _current.VerticalVelocity = 0f;
                    _platform = previous;
                    _platformLastW2L = previous.transform.worldToLocalMatrix;
                }
                _cameraRig?.ExitShipMode();
                _cameraRig?.ResetYaw(transform.eulerAngles.y);
                _cameraRig?.SetPitch(10f);
            }
        }
    }

    /// <summary>Owner: ask the server to let this player take the wheel of <paramref name="ship"/>.</summary>
    public void RequestBoard(ShipController ship) { if (IsOwner && ship != null) BoardRpc(ship.NetworkObject); }
    /// <summary>Owner: ask the server to step off the wheel.</summary>
    public void RequestLeaveShip() { if (IsOwner && _drivingShip != null) LeaveRpc(); }

    /// <summary>Owner: climb from the water/dock onto a ship's deck and start riding it as a passenger. Movement
    /// is client-authoritative, so the owner places itself and the position replicates; the passenger carry
    /// then keeps it glued to the deck for everyone.</summary>
    public void BoardAsPassenger(ShipController ship)
    {
        if (!IsOwner || ship == null || _drivingShip != null) return;
        Vector3 spot = ship.DeckBoardPoint;
        Teleport(spot);
        _current.Position = spot;
        _current.Yaw = ship.transform.eulerAngles.y;
        _current.VerticalVelocity = 0f;
        _current.IsSwimming = false;
        _platform = ship;                                   // start riding immediately (no fall/slide gap)
        _platformLastW2L = ship.transform.worldToLocalMatrix;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void BoardRpc(NetworkObjectReference shipRef)
    {
        if (!shipRef.TryGet(out var no)) return;
        var ship = no.GetComponent<ShipController>();
        if (ship == null || ship.IsDriven) return; // someone already has the wheel
        _shipRef.Value = shipRef;
        ship.SetDriverServer(OwnerClientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void LeaveRpc()
    {
        if (_shipRef.Value.TryGet(out var no)) no.GetComponent<ShipController>()?.SetDriverServer(ShipController.NoDriver);
        _shipRef.Value = default;
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

        if (_drivingShip != null) { DriveUpdate(); return; }

        if (_jump != null && _jump.WasPressedThisFrame()) _jumpLatched = true;
        // Freeze the look while aiming the emote wheel or with the pause menu open.
        if (!EmoteWheel.IsOpen && !PauseMenu.IsOpen) _cameraRig?.UpdateAngles(Time.deltaTime);

        // Simulate this frame with real delta time -> frame-aligned, perfectly smooth motion.
        InputCommand cmd = SampleInput();
        cmd.Tick = NetworkManager.LocalTime.Tick;
        if (PauseMenu.IsOpen) { cmd.MoveX = 0f; cmd.MoveY = 0f; cmd.Jump = false; cmd.Sprint = false; _jumpLatched = false; }

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

    // Driving: parked at the ship's wheel. The ship reads its own W/S/A/D and moves us with it; here we just
    // pin to the PlayerPoint (which rocks with the ship) and keep orbiting the camera with the mouse.
    private void DriveUpdate()
    {
        if (!EmoteWheel.IsOpen && !PauseMenu.IsOpen) _cameraRig?.UpdateAngles(Time.deltaTime);
        var pp = _drivingShip.PlayerPoint;
        if (pp != null)
        {
            transform.SetPositionAndRotation(pp.position, pp.rotation);
            _current.Position = pp.position;
            _current.Yaw = pp.eulerAngles.y;
            _current.Speed = 0f; _current.MoveX = 0f; _current.MoveY = 0f;
            _current.Grounded = true; _current.IsSwimming = false;
        }
        _current.Tick = NetworkManager.LocalTime.Tick;
    }

    // Owner on foot: if standing on a ship, move with it (translation + heading + bob) each frame so we stay
    // put on the deck instead of sliding/jittering as the hull sails and rocks beneath us. Runs in LateUpdate
    // AFTER ShipController moved the hull (execution order), using the hull's flat pose (yaw only, no tilt) so
    // the character stays upright. The carried position replicates normally, so remotes see us ride along too.
    private void CarryOnShip()
    {
        ShipController ground = DetectGroundShip();
        if (ground != null && ground == _platform && _cc != null && _cc.enabled)
        {
            // Map our world position onto the deck with the FULL hull transform this frame (translation +
            // heading + wave pitch/roll/bob), so we track the exact spot we stand on — wave rock included —
            // and the ship never slides beneath our feet. Only the position is carried; we stay upright.
            Vector3 local = _platformLastW2L.MultiplyPoint3x4(transform.position);
            Vector3 carried = ground.transform.localToWorldMatrix.MultiplyPoint3x4(local);
            Vector3 delta = carried - transform.position;
            if (delta.sqrMagnitude > 1e-8f)
            {
                _cc.Move(delta);
                _current.Position = transform.position;
            }
        }
        _platform = ground;
        if (ground != null) _platformLastW2L = ground.transform.worldToLocalMatrix;
    }

    // The ship directly under our feet (or null). Skips our own collider.
    private ShipController DetectGroundShip()
    {
        if (_cc == null) return null;
        Bounds b = _cc.bounds;
        Vector3 origin = new Vector3(b.center.x, b.min.y + 0.3f, b.center.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, 0.8f, ~0, QueryTriggerInteraction.Ignore);
        ShipController best = null;
        float bestDist = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider.transform == transform || h.collider.transform.IsChildOf(transform)) continue; // self
            var s = h.collider.GetComponentInParent<ShipController>();
            if (s != null && h.distance < bestDist) { bestDist = h.distance; best = s; }
        }
        return best;
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
        StatePayload net = ToNetworkState();      // world pose, or ship-local while riding (so remotes glue us)
        if (IsServer) _netState.Value = net;      // host owner writes the NetworkVariable directly
        else SubmitStateRpc(net);                 // client owner ships it to the server to publish
    }

    // Build the state to replicate. On a ship, ship Position/Yaw as LOCAL to the hull + the ship's id, so every
    // remote rebuilds our world pose against that ship's own transform and we stay glued to the deck.
    private StatePayload ToNetworkState()
    {
        var s = _current;
        ShipController ride = _drivingShip != null ? _drivingShip : _platform;
        if (ride != null && ride.NetworkObject != null && ride.NetworkObject.IsSpawned)
        {
            s.PlatformId = ride.NetworkObject.NetworkObjectId;
            s.Position = ride.transform.InverseTransformPoint(_current.Position);
            s.Yaw = Mathf.DeltaAngle(ride.transform.eulerAngles.y, _current.Yaw);
        }
        else s.PlatformId = 0UL;
        return s;
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
        // A driver — owner OR remote — is pinned to the wheel here in LateUpdate, AFTER ShipController moved the
        // hull this frame (it runs earlier via execution order). Pinning post-move means the player never lags
        // a frame behind the rocking/sailing ship → no jitter, identical on host and clients.
        if (_drivingShip != null && _drivingShip.PlayerPoint != null)
        {
            var pp = _drivingShip.PlayerPoint;
            transform.SetPositionAndRotation(pp.position, pp.rotation);
        }
        else if (!_ownerSim)
        {
            // Non-drivers we don't own render from interpolated snapshots of their authoritative state.
            Interpolate(NetworkManager.ServerTime.Time - interpolationTicks * _dt);
        }
        else
        {
            // Owner on foot: ride any ship we're standing on so we stay put on the deck as it sails/rocks.
            CarryOnShip();
        }

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
        Vector3 pos;
        float yaw;
        if (b.PlatformId != 0UL)
        {
            // Riding a ship: Position/Yaw are ship-LOCAL. Rebuild against the ship's CURRENT transform so this
            // player stays glued to the (sailing, rocking) deck instead of sliding relative to it.
            if (!TryGetShipTransform(b.PlatformId, out var shipT)) return; // ship not resolvable — hold pose
            if (a.PlatformId == b.PlatformId)
            {
                Vector3 local = Vector3.Lerp(a.Position, b.Position, f);
                pos = shipT.TransformPoint(local);
                yaw = shipT.eulerAngles.y + Mathf.LerpAngle(a.Yaw, b.Yaw, f);
            }
            else // just boarded (prior snapshot was land / another ship): snap to b, no cross-space blend
            {
                pos = shipT.TransformPoint(b.Position);
                yaw = shipT.eulerAngles.y + b.Yaw;
            }
        }
        else
        {
            pos = Vector3.Lerp(a.Position, b.Position, f);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, f);
        }
        if (!IsFinite(pos)) return; // ignore a corrupt snapshot rather than snap the transform to NaN
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        _current = b;
        _current.Position = pos; // keep _current world-space for any downstream consumer
        _current.Yaw = yaw;
    }

    private bool TryGetShipTransform(ulong netId, out Transform t)
    {
        t = null;
        var nm = NetworkManager;
        if (nm != null && nm.SpawnManager != null &&
            nm.SpawnManager.SpawnedObjects.TryGetValue(netId, out var no) && no != null)
        { t = no.transform; return true; }
        return false;
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
