# 02 - PredictionBehaviour API (what a user writes)

**Summary.** A predicted object is a Mirage `NetworkBehaviour` that derives from `PredictionBehaviour<TInput,TState>` (player-controlled) or `PredictionBehaviour<TState>` (no input). The user supplies an input struct, an **unmanaged state struct that lives in CSP-owned unmanaged memory**, and hand-written glue that copies state out of Unity objects after each tick (`AfterTick`) and back into them after a rollback (`AfterStateChanged`). Example3 (move, look, shoot, health, bullet, moving target) shows the intended style, but its shooting/health handling is server-authoritative and **not** truly predicted, and contains several example-level bugs.

## 1. Type hierarchy

```
Mirage.NetworkBehaviour
 └─ SnapshotBehaviour<TState>  where TState : unmanaged        (DeltaSnapshot/SnapshotBehaviour.cs:41)
     └─ PredictionBehaviourBase<TInput,TState>  : IPredictionBehaviour   (PredictionBehaviour.cs:38)
         ├─ PredictionBehaviour<TInput,TState>   HasInput => true                     (PredictionBehaviour.cs:33-36)
         └─ PredictionBehaviour<TState>          TInput = NoValues; HasInput => false (PredictionBehaviour.cs:21-27)
                                                 GetInput/MissingInput/ApplyInputs throw NotSupported (sealed)
IPredictionUpdates (Order, PredictionTime, InputUpdate, NetworkFixedUpdate, VisualUpdate)   (Interfaces.cs:126-140)
 └─ IPredictionBehaviour (+ ISnapshotBehaviour: HasInput, AfterStateChanged, AfterTick, Server/ClientSetup, CleanUp, controllers)  (Interfaces.cs:142-166)
```

`IPredictionUpdates` can also be implemented by any `NetworkBehaviour` that has **no state** and only wants to be called in tick order (`ClientInterpolation` and the UniTask timing helper do this; `PredictionCollection.Add(identity)` picks up any `NetworkBehaviour` implementing it, `PredictionCollection.cs:29-40,73`). Such objects get only `NetworkFixedUpdate()` during simulate/resim (`ClientManager.cs:358-362`, `ServerManager.cs:165-169`).

## 2. Type parameters

### `TInput`
- Any struct that Mirage's weaver can serialize (examples tag it `[NetworkMessage]`, optionally with `[BitCountFromRange(-1,1)]` for 2-bit axes, `Assets/Example2/PredictionExample2.cs:143-146`). It is written with `writer.Write(input)` (`ClientController.cs:104`) and read with `reader.Read<TInput>()` (`ServerController.cs:49`).
- **Not** required to be unmanaged. It is held in a managed `NullableRingBuffer<TInput>` of 64 entries per behaviour (`ClientController.cs:34-42`, `ServerController.cs:37-45`).
- `NetworkInputs<TInput>` is a readonly pair `{Current, Previous}` (`Assets/source/Runtime/NetworkInputs.cs:12-22`): the previous tick's input is supplied so the user can detect button *presses* (`Current.Jump && !Previous.Jump`).

### `TState` (the severe constraint)
- `where TState : unmanaged` is on every generic (`SnapshotBehaviour.cs:41`, `PredictionBehaviour.cs:21,33,38`).
- The struct is used as **raw memory**: compared with `memcmp`, copied with `memcpy`, delta-encoded as `int[]`, and shipped inside `DeltaWorldState` with no per-field serialization. Hence (details in file 04):
  - no `bool` - use `NetworkBool` (1 byte, `Utils/NetworkBool.cs:20-35`);
  - no references, strings, arrays, enums with platform-dependent size are risky;
  - deterministic layout: examples use `[StructLayout(LayoutKind.Explicit, Size = N)]` with explicit `[FieldOffset]` (`Example1/PredictionExample1.cs:113-118`, `Example2/PredictionExample2.cs:155-161`, `Example3/Scripts/PlayerMove.cs:108-114`). `Update-beta5.md` ("State Object (changed)") states this is required and that the size is rounded up to 32 bits.
  - `AllocationSizeInts = (sizeof(TState) + 3) / 4` (`SnapshotBehaviour.cs:86`).
  - all fields are compared bitwise, so `-0f` vs `0f`, NaNs and padding bytes are "changes".
