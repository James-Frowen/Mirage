# 01 - Objects, Ownership, Lifecycle

**Summary.** A networked object is a `NetworkIdentity` (one per hierarchy root) plus any number of `NetworkBehaviour`s (max 255) addressed on the wire by *component index*. Lifecycle hooks are **events on `NetworkIdentity`** (there are no virtual `OnStartServer`/`OnStartClient` methods on `NetworkBehaviour`). "Owner" exists only on the server; on clients the equivalent is `HasAuthority`, and `IsLocalPlayer` is true only for the one object set as the player's *character*. Interest management removes objects from a client entirely (client copy is destroyed), which matters for any per-object prediction buffer.

Conventions: `path:line` refs are repo-relative at commit `7116e9f` (package version 158.0.0, `Assets/Mirage/package.json:4`). "Verified" = read in code; "Inferred" = reasoned from code but not executed; "Unverified" = needs an in-engine test.

---

## 1. NetworkIdentity and NetworkBehaviour basics

| Fact | Where |
|---|---|
| `NetworkIdentity` must be on the root of the hierarchy; nested identities unsupported | `Assets/Mirage/Runtime/NetworkIdentity.cs:24-26` |
| `NetworkBehaviours` array = `GetComponentsInChildren<NetworkBehaviour>(true)` filtered to those whose `Identity == this`; cached on first access; **max 255** because component index is written as one byte | `NetworkIdentity.cs:287-305, 319-348` |
| Component index on the wire is the array position; server and client must have identical prefab hierarchies | `NetworkIdentity.cs:816-826` (index byte), `:900-909` (error text) |
| `NetworkBehaviour.Identity` is found with `GetComponentInParent<NetworkIdentity>(true)`; **throws** `InvalidOperationException` if none | `Assets/Mirage/Runtime/NetworkBehaviour.cs:177-196, 233-248` |
| `NetworkBehaviour.ComponentIndex` is cached from position in `Identity.NetworkBehaviours` | `NetworkBehaviour.cs:203-226` |
| `NetworkBehaviour.Id` struct = `(NetId, ComponentIndex)` | `NetworkBehaviour.cs:580-614` |
| `NetId` is a server-assigned `uint` (starts at 1, per `ServerObjectManager`, or a custom `INetIdGenerator`) | `Assets/Mirage/Runtime/ServerObjectManager.cs:34-36`, `NetworkIdentity.cs:161` |

## 2. How a behaviour finds its server / client / world

All of these are forwarded through `Identity`; they are **null/false before the object is spawned** and are nulled again in `NetworkReset()` (after `OnStopServer`/`OnStopClient` have fired).

| Property on `NetworkBehaviour` | Source | Notes |
|---|---|---|
| `Server` (`NetworkServer`) | `NetworkBehaviour.cs:88` -> `NetworkIdentity.Server` | Set in `SetServerValues` (`NetworkIdentity.cs:971-981`) |
| `ServerObjectManager` | `NetworkBehaviour.cs:93` | server/host only |
| `Client` (`NetworkClient`) | `NetworkBehaviour.cs:98` | On server this is `networkServer.LocalClient` (non-null only in host mode) (`NetworkIdentity.cs:977`) |
| `ClientObjectManager` | `NetworkBehaviour.cs:103` | client/host only |
| `World` (`NetworkWorld`) | `NetworkBehaviour.cs:110` | Server's world on server/host, client's world on a pure client (`NetworkIdentity.cs:975, 997-1001`) |
| `NetworkTime` | `NetworkBehaviour.cs:115` = `World.Time` | NRE if not spawned |
| `Owner` | `NetworkBehaviour.cs:108` | **server only**; always null on clients |
| `IsServer` / `IsClient` / `IsHost` | `NetworkIdentity.cs:116-131` | `IsServer = IsSpawned && Server != null && Server.Active`; `IsClient = IsSpawned && Client != null && Client.Active`; `IsHost = IsSpawned && Server != null && Server.IsHost` |
| `IsServerOnly` / `IsClientOnly` | `NetworkBehaviour.cs:66-71` | |
| `IsLocalPlayer` | `NetworkIdentity.cs:137` | `Client.Player?.Identity == this` |
| `HasAuthority` | `NetworkIdentity.cs:144` | see section 5 |

