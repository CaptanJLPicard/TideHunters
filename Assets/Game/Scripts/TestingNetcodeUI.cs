using System.Collections;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the pre-built main-menu UI (wood/gold pirate style, matching the inventory): a CREATE panel
/// (name a server and host it), a JOIN panel (enter a server address and connect), and a "SERVER NOT
/// FOUND" popup for failed joins. The UI itself lives in the scene as real GameObjects (built once, not at
/// runtime) — this script only wires the buttons and the connection logic and shows / hides panels. Hides
/// itself (and the in-game HUD) while connected and returns on disconnect.
/// </summary>
public class TestingNetcodeUI : MonoBehaviour
{
    [Header("Roots")]
    [SerializeField] private GameObject menuRoot;   // the whole menu (hidden while connected)
    [SerializeField] private GameObject hudPanel;   // the in-game HUD (hidden while the menu is up)

    [Header("State panels")]
    [SerializeField] private GameObject mainPanel;
    [SerializeField] private GameObject createPanel;
    [SerializeField] private GameObject joinPanel;
    [SerializeField] private GameObject errorPanel;
    [SerializeField] private TMP_Text errorText;
    [SerializeField] private TMP_Text statusText;

    [Header("Inputs")]
    [SerializeField] private TMP_InputField createInput;
    [SerializeField] private TMP_InputField joinInput;

    [Header("Buttons")]
    [SerializeField] private Button gotoCreateButton;
    [SerializeField] private Button gotoJoinButton;
    [SerializeField] private Button createButton;
    [SerializeField] private Button joinButton;
    [SerializeField] private Button createBackButton;
    [SerializeField] private Button joinBackButton;
    [SerializeField] private Button errorOkButton;

    [Header("Connection")]
    [SerializeField] private ushort port = 7777;
    [SerializeField] private float joinTimeout = 6f;

    private bool _joining;
    private Coroutine _joinWatch;

    /// <summary>The name the local host created its server with (cosmetic / future lobby display).</summary>
    public static string ServerName { get; private set; } = "My Server";

    private void Awake()
    {
        Wire(gotoCreateButton, () => ShowState(1));
        Wire(gotoJoinButton, () => ShowState(2));
        Wire(createButton, Host);
        Wire(joinButton, Join);
        Wire(createBackButton, () => ShowState(0));
        Wire(joinBackButton, () => ShowState(0));
        Wire(errorOkButton, () => { if (errorPanel) errorPanel.SetActive(false); ShowState(2); });

        ShowState(0);
        if (errorPanel) errorPanel.SetActive(false);

        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback += OnConnected;
            nm.OnClientDisconnectCallback += OnDisconnected;
            nm.OnServerStarted += RefreshVisibility;
            nm.OnClientStopped += OnNetStopped;
            nm.OnServerStopped += OnNetStopped;
        }
        RefreshVisibility();
    }

    private void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnConnected;
            nm.OnClientDisconnectCallback -= OnDisconnected;
            nm.OnServerStarted -= RefreshVisibility;
            nm.OnClientStopped -= OnNetStopped;
            nm.OnServerStopped -= OnNetStopped;
        }
    }

    // Session ended (host/client shut down, e.g. MAIN MENU) — reliably reopen the menu.
    private void OnNetStopped(bool _)
    {
        _joining = false;
        if (errorPanel != null) errorPanel.SetActive(false);
        ShowState(0);
        RefreshVisibility();
    }

    private static void Wire(Button b, UnityEngine.Events.UnityAction a)
    {
        if (b != null) b.onClick.AddListener(a);
    }

    private void OnConnected(ulong _)
    {
        _joining = false;
        RefreshVisibility();
    }

    private void OnDisconnected(ulong _)
    {
        if (_joining) { _joining = false; ShowError("SERVER NOT FOUND", "No server answered at that address."); }
        else ShowState(0);
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        var nm = NetworkManager.Singleton;
        bool live = nm != null && (nm.IsClient || nm.IsServer);
        if (live && errorPanel != null) errorPanel.SetActive(false);
        if (menuRoot != null) menuRoot.SetActive(!live);
        if (hudPanel != null) hudPanel.SetActive(live);
        if (!live) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } // the menu needs a cursor
    }

    // ---- Actions --------------------------------------------------------------------------

    private void Host()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        ServerName = createInput != null && !string.IsNullOrWhiteSpace(createInput.text) ? createInput.text.Trim() : "My Server";
        SetStatus("Hosting...");
        if (!nm.StartHost()) ShowError("COULD NOT HOST", "The port may already be in use.");
    }

    private void Join()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        string addr = joinInput != null && !string.IsNullOrWhiteSpace(joinInput.text) ? joinInput.text.Trim() : "127.0.0.1";
        var utp = nm.GetComponent<UnityTransport>();
        if (utp != null) utp.SetConnectionData(addr, port);
        SetStatus("Connecting...");
        _joining = true;
        if (!nm.StartClient()) { _joining = false; ShowError("SERVER NOT FOUND", "Could not start the connection."); return; }
        if (_joinWatch != null) StopCoroutine(_joinWatch);
        _joinWatch = StartCoroutine(JoinWatch());
    }

    private IEnumerator JoinWatch()
    {
        float t = 0f;
        var nm = NetworkManager.Singleton;
        while (t < joinTimeout)
        {
            if (nm == null || nm.IsConnectedClient) yield break;
            if (!_joining) yield break;
            t += Time.deltaTime;
            yield return null;
        }
        if (_joining)
        {
            _joining = false;
            if (nm != null) nm.Shutdown();
            ShowError("SERVER NOT FOUND", "No server answered at that address.");
        }
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }

    private void ShowState(int i)
    {
        if (mainPanel != null) mainPanel.SetActive(i == 0);
        if (createPanel != null) createPanel.SetActive(i == 1);
        if (joinPanel != null) joinPanel.SetActive(i == 2);
        SetStatus("");
    }

    private void ShowError(string title, string detail)
    {
        if (errorText != null) errorText.text = title + "\n<size=55%><color=#F6EDCB>" + detail + "</color></size>";
        if (errorPanel != null) errorPanel.SetActive(true);
        SetStatus("");
    }
}