- `PredictionBehaviour<TState>` uses `NoValues` as `TInput`, an explicit-layout struct with `Size = 0` (`Utils/NoValues.cs:18-19`).

## 3. Members the user touches

| Member | Kind | Called by / when | Notes |
|---|---|---|---|
| `Order` | virtual `int` (0) | `PredictionCollection` sorts by it (`PredictionCollection.cs:195-210`) | Lower runs first in `NetworkFixedUpdate`/`ApplyInputs`/`InputUpdate`/`VisualUpdate`. `List.Sort` is unstable, so equal orders have no guaranteed relative order. Example3 uses it explicitly: `PlayerLook.ORDER = 0`, `PlayerMove.ORDER = Look + 1` (`PlayerLook.cs:10-11`, `PlayerMove.cs:10-11`). |
| `HasInput` | abstract (sealed by the two base classes) | `UseInputs()` | Choose by choosing the base class. |
| `GetInput()` | abstract (input variant) | once per client tick for objects where `UseInputs()` (owner client) and in host mode | Must return the input for *this tick*. Called from `ClientController.InputTick` (`ClientController.cs:86-99`) after the per-frame `InputUpdate`. |
| `MissingInput(prev, prevTick, curTick)` | virtual | server, when no input was received for a tick | Default returns the previous valid input (`PredictionBehaviour.cs:84-93`). |
| `ApplyInputs(NetworkInputs<TInput>)` | abstract (input variant) | server and owner client, **before** `NetworkFixedUpdate`, every tick and every resim step | Where input is turned into forces/velocity. |
| `InputUpdate()` | virtual | once per Unity frame before ticks, on client (all behaviours incl. remote) and host (`PredictionManager.cs:203-207,275-295`) | Poll Unity input and accumulate into a field for `GetInput`. |
| `NetworkFixedUpdate()` | virtual | every tick on server and client for **all** behaviours (owned or remote), and every resim step | Game logic that mutates state (gravity, custom drag, AI). Must be deterministic. |
| `VisualUpdate()` | virtual | once per frame after ticks, client/host | Rendering/animation. |
| `AfterTick()` | virtual | after `Physics.Simulate` each tick/resim step | **Copy Unity state -> `State`** (e.g. `State.Position = _body.position`). Forgetting this silently desyncs. |
| `AfterStateChanged()` | virtual | client only: start of every resim (server state applied), and again from `AfterResimulate` | **Copy `State` -> Unity objects** (`_body.position = State.Position` ...). |
| `EnableResimulationTransition` | virtual bool (true) | `ClientController` | Set false to avoid capturing before/after state per resim (`ClientController.cs:44-68`). |
| `ResimulationTransition(before, after)` | virtual, returns new state | after resim, client | Smoothing hook (file 05). Default returns `after`. |
| `OnPredictionSetup` | `IAddLateEvent` | end of `ServerSetup`/`ClientSetup` (`PredictionBehaviour.cs:162-174`) | The first moment `State` and `PredictionTime` are valid. Reset on `CleanUp`. |
| `State` | `protected ref TState` | anywhere after setup | Reference into the *currently active tick slot*; throws `SnapshotException` if the pointer is null (`SnapshotBehaviour.cs:50-59`). |
| `StatePtr`, `HasState`, `GetStateAtTick(int)` | public | user/interpolation | Raw pointer access to any ring slot (`SnapshotBehaviour.cs:43-66`). `GetStateAtTick` does not say whether that slot actually contains data for that tick. |
| `IsRemoteClient` | bool | user | `!IsServer && IsClient && !HasAuthority` (`PredictionBehaviour.cs:62`) |
| `PredictionTime` | `IPredictionTime` | user | Set by the collection when added (`PredictionCollection.cs:87`) and nulled on `CleanUp` (`PredictionBehaviour.cs:178`); using it in `Awake` would be a null reference. |