Weaver guard attributes (`[Server]`, `[Client]`, `[HasAuthority]`, `[LocalPlayer]`, `[NetworkMethod]`) call these *per-object* properties, not the global `NetworkServer.Active` (`Assets/Mirage/Weaver/Processors/AttributeProcessor.cs:107-116`).

### Role matrix (what each flag is for each process)

| | Dedicated server | Remote client, owner | Remote client, non-owner | Host (server+client) |
|---|---|---|---|---|
| `IsServer` | true | false | false | true |
| `IsClient` | false | true | true | true |
| `IsHost` | false | false | false | true |
| `Owner` | player (or null) | null | null | player (or null) |
| `HasAuthority` | false (`SetServerValues` only sets it for the host player) | true | false | true iff owner is the local host player |
| `IsLocalPlayer` | false (`Client` null) | true iff this is the player's character | false | true iff this is the host player's character |

Host `HasAuthority` is set in `ServerObjectManager.Spawn` (`ServerObjectManager.cs:425-430`) and in `AddCharacter`/`ReplaceCharacter` (`:241-246`, `:179-184`), and re-derived from `SpawnMessage.IsOwner` in `SetClientValues` (`NetworkIdentity.cs:993`).

## 3. Lifecycle events

Events live on `NetworkIdentity` as `AddLateEvent`s (`NetworkIdentity.cs:430-496`). Subscribe with `Identity.OnStartServer.AddListener(...)` etc. Docs say the same (`doc/docs/guides/game-objects/lifecycle.md:30-34`).

**Late-add semantics:** if the event has already fired when you call `AddListener`, the handler is invoked *immediately* and also stays subscribed (`Assets/Mirage/Runtime/Events/AddLateEvent.cs:74-84`). Events with arguments (e.g. `OnAuthorityChanged(bool)`) replay the most recent argument. `ResetEvents()` is called in `NetworkReset()` (`NetworkIdentity.cs:1233-1242`) and resets start/stop/authority events but **not** `_onOwnerChanged`.

| Event | Fires on | When (code) |
|---|---|---|
| `OnStartServer` | server, host | In `ServerObjectManager.Spawn`, **before** `World.AddIdentity` and before observers are built (`ServerObjectManager.cs:432-442`, `NetworkIdentity.cs:585-595`, doc comment `:446`). `Owner` is already set if spawned with an owner; `NetId` is assigned; `observers` is empty. |
| `OnOwnerChanged(INetworkPlayer)` | server only | Inside `SetOwner` (`NetworkIdentity.cs:277`) |
| `OnAuthorityChanged(bool)` | client, host | `NotifyAuthority()` -> `CallStartAuthority/CallStopAuthority` (`NetworkIdentity.cs:625-654`); only when `HasAuthority` actually changed vs `_hadAuthority` |
| `OnStartClient` | client, host | After initial state deserialize and `NotifyAuthority`, once per spawn (`NetworkIdentity.cs:602-614`, guarded by `_clientStarted`) |
| `OnStartLocalPlayer` | client, host | After `OnStartClient`, only if this identity is `Client.Player.Identity` (`ClientObjectManager.cs:844-853`, `NetworkIdentity.cs:616-623`) |
| `OnStopClient` | client, host | On `ObjectDestroy`/`ObjectHide`/disconnect (`ClientObjectManager.cs:483-516`; host: `ServerObjectManager.cs:660-667`) |
| `OnStopServer` | server, host | `DestroyObject` (`ServerObjectManager.cs:669`) |

### Client spawn order (verified)

`ClientObjectManager.ApplySpawnPayload` (`ClientObjectManager.cs:554-578`):

