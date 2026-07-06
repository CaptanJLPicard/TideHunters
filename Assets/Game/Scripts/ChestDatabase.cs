using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central chest registry, loaded from Resources so any system (inventory, HUD, carry visual, drop) can
/// resolve a <see cref="ChestId"/> to its definition without a per-object reference. Mirrors
/// <see cref="WeaponDatabase"/>.
///
/// NOTE: this ScriptableObject lives in its OWN file (matching the class name). Unity ties a
/// ScriptableObject's script reference to the file name; keeping it in a shared file makes the .asset's
/// script link fragile and it can lose its type on reimport. Do not merge it into another file.
/// </summary>
[CreateAssetMenu(fileName = "ChestDatabase", menuName = "TideHunters/Chest Database")]
public class ChestDatabase : ScriptableObject
{
    public ChestDef[] chests;

    private Dictionary<ChestId, ChestDef> _map;
    private static ChestDatabase _instance;

    /// <summary>Shared instance, loaded once from Resources/ChestDatabase.</summary>
    public static ChestDatabase Instance =>
        _instance != null ? _instance : (_instance = Resources.Load<ChestDatabase>("ChestDatabase"));

    public ChestDef Get(ChestId id)
    {
        if (id == ChestId.None) return null;
        if (_map == null)
        {
            _map = new Dictionary<ChestId, ChestDef>();
            if (chests != null)
                foreach (var c in chests)
                    if (c != null) _map[c.id] = c;
        }
        return _map.TryGetValue(id, out var def) ? def : null;
    }

    public string NameOf(ChestId id)
    {
        var d = Get(id);
        return d != null ? d.displayName : id.ToString();
    }

    public Sprite IconOf(ChestId id)
    {
        var d = Get(id);
        return d != null ? d.icon : null;
    }
}
