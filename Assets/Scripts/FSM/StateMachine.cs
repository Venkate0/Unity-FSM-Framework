// ─────────────────────────────────────────────────────────────────────────────
// StateMachine.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. Dictionary<Type, IState> — THE TYPE *IS* THE KEY.
//    Keying states by their System.Type kills the two classic FSM failure
//    modes at once:
//      • No string keys → no typo bugs ("Chse"), no silent mismatches after
//        a rename, full refactoring-tool support.
//      • No enum keys → no enum that must be kept in sync with the class
//        list by hand.
//    SetState<PatrolState>() is compile-time checked: if the state class
//    doesn't exist, the code doesn't build.
//
// 2. PLAIN C# CLASS, NOT A MonoBehaviour.
//    The machine has no scene identity and no Unity lifecycle needs. The
//    owner (any class — a MonoBehaviour, a server sim, a test) constructs
//    it and forwards Update/FixedUpdate. Consequences of purity:
//      • Errors are thrown as exceptions, not Debug.LogError — the core
//        cannot reference UnityEngine, and misconfiguration (setting an
//        unregistered state) is a programming error that should fail fast.
//      • Fully unit-testable without an editor.
//
// 3. TRANSITION ORDER: Exit → swap → Enter → event.
//    Exit always sees the machine still "in" the old state; Enter always
//    sees CurrentState already pointing at the new one; observers get the
//    event only after the transition is complete and consistent.
//
// 4. SELF-TRANSITIONS ARE IGNORED.
//    SetState<T>() while T is already current is a no-op. This makes state
//    logic simpler ("every frame I see the player, ensure ChaseState") and
//    avoids accidental Exit/Enter churn. If a design needs explicit
//    re-entry, that is a deliberate feature to add, not a default.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

namespace FsmFramework
{
    /// <summary>
    /// A minimal, allocation-free-at-runtime finite state machine keyed by
    /// state <see cref="Type"/>. Pure C# — no Unity dependencies — so it is
    /// portable to any project and trivially unit-testable.
    /// </summary>
    /// <remarks>
    /// Typical usage from a MonoBehaviour owner:
    /// <code>
    /// _machine = new StateMachine();
    /// _machine.AddState(new IdleState(this));
    /// _machine.AddState(new PatrolState(this));
    /// _machine.SetState&lt;IdleState&gt;();
    /// // ...then forward the loop:
    /// void Update()      => _machine.Update();
    /// void FixedUpdate() => _machine.FixedUpdate();
    /// </code>
    /// </remarks>
    public class StateMachine
    {
        private readonly Dictionary<Type, IState> _states = new Dictionary<Type, IState>();

        /// <summary>
        /// The state currently receiving <see cref="Update"/> and
        /// <see cref="FixedUpdate"/> calls, or <c>null</c> before the first
        /// <see cref="SetState{T}"/>.
        /// </summary>
        public IState CurrentState { get; private set; }

        /// <summary>
        /// Raised after every completed transition, with
        /// (oldState, newState). The old state is <c>null</c> for the very
        /// first transition. Fired after the new state's
        /// <see cref="IState.Enter"/> has run, so subscribers observe a
        /// fully consistent machine.
        /// </summary>
        public event Action<IState, IState> OnStateChanged;

        /// <summary>
        /// Registers a state instance, keyed by its concrete runtime type.
        /// Each type may be registered exactly once.
        /// </summary>
        /// <param name="state">The state instance to register.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="state"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown if a state of the same type is already registered —
        /// duplicate registration is always a wiring bug worth failing fast on.
        /// </exception>
        public void AddState(IState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            Type key = state.GetType();
            if (_states.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"A state of type '{key.Name}' is already registered. Each state type may be added once.");
            }

            _states.Add(key, state);
        }

        /// <summary>
        /// Transitions to the registered state of type <typeparamref name="T"/>:
        /// calls <see cref="IState.Exit"/> on the current state, swaps, calls
        /// <see cref="IState.Enter"/> on the new state, then raises
        /// <see cref="OnStateChanged"/>. Setting the type that is already
        /// current is a no-op (see design decision #4).
        /// </summary>
        /// <typeparam name="T">The concrete state type to activate.</typeparam>
        /// <exception cref="InvalidOperationException">
        /// Thrown if no state of type <typeparamref name="T"/> was registered
        /// via <see cref="AddState"/>.
        /// </exception>
        public void SetState<T>() where T : IState
        {
            Type key = typeof(T);

            if (!_states.TryGetValue(key, out IState nextState))
            {
                throw new InvalidOperationException(
                    $"No state of type '{key.Name}' is registered. Call AddState(new {key.Name}(...)) first.");
            }

            if (ReferenceEquals(nextState, CurrentState))
            {
                return; // Self-transition: intentionally ignored.
            }

            IState previousState = CurrentState;
            previousState?.Exit();

            CurrentState = nextState;
            nextState.Enter();

            OnStateChanged?.Invoke(previousState, nextState);
        }

        /// <summary>
        /// Returns whether a state of type <typeparamref name="T"/> is
        /// currently active. Type-based — never compare state names.
        /// </summary>
        /// <typeparam name="T">The state type to test against.</typeparam>
        public bool IsInState<T>() where T : IState
        {
            return CurrentState is T;
        }

        /// <summary>
        /// Forwards the per-frame tick to the current state. Call from the
        /// owner's <c>Update()</c>. Safe to call before any state is set.
        /// </summary>
        public void Update()
        {
            CurrentState?.Update();
        }

        /// <summary>
        /// Forwards the physics-step tick to the current state. Call from
        /// the owner's <c>FixedUpdate()</c>. Safe to call before any state
        /// is set.
        /// </summary>
        public void FixedUpdate()
        {
            CurrentState?.FixedUpdate();
        }
    }
}
