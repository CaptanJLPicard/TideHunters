using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Player ship gunnery for whoever is at the wheel. Hold RIGHT mouse to AIM: the view snaps over the cannon
/// (a Cinemachine vcam that sits at the ship's CannonCameraPos and looks down the barrel), the Global Volume
/// depth-of-field pulls out (gaussian start 55→250), and moving the mouse UP / DOWN raises / lowers the barrel
/// (X-euler pitchMin..pitchMax → parabola vs flat shot). LEFT-click fires along the barrel. Owner-only visuals;
/// the barrel pitch replicates through the ship's ShipCannon and the shot is spawned server-authoritatively.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class ShipGunner : NetworkBehaviour
{
    [Header("Aim camera")]
    [Tooltip("Scene Cinemachine vcam that the aim view blends to (positioned over the cannon each frame).")]
    [SerializeField] private string aimCamName = "CannonAimCam";
    [SerializeField] private int aimPriority = 30;

    [Header("Pitch (mouse Y)")]
    [SerializeField] private float pitchSensitivity = 0.08f;
    [Tooltip("Fixed downward tilt of the aim camera (deg). The camera holds this — only the cannon pitches with the mouse.")]
    [SerializeField] private float camPitch = 5f;

    [Tooltip("Camera shake (deg) on each ship-cannon shot — punchier than the hand-gun shake (~1.4).")]
    [SerializeField] private float cannonShake = 2.6f;

    [Header("Depth of field (Global Volume, Gaussian start)")]
    [SerializeField] private float dofAimStart = 250f;
    [SerializeField] private float dofRestStart = 55f;
    [SerializeField] private float blendSpeed = 12f;

    private PlayerController _pc;
    private DepthOfField _dof;
    private bool _dofSearched;
    private CinemachineCamera _aimCam;
    private int _aimCamBasePriority;
    private float _aimPitch = 4f;
    private float _camShake;       // aim-cam shake magnitude (deg), decays
    private float _nextClientFire; // client-side reload mirror so FX/shake only play on a shot that actually fires

    public override void OnNetworkSpawn()
    {
        _pc = GetComponent<PlayerController>();
        if (!IsOwner) enabled = false; // only the local driver aims their own camera / DoF
    }

    private void LateUpdate()
    {
        if (_pc == null) return;

        var ship = _pc.IsDriving ? _pc.RidingShip : null;
        var cannon = ship != null ? ship.GetComponent<ShipCannon>() : null;
        var mouse = Mouse.current;
        bool aiming = cannon != null && mouse != null && mouse.rightButton.isPressed;

        EnsureRefs();
        float k = 1f - Mathf.Exp(-blendSpeed * Time.deltaTime);

        if (aiming)
        {
            // Mouse Y raises / lowers the BARREL only; the camera holds a fixed view over the cannon.
            float my = mouse.delta.ReadValue().y;
            _aimPitch = Mathf.Clamp(_aimPitch - my * pitchSensitivity, cannon.PitchMin, cannon.PitchMax);
            cannon.SetPitchOwner(_aimPitch);

            var ccp = FindCannonCamPos(ship);
            if (_aimCam != null && ccp != null)
            {
                Vector3 bow = ship.transform.forward; bow.y = 0f;
                if (bow.sqrMagnitude < 1e-4f) bow = Vector3.forward; else bow.Normalize();
                Quaternion rot = Quaternion.LookRotation(bow, Vector3.up) * Quaternion.Euler(camPitch, 0f, 0f); // fixed
                if (_camShake > 0.01f)
                    rot *= Quaternion.Euler(Random.Range(-_camShake, _camShake), Random.Range(-_camShake, _camShake), 0f);
                _aimCam.transform.SetPositionAndRotation(ccp.position, rot);
                _aimCam.Priority = aimPriority;
            }
            if (_dof != null) _dof.gaussianStart.value = Mathf.Lerp(_dof.gaussianStart.value, dofAimStart, k);
        }
        else
        {
            if (_aimCam != null) _aimCam.Priority = _aimCamBasePriority; // blend back to the follow cam
            if (_dof != null) _dof.gaussianStart.value = Mathf.Lerp(_dof.gaussianStart.value, dofRestStart, k);
        }

        // Fire on LEFT-click whenever manning the cannon — but only when the barrel has actually reloaded, so the
        // muzzle FX + camera shake fire ONLY on a real shot (not on every spammed click during reload).
        if (cannon != null && mouse != null && mouse.leftButton.wasPressedThisFrame && Time.time >= _nextClientFire)
        {
            _nextClientFire = Time.time + cannon.ReloadTime;
            FireCannonRpc(new NetworkObjectReference(ship.NetworkObject));
            _camShake = cannonShake;      // shakes the aim cam (see above)
            _pc.ShakeCamera(cannonShake); // and the follow cam when hip-firing — stronger kick than a hand gun
        }
        _camShake = Mathf.Lerp(_camShake, 0f, 1f - Mathf.Exp(-11f * Time.deltaTime));
    }

    private static Transform FindCannonCamPos(ShipController ship)
    {
        var t = ship.transform.Find("CannonCameraPos");
        if (t != null) return t;
        foreach (var c in ship.GetComponentsInChildren<Transform>(true)) if (c.name == "CannonCameraPos") return c;
        return null;
    }

    private void EnsureRefs()
    {
        if (!_dofSearched)
        {
            _dofSearched = true;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v.profile == null) continue;
                v.profile = Instantiate(v.profile);              // clone so runtime tweaks never touch the shared asset
                if (v.profile.TryGet<DepthOfField>(out var dof)) { _dof = dof; break; }
            }
        }
        if (_aimCam == null)
        {
            var go = GameObject.Find(aimCamName);
            if (go != null) { _aimCam = go.GetComponent<CinemachineCamera>(); if (_aimCam != null) _aimCamBasePriority = _aimCam.Priority; }
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void FireCannonRpc(NetworkObjectReference shipRef)
    {
        if (!shipRef.TryGet(out var no)) { Debug.Log("[GUNNER-SRV] no ship ref"); return; }
        var ship = no.GetComponent<ShipController>();
        if (ship == null || ship.Driver != OwnerClientId) { Debug.Log("[GUNNER-SRV] reject: driver=" + (ship!=null?ship.Driver:999) + " owner=" + OwnerClientId); return; }
        var cannon = ship.GetComponent<ShipCannon>();
        bool fired = cannon != null && cannon.ServerFireForward(OwnerClientId);
        Debug.Log("[GUNNER-SRV] cannon=" + (cannon!=null) + " fired=" + fired);
    }
}
