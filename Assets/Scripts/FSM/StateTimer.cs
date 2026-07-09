// ─────────────────────────────────────────────────────────────────────────────
// StateTimer.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. WHY DOES A "TIME IN STATE" HELPER EXIST AT ALL?
//    Almost every gameplay state has a timed rule: "idle for 2 seconds",
//    "chase, but give up after 3 seconds without contact", "attack every
//    1.2 seconds". Without a shared timer, every state grows its own
//    "_elapsed += dt" field that someone forgets to reset in Enter() —
//    the single most common FSM bug. StateTimer resets itself by listening
//    to the machine's OnStateChanged event, so "time in current state" is
//    always correct by construction.
//
// 2. WHY IS deltaTime INJECTED VIA Tick() INSTEAD OF READING Time.deltaTime?
//    Purity. Reading UnityEngine.Time would chain the whole FSM core to
//    Unity. The owner already has a heartbeat (its Update) — it forwards
//    the delta. Bonus: tests can feed synthetic time ("advance 3 seconds")
//    without waiting real seconds, and a server sim can feed its own tick.
//
// 3. LIFETIME.
//    The timer subscribes to the machine's event in its constructor. In the
//    intended usage the owner creates both and they live and die together,
//    so no unsubscription is needed. If you create timers dynamically
//    against a longer-lived machine, call Detach() when done.
// ─────────────────────────────────────────────────────────────────────────────

using System;

namespace FsmFramework
{
    /// <summary>
    /// Tracks elapsed time inside the current state of a
    /// <see cref="StateMachine"/>, resetting automatically on every
    /// transition. Drive it by calling <see cref="Tick"/> once per frame
    /// with the frame's delta time.
    /// </summary>
    public class StateTimer
    {
        private readonly StateMachine _machine;

        /// <summary>Seconds accumulated since the machine last changed state.</summary>
        public float Elapsed { get; private set; }

        /// <summary>
        /// Creates a timer bound to <paramref name="machine"/>. The timer
        /// resets to zero automatically whenever the machine transitions.
        /// </summary>
        /// <param name="machine">The machine whose state time to track.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="machine"/> is null.
        /// </exception>
        public StateTimer(StateMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
            _machine.OnStateChanged += HandleStateChanged;
        }

        /// <summary>
        /// Advances the timer. Call once per frame from the owner's Update,
        /// passing <c>Time.deltaTime</c> (or any synthetic delta in tests).
        /// </summary>
        /// <param name="deltaTime">Seconds elapsed since the last tick.</param>
        public void Tick(float deltaTime)
        {
            Elapsed += deltaTime;
        }

        /// <summary>
        /// Convenience for timed transitions:
        /// <c>if (timer.HasElapsed(3f)) machine.SetState&lt;PatrolState&gt;();</c>
        /// </summary>
        /// <param name="seconds">Threshold to compare against.</param>
        /// <returns>True once the current state has lasted at least <paramref name="seconds"/>.</returns>
        public bool HasElapsed(float seconds)
        {
            return Elapsed >= seconds;
        }

        /// <summary>
        /// Manually zeroes the timer without a state change (e.g. an attack
        /// state resetting its own rhythm mid-state).
        /// </summary>
        public void Reset()
        {
            Elapsed = 0f;
        }

        /// <summary>
        /// Unsubscribes from the machine's transition event. Only needed if
        /// the timer's lifetime is shorter than the machine's.
        /// </summary>
        public void Detach()
        {
            _machine.OnStateChanged -= HandleStateChanged;
        }

        private void HandleStateChanged(IState oldState, IState newState)
        {
            Elapsed = 0f;
        }
    }
}
