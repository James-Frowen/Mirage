# 03 - RPCs and Messages

**Summary.** `[ServerRpc]`/`[ClientRpc]` are weaver-generated wrappers that serialize arguments into an `RpcMessage { NetId, FunctionIndex, Payload }` and send it with `Channel.Reliable` or `Channel.Unreliable` (default Reliable); the receiver dispatches by `NetId` + function index, checks ownership/rate limits, and runs the body **inline inside `UpdateReceive`**. Custom `[NetworkMessage]` structs are registered per `MessageHandler` and sent via `INetworkPlayer.Send / NetworkClient.Send / NetworkServer.SendTo*`. There are three transport modes: Reliable (ordered, batched, fragmentable), Unreliable (batched, unsequenced, MTU-limited) and **Notify** (sequenced-unreliable with delivered/lost callbacks; only reachable through the `Send(msg, INotifyCallBack)` overloads, not through RPC attributes).

---

## 1. Attribute options

| Attribute | Option | Default | Meaning | Source |
|---|---|---|---|---|
| `[ServerRpc]` | `channel` | `Reliable` | `Reliable` or `Unreliable` | `Assets/Mirage/Runtime/CustomAttributes.cs:101-104` |
| | `requireAuthority` | `true` | Only the object's `Owner` may call; `false` lets any authenticated player (add an `INetworkPlayer sender` parameter to know who) | `:105`, check at `RpcHandler.cs:200-212` |
| | `allowServerToCall` | `false` | Server (even dedicated) may invoke the body locally; bypasses authority checks | `:106-109`, `ServerRpcSender.cs:80-99` |
| `[ClientRpc]` | `channel` | `Reliable` | | `CustomAttributes.cs:159-166` |
| | `target` | `Observers` | `Observers` / `Owner` / `Player` | `:138-152` |
| | `excludeOwner` | `false` | Skip the owner (only with `Observers`; `Owner + excludeOwner` is a weaver error) | `ClientRpcProcessor.cs:259-272` |
| | `excludeHost` | `false` | Do not run the body on the host (still sends to remotes) | `ClientRpcProcessor.cs:93-94` |
| `[RateLimit]` | `Interval`=1 s, `Refill`=50, `MaxTokens`=200, `Penalty`=1 | opt-in, **ServerRpc only** | token bucket per (player, RPC) | `CustomAttributes.cs:113-133`, `ServerRpcProcessor.cs:159-178` |

Method rules enforced by the weaver (`Assets/Mirage/Weaver/Processors/RpcProcessor.cs:254-359`): not static/abstract/generic/coroutine; no `out` or optional parameters (except `INetworkPlayer`); `ClientRpc` returns `void` for `Observers`, `UniTask<T>` allowed only for `Owner`/`Player`; `ServerRpc` returns `void` or `UniTask<T>`; `RpcTarget.Player` requires `INetworkPlayer` as the **first** parameter. Class-typed parameters give a warning (allocation). Bit-packing attributes work on RPC parameters (file 05).

`INetworkPlayer` parameters are never serialized: in a ServerRpc the skeleton injects the **sender**; in a ClientRpc with `Player` target the first parameter selects the recipient and is replaced with `Client.Player` on the receiving client (`RpcProcessor.cs:185-248`, `ClientRpcSender.cs:28-38`).

## 2. What the weaver generates

For `void Foo(int a)` it (a) moves your code to `UserCode_Foo_<hash>`, (b) replaces `Foo` with a stub that serializes args into a pooled writer and calls `ServerRpcSender.Send` / `ClientRpcSender.Send|SendTarget`, and (c) adds a `Skeleton_Foo_<hash>` that reads args and calls `UserCode_*`; skeletons are registered in `RegisterRpc` (`RpcProcessor.cs:79-124, 385-412`, `Assets/Mirage/Weaver/Processors/NetworkBehaviourProcessor.cs:99-119`). Function indices are per `NetworkIdentity`: `IndexOffset[componentIndex] + relativeIndex`, computed lazily by `RemoteCallCollection.RegisterAll` (`Assets/Mirage/Runtime/RemoteCalls/RemoteCallHelper.cs:21-41, 123-142`). So both sides need the same component order and RPC set (a mismatch yields `RpcSync` errors, see section 6). Calls to a base-class RPC from an override are rewritten to `UserCode_` (`RpcProcessor.cs:420-457`).

