using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Reacts to the local/remote player's death and revival. On death it fires a "Die" animator trigger (a hook —
/// harmless until a Die state/clip is wired into the player controller) and freezes the corpse's control so it
/// can't move or shoot; for the OWNING player it opens the "YOU DIED" screen. On revival (round restart) it
/// re-enables control and hides the screen. Runs on every peer so the death reads the same everywhere.
/// </summary>
[RequireComponent(typeof(Health))]
public class PlayerDeath : NetworkBehaviour
{
    private static readonly int DieHash = Animator.StringToHash("Die");
    private static readonly int DeathTypeHash = Animator.StringToHash("DeathType");

    private Health _health;
    private Animator _animator;
    private bool _frozen;

    /// <summary>True while this player is down (dead, awaiting revive or the death screen) — the interactor reads
    /// it to offer "Press E to revive" to teammates.</summary>
    public bool IsDown => _frozen;

    public override void OnNetworkSpawn()
    {
        _health = GetComponent<Health>();
        _animator = GetComponent<Animator>();
        _health.OnDeath += HandleDeath;
        _health.OnChanged += HandleHpChanged;
        if (!_health.IsAlive) HandleDeath(0); // spawned into an already-dead state
    }

    public override void OnNetworkDespawn()
    {
        if (_health != null) { _health.OnDeath -= HandleDeath; _health.OnChanged -= HandleHpChanged; }
    }

    private void HandleHpChanged(int cur, int max)
    {
        if (cur > 0 && _frozen) Revive();      // came back to life (round restart) → un-freeze
    }

    private void HandleDeath(ulong attacker)
    {
        if (_frozen) return;
        _frozen = true;
        if (_animator != null)
        {
            _animator.SetInteger(DeathTypeHash, _health != null ? (int)_health.LastDamageType : 1); // 2=Sword else gun
            _animator.SetTrigger(DieHash);
            ZeroDeathLayers();                                  // let the full-body death anim show cleanly
        }
        SetControl(false);                                      // corpse can't move / shoot / interact
        // Solo → the YOU DIED screen. With teammates alive, stay down and wait to be revived (no screen yet).
        if (IsOwner && !AnyTeammateAlive()) DeathScreen.Instance?.Show();
    }

    private void ZeroDeathLayers()
    {
        int ub = _animator.GetLayerIndex("UpperBody");
        int carry = _animator.GetLayerIndex("Carry");
        if (ub >= 0) _animator.SetLayerWeight(ub, 0f);
        if (carry >= 0) _animator.SetLayerWeight(carry, 0f);
    }

    private bool AnyTeammateAlive()
    {
        foreach (var h in Health.All)
            if (h != null && h != _health && h.Side == Team.Player && h.IsAlive) return true;
        return false;
    }

    private void Revive()
    {
        _frozen = false;
        SetControl(true);
        if (IsOwner) DeathScreen.Instance?.Hide();
    }

    private void SetControl(bool on)
    {
        // Freeze (don't disable) the controller: a corpse must keep riding the moving deck and replicating its
        // pose (so it stays glued to the ship on every client) — it just stops taking input.
        var pc = GetComponent<PlayerController>(); if (pc != null) pc.SetFrozen(!on);
        var combat = GetComponent<PlayerCombat>(); if (combat != null) combat.enabled = on;
        var interactor = GetComponent<PlayerInteractor>(); if (interactor != null) interactor.enabled = on;
    }
}
