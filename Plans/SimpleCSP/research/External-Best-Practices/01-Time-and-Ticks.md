# 01 – Time and Ticks

How the predicted simulation is clocked, how client and server agree on "which tick is it", how far ahead the client runs, and how to survive hitches.

Legend: **[Consensus]** = several independent primary sources agree. **[Single-source]** = one primary source (or one engine) only. *(unverified)* = could not back with a fetched source.

---

## 1. Fixed tick vs variable dt for the predicted simulation

**Practice: run the predicted simulation (character + weapons) on a fixed tick with a fixed `dt`, identical on client and server.** [Consensus]

- *What*: one `Tick(input, ref state, fixedDt)` function, called once per tick number, on server and on client (normal tick and replays alike).
- *Why*:
  - Replays must reproduce the original step. Gaffer: "semi-fixed" steps are "good enough in most cases but … not exactly the same due to the limited precision of floating point arithmetic"; the fix is a fixed `dt` with the renderer "producing" time and the simulation "consuming" it in `dt` steps ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)).
  - A tick number gives a shared, exact key for inputs, state history, acks and lag-compensation history. Netcode for Entities (NfE), Fusion, FishNet, PurrNet, Quantum, lightyear and netfox are all tick-based ([NfE intro to prediction](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [PurrNet flow](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md), [lightyear time sync](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/bevy_integration/client/time_sync.md)).
  - NfE goes further: when catching up it refuses to batch two ticks together if the input changed between them, "to help preserve determinism" ([NfE intro](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- *When it doesn't apply / counter-examples*:
  - **Quake 3** steps player movement by each usercmd's own `msec` (variable), with an optional `pmove_fixed` mode (`pmove_msec` default 8, clamped 8–33) that quantises command times ([g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c), [bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c)). That it was made optional suggests variable `msec` caused problems. The well-known frame-rate-dependent jump height in Q3 is *(unverified)*.
  - **Unreal CharacterMovementComponent (CMC)** uses each client frame's delta time per "move", combines similar moves, and the server derives `dt` from client timestamps ([UE CMC networking doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine)). This works because CMC *compares end positions with a tolerance* instead of expecting bit-exact replays. Epic's newer Network Prediction plugin offers both fixed and "independent" ticking, and forum users report independent ticking as fragile ([FFixedTickState](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Plugins/NetworkPrediction/FFixedTickState), [UE forum](https://forums.unrealengine.com/t/mover-network-prediction-fixed-ticking-policy-without-fixed-frame-rate/2693047)).
  - See [12-Contested-Points](12-Contested-Points.md#2-fixed-tick-vs-variable-dt) for the trade-off.

**Practice: keep tick rate and network send rate as separate settings; the send rate should divide the tick rate.** [Consensus]
- NfE: `SimulationTickRate` and `NetworkTickRate` (default 60/60) may differ, but the network rate must be lower than and a factor of the simulation rate (for example 60/30). The server can spread sends across ticks to different client subsets ([NfE intro](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)).
- Quake 3 runs the world at `sv_fps` 20 but executes client movement per usercmd as it arrives ([sv_init.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/server/sv_init.c)).

**Practice: decouple rendering from ticks with interpolation, not with variable-dt prediction** (details in [04](04-Smoothing-and-Camera.md#3-render-interpolation-between-ticks)).
- NfE is the notable exception: it runs *partial ticks* on the client (a rolled-back sim step with variable `dt` each render frame), then discards them at the next full tick ([NfE intro, "Partial ticks"](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)). It is effective but adds a whole second code path. For a simple system, render interpolation of the last two full ticks is simpler (lightyear and the Fusion KCC default to it).

---

## 2. Clock and tick synchronisation – two families

### (a) Server is the clock master; the client runs *ahead* and is steered by feedback

The server ticks at a fixed rate. The client estimates the server tick and deliberately runs ahead of it, so that the input for tick *N* reaches the server just before the server simulates *N*. Every input is stamped with its target tick.

| Source | How the client is steered |
|---|---|
| **NfE** | Server measures how long each command waited in its buffer ("command age") and sends it back. The client adjusts its `deltaTime` scale within **0.9–1.1** (`PredictionTimeScaleMin/Max`), correcting ~10% of the error per frame. If the estimate is off by more than ~10 ticks it jumps instead (and may roll back). Target slack is **2 ticks** (`TargetCommandSlack`). ([time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [NetworkTimeSystem.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/PredictionTicking/NetworkTimeSystem.cs), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)) |
| **lightyear** | A `LocalTimelineSync` controller "either shifts the tick by whole ticks or speeds/slows the simulation"; whole-tick shifts emit an event so that input buffers and prediction history shift together ([time_sync.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/bevy_integration/client/time_sync.md)) |
| **netfox** | NTP-style sampling of offset/RTT/jitter adjusts a *reference clock*. A separate *simulation clock* follows it and "is guaranteed to only move forwards". A large offset triggers a "panic" reset ([network-time-synchronizer.md](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-time-synchronizer.md)) |
| **PurrNet** | "If the client's lead over the server drifts out of range, the manager re-centers the local tick. Corrections only move the tick forward or briefly pause it; the timeline never rewinds." ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)) |
| **Rocket League** | Official support page: the server keeps an input buffer per client, and "Sim Time Scaling" lets "the game server … instruct your client to subtly speed up or slow down to maintain a smooth input stream" ([Epic support](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971)) |
| **Overwatch** | Secondary sources only: fixed command frames, client ahead by about ½ RTT + one command frame, and the server signals the client to simulate faster when its input buffer starves ([Edgegap write-up](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback), [GameDev.net thread](https://gamedev.net/forums/topic/696756-command-frames-and-tick-synchronization/)). Primary: [GDC 2017 talk](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and) (not transcribed here) |
| **Fusion, FishNet** | Tick-based client/server with timing adjustment by the TimeManager/Runner ([FishNet TimeManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs), [Fusion Simulation API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_simulation.html)) |

*Why*: the server simulates every player in lock-step on one timeline, so lag-compensation history, world snapshots and server game logic all share one tick. Small adjustments (dilation) are invisible, while large ones (snaps) are reserved for real desync.

### (b) The server consumes each client's command stream (per-player clock)

The server does not hold inputs until a global tick. It runs each player's movement *when that player's commands arrive*, using the client's own time/tick base. The world ticks independently.

| Source | Details |
|---|---|
| **Source engine** | Each player has an `m_nTickBase`. Commands are executed as they arrive, up to a per-player budget, and the tick base is only corrected if the result drifts outside **±`sv_clockcorrection_msecs` (60 ms)** of the server tick count. The budget comment: it "prevents users from manipulating the game time by more than 0.25s" ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)). |
| **Unreal CMC** | Each `ServerMove` carries a client timestamp. The server derives the move's `dt` from the timestamp delta and simulates immediately. Optional "time discrepancy detection/resolution" (off by default) pays back client time that runs ahead of the server ([UE CMC doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [UE forum: client speed hack](https://forums.unrealengine.com/t/client-speed-hack/359179)) |
| **Quake 3** | Each usercmd carries `serverTime`. The server clamps it to `[level.time − 1000, level.time + 200]` ms "to prevent speedup cheating" and runs `Pmove` per command on arrival ([g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c)) |
| **Gaffer (2004)** | Each input RPC carries a time. The server ignores inputs older than its current time, and "each character object has its physics advanced ahead in time individually as input rpcs are received … the physics state of different client characters are slightly out of phase on the server" ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)) |

*Why*: there is no server input buffer, so it adds no latency and needs no dilation protocol. Jitter is absorbed because the server simply runs commands late.
*Costs*: players are out of phase on the server (bad for player-vs-player physics), you need explicit speed-hack budgets, and lag compensation must add measured latency to the client's interpolation time (see [06](06-Lag-Compensation-and-Hit-Registration.md)).

**What decides it**: see [12-Contested-Points §1](12-Contested-Points.md#1-clock-ownership). For a tick-based Unity library with per-object prediction, (a) fits better because every object's state is keyed by one global tick. (b) is the simpler choice if only the local player is predicted and you are willing to give up a global tick for player movement.

---

## 3. How far ahead the client runs

**Practice: target "server tick at arrival + small safety slack", measured from feedback, not from a raw RTT formula alone.** [Consensus within family (a)]

- NfE's timelines ([interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)):
  - input/prediction tick = `T + RTT/2 + TargetCommandSlack` (slack default 2 ticks);
  - interpolated (remote) tick = `T − (RTT/2 + InterpolationTimeNetTicks)`.
  - So the gap between what you predict and what you see of others is roughly `RTT + slack + interpolation buffer`. This matters for lag compensation.
- Cap the lead: NfE's `MaxPredictAheadTimeMS` (default **500 ms**) clamps RTT for this purpose. Above it, "the client will begin to incur input latency" instead of predicting further, which bounds replay cost ([ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs), [NetworkTimeSystem.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/PredictionTicking/NetworkTimeSystem.cs)).
- The best feedback signal is **how long inputs waited in the server buffer** (NfE "command age"; Rocket League input-buffer size). Ping alone ignores jitter and the server's own frame phase.
- *Optional*: **input delay** (deliberately running fewer ticks ahead and applying local input a few ticks late) trades responsiveness for fewer mispredictions and less replay CPU. Fusion 2.1 added it, NfE has `ForcedInputLatencyTicks`, and Quantum recommends it ([Fusion 2.1 release](https://blog.photonengine.com/?p=7342), [NfE reduce-prediction-overhead](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/optimization/reduce-prediction-overhead.md), [Quantum mispredictions](https://doc.photonengine.com/quantum/v3/concepts-and-patterns/mispredictions-and-entity-views)). For an FPS character controller, the default should be 0. NfE warns the cost is "considerable … input latency".

---

## 4. Startup / join synchronisation

- **Start from an estimate, then only make small corrections.** NfE builds an initial estimate from RTT and the latest snapshot, then changes "the time progress rather than doing large changes to the current time" ([time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md)). [Consensus]
- **Gate prediction until the clock is synced.** lightyear skips systems holding `SyncedLocalTimeline` until the timeline is synced, and notes that "entities frozen for the first second" usually mean "timeline not synced yet" ([time_sync.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/bevy_integration/client/time_sync.md)). [Single-source, but cheap and obviously useful]
- **First state must be a full baseline.** PurrNet sends full frames on join/resync and deltas afterwards ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)). Gaffer's delta compression also needs an explicit "relative to initial state" path for the first RTT ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)).
- *Simple approach for Mirage-like systems*: server sends `(serverTick, tickRate)` at spawn. Client sets `predictedTick = serverTick + ceil((RTT/2)/dt) + slack` and does not send inputs or predict until it has at least one RTT sample *(derived from the above; not a single cited recipe)*.

## 5. Recovering from hitches and large desync

- **Never let the predicted clock run backwards; prefer pause or jump forward.** netfox keeps the simulation clock monotonic, PurrNet only re-centers forward or pauses, and NfE ensures "neither the interpolation tick nor the prediction tick goes back in time" ([netfox](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-time-synchronizer.md), [PurrNet flow](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md), [NfE](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md)). [Consensus]
- **Small error → time dilation; large error → hard resync.** NfE uses about ±10 ticks as the boundary, netfox has a "panic" reset, and lightyear shifts whole ticks. [Consensus]
- **Cap ticks per frame (avoid the "spiral of death").** Gaffer: either keep headroom or "clamp at a maximum # of steps per-frame and the simulation will appear to slow down" ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/); his sample clamps frame time to 0.25 s). NfE server defaults are `MaxSimulationStepsPerFrame = 1` and `MaxSimulationStepBatchSize = 4` (batching loses accuracy). FishNet's TimeManager can drop ticks above a per-frame maximum "to prevent the client falling further behind" ([ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs), [FishNet TimeManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs)). [Consensus]
- **After a long client stall (alt-tab, loading), treat it as a re-join**: hard-resync the tick, clear input/state history and snap. Do not replay hundreds of ticks *(unverified as a cited rule; follows from the replay caps in [03](03-Reconciliation-and-Replay.md#5-replay-cost-and-limits))*.

## 6. Tick-rate choice

| Game / library | Rate | Source |
|---|---|---|
| NfE default | 60 Hz sim, 60 Hz net | [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs) |
| Valorant | 128 Hz | [Riot tech blog](https://technology.riotgames.com/news/peeking-valorants-netcode) |
| CS2 | 64 Hz + sub-tick input timestamps | [Steam discussion quoting Valve](https://steamcommunity.com/app/730/discussions/0/3821922393612397353), [Dexerto](https://www.dexerto.com/counter-strike-2/counter-strike-2-sub-tick-updates-explained-2094004/) (secondary) |
| Rocket League | 120 Hz physics | [GDC 2018 slides](https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf) (via search summary) |
| Overwatch | 16 ms command frames (~62.5 Hz) | secondary: [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback) |
| Quake 3 | `sv_fps` 20 world; movement per usercmd | [sv_init.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/server/sv_init.c) |

- *Guidance for a 16–32 player FPS*: **30–64 Hz**. 60 Hz is a common default (NfE). Replay cost grows linearly with tick rate × RTT, so a 100 ms RTT replay is about 6–8 ticks at 60 Hz and 13+ at 128 Hz. NfE: "Lowering `NetworkTickRate` can reduce the cost of using client prediction, although at the cost of increased latency" ([NfE intro](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- High tick rates mainly help peeker's advantage and hit-registration granularity. An IEEE ToG simulation study (via search summary) found client frame rate mattered more than server tick rate for peeker's advantage ([IEEE ToG spotlight](https://transactions.games/spotlight/august-2026-spotlight-paper)) [Single-source].

---

## Sources
- Glenn Fiedler – [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/), [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/), [Snapshot Compression](https://gafferongames.com/post/snapshot_compression/) (read from [gafferongames repo](https://github.com/gafferongames/gafferongames))
- Unity Netcode for Entities 7.1 – [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md), [reduce-prediction-overhead.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/optimization/reduce-prediction-overhead.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs), [NetworkTimeSystem.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/PredictionTicking/NetworkTimeSystem.cs)
- Valve Source SDK 2013 – [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)
- id Software Quake III Arena – [g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c), [bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c), [sv_init.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/server/sv_init.c)
- Epic – [Understanding Networked Movement in the CMC](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [FFixedTickState](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Plugins/NetworkPrediction/FFixedTickState), [Client speed hack thread](https://forums.unrealengine.com/t/client-speed-hack/359179), [Mover/NPP ticking thread](https://forums.unrealengine.com/t/mover-network-prediction-fixed-ticking-policy-without-fixed-frame-rate/2693047)
- lightyear book – [time_sync.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/bevy_integration/client/time_sync.md)
- netfox – [network-time-synchronizer.md](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-time-synchronizer.md)
- PurrNet – [flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)
- FishNet – [TimeManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Timing/TimeManager.cs)
- Photon – [Fusion Simulation API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_simulation.html), [Fusion 2.1 release](https://blog.photonengine.com/?p=7342), [Quantum mispredictions](https://doc.photonengine.com/quantum/v3/concepts-and-patterns/mispredictions-and-entity-views)
- Rocket League – [Input buffering support page](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971), [GDC 2018 slides](https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf)
- Overwatch – [GDC Vault](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and), secondary [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback), [GameDev.net](https://gamedev.net/forums/topic/696756-command-frames-and-tick-synchronization/)
- Riot – [Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode)
- CS2 – [Steam discussion](https://steamcommunity.com/app/730/discussions/0/3821922393612397353), [Dexerto](https://www.dexerto.com/counter-strike-2/counter-strike-2-sub-tick-updates-explained-2094004/)
- [IEEE Transactions on Games spotlight](https://transactions.games/spotlight/august-2026-spotlight-paper)
