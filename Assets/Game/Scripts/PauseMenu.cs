using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// In-game pause menu (opened with ESC), wood/gold pirate style like the inventory. Drives a pre-built UI
/// in the scene (no runtime UI creation): RESUME, RESTART (reloads the scene for everyone — host only), and
/// MAIN MENU (leaves the session and reopens the connect menu). The restart is server-authoritative and
/// synced through Netcode scene management, so it happens on every client; on clients the restart button is
/// non-interactable. While the pause menu is open <see cref="IsOpen"/> lets the player scripts freeze input.
/// </summary>
public class PauseMenu : MonoBehaviour
{
    [SerializeField] private GameObject escRoot;
    [SerializeField] private Button resumeButton;
    [SerializeField] private Button restartButton;
    [SerializeField] private Button mainMenuButton;
    [Tooltip("Optional 'host only' note shown to clients under the restart button.")]
    [SerializeField] private TMP_Text restartHint;

    /// <summary>True while the pause menu is showing — player input scripts skip their input while it is.</summary>
    public static bool IsOpen { get; private set; }

    private void Awake()
    {
        if (resumeButton != null) resumeButton.onClick.AddListener(Close);
        if (restartButton != null) restartButton.onClick.AddListener(Restart);
        if (mainMenuButton != null) mainMenuButton.onClick.AddListener(LeaveToMenu);
        SetOpen(false);
    }

    private void OnDestroy() { IsOpen = false; }

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        bool live = nm != null && (nm.IsClient || nm.IsServer);
        var kb = Keyboard.current;
        if (live && kb != null && kb.escapeKey.wasPressedThisFrame) SetOpen(!IsOpen);
        else if (!live && IsOpen) SetOpen(false); // never linger outside a session
    }

    private void SetOpen(bool open)
    {
        IsOpen = open;
        if (escRoot != null) escRoot.SetActive(open);

        var nm = NetworkManager.Singleton;
        if (open)
        {
            bool host = nm != null && nm.IsServer;
            if (restartButton != null) restartButton.interactable = host;   // only the host may restart
            if (restartHint != null) restartHint.gameObject.SetActive(!host);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        else if (nm != null && (nm.IsClient || nm.IsServer))
        {
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
    }

    private void Close() => SetOpen(false);

    // Host-only, synced: reload the active scene for everyone via Netcode scene management.
    private void Restart()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        Close();

        // Clear dynamic session objects + inventories so the reloaded scene starts fresh. (The in-scene
        // chests reset with the reload; players persist across it and are moved back to spawn by
        // PlayerController — so here we only despawn dynamically-spawned drops and wipe the inventories.)
        foreach (var dw in DroppedWeapon.Active.ToArray())
            if (dw != null && dw.NetworkObject != null && dw.NetworkObject.IsSpawned && dw.NetworkObject.IsSceneObject != true)
                dw.NetworkObject.Despawn(true);
        foreach (var wc in WorldChest.Active.ToArray())
            if (wc != null && wc.NetworkObject != null && wc.NetworkObject.IsSpawned && wc.NetworkObject.IsSceneObject != true)
                wc.NetworkObject.Despawn(true);
        foreach (var inv in Object.FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
            inv.ResetServer();

        nm.SceneManager.LoadScene(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
    }

    private void LeaveToMenu()
    {
        Close();
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.Shutdown(); // TestingNetcodeUI reopens the menu on disconnect
    }
}
