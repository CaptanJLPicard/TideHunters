using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A server-authoritative enemy crew member. It is deliberately "the player, minus the human": it reuses the
/// player's <see cref="PlayerMotor"/>, <see cref="PlayerAnimator"/> and the deck-riding carry
/// (<see cref="DeckRider"/>) unchanged, and only adds a brain + sensors + gun combat. NavMesh is not used —
/// it walks the moving deck with the same downward-raycast carry the player uses, plus short forward/edge
/// probes so it never strolls off the hull, and separation from the other crew so they don't clip through
/// each other.
///
/// Netcode: only the SERVER simulates; the resulting <see cref="StatePayload"/> (ship-LOCAL while aboard, so
/// it stays glued to the rocking deck) + posture + gun state replicate, and every other client interpolates
/// it exactly like a remote player. The brain reads the tactical picture from its <see cref="EnemyShipAI"/>
/// commander (idle → alarm → repel boarders → counter-board), so the whole crew acts as one.
///
/// It implements <see cref="IAimSource"/>, so the SAME <see cref="PlayerSpineAim"/> that aims the player's gun
/// aims the NPC's — the barrel tracks its target with the identical spine-bend + arm IK.
/// </summary>
[DefaultExecutionOrder(-90)] // after ShipController (-100) so the hull has moved; before default LateUpdaters
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(Health))]
public class EnemyNpcController : NetworkBehaviour, IAimSource
{
    // ---- Posture ids (idle animation at a post) ----
    public const int PostureNone = 0, PostureKneel = 1, PostureWarrior = 2, PostureSit = 3, PostureCarry = 4;

    [Header("Movement")]
    [SerializeField] private MotorConfig motor = MotorConfig.Default;
    [SerializeField] private float animDampTime = 0.15f;

    [Header("Assignment (per NPC)")]
    [Tooltip("The crew's ship/commander. If null it finds the nearest EnemyShipAI at spawn.")]
    [SerializeField] private EnemyShipAI ship;
    [Tooltip("Where this NPC idles before combat (a child of the ship — an NPC_Points entry).")]
    [SerializeField] private Transform homePoint;
    [Tooltip("Idle posture at the home point: 1=Kneel, 2=Warrior, 3=Sit, 4=Carry(driver).")]
    [SerializeField] private int homePosture = PostureWarrior;

    [Header("Combat (HeavyGun)")]
    [SerializeField] private Transform gunMuzzle;         // Muzzle child of the held HeavyGun
    [SerializeField] private GameObject muzzleFx;         // same flash prefab the player's gun uses
    [SerializeField] private float muzzleFxLifetime = 3f;
    [SerializeField] private float muzzleFxScale = 0.5f;
    [Header("Bullet (physical + tracer)")]
    [SerializeField] private GameObject bulletPrefab;
    [SerializeField] private GameObject bulletImpactFx;
    [SerializeField] private float bulletSpeed = 90f;
    [SerializeField] private float bulletGravity = 0f;
    [SerializeField] private float bulletLife = 1.2f;
    [Header("Combat tuning")]
    [SerializeField] private int gunDamage = 20;
    [SerializeField] private float gunRange = 26f;
    [SerializeField] private float fireInterval = 1.2f;
    [Tooltip("Aim spread (deg). Larger = less accurate. The chest carrier is prioritised but still shot with spread.")]
    [SerializeField] private float aimSpread = 4f;
    [SerializeField] private float keepDistance = 7f;     // preferred gap from the target while repelling

    [Header("Sensors (no NavMesh)")]
    [SerializeField] private float edgeProbe = 1.0f;      // look this far ahead for a deck edge
    [SerializeField] private float edgeDrop = 1.4f;       // no deck within this drop → an edge
    [SerializeField] private float separation = 1.3f;     // push apart from other crew within this
    [SerializeField] private float arriveDist = 0.35f;    // "arrived" at a point
    [Tooltip("Max distance a crew member roams from its post while patrolling/fighting — keeps it safely on the " +
             "deck near its station instead of backing off into the sea.")]
    [SerializeField] private float maxRoam = 3.2f;

    /// <summary>All spawned crew (server + clients), for cheap neighbour separation queries.</summary>
    public static readonly List<EnemyNpcController> All = new List<EnemyNpcController>();

