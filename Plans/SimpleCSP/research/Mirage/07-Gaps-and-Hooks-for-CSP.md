> Verified against main @ 0638bf8e5

# 07 - Gaps and Hooks for CSP (ASSESSMENT)

**This file is an assessment, not a specification.** Facts carry a repo-relative `path:line` citation; judgements are prefixed **Assessment:**; things read from code but not run are tagged *(inferred)*; things I could not confirm are tagged *(unverified)*. Nothing here was executed in Unity.

**Bottom line.** Mirage already supplies a per-object network identity, ownership/authority, interest management, per-object unreliable RPCs with owner/observer targeting, an acked-unreliable path (Notify), packet batching, a bit-packing serializer and (tested) support for generic `NetworkBehaviour`s with RPCs. It supplies **no** tick clock, input/state history, resimulation, reconciliation, snapshot interpolation, lag compensation, or unreliable state-sync path (SyncVars are reliable-only). Several defaults (reliable RPCs, 100 ms SyncVar interval, frame-bound send/receive, `HashSet` ordering) are wrong for 60 Hz prediction traffic and must be overridden or avoided.

---

## 0. Baseline, and one correction to the brief

- Code baseline: `origin/main` @ `0638bf8e5`, package 159.1.1 (`Assets/Mirage/package.json:4`). The checked-out tree equals it (`git diff origin/main HEAD` touches only files under `Plans/`).
- **Correction.** The brief says the "SyncVar-as-properties work is now on `main`". In this repository it is **not**: `[SyncVar]` on `main` is field-only (`Assets/Mirage/Runtime/CustomAttributes.cs:10`, `AttributeTargets.Field`) and `PropertySiteProcessor` rewrites field accesses (`Assets/Mirage/Weaver/Processors/PropertySiteProcessor.cs:18-22`). The property work (commits such as `f05b9f5c0` "support properties in Weaver and delete PropertySiteProcessor", `2e73dbf5b` "restrict SyncVarAttribute usage to properties") exists only on `origin/master` @ `7116e9f`; `git merge-base --is-ancestor f05b9f5c0 origin/main` is false, and `git ls-remote origin` shows `main`=`0638bf8e5`, `master`=`7116e9f`. `main` instead carries 83 commits `master` lacks (profiler markers, `ISpawnValuesHandler`, `RemoteCallCollectionCache`, `[MaxLength]`, `PeerConfigProfile`, client-side RPC rate limit). `doc/docs/guides/sync/sync-var.md:20-27, 91` on `main` still describe the property form, which would not compile here.
- Files 01-06 were first written against `master` and have been re-verified and corrected for `main`. If the properties branch is merged later, re-check file 02 section 1, file 05 section 4, and the cross-assembly caveat in section 5.6 below.
- Related report (not repeated here): `Plans/SimpleCSP/research/ClientSidePrediction/TickRunner-and-TimeSync-Mirage.md` covers `NetworkTime` accuracy, the ping/pong quirks, and what the SocketLayer/Notify can and cannot tell you about RTT. Its code references were taken at 158.0.0; `NetworkTime.cs` and the Notify/Ack code are unchanged on `main` apart from profiler markers.

---

## 1. What Mirage already gives a per-object CSP system

| Need | What exists | Evidence | Fit (Assessment) |
|---|---|---|---|
| Route a message to one component of one object | `RpcMessage { NetId, FunctionIndex, Payload }`; function index = component offset + relative index; layouts shared per component-type sequence | `Assets/Mirage/Runtime/RemoteCalls/RpcMessages.cs:5-11`, `Assets/Mirage/Runtime/RemoteCalls/ClientRpcSender.cs:23`, `Assets/Mirage/Runtime/RemoteCalls/RemoteCallHelper.cs:27-49, 367-417` | Good. A `[ServerRpc]`/`[ClientRpc]` on the prediction component needs no custom dispatcher. Header cost is small (section 6). |
| Who owns / controls an object | `Owner` (server only), `HasAuthority`, `IsLocalPlayer`, `INetworkPlayer.OwnedObjects` (character plus weapons) | `Assets/Mirage/Runtime/NetworkIdentity.cs:150, 157, 258`, `Assets/Mirage/Runtime/NetworkPlayer.cs:145` | Good. Distinguishes "my character" from "my weapon" (file 01 section 5). |
| Know when to create/destroy prediction state | Late-add events `OnStartServer/OnStartClient/OnStartLocalPlayer/OnAuthorityChanged/OnOwnerChanged/OnStopClient/OnStopServer`; a late `AddListener` is invoked immediately | `NetworkIdentity.cs:444-510`, `Assets/Mirage/Runtime/Events/AddLateEvent.cs:74-91` | Good. |
| Unreliable, targeted per-object sends | `[ServerRpc(channel=...)]`, `[ClientRpc(channel=..., target=Owner/Observers/Player, excludeOwner, excludeHost)]` | `CustomAttributes.cs:82-91, 119-147` | Good (section 4 Q3). |
| Server-side validation | Owner check, `[RateLimit]`, per-player error budget (disconnect), `[MaxLength]` caps | `Assets/Mirage/Runtime/RemoteCalls/RpcHandler.cs:137-174, 216-228`, `CustomAttributes.cs:94-114, 296-310`, `Assets/Mirage/Runtime/NetworkServer.cs:137-145, 207-229` | Useful against input flooding; budget needs tuning (section 5.6). |
| Several messages per frame in one packet | Reliable and unreliable batches, flushed once per `Update` or when full | `Assets/Mirage/Runtime/SocketLayer/Connection/Batch.cs:23-40`, `Assets/Mirage/Runtime/SocketLayer/Connection/ReliableConnection.cs:80-103, 302-306` | Good (section 4 Q5). |
| Acked unreliable | Notify: `Send(msg, INotifyCallBack)` with `OnDelivered`/`OnLost`; late/duplicate packets dropped by the receiver | `Assets/Mirage/Runtime/NetworkPlayer.cs:301-330`, `Assets/Mirage/Runtime/SocketLayer/Connection/INotifyCallBack.cs:9-13`, `Assets/Mirage/Runtime/SocketLayer/Connection/AckSystem.cs:320-348, 460-484` | Good for input redundancy and delta baselines; not batched (section 3.5). |
| Compact serialization | Weaver-generated serializers for structs; `[BitCount]`, `[FloatPack]`, `[Vector3Pack]`, ...; pooled writers/readers | file 05; `Assets/Mirage/Runtime/Serialization/WeaverAttributes.cs:47-156` | Good. A 71-bit input and ~134-bit state are achievable (file 05 section 6). |
| Generic `NetworkBehaviour` with RPCs | Weaver + runtime tests | section 4 Q1 | Good with the restrictions listed there. |
| Baseline state for late join / respawn | `OnSerialize(writer, initialState: true)` output is placed in the spawn message; re-sent on `AssignClientAuthority`/`ReplaceCharacter` | `Assets/Mirage/Runtime/NetworkBehaviour.cs:399-406`, `Assets/Mirage/Runtime/ServerObjectManager.cs:455-498, 567-582`, `NetworkIdentity.cs:741-747, 1156-1181` | Good place for `(tick, full state)`. Called once per send, not once per object (section 5.6). |
| Per-observer interest management | `observers` set; `ClientRpc` with `Observers` only reaches it | `NetworkIdentity.cs:168, 1073-1099`, `ClientRpcSender.cs:19-32` | Good, but objects leaving visibility are destroyed on the client (file 01 section 6). |
| A place for a global prediction manager | `NetworkWorld.onSpawn/onUnspawn/OnAuthorityChanged`, `AddAndInvokeOnSpawn`, `Server.Started`/`Client.Started` late events | `Assets/Mirage/Runtime/NetworkWorld.cs:34-49`, `Assets/Mirage/Runtime/Extensions/NetworkWorldExtensions.cs:11-18`, `NetworkServer.cs:87` | Good. |
| Control of when Mirage runs | `ManualUpdate` + public `UpdateReceive()` / `UpdateSent()` | `NetworkServer.cs:205, 421-445`, `Assets/Mirage/Runtime/NetworkClient.cs:123, 377-406` | Good; see section 4 Q4. |
| Profiling | Markers `Mirage.Rpc.<name>.Invoke`, `Mirage.Message.<Type>.Invoke/Send/SendToMany`, `Mirage.NetworkServer.UpdateReceive/UpdateSent`, `Mirage.SocketLayer.Peer.UpdateReceive`, ... (main only) | `RemoteCallHelper.cs:259, 280`, `Assets/Mirage/Runtime/MessageHandler.cs:246-252`, `Assets/Mirage/Runtime/Serialization/MessagePacker.cs:15-17` | Free per-RPC cost visibility in the Unity Profiler. |
| Rough clock/RTT | `NetworkTime` (ping every 2 s default, EMA) | file 04 section 6, sibling report | Seed only. |

