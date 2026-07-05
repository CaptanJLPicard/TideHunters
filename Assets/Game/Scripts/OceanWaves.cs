using UnityEngine;

/// <summary>
/// Standard multi-directional Gerstner ocean surface, re-implemented here as our own code (no
/// dependency on any third-party water script). The direction / wavelength / steepness / speed
/// tables and the per-wave math mirror the classic Gerstner formulation the scene's water renders
/// with, so a swimming character can ride the exact same surface it sees. Because the character and
/// the water evaluate the same function at the same XZ and time, their relative depth stays constant
/// across every wave — the character never sinks under or floats above the visible surface.
///
/// Visual-only: applied as a render offset, never part of the authoritative simulation, so it needs
/// no networking and cannot affect prediction/reconciliation.
/// </summary>
public static class OceanWaves
{
    private static readonly Vector2[] Dirs =
    {
        new Vector2(0.53f, 0.45f), new Vector2(-0.209f, 0.4f), new Vector2(-0.125f, 0.592f),
        new Vector2(0.482f, -0.876f), new Vector2(-0.729f, -0.694f), new Vector2(-0.248f, 0.968f),
        new Vector2(0.844f, -0.538f)
    };
    private static readonly float[] Lengths = { 3.56f, 2.85f, 2.10f, 1.30f, 1.10f, 1.2f };
    private static readonly float[] Steepnesses = { 1.0f, 1.8f, 1.6f, 1.25f, 0.5f };
    private static readonly float[] Speeds = { 0.62f, -0.8f, 0.45f, -0.75f, 0.88f, 0.70f, -0.56f, 0.35f, -0.71f };
    private const float Gravity = 9.8f;

    /// <summary>
    /// Vertical displacement of the water surface at a world XZ position.
    /// <paramref name="timeX"/> must be the renderer's time scaled the same way (seconds / 20) so the
    /// result stays phase-locked to the visible water.
    /// </summary>
    public static float SurfaceOffsetY(Vector3 worldPos, float timeX,
        int waveCount, float waveLength, float waveSteepness, float waveSpeed, float waveAmplitude)
    {
        float y = 0f;
        for (int i = 0; i < waveCount; i++)
        {
            float steepnessMul = Mathf.Lerp(1f, 0.1f, (1f / 32f) * i);
            float length = Lengths[i % Lengths.Length] * waveLength;
            float steepness = Steepnesses[i % Steepnesses.Length] * waveSteepness * steepnessMul;
            float speed = Speeds[i % Speeds.Length] * waveSpeed;
            Vector2 d = Dirs[i % Dirs.Length].normalized;

            float dispersion = 6.28318f / length;
            float c = Mathf.Sqrt(Gravity / dispersion) * speed;
            float f = dispersion * (d.x * worldPos.x + d.y * worldPos.z - c * timeX);
            float a = steepness / (dispersion * 1.5f);
            y += a * Mathf.Sin(f);
        }
        return y * waveAmplitude;
    }
}
