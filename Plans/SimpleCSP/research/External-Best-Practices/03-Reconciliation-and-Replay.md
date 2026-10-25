# 03 – Reconciliation and Replay

What the client does when an authoritative state for an old tick arrives: compare, restore, replay. Also covers what to store and how to keep replays cheap and free of side effects.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. The core loop (universal)

**Practice:** keep a ring buffer of `(tick, input, predictedStateAfterTick)` on the client. When server state for tick *T* arrives:
1. drop history ≤ *T* that is no longer needed,
2. (optionally) compare server state with the predicted state at *T*,
3. restore server state as the state at *T*,
4. re-run `Tick()` with the stored inputs *T+1 … now*, overwriting the stored predicted states,
5. hand the visual layer the difference between the old and new present state (see [04](04-Smoothing-and-Camera.md)). [Consensus]

- Gaffer: keep "a circular buffer of past character state and input … discards any buffered state older than the corrected state … and replays … In effect the client invisibly 'rewinds and replays' the last n frames of local player character movement while holding the rest of the world fixed" ([What every programmer needs to know](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)).
- Gambetta: inputs carry sequence numbers, and the server reply includes "the sequence number of the last input it processed" ([Part II](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html)).
- Also Quake 3 ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)), Unreal CMC `SavedMoves` ([UE CMC doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine)), NfE ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)) and lightyear ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)).

```csharp
// Illustrative per-object reconcile
void OnServerState(uint serverTick, in State s) {
    if (serverTick <= lastAppliedServerTick) return;          // stale / out of order
    lastAppliedServerTick = serverTick;
    var predicted = history[serverTick].State;
    if (Matches(predicted, s)) return;                         // optional (§2)
    Vector3 oldVisualPos = current.Position;
    current = s;
    for (uint t = serverTick + 1; t <= localTick; t++) {
        current = Tick(history[t].Input, current, Dt, isReplay: true);
        history[t].State = current;                            // re-baseline (§3)
    }
    visual.AddError(oldVisualPos - current.Position);         // see 04
}
```

---

## 2. Always replay vs compare-then-replay with a tolerance

| Approach | Who | Notes |
|---|---|---|
| **Always replay** whenever a server state arrives | Quake 3 ("we simulate all unacknowledged commands each time, not just the new ones"; its comment lists "don't re-simulate unless the newly arrived snapshot … differs" as an *OPTIMIZE* TODO) ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)); NfE (rollback "happens every time the client receives a snapshot … regardless of whether there's a discrepancy") ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)); Quantum (deterministic, re-predicts from verified frames) | Simplest and most robust: no comparison code, and no missed "equal but not really" cases. CPU cost every snapshot. |
| **Compare, replay only on mismatch** | lightyear default `RollbackMode::Check` ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)); Gaffer 2004: "If the two physics states differ above some threshold then the client rewinds" ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)); Mirror prediction compares history with server state ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)) | Saves CPU in the common no-error case. Needs a correct comparison over *all* state (§3) plus a tolerance. |
| **Server compares, client replays only when told** | Unreal CMC: client sends its resulting location; server checks `ServerExceedsAllowablePositionError` (location diff, or movement-mode mismatch) and sends `ClientAdjustPosition` only then. Otherwise it acks "good moves", throttled to one per **0.1 s** (`NetworkMinTimeBetweenClientAckGoodMoves`) ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)) | Least downstream bandwidth and no client compare cost. But the server trusts nothing yet still must send corrections, and the client never learns about small drift until it exceeds the server tolerance. |

- lightyear's notes on `Always`: it "skips the check cost entirely (for states it also means no `PredictionHistory` needs storing)", and it is "a good stress test: if your game can't handle the CPU load of constant rollbacks, you'll find out fast" ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)).
- **Recommendation for a small, debuggable system**: implement *always replay* first. It is the reference behaviour, and for a character + weapon state of a few dozen bytes and about 10 ticks of replay it is cheap. Add *compare with tolerance* later as an optimisation flag that must produce identical visuals. Keep a debug switch to force always-replay. See [12 §3](12-Contested-Points.md#3-always-replay-vs-compare-with-tolerance).

**Tolerance values seen in practice**
- Quake 3 counts an error only if `> 0.1` units ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)).
- Gaffer 2004 example: ignore < 10 cm, smooth 10 cm–2 m, snap > 2 m ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)).
- UE CMC: tolerance via `AGameNetworkManager::ExceedsAllowablePositionError`, with a "large correction" flag above `NetworkLargeClientCorrectionDistance` = 15 units ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)). The default `MAXPOSITIONERRORSQUARED` value is *(unverified)*.
- **Discrete state** (ammo, reload phase, weapon id, grounded flag, movement mode) must compare *exactly*. UE treats a movement-mode mismatch as a correction by itself ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)).

---

## 3. What state to store and compare

