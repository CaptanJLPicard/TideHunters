using UnityEngine;

/// <summary>
/// Aim data consumed by <see cref="PlayerSpineAim"/> so the SAME spine-bend + gun-arm aim IK can be driven
/// by either a human player or an AI crew member. Implemented by <c>PlayerController</c> (reads its own
/// replicated aim + combat/inventory) and by the enemy NPC controller (reads its brain's target). All
/// values are authoritative/replicated, so every client deforms the skeleton identically.
/// </summary>
public interface IAimSource
{
    /// <summary>Look pitch in degrees (aim up positive/negative per the camera convention).</summary>
    float AimPitch { get; }

    /// <summary>Horizontal angle (deg) between where the aim points and where the body faces — the spine twist.</summary>
    float AimYawOffset { get; }

    /// <summary>Planar locomotion speed (0..1); used to fade the idle torso sway down while walking.</summary>
    float PlanarSpeed { get; }

    /// <summary>World-space direction the crosshair/gun points (from replicated yaw+pitch).</summary>
    Vector3 AimDirection { get; }

    /// <summary>True while aiming a gun — blends in the arm aim so the barrel lines up with the target.</summary>
    bool AimingGun { get; }

    /// <summary>True to hold the pure animated pose with no aim lean/arm-aim (e.g. carrying a chest or steering).</summary>
    bool SuppressAim { get; }
}
