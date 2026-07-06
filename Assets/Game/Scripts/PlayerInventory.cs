using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Replicated inventory: 4 weapon slots + the selected slot. Byte-packed for cheap sync.</summary>
public struct InvState : INetworkSerializable, IEquatable<InvState>
{
    public byte S0, S1, S2, S3; // WeaponId per slot
    public byte Selected;       // 0..3

    public WeaponId Get(int i) => (WeaponId)(i == 0 ? S0 : i == 1 ? S1 : i == 2 ? S2 : S3);
    public void Set(int i, WeaponId v)
    {
        byte b = (byte)v;
        if (i == 0) S0 = b; else if (i == 1) S1 = b; else if (i == 2) S2 = b; else S3 = b;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref S0); s.SerializeValue(ref S1); s.SerializeValue(ref S2); s.SerializeValue(ref S3);
        s.SerializeValue(ref Selected);
    }
    public bool Equals(InvState o) => S0 == o.S0 && S1 == o.S1 && S2 == o.S2 && S3 == o.S3 && Selected == o.Selected;
    public override bool Equals(object obj) => obj is InvState o && Equals(o);
    public override int GetHashCode() => (S0 << 24) | (S1 << 16) | (S2 << 8) | Selected;
}

/// <summary>
/// Server-authoritative weapon inventory. Holds 4 slots and a selected index, replicated to everyone.
/// The weapon in the selected slot is shown parented to the right hand on every client (owner and
/// remotes) so what you hold is always in sync. The owner picks slots with keys 1-4.
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

    /// <summary>Raised on every client whenever the inventory or selection changes (for the hotbar UI).</summary>
    public event Action OnChanged;

    public int SelectedSlot => _inv.Value.Selected;
    public WeaponId GetSlot(int i) => _inv.Value.Get(i);
    public WeaponId SelectedWeapon => _inv.Value.Get(_inv.Value.Selected);
    public bool HasWeaponEquipped => SelectedWeapon != WeaponId.None;
    // Derive from the id (not the database) so a missing/broken database can never make a gun slash like a sword.
    public WeaponCategory SelectedCategory => WeaponDatabase.CategoryFor(SelectedWeapon);

    /// <summary>Muzzle transform (a "Muzzle" child) of the currently-held weapon, or null. Used to spawn gun fire FX.</summary>
    public Transform HeldMuzzle =>
        _shownWeapon != WeaponId.None && _weaponObjects != null && _weaponObjects.TryGetValue(_shownWeapon, out var go) && go != null
            ? go.transform.Find("Muzzle") : null;

    public override void OnNetworkSpawn()
    {
        _combat = GetComponent<PlayerCombat>();
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
        if (IsServer) DropSelectedServer(); else DropRpc();
    }

    [Rpc(SendTo.Server)]
    private void DropRpc() { DropSelectedServer(); }

    /// <summary>Server: drop the selected weapon in front of the player as a pickup and clear its slot.</summary>
    private void DropSelectedServer()
    {
        if (!IsServer || droppedWeaponPrefab == null) return;
        var s = _inv.Value;
        WeaponId id = s.Get(s.Selected);
        if (id == WeaponId.None) return;
        s.Set(s.Selected, WeaponId.None);
        _inv.Value = s;

        Vector3 origin = transform.position + transform.forward * 1.2f + Vector3.up * 0.6f;
        Vector3 pos = Physics.Raycast(origin, Vector3.down, out var hit, 4f) ? hit.point : origin - Vector3.up * 0.5f;
        var go = Instantiate(droppedWeaponPrefab, pos, Quaternion.identity);
        go.GetComponent<NetworkObject>().Spawn(true);
        go.GetComponent<DroppedWeapon>().SetWeaponServer(id);
    }

    // ---- Owner requests → server ---------------------------------------------------------

    public void RequestSelectSlot(int slot) { if (IsOwner) SelectSlotRpc((byte)Mathf.Clamp(slot, 0, SlotCount - 1)); }
    public void RequestPickup(WeaponId id) { if (IsOwner) PickupRpc(id); }

    [Rpc(SendTo.Server)]
    private void SelectSlotRpc(byte slot)
    {
        var s = _inv.Value; s.Selected = slot; _inv.Value = s;
    }

    [Rpc(SendTo.Server)]
    private void PickupRpc(WeaponId id) => AddWeaponServer(id);

    // ---- Server logic --------------------------------------------------------------------

    /// <summary>
    /// Adds a weapon to the currently selected slot if it is empty (so you pick up into the slot you're
    /// on); otherwise to the first empty slot without changing the selection. False if the inventory is full.
    /// </summary>
    public bool AddWeaponServer(WeaponId id)
    {
        if (!IsServer || id == WeaponId.None) return false;
        var s = _inv.Value;

        if (s.Get(s.Selected) == WeaponId.None)
        {
            s.Set(s.Selected, id);
            _inv.Value = s;
            return true;
        }
        for (int i = 0; i < SlotCount; i++)
        {
            if (s.Get(i) == WeaponId.None)
            {
                s.Set(i, id);
                _inv.Value = s;
                return true;
            }
        }
        return false; // full
    }

    // ---- Held weapon visual (every client) -----------------------------------------------

    private void ApplyHeld()
    {
        WeaponId want = SelectedWeapon;
        if (want == _shownWeapon) return;
        _shownWeapon = want;
        if (_weaponObjects == null) return;
        foreach (var kv in _weaponObjects)
            kv.Value.SetActive(kv.Key == want);
    }
}
