using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Replicated ship state. XZ + heading + speed + steering + sails are authoritative; the vertical
/// bob and wave tilt are recomputed locally from XZ+time (like the player's swim bob) so they stay smooth
/// and phase-locked to the visible water without networking.</summary>
public struct ShipState : INetworkSerializable, IEquatable<ShipState>
{
    public float PosX, PosZ;
    public float Yaw;
    public float Speed;   // forward speed (m/s)
    public float Turn;    // steering -1..1 (drives wheel + bank)
    public bool SailsOpen;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref PosX); s.SerializeValue(ref PosZ); s.SerializeValue(ref Yaw);
        s.SerializeValue(ref Speed); s.SerializeValue(ref Turn); s.SerializeValue(ref SailsOpen);
    }
    public bool Equals(ShipState o) => PosX == o.PosX && PosZ == o.PosZ && Yaw == o.Yaw && Speed == o.Speed && Turn == o.Turn && SailsOpen == o.SailsOpen;
    public override bool Equals(object obj) => obj is ShipState o && Equals(o);
    public override int GetHashCode() => PosX.GetHashCode() ^ PosZ.GetHashCode();
}

/// <summary>
/// A drivable ship. Client-authoritative like the player: the driving client owns the ship and simulates it
/// every frame (heavy, sail-driven sailing + gentle banked turns), publishing an authoritative
/// <see cref="ShipState"/>; everyone else eases toward it. When no one drives, the server owns it and the
/// speed bleeds off to a stop. The ship rides the same Gerstner ocean as the player (bob + pitch/roll from
/// the wave slope, so it rocks fore/aft and side to side), the wheel turns with the steering, the sails
/// swap open/furled, and the wake effect toggles above a speed. All synced.
/// </summary>
// Run before PlayerController: the hull moves this frame BEFORE players pin to it and the camera reads its
// anchor, so a driver/passenger never lags a frame behind the rocking ship (no jitter).
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(NetworkObject))]
public class ShipController : NetworkBehaviour
{
    public const ulong NoDriver = ulong.MaxValue;

    /// <summary>All spawned ships (every client), for the interactor's "look at the wheel" check.</summary>
    public static readonly System.Collections.Generic.List<ShipController> Active = new System.Collections.Generic.List<ShipController>();

    /// <summary>Optional AI steering hook. When set AND the server owns the ship (no human driver), the pilot's
    /// output replaces the keyboard driver — the ship steers itself. Player ships leave this null (unchanged).</summary>
    public IShipPilot Pilot;

    /// <summary>True while an AI pilot is steering this hull — players may board its deck but not take its wheel.</summary>
    public bool AiControlled => Pilot != null;

    /// <summary>Wrecked: no thrust and no steering (set by <see cref="ShipHealth"/> when the hull is destroyed and
    /// sinking). The hull still floats/rocks; ShipHealth lowers it visually.</summary>
    public bool Disabled;

    [Header("Ship-vs-ship collision")]
    [Tooltip("Approximate hull radius (m). Two ships closer than the sum of their radii push apart and take a hit.")]
    [SerializeField] private float collisionRadius = 5f;
    [SerializeField] private int collisionDamage = 20;
    [Tooltip("Seconds between collision-damage ticks for a pair (stops continuous grinding damage).")]
    [SerializeField] private float collisionCooldown = 1.5f;
    private float _nextCollisionDamage;
    public float CollisionRadius => collisionRadius;

    [Header("Sailing (heavy feel)")]
    [SerializeField] private float maxSpeed = 9f;
    [SerializeField] private float accel = 1.1f;        // ramp up while sails are open
    [SerializeField] private float decel = 0.7f;        // slower bleed-off = heavy
    [SerializeField] private float turnRate = 13f;      // deg/sec at full steering + speed
    [SerializeField] private float turnSmooth = 1.4f;   // steering input eases in slowly (heavy wheel)
    [Tooltip("Fraction of the turn rate available with NO way on (turning on the spot). Rises to 1 at full speed.")]
    [SerializeField] private float stationaryTurn = 0.4f;
    [SerializeField] private float maxBank = 9f;        // deg the hull leans into a turn
    [SerializeField] private float bankSmooth = 1.3f;

