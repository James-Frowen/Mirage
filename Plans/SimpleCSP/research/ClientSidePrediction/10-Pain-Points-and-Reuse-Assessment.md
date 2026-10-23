# 10 - Pain Points and Reuse Assessment

> **THIS FILE IS OPINION / ASSESSMENT.** Files 01-09 are factual and cited. This file interprets them for the goal of designing a much simpler *per-network-object* prediction system ("SimpleCSP") for a server-authoritative FPS (16-32 players, character controller + weapons) that coexists with Mirage SyncVars/RPCs. Where an item is a code fact it is cited; where it is a judgement it says so. Confidence tags: **C** = unambiguous from the code, **R** = reasoned from the code but not run/tested.

**Summary.** CSP's design is internally coherent but pays a large complexity tax for one decision: *treat the whole world as one flat, raw-memory snapshot*. That single choice drives the unmanaged allocator, the pointer-valued `State`, the explicit-layout/`NetworkBool` rules, the delta codecs and ack logic, the all-or-nothing `memcmp` reconciliation and the "replay everything, including physics" cost model. Roughly 40% of the runtime code (about 2.5k of 6.5k lines) exists only for that. The pieces worth keeping are small and mostly per-object already: the tick-addressed input pipeline (ack-driven redundancy, `MissingInput`, ring buffers), the `NetworkInputs{Current,Previous}` idea, `IsResimulation`, the time-sync approach, and the debugging tools (especially `LagSocket`).

## 1. Complexity hot-spots (ranked)

1. **Flat whole-world state in unmanaged memory** - `WorldSnapshot`/`IdentitySnapshot` (304 lines), `WorldStateCopy`, `StateSender` (348), `DeltaSnapshot` (796, six codecs, one used), `Alloc/*` (~200), `SnapshotBehaviour` (88), plus the receive half in `ClientManager` (~150 lines). Failure modes are native crashes, silent corruption or layout drift, not exceptions (`ClientManager.cs:199-203,239-245`). It also forces user state to be a fixed-size blittable struct (file 04 section 6).
2. **Whole-world resimulation** - every received state with any bitwise difference triggers replay of ticks N+1..T-1 for *all* objects and a full `Physics.Simulate` per step (file 05). Cost per client tick is roughly `(1 + lead) x (all objects' scripts + physics + whole-world memcpy)`. With ~100 ms RTT that is about 7-8x, and with 32 players the *remote* players alone guarantee the mismatch every tick (R; not benchmarked).
3. **State glue the user must hand-write** - `AfterTick` (Unity -> state) and `AfterStateChanged` (state -> Unity) on every behaviour, plus a hand-laid-out struct. Forgetting either silently desyncs. `State` is a moving pointer into a ring (file 02 section 7). This is the main usability cost.
4. **Implicit invariants everywhere** - untagged ring slots, `tick % 64` indexing, `Order` + unstable `List.Sort`, static caches, global physics mode, mirrored `Time.timeScale`, `MissingInput` called twice per missing tick, spawn/state race handled by a warning. None are enforced by types or asserts.
5. **Time sync** - covered in `TickRunner-and-TimeSync.md`. It is the part most tuned by the author (thresholds, RTT averaging, skip logic) and is a large part of why the resim window exists at all.
6. **Debuggability** - the debugging aids are raw-int hex dumps and an after-image of positions; there is no per-object inspector of "predicted vs server" values, and **zero tests** for prediction, input handling, ack logic or resim (`Assets/Tests` has only delta codecs and `MovingAverage`).

## 2. Coupling to whole-world / delta snapshots / unmanaged memory

W = depends on the whole-world model, D = depends on delta/ack machinery, U = depends on unmanaged/raw-pointer state.

