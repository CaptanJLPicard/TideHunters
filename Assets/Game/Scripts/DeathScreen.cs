using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The "YOU DIED" screen, opened automatically when the LOCAL player's <see cref="Health"/> reaches 0. Wood/gold
/// pirate style like the pause menu, and it drives a pre-built panel under the Canvas (no runtime UI creation).
/// RESTART reloads the round for everyone and revives all players — HOST ONLY, exactly like the pause menu's
/// restart (clients see it disabled with a hint). MAIN MENU leaves the session and reopens the connect menu.
/// </summary>
public class DeathScreen : MonoBehaviour
{
    public static DeathScreen Instance { get; private set; }

    [SerializeField] private GameObject deathRoot;
    [SerializeField] private Button restartButton;
    [SerializeField] private Button mainMenuButton;
    [Tooltip("'Only the host can restart' note shown to clients.")]
    [SerializeField] private TMP_Text restartHint;

    /// <summary>True while the death screen is showing — player/other UI scripts can check this.</summary>
    public static bool IsOpen { get; private set; }

    private void Awake()
    {
        Instance = this;
        if (restartButton != null) restartButton.onClick.AddListener(Restart);
        if (mainMenuButton != null) mainMenuButton.onClick.AddListener(LeaveToMenu);
        Hide();
    }

    private void OnDestroy() { if (Instance == this) Instance = null; IsOpen = false; }

    public void Show()
    {
        IsOpen = true;
        if (deathRoot != null) deathRoot.SetActive(true);
        var nm = NetworkManager.Singleton;
        bool host = nm != null && nm.IsServer;
        if (restartButton != null) restartButton.interactable = host; // only the host may restart the round
        if (restartHint != null) restartHint.gameObject.SetActive(!host);
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    public void Hide()
    {
        IsOpen = false;
        if (deathRoot != null) deathRoot.SetActive(false);
    }

    // Host-only, synced: revive everyone, clear dynamic session objects, and reload the scene for all clients.
    private void Restart()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        Hide();

        foreach (var h in Health.All.ToArray()) if (h != null) h.ResetServer();
        foreach (var dw in DroppedWeapon.Active.ToArray())
            if (dw != null && dw.NetworkObject != null && dw.NetworkObject.IsSpawned && dw.NetworkObject.IsSceneObject != true)
                dw.NetworkObject.Despawn(true);
        foreach (var wc in WorldChest.Active.ToArray())
            if (wc != null && wc.NetworkObject != null && wc.NetworkObject.IsSpawned && wc.NetworkObject.IsSceneObject != true)
                wc.NetworkObject.Despawn(true);
        foreach (var inv in Object.FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None)) inv.ResetServer();

        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        nm.SceneManager.LoadScene(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
    }

    private void LeaveToMenu()
    {
        Hide();
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.Shutdown(); // TestingNetcodeUI reopens the menu on disconnect
    }
}
