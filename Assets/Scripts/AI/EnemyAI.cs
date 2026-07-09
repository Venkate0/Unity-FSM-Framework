// ─────────────────────────────────────────────────────────────────────────────
// EnemyAI.cs
//
// DESIGN DECISIONS
// ────────────────
// 1. STATES AS NESTED PRIVATE CLASSES.
//    The five states exist only to drive THIS component — they are
//    implementation detail, not public API. Nesting them:
//      • gives them access to EnemyAI's private fields (no awkward public
//        accessors leaking into the Inspector-facing surface),
//      • keeps the whole behaviour readable top-to-bottom in one file,
//      • prevents other systems from ever referencing PatrolState directly.
//    A larger game with states shared across enemy types would promote
//    them to their own files with a context interface — see the README's
//    "Key Design Decisions" for that trade-off.
//
// 2. ONE SHARED BASE (EnemyStateBase) WITH THE OWNER INJECTED.
//    Every state touches the enemy only through its Owner reference —
//    no statics, no scene lookups, no service locators. That makes a state
//    a pure function of (owner, time), which is what makes FSMs debuggable.
//
// 3. PERCEPTION IS CENTRALIZED, NOT PER-STATE.
//    CanSeePlayer / CanHearPlayer are computed once per frame in
//    UpdatePerception() and read by whichever state cares. If each state
//    ran its own OverlapSphere, the physics cost would multiply and the
//    values could disagree within a frame.
//    Sight = OverlapSphere (radius = sight range) + Vector3.Angle FOV cone.
//    Hearing = smaller OverlapSphere, no angle check (ears have no
//    direction), optionally requiring the player to be moving (noise).
//    Both use OverlapSphereNonAlloc into a reused buffer — zero garbage.
//
// 4. TRANSITIONS GO THROUGH ChangeState<T>(reason).
//    A single funnel that records WHY the last transition happened. The
//    reason string is diagnostics-only (shown in the overlay) — it is never
//    compared or branched on. All state comparisons are type-based
//    (SetState<T>, IsInState<T>), per the no-string-comparison rule.
//
// 5. NavMeshAgent NOTES.
//    Chase re-paths on a short serialized interval instead of every frame:
//    SetDestination triggers path planning, and 60 requests/second per
//    agent is wasted work when the target moved 8 cm. Death disables the
//    agent entirely — a dead enemy must not hold a NavMesh carve/slot.
// ─────────────────────────────────────────────────────────────────────────────

using FsmFramework;
using UnityEngine;
using UnityEngine.AI;

namespace FsmFramework.AI
{
    /// <summary>
    /// Patrol/chase/attack enemy driven by the pure-C# <see cref="StateMachine"/>.
    /// Owns five nested states (Idle, Patrol, Chase, Attack, Dead), a
    /// centralized sight+hearing perception pass, and full Scene-view gizmos.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        private const string PlayerTag = "Player";

        // Gizmo palette — named, not sprinkled inline (see coding standards).
        private static readonly Color WaypointGizmoColor = Color.yellow;
        private static readonly Color SightGizmoColor = Color.white;
        private static readonly Color HearingGizmoColor = Color.blue;
        private static readonly Color FovGizmoColor = Color.green;

        [Header("Idle")]
        [Tooltip("Seconds the enemy waits in Idle before resuming patrol.")]
        [SerializeField] private float _idleDuration = 2f;

        [Header("Patrol")]
        [Tooltip("Waypoints visited in order, looping. Four in a square for the demo scene.")]
        [SerializeField] private Transform[] _waypoints = new Transform[0];

        [Tooltip("Movement speed while patrolling.")]
        [SerializeField] private float _patrolSpeed = 2f;

        [Tooltip("How close (in units) counts as 'reached the waypoint'.")]
        [SerializeField] private float _waypointTolerance = 0.5f;

        [Header("Sight")]
        [Tooltip("Radius of the sight OverlapSphere.")]
        [SerializeField] private float _sightRange = 10f;

        [Tooltip("Full field-of-view cone angle in degrees (the check is against half of this).")]
        [Range(1f, 360f)]
        [SerializeField] private float _fovAngle = 90f;

        [Tooltip("Layers included in perception overlap tests. Set to the layer(s) the player lives on.")]
        [SerializeField] private LayerMask _detectionLayers = ~0;

        [Header("Hearing")]
        [Tooltip("Radius of the hearing OverlapSphere. No FOV check — ears have no direction.")]
        [SerializeField] private float _hearingRange = 4f;