## 3. Sending

### ServerRpc (client -> server)
`ServerRpcSender.Send` (`Assets/Mirage/Runtime/RemoteCalls/ServerRpcSender.cs:12-25`): `Validate` requires an active client, `HasAuthority` when `requireAuthority`, and a non-null `Client.Player` (`:50-71`), then `behaviour.Client.Send(RpcMessage, channelId)`. Return-value version uses `RpcWithReplyMessage` and is **always Reliable** (`:27-48`).

### ClientRpc (server -> client)
- `Observers`: `identity.SendToRemoteObservers(msg, includeOwner: !excludeOwner, channel)` = `NetworkServer.SendToObservers(identity, msg, excludeLocalPlayer: true, excludeOwner, channel)` -> serialize once, loop `observers` minus host (and owner if excluded) (`ClientRpcSender.cs:15-26`, `NetworkServer.cs:617-629, 644-666, 748-751`). The owner is in `observers` only if scene-ready (file 01).
- `Owner` / `Player`: `player.Send(msg, channel)` directly. `GetTarget` falls back to `behaviour.Owner` and **throws** if null (`:63-74`). There is **no observer or scene-ready check**, despite the enum doc saying the target must be an observer (`CustomAttributes.cs:149`).
- `Validate` only checks that the server is active (`:88-96`). Calling a ClientRpc on an object that isn't spawned throws.

## 4. Receiving and dispatch

1. Packet -> `MessageHandler.HandleMessage` reads the 16-bit message id and calls the registered handler (`MessageHandler.cs:101-130, 165-192`).
2. Server registers `RpcMessage`, `RpcWithReplyMessage`, `RpcReply` in `ServerObjectManager.ServerStarted` (`ServerObjectManager.cs:57-58`, `RpcHandler.cs:42-47`); clients in `ClientObjectManager.RegisterMessageHandlers` (`ClientObjectManager.cs:123-126`). **Host mode registers none of these on the client** (`:106-113`).
3. `RpcHandler.HandleRpc` (`RpcHandler.cs:75-198`):
   - null payload array -> `DeserializationException` error (cost 50);
   - `TryGetIdentity(netId)` fails -> if destroyed within `DESTROY_GRACE_PERIOD` (1 s) just warn, otherwise cost-1 error (`:84-114`);
   - function index out of range -> `RpcSync` cost 50; wrong direction (server RPC index delivered to client or vice versa) -> cost 50 (`:116-129`);
   - ServerRpc authority: `identity.Owner == sender` unless `requireAuthority == false`; failure within 1 s of `AuthorityRemovedTime` is only a warning, otherwise cost-10 `NoAuthority` (`:131-155`);
   - `[RateLimit]`: drop and charge `Penalty` (`:157-166`, `NetworkPlayer.cs:528-552`);
   - invoke `remoteCall.Invoke(reader, sender, replyId)` inside try/catch: `EndOfStreamException` -> cost 50; `NullReference/MissingReference` -> cost 1; other -> cost 2 (`:170-195`). **A throwing RPC body does not disconnect**; it only spends error tokens (which can eventually trigger `ErrorRateLimitReached` on the server).
4. On a client, `ClientObjectManager.OnRpcMessage` first checks `pendingSpawn` and queues the RPC if that object is still being async-spawned (`ClientObjectManager.cs:132-152`).
5. Execution timing: all of the above runs synchronously inside `Peer.UpdateReceive` (file 04). RPC bodies therefore execute at the **start of the Mirage update**, before `SyncVarSender.Update`/flush in the same call to `NetworkServer.Update`.

