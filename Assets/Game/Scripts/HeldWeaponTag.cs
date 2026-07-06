using UnityEngine;

/// <summary>
/// Marks a weapon that is pre-attached to the player's hand in the prefab. The inventory just toggles
/// these active/inactive by <see cref="weaponId"/> instead of instantiating — so each weapon keeps the
/// hand-fitted local transform you set on the prefab (tune it in the prefab, or copy/paste the
/// Transform values from play mode).
/// </summary>
public class HeldWeaponTag : MonoBehaviour
{
    public WeaponId weaponId;
}
