# 09 – Testing and Debugging

How to see mispredictions, reproduce bad networks, detect desync, and keep a prediction system debuggable.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Simulate bad networks all the time, not at the end

**Practice: develop with latency, jitter and loss enabled by default, and test against a separate server process (not host mode).** [Consensus]

| Tool | What it does | Source |
|---|---|---|
| Unity Transport `SimulatorPipelineStage` | packet delay, jitter, drop % per driver. Suggested delay "20 for a good broadband connection, up to 200 for bad mobile", jitter "typically half of the delay or slightly less", loss "rarely … above 3". Put it last in the pipeline | [Transport pipelines](https://docs.unity3d.com/Packages/com.unity.transport@2.5/manual/pipelines-usage.html) |
| NfE PlayMode Tool | RTT delay, RTT jitter, packet drop %, packet fuzz; "a higher ping requires the client to perform more prediction ticks". Load presets from JSON on the command line. Thin clients for load | [playmode-tool.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/testing/playmode-tool.md) |
| Mirror `LatencySimulation` transport | wraps any transport: latency, loss %, scramble/reorder. "Packet loss / scramble over reliable manifests itself via latency" | [latency-simulaton-transport.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/transports/latency-simulaton-transport.md) |
| netfox network simulator | in-engine simulator (Godot) | [network-simulator.md](https://github.com/foxssake/netfox/blob/main/docs/netfox.extras/guides/network-simulator.md) |
| clumsy (Windows) | OS-level lag, drop, throttle, duplicate, out-of-order, tamper (WinDivert) | [jagt/clumsy](https://github.com/jagt/clumsy) |
| Linux `tc netem`, macOS Network Link Conditioner | OS-level delay/jitter/loss/reorder | *(unverified: no page fetched; standard OS tools)* |

**What profiles to test** [Consensus]
- NfE: "Testing at 50, 150, and 500 ms of latency is usually enough to cover most common network conditions" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)).
- Add jitter (±20–50 % of delay), 1–5 % loss, and **bursts** (Gaffer: "a burst of packet loss like 1/4 a second with no packets getting through … is inevitable") ([State Synchronization](https://gafferongames.com/post/state_synchronization/)).
- Add a long stall/resume (Mirror's "100s delay" mobile case) ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- PurrNet's failure-mode list: "Test as a remote client, not only as host … Latency, jitter, and packet loss; Ownership transfer and reconnect; Spawn/despawn during rollback; … Different render frame rates and tick rates; Large corrections, teleports, and late joiners" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
- **Different render frame rates** matter a lot (30 fps, 60, 144, uncapped, plus a deliberate hitch). Many CSP bugs come from frame/tick aliasing *(general; supported by PurrNet's list)*.
- **Asymmetric conditions**: loss on uplink only (input loss) vs downlink only (state loss) exercise different code (redundancy vs newest-wins) *(reasoning)*.

## 2. Make mispredictions visible

**Practice: count and log every correction (tick, magnitude, which fields differed), and have a debug overlay/console command that shows them live.** [Consensus by example]
- Quake 3 `cg_showmiss`: prints "Prediction miss: %f" with the distance, and "PredictionTeleport" ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)).
- Source `cl_showerror` "Show prediction errors, 2 for above plus detailed field deltas"; `cl_predictionlist` "Show which entities are predicting"; `cl_pdump` "Dump info about this entity to screen" ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)). Field-level deltas are the most useful variant: they tell you *which* variable diverged.
- NfE **Prediction Tracing Tool** records selected components "before and after each of the systems you select, in both the client world and the server world", then "compares the two recordings tick by tick and highlights every point where the client and the server disagree … you can identify the first system that introduced a divergence" ([prediction-tracing-tool.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/testing/prediction-tracing-tool.md)). *A simplified per-tick, per-field diff log is the low-tech version.*
- PurrNet: "Use the bandwidth and prediction profilers to verify history size, correction frequency, and per-tick state cost" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
- **Expected baseline**: with no packet loss and nothing external affecting the player, the local player should see **zero** corrections. Gaffer: "only when the player's character is affected by something external to the local player's input … will the player's position need to be corrected" ([What every programmer…](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)). Any steady trickle of small errors in that setup is a bug (non-shared state, float order, quantisation mismatch, `Time.deltaTime` use). [Consensus]

## 3. Visual debug

- **Draw the server ("ghost") state next to the predicted one.** Mirror's `PredictedRigidbody` shows the predicted physics object, the smoothed rendered object, and "The (transparent) remote state object: this is for debugging. It shows you the latest server state of the object" ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)). Gambetta's demo draws the last two received positions as grey outlines ([live demo](https://www.gabrielgambetta.com/client-side-prediction-live-demo.html)). [Consensus]
- **Draw lag-compensated hitboxes.** Source `sv_showlagcompensation` "Show lag compensated hitboxes whenever a player is lag compensated" ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)). Show client-side and server-side impact markers together (CS `sv_showimpacts` is well known *(unverified: not in SDK files fetched)*).
- **Timeline HUD**: current predicted tick, last acked server tick, server-buffer occupancy/command age, interpolation tick and delay, RTT, jitter, corrections/sec, replay ticks/frame. NfE exposes time offsets and scales in its Network Profiler ([time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md)).

## 4. Desync and determinism checks

- **Offline determinism test (Recommended)**: run the same input sequence twice from the same initial state, in-process, and compare state per tick. Then run client-code vs server-code builds (Mono vs IL2CPP, different machines) to see whether drift is from code divergence or floats. Gaffer's lockstep article shows how even "the smallest divergence" grows if the sim is not deterministic ([Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)). For CSP you need *near*-determinism over ~0.5 s, not bit-exactness. Gaffer: "I'm not talking about perfect floating point determinism here, just a reasonable 1/2 second prediction giving approximately the same result" ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)).
- **Replay-twice test (Recommended)**: on the client, after a reconcile, replay the same ticks again and assert identical results. This catches hidden state (statics, Unity component state not in the struct, `Time.*` use) cheaply *(technique suggested by lightyear's "Always rollback … good stress test" ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)))*.
- **Force-rollback stress mode (Recommended)**: a debug flag that rolls back N ticks every tick even with no error. lightyear: "if your game can't handle the CPU load of constant rollbacks, you'll find out fast … if … it plays a sound or spawns an entity as a side effect every time it runs, constant rollbacks will expose that immediately" ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)).
- **State hashing over the network (Nice-to-have)**: Quantum computes per-tick checksums and reports `DeterministicTickChecksumError` with per-client values ([Quantum API](https://doc-api.photonengine.com/en/quantum/current/class_photon_1_1_deterministic_1_1_deterministic_tick_checksum_error.html)). For state-sync CSP, comparing a hash of the owner's predicted state against the server's on each ack is a cheap desync alarm *(adaptation; not from a CSP source)*.
- **Shot agreement test**: NfE suggests counting hits on client and server with bots and "ensuring that those numbers remain similar and converge when shooting stops" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)). [Single-source; very practical for an FPS]