If a ClientRpc arrives for a `NetId` the client doesn't have (not yet spawned, hidden, or already destroyed) it is dropped with a warning (`RpcHandler.cs:100-111`); there is no buffering except async-spawn pending queues. An Unreliable RPC can overtake the Reliable spawn message and be lost this way.

## 5. Return-value RPCs

`UniTask<T>` return: caller gets a `UniTask` completed when an `RpcReply { ReplyId, Success, Payload }` arrives; `CreateReplyTask` records the expected sender and rejects spoofed replies (`RpcHandler.cs:221-283`). Request and reply are **always Reliable** (`ServerRpcSender.cs:44-45`, `ClientRpcSender.cs:57-58`, `RemoteCallHelper.cs:100-111`). If the call is dropped (rate limit, auth, bad index) the server sends a failure reply (`RpcHandler.cs:54-67`). The ClientRpc form is only allowed for `Owner`/`Player`. Not useful for per-tick traffic (reliable + async).

## 6. Wire format and per-call overhead

`RpcMessage` = `[id:16 bits][NetId: packed uint][FunctionIndex: packed zigzag int][Payload: packed (len+1) | pad | bytes]` (`RemoteCalls/RpcMessages.cs:6-11`; encodings in file 05). With small ids that is roughly **5-9 bytes of header + up to 7 bits padding** on top of the argument bytes, plus **2 bytes** batch length prefix, plus the per-packet header (1 byte unreliable / 15 bytes reliable) shared by everything batched into that packet. Arguments are written to a pooled writer, then copied into the message writer (a second copy).

Message ids: `type.FullName.GetStableHashCode() & 0xFFFF`, written as fixed `UInt16` (`Assets/Mirage/Runtime/Serialization/MessagePacker.cs:74-80, 97`; the "varint header" comment at `:19-25` is stale). Colliding ids throw at registration (`:48-57`).

## 7. Channels in detail

`Channel` has exactly two values, `Reliable = 0`, `Unreliable = 1` (`Assets/Mirage/Runtime/Channel.cs`); any value other than `Reliable` is treated as unreliable (`NetworkPlayer.cs:271-278`). Notify is a third path.

### Reliable
- `ReliableConnection.SendReliable` -> `AckSystem.SendReliable`: message appended to the connection's current `ReliableBatch` packet (header 15 B: type, seq, ack, ack-mask(8), order; each message prefixed by a 2 B length) (`AckSystem.cs:12-29, 344-365`, `Batch.cs:23-40`).
- If a message doesn't fit the current packet the batch is sent first and a new one started (`Batch.cs:27-34`). Messages larger than `MTU-17` are **fragmented** (current batch is flushed first so ordering holds) (`AckSystem.cs:351-362, 375-402`).
- Receive side re-inserts packets into a ring buffer by `order` and only releases the **next in-order packet** (`AckSystem.cs:182-196, 498-541`, `ReliableConnection.cs:124-138`). A lost packet therefore **blocks every later reliable message** (SyncVars, RPCs, spawns, reliable custom messages) until it is retransmitted: classic head-of-line blocking. `PacketType.Reliable` doc says so explicitly (`Enums/PacketType.cs:22-24`).
- Loss detection is driven by the ack bitmask carried on incoming packets: a packet is retransmitted when a later ack covers its sequence but its bit is 0 (`AckSystem.cs:636-692`); retransmits are sent from `AckSystem.Update` once per frame (`:227-241, 694-725`). I found **no timer-based retransmit**; a lost *last* packet is not resent until the sender emits another ackable (reliable/notify) packet and gets an ack back (Inferred; unverified in-engine). Empty acks go out after `TimeBeforeEmptyAck = 0.11 s` (up to 8) or after 8 receives (`Config.cs:72-85`).
- Buffer limits: `MaxReliablePacketsInSendBufferPerConnection = 2000`, `SequenceSize = 12` (4096-entry ring); overflow throws `BufferFullException`, which `NetworkPlayer.Send` catches and answers by **disconnecting the player** (`SendBufferFull`) (`Config.cs:92-99`, `AckSystem.cs:346-349, 439-446`, `NetworkPlayer.cs:280-284`).

