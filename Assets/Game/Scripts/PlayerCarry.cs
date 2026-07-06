using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shows the chest the player is currently carrying (their selected inventory slot holds a
/// <see cref="ChestId"/>) in their hands, and blends in the full-body carry pose via a dedicated "Carry"
/// animator layer. Both are derived purely from the replicated inventory, so every client — owner and
/// remotes — sees the same chest carried with the same animation, with no extra network state. Mirrors how
/// <see cref="PlayerCombat"/> drives the UpperBody attack layer.
///
/// The carried chest is a local, cosmetic clone of the chest's world prefab with its networking and physics
/// stripped (never spawned). Every LateUpdate — after the Animator has posed the skeleton — the chest is
/// placed at the midpoint of the two hand bones, so it moves and sways WITH the arms/torso and reads as
/// genuinely carried. While swimming, both arms are raised (by a per-chest, live-tunable amount) so the
/// chest lifts clear of the water; because the chest tracks the hands, raising the arms raises the chest.
/// The per-chest offset/rotation/scale/arm-raise are read LIVE from <see cref="ChestDatabase"/> each frame,
/// so they can be tuned in the ChestDatabase asset's inspector while the game is playing.
/// </summary>
[RequireComponent(typeof(PlayerInventory))]
[RequireComponent(typeof(Animator))]
public class PlayerCarry : NetworkBehaviour
{
    [Tooltip("Smoothness of the carry-layer blend in / out (higher = snappier, lower = softer). Eased.")]
    [SerializeField] private float weightSharpness = 9f;
    [Tooltip("Smoothness of the swim arm-raise easing when entering / leaving the water (higher = snappier).")]
    [SerializeField] private float armRaiseSharpness = 7f;
    [Tooltip("Smoothness of the tread<->swim blend (the raw swim speed flips 0/1 instantly; this eases it).")]
    [SerializeField] private float moveSharpness = 5f;

    private PlayerInventory _inv;
    private PlayerController _pc;   // for the driving pose
    private Animator _animator;
    private int _carryLayer = -1;
    private Transform _leftHand, _rightHand, _leftUpperArm, _rightUpperArm;
    private GameObject _visual;
    private ChestId _shown = ChestId.None;
    private float _weight;
    private float _smoothRaise;
    private float _smoothMove;

    /// <summary>True on every client while this player holds the carry pose — carrying a chest, or steering a
    /// ship (hands on the wheel reuse the same two-handed pose).</summary>
    public bool IsCarrying => (_inv != null && _inv.IsCarryingChest) || (_pc != null && _pc.IsDriving);

    public override void OnNetworkSpawn()
    {
        _inv = GetComponent<PlayerInventory>();
        _pc = GetComponent<PlayerController>();
        _animator = GetComponent<Animator>();
        _carryLayer = _animator != null ? _animator.GetLayerIndex("Carry") : -1;
        if (_animator != null)
        {
            _leftHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            _leftUpperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        }

        _inv.OnChanged += Refresh;
        Refresh();
    }

    public override void OnNetworkDespawn()
    {
        if (_inv != null) _inv.OnChanged -= Refresh;
        if (_visual != null) { Destroy(_visual); _visual = null; }
    }

    private void OnDestroy()
    {
        if (_visual != null) { Destroy(_visual); _visual = null; }
    }

    // Swap the carried chest visual when the selected slot's chest changes (mirrors PlayerInventory.ApplyHeld).
    private void Refresh()
    {
        ChestId want = _inv != null ? _inv.SelectedChest : ChestId.None;
        if (want == _shown) return;
        _shown = want;

        if (_visual != null) { Destroy(_visual); _visual = null; }
        if (want == ChestId.None) return;

        var def = ChestDatabase.Instance != null ? ChestDatabase.Instance.Get(want) : null;
        if (def == null || def.worldPrefab == null) return;

        // Instantiate unparented, strip networking synchronously, THEN parent — so a nested NetworkObject
        // never briefly exists under the (spawned) player. World transform is driven in LateUpdate.
        _visual = Instantiate(def.worldPrefab);
        StripVisual(_visual);
        _visual.transform.SetParent(transform, false);
    }

    // The carried visual is cosmetic and local-only: remove networking + physics so it never tries to spawn,
    // collide, or fall. WorldChest is removed before NetworkObject (it RequireComponents it).
    private static void StripVisual(GameObject go)
    {
        foreach (var wc in go.GetComponentsInChildren<WorldChest>(true)) DestroyImmediate(wc);
        foreach (var no in go.GetComponentsInChildren<NetworkObject>(true)) DestroyImmediate(no);
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rb);
        foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
    }

    private void Update()
    {
        if (_animator == null || _carryLayer < 0) return;
        float target = IsCarrying ? 1f : 0f;
        _weight = Mathf.Lerp(_weight, target, 1f - Mathf.Exp(-weightSharpness * Time.deltaTime)); // smooth ease
        _animator.SetLayerWeight(_carryLayer, _weight);
    }

    // Glue the chest to the hands AFTER the animator has posed the skeleton, so it sways with the carry
    // animation. Offset/rotation/scale/arm-raise are read live from the ChestDatabase (tunable at runtime).
    private void LateUpdate()
    {
        if (_visual == null) return;
        var def = ChestDatabase.Instance != null ? ChestDatabase.Instance.Get(_shown) : null;
        if (def == null) return;

        // While swimming, raise both arms so the carried chest lifts clear of the water. The chest tracks
        // the hands, so raising the arms raises the chest with them. Applied BEFORE reading the hand
        // positions so the chest reflects the raised arms. Amount is per-chest and live-tunable; blends by
        // move speed between the tread (idle) and swim (moving) values.
        // Smooth the move factor so the tread<->swim arm-raise blend is gradual. The raw swim speed flips
        // 0/1 instantly (and on remote clients arrives in snapshot steps), which would jerk the chest up/down
        // when you stop swimming; easing it here keeps the transition smooth on host and clients alike.
        bool swimming = _pc != null && _pc.IsSwimming;
        float moveTarget = swimming ? Mathf.Clamp01(_pc.PlanarSpeed) : 0f;
        _smoothMove = Mathf.Lerp(_smoothMove, moveTarget, 1f - Mathf.Exp(-moveSharpness * Time.deltaTime));

        float raiseTarget = swimming ? Mathf.Lerp(def.swimIdleArmRaise, def.swimMoveArmRaise, _smoothMove) : 0f;
        _smoothRaise = Mathf.Lerp(_smoothRaise, raiseTarget, 1f - Mathf.Exp(-armRaiseSharpness * Time.deltaTime)); // smooth ease
        if (Mathf.Abs(_smoothRaise) > 0.01f && _leftUpperArm != null && _rightUpperArm != null)
        {
            // Both arms rotate around the SAME world axis (the character's right), so they raise/lower forward-up
            // by the exact same amount — symmetric, never crossed.
            Quaternion lift = Quaternion.AngleAxis(-_smoothRaise, transform.right);
            _leftUpperArm.rotation = lift * _leftUpperArm.rotation;
            _rightUpperArm.rotation = lift * _rightUpperArm.rotation;
        }

        // Midpoint of the two hands (now including any arm raise) = where the chest is held.
        Vector3 mid = (_leftHand != null && _rightHand != null)
            ? (_leftHand.position + _rightHand.position) * 0.5f
            : transform.position + Vector3.up * 1.05f + transform.forward * 0.22f;

        Quaternion baseRot = transform.rotation;
        _visual.transform.position = mid + baseRot * def.carryPosition;
        _visual.transform.rotation = baseRot * Quaternion.Euler(def.carryEuler);
        _visual.transform.localScale = Vector3.one * (def.carryScale <= 0f ? 1f : def.carryScale);
    }
}
