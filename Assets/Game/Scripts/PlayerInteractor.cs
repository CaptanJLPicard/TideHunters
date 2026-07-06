using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner-only interaction. Finds the shop item / world chest under the crosshair (or the nearest dropped
/// weapon), shows the animated "Press E to …" prompt, and on E asks the server to act. The server validates
/// and applies it (consume a shop item, carry a chest, pick up a drop), staying authoritative.
/// </summary>
[RequireComponent(typeof(PlayerInventory))]
public class PlayerInteractor : NetworkBehaviour
{
    [Tooltip("Max distance from the player to an item to interact with it.")]
    [SerializeField] private float range = 4.5f;
    [Tooltip("How wide (deg) around the crosshair an item still counts as 'looked at'. Larger = more forgiving.")]
    [SerializeField] private float aimAngle = 18f;

    private PlayerInventory _inv;
    private PlayerCombat _combat;

    public override void OnNetworkSpawn()
    {
        _inv = GetComponent<PlayerInventory>();
        _combat = GetComponent<PlayerCombat>();
    }

    private void Update()
    {
        if (!IsOwner) return;

        var hud = GameHUD.Instance;
        var shop = ShopManager.Instance;
        bool ePressed = Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;

        // 1) A shop item under the crosshair takes priority.
        int shopTarget = shop != null ? FindLookedAt(shop) : -1;
        if (shopTarget >= 0)
        {
            hud?.ShowPrompt($"Press E to equip {shop.Name(shopTarget)}");
            if (ePressed) PickupShopRpc(shopTarget);
            return;
        }

        // 2) A world chest under the crosshair — carry it (if there is a free slot).
        var chest = FindLookedAtChest();
        if (chest != null)
        {
            // Hands full mid-attack — don't offer to carry (mirrors the slot-switch lock in PlayerInventory.Update).
            if (_combat != null && _combat.IsAttacking) { hud?.HidePrompt(); return; }
            if (_inv != null && _inv.HasEmptySlot)
            {
                string cname = ChestDatabase.Instance != null ? ChestDatabase.Instance.NameOf(chest.Chest) : chest.Chest.ToString();
                hud?.ShowPrompt($"Press E to carry {cname}");
                if (ePressed) CarryChestRpc(chest.NetworkObject);
            }
            else
            {
                hud?.ShowPrompt("Not enough space");
            }
            return;
        }

        // 3) Otherwise the nearest dropped weapon within reach.
        var drop = FindNearestDrop();
        if (drop != null)
        {
            string name = WeaponDatabase.Instance != null ? WeaponDatabase.Instance.NameOf(drop.Weapon) : drop.Weapon.ToString();
            hud?.ShowPrompt($"Press E to pick up {name}");
            if (ePressed) PickupDropRpc(drop.NetworkObject);
            return;
        }

        hud?.HidePrompt();
    }

    private DroppedWeapon FindNearestDrop()
    {
        DroppedWeapon best = null;
        float bestSqr = range * range;
        foreach (var d in DroppedWeapon.Active)
        {
            if (d == null) continue;
            float sq = (d.transform.position - transform.position).sqrMagnitude;
            if (sq < bestSqr) { bestSqr = sq; best = d; }
        }
        return best;
    }

    /// <summary>The available world chest nearest to the crosshair that is within reach (angular look-at,
    /// like shop items — forgiving of imprecise aim). Null if none.</summary>
    private WorldChest FindLookedAtChest()
    {
        var cam = Camera.main;
        if (cam == null) return null;
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        float rangeSqr = range * range;

        WorldChest best = null;
        float bestAngle = aimAngle;
        foreach (var c in WorldChest.Active)
        {
            if (c == null) continue;
            Vector3 aim = c.AimPoint;
            if ((aim - transform.position).sqrMagnitude > rangeSqr) continue;   // near the player
            float angle = Vector3.Angle(camFwd, aim - camPos);                  // near the crosshair
            if (angle < bestAngle) { bestAngle = angle; best = c; }
        }
        return best;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void CarryChestRpc(NetworkObjectReference chestRef)
    {
        if (_inv == null || !chestRef.TryGet(out var no)) return;
        if (_combat != null && _combat.IsAttacking) return; // never switch to carrying mid-attack
        var chest = no.GetComponent<WorldChest>();
        if (chest == null) return;
        if ((no.transform.position - transform.position).sqrMagnitude > (range + 1.5f) * (range + 1.5f)) return;
        if (_inv.AddChestServer(chest.Chest))
            no.Despawn(true);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void PickupDropRpc(NetworkObjectReference dropRef)
    {
        if (_inv == null || !dropRef.TryGet(out var no)) return;
        var drop = no.GetComponent<DroppedWeapon>();
        if (drop == null) return;
        if ((no.transform.position - transform.position).sqrMagnitude > (range + 1.5f) * (range + 1.5f)) return;
        if (_inv.AddWeaponServer(drop.Weapon))
            no.Despawn(true);
    }

    /// <summary>
    /// The available shop item nearest to the crosshair that is within reach. Uses angular proximity to
    /// the camera forward (not a thin ray), so it forgives imprecise aim and reliably catches thin
    /// blades like swords that a single ray would miss.
    /// </summary>
    private int FindLookedAt(ShopManager shop)
    {
        var cam = Camera.main;
        if (cam == null) return -1;
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        float rangeSqr = range * range;

        int best = -1;
        float bestAngle = aimAngle;
        for (int i = 0; i < shop.Count; i++)
        {
            if (!shop.IsAvailable(i)) continue;
            Vector3 aim = shop.AimPoint(i);
            if ((aim - transform.position).sqrMagnitude > rangeSqr) continue;   // near the player
            float angle = Vector3.Angle(camFwd, aim - camPos);                  // near the crosshair
            if (angle < bestAngle) { bestAngle = angle; best = i; }
        }
        return best;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void PickupShopRpc(int shopId)
    {
        var shop = ShopManager.Instance;
        if (shop == null || _inv == null || !shop.IsAvailable(shopId)) return;
        if ((shop.Position(shopId) - transform.position).sqrMagnitude > (range + 1.5f) * (range + 1.5f)) return;

        if (shop.TryConsumeServer(shopId, out var weapon) && _inv.AddWeaponServer(weapon))
            HideShopItemRpc(shopId);
    }

    [Rpc(SendTo.Everyone)]
    private void HideShopItemRpc(int shopId) => ShopManager.Instance?.HideItem(shopId);
}