| Component | W | D | U | Comment |
|---|---|---|---|---|
| `PredictionManager` | wires | wires | owns `SimpleAlloc` | Mostly orchestration + Mirage hooks; only the allocator and manager creation are coupled |
| `ClientManager` | **yes** (global memcmp, global resim, `_needResimulate`) | **yes** (decode, `_worldStateCopy` ring) | **yes** | Core of the coupling |
| `ServerManager` | **yes** (`WorldSnapshot`, `CopyFromPreviousTick`) | via `StateSender` | **yes** | Thin; most logic is the tick loop |
| `StateSender` | **yes** | **yes** | **yes** | 100% droppable |
| `WorldSnapshot`/`IdentitySnapshot`/`WorldStateCopy`/`SnapshotBehaviour` | **yes** | - | **yes** | 100% droppable in a per-object design |
| `DeltaSnapshot*`, `HoffmanCoding`, `StateReceiver`, `WorldStateDump` | - | **yes** | **yes** | 100% droppable |
| `Alloc/*` | - | - | **yes** | Droppable |
| `ClientController` / `ServerController` | per-object | none | `*_statePtr` copies; `TState : unmanaged` | Closest thing to the target; need decoupling from the pointer |
| `PredictionBehaviourBase` | per-object API | none | `State` ref, `TState : unmanaged`, `AfterTick/AfterStateChanged` | API shape is the issue, not the logic |
| `PredictionCollection` | none | none | none | Generic ordered list with deferred add/remove |
| `ServerInputHandler`, `InputState`, `TickNotifyToken` | none | ack pattern shared with state | none | Fully reusable |
| `TickRunner`/`ClientTickRunner`/`PredictionTime` | none | none | none | Reusable (see timing file) |
| `ClientInterpolation` | reads the world ring | none | pointers | Unfinished; needs redesign anyway |
| `DefaultPredictionSimulation` | whole physics scene | none | none | Only relevant if physics-driven |
| Debug tools | `WorldStateDump` only | - | `WorldStateDump` only | Others standalone |
| Utils | none | none | `NetworkBool`/`NoValues` (because of raw state) | |

## 3. What looks fragile, buggy or unfinished

### 3.1 Consolidated bug / hazard list

