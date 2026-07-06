using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A weapon lying in the world after being dropped (G). Server-spawned NetworkObject: it replicates its
/// <see cref="WeaponId"/>, shows that weapon's model, and gently bobs up and down. Any player can walk up
/// and press E to pick it up (handled by <see cref="PlayerInteractor"/>), which despawns it.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DroppedWeapon : NetworkBehaviour
{
    /// <summary>All dropped weapons currently in the world (every client), for proximity pickup.</summary>
    public static readonly List<DroppedWeapon> Active = new List<DroppedWeapon>();

    [SerializeField] private float restHeight = 0.35f; // how high the model floats above the drop point
    [SerializeField] private float bobAmplitude = 0.06f;
    [SerializeField] private float bobSpeed = 1.6f;
    [SerializeField] private float spinSpeed = 45f;

    private readonly NetworkVariable<byte> _weaponId = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public WeaponId Weapon => (WeaponId)_weaponId.Value;

    private GameObject _visual;
    private float _phase;

    /// <summary>Server: set the weapon this pickup grants. Call right after spawning.</summary>
    public void SetWeaponServer(WeaponId id) { if (IsServer) _weaponId.Value = (byte)id; }

    public override void OnNetworkSpawn()
    {
        Active.Add(this);
        _phase = transform.position.x + transform.position.z; // desync bob between drops
        _weaponId.OnValueChanged += (_, __) => BuildVisual();
        BuildVisual();
    }

    public override void OnNetworkDespawn() => Active.Remove(this);

    private void BuildVisual()
    {
        if (_visual != null) Destroy(_visual);
        var def = WeaponDatabase.Instance != null ? WeaponDatabase.Instance.Get(Weapon) : null;
        if (def == null || def.prefab == null) return;
        _visual = Instantiate(def.prefab, transform);
        _visual.transform.localPosition = Vector3.up * restHeight;
        _visual.transform.localRotation = Quaternion.identity;
        foreach (var col in _visual.GetComponentsInChildren<Collider>(true)) col.enabled = false;
    }

    private void Update()
    {
        if (_visual == null) return;
        float t = Time.time * bobSpeed + _phase;
        _visual.transform.localPosition = new Vector3(0f, restHeight + Mathf.Sin(t) * bobAmplitude, 0f);
        _visual.transform.localRotation = Quaternion.Euler(0f, Time.time * spinSpeed, 0f);
    }
}
