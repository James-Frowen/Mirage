> Verified against main @ 0638bf8e5

# 06 - Existing Components and Precedents

**Summary.** Mirage's `Components/` folder is mostly infrastructure (scene loading, visibility, HUD, headless helpers). The only state-sync component is `NetworkTransform` (+`NetworkTransformChild`), which is marked `[Obsolete]`/"not for production" and uses the plain SyncVar pipeline (reliable, 100 ms default) with simple two-point interpolation; `NetworkAnimator` is demoted to a legacy sample. A repo-wide search for prediction, reconciliation, rewind, lag compensation and snapshot buffers finds **nothing**; the only interpolation is in `NetworkTransformBase`. The closest design precedents are the `Notify` sample (ack-aware deltas), `NetworkTransformBase` (custom `OnSerialize` + `SetDirtyBit`), and the owner->server `ReadyCheck` SyncVar.

---

## 1. Inventory

`Assets/Mirage/Components/` (assembly `Mirage.Components`):

| Component | What it does | Relevance to CSP |
|---|---|---|
| `NetworkTransform`, `NetworkTransformChild` (`NetworkTransformBase`) | Position/rotation/scale sync, optional client authority | Precedent for custom `OnSerialize` + interpolation; **not** prediction. Obsolete. |
| `NetworkSceneLoader` | Scene loading + player spawn helper; sets `SceneIsReady`, registers `SceneReadyMessage` | Defines when a player becomes an observer (`NetworkSceneLoader.cs:61-181`) |
| `OnlineOfflineScene`, `LobbyReady`, `ReadyCheck` | Lobby/ready flow; `ReadyCheck` is an owner->server SyncVar with hooks | Pattern for owner-authored SyncVar (`ReadyCheck.cs:15-16`) |
| `SyncObjectActive` | SyncVar `_active` with hook + `invokeHookOnServer` | tiny example of server-side hook usage |
| Visibility: `NetworkProximityChecker`, `NetworkMatchChecker`, `SceneVisibilityChecker`, `NetworkSceneChecker` (obsolete), `AlwaysNetworkVisibility`, `HostRendererVisibility` | Interest management | Controls who gets state; changes destroy/recreate client objects (file 01) |
| `NetworkManagerHud`, `NetworkManagerGUI`, `NetworkPingDisplay`, `DisplayMetrics/*` | Debug UI, RTT display (`Client.World.Time.Rtt`), peer metrics viewer | `RequestServerMetrics` shows how to read `Metrics` frames |
| `HeadlessAutoStart`, `HeadlessFrameLimiter` | Start server in batch mode; set `Application.targetFrameRate = 30` | Defines server frame/flush rate (file 04) |

Elsewhere:
- `Assets/Mirage/Runtime/` core (file README). `Assets/Mirage/Authenticators/BasicAuthenticator.cs` (auth flow).
- `Assets/Mirage/Samples~/` (`Assets/Examples` is a symlink to it): `Basic`, `Chat`, `Pong`, `Tanks`, `RigidbodyPhysics`, `Notify`, `SendPrefab`, `InterestManagement`, `LegacyNetworkAnimator`, scene-loading samples, `Snippets` (docs code). `LegacyNetworkAnimator/NetworkAnimator.cs:22` is `[Obsolete("NetworkAnimator is not optimzied so should not used in production.")]`.
- Docs list `network-animator`, `network-discovery`, etc. under `doc/docs/components/`; `NetworkAnimator` is not in the runtime `Components` folder any more.

## 2. NetworkTransform in detail

Source: `Assets/Mirage/Components/NetworkTransformBase.cs` (451 lines). Banner: `[Obsolete("NetworkTransform is not optimzied so should not used in production. Use NetworkPositionSync instead")]` and an editor help-box pointing to the external `James-Frowen/NetworkPositionSync` project (`:25-46`, doc `doc/docs/components/network-transform.md:6-8`). That external project is outside this repo and was not inspected.

**How state travels**
- It overrides `OnSerialize/OnDeserialize` (not SyncVars): writes `Vector3 localPosition`, `Quaternion localRotation` (**lossy 29-bit** `WriteQuaternion`, despite the comment "we do NOT compress rotation"), `Vector3 localScale` (`:95-111, 214-218`). ~221 bits + framing.
- The server's `Update` checks `HasEitherMovedRotatedScaled()` against sensitivity thresholds (0.01 position/rotation/scale) and calls `SetDirtyBit(1UL)` (`:312-333, 344-365`).
- Rate and channel are the generic `SyncSettings` (default 0.1 s, `Variable`) and **Reliable** (SyncVar path; file 02). So default transform sync is ~10 Hz reliable. `doc/docs/components/network-transform.md:18` confirms "Sync Mode and Sync Interval are controlled by the NetworkBehaviour's SyncSettings".
- Client authority (`ClientAuthority` bool): the owner moves locally and sends `[ServerRpc] CmdClientToServerSync(byte[] payload)` (Reliable by default) at most every `SyncSettings.Interval` when moved; the server deserializes, then `SetDirtyBit(1)` so the normal path rebroadcasts (`:220-239, 367-387`). The sync *direction* flags are **not** used for this (it predates them); default `From.Server` stays set.

