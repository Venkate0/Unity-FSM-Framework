// ─────────────────────────────────────────────────────────────────────────────
// DebugOverlay.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. IMGUI (OnGUI) BECAUSE IT'S A DEV TOOL.
//    Zero scene setup — no Canvas, no EventSystem. Drop the component on an
//    empty GameObject, assign the enemy, done. The minor per-frame IMGUI
//    allocation is acceptable for a debug overlay that ships disabled.
//
// 2. THE OVERLAY READS, IT NEVER DRIVES.
//    Everything shown comes from EnemyAI's read-only properties
//    (CurrentStateName, TimeInState, LastTransitionReason, CanSee/CanHear).
//    The one action it offers — the kill key — goes through the enemy's
//    public Kill() method, same as any gameplay system would.
//
// 3. DISPLAY-ONLY STRINGS.
//    CurrentStateName is printed, never compared. All comparisons anywhere
//    in the project are type-based; the string exists purely for humans.
// ─────────────────────────────────────────────────────────────────────────────

using FsmFramework.AI;
using UnityEngine;

namespace FsmFramework.Demo
{
    /// <summary>
    /// IMGUI panel showing the observed enemy's live FSM status: current
    /// state, time in state, last transition reason, and sight/hearing
    /// indicators. Also exposes a debug kill key to demo DeadState.
    /// </summary>
    public class DebugOverlay : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The enemy whose state machine this overlay displays.")]
        [SerializeField] private EnemyAI _enemy;

        [Header("Debug Keys")]
        [Tooltip("Key that kills the enemy (demonstrates the terminal DeadState).")]
        [SerializeField] private KeyCode _killKey = KeyCode.K;

        [Header("Layout")]
        [Tooltip("Panel width in pixels.")]
        [SerializeField] private float _panelWidth = 360f;

        [Tooltip("Distance from the top-left screen corner in pixels.")]
        [SerializeField] private float _screenPadding = 10f;

        private void Update()
        {
            if (_enemy != null && Input.GetKeyDown(_killKey))
            {
                _enemy.Kill();
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(
                _screenPadding, _screenPadding, _panelWidth, Screen.height - _screenPadding * 2f));
            GUILayout.BeginVertical(GUI.skin.box);

            if (_enemy == null)
            {
                GUILayout.Label("DebugOverlay: no EnemyAI assigned.");
            }
            else
            {
                GUILayout.Label($"State: {_enemy.CurrentStateName}");
                GUILayout.Label($"Time in state: {_enemy.TimeInState:F1}s");
                GUILayout.Label($"Last transition: {_enemy.LastTransitionReason}");

                GUILayout.Space(4f);

                // Unicode check/cross keeps the senses readable at a glance.
                GUILayout.Label($"Sight:   {(_enemy.CanSeePlayer ? "✔ player visible" : "✘ nothing in cone")}");
                GUILayout.Label($"Hearing: {(_enemy.CanHearPlayer ? "✔ player audible" : "✘ silence")}");

                GUILayout.Space(4f);
                GUILayout.Label($"[WASD] move player    [{_killKey}] kill enemy");
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}
