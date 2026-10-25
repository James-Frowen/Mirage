# External Best Practices – Client-Side Prediction (CSP)

An independent baseline of industry and Unity practice for **client-side prediction in a server-authoritative FPS with 16–32 players**. The predicted parts are the character controller and weapons (fire, ammo, reload, cooldowns), and the state is small. Simplicity and debuggability are the priority. It was built only from external sources (engine source code on GitHub, official docs, talk write-ups, library docs) and is meant to be compared later against the author's own notes.

## Index

| File | Topic |
|---|---|
| [01-Time-and-Ticks.md](01-Time-and-Ticks.md) | Fixed tick vs variable dt; clock ownership (server master + run-ahead vs per-player command stream); lead amount; startup; hitches; tick-rate choice |
| [02-Inputs.md](02-Inputs.md) | Input struct, sampling vs tick, redundancy, server input buffer, missing/late/early inputs, speed hacks, lag switches |
| [03-Reconciliation-and-Replay.md](03-Reconciliation-and-Replay.md) | The reconcile loop; always-replay vs compare; what state; re-baselining; replay limits; out-of-order/partial updates; side effects during replay |
| [04-Smoothing-and-Camera.md](04-Smoothing-and-Camera.md) | Visual vs sim root; error offset decay; render interpolation between ticks; camera rules |
| [05-Remote-Entities-Interpolation.md](05-Remote-Entities-Interpolation.md) | Snapshot interpolation; buffer size; adaptive delay; timeline; extrapolation limits; loss/jitter |
| [06-Lag-Compensation-and-Hit-Registration.md](06-Lag-Compensation-and-Hit-Registration.md) | Server rewind; what the client reports; max rewind; hitboxes; peeker's advantage; client hit feedback; hitscan vs projectiles |
| [07-Weapons-and-Predicted-Gameplay.md](07-Weapons-and-Predicted-Gameplay.md) | Predicting fire/ammo/reload/cooldowns; RNG; predicted spawns and mispredicts; platforms, pushing, player collisions |
| [08-Bandwidth-Messaging-AntiCheat.md](08-Bandwidth-Messaging-AntiCheat.md) | Send rates; reliable vs unreliable; batching; quantisation; delta compression at this scale; validation and anti-cheat |
| [09-Testing-and-Debugging.md](09-Testing-and-Debugging.md) | Network simulators; misprediction visibility; visual debug; determinism/desync checks; logging; unit tests |
| [10-Unity-Specific.md](10-Unity-Specific.md) | Tick loop vs FixedUpdate; `maximumDeltaTime`/`timeScale`; replay in one frame; CharacterController vs Rigidbody; `Physics.Simulate`/`SyncTransforms`/physics scenes; float determinism; GC; Animator; execution order and host mode; **how Unity libraries do it** |
| [11-Reference-Architectures.md](11-Reference-Architectures.md) | Quake 3, Source/CS:GO, CS2, Overwatch, Rocket League, Halo Reach, Valorant, UE CMC/NPP, Gaffer, Gambetta, Fusion, and others, plus a **comparison table** |
| [12-Contested-Points.md](12-Contested-Points.md) | Where sources disagree: clock ownership, fixed vs variable dt, always-replay vs tolerance, missing-input policy, max rewind, local smoothing, delta compression, hit markers, input delay, per-object rollback, partial ticks, predicted spawns, remote prediction |

**Conventions used throughout**
- **[Consensus]**: several independent primary sources agree.
- **[Single-source]**: one primary source or one engine only.
- **[Secondary]**: write-ups, forum or dev comments, search summaries.
- *(unverified)*: could not be backed with a fetched source.