1. `identity.SetClientValues(msg)` - applies `SpawnValues` (pos/rot/scale/name/active), sets `NetId`, `HasAuthority = msg.IsOwner`, `Client`, `ClientObjectManager`, `World`, `SyncVarSender` (`NetworkIdentity.cs:983-1005`).
2. If `msg.IsLocalPlayer`: `Client.Player.Identity = identity`.
3. `OnDeserializeAll(payload, initialState: true)` -> SyncVars/`OnDeserialize` run, **SyncVar hooks fire here** (see file 02).
4. `NotifyAuthority()` -> `OnAuthorityChanged(true)` if owner.
5. `StartClient()` -> `OnStartClient`.
6. `CheckForLocalPlayer()` -> `OnStartLocalPlayer`.
7. *After* all that (`ClientObjectManager.cs:639-641`): `World.AddIdentity` (so `TryGetIdentity(netId)` fails inside hooks/`OnStartClient` for the object itself - Inferred from ordering at `:626` vs `:637-641`).

Unity's `Awake`/`OnEnable` of the instantiated prefab runs *before* all of this (inside `Instantiate`, `ClientObjectManager.cs:712`); `NetworkBehaviour` network properties are not valid in `Awake`.

### Host-mode spawn order (verified)

Server: `Spawn` -> `StartServer()` -> `AddIdentity` -> `RebuildObservers(true)` -> `SendSpawnMessage` to the host player. Because the host connection is an in-process pipe that calls the receiver synchronously (`Assets/Mirage/Runtime/PipePeerConnection.cs:114-128`), the host client's `OnHostClientSpawn` runs **inline inside `Spawn`**: `SetClientValues`, `NotifyAuthority`, `StartClient`, `CheckForLocalPlayer` (`ClientObjectManager.cs:829-842`). **No payload is deserialized** in host mode - the server's object *is* the client's object. `InitialState` is therefore never true for the host.

If an object was spawned on the server before the host client connected, `FirstServerSpawn` back-fills client-side start events using a synthetic `SpawnMessage` (`ServerObjectManager.cs:95-133`).

## 4. Spawning

### Server side

- `ServerObjectManager.Spawn(identity[, owner | prefabHash])` (`ServerObjectManager.cs:382-443`): `SetOwner` -> `SetServerValues` -> assign `NetId` -> `StartServer` -> `World.AddIdentity` -> `RebuildObservers(initialize: true)`. Throws if server inactive; in-Editor throws if given a prefab asset (`:598-606`).
- `AddCharacter(player, identity)` makes the object the player's **character** (`player.Identity`), owner, and spawns it; throws if player already has one (`:224-254`). `ReplaceCharacter` swaps and by default removes authority from the previous character (`:158-199`).
- Player spawning is **user code**: `AddCharacterMessage` exists (`Messages.cs:16`) but no built-in handler registers it; the repo sample is `Assets/Mirage/Samples~/LegacySceneLoading/CharacterSpawner.cs:111-136`.
- `SpawnSceneObjects()` spawns all scene `NetworkIdentity`s that have a `SceneId` (`ServerObjectManager.cs:704-731`). It sorts them with a `NetId` comparer (`:724, 831-838`), but unspawned objects all have `NetId == 0` at that point, so the sort is effectively a no-op (Inferred) - do not rely on scene-object spawn order.
- Per-observer spawn message: `SpawnMessage { NetId, IsLocalPlayer, IsOwner, SceneId?, PrefabHash?, SpawnValues, Payload }` (`Messages.cs:32-65`). The **owner gets a different payload than observers** (`SendSpawnMessage` builds `ownerWriter` and `observersWriter`, `ServerObjectManager.cs:445-487`); see file 02 for what goes in each.
- `SpawnValues` default: position+rotation sent (unpacked `Vector3?`/`Quaternion?`), scale/name not, `SelfActive = ForceEnable` (`NetworkSpawnSettings.cs:30-36`, `ServerObjectManager.cs:544-566`).

### Client side

