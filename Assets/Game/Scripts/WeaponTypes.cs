using UnityEngine;

/// <summary>Stable network id for each weapon (byte-sized so it packs into the inventory state).</summary>
public enum WeaponId : byte
{
    None = 0,
    HeavyGun = 1,
    LightGun = 2,
    WoodenSword = 3,
    GoldenSword = 4,
}

/// <summary>Drives which attack animation set and behaviour a weapon uses.</summary>
public enum WeaponCategory : byte
{
    Gun,
    Sword,
}

/// <summary>Static definition of one weapon: its visual prefab, category and how it sits in the hand.</summary>
[System.Serializable]
public class WeaponDef
{
    public WeaponId id;
    public string displayName = "Weapon";
    public WeaponCategory category;
    public GameObject prefab;         // visual prefab under Assets/Game/Prefabs/Weapons
    public Sprite icon;               // hotbar thumbnail
    public Vector3 holdPosition;      // local offset when parented to the right hand
    public Vector3 holdEuler;         // local rotation
    public float holdScale = 1f;
}