    [Header("Waves (match the scene water / player)")]
    [SerializeField] private int waveCount = 4;
    [SerializeField] private float waveLength = 4.22f;
    [SerializeField] private float waveSteepness = 0.105f;
    [SerializeField] private float waveSpeed = 20f;
    [SerializeField] private float waveAmplitude = 0.431f;
    [SerializeField] private float tiltGain = 2.2f;     // amplify the wave pitch/roll for a visible rock
    [Tooltip("Low-pass on the wave rock (lower = calmer/heavier). Keeps a fast hull from rocking frantically as " +
             "it crosses wave crests, while still tracking the swell when slow or stopped.")]
    [SerializeField] private float tiltSmooth = 2.5f;
    [SerializeField] private float sampleSpanZ = 5f;    // fore/aft sample distance (pitch)
    [SerializeField] private float sampleSpanX = 5f;    // port/starboard sample distance (roll)

    [Header("Stay in the sea")]
    [SerializeField] private bool blockLand = true;      // stop the hull climbing onto land / shallows
    [SerializeField] private float waterLevel = -1.6f;   // sea surface height
    [SerializeField] private float minDepth = 1.5f;      // ground must sit this far below the surface to sail over
    [SerializeField] private bool clampToSea = false;    // optional rectangular fence (enable + set if the ship escapes)
    [SerializeField] private Vector2 seaCenter = Vector2.zero;
    [SerializeField] private Vector2 seaHalfExtents = new Vector2(300f, 300f);

    [Header("Play boundary (radial hard limit — nothing crosses it)")]
    [SerializeField] private bool clampToBoundary = false;
    [SerializeField] private Vector3 boundaryCenter;     // e.g. the WeaponShop
    [SerializeField] private float boundaryRadius = 180f;

    public bool HasBoundary => clampToBoundary;
    public Vector3 BoundaryCenter => boundaryCenter;
    public float BoundaryRadius => boundaryRadius;
    /// <summary>Public deep-water test (used by the AI patrol to pick navigable waypoints that dodge islands).</summary>
    public bool IsDeepWaterAt(float x, float z) => DeepWaterAt(x, z);

    [Header("Children")]
    [SerializeField] private GameObject openSails;      // Sails/OpenSails
    [SerializeField] private GameObject furledSails;    // Sails/SM_..._Sails_Up
    [SerializeField] private Transform shipWheel;       // Attachments/ShipWheel
    [SerializeField] private GameObject waterEffect;    // ShipWaterEffect
    [SerializeField] private GameObject indicatorEffect; // IndicatorEffect (the "interact" marker; hidden while driven)
    [SerializeField] private Transform playerPoint;     // PlayerPoint (driver stands here)
    [SerializeField] private Transform camTarget;       // a child of this ship: its LOCAL offset defines the camera anchor
    [SerializeField] private float wheelMaxAngle = 120f;
    [SerializeField] private float waterEffectSpeed = 2.5f;

    public Transform CamTarget => camTarget != null ? camTarget : transform;
    public Vector3 WheelAimPoint => shipWheel != null ? shipWheel.position + Vector3.up * 0.25f : transform.position;

    /// <summary>Nearest point on the hull's bounds to <paramref name="from"/> — the "edge" a swimmer looks at
    /// when climbing aboard.</summary>
    public Vector3 ClosestHullPoint(Vector3 from)
    {
        var col = GetComponent<Collider>();
        return col != null ? col.ClosestPointOnBounds(from) : transform.position;
    }

    /// <summary>Where a player climbing aboard from the water is dropped: on the deck, a bit forward of the wheel.</summary>
    public Vector3 DeckBoardPoint => playerPoint != null
        ? playerPoint.position + playerPoint.forward * 2f + Vector3.up * 0.4f
        : transform.position + Vector3.up * 4f;

    /// <summary>The world point the driving camera orbits: the ship's authored camera-target offset placed on
    /// the ship's FLAT pose (XZ + heading, at the rest waterline) — it deliberately ignores the wave bob/rock,
    /// so the camera holds steady like it does when the ship is stationary. Read by <see cref="PlayerCameraRig"/>.</summary>
    public Vector3 CamAnchor
    {
        get
        {
            Vector3 local = camTarget != null ? camTarget.localPosition : new Vector3(0f, 4f, 0f);
            return new Vector3(_current.PosX, _restY, _current.PosZ) + Quaternion.Euler(0f, _current.Yaw, 0f) * local;
        }
    }