| # | Item | Where | Conf. | Matters for SimpleCSP? |
|---|---|---|---|---|
| 1 | `SetServerRunning` dereferences a null `_tickRunner` if called before server start | `PredictionManager.cs:266-273` (vs guarded `:250`) | C | No (manager dropped) but shows wiring fragility |
| 2 | Server `ManualUpdate` never set true: Mirage `UpdateReceive/Sent` runs from both Mirage and the tick runner | `PredictionManager.cs:93,129-130,319-320` | C | Yes if tick-aligned receive is wanted |
| 3 | Stop/cleanup only touches the first `IPredictionBehaviour` on the root object (`TryGetComponent`) | `PredictionManager.cs:144-148,225-229` | C | Lesson |
| 4 | Client "not ready" path unreachable; server never learns a client stopped being ready and keeps sending (eventually vs-zero) full states | `ClientManager.cs:426-429,477-490`, `PredictionManager.cs:250-254` | R | Lesson (explicit readiness) |
| 5 | First input packet dropped (`int > null` is false) | `ServerController.cs:51` | C | Lesson (null-safe tick compares) |
| 6 | No upper bound on input tick; untagged 64-slot ring can be overwritten; `lastReceivedInput` can be pushed far ahead, rejecting honest packets | `ServerInputHandler.cs:46,85-96`, `ServerController.cs:51-54` | R | **Yes** (input validation) |
| 7 | `HasInput` not checked before routing inputs (null `_inputBuffer`) | `ServerInputHandler.cs:65-75` | R | Yes (validation) |
| 8 | Oversize input message would throw `MessageSizeException` out of `Tick` (not caught by `NetworkPlayer.Send`) | `ClientManager.cs:473`, Mirage `NetworkPlayer.cs:312-324` | R | Yes if inputs grow (weapons) |
| 9 | Resim with `from > to` logs an error and skips applying server state | `ClientManager.cs:297-301` | C | Lesson |
| 10 | Replay flag not exception-safe (`IsResimulation` stuck true; `AfterResimulate` skipped) | `ClientManager.cs:329-337` | C | Yes |
| 11 | `AfterStateChanged` runs twice per resim for every behaviour; `ResimulationTransition` runs for all | `ClientManager.cs:319-326`, `ClientController.cs:55-68` | C | Lesson (event duplication) |
| 12 | `ResimulationTransition` overwrites simulation history with a blend, so smoothing changes sim state and keeps mismatches alive | `ClientController.cs:59-60` | C (effect R) | **Yes** (separate visual smoothing from sim) |
| 13 | Client trusts server `VsTick`; null/mismatched base buffer would deref a null native pointer | `ClientManager.cs:199-203` | R | No (dropped) |
| 14 | Layout mismatch is only logged, then copies the wrong length | `ClientManager.cs:239-245` | C | No |
| 15 | Every spawn/despawn changes world size -> vs-zero full state to all clients for ~RTT; > 1193 B goes reliable-fragmented | `StateSender.cs:227-254,135-166` | R | Only as an argument against flat world state |
| 16 | `WorldStateCopy` rings never released on stop (until `OnDestroy`) | `StateSender.cs:60-61`, `ClientManager.cs:113-117`, `WorldStateCopy.cs:29-40` | R | No |
| 17 | Reliable fallback requires `MaxReliableFragments > 0` | `StateSender.cs:161-169`, Mirage `AckSystem.cs:351-379` | C | No |
| 18 | Global `Time.timeScale` overwritten from server messages | `ClientManager.cs:170-178`, `StateSender.cs:81` | C | Decide deliberately |
| 19 | Simulation order uses unstable `List.Sort` on `Order`; equal orders may differ between server and client | `PredictionCollection.cs:195-196` | R | **Yes** (determinism) |
| 20 | `ClientInterpolation`: clock never fed (`_timeSync.OnMessage` has no callers), `Tick2` always 0, buffer never pruned | `ClientInterpolation.cs:454,495-526` | C (effect R) | Yes (remote objects) |
| 21 | `IPredictionTime.UnscaledTime` documented as rewinding, implemented as wall clock | `Interfaces.cs:31-37`, `TickRunner.cs:27,144-148` | C | Yes (API clarity) |
| 22 | Dead/unfinished code: `ReceiveHostInput`, `ClearLastAcked`, `SendNotReadyInputs`, `StateReceiver`, `HoffmanCoding`, stale `InputMessage` doc, README flow chart pre-beta.5 | various | C | - |
| 23 | `DeltaSnapshot_LossyFloats` unfinished yet listed in the test fixtures; `DeltaSnapshot_FromObject` indexing bug | `DeltaSnapshot.cs:549,728`, `DeltaSnapshotTest.cs:9-14` | C | No |
| 24 | `RNG.OnUnitSphere` (positive octant only) and `InsideUnitSphere` ([-2,0]) wrong | `MulBerry32.cs:51-69` | C | Yes (weapon spread) |
| 25 | `MovingAverage` count off-by-one once wrapped | `MovingAverage.cs:33-41` | C (effect R) | If reused |
| 26 | `LagSocketFactory.CreateServerSocket` uses `CreateClientSocket`; unlocked reads of the shared list | `LagSocketFactory.cs:28,249-277` | C | If reused (it should be) |
| 27 | `AfterImageHelper` never hides renderers and is unused | `AfterImageHelper.cs:7-36` | C | Minor |
| 28 | `WorldStateDump` passes a raw pointer to a thread-pool task | `WorldStateDump.cs:25-57` | C | No |
| 29 | `UniTaskExtras` static queues replaced by a second `Init()`; not resimulation-aware | `UniTaskExtras.cs:73-87`, `ClientManager.cs:358-362` | C/R | If reused |
| 30 | Static caches/globals: `GetBehaviourCache<T>`, `TickNotifyToken.pool`, global `Physics.simulationMode` | `PredictionCollection.cs:18-41`, `ITickNotifyTracker.cs:24`, `DefaultPredictionSimulation.cs:206-223` | C | Lesson |

