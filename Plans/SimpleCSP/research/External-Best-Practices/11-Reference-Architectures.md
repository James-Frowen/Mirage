# 11 – Reference Architectures

Short summaries of how well-known games, engines and libraries do CSP, followed by a comparison table. Talk-based entries (Overwatch, Rocket League, Halo Reach, CS2) rely on secondary write-ups where the primary slides or video could not be fetched. Those are marked.

Legend: **[Primary]** = read from source code/official docs. **[Secondary]** = write-ups, dev comments, search summaries. *(unverified)* = not backed by a fetched source.

---

## Quake 3 (id Software, 1999) [Primary: source code]
- Client sends usercmds (with `serverTime`), repeating the previous `cl_packetdup` (default 1) packets' commands. Up to `cl_maxpackets` (default 30) packets/s ([cl_input.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_input.c), [cl_main.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_main.c)).
- Server runs `Pmove` per usercmd on arrival (per-player time), clamps `serverTime` to [−1000, +200] ms, caps `msec` at 200. Optional `pmove_fixed` quantises steps ([g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c), [bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c)). World at `sv_fps` 20.
- Client prediction "simulate[s] all unacknowledged commands each time" on top of the latest snapshot `playerState`. Errors > 0.1 units decay linearly over `cg_errordecay` (100 ms), applied to the view origin. Teleports clear the error. Movers are compensated ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c), [cg_view.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_view.c)).
- Snapshots are delta-compressed against acked baselines *(well known; not re-read here, unverified)*.
- No server-side lag compensation in vanilla Q3 *(unverified; the code read shows no rewind)*.