---

## 2. What is missing (SimpleCSP must build it)

1. **A tick clock and tick alignment.** `NetworkTime` is wall-clock based and sampled every 2 s (`Assets/Mirage/Runtime/NetworkTime.cs:19`); it has no notion of ticks. Details and verdict in the sibling report, section 5.
2. **Input and state history per object.** A repo search for prediction, reconciliation, rewind, lag compensation, snapshots finds nothing (file 06 section 3; re-run on `main`: only a doc comment at `NetworkBehaviour.cs:392` and `doc/docs/guides/sync/custom-serialization.md`).
3. **Resimulation, reconciliation and smoothing.** Nothing in the runtime; the only interpolation is `NetworkTransformBase` (arrival-time based, reliable-only, obsolete) (`Assets/Mirage/Components/NetworkTransformBase.cs:25-46, 127-212`).
4. **Snapshot interpolation of remote objects** keyed by server tick (same note).
5. **Lag compensation / rewind.**
6. **An unreliable state-sync path.** SyncVars and `OnSerialize` data are reliable-only (`Assets/Mirage/Runtime/SyncVarSender.cs:111, 138`; defaults at `NetworkPlayer.cs:240`). There is no per-behaviour channel field in `SyncSettings` (`Assets/Mirage/Runtime/SyncSettings.cs:10-29`).
7. **Sequencing/dedupe for unreliable data.** Unreliable messages carry no sequence number; they can be lost, duplicated and reordered (`ReliableConnection.cs:105-108`, file 03 section 7). Every unreliable payload needs its own tick/sequence.
8. **Send scheduling tied to a fixed tick.** Everything is flushed once per Mirage `Update`; the only early flush is a full batch or a manual `IConnection.FlushBatch()` (`Assets/Mirage/Runtime/SocketLayer/Connection/IConnection.cs:40-46`) *(unverified as a supported pattern)*.
9. **Per-object routing for custom messages.** Handlers are registered by message type only (`MessageHandler.cs:28-42`); you add `NetId` and dispatch yourself.
10. **Bandwidth shaping.** No per-connection byte budget, priority, delta compression or relevance filter for unreliable data; `Metrics` is read-only counters (file 03 section 8, `NetworkServer.cs:44-47`).
11. **Anti-cheat on ticks.** Mirage validates ownership and rate; it does not validate that an input's tick is inside an acceptable window.
12. **Host-mode behaviour.** Everything must be special-cased (section 5.4).
13. **Object re-creation handling.** Visibility changes destroy the client object; `AssignClientAuthority`/`ReplaceCharacter` re-apply spawn payload and spawn values (section 5.6).

---

## 3. Extension points

### 3.1 A custom `NetworkBehaviour` base class

- `NetworkBehaviour` is an abstract `MonoBehaviour` (`Assets/Mirage/Runtime/NetworkBehaviour.cs:23`). You may derive any depth of abstract or generic classes. The weaver walks from the concrete type up to `NetworkBehaviour` and processes each class parent-first (`Assets/Mirage/Weaver/Weaver.cs:151-163, 173-190`); SyncVar and RPC counts chain through `const` fields in each class (`Assets/Mirage/Weaver/Processors/NetworkBehaviour/ConstFieldTracker.cs:25-39`), also across assemblies as long as every assembly in the chain references Mirage (so the weaver runs on it).
- Useful members: `IsServer/IsClient/IsHost/IsLocalPlayer/HasAuthority/Owner/NetId/Server/Client/World/NetworkTime` (`NetworkBehaviour.cs:40-115`), `SyncSettings` (`:34`), `SetDirtyBit` (`:312`), identity events through `Identity.On...` (`NetworkIdentity.cs:444-510`). `[ShowSyncSettings]` exposes `SyncSettings` in the inspector for a behaviour that has no SyncVars (`CustomAttributes.cs:292-293`, `NetworkBehaviour.cs:30-34`).
- Hard limits: at most 255 behaviours per identity (`NetworkIdentity.cs:309-312`); identical component order/types on server and client because RPC indices are positional (`RemoteCallHelper.cs:27-49`); no adding/removing networked components at runtime (`NetworkBehaviour.cs:222`).
- **Assessment:** one `PredictionBehaviour<TInput, TState>`-style base per predicted object is workable. Keep its RPC surface to `TInput`/`TState` as plain parameters, or to `ArraySegment<byte>` (section 4 Q1/Q2), and attach concrete subclasses (Unity cannot attach an open generic).

### 3.2 `OnSerialize` / `OnDeserialize` overrides

