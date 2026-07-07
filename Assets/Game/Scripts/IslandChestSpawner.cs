using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-spawns the island's fixed chests when the host starts. In-scene-placed chest NetworkObjects desync for
/// remote clients (a carry/despawn on a client leaves a non-interactable ghost behind); spawning them from the
/// server instead means every client sees the SAME single chest at each spot, and a carry/despawn syncs everywhere.
/// The placements are captured from where the chests were authored in the scene.
/// </summary>
public class IslandChestSpawner : MonoBehaviour
{
    [System.Serializable]
    public struct Placement { public ChestId type; public Vector3 pos; public Vector3 euler; }

    [SerializeField] private Placement[] chests;
    private bool _done;

    // Subscribe in Start (not OnEnable) so NetworkManager.Singleton is guaranteed set (Awake order isn't); also
    // fire immediately if the server is already up.
    private void Start()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        nm.OnServerStarted += SpawnChests;
        if (nm.IsServer) SpawnChests();
    }
    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null) NetworkManager.Singleton.OnServerStarted -= SpawnChests;
    }

    private void SpawnChests()
    {
        var nm = NetworkManager.Singleton;
        if (_done || nm == null || !nm.IsServer) return;
        _done = true;
        var db = ChestDatabase.Instance;
        if (db == null || chests == null) return;
        foreach (var c in chests)
        {
            var def = db.Get(c.type);
            if (def == null || def.worldPrefab == null) continue;
            var go = Instantiate(def.worldPrefab, c.pos, Quaternion.Euler(c.euler));
            go.GetComponent<NetworkObject>().Spawn(true);
        }
    }
}