## 5. Logging that helps

**Practice: log per tick, in a compact machine-readable form, on both sides with the same tick key.** [Consensus by example: NfE tracing, Source cl_showerror detail]
- Client: `tick, inputHash/bits, predictedStateHash, (on reconcile) serverTick, diffFields, errorMagnitude, replayCount`.
- Server: `tick, clientId, inputTickConsumed, wasRepeated(missing), bufferDepth, stateHash`.
- Diff the two logs offline by `(clientId, tick)` to find the first divergent tick and field.
- NfE: the server should "never overwrite or change the input received by the client". Logging when the server *substitutes* input (repeat-last) is essential to explain corrections ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md)).

## 6. Automated tests (unit-level)

- Mirror built its snapshot interpolation as engine-independent code with heavy unit tests: "More than half the time was spent on tests & simulations to guarantee stability" ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)). Its prediction algorithm "comes with heavy test coverage" ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)).
- **Recommendation**: keep the reconcile loop, ring buffers, clock controller and interpolation buffer free of Unity dependencies, and test them with scripted packet timelines (loss, reorder, duplicate, burst, stall). [Single-source practice, strongly aligned with debuggability goals]

---

## Sources
- Unity – [Transport pipelines / SimulatorPipelineStage](https://docs.unity3d.com/Packages/com.unity.transport@2.5/manual/pipelines-usage.html)
- Unity NfE – [playmode-tool.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/testing/playmode-tool.md), [prediction-tracing-tool.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/testing/prediction-tracing-tool.md), [server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md), [time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md)
- Mirror – [latency-simulaton-transport.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/transports/latency-simulaton-transport.md), [snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md), [client-side-prediction.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)
- netfox – [network-simulator.md](https://github.com/foxssake/netfox/blob/main/docs/netfox.extras/guides/network-simulator.md)
- [clumsy](https://github.com/jagt/clumsy)
- Quake III Arena – [cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)
- Valve Source SDK 2013 – [prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp), [player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)
- PurrNet – [best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)
- lightyear – [prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)
- Glenn Fiedler – [State Synchronization](https://gafferongames.com/post/state_synchronization/), [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/), [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/), [What every programmer needs to know about game networking](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)
- Gabriel Gambetta – [Live demo](https://www.gabrielgambetta.com/client-side-prediction-live-demo.html)
- Photon Quantum – [DeterministicTickChecksumError](https://doc-api.photonengine.com/en/quantum/current/class_photon_1_1_deterministic_1_1_deterministic_tick_checksum_error.html)
