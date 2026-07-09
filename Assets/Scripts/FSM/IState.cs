// ─────────────────────────────────────────────────────────────────────────────
// IState.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. ZERO UNITY DEPENDENCIES.
//    Nothing in this file (or StateMachine.cs / StateTimer.cs) references
//    UnityEngine. The FSM core is plain C#: it can be unit-tested without
//    the Unity test runner, reused in a server build, or dropped into a
//    non-Unity tools project unchanged.
//
// 2. WHY BOTH Update() AND FixedUpdate()?
//    Gameplay states routinely mix per-frame logic (input, timers,
//    animation triggers) with physics-step logic (forces, rigidbody moves).
//    Giving IState both hooks — forwarded by the owner MonoBehaviour —
//    means a state never has to cache "do this next physics step" flags.
//    States that don't need one simply leave the method empty (or inherit
//    an empty virtual from a base class, as EnemyAI's states do).
//
// 3. WHY Enter/Exit INSTEAD OF CONSTRUCTOR/FINALIZER SEMANTICS?
//    States are constructed once and re-entered many times. Enter() is the
//    per-activation reset point (like OnSpawn in a pooled object); Exit()
//    is the guaranteed cleanup hook that runs even when a transition is
//    triggered from outside the state.
// ─────────────────────────────────────────────────────────────────────────────

namespace FsmFramework
{
    /// <summary>
    /// A single state in a <see cref="StateMachine"/>. Implementations are
    /// constructed once, registered via <see cref="StateMachine.AddState"/>,
    /// and re-entered any number of times.
    /// </summary>
    public interface IState
    {
        /// <summary>
        /// Called once each time this state becomes the current state.
        /// Reset per-activation data (timers, targets, flags) here.
        /// </summary>
        void Enter();

        /// <summary>
        /// Called every frame while this state is current
        /// (forwarded from the owner's MonoBehaviour.Update).
        /// </summary>
        void Update();

        /// <summary>
        /// Called every physics step while this state is current
        /// (forwarded from the owner's MonoBehaviour.FixedUpdate).
        /// </summary>
        void FixedUpdate();

        /// <summary>
        /// Called once when the machine leaves this state, before the next
        /// state's <see cref="Enter"/>. Undo anything Enter set up.
        /// </summary>
        void Exit();
    }
}
