using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The on-screen controls guide. A "Press I for controls" hint sits in the bottom-right the whole time; pressing
/// I (or the panel's Close button, or I again) toggles the guide panel open/closed. Both the hint and the panel
/// are authored in the Canvas (wood/gold inventory style); this only drives the toggle.
/// </summary>
public class ControlsPanel : MonoBehaviour
{
    [SerializeField] private GameObject panel; // the controls guide (starts hidden)
    [SerializeField] private UnityEngine.UI.Button closeButton;

    private void Awake()
    {
        if (panel != null) panel.SetActive(false);
        if (closeButton != null) closeButton.onClick.AddListener(Close);
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.iKey.wasPressedThisFrame && !PauseMenu.IsOpen) Toggle();
    }

    public void Toggle() { if (panel != null) panel.SetActive(!panel.activeSelf); }
    public void Close()  { if (panel != null) panel.SetActive(false); }
}