- Prefabs registered by hash in `ClientObjectManager` (`spawnPrefabs`, `NetworkPrefabs`, `RegisterPrefab`, `RegisterSpawnHandler`, async handlers, dynamic handlers) (`ClientObjectManager.cs:65-88, 209-479`).
- Async spawn handlers: while a spawn is pending, incoming `Rpc`, `UpdateVars`, `Hide`, `Destroy`, `RemoveAuthority`, `Spawn` messages for that `NetId` are queued and replayed after spawn (`ClientObjectManager.cs:132-164, 644-687, 960-1097`).
- Scene objects are matched by `SceneId` from `spawnableObjects` (populated by `PrepareToSpawnSceneObjects`) (`ClientObjectManager.cs:190-207, 715-745`).
- `NetworkWorld` (`Assets/Mirage/Runtime/NetworkWorld.cs`): per-side registry `_spawnedObjects`, `TryGetIdentity(netId)`, `GetSortedIdentities()`, events `onSpawn`, `onUnspawn`, `OnAuthorityChanged` (`:29-44`), `Time` (`:49`). Useful for a global "prediction manager" that hooks all objects without per-object listeners; `AddAndInvokeOnSpawn` helper at `Assets/Mirage/Runtime/Extensions/NetworkWorldExtensions.cs:28-35`.

## 5. Ownership and authority

Three separate concepts - do not conflate:

| Concept | Lives on | Meaning |
|---|---|---|
| **Owner** (`NetworkIdentity.Owner`, `INetworkPlayer`) | server (and host) only | The player whose RPCs/SyncVars are accepted for this object. Exactly one. `SetOwner` throws if already owned and new owner != null (`NetworkIdentity.cs:247-282`). |
| **HasAuthority** | client (and host) | "This process owns the object". Remote client: set from `SpawnMessage.IsOwner` (`:993`), cleared by `RemoveAuthorityMessage`/`RemoveCharacterMessage` (`ClientObjectManager.cs:747-789`). |
| **Character** (`player.Identity`) / **`IsLocalPlayer`** | server: `player.Identity`; client: `Client.Player.Identity` | The *one* object designated as the player's avatar. A weapon owned by the player has `HasAuthority == true` but `IsLocalPlayer == false`. |

Details:

- `INetworkPlayer.OwnedObjects` (server only) lists everything the player owns, character included (`NetworkPlayer.cs:144, 368-386`). Use it to validate messages sent by a client (comment at `:141`).
- `AssignClientAuthority(player)` (server): `SetOwner` then `SendSpawnMessage(this, player)` - i.e. the owner gets a **full spawn message again** for the existing object, which re-applies initial state (hooks fire again, `InitialState == true`) and notifies authority (`NetworkIdentity.cs:1136-1161`; client path `ClientObjectManager.cs:596, 626`). `RemoveClientAuthority()` sends only `RemoveAuthorityMessage` (`:1169-1193`) and cannot remove authority from the player's character (throws).
- When ownership is removed the server records `AuthorityRemovedTime = (oldOwner, unscaledTime)`; ServerRpcs from the old owner within **1 s** are dropped without error cost (`NetworkIdentity.cs:259-262`, `RpcHandler.cs:14, 137-153`).
- `OnAuthorityChanged` fires on **each** gain/loss; world-level `NetworkWorld.OnAuthorityChanged(identity, hasAuthority, owner)` fires on both server and client (owner is null on pure clients) (`NetworkWorld.cs:14, 44, 103-106, 161-164`).
- Owner disconnect: `NetworkServer.OnDisconnected` -> `Disconnected` event -> `player.DestroyOwnedObjects()` (all owned, character last) (`NetworkServer.cs:669-687`, `NetworkPlayer.cs:430-463`). Remove an object from the owned list in the `Disconnected` handler to keep it alive (doc comment on `IObjectOwner.DestroyOwnedObjects`, `Assets/Mirage/Runtime/INetworkPlayer.cs:110-115`).
- The owner is always added to `observers` when `Owner.SceneIsReady` (`NetworkIdentity.cs:1060-1064`).
- ServerRpc authority check is simply `identity.Owner == sender` (`RpcHandler.cs:200-212`).

## 6. Visibility / interest management