### `UseInputs()` rule (who is "controlled")
`HasInput` and (server: `Owner != null`; client: `HasAuthority`) (`PredictionBehaviour.cs:195-216`). Consequences: un-owned objects can never take input; input-less behaviours and remote clients only run `NetworkFixedUpdate`. `IsRemoteClient` is the only helper for "I'm a puppet": user code must check it (or `IsServer`) inside `NetworkFixedUpdate` to avoid simulating things the client cannot know (as `MovingTarget` does with `if (!IsServer) return;`, `Example3/Scripts/MovingTarget.cs:23-24`).

### `IPredictionTime` (the time object given to user code)
`FixedDeltaTime`, `Tick`, `FixedTime (= Tick*dt)`, `Time`/`DeltaTime` (frame-level, scaled), `UnscaledTime`, `IsResimulation`, `Method` (`Interfaces.cs:24-77`; implemented by `PredictionTime`, `TickRunner.cs:17-35`).
- `Tick`/`FixedTime`/`IsResimulation` are the ones that rewind during resim (`ClientManager.cs:329-334`).
- **Doc/implementation mismatch:** `UnscaledTime` is documented as rewinding during resim (`Interfaces.cs:31-37`) but is implemented as wall-clock `stopwatch.Elapsed` (`TickRunner.cs:27,144-148`). `Time`/`DeltaTime` are per-frame values and also do not rewind.
- `Method` (`None/NetworkFixed/Input/Visual`) is set around each phase (`PredictionManager.cs:279,298`, `ServerManager.cs:143`, `ClientManager.cs:382`) but nothing in the library asserts on it.
- `IsResimulation` is the flag user code needs to avoid replaying side effects (sounds, spawns); the examples do not use it (`grep IsResimulation` in `Assets/Example*` is empty), and Example3's `Shoot()` suffers for it (section 6).

## 4. Simulation / physics modes

- `IPredictionSimulation { void Simulate(float fixedDelta); }` (`Interfaces.cs:19-22`). Called exactly once per tick after all `ApplyInputs`/`NetworkFixedUpdate`, on both sides, and once per resim step on the client (`ServerManager.cs:172`, `ClientManager.cs:364`).
- `SimulationMode { Physics3D, Physics2D, Local3D, Local2D }` (`Messages.cs:68-74`, selected on the `PredictionManager`, `PredictionManager.cs:44-45`).
- `DefaultPredictionSimulation` (`Simulations/DefaultPredictionSimulation.cs:194-282`):
  - `Physics3D`/`Physics2D`: sets the **global** `Physics.simulationMode = Script` (`Physics2D.simulationMode = SimulationMode2D.Script`) and calls `Physics.Simulate(dt)` / `Physics2D.Simulate(dt)`.
  - `Local3D`/`Local2D`: uses the `PhysicsScene` of the scene containing the `PredictionManager` (`scene.GetPhysicsScene()`); this is what lets server and client share one Unity process (Single Instance debug start loads the scene twice with local physics, `Debugging/SingleInstanceDebugStart.cs:109,140`).
  - `SetPredictionSimulation(sim)` replaces it, but only before the server/client have started (`PredictionManager.cs:74-80`). README: "Giving a `DefaultPredictionSimulation` will allow you to use local physics in a scene that the `PredictionManager` is not in."
- Anything that moves during `Simulate` but is not a `SnapshotBehaviour` is **not** saved/restored by rollback (see file 05).

## 5. Minimal example (Example1, condensed)

`Assets/Example1/PredictionExample1.cs` - a Rigidbody cube moved left/right/jump:

```csharp
public class PredictionExample1 : PredictionBehaviour<InputState, ObjectState>
{
    public override InputState GetInput() => new InputState(Input.GetKey(D), Input.GetKey(A), Input.GetKey(Space)); // :66-73

    public override void ApplyInputs(NetworkInputs<InputState> inputs)                                             // :29-40
    {
        var move = inputs.Current.Horizontal * new Vector3(1, .25f, 0);
        body.AddForce(speed * move, ForceMode.Acceleration);
        if (inputs.Current.jump && !inputs.Previous.jump) body.AddForce(Vector3.up * 10, ForceMode.Impulse);
    }
    public override void NetworkFixedUpdate() { /* gravity tweaks on body */ }                                    // :42-51
    public override void AfterTick()         { State.Position = body.position; State.Velocity = body.velocity; }  // :53-57
    public override void AfterStateChanged() { body.position = State.Position; body.velocity = State.Velocity; } // :59-63
}
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct ObjectState { [FieldOffset(0)] public Vector3 Position; [FieldOffset(12)] public Vector3 Velocity; } // :113-118
```

