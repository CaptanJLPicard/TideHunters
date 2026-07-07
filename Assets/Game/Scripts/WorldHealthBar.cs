using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A small billboarded health bar that floats above an enemy's head. Reads the parent <see cref="Health"/>, faces
/// the local player's camera, and shows the replicated health fraction in the wood/gold inventory-slot style.
/// Authored once as a prefab (a world-space Canvas) and parented above each Enemy_NPC's head.
/// </summary>
[DisallowMultipleComponent]
public class WorldHealthBar : MonoBehaviour
{
    [SerializeField] private Image fill;
    [SerializeField] private Health health;          // auto-found in a parent if left null
    [SerializeField] private Color low = new Color(0.85f, 0.20f, 0.15f);
    [SerializeField] private Color high = new Color(0.86f, 0.66f, 0.26f);

    private Transform _cam;
    private CanvasGroup _group;

    private void Awake()
    {
        if (health == null) health = GetComponentInParent<Health>();
        _group = GetComponent<CanvasGroup>();
    }

    private void LateUpdate()
    {
        if (_cam == null) { var c = Camera.main; if (c != null) _cam = c.transform; }
        if (_cam != null) transform.rotation = _cam.rotation;   // billboard: always face the viewing camera

        if (health == null) return;
        float f = health.Fraction;
        if (fill != null) { fill.fillAmount = f; fill.color = Color.Lerp(low, high, f); }
        if (_group != null) _group.alpha = health.IsAlive ? 1f : 0f; // hide once dead
    }
}
