using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Per-player save / load, netcode-aware. On spawn the OWNER loads its local slot (<see cref="SaveSystem.ActiveSlot"/>)
/// and applies it: it teleports itself to the saved spot and asks the SERVER to restore gold + inventory (those are
/// server-authoritative). The ESC "Save" button calls <see cref="SaveLocal"/> to write the current gold / inventory /
/// position back to that slot on the local machine — host and clients each keep their own file.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerSaveAgent : NetworkBehaviour
{
    private PlayerController _pc;
    private PlayerInventory _inv;
    private PlayerWallet _wallet;

    /// <summary>The most recently spawned local (owned) agent — the ESC menu finds it to save.</summary>
    public static PlayerSaveAgent Local { get; private set; }

    public override void OnNetworkSpawn()
    {
        _pc = GetComponent<PlayerController>();
        _inv = GetComponent<PlayerInventory>();
        _wallet = GetComponent<PlayerWallet>();
        if (IsOwner) { Local = this; Invoke(nameof(ApplyLoad), 0.4f); } // let the networked state settle first
    }

    public override void OnNetworkDespawn() { if (Local == this) Local = null; }

    private void ApplyLoad()
    {
        if (!IsOwner || !SaveSystem.HasSave(SaveSystem.ActiveSlot)) return;
        var d = SaveSystem.Load(SaveSystem.ActiveSlot);
        if (!d.exists) return;
        var pos = new Vector3(d.px, d.py, d.pz);
        if (pos.sqrMagnitude > 0.01f && _pc != null) _pc.TeleportTo(pos);                       // owner moves itself
        ApplyServerRpc(d.gold, d.weapons ?? new int[0], d.chests ?? new int[0], d.selected);     // server restores gold + inventory
        if (IsServer) RestoreWorldObjects(d);                                                    // host owns the world pickups
    }

    // Host only: clear the current loose pickups and respawn the saved ones at their spots.
    private void RestoreWorldObjects(PlayerSaveData d)
    {
        foreach (var w in DroppedWeapon.Active.ToArray())
            if (w != null && w.NetworkObject != null && w.NetworkObject.IsSpawned) w.NetworkObject.Despawn(true);
        foreach (var c in WorldChest.Active.ToArray())
            if (c != null && c.NetworkObject != null && c.NetworkObject.IsSpawned) c.NetworkObject.Despawn(true);

        if (_inv == null) return;
        if (d.dwIds != null)
            for (int i = 0; i < d.dwIds.Length; i++)
                _inv.SpawnDroppedWeaponServer((WeaponId)(byte)d.dwIds[i], new Vector3(d.dwX[i], d.dwY[i], d.dwZ[i]));
        if (d.wcIds != null)
            for (int i = 0; i < d.wcIds.Length; i++)
                _inv.SpawnWorldChestServer((ChestId)(byte)d.wcIds[i], new Vector3(d.wcX[i], d.wcY[i], d.wcZ[i]));
    }

    /// <summary>Owner: capture the current gold / inventory / position into the active slot.</summary>
    public bool SaveLocal()
    {
        if (!IsOwner) return false;
        var d = new PlayerSaveData
        {
            gold = _wallet != null ? _wallet.Gold : 0,
            weapons = new int[PlayerInventory.SlotCount],
            chests = new int[PlayerInventory.SlotCount],
            selected = _inv != null ? _inv.SelectedSlot : 0,
            px = transform.position.x, py = transform.position.y, pz = transform.position.z,
        };
        for (int i = 0; i < PlayerInventory.SlotCount; i++)
        {
            d.weapons[i] = _inv != null ? (byte)_inv.GetSlot(i) : 0;
            d.chests[i] = _inv != null ? (byte)_inv.GetChestSlot(i) : 0;
        }

        // Snapshot the loose world pickups (weapons + chests lying around) at their current spots.
        var dws = DroppedWeapon.Active;
        d.dwIds = new int[dws.Count]; d.dwX = new float[dws.Count]; d.dwY = new float[dws.Count]; d.dwZ = new float[dws.Count];
        for (int i = 0; i < dws.Count; i++)
        {
            var p = dws[i].transform.position;
            d.dwIds[i] = (byte)dws[i].Weapon; d.dwX[i] = p.x; d.dwY[i] = p.y; d.dwZ[i] = p.z;
        }
        var wcs = WorldChest.Active;
        d.wcIds = new int[wcs.Count]; d.wcX = new float[wcs.Count]; d.wcY = new float[wcs.Count]; d.wcZ = new float[wcs.Count];
        for (int i = 0; i < wcs.Count; i++)
        {
            var p = wcs[i].transform.position;
            d.wcIds[i] = (byte)wcs[i].Chest; d.wcX[i] = p.x; d.wcY[i] = p.y; d.wcZ[i] = p.z;
        }

        SaveSystem.Save(SaveSystem.ActiveSlot, d);
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void ApplyServerRpc(int gold, int[] weapons, int[] chests, int selected)
    {
        if (_wallet != null) _wallet.SetServer(gold);
        if (_inv != null) _inv.SetInventoryServer(weapons, chests, selected);
    }
}
