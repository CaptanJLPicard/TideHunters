using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative hull hit points for a ship. Damage (from cannon fire) is applied on the server and
/// replicated. Optionally swaps a set of damage-state visual objects (e.g. the enemy ship's
/// Health100/70/40/10 meshes): the highest stage whose <see cref="stageMinFraction"/> the current health
/// still meets is shown, the rest hidden. Exposes <see cref="IDamageable"/> so a cannonball can hit the hull.
/// </summary>
[DisallowMultipleComponent]
public class ShipHealth : NetworkBehaviour, IDamageable
{
    [SerializeField] private int maxHealth = 100;

    [Header("Damage-state visuals (optional, paired by index; ordered pristine → wrecked)")]
    [Tooltip("Visual roots for each damage stage, e.g. [Health100, Health70, Health40, Health10].")]
    [SerializeField] private GameObject[] stageObjects;
    [Tooltip("Show stage i while health fraction >= this, e.g. [0.7, 0.4, 0.1, 0]. Same length as stageObjects.")]
    [SerializeField] private float[] stageMinFraction = { 0.7f, 0.4f, 0.1f, 0f };

    private readonly NetworkVariable<int> _hp = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _dead = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private ulong _lastAttacker;

    public int Max => maxHealth;
    public int Current => _hp.Value;
    public float Fraction => maxHealth > 0 ? Mathf.Clamp01((float)_hp.Value / maxHealth) : 0f;
    public bool IsAlive => !_dead.Value;
    public Transform Transform => transform;

    public event Action<int, int> OnChanged;
    public event Action<ulong> OnDeath;

    public override void OnNetworkSpawn()
    {
        if (IsServer && _hp.Value <= 0) _hp.Value = maxHealth;
        _hp.OnValueChanged += HandleHpChanged;
        _dead.OnValueChanged += HandleDeadChanged;
        ApplyVisuals(Fraction);
        OnChanged?.Invoke(_hp.Value, maxHealth);
    }

    public override void OnNetworkDespawn()
    {
        _hp.OnValueChanged -= HandleHpChanged;
        _dead.OnValueChanged -= HandleDeadChanged;
    }

    private void HandleHpChanged(int _, int now)
    {
        ApplyVisuals(maxHealth > 0 ? Mathf.Clamp01((float)now / maxHealth) : 0f);
        OnChanged?.Invoke(now, maxHealth);
    }
    private void HandleDeadChanged(bool was, bool now) { if (now && !was) OnDeath?.Invoke(_lastAttacker); }

    public void ApplyDamage(int amount, ulong attacker, Vector3 hitPoint)
    {
        if (!IsServer || _dead.Value || amount <= 0) return;
        _lastAttacker = attacker;
        int v = Mathf.Max(0, _hp.Value - amount);
        _hp.Value = v;
        if (v == 0) _dead.Value = true;
    }

    /// <summary>Show only the highest damage stage the current health still satisfies.</summary>
    private void ApplyVisuals(float fraction)
    {
        if (stageObjects == null || stageObjects.Length == 0) return;
        int show = -1;
        for (int i = 0; i < stageObjects.Length; i++)
        {
            float min = (stageMinFraction != null && i < stageMinFraction.Length) ? stageMinFraction[i] : 0f;
            if (fraction >= min) { show = i; break; }
        }
        if (show < 0) show = stageObjects.Length - 1; // wrecked fallback
        for (int i = 0; i < stageObjects.Length; i++)
            if (stageObjects[i] != null && stageObjects[i].activeSelf != (i == show))
                stageObjects[i].SetActive(i == show);
    }
}
