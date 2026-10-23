# 05 - Reconciliation and Resimulation

**Summary.** The client does **not** run a "was my prediction wrong?" check per object or with a tolerance. For every received world state it `memcmp`s *every identity's* block against what the client had stored for that tick; if **any** byte of **any** object differs, the next client tick rolls the **entire world** back to the server's state for tick N and re-simulates ticks N+1 .. T-1 (inputs replayed from the client's own buffer, all objects' `NetworkFixedUpdate`, a full `Physics.Simulate` per step) before simulating tick T as usual. In a world with any remote moving object or any floating-point divergence this means a resimulation of about "client lead" ticks (roughly RTT in ticks + a few) on essentially every tick.

## 1. Detection

Location: `ClientManager.CopyStateForTick(tick)` (`Assets/source/Runtime/ClientManager.cs:211-259`).

- For each identity block in the decoded server buffer: `anyChanged |= UnsafeHelper.CopyAndCheckChanged(serverBlock, clientSlot(tick), snapshot.IntSizePerTick)` (`:244-245`). `CopyAndCheckChanged` is `MemCmp` then, if different, `MemCpy` (`DeltaSnapshot/WorldSnapshot.cs:291-302`).
- `clientSlot(tick)` is the client's own ring slot for the server's tick N: i.e. **what the client predicted at tick N** (written when the client simulated N, which it did long ago because it runs ahead). If different, it is overwritten with the server's version.
- `_needResimulate = anyChanged` (`:258`), `lastReceivedTick = tick` (`:215`). Only tick N is compared; earlier predicted ticks are not.
- No tolerance, no per-object filtering, no distinction between owned and remote objects, no distinction between "gameplay-relevant" and "irrelevant" bytes (padding, unused fields, `PlayerLook`'s 44 empty bytes).
- If two messages arrive in one frame, only the last one's comparison result is kept (`=` not `|=`, `:258`). That is acceptable because tick N+1 matching also validates the chain up to N+1, but it is worth knowing.
- So the answer to "does it always resim?": **only when something differs**, but with bitwise comparison of a flat world that includes other players, any remote movement or physics divergence makes that true nearly every tick. A world consisting only of the local player, no float divergence, and no packet loss would never resim; Example3's `MovingTarget` alone guarantees a resim every tick (its server-only motion never matches the client's stored copy, `Example3/Scripts/MovingTarget.cs:23-24`).

## 2. The resimulation step by step

`ClientManager.Tick(tick)` (`ClientManager.cs:380-411`):

```csharp
if (_needResimulate)
{
    // from +1 because we receive N, so we need to simulate n+1
    // sim up to N-1, we do N below when we get new inputs
    Resimulate(lastReceivedTick.Value + 1, tick - 1);
    _needResimulate = false;
}
```
(`:388-394`). Then normal input gather/send/`Simulate(tick)`.

`Resimulate(from, to)` (`:292-343`), with `from = N+1`, `to = tick-1`:

| Step | Code | What happens |
|---|---|---|
| 0 | `:297-306` | If `from > to` -> `LogError` and **return** (state not applied); if `to - from > _bufferSize (64)` -> `LogError` and return. |
| 1 | `:313-314` | `ClientController.BeforeResimulate()` on every behaviour: if it has simulated locally and `EnableResimulationTransition`, copy the **currently active** state (the last simulated tick, T-1) into `beforeResimulateState` (`ClientController.cs:44-53`). |
| 2 | `:317` | `_worldSnapshot.SetActivePtr(from - 1)`: every `State` now points at slot N, which holds the **server's** state (written in `CopyStateForTick`). |
| 3 | `:319-326` | `AfterStateChanged()` on **every** behaviour (owned, remote, input-less): user code pushes `State` into Rigidbody/Transform. Debug: orange after-image if `IDebugPredictionAfterImage`. |
| 4 | `:329-334` | `_time.IsResimulation = true`; `for tick in from..to: Simulate(tick)`; `IsResimulation = false` (not in a `finally`). |
| 5 | `:336-337` | `ClientController.AfterResimulate()` on every behaviour: `next = *State` (state at T-1 after replay); `*State = ResimulationTransition(before, next)`; `AfterStateChanged()` (`ClientController.cs:55-68`). Debug: blue after-image of `next`. |