- Signatures `bool OnSerialize(NetworkWriter, bool initialState)` and `void OnDeserialize(NetworkReader, bool initialState)` are virtual (`NetworkBehaviour.cs:399-419`). Call `SetDirtyBit(mask)` to have the object serialized at the next flush (`:312-321`).
- If you override without calling `base`, the generated SyncVar/SyncObject serialization of that class is skipped (`:399-406`). If a class defines its own `SerializeSyncVars`/`DeserializeSyncVars`, the weaver does not generate them (`Assets/Mirage/Weaver/Processors/NetworkBehaviour/BaseMethodHelper.cs:64-71`).
- The `bool` return is ignored; dirty bits are cleared after serialization (`NetworkIdentity.cs:794-804, 844`).
- Owner and observers can receive different payloads only through different components or through `SyncSettings.To` flags (`NetworkIdentity.cs:758-791`, `SyncSettings.cs:95-131`).
- Precedent: `NetworkTransformBase` (`Assets/Mirage/Components/NetworkTransformBase.cs:95-111, 214-218, 359-365`).
- **Assessment:** use it for the **spawn baseline** (`initialState == true`) carrying `(tick, full state)`. Do not use it for per-tick state: reliable, shared with RPCs and spawns, default 100 ms interval (`SyncSettings.cs:24-29`), and the stream blocks behind loss (section 5.2).

### 3.3 Weaver attributes worth knowing

| Attribute | Use for CSP | Source |
|---|---|---|
| `[ServerRpc(channel, requireAuthority, allowServerToCall)]` | inputs, owner-authored events | `CustomAttributes.cs:82-91` |
| `[ClientRpc(channel, target, excludeOwner, excludeHost)]` | state to owner / to others | `CustomAttributes.cs:119-147` |
| `[RateLimit(Interval, Refill, MaxTokens, Penalty)]` | cap input RPC rate (also checked client-side, `ServerRpcSender.cs:81-89`) | `CustomAttributes.cs:94-114`, `ServerRpcProcessor.cs:159-178` |
| `[MaxLength(n)]` (main only) | bound `PlayerInput[]`, `ArraySegment<byte>`, strings | `CustomAttributes.cs:296-310`, `ValueSerializerFinder.cs:124-149` |
| `[NetworkMessage]` | make the weaver generate a writer/reader for a type (also needed for generic arguments) | `CustomAttributes.cs:153-155`, `ReaderWriterProcessor.cs:191-211` |
| `[BitCount]`, `[BitCountFromRange]`, `[ZigZagEncode]`, `[FloatPack]`, `[Vector2Pack]`, `[Vector3Pack]`, `[QuaternionPack]`, `[VarInt]`, `[VarIntBlocks]` | shrink input/state | file 05 section 4 |
| `[SyncVar(hook, initialOnly, invokeHookOnServer, invokeHookOnOwner)]` | low-rate data and spawn-time data | `CustomAttributes.cs:10-37` |
| `[Server]`, `[Client]`, `[HasAuthority]`, `[LocalPlayer]`, `[NetworkMethod]` | per-object guards on `Update`-style methods | `Assets/Mirage/Weaver/Processors/AttributeProcessor.cs:96-105` |
| `[WeaverIgnore]`, `[WeaverWriteAsGeneric]`, `[WeaverSerializeCollection]` | custom reader/writer control | `WeaverAttributes.cs:11-12, 25-26, 32-36` |

Custom serializers: any `public static void X(this NetworkWriter, T)` / `public static T Y(this NetworkReader)` in an assembly that references Mirage is picked up as the serializer for `T` (`ReaderWriterProcessor.cs:463-525, 557-569`).

### 3.4 Custom messages per object

- Pattern: define `[NetworkMessage] struct M { uint NetId (or NetworkIdentity/NetworkBehaviour field); ushort Tick; ... }`, register one handler per side after the handler object exists: `Server.MessageHandler.RegisterHandler<M>(...)` from `Server.Started` and `Client.MessageHandler.RegisterHandler<M>(...)` from `Client.Started`. `MessageHandler` is created at `StartServer`/`Connect`, so re-register every session (`NetworkServer.cs:328`, `NetworkClient.cs:155`). A second registration for the same type replaces the first (`MessageHandler.cs:28-42`).
- A `NetworkIdentity`/`NetworkBehaviour` field in a message is written as a packed NetId (+1 component-index byte) and resolved through the reader's locator; it is `null` if the object is not spawned on that side yet (`Assets/Mirage/Runtime/Serialization/MirageTypesExtensions.cs:11-98`). Server handlers must check `identity.Owner == player` (hint at `NetworkPlayer.cs:142`).
- Sending: client to server `Client.Send(msg, Channel)` (`NetworkClient.cs:344-355`); one player `INetworkPlayer.Send(msg, Channel)` (`NetworkPlayer.cs:240-255`); owner only `identity.Owner.Send(...)`; observers except owner `Server.SendToObservers(identity, msg, excludeLocalPlayer, excludeOwner: true, Channel.Unreliable)` (`NetworkServer.cs:644-656`); arbitrary list `NetworkServer.SendToMany` (packs once, copies per player, `:671-695`). `SendToAll(..., authenticatedOnly: true, ...)` ignores the channel and sends Reliable (`:586-591`).
- **Assessment:** an RPC buys routing, authority check, rate limit and a profiler marker per method; a message buys a single dispatcher and the ability to pack **many objects' states into one message** (fewer headers, one allocation-free write). For 32 players, a per-tick `WorldState` message per client is probably cheaper than 31 separate `ClientRpc`s; measure before choosing.

### 3.5 Reaching the Peer and the Notify channel

- `NetworkServer`/`NetworkClient` keep `_peer` private (`NetworkServer.cs:76`, `NetworkClient.cs:65`). The public handle is `INetworkPlayer.Connection` (`IConnection`), with `SendReliable`, `SendUnreliable`, `SendNotify(..., INotifyCallBack)` and `FlushBatch()` (`Assets/Mirage/Runtime/INetworkPlayer.cs:125-130`, `IConnection.cs:20-47`, extension overloads `Assets/Mirage/Runtime/SocketLayer/Connection/ConnectionExtensions.cs:7-41`). On the client the player is `Client.Player`.
- Higher level: `INetworkPlayer.Send<T>(T, INotifyCallBack)` (`INetworkPlayer.cs:17`, `NetworkPlayer.cs:301-330`) and `NetworkClient.Send<T>(T, INotifyCallBack)` (`NetworkClient.cs:367-375`). Use a pooled class (not a struct) for the callback to avoid allocation (`Assets/Mirage/Samples~/Notify/NotfyExample.cs:99-125`).
- Notify facts: written to the socket immediately, one message per packet, 13-byte header (`AckSystem.cs:320-348`); receiver drops duplicates and anything older than the newest received (`:460-484`); `OnDelivered`/`OnLost` fire inside the sender's `UpdateReceive` when a later ack covers the packet (`:644-674`), with no timer (a lost *last* packet is not reported until a later packet is acked, *inferred*); shares the 4096-entry sent-packet ring with reliable (`Assets/Mirage/Runtime/SocketLayer/Config.cs:100-107`); max size `MTU - 13` (`AckSystem.cs:322-325`); oversized throws `MessageSizeException` from `NetworkPlayer.Send` (it only catches `BufferFullException`/`NoConnectionException`, `NetworkPlayer.cs:315-328`).
- Config: `NetworkServer.PeerConfig` / `PeerConfigProfile` (`NetworkServer.cs:52-55`, `Config.cs:3-9, 121-154`); the `WebSocket` profile sets `DisableReliableLayer`, which maps Unreliable and Notify onto Reliable (`NoReliableConnection.cs:34-47`), so CSP over TCP-like sockets inherits head-of-line blocking.
- Metrics: `EnablePeerMetrics` (`NetworkServer.cs:44-47`), `NetworkDiagnostics.InMessageEvent/OutMessageEvent` (file 03 section 8).

