// ─────────────────────────────────────────────────────────────────────────────
// PlayerDummy.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. DELIBERATELY MINIMAL.
//    This object exists purely to trigger the enemy's state transitions in
//    Play mode. No CharacterController, no physics — direct transform
//    movement keeps the demo scene setup at "add one component".
//
// 2. IsMakingNoise FEEDS THE HEARING SYSTEM.
//    Hearing that fires on a perfectly still player feels wrong (and makes
//    the sight cone impossible to demo — you could never sneak). The dummy
//    publishes whether it moved this frame; EnemyAI's hearing check reads
//    it (toggleable via 'Hear Only Moving Player' on the enemy). Sneak up
//    behind the enemy by NOT holding a key, then move to be heard.
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace FsmFramework.Demo
{
    /// <summary>
    /// WASD-movable test target. Tag its GameObject "Player" so EnemyAI's
    /// perception recognizes it. Publishes <see cref="IsMakingNoise"/> for
    /// the enemy's hearing check.
    /// </summary>
    public class PlayerDummy : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Movement speed in units per second.")]
        [SerializeField] private float _moveSpeed = 5f;

        [Tooltip("Turn rate in degrees per second toward the movement direction.")]
        [SerializeField] private float _turnSpeed = 720f;

        /// <summary>True while the player moved this frame — i.e. is audible.</summary>
        public bool IsMakingNoise { get; private set; }

        private void Update()
        {
            var input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));

            IsMakingNoise = input.sqrMagnitude > 0f;

            if (!IsMakingNoise)
            {
                return;
            }

            if (input.sqrMagnitude > 1f)
            {
                input.Normalize(); // Diagonals must not be faster than cardinals.
            }

            transform.position += input * (_moveSpeed * Time.deltaTime);

            // Face the direction of travel so the capsule reads as "walking".
            Quaternion desired = Quaternion.LookRotation(input, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, desired, _turnSpeed * Time.deltaTime);
        }
    }
}
