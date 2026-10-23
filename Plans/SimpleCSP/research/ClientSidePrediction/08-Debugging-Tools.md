# 08 - Debugging Tools

**Summary.** `Assets/source/Runtime/Debugging/` (about 1000 lines) holds the author's test harnesses: a tick-clock debugger (`TickDebugger*`), a Mirage socket wrapper that simulates latency/loss/jitter (`LagSocket`), a one-process server+client starter used by the examples (`SingleInstanceDebugStart`), a raw world-state file dumper, and two tiny helpers. Most of it is independent of prediction itself; `LagSocket` is the clear standalone win. Several pieces are unused or have small bugs (listed below).

| Tool | File | Lines | Used by | Reusable standalone? |
|---|---|---|---|---|
| `TickDebugger` + `TickDebuggerOutput`/`Gui`/`CanvasGraph`/`Setup` | `Debugging/TickDebugger*.cs` | 91+19+43+68+25 | scenes (ExampleTicks); `PredictionManager.DebugOutput` | Only with `TickRunner`/`ClientTickRunner` (time-sync work) |
| `LagSocketFactory`, `LagSocket`, `LagSettings`, `LagSocketFactoryGUI` | `Debugging/LagSocket/*` | 411 + 33 | Scenes (a SocketFactory component) | **Yes** (depends only on Mirage socket layer) |
| `SingleInstanceDebugStart` | `Debugging/SingleInstanceDebugStart.cs` | 203 | Example1/2 scenes | Concept yes; code is tied to `PredictionManager` |
| `WorldStateDump` | `Debugging/WorldStateDump.cs` | 89 | `StateSender` (disabled) | No (raw-int world buffer specific) |
| `AfterImageHelper` | `Debugging/AfterImageHelper.cs` | 38 | **nothing** | Yes, after fixing |
| `LogValueTracker` | `Debugging/LogValueTracker.cs` | 52 | `StateSender` | Yes (trivial) |
| `IDebugPredictionLocalCopy`, `IDebugPredictionAfterImage` | `Interfaces.cs:112-124` | - | Example1/2 behaviours; `ClientManager`/`ClientController` | Hooks inside the hot path |
| Profiler markers, verbose loggers, `#if DEBUG` GUI values | various | - | `ClientManager`, `PredictionManager` | pattern only |

## 1. TickDebugger family
- `TickDebugger : NetworkBehaviour` (`Debugging/TickDebugger.cs:6-90`) is a **minimal harness for the clock sync alone**: on server it creates a plain `TickRunner`, on client a `ClientTickRunner` (`:47-66`), and exchanges the tick number and wall-clock time using two unreliable RPCs `ToClient_StateMessage(tick, clientTime)` / `ToServer_InputMessage(tick, clientTime)` (`:74-89`). It calls `tickRunner.OnUpdate()` unguarded in `Update` (`:26`; NRE if neither side has started) and sets Mirage's ping interval to 0 on the client (`:65`). It is independent of the real `PredictionManager` path and exists to tune `ClientTickRunner`. See the TickRunner file for what it measures.
- `TickDebuggerOutput` (abstract `MonoBehaviour`, `TickDebuggerOutput.cs:5-18`) is a bag of properties (`IsServer`, `ClientTick`, `ServerTick`, `Diff`, `ClientDelayInTicks`, `ClientTimeScale`, `ClientRTT`, `ClientJitter`). `PredictionManager` fills it under `#if DEBUG` in `SetGuiValues` (`PredictionManager.cs:335-367`; the server branch uses `ServerManager.Debug_FirstPlayertracker`, a LINQ `FirstOrDefault` per frame, `ServerManager.cs:44`).
- `TickDebuggerGui` renders it with `OnGUI` (client/server tick, diff, target delay, scale, RTT, jitter), `TickDebuggerCanvasGraph` draws a scrolling "Diff" line using one UI `Image` GameObject per pixel column and moving all of them every `LateUpdate` (`TickDebuggerCanvasGraph.cs:24-27,29-68`) - fine for a debug view, O(width) per frame.
- `TickDebuggerSetup` (`TickDebuggerSetup.cs`) instantiates two `NetworkManager` prefabs in one scene and starts server then client after a delay (`:7-24`).

