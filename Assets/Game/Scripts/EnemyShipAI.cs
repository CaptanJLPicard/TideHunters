using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Brain + pilot of an AI enemy ship. Runs ONLY on the server (the ship stays server-owned; its
/// <see cref="ShipController"/> replicates the hull to clients as usual). As <see cref="IShipPilot"/> it
/// steers the hull so the BOW (where the cannon sits) always points at the player ship, holding a cannon-
/// range gap, and fires the bow cannon when lined up. It also derives the global tactical <see cref="Mode"/>
/// and the set of boarders on its own deck, which the crew NPCs read to switch between idling at their
/// posts, repelling boarders, and counter-boarding. See the design spec for the full state machine.
/// </summary>
[RequireComponent(typeof(ShipController))]
public class EnemyShipAI : NetworkBehaviour, IShipPilot
{
    public enum ShipMode
    {
        Peaceful,     // player ship far — crew idles at their posts, cannon fires if it drifts into range
        Alarm,        // player ship came close — crew leaves idle and patrols the deck
        Repel,        // a player boarded us — crew keeps distance and shoots the boarders
        CounterBoard  // our deck cleared mid-fight — crew leaps to the player ship and attacks
    }

    [Header("Patrol (no target)")]
    [Tooltip("With no target, the ship roams around this object (the Mansion), hunting for a manned player ship.")]
    [SerializeField] private Transform patrolCenter;
    [Tooltip("Radius of the roaming circle around the patrol centre.")]
    [SerializeField] private float patrolRadius = 55f;
    [Tooltip("How close to a patrol waypoint counts as reached (then it swings to the next).")]
    [SerializeField] private float patrolArriveDist = 14f;
    [Tooltip("Acquire a manned player ship this close; the target is then held until it passes resetRange.")]
    [SerializeField] private float detectRange = 42f;

    [Header("Territory")]
    [Tooltip("Legacy fallback territory radius (used only if no patrol centre is set).")]
    [SerializeField] private float territoryRadius = 80f;

    [Header("Engagement ranges (m)")]
    [Tooltip("Player ship this close → the crew snaps to alarm.")]
    [SerializeField] private float alarmRange = 25f;
    [Tooltip("Gap the hull tries to hold for the cannon duel.")]
    [SerializeField] private float preferredRange = 30f;
    [Tooltip("Player ship beyond this (and never boarded) → crew stands down to Peaceful.")]
    [SerializeField] private float resetRange = 60f;
    [Tooltip("Counter-board only while the player ship is within this range; if it escapes past this, the crew " +
             "that hasn't reached it is recalled back to their own ship.")]
    [SerializeField] private float counterBoardRange = 45f;
    [SerializeField] private float rangeHysteresis = 4f;

    [Header("Steering")]
    [Tooltip("Heading error (deg) that maps to full rudder.")]
    [SerializeField] private float turnBand = 30f;
    [Tooltip("How far ahead (m) the ship looks for land / other hulls to steer around.")]
    [SerializeField] private float avoidLookAhead = 40f;
    [Tooltip("Treat another hull within this radius of a look-ahead point as blocking.")]
    [SerializeField] private float avoidShipRadius = 16f;

    [Header("Volley")]
    [Tooltip("Minimum seconds between ANY two crew gun shots. The crew shares this so they fire in a staggered " +
             "sequence (one at a time), not all together — giving the player room to dodge.")]
    [SerializeField] private float crewFireSpacing = 0.7f;

    [Header("Cannon-hit boarding")]
    [Tooltip("Once this many cannon hits land on the target player ship, the boarding party jumps across (if it's " +
             "close and nearly stopped): the 4 non-cannon crew swim over; the cannon NPC keeps working the gun.")]
    [SerializeField] private int boardAfterHits = 5;
    [SerializeField] private float boardTriggerRange = 22f;
    [Tooltip("Only board while the target ship is nearly stopped (metres it moved per 0.2s poll must be under this).")]
    [SerializeField] private float boardStationaryDelta = 0.6f;

    [Header("Deck test")]
    [SerializeField] private float waterLevel = -1.6f;
    [Tooltip("A point above the waterline by this much, inside the hull footprint, counts as 'on our deck'.")]
    [SerializeField] private float deckClearance = 0.4f;

    [Header("Refs")]
    [SerializeField] private ShipCannon cannon;
    [Tooltip("The ship's reward chest (SmallShipChest). Becomes null when a player carries it off.")]
    [SerializeField] private Transform chest;

    [Header("Death / despawn")]
    [Tooltip("Seconds after the hull is destroyed before the wreck + its drowned crew despawn (covers the sink).")]
    [SerializeField] private float despawnAfterDeath = 24f;

    private ShipHealth _health;
    private bool _dying;
    private float _deathTime;

    // --- server state ---
    private ShipController _ship;
    private Collider _col;
    private ShipController _playerShip;
    private ShipMode _mode = ShipMode.Peaceful;
    private bool _wasBoarded;
    private bool _sailsOpen;
    private readonly List<Health> _boarders = new List<Health>();
    private Health _chestCarrier;
    private int _crewAboard;
    private float _refreshTimer;
    private float _nextCrewFireTime;
    private Vector3 _homeAnchor;       // captured at spawn — the centre of this ship's territory
    private float _patrolAngle;        // swings around the patrol centre as it roams
    private Vector3 _patrolTarget;     // current roam waypoint (deep water, inside the boundary)
    private bool _hasPatrol;
    private float _patrolRepick;
    private float _avoidDir;           // committed evasive turn direction
    private float _avoidUntil;
    private int _cannonHits;           // hits landed on the current target player ship
    private bool _boardOrder;          // latched: 4 crew jump across to the player ship
    private ShipHealth _targetHp;
    private int _targetLastHp;
    private Vector3 _targetLastPos;

    /// <summary>Server: true once the target player ship has taken enough cannon hits (and is close + stopped) that
    /// the 4 non-cannon crew should swim over and board it. The cannon NPC keeps working the gun.</summary>
    public bool BoardOrder => _boardOrder;

    /// <summary>Server: a crew member asks permission to shoot this frame. Grants at most one shot per
    /// <see cref="crewFireSpacing"/> across the WHOLE crew, so the volley is staggered (one at a time).</summary>
    public bool TryReserveFireSlot()
    {
        if (Time.time < _nextCrewFireTime) return false;
        _nextCrewFireTime = Time.time + crewFireSpacing;
        return true;
    }

    // --- public read API (server-side; the crew NPCs query this) ---
    public ShipMode Mode => _mode;
    public ShipController Ship => _ship;
    public ShipController PlayerShip => _playerShip;
    public IReadOnlyList<Health> Boarders => _boarders;
    public Health ChestCarrier => _chestCarrier;
    public bool ChestAboard => chest != null;
    /// <summary>Live crew members currently standing on this ship's deck (0 → the hull won't sail).</summary>
    public int CrewAboard => _crewAboard;
    public Vector3 DeckCenter => _ship != null ? _ship.transform.position : transform.position;

    /// <summary>Every live AI enemy ship (server-side) — the fleet spawner reads this to keep the sea populated.</summary>
    public static readonly List<EnemyShipAI> Active = new List<EnemyShipAI>();

    /// <summary>Spawner hook: point this ship's patrol at a world anchor (the Mansion) after a runtime spawn, and
    /// seed its patrol angle to its spawn bearing so multiple ships spread around the ring instead of converging.</summary>
    public void SetPatrolCenter(Transform t)
    {
        patrolCenter = t;
        if (t != null) { Vector3 d = transform.position - t.position; _patrolAngle = Mathf.Atan2(d.x, d.z); }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; } // brain is server-only; clients just render the replicated hull
        _ship = GetComponent<ShipController>();
        _col = GetComponent<Collider>();
        if (cannon == null) cannon = GetComponent<ShipCannon>();
        _homeAnchor = transform.position; // centre of this ship's territory
        _ship.Pilot = this; // hand the wheel to the AI (ShipController keeps floating/sailing itself)
        if (patrolCenter != null) { Vector3 d = transform.position - patrolCenter.position; _patrolAngle = Mathf.Atan2(d.x, d.z); }
        _health = GetComponent<ShipHealth>();
        if (_health != null) _health.OnDeath += OnShipDead;
        if (!Active.Contains(this)) Active.Add(this);
    }

    public override void OnNetworkDespawn()
    {
        Active.Remove(this);
        if (_health != null) _health.OnDeath -= OnShipDead;
        if (_ship != null && ReferenceEquals(_ship.Pilot, this)) _ship.Pilot = null;
    }

    /// <summary>True once the hull is destroyed — it stops counting as a live, movable ship (the spawner ignores it).</summary>
    public bool IsAlive => _health == null || _health.IsAlive;

    // The hull was destroyed: the crew drown (killed now, riding the sinking deck down), and after the wreck has
    // gone under, the ship + crew despawn so the fleet spawner can bring in a replacement.
    private void OnShipDead(ulong attacker)
    {
        if (!IsServer || _dying) return;
        _dying = true;
        _deathTime = Time.time;
        var crew = EnemyNpcController.All;
        for (int i = 0; i < crew.Count; i++)
        {
            var n = crew[i];
            if (n == null || n.CommanderShip != this) continue;
            var h = n.GetComponent<Health>();
            if (h != null && h.IsAlive) h.ApplyDamage(9999, attacker, n.transform.position, DamageType.Generic); // drown
        }
    }

    private void DespawnFleet()
    {
        var crew = EnemyNpcController.All;
        for (int i = crew.Count - 1; i >= 0; i--)
        {
            var n = crew[i];
            if (n != null && n.CommanderShip == this && n.NetworkObject != null && n.NetworkObject.IsSpawned)
                n.NetworkObject.Despawn(true);
        }
        if (NetworkObject != null && NetworkObject.IsSpawned) NetworkObject.Despawn(true);
    }

    private void Update()
    {
        if (!IsServer || _ship == null) return;

        if (_dying) // wrecked: no tactics or cannon; just wait out the sink, then remove ship + drowned crew
        {
            if (Time.time >= _deathTime + despawnAfterDeath) DespawnFleet();
            return;
        }

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f) { _refreshTimer = 0.2f; RefreshTactical(); }

        // Fire the bow cannon at the player ship whenever it drifts into range and the bow is aimed (the
        // cannon self-gates range / alignment / reload). Held below min-range during a boarding scrum.
        if (_playerShip != null && cannon != null)
            cannon.ServerTryFireAt(_playerShip.transform.position + Vector3.up * 0.8f, NetworkManager.ServerClientId);
    }

    // IShipPilot: steer the bow at the player ship and open/furl the sails to hold the cannon range.
    public void GetShipInput(out float turn, out bool sailsOpen)
    {
        turn = 0f;
        if (_crewAboard <= 0) { _sailsOpen = false; sailsOpen = false; return; } // no crew aboard → the hull can't sail

        // No target → roam the boundary, hunting for a manned player ship (dodging islands + other hulls).
        if (_playerShip == null)
        {
            turn = AvoidSteer(PatrolWaypoint() - _ship.transform.position);
            _sailsOpen = true;
            sailsOpen = true;
            return;
        }

        Vector3 to = _playerShip.transform.position - _ship.transform.position; to.y = 0f;
        float dist = to.magnitude;
        turn = AvoidSteer(_playerShip.transform.position - _ship.transform.position); // aim the bow at the target, weaving around obstacles

        if (_mode == ShipMode.Repel || _mode == ShipMode.CounterBoard)
        {
            _sailsOpen = false; // hold position for the boarding fight (still keep the bow on them)
        }
        else if (dist > preferredRange + rangeHysteresis) _sailsOpen = true;   // close the gap
        else if (dist < preferredRange - rangeHysteresis) _sailsOpen = false;  // ease off, sit and shoot
        // else: within the hysteresis band → keep the current sail state

        sailsOpen = _sailsOpen;
    }

    // Roam to deep-water waypoints INSIDE the play boundary, dodging islands. Picks a new one on arrival, or if it
    // can't reach the current one in time (blocked by land → re-route).
    private Vector3 PatrolWaypoint()
    {
        bool reached = _hasPatrol && Flat(_ship.transform.position - _patrolTarget).sqrMagnitude < patrolArriveDist * patrolArriveDist;
        if (!_hasPatrol || reached || Time.time > _patrolRepick)
        {
            _patrolTarget = PickPatrolPoint();
            _hasPatrol = true;
            _patrolRepick = Time.time + 15f;
        }
        return _patrolTarget;
    }

    private Vector3 PickPatrolPoint()
    {
        // Stay inside the radial boundary (WeaponShop, 180) if set; otherwise fall back to the patrol circle.
        Vector3 center = _ship.HasBoundary ? _ship.BoundaryCenter : (patrolCenter != null ? patrolCenter.position : _homeAnchor);
        float maxR = _ship.HasBoundary ? _ship.BoundaryRadius * 0.9f : patrolRadius;
        float y = _ship.transform.position.y;
        for (int i = 0; i < 20; i++)
        {
            _patrolAngle += 2.399963f; // golden angle → well-spread candidates
            float r = maxR * (0.35f + 0.6f * Mathf.Abs(Mathf.Sin(_patrolAngle * 1.7f)));
            Vector3 p = new Vector3(center.x + Mathf.Sin(_patrolAngle) * r, y, center.z + Mathf.Cos(_patrolAngle) * r);
            if (_ship.IsDeepWaterAt(p.x, p.z)) return p; // navigable open water (no island there)
        }
        return new Vector3(center.x, y, center.z);
    }

    private void SteerTowards(Vector3 worldTarget, out float turn)
    {
        turn = 0f;
        Vector3 to = worldTarget - _ship.transform.position; to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return;
        float desiredYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        float err = Mathf.DeltaAngle(_ship.transform.eulerAngles.y, desiredYaw);
        turn = Mathf.Clamp(err / turnBand, -1f, 1f);
    }

    // Context steering: scan candidate headings starting at the DESIRED direction and fanning outward, and steer
    // toward the nearest one whose look-ahead is clear of land + other hulls. So the ship never turns INTO an
    // obstacle on the side it wanted to go — it detours around it from far enough to make the turn. Returns rudder.
    private float AvoidSteer(Vector3 desiredDir)
    {
        Vector3 pos = _ship.transform.position;
        Vector3 fwd = _ship.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) return 0f; else fwd.Normalize();
        desiredDir.y = 0f;
        if (desiredDir.sqrMagnitude < 1e-4f) desiredDir = fwd; else desiredDir.Normalize();
        float desiredYaw = Mathf.Atan2(desiredDir.x, desiredDir.z) * Mathf.Rad2Deg;

        // Hold a committed hard evasive turn until straight ahead clears (prevents shoreline oscillation).
        if (Time.time < _avoidUntil)
        {
            if (FeelerClear(pos, fwd, avoidLookAhead)) _avoidUntil = 0f;
            else return _avoidDir;
        }

        // Fan out from the desired heading (0, ±22°, ±44°, …) and take the first clear one.
        for (int step = 0; step <= 6; step++)
        {
            int sides = step == 0 ? 1 : 2;
            for (int s = 0; s < sides; s++)
            {
                float dev = step * 22f * (s == 0 ? 1f : -1f);
                float yaw = desiredYaw + dev;
                Vector3 dir = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
                if (!FeelerClear(pos, dir, avoidLookAhead)) continue;
                float err = Mathf.DeltaAngle(_ship.transform.eulerAngles.y, yaw);
                float turn = Mathf.Clamp(err / turnBand, -1f, 1f);
                if (step >= 3) { _avoidDir = turn >= 0f ? 1f : -1f; _avoidUntil = Time.time + 1.2f; } // sharp detour → commit
                return turn;
            }
        }
        // Boxed in on every heading → commit a hard turn out and hope the next frame opens up.
        _avoidDir = _avoidDir == 0f ? 1f : _avoidDir;
        _avoidUntil = Time.time + 1.2f;
        return _avoidDir;
    }

    // A feeler is clear if every sample along it is open deep water with no other hull sitting on it.
    private bool FeelerClear(Vector3 pos, Vector3 dir, float dist)
    {
        for (float d = 9f; d <= dist; d += 7f)
        {
            Vector3 p = pos + dir * d;
            if (!_ship.IsDeepWaterAt(p.x, p.z)) return false;                 // island / shallow ahead
            var ships = ShipController.Active;
            for (int i = 0; i < ships.Count; i++)
            {
                var o = ships[i];
                if (o == null || o == _ship) continue;
                if ((o.transform.position - p).sqrMagnitude < avoidShipRadius * avoidShipRadius) return false; // another hull ahead
            }
        }
        return true;
    }

    private void RefreshTactical()
    {
        _playerShip = FindNearestPlayerShip();

        _boarders.Clear();
        _chestCarrier = null;
        var all = Health.All;
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h == null || h.Side != Team.Player || !h.IsAlive) continue;
            if (!IsOverDeck(h.transform.position)) continue;
            _boarders.Add(h);
            if (_chestCarrier == null)
            {
                var inv = h.GetComponent<PlayerInventory>();
                if (inv != null && inv.IsCarryingChest) _chestCarrier = h;
            }
        }

        // Count our own live crew standing on the deck — the hull won't sail without a crew, and once the deck
        // clears the ship holds until they climb back aboard.
        _crewAboard = 0;
        var crew = EnemyNpcController.All;
        for (int i = 0; i < crew.Count; i++)
        {
            var n = crew[i];
            if (n != null && n.IsAlive && IsOverDeck(n.transform.position)) _crewAboard++;
        }

        float shipDist = _playerShip != null
            ? Vector3.Distance(Flat(_ship.transform.position), Flat(_playerShip.transform.position))
            : float.MaxValue;

        // Boarders on OUR deck take priority (their own ship reads as empty while they're aboard us, so this must
        // be checked before the "no target ship" case).
        if (_boarders.Count > 0) { _mode = ShipMode.Repel; _wasBoarded = true; }
        else if (_playerShip == null || shipDist > resetRange) { _mode = ShipMode.Peaceful; _wasBoarded = false; }
        else if (_wasBoarded && shipDist < counterBoardRange) { _mode = ShipMode.CounterBoard; } // still in reach → invade
        else if (shipDist < alarmRange) { _mode = ShipMode.Alarm; }
        else { _mode = ShipMode.Peaceful; }                                                     // escaped/quiet → crew recalls

        UpdateBoardOrder();
    }

    // Count cannon hits on the target player ship and, once past the threshold while it's close + nearly stopped,
    // latch the boarding order (the crew's Think reads BoardOrder). Cleared when the target escapes.
    private void UpdateBoardOrder()
    {
        if (_playerShip == null) { _boardOrder = false; _cannonHits = 0; _targetHp = null; return; }

        var ph = _playerShip.GetComponent<ShipHealth>();
        if (ph != _targetHp)                                   // new target → reset the tally
        {
            _targetHp = ph;
            _targetLastHp = ph != null ? ph.Current : 0;
            _targetLastPos = _playerShip.transform.position;
            _cannonHits = 0;
        }
        else if (ph != null)
        {
            if (ph.Current < _targetLastHp) _cannonHits++;     // a hit landed since the last poll
            _targetLastHp = ph.Current;
        }

        Vector3 pos = _playerShip.transform.position;
        bool stationary = Flat(pos - _targetLastPos).magnitude < boardStationaryDelta;
        _targetLastPos = pos;
        float d = Vector3.Distance(Flat(_ship.transform.position), Flat(pos));

        if (!_boardOrder && _cannonHits >= boardAfterHits && d < boardTriggerRange && stationary) _boardOrder = true;
        if (d > resetRange) { _boardOrder = false; _cannonHits = 0; } // target got away → stand down
    }

    /// <summary>The manned player ship we should be hunting. While roaming we acquire the nearest one within
    /// <see cref="detectRange"/>; once locked we keep it until it goes empty or slips past <see cref="resetRange"/>
    /// (hysteresis, so it isn't dropped the instant it edges out of detect range). Empty ships are always ignored.</summary>
    private ShipController FindNearestPlayerShip()
    {
        // Keep the current target while it stays manned and within the reset leash.
        if (_playerShip != null && ShipManned(_playerShip) &&
            (_playerShip.transform.position - _ship.transform.position).sqrMagnitude <= resetRange * resetRange)
            return _playerShip;

        ShipController best = null;
        float bestSqr = detectRange * detectRange;
        var ships = ShipController.Active;
        for (int i = 0; i < ships.Count; i++)
        {
            var s = ships[i];
            if (s == null || s == _ship || s.AiControlled) continue;
            if (!ShipManned(s)) continue;                          // empty ship → ignore
            float d = (s.transform.position - _ship.transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = s; }
        }
        return best;
    }

    // True if at least one live player is standing on that ship's deck (so an empty ship is never chased or shot).
    private bool ShipManned(ShipController s)
    {
        var all = Health.All;
        for (int i = 0; i < all.Count; i++)
        {
            var h = all[i];
            if (h == null || h.Side != Team.Player || !h.IsAlive) continue;
            var hits = Physics.RaycastAll(h.transform.position + Vector3.up * 1.0f, Vector3.down, 3.0f, ~0, QueryTriggerInteraction.Ignore);
            for (int j = 0; j < hits.Length; j++)
                if (hits[j].collider.GetComponentInParent<ShipController>() == s) return true;
        }
        return false;
    }

    /// <summary>True if a world point is standing on THIS ship's deck — tested by a short downward raycast onto
    /// the hull (wave-independent and robust, unlike an absolute-height check: the low deck rides barely above
    /// the sea, so a fixed Y threshold can't tell deck from water while the ship bobs).</summary>
    public bool IsOverDeck(Vector3 p)
    {
        if (_ship == null) return false;
        var hits = Physics.RaycastAll(p + Vector3.up * 1.0f, Vector3.down, 3.0f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
            if (hits[i].collider.GetComponentInParent<ShipController>() == _ship) return true;
        return false;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