### Unreliable
- `ReliableConnection.SendUnreliable` -> `ArrayBatch` of `PacketType.Unreliable` (1-byte header + [2 B length + message]...). If `length + 3 > MTU` -> `MessageSizeException` (`ReliableConnection.cs:77-89`, `Batch.cs:58-90`).
- No sequence numbers: may be lost, duplicated, reordered. Handled immediately on arrival, in packet order (`ReliableConnection.cs:91-94`, `Connection.cs:241-276`). Mirage does not dedupe or reorder.
- Flushed when the batch fills or at `FlushBatch`, **after** the reliable batch/resends/acks of that connection (`ReliableConnection.cs:286-290`).

### Notify (sequenced, unreliable, with delivered/lost callbacks)
- API: `INetworkPlayer.Send<T>(T, INotifyCallBack)` (`INetworkPlayer.cs:25`, `NetworkPlayer.cs:298-325`), `NetworkClient.Send<T>(T, INotifyCallBack)` (`NetworkClient.cs:349-357`), and lower-level `player.Connection.SendNotify(...)` returning `INotifyToken` (allocates) or taking `INotifyCallBack` (no alloc) (`IConnection.cs:28-30`, `ConnectionExtensions.cs:16-32`). Sample: `Assets/Mirage/Samples~/Notify/NotfyExample.cs:17-120` (sends only the values the peer hasn't acknowledged yet).
- Not batched: each notify is its own packet with a 13 B header (`AckSystem.NOTIFY_HEADER_SIZE`), written to the socket **immediately** at call time (`AckSystem.cs:314-342` -> `Send` `:273-277` -> `Peer.Send` -> `socket.Send`).
- Receive: duplicates and *late* (older than the newest received) notify packets are **dropped**, so delivery is monotonically ordered but lossy (`AckSystem.cs:454-478`, `ReceiveNotifyPacket` `ReliableConnection.cs:261-279`). The message then goes through the same `MessageHandler` as any other message.
- `OnDelivered`/`OnLost` are invoked on the sender when an ack covering the packet is processed in `UpdateReceive` (`AckSystem.cs:636-666`); lost = not in the mask or beyond 64 packets. A token can be notified only once (`NotifyToken.cs`).
- Notify shares the sent-packet ring buffer with reliable (4096 entries); full -> `BufferFullException` -> player disconnected (above).
- Max size `MTU-13`; oversized -> `MessageSizeException` (`AckSystem.cs:316-319`), which is **not** caught by `NetworkPlayer.Send` (it only catches `BufferFullException` and `NoConnectionException`, `NetworkPlayer.cs:310-323`).

### Alternative: no reliable layer
`Config.DisableReliableLayer` (set via `NetworkServer/NetworkClient.PeerConfig`, for TCP/WebSocket) switches to `NoReliableConnection`, which maps **Unreliable and Notify onto Reliable** and reports Notify as delivered immediately; the unreliable/notify receive paths throw `NotSupported` (`Config.cs:109-111`, `Peer.cs:680-695`, `NoReliableConnection.cs:31-44`).

### Summary table

| | Reliable | Unreliable | Notify |
|---|---|---|---|
| Ordered | yes (stream) | no | monotonic (late dropped) |
| Lossless | yes (retransmit) | no | no (but you are told) |
| Duplicates | no | possible | dropped |
| Batched | yes, with other reliable | yes, with other unreliable | no (1 message/packet) |
| Leaves the process | at flush (`UpdateSent`) or when batch fills | same | immediately at `Send` |
| HOL blocking | yes | no | no |
| Max message (UDP default) | 1215 B, up to 60,800 B fragmented | 1229 B | 1219 B |
| Used by | SyncVar, spawn, default RPC/messages, return RPC | `NetworkPing/Pong`, opt-in RPC/messages | opt-in messages only |

### Max sizes (UDP defaults, derived)
`UdpMTU.MaxPacketSize = 1280 - 48 = 1232` (`Assets/Mirage/Runtime/Sockets/Udp/UdpSocketFactory.cs:221-239`). Header constants `AckSystem.cs:12-29`; `Peer.GetMax*MessageSize` `Peer.cs:835-881`:
- Unreliable: `1232 - 3 = 1229` bytes including the 2-byte message id.
- Notify: `1232 - 13 = 1219`.
- Reliable unfragmented: `1232 - 17 = 1215`. Fragmented: `MaxReliableFragments (50) * (1232 - 16) = 60,800` (configurable up to 255; 0 disables) (`Config.cs:106`, `Peer.cs:80-81`).
- `NetworkWriter` grows up to 512 KiB (`NetworkWriter.cs:43, 109-134`) and logs a warning each time it resizes (pool buffers start at 1232 B).

## 8. Custom NetworkMessages

- Define `[NetworkMessage] public struct M { ... }` (`CustomAttributes.cs:172`). The attribute is optional for types used with `Send<T>/RegisterHandler<T>/SendToAll/SendToMany` because the weaver scans call sites (`ReaderWriterProcessor.cs:191-211, 296-311`). Open generics are rejected (`:195-198`).
- Register on the correct handler: `Server.MessageHandler.RegisterHandler<M>((player, msg) => ...)` / `Client.MessageHandler.RegisterHandler<M>(msg => ...)`. Overloads: with/without player, async `UniTaskVoid`, `allowUnauthenticated` (`Assets/Mirage/Runtime/MessageReceiverExtensions.cs:12-57`, `MessageHandler.cs:26-40`). **`MessageHandler` is created on `StartServer` / `Connect`** (`NetworkServer.cs:309`, `NetworkClient.cs:144`) so register from `Server.Started` / `Client.Started` (late events), and again each session. Registering a second handler for the same type **replaces** the first.
- Handlers for unauthenticated players: not invoked; the player is disconnected with `Unauthenticated` (`MessageHandler.cs:194-216`).
- Unknown message type: cost 20 (known struct, no handler) or 60 (unknown id) error tokens (`:165-192`).
- Sending:

| API | Target | Notes |
|---|---|---|
| `NetworkClient.Send<T>(msg, Channel)` | server | `NetworkClient.cs:326-337` |
| `INetworkPlayer.Send<T>(msg, Channel)` | one player | `NetworkPlayer.cs:239-252`; raw `Send(ArraySegment<byte>, Channel)` needs your own id prefix (`:264-290`) |
| `NetworkServer.SendToAll(msg, authenticatedOnly, excludeLocalPlayer, Channel)` | all players | **Bug: when `authenticatedOnly` is true the `channelId` is not forwarded**, so it always goes Reliable (`NetworkServer.cs:559-564`) |
| `NetworkServer.SendToMany(list, msg[, excludeLocal], Channel)` | list | static version packs once then `Send(segment)` per player (`:644-666`) |
| `NetworkServer.SendToObservers(identity, msg, excludeLocalPlayer, excludeOwner, Channel)` | `identity.observers` | `:617-629` |
| `LobbyReady.SendToReady` | ready players | `Assets/Mirage/Components/LobbyReady.cs:81-106` |

- A message is routed by **type only**; Mirage knows nothing about objects. To address a per-object buffer, include `NetId` (+ component index) yourself, or use fields of type `NetworkIdentity`/`NetworkBehaviour` which serialize as packed netId (+1 byte component index) and resolve through the reader's `IObjectLocator`, yielding `null` if the object isn't spawned on that side yet (`Assets/Mirage/Runtime/Serialization/MirageTypesExtensions.cs:11-98`). Your handler must validate `identity.Owner == player` on the server (hint at `NetworkPlayer.cs:141`).
- Handler exceptions: caught in `MessageHandler`, logged, `SetError(10)`; **on a client they also disconnect** (`NetworkClient.DisconnectOnException = true`), on the server only if the error budget is exceeded (`MessageHandler.cs:42-72, 153-163`; `NetworkServer.cs:309`).
- Diagnostics: `NetworkDiagnostics.OutMessageEvent/InMessageEvent` give per-message byte counts (`NetworkDiagnostics.cs`); `Metrics` per peer gives per-channel counts if `EnablePeerMetrics` (`NetworkServer.cs:31-34`, `SocketLayer/Metrics.cs`).

## 9. Ordering and batching, as observed from an application

- Within one connection and channel (Reliable): messages arrive in exactly the order the sender enqueued them (RPCs at call time, SyncVars at the flush point, spawn at spawn time).
- Across channels: no guarantee.
- Within one frame: all messages enqueued before `Peer.UpdateSent` share packets (reliable together, unreliable together). The packet leaves the process in `UpdateSent`, except Notify (immediate) and batches that fill up.
- Receive: every packet available is processed in the next `UpdateReceive` in arrival order; there is no per-frame cap (`Peer.cs:261-317`).

## 10. Rate limiting and the error budget

- Server-wide: `ErrorRateLimitEnabled = true`, bucket `MaxTokens=200, Refill=50/s, Interval=1` (`NetworkServer.cs:121-129`). Exceeding it disconnects the player or calls your callback (`SetErrorRateLimitReachedCallback`, `:191-212`).
- Per-RPC: only if `[RateLimit]` present.
- For tick-rate input traffic: RPCs for unknown/destroyed objects (1 token each, unless within 1 s grace) and ownership failures (10 tokens each) can disconnect a client that keeps sending inputs after ownership/despawn - budget ~20 ownership errors burst.

## 11. Host-mode semantics

- The host player's connection is a `PipePeerConnection`: `SendReliable/SendUnreliable/SendNotify` call the other side's `IDataHandler.ReceiveMessage` **synchronously and re-entrantly** (`Assets/Mirage/Runtime/PipePeerConnection.cs:78-128`). Channel is irrelevant. A custom message sent by host-client code runs the server handler *inside the `Send` call* (and vice versa). Notify callbacks fire `OnDelivered()` before `Send` returns (`:90-98`).
- ClientRpc in host: the stub invokes the body **first, inline, before serializing/sending** to remote observers (`ClientRpcProcessor.cs:93-94, 187-200`); `ShouldInvokeLocally` decides by observer/owner membership of the local player (`ClientRpcSender.cs:105-159`). With `Owner`/`Player` target and the host as target, the stub returns without sending.
- ServerRpc in host: body invoked inline (no message, no rate limit, no sender-ownership check beyond `requireAuthority && !HasAuthority` -> throws); `INetworkPlayer` parameters receive `Server.LocalPlayer` (`ServerRpcProcessor.cs:95-119, 191-214`, `ServerRpcSender.cs:80-99`).
- A ServerRpc on a dedicated server (no client) throws unless `allowServerToCall` (`ServerRpcSender.cs:86-88`).

## 12. Gotchas for CSP specifically

1. Default `Channel.Reliable` on both RPC kinds: 60 Hz input RPCs at default settings will queue behind any retransmit.
2. Unreliable RPCs have no sequence/dedupe: carry your own tick/seq and redundancy (arrays of recent inputs are legal parameters).
3. Unreliable `MessageSizeException` is thrown to the caller, not swallowed.
4. `RpcTarget.Owner/Player` bypass visibility; `Observers` does not reach a non-ready owner.
5. RPC to a not-yet-spawned object is silently dropped client-side; an unreliable state RPC right after (re)gaining visibility will often be lost.
6. A throwing handler/RPC on the client: RPC -> logged only; NetworkMessage handler or SyncVar hook -> client disconnects by default.
7. Host mode runs everything inline - no network reorder, no latency, re-entrancy.