### 3.6 Other hooks

- `ISpawnValuesHandler` (main only): `ClientObjectManager.SpawnValuesHandler` (`ClientObjectManager.cs:54, 576`) and `ServerObjectManager.SpawnValuesHandler` (`ServerObjectManager.cs:41, 493`). A custom handler can skip applying position/rotation for an object the local player predicts, so a re-sent spawn message does not snap it (`Assets/Mirage/Runtime/ISpawnValuesHandler.cs:65-82`).
- `INetworkVisibility` / `NetworkVisibility` for relevance rules (`Assets/Mirage/Runtime/INetworkVisibility.cs:14-37`).
- `Server.Disconnected`, `Client.Disconnected`, `INetworkPlayer.SceneIsReady` gate who is an observer (`NetworkPlayer.cs:108`, `NetworkIdentity.cs:1095-1097`).

---

## 4. Specific questions

### Q1. Does the Weaver support generic `NetworkBehaviour` subclasses with RPCs?

**Yes for a generic declaring class whose RPC parameters are `T` or concrete types. It is covered by runtime tests. Composite types built from the open `T` (`T[]`, `List<T>`, `ArraySegment<T>`, `T?`) are untested and probably broken. The RPC *method* itself cannot be generic. An abstract RPC *method* is rejected; an abstract *class* with concrete or virtual RPCs is accepted.**

| Case | Result | Evidence |
|---|---|---|
| `class Foo<T> : NetworkBehaviour` with `[ServerRpc]`/`[ClientRpc]` that do not use `T` | Works at runtime (server and client calls) | `Assets/Tests/Runtime/ClientServer/Generics/GenericWithRpc.cs:9-31, 33-107` |
| RPC parameter of type `T` (`int`, and a class) | Works at runtime in both directions, with `requireAuthority = false` and an `INetworkPlayer sender` parameter | `Assets/Tests/Runtime/ClientServer/Generics/WithGenericRpc.cs:9-31, 34-107` |
| Generic base -> generic middle -> concrete, RPCs at each level | Works at runtime | `Assets/Tests/Runtime/RpcTests/GenericNetworkBehaviourDeeperRpcTest.cs:12-124`, `Assets/Tests/Runtime/RpcTests/GenericNetworkBehaviourRpcTest.cs:12-68` |
| Weaver build of `Foo<T>` with `[ServerRpc] void Cmd(T arg)` / `[ClientRpc] void Rpc(T arg)` | Succeeds | `Assets/Tests/Weaver/NetworkBehaviourTests.cs:31-35, 58-62`; `Assets/Tests/Weaver/NetworkBehaviourTests~/NetworkBehaviourCmdGenericArgument.cs` |
| `[SyncVar] T value;` and `[SyncVar(hook=...)]` in a generic behaviour | Works at runtime | `Assets/Tests/Runtime/ClientServer/Generics/WithGenericSyncVar.cs:7-12`, `Assets/Tests/Runtime/ClientServer/Generics/WithGenericSyncVarHook.cs` |
| The RPC method has its own generic parameters (`void Cmd<T>()`) | **Weaver error** "cannot have generic parameters" | `Assets/Mirage/Weaver/Processors/RpcProcessor.cs:271-274`; `Assets/Tests/Weaver/NetworkBehaviourTests.cs:37-42, 64-69`; doc `doc/docs/guides/serialization/generics.md:14-16` |
| `abstract` RPC method (also an override of one) | **Weaver error** "Abstract Rpcs are currently not supported, use virtual method instead" | `RpcProcessor.cs:256-259`; `Assets/Tests/Weaver/ClientRpcTests.cs:38-50`, `Assets/Tests/Weaver/ServerRpcTests.cs:79-89` |
| `virtual` RPC and `override`, including `base.Method()` | Supported; calls to the base RPC are rewritten to `UserCode_` | `Assets/Mirage/Weaver/Processors/RpcProcessor.cs:413-450`; `Assets/Tests/Weaver/ClientRpcTests.cs:26-36`, `Assets/Tests/Weaver/ServerRpcTests.cs:55-77` |
| `abstract class` containing concrete/virtual RPCs | Accepted: the weaver has no abstract-type check (`IsAbstract` is only tested on methods) *(inferred; no dedicated test)* | `Weaver.cs:151-163`, `RpcProcessor.cs:256` |
| `T[]` / `List<T>` / `ArraySegment<T>` / `Nullable<T>` as an RPC parameter inside the generic class | **Not tested; probably broken.** Only a bare `T` becomes a runtime-generic `Write<T>`/`Read<T>` call; an array/collection of `T` goes to `GenerateCollectionFunction` and emits a helper in the generated (non-generic) class whose signature mentions the class's open `T`, which should produce invalid metadata *(inferred, unverified)*. The tests keep `private T[] values;` only as an unused field "to trigger possible errors". The docs only promise closed generic instances | `Assets/Mirage/Weaver/SerializeFunctionBase.cs:141-142, 184-201`; `Assets/Mirage/Weaver/Serialization/Writers.cs:164-190`; `Assets/Tests/Runtime/ClientServer/GenericNetworkBehaviorSyncvarTest.cs:22-24`; `doc/docs/guides/serialization/generics.md:30-46` |
| `SyncList<T>` / other `ISyncObject` fields whose type contains an open `T` | Silently **skipped** (not synchronized) | `Assets/Mirage/Weaver/Processors/SyncObjectProcessor.cs:34-37` |

Serializer availability for concrete `T`:

- For a bare `T` the stub calls `writer.Write<T>(x)` / `reader.Read<T>()`, which look up `Writer<T>.Write` / `Reader<T>.Read` at runtime (`Assets/Mirage/Runtime/Serialization/GenericTypesSerializationExtensions.cs:43-49, 77-83`). The weaver deliberately returns without generating anything for a generic parameter (`ReaderWriterProcessor.cs:259-260`).
- A writer for each concrete `T` therefore has to be generated in some weaved assembly: tag the type `[NetworkMessage]` (the test type `MyClass` does exactly this "to tell weaver to make writer", `Assets/Tests/Runtime/ClientServer/Generics/MyClass.cs:3-8`), or provide extension methods, or use it concretely somewhere (RPC parameter, `Send<T>`, `writer.Write<T>` with a concrete T). Do not count on the weaver scanning closed base-class arguments of your subclasses; the docs say auto-detection "does not always work" (`doc/docs/guides/serialization/generics.md:18-22`).
- If it is missing, the first send or receive throws `KeyNotFoundException("No writer found for ...")` (`GenericTypesSerializationExtensions.cs:64-67, 98-101`).
- Each concrete subclass must be a real `MonoBehaviour` type to attach in Unity; the tests derive `: Foo<int>` (`GenericWithRpc.cs:26-31`). `RemoteCallCollectionCache` keys on the concrete runtime types, so `Foo<int>` and `Foo<MyState>` get separate layouts (`RemoteCallHelper.cs:373-417`).