**Practice: the predicted state struct must contain *every* value that can influence a later tick, and nothing else.** [Consensus]
- PurrNet: "State should include every value that can change a later simulation result: position, velocity, cooldowns, counters, targets, random state, active modes, and stable predicted object IDs." Also: "Inspector fields are configuration, not mutable simulation state" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
- lightyear's rule: predict a component if it "cannot be calculated using the predicted components available within the current tick" *and* "may be modified after creation". Derived values don't need predicting ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)).
- Gaffer state sync: send velocities too, because the simulation extrapolates from them ([State Synchronization](https://gafferongames.com/post/state_synchronization/)).
- For an FPS character + weapon, typically:
  - movement: position, velocity, grounded/movement mode, crouch/stance, jump/landing timers, ground-entity id (for platforms);
  - weapon: current weapon id, ammo in clip, reserve, next-fire tick, reload-end tick, burst counter, spread/recoil accumulator, RNG seed or counter.
- **Store timers as tick numbers** ("next fire at tick N") rather than float countdowns. It is exact, replay-safe and cheap to compare *(recommendation; consistent with PurrNet "read time … from the predicted world")*.

**Practice: quantise state on *both* sides, or don't quantise predicted fields.** [Consensus]
- NfE: replicated values are quantised but resimulated intermediate ticks are not, so "enabling quantization worsens indeterminism". Fix: increase precision, disable quantization, or "quantize values yourself at the end of your tick (even on the server)" ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)).
- Gaffer: "quantize the state on both sides"; predicted state needed 4096 positions/m instead of 512 for visual-only data ([State Synchronization](https://gafferongames.com/post/state_synchronization/)).

---

## 4. Re-baselining

**Practice: after replay, overwrite the stored predicted states for *T+1 … now* with the replayed results.** Future comparisons are then against the corrected timeline, not the stale one. [Consensus: Gaffer, PurrNet `SaveStateInHistory` during replay ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)), Mirror's history correction ([Prediction.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/Prediction/Prediction.cs))]
- If you skip this, the next server state (T+1) is compared against the *old* misprediction and you correct twice *(reasoning)*.
- Mirror takes a different route. It stores per-entry *deltas* and, on correction, inserts the server state and recomputes later entries by re-applying the deltas instead of re-simulating. That fits its no-`Physics.Simulate` design, where it explicitly "sacrifices accuracy for performance" ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md), [Prediction.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/Prediction/Prediction.cs)). [Single-source]

---

## 5. Replay cost and limits

**Practice: bound the replay window and size the history buffers to it. Beyond the bound, hard-snap instead of replaying.** [Consensus]

| Source | Limit |
|---|---|
| NfE | Rollback clamped to the input queue size (const **64**); `MaxPredictAheadTimeMS` default 500 ms ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)) |
| lightyear | `max_rollback_ticks` default **20** ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)) |
| Quantum | `RollbackWindow` default **60** frames ([DeterministicSessionConfig](https://doc-api.photonengine.com/en/quantum/current/class_photon_1_1_deterministic_1_1_deterministic_session_config.html)) |
| UE CMC | `MaxSavedMoveCount` **96** ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)) |
| Quake 3 | `CMD_BACKUP` ring; prints "exceeded PACKET_BACKUP on commands" when exceeded ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)) |

**Ways to cut cost** (in order of simplicity):
1. Keep the predicted sim small: character controller + weapon only, no full-scene physics step. Mirror's doc explains why re-running `Physics.Simulate()` for a whole scene per correction "doesn't scale" ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)). NfE: "Client prediction can be a very CPU intensive way of managing latency" ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
2. Compare-then-replay (§2).
3. Throttle reconciles when the client is struggling. FishNet: "When frame rate drops below [50 by default] reconciles will be reduced to roughly 4-5 times a second" ([PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs)). [Single-source]
4. Lower tick rate, or input delay ([01 §3](01-Time-and-Ticks.md#3-how-far-ahead-the-client-runs)).
5. Batch replay ticks (NfE `MaxPredictionStepBatchSize…`, "at the cost of simulation accuracy"). *Overkill / risky for a simple system.*

---

## 6. Corrections arriving out of order, lost, or partial

- **Discard stale states.** PurrNet: "Frames at or below the verified tick are stale and get discarded" ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)). Gaffer: unreliable updates are applied only if newer ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)). [Consensus]
- **Send state unreliably, newest-wins.** Losing one correction just means the next one fixes it. Mirror warns against sending "only if changed" without knowing what the other side received ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)). [Consensus]
- **Per-object state vs world snapshots: the partial-rollback hazard** (important for a *per-network-object* design). NfE documents that with partial snapshots "only the ghosts that are included in the snapshot are rolled back", so a rolled-back object interacting with a non-rolled-back one mispredicts. Its fix, `AlwaysRollbackAllPredictedGhosts`, rolls every predicted object back to the same tick, at CPU and memory cost ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)). [Single-source, but well explained]
  - *Implication*: if the local player's predicted objects (character, weapon, own grenades) interact, reconcile them **as a group at one tick**. For example, have the server send their states in the same message with the same tick, or replay all predicted objects of that client together. If they never interact, per-object reconcile is fine.