    // ---- replicated state ----
    private readonly NetworkVariable<StatePayload> _netState = new(default,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _posture = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _aiming = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireStamp = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ---- components ----
    private CharacterController _cc;
    private Animator _animator;
    private PlayerAnimator _anim;
    private Health _health;
    private DeckRider _deckRider;

    // ---- server sim ----
    private StatePayload _current;
    private bool _pinned;
    private bool _ccOn;
    private Vector3 _wanderTarget;
    private float _wanderTimer;
    private float _pauseTimer;
    private float _nextFireTime;
    private ShipController _homeShip;   // the enemy ship we crew
    private ShipController _boardedShip; // the player ship we've jumped onto (counter-board), else null
    private bool _jumping;             // mid counter-board leap
    private bool _blocked;             // steer hit a dead-end this frame → turn around / repick a wander target
    private bool _freeSwim;            // in open water (recovery / counter-board leap) → ignore the deck-edge constraint
    private Vector3 _lastPos;          // for stuck detection
    private float _stuckTime;          // how long we've been trying to move but not advancing
    private float _hardStuckTime;      // total wedged time across escape attempts → triggers a teleport unstick
    private float _escapeUntil;        // commit to _escapeDir until this time (breaks the spin-in-place at a snag)
    private Vector3 _escapeDir;        // a committed heading out of a stuck spot

    // ---- client interpolation ----
    private struct Snap { public double time; public StatePayload state; }
    private readonly List<Snap> _snaps = new List<Snap>(64);
    private float _dt = 1f / 60f;

    // ---- animator ----
    private int _postureLayer = -1, _upperLayer = -1;
    private float _postureWeight, _gunWeight;
    private bool _wasAiming;
    private int _fireStampLocal;
    private static readonly int PostureHash = Animator.StringToHash("Posture");
    private static readonly int GunAHash = Animator.StringToHash("GunA");
    private static readonly int GunSpeedHash = Animator.StringToHash("GunSpeed");

    // ================= IAimSource =================
    public float AimPitch => _current.Pitch;
    public float AimYawOffset => Mathf.DeltaAngle(_current.Yaw, _current.AimYaw);
    public float PlanarSpeed => _current.Speed;
    public Vector3 AimDirection => Quaternion.Euler(_current.Pitch, _current.AimYaw, 0f) * Vector3.forward;
    public bool AimingGun => _aiming.Value;
    public bool SuppressAim => _posture.Value > 0; // holding an idle pose → no gun aim lean

    /// <summary>True while this crew member is alive (the commander counts these as its crew-aboard).</summary>
    public bool IsAlive => _health == null || _health.IsAlive;

    // ================= lifecycle =================
    public override void OnNetworkSpawn()
    {
        _cc = GetComponent<CharacterController>();
        _animator = GetComponent<Animator>();
        _health = GetComponent<Health>();
        _animator.applyRootMotion = false;
        _anim = new PlayerAnimator(_animator, animDampTime, motor.walkSpeed, motor.runSpeed, motor.swimSpeed, motor.swimSprintSpeed);
        _postureLayer = _animator.GetLayerIndex("Posture");
        _upperLayer = _animator.GetLayerIndex("UpperBody");
        _deckRider = new DeckRider(_cc);

        if (ship == null) ship = FindNearestShip();
        _homeShip = ship != null ? ship.GetComponent<ShipController>() : null;

        uint tr = NetworkManager.NetworkConfig.TickRate == 0 ? 60u : NetworkManager.NetworkConfig.TickRate;
        _dt = 1f / tr;

        _ccOn = IsServer;
        _cc.enabled = IsServer; // only the server simulates + collides; clients interpolate the replicated state

        _current = new StatePayload
        {
            Position = transform.position,
            Yaw = transform.eulerAngles.y,
            AimYaw = transform.eulerAngles.y,
            Grounded = true,
        };
        if (IsServer)
        {
            _deckRider.SetPlatform(_homeShip);
            _netState.Value = ToNetworkState();
        }

        _netState.OnValueChanged += OnNetState;
        if (_health != null) _health.OnDeath += OnDeath;
        if (!All.Contains(this)) All.Add(this);
    }

    public override void OnNetworkDespawn()
    {
        _netState.OnValueChanged -= OnNetState;
        if (_health != null) _health.OnDeath -= OnDeath;
        All.Remove(this);
    }

    private EnemyShipAI FindNearestShip()
    {
        EnemyShipAI best = null; float bestSqr = float.MaxValue;
        foreach (var s in FindObjectsByType<EnemyShipAI>(FindObjectsSortMode.None))
        {
            float d = (s.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = s; }
        }
        return best;
    }

    // ================= server simulation =================
    private void Update()
    {
        if (!IsServer) return;
        if (_health != null && !_health.IsAlive) { _aiming.Value = false; return; } // corpse: no more thinking

        float dt = Time.deltaTime;
        Think(dt);
    }

    // Decide what to do this frame from the commander's tactical mode, then either pin at a post or drive the motor.
    private void Think(float dt)
    {
        _freeSwim = false; // default: constrained to the deck (edge sensors on). Water phases turn this on.
        ShipMode mode = ship != null ? MapMode(ship.Mode) : ShipMode.Idle;

        if (mode == ShipMode.Invade) { Invade(dt); return; } // actively counter-boarding the player ship

        // Any other mode: if we're NOT standing on our own ship — in the water after a fall, or stranded on the
        // player ship after a recall (the player ship escaped) — come home first, then resume post/idle/fight.
        if (_deckRider.Platform != _homeShip) { GoHome(dt); return; }

        switch (mode)
        {
            case ShipMode.Idle:   IdleAtPost(dt); break;
            case ShipMode.Patrol: Patrol(dt); break;
            case ShipMode.Fight:  FightBoarders(dt); break;
        }
    }

    // Come home: swim in from the water (or leave the player ship after a recall) and climb back aboard our own
    // ship. Once home, Think resumes IdleAtPost so the crew walk to their posts, play their idle, and the hull sails again.
    private void GoHome(float dt)
    {
        _pinned = false;
        _posture.Value = PostureNone;
        _aiming.Value = false;
        _freeSwim = true; // in open water or leaving the player deck — don't let the deck-edge sensor freeze us
        ShipController goal = _homeShip;
        if (goal == null) { StandStill(dt); return; }
        Vector3 board = goal.DeckBoardPoint;
        MoveToward(board, true, 0f, false, dt);       // swim/run for the rail
        if (Vector3.Distance(Flat(transform.position), Flat(board)) < 2.5f)
        {
            Teleport(board);
            _current.Position = board;
            _current.VerticalVelocity = 0f;
            _boardedShip = null;
            _deckRider.SetPlatform(goal);              // back on deck
        }
    }

    private enum ShipMode { Idle, Patrol, Fight, Invade }
    private ShipMode MapMode(EnemyShipAI.ShipMode m)
    {
        switch (m)
        {
            case EnemyShipAI.ShipMode.Peaceful: return ShipMode.Idle;
            case EnemyShipAI.ShipMode.Alarm: return ShipMode.Patrol;
            case EnemyShipAI.ShipMode.Repel: return ShipMode.Fight;
            case EnemyShipAI.ShipMode.CounterBoard: return ShipMode.Invade;
        }
        return ShipMode.Idle;
    }

    // ---- Idle: walk back to the assigned post, then pin + play the assigned idle animation ----
    private void IdleAtPost(float dt)
    {
        _aiming.Value = false;
        if (homePoint == null) { _pinned = false; return; }

        float d = Vector3.Distance(Flat(transform.position), Flat(homePoint.position));
        if (d <= arriveDist)
        {
            _pinned = true;
            _posture.Value = homePosture;   // Kneel / Warrior / Sit / Carry(driver)
        }
        else
        {
            _pinned = false;
            _posture.Value = PostureNone;
            MoveToward(homePoint.position, false, homePoint.eulerAngles.y, false, dt);
        }
    }

    // ---- Patrol (alarm): pace the deck near the post AND shoot any player in range (e.g. on the near-by
    // player ship). Pauses sometimes; turns around at edges/obstacles. ----
    private void Patrol(float dt)
    {
        _pinned = false;
        _posture.Value = PostureNone;

        Health target = PickTarget();                 // nearest live player within gun range (or the chest carrier)
        _aiming.Value = target != null;

        _pauseTimer -= dt;
        bool paused = _pauseTimer > 0f;
        if (!paused)
        {
            _wanderTimer -= dt;
            if (_wanderTimer <= 0f || Vector3.Distance(Flat(transform.position), Flat(_wanderTarget)) < arriveDist + 0.2f)
            {
                _wanderTimer = 1.1f + Hash01(0) * 1.4f;
                if (Hash01(1) < 0.3f) _pauseTimer = 0.5f + Hash01(2) * 1.0f; // sometimes hold position
                _wanderTarget = PickDeckPointNear(homePoint != null ? homePoint.position : transform.position, 3.0f);
            }
        }

        Vector3 moveTarget = ClampToHome(paused ? transform.position : _wanderTarget);
        Vector3? aimAt = target != null ? (Vector3?)target.transform.position : null;
        MoveToward(moveTarget, false, 0f, target != null, dt, aimAt);
        if (target != null) TryFire(target, dt);
    }

    // ---- Fight: a player boarded us. Keep distance, strafe, and shoot the (prioritised) target ----
    private void FightBoarders(float dt)
    {
        _pinned = false;
        _posture.Value = PostureNone;
        Health target = PickTarget();
        if (target == null) { _aiming.Value = false; StandStill(dt); return; }

        Vector3 tp = target.transform.position;
        Vector3 away = Flat(transform.position - tp);
        float dist = away.magnitude;
        _aiming.Value = true;

        Vector3 desired;
        if (dist < keepDistance - 0.5f) desired = transform.position + away.normalized * 2f;      // back off
        else if (dist > keepDistance + 2f) desired = tp;                                            // close in a little
        else desired = transform.position + Vector3.Cross(Vector3.up, away.normalized) * StrafeSign() * 2f; // strafe

        MoveToward(ClampToHome(desired), false, 0f, true, dt, tp);   // clamp so backing off never walks into the sea
        TryFire(target, dt);
    }

    // ---- Invade (counter-board): no players left aboard → leap to the player ship and fight there ----
    private void Invade(float dt)
    {
        _pinned = false;
        _posture.Value = PostureNone;
        ShipController ps = ship != null ? ship.PlayerShip : null;

        // Already aboard the player ship? Fight whoever is there.
        if (_boardedShip != null)
        {
            Health target = PickTarget();
            if (target != null) { _aiming.Value = true; FightBoarders(dt); return; }
            _aiming.Value = false;
            if (ps != null) MoveToward(ps.transform.position, false, 0f, false, dt); // roam toward the deck centre
            return;
        }

        if (ps == null) { StandStill(dt); return; }
        _aiming.Value = false;
        _freeSwim = true; // leaping into the sea toward the player ship — swim freely, no deck-edge constraint
        // Head for the player ship's boarding spot; once close, snap aboard as a passenger (same as a player climbing on).
        Vector3 board = ps.DeckBoardPoint;
        MoveToward(board, true, 0f, false, dt); // sprint/swim toward the rail / gap
        if (Vector3.Distance(Flat(transform.position), Flat(board)) < 2.5f)
            BoardPlayerShip(ps);
    }

    private void BoardPlayerShip(ShipController ps)
    {
        Vector3 spot = ps.DeckBoardPoint;
        Teleport(spot);
        _current.Position = spot;
        _current.VerticalVelocity = 0f;
        _boardedShip = ps;
        _deckRider.SetPlatform(ps);
    }

    // ================= movement primitive =================
    // Build an InputCommand toward a world point (with edge/obstacle/neighbour steering) and run the shared motor.
    // faceYaw: body facing when not aiming (e.g. the post's forward). aimTarget: when aiming, spine/arm track it.
    private void MoveToward(Vector3 worldTarget, bool sprint, float faceYaw, bool aiming, float dt, Vector3? aimTarget = null)
    {
        float advanced = Flat(transform.position - _lastPos).magnitude; // how far we actually moved since last frame
        _lastPos = transform.position;

        // While an escape is committed, walk that heading decisively (no re-deciding each frame → no spin-in-place).
        bool escaping = Time.time < _escapeUntil;
        Vector3 to = escaping ? _escapeDir : Flat(worldTarget - transform.position);
        Vector3 dir = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.zero;

        _blocked = false;
        dir = Steer(dir);                        // avoid edges / obstacles / neighbours
        bool moving = dir.sqrMagnitude > 1e-4f && (escaping || to.magnitude > arriveDist);

        // Stall detection (deck only): trying to move but barely advancing, or steer reports boxed → we're wedged
        // (typically at a stair or a hull jut). Commit a decisive escape heading for a short while to break free.
        if (!_freeSwim)
        {
            bool wedged = moving && advanced < 0.015f;
            if (wedged) { _stuckTime += dt; _hardStuckTime += dt; }
            else { _stuckTime = Mathf.Max(0f, _stuckTime - dt * 2f); _hardStuckTime = Mathf.Max(0f, _hardStuckTime - dt * 3f); }

            // Still wedged after the escape attempts (e.g. jammed against the stairs) → hard-unstick by nudging
            // back toward the post, a guaranteed-safe interior deck spot.
            if (_hardStuckTime > 0.8f) { HardUnstick(); return; }

            if (!escaping && (_blocked || _stuckTime > 0.25f))
            {
                _escapeDir = PickEscapeDir();
                _escapeUntil = Time.time + 0.65f;
                _stuckTime = 0f;
                _wanderTimer = 0f;               // pick a fresh patrol destination once we're free
                dir = Steer(_escapeDir);
                moving = dir.sqrMagnitude > 1e-4f;
            }
        }

        // Desired body heading: toward the move dir (rotate-to-move, like the player). Feed it as the input Yaw.
        float moveYaw = moving ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg : _current.Yaw;

        // Aim: toward the target if aiming/combat, else along the facing.
        float lookYaw = moveYaw, lookPitch = 0f;
        if (aiming && aimTarget.HasValue)
        {
            Vector3 a = aimTarget.Value + Vector3.up * 1.2f;
            Vector3 eye = transform.position + Vector3.up * 1.5f;
            Vector3 av = a - eye;
            lookYaw = Mathf.Atan2(av.x, av.z) * Mathf.Rad2Deg;
            lookPitch = -Mathf.Atan2(av.y, new Vector2(av.x, av.z).magnitude) * Mathf.Rad2Deg;
            if (!moving) moveYaw = lookYaw; // stand and face the target
        }

        var cmd = new InputCommand
        {
            MoveX = 0f,
            MoveY = moving ? 1f : 0f,
            Yaw = moving ? moveYaw : (aiming ? lookYaw : (faceYaw != 0f ? faceYaw : _current.Yaw)),
            Pitch = lookPitch,
            Sprint = sprint,
        };

        StatePayload next = PlayerMotor.Simulate(_cc, _current, cmd, dt, motor);
        if (IsFinite(next.Position)) _current = next; else Teleport(_current.Position);

        // Overlay the aim (the motor set AimYaw=input.Yaw); the spine/arm IK read these.
        _current.AimYaw = lookYaw;
        _current.Pitch = lookPitch;

        // Never play a run/walk cycle while genuinely wedged in place — force the idle so it doesn't "run on the spot".
        if (_stuckTime > 0.12f) { _current.Speed = 0f; _current.MoveX = 0f; _current.MoveY = 0f; }

        // Stationary + should face somewhere specific → ease the body around (motor only turns while moving).
        if (!moving)
        {
            float wantYaw = aiming ? lookYaw : (faceYaw != 0f ? faceYaw : _current.Yaw);
            float y = Mathf.LerpAngle(_current.Yaw, wantYaw, 1f - Mathf.Exp(-motor.turnSharpness * dt));
            _current.Yaw = y;
            transform.rotation = Quaternion.Euler(0f, y, 0f);
        }
    }

    private void StandStill(float dt) => MoveToward(transform.position, false, _current.Yaw, false, dt);

    // Teleport a hopelessly-wedged NPC a step back toward its post (a known-safe interior deck spot). Resets the
    // stuck/escape state so it resumes normally from there.
    private void HardUnstick()
    {
        Vector3 goal = homePoint != null ? homePoint.position
                     : (_homeShip != null ? _homeShip.transform.position : transform.position);
        Vector3 toGoal = Flat(goal - transform.position);
        float d = toGoal.magnitude;
        Vector3 dest = d > 1.6f ? transform.position + toGoal.normalized * 1.6f : goal;
        Teleport(new Vector3(dest.x, transform.position.y + 0.1f, dest.z));
        _current.Position = transform.position;
        _current.Speed = 0f; _current.MoveX = 0f; _current.MoveY = 0f;
        _hardStuckTime = 0f; _stuckTime = 0f; _escapeUntil = 0f;
    }

    // Keep an on-deck move target within maxRoam of this NPC's post (which moves with the ship), so the crew
    // never wanders or backs off far enough to fall into the sea.
    private Vector3 ClampToHome(Vector3 target)
    {
        if (homePoint == null) return target;
        Vector3 home = homePoint.position;
        Vector3 off = Flat(target - home);
        float m = off.magnitude;
        if (m <= maxRoam) return target;
        Vector3 c = home + off / m * maxRoam;
        return new Vector3(c.x, target.y, c.z);
    }

    // Steer the desired dir away from crowding neighbours, then away from a deck edge OR an obstacle ahead;
    // if fully boxed in (a corner / a hull jut), flag it and turn around to head back the other way.
    private Vector3 Steer(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-4f) return dir;

        // Neighbour separation (don't clip through the other crew).
        Vector3 push = Vector3.zero;
        var all = All;
        for (int i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o == null || o == this) continue;
            Vector3 d = Flat(transform.position - o.transform.position);
            float m = d.magnitude;
            if (m > 0.001f && m < separation) push += d / m * (separation - m);
        }
        if (push.sqrMagnitude > 1e-4f) dir = (dir + push.normalized * 0.8f).normalized;