**Assessment:** `PredictionBehaviour<TInput, TState>` with `[ServerRpc] void SendInput(TInput input)` and `[ClientRpc] void ReceiveState(TState state)` is within tested territory. For input bundles or redundancy do **not** use `TInput[]` as an RPC parameter in the generic class. Either (a) give the RPC an `ArraySegment<byte>` parameter and write/read the array yourself with `writer.Write<TInput>(...)` in generic code, or (b) declare the array-typed RPC in a **non-generic** concrete subclass. Put `[NetworkMessage]` on `TInput` and `TState`. A 10-line weaver test (a `Foo<T>` with a `T[]` RPC, in the style of `NetworkBehaviourTests~/`) would settle the array question before the design depends on it.

### Q2. Can an RPC take `ArraySegment<byte>` or a struct array without allocation?

| Parameter | Send side | Receive side | Evidence |
|---|---|---|---|
| `ArraySegment<byte>` | no allocation: pooled writer, `WriteSpanRaw` memcpy | **no allocation**: the reader returns a segment aliasing the receive buffer (valid only inside the RPC body) | `Assets/Mirage/Runtime/Serialization/CollectionExtensions.cs:43-49, 68-71`; `Assets/Mirage/Runtime/Serialization/NetworkReader.cs:421-431`; reader released right after invoke `RpcHandler.cs:178-211` |
| `T[]` of structs | caller allocates the array | allocates `new T[len]` per call | `CollectionExtensions.cs:305-327` (alloc at `:321`) |
| `ArraySegment<T>` (not byte) | caller owns array | allocates an array and wraps it | `CollectionExtensions.cs:335-340` |
| `List<T>` | caller owns list | allocates `new List<T>` | `CollectionExtensions.cs:285-303` (alloc at `:297`) |
| `Span<T>` | n/a | allocates (`// todo remove allocation`) | `CollectionExtensions.cs:383-398` (`:392`) |
| class-typed parameter | caller allocates | allocates per read | file 05 section 5 |

- `ReadArrayNonAlloc`, `ReadListNonAlloc`, `ReadDictionaryNonAlloc`, `ReadSpanNonAlloc` exist as manual APIs (`CollectionExtensions.cs:414-540`), but the weaver never uses them for RPC parameters.
- Message sends are allocation-free for struct messages (test: `Assets/Tests/Runtime/SendToAllocations.cs:40-52`); the official Notify sample also says "to avoid allocations use an array segment and write length + values" (`Assets/Mirage/Samples~/Notify/NotfyExample.cs:93`).
- **Assessment:** the allocation-free pattern is an RPC (or message) whose payload is `ArraySegment<byte>`: sender uses `NetworkWriterPool.GetWriter()` and writes `count` plus `n` inputs with the generated struct writer; receiver wraps the segment in `NetworkReaderPool.GetReader(segment, World)` and reads into a preallocated ring buffer. You pay one extra layer of framing (about 2 bytes: count + padding) and lose per-field `[MaxLength]` on the inner array; cap the segment with `[MaxLength]` instead (`CollectionExtensions.cs:73-79, 223-234`). Unreliable limit: `1229` bytes including the 2-byte message id for default UDP (`Assets/Mirage/Runtime/SocketLayer/Peer.cs:873-879`).

### Q3. Can RPCs or SyncVars be unreliable? To the owner only? To observers excluding the owner?

| Mechanism | Unreliable? | Owner only | Observers except owner |
|---|---|---|---|
| `[ServerRpc]` (client to server) | yes: `channel = Channel.Unreliable` (`CustomAttributes.cs:84`) | n/a | n/a |
| `[ClientRpc]` | yes: `channel = Channel.Unreliable` (`CustomAttributes.cs:142`) | `target = RpcTarget.Owner`; throws if there is no owner; no observer or scene-ready check (`ClientRpcSender.cs:34-45, 72-83`) | `target = Observers` (default) with `excludeOwner = true` (`CustomAttributes.cs:144`; `Owner + excludeOwner` is a weaver error, `Assets/Mirage/Weaver/Processors/ClientRpcProcessor.cs:267-271`) |
| Return-value RPC (`UniTask<T>`) | **no**, always Reliable | `Owner`/`Player` only | n/a (`ServerRpcSender.cs:53-54`, `ClientRpcSender.cs:66-67`) |
| `[SyncVar]`, `SyncList`, `OnSerialize` | **no**: reliable only, no channel field | `SyncSettings.To = SyncTo.Owner` (host-mode caveat, file 02 section 3) | `SyncSettings.To = SyncTo.ObserversOnly` excludes the owner; the default `Owner` + `ObserversOnly` (`SyncTo.OwnerAndObservers`) sends to both (`SyncSettings.cs:24-29, 95-131, 241-260`) |
| Custom message | yes (`Channel` argument) | `identity.Owner.Send(msg, Channel.Unreliable)` | `Server.SendToObservers(identity, msg, excludeLocalPlayer, excludeOwner: true, Channel.Unreliable)` (`NetworkServer.cs:644-656`) |
| Notify | own path (acked; receiver drops late and duplicate packets, so delivery is monotonic but lossy) | `owner.Send(msg, callbacks)` | loop over players (no helper) |

Other facts: `Channel` has only `Reliable = 0` and `Unreliable = 1` (`Assets/Mirage/Runtime/Channel.cs:3-7`); the weaver bakes the channel in as a constant (`ClientRpcProcessor.cs:81, 113`). The SyncVar path calls `player.Send(varsMessage)` and `SendToRemoteObservers(varsMessage, false)` with default `Reliable` (`SyncVarSender.cs:111, 138`). Unreliable size limit: `MessageSizeException` is thrown to the caller (`ReliableConnection.cs:89-103`). Over the `WebSocket` profile "unreliable" becomes reliable (section 3.5).

### Q4. When are RPCs and messages handled relative to `Update` and `FixedUpdate`?

