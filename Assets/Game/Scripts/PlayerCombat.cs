using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Weapon attacks, layered onto the upper body so the character keeps moving. Server-authoritative.
///
///  - Guns: hold right mouse to AIM — GunPlay plays from the start up to the aim hold-frame and freezes
///    (GunSpeed=0). Left mouse FIRES — it resumes through the shot+reload to the end, then re-raises from
///    the start back to the aim hold (the clip is a musket cycle that ends lowered, so re-raising reads
///    naturally and keeps every transition a crossfade). Every re-aim restarts from the beginning.
///    Release drops back to locomotion.
///  - Swords: left mouse plays one of two slash clips at random (server picks so everyone agrees).
///
/// Smoothness trick: two identical gun states (GunA/GunB) are alternated so every transition is a real
/// cross-state CrossFade (Unity does not crossfade a state onto itself). <see cref="AimingGun"/> lets
/// the spine lean the torso so the gun lines up with the crosshair.
/// </summary>
[RequireComponent(typeof(PlayerInventory))]
[RequireComponent(typeof(Animator))]
public class PlayerCombat : NetworkBehaviour
{
    [Tooltip("Normalized GunPlay time held at while aiming (frame ~60 / 152).")]
    [SerializeField] private float aimHold = 0.39f;
    [Tooltip("Playback speed of the raise (0→aim) portion.")]
    [SerializeField] private float raiseSpeed = 1.6f;
    [Tooltip("Playback speed of the fire (aim→end) portion.")]
    [SerializeField] private float fireSpeed = 1.3f;
    [SerializeField] private float weightSharpness = 14f;
    [SerializeField] private float enterBlend = 0.12f;
    [Tooltip("Sword slash direction: decay rate of the remembered view-sweep before the hit. Higher = " +
             "shorter memory / more reactive to the latest flick; lower = more general momentum.")]
    [SerializeField] private float lookMomentumDecay = 5f;
    [Tooltip("Log the detected sword-sweep direction to the console (to verify/tune). Turn off when happy.")]
    [SerializeField] private bool debugLogSlashDir = true;

    [Header("Fire FX")]
    [Tooltip("Muzzle flash/smoke prefab spawned at the held gun's Muzzle at the shot frame. Spawned " +
             "unparented and world-space (the puff stays put while the gun moves) and locally on every " +
             "client from the networked shot so everyone sees it.")]
    [SerializeField] private GameObject muzzleFx;
    [SerializeField] private float muzzleFxLifetime = 3.5f;
    [Tooltip("Optional extra rotation (deg) on top of the muzzle's forward, in case the look needs a nudge. " +
             "The prefab is authored +Z-forward, so 0 spawns it straight along the barrel.")]
    public Vector3 muzzleFxEuler = Vector3.zero;
    [Tooltip("Uniform size/speed multiplier for the FX (the prefab is authored large). Public — tune live.")]
    public float muzzleFxScale = 0.5f;
    [Tooltip("Camera shake (deg) kicked on the owner at the shot, for punchier fire feedback.")]
    [SerializeField] private float cameraShake = 1.4f;

    [Header("Gun bullet (physical + tracer)")]
    [Tooltip("Damage a gun bullet deals to an enemy NPC.")]
    [SerializeField] private int gunDamage = 40;
    [Tooltip("Tracer bullet prefab (fast, trailing). Fired from the held gun's Muzzle along the aim.")]
    [SerializeField] private GameObject bulletPrefab;
    [SerializeField] private GameObject bulletImpactFx;
    [SerializeField] private float bulletSpeed = 95f;
    [SerializeField] private float bulletGravity = 0f;
    [SerializeField] private float bulletLife = 1.2f;
    [Tooltip("Ray origin height above the player's feet, used only when the gun has no Muzzle child.")]
    [SerializeField] private float gunEyeHeight = 1.5f;

    [Header("Sword (server melee)")]
    [SerializeField] private int swordDamage = 60;
    [SerializeField] private float swordRange = 2.6f;
    [Tooltip("Hit cone (deg) in front of the player.")]
    [SerializeField] private float swordArc = 150f;

    /// <summary>Debug: freeze the player in the gun aim pose (ignores input) so the aim can be tuned in the
    /// Inspector. Toggled by the button on PlayerSpineAim. Not serialized — never persists past play.</summary>
    [System.NonSerialized] public bool debugHoldAim;

