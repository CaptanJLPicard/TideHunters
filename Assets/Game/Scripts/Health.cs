using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Which side a combatant fights for — used for target selection and friendly-fire filtering.</summary>
public enum Team : byte { Player = 0, Enemy = 1 }

/// <summary>
/// Server-authoritative hit points for a character (player or enemy NPC). 100 max by default. Damage is
/// applied on the server via <see cref="IDamageable.ApplyDamage"/> and the value replicates to every client;
/// UI and death visuals subscribe to <see cref="OnChanged"/> / <see cref="OnDeath"/>. The <c>attacker</c> in
/// <see cref="OnDeath"/> is only reliable on the server (kill attribution lives there).
/// </summary>
[DisallowMultipleComponent]
public class Health : NetworkBehaviour, IDamageable
{
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private Team team = Team.Player;

    /// <summary>Every spawned Health (players + NPCs, on every client) — for AI target selection.</summary>
    public static readonly List<Health> All = new List<Health>();

    public Team Side => team;

    private readonly NetworkVariable<int> _hp = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<DamageType> _lastType = new(DamageType.Generic,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _dead = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private ulong _lastAttacker;

    /// <summary>The kind of hit that most recently damaged this character — drives the death animation.</summary>
    public DamageType LastDamageType => _lastType.Value;

    public int Max => maxHealth;
    public int Current => _hp.Value;
    public float Fraction => maxHealth > 0 ? Mathf.Clamp01((float)_hp.Value / maxHealth) : 0f;
    public bool IsAlive => !_dead.Value;
    public Transform Transform => transform;

    /// <summary>Fired on every client whenever hp changes: (current, max).</summary>
    public event Action<int, int> OnChanged;
    /// <summary>Fired on every client when hp first reaches 0. The <c>attacker</c> id is server-authoritative.</summary>
    public event Action<ulong> OnDeath;

    public override void OnNetworkSpawn()
    {
        if (IsServer && _hp.Value <= 0) _hp.Value = maxHealth; // seed on first spawn (keeps a set value on scene reload)
        _hp.OnValueChanged += HandleHpChanged;
        _dead.OnValueChanged += HandleDeadChanged;
        if (!All.Contains(this)) All.Add(this);
        OnChanged?.Invoke(_hp.Value, maxHealth); // prime any UI already listening
    }

    public override void OnNetworkDespawn()
    {
        _hp.OnValueChanged -= HandleHpChanged;
        _dead.OnValueChanged -= HandleDeadChanged;
        All.Remove(this);
    }

    private void HandleHpChanged(int _, int now) => OnChanged?.Invoke(now, maxHealth);
    private void HandleDeadChanged(bool was, bool now) { if (now && !was) OnDeath?.Invoke(_lastAttacker); }

    public void ApplyDamage(int amount, ulong attacker, Vector3 hitPoint, DamageType type)
    {
        if (!IsServer || _dead.Value || amount <= 0) return;
        _lastAttacker = attacker;
        _lastType.Value = type;
        int v = Mathf.Max(0, _hp.Value - amount);
        _hp.Value = v;
        if (v == 0) _dead.Value = true;
    }

    /// <summary>Server: revive to full (e.g. on respawn / round reset).</summary>
    public void ResetServer()
    {
        if (!IsServer) return;
        _dead.Value = false;
        _hp.Value = maxHealth;
    }

    /// <summary>Server: revive a downed character with a partial amount of hit points (teammate revive).</summary>
    public void ReviveServer(int hp)
    {
        if (!IsServer || !_dead.Value) return;
        _dead.Value = false;
        _hp.Value = Mathf.Clamp(hp, 1, maxHealth);
    }
}