### 3.2 Example3 (the FPS-like example) problems that matter as lessons
- `PlayerLook.ApplyInputs` ignores the received `Yaw/Pitch` and uses the local `_yaw/_pitch` fields, so the server never aims from client input (`PlayerLook.cs:90`). C.
- `Shoot()` is called from `ApplyInputs`, so every replay of a shoot tick instantiates another non-networked bullet; client bullets are never destroyed because the early return precedes `Destroy` (`PlayerLook.cs:93-96`, `Shooter.cs:16-24`, `Bullet.cs:9-20`). C (effect on replay R).
- `Health.AfterStateChanged` re-fires `OnHarm`/`OnDeath` repeatedly because `_clientHealth` is never updated (`Health.cs:47-52`). C.
- Sample-and-clear input (`GetInput` returns then resets the accumulator) zeroes `Move/Yaw/Pitch` for a second tick within one frame (`PlayerInputBase.cs:27-32`). R.
- No hit prediction, hitscan or lag compensation exists anywhere; the target use case (hit detection with rollback) is *not demonstrated* by the repo.

### 3.3 Notable `todo`s left in the code (not exhaustive)
`ClientManager.cs:213` (state/timing separation), `:366` (is client `AfterTick` needed?), `:386` (what if two ticks at once); `PredictionManager.cs:166-170,217` (host cleanup, no world state to host); `StateSender.cs:26,45,140,149,272` (header size, expose dump, cache payload per base tick, Mirage constants, far base ticks); `ClientInterpolation.cs:175,198,358`; `WorldSnapshot.cs:59,210`; `SnapshotBehaviour.cs:49`; `ServerController.cs:109` (boxing); `Interfaces.cs:136` (rename to early/late); `DefaultPredictionSimulation.cs:26,60`; `PlayerTimeTracker.cs:16`; `NullableRingBuffer.cs:78`; many in `TickRunner.cs` (see timing file). They cluster around timing, interpolation and host mode.

## 4. Lessons for a per-object design (opinion)