- Mirage has two Unity entry points, `NetworkServer.Update()` (`NetworkServer.cs:421-428`) and `NetworkClient.Update()` (`NetworkClient.cs:377-391`), both internal Unity `Update` messages with no `FixedUpdate`/`LateUpdate`/PlayerLoop hook and no `[DefaultExecutionOrder]` anywhere in the package (grep over `Assets/Mirage`; the only `FixedUpdate` hit is a comment at `NetworkTransformBase.cs:8`). Each does `UpdateReceive()` then `UpdateSent()`.
- `UpdateReceive` drains **all** pending datagrams and runs every handler inline: ServerRpc/ClientRpc bodies, custom message handlers, SyncVar deserialize and hooks, spawn/destroy (`Assets/Mirage/Runtime/SocketLayer/Peer.cs:281-349`, `RpcHandler.cs:79-214`, `MessageHandler.cs:103-133`).
- `UpdateSent` runs `SyncVarSender.Update()` (serializes dirty objects, enqueues) then `Peer.UpdateSent()` (flushes batches to the socket) (`NetworkServer.cs:439-445`, `NetworkClient.cs:400-406`, `Peer.cs:266-276`).
- Unity's own order within a frame is `FixedUpdate` (0..n times) -> `Update` -> `LateUpdate`. So:
  - A ServerRpc body (or message handler) never runs inside `FixedUpdate`; it runs in the `Update` phase of Mirage's component. Inputs received in frame N are first visible to a `FixedUpdate` simulation in frame N+1. **Assessment:** handlers should only enqueue into a per-object input buffer.
  - RPCs/messages you send from `FixedUpdate` (any number of ticks in that frame) are enqueued immediately and leave the process in the same frame's `UpdateSent`, because `FixedUpdate` precedes `Update`. The same is true of a script that runs before Mirage's `Update`; a script that runs after it, or `LateUpdate`, sends next frame. The relative order of your `Update` and Mirage's is undefined unless you set it (Script Execution Order / `[DefaultExecutionOrder]` on your driver; the package sets none).
  - `ManualUpdate = true` stops the automatic `UpdateReceive/UpdateSent` (`NetworkServer.cs:423`, `NetworkClient.cs:386`) and lets you place them yourself, giving *receive -> consume inputs -> simulate -> produce -> flush*. You must still call both every frame (timeouts, keep-alive, ack, resend live there, file 04 section 4.4). The client's ping scheduling still runs because it sits above the early return (`NetworkClient.cs:379-386`). `ManualUpdate` is not referenced by any test or sample *(treat as lightly exercised)*.
- Host mode: RPC bodies and message handlers for the host's own traffic run synchronously inside the caller's stack, so a ServerRpc called from `FixedUpdate` on the host executes in `FixedUpdate` (section 5.4).

### Q5. Are multiple messages sent in one frame batched into one packet?

**Yes.** Each connection keeps one reliable batch and one unreliable batch. `Send` appends `[2-byte length][message]` to the current batch and only writes a packet when the batch would overflow the MTU or at the per-frame flush:

- Reliable: `AckSystem.SendReliable` -> `_batch.AddMessage` (`AckSystem.cs:350-371`); flush in `AckSystem.Update` (`:231-247`).
- Unreliable: `ReliableConnection.SendUnreliable` -> `_unreliableBatch.AddMessage` (`ReliableConnection.cs:89-103`); flush after the reliable one in `FlushBatch` (`:302-306`), driven by `Peer.UpdateSent` -> `Connection.UpdateConnected` (`Peer.cs:266-276, 802-814`, `Assets/Mirage/Runtime/SocketLayer/Connection/Connection.cs:195-218`).
- Overflow: `Batch.AddMessage` sends the full batch and starts a new one (`Batch.cs:23-40`), so a long list of per-tick messages spills into extra packets automatically. A single message larger than the MTU is fragmented if reliable (up to `MaxReliableFragments = 50`) and throws if unreliable (`AckSystem.cs:357-368`, `ReliableConnection.cs:95-100`).
- **Notify is the exception:** it is written to the socket immediately, one message per packet, never batched (`AckSystem.cs:320-348`).
- Consequence for "k fixed ticks per frame": k separate unreliable RPCs from one frame share a packet (1-byte packet header, 2-byte length each) but each still pays its own message header (about 5 bytes plus payload, section 6). A single RPC carrying all k inputs (plus redundancy) saves those headers and is the better unit. Send order inside the batch is call order; arrival order across packets is not guaranteed.

---

## 5. Gotchas

### 5.1 SyncVar hooks firing at awkward times

- **Per variable, not per message.** In a delta, each dirty variable is read, assigned and its hook run before the next variable is read; later variables (and later components) still hold old values while an earlier hook runs (`Assets/Mirage/Weaver/Processors/SyncVarProcessor.cs:605-617`; file 02 section 8).
- **During spawn.** Initial state reads all SyncVars of a component first, then runs hooks (`SyncVarProcessor.cs:584-600`) - before `NotifyAuthority`, `OnStartClient`, and before the identity is added to `World` (`ClientObjectManager.cs:568-595, 656-660`), so `World.TryGetIdentity(netId)` fails for the object itself inside a hook. `NetworkIdentity.InitialState` is true for the duration (`NetworkIdentity.cs:231, 864-907`).
- **Re-spawn replays them.** `AssignClientAuthority`/`ReplaceCharacter` send a new `SpawnMessage` for a live object, so initial-state hooks run again for any value that differs, and spawn values (position/rotation) are re-applied (`NetworkIdentity.cs:1156-1181`, `ServerObjectManager.cs:264-276`, `ClientObjectManager.cs:575-576`).
- **Setter-invoked hooks.** Writing the field on the server/host runs the hook inline: default only if `IsHost`; `invokeHookOnServer` also on a dedicated server; `invokeHookOnOwner` on the owning client (`SyncVarProcessor.cs:236-267`; `doc/docs/guides/sync/sync-var-hooks.md:26-52`). A hook is not invoked before the object is spawned (`IsSpawned` false) (`doc/docs/guides/sync/sync-var-hooks.md:50`). A re-entrancy guard stops a hook from re-triggering itself (`NetworkBehaviour.cs:129-142`).
- **Equality gate.** A hook runs only if `old != new` by `EqualityComparer<T>.Default` (`NetworkBehaviour.cs:301-305`, `SyncVarProcessor.cs:663-707`). A struct without `IEquatable<T>` is compared by boxing; a struct that includes a per-tick counter is always "changed".
- **Exceptions disconnect.** A throwing hook runs inside the message handler; on a client that disconnects by default (`NetworkClient.cs:58, 155`, `MessageHandler.cs:158-172`), unlike an RPC body which only costs error tokens (`RpcHandler.cs:178-211`).
- **Assessment:** keep hooks to "copy the new value into a buffer and set a flag". Reconcile in your tick loop, not inside the hook.

### 5.2 Head-of-line blocking if inputs ride the reliable channel

- Every `[ServerRpc]`/`[ClientRpc]` defaults to `Channel.Reliable` (`CustomAttributes.cs:84, 142`). Reliable shares one ordered stream per connection with SyncVars, spawns, destroys and reliable custom messages. The receiver only releases the **next in-order packet**; one lost packet holds every later reliable message until the retransmit arrives (`AckSystem.cs:186-200, 504-549`, `ReliableConnection.cs:138-152`, doc at `Assets/Mirage/Runtime/SocketLayer/Enums/PacketType.cs:22-24`).
- Loss is detected only from ack masks on later packets, and resends happen from `AckSystem.Update` once per frame; there is no retransmit timer (`AckSystem.cs:231-247, 644-733`). A stall lasts roughly one RTT plus frame quantisation (file 04 section 4.5; *inferred, not measured*), and a lost *final* packet is not resent until the sender emits another ackable packet and gets an ack back (file 03 section 7).
- **Assessment:** inputs and per-tick state should be `Channel.Unreliable` (with redundancy) or Notify. Keep reliable for spawn baselines, equip/ownership events and anything that must not be lost, and expect it to arrive late and in a burst after loss. Tick-stamp everything.

