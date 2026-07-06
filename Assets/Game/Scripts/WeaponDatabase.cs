using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central weapon registry. Loaded from Resources so any system (inventory, pickups, dropped weapons)
/// can resolve a <see cref="WeaponId"/> to its definition without a per-object reference.
///
/// NOTE: this ScriptableObject lives in its OWN file (matching the class name). Unity ties a
/// ScriptableObject's script reference to the file name; keeping it in a shared file (WeaponTypes.cs)
/// made the .asset's script link fragile and it kept losing its type on reimport. Do not merge it back.
/// </summary>
[CreateAssetMenu(fileName = "WeaponDatabase", menuName = "TideHunters/Weapon Database")]
public class WeaponDatabase : ScriptableObject
{
    public WeaponDef[] weapons;

    private Dictionary<WeaponId, WeaponDef> _map;
    private static WeaponDatabase _instance;

    /// <summary>Shared instance, loaded once from Resources/WeaponDatabase.</summary>
    public static WeaponDatabase Instance =>
        _instance != null ? _instance : (_instance = Resources.Load<WeaponDatabase>("WeaponDatabase"));

    /// <summary>
    /// Category derived purely from the id — no database needed. Used as a robust fallback so a
    /// missing/broken database can never make a gun behave like a sword.
    /// </summary>
    public static WeaponCategory CategoryFor(WeaponId id) =>
        (id == WeaponId.WoodenSword || id == WeaponId.GoldenSword) ? WeaponCategory.Sword : WeaponCategory.Gun;

    public WeaponDef Get(WeaponId id)
    {
        if (id == WeaponId.None) return null;
        if (_map == null)
        {
            _map = new Dictionary<WeaponId, WeaponDef>();
            if (weapons != null)
                foreach (var w in weapons)
                    if (w != null) _map[w.id] = w;
        }
        return _map.TryGetValue(id, out var def) ? def : null;
    }

    public WeaponCategory CategoryOf(WeaponId id)
    {
        var d = Get(id);
        return d != null ? d.category : CategoryFor(id);
    }

    public string NameOf(WeaponId id)
    {
        var d = Get(id);
        return d != null ? d.displayName : id.ToString();
    }

    public Sprite IconOf(WeaponId id)
    {
        var d = Get(id);
        return d != null ? d.icon : null;
    }
}
