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
    [SerializeField] private int damage = 20;
    [Tooltip("Bow must be within this many degrees of the target (horizontal) before the cannon fires.")]
    [SerializeField] private float aimTolerance = 12f;

    public float MaxRange => maxRange;
    public float MinRange => minRange;
    public Vector3 MuzzlePos => muzzle != null ? muzzle.position : transform.position;
    public Vector3 MuzzleForward => muzzle != null ? muzzle.forward : transform.forward;

    private float _nextFireTime;
    public bool ReadyToFire => Time.time >= _nextFireTime;

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

    [Rpc(SendTo.NotServer)]
    private void FireClientRpc(Vector3 pos, Vector3 vel, ulong attacker) => SpawnBall(pos, vel, attacker, false);

    private void SpawnBall(Vector3 pos, Vector3 vel, ulong attacker, bool authority)
    {
        Quaternion rot = vel.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(vel) : Quaternion.identity;
        var ball = Instantiate(cannonBallPrefab, pos, rot);
        var proj = ball.GetComponent<CannonBallProjectile>();
        if (proj == null) proj = ball.AddComponent<CannonBallProjectile>();
        // Cannon: enemy team, may hole a hull (hitCharactersOnly=false); ignores its own ship.
        proj.Launch(vel, projectileGravity, projectileLifetime, damage, attacker, Team.Enemy, authority, false, transform, impactFx, 1f);
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