Example2 adds a `Quaternion`/angular velocity state (52 bytes) and a smoothing `ResimulationTransition` that lerps 10% toward the corrected state per resim (`PredictionExample2.cs:59-68`; same in `PredictionRigidBody.cs:35-44`). Both examples also implement debug-only interfaces `IDebugPredictionLocalCopy` and (Example2) `IDebugPredictionAfterImage` (file 08).

## 6. Example3 (FPS-ish): how it uses the API

Setup (`Assets/Example3/Example3Scene.unity:1215-1230`): `PredictionManager` with `PhysicsMode: 0` (Physics3D), `TickRate: 50`, `PingInterval: 0.2`, client settings `diffThreshold 0.5`, `skipThreshold 10`, `movingAverageCount 30`. Player prefab = Rigidbody + `PlayerMove` + `PlayerLook` + `Shooter` + `GroundChecker` + `FollowTarget`; Target prefab = `MovingTarget` + `Health` + `FlashTargetOnHit`; **Bullet prefab = plain Rigidbody + `Bullet` script with no `NetworkIdentity`** (checked by grepping the prefab YAML for the script GUIDs).

### Input gathering (`PlayerInputBase.cs`, `PlayerMove.cs`, `PlayerLook.cs`)
- Each player instance (including remote ones) creates its own `InputActions` (`PlayerInputBase.cs` `Awake`), and every instance's `InputUpdate` reads the *local* keyboard/mouse; only the owner's values are ever sent.
- Pattern: `InputUpdate` (per frame) accumulates into `_networkInput` (`Jump |= pressed`, `Shoot |= pressed`, `Move = latest`, `Yaw/Pitch = current look`); `GetInput` (per tick) returns the accumulator and **resets it to `default`** (`PlayerInputBase.cs:27-32`).
- Side effect: if two ticks run in one frame, the second gets `default` for *everything*, including `Move`, `Yaw` and `Pitch` (not just the edge-triggered buttons the code comment worries about, `PlayerLook.cs:49-50`). Not verified at runtime.

### Movement (`PlayerMove`)
- Input: `{NetworkBool Jump, Vector2 Move}`; state: `{Vector3 Position (0), Vector3 Velocity (12), float GroundedTimer (40)}` in an explicit 44-byte struct (`PlayerMove.cs:101-114`).
- `ApplyInputs` (`:42-60`): set body yaw from the head's yaw, set horizontal velocity by lerp toward `Move * speed`, update the grounded timer **in place** through `ref State.GroundedTimer` (`:55`), and jump when `Jump && !Previous.Jump` and grounded.
- `AfterTick` copies `Rigidbody.position/velocity` into `State` (`:95-99`); `AfterStateChanged` copies them back (`:90-94`). Rotation is not in the state; it is rebuilt from the head each tick.

### Look and shoot (`PlayerLook`)
- Input: `{NetworkBool Shoot, float Yaw, float Pitch}` (`:105-112`). `ApplyInputs` (`:86-97`) sets `_head.rotation` and, if `Shoot && !Previous.Shoot`, calls `_shooter.Shoot()`.
- **Example bug:** `ApplyInputs` builds the head rotation from the behaviour's local `_pitch/_yaw` fields (`:90`), not from `inputs.Current.Pitch/Yaw`. Those fields are only updated in client `InputUpdate`, so on the server (and for any historical tick during resim) they do not reflect the received input. `// todo only apply here if not local` comments (`PlayerLook.cs:88`, `PlayerMove.cs:44`) show the author knew the example was unfinished.
- `PlayerLook.NetworkState` is an *empty* struct annotated `Size = 44` (`:114-117`), so every player carries 11 ints of zeros in snapshots and in each world-state message (they delta to nothing but still cost memory and a run-length entry).

