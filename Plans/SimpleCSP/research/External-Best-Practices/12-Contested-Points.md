# 12 – Contested Points

Where reputable sources disagree or make different trade-offs. For each point: each side's argument, who does it, and what decides it for a **simple, per-object, server-authoritative 16–32 player FPS** where only the character controller and weapons are predicted.

---

## 1. Clock ownership

**(a) Server clock master, client runs ahead, steered by feedback (time dilation / tick shifts)**
- *Who*: Unity NfE (time scale 0.9–1.1, 2-tick slack, jump if > ~10 ticks off) ([NetworkTimeSystem.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/PredictionTicking/NetworkTimeSystem.cs)), lightyear, netfox, PurrNet, Fusion, FishNet, Rocket League (Sim Time Scaling, [support page](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971)), Overwatch [Secondary].
- *For*:
  - One global tick keys inputs, state history, snapshots and lag-comp history.
  - All players are simulated in phase on the server, which matters for player–player interactions.
  - The input buffer gives explicit jitter absorption and a natural speed-hack limit (one input per tick).
  - Lag compensation is a pure tick lookup.
- *Against*:
  - You need a feedback protocol (command age / buffer depth) and a controller with dilation limits, hard-resync rules and startup gating.
  - The input buffer adds slack ticks of latency (NfE: 2 ticks ≈ 33 ms at 60 Hz) to the server's view of the player.

**(b) Server consumes each client's command stream on arrival (per-player clock)**
- *Who*: Source (tickbase, ±60 ms correction window, `sv_maxusrcmdprocessticks` budget) ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)), UE CMC (timestamps) ([Epic doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine)), Quake 3 ([g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c)), Gaffer 2004 ([article](https://gafferongames.com/post/networked_physics_2004/)), Gambetta.
- *For*:
  - No server input buffer and no dilation protocol. Simple to explain.
  - Jitter becomes harmless phase shift. Shipped in the most-played FPS engines.
- *Against*:
  - Players are out of phase on the server.
  - Speed hacks need explicit time budgets (Source's comment admits 0.25 s of manipulation remains).
  - Lag compensation must estimate latency and sanity-check client ticks (Source's 200 ms check).
  - Per-object state is keyed by per-player time, not a global tick.

**What decides it**:
- (a) fits if the library wants a single tick across all networked objects (needed anyway for per-object snapshots and lag-comp history), or if predicted objects of different owners might interact.
- (b) is simpler if only the local player's character and weapons are predicted and nothing else needs the same timeline.
- A middle path seen in Source: a global server tick for the world, plus per-player command processing bounded by a budget and a ±window clock correction (which is a form of feedback).
- *For Mirage-scale simplicity*, (a) with a minimal controller works: dilation ±5–10%, hard snap past a few ticks, slack 1–2 ticks. It keeps one mental model ("everything is tick T").

## 2. Fixed tick vs variable dt

- *Fixed*: Gaffer ("fixed delta time … plus the ability to render at different framerates" via an accumulator; semi-fixed is not exactly reproducible) ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)), NfE full ticks, Fusion, FishNet, PurrNet, Quantum, lightyear, netfox.
- *Variable*:
  - UE CMC moves use frame `dt`, are combined and tolerance-checked ([Epic doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine)).
  - Quake 3 usercmd `msec`, with `pmove_fixed` as an option ([bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c)).
  - NfE *partial ticks* are a hybrid: variable `dt` for presentation-only steps that are always rolled back ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- *For variable*: input is applied at render rate with zero quantisation latency. No tick/frame aliasing.
- *Against variable*: replays aren't reproducible, so you must use tolerance-based reconcile. Frame-rate-dependent physics (Q3 jump folklore, *(unverified)*). Moves must be sent per frame or merged.
- **What decides it**: if you reconcile by replaying stored inputs (the standard Gaffer/Gambetta/NfE loop), use a fixed tick. Variable `dt` only makes sense with a UE-style "server checks a tolerance" design. **Fixed tick for this project.**

## 3. Always replay vs compare with tolerance

- *Always replay*: Quake 3 ("simulate all unacknowledged commands each time"), NfE (every snapshot), Quantum, lightyear `Always` ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c), [NfE intro](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [lightyear](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)).
  - *For*: no comparison logic (which must cover every field to be correct); constant, testable cost; exposes side-effect bugs immediately ("a good stress test").
  - *Against*: CPU every snapshot, and visual noise if float drift makes each replay move things by tiny amounts.
- *Compare then replay*: lightyear default `Check`, Gaffer 2004 threshold, Mirror, Q3's own TODO ("OPTIMIZE: don't re-simulate unless … differs").
  - *For*: saves CPU when predictions are right (the common case).
  - *Against*: a missed field in the compare causes silent divergence; tolerance tuning.
- *Server-side tolerance* (UE CMC `ServerCheckClientError`):
  - *For*: least bandwidth; the client replays only when told.
  - *Against*: the client sends its result (more upstream), and drift below the tolerance is never corrected.
- **What decides it**: state size × replay ticks × predicted objects. For one character + weapon (~50–100 B, ~6–15 ticks), always-replay is cheap. **Start with always-replay (reference behaviour), and add compare-with-tolerance as a switchable optimisation once tests prove equivalence.** If you compare, compare *everything* in the state struct, with exact equality for discrete fields.

## 4. Missing input: repeat vs pause vs drop

- *Repeat last input*: NfE ("apply the last received input"), Source (repeat last cmd for drops and stalls), Quantum (repeat for prediction), Rocket League [Secondary], Fusion docs (cache last good input), PurrNet (extrapolate with a cap) ([NfE intro](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp), [PurrNet input-handling](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)).
  - *For*: movement continues smoothly for others; usually right (players hold keys); the owner's prediction (which used the real input) usually agrees.
  - *Against*: repeats one-shots (jump, fire) unless stripped, so PurrNet strips edges. Gives a lag-switch user continued motion.
- *Pause* (don't simulate the player): Gaffer's deterministic lockstep, which must wait ([Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)).
  - *For*: never invents input.
  - *Against*: the player freezes for everyone, and lag switches become a defensive exploit; it also adds catch-up bursts.
- *Drop / neutral input*: Gaffer 2004's server ignores out-of-order/lost inputs ([article](https://gafferongames.com/post/networked_physics_2004/)).
  - *For*: simple; never repeats actions.
  - *Against*: a stop-and-go stutter on loss, and more owner corrections.
- **What decides it**: input redundancy makes true misses rare. When they happen in an FPS, **repeat continuous inputs (move, held buttons, aim) for a few ticks, strip one-shot edges, then fall back to neutral**. Late arrivals for already-simulated ticks are dropped.

## 5. How far to favour the shooter (max rewind)

- *Generous*: Source `sv_maxunlag` 1.0 s ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)); Mirror 600 ms ([LagCompensationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/LagCompensation/LagCompensationSettings.cs)). This is fair to high-ping shooters.
- *Restrictive*:
  - NfE advises 250–500 ms to avoid "being shot behind a wall" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)).
  - PurrNet defaults to 0.15 s, but it measures only the view offset ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)).
  - Fusion keeps 200 ms of hitbox history ([LagCompensationSettings](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_lag_compensation_settings.html)).
  - Overwatch favours defenders for some abilities [Secondary].