**How sources were accessed**: GitHub-hosted source and docs (Quake III, Source SDK 2013, Gaffer on Games, Unity NfE package mirror, NGO docs, Mirror docs and code, FishNet code, PurrNet docs, lightyear, netfox, a copy of UE's CharacterMovementComponent) were read directly. Other sites (Valve wiki, gabrielgambetta.com, Unity manual, Photon docs, Riot blog, GDC Vault, Epic docs) could not be fetched directly from this environment, so their content comes via web-search summaries of those pages. Treat talk-based numbers (Overwatch, Rocket League, Halo Reach, CS2) as secondary.

---

## Ranked checklist (16–32 player server-authoritative FPS, predicted character + weapons)

Tags: **E** = Essential · **R** = Recommended · **N** = Nice-to-have · **O** = Overkill for a 16–32p FPS.

| # | Practice | Tag | Why (one line) | Details |
|---|---|---|---|---|
| 1 | Server-authoritative simulation; clients send *inputs*, never positions/hits. Aim angles are the accepted exception | **E** | Single source of truth and cheat resistance; prediction "fools only themselves" | [02 §1](02-Inputs.md#1-input--the-only-thing-the-client-is-authoritative-over), [08 §6](08-Bandwidth-Messaging-AntiCheat.md#6-what-to-trust-and-what-to-validate) |
| 2 | One shared `Tick(input, ref state, fixedDt)` for server tick, client predict and client replay; no `Time.*`, `Random`, `Input` inside | **E** | Replays must reproduce the original step | [01 §1](01-Time-and-Ticks.md#1-fixed-tick-vs-variable-dt-for-the-predicted-simulation), [02 §2](02-Inputs.md#2-input-sampling-vs-tick-rate), [10 §3](10-Unity-Specific.md#3-running-the-simulation-multiple-times-per-frame-replay) |
| 3 | Tick-indexed, allocation-free struct ring buffers for inputs and predicted states | **E** | Reconcile needs per-tick history; GC spikes cause hitches | [03 §1](03-Reconciliation-and-Replay.md#1-the-core-loop-universal), [10 §7](10-Unity-Specific.md#7-gc-and-allocation-struct-ring-buffers) |
| 4 | Inputs over unreliable channel with redundancy (≥ 3 previous ticks, or all unacked) | **E** | Loss is repaired with no RTT stall; inputs are tiny | [02 §3](02-Inputs.md#3-redundant-input-sending-and-packet-loss) |
| 5 | Choose one clock model explicitly: (a) server-tick master + client runs ahead (RTT/2 + 1–2 ticks) with buffer feedback, or (b) Source-style per-player command stream with budget | **E** | Everything (buffers, lag comp, anti-speedhack) depends on it | [01 §2–3](01-Time-and-Ticks.md#2-clock-and-tick-synchronisation--two-families), [12 §1](12-Contested-Points.md#1-clock-ownership) |
| 6 | Missing input: repeat continuous inputs for a few ticks, strip one-shot edges (jump/fire), then neutral. Drop late inputs | **E** | Smooth for observers; never invents actions; lag-switch resistant | [02 §5–6](02-Inputs.md#5-missing-input-policy-input-not-there-when-the-server-ticks), [12 §4](12-Contested-Points.md#4-missing-input-repeat-vs-pause-vs-drop) |
| 7 | Server-side input rate limit: about one input tick per server tick, bounded catch-up, clamp far-future/past ticks | **E** | Speed-hack and burst defence | [02 §7](02-Inputs.md#7-rate-limiting-speed-hack-defence-lag-switches-max-catch-up) |
| 8 | Reconcile loop: discard stale/out-of-order states → restore server state at T → replay T+1…now → overwrite history (re-baseline) | **E** | The core of CSP; skipping re-baseline causes double corrections | [03 §1, §4, §6](03-Reconciliation-and-Replay.md) |
| 9 | Predicted state contains *every* sim-affecting field (stance, grounded, ammo, `nextFireTick`, `reloadEndTick`, RNG counter); timers as ticks | **E** | Missing fields are the #1 cause of constant mispredictions | [03 §3](03-Reconciliation-and-Replay.md#3-what-state-to-store-and-compare), [07 §1](07-Weapons-and-Predicted-Gameplay.md#1-predict-the-weapon-as-part-of-the-players-tick) |
| 10 | Side effects (sounds, VFX, RPCs) run only on first prediction or from a view-layer state diff; never during replay | **E** | Replays would double-fire effects | [03 §7](03-Reconciliation-and-Replay.md#7-side-effects-during-replay-sounds-vfx-rpcs-events-fire-once) |
| 11 | Separate visual from sim root; error offset with ~100 ms decay, snap threshold, reset on teleport | **E** | Hides corrections without corrupting the sim | [04 §1–2](04-Smoothing-and-Camera.md#1-separate-the-visual-from-the-simulation-root) |
| 12 | Never roll back or correct view rotation; apply look every render frame; send absolute angles | **E** | Corrected aim feels broken; deltas drift under loss | [04 §4](04-Smoothing-and-Camera.md#4-camera-rules), [02 §1](02-Inputs.md#1-input--the-only-thing-the-client-is-authoritative-over) |
| 13 | Render interpolation between previous/current tick (alpha from accumulator) for predicted and host objects | **E** | Tick-rate stutter on high-Hz displays otherwise | [04 §3](04-Smoothing-and-Camera.md#3-render-interpolation-between-ticks) |
| 14 | Remote players: snapshot interpolation on the server-tick timeline, buffer ≈ 2 send intervals (+jitter), short capped extrapolation | **E** | Smooth others despite loss/jitter; defines what lag comp must reproduce | [05](05-Remote-Entities-Interpolation.md) |
| 15 | Server lag compensation for hitscan: per-tick hit-volume history, rewind to the shooter's reported view tick, clamped max rewind | **E** | Otherwise players must lead targets by RTT + buffer | [06 §1–3](06-Lag-Compensation-and-Hit-Registration.md#1-server-side-rewind-favor-the-shooter) |
| 16 | Server validation: sanitise inputs (NaN/range), enforce cooldown/ammo/alive/ownership in the authoritative tick | **E** | Predicted ≠ trusted | [08 §6–8](08-Bandwidth-Messaging-AntiCheat.md#6-what-to-trust-and-what-to-validate) |
| 17 | Clock never runs backwards; small error → dilation, large → hard resync; cap ticks per frame | **E** | Avoids spiral of death and time-travel bugs | [01 §5](01-Time-and-Ticks.md#5-recovering-from-hitches-and-large-desync) |
| 18 | Develop with simulated latency/jitter/loss on (50/150/500 ms, bursts), as a remote client, at several frame rates | **E** | Most CSP bugs are invisible on LAN or host | [09 §1](09-Testing-and-Debugging.md#1-simulate-bad-networks-all-the-time-not-at-the-end) |
| 19 | Count and log corrections with per-field diffs; expect **zero** in clean conditions | **E** | A trickle of small errors means hidden state or non-shared code | [09 §2, §5](09-Testing-and-Debugging.md#2-make-mispredictions-visible) |
| 20 | Clock steered by server buffer feedback ("command age"), dilation ±5–10% | **R** | Ping alone ignores jitter and server frame phase | [01 §2–3](01-Time-and-Ticks.md#3-how-far-ahead-the-client-runs) |
| 21 | Start with always-replay; add compare-with-tolerance (exact for discrete fields) as an optional optimisation | **R** | Simplest correct behaviour first; optimise later | [03 §2](03-Reconciliation-and-Replay.md#2-always-replay-vs-compare-then-replay-with-a-tolerance), [12 §3](12-Contested-Points.md#3-always-replay-vs-compare-with-tolerance) |
| 22 | Deterministic spread/recoil RNG seeded by tick or command number | **R** | Predicted spread matches the server | [07 §1](07-Weapons-and-Predicted-Gameplay.md#1-predict-the-weapon-as-part-of-the-players-tick) |
| 23 | Quantise predicted state identically on both sides (or send it at full precision) | **R** | Wire quantisation otherwise creates mispredictions | [03 §3](03-Reconciliation-and-Replay.md#3-what-state-to-store-and-compare) |
| 24 | Reconcile all of one owner's interacting predicted objects at the same tick | **R** | Avoids partial-rollback mispredictions in per-object designs | [03 §6](03-Reconciliation-and-Replay.md#6-corrections-arriving-out-of-order-lost-or-partial), [12 §10](12-Contested-Points.md#10-per-object-rollback-vs-whole-world-rollback) |
| 25 | Bound the replay window (e.g. ≤ 500 ms / 32–64 ticks) and snap beyond it | **R** | Bounded CPU after stalls | [03 §5](03-Reconciliation-and-Replay.md#5-replay-cost-and-limits) |
| 26 | Predict cosmetic impacts; confirm hit markers/damage from the server | **R** | Instant feel without false "hits" | [06 §6](06-Lag-Compensation-and-Hit-Registration.md#6-client-side-hit-prediction-hit-markers-blood-decals), [12 §8](12-Contested-Points.md#8-client-predicted-hit-markers) |
| 27 | Kinematic character (custom or `CharacterController` with disable/enable or `SyncTransforms` on restore); no dynamic Rigidbody in the predicted set | **R** | Replay-safe and cheap; PhysX resim is costly and non-deterministic | [10 §4–5](10-Unity-Specific.md#4-charactercontroller-vs-rigidbody-vs-kinematic-movement) |
| 28 | Own tick loop on unscaled time (not `FixedUpdate`); `SimulationMode.Script` if stepping physics; `Physics.SyncTransforms` after restores | **R** | `FixedUpdate` depends on `timeScale`/`maximumDeltaTime` and can't be re-run | [10 §1–2, §5](10-Unity-Specific.md#1-fixedupdate-vs-a-custom-tick-loop) |
| 29 | Moving platforms: player state stored relative to the platform; platforms evaluated as f(tick) | **R** | Interpolated platforms under a predicted player cause constant corrections | [07 §5](07-Weapons-and-Predicted-Gameplay.md#5-predicted-player-vs-non-predicted--remote-objects) |
| 30 | Adaptive interpolation buffer (catch-up/slow-down, jitter-based target) | **R** | Low delay on good links, smooth on bad ones | [05 §3](05-Remote-Entities-Interpolation.md#3-adaptive-delay) |
| 31 | Debug views: server ghost vs predicted, lag-comp hitboxes, timeline HUD; "replay twice" and "force rollback every tick" test modes | **R** | Makes divergence and side-effect bugs obvious | [09 §3–4](09-Testing-and-Debugging.md#3-visual-debug) |
| 32 | Unity-free core (ring buffers, reconcile, clock, interpolation) with unit tests over scripted packet timelines | **R** | Debuggable and testable without play mode | [09 §6](09-Testing-and-Debugging.md#6-automated-tests-unit-level) |
| 33 | Animator outside the prediction; gameplay timings from state ticks, not animation events | **R** | Animator can't be rewound | [10 §8](10-Unity-Specific.md#8-animation-and-animator-under-prediction) |
| 34 | Delta compression against acked baselines | **N** | ~170–340 kbit/s per client is fine at 32p with quantisation alone | [08 §5](08-Bandwidth-Messaging-AntiCheat.md#5-is-delta-compression-needed-at-1632-players), [12 §7](12-Contested-Points.md#7-is-delta-compression-needed) |
| 35 | Predicted projectile spawns with deterministic IDs and timeout cleanup (or projectiles-as-data) | **N** | Only needed for slow, visible projectiles | [07 §3](07-Weapons-and-Predicted-Gameplay.md#3-predicted-spawns-projectiles-and-what-to-do-on-mispredict) |
| 36 | Partial ticks / render prediction instead of render interpolation | **N** | Saves ≤ 1 tick of body latency at the cost of a second code path | [04 §3](04-Smoothing-and-Camera.md#3-render-interpolation-between-ticks), [12 §11](12-Contested-Points.md#11-partial-ticks-vs-render-interpolation-for-high-fps-displays) |
| 37 | Optional input-delay setting | **N** | Fewer mispredictions for low-end/high-ping, but worse feel | [12 §9](12-Contested-Points.md#9-input-delay-vs-zero-delay-prediction) |
| 38 | Hermite interpolation for remotes | **N** | Only visible at low send rates | [05 §6](05-Remote-Entities-Interpolation.md#6-packet-loss-and-jitter-handling) |
| 39 | Priority accumulator, interest management, visibility culling vs wallhacks | **N** | Everything fits in one MTU at 32p; anti-ESP is a later concern | [08 §3, §10](08-Bandwidth-Messaging-AntiCheat.md#3-per-object-messages-vs-batched-world-snapshots) |
| 40 | Networked checksums of predicted state | **N** | Cheap desync alarm, but logs usually suffice | [09 §4](09-Testing-and-Debugging.md#4-desync-and-determinism-checks) |
| 41 | Throttle reconciles when client FPS is low | **N** | Protects weak clients (FishNet) | [03 §5](03-Reconciliation-and-Replay.md#5-replay-cost-and-limits) |
| 42 | Bit-exact cross-platform determinism / fixed-point math / lockstep | **O** | CSP only needs near-determinism over ~0.5 s; the server corrects | [10 §6](10-Unity-Specific.md#6-float-determinism-limits-mono-vs-il2cpp-platforms) |
| 43 | Predicting remote players | **O** | Human FPS movement is erratic; interpolation is standard | [12 §13](12-Contested-Points.md#13-predict-remote-players-vs-interpolate) |
| 44 | Full-scene `Physics.Simulate` resimulation for character + weapons | **O** | Doesn't scale; not needed for kinematic movement | [03 §5](03-Reconciliation-and-Replay.md#5-replay-cost-and-limits), [10 §4](10-Unity-Specific.md#4-charactercontroller-vs-rigidbody-vs-kinematic-movement) |
| 45 | Sub-tick input timestamps (CS2), 128 Hz ticks (Valorant) | **O** | Competitive-esports refinements; 30–64 Hz suffices | [01 §6](01-Time-and-Ticks.md#6-tick-rate-choice), [11](11-Reference-Architectures.md) |
| 46 | Replay step batching (merging ticks during replay) | **O** | Trades accuracy for CPU; replay is already cheap here | [03 §5](03-Reconciliation-and-Replay.md#5-replay-cost-and-limits) |

### Most often skipped by simple implementations
Items **6** (strip one-shots on repeat), **7** (catch-up budget), **8** (re-baseline + stale-drop), **9** (all state, including weapon timers and RNG), **10** (replay side effects), **12** (camera rotation outside prediction), **13** (render interpolation), **15** (interp-time accounting in lag comp), **17** (hitch handling), **23** (quantisation mismatch) and **24** (partial rollback). See the per-topic files.

---

## Main sources

**Engine source code (read directly)**
- id Software – [Quake III Arena](https://github.com/id-Software/Quake-III-Arena) (`cg_predict.c`, `cl_input.c`, `g_active.c`, `bg_pmove.c`)
- Valve – [Source SDK 2013](https://github.com/ValveSoftware/source-sdk-2013) (`prediction.cpp`, `player.cpp`, `player_command.cpp`, `player_lagcompensation.cpp`, `usercmd.h`, `c_baseplayer.cpp`, `cdll_bounded_cvars.cpp`)
- Epic – CharacterMovementComponent ([public gist copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)) and [CMC networking doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine)

**Author articles**
- Glenn Fiedler – [Gaffer on Games](https://gafferongames.com/) (Fix Your Timestep, Networked Physics 2004, What every programmer…, Snapshot Interpolation/Compression, State Synchronization, Deterministic Lockstep, Floating Point Determinism)
- Gabriel Gambetta – [Fast-Paced Multiplayer I–IV](https://www.gabrielgambetta.com/client-server-game-architecture.html)

**Unity and Unity-ecosystem libraries**
- [Netcode for Entities 7.1 docs (package mirror)](https://github.com/needle-mirror/com.unity.netcode/tree/master/Documentation~) and runtime (`ClientServerTickRate.cs`, `NetworkTimeSystem.cs`)
- [Netcode for GameObjects docs](https://github.com/Unity-Technologies/com.unity.multiplayer.docs) (dealing-with-latency, client-anticipation, networktime-ticks)
- [Mirror docs](https://github.com/MirrorNetworking/MirrorDocs) and [Mirror code](https://github.com/MirrorNetworking/Mirror)
- [FishNet code](https://github.com/FirstGearGames/FishNet) and [docs](https://fish-networking.gitbook.io/docs)
- [PurrNet docs](https://github.com/PurrNet/PurrDocs)
- Photon [Fusion 2](https://doc.photonengine.com/fusion/current/) and [Quantum 3](https://doc.photonengine.com/quantum/current/) docs
- [coherence docs](https://docs.coherence.io/), [Normcore docs](https://docs.normcore.io/)
- Unity manual/API: Time, Physics.Simulate, autoSyncTransforms, PhysicsScene, Animator.Update, DefaultExecutionOrder (links in [10](10-Unity-Specific.md))

**Other open-source netcode**
- [lightyear book (Bevy)](https://github.com/cBournhonesque/lightyear/tree/main/book), [netfox docs (Godot)](https://github.com/foxssake/netfox/tree/main/docs)

**Talks and studio write-ups** (mostly via secondary summaries)
- Overwatch GDC 2017 ([GDC Vault](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)); Rocket League GDC 2018 ([slides](https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf), [support page](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971)); Halo Reach GDC 2011 ([recording](https://archive.org/details/youtube-h47zZrqjgLc)); Riot ([Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode), [State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/)); CS2 sub-tick ([Steam discussion](https://steamcommunity.com/app/730/discussions/0/3821922393612397353))