### Shooting and hit detection (`Shooter`, `Bullet`)
- `Shooter.Shoot()` (`Shooter.cs:16-24`): `Instantiate(_bullet)`, set Rigidbody position/velocity from the gun transform (`ApplyForce`, `:26-31`), store `bullet.Shooter`.
- `Bullet.OnCollisionEnter` (`Bullet.cs:9-20`): `if (!Shooter.IsServer) return;` then `health.Harm(Shooter.Damage)` and `Destroy(gameObject)`.
- So the bullet is a **non-networked projectile simulated by Unity physics on every side that spawned it**. The server's copy is authoritative for damage. The client also spawns one (because `ApplyInputs` runs on the client for the owner), but it only ever bounces: the early `return` for non-servers means client bullets are never destroyed (`Bullet.cs:11-12`).
- Because `Shoot()` is called from `ApplyInputs`, **every resimulation that replays a tick with the shoot button spawns another bullet** on the client; bullets are not in any snapshot and not rolled back. `IsResimulation` is not checked. (Inferred from reading; this is the example's biggest "what not to do" for FPS weapons.)
- There is no hitscan, no client-side hit prediction, and no lag compensation: damage is decided at the server tick when the bullet's collider touches the target's collider (using server-time positions), and the client learns about it only through the next state message.

### Health (`Health : PredictionBehaviour<NetworkState>`)
- State `{float Health}` (`Health.cs:56-59`), no input. `OnPredictionSetup` initialises `State.Health` and a local `_clientHealth` (`:13-20`) on both sides.
- `Harm(value)` (`:27-33`) asserts `IsServer`, decrements `State.Health` **in place** (this runs inside `Physics.Simulate` -> `OnCollisionEnter`, so `State` points at the current tick's slot and the change is captured into that tick's world state and sent), then fires `OnHarm` and `OnDeath` (`< 0`, not `<= 0`).
- Client side: health changes arrive only via server state. `AfterStateChanged` (`:47-52`) calls `CallEvents()` when `_clientHealth != State.Health`, but `_clientHealth` is never updated after `Setup`, so once health has changed, `OnHarm`/`OnDeath` fire **on every `AfterStateChanged`** (twice per resim per behaviour; see file 05). `FlashTargetOnHit` subscribes to `OnHarm` (`Assets/Example3/Scripts/FlashTargetOnHit.cs:19`).
- Take-away for per-object design: "events derived from state changes" are re-fired by rollback unless the code tracks what it already announced.

### Moving target (`MovingTarget : PredictionBehaviour<NetworkState>`)
- Server-only logic (`NetworkFixedUpdate` returns on clients, `MovingTarget.cs:21-24`), position copied to/from `State` in `AfterTick`/`AfterStateChanged` (`:44-51`). The client does **not** predict it; it just shows server state from `lastReceivedTick`. Because it changes every tick, every received world state differs from what the client had stored for that tick, which triggers a full-world resimulation every tick (file 05).

## 7. Things a user must know that the API does not enforce
- You must override both `AfterTick` and `AfterStateChanged` for anything that has Unity-side state; the base implementations are empty (`PredictionBehaviour.cs:133,139`).
- `State` is a moving reference: it is re-pointed to a different ring slot every simulated tick and on resim (`WorldSnapshot.SetActivePtr`, `ClientManager.cs:317,352`). Do not cache `ref State` or `StatePtr` across ticks.
- State must be initialised in `OnPredictionSetup` (spawn tick slot only); older ring slots are zeros.
- Everything the simulation reads or writes must be reproducible: if a non-`PredictionBehaviour` object affects `Physics.Simulate`, it is not rolled back.
- `README.md` documents the pre-beta.5 API (`GatherState`, `ApplyState`, `ResimulationTransition(current, next)` as void). `Update-beta5.md` documents the change to pointer state, `AfterStateChanged`, `AfterTick` and `NetworkBool`; the README flow chart was never updated.
