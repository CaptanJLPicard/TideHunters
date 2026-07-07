using UnityEngine;

/// <summary>
/// Marks the Home delivery spot. A player carrying a stolen chest who comes within <see cref="radius"/> of it can
/// hand the chest in for gold (see PlayerInteractor). Plain scene singleton — the interactor queries it.
/// </summary>
public class HomeZone : MonoBehaviour
{
    public static HomeZone Instance { get; private set; }

    [SerializeField] private float radius = 7f;

    public float Radius => radius;
    public Vector3 Position => transform.position;

    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }
}
