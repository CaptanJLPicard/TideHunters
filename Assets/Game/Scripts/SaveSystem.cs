using System.IO;
using UnityEngine;

/// <summary>One player's saved progress — gold, the hotbar (a weapon OR chest id per slot + which is selected), and
/// where they were standing. Small + JsonUtility-friendly (int arrays, not enums).</summary>
[System.Serializable]
public struct PlayerSaveData
{
    public bool exists;
    public int gold;
    public int[] weapons;   // WeaponId per hotbar slot (0 = none)
    public int[] chests;    // ChestId per hotbar slot (0 = none)
    public int selected;    // selected hotbar slot
    public float px, py, pz; // player world position

    // Loose equippable objects lying in the world (restored to these spots on load).
    public int[] dwIds; public float[] dwX, dwY, dwZ; // dropped weapons
    public int[] wcIds; public float[] wcX, wcY, wcZ; // world chests
}

/// <summary>
/// Local, per-computer save slots (2). Each player saves their OWN progress to their OWN machine (host and
/// clients alike) — the files never travel over the network; only the loaded values are applied to the
/// networked player on spawn. Stored as JSON under <see cref="Application.persistentDataPath"/>.
/// </summary>
public static class SaveSystem
{
    public const int SlotCount = 2;

    /// <summary>The slot the local player is currently playing (picked in the menu; the ESC "Save" writes to it).</summary>
    public static int ActiveSlot = 0;

    private static string PathFor(int slot) => Path.Combine(Application.persistentDataPath, "tidehunters_slot" + slot + ".json");

    public static bool HasSave(int slot) => slot >= 0 && slot < SlotCount && File.Exists(PathFor(slot));

    public static void Save(int slot, PlayerSaveData data)
    {
        if (slot < 0 || slot >= SlotCount) return;
        data.exists = true;
        try { File.WriteAllText(PathFor(slot), JsonUtility.ToJson(data)); }
        catch (System.Exception e) { Debug.LogWarning("[SaveSystem] save failed: " + e.Message); }
    }

    public static PlayerSaveData Load(int slot)
    {
        if (!HasSave(slot)) return default;
        try { return JsonUtility.FromJson<PlayerSaveData>(File.ReadAllText(PathFor(slot))); }
        catch (System.Exception e) { Debug.LogWarning("[SaveSystem] load failed: " + e.Message); return default; }
    }

    /// <summary>A one-line summary for the menu slot button ("Empty" or "1250 G").</summary>
    public static string Summary(int slot)
    {
        if (!HasSave(slot)) return "Empty";
        var d = Load(slot);
        return d.gold + " G";
    }
}