    private readonly NetworkVariable<bool> _aiming = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireStamp = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _slashStamp = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _slashVariant = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private PlayerInventory _inv;
    private Animator _animator;
    private int _upperLayer;
    private static readonly int GunAHash = Animator.StringToHash("GunA");
    private static readonly int GunBHash = Animator.StringToHash("GunB");
    private static readonly int SlashAHash = Animator.StringToHash("SlashA");
    private static readonly int SlashBHash = Animator.StringToHash("SlashB");
    private static readonly int GunSpeedHash = Animator.StringToHash("GunSpeed");

    private enum GunPhase { Idle, Raising, Holding, Firing }
    private GunPhase _phase = GunPhase.Idle;
    private int _gunSlot; // 0=GunA, 1=GunB (alternated for real crossfades)

    private bool _localAiming;
    private int _lastFireStamp, _lastSlashStamp;
    private bool _wasAiming;
    private PlayerController _pc;
    private float _lookMomentum; // owner-side leaky-integrated recent view-sweep → picks the slash direction
    private float _prevCamYaw;
    private bool _slashing;
    private bool _slashArmed;      // becomes true once the new slash's restart is confirmed (norm rewound)
    private string _slashName;
    private float _weight;
    private float _slashLockUntil; // owner-side: block re-slash/switch until the swing has actually started

    /// <summary>True on every client while this player is aiming a gun — drives the spine aim lean.</summary>
    public bool AimingGun => debugHoldAim ||
        (_aiming.Value && _inv != null && _inv.HasWeaponEquipped && _inv.SelectedCategory == WeaponCategory.Gun);

    /// <summary>
    /// True while an attack animation is playing (a sword slash, or a gun in its fire+reload). Used to
    /// block re-triggering a slash mid-swing and to lock weapon switching until the attack finishes.
    /// </summary>
    public bool IsAttacking => _phase == GunPhase.Firing || Time.time < _slashLockUntil || _slashing;

    private int CurGunHash => _gunSlot == 0 ? GunAHash : GunBHash;
    private string CurGunName => _gunSlot == 0 ? "GunA" : "GunB";

    public override void OnNetworkSpawn()
    {
        _inv = GetComponent<PlayerInventory>();
        _pc = GetComponent<PlayerController>();
        _animator = GetComponent<Animator>();
        _upperLayer = _animator.GetLayerIndex("UpperBody");
        _lastFireStamp = _fireStamp.Value;
        _lastSlashStamp = _slashStamp.Value;
        if (_pc != null) _prevCamYaw = _pc.CameraYaw;
    }

    private void Update()
    {
        if (IsOwner)
        {
            TrackLookMomentum();
            if (!debugHoldAim && !PauseMenu.IsOpen && (_pc == null || !_pc.IsDriving)) OwnerInput();
        }
        DriveAnimation();
    }

    // Accumulate the recent left/right view-sweep (camera yaw change) with a leaky decay, so at the
    // instant of a sword swing we know which way the player was steering — right up to that moment.
    private void TrackLookMomentum()
    {
        if (_pc == null) return;
        float yaw = _pc.CameraYaw;
        _lookMomentum = _lookMomentum * Mathf.Exp(-lookMomentumDecay * Time.deltaTime) + Mathf.DeltaAngle(_prevCamYaw, yaw);
        _prevCamYaw = yaw;
    }

    private void OwnerInput()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        bool hasWeapon = _inv.HasWeaponEquipped;
        var cat = _inv.SelectedCategory;