    private readonly NetworkVariable<ulong> _driver = new(NoDriver,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<ShipState> _state = new(default,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private ShipState _current;
    private float _restY;
    private Quaternion _baseWheelRot;
    private float _bank, _wheelDisplay;
    private float _smoothPitch, _smoothRoll;

    public Transform PlayerPoint => playerPoint;
    public float Speed => _current.Speed;
    public float MaxSpeed => maxSpeed;
    public ulong Driver => _driver.Value;
    public bool IsDriven => _driver.Value != NoDriver;
    public bool DrivenByLocalPlayer => NetworkManager != null && _driver.Value == NetworkManager.LocalClientId;

    public override void OnNetworkSpawn()
    {
        _restY = transform.position.y;
        _baseWheelRot = shipWheel != null ? shipWheel.localRotation : Quaternion.identity;
        _current = new ShipState { PosX = transform.position.x, PosZ = transform.position.z, Yaw = transform.eulerAngles.y };
        if (IsOwner) _state.Value = _current;
        if (!Active.Contains(this)) Active.Add(this);
    }

    public override void OnNetworkDespawn() => Active.Remove(this);

    /// <summary>Server: hand the ship to a driver (transfers ownership so that client simulates it) or, with
    /// <see cref="NoDriver"/>, release it back to the server.</summary>
    public void SetDriverServer(ulong clientId)
    {
        if (!IsServer) return;
        _driver.Value = clientId;
        var no = NetworkObject;
        if (clientId == NoDriver)
        {
            if (no.OwnerClientId != NetworkManager.ServerClientId) no.ChangeOwnership(NetworkManager.ServerClientId);
        }
        else if (no.OwnerClientId != clientId) no.ChangeOwnership(clientId);
    }

    private void Update()
    {
        if (!IsSpawned) return; // don't touch the transform before OnNetworkSpawn captured the scene pose
        if (IsOwner) SimulateOwner(Time.deltaTime);
        else SmoothRemote(Time.deltaTime);
        if (IsServer) CheckCollisionDamage();
    }

    // Owner-side: nudge the next XZ out of any other ship's hull footprint so hulls don't interpenetrate.
    private void ResolveShipOverlap(ref float x, ref float z, ref float speed)
    {
        var ships = Active;
        for (int i = 0; i < ships.Count; i++)
        {
            var o = ships[i];
            if (o == null || o == this) continue;
            Vector3 op = o.transform.position;
            float dx = x - op.x, dz = z - op.z;
            float distSq = dx * dx + dz * dz;
            float minD = collisionRadius + o.CollisionRadius;
            if (distSq < minD * minD && distSq > 1e-4f)
            {
                float dist = Mathf.Sqrt(distSq);
                float push = minD - dist;                 // full separation from our side (the other pushes too)
                x += (dx / dist) * push;
                z += (dz / dist) * push;
                speed = Mathf.Min(speed, maxSpeed * 0.3f); // shed way on impact
            }
        }
    }

    // Server-side: two hulls in contact take collisionDamage each, once per cooldown. Processed once per pair
    // (by the lower-instance ship) so both hulls are hit exactly once.
    private void CheckCollisionDamage()
    {
        if (Time.time < _nextCollisionDamage) return;
        var ships = Active;
        for (int i = 0; i < ships.Count; i++)
        {
            var o = ships[i];
            if (o == null || o == this || GetInstanceID() >= o.GetInstanceID()) continue; // each pair once
            if (Time.time < o._nextCollisionDamage) continue;
            Vector3 mp = transform.position, op = o.transform.position;
            float dx = mp.x - op.x, dz = mp.z - op.z;
            float minD = collisionRadius + o.CollisionRadius;
            if (dx * dx + dz * dz >= minD * minD) continue;      // not touching
            // A hull only takes ram damage when a PLAYER is actually ABOARD one of the two ships — so a parked,
            // empty player ship (and any AI-vs-AI touch) is never damaged.
            if (!IsManned() && !o.IsManned())
            {
                _nextCollisionDamage = o._nextCollisionDamage = Time.time + collisionCooldown;
                continue;
            }
            var myHp = GetComponent<ShipHealth>();
            var oHp = o.GetComponent<ShipHealth>();
            if (myHp != null) myHp.ApplyDamage(collisionDamage, NetworkManager.ServerClientId, mp, DamageType.Cannon);
            if (oHp != null) oHp.ApplyDamage(collisionDamage, NetworkManager.ServerClientId, op, DamageType.Cannon);
            _nextCollisionDamage = o._nextCollisionDamage = Time.time + collisionCooldown;
        }
    }

    /// <summary>True if a live player is standing on this hull's deck (used to gate ram damage — an empty hull is
    /// never damaged by a bump).</summary>
    public bool IsManned()
    {
        var all = Health.All;
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h == null || h.Side != Team.Player || !h.IsAlive) continue;
            int n = Physics.RaycastNonAlloc(h.transform.position + Vector3.up, Vector3.down, _depthHits, 3.5f, ~0, QueryTriggerInteraction.Ignore);
            for (int j = 0; j < n; j++)
                if (_depthHits[j].collider.GetComponentInParent<ShipController>() == this) return true;
        }
        return false;
    }

