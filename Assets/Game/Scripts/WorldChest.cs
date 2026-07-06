using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A chest resting in the world — placed in a scene, spawned inside ships / on the island, or dropped by a
/// player with G. It is a server-spawned/despawned <see cref="NetworkObject"/> so every client sees it
/// appear and disappear in sync. Its <see cref="ChestId"/> is intrinsic to the prefab (each chest type has
/// its own prefab) — so it needs no replicated state at all. Any player can look at it and press E to carry
/// it (handled by <see cref="PlayerInteractor"/>): the server validates space, despawns the chest, and
/// grants it to the player's inventory as a carried item.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WorldChest : NetworkBehaviour
{
    /// <summary>All world chests currently spawned (every client), for proximity/look-at pickup.</summary>
    public static readonly List<WorldChest> Active = new List<WorldChest>();

    [Tooltip("Which chest this is. Intrinsic to the prefab — each chest type has its own prefab, so this " +
             "never needs to be networked.")]
    [SerializeField] private ChestId chestType = ChestId.None;

    public ChestId Chest => chestType;

    public override void OnNetworkSpawn() { if (!Active.Contains(this)) Active.Add(this); }
    public override void OnNetworkDespawn() => Active.Remove(this);

    /// <summary>Better look-at target than the pivot: the visual centre of the chest mesh.</summary>
    public Vector3 AimPoint
    {
        get
        {
            var rend = GetComponentInChildren<Renderer>();
            return rend != null ? rend.bounds.center : transform.position;
        }
    }
}
