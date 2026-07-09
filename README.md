# Unity-FSM-Framework

A **pure-C#, type-safe finite state machine** for Unity — zero Unity dependencies in the core, so it drops into any project (or any C# codebase) unchanged — demonstrated by a complete patrol/chase/attack enemy AI with sight and hearing perception, NavMesh movement, and full editor gizmos.

Built for Unity 6 (6000.x). The FSM core itself has no Unity version requirement at all.

---

## Architecture

```
                       ┌────────────────────────────────┐
                       │   StateMachine   (pure C#)     │
                       │────────────────────────────────│
                       │  Dictionary<Type, IState>      │
                       │  AddState(IState)              │
                       │  SetState<T>()   ── type-safe  │
                       │  Update() / FixedUpdate()      │
                       │  CurrentState                  │
                       │  event OnStateChanged(old,new) │
                       └───────────────┬────────────────┘
                                       │ drives
                                       ▼
                       ┌────────────────────────────────┐          ┌───────────────────┐
                       │      IState    (pure C#)       │◄─resets──│    StateTimer     │
                       │  Enter() Update()              │          │  Elapsed          │
                       │  FixedUpdate() Exit()          │          │  HasElapsed(s)    │
                       └───────────────┬────────────────┘          │  Tick(deltaTime)  │
                                       │ implemented by            └───────────────────┘
              ┌────────────┬───────────┼────────────┬─────────────┐
              ▼            ▼           ▼            ▼             ▼
        ┌──────────┐ ┌───────────┐ ┌──────────┐ ┌───────────┐ ┌──────────┐
        │IdleState │ │PatrolState│ │ChaseState│ │AttackState│ │DeadState │
        └────┬─────┘ └─────┬─────┘ └────┬─────┘ └─────┬─────┘ └────┬─────┘
             └─────────────┴────────────┴─────────────┴────────────┘
                          nested private classes inside
                       ┌────────────────────────────────┐
                       │    EnemyAI   (MonoBehaviour)   │
                       │  owns machine + timer + agent  │
                       │  perception: sight + hearing   │
                       │  forwards Update/FixedUpdate   │
                       └────────────────────────────────┘

Transitions:   Idle ──2s──▶ Patrol ──see/hear──▶ Chase ──in range──▶ Attack
                ▲              ▲                   │  ▲                │
                └─reached wp───┘◄────lost 3s───────┘  └──out of range─┘
                                     Kill() from anywhere ──▶ Dead (terminal)
```

---

## What This Demonstrates

| Concept | Where in code |
|---|---|
| Pure C# FSM core — zero `UnityEngine` references, portable & unit-testable | [`StateMachine.cs`](Assets/Scripts/FSM/StateMachine.cs), [`IState.cs`](Assets/Scripts/FSM/IState.cs), [`StateTimer.cs`](Assets/Scripts/FSM/StateTimer.cs) |
| Type-keyed states (`Dictionary<Type, IState>`, `SetState<T>()`) — no strings, no enums to sync | `StateMachine.cs` |
| Transition ordering guarantee (Exit → swap → Enter → event) | `StateMachine.SetState<T>()` |
| Observable transitions (`event Action<IState, IState> OnStateChanged`) | `StateMachine.cs`; consumed by `StateTimer` |
| Self-resetting "time in state" helper for timed rules | `StateTimer.cs` (auto-resets via `OnStateChanged`) |
| Five-state enemy AI as nested private classes with injected owner | [`EnemyAI.cs`](Assets/Scripts/AI/EnemyAI.cs) |
| Centralized perception: sight = `OverlapSphere` + `Vector3.Angle` FOV cone; hearing = smaller sphere, no angle | `EnemyAI.UpdatePerception()` |
| Zero-allocation physics queries (`OverlapSphereNonAlloc` + reused buffer) | `EnemyAI.cs` |
| NavMesh patrol/chase with throttled re-pathing | `PatrolState` / `ChaseState` in `EnemyAI.cs` |
| Chase↔Attack boundary hysteresis (no flip-flop on the range edge) | `AttackState` in `EnemyAI.cs` |
| Terminal state design (DeadState has no exits, releases its NavMesh slot) | `DeadState` in `EnemyAI.cs` |
| Transition-reason diagnostics without string-based logic | `EnemyAI.ChangeState<T>(reason)` |
| Full editor gizmos: waypoint loop, sight/hearing ranges, FOV cone, coloured state label | `EnemyAI.OnDrawGizmosSelected()` |
| Zero-setup debug overlay (IMGUI) | [`DebugOverlay.cs`](Assets/Scripts/Demo/DebugOverlay.cs) |
| XML documentation on the entire public FSM API | `StateMachine.cs`, `StateTimer.cs`, `IState.cs` |

---

## Why Not Animator?

Unity's Animator is often (mis)used as a general-purpose state machine. For gameplay logic, a code-driven FSM wins on every axis that matters:

1. **Diffable and reviewable.** An Animator controller is a binary-ish YAML blob; a merge conflict in one is a rebuild. This FSM is plain C# — every transition change is a readable line in a pull request.

