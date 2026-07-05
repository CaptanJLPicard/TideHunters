using UnityEngine;

/// <summary>
/// Keeps the camera from dipping below the ocean surface (the water is a shader plane with no
/// collider, so Cinemachine's physics-based obstacle avoidance cannot catch it). Runs after the
/// CinemachineBrain has positioned the camera and lifts it back to just above the surface.
/// Attach to the Main Camera (same GameObject as the CinemachineBrain).
/// </summary>
[DefaultExecutionOrder(10000)]
public class CameraWaterClamp : MonoBehaviour
{
    [Tooltip("World Y of the ocean surface.")]
    [SerializeField] private float waterSurfaceY = -1.58f;
    [Tooltip("How far above the surface the camera is kept, so the near plane never goes under.")]
    [SerializeField] private float margin = 0.6f;

    private void LateUpdate()
    {
        float minY = waterSurfaceY + margin;
        Vector3 p = transform.position;
        if (p.y < minY)
        {
            p.y = minY;
            transform.position = p;
        }
    }
}
