using UnityEngine;

/// <summary>Stable network id for each carriable chest (byte-sized, mirrors <see cref="WeaponId"/>).</summary>
public enum ChestId : byte
{
    None = 0,
    Small = 1,
    Medium = 2,
    Large = 3,
}

/// <summary>
/// Static definition of one chest: the networked prefab that lies in the world, its hotbar icon, and how
/// it sits in the hands while carried. Mirrors <see cref="WeaponDef"/>.
/// </summary>
[System.Serializable]
public class ChestDef
{
    public ChestId id;
    public string displayName = "Chest";
    public GameObject worldPrefab;    // networked chest prefab (Assets/Game/Prefabs/Chest) — has NetworkObject + WorldChest
    public Sprite icon;               // hotbar thumbnail
    public Vector3 carryPosition;     // local offset under the carry anchor (where it sits in the hands)
    public Vector3 carryEuler;        // local rotation
    public float carryScale = 1f;

    [Header("Swim — raise both arms so the chest stays above the water")]
    [Tooltip("Degrees both arms are raised while treading water (standing/idle in the water). The chest " +
             "tracks the hands, so raising the arms lifts the chest with them.")]
    public float swimIdleArmRaise = 0f;
    [Tooltip("Degrees both arms are raised while swimming (moving in the water).")]
    public float swimMoveArmRaise = 0f;
}
