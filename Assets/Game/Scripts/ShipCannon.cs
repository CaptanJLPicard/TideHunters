using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The enemy ship's bow cannon. The ship's AI aims the whole hull so the bow (this muzzle) points at the
/// target, then calls <see cref="ServerTryFireAt"/> when in range + roughly aligned + off cooldown. Fire is
/// server-driven: the server spawns the authoritative (damaging) cannonball and tells every client to spawn a
/// cosmetic one with the same launch, so the arc matches everywhere. It cannot shoot beyond <see
/// cref="maxRange"/> (and holds fire below <see cref="minRange"/>) — "can't hit from very far".
/// Placed on the ship root (shares the ship's NetworkObject); <see cref="muzzle"/> points at the barrel tip.
/// </summary>
public class ShipCannon : NetworkBehaviour
{
    [Header("Refs")]
    [SerializeField] private Transform muzzle;              // barrel tip, +Z along the barrel
    [SerializeField] private GameObject cannonBallPrefab;   // Assets/Game/Prefabs/Ammos/CannonBall
    [SerializeField] private GameObject muzzleFx;           // optional flash at the barrel
    [SerializeField] private GameObject impactFx;           // optional splash/burst at the hit

    [Header("Ballistics")]
    [SerializeField] private float muzzleSpeed = 34f;
    [SerializeField] private float projectileGravity = -11f;
    [SerializeField] private float projectileLifetime = 6f;
    [Tooltip("Launch elevation above the horizontal (deg). The ball lobs up at this angle and arcs down in a " +
             "clear parabola matching the cannon's tilt; speed is auto-fitted to the target distance.")]
    [SerializeField] private float launchElevation = 24f;

    [Header("Range / rate")]
    [SerializeField] private float maxRange = 45f;          // can't hit beyond this
    [SerializeField] private float minRange = 8f;           // too close to depress the barrel
    [SerializeField] private float reloadTime = 3.5f;
    [Tooltip("Damage per hit to the enemy hull.")]
    [SerializeField] private int damage = 10;
    [Tooltip("Bow must be within this many degrees of the target (horizontal) before the cannon fires.")]
    [SerializeField] private float aimTolerance = 12f;
    [Tooltip("Which side this cannon belongs to (Enemy = AI ship, Player = player-crewed ship).")]
    [SerializeField] private Team team = Team.Enemy;

    [Header("Manual pitch (player-crewed cannon only)")]
    [Tooltip("The cannon mesh to tilt as the gunner aims (SmallShipCannon1). Null = fixed cannon (enemy auto-aim).")]
    [SerializeField] private Transform pitchPivot;
    [Tooltip("Max elevation X-euler (barrel raised → parabola).")]
    [SerializeField] private float pitchMin = -16f;
    [Tooltip("Level X-euler (flat shot).")]
    [SerializeField] private float pitchMax = 4f;
    [SerializeField] private float pitchLerp = 12f;
    [Tooltip("Barrel muzzle in the cannon mesh's local space (the shot leaves here, tilting with the barrel).")]
    [SerializeField] private Vector3 barrelTipLocal = new Vector3(0f, 0.51f, 1.65f);

    public float MaxRange => maxRange;
    public float MinRange => minRange;
    // For a player-crewed cannon the shot leaves the real barrel TIP + travels along the barrel (both taken from
    // the tilting cannon mesh, so re-parenting a muzzle isn't needed). Enemy cannons keep their fixed muzzle child.
    public Vector3 MuzzlePos => pitchPivot != null ? pitchPivot.TransformPoint(barrelTipLocal)
                                                   : (muzzle != null ? muzzle.position : transform.position);
    public Vector3 MuzzleForward => pitchPivot != null ? pitchPivot.forward
                                                       : (muzzle != null ? muzzle.forward : transform.forward);

    private float _nextFireTime;
    public bool ReadyToFire => Time.time >= _nextFireTime;
    public float ReloadTime => reloadTime;

    // Replicated barrel pitch (X-euler) for the player-crewed cannon. Owner (the ship's driver) writes it; every
    // peer tilts the barrel + the muzzle child to match, so the shot direction is consistent everywhere.
    private readonly NetworkVariable<float> _pitch = new(4f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private float _pitchBaseY, _pitchBaseZ;
    private bool _pitchBaseCaptured;

    public float PitchMin => pitchMin;
    public float PitchMax => pitchMax;
    public float Pitch => _pitch.Value;

    /// <summary>Owner (driver) only: set the barrel elevation, clamped to [pitchMin, pitchMax].</summary>
    public void SetPitchOwner(float p) { if (IsOwner) _pitch.Value = Mathf.Clamp(p, pitchMin, pitchMax); }

    // Bow yaw direction (flat), taken from the muzzle so it follows the hull heading.
    private Vector3 ForwardFlat()
    {
        Vector3 f = new Vector3(MuzzleForward.x, 0f, MuzzleForward.z);
        return f.sqrMagnitude < 1e-4f ? transform.forward : f.normalized;
    }

    /// <summary>The launch direction from the current pitch: flat at pitchMax (level shot), tilting up toward
    /// pitchMin (parabola). Derived from the pitch VALUE (not the mesh transform) so it's rig-independent.</summary>
    public Vector3 AimDir()
    {
        float e = (pitchMax - _pitch.Value) * Mathf.Deg2Rad; // 0° at level, up to (pitchMax-pitchMin)° at max elevation
        Vector3 f = ForwardFlat();
        return (f * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e)).normalized;
    }

