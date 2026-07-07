using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-only fleet director. Keeps up to <see cref="maxShips"/> crewed enemy ships on the sea — spawned far
/// apart and away from Home, each wired to patrol the Mansion and hunt for a manned player ship. Re-checks
/// periodically and tops the fleet back up as ships are sunk. Counts the scene-placed enemy ship too.
/// </summary>
public class EnemyFleetSpawner : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject shipPrefab;   // SmallEnemyShip.prefab (registered network prefab)
    [SerializeField] private GameObject npcPrefab;    // Enemy_NPC.prefab (registered network prefab)

    [Header("Anchors")]
    [SerializeField] private Transform patrolCenter;  // Mansion — spawned ships roam around it
    [SerializeField] private Transform home;          // keep spawns away from the player base

    [Header("Fleet")]
    [Tooltip("Max live, movable enemy ships on the sea at once (wrecks that are sinking don't count).")]
    [SerializeField] private int maxShips = 2;
    [Tooltip("Seconds to wait before spawning a replacement once the fleet is short (a sunk ship is replaced within this).")]
    [SerializeField] private float respawnDelay = 30f;
    [SerializeField] private int crewPerShip = 5;
    [SerializeField] private float spawnRadius = 90f;      // ring radius around the patrol centre
    [SerializeField] private float minSeparation = 60f;    // keep spawned ships this far apart
    [SerializeField] private float minHomeDistance = 80f;  // and this far from Home
    [SerializeField] private float checkInterval = 6f;
    [SerializeField] private float shipWaterY = -2.8f;     // sea-level Y for a fresh hull

    private float _timer = 3f;        // small initial delay so the scene finishes spawning first
    private float _shortfallSince = -1f;

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || !nm.IsListening) return; // server-authoritative spawning only
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = checkInterval;
        TopUpFleet();
    }

    private void TopUpFleet()
    {
        if (shipPrefab == null || npcPrefab == null) return;

        // Count only LIVE, movable ships — wrecks that are sinking/despawning don't fill a slot.
        int alive = 0;
        foreach (var s in EnemyShipAI.Active) if (s != null && s.IsAlive) alive++;
        if (alive >= maxShips) { _shortfallSince = -1f; return; }

        // Short of the target → wait respawnDelay before bringing one in (so replacements aren't instant).
        if (_shortfallSince < 0f) _shortfallSince = Time.time;
        if (Time.time - _shortfallSince < respawnDelay) return;
        if (TryPickSpawn(out Vector3 pos)) { SpawnShip(pos); _shortfallSince = -1f; }
    }

    private bool TryPickSpawn(out Vector3 pos)
    {
        Vector3 c = patrolCenter != null ? patrolCenter.position : transform.position;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            float ang = attempt * 2.399963f; // golden angle (rad) → well-spread points
            float r = spawnRadius * (0.55f + 0.45f * ((attempt % 5) / 4f));
            Vector3 p = new Vector3(c.x + Mathf.Sin(ang) * r, shipWaterY, c.z + Mathf.Cos(ang) * r);
            if (home != null && (p - home.position).sqrMagnitude < minHomeDistance * minHomeDistance) continue;
            if (!IsWaterAt(p.x, p.z)) continue;  // don't spawn on an island
            bool tooClose = false;
            foreach (var s in EnemyShipAI.Active)
                if (s != null && (s.transform.position - p).sqrMagnitude < minSeparation * minSeparation) { tooClose = true; break; }
            if (tooClose) continue;
            pos = p; return true;
        }
        pos = default; return false;
    }

    // Deep-water test via the fleet's existing hull (mirrors ShipController's own navigability check).
    private bool IsWaterAt(float x, float z)
    {
        foreach (var s in EnemyShipAI.Active)
            if (s != null && s.Ship != null) return s.Ship.IsDeepWaterAt(x, z);
        return true; // no reference hull yet — allow (the scene ship exists first)
    }

    private void SpawnShip(Vector3 pos)
    {
        var shipGo = Instantiate(shipPrefab, pos, Quaternion.identity);
        shipGo.GetComponent<NetworkObject>().Spawn(true);
        var ai = shipGo.GetComponent<EnemyShipAI>();
        if (ai != null && patrolCenter != null) ai.SetPatrolCenter(patrolCenter);

        // Collect this hull's crew posts (the NPC_Points children).
        var posts = new List<Transform>();
        foreach (var t in shipGo.GetComponentsInChildren<Transform>(true))
            if (t.name.Contains("NPC_point")) posts.Add(t);

        for (int i = 0; i < crewPerShip; i++)
        {
            Transform post = i < posts.Count ? posts[i] : shipGo.transform;
            var npcGo = Instantiate(npcPrefab, post.position, post.rotation);
            npcGo.GetComponent<NetworkObject>().Spawn(true);
            var npc = npcGo.GetComponent<EnemyNpcController>();
            string pn = post.name.ToLower();
            bool cannonCrew = pn.Contains("cannon") && !pn.Contains("ammo");
            if (npc != null) npc.ConfigureServer(ai, post, 2 /* Warrior idle */, cannonCrew);
        }
    }
}