        if (hasWeapon && cat == WeaponCategory.Gun)
        {
            bool aimHeld = mouse.rightButton.isPressed;
            if (aimHeld != _localAiming) { _localAiming = aimHeld; SetAimRpc(aimHeld); }
            // Fire only from the aim hold (not mid-raise/mid-fire), so each shot is a single, deliberate one.
            if (_localAiming && mouse.leftButton.wasPressedThisFrame && _phase == GunPhase.Holding) FireRpc();
        }
        else
        {
            if (_localAiming) { _localAiming = false; SetAimRpc(false); }
            // Only start a new slash when the previous one has finished — ignore clicks mid-swing so it
            // plays to the end instead of restarting. The swing direction follows the recent mouse motion:
            // sweeping the view right→left plays SlashA, left→right plays SlashB (feels like you steer the blade).
            if (hasWeapon && cat == WeaponCategory.Sword && mouse.leftButton.wasPressedThisFrame && !IsAttacking)
            {
                _slashLockUntil = Time.time + 0.3f; // cover the RPC round-trip until the slash state is live
                byte variant = _lookMomentum < 0f ? (byte)1 : (byte)0; // sweep left → SlashB, right → SlashA
                if (debugLogSlashDir)
                    Debug.Log($"[SwordDir] momentum={_lookMomentum:F1} → {(variant == 0 ? "SlashA (swept right)" : "SlashB (swept left)")}");
                SlashRpc(variant);
            }
        }
    }

    [Rpc(SendTo.Server)] private void SetAimRpc(bool a) => _aiming.Value = a;
    [Rpc(SendTo.Server)] private void FireRpc() { if (_aiming.Value) { _fireStamp.Value++; ServerFireBullet(); } }
    [Rpc(SendTo.Server)] private void SlashRpc(byte variant) { _slashVariant.Value = variant; _slashStamp.Value++; ServerSwordHit(); }

    // Server-authoritative melee: a forward cone sweep that damages every enemy character in reach. Guns fire a
    // bullet (ServerFireBullet); swords land here — so all four shop weapons deal damage.
    private void ServerSwordHit()
    {
        Vector3 origin = transform.position + Vector3.up * 1.0f;
        Vector3 fwd = transform.forward;
        var hits = Physics.OverlapSphere(origin, swordRange, ~0, QueryTriggerInteraction.Ignore);
        var done = new HashSet<Health>();
        foreach (var c in hits)
        {
            var h = c.GetComponentInParent<Health>();
            if (h == null || !h.IsAlive || h.Side == Team.Player || done.Contains(h)) continue; // enemies only, never players
            Vector3 to = h.transform.position - origin; to.y = 0f;
            if (to.sqrMagnitude > 0.01f && Vector3.Angle(fwd, to) > swordArc * 0.5f) continue;   // must be in the swing cone
            done.Add(h);
            h.ApplyDamage(swordDamage, OwnerClientId, h.transform.position, DamageType.Sword);
        }
    }

    // Server-authoritative gun shot: a physical tracer bullet from the gun muzzle along the replicated aim.
    // The server copy carries the damage (Team.Player → only hurts enemies, never other players or ships);
    // every client spawns a matching cosmetic tracer so the trail is visible everywhere.
    private void ServerFireBullet()
    {
        if (_pc == null || bulletPrefab == null) return;
        Vector3 origin = _inv != null && _inv.HeldMuzzle != null ? _inv.HeldMuzzle.position : transform.position + Vector3.up * gunEyeHeight;
        Vector3 vel = _pc.AimDirection.normalized * bulletSpeed;
        CannonBallProjectile.Spawn(bulletPrefab, origin, vel, bulletGravity, bulletLife, gunDamage,
            OwnerClientId, Team.Player, DamageType.Gun, true, true, transform, bulletImpactFx, 1f);
        FireBulletClientRpc(origin, vel);
    }

    [Rpc(SendTo.NotServer)]
    private void FireBulletClientRpc(Vector3 pos, Vector3 vel) =>
        CannonBallProjectile.Spawn(bulletPrefab, pos, vel, bulletGravity, bulletLife, 0, 0UL, Team.Player, DamageType.Gun, false, true, transform, bulletImpactFx, 1f);

    private void DriveAnimation()
    {
        if (_animator == null || _upperLayer < 0) return;
        float dt = Time.deltaTime;

        bool fireEvent = _fireStamp.Value != _lastFireStamp; if (fireEvent) _lastFireStamp = _fireStamp.Value;
        bool slashEvent = _slashStamp.Value != _lastSlashStamp; if (slashEvent) _lastSlashStamp = _slashStamp.Value;

        bool gunAiming = AimingGun;
        float targetWeight = 0f;

        if (gunAiming)
        {
            targetWeight = 1f;
            _slashing = false;
            if (!_wasAiming) StartRaise();

            var st = _animator.GetCurrentAnimatorStateInfo(_upperLayer);
            bool onGun = st.IsName(CurGunName);
            bool inTrans = _animator.IsInTransition(_upperLayer);
            float norm = st.normalizedTime;

            switch (_phase)
            {
                case GunPhase.Raising:
                    if (!inTrans && onGun && norm >= aimHold) { _animator.SetFloat(GunSpeedHash, 0f); _phase = GunPhase.Holding; }
                    break;
                case GunPhase.Holding:
                    if (fireEvent) BeginFire();
                    break;
                case GunPhase.Firing:
                    if (fireEvent) BeginFire();
                    else if (!inTrans && onGun && norm >= 0.99f) StartRaise(); // fire+reload done → re-raise to aim
                    break;
                default:
                    StartRaise();
                    break;
            }
        }
        else
        {
            _phase = GunPhase.Idle;
            if (slashEvent)
            {
                _slashing = true;
                _slashArmed = false;
                int hash = _slashVariant.Value == 0 ? SlashAHash : SlashBHash;
                _slashName = _slashVariant.Value == 0 ? "SlashA" : "SlashB";
                // Repeating the same variant needs Play() to restart (CrossFade onto the same state won't
                // rewind it); a different variant crossfades smoothly.
                if (_animator.GetCurrentAnimatorStateInfo(_upperLayer).IsName(_slashName))
                    _animator.Play(hash, _upperLayer, 0f);
                else
                    _animator.CrossFadeInFixedTime(hash, 0.1f, _upperLayer, 0f);
            }
            // If the held weapon went away mid-swing (e.g. a chest pickup auto-selected its slot), cancel the
            // slash so the UpperBody layer drops instead of playing an empty-handed swing over the carry pose.
            if (_slashing && (_inv == null || !_inv.HasWeaponEquipped)) _slashing = false;
            if (_slashing)
            {
                targetWeight = 1f;
                var st = _animator.GetCurrentAnimatorStateInfo(_upperLayer);
                bool onSlash = st.IsName(_slashName);
                // Wait until the restart has actually taken effect (norm rewound) before arming the
                // end-check — the trigger frame still reports the previous, stale (high) normalizedTime.
                if (!_slashArmed) { if (onSlash && st.normalizedTime < 0.5f) _slashArmed = true; }
                else if (onSlash && st.normalizedTime >= 0.95f) _slashing = false;
            }
        }

        _wasAiming = gunAiming;
        _weight = Mathf.MoveTowards(_weight, targetWeight, weightSharpness * dt);
        _animator.SetLayerWeight(_upperLayer, _weight);
    }

    // Alternate GunA/GunB so every entry is a genuine cross-state fade (and time actually restarts).
    private void CrossToGun(float normTime, float blend, float gunSpeed)
    {
        _gunSlot = 1 - _gunSlot;
        _animator.SetFloat(GunSpeedHash, gunSpeed);
        _animator.CrossFadeInFixedTime(CurGunHash, blend, _upperLayer, normTime);
    }

    private void StartRaise() { CrossToGun(0f, enterBlend, raiseSpeed); _phase = GunPhase.Raising; }
    private void BeginFire()
    {
        _animator.SetFloat(GunSpeedHash, fireSpeed);
        _phase = GunPhase.Firing;
        SpawnMuzzleFx();                                          // FX once, at the shot frame (~60), on every client
        if (IsOwner && _pc != null) _pc.ShakeCamera(cameraShake); // punch the local camera for hit feel
    }

    // Spawn the muzzle FX at the held gun's Muzzle, unparented and world-simulated so the puff stays in
    // place as the gun keeps moving. Cosmetic + local: every client runs this off the synced fire event.
    private void SpawnMuzzleFx()
    {
        if (muzzleFx == null || _inv == null) return;
        var muzzle = _inv.HeldMuzzle;
        if (muzzle == null) return;
        // The prefab is authored to fire along +Z, so just spawn it at the muzzle with the muzzle's rotation.
        // Unparented + world-space so the puff stays put at the shot spot instead of trailing the gun.
        var fx = Instantiate(muzzleFx, muzzle.position, muzzle.rotation * Quaternion.Euler(muzzleFxEuler));
        foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSizeMultiplier *= muzzleFxScale;
            main.startSpeedMultiplier *= muzzleFxScale;
        }
        Destroy(fx, muzzleFxLifetime);
    }
}