        [Tooltip("If enabled, the enemy only hears the player while the player is moving (making noise).")]
        [SerializeField] private bool _hearOnlyMovingPlayer = true;

        [Header("Chase")]
        [Tooltip("Movement speed while chasing.")]
        [SerializeField] private float _chaseSpeed = 4.5f;

        [Tooltip("Seconds without seeing OR hearing the player before the chase is abandoned.")]
        [SerializeField] private float _loseTargetDuration = 3f;

        [Tooltip("Seconds between NavMesh re-path requests while chasing (see header, decision #5).")]
        [SerializeField] private float _repathInterval = 0.2f;

        [Header("Attack")]
        [Tooltip("Distance at which the enemy stops chasing and attacks.")]
        [SerializeField] private float _attackRange = 1.8f;

        [Tooltip("Extra distance beyond attack range before dropping back to chase. Prevents flip-flopping on the boundary.")]
        [SerializeField] private float _attackRangeHysteresis = 0.4f;

        [Tooltip("Seconds between attacks.")]
        [SerializeField] private float _attackCooldown = 1.2f;

        [Tooltip("Turn rate in degrees per second while facing the player during attacks.")]
        [SerializeField] private float _attackTurnSpeed = 360f;

        [Header("Gizmos")]
        [Tooltip("Radius of the small yellow sphere drawn at each waypoint.")]
        [SerializeField] private float _waypointGizmoRadius = 0.3f;

        [Tooltip("Height above the enemy's pivot for the state label.")]
        [SerializeField] private float _stateLabelHeight = 2.2f;

        // ── Runtime ─────────────────────────────────────────────────────────
        private StateMachine _machine;
        private StateTimer _stateTimer;
        private NavMeshAgent _agent;
        private int _waypointIndex;

        // Reused buffer for OverlapSphereNonAlloc — zero per-frame garbage.
        private readonly Collider[] _overlapBuffer = new Collider[8];

        // ── Read-only surface for the debug overlay ─────────────────────────

        /// <summary>Name of the active state's type, for display only — never compared.</summary>
        public string CurrentStateName =>
            _machine?.CurrentState != null ? _machine.CurrentState.GetType().Name : "(none)";

        /// <summary>Seconds spent in the current state.</summary>
        public float TimeInState => _stateTimer?.Elapsed ?? 0f;

        /// <summary>Human-readable cause of the most recent transition (diagnostics only).</summary>
        public string LastTransitionReason { get; private set; } = "(initial)";

        /// <summary>True if the player is inside sight range AND the FOV cone this frame.</summary>
        public bool CanSeePlayer { get; private set; }

        /// <summary>True if the player is inside hearing range this frame (no FOV check).</summary>
        public bool CanHearPlayer { get; private set; }

        /// <summary>Last transform detected by sight or hearing. Kept after losing contact so Chase can head to the last known spot.</summary>
        public Transform PlayerTarget { get; private set; }

        /// <summary>True once <see cref="Kill"/> has been called. Dead enemies never transition again.</summary>
        public bool IsDead { get; private set; }

        // ── Unity lifecycle ──────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();

            if (_waypoints == null || _waypoints.Length == 0)
            {
                Debug.LogWarning(
                    $"[EnemyAI] '{name}' has no patrol waypoints assigned — it will ping-pong " +
                    "between Idle and Patrol in place.", this);
            }

            // Build the machine once. States are constructed once and
            // re-entered many times; per-activation resets live in Enter().
            _machine = new StateMachine();
            _machine.AddState(new IdleState(this));
            _machine.AddState(new PatrolState(this));
            _machine.AddState(new ChaseState(this));
            _machine.AddState(new AttackState(this));
            _machine.AddState(new DeadState(this));