        if (_freeSwim) return dir; // swimming in open water: no deck to hug, just head for the ship

        if (Clear(dir)) return dir;

        // Edge or obstacle ahead: fan out to either side to find open deck.
        for (int a = 25; a <= 135; a += 25)
        {
            Vector3 l = Quaternion.Euler(0f, -a, 0f) * dir;
            if (Clear(l)) return l;
            Vector3 r = Quaternion.Euler(0f, a, 0f) * dir;
            if (Clear(r)) return r;
        }

        // Boxed in on all sides → flag it and stop this frame; the mover commits a decisive escape heading
        // (back toward the post / a clear way) so the crew never spins in place at a stair or corner.
        _blocked = true;
        return Vector3.zero;
    }

    // Choose a decisive way out of a stuck spot: prefer heading back toward the post (interior/safe); otherwise
    // scan away from the current facing for the first clear direction.
    private Vector3 PickEscapeDir()
    {
        if (homePoint != null)
        {
            Vector3 toHome = Flat(homePoint.position - transform.position);
            if (toHome.sqrMagnitude > 0.09f && Clear(toHome.normalized)) return toHome.normalized;
        }
        Vector3 basis = Flat(-transform.forward);
        if (basis.sqrMagnitude < 1e-4f) basis = Vector3.forward;
        basis.Normalize();
        for (int a = 0; a <= 180; a += 30)
        {
            Vector3 l = Quaternion.Euler(0f, -a, 0f) * basis; if (Clear(l)) return l;
            if (a > 0) { Vector3 r = Quaternion.Euler(0f, a, 0f) * basis; if (Clear(r)) return r; }
        }
        return basis;
    }

    // Clear to walk = deck underfoot a step ahead AND no wall/railing/mast/prop blocking the way.
    private bool Clear(Vector3 dir) => DeckAhead(dir) && !ObstacleAhead(dir);

    private bool DeckAhead(Vector3 dir)
    {
        Vector3 probe = transform.position + dir * edgeProbe + Vector3.up * 0.5f;
        if (!Physics.Raycast(probe, Vector3.down, out var hit, edgeDrop + 0.5f, ~0, QueryTriggerInteraction.Ignore))
            return false;
        // The deck a step ahead must be roughly at foot level — a surface that drops away (the sloping hull
        // running down to the sea, or a railing gap) reads as an EDGE so the crew never walks off into the water.
        return hit.point.y >= transform.position.y - 0.5f;
    }

    private bool ObstacleAhead(Vector3 dir)
    {
        Vector3 origin = transform.position + Vector3.up * 1.0f;
        if (!Physics.Raycast(origin, dir, out var hit, 0.7f, ~0, QueryTriggerInteraction.Ignore)) return false;
        Transform t = hit.collider.transform;
        if (t == transform || t.IsChildOf(transform)) return false;                       // self
        if (hit.collider.GetComponentInParent<EnemyNpcController>() != null) return false; // crew: handled by separation
        return true;                                                                       // a wall / railing / mast / prop
    }

    private Vector3 PickDeckPointNear(Vector3 center, float radius)
    {
        for (int i = 0; i < 6; i++)
        {
            float ang = Hash01(10 + i) * 360f;
            Vector3 p = center + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * (radius * (0.4f + 0.6f * Hash01(20 + i)));
            if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore))
                return p;
        }
        return center;
    }

    // ================= combat =================
    private Health PickTarget()
    {
        // Prioritise a chest carrier the commander flagged; else the nearest live player in range.
        if (ship != null && ship.ChestCarrier != null && ship.ChestCarrier.IsAlive) return ship.ChestCarrier;
        Health best = null; float bestSqr = gunRange * gunRange;
        var all = Health.All;
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h == null || h.Side != Team.Player || !h.IsAlive) continue;
            float d = (h.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = h; }
        }
        return best;
    }

    private void TryFire(Health target, float dt)
    {
        if (Time.time < _nextFireTime || target == null) return;
        Vector3 origin = gunMuzzle != null ? gunMuzzle.position : transform.position + Vector3.up * 1.5f;
        Vector3 aim = (target.transform.position + Vector3.up * 1.1f) - origin;
        if (aim.magnitude > gunRange) return;
        // Wait for the commander's staggered volley slot so the crew doesn't all shoot at once (dodgeable).
        if (ship != null && !ship.TryReserveFireSlot()) return;
        aim.Normalize();
        // Spread so the shot deviates (and the chest carrier is prioritised but not pinpoint-sniped).
        aim = Quaternion.Euler((Hash01(31) - 0.5f) * 2f * aimSpread, (Hash01(32) - 0.5f) * 2f * aimSpread, 0f) * aim;

        _nextFireTime = Time.time + fireInterval;
        _fireStamp.Value++;                       // cosmetic muzzle flash on every client

        Vector3 vel = aim * bulletSpeed;
        // Authority bullet carries the damage (Team.Enemy → only hurts players, never crew); clients spawn a matching tracer.
        CannonBallProjectile.Spawn(bulletPrefab, origin, vel, bulletGravity, bulletLife, gunDamage,
            NetworkManager.ServerClientId, Team.Enemy, true, true, transform, bulletImpactFx, 1f);
        FireBulletClientRpc(origin, vel);
    }

    [Rpc(SendTo.NotServer)]
    private void FireBulletClientRpc(Vector3 pos, Vector3 vel) =>
        CannonBallProjectile.Spawn(bulletPrefab, pos, vel, bulletGravity, bulletLife, 0, 0UL, Team.Enemy, false, true, transform, bulletImpactFx, 1f);

    // ================= render (both roles) =================
    private void LateUpdate()
    {
        if (IsServer)
        {
            bool alive = _health == null || _health.IsAlive;
            if (alive)
            {
                if (_pinned && homePoint != null)
                {
                    SetCC(false);
                    transform.SetPositionAndRotation(homePoint.position, homePoint.rotation);
                    _current.Position = homePoint.position;
                    _current.Yaw = homePoint.eulerAngles.y;
                    _current.AimYaw = homePoint.eulerAngles.y;
                    _current.Speed = 0f; _current.MoveX = 0f; _current.MoveY = 0f;
                    _deckRider.SetPlatform(_boardedShip != null ? _boardedShip : _homeShip);
                }
                else
                {
                    SetCC(true);
                    var deck = _deckRider.DetectAndCarry();     // stick to the moving deck (enemy or boarded player ship)
                    _current.Position = transform.position;
                    if (deck != null && _boardedShip == null && deck != _homeShip) { /* stepped onto another deck */ }
                }
            }
            _netState.Value = ToNetworkState();
        }
        else
        {
            Interpolate(NetworkManager.ServerTime.Time - 2 * _dt);
        }

        ApplyAnimator(Time.deltaTime);
    }

    private void SetCC(bool on)
    {
        if (_ccOn == on) return;
        _ccOn = on;
        _cc.enabled = on;
    }

    // Ship-LOCAL pose + platform id while aboard, so remotes rebuild it against the moving hull (glued to the deck).
    private StatePayload ToNetworkState()
    {
        var s = _current;
        ShipController ride = _boardedShip != null ? _boardedShip : (_deckRider.Platform != null ? _deckRider.Platform : _homeShip);
        if (ride != null && ride.NetworkObject != null && ride.NetworkObject.IsSpawned)
        {
            s.PlatformId = ride.NetworkObject.NetworkObjectId;
            s.Position = ride.transform.InverseTransformPoint(_current.Position);
            s.Yaw = Mathf.DeltaAngle(ride.transform.eulerAngles.y, _current.Yaw);
            s.AimYaw = Mathf.DeltaAngle(ride.transform.eulerAngles.y, _current.AimYaw);
        }
        else s.PlatformId = 0UL;
        s.Tick = NetworkManager.LocalTime.Tick;
        return s;
    }

    private void OnNetState(StatePayload _, StatePayload next)
    {
        if (IsServer) return;
        _snaps.Add(new Snap { time = next.Tick * (double)_dt, state = next });
        while (_snaps.Count > 64) _snaps.RemoveAt(0);
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
        Vector3 pos; float yaw, aimYaw;
        if (b.PlatformId != 0UL)
        {
            if (!TryGetShipTransform(b.PlatformId, out var shipT)) { _current = b; return; }
            if (a.PlatformId == b.PlatformId)
            {
                pos = shipT.TransformPoint(Vector3.Lerp(a.Position, b.Position, f));
                yaw = shipT.eulerAngles.y + Mathf.LerpAngle(a.Yaw, b.Yaw, f);
                aimYaw = shipT.eulerAngles.y + Mathf.LerpAngle(a.AimYaw, b.AimYaw, f);
            }
            else { pos = shipT.TransformPoint(b.Position); yaw = shipT.eulerAngles.y + b.Yaw; aimYaw = shipT.eulerAngles.y + b.AimYaw; }
        }
        else
        {
            pos = Vector3.Lerp(a.Position, b.Position, f);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, f);
            aimYaw = Mathf.LerpAngle(a.AimYaw, b.AimYaw, f);
        }
        if (!IsFinite(pos)) return;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        _current = b;
        _current.Position = pos; _current.Yaw = yaw; _current.AimYaw = aimYaw;
    }

    private bool TryGetShipTransform(ulong netId, out Transform t)
    {
        t = null;
        var nm = NetworkManager;
        if (nm != null && nm.SpawnManager != null && nm.SpawnManager.SpawnedObjects.TryGetValue(netId, out var no) && no != null)
        { t = no.transform; return true; }
        return false;
    }

    // ================= animator =================
    private void ApplyAnimator(float dt)
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        _anim.Apply(_current, dt);

        int posture = _posture.Value;
        if (_postureLayer >= 0)
        {
            _animator.SetInteger(PostureHash, posture);
            // Ramp the idle-pose layer IN smoothly; drop it OUT instantly so the empty None state never
            // blends a bind pose over the locomotion as the crew springs into action.
            _postureWeight = posture > 0 ? Mathf.MoveTowards(_postureWeight, 1f, 4f * dt) : 0f;
            _animator.SetLayerWeight(_postureLayer, _postureWeight);
        }

        if (_upperLayer >= 0)
        {
            bool aiming = _aiming.Value && posture == 0;
            float target = aiming ? 1f : 0f;
            _gunWeight = Mathf.MoveTowards(_gunWeight, target, 8f * dt);
            _animator.SetLayerWeight(_upperLayer, _gunWeight);
            if (aiming && !_wasAiming) { _animator.CrossFadeInFixedTime(GunAHash, 0.15f, _upperLayer, 0f); _animator.SetFloat(GunSpeedHash, 1f); }
            if (aiming)
            {
                var st = _animator.GetCurrentAnimatorStateInfo(_upperLayer);
                if (st.IsName("GunA") && st.normalizedTime >= 0.39f) _animator.SetFloat(GunSpeedHash, 0f); // hold aim
            }
            _wasAiming = aiming;
        }

        if (_fireStamp.Value != _fireStampLocal)
        {
            _fireStampLocal = _fireStamp.Value;
            if (_upperLayer >= 0) { _animator.Play(GunAHash, _upperLayer, 0.34f); _animator.SetFloat(GunSpeedHash, 1.3f); }
            SpawnMuzzleFx();
        }
    }

    private void SpawnMuzzleFx()
    {
        if (muzzleFx == null || gunMuzzle == null) return;
        var fx = Instantiate(muzzleFx, gunMuzzle.position, gunMuzzle.rotation);
        foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSizeMultiplier *= muzzleFxScale;
            main.startSpeedMultiplier *= muzzleFxScale;
        }
        Destroy(fx, muzzleFxLifetime);
    }

    // ================= death =================
    private void OnDeath(ulong attacker)
    {
        _aiming.Value = false;
        _posture.Value = PostureNone;
        if (IsServer) { _pinned = false; SetCC(false); }
        // Freeze as a corpse (no ragdoll rig set up). Leave the last pose; could despawn on a timer later.
        enabled = enabled; // keep component; brain early-outs on !IsAlive
    }

    // ================= helpers =================
    private void Teleport(Vector3 pos)
    {
        bool was = _cc.enabled;
        _cc.enabled = false;
        transform.position = pos;
        _cc.enabled = was;
    }

    private float StrafeSign()
    {
        // Stable per-NPC left/right bias so the crew doesn't all strafe the same way (uses the instance id).
        return ((GetInstanceID() & 2) == 0) ? 1f : -1f;
    }

    // Cheap deterministic pseudo-random in [0,1) varied by NPC + salt + time bucket (no Random — keeps replays sane).
    private float Hash01(int salt)
    {
        int t = Mathf.FloorToInt(Time.time * 3f);
        uint h = (uint)(GetInstanceID() * 73856093 ^ (salt * 19349663) ^ (t * 83492791));
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0x1000000;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
}