`Simulate(tick)` during replay is the *same* function as the first-time simulation (`ClientManager.cs:345-378`): `CopyFromPreviousTick(tick)` (whole world memcpy + repoint), then for each update in `Order`: `ClientController.Simulate` (owned: `ApplyInputs(buffer[tick], buffer[tick-1])`, then `NetworkFixedUpdate`) or plain `NetworkFixedUpdate`; then `_simulation.Simulate(dt)`; then `AfterTick()` on all behaviours.

```mermaid
sequenceDiagram
  participant Net as Mirage receive (frame start)
  participant CM as ClientManager
  participant Snap as WorldSnapshot ring
  participant PB as all PredictionBehaviours
  participant Phys as Physics.Simulate

  Net->>CM: DeltaWorldState(N=100)
  CM->>CM: delta-decode, memcmp each identity vs predicted slot[100] -> differs
  CM->>Snap: overwrite slot[100] with server state; _needResimulate = true
  Note over CM: client last simulated 106, now starting tick 107
  CM->>PB: BeforeResimulate (save state[106])
  CM->>Snap: SetActivePtr(100)
  CM->>PB: AfterStateChanged (all objects)
  loop tick = 101..106 (IsResimulation = true)
    CM->>Snap: CopyFromPreviousTick(tick)
    CM->>PB: ApplyInputs (owned, from client input ring) + NetworkFixedUpdate (all)
    CM->>Phys: Simulate(dt)
    CM->>PB: AfterTick (all)
  end
  CM->>PB: AfterResimulate: state = ResimulationTransition(before[106], resim[106]); AfterStateChanged
  CM->>CM: normal tick 107: InputTick, SendInputs, Simulate(107)
```

Worked numbers: with the client lead `delay ~ 6 ticks`, each client tick runs 1 normal + 6 replayed simulation steps, i.e. about 7x the per-tick simulation and physics cost, plus 2 calls of `AfterStateChanged` per behaviour. The lead is `~ (avg RTT + 2*stddev) * TickRate + 1` ticks, capped at 60 (`TickRunner.cs:380-403`); see `TickRunner-and-TimeSync.md`.

## 3. `ResimulationTransition` (smoothing)

- Signature: `virtual TState ResimulationTransition(TState before, TState after)` returning the state to use (`PredictionBehaviour.cs:153-160`); default returns `after`. `EnableResimulationTransition` (default true) skips capturing before-state (`:145`, `ClientController.cs:48,57`).
- `before` = the state at the **latest predicted tick (T-1) as it was before the rollback**; `after` = the state at T-1 produced by replaying from the server state. Examples blend them: `Lerp(before, after, ResimulateLerp = 0.1)` (`Example2/PredictionExample2.cs:59-68`).
- The returned value is **written into the simulation state** (`*behaviour._statePtr = ...`, `ClientController.cs:60`) and pushed to Unity objects (`AfterStateChanged`). So the smoothing is not a visual-only offset: tick T's simulation starts from the blended state, the ring slot T-1 now holds a blend that is neither the pure prediction nor the server value, and when the server's state for T-1 arrives the compare is against that blend. With a 10% lerp the error decays geometrically over many ticks while resimulation (triggered by the persistent mismatch) continues. Consequences (reasoned, not measured): smoothing couples visuals to simulation, makes the client's own history non-reproducible, and keeps `_needResimulate` true longer.
- The smoothing function runs for **every** behaviour on every resim, including remote ones (default pass-through) and input-less ones.

## 4. What happens to non-predicted things during a resim

