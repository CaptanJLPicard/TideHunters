using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds an aim-tuning debug panel to the PlayerSpineAim inspector: a button that freezes the player in
/// the gun aim pose (so it can be tuned without holding right-mouse) plus a live read-out of how far the
/// forearm is off the crosshair, so aimOffsetEuler can be dialled in to ~0.
/// </summary>
[CustomEditor(typeof(PlayerSpineAim))]
public class PlayerSpineAimEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var spine = (PlayerSpineAim)target;
        var combat = spine.GetComponent<PlayerCombat>();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Aim Tuning (Debug)", EditorStyles.boldLabel);

        if (combat == null)
        {
            EditorGUILayout.HelpBox("No PlayerCombat on this GameObject — nothing to hold.", MessageType.Warning);
            return;
        }

        bool held = combat.debugHoldAim;
        Color prev = GUI.backgroundColor;
        GUI.backgroundColor = held ? new Color(0.40f, 0.85f, 0.40f) : prev;
        if (GUILayout.Button(held ? "● HOLDING AIM POSE — click to release" : "Hold Aim Pose (freeze to tune)", GUILayout.Height(32)))
            combat.debugHoldAim = !held;
        GUI.backgroundColor = prev;

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play mode, then click to freeze the aim pose and tune aimOffsetEuler live.", MessageType.None);
            return;
        }

        if (held)
        {
            float err = spine.DebugForearmYawError();
            EditorGUILayout.HelpBox(
                $"Forearm vs crosshair (horizontal): {err:+0.0;-0.0}°\n" +
                "Adjust aimOffsetEuler.y above until this reads ~0 → the gun points where the camera looks.",
                Mathf.Abs(err) <= 5f ? MessageType.Info : MessageType.Warning);
            Repaint(); // keep the read-out live while holding
        }
        else
        {
            EditorGUILayout.HelpBox("Equip a gun, then click the button to freeze the aim pose.", MessageType.None);
        }
    }
}
