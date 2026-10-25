# 08 – Bandwidth, Messaging, Server Authority and Anti-Cheat

Send rates, reliable vs unreliable, delta compression (needed or not at 16–32 players), per-object vs batched snapshots, and what the server must validate.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## Part A – Bandwidth and messaging

### 1. Send rates
- **Inputs**: one packet per client tick, carrying the redundant window ([02 §3](02-Inputs.md#3-redundant-input-sending-and-packet-loss)).
  - NfE sends commands once per full tick, "typically one packet per `NetworkTime.ServerTick`" ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md)).
  - Q3 caps at `cl_maxpackets` (default 30, 15–125) ([cl_input.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_input.c)).
  - UE CMC throttles move sends (`ClientNetSendMoveDeltaTime`, clamped 1/120–1/5 s) and combines similar moves ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)).
  - PurrNet resends the current input window every 20 ms ([input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)).
- **State/snapshots**: equal to the tick rate or an integer fraction of it. NfE default 60/60; half or a third "is often good enough" (tooltip in [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)). NfE also spreads per-client sends across ticks to flatten server CPU ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)). [Consensus]
- **Higher send rate means a smaller interpolation buffer** ([05 §2](05-Remote-Entities-Interpolation.md#2-buffer-delay)) and fewer ticks between reconciles. This is usually worth more than squeezing bytes.

### 2. Unreliable vs reliable
**Practice:** [Consensus]

| Data | Channel | Why |
|---|---|---|
| Inputs | **Unreliable + redundancy** | Retransmit adds ≥ RTT. Gaffer: "receive the most recent input and state without waiting for lost packets to be resent" ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/), [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)) |
| Owner's predicted-state corrections | **Unreliable, newest wins** | The next one supersedes a lost one ([03 §6](03-Reconciliation-and-Replay.md#6-corrections-arriving-out-of-order-lost-or-partial)) |
| Remote entity snapshots | **Unreliable**, sent every interval | Gaffer, Mirror `NetworkTransform` ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)) |
| Spawn/despawn, ownership, damage/kill events, chat, scoreboard | **Reliable** (ordered where needed) | Must not be lost. Tribes separated a *move manager* (fast), a *ghost manager* (guarantees the *most recent* state arrives) and an *event manager* (tracks reliable events) (secondary summaries: [Edgegap Tribes](https://edgegap.com/blog/game-backend-deep-dive-tribes-(1998)), [SnapNet part 4](http://www.snapnet.dev/blog/netcode-architectures-part-4-tribes/)) |

- **Prefer carrying gameplay-relevant "events" inside state** (for example `shotsFiredCount`, `lastFireTick`, `reloadEndTick`) instead of separate reliable RPCs. They then share the snapshot's tick and survive loss. Reliable RPCs arrive on a different timeline from unreliable state and can be applied out of tick order *(reasoning; consistent with Tribes' ghost-manager "most recent state" idea)*.
- PurrNet escalates under sustained heavy loss: the server "temporarily promotes that client to reliable full frames … until the client catches up" ([flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)). [Single-source; nice-to-have]

### 3. Per-object messages vs batched world snapshots
- **Batch all of one tick's updates for a client into one packet/message, with one tick/timestamp header.** [Consensus]
  - Mirror batches every message until end of frame (~1200-byte MTU batches) and puts "an 8 byte `double` precision `timestamp` in every Batch" instead of per message ([timestamp-batching.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/timestamp-batching.md)).
  - NfE sends one snapshot per connection per network tick ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- **Consistency matters more than packaging**: if objects that interact are updated at different ticks, you get NfE's partial-rollback mispredictions. Its mitigations are "rollback all", `GhostGroup` (send together), or prioritisation ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)). For a per-object design, make sure **a client's own predicted objects are always sent together, stamped with the same tick**.
- **At 16–32 players everything usually fits in one MTU per tick** (see §5 estimate), so priority/interest management is optional. If it doesn't fit, Gaffer's **priority accumulator** distributes updates fairly ([State Synchronization](https://gafferongames.com/post/state_synchronization/)). [Single-source technique; nice-to-have]

### 4. Quantisation
- **Quantise positions, angles and velocities** (Gaffer: positions at 512/m for visuals, smallest-three quaternions at 9 bits/component, velocity 11 bits/component) ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)).
- **For predicted state, either quantise identically on both sides at the end of every tick, or send predicted fields at full precision.** See NfE's quantization-vs-determinism warning ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)) and Gaffer's 4096/m for simulated state ([State Synchronization](https://gafferongames.com/post/state_synchronization/)). [Consensus]
- **Input structs**: keep them small and stable. PurrNet: "a field that changes every tick defeats the repeat-bit compression" ([input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)).

### 5. Is delta compression needed at 16–32 players?

**Back-of-envelope (own calculation, not from a source):**
- Remote player snapshot ≈ id (1–2 B) + position (3 × 16-bit quantised = 6 B) + yaw/pitch (2–4 B) + stance/flags/weapon (2–3 B) + health/misc (1–2 B) ≈ **~14–20 B**.
- 31 remotes × 20 B ≈ 620 B per snapshot, plus own predicted state ~40 B, plus headers ~30 B ≈ **~700 B**, which fits one MTU.
  - at 30 Hz: ~21 KB/s ≈ **~170 kbit/s** down per client;
  - at 60 Hz: **~340 kbit/s**.
- Server upstream for 32 clients at 60 Hz ≈ 32 × 340 ≈ **~11 Mbit/s**.
- Client upstream: 4 redundant inputs × ~14 B × 60 Hz + UDP/IP 28 B × 60 ≈ **~40 kbit/s**.

**Conclusion**: at this scale, *quantisation + "unchanged" bits* gets most of the benefit. Full ack-based delta compression is **Nice-to-have**, not essential. Supporting evidence:
- Mirror ships without delta compression, sending full `NetworkTransform` snapshots every interval. Its docs say bandwidth "will be significantly reduced once we implement Bitpacking and Delta Compression" ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- Delta compression gives large wins mainly when most objects are unchanged (Gaffer's 901 cubes reduced to ~15 kbit/s at rest) ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)). In an FPS, the 31 remote players are mostly *moving*.
- Delta compression requires acks of received baselines. Gaffer: "it is of course essential that the sender only encodes snapshots relative to baselines that the other side has received" ([Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)). PurrNet and Quake 3 do it ([PurrNet flow](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)). That adds code and failure modes, a cost against the "simple and debuggable" goal.
- **Cheap middle ground**: per-field "same as last *sent*" bits are unsafe without acks (Mirror warns against "send only if changed"). Per-field "same as previous *tick* in this packet" (input repeat bits, as in PurrNet and NfE command deltas) is safe because the baseline is in the same packet.
- See [12 §7](12-Contested-Points.md#7-is-delta-compression-needed).

---

## Part B – Server authority and anti-cheat

### 6. What to trust and what to validate

| Client sends | Trust? | Validation (sources) |
|---|---|---|
| Move axes, buttons | Trusted as *intent* | Clamp ranges, reject NaN (PurrNet `SanitizeInput` ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md))) |
| View angles | Effectively trusted (aim) | Validity and range check (Source `IsEntityQAngleReasonable` ([player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp))). Aimbot detection is out of scope for netcode; NGO: "how do you differentiate an aim bot vs someone flicking their mouse super fast" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)) |
| Input tick / timestamp | Bounded | One input per server tick; clamp future/past; catch-up budget (Source `sv_maxusrcmdprocessticks`, Q3 clamps, FishNet queue cap, UE time discrepancy) ([02 §7](02-Inputs.md#7-rate-limiting-speed-hack-defence-lag-switches-max-catch-up)) |
| View/interp tick for lag comp | Bounded | Clamp to max rewind; sanity-check against measured latency (Source 200 ms check; PurrNet clamp) ([06 §2–3](06-Lag-Compensation-and-Hit-Registration.md#2-what-ticktime-the-client-reports-and-interpolation-accounting)) |
| Position / hit results | **Never** (in server-auth CSP) | Server simulates movement and traces shots itself. If you do accept client hit claims (Mirror style), validate against history with a tolerance ([lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)) |
| Fire/reload/switch requests | As input bits | Server enforces cooldown, ammo, alive, ownership: "a sanitized fire button still needs a server-side cooldown, ammo check, and ownership check" ([PurrNet best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)) |

### 7. Movement validation
- **Server-authoritative CSP is movement validation**: the server runs the same movement code on the same inputs. Cheating a position only "fool[s] themselves locally while the server physics remains unaffected" ([Gaffer 2004](https://gafferongames.com/post/networked_physics_2004/)). [Consensus]
- **Client-authoritative-with-tolerance** (UE `ClientAuthorativePosition` / `bServerAcceptClientAuthoritativePosition` options, NGO's "soft validation … range checks") is a weaker alternative. It is acceptable for PvE, not for competitive PvP ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897), [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).
- **Speed hacks** are time manipulation. Defend in the input path with a tick budget ([02 §7](02-Inputs.md#7-rate-limiting-speed-hack-defence-lag-switches-max-catch-up)), not by checking distance moved.

### 8. Rate validation
- Fire rate, reload duration and ability cooldowns are enforced by the authoritative sim's tick-based timers (the same code the client predicts with) ([07 §1](07-Weapons-and-Predicted-Gameplay.md#1-predict-the-weapon-as-part-of-the-players-tick)).
- Message-rate limits on RPCs and input packets are separate network safeguards ("not gameplay validation", [PurrNet security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).
- PurrNet also recommends rate-limiting "expensive actions and predicted hierarchy creation" ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).

### 9. Host / listen-server caveat
- The host is the authority and can cheat. Photon: "the host player also acts as the server, they can, in theory, cheat" ([Fusion intro](https://doc-us-test.photonengine.com/fusion/current/fusion-intro), via search). NGO says the same for its Host model ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)). Use dedicated servers for competitive play. [Consensus]

### 10. Information leaks (wallhacks)
- Sending every player's position to everyone enables ESP/wallhacks. Server-side visibility culling of snapshots mitigates this. *(No primary source fetched for a specific game's implementation, so this is unverified. Treat it as Nice-to-have at this scale.)* NfE notes relevancy/importance features exist ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)).

---

## Sources
- Glenn Fiedler – [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/), [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/), [Snapshot Compression](https://gafferongames.com/post/snapshot_compression/), [State Synchronization](https://gafferongames.com/post/state_synchronization/)
- Quake III Arena – [cl_input.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_input.c)
- Valve Source SDK 2013 – [player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp)
- Unity NfE – [command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md), [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)
- Unity NGO docs – [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)
- Mirror – [timestamp-batching.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/timestamp-batching.md), [snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md), [lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)
- PurrNet – [security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md), [input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md), [best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md), [flow.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/flow.md)
- Epic – [CMC code copy (gist)](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)
- Photon – [Fusion intro](https://doc-us-test.photonengine.com/fusion/current/fusion-intro)
- Tribes (secondary) – [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-tribes-(1998)), [SnapNet Part 4](http://www.snapnet.dev/blog/netcode-architectures-part-4-tribes/)