2. **Debuggable.** You can put a breakpoint inside `ChaseState.Update()`, inspect every variable, and step through a transition. Try breakpointing an Animator transition arrow.

3. **Type-safe.** Animator states and parameters are addressed by string (`SetTrigger("Atack")` compiles fine and fails silently at runtime). Here, `SetState<AttackState>()` doesn't compile if the state doesn't exist.

4. **No hidden execution model.** Animator transitions have interruption sources, exit times, blend durations, and evaluation-order rules that interact in famously surprising ways. This machine has one rule: Exit, swap, Enter, event. Read `SetState<T>()` — that's the whole model, 20 lines.

5. **Testable.** The core never touches `UnityEngine`, so plain NUnit tests can drive it: add states, tick synthetic time through `StateTimer.Tick()`, assert transitions — no editor, no play mode, no frame loop.

6. **Right tool separation.** The Animator is excellent at what it's actually for — blending animation clips. The clean pattern is: gameplay FSM decides *what* the character does; it pokes the Animator to show it. `AttackState.Enter()` calling `animator.SetTrigger(AttackHash)` is fine. Gameplay *decisions* living inside Animator states is how projects end up unshippable.

---

## Scene Setup (step by step)

The repository contains the scripts plus minimal project files. Building the demo scene takes ~10 minutes.

### 0. Open the project

1. **Unity Hub → Add → Add project from disk**, select the `Unity-FSM-Framework` folder, open with **Unity 6** (6000.x). The **AI Navigation** package is already declared in `Packages/manifest.json` and installs automatically.
2. **File → New Scene** (Basic 3D), save as `Assets/Scenes/FsmDemo.unity`.

### 1. Ground + NavMesh

1. **GameObject → 3D Object → Plane**, rename `Ground`, Position `(0, 0, 0)`, Scale `(5, 1, 5)`.
2. With `Ground` selected: **Add Component → NavMesh Surface** (from the AI Navigation package).
3. On the NavMesh Surface component click **Bake**. A blue overlay appears on the plane (toggle visibility with the scene-view AI Navigation overlay if needed).

### 2. Patrol waypoints (square)

1. **GameObject → Create Empty**, rename `Waypoints`, Position `(0, 0, 0)`.
2. Create four empty children of `Waypoints`, named `WP_0` … `WP_3`, positioned in a square:

   | Waypoint | Position |
   |---|---|
   | `WP_0` | `(-8, 0, -8)` |
   | `WP_1` | `( 8, 0, -8)` |
   | `WP_2` | `( 8, 0,  8)` |
   | `WP_3` | `(-8, 0,  8)` |

### 3. Player dummy

1. **GameObject → 3D Object → Capsule**, rename `PlayerDummy`, Position `(0, 1, 12)` — outside the patrol square so the enemy starts unaware.
2. In the Inspector's **Tag** dropdown, set the tag to **Player** (it's a built-in tag — no need to create it).
3. **Add Component → Player Dummy** (script). Move Speed `5`, Turn Speed `720`.
4. Keep the default Capsule Collider (the perception OverlapSphere needs a collider to find).
5. (Optional) A bright material so you can spot yourself.

### 4. Enemy

