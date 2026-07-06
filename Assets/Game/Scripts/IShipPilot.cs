/// <summary>
/// AI steering hook for <see cref="ShipController"/>. When the SERVER owns a ship (no human at the wheel)
/// and a pilot is assigned, the pilot's output replaces the (absent) keyboard driver — the enemy ship
/// steers itself. The ship still runs its own sailing physics, wave float and land-blocking unchanged;
/// the pilot only decides how hard to turn and whether the sails are drawing.
/// </summary>
public interface IShipPilot
{
    /// <summary>Called each simulated frame on the server-owned ship.</summary>
    /// <param name="turn">Steering, -1 (hard port) .. +1 (hard starboard).</param>
    /// <param name="sailsOpen">True to draw the sails (accelerate toward max speed); false to furl and coast to a stop.</param>
    void GetShipInput(out float turn, out bool sailsOpen);
}