| Thing | During resim | Notes |
|---|---|---|
| Local player (owned `PredictionBehaviour`) | Rolled back to server state at N, replays its own buffered inputs for N+1..T-1 | Replays the client's inputs even where the server substituted `MissingInput`/dropped late input |
| Remote `PredictionBehaviour`s (other players, targets) | Rolled back to server state at N; `NetworkFixedUpdate` runs for each replayed tick (user code must guard with `IsServer`/`IsRemoteClient`); no inputs are known | Unless user code extrapolates, they are **frozen at server state N** through the replay, so the local player interacts with where remote objects were `lead` ticks ago. `AfterTick` writes the unchanged transform back into `State` |
| Plain Unity objects/rigidbodies that are **not** `SnapshotBehaviour`s (e.g. Example3 `Bullet`, debris, ragdolls) | **Not restored**, but `Physics.Simulate` steps the whole physics scene on every replayed tick (`_simulation.Simulate`, `ClientManager.cs:364`) | They advance an extra `lead` physics steps each time a resim occurs (reasoned from code; not observed). This is the main reason everything simulated must be a `PredictionBehaviour` |
| `IPredictionUpdates` classes without state | `NetworkFixedUpdate()` replayed | e.g. `ClientInterpolation` ignores it, the UniTask timing queues run their continuations (`UniTaskExtras.cs:124`), so awaited continuations scheduled on `NetworkFixedUpdate` timing can run during replay |
| Physics engine internals (contact caches, sleeping, joints, character-controller state) | Not part of any snapshot | Only what the user mirrors into `State` is restored; replays are therefore not bit-identical to the original run, which is another source of persistent "mismatch" (general PhysX behaviour, not verified in this repo) |
| Spawned/destroyed objects, UI, audio, particles | Not rolled back | Example3's `Shoot()` runs inside `ApplyInputs` and so **instantiates another bullet for every replay of a shoot tick** (file 02) |
| Events derived from state | Fired again | `Health.AfterStateChanged` re-raises `OnHarm`/`OnDeath` each call (file 02) |

`IsResimulation` (`_time.IsResimulation`) is true only inside the replay loop (`ClientManager.cs:329-334`). It is false during the `AfterStateChanged` calls before and after the loop, and during the final normal `Simulate(T)`. User code that wants to suppress side effects must check it in `ApplyInputs`/`NetworkFixedUpdate`.

## 5. Edge cases and fragilities

- **Short lead:** `from > to` happens when the server tick N is already >= the client's last simulated tick (`lastReceivedTick >= tick - 1`). The code logs an error and returns, which means the server state is not applied via `AfterStateChanged` that tick (`ClientManager.cs:297-301`) even though slot N was overwritten. The +1 tick safety in `DelayInTicks` should prevent it in steady state; jitter spikes could hit it (not tested).
- **Too long:** `to - from > 64` -> error and no resim; self-heals when a later message arrives with a closer `from` (`:302-306`). The client lead is clamped to 60 ticks for this reason (`TickRunner.cs:242-244`).
- **No try/finally around the replay flag:** an exception in user `ApplyInputs`/`NetworkFixedUpdate` during replay leaves `IsResimulation == true` and skips `AfterResimulate` (`ClientManager.cs:329-337`), and `TickRunner` has no try/catch around `OnTick` (`TickRunner.cs:194-196`).
- **Double `AfterStateChanged`:** every behaviour gets it once at the start of each resim and again after the transition (`ClientManager.cs:319-326`, `ClientController.cs:55-68`) because `hasSimulatedLocally` is set after the first local `Simulate` and never cleared (`ClientController.cs:28,83`), so effectively always.
- **Per-frame limits:** `TickRunner` runs at most 5 ticks per frame; a resim only happens at the first tick of a frame because the flag is consumed (`:388-394`), but its cost is paid inside that frame.
- **Whole-world coupling:** one mismatching object forces all objects to be rolled back and replayed; nothing is "already correct and can be left alone".
- **Input-coupled corrections:** because the replay uses the client's input history, a server-side input substitution (late/lost input) produces a visible correction (rubber-banding) with no way for the server to say which input was used.
- **No server-side reconciliation / lag compensation:** the server never rewinds; hits are evaluated at server tick time against server-time positions. The server *does* keep 64 ticks of state history per identity (`IdentitySnapshot` ring), which would be enough for rewinding state, but no code uses it that way (on the server, old history is only read as delta bases from `StateSender`'s `WorldStateCopy` ring, `StateSender.cs:227-254`; on the client, `ClientInterpolation` reads old slots).
- **Remote objects are in the past:** after a resim the remote objects sit at state N (lead ticks old) while the local player is at T-1. Visual treatment is left to the user (file 06).

## 6. What in the code causes the whole-world behaviour (facts, no recommendations; see file 10 for assessment)
- State for all objects is one flat buffer compared with a single `anyChanged` flag (`ClientManager.cs:222-258`), so comparison and rollback are all-or-nothing.
- `AfterStateChanged` + `AfterTick` exist because state lives in a CSP-owned pointer rather than in the Unity objects/fields; the rollback restores the pointer slot and relies on user glue to push it into Unity.
- `Simulate(tick)` iterates *all* updates and runs the *whole* physics scene (`ClientManager.cs:354-364`); there is no notion of "objects that need replay", so replay cost = lead x world cost.