- **What decides it**: your player population's ping distribution and how much "shot behind cover" you'll accept. Note that limits are not comparable across systems; check whether the number includes RTT. For a regional 16–32p FPS: cap total rewind at roughly 200–300 ms (≈ RTT + buffers for ~150–200 ms ping players). Above that, the shooter must lead.

## 6. Smooth the local player or snap

- *Smooth*: Quake 3 (`cg_errordecay` 100 ms on view origin), Source (`cl_smoothtime` 0.1 s), Gaffer (adaptive exponential), FishNet/PurrNet/Mirror visual layers.
- *Snap (no owner smoothing)*: UE CMC's `SmoothCorrection` runs only for simulated proxies and listen-server remotes ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)). Mirror offers a "Fast" mode that is snappier but harsher ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)).
- *For smoothing*: hides small pops; in first person, a camera jump is very noticeable.
- *For snapping*: what you see is where you are (aiming accuracy), and there's less code.
- **What decides it**: correction frequency. If prediction is good, corrections are rare and either works. **Use a short (~100 ms) decay with a snap threshold**: the classic FPS approach. Smooth position only, never view angles.

## 7. Is delta compression needed?

- *Yes*: Quake 3 and Gaffer (an order-of-magnitude win, "One bit: Not changed!") ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)); PurrNet delta frames ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)); NfE baselines.
- *Not yet*: Mirror ships full snapshots every interval and plans delta compression later ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- *For*: big savings when most entities are unchanged, and it scales to large worlds.
- *Against*: needs per-client acked baselines, history per client, and more failure modes. The win is smaller when most entities move (players in an FPS).
- **What decides it**: entity count × change rate × send rate vs your bandwidth budget. The back-of-envelope in [08 §5](08-Bandwidth-Messaging-AntiCheat.md#5-is-delta-compression-needed-at-1632-players) gives ~170–340 kbit/s per client for 32 players at 30–60 Hz with quantisation and no delta compression. **Nice-to-have, not essential.** Do quantisation and in-packet repeat bits first.

## 8. Client-predicted hit markers

- *Predict cosmetics only; confirm damage feedback*: [The art of Hit Registration](https://danieljimenezmorales.github.io/2023-10-29-the-art-of-hit-registration/); Valorant waits for the server (RTT delay on hit effects) ([State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/)).
- *Predict hits too*: PurrNet runs lag-compensated queries in the predicted sim on the shooter ("predicted locally with no round trip"), corrected by verified frames ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)). Source predicts weapon effects ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)).
- *For prediction*: instant feedback feels better.
- *Against*: false positives ("blood but no damage" complaints, [Black Ops 6](https://www.pcgamesn.com/call-of-duty-black-ops-6/hit-registration)).
- **What decides it**: lag-comp accuracy (how often client and server disagree) and how competitive the game is. Default: cosmetic impacts predicted, hit marker/damage confirmed.

## 9. Input delay vs zero-delay prediction

- *Input delay*: Fusion 2.1 Input Delay ([blog](https://blog.photonengine.com/?p=7342)); NfE `ForcedInputLatencyTicks` ("considerable expense of increased input latency", but fewer mispredictions; can feel like character "weight") ([reduce-prediction-overhead.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/optimization/reduce-prediction-overhead.md)); Quantum ([mispredictions](https://doc.photonengine.com/quantum/v3/concepts-and-patterns/mispredictions-and-entity-views)).
- *Zero delay*: the default for FPS character control in Source, UE CMC, NfE and Overwatch.
- **What decides it**: genre. FPS aiming and movement need zero delay. Input delay suits fighting games and lockstep-like designs, or low-end hardware. **Keep 0 as the default; optionally expose it.**

## 10. Per-object rollback vs whole-world rollback

- *Per-object (selective)*: NfE default (only ghosts in the snapshot roll back); Mirror per-Rigidbody; FishNet per-object reconciles; Mirage's intended design.
- *Whole predicted set*: NfE `AlwaysRollbackAllPredictedGhosts` ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)); Quantum (entire sim); classic physics-scene resimulation (criticised as unscalable by [Mirror](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)).
- *For per-object*: cheap, simple and local. Fine when predicted objects don't interact.
- *Against per-object*: mispredicts when a rolled-back object interacts with one that isn't (NfE's detailed examples).
- **What decides it**: interaction between predicted objects. If only the local player's character and weapon are predicted (and maybe its own projectiles), **reconcile that owner's predicted objects together at the same tick**, and keep everything else interpolated.

## 11. Partial ticks vs render interpolation for high-fps displays

- *Partial ticks* (NfE): re-simulate a fractional tick each frame, so there is no added latency, at the cost of an extra code path and CPU ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- *Render one tick behind with interpolation* (Gaffer, lightyear, Fusion KCC default): simple and robust, adding ≤ 1 tick of visual latency ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/), [lightyear](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/visual_interpolation.md)).
- *Render prediction/extrapolation* (Fusion KCC option): low latency, but costs CPU and can mispredict.
- **What decides it**: tick rate. At 60 Hz the added latency is ≤ 16.7 ms on body position. The camera rotation is applied per frame anyway, so **render interpolation is enough**.

