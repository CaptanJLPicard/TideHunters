using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative hull hit points for a ship. Damage (cannon fire) is applied on the server and
/// replicated. Every client derives the damage feedback from the replicated health fraction:
///  - optional damage-state meshes (enemy Health100/70/40/10 stages),
///  - fire VFX spawned progressively / in sequence at the ship's FirePoints as the hull is battered,
///  - optional sinking (player ship): at 0 hp the hull is disabled (no thrust) and slowly settles + lists.
/// </summary>
[DisallowMultipleComponent]
public class ShipHealth : NetworkBehaviour, IDamageable
{
    [SerializeField] private int maxHealth = 100;

    [Header("Damage-state meshes (optional; pristine → wrecked)")]
    [SerializeField] private GameObject[] stageObjects;
    [SerializeField] private float[] stageMinFraction = { 0.7f, 0.4f, 0.1f, 0f };

    [Header("Damage fire VFX (FirePoints)")]
    [Tooltip("FX_Fire_01 prefab, spawned at fire points as the hull takes damage.")]
    [SerializeField] private GameObject fireEffectPrefab;
    [Tooltip("Parent whose LEAF descendants are the fire points (e.g. FirePoints). All childless descendants are used.")]
    [SerializeField] private Transform fireRoots;
    [Tooltip("Max simultaneous fires (near 0 hp).")]
    [SerializeField] private int maxFires = 14;
    [Tooltip("Fires start appearing below this health fraction (no fire near full).")]
    [SerializeField] private float fireStartFraction = 0.85f;
    [Tooltip("How fast fires appear/vanish (points per second) — the smooth 'in-sequence' ramp.")]
    [SerializeField] private float fireRampSpeed = 2.5f;
    [SerializeField] private float fireScale = 1f;

    [Header("Sinking")]
    [SerializeField] private bool sinkOnDeath = false;
    [Tooltip("Seconds the wreck stays afloat (dead in the water, burning) before it begins to sink.")]
    [SerializeField] private float sinkDelay = 0f;
    [SerializeField] private float sinkSpeed = 0.35f;
    [SerializeField] private float sinkMaxDepth = 9f;
    [SerializeField] private float sinkTilt = 16f;

    private float _deadSince = -1f;
    public bool IsDead => _dead.Value;
    /// <summary>Seconds since this hull was destroyed (−1 while alive).</summary>
    public float DeadFor => _deadSince < 0f ? -1f : Time.time - _deadSince;

    private readonly NetworkVariable<int> _hp = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _dead = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private ulong _lastAttacker;

    private ShipController _ship;
    private readonly List<Transform> _firePoints = new List<Transform>();
    private readonly List<GameObject> _fires = new List<GameObject>();
    private float _fireCount; // eased active-fire count
    private float _sink;

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
        _ship = GetComponent<ShipController>();
        CollectFirePoints();
        _hp.OnValueChanged += HandleHpChanged;
        _dead.OnValueChanged += HandleDeadChanged;
        ApplyVisuals(Fraction);
        OnChanged?.Invoke(_hp.Value, maxHealth);
    }

    public override void OnNetworkDespawn()
    {
        _hp.OnValueChanged -= HandleHpChanged;
        _dead.OnValueChanged -= HandleDeadChanged;
        for (int i = 0; i < _fires.Count; i++) if (_fires[i] != null) Destroy(_fires[i]);
        _fires.Clear();
    }

    private void CollectFirePoints()
    {
        _firePoints.Clear();
        if (fireRoots == null) return;
        foreach (var t in fireRoots.GetComponentsInChildren<Transform>(true))
            if (t != fireRoots && t.childCount == 0) _firePoints.Add(t);
    }

    private void HandleHpChanged(int _, int now)
    {
        ApplyVisuals(maxHealth > 0 ? Mathf.Clamp01((float)now / maxHealth) : 0f);
        OnChanged?.Invoke(now, maxHealth);
    }
    private void HandleDeadChanged(bool was, bool now)
    {
        if (now && !was) { _deadSince = Time.time; OnDeath?.Invoke(_lastAttacker); }
        else if (!now) _deadSince = -1f;
    }

    public void ApplyDamage(int amount, ulong attacker, Vector3 hitPoint, DamageType type)
    {
        if (!IsServer || _dead.Value || amount <= 0) return;
        _lastAttacker = attacker;
        int v = Mathf.Max(0, _hp.Value - amount);
        _hp.Value = v;
        if (v == 0) _dead.Value = true;
    }

    public void ResetServer()
    {
        if (!IsServer) return;
        _dead.Value = false; _hp.Value = maxHealth;
    }

    private void Update()
    {
        float frac = Fraction;

        // Fire count target: 0 at/above fireStartFraction, ramps to maxFires at 0 hp. Eased for a smooth,
        // in-sequence spread of flames as the hull is worn down.
        float t = Mathf.InverseLerp(fireStartFraction, 0f, frac);
        int target = Mathf.Clamp(Mathf.RoundToInt(t * maxFires), 0, Mathf.Min(maxFires, _firePoints.Count));
        _fireCount = Mathf.MoveTowards(_fireCount, target, fireRampSpeed * Time.deltaTime);
        int want = Mathf.RoundToInt(_fireCount);
        while (_fires.Count < want && _fires.Count < _firePoints.Count) SpawnFire(_fires.Count);
        while (_fires.Count > want && _fires.Count > 0)
        {
            var f = _fires[_fires.Count - 1];
            _fires.RemoveAt(_fires.Count - 1);
            if (f != null) Destroy(f);
        }

        if (_dead.Value && sinkOnDeath)
        {
            if (_ship != null) _ship.Disabled = true;            // wrecked: no thrust
            if (_deadSince < 0f) _deadSince = Time.time;         // safety (spawned already dead)
            if (Time.time >= _deadSince + sinkDelay)             // burn/float a while, then go under
                _sink = Mathf.MoveTowards(_sink, sinkMaxDepth, sinkSpeed * Time.deltaTime);
        }
    }

    private void SpawnFire(int index)
    {
        if (fireEffectPrefab == null || index >= _firePoints.Count) { _fires.Add(null); return; }
        var p = _firePoints[index];
        var fx = Instantiate(fireEffectPrefab, p.position, p.rotation, p); // parented → rides + sinks with the ship
        fx.transform.localScale = Vector3.one * fireScale;
        _fires.Add(fx);
    }

    private void LateUpdate()
    {
        if (_sink <= 0.001f) return;
        // Applied AFTER ShipController positioned the hull this frame (exec order -100 vs default): settle + list.
        float t = sinkMaxDepth > 0.01f ? Mathf.Clamp01(_sink / sinkMaxDepth) : 1f;
        transform.position -= Vector3.up * _sink;
        transform.rotation *= Quaternion.Euler(sinkTilt * t, 0f, sinkTilt * 0.6f * t);
    }

    private void ApplyVisuals(float fraction)
    {
        if (stageObjects == null || stageObjects.Length == 0) return;
        int show = -1;
        for (int i = 0; i < stageObjects.Length; i++)
        {
            float min = (stageMinFraction != null && i < stageMinFraction.Length) ? stageMinFraction[i] : 0f;
            if (fraction >= min) { show = i; break; }
        }
        if (show < 0) show = stageObjects.Length - 1;
        for (int i = 0; i < stageObjects.Length; i++)
            if (stageObjects[i] != null && stageObjects[i].activeSelf != (i == show))
                stageObjects[i].SetActive(i == show);
    }
}