## Source engine / CS:GO (Valve) [Primary: SDK 2013 code; wiki not fetchable]
- Usercmd: command number, `tick_count`, absolute view angles, move, buttons, weapon select, `random_seed` (from `MD5_PseudoRandom(command_number)`) ([usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h), [in_main.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/in_main.cpp)).
- Server: per-player `m_nTickBase`. Executes commands as they arrive within a budget (`sv_maxusrcmdprocessticks` 24), corrects the tick base if beyond ±60 ms. Uses backup cmds for lost packets and repeats the last cmd when missing ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)).
- Client prediction with `IsFirstTimePredicted()` and event suppression on re-prediction ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)). Error smoothing `cl_smoothtime` 0.1 s ([c_baseplayer.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_baseplayer.cpp)).
- Remote entities interpolated: `max(cl_interp 0.1, cl_interp_ratio 2 / cl_updaterate)` ([cdll_bounded_cvars.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/cdll_bounded_cvars.cpp)).
- Lag compensation: rewind other players to `cmd->tick_count − lerp`, sanity-checked against latency (> 200 ms → use latency), clamped to `sv_maxunlag` (1.0 s), teleport guard 64 units ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)).
- Canonical write-ups: [Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking), [Latency Compensating Methods (Bernier 2001)](https://developer.valvesoftware.com/wiki/Latency_Compensating_Methods_in_Client/Server_In-game_Protocol_Design_and_Optimization), [Prediction](https://developer.valvesoftware.com/wiki/Prediction) (not fetched; facts above are from code).

## CS2 "sub-tick" (Valve, 2023) [Secondary]
- 64 Hz server ticks, but inputs carry sub-tick timestamps, so the server knows "the exact instant motion starts, a shot fires, or a grenade is thrown" ([Steam discussion quoting Valve](https://steamcommunity.com/app/730/discussions/0/3821922393612397353); [Dexerto](https://www.dexerto.com/counter-strike-2/counter-strike-2-sub-tick-updates-explained-2094004/); [Edgegap](https://edgegap.com/blog/game-server-tick-rate-explained-gameplay-precision-vs-infrastructure-cost)). Player reception is mixed. Details of interpolation/lag comp are *(unverified)*. **Overkill** for a simple system.

## Overwatch (Blizzard, GDC 2017 – Tim Ford) [Secondary]
- ECS architecture; fixed command frames (16 ms; a 7 ms figure is also quoted). Client runs ahead by about ½ RTT + 1 frame. The server keeps a small input buffer and tells the client to speed up or slow down (time dilation). Redundant unacked inputs. Predicts abilities/projectiles by default. Server-side rewind generally favours the shooter, with defender-favouring exceptions for some abilities ([GDC Vault](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and), [YouTube](https://www.youtube.com/watch?v=8QHHVpBiG-I); secondary: [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback), [GameDev.net](https://gamedev.net/forums/topic/696756-command-frames-and-tick-synchronization/), [PCGamesN](https://www.pcgamesn.com/overwatch/cute-stickers-and-little-cups-help-explain-the-overwatch-netcode)). Exact numbers *(unverified)*.

## Rocket League (Psyonix, GDC 2018 – Jared Cone) [Secondary + official support page]
- Bullet physics at a fixed 120 Hz ([slides PDF](https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf), via search; [GDC Vault](https://www.gdcvault.com/play/1024972/It-IS-Rocket-Science-The), [YouTube](https://www.youtube.com/watch?v=ueEmiDM94IE)).
- Server keeps a per-client input buffer. "Sim Time Scaling" tells the client to subtly speed up or slow down to keep it flowing ([official support](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971)). The buffer adapts to jitter; up to 10 frames of input history per packet ([dev comments](https://devtrackers.gg/rocket-league/p/c2e0e1bc-netcode-lag-explained-with-lots-of-examples-rocket-science-16)).
- Predicts all cars (including remote, using last input) and the ball, replaying on correction *(secondary/unverified)*. Shows the "predict everything" end of the spectrum, which suits physics-car games and not FPS characters.

## Halo: Reach (Bungie, GDC 2011 – David Aldridge, "I Shot You First") [Secondary, thin]
- Host-authoritative (one player's console hosts), building on the Tribes networking model. Designers adjusted timings with networking in mind ([recording](https://archive.org/details/youtube-h47zZrqjgLc); secondary [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-halo-reach-netcode-host-migration)). Details of prediction, prioritisation and grenades could not be fetched *(unverified)*. The talk is often cited for treating "who shot first" as a design and priority problem, not only a netcode one *(unverified)*.

## Valorant (Riot) [Primary blog via search + dev posts]
- 128 Hz servers, server-authoritative, client predicts own movement. Remote players buffered by one 128 Hz frame (7.8125 ms) by default ("Network Buffering" setting). Riot calls buffering "effectively … extra network latency" and reasons explicitly about peeker's advantage ([Riot tech blog](https://technology.riotgames.com/news/peeking-valorants-netcode), [dev post](https://devtrackers.gg/valorant/p/e1133afd-peekers-advantage-strafe-shooting), [04: Peeker's Advantage](https://playvalorant.com/en-us/news/game-updates/04-on-peeker-s-advantage-ranked)).
- Shots: the server rewinds to the client's timestamp and evaluates. The client waits for the result, so hit effects lag tracers by one RTT ([State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/)).

## Unreal Engine CharacterMovementComponent [Primary: Epic docs + engine code copy]
- Client performs a move with its frame `dt`, saves it in `SavedMoves` (combining similar moves), and sends `ServerMove` (timestamp, accel, end location, flags, packed view, movement base). It sends the oldest unacked move alongside the newest ("ServerMoveOld"/"Dual") ([Epic doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [UE forum](https://forums.unrealengine.com/t/character-movement-component-networking-clarifications/151222)).
- Server replays the move with a `dt` from the timestamps. It checks the error (location tolerance or movement-mode mismatch) and sends `ClientAdjustPosition` only on mismatch (throttled 0.1 s, 0.05 s for large corrections), otherwise acks good moves. The client then replays the remaining saved moves ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)).
- Simulated proxies smoothed (Linear/Exponential, 0.1 s; teleport > 384) ([ENetworkSmoothingMode](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/ENetworkSmoothingMode)). Optional time-discrepancy (speed-hack) detection ([UE forum](https://forums.unrealengine.com/t/client-speed-hack/359179)).

## Unreal Network Prediction plugin / Mover [Secondary]
- Generic prediction framework with fixed-tick or independent-tick modes and built-in rollback. "FixedTick mode will accumulate real time and run 0-N Sim frames per engine frame" ([FFixedTickState](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Plugins/NetworkPrediction/FFixedTickState)). Forum reports suggest the Chaos networked-physics backend is now favoured ([UE forum](https://forums.unrealengine.com/t/should-we-be-using-the-network-prediction-plugin-or-netphysics-predicted-physics/2535018)) *(unverified)*.

## Glenn Fiedler / Gaffer on Games [Primary]
- FPS model: input-driven character, state struct, unreliable time-stamped input stream, server advances each character as its inputs arrive, client move ring buffer with rewind-and-replay above a threshold, then smoothing ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/), [What every programmer…](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)).
- Supporting articles: fixed timestep + render interpolation ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)); snapshot interpolation buffer sizing ([Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)); quantisation and delta compression ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)); jitter buffer, quantise-both-sides, adaptive error smoothing ([State Synchronization](https://gafferongames.com/post/state_synchronization/)); float determinism limits ([Floating Point Determinism](https://gafferongames.com/post/floating_point_determinism/)).

## Gabriel Gambetta – Fast-Paced Multiplayer [Primary via search summaries]
- Part I: authoritative server, clients send inputs. Part II: client-side prediction with input sequence numbers, the server reports the last processed input, the client re-applies the rest. Part III: entity interpolation, rendering others in the past. Part IV: lag compensation with the client sending timestamp + aim and the server rewinding ([I](https://www.gabrielgambetta.com/client-server-game-architecture.html), [II](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html), [III](https://www.gabrielgambetta.com/entity-interpolation.html), [IV](https://www.gabrielgambetta.com/lag-compensation.html), [demo](https://www.gabrielgambetta.com/client-side-prediction-live-demo.html)). The simplest complete mental model. It uses per-input sequence numbers rather than a global tick.

## Photon Fusion 2 [Primary docs via search]
- Tick-based; Server/Host mode uses "client-side prediction and server reconciliation (rollback and resimulation loop)", while Shared mode doesn't ([Fusion Starter](https://doc.photonengine.com/fusion/current/game-samples/fusion-starter)). `IsForward`/`IsResimulation` flags ([API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_network_runner.html)). Missing inputs: `GetInput` false; cache the last good one ([player input](https://doc.photonengine.com/fusion/current/manual/data-transfer/player-input)). Hitbox lag compensation with 200 ms default history ([lag comp](https://doc.photonengine.com/fusion/current/manual/advanced/lag-compensation)). KCC render modes ([render behaviour](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior)). Projectiles as data buffers ([projectiles](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials)). 2.1 adds Input Delay and Forecast Physics ([blog](https://blog.photonengine.com/?p=7342)).

## Others used in this research
- **Unity NfE, NGO, FishNet, Mirror, PurrNet, coherence, Normcore, Quantum** – see [10 §10](10-Unity-Specific.md#10-how-unity-netcode-libraries-do-prediction).
- **lightyear (Bevy)** – tick timeline steered by speed/slow or whole-tick shifts; rollback policy Check/Always; `max_rollback_ticks` 20; input redundancy; prespawn matching by hash; frame interpolation one tick behind ([book](https://github.com/cBournhonesque/lightyear/tree/main/book/src/concepts)).
- **netfox (Godot)** – NTP-style clock with a monotonic simulation clock; rollback with `is_fresh`; input redundancy formula; responsive projectiles with server acceptance ([docs](https://github.com/foxssake/netfox/tree/main/docs)).
- **Tribes (1998)** – move/ghost/event managers; the root of Torque and (reportedly) Halo's model ([SnapNet part 4](http://www.snapnet.dev/blog/netcode-architectures-part-4-tribes/), [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-tribes-(1998))) [Secondary].

---

## Comparison table

Abbreviations: **CA** = server clock master, client runs ahead (dilation/feedback). **CS** = server consumes the client's command stream on arrival (per-player clock). **AR** = always replay on server state. **CR** = compare, then replay on mismatch. **SC** = server compares and sends corrections only on error.

| System | Tick model | Clock ownership | Input transport | Reconcile style | Local smoothing | Remote interp delay | Lag comp |
|---|---|---|---|---|---|---|---|
| Quake 3 | variable msec per usercmd (opt. fixed 8 ms); world 20 Hz | CS (`serverTime`, clamped) | unreliable, previous N packets duplicated (`cl_packetdup` 1) | AR (all unacked cmds) | error decay 100 ms (view origin) | snapshot interp (default *(unverified)*) | none in vanilla *(unverified)* |
| Source / CS:GO | fixed tick (e.g. 64) | CS (per-player tickbase, ±60 ms correction, cmd budget) | unreliable usercmds + backups | AR *(engine prediction; unverified detail)* with error smoothing | `cl_smoothtime` 0.1 s | max(0.1 s, 2/updaterate) | rewind to tick_count − lerp, ≤ `sv_maxunlag` |
| CS2 | 64 Hz + sub-tick timestamps | *(unverified)* | *(unverified)* | *(unverified)* | *(unverified)* | *(unverified)* | sub-tick-accurate shot time [Secondary] |
| Overwatch | fixed 16 ms command frames [Secondary] | CA (time dilation) [Secondary] | unreliable, redundant unacked [Secondary] | rollback/replay [Secondary] | *(unverified)* | adaptive [Secondary] | favour shooter + exceptions [Secondary] |
| Rocket League | fixed 120 Hz physics | CA (Sim Time Scaling) | unreliable, ≤ 10 frames history [Secondary] | replay whole physics *(unverified)* | *(unverified)* | predicts remotes instead [Secondary] | n/a (physics game) |
| Halo: Reach | *(unverified)* | host authority | *(unverified)* | *(unverified)* | *(unverified)* | *(unverified)* | *(unverified)* |
| Valorant | 128 Hz | CA-like (server plays inputs "at a delay but … synchronized") [Secondary] | *(unverified)* | client prediction (details unverified) | *(unverified)* | 1 frame (7.8 ms) default buffer | server rewinds to client timestamp |
| UE CMC | variable dt per move (combined) | CS (timestamps; optional discrepancy detection) | unreliable RPC, new + oldest unacked move | SC (tolerance), then client replays saved moves | not for owner; simulated proxies 0.1 s | proxies smoothed, not buffered interp | game-specific (none in CMC) |
| UE Network Prediction | fixed or independent | engine-managed | engine-managed | rollback | *(unverified)* | *(unverified)* | *(unverified)* |
| Gaffer (2004 FPS) | per-input time | CS (time-stamped inputs) | unreliable stream | CR (threshold) + smoothing | exp. smoothing, snap > 2 m | n/a | n/a |
| Gambetta | per input (seq numbers) | CS (process inputs on arrival) | per-input messages | AR (re-apply unacked) | none described | renders between last two received positions (demo); exact delay *(unverified)* | timestamp + aim, server rewinds |
| Photon Fusion 2 | fixed tick | CA | tick inputs, `OnInput`/`GetInput` | AR (resimulate from latest authority tick) | render interpolation; KCC options | interpolation (proxies) | hitboxes, 200 ms default |
| Photon Quantum | fixed tick, deterministic | server-relayed input clock | input to relay; all clients sim all | AR on verified input (rollback window 60) | view smoothing | n/a (everything predicted) | n/a |
| Unity NfE | fixed 60 Hz (+ partial ticks) | CA (time scale 0.9–1.1, slack 2) | unreliable, current + 4 previous, delta-coded | AR (every snapshot); opt. rollback-all | `GhostPredictionSmoothing` | 2 net ticks, adaptive | physics history + interp delay; 250–500 ms advice |
| Unity NGO | tick | server time | RPCs | no CSP (anticipation only) | `Smooth` on anticipated values | NetworkTransform interp | none built in |
| FishNet v4 | fixed tick (`TimeManager`) | CA (timing adjust) | unreliable replicates, redundancy 3, server queue 15 | reconcile from server states (local states option) | graphical-object smoothing | state interpolation 2 ticks | RollbackManager (Pro) |
| Mirror | send interval (no global sim tick) | server `NetworkTime` (double) | Commands (user) | experimental PredictedRigidbody (delta correction, no `Physics.Simulate`) | ghost/smooth mode | 2 × send interval, dynamic, catch-up 2%/slow 4% | bounds history 6 × 100 ms, client hit-point verified |
| PurrNet | fixed tick | CA (lead re-centred forward/pause) | unreliable window 4–32 ticks, repeat bits | rollback to verified tick + replay | view error accumulation, snap thresholds | view buffer ≤ 0.1 s | view-offset rewind ≤ 0.15 s default |
| lightyear (Bevy) | fixed tick | CA (speed/slow or whole-tick shift) | unreliable with redundancy | CR (default) or AR | frame interpolation (1 tick behind) | interpolation timeline | *(not covered)* |
| netfox (Godot) | fixed tick | NTP-style sync to host | redundancy *r* | rollback | tick interpolator | *(not covered)* | *(not covered)* |
| Normcore | 20 Hz sync | owner-authoritative | n/a | none (no CSP) | n/a | interpolation | none |

---

## Sources
All links are inline above. Key primary sources:
- Quake III code ([repo](https://github.com/id-Software/Quake-III-Arena)); Source SDK 2013 code ([repo](https://github.com/ValveSoftware/source-sdk-2013)); Gaffer on Games ([site](https://gafferongames.com/), [repo](https://github.com/gafferongames/gafferongames)); Gambetta ([series](https://www.gabrielgambetta.com/client-server-game-architecture.html))
- Unity NfE docs ([mirror](https://github.com/needle-mirror/com.unity.netcode/tree/master/Documentation~)); NGO docs ([repo](https://github.com/Unity-Technologies/com.unity.multiplayer.docs)); Mirror docs ([repo](https://github.com/MirrorNetworking/MirrorDocs)); FishNet ([repo](https://github.com/FirstGearGames/FishNet), [docs](https://fish-networking.gitbook.io/docs)); PurrNet docs ([repo](https://github.com/PurrNet/PurrDocs)); lightyear ([book](https://github.com/cBournhonesque/lightyear/tree/main/book)); netfox ([docs](https://github.com/foxssake/netfox/tree/main/docs))
- Epic CMC ([doc](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-networked-movement-in-the-character-movement-component-for-unreal-engine), [code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897))
- Photon Fusion/Quantum docs (links inline)
- Riot ([Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode), [State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/))
- GDC talks (Overwatch, Rocket League, Halo Reach) and their secondary write-ups (links inline)