- `NetworkIdentity.observers` (`HashSet<INetworkPlayer>`, server only) is the set of players that have the object spawned on their client. Only **ready** players (`SceneIsReady`) can be observers (`NetworkIdentity.cs:1075-1077, 1112`). `SceneIsReady` is set by the scene-loading helper (`Assets/Mirage/Components/NetworkSceneLoader.cs:83-167`); it defaults to **true** for a new connection (`NetworkPlayer.cs:107`).
- Visibility policy: `INetworkVisibility` (`OnCheckObserver(player)`, `OnRebuildObservers(set, initialize)`). If the identity has no `NetworkVisibility` component, `AlwaysVisible` is used (all authenticated, scene-ready players) (`Assets/Mirage/Runtime/INetworkVisibility.cs:14-37`, `NetworkIdentity.cs:355-379`). `NetworkVisibility` must be on the **same GameObject** as the identity.
- **Observers are not recomputed automatically.** `RebuildObservers(false)` runs only when something calls it. Built-ins: `NetworkProximityChecker` calls it via `InvokeRepeating` every `VisibilityUpdateInterval` (default 1 s) (`Assets/Mirage/Components/Visibility/NetworkProximityChecker.cs:65, 74-90`); `NetworkMatchChecker` on match change; `SceneVisibilityChecker`/`NetworkSceneChecker` (obsolete) on scene change.
- New observer -> spawn message; removed observer -> `ObjectHideMessage` (`ServerObjectManager.cs:275-321`). On the client, `ObjectHide` is handled **identically to destroy**: `DestroyObject` -> `UnSpawn` -> `OnStopClient`, then `Destroy(gameObject)` (or unspawn handler/pool) (`ClientObjectManager.cs:791-827, 483-516`). **Any client-side prediction state on the object is lost when it leaves visibility** and is rebuilt from a fresh spawn when it re-enters.
- Destroy -> `ObjectDestroyMessage` to remote observers (`ServerObjectManager.cs:657`); host skips (`ClientObjectManager.cs:108-109`).
- What visibility gates: SyncVar/`OnSerialize` updates (sent to `observers` only, `SyncVarSender.cs:48`), ClientRpc with `RpcTarget.Observers`. It does **not** gate `RpcTarget.Owner/Player` (no observer check, `ClientRpcSender.cs:28-38, 63-74`) or custom messages.
- `NetworkVisibility.OnVisibilityChanged` is invoked on the server before the show/hide message is sent (`NetworkVisibility.cs:14-24`, `ServerObjectManager.cs:277-279`). `HostRendererVisibility` shows the pattern for host-mode (no spawn/destroy, so hide renderers instead) (`Assets/Mirage/Components/Visibility/HostRendererVisibility.cs`).

## 7. Destroy / unspawn

- Server `Destroy`: `RecentlyDestroyed.Add` (RPC grace), remove from world, `ObjectDestroyMessage` to remote observers, `ClearObservers`, host-side `CallStopAuthority`/`StopClient`, `StopServer`, `NetworkReset`, then `UnityEngine.Object.Destroy` unless `destroyServerObject:false` (`ServerObjectManager.cs:641-677`).
- RPCs/SyncVar updates for an unknown `NetId` are dropped with a warning; on the server each such ServerRpc adds 1 error token unless the id was destroyed within 1 s (`RpcHandler.cs:84-114`, `SyncVarReceiver.cs:42-45`).
- Client disconnect: every non-host object is `UnSpawn`ed (`ClientObjectManager.cs:522-552`).

## 8. Relevance to SimpleCSP (this file only)

- Per-object prediction can hang entirely off `Identity.OnStartClient / OnStartLocalPlayer / OnAuthorityChanged / OnStopClient` + `HasAuthority`. For non-character owned objects (weapons) use `HasAuthority`, **not** `IsLocalPlayer`.
- On the server, `Owner` identifies which `INetworkPlayer` inputs must come from; `OwnedObjects` is the authoritative list.
- Expect object re-creation on visibility changes and a full-state re-apply on `AssignClientAuthority`/`ReplaceCharacter` (both reset any local predicted state unless you guard with `InitialState`).
- Host mode must be special-cased everywhere (no SyncVar deserialize, no hooks from the network, synchronous client callbacks) - detailed in file 07.