1. **GameObject → 3D Object → Capsule**, rename `Enemy`, Position `(-8, 1, -8)` (on the first waypoint).
2. **Add Component → Nav Mesh Agent.** Defaults are fine (Speed is overridden by the states).
3. **Add Component → Enemy AI** (script). Set fields:

   | Field | Value |
   |---|---|
   | Idle Duration | `2` |
   | Waypoints | Size `4` → drag `WP_0`, `WP_1`, `WP_2`, `WP_3` in order |
   | Patrol Speed | `2` |
   | Waypoint Tolerance | `0.5` |
   | Sight Range | `10` |
   | Fov Angle | `90` |
   | Detection Layers | `Everything` (or just the player's layer) |
   | Hearing Range | `4` |
   | Hear Only Moving Player | ✔ |
   | Chase Speed | `4.5` |
   | Lose Target Duration | `3` |
   | Repath Interval | `0.2` |
   | Attack Range | `1.8` |
   | Attack Range Hysteresis | `0.4` |
   | Attack Cooldown | `1.2` |
   | Attack Turn Speed | `360` |

4. (Optional) Drag `Enemy` into `Assets/Prefabs/` to make it a prefab — waypoint references are scene objects, so re-assign them on scene instances.

### 5. Debug overlay

1. **GameObject → Create Empty**, rename `DebugOverlay`. **Add Component → Debug Overlay.**
2. Assign **Enemy** = the `Enemy` object. Kill Key `K`.

### 6. Camera

Select **Main Camera**, Position `(0, 22, -16)`, Rotation `(55, 0, 0)` — a comfortable overview of the whole patrol square.

### 7. Play

Press **Play**:

- The enemy idles 2 s, then patrols the yellow square (select it in the Scene view to watch the gizmos and the coloured state label live).
- Walk into the white sight ring *inside* the green cone → **Chase**.
- Sneak up *behind* the enemy while stationary — nothing. Move inside the blue ring → heard → **Chase**.
- Let it reach you → **Attack** (Console logs the strikes on cooldown).
- Run away → **Chase**, stay hidden 3 s → back to **Patrol**.
- Press **K** → **Dead**, permanently.

The overlay shows the state, time-in-state, why the last transition fired, and live sight/hearing indicators throughout.

---

## Dropping This FSM Into Any Project (3 steps)

1. **Copy one folder.** `Assets/Scripts/FSM/` — three files (`IState`, `StateMachine`, `StateTimer`), no dependencies on anything else in this repo, no `UnityEngine` references at all.

2. **Write states, wire a machine.** In any class (MonoBehaviour or not):

   ```csharp
   _machine = new StateMachine();
   _machine.AddState(new OpeningState(this));
   _machine.AddState(new ClosedState(this));
   _machine.SetState<ClosedState>();
   _timer = new StateTimer(_machine);   // optional
   ```

3. **Forward the heartbeat.** From the owner:

   ```csharp
   void Update()
   {
       _timer.Tick(Time.deltaTime);
       _machine.Update();
   }
   void FixedUpdate() => _machine.FixedUpdate();
   ```

That's the entire integration surface. Doors, game-flow (menu/loading/playing/paused), bosses, network session handling — anything with modes fits.

---

## Key Design Decisions

Each source file carries a fuller design block in its header; the highlights:

### `Dictionary<Type, IState>` — the type *is* the key
String keys typo silently (`"Chse"`), survive renames wrongly, and defeat refactoring tools. Enum keys demand a hand-synced parallel list. Using `System.Type` means the state *class* is its own identity: `SetState<PatrolState>()` is checked by the compiler, "find all usages" works, and renaming a state renames every reference. Lookup cost is one dictionary hit per transition — transitions happen a few times per second, not per frame.

### Nested private classes vs separate files
These five states exist solely to drive `EnemyAI` — they're implementation detail. Nesting gives them access to the owner's private fields (no leaky public accessors), keeps the behaviour readable as one document, and makes it impossible for outside code to depend on `PatrolState`. **The rule of thumb:** states owned by exactly one component → nested private; states shared across many AI types → separate files talking to a context interface (e.g. `IEnemyContext`) instead of a concrete owner. This project deliberately shows the first pattern; the second is the natural refactor when a second enemy type appears.

### Why `StateTimer` earns its existence
Nearly every state has a timed rule — idle 2 s, give up after 3 s, cooldown 1.2 s. Without a shared helper, each state grows its own `_elapsed += Time.deltaTime` field, and *someone forgets to reset one in `Enter()`* — the single most common FSM bug, and it only manifests on the second visit to the state. `StateTimer` subscribes to `OnStateChanged` and resets itself, so "time in current state" is correct by construction. Time is injected through `Tick(deltaTime)` rather than read from `UnityEngine.Time`, which keeps the core pure and lets unit tests fast-forward synthetic time.

### Exceptions, not `Debug.LogError`, in the core
A pure C# core can't reference Unity's logger — and shouldn't want to. Setting an unregistered state or double-registering a type is a programming error; throwing makes it impossible to miss during development, and the message says exactly which state and what to do.

### Transition reasons are diagnostics, not logic
`ChangeState<T>(string reason)` records *why* for the overlay ("heard player while patrolling"). The string is written and displayed, never compared — all branching is on types. This gives human-readable debugging without reintroducing stringly-typed state logic.

### Hysteresis on the Attack↔Chase boundary
Entering attack at `range` but only leaving at `range + 0.4` means a player strafing exactly on the boundary can't cause an Enter/Exit storm every frame — a small detail that separates FSMs that feel solid from ones that jitter.

---

## Controls

| Input | Action |
|---|---|
| **W / A / S / D** | Move the player dummy |
| **K** | Kill the enemy (demonstrates terminal DeadState) |
| *(stand still)* | Go silent — sneak through the hearing ring |

---

## Project Structure

```
Unity-FSM-Framework/
├── Assets/
│   └── Scripts/
│       ├── FSM/                      ← the portable core (copy this folder)
│       │   ├── IState.cs             # Enter/Update/FixedUpdate/Exit contract
│       │   ├── StateMachine.cs       # Type-keyed machine, pure C#
│       │   └── StateTimer.cs         # Self-resetting time-in-state helper
│       ├── AI/
│       │   └── EnemyAI.cs            # 5 nested states + perception + gizmos
│       └── Demo/
│           ├── PlayerDummy.cs        # WASD target, noise source for hearing
│           └── DebugOverlay.cs       # IMGUI: state, timer, reason, senses
├── Packages/manifest.json            # includes com.unity.ai.navigation
├── ProjectSettings/ProjectVersion.txt
├── .gitignore
└── README.md
```

---

## .gitignore

The included [.gitignore](.gitignore) is the standard Unity set: `Library/`, `Temp/`, `Logs/`, `obj/`, `Build(s)/`, `UserSettings/`, IDE caches, and OS junk excluded; `Assets/`, `Packages/`, and `ProjectSettings/` (with `.meta` files, via the `!/Assets/**/*.meta` keep-rule) are what belongs in version control.
