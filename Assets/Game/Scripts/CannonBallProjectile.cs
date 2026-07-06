using UnityEngine;

/// <summary>
/// A projectile in flight — a cannonball OR a gun bullet (same physics, different prefab/params). Spawned
/// locally on every peer from a single synced shot (same launch position + velocity), so the arc/tracer looks
/// the same everywhere. Only the AUTHORITY copy (spawned on the server) deals damage: each frame it
/// sweep-raycasts the segment it just travelled and, on hitting a valid target, applies damage once.
///
/// Targeting rules (so nobody shoots their own side):
///  - It passes harmlessly THROUGH the shooter (<paramref name="ignoreRoot"/>) and through any character on the
///    attacker's own <see cref="Team"/> (friendly fire off).
///  - It damages an enemy character (a <see cref="Health"/> on the other team), always.
///  - It damages a ship / world <see cref="IDamageable"/> only when <c>hitCharactersOnly</c> is false (cannon =
///    false → can hole a hull; gun bullets = true → punch through to people, don't scratch ships).
/// Every copy self-destructs on impact or after its lifetime, leaving an optional impact FX. Cosmetic-only on clients.
/// </summary>
public class CannonBallProjectile : MonoBehaviour
{
    private Vector3 _vel;
    private float _gravity;      // negative
    private float _life;
    private int _damage;
    private ulong _attacker;
    private Team _attackerTeam;
    private bool _authority;
    private bool _hitCharactersOnly;
    private Transform _ignoreRoot;
    private GameObject _impactFx;
    private float _impactFxScale = 1f;
    private Vector3 _prev;
    private bool _launched;

    public void Launch(Vector3 velocity, float gravity, float lifetime, int damage, ulong attacker,
        Team attackerTeam, bool authority, bool hitCharactersOnly, Transform ignoreRoot, GameObject impactFx, float impactFxScale)
    {
        _vel = velocity; _gravity = gravity; _life = lifetime; _damage = damage;
        _attacker = attacker; _attackerTeam = attackerTeam; _authority = authority;
        _hitCharactersOnly = hitCharactersOnly; _ignoreRoot = ignoreRoot;
        _impactFx = impactFx; _impactFxScale = impactFxScale;
        _prev = transform.position;
        _launched = true;
        if (_vel.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(_vel);
    }

    /// <summary>Spawn a projectile from a prefab and launch it in one call (used by the cannon and both guns).</summary>
    public static CannonBallProjectile Spawn(GameObject prefab, Vector3 pos, Vector3 velocity, float gravity,
        float lifetime, int damage, ulong attacker, Team attackerTeam, bool authority, bool hitCharactersOnly,
        Transform ignoreRoot, GameObject impactFx, float impactFxScale)
    {
        if (prefab == null) return null;
        Quaternion rot = velocity.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(velocity) : Quaternion.identity;
        var go = Instantiate(prefab, pos, rot);
        var p = go.GetComponent<CannonBallProjectile>();
        if (p == null) p = go.AddComponent<CannonBallProjectile>();
        p.Launch(velocity, gravity, lifetime, damage, attacker, attackerTeam, authority, hitCharactersOnly, ignoreRoot, impactFx, impactFxScale);
        return p;
    }

    private void Update()
    {
        if (!_launched) return;
        float dt = Time.deltaTime;
        _vel += Vector3.up * (_gravity * dt);
        Vector3 next = transform.position + _vel * dt;

        Vector3 seg = next - _prev;
        float dist = seg.magnitude;
        if (dist > 1e-4f &&
            Physics.Raycast(_prev, seg / dist, out var hit, dist, ~0, QueryTriggerInteraction.Ignore) &&
            !IsPassThrough(hit.collider))
        {
            if (_authority)
            {
                var h = hit.collider.GetComponentInParent<Health>();
                IDamageable dmg = h != null ? h : (_hitCharactersOnly ? null : hit.collider.GetComponentInParent<IDamageable>());
                if (dmg != null && dmg.IsAlive) dmg.ApplyDamage(_damage, _attacker, hit.point);
            }
            Impact(hit.point);
            return;
        }

        transform.position = next;
        _prev = next;
        if (_vel.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(_vel);

        _life -= dt;
        if (_life <= 0f) Impact(transform.position);
    }

    // The shot flies harmlessly through the shooter (and its ship) and through friendly characters.
    private bool IsPassThrough(Collider c)
    {
        Transform t = c.transform;
        if (_ignoreRoot != null && (t == _ignoreRoot || t.IsChildOf(_ignoreRoot))) return true;
        var h = c.GetComponentInParent<Health>();
        return h != null && h.Side == _attackerTeam;
    }

    private void Impact(Vector3 point)
    {
        if (_impactFx != null)
        {
            var fx = Instantiate(_impactFx, point, Quaternion.identity);
            fx.transform.localScale *= _impactFxScale;
            Destroy(fx, 4f);
        }
        Destroy(gameObject);
    }
}