            _stateTimer = new StateTimer(_machine);
        }

        private void Start()
        {
            ChangeState<IdleState>("spawned");
        }

        private void Update()
        {
            if (!IsDead)
            {
                UpdatePerception();
            }

            _stateTimer.Tick(Time.deltaTime);
            _machine.Update();
        }

        private void FixedUpdate()
        {
            _machine.FixedUpdate();
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Kills the enemy: transitions to the terminal DeadState, which
        /// disables the NavMeshAgent and stops all behaviour. Idempotent.
        /// </summary>
        public void Kill()
        {
            if (IsDead)
            {
                return;
            }

            IsDead = true;
            ChangeState<DeadState>("killed");
        }

        // ── Perception (see header, decision #3) ─────────────────────────────

        /// <summary>
        /// Computes <see cref="CanSeePlayer"/> and <see cref="CanHearPlayer"/>
        /// once per frame so every state reads consistent values.
        /// </summary>
        private void UpdatePerception()
        {
            CanSeePlayer = false;
            CanHearPlayer = false;

            // ── Sight: OverlapSphere + FOV cone angle check ──────────────────
            int hitCount = Physics.OverlapSphereNonAlloc(
                transform.position, _sightRange, _overlapBuffer, _detectionLayers);

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _overlapBuffer[i];
                if (!hit.CompareTag(PlayerTag))
                {
                    continue;
                }

                Vector3 toPlayer = hit.transform.position - transform.position;
                toPlayer.y = 0f; // FOV is a ground-plane cone; height differences don't blind the enemy.

                if (Vector3.Angle(transform.forward, toPlayer) <= _fovAngle * 0.5f)
                {
                    CanSeePlayer = true;
                    PlayerTarget = hit.transform;
                }

                break; // Only one player in the demo — stop at the first tagged hit.
            }

            // ── Hearing: smaller sphere, no angle check ──────────────────────
            hitCount = Physics.OverlapSphereNonAlloc(
                transform.position, _hearingRange, _overlapBuffer, _detectionLayers);

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _overlapBuffer[i];
                if (!hit.CompareTag(PlayerTag))
                {
                    continue;
                }

                bool audible = true;
                if (_hearOnlyMovingPlayer && hit.TryGetComponent(out Demo.PlayerDummy dummy))
                {
                    audible = dummy.IsMakingNoise; // A stationary player is silent.
                }

                if (audible)
                {
                    CanHearPlayer = true;
                    PlayerTarget = hit.transform;
                }

                break;
            }
        }

        // ── Transition funnel (see header, decision #4) ──────────────────────

        /// <summary>
        /// The single entry point for all transitions. Records the
        /// human-readable reason (for the debug overlay) then delegates to
        /// the type-safe <see cref="StateMachine.SetState{T}"/>.
        /// </summary>
        private void ChangeState<T>(string reason) where T : IState
        {
            LastTransitionReason = reason;
            _machine.SetState<T>();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STATES — nested private classes (see header, decision #1)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shared base: stores the injected owner and provides empty virtual
        /// hooks so concrete states only override what they use.
        /// </summary>
        private abstract class EnemyStateBase : IState
        {
            protected readonly EnemyAI Owner;

            protected EnemyStateBase(EnemyAI owner)
            {
                Owner = owner;
            }

            public virtual void Enter() { }
            public virtual void Update() { }
            public virtual void FixedUpdate() { }
            public virtual void Exit() { }
        }

        /// <summary>
        /// Stand still for a configured duration, then resume patrolling.
        /// Still reacts to the player: seeing or hearing them interrupts
        /// the idle immediately.
        /// </summary>
        private sealed class IdleState : EnemyStateBase
        {
            public IdleState(EnemyAI owner) : base(owner) { }

            public override void Enter()
            {
                Owner._agent.isStopped = true;
                Owner._agent.ResetPath();
            }

            public override void Update()
            {
                if (Owner.CanSeePlayer)
                {
                    Owner.ChangeState<ChaseState>("saw player while idle");
                    return;
                }

                if (Owner.CanHearPlayer)
                {
                    Owner.ChangeState<ChaseState>("heard player while idle");
                    return;
                }

                // StateTimer resets on entry automatically, so this reads as
                // exactly what it means: "idled long enough".
                if (Owner._stateTimer.HasElapsed(Owner._idleDuration))
                {
                    Owner.ChangeState<PatrolState>("idle finished");
                }
            }

            public override void Exit()
            {
                Owner._agent.isStopped = false;
            }
        }

        /// <summary>
        /// Walk the waypoint loop at patrol speed. Reaching a waypoint drops
        /// into Idle (which returns here, creating the patrol rhythm).
        /// Seeing or hearing the player starts a chase.
        /// </summary>
        private sealed class PatrolState : EnemyStateBase
        {
            public PatrolState(EnemyAI owner) : base(owner) { }

            public override void Enter()
            {
                Owner._agent.speed = Owner._patrolSpeed;

                if (Owner._waypoints.Length > 0 && Owner._waypoints[Owner._waypointIndex] != null)
                {
                    Owner._agent.SetDestination(Owner._waypoints[Owner._waypointIndex].position);
                }
            }

            public override void Update()
            {
                if (Owner.CanSeePlayer)
                {
                    Owner.ChangeState<ChaseState>("saw player while patrolling");
                    return;
                }

                if (Owner.CanHearPlayer)
                {
                    Owner.ChangeState<ChaseState>("heard player while patrolling");
                    return;
                }

                if (Owner._waypoints.Length == 0)
                {
                    Owner.ChangeState<IdleState>("no waypoints to patrol");
                    return;
                }

                // Arrived? Advance the loop index (persisted on the owner so
                // the route continues where it left off after a chase) and
                // take a breather in Idle.
                if (!Owner._agent.pathPending &&
                    Owner._agent.remainingDistance <= Owner._waypointTolerance)
                {
                    Owner._waypointIndex = (Owner._waypointIndex + 1) % Owner._waypoints.Length;
                    Owner.ChangeState<IdleState>("reached waypoint");
                }
            }
        }

        /// <summary>
        /// Pursue the player at chase speed, re-pathing on an interval.
        /// Close enough → Attack. No sight or sound for the configured
        /// duration → give up and return to Patrol.
        /// </summary>
        private sealed class ChaseState : EnemyStateBase
        {
            private float _timeSinceContact;
            private float _nextRepathTime;

            public ChaseState(EnemyAI owner) : base(owner) { }

            public override void Enter()
            {
                Owner._agent.speed = Owner._chaseSpeed;
                _timeSinceContact = 0f;
                _nextRepathTime = 0f; // Path immediately on the first Update.
            }

            public override void Update()
            {
                // Contact bookkeeping: any sense resets the give-up clock.
                if (Owner.CanSeePlayer || Owner.CanHearPlayer)
                {
                    _timeSinceContact = 0f;
                }
                else
                {
                    _timeSinceContact += Time.deltaTime;
                }

                if (_timeSinceContact >= Owner._loseTargetDuration)
                {
                    Owner.ChangeState<PatrolState>(
                        $"lost player for {Owner._loseTargetDuration:F0}s");
                    return;
                }

                if (Owner.PlayerTarget == null)
                {
                    return; // Heard something once but never resolved a target — wait out the clock.
                }

                // Throttled re-path (header, decision #5). The agent keeps
                // following its previous path between requests.
                if (Time.time >= _nextRepathTime)
                {
                    _nextRepathTime = Time.time + Owner._repathInterval;
                    Owner._agent.SetDestination(Owner.PlayerTarget.position);
                }

                float distanceToPlayer = Vector3.Distance(
                    Owner.transform.position, Owner.PlayerTarget.position);

                if (distanceToPlayer <= Owner._attackRange)
                {
                    Owner.ChangeState<AttackState>("player in attack range");
                }
            }
        }

        /// <summary>
        /// Hold position, face the player, and strike on a cooldown.
        /// The player escaping past attack range (plus hysteresis) resumes
        /// the chase.
        /// </summary>
        private sealed class AttackState : EnemyStateBase
        {
            private float _cooldownRemaining;

            public AttackState(EnemyAI owner) : base(owner) { }

            public override void Enter()
            {
                Owner._agent.isStopped = true;
                Owner._agent.ResetPath();
                _cooldownRemaining = 0f; // First strike lands immediately.
            }

            public override void Update()
            {
                if (Owner.PlayerTarget == null)
                {
                    Owner.ChangeState<ChaseState>("attack target vanished");
                    return;
                }

                FaceTarget();

                float distanceToPlayer = Vector3.Distance(
                    Owner.transform.position, Owner.PlayerTarget.position);

                // Hysteresis: leaving needs MORE distance than entering did,
                // so hovering on the exact boundary can't cause Chase↔Attack
                // flip-flopping every frame.
                if (distanceToPlayer > Owner._attackRange + Owner._attackRangeHysteresis)
                {
                    Owner.ChangeState<ChaseState>("player left attack range");
                    return;
                }

                _cooldownRemaining -= Time.deltaTime;
                if (_cooldownRemaining <= 0f)
                {
                    _cooldownRemaining = Owner._attackCooldown;

                    // Placeholder strike — a real game raises an event or
                    // triggers an animation + hitbox here.
                    Debug.Log($"[EnemyAI] {Owner.name} attacks the player!", Owner);
                }
            }

            public override void Exit()
            {
                Owner._agent.isStopped = false;
            }

            private void FaceTarget()
            {
                Vector3 toTarget = Owner.PlayerTarget.position - Owner.transform.position;
                toTarget.y = 0f;

                if (toTarget.sqrMagnitude < Mathf.Epsilon)
                {
                    return;
                }

                Quaternion desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                Owner.transform.rotation = Quaternion.RotateTowards(
                    Owner.transform.rotation, desired, Owner._attackTurnSpeed * Time.deltaTime);
            }
        }

        /// <summary>
        /// Terminal state. Disables the NavMeshAgent so the corpse releases
        /// its avoidance slot, and defines no outgoing transitions — dead
        /// stays dead.
        /// </summary>
        private sealed class DeadState : EnemyStateBase
        {
            public DeadState(EnemyAI owner) : base(owner) { }

            public override void Enter()
            {
                if (Owner._agent.enabled)
                {
                    Owner._agent.isStopped = true;
                    Owner._agent.ResetPath();
                    Owner._agent.enabled = false;
                }

                Debug.Log($"[EnemyAI] {Owner.name} died.", Owner);
            }

            // No Update, no exits: a terminal state is defined by what it
            // deliberately does NOT implement.
        }

        // ═════════════════════════════════════════════════════════════════════
        //  EDITOR GIZMOS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws the enemy's full sensory and patrol picture when selected:
        /// yellow waypoint loop, white sight sphere, blue hearing sphere,
        /// green FOV cone, and a coloured state label above the head.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            DrawWaypointGizmos();

            // Sight range — white.
            Gizmos.color = SightGizmoColor;
            Gizmos.DrawWireSphere(transform.position, _sightRange);

            // Hearing range — blue.
            Gizmos.color = HearingGizmoColor;
            Gizmos.DrawWireSphere(transform.position, _hearingRange);

            DrawFovGizmo();
            DrawStateLabel();
        }

        /// <summary>Yellow spheres at each waypoint plus lines closing the loop.</summary>
        private void DrawWaypointGizmos()
        {
            if (_waypoints == null || _waypoints.Length == 0)
            {
                return;
            }

            Gizmos.color = WaypointGizmoColor;

            for (int i = 0; i < _waypoints.Length; i++)
            {
                if (_waypoints[i] == null)
                {
                    continue;
                }

                Gizmos.DrawWireSphere(_waypoints[i].position, _waypointGizmoRadius);

                // Line to the next waypoint, wrapping so the loop closes.
                Transform next = _waypoints[(i + 1) % _waypoints.Length];
                if (next != null)
                {
                    Gizmos.DrawLine(_waypoints[i].position, next.position);
                }
            }
        }

        /// <summary>
        /// Green FOV cone: the two edge rays plus an arc across the far end,
        /// approximated with straight segments (Gizmos has no arc primitive).
        /// </summary>
        private void DrawFovGizmo()
        {
            const int arcSegments = 24;

            Gizmos.color = FovGizmoColor;

            float halfFov = _fovAngle * 0.5f;
            Vector3 origin = transform.position;

            Vector3 previousPoint = origin +
                Quaternion.Euler(0f, -halfFov, 0f) * transform.forward * _sightRange;

            // Left edge of the cone.
            Gizmos.DrawLine(origin, previousPoint);

            for (int i = 1; i <= arcSegments; i++)
            {
                float angle = -halfFov + _fovAngle * (i / (float)arcSegments);
                Vector3 point = origin +
                    Quaternion.Euler(0f, angle, 0f) * transform.forward * _sightRange;

                Gizmos.DrawLine(previousPoint, point);
                previousPoint = point;
            }

            // Right edge of the cone (previousPoint is now the last arc point).
            Gizmos.DrawLine(origin, previousPoint);
        }

        /// <summary>
        /// Coloured state name above the enemy's head via Handles.Label.
        /// Editor-only API, so the whole method compiles away in builds.
        /// </summary>
        private void DrawStateLabel()
        {
#if UNITY_EDITOR
            var style = new GUIStyle
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = GetStateLabelColor() }
            };

            UnityEditor.Handles.Label(
                transform.position + Vector3.up * _stateLabelHeight,
                Application.isPlaying ? CurrentStateName : "EnemyAI (not playing)",
                style);
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Type-pattern colour lookup — even the debug label obeys the
        /// "no string-based state comparisons" rule.
        /// </summary>
        private Color GetStateLabelColor()
        {
            return _machine?.CurrentState switch
            {
                IdleState => Color.gray,
                PatrolState => Color.yellow,
                ChaseState => new Color(1f, 0.5f, 0f), // orange
                AttackState => Color.red,
                DeadState => Color.black,
                _ => Color.white
            };
        }
#endif
    }
}
