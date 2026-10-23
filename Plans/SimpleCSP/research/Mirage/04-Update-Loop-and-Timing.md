# 04 - Update Loop and Timing

**Summary.** Mirage has exactly two Unity entry points: `NetworkServer.Update()` and `NetworkClient.Update()`. Each does `UpdateReceive()` (drain the socket and run *every* message handler, RPC body, SyncVar deserialize and hook inline) and then `UpdateSent()` (flush dirty SyncVars, then flush each connection's batches to the socket). There is no `FixedUpdate`, `LateUpdate`, or PlayerLoop integration, no `DefaultExecutionOrder`, and no tick concept; the relative order against your scripts' `Update` is therefore whatever Unity's (unconfigured) script execution order gives. `ManualUpdate = true` lets you drive both halves yourself.

---

## 1. Entry points (verified)

| Component | Code |
|---|---|
| `NetworkServer.Update()` (`internal`, Unity message) | `Assets/Mirage/Runtime/NetworkServer.cs:401-408`: `if (ManualUpdate) return; UpdateReceive(); UpdateSent();` |
| `NetworkServer.UpdateReceive()` (`public`) | `:410-415`: refill per-player error buckets (`UpdatePlayerErrorLimits`, `:214-219`), then `_peer?.UpdateReceive()` |
| `NetworkServer.UpdateSent()` (`public`) | `:417-421`: `SyncVarSender?.Update(); _peer?.UpdateSent();` |
| `NetworkClient.Update()` | `Assets/Mirage/Runtime/NetworkClient.cs:359-373`: if connected and **not host**, `World.Time.UpdateClient(this)` (ping scheduling) **before** `if (ManualUpdate) return;` then `UpdateReceive(); UpdateSent();` |
| `NetworkClient.UpdateReceive/UpdateSent` | `:375-380`: `_peer?.UpdateReceive()`; `SyncVarSender?.Update(); _peer?.UpdateSent()` |
| `ManualUpdate` | `public bool` marked `[HideInInspector]` on both (`NetworkServer.cs:188-189`, `NetworkClient.cs:111-112`) |

Searches found no other update hooks: `grep` for `PlayerLoop`, `FixedUpdate`, `LateUpdate`, `DefaultExecutionOrder`, `WaitForEndOfFrame`, `onBeforeRender` across `Assets/Mirage/Runtime`, `Components`, `Weaver` returns nothing relevant (`FixedUpdate` appears only in a comment in `Components/NetworkTransformBase.cs:8`). `NetworkProximityChecker` uses `InvokeRepeating` for visibility rebuilds (`Components/Visibility/NetworkProximityChecker.cs:74-90`). No `.meta` file in the package sets a script execution order (checked `executionOrder:` in `Assets/Mirage`). `ManualUpdate` is not referenced by any test or sample (only the two declarations and their `if`s) - treat as lightly exercised.

`Application.runInBackground = true` is applied when `RunInBackground` is set (default true) so the loop keeps running unfocused (`NetworkServer.cs:49-50, 345-346`; `NetworkClient.cs:51-52, 160-161`).

## 2. What happens inside one `Update`

```
Unity frame
 ├─ FixedUpdate x k      (0..n times; Mirage does nothing here)
 ├─ Update                (order between scripts is undefined unless you configure it)
 │    ├─ your scripts
 │    ├─ NetworkClient.Update          NetworkServer.Update
 │    │    ping scheduling (client)     ┌──────────────────────────────────────────────────┐
 │    │                                 │ UpdateReceive                                    │
 │    │                                 │   error-bucket refill (server)                    │
 │    │                                 │   Peer.UpdateReceive                              │
 │    │                                 │     socket.Tick(): read every pending datagram    │
 │    │                                 │       -> Peer.OnData -> Connection.Receive*       │
 │    │                                 │       -> MessageHandler -> handler / RPC /        │
 │    │                                 │          UpdateVars+hooks / spawn / destroy       │
 │    │                                 │ UpdateSent                                        │
 │    │                                 │   SyncVarSender.Update  (serialize dirty ids)     │
 │    │                                 │   Peer.UpdateSent                                 │
 │    │                                 │     per connection: timeout check, FlushBatch     │
 │    │                                 │       (reliable batch, resends, empty ack,        │
 │    │                                 │        unreliable batch), keep-alive              │
 │    │                                 │     socket.Flush()                                │
 │    │                                 └──────────────────────────────────────────────────┘
 └─ LateUpdate            (your scripts; Mirage nothing)
```

Code path for receive (`Assets/Mirage/Runtime/SocketLayer/Peer.cs:261-317`): `_socket.Tick()` -> for UDP, `while (socket.Poll(0, SelectRead)) ReceiveFrom(...) -> _onData` (`Sockets/Udp/UdpSocket.cs:105-124`) -> `Peer.OnDataEvent/OnData` -> connection lookup -> `HandleMessage` switches on `PacketType` (`Peer.cs:436-510`) -> `Connection.ReceiveReliablePacket/ReceiveUnreliablePacket/ReceiveNotifyPacket` (`ReliableConnection.cs:91-122, 261-279`) -> `HandleReliableBatched` splits the batch (`Connection.cs:241-276`) -> `IDataHandler.ReceiveMessage` = `NetworkServer.DataHandler` / `NetworkClient.DataHandler` (`NetworkServer.cs:705-735`, `NetworkClient.cs:446-471`) -> `MessageHandler.HandleMessage` -> registered delegate. Everything is synchronous on the main thread; a try/catch around the handler protects the socket layer (`NetworkServer.cs:721-729`). The loop drains **all** available datagrams - there is no per-frame cap.

Code path for send (`Peer.cs:251-256, 764-776`): `UpdateConnections` -> `Connection.Update` -> `UpdateConnected` (`Connection.cs:192-215`): timeout check -> `FlushBatch()` -> keep-alive. `ReliableConnection.FlushBatch` = `_ackSystem.Update()` (flush reliable batch, run resends, maybe empty ack; `AckSystem.cs:227-241`) **then** `_unreliableBatch.Flush()` (`ReliableConnection.cs:286-290`). Finally `_socket.Flush()` (no-op for managed UDP, `UdpSocket.cs:125`).

## 3. Direct answers

**When are SyncVars flushed?** At `SyncVarSender.Update()`, which runs at the start of `UpdateSent`, i.e. once per `NetworkServer.Update` / `NetworkClient.Update`, after that same call's `UpdateReceive`. Per component the `SyncSettings` gate applies (default: at most every 0.1 s; `NoInterval` -> every flush). The serialized `UpdateVarsMessage` is *enqueued* there and leaves the process in the immediately-following `Peer.UpdateSent` (`NetworkServer.cs:417-421`, `SyncVarSender.cs:26-72`).

**When do message handlers / RPCs / hooks run?** Only inside `Peer.UpdateReceive` at the beginning of Mirage's `Update`. ServerRpc bodies on the server, ClientRpc bodies on the client, SyncVar `OnDeserialize` + hooks, spawn/destroy/hide handlers, `OnStartClient`/`OnAuthorityChanged` events and custom `NetworkMessage` handlers all run there, in arrival order, interleaved. Host-mode callbacks are the exception: they run inline in the caller's stack (file 03 section 11).

**When do my sends hit the wire?**

| You call it from | Reliable / Unreliable message or RPC | SyncVar change | Notify |
|---|---|---|---|
| `FixedUpdate` | same frame's Mirage `Update` (Unity runs FixedUpdate before Update) | same | immediately |
| `Update`, script runs **before** Mirage's `Update` | same frame | same | immediately |
| `Update`, script runs **after** Mirage's `Update` | next frame | next frame | immediately |
| `LateUpdate` | next frame | next frame | immediately |
| inside a Mirage handler (during `UpdateReceive`) | same `Update`'s `UpdateSent` | same | immediately |

(An early flush happens when a reliable or unreliable batch fills to the MTU: `Batch.cs:23-40`.) The "FixedUpdate precedes Update" row is Unity's documented order, not Mirage code.

## 4. Implications for a fixed-tick CSP loop

1. **Mirage is frame-driven; your tick is not.** Outbound data and inbound dispatch happen once per rendered/served frame. A server that runs at 30 fps (e.g. `HeadlessFrameLimiter.serverTickRate = 30`, `Components/HeadlessFrameLimiter.cs:15-27`) can send/receive at most 30 times per second regardless of `Time.fixedDeltaTime`.
2. **Fixed-tick simulation in `FixedUpdate`** (typical): inputs received in frame N's `UpdateReceive` are first visible to the sim in frame N+1's `FixedUpdate` steps (<= 1 frame + up to one fixed step of added latency). State produced in frame N's `FixedUpdate` steps reaches `SyncVarSender`/the batch before frame N's flush (FixedUpdate always precedes Update), so sim -> wire has no extra frame. If a long frame runs `k > 1` fixed steps, you enqueue `k` messages (they batch into the same packet) but a SyncVar only conveys the last value.
3. **Fixed-tick simulation in `Update`** (your own accumulator): ordering vs Mirage depends on script execution order. Run your driver *before* Mirage's `Update` to flush this frame's output now but consume this frame's inputs next frame; run it *after* for the opposite. This choice is not set anywhere by the package.
4. **Deterministic order options**: (a) set `NetworkServer`/`NetworkClient` execution order relative to your driver in Project Settings > Script Execution Order (not in the package, so must be done by the consuming project); (b) set `ManualUpdate = true` and call `UpdateReceive()` at the start of your tick/frame step and `UpdateSent()` at the end, giving the order *receive -> consume inputs -> simulate -> produce output -> flush*. If you do (b) you must call both every frame: connection timeouts (`TimeoutDuration = 10 s`), keep-alives (`KeepAliveInterval = 2 s`), ack flushing and retransmits live in `UpdateSent`/`UpdateReceive` (`Config.cs:36-41`, `Connection.cs:192-215`). On the client, `NetworkClient.Update` still handles ping scheduling even with `ManualUpdate` (it is above the early return).
5. **Retransmit latency has a frame floor**: reliable resends are issued from `AckSystem.Update` in `UpdateSent` (once per frame), driven by acks that arrive in `UpdateReceive` - loss recovery is roughly `RTT + 1-2 frames` (Inferred, not measured) (+ up to `TimeBeforeEmptyAck = 0.11 s` if the peer had nothing else to send and delays the ack, `Config.cs:72`).
6. **Intervals use unscaled real time** (`Time.unscaledTimeAsDouble`): SyncVar intervals, rate limits and timeouts ignore `Time.timeScale` (`SyncVarSender.cs:36`, `Peer.cs:11-14`). Pausing/slow-mo your sim doesn't pause Mirage timers.
7. **No cap on work per frame**: a burst of N queued datagrams runs N handler invocations in one `UpdateReceive`. A client hitch followed by flood of unreliable state messages is processed in a single frame; your tick logic should be robust to receiving many ticks at once.
8. **Host mode**: `NetworkClient.Update` in host mode has no peer (no-op `UpdateReceive/Sent`, `_peer == null`); local traffic is delivered inline and never waits for a frame (file 03 section 11). The host client also skips ping (`NetworkClient.cs:361-366`).

## 5. Time-related settings summary

| Setting | Default | Effect | Source |
|---|---|---|---|
| `SyncSettings.Interval` / `Timing` | 0.1 s / `Variable` | SyncVar/OnSerialize rate (per component) | `SyncSettings.cs:24-29` |
| `Config.KeepAliveInterval` | 2 s | keep-alive after idle send | `Config.cs:36` |
| `Config.TimeoutDuration` | 10 s | disconnect without any received packet | `Config.cs:41` |
| `Config.TimeBeforeEmptyAck` / `EmptyAckLimit` / `ReceivesBeforeEmptyAck` | 0.11 s / 8 / 8 | ack cadence | `Config.cs:72-85` |
| `Config.ConnectAttemptInterval` / `MaxConnectAttempts` | 0.25 s / 10 | client connect | `Config.cs:24-29` |
| `NetworkTime.PingInterval` | 2 s | RTT/clock samples | `NetworkTime.cs:19` |
| `RpcHandler.DESTROY_GRACE_PERIOD` | 1 s | ignore RPCs to just-destroyed/de-authorized objects | `RemoteCalls/RpcHandler.cs:14` |
| `NetworkProximityChecker.VisibilityUpdateInterval` | 1 s | observer rebuild | `NetworkProximityChecker.cs:65` |

## 6. NetworkTime / ping - pointer only (another agent covers this)

- **Where:** `NetworkTime` (`Assets/Mirage/Runtime/NetworkTime.cs`), one instance per `NetworkWorld` (`NetworkWorld.cs:49`). Reach it via `NetworkBehaviour.NetworkTime`, `NetworkClient.World.Time`, `NetworkServer.World.Time`.
- **Exposes:** `Time` (double seconds; `Stopwatch` local time minus an EMA-smoothed clock offset, cached per Unity frame; on the server offset is 0), `Offset`, `Rtt`, `RttVar/RttSd`, `TimeVar/TimeSd`, `PingInterval`, `PingWindowSize`, `PingNow(IMessageSender)`, `Reset()` (`NetworkTime.cs:19-24, 49-77, 158-213`).
- **Mechanism:** client sends `NetworkPingMessage` on `Channel.Unreliable` when connected and then every 2 s from `NetworkClient.Update`; server replies with `NetworkPongMessage` (Unreliable); client updates RTT/offset EMAs (`NetworkTime.cs:57-127`, `NetworkClient.cs:190-197, 359-366, 382-385`, `NetworkServer.cs:310`).
- **Not** tick-based, **not** locked to server frames, sampled every 2 s only. Host: no pings, offset 0.
