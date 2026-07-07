using UnityEngine;

/// <summary>How a hit was dealt — drives the death animation (gun vs sword) and future FX.</summary>
public enum DamageType : byte { Generic = 0, Gun = 1, Sword = 2, Cannon = 3 }

/// <summary>
/// Anything a gunshot, cannonball or melee hit can damage. Damage is ALWAYS applied on the server
/// (Netcode authoritative); implementations replicate the resulting hit points to clients themselves.
/// Look it up on a hit with <c>collider.GetComponentInParent&lt;IDamageable&gt;()</c>.
/// </summary>
public interface IDamageable
{
    /// <summary>Server-only. Subtract <paramref name="amount"/> hit points. <paramref name="attacker"/> is the
    /// client id that caused it (<see cref="Unity.Netcode.NetworkManager.ServerClientId"/> for the AI),
    /// <paramref name="hitPoint"/> is the world impact point, and <paramref name="type"/> is the weapon kind.</summary>
    void ApplyDamage(int amount, ulong attacker, Vector3 hitPoint, DamageType type);

    /// <summary>False once destroyed / dead, so a shot can skip corpses and wrecks.</summary>
    bool IsAlive { get; }

    /// <summary>The damageable's transform (its world position — e.g. a ship hull or a body).</summary>
    Transform Transform { get; }
}
