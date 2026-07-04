using Unity.Netcode;
using Unity.UI;
using UnityEngine;
using UnityEngine.UI;

public class TestingNetcodeUI : MonoBehaviour
{
    [SerializeField] private Button startHostButton;
    [SerializeField] private Button startClientButton;

    private void Awake()
    {
        startHostButton.onClick.AddListener(delegate
        {
            NetworkManager.Singleton.StartHost();
        });
        startClientButton.onClick.AddListener(delegate
        {
            NetworkManager.Singleton.StartClient();
        });
    }
}
