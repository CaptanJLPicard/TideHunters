using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Replicated inventory: 4 slots + the selected slot. Each slot holds a weapon XOR a chest (both byte
/// ids, so the whole thing packs cheaply for sync).
/// </summary>
public struct InvState : INetworkSerializable, IEquatable<InvState>
{
    public byte S0, S1, S2, S3; // WeaponId per slot
    public byte C0, C1, C2, C3; // ChestId per slot (a slot is a weapon XOR a chest)
    public byte Selected;       // 0..3

    public WeaponId Get(int i) => (WeaponId)(i == 0 ? S0 : i == 1 ? S1 : i == 2 ? S2 : S3);
    public void Set(int i, WeaponId v)
    {
        byte b = (byte)v;
        if (i == 0) S0 = b; else if (i == 1) S1 = b; else if (i == 2) S2 = b; else S3 = b;
    }

    public ChestId GetChest(int i) => (ChestId)(i == 0 ? C0 : i == 1 ? C1 : i == 2 ? C2 : C3);
    public void SetChest(int i, ChestId v)
    {
        byte b = (byte)v;
        if (i == 0) C0 = b; else if (i == 1) C1 = b; else if (i == 2) C2 = b; else C3 = b;
    }

    /// <summary>A slot is free only when it holds neither a weapon nor a chest.</summary>
    public bool SlotEmpty(int i) => Get(i) == WeaponId.None && GetChest(i) == ChestId.None;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref S0); s.SerializeValue(ref S1); s.SerializeValue(ref S2); s.SerializeValue(ref S3);
        s.SerializeValue(ref C0); s.SerializeValue(ref C1); s.SerializeValue(ref C2); s.SerializeValue(ref C3);
        s.SerializeValue(ref Selected);
    }

    public bool Equals(InvState o) =>
        S0 == o.S0 && S1 == o.S1 && S2 == o.S2 && S3 == o.S3 &&
        C0 == o.C0 && C1 == o.C1 && C2 == o.C2 && C3 == o.C3 &&
        Selected == o.Selected;
    public override bool Equals(object obj) => obj is InvState o && Equals(o);
    public override int GetHashCode()
    {
        int h = (S0 << 24) | (S1 << 16) | (S2 << 8) | Selected;
        return h ^ ((C0 << 24) | (C1 << 16) | (C2 << 8) | C3);
    }
}

