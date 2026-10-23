# 07 - Messages and Protocol

**Summary.** CSP defines three `[NetworkMessage]` structs in `Messages.cs`: `InputState` (client to server, every tick, Notify channel), `DeltaWorldState` (server to each client, every tick, Notify channel, falling back to reliable fragmented sends when over 1193 payload bytes) and `DeltaWorldStateFragmentedAck` (client to server, reliable). Mirage's own ping/pong (at CSP's 0.2 s interval) and a debug-only `TickDebugger` RPC pair are the only other traffic CSP causes. Both the inputs and the world state use Mirage's Notify channel specifically for its *delivered/lost callbacks*, which drive input redundancy and the delta base tick. All sizes below are estimates from field types (bit-packed Mirage serialization), not measurements.

## 1. Message table

| Message | File:line | Direction | Channel | Frequency | Fields | Approx. wire size |
|---|---|---|---|---|---|---|
| `InputState` | `Assets/source/Runtime/Messages.cs:49-66` | client -> server | **Notify** (unreliable + delivered/lost token `TickNotifyToken(clientManager, tick)`) | 1 per client tick (50/s default) | `int Tick`; `double ClientTime`; `bool Ready`; `int NumberOfInputs` (`[BitCountFromRange(1,8)]`); `ArraySegment<byte> Payload` | 2 (msg id) + 4 + 8 + ~0.5 + 1-2 (length) + payload; payload = per owned behaviour: ref (packed NetId + 1 byte) + `NumberOfInputs` x input size. Example3 player at ~6 inputs: ~100 B payload. Plus 13 B Notify header. **~130 B/packet, ~6.5 KB/s** (estimate) |
| `DeltaWorldState` (with payload) | `Messages.cs:16-38` | server -> each ready client | **Notify** if `payload <= 1193 B` else **Reliable (fragmented)** with `Fragmented = true` | 1 per server tick per ready player | `int Tick`; `int? VsTick`; `float? TimeScale`; `double ClientTime`; `int StateIntSize`; `bool Fragmented`; `ArraySegment<byte> DeltaState` | ~24-25 B header + payload (`MESSAGE_HEADER = 26` "rough guess", `StateSender.cs:27`) + 13 B Notify header. Payload = delta-compressed whole world (file 04): ~1 KB at 32 moving players (estimate) |
| `DeltaWorldState` (header only) | same | server -> client not yet `Ready` | Notify with `TickNotifyToken(null, tick)` | 1 per tick per not-ready player (in host mode this presumably includes the host's own connection, inferred from `PredictionManager.cs:166-167`) | same fields, `DeltaState` = default (null array), `VsTick`/`StateIntSize` = default | ~25 B + 13 B. Used to bootstrap the client clock before it is ready |
| `DeltaWorldStateFragmentedAck` | `Messages.cs:40-44` | client -> server | Reliable (`player.Send(msg)`, `ClientManager.cs:161`) | once per received fragmented state | `int Tick` | 2 + 4 B |
| `NetworkPingMessage` / `NetworkPongMessage` (Mirage, not CSP) | `Mirage/Runtime/Messages.cs:169-184` | client -> server / server -> client | Unreliable (`Mirage/Runtime/NetworkTime.cs:75,92`) | every `World.Time.PingInterval`; CSP sets 0.2 s (`PredictionManager.cs:177`) i.e. 5/s each (Mirage default is 2 s) | `double ClientTime` / `double ClientTime, double ServerTime` | ~10 B / ~18 B. CSP does not consume Mirage's RTT (it measures its own through `ClientTime` echo) |
| `TickDebugger.ToClient_StateMessage` / `ToServer_InputMessage` (debug RPCs) | `Assets/source/Runtime/Debugging/TickDebugger.cs:74-89` | both | Unreliable RPC | every tick | `int tick, double clientTime` | tiny; only if the `TickDebugger` component is used instead of the real managers |
| Mirage spawn/despawn/authority/SyncVar messages | Mirage | server -> client | Reliable | on events | n/a | Unchanged Mirage behaviour; CSP only observes spawns via `NetworkWorld.onSpawn` |

`SimulationMode` in the same file is just an enum (`Messages.cs:68-74`). The doc comment on `InputState.Payload` references a non-existent `InputMessage` type (`:63`), a leftover from an earlier format.

## 2. Payload formats

### `InputState.Payload`
Bit-packed `NetworkWriter` content (`ClientManager.SendReadyInputs`, `Assets/source/Runtime/ClientManager.cs:442-461`), repeated for each owned behaviour:
```
[ NetworkBehaviour reference: PackedUInt32(netId) + byte componentIndex ]
[ TInput for tick T     ]   // newest first
[ TInput for tick T-1   ]
...                         // NumberOfInputs entries in total, max 8
```
No per-behaviour length prefix: the server reads until `reader.CanReadBytes(1)` is false (`ServerInputHandler.cs:56`) and reads exactly `NumberOfInputs` inputs per behaviour. Input types are serialised by the weaver generated writer for `TInput`.

### `DeltaWorldState.DeltaState`
Output of `IDeltaSnapshot.WriteDelta(writer, StateIntSize, base*, to*)` over the flat world buffer (file 04): for the default codec, a sequence of `PackedUInt32` run lengths (alternating zero-diff / non-zero-diff runs, starting with zeros) covering all `StateIntSize` ints, followed by one `PackedInt32` per non-zero diff. `VsTick == null` means the base is an all-zero buffer (full state). The flat buffer is `[Header{NetId,IntSize}][behaviour state...]` for each identity in server spawn order.

`StateIntSize` (the size of the *target* world buffer in ints) lets the receiver size its `WorldStateCopy` before decoding (`ClientManager.cs:196-197`).

## 3. Channel semantics CSP relies on

- **Notify** (Mirage `AckSystem.SendNotify`, `Mirage/Runtime/SocketLayer/Connection/AckSystem.cs:314-341`): unreliable, each packet carries 16-bit sequence + latest ack + 64-bit ack mask (13 B header); the sender gets `OnDelivered`/`OnLost` per packet; the receiver drops duplicate/late packets (`ReceiveNotify`, `:454-475`) but still acks them (`SetAckValues`, `:570-598`). Max payload per message = MTU 1232 - 13 = 1219 bytes (`UdpMTU.MaxPacketSize`, `Mirage/Runtime/Sockets/Udp/UdpSocketFactory.cs:221-239`). Oversize throws `MessageSizeException`, `BufferFull` disconnects the player (`NetworkPlayer.cs:312-324`).
- **Why Notify:** `TickNotifyToken` pairs a tick with the tracker. Server -> `PlayerTimeTracker.LastAckedTick` (delta base); client -> `ClientManager.ackedInput` (input redundancy). `OnLost` is ignored (just recycles the pooled token, `ITickNotifyTracker.cs:48-51`): the design reacts only to *delivered*.
- **Reliable fragmented fallback** (`StateSender.cs:161-169`): requires `Config.MaxReliableFragments > 0` (Mirage default 50, `SocketLayer/Config.cs:106`) else Mirage throws "fragmentation is disabled" (`AckSystem.cs:351-355`). Messages above `MaxReliableFragments * SizePerFragment` also throw (`:377-379`).
- **Ordering across channels is not guaranteed:** Mirage reliable spawn messages and Notify state messages are independent streams (see file 01 for the spawn race).

## 4. Round trip, annotated

```mermaid
sequenceDiagram
  participant C as Client (tick Tc)
  participant S as Server (tick Ts)
  Note over C,S: Client lead L = Tc - Ts approx RTT in ticks + jitter margin + 1
  C->>S: InputState(Tick=Tc, ClientTime=wallclock, inputs Tc..Tc-n+1)  [Notify, every tick]
  Note over S: arrives while server is at Ts < Tc (early); stored in ring
  S->>S: ... tick Tc reached: ApplyInputs(inputs[Tc]); simulate; copy world; delta vs LastAckedTick
  S-->>C: DeltaWorldState(Tick=Tc, VsTick=base, ClientTime=last received client wallclock, delta)  [Notify, every tick]
  C->>C: decode vs WorldStateCopy[base]; memcmp vs predicted state[Tc]; if differs -> resim from Tc+1
  C->>C: clientTickRunner.OnMessage(Tc, ClientTime): RTT = now - ClientTime; adjust speed / skip
  Note over S: transport ack of that state packet -> TickNotifyToken.OnDelivered -> LastAckedTick = max(.., Tc)
  Note over C: transport ack of InputState packet -> ackedInput = max(.., T)
  opt payload > 1193 B
    S-->>C: DeltaWorldState(Fragmented=true) [Reliable, fragmented]
    C-->>S: DeltaWorldStateFragmentedAck(Tick) [Reliable]
    Note over S: LastAckedTick = max(.., Tick)
  end
```

Notes:
- `ClientTime` in `DeltaWorldState` is the **last** `InputState.ClientTime` the server received from that player (`StateSender.cs:83-84`, `ServerInputHandler.cs:36`), not the one for the matching tick, so the measured RTT includes however long the server waited between receiving the input and sending the next state (up to about one tick). The client then averages it (see `TickRunner-and-TimeSync.md`).
- Both directions send **every tick** regardless of change: ~50 + 50 packets/s per client (+ 5 + 5 Mirage ping/pong per second at CSP's setting).
- There is no explicit handshake: "Ready" is carried inside every `InputState`; the server flips `tracker.ReadyForWorldState` from it (`ServerInputHandler.cs:41`), and the client's tick runner only starts after the first state message.
- Host mode: no `InputState`/payload traffic is generated for the host; the server still sends it header-only state messages, which a no-op handler swallows (`PredictionManager.cs:166-167`).

## 5. Bandwidth summary (estimates, 50 Hz, 32 players)

| Stream | Per client | Server total (32 clients) |
|---|---|---|
| Inputs (client -> server), ~100 ms RTT, Example3-like input | ~6.5 KB/s | ~210 KB/s |
| World state (server -> client), all players moving | ~40-60 KB/s | ~1.5-2 MB/s |
| Ping/pong | ~0.15 KB/s | ~5 KB/s |

These are derived from the encodings above and the Example3 state sizes; the repo contains a bandwidth test (`Assets/Tests/DeltaSnapshotTest.cs`, `DeltaSnapshotTest_Bandwidth`) but it only logs and no results are recorded.
