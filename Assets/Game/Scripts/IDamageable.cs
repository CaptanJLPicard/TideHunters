using UnityEngine;

/// <summary>
/// Anything a gunshot, cannonball or melee hit can damage. Damage is ALWAYS applied on the server
/// (Netcode authoritative); implementations replicate the resulting hit points to clients themselves.
/// Look it up on a hit with <c>collider.GetComponentInParent&lt;IDamageable&gt;()</c>.
/// </summary>
public interface IDamageable
{
    /// <summary>Server-only. Subtract <paramref name="amount"/> hit points. <paramref name="attacker"/> is the
    /// client id that caused it (<see cref="Unity.Netcode.NetworkManager.ServerClientId"/> for the AI), and
    /// <paramref name="hitPoint"/> is the world impact point (for FX / knockback).</summary>
    void ApplyDamage(int amount, ulong attacker, Vector3 hitPoint);

    /// <summary>False once destroyed / dead, so a shot can skip corpses and wrecks.</summary>
    bool IsAlive { get; }

    /// <summary>The damageable's transform (its world position — e.g. a ship hull or a body).</summary>
    Transform Transform { get; }
}
