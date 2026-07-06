using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the in-scene HUD (built under the Canvas's GamePanel, not procedurally): a pirate-styled
/// 4-slot hotbar (keys 1-4, selected slot highlighted, weapon icon) and a single world prompt used
/// for every "Press E to …" message. Lives on the GamePanel and references its pre-built children.
/// </summary>
public class GameHUD : MonoBehaviour
{
    public static GameHUD Instance { get; private set; }

    private static readonly Color WoodColor = new Color(0.36f, 0.22f, 0.11f, 0.92f);
    private static readonly Color GoldColor = new Color(0.86f, 0.66f, 0.26f, 0.98f);
    private static readonly Color HpHigh = new Color(0.86f, 0.66f, 0.26f, 0.98f); // full → gold (matches the slots)
    private static readonly Color HpLow = new Color(0.80f, 0.20f, 0.15f, 0.98f);  // hurt → red

    private Image[] _slotInner;
    private Image[] _slotIcon;
    private TMP_Text _prompt;
    private CanvasGroup _promptGroup;
    private RectTransform _promptRT;
    private float _promptTarget;   // 0 hidden, 1 shown
    private float _promptAnim;     // eased 0..1
    private PlayerInventory _inv;

    // Player health bar (authored under GamePanel/HealthBar; driven from the local player's Health).
    private Image _healthFill;
    private TMP_Text _healthLabel;
    private Health _health;

    private void Awake()
    {
        Instance = this;
        int n = PlayerInventory.SlotCount;
        _slotInner = new Image[n];
        _slotIcon = new Image[n];

        var hotbar = transform.Find("Hotbar");
        if (hotbar != null)
        {
            for (int i = 0; i < n; i++)
            {
                var slot = hotbar.Find("Slot" + i);
                if (slot == null) continue;
                var inner = slot.Find("Inner");
                _slotInner[i] = inner != null ? inner.GetComponent<Image>() : slot.GetComponent<Image>();
                var icon = inner != null ? inner.Find("Icon") : slot.Find("Icon");
                _slotIcon[i] = icon != null ? icon.GetComponent<Image>() : null;
            }
        }
        var p = transform.Find("Prompt");
        if (p != null)
        {
            _prompt = p.GetComponent<TMP_Text>();
            _promptRT = p.GetComponent<RectTransform>();
            _promptGroup = p.GetComponent<CanvasGroup>();
            if (_promptGroup == null) _promptGroup = p.gameObject.AddComponent<CanvasGroup>();
            _promptGroup.alpha = 0f;
        }

        var hb = transform.Find("HealthBar");
        if (hb != null)
        {
            var fill = hb.Find("FillArea/Fill") ?? hb.Find("Fill");
            _healthFill = fill != null ? fill.GetComponent<Image>() : null;
            var lbl = hb.Find("Label");
            _healthLabel = lbl != null ? lbl.GetComponent<TMP_Text>() : null;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_inv != null) _inv.OnChanged -= Refresh;
        if (_health != null) _health.OnChanged -= RefreshHealth;
    }

    private void Update()
    {
        if (_inv == null)
        {
            _inv = FindLocalInventory();
            if (_inv != null) { _inv.OnChanged += Refresh; Refresh(); }
        }

        if (_health == null)
        {
            _health = FindLocalHealth();
            if (_health != null) { _health.OnChanged += RefreshHealth; RefreshHealth(_health.Current, _health.Max); }
        }

        // Smooth popup for the prompt: fade + scale with a slight overshoot on appear.
        if (_promptGroup != null)
        {
            _promptAnim = Mathf.MoveTowards(_promptAnim, _promptTarget, Time.deltaTime * 7f);
            _promptGroup.alpha = _promptAnim;
            float e = _promptTarget > 0.5f ? EaseOutBack(_promptAnim) : _promptAnim;
            if (_promptRT != null) _promptRT.localScale = Vector3.one * Mathf.Lerp(0.7f, 1f, e);
        }
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    private static PlayerInventory FindLocalInventory()
    {
        foreach (var pi in FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
            if (pi.IsSpawned && pi.IsOwner) return pi;
        return null;
    }

    // The local player's own Health (only PlayerController.IsOwner is true for the local player; NPCs have none).
    private static Health FindLocalHealth()
    {
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            if (pc.IsSpawned && pc.IsOwner) return pc.GetComponent<Health>();
        return null;
    }

    private void RefreshHealth(int cur, int max)
    {
        float frac = max > 0 ? Mathf.Clamp01((float)cur / max) : 0f;
        if (_healthFill != null) { _healthFill.fillAmount = frac; _healthFill.color = Color.Lerp(HpLow, HpHigh, frac); }
        if (_healthLabel != null) _healthLabel.text = cur + " / " + max;
    }

    private void Refresh()
    {
        if (_inv == null) return;
        var wdb = WeaponDatabase.Instance;
        var cdb = ChestDatabase.Instance;
        for (int i = 0; i < _slotInner.Length; i++)
        {
            bool sel = i == _inv.SelectedSlot;
            if (_slotInner[i] != null) _slotInner[i].color = sel ? GoldColor : WoodColor;
            // A slot holds a weapon XOR a chest — show whichever it is.
            var chest = _inv.GetChestSlot(i);
            var icon = chest != ChestId.None
                ? (cdb != null ? cdb.IconOf(chest) : null)
                : (wdb != null ? wdb.IconOf(_inv.GetSlot(i)) : null);
            if (_slotIcon[i] != null) { _slotIcon[i].sprite = icon; _slotIcon[i].enabled = icon != null; }
        }
    }

    public void ShowPrompt(string msg)
    {
        if (_prompt != null && _prompt.text != msg) _prompt.text = msg;
        _promptTarget = 1f;
    }

    public void HidePrompt() => _promptTarget = 0f;
}