### 5.3 Dirty-flag clearing

- Bits are cleared when the component is serialized, not when it is delivered: `ClearShouldSync(now)` runs after the serialization loop (`NetworkIdentity.cs:794-804`, `NetworkBehaviour.cs:350-354`). `OnSerialize`'s return value is ignored (`NetworkIdentity.cs:844`), contrary to its doc comment (`NetworkBehaviour.cs:392-394`). Delivery is the reliable layer's job; a stale delta can arrive after a newer local change.
- Only the **latest value at flush time** is sent per frame, so intermediate ticks are never replayed through SyncVars (`SyncVarSender.cs:30-78`).
- An object with no observers and no authority has its bits cleared without sending (`SyncVarSender.cs:54-71`); bits set before spawn stay set until the first flush (`NetworkBehaviour.cs:312-321`).
- On receive, `ClearDirtyBit(_deserializeMask)` clears only the bits just received, so a locally changed but unsent value is not lost, but a local predicted write to a received SyncVar is overwritten by the server value (`NetworkIdentity.cs:909-923`).
- Interval gating: `SyncSettings.Interval` defaults to 0.1 s with `Variable` timing; `NoInterval` sends every flush (`SyncSettings.cs:24-29, 37-56`).

### 5.4 Host mode (server and client in one process)

| Topic | Behaviour | Evidence |
|---|---|---|
| Role flags | `IsServer` and `IsClient` are **both** true for every spawned object; `IsHost` true; `IsServerOnly`/`IsClientOnly` false. `if (IsServer) ... else if (IsClient)` is wrong in host mode | `NetworkIdentity.cs:129-144`, `NetworkBehaviour.cs:55, 66-71`, `NetworkIdentity.cs:1002` |
| Shared state | The host client uses the server's `World`; its player is `Server.LocalPlayer`; there is no `SyncVarSender`, no peer, no ping, offset 0 | `NetworkClient.cs:235-253`, `NetworkClient.cs:379-384`, file 04 section 6 |
| Local transport | `PipePeerConnection` delivers synchronously and re-entrantly; channel is irrelevant; Notify fires `OnDelivered()` before `Send` returns | `Assets/Mirage/Runtime/PipePeerConnection.cs:78-128` |
| Spawn callbacks | `OnStartServer` (inside `Spawn`, before `AddIdentity`), then inline `OnAuthorityChanged(true)` (if owner), `OnStartClient`, `OnStartLocalPlayer` from `OnHostClientSpawn`. No payload is deserialized, `InitialState` is never true | `ServerObjectManager.cs:421-453`, `ClientObjectManager.cs:852-865` |
| `OnAuthorityChanged` | Fires once per change; the world-level event is raised by the server side only (`!IsServer` guard) | `NetworkIdentity.cs:656-674` |
| SyncVars | The host client never receives `UpdateVarsMessage` (handler not registered, server skips `LocalPlayer`); the setter runs hooks instead | `ClientObjectManager.cs:116-123`, `SyncVarSender.cs:125-127`, `SyncVarProcessor.cs:236-267` |
| `SyncTo.Owner`-only | Not enqueued in host mode, even for remote-owned objects (`ShouldSyncFrom` returns `!IsClient`) *(inferred)* | `SyncSettings.cs:58-75` |
| ClientRpc | The body runs **first, inline, before** serialization if the host is in the target set (Observers with the host as observer and not excluded; Owner/Player if the host is the target); `excludeHost = true` skips it; remote clients still get the message | `ClientRpcProcessor.cs:93-94, 187-200`, `ClientRpcSender.cs:114-168`, `CustomAttributes.cs:146` |
| ServerRpc | Called by the host it runs inline, no message, no rate limit; `requireAuthority && !HasAuthority` throws; an `INetworkPlayer` parameter receives `Server.LocalPlayer` | `ServerRpcSender.cs:99-118`, `ServerRpcProcessor.cs:95-119, 191-214` |
| Custom messages | `Client.Send` runs the server handler inside the call; `server.SendToAll(..., excludeLocalPlayer: false)` runs the host client's handler inside the call: no latency, no reordering, re-entrancy hazards | `PipePeerConnection.cs:103-128`, `NetworkServer.cs:586-595` |
| Prediction | The host is the authority. Disable prediction/reconciliation for host-owned objects and treat other players' objects as the real server objects | **Assessment** |

### 5.5 Unreliable RPCs versus SyncVar updates

- Different streams: SyncVar data and reliable RPCs ride the ordered reliable batch; unreliable RPCs ride the unreliable batch. Within one frame the reliable packet is written to the socket first and the unreliable one second (`ReliableConnection.cs:302-306`), but the network may reorder them, and after a reliable loss the unreliable stream runs ahead of the blocked SyncVar stream.
- Receiver processes packets in arrival order, interleaved (`Peer.cs:281-349`). A late SyncVar delta can therefore **overwrite newer unreliable-derived state** with an older value.
- Within the reliable stream the order is the order of enqueue: RPCs/messages are enqueued at call time, SyncVar deltas are enqueued at the `SyncVarSender.Update()` flush point (`NetworkServer.cs:439-445`). A reliable RPC called before the flush in the same frame arrives before the SyncVar delta that contains the same-frame change.
- **Assessment:** never let a SyncVar carry the same quantity as an unreliable RPC. If both exist, tick-stamp both and apply "latest tick wins" in one place.

### 5.6 Other gotchas that bite CSP