    // The owner (driver's client, or the server when idle) advances + publishes the authoritative state.
    private void SimulateOwner(float dt)
    {
        var s = _current;
        float turnInput = 0f;
        bool driving = NetworkManager != null && _driver.Value == NetworkManager.LocalClientId;
        if (Disabled)
        {
            s.SailsOpen = false; // wrecked hull: no thrust, no steering
        }
        else if (driving)
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.wasPressedThisFrame) s.SailsOpen = true;   // raise sails -> accelerate
                if (kb.sKey.wasPressedThisFrame) s.SailsOpen = false;  // lower sails  -> coast to a stop
                turnInput = (kb.aKey.isPressed ? -1f : 0f) + (kb.dKey.isPressed ? 1f : 0f);
            }
        }
        else if (IsServer && Pilot != null)
        {
            // AI ship: the pilot steers instead of the (absent) human driver. Sailing physics/float/land-blocking below are unchanged.
            Pilot.GetShipInput(out turnInput, out bool aiSails);
            s.SailsOpen = aiSails;
        }
        else s.SailsOpen = false; // nobody at the wheel: sails down, drift to a halt

        float target = s.SailsOpen ? maxSpeed : 0f;
        s.Speed = Mathf.MoveTowards(s.Speed, target, (s.SailsOpen ? accel : decel) * dt);

        s.Turn = Mathf.Lerp(s.Turn, turnInput, 1f - Mathf.Exp(-turnSmooth * dt));
        float speedFactor = maxSpeed > 0.01f ? Mathf.Clamp01(s.Speed / maxSpeed) : 0f;
        s.Yaw += s.Turn * turnRate * dt * (stationaryTurn + (1f - stationaryTurn) * speedFactor);

        Vector3 fwd = Quaternion.Euler(0f, s.Yaw, 0f) * Vector3.forward;
        float nextX = s.PosX + fwd.x * s.Speed * dt;
        float nextZ = s.PosZ + fwd.z * s.Speed * dt;

        ResolveShipOverlap(ref nextX, ref nextZ, ref s.Speed);  // push out of any other hull we'd interpenetrate

        if (clampToSea)
        {
            nextX = Mathf.Clamp(nextX, seaCenter.x - seaHalfExtents.x, seaCenter.x + seaHalfExtents.x);
            nextZ = Mathf.Clamp(nextZ, seaCenter.y - seaHalfExtents.y, seaCenter.y + seaHalfExtents.y);
        }
        if (clampToBoundary)
        {
            float bx = nextX - boundaryCenter.x, bz = nextZ - boundaryCenter.z;
            float bd = Mathf.Sqrt(bx * bx + bz * bz);
            if (bd > boundaryRadius)                              // radial hard fence — can't sail past it
            {
                float f = boundaryRadius / bd;
                nextX = boundaryCenter.x + bx * f;
                nextZ = boundaryCenter.z + bz * f;
                s.Speed = Mathf.Min(s.Speed, maxSpeed * 0.2f);
            }
        }
        if (blockLand && !DeepWaterAt(nextX, nextZ))
        {
            nextX = s.PosX; nextZ = s.PosZ;                 // land / shallow ahead — don't climb ashore
            s.Speed = Mathf.Min(s.Speed, maxSpeed * 0.15f); // shed way so the hull noses to a stop
        }
        s.PosX = nextX;
        s.PosZ = nextZ;

        _current = s;
        _state.Value = s;
    }

    // Non-owners: ease toward the replicated state (a ship is slow, so simple smoothing reads fine).
    private void SmoothRemote(float dt)
    {
        var t = _state.Value;
        float k = 1f - Mathf.Exp(-10f * dt);
        _current.PosX = Mathf.Lerp(_current.PosX, t.PosX, k);
        _current.PosZ = Mathf.Lerp(_current.PosZ, t.PosZ, k);
        _current.Yaw = Mathf.LerpAngle(_current.Yaw, t.Yaw, k);
        _current.Speed = Mathf.Lerp(_current.Speed, t.Speed, k);
        _current.Turn = Mathf.Lerp(_current.Turn, t.Turn, k);
        _current.SailsOpen = t.SailsOpen;
    }

    private void LateUpdate()
    {
        if (!IsSpawned) return;
        float dt = Time.deltaTime;
        float timeX = Time.timeSinceLevelLoad / 20f;
        Vector3 flat = new Vector3(_current.PosX, 0f, _current.PosZ);

        Quaternion yawRot = Quaternion.Euler(0f, _current.Yaw, 0f);
        Vector3 fwd = yawRot * Vector3.forward, right = yawRot * Vector3.right;
        float cy = Wave(flat, timeX);
        float hF = Wave(flat + fwd * sampleSpanZ, timeX), hB = Wave(flat - fwd * sampleSpanZ, timeX);
        float hR = Wave(flat + right * sampleSpanX, timeX), hL = Wave(flat - right * sampleSpanX, timeX);
        float pitchTarget = Mathf.Atan2(hB - hF, 2f * sampleSpanZ) * Mathf.Rad2Deg * tiltGain;
        float rollTarget = Mathf.Atan2(hL - hR, 2f * sampleSpanX) * Mathf.Rad2Deg * tiltGain;
        // Low-pass the wave rock so a fast hull (crossing crests rapidly) doesn't oscillate frantically — it
        // stays calm/heavy at speed while still tracking the swell when slow or stopped.
        float kt = 1f - Mathf.Exp(-tiltSmooth * dt);
        _smoothPitch = Mathf.Lerp(_smoothPitch, pitchTarget, kt);
        _smoothRoll = Mathf.Lerp(_smoothRoll, rollTarget, kt);

        _bank = Mathf.Lerp(_bank, -_current.Turn * maxBank, 1f - Mathf.Exp(-bankSmooth * dt));

        transform.SetPositionAndRotation(
            new Vector3(_current.PosX, _restY + cy, _current.PosZ),
            yawRot * Quaternion.Euler(_smoothPitch, 0f, _smoothRoll + _bank));

        if (shipWheel != null)
        {
            _wheelDisplay = Mathf.Lerp(_wheelDisplay, _current.Turn * wheelMaxAngle, 1f - Mathf.Exp(-8f * dt));
            shipWheel.localRotation = _baseWheelRot * Quaternion.Euler(0f, 0f, _wheelDisplay);
        }

        if (openSails != null && openSails.activeSelf != _current.SailsOpen) openSails.SetActive(_current.SailsOpen);
        if (furledSails != null && furledSails.activeSelf == _current.SailsOpen) furledSails.SetActive(!_current.SailsOpen);
        if (waterEffect != null)
        {
            bool on = _current.Speed > waterEffectSpeed;
            if (waterEffect.activeSelf != on) waterEffect.SetActive(on);
        }

        // The interact indicator only shows while the ship is free to board — hide it once someone is at the
        // wheel. Driven state is replicated, so this stays in sync on every client.
        if (indicatorEffect != null)
        {
            bool show = !IsDriven;
            if (indicatorEffect.activeSelf != show) indicatorEffect.SetActive(show);
        }
    }

    // A raycast down the water column at (x,z): true if the seabed there is far enough below the surface to
    // float over. Islands/beaches/docks that rise near the surface read as land → false. Ships and players are
    // ignored. Nothing solid below (open sea) counts as water.
    private static readonly RaycastHit[] _depthHits = new RaycastHit[16];
    private bool DeepWaterAt(float x, float z)
    {
        Vector3 from = new Vector3(x, waterLevel + 60f, z);
        int n = Physics.RaycastNonAlloc(from, Vector3.down, _depthHits, 120f, ~0, QueryTriggerInteraction.Ignore);
        float highest = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var c = _depthHits[i].collider;
            if (c.GetComponentInParent<ShipController>() != null) continue;  // ignore any ship (incl. self)
            if (c.GetComponentInParent<PlayerController>() != null) continue; // ignore players on deck
            if (_depthHits[i].point.y > highest) highest = _depthHits[i].point.y;
        }
        if (highest == float.MinValue) return true;        // open water — nothing solid below
        return highest <= waterLevel - minDepth;           // deep enough to sail over
    }

    private float Wave(Vector3 p, float t) =>
        OceanWaves.SurfaceOffsetY(p, t, waveCount, waveLength, waveSteepness, waveSpeed, waveAmplitude);
}
