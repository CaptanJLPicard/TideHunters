using UnityEngine;

/// <summary>
/// Keeps a <see cref="CharacterController"/> glued to the deck of a moving ship WITHOUT parenting — the exact
/// moving-platform carry the player uses (see PlayerController.CarryOnShip/DetectGroundShip). Each frame it
/// finds the ship under the feet with a short downward raycast, then re-projects the character's world
/// position through the hull's previous→current transform and nudges the CharacterController by that delta,
/// so translation + heading turn + wave pitch/roll/bob are all carried while the body stays upright.
///
/// Usage: construct once with the NPC's CharacterController; call <see cref="DetectAndCarry"/> every
/// LateUpdate AFTER the ship moved (ShipController runs at execution order -100, before LateUpdate). Call
/// <see cref="SetPlatform"/> right after teleporting the character aboard so the first carry frame has a base.
/// </summary>
public class DeckRider
{
    private readonly CharacterController _cc;
    private readonly Transform _t;
    private ShipController _platform;
    private Matrix4x4 _platformLastW2L;

    /// <summary>The ship currently under the character's feet (null while off any deck / in the water).</summary>
    public ShipController Platform => _platform;

    public DeckRider(CharacterController cc)
    {
        _cc = cc;
        _t = cc != null ? cc.transform : null;
    }

    /// <summary>Force the tracked deck (e.g. immediately after teleporting aboard), so the next carry frame
    /// measures motion from here instead of snapping.</summary>
    public void SetPlatform(ShipController ship)
    {
        _platform = ship;
        if (ship != null) _platformLastW2L = ship.transform.worldToLocalMatrix;
    }

    /// <summary>Detect the deck under the feet and move the CharacterController by the deck's motion this
    /// frame. Returns the deck ship (or null if standing on land / floating free).</summary>
    public ShipController DetectAndCarry()
    {
        ShipController ground = DetectGroundShip();
        if (ground != null && ground == _platform && _cc != null && _cc.enabled)
        {
            Vector3 local = _platformLastW2L.MultiplyPoint3x4(_t.position);
            Vector3 carried = ground.transform.localToWorldMatrix.MultiplyPoint3x4(local);
            Vector3 delta = carried - _t.position;
            if (delta.sqrMagnitude > 1e-8f) _cc.Move(delta);
        }
        _platform = ground;
        if (ground != null) _platformLastW2L = ground.transform.worldToLocalMatrix;
        return ground;
    }

    // The ship directly under the feet (or null). Skips the character's own colliders.
    private ShipController DetectGroundShip()
    {
        if (_cc == null) return null;
        Bounds b = _cc.bounds;
        Vector3 origin = new Vector3(b.center.x, b.min.y + 0.3f, b.center.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, 0.8f, ~0, QueryTriggerInteraction.Ignore);
        ShipController best = null;
        float bestDist = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider.transform == _t || h.collider.transform.IsChildOf(_t)) continue;
            var s = h.collider.GetComponentInParent<ShipController>();
            if (s != null && h.distance < bestDist) { bestDist = h.distance; best = s; }
        }
        return best;
    }
}