    private void Update()
    {
        if (pitchPivot == null) return; // fixed cannon (enemy)
        if (!_pitchBaseCaptured)
        {
            var e0 = pitchPivot.localEulerAngles;
            _pitchBaseY = e0.y; _pitchBaseZ = e0.z; _pitchBaseCaptured = true;
        }
        float curX = pitchPivot.localEulerAngles.x;
        float nx = Mathf.LerpAngle(curX, _pitch.Value, 1f - Mathf.Exp(-pitchLerp * Time.deltaTime));
        pitchPivot.localEulerAngles = new Vector3(nx, _pitchBaseY, _pitchBaseZ);
    }

    /// <summary>Server: fire at a world point if in range, roughly aligned and reloaded. Returns true if fired.</summary>
    public bool ServerTryFireAt(Vector3 targetPoint, ulong attacker)
    {
        if (!IsServer || muzzle == null || cannonBallPrefab == null) return false;
        if (Time.time < _nextFireTime) return false;

        Vector3 flat = targetPoint - MuzzlePos; flat.y = 0f;
        float dist = flat.magnitude;
        if (dist > maxRange || dist < minRange) return false;

        Vector3 fwdFlat = new Vector3(MuzzleForward.x, 0f, MuzzleForward.z);
        if (Vector3.Angle(fwdFlat, flat) > aimTolerance) return false; // hull hasn't finished aiming the bow

        Vector3 vel = SolveArc(MuzzlePos, targetPoint);
        _nextFireTime = Time.time + reloadTime;
        SpawnBall(MuzzlePos, vel, attacker, true);   // server copy = authority (deals damage)
        FireClientRpc(MuzzlePos, vel, attacker);     // clients spawn a matching cosmetic ball
        return true;
    }

    /// <summary>Server: player-crewed fire — launch a ball straight along the (tilted) barrel. The muzzle is a
    /// child of the pitched cannon, so its forward already carries the aim; gravity turns a raised barrel into a
    /// parabola and a level one into a near-flat shot. Respects the reload. Returns true if it fired.</summary>
    public bool ServerFireForward(ulong attacker)
    {
        if (!IsServer || muzzle == null || cannonBallPrefab == null) return false;
        if (Time.time < _nextFireTime) return false;
        // Fire straight along the real barrel (the muzzle is at the barrel tip, tilting with the cannon), so the
        // ball leaves exactly where + how the gunner aimed: level barrel → near-flat shot, raised → parabola.
        Vector3 vel = MuzzleForward.normalized * muzzleSpeed;
        _nextFireTime = Time.time + reloadTime;
        SpawnBall(MuzzlePos, vel, attacker, true);
        FireClientRpc(MuzzlePos, vel, attacker);
        return true;
    }

    [Rpc(SendTo.NotServer)]
    private void FireClientRpc(Vector3 pos, Vector3 vel, ulong attacker) => SpawnBall(pos, vel, attacker, false);

    private void SpawnBall(Vector3 pos, Vector3 vel, ulong attacker, bool authority)
    {
        Quaternion rot = vel.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(vel) : Quaternion.identity;
        var ball = Instantiate(cannonBallPrefab, pos, rot);
        var proj = ball.GetComponent<CannonBallProjectile>();
        if (proj == null) proj = ball.AddComponent<CannonBallProjectile>();
        // Cannon: may hole a hull (hitCharactersOnly=false); ignores its own ship; friendly fire off via team.
        proj.Launch(vel, projectileGravity, projectileLifetime, damage, attacker, team, DamageType.Cannon, authority, false, transform, impactFx, 1f);
        if (muzzleFx != null)
        {
            var fx = Instantiate(muzzleFx, pos, rot);
            Destroy(fx, 3f);
        }
    }

    /// <summary>Launch velocity that LOBS the ball toward <paramref name="to"/> at the fixed
    /// <see cref="launchElevation"/> — a clear parabola along the cannon's tilt — with the speed auto-fitted so
    /// it lands at the target's horizontal distance. Falls back to a flat shot only if elevation is ~0.</summary>
    private Vector3 SolveArc(Vector3 from, Vector3 to)
    {
        Vector3 flat = to - from; flat.y = 0f;
        float d = flat.magnitude;
        Vector3 hdir = d > 0.01f ? flat / d : new Vector3(MuzzleForward.x, 0f, MuzzleForward.z).normalized;
        float theta = launchElevation * Mathf.Deg2Rad;
        float g = -projectileGravity;                 // positive
        float sin2 = Mathf.Sin(2f * theta);
        float v = sin2 > 0.02f ? Mathf.Sqrt(Mathf.Max(1f, d * g / sin2)) : muzzleSpeed; // speed to land at range d
        v = Mathf.Clamp(v, 8f, 80f);
        Vector3 dir = (hdir * Mathf.Cos(theta) + Vector3.up * Mathf.Sin(theta)).normalized;
        return dir * v;
    }
}
