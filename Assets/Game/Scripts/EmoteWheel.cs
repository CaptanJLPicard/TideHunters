using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Radial emote menu (hold Z). Lives under the scene Canvas's GamePanel next to the hotbar and shares its
/// pirate wood/gold styling. Holding Z opens the wheel and frees the cursor; the segment the cursor points
/// at (by angle from the centre) highlights gold; releasing Z plays that emote via
/// <see cref="PlayerController.TriggerEmote"/>. Camera look is frozen while it is open.
/// </summary>
public class EmoteWheel : MonoBehaviour
{
    /// <summary>True while the wheel is open — the player camera stops turning so the cursor can aim it.</summary>
    public static bool IsOpen { get; private set; }

    private static readonly Color Wood = new Color(0.36f, 0.22f, 0.11f, 0.92f);
    private static readonly Color Gold = new Color(0.86f, 0.66f, 0.26f, 0.98f);

    [Tooltip("Cursor distance (px) from the centre below which nothing is selected.")]
    [SerializeField] private float deadzone = 45f;

    private GameObject _wheel;
    private Image[] _segBg;
    private int _selected = -1;
    private bool _open;
    private PlayerController _player;

    private void Awake()
    {
        var w = transform.Find("Wheel");
        if (w == null) return;
        _wheel = w.gameObject;

        var bgs = new List<Image>();
        for (int i = 0; ; i++)
        {
            var seg = w.Find("Segment" + i);
            if (seg == null) break;
            var inner = seg.Find("Inner");
            bgs.Add(inner != null ? inner.GetComponent<Image>() : seg.GetComponent<Image>());
        }
        _segBg = bgs.ToArray();
        _wheel.SetActive(false);
    }

    private void Update()
    {
        if (_wheel == null) return;
        var kb = Keyboard.current;
        bool z = kb != null && kb.zKey.isPressed;
        if (z && !_open) Open();
        else if (!z && _open) Close();
        if (_open) UpdateSelection();
    }

    private void Open()
    {
        _open = true;
        IsOpen = true;
        _selected = -1;
        _wheel.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Highlight(-1);
    }

    private void Close()
    {
        if (_selected >= 0) LocalPlayer()?.TriggerEmote(_selected + 1);
        _open = false;
        IsOpen = false;
        _wheel.SetActive(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        _selected = -1;
    }

    private void UpdateSelection()
    {
        if (_segBg == null || _segBg.Length == 0 || Mouse.current == null) return;
        Vector2 center = new Vector2(Screen.width, Screen.height) * 0.5f; // wheel is screen-centred
        Vector2 dir = Mouse.current.position.ReadValue() - center;

        int sel = -1;
        if (dir.magnitude >= deadzone)
        {
            float angle = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg; // 0 = up, clockwise
            if (angle < 0f) angle += 360f;
            sel = Mathf.RoundToInt(angle / (360f / _segBg.Length)) % _segBg.Length;
        }
        if (sel != _selected) { _selected = sel; Highlight(sel); }
    }

    private void Highlight(int sel)
    {
        for (int i = 0; i < _segBg.Length; i++)
            if (_segBg[i] != null) _segBg[i].color = i == sel ? Gold : Wood;
    }

    private PlayerController LocalPlayer()
    {
        if (_player != null) return _player;
        foreach (var p in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            if (p.IsOwner) { _player = p; break; }
        return _player;
    }
}