## 12. Predict spawns vs anticipate

- *Predict*: lightyear prespawn, NfE predicted spawns, Overwatch projectiles [Secondary], netfox responsive projectiles.
- *Anticipate/wait*: NGO action anticipation (grenades: "the safest approach would be a server authoritative grenade") ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).
- *Data not objects*: Fusion projectile buffers ([projectiles](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials)).
- **What decides it**: projectile speed and visibility. Fast projectiles: hitscan or data + cosmetic. Slow, visible projectiles (rockets): predict with deterministic IDs and a timeout cleanup, or anticipate with a throw animation. For a first version, prefer **data/hitscan + anticipation** over predicted network-object spawns.

## 13. Predict remote players vs interpolate

- *Interpolate*: Source, Valorant, Gambetta, NfE owner-predicted mode, Mirror. Standard for FPS.
- *Predict (with last input)*: Rocket League [Secondary], NfE "predicted" mode with replicated inputs ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)), Quantum.
- **What decides it**: predictability and physical interaction. Human FPS movement is erratic, and NfE notes players "trying to avoid being hit" are hard to predict ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)). **Interpolate remote players; predicting them is overkill and usually worse for an FPS.**

---

## Sources
All links are inline. Primary sources used most: [Gaffer on Games](https://gafferongames.com/), [Quake III code](https://github.com/id-Software/Quake-III-Arena), [Source SDK 2013 code](https://github.com/ValveSoftware/source-sdk-2013), [Unity NfE docs](https://github.com/needle-mirror/com.unity.netcode/tree/master/Documentation~), [Unity NGO docs](https://github.com/Unity-Technologies/com.unity.multiplayer.docs), [PurrNet docs](https://github.com/PurrNet/PurrDocs), [lightyear book](https://github.com/cBournhonesque/lightyear/tree/main/book), [Mirror docs](https://github.com/MirrorNetworking/MirrorDocs), [Epic CMC doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897), Photon Fusion/Quantum docs, Riot posts.