/// <summary>
/// Server-authoritative inventory. Holds 4 slots (each a weapon or a chest) and a selected index,
/// replicated to everyone. The item in the selected slot is shown on every client: a weapon parented to the
/// hand, or — for a chest — carried in the arms (see <see cref="PlayerCarry"/>), so what you hold is always
/// in sync. The owner picks slots with keys 1-4 and drops the selected item with G.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerInventory : NetworkBehaviour
{
    public const int SlotCount = 4;

    [Tooltip("NetworkObject prefab spawned when a weapon is dropped (G).")]
    [SerializeField] private GameObject droppedWeaponPrefab;

    private readonly NetworkVariable<InvState> _inv = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Dictionary<WeaponId, GameObject> _weaponObjects;
    private WeaponId _shownWeapon = WeaponId.None;
    private PlayerCombat _combat;
    private PlayerController _pc;

    /// <summary>Raised on every client whenever the inventory or selection changes (for the hotbar UI + carry visual).</summary>
    public event Action OnChanged;

    public int SelectedSlot => _inv.Value.Selected;
    public WeaponId GetSlot(int i) => _inv.Value.Get(i);
    public ChestId GetChestSlot(int i) => _inv.Value.GetChest(i);
    public WeaponId SelectedWeapon => _inv.Value.Get(_inv.Value.Selected);
    public ChestId SelectedChest => _inv.Value.GetChest(_inv.Value.Selected);
    public bool IsCarryingChest => SelectedChest != ChestId.None;
    // A chest slot never holds a weapon, so this is naturally false while carrying — combat/aim stay off.
    public bool HasWeaponEquipped => SelectedWeapon != WeaponId.None;
    // Derive from the id (not the database) so a missing/broken database can never make a gun slash like a sword.
    public WeaponCategory SelectedCategory => WeaponDatabase.CategoryFor(SelectedWeapon);

    /// <summary>True when at least one slot is completely free (no weapon and no chest) — a chest can be taken.</summary>
    public bool HasEmptySlot
    {
        get
        {
            var s = _inv.Value;
            for (int i = 0; i < SlotCount; i++) if (s.SlotEmpty(i)) return true;
            return false;
        }
    }

    /// <summary>Muzzle transform (a "Muzzle" child) of the currently-held weapon, or null. Used to spawn gun fire FX.</summary>
    public Transform HeldMuzzle =>
        _shownWeapon != WeaponId.None && _weaponObjects != null && _weaponObjects.TryGetValue(_shownWeapon, out var go) && go != null
            ? go.transform.Find("Muzzle") : null;

    public override void OnNetworkSpawn()
    {
        _combat = GetComponent<PlayerCombat>();
        _pc = GetComponent<PlayerController>();
        CacheWeapons();
        _inv.OnValueChanged += OnInvChanged;
        ApplyHeld();
        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        _inv.OnValueChanged -= OnInvChanged;
    }

    /// <summary>Collects the weapons pre-attached to the hand (in the prefab) and hides them all.</summary>
    private void CacheWeapons()
    {
        _weaponObjects = new Dictionary<WeaponId, GameObject>();
        foreach (var tag in GetComponentsInChildren<HeldWeaponTag>(true))
        {
            if (tag.weaponId == WeaponId.None) continue;
            _weaponObjects[tag.weaponId] = tag.gameObject;
            foreach (var col in tag.GetComponentsInChildren<Collider>(true)) col.enabled = false;
            tag.gameObject.SetActive(false);
        }
    }

    private void OnInvChanged(InvState _, InvState __)
    {
        ApplyHeld();
        OnChanged?.Invoke();
    }

    private void Update()
    {
        if (!IsOwner) return;
        if (PauseMenu.IsOpen) return;
        if (_pc != null && _pc.IsDriving) return; // no weapon/slot input while steering
        if (_combat != null && _combat.IsAttacking) return; // no weapon switching mid-attack
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) RequestSelectSlot(0);
        else if (kb.digit2Key.wasPressedThisFrame) RequestSelectSlot(1);
        else if (kb.digit3Key.wasPressedThisFrame) RequestSelectSlot(2);
        else if (kb.digit4Key.wasPressedThisFrame) RequestSelectSlot(3);
        else if (kb.gKey.wasPressedThisFrame) RequestDrop();
    }

    public void RequestDrop()
    {
        if (!IsOwner) return;
        if (_pc != null && _pc.InWater) return; // never drop items into the sea (swimming or standing in water)
        if (IsServer) DropSelectedServer(); else DropRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void DropRpc() { DropSelectedServer(); }

    /// <summary>Server: drop the selected item in front of the player (a chest as a WorldChest, otherwise a
    /// weapon pickup) and clear its slot.</summary>
    private void DropSelectedServer()
    {
        if (!IsServer) return;
        var s = _inv.Value;

        ChestId chest = s.GetChest(s.Selected);
        if (chest != ChestId.None) { DropChestServer(chest); return; }

        if (droppedWeaponPrefab == null) return;
        WeaponId id = s.Get(s.Selected);
        if (id == WeaponId.None) return;
        s.Set(s.Selected, WeaponId.None);
        _inv.Value = s;

        Vector3 origin = transform.position + transform.forward * 1.2f + Vector3.up * 0.6f;
        ShipController ship = null;
        Vector3 pos;
        if (Physics.Raycast(origin, Vector3.down, out var hit, 4f))
        {
            pos = hit.point;
            ship = hit.collider.GetComponentInParent<ShipController>(); // dropped onto a ship's deck?
        }
        else pos = origin - Vector3.up * 0.5f;

        var go = Instantiate(droppedWeaponPrefab, pos, Quaternion.identity);
        var no = go.GetComponent<NetworkObject>();
        no.Spawn(true);
        go.GetComponent<DroppedWeapon>().SetWeaponServer(id);
        // Dropped onto a ship's deck: parent it to the hull so it rides along (not just chests — any drop).
        if (ship != null) no.TrySetParent(ship.NetworkObject, true);
    }

    /// <summary>Server: put the carried chest back into the world in front of the player and clear its slot.</summary>
    private void DropChestServer(ChestId chest)
    {
        var db = ChestDatabase.Instance;
        var def = db != null ? db.Get(chest) : null;
        if (def == null || def.worldPrefab == null) return;

        var s = _inv.Value;
        s.SetChest(s.Selected, ChestId.None);
        _inv.Value = s;

        // Drop point in front of the player; raycast down to find the ground under it.
        Vector3 dropXZ = transform.position + transform.forward * 1.3f;
        float groundY = transform.position.y;
        ShipController ship = null;
        if (Physics.Raycast(dropXZ + Vector3.up * 3f, Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore))
        {
            groundY = hit.point.y;
            ship = hit.collider.GetComponentInParent<ShipController>(); // dropped onto a ship's deck?
        }

        var go = Instantiate(def.worldPrefab, new Vector3(dropXZ.x, groundY, dropXZ.z),
            Quaternion.Euler(0f, transform.eulerAngles.y, 0f));

        // Snap so the chest's collider bottom rests exactly on the ground (independent of the prefab's pivot).
        Physics.SyncTransforms();
        var col = go.GetComponentInChildren<Collider>();
        if (col != null) go.transform.position += Vector3.up * (groundY - col.bounds.min.y);

        var no = go.GetComponent<NetworkObject>();
        no.Spawn(true);
        // Dropped onto a ship's deck: parent it to the hull so it rides along — you can haul chests by sea.
        if (ship != null) no.TrySetParent(ship.NetworkObject, true);
    }

    /// <summary>Server: spawn a loose weapon in the world at a saved spot (used to restore world pickups).</summary>
    public void SpawnDroppedWeaponServer(WeaponId id, Vector3 pos)
    {
        if (!IsServer || droppedWeaponPrefab == null || id == WeaponId.None) return;
        var go = Instantiate(droppedWeaponPrefab, pos, Quaternion.identity);
        go.GetComponent<NetworkObject>().Spawn(true);
        go.GetComponent<DroppedWeapon>().SetWeaponServer(id);
    }

    /// <summary>Server: spawn a world chest at a saved spot (used to restore world pickups).</summary>
    public void SpawnWorldChestServer(ChestId chest, Vector3 pos)
    {
        if (!IsServer || chest == ChestId.None) return;
        var def = ChestDatabase.Instance != null ? ChestDatabase.Instance.Get(chest) : null;
        if (def == null || def.worldPrefab == null) return;
        var go = Instantiate(def.worldPrefab, pos, Quaternion.identity);
        go.GetComponent<NetworkObject>().Spawn(true);
    }

    /// <summary>Server: overwrite the whole inventory from a save (a weapon OR a chest per slot + selection).</summary>
    public void SetInventoryServer(int[] weapons, int[] chests, int selected)
    {
        if (!IsServer) return;
        var s = new InvState();
        for (int i = 0; i < SlotCount; i++)
        {
            s.Set(i, weapons != null && i < weapons.Length ? (WeaponId)(byte)weapons[i] : WeaponId.None);
            s.SetChest(i, chests != null && i < chests.Length ? (ChestId)(byte)chests[i] : ChestId.None);
        }
        s.Selected = (byte)Mathf.Clamp(selected, 0, SlotCount - 1);
        _inv.Value = s;
    }

    /// <summary>Server: hand in the carried chest for gold — remove it from the inventory WITHOUT dropping it back
    /// into the world. Returns the delivered chest id (None if not carrying one). The carry visual + hotbar clear
    /// automatically from the replicated inventory.</summary>
    public ChestId DeliverSelectedChestServer()
    {
        if (!IsServer) return ChestId.None;
        ChestId c = SelectedChest;
        if (c == ChestId.None) return ChestId.None;
        var s = _inv.Value;
        s.SetChest(s.Selected, ChestId.None);
        _inv.Value = s;
        return c;
    }

    // ---- Owner requests → server ---------------------------------------------------------

    public void RequestSelectSlot(int slot) { if (IsOwner) SelectSlotRpc((byte)Mathf.Clamp(slot, 0, SlotCount - 1)); }

    // Owner-only + server-side clamped: never trust a client-supplied slot index.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void SelectSlotRpc(byte slot)
    {
        var s = _inv.Value; s.Selected = (byte)Mathf.Clamp(slot, 0, SlotCount - 1); _inv.Value = s;
    }

    // ---- Server logic --------------------------------------------------------------------

    /// <summary>
    /// Adds a weapon to the currently selected slot if it is empty (so you pick up into the slot you're
    /// on); otherwise to the first empty slot without changing the selection. False if the inventory is full.
    /// </summary>
    public bool AddWeaponServer(WeaponId id)
    {
        if (!IsServer || id == WeaponId.None) return false;
        var s = _inv.Value;

        if (s.SlotEmpty(s.Selected))
        {
            s.Set(s.Selected, id);
            _inv.Value = s;
            return true;
        }
        for (int i = 0; i < SlotCount; i++)
        {
            if (s.SlotEmpty(i))
            {
                s.Set(i, id);
                _inv.Value = s;
                return true;
            }
        }
        return false; // full
    }

    /// <summary>
    /// Adds a chest to the selected slot if it is empty, else to the first empty slot — and selects that
    /// slot so the player starts carrying it immediately (a held weapon is hidden while the chest is held).
    /// False if every slot is occupied.
    /// </summary>
    public bool AddChestServer(ChestId id)
    {
        if (!IsServer || id == ChestId.None) return false;
        var s = _inv.Value;

        int target = -1;
        if (s.SlotEmpty(s.Selected)) target = s.Selected;
        else { for (int i = 0; i < SlotCount; i++) if (s.SlotEmpty(i)) { target = i; break; } }
        if (target < 0) return false; // full

        s.SetChest(target, id);
        s.Selected = (byte)target; // auto-select → carry it now
        _inv.Value = s;
        return true;
    }

    /// <summary>Server: wipe the inventory back to empty (used on a match restart).</summary>
    public void ResetServer()
    {
        if (IsServer) _inv.Value = default;
    }

    // ---- Held weapon visual (every client) -----------------------------------------------

    private void ApplyHeld()
    {
        // A chest slot has WeaponId.None, so selecting a chest hides every weapon here; PlayerCarry then
        // shows the chest.
        WeaponId want = SelectedWeapon;
        if (want == _shownWeapon) return;
        _shownWeapon = want;
        if (_weaponObjects == null) return;
        foreach (var kv in _weaponObjects)
            kv.Value.SetActive(kv.Key == want);
    }
}
