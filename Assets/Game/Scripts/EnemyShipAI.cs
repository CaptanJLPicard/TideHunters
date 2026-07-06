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

    [Header("Territory")]
    [Tooltip("The ship only engages a player ship within this radius of its home spot. Beyond it (or if the " +
             "player ship is empty — nobody aboard) it is ignored.")]
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

    [Header("Volley")]
    [Tooltip("Minimum seconds between ANY two crew gun shots. The crew shares this so they fire in a staggered " +
             "sequence (one at a time), not all together — giving the player room to dodge.")]
    [SerializeField] private float crewFireSpacing = 0.7f;

    [Header("Deck test")]
    [SerializeField] private float waterLevel = -1.6f;
    [Tooltip("A point above the waterline by this much, inside the hull footprint, counts as 'on our deck'.")]
    [SerializeField] private float deckClearance = 0.4f;

    [Header("Refs")]
    [SerializeField] private ShipCannon cannon;
    [Tooltip("The ship's reward chest (SmallShipChest). Becomes null when a player carries it off.")]
    [SerializeField] private Transform chest;

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

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; } // brain is server-only; clients just render the replicated hull
        _ship = GetComponent<ShipController>();
        _col = GetComponent<Collider>();
        if (cannon == null) cannon = GetComponent<ShipCannon>();
        _homeAnchor = transform.position; // centre of this ship's territory
        _ship.Pilot = this; // hand the wheel to the AI (ShipController keeps floating/sailing itself)
    }

    public override void OnNetworkDespawn()
    {
        if (_ship != null && ReferenceEquals(_ship.Pilot, this)) _ship.Pilot = null;
    }

    private void Update()
    {
        if (!IsServer || _ship == null) return;

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
        if (_playerShip == null) { _sailsOpen = false; sailsOpen = false; return; }

        Vector3 to = _playerShip.transform.position - _ship.transform.position; to.y = 0f;
        float dist = to.magnitude;
        if (dist > 0.01f)
        {
            float desiredYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float err = Mathf.DeltaAngle(_ship.transform.eulerAngles.y, desiredYaw);
            turn = Mathf.Clamp(err / turnBand, -1f, 1f);
        }

        if (_mode == ShipMode.Repel || _mode == ShipMode.CounterBoard)
        {
            _sailsOpen = false; // hold position for the boarding fight (still keep the bow on them)
        }
        else if (dist > preferredRange + rangeHysteresis) _sailsOpen = true;   // close the gap
        else if (dist < preferredRange - rangeHysteresis) _sailsOpen = false;  // ease off, sit and shoot
        // else: within the hysteresis band → keep the current sail state

        if (_crewAboard <= 0) _sailsOpen = false; // no crew aboard → the hull can't sail (holds until they return)
        sailsOpen = _sailsOpen;
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
    }

    /// <summary>Nearest player-controllable ship that is inside our territory AND has a live player aboard.
    /// An empty player ship, or one outside our waters, is ignored. Null if there is no valid target.</summary>
    private ShipController FindNearestPlayerShip()
    {
        ShipController best = null;
        float bestSqr = float.MaxValue;
        var ships = ShipController.Active;
        for (int i = 0; i < ships.Count; i++)
        {
            var s = ships[i];
            if (s == null || s == _ship || s.AiControlled) continue;
            if ((s.transform.position - _homeAnchor).sqrMagnitude > territoryRadius * territoryRadius) continue; // outside our waters
            if (!ShipManned(s)) continue;                                                                        // empty ship → ignore
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
