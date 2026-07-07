using UnityEngine;

/// <summary>
/// Impact feedback: whenever this character's <see cref="Health"/> drops, the upper spine jerks back a little and
/// eases back to pose — retriggered on every hit. Purely visual and driven off the replicated hit points, so it
/// plays on every peer. Runs in LateUpdate at a high execution order so it layers on TOP of the animator + aim.
/// </summary>
[DefaultExecutionOrder(15000)]
[DisallowMultipleComponent]
public class HitReaction : MonoBehaviour
{
    [SerializeField] private Health health;              // auto-found in a parent if null
    [SerializeField] private Transform spine;           // upper spine / chest bone; auto-found if null
    [Tooltip("Peak backward tilt (deg) on a hit.")]
    [SerializeField] private float recoilAngle = 18f;
    [Tooltip("How fast the jerk eases back out (1/sec).")]
    [SerializeField] private float recoverSpeed = 4.5f;
    [Tooltip("Local axis to pitch the spine around (rig-dependent).")]
    [SerializeField] private Vector3 axis = Vector3.right;

    private int _lastHp = int.MinValue;
    private float _recoil;

    private void Awake()
    {
        if (health == null) health = GetComponentInParent<Health>();
        if (spine == null) spine = FindSpine(transform);
    }

    private static Transform FindSpine(Transform root)
    {
        Transform best = null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.ToLowerInvariant().Contains("spine")) best = t; // last match = the upper spine / chest
        return best;
    }

    private void LateUpdate()
    {
        if (health != null)
        {
            int hp = health.Current;
            if (_lastHp != int.MinValue && hp < _lastHp && health.IsAlive) _recoil = 1f; // fresh hit → jerk
            _lastHp = hp;
        }
        if (_recoil <= 0.001f || spine == null) return;
        spine.localRotation *= Quaternion.AngleAxis(-recoilAngle * _recoil, axis.normalized);
        _recoil = Mathf.MoveTowards(_recoil, 0f, recoverSpeed * Time.deltaTime);
    }
}
