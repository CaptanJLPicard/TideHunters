using System.Collections.Generic;
using UnityEngine;

/// <summary>One weapon on sale: a reference to its existing display object plus which weapon it grants.</summary>
[System.Serializable]
public class ShopEntry
{
    public Transform item;
    public WeaponId weapon;
}

/// <summary>
/// Plain scene singleton (no NetworkObject — avoids the fragility of networking prefab-instance shop
/// meshes). It only references the shop's display objects and their weapon ids. Pickup is driven by
/// the player's networked <see cref="PlayerInteractor"/>: the server consumes an item and tells every
/// client to hide it, so the shop stays in sync while the meshes themselves need no network components.
/// </summary>
public class ShopManager : MonoBehaviour
{
    public static ShopManager Instance { get; private set; }

    [SerializeField] private ShopEntry[] entries;

    private readonly HashSet<int> _consumedServer = new HashSet<int>(); // server-only guard
    private readonly HashSet<int> _hidden = new HashSet<int>();

    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    public int Count => entries != null ? entries.Length : 0;
    public bool IsAvailable(int id) => id >= 0 && id < Count && entries[id].item != null && !_hidden.Contains(id);
    public Vector3 Position(int id) => entries[id].item.position;

    /// <summary>Better aim target than the pivot: the visual centre of the item (handles off-centre pivots like sword handles).</summary>
    public Vector3 AimPoint(int id)
    {
        var item = entries[id].item;
        var rend = item.GetComponentInChildren<Renderer>();
        return rend != null ? rend.bounds.center : item.position;
    }
    public WeaponId Weapon(int id) => entries[id].weapon;
    public string Name(int id) =>
        WeaponDatabase.Instance != null ? WeaponDatabase.Instance.NameOf(entries[id].weapon) : entries[id].weapon.ToString();

    public int FindNearest(Vector3 pos, float range)
    {
        int best = -1; float bestSqr = range * range;
        for (int i = 0; i < Count; i++)
        {
            if (!IsAvailable(i)) continue;
            float d = (entries[i].item.position - pos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = i; }
        }
        return best;
    }

    /// <summary>Which available shop item does this hit transform (or an ancestor) belong to? -1 if none.</summary>
    public int FindByTransform(Transform t)
    {
        for (int i = 0; i < Count; i++)
        {
            if (!IsAvailable(i)) continue;
            var item = entries[i].item;
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur == item) return i;
        }
        return -1;
    }

    /// <summary>Server-only: reserve the item if still available. Returns its weapon.</summary>
    public bool TryConsumeServer(int id, out WeaponId weapon)
    {
        weapon = WeaponId.None;
        if (id < 0 || id >= Count || _consumedServer.Contains(id)) return false;
        _consumedServer.Add(id);
        weapon = entries[id].weapon;
        return true;
    }

    /// <summary>Every client: hide a consumed item.</summary>
    public void HideItem(int id)
    {
        if (id < 0 || id >= Count) return;
        _hidden.Add(id);
        if (entries[id].item != null) entries[id].item.gameObject.SetActive(false);
    }

    public void SetEntries(ShopEntry[] e) => entries = e; // editor setup
}
