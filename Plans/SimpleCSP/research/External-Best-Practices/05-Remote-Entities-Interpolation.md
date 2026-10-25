# 05 – Remote Entities: Snapshot Interpolation

How to present other players (and other non-predicted objects) smoothly. This also sets what the shooter actually sees, which lag compensation must reproduce.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Interpolate remote players; don't predict them (for an FPS)

**Practice: render remote players in the past, interpolating between buffered server states. Do not run their movement simulation forward.** [Consensus for FPS]
- Gambetta: entity interpolation "doesn't predict future positions at all – it uses only real entity data provided by the server, thus showing the other entities slightly delayed in time" ([Part III](https://www.gabrielgambetta.com/entity-interpolation.html)).
- Source, Valorant, Mirror `NetworkTransform`, NfE interpolated ghosts and Fusion proxies all work this way for remote players ([Source interp cvars](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/cdll_bounded_cvars.cpp), [NfE interpolation](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md), [Mirror snapshot interpolation](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- *When it doesn't apply*: physics-heavy, highly-interactive games predict remote objects. Rocket League predicts all cars (secondary sources). NfE "predicted" mode lets clients predict others using replicated inputs and "the last input in the local input history" for missing ticks ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)). Players dodge unpredictably, so FPS games interpolate. NfE notes "players can also be difficult to predict, especially when they're trying to avoid being hit" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)).

## 2. Buffer delay

**Practice: buffer delay ≈ 2 × send interval (+ jitter margin), so one lost packet still leaves two states to interpolate between.** [Consensus]

| Source | Default |
|---|---|
| Source | interp = max(`cl_interp` **0.1 s**, `cl_interp_ratio` **2** / `cl_updaterate`) ([cdll_bounded_cvars.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/cdll_bounded_cvars.cpp)) |
| NfE | `InterpolationTimeNetTicks` **2** network ticks (or `InterpolationTimeMS`), adapted for RTT/jitter/packet arrival rate ([time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)) |
| Mirror | `bufferTimeMultiplier` **2** × send interval, plus dynamic adjustment ([SnapshotInterpolationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/SnapshotInterpolation/SnapshotInterpolationSettings.cs)) |
| Gaffer | "enough delay so that I can lose two packets in a row": at 2–5% loss, about 3 × the send interval plus 1–2 frames of jitter. That is 350 ms at 10 pps, 150 ms at 30 pps and 85 ms at 60 pps ([Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)) |
| Valorant | remote players rendered with a 7.8125 ms buffer by default (one 128 Hz tick), adjustable via "Network Buffering" (Riot dev post via [devtrackers](https://devtrackers.gg/valorant/p/e1133afd-peekers-advantage-strafe-shooting); Riot: buffering "effectively serves as extra network latency" ([Riot tech blog](https://technology.riotgames.com/news/peeking-valorants-netcode))) |
| PurrNet | view buffer up to 0.1 s (`tickRate / 10`, min 3 ticks) ([views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)) |

- *The real lever is send rate*: Gaffer: "The solution is simple: *increase the send rate!*" ([Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)). At 30–60 Hz snapshots the buffer is 35–100 ms.
- *Trade-off*: every ms of buffer is added to peeker's advantage and to the lag-compensation rewind ([06](06-Lag-Compensation-and-Hit-Registration.md)). Riot keeps it to one tick at 128 Hz for that reason.

## 3. Adaptive delay

**Practice: adapt the buffer to measured jitter, and change it slowly by time-scaling the interpolation clock (catch-up / slow-down), never by jumping.** [Consensus]
- Mirror: `dynamicAdjustment` sets the multiplier from the moving standard deviation of delivery times (`deliveryTimeEmaDuration` 2 s) + `dynamicAdjustmentTolerance` 1. Catch-up **+2%** / slow-down **−4%** when the buffer drifts beyond ±1 snapshot ("slow down a little faster so we don't encounter empty buffer") ([SnapshotInterpolationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/SnapshotInterpolation/SnapshotInterpolationSettings.cs)).
- NfE: interpolation delay from RTT, jitter and arrival rate. Jitter multiplier 1.25, delay changes capped at 10% of frame delta, `InterpolationTimeScale` clamped to 0.85–1.1 ([ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)).
- Kotaku's write-up of the Overwatch dev video mentions "adaptive interpolation delays" (no numbers) ([Kotaku](https://kotaku.com/to-explain-netcode-properly-you-need-props-1769244398), secondary).
- *Simple version*: fixed 2 × send interval, plus a catch-up rule (play at 1.02× if buffered > target + 1 interval, 0.96× if < target − 1 interval). That is Mirror's scheme without dynamic adjustment.

## 4. Which timeline to interpolate on

**Practice: interpolate on the *server's* timeline (server tick or server timestamp embedded in the message), not on local arrival time.** [Consensus]
- NfE: interpolated tick = server tick estimate − (RTT/2 + buffer), and "the server is the authority on the current time" ([interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)).
- Mirror stamps every batch with the sender's `double` timestamp: "For a smooth interpolation, we need to exactly reconstruct what happened on the server" ([timestamp-batching.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/timestamp-batching.md)).
- NGO `ServerTime` "on clients is behind the server" for exactly this use ([networktime-ticks.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/advanced-topics/networktime-ticks.md)).
- With tick-based servers, the interpolation clock is just `serverTickEstimate − delayTicks` with a fractional part.
- **Use one interpolation clock for all remote objects.** Then objects stay mutually consistent, and lag compensation can report a single "view tick". PurrNet: "The manager keeps one view clock for the whole scene … `viewTick` is also what lag compensation reports to the server" ([views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)). [Consensus]

## 5. Extrapolation limits

**Practice: when the buffer runs dry, extrapolate briefly (linear, from last velocity), then hold. Cap it at ~200–350 ms.** [Consensus]
- NfE: extrapolation is "effectively an unclamped interpolation", limited by default to **20 ticks** (about a third of a second at 60 Hz) ([interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)).
- Source: `cl_extrapolate` 1 "if interpolation history runs out" ([c_baseentity.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_baseentity.cpp)). The `cl_extrapolate_amount` default of 0.25 s is *(unverified)*.
- Gaffer: extrapolation is poor for colliding rigid bodies. For "mostly linear motion … a simple extrapolation works well for short time periods (50-250ms or so)" ([Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)).
- *Don't* extrapolate through walls: either clamp extrapolation to a short time or do a cheap collision check *(general advice; Gaffer discusses the failure)*.
- When data resumes after extrapolation, blend back with a short error decay rather than snapping *(practice consistent with [04](04-Smoothing-and-Camera.md); not separately cited)*.

## 6. Packet loss and jitter handling

- **Unreliable, newest-wins snapshots**: "If a snapshot is lost we can just skip past it and interpolate towards a more recent snapshot … We don't ever want to stop and wait for a lost snapshot packet to be resent" ([Gaffer](https://gafferongames.com/post/snapshot_interpolation/)). [Consensus]
- **Send every interval, not only on change**, unless you have acks/delta baselines. Mirror: "Do not send only if changed, this would require knowledge about the other end's last received snapshot" ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)). [Single-source but sound]
- **Insert in timestamp order; drop duplicates/too-old; cap the buffer** (Mirror `bufferLimit` 32, `InsertIfNewEnough`) ([SnapshotInterpolationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/SnapshotInterpolation/SnapshotInterpolationSettings.cs), [snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- **Test the edge cases**: Mirror simulates "a mobile user switches back to the game after a 100s delay", "99% packet loss & scramble", and catch-up when the buffer is too large ([snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md)).
- **Interpolation quality**: linear for position + slerp for rotation is the baseline. Hermite (with velocity) removes visible 1st-order discontinuities at low send rates ([Gaffer](https://gafferongames.com/post/snapshot_interpolation/)). *Nice-to-have at 30–60 Hz.*
- **Teleports / respawns**: mark them (a "teleport counter" or flag in the state) so interpolation doesn't slide across the map. Source lag compensation also refuses to interpolate a player whose recorded position jumped > `sv_lagcompensation_teleport_dist` (64 units) ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)).

## 7. Interaction between the predicted local player and interpolated remotes

- The local player lives at `T + RTT/2 + slack`; remotes are seen at `T − RTT/2 − buffer` ([NfE timelines](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)). Anything the local player does *to* a remote (bump, block, shoot) is evaluated against a different time than the server will use. NGO: "if a local player collides with a server-driven networked object, the player sees the Collider overlap with it before the server reacts" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)). [Consensus]
- Shooting is fixed by lag compensation ([06](06-Lag-Compensation-and-Hit-Registration.md)). Body collisions are covered in [07 §5](07-Weapons-and-Predicted-Gameplay.md#5-predicted-player-vs-non-predicted--remote-objects).

---

## Sources
- Gabriel Gambetta – [Part III: Entity Interpolation](https://www.gabrielgambetta.com/entity-interpolation.html)
- Glenn Fiedler – [Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)
- Valve Source SDK 2013 – [cdll_bounded_cvars.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/cdll_bounded_cvars.cpp), [c_baseentity.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_baseentity.cpp), [player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp); [Source Multiplayer Networking (wiki)](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking) (not fetched)
- Unity NfE – [interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md), [time-synchronization.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/time-synchronization.md), [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)
- Unity NGO docs – [networktime-ticks.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/advanced-topics/networktime-ticks.md), [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)
- Mirror – [snapshot-interpolation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/components/network-transform/snapshot-interpolation.md), [timestamp-batching.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/timestamp-batching.md), [SnapshotInterpolationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/SnapshotInterpolation/SnapshotInterpolationSettings.cs)
- PurrNet – [views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)
- Riot – [Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode); [devtrackers (dev post)](https://devtrackers.gg/valorant/p/e1133afd-peekers-advantage-strafe-shooting)
- [Kotaku on the Overwatch netcode video](https://kotaku.com/to-explain-netcode-properly-you-need-props-1769244398) (secondary)
