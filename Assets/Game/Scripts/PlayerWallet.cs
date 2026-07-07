using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative gold balance for a player. The value replicates to every client; the HUD counter and the
/// shop affordability check read it via <see cref="OnChanged"/> / <see cref="Gold"/>. Gold is earned by
/// delivering stolen chests to Home and spent buying weapons at the shop.
/// </summary>
[DisallowMultipleComponent]
public class PlayerWallet : NetworkBehaviour
{
    [SerializeField] private int startingGold = 0;

    private readonly NetworkVariable<int> _gold = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int Gold => _gold.Value;
    public event Action<int> OnChanged;

    public override void OnNetworkSpawn()
    {
        if (IsServer) _gold.Value = startingGold;
        _gold.OnValueChanged += Handle;
        OnChanged?.Invoke(_gold.Value);
    }
    public override void OnNetworkDespawn() => _gold.OnValueChanged -= Handle;
    private void Handle(int _, int now) => OnChanged?.Invoke(now);

    public bool CanAfford(int price) => _gold.Value >= price;

    /// <summary>Server: add (or, negative, remove) gold, clamped at 0.</summary>
    public void AddServer(int amount) { if (IsServer && amount != 0) _gold.Value = Mathf.Max(0, _gold.Value + amount); }

    /// <summary>Server: set the balance outright (used when loading a save).</summary>
    public void SetServer(int amount) { if (IsServer) _gold.Value = Mathf.Max(0, amount); }

    /// <summary>Server: spend <paramref name="price"/> if affordable. Returns false (and spends nothing) otherwise.</summary>
    public bool TrySpendServer(int price)
    {
        if (!IsServer || price < 0 || _gold.Value < price) return false;
        _gold.Value -= price;
        return true;
    }
}