1. Keep history **per object** and **tick-tag** every slot (the untagged `tick % N` rings are the root of several hazards).
2. Keep **simulation state separate from visual smoothing**; apply error offsets visually (file 05 section 3).
3. Compare **per object with tolerance** (or a user `Equals`), and replay **only objects that mispredicted**, ideally only the owned character.
4. Provide a first-class **"is this a replay?"** signal and an events pattern (record what was announced per tick) so audio/VFX/projectiles/damage-events do not duplicate.
5. Make ordering **explicit and stable** (stable sort or explicit execution list) and identical on both sides.
6. Keep **tick-addressed, ack-redundant inputs**, but bound the accepted tick window, null-safe the compares, and consider giving the client feedback on late/substituted inputs.
7. Design input sampling deliberately: edge events accumulate, continuous axes (move, aim) hold their last value (avoid the example's reset-to-default).
8. Treat **remote objects as a separate path** (SyncVar/state messages + interpolation), not part of the replay; this removes the "frozen at state N" artefact.
9. Decide how **projectiles and hits** work under prediction (predicted spawn with client-side id, server-authoritative hit, or hitscan with server-side rewind). The old repo's per-identity 64-tick server history shows rewind is feasible; a per-object history ring on the server gives that for free.
10. Add **tests** for the input path, ack logic and replay determinism from day one; the old repo has none.

## 5. Keep / simplify / drop table (for a per-object SimpleCSP)

| Component | Verdict | Why |
|---|---|---|
| `TickRunner`, `PredictionTime`/`IPredictionTime` | **Keep (simplify)** | Fixed-step driver and `IsResimulation`/`Tick` are needed regardless; see timing file for how much of `ClientTickRunner` to keep. Fix the `UnscaledTime` doc/impl mismatch |
| `ClientTickRunner` clock sync | **Keep idea, simplify** | A tick-stamped-input model needs the client to lead the server; but a per-object system can run a much shorter, cheaper replay window |
| `PredictionManager` | **Simplify** | Keep "drive ticks from one Update, pump Mirage receive/send at tick boundaries"; drop managers/allocator/host special-casing; fix `ManualUpdate` usage |
| `ServerManager` / `ClientManager` | **Drop (replace with a thin driver)** | They exist to own the world snapshot and global resim; a per-object system needs a registry + tick loop only |
| `PredictionCollection` | **Simplify** | Deferred add/remove + ordering is useful; make ordering stable/explicit |
| `ClientController` / `ServerController` | **Keep concept, rewrite** | Already per-object (input ring, `MissingInput`, Before/AfterResimulate); detach from pointer state |
| `PredictionBehaviour` API (`GetInput/ApplyInputs/MissingInput/NetworkFixedUpdate/OnPredictionSetup`) | **Keep (trim)** | Familiar and sensible; remove `State` pointer, `AfterTick`/`AfterStateChanged` glue, `NetworkBool` need |
| `NetworkInputs<T>{Current,Previous}` | **Keep as-is** | Cheap, avoids input-edge bugs |
| `InputState` message + ack-driven redundancy (`TickNotifyToken`, `ackedInput`, 8-input cap) | **Keep (harden)** | Best-validated idea in the repo; add tick bounds, `HasInput` check, size guard; consider grouping per object/per player |
| `ServerInputHandler` | **Keep (simplify)** | Validation flow is right; fix findings 5-8 |
| `NullableRingBuffer`/`RingBuffer` | **Keep (add tick tags)** | Good fit for per-object input/state history |
| `WorldSnapshot`, `IdentitySnapshot`, `ISnapshotBehaviour`, `SnapshotBehaviour`, `WorldStateCopy` | **Drop** | Flat raw-memory world is the root of the complexity |
| `StateSender`, `DeltaWorldState`, `DeltaWorldStateFragmentedAck`, `DeltaSnapshot*`, `HoffmanCoding`, `StateReceiver` | **Drop** | Replace with Mirage SyncVars/RPCs or a small per-object state message (optionally notify-acked) |
| `Alloc/*`, `UnsafeHelper`, `WorldStateDump` | **Drop** | Only exist for raw memory; per-object managed structs suffice at 16-32 players |
| Global `memcmp` reconciliation | **Drop (replace)** | Per-object compare with tolerance on owned state |
| `ResimulationTransition` | **Simplify** | Keep the need for correction smoothing, but as visual error offset, not sim overwrite |
| `ClientInterpolation`, `InterpolationTime`, `SnapshotBuffer<T>` | **Drop / rewrite** | Unfinished (clock never fed, `Tick2` bug); `SnapshotBuffer<T>` (generic time-sorted list with lerp) is a reasonable base for remote-object interpolation if given a real clock |
| `DefaultPredictionSimulation` / `SimulationMode` | **Simplify / optional** | Only needed if rollback must re-step Unity physics; a kinematic character controller replays without a global physics step |
| `NetworkBool`, `NoValues`, explicit-layout rules | **Drop** | Raw-memory artefacts |
| `CompoundBool` | **Keep (optional)** | Nice for packing input/state flags |
| `RNG` (MulBerry32) | **Keep (fix)** | Useful for deterministic weapon spread; fix sphere functions |
| `UniTaskExtras` | **Optional / rework** | Tick-aligned awaits are nice but must be resimulation-aware and non-static |
| `MovingAverage` | **Keep if timing reused (fix)** | Off-by-one once wrapped |
| `LagSocket*` | **Keep as-is (fix 2 small issues)** | Standalone, valuable for testing prediction |
| `TickDebugger*` | **Keep if time sync kept** | Isolated harness for the clock |
| `AfterImageHelper` + `IDebugPredictionAfterImage` idea | **Keep idea** | Excellent reconciliation visual; implement independent of state storage |
| `SingleInstanceDebugStart` | **Keep idea** | Two Mirage instances in one editor with local physics is useful for tests |
| `LogValueTracker` | **Keep** | Trivial |
| `IDebugPredictionLocalCopy` | **Optional** | Side-by-side "no network" cube is a useful comparison but wired into the hot path |

## 6. Questions this analysis leaves open for the design
- What replaces the "state" abstraction: user-owned fields with `Gather/Apply`, a managed struct per behaviour, or SyncVar-like members? (Needs to support cheap snapshot/restore and equality.)
- Is any *physics* replay required (rigidbody characters, projectiles), or can characters be kinematic with replay limited to the owner?
- How do non-owned characters get their positions (Mirage SyncVars + interpolation vs a custom per-object state message with ack/delta)?
- Where do server-side hit checks run relative to client time (rewind by client lead vs predicted hits)?
- How much of `ClientTickRunner`'s lead computation is needed if the server buffers inputs with a small jitter window?