- **Predicted spawns have no server state yet**, so they are not rolled back with others. NfE offers rolling them back "to spawn tick" ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)). See [07](07-Weapons-and-Predicted-Gameplay.md#3-predicted-spawns-projectiles-and-what-to-do-on-mispredict).

---

## 7. Side effects during replay (sounds, VFX, RPCs, events fire once)

**Practice: the simulation function may run many times for the same tick. Anything irreversible must be gated so it runs only the *first* time a tick is predicted (or only when it's verified), and must never run during replay.** [Consensus: every engine/library has this flag]

| Source | Flag / mechanism |
|---|---|
| Source | `IsFirstTimePredicted()`, plus `IPredictionSystem::SuppressEvents(!IsFirstTimePredicted())` around each predicted command. Client-created (predicted) entities are simulated "only … on new usercmds" ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)) |
| NfE | `NetworkTime.IsFirstTimeFullyPredictingTick`: "Useful to implement actions that should be executed only once" ([time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md)) |
| Fusion | `Runner.IsForward` / `Runner.IsResimulation` / `IsFirstTick` ([NetworkRunner API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_network_runner.html)); effects "should play only on forward ticks" ([community guide](https://ulrikdamm.github.io/photon-fusion-guide), via search) |
| FishNet | `ReplicateState` flags `Ticked`, `Replayed`, `Created`; `PredictionManager.IsReconciling` ([ReplicateState.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Object/Prediction/ReplicateState.cs), [PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs)) |
| PurrNet | `Simulate` "can execute more than once for the same tick … must not directly perform irreversible work such as: Playing audio or particles; … Sending ordinary RPCs on every replay". Use `PredictedEvent`, `isVerifiedView`, or view callbacks ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)) |
| Quantum | `Frame.IsVerified` / `IsPredicted`; `Update()` may run several times for one frame number ([Quantum FAQ](https://doc.photonengine.com/quantum/current/getting-started/faq), [Frames](https://doc.photonengine.com/quantum/current/manual/frames)) |
| netfox | `_rollback_tick(delta, tick, is_fresh)`; caveat: with input delay ≥ latency, `is_fresh` may never be true ([network-rollback.md](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-rollback.md)) |

**Patterns that work**
1. **Gate by "first time predicted"** for local feedback (muzzle flash, fire sound, recoil kick, shell eject). It's cheap and gives zero-latency feedback. Downside: if the replay changes the outcome (the shot didn't happen), the effect already played. That is acceptable for cosmetic one-shots.
2. **Event-from-state-diff**: effects are emitted by the *view* layer when it sees a state transition (for example `ammo` decreased or `reloadEndTick` set) in the presented state, rather than from inside `Tick()`. Replays then cannot double-fire, and mispredicted events can be cancelled. This is close to PurrNet's view callbacks and the NGO "anticipation" split of visual vs authoritative values ([client-anticipation.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/advanced-topics/client-anticipation.md)).
3. **Never send RPCs from predicted code.** Predicted *requests* go through the input struct (fire button); outcomes come from the server. PurrNet: "Keep authoritative rewards and persistence on the server, make them idempotent where possible" ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).
4. **Server never sees "replay"**. On the server every tick is the first time, so effects that should be seen by *others* (third-person fire anim/sound) come from replicated state or server events, with the owner excluded *(common practice; see [07](07-Weapons-and-Predicted-Gameplay.md))*.

---

## Sources
- Glenn Fiedler – [What every programmer needs to know about game networking](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/), [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/), [State Synchronization](https://gafferongames.com/post/state_synchronization/)
- Gabriel Gambetta – [Part II: Client-Side Prediction and Server Reconciliation](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html)
- Quake III Arena – [cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)
- Valve Source SDK 2013 – [prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)
- Epic – [CMC networking doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [CMC code copy (gist)](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)
- Unity NfE – [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md), [time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)
- Unity NGO docs – [client-anticipation.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/advanced-topics/client-anticipation.md)
- lightyear – [prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)
- PurrNet – [best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md), [flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md), [security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)
- FishNet – [PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs), [ReplicateState.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Object/Prediction/ReplicateState.cs)
- Mirror – [client-side-prediction.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md), [snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md), [Prediction.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/Prediction/Prediction.cs)
- Photon – [Fusion NetworkRunner API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_network_runner.html), [Big Photon Fusion Guide (community)](https://ulrikdamm.github.io/photon-fusion-guide), [Quantum FAQ](https://doc.photonengine.com/quantum/current/getting-started/faq), [Quantum Frames](https://doc.photonengine.com/quantum/current/manual/frames), [DeterministicSessionConfig](https://doc-api.photonengine.com/en/quantum/current/class_photon_1_1_deterministic_1_1_deterministic_session_config.html)
- netfox – [network-rollback.md](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-rollback.md)
