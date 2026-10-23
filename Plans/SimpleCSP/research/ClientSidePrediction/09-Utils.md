# 09 - Utils

**Summary.** Small, mostly standalone helpers under `Assets/source/Runtime/Utils/` and `UniTask/`. The ring buffers (`RingBuffer`, `NullableRingBuffer`) are used everywhere (inputs, received world states); `NetworkBool`/`NoValues` exist because of the raw-memory state design; `CompoundBool` and `RNG` are unused in the repo; `UniTaskExtras` provides tick-aligned `await` timing. `MovingAverage` lives here too but belongs to the time-sync write-up (`TickRunner-and-TimeSync.md`).

| Util | File | Used by | Reuse potential for a per-object design |
|---|---|---|---|
| `RingBuffer<T>` | `Utils/RingBuffer.cs:18-68` | `StateSender._worldStateCopy` (`StateSender.cs:60`), base class of `NullableRingBuffer` | Trivial; would want a tick-tagged variant |
| `NullableRingBuffer<T>` | `Utils/NullableRingBuffer.cs:21-86` | input buffers (`ClientController.cs:34-42`, `ServerController.cs:37-45`), `ClientManager._worldStateCopy` (`ClientManager.cs:113-117`) | Good fit for input history; add tick validation |
| `NetworkBool` | `Utils/NetworkBool.cs:20-56` | Example3 input/state structs | Only needed for raw-memory state; unnecessary with field serialisation |
| `CompoundBool` | `Utils/CompoundBool.cs:19-56` | nothing | Handy for packing input/state flags |
| `RNG` (MulBerry32) | `Utils/MulBerry32.cs:20-84` | nothing | Valuable for deterministic weapon spread, but has bugs (below) |
| `NoValues` | `Utils/NoValues.cs:18-19` | `PredictionBehaviour<TState>` | Placeholder only |
| `UniTaskExtras` | `UniTask/UniTaskExtras.cs` | `PredictionManager` (`Init()` on both sides) | Optional; has static-state problems |
| `MovingAverage` | `Utils/MovingAverage.cs` | `ClientTickRunner` | Belongs to time-sync file; see note below |

## RingBuffer<T> (`Utils/RingBuffer.cs`)
- Fixed array of `size` elements; `Get/Set/Clear(index)` map `index` to `index % size` after adding `size` once if negative (`IndexToBuffer`, `:36-42`). A negative index below `-size` would still be negative and throw `IndexOutOfRangeException`.
- `All` exposes the raw array; `FillWithNew<T2>()` pre-populates reference-type slots (`:61-67`); `Count` is the capacity.
- **No tick tag:** callers index by `tick` and nothing records which tick a slot currently holds, so reading `tick` after the writer has moved 64+ ticks past returns the newer tick's data silently.

## NullableRingBuffer<T> (`Utils/NullableRingBuffer.cs`)
- `class NullableRingBuffer<T> : RingBuffer<Valid>` where `Valid` is `{ T Value; bool HasValue }` (`:21,79-83`), "so we don't need `where T : struct`" (`:76-78`, with a `todo should we just make T a struct??`).
- API: `Set(index, T)` (hides the base `Set`), `Get` (throws `KeyNotFoundException` if empty, `:25-32`), `GetOrDefault` (`:34-44`), `TryGet` (`:52-65`), `IsValid` (`:46-50`), inherited `Clear(index)` which resets `HasValue` to false.
- Because it inherits `RingBuffer<Valid>`, the inherited indexer `buffer[i]` returns the `Valid` wrapper, not `T` (`RingBuffer.cs:44`) - an easy mistake.
- Usage semantics in CSP: client input history (`GetOrDefault` replays), server input queue (`TryGet` for "missing input" detection plus `Clear(tick-1)` after use, `ServerController.cs:77`), and the client's received world-state copies (`GetOrDefault` always returns a pre-allocated object, `ClientManager.cs:113-117,196`).

## NetworkBool (`Utils/NetworkBool.cs`)
- `[StructLayout(Explicit, Size = 1)] struct NetworkBool { byte _value }`; `Value` getter is `_value == 1` (so any byte other than exactly 1 reads as false), implicit conversions to/from `bool`, `GetNullable/SetNullable` helpers for encoding `T?` in blittable state (`:20-56`).
- Reason (doc comment `:11-19`): `bool` is not blittable and may have a different representation on different machines, which matters when raw state bytes are `memcmp`'d and sent. `Update-beta5.md` notes it is "currently only needed for state"; input structs are serialised field-by-field by Mirage.
- Serialisation hooks: `Write(NetworkWriter, NetworkBool)` writes a 1-bit boolean, `Read(NetworkReader)` reads one (`:58-69`), so on the wire an input `NetworkBool` is 1 bit while in the raw state it is a whole byte (and rounds the struct up to an int).