**Interpolation (client, non-authority)**
- On each received point `DeserializeFromReader` creates a `DataPoint` stamped with local `Time.time` (arrival time, **not** a server tick/timestamp) and re-bases `start` at the current transform when close enough (`:127-212`).
- Position: `Vector3.MoveTowards(current, goal, speed * Time.deltaTime)` with speed = `max(start.speed, goal.speed)`, where speed is estimated from distance / arrival-time delta (`:118-124, 257-274`). Rotation/scale: `Slerp`/`Lerp` using arrival-time ratio (`:242-255, 276-294`).
- Teleport: if `Time.time - goal.timestamp > 5 * (goal.timestamp - start.timestamp)`, snap and clear points (`:296-309, 395-402`).
- Runs in `Update`; the header comment says FixedUpdate handles rigidbodies (`:8`) but no `FixedUpdate` exists (stale). No extrapolation, no buffer depth, no clock-sync use.

**Host mode:** no `UpdateVarsMessage` is delivered to the host client, so `OnDeserialize` never runs there and `goal` stays null for server-authoritative objects - the host never interpolates them (it *is* the authority). For client-authority objects owned by a remote client, the server's `CmdClientToServerSync` fills `start/goal`, and because `IsClient` is true on the host, `UpdateClient` interpolates that object on the host (`NetworkTransformBase.cs:221-239, 352-356, 392-411`; Inferred from code, not run).

**Takeaways:** reusable ideas - dirty-bit-driven `OnSerialize`, sensitivity gating, teleport heuristics. Not reusable for CSP: timestamps are local arrival times, reliable-only, 10 Hz, no tick association, no rewind.

## 3. Search results for prediction / lag-comp precedents

Performed across `Assets/Mirage` (Runtime, Components, Weaver, Samples~), `Assets/Tests`, `doc/docs`:
- Case-insensitive greps for `predict`, `reconcil`, `rewind`, `lag comp`, `snapshot`, `interpolat`: the only real hit is `NetworkTransformBase.cs` (interpolation). Other hits are non-features: the word "snapshot" in the `OnSerialize` doc comment (`NetworkBehaviour.cs:392`) and `doc/docs/guides/sync/custom-serialization.md:16` (meaning "full initial state"), "string interpolation" in `Assets/Tests/Weaver/BitAttributeTests.cs:181`, and binary prefab/FBX matches (`reconTank.fbx`, prefabs).
- No tick counters, input buffers, history buffers, or state snapshots in the runtime.
- `NetworkWorld`/`NetworkTime` give wall-clock time and RTT only (file 04).

## 4. Closest design precedents inside the repo

| Precedent | What to borrow | Where |
|---|---|---|
| **Notify sample** `SendChangesNotify` | Send "changes since last acked" over unreliable via `Send(msg, INotifyCallBack)`; pooled callback objects; track `lastReceivedCount` on `OnDelivered` | `Assets/Mirage/Samples~/Notify/NotfyExample.cs:17-120` |
| **NetworkTransformBase** | Per-object `OnSerialize/OnDeserialize` override with `SetDirtyBit`, plus owner `ServerRpc` for authoritative input | `NetworkTransformBase.cs:95-111, 220-239` |
| **ReadyCheck** | Owner-authored SyncVar (set `SyncFrom.Owner`, `To.Server`) with `invokeHookOnServer/Owner` | `Assets/Mirage/Components/ReadyCheck.cs:15-16` |
| **LobbyReady** | Global manager reacting to `World.onSpawn/onUnspawn`, sends `NetworkServer.SendToMany` with channel | `Assets/Mirage/Components/LobbyReady.cs:20-37` (spawn/unspawn hooks), `:55-80` (`SendToReady`) |
| **HostRendererVisibility** | Host-mode special-casing of per-object logic | `Visibility/HostRendererVisibility.cs` |
| **Tanks sample** | Client-authoritative movement via transform sync + `ServerRpc` for fire event + `ClientRpc` for FX (no prediction) | `Assets/Mirage/Samples~/Tanks/Scripts/Tank.cs:35-88` |
| **RequestServerMetrics** | Reading `NetworkServer.Metrics` frames for bandwidth/resend stats | `Components/DisplayMetrics/RequestServerMetrics.cs` |

## 5. Visibility components (quick facts)

- `NetworkProximityChecker`: brute-force distance check against each authenticated player's **character** position; rebuilds via `InvokeRepeating` every `VisibilityUpdateInterval` (1 s) (`NetworkProximityChecker.cs:28, 37-53, 76-100`).
- `NetworkMatchChecker`: groups by `Guid MatchId`; has a `[SyncVar] currentMatchDebug` string; rebuilds immediately on change (`NetworkMatchChecker.cs:16-134`; `[SyncVar]` at `:22-23`).
- `SceneVisibilityChecker`: same-scene visibility; `NetworkSceneChecker` (obsolete, polls scene in `Update`) (`SceneChecking/*`).
- `AlwaysNetworkVisibility`: observers = all authenticated players, even while loading scenes (`Visibility/AlwaysNetworkVisibility.cs`).
- All require the component to be on the **same GameObject** as the identity (`NetworkIdentity.cs:365-376`).

## 6. Implication

Everything SimpleCSP needs beyond "reliable low-rate transform/state" is missing and must be built: a tick clock, input/state history, resimulation, snapshot interpolation of remote objects, rewind for hit detection, an unreliable/acked state path. Mirage supplies the object model, authority, interest management, serialization, and (via custom messages / unreliable RPCs / Notify) the transport primitives; see file 07.