## 2. LagSocket (`Debugging/LagSocket/LagSocketFactory.cs`)
- `LagSocketFactory : SocketFactory` wraps another `SocketFactory` (`inner`) and returns `LagSocket`s (`:21-34`). **Copy/paste error:** `CreateServerSocket()` calls `inner.CreateClientSocket()` (`:28`); harmless only if the inner factory returns the same socket type for both.
- `LagSocket : ISocket` wraps an inner socket. It starts its **own receive thread** (`new Thread(ReceiveLoop)`, `Thread.Sleep(1)` loop, `:131,138-155`) that polls the inner socket, applies drop and delay, and queues messages with a due time. `Poll`/`Receive` (called from Mirage's main-thread tick) hand them out once due (`:244-294`).
- Models (`LagSettings`, `:36-70`): drop chance modulated by a sine wave (`DropChance * sin(angle)`, negative half-waves mean no drops, `:210-222`); latency = base + slow sine + fast "jitter" sine + sawtooth "zag" (`:224-242`); because each packet gets its own delay, **packets can be reordered**, which is exactly what exercises Notify's late-packet logic.
- `Send` is passed straight through (`:109`): only **incoming** packets are delayed/dropped, so one-way lag requires wrapping the socket on each side that should lag.
- Thread safety: writes to `messages` are locked, but `AnyMessages` and the min-scan in `Receive` read the list unlocked (`:249-262,264-277`) while the receive thread may add, a latent race.
- `LagSocketFactoryGUI`/`LagSocketGUI` add runtime sliders (log-scale), `OnGUI` (`LagSocketFactoryGUI.cs`, `LagSocketFactory.cs:313-410`); sliders edit the shared `LagSettings` at runtime.
- Dependencies: Mirage `ISocket`, `SocketFactory`, `Pool<ByteBuffer>`, `IConnectionHandle`; no CSP types. Namespace `JamesFrowen.Mirage.DebugScripts`.

## 3. SingleInstanceDebugStart (`Debugging/SingleInstanceDebugStart.cs`)
A `NetworkManager` subclass that runs a server and a client **inside one Unity process** so Example1/2 can show server/client/"no network" copies side by side:
- Sets script-simulated physics (`:79-94`), loads the example scene **additively twice** (server scene and client scene) with `LocalPhysicsMode` so each has an isolated `PhysicsScene` (`:109,140`), optionally a third "no network" copy (`:145-150,160-173`).
- Creates one `PredictionManager` per scene (`CreateManager`, `:60-76`), the server side after a player authenticates, the client in a custom spawn handler; calls `SetClientReady(true)`/`SetServerRunning(true)` once connected (`:100-104`).
- Colors objects (server red, client green, no-network blue), toggles renderers (`ShowServer/ShowClient/ShowNoNetwork`) and installs a timestamping log handler globally via `LogFactory.ReplaceLogHandler` (`:36,45-58`).
- Static `instance` singleton (`:16,38-42`). Needs a prefab with a `Renderer` and `IDebugPredictionLocalCopy` for the third copy.
- Value as a pattern: validates the architecture's ability to run both sides in one process (local physics + per-scene manager), which also explains the `Local3D/Local2D` modes and the static-state concerns in file 01.

## 4. WorldStateDump (`Debugging/WorldStateDump.cs`)
`internal static unsafe class` that writes a tick's raw world buffer to `persistentDataPath/WorldState/{tick:D4}.data` via `UniTask.RunOnThreadPool` (`:25-29`), with `FromFile` and `ClearFolder` (moves the previous run to `WorldState_prev`, `:67-88`). Enabled only by `StateSender._dumpToFileCount`, a hard-coded `0` with a todo to expose it (`StateSender.cs:45-46,128-131,64-66`). **Hazard:** the raw `int*` is captured by the thread-pool task while the main thread keeps reusing/re-allocating the `WorldStateCopy` memory (`ToFileInternal` copies from `ptr` on another thread, `:31-57`), a use-after-free/torn-read risk if ever enabled. Only useful with a flat raw-int world buffer.

## 5. AfterImageHelper and the after-image hooks
- `AfterImageHelper.CreateAfterImage(Vector3, Color)` (`Debugging/AfterImageHelper.cs:7-30`) takes a sphere from a static pool (or creates one with its collider removed), sets colour/position and calls `HideAsync` which, after 1 s, only **enqueues it back into the pool** (`:32-36`): it never hides or disables the renderer, and `afterImage.material.color` creates a material instance per call. It is not referenced anywhere in the repo (Example2 implements its own cube-based after-image in `PredictionExample2.CreateAfterImage`, `Example2/PredictionExample2.cs:103-137`).
- The hooks: `IDebugPredictionAfterImage.CreateAfterImage(void* state, Color)` is invoked by `ClientManager.Resimulate` with the *server state applied* (orange, `ClientManager.cs:324-325`) and by `ClientController.AfterResimulate` with the *post-resim state* (blue, `ClientController.cs:62-63`), giving a visual diff of "where the server says I was" vs "where resimulation put me". This is a useful reconciliation visualisation, but it is wired through the core hot path with `is` type checks.

## 6. LogValueTracker (`Debugging/LogValueTracker.cs`)
Internal list-of-ints accumulator with `Flush(out avg, out min, out max)` (`:31-50`). `StateSender` uses it to log average/min/max delta payload size over 60 ticks (`StateSender.cs:44,177-194`). Trivially reusable.

## 7. Logging, markers and verbose dumps
- Mirage `LogFactory` named loggers per class; companion `"..._Verbose"` loggers default to `LogType.Exception` (i.e. off) and, when enabled, hex-dump each behaviour's raw state on send and receive (`ClientManager.cs:29,247-248,261-290`; `ServerManager.cs:26`; `ServerInputHandler.cs:22`; `StateSender.cs:31,115-118,298-328`). Invaluable for finding client/server layout mismatches in a raw-int design; irrelevant to a per-object field-serialised design.
- `ProfilerMarker`s `Client.Simulate` and `Client.Resimulate` (`ClientManager.cs:30-31,294,341,347,376`).
- `#if CLIENT_TICK_RUNNER_VERBOSE` writes a CSV of tick-sync internals (`TickRunner.cs:262-305,458-480`).
- `Debug_DelayInTicks`/`Debug_RTT` properties exist only under `#if DEBUG` (`TickRunner.cs:257-260`).

## 8. Reuse notes (facts, assessment in file 10)
- `LagSocket` is directly reusable for testing any Mirage netcode (fix `CreateServerSocket`, lock reads); it does not depend on prediction.
- `LogValueTracker` and the profiler-marker pattern are trivial.
- The after-image concept (draw the pre- and post-correction positions) is independent of how state is stored, but `AfterImageHelper` itself needs fixing.
- `SingleInstanceDebugStart` shows how to run both ends in one process with local physics; useful as a test scene pattern.
- `TickDebugger*` and `WorldStateDump` are specific to the existing tick runner and flat world buffer.