## CompoundBool (`Utils/CompoundBool.cs`)
8 flags in one byte via an indexer (`Raw & (1 << index)`); DEBUG-only range check throwing `IndexOutOfRangeException` (`:19-56`). Unused anywhere in the repo.

## NoValues (`Utils/NoValues.cs`)
`[StructLayout(Explicit, Size = 0)] struct NoValues` - placeholder `TInput` for input-less `PredictionBehaviour<TState>` ("should never be used by scripts", `:12-19`).

## RNG / MulBerry32 (`Utils/MulBerry32.cs`)
- A tiny seedable PRNG intended for "same random values on client and server ... using previous state to get next random state" (doc `:14-19`). `RNG { uint _seed }` can be constructed from `uint`, `int`, or the bit pattern of a `float` (`:22-25`), `MulBerry32(uint)` is the Mulberry32 hash (public domain, `:27-35`), `Next()` returns 0..1 (`:37-44`), `Next(min,max)`, and static one-shot `Next(float seed)` variants keyed off a float state (`:72-83`).
- That static form is the interesting FPS-relevant idea: derive spread/crit rolls purely from a state value so the same tick gives the same number on server and client without sharing generator state.
- **Bugs by reading:** `OnUnitSphere` builds a vector from three values in [0,1] and normalises it, so it only covers the positive octant (`:51-57`); `InsideUnitSphere` uses `(Next() - 1) * 2`, which lies in [-2, 0] rather than [-1, 1] (`:59-69`), then normalises if too long. `Next()` divides by `(float)uint.MaxValue`, which can round to exactly 1.0 (`:43,77`). Nothing in the repo uses `RNG`.

## UniTaskExtras (`UniTask/UniTaskExtras.cs`)
Tick-aligned `await` built on UniTask:
- `NetworkYield.Run(CustomTiming timing, Action)` awaits a `NetworkYieldAwaitable` then invokes the action (`:10-20`); the awaitable's `Awaiter.IsCompleted` is always false and `OnCompleted` enqueues the continuation (`:22-59`).
- `CustomTiming` has six slots: `First/Normal/Last` x `NetworkFixedUpdate/VisualUpdate` (`:61-69`).
- `CustomTimingHelper.Init()` creates six `CustomTimingQueue`s and returns three `IPredictionUpdates` (`Order` = `int.MinValue`, `0`, `int.MaxValue`) whose `NetworkFixedUpdate()`/`VisualUpdate()` drain the corresponding queues (`:71-127`); `PredictionManager` adds them to the manager's collection on both server and client (`PredictionManager.cs:123,193`).
- `CustomTimingQueue` double-buffers (two `Queue<Action>`, swapped each run) so continuations enqueued while running execute on the *next* run, preventing infinite loops (`:129-211`); DEBUG builds check main-thread use; exceptions in continuations are caught and logged per continuation (`:198-209`).
- Observations (reading-based):
  - `queues` is a **static** array; a second `Init()` only logs "Replacing existing queues" and replaces it (`:75-87`), so a host or a server+client in one process (e.g. `SingleInstanceDebugStart`) leaves the first manager's helper objects pointing at orphaned queues.
  - It is not resimulation-aware: `NetworkFixedUpdate` queues are drained by *any* simulate call, including replayed ticks (`ClientManager.Simulate` -> `update.NetworkFixedUpdate()`, `:358-362`), so "await the next network fixed update" inside predicted code resumes during a replay, not on a real new tick.
  - Queues are not cleared on stop.
  - The UniTask package dependency is not declared in `package.json` (only in the asmdef GUID list and the project's OpenUPM scoped registry, `Packages/manifest.json`).
  - `NetworkYield`/`CustomTiming` are not used by any example.

## Related small pieces that live elsewhere
- `GetBehaviourCache<T>`: static reusable `List<T>` for component lookups (`PredictionCollection.cs:18-41`).
- `TickNotifyToken` + `ITickNotifyTracker`: pooled delivered/lost callback bound to a tick (`ITickNotifyTracker.cs:15-52`); the generic idea "carry a tick in the Notify callback and keep the max acked" is reusable and tiny.
- `MovingAverage` (`Utils/MovingAverage.cs`): fixed-size moving average/stddev for RTT. Pointer for the time-sync author: `Add` computes `_countInBuffer = Math.Max(_index, _countInBuffer)` *after* `_index` has been wrapped to 0 (`:33-41`), so once the buffer is full the count stays at `size - 1` and slot `size - 1` is permanently excluded from `GetAverage`/stddev (`:50-62,70-81`); the tests only cover the not-yet-wrapped case (`Assets/Tests/SimpleMovingAverageTest.cs`). Reasoned from code, not executed.