- **RPC to an object the client does not have is dropped** (not buffered, except for async spawns), costing one error token on the server (`RpcHandler.cs:90-120`; `ClientObjectManager.cs:142-163`). An unreliable state RPC sent right after an object becomes visible can overtake the reliable spawn and be lost.
- **`RpcTarget.Owner/Player` ignore visibility** (`ClientRpcSender.cs:34-45, 72-83`) so the owner can receive an RPC for an object whose spawn has not arrived.
- **Error budget.** Failing ServerRpc validation costs tokens (ownership failure 10, unknown object 1, deserialization 50, `SerializationLimit` 100); the bucket is 200 tokens refilling 50/s and exceeding it disconnects (`RpcHandler.cs:105-158`, `NetworkServer.cs:137-145, 207-229`). A client that keeps sending 60 Hz inputs after losing ownership burns the budget in about 20 inputs, except inside the 5 s grace window after ownership removal or destroy (`RpcHandler.cs:18, 143-161`).
- **Visibility changes destroy the client object**, taking the prediction buffers with it; re-entering visibility is a fresh spawn (file 01 section 6; `ClientObjectManager.cs:495-530, 814-824`).
- **Spawn values re-applied.** The default handler resets the transform to the server's position/rotation on every spawn message, including the re-send on `AssignClientAuthority`/`ReplaceCharacter` (`ISpawnValuesHandler.cs:65-82`, `ClientObjectManager.cs:575-576`). Override `ISpawnValuesHandler` if a predicted object must not snap (section 3.6).
- **`OnSerialize(initialState)` is called per spawn send**, once for each player batch that gets the spawn message (`ServerObjectManager.cs:455-498, 499-555`); keep it side-effect free.
- **SyncVar fields written from another assembly or by `ref` do not mark dirty** (`PropertySiteProcessor.cs:24-39, 127-163`; `doc/docs/guides/sync/sync-var.md:45`; `Assets/Mirage/Components/ReadyCheck.cs:20` adds a setter method for this reason). A prediction base class in its own asmdef must expose setter methods, not let other assemblies assign its SyncVar fields.
- **`SendToAll(..., authenticatedOnly: true, ..., channel)` drops the channel** and sends Reliable (`NetworkServer.cs:586-591`).
- **Unreliable oversize throws to the caller** (`ReliableConnection.cs:95-100`).
- **Everything is frame-bound.** With a 30 fps headless server (`Assets/Mirage/Components/HeadlessFrameLimiter.cs:15-27`) a 60 Hz tick cannot be sent or received more than 30 times per second; intervals use `Time.unscaledTimeAsDouble` and ignore `timeScale` (`SyncVarSender.cs:43`).
- **Hash ordering.** Dirty objects are iterated from a `HashSet`, so cross-object SyncVar order within a frame is unspecified (`SyncVarSender.cs:20, 45`).

---

## 6. Bandwidth cheat sheet (derived from header constants, not measured)

| Item | Bytes | Source |
|---|---|---|
| Unreliable packet header | 1 | `ReliableConnection.cs:95`, `Batch.cs:79-83` |
| Reliable packet header (type, seq, ack, 8-byte mask, order) | 15 | `AckSystem.cs:16-21` |
| Notify packet header | 13 | `AckSystem.cs:16-19` |
| Empty ack packet | 11 | `AckSystem.cs:25` |
| Per-message batch length prefix | 2 | `Batch.cs:7` |
| Message id | 2 (fixed 16 bits) | `Assets/Mirage/Runtime/Serialization/MessagePacker.cs:101` |
| `NetId`, function index, payload length (packed) | 1 byte if value <= 240 (zigzag for the index), 2 if <= 2287, 3 if <= 67823 | `Assets/Mirage/Runtime/Serialization/Packers/PackedExtensions.cs:33-75` |
| `RpcMessage` overhead | 2 (len) + 2 (id) + 1 (NetId) + 1 (index) + 1 (length) = **7 bytes** + payload | `RpcMessages.cs:5-11` |
| `UpdateVarsMessage` overhead | 2 + 2 + 1 (NetId) + 1 (length) + per component 1 (index) + mask bits + values + 1 (barrier) | `Messages.cs:162-166`, `NetworkIdentity.cs:839-849` |
| Max message, default UDP (`MTU` 1232) | unreliable 1229, notify 1219, reliable 1215 (fragmented up to 50 x 1216) | `UdpSocketFactory.cs:237-256`, `Peer.cs:873-921` |

Worked example (arithmetic on the numbers above, 16-32 players; not a measurement):

- Client input, 71-bit `PlayerInput` (about 9 bytes), one unreliable ServerRpc per tick: 7 + 9 = 16 bytes per tick. With a 3-input redundant array (1-byte count + 27 bytes) it is 7 + 28 = 35 bytes. At 60 ticks/s that is 0.96 KB/s vs 2.1 KB/s of Mirage payload, plus 1 byte per packet and 28 (IPv4) or 48 (IPv6) bytes of IP/UDP per packet; with one packet per rendered frame at 60 fps the headers add about 1.7 KB/s.
- Server state, 134-bit `PlayerState` (about 17 bytes) as an unreliable `ClientRpc` to observers: 7 + 17 = 24 bytes per object per tick. Per client, 31 other players at 30 Hz is 31 x 24 x 30 = about 22 KB/s (fits one 1229-byte packet per tick); the server's total for 32 clients is about 700 KB/s, and `SendToMany` copies the packed message into each connection's batch (`NetworkServer.cs:671-695`).
- Packing all objects into one custom message per client per tick (each state keeping a 1-byte NetId instead of a 7-byte message header) would save about 31 x 6 - 7 = 180 bytes per tick per client.

---

## 7. Assessment: reuse, reuse with care, build

| | Items |
|---|---|
| **Reuse as is** | `NetworkIdentity`/`NetworkBehaviour` addressing; ownership and `OwnedObjects`; lifecycle events; unreliable `ServerRpc`/`ClientRpc` with owner/observer targeting; `[RateLimit]`, `[MaxLength]`, error budget; weaver serializers and bit-packing; generic behaviours with `T`-typed RPC parameters; spawn-baseline `OnSerialize(initialState: true)`; batching; `NetworkWorld` events |
| **Reuse with care** | Notify (not batched, ring shared with reliable, simulated reliable on TCP-like sockets); `ManualUpdate` (every frame, both halves); custom messages (own dispatcher and ownership check); `ISpawnValuesHandler` (main only); `FlushBatch()` for an early send *(unverified)*; `NetworkTime` as a seed (sibling report) |
| **Do not use for per-tick data** | SyncVars/`OnSerialize` deltas (reliable, 100 ms default, HOL, hook timing); reliable-by-default RPCs; return-value RPCs; `SyncList` |
| **Build** | Tick clock and alignment; per-object input/state ring buffers; resimulation and reconcile; remote snapshot interpolation; lag compensation; tick/sequence/redundancy protocol for unreliable data; tick-window validation; bandwidth shaping; host-mode and object-re-creation handling |

---

## 8. Unverified or worth a quick in-engine test

1. Weaver behaviour for `T[]`/`List<T>`/`ArraySegment<T>`/`Nullable<T>` RPC parameters inside a generic `NetworkBehaviour` (section 4 Q1).
2. A concrete subclass of an `abstract` generic base that declares concrete RPCs (no dedicated test found).
3. `IConnection.FlushBatch()` called from `FixedUpdate` as an "early send" (code reads as safe, no test or sample).
4. `ManualUpdate = true` ordering (not referenced by tests/samples).
5. Default `int/uint/long/ulong` encoding is variable-length packed (derived from extension-method registration; docs say 32 bits at `doc/docs/guides/bit-packing/index.md:5`).
6. Host-mode `SyncTo.Owner`-only caveat for remote-owned objects (file 02 section 3; only host-owned objects are covered by tests, `Assets/Tests/Runtime/Syncing/SyncDirectionSyncVarsTest_Host.cs:83-107`).
7. Retransmit latency after a lost *final* reliable packet (no timer-based resend found).
