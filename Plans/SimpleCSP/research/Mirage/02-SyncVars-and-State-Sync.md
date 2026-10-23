# 02 - SyncVars and State Sync

**Summary.** `[SyncVar]` auto-properties set a per-component dirty bit; dirty objects are collected in a `HashSet` and serialized once per frame by `SyncVarSender.Update()` (called from `NetworkServer.UpdateSent` / `NetworkClient.UpdateSent`), gated by a per-`NetworkBehaviour` `SyncSettings` (direction, timing, interval; **default 0.1 s**). Everything - SyncVars, SyncLists, `OnSerialize` data, initial state - travels in an `UpdateVarsMessage` on the **Reliable channel only**; there is no per-behaviour channel option. One `OnSerialize` result is shared by owner and observers (identical bytes), so "different data for owner vs observers" requires two components.

---

## 1. Declaring SyncVars (weaver behaviour)

- Must be an **auto-property** with simple `get; set;` (any accessibility), non-static, not an array, not an `ISyncObject` (`Assets/Mirage/Weaver/Processors/SyncVarProcessor.cs:77-107`). A `[SerializeField]` is injected on the backing field and `UnityEngine`/`Mirage.Serialization` attributes on the property (e.g. `[BitCount]`) are copied onto it (`:159-192`).
- Max SyncVars per class **chain**: the check is `inBase + inClass >= 64` -> error, so effectively **63** (`Assets/Mirage/Weaver/Processors/NetworkBehaviour/ConstFieldTracker.cs:34`, `FoundNetworkBehaviour.cs:18`). Dirty mask is a `ulong` (`NetworkBehaviour.cs:123`).
- Attribute options (`Assets/Mirage/Runtime/CustomAttributes.cs:11-37`): `hook`, `hookType` (method/event with 0/1/2 args; `Automatic` searches), `initialOnly` (only in spawn payload, setter doesn't mark dirty), `invokeHookOnServer`, `invokeHookOnOwner`. `invokeHook*` without a hook is a weaver error (`FoundSyncVar.cs:104-105`).
- Reference-typed SyncVars: `NetworkIdentity`, `GameObject`, `NetworkBehaviour` are wrapped in `*Syncvar` structs that carry the netId and lazily resolve through `IObjectLocator` (`FoundSyncVar.cs:61-85`, `Assets/Mirage/Runtime/NetworkIdentitySyncvar.cs`). Classes produce a weaver warning unless `[WeaverSafeClass]` (`SyncVarProcessor.cs:109-115`).
- Change detection uses `EqualityComparer<T>.Default` (`NetworkBehaviour.cs:301-305`). A struct SyncVar without `IEquatable<T>` boxes and uses `ValueType.Equals`; a struct that includes a per-tick counter is *always* "changed".

### What the setter does (equivalent of the generated IL, `SyncVarProcessor.cs:235-331`)

```csharp
set {
    if (SyncVarEqual(value, field)) return;      // no-op if equal
    var old = field;  field = value;
    SetDirtyBit(1UL << index);                   // NetworkBehaviour.cs:312
    if (hasHook && !GetSyncVarHookGuard(bit)) {  // re-entrancy guard
        if ((invokeHookOnOwner && HasAuthority) ||
            (invokeHookOnServer ? IsServer : IsHost)) {
            SetSyncVarHookGuard(bit, true); Hook(old, value); SetSyncVarHookGuard(bit, false);
        }
    }
}
```

So by default **the setter fires the hook only in host mode**; a dedicated server never runs its own hook unless `invokeHookOnServer = true`; an owning client runs it only with `invokeHookOnOwner = true`. A non-authoritative client that assigns the property just stores locally (dirty bit set, nothing queued: `SetDirtyBit` only enqueues if `SyncSettings.ShouldSyncFrom(...)`, `NetworkBehaviour.cs:312-321`).

## 2. Dirty tracking and the send loop

1. `SetDirtyBit(mask)` ORs `_syncVarDirtyBits` and, if this side may send (see section 4), calls `Identity.SyncVarSender.AddDirtyObject(identity)` -> a `HashSet<NetworkIdentity>` (`Assets/Mirage/Runtime/SyncVarSender.cs:16, 19-24`). Iteration order of that set is not by `NetId`, so **cross-object message order within a frame is unspecified**.
2. `SyncVarSender.Update()` (`SyncVarSender.cs:26-69`), once per call (= once per frame):
   - `now = Time.unscaledTimeAsDouble`.
   - For each dirty identity: if it has observers **or** `HasAuthority` -> `SendUpdateVarsMessage`; then if `StillDirty()` (some component's interval hadn't elapsed) it is carried to the next frame. If no observers and no authority, all dirty bits are **cleared without sending** (the next spawn carries full state).
3. `NetworkIdentity.OnSerializeDelta(now, ownerWriter, observersWriter)` walks every `NetworkBehaviour` (`NetworkIdentity.cs:693-784`):
   - skip unless `comp.ShouldSync(now)` = any dirty bit **and** `now >= _nextSyncTime` (`NetworkBehaviour.cs:362-376`);
   - skip unless `SyncSettings.ShouldSyncFrom(identity, false)` (direction check);
   - write `[component index byte] [OnSerialize payload] [barrier byte 171]` into the owner writer, the observers writer, or both (section 5);
   - after the loop, for every serialized component `ClearShouldSync(now)` = update `_nextSyncTime` per `SyncTiming` and clear dirty bits + flush SyncObjects (`:771-781`, `NetworkBehaviour.cs:334-354`).
4. Result is sent as `UpdateVarsMessage { NetId, Payload }` (`Assets/Mirage/Runtime/Messages.cs:163-167`) once for the owner (if anything was written for the owner and owner is a remote, scene-ready player) and once for all remote observers *excluding owner* (`SyncVarSender.cs:74-133`).

**Dirty bits are cleared unconditionally once serialized.** The `bool` returned by `OnSerialize` is **ignored** by `NetworkIdentity` (`NetworkIdentity.cs:821`), contrary to the doc comment at `NetworkBehaviour.cs:392-394`. There is no retry; delivery is delegated to the reliable channel. Also, the doc text "non-dirty behaviours write a zero" (`NetworkIdentity.cs:62`, `doc/docs/guides/sync/serialization-flow.md`) is stale - non-dirty components are simply omitted.

### SyncSettings (per `NetworkBehaviour`, not per SyncVar)

`public SyncSettings SyncSettings = SyncSettings.Default;` (`NetworkBehaviour.cs:34`). Default: `From = Server`, `To = Owner | ObserversOnly`, `Interval = 0.1f`, `Timing = Variable` (`Assets/Mirage/Runtime/SyncSettings.cs:24-29`).

| `SyncTiming` | `_nextSyncTime` update | Effect |
|---|---|---|
| `Variable` (default) | `now + Interval` | At least `Interval` between sends; a burst of changes is coalesced to the latest value. |
| `Fixed` | `_nextSyncTime += Interval` | Steady `1/Interval` Hz *if continuously dirty*. After an idle period `_nextSyncTime` is far in the past, so subsequent frames send **every frame** until it catches up (Inferred from `SyncSettings.cs:45-49` + `NetworkBehaviour.cs:373-376`; untested). |
| `NoInterval` | `now` | Send on the next `SyncVarSender.Update`. |

(`SyncSettings.cs:37-56, 262-296`). Timing/Interval are only evaluated at flush time, which is once per frame (file 04), so the effective floor is one frame.

## 3. Sync direction (`SyncFrom` / `SyncTo`)

Flags: `SyncFrom {None, Owner=1, Server=2}`, `SyncTo {None, Owner=1, ObserversOnly=2, Server=4}` (`SyncSettings.cs:241-260`). `ObserversOnly` **excludes the owner**; add `Owner` to include it. Validity rules: `IsValidDirection` (`:133-184`) - e.g. `From.Owner` requires `To.Server`; `To.Owner` requires `From.Server`; `Server -> Owner only` *is* valid, whereas `Owner -> Owner` isn't.

| Config | Who sends | Who receives | Notes |
|---|---|---|---|
| **Default** `From.Server`, `To.Owner|Observers` | server | owner + other observers | one `OnSerialize` result copied to both |
| `From.Server`, `To.ObserversOnly` | server | observers except owner | writes only to observers writer (`ToObserverWriterOnly`, `SyncSettings.cs:95-119`) |
| `From.Server`, `To.Owner` | server | owner only | **Host-mode caveat:** `ShouldSyncFrom(identity, false)` returns `!identity.IsClient` for this case (`SyncSettings.cs:62-74`), and `IsClient` is true for *every* spawned object when the server is a host - so owner-only SyncVars/`OnSerialize` are never enqueued/serialized in host mode **even for objects owned by remote clients** (Inferred from code; the host tests only cover host-owned objects, `Assets/Tests/Runtime/Syncing/SyncDirectionSyncVarsTest_Host.cs:83-107`). Owner-only SyncObjects do get enqueued (`syncInHostMode: true`, `NetworkBehaviour.cs:274-281`) but are then skipped in `OnSerialize` and stay dirty (Inferred). Use `To = Owner|ObserversOnly` instead if you need host-mode correctness. |
| `From.Owner`, `To.Server` | owner client | server only | server validates `sender == Owner` and some component has `To.Server` (`SyncVarReceiver.cs:47-70`) |
| `From.Owner`, `To.Server|ObserversOnly` | owner client | server, then **relayed by server** to remote observers (not owner, not host) | relay is inline in the receive handler via `ForwardToObservers`, so it ignores interval/timing (`NetworkIdentity.cs:937-969`) |
| `From.Server|Owner`, `To.ObserversOnly` | both | observers | documented race, avoid (`doc/docs/guides/sync/sync-settings.md`) |

Owner -> server sends use the same machinery on the client: `NetworkClient.UpdateSent` runs the client's `SyncVarSender` (`NetworkClient.cs:376-380`); identities are enqueued if `HasAuthority`; the `UpdateVarsMessage` goes `Client.Player.Send` on Reliable (`SyncVarSender.cs:109-133`). Receiving side checks `ThrowIfInvalidSendToServer` (`NetworkIdentity.cs:910-922`).

## 4. Channel and reliability - the key limitation

`SendUpdateVarsMessage` calls `player.Send(varsMessage)` (owner) and `identity.SendToRemoteObservers(varsMessage, false)` (observers) with **default `Channel.Reliable`** (`SyncVarSender.cs:100-104, 130`; defaults at `NetworkServer.cs:748-751`, `NetworkPlayer.cs:239`). `SyncSettings` has no channel field. The initial `SpawnMessage` is reliable too (`ServerObjectManager.cs:485`).

Therefore:
- **A SyncVar cannot be sent unreliably** without modifying Mirage.
- SyncVars/SyncLists/`OnSerialize` data share the single reliable stream with RPCs, spawn/destroy, and reliable custom messages -> head-of-line blocking after packet loss (file 03 section 7).
- The reliable layer coalesces nothing semantically: only the **latest value at flush time** is sent per frame, but every flush that has dirty data is delivered, in order, even if stale by arrival.

## 5. Owner vs observers: can they get different data?

- Per **component**: `NetworkIdentity.OnSerialize` calls `comp.OnSerialize(...)` **once** and copies the bit range into the observers writer when `CopyToObservers` (`NetworkIdentity.cs:735-768`, `SyncSettings.cs:121-131`). The serializer gets no hint who it is writing for. So one component can send "owner only", "observers only", or identical bytes to both.
- Per **object**: put two behaviours on the same identity - e.g. `PredictedOwnerState` with `To = Owner` and `PredictedRemoteState` with `To = ObserversOnly` - each serialized once; recipients get different payloads. The owner and observers messages are separate `UpdateVarsMessage`s (the owner's contains only owner-destined components).
- Initial state (spawn) uses the same split: `OnSerializeInitial` writes every component that has `To & (Owner|ObserversOnly)`, even those with `From.Server == false` (`NetworkIdentity.cs:718-724`); owner gets `ownerWriter`, others `observersWriter` (`ServerObjectManager.cs:464-471`). Host player: the spawn payload is built but ignored by the host client (file 01).

## 6. Wire format and cost

`UpdateVarsMessage` = `[msg id: 16 bits][NetId: packed uint][Payload: packed (count+1), pad to byte, raw bytes]`. Payload = repeated `[component index: 8 bits][component data][0xAB]` (`NetworkIdentity.cs:670, 816-826`). Component data = `SerializeObjects` (only if the behaviour has SyncObjects: packed `ulong` mask + deltas, `NetworkBehaviour.cs:470-516`) then generated `SerializeSyncVars`: **n-bit dirty mask where n = SyncVars declared in that class** followed by the dirty values only (`SerializeHelper.cs:75-100`, `SyncVarProcessor.cs:562-606`). Inherited SyncVars come first through the base call (`BaseMethodHelper.cs:47-62`).

Rule of thumb: ~6-8 bytes of message/identity overhead + 2 (component index + barrier) + mask bits + values, plus 2 bytes batch length (file 03) and 15-byte reliable packet header amortized across everything batched that frame. Each observer gets its own copy (serialized once, `SendToMany` loops `players[i].Send(segment)`, `NetworkServer.cs:644-666`).

Primitive encodings are in file 05; notably `int`/`uint`/`long`/`ulong` SyncVars default to a variable-length packed encoding, not 32 bits (derived from the weaver's extension-method registration; verify in-engine).

## 7. Initial state on spawn

- Payload comes from `identity.OnSerializeInitial` (`NetworkIdentity.cs:677-680`): **all** SyncVars of each included component (generated `WriteIfInitial` writes every var, including `initialOnly`, with no dirty mask) plus `SerializeObjectsAll` (`SyncVarProcessor.cs:578-584`, `NetworkBehaviour.cs:475-481`). Ignores dirty bits and sync interval.
- Dirty bits are **not cleared** by initial serialization (`!initialState` guard at `NetworkIdentity.cs:772`). Consequence (Inferred): values set during/after `OnStartServer` (when `IsServer` is true and the object is enqueueable) cause one redundant delta with the same values shortly after the spawn message; clients' hooks don't re-fire because old == new.
- Client: `OnDeserializeAll(payload, initialState: true)` with `InitialState` property true for the duration (`NetworkIdentity.cs:841-882`). `UpdateSyncObjectShouldSync()` is run afterwards so SyncList read-only flags match the new role (`NetworkBehaviour.cs:475-480, 523-527`).
- A *second* spawn message for an existing object (`AssignClientAuthority`, `ReplaceCharacter`'s `Respawn`) re-applies initial state over the live object (file 01 section 5).

## 8. Receiving: hooks and ordering inside a message

Receive path: `UpdateVarsMessage` handler -> `SyncVarReceiver.OnUpdateVarsMessage` -> `identity.OnDeserializeAll(reader, false)` (`SyncVarReceiver.cs:27-45`; client registers it in `ClientObjectManager.cs:129`, server in `SyncVarReceiver.cs:22-25`). Unknown `NetId` -> warning and drop (`:42-45`). Pending async spawn -> queued (`ClientObjectManager.cs:154-164`).

`OnDeserializeAll` loops components in the order written; per component: validate -> `comp.OnDeserialize` -> check barrier -> `ClearDirtyBit(_deserializeMask)` (clears only the bits just received so a locally changed-but-not-yet-sent value isn't lost, `NetworkIdentity.cs:884-898`).

**Hook timing (generated by `GenerateDeserialization`, `SyncVarProcessor.cs:616-668`):**

| Case | Behaviour |
|---|---|
| Initial state | Read **all** SyncVars of the component first, *then* run hooks for each var whose new value != value before the read (`:632-648`). Hooks run before `NotifyAuthority`/`OnStartClient` and before the object is in `World` (file 01). |
| Delta | For each dirty var **in declaration order: read -> assign -> run hook immediately** (`:653-665`). Later vars in the same message still hold their *old* values while earlier hooks run. Components later in the identity are not yet deserialized either. |
| Unchanged value | `EndHook` skips the hook if `SyncVarEqual(old, new)` (`:711-755`). If client code had locally overwritten the property with the value that then arrives, no hook fires. |
| Server receiving Owner->Server data | Hook suppressed unless `invokeHookOnServer` (`:723-732`). |
| Host | No receive path; the setter invokes the hook (section 1). |

Exceptions thrown from a hook propagate through `MessageHandler`, which on a client **disconnects by default** (`NetworkClient.DisconnectOnException = true`, `NetworkClient.cs:47, 144`; `MessageHandler.cs:153-163`). (RPC bodies are caught separately in `RpcHandler.HandleRpc` and do not disconnect.)

Hook signatures: 0/1/2 args (`new`, or `old,new`), static or instance, or a C# `event` (`CustomAttributes.cs:58-94`, `HookMethodFinder.cs:27-80`).

## 9. Ordering relative to RPCs and messages

- Reliable RPCs/messages and SyncVar updates all go onto the same per-connection ordered reliable stream **in the order they are enqueued**. RPCs/messages are enqueued at call time; SyncVar data is enqueued at the flush point inside `NetworkServer.UpdateSent` (`NetworkServer.cs:417-421`). So within one frame: an RPC called *before* the flush arrives *before* the SyncVar delta that includes the same-frame change; an RPC called after the flush (later in the frame, `LateUpdate`) arrives after it. Which side of the flush your code is on depends on script execution order (file 04).
- **Unreliable** RPCs/messages are a separate packet stream: no ordering relative to SyncVars at all. After a reliable loss the unreliable stream can be arbitrarily "ahead" of the (blocked) SyncVar stream, and a late SyncVar can then overwrite newer unreliable-derived state with an older value (file 07 gotchas).
- Host: no ordering concerns for SyncVars (no transport), but client RPC callbacks run inline at call time (file 03).

## 10. SyncObjects (SyncList / SyncDictionary / SyncSet / SyncStack)

Operation-log based: changes accumulate in `_changes`, are serialized as a list of ops, and applied in order on the receiver, with a `_changesAhead` counter to skip ops already included in a previously sent full state (`Assets/Mirage/Runtime/Collections/SyncList.cs:57-188, 193-240`). They require reliable in-order delivery and are marked read-only on the non-sending side (`SetShouldSyncFrom`, `:15`; `ThrowIfReadOnly` `:106`). Not suited to per-tick input/state, but fine for low-rate inventory-like data. `OnChange` is raised on the receiver after deserialization (`ISyncObject.cs:12-18`). `NetworkBehaviour.SyncObject_OnChange` enqueues the identity as dirty when the side is allowed to sync (`NetworkBehaviour.cs:274-281`).

## 11. Bit-packing attributes on SyncVars

All of `[BitCount]`, `[BitCountFromRange]`, `[ZigZagEncode]`, `[FloatPack]`, `[Vector2Pack]`, `[Vector3Pack]`, `[QuaternionPack]`, `[VarInt]`, `[VarIntBlocks]` target `Field | Property | Parameter` (`Assets/Mirage/Runtime/Serialization/WeaverAttributes.cs:47-146`) and are resolved for SyncVars via `ValueSerializerFinder.GetSerializer(syncVar...)` (`Assets/Mirage/Weaver/Serialization/ValueSerializerFinder.cs:15-18, 127-170`; `FoundSyncVar.cs:102`). Example:

```csharp
[SyncVar, BitCount(10)] public int Health { get; set; }
[SyncVar, Vector3Pack(100f, 20f, 100f, 0.01f)] public Vector3 Pos { get; set; }
```

Details and caveats in file 05.

## 12. "State + tick" consistently - what it takes

Goal: the receiver must see `(tick, state)` as an atomic pair.

| Approach | Atomicity | Cost / caveats |
|---|---|---|
| **A. One struct SyncVar** `struct PredState { ushort Tick; ...state }` | Atomic: one value, one dirty bit, serialized together, delivered in one message. A hook gets the whole struct. | Needs `IEquatable` to avoid boxing; hook fires only if struct != old. Reliable only; Variable/100 ms default must be changed to `NoInterval`. |
| **B. Several SyncVars in one component** | Same message and same dirty mask, so they *arrive* together, but **delta hooks fire one at a time** (section 8). A hook on `State` can see the old `Tick`. | Put everything in a single hook target or read all in `OnDeserialize` override. |
| **C. Override `OnSerialize`/`OnDeserialize`** (`NetworkBehaviour.cs:399-419`) and call `SetDirtyBit(1)` each sim tick | Full control: write `tick` + any compact/delta format; read all, then act once. `initialState` flag distinguishes spawn. Used as precedent by `NetworkTransformBase` (`Assets/Mirage/Components/NetworkTransformBase.cs:95-111, 214-218, 359-365`). | If you override `OnSerialize` without calling `base`, SyncVars and SyncObjects of that class are not serialized (base writes them, `NetworkBehaviour.cs:399-406`). Weaver won't generate `SerializeSyncVars` if you define a method of that exact name (`BaseMethodHelper.cs:64-71`). Return value ignored. |
| **D. Two components (owner / observers)** | Different payloads per audience | Component-per-audience bookkeeping. |

For all: set `Timing = NoInterval` (or small `Interval`) on the component; remember only the *latest* state at flush time is sent, so skipped ticks are not replayed - which is usually what reconciliation wants (latest authoritative `(tick, state)`), but means SyncVars cannot carry a history. For unreliable or acked delivery you must leave the SyncVar system (custom message / RPC / Notify; files 03, 07).

## 13. Host-mode summary for state sync

- Host's own client never receives `UpdateVarsMessage` (no handler registered in host mode, `ClientObjectManager.cs:106-113`); server sends skip `LocalPlayer` (`NetworkServer.cs:626, 748-751`; `SyncVarSender.cs:117-119`).
- The client side of an object *is* the server object: no deserialization, no hooks from the network, `InitialState` never true.
- SyncVars with `To = Owner` only are not marked for sync anywhere in host mode, including for remote-owned objects (see the caveat in the section 3 table; `SyncSettings.cs:58-75`).
- Setter-invoked hooks emulate "client received" for the host (section 1).

## 14. Gotchas checklist (details in file 07)

1. Reliable-only, shared with RPCs -> HOL blocking and stale-overwrite risk.
2. Default 100 ms `Variable` interval; set `NoInterval` for tick-rate data.
3. Delta hooks fire per-variable, not per-message.
4. Dirty bits cleared whether or not delivered; `OnSerialize` return ignored.
5. `HashSet` ordering across objects.
6. Spawn payload re-applied on `AssignClientAuthority`/`ReplaceCharacter` (resets local predicted fields; `InitialState == true` during hooks).
7. Stale documentation: `NetworkBehaviour.cs:392-394`, `serialization-flow.md`, `bit-packing/index.md:5`.
