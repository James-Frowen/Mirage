# 06 – Lag Compensation and Hit Registration (FPS)

Server-side rewind of hit volumes, what the client reports, how far to rewind, peeker's advantage, client-side hit feedback, and hitscan vs projectiles.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Server-side rewind ("favor the shooter")

**Practice: the server keeps a per-tick history of each player's hit volumes. When it processes a shot, it temporarily moves the *other* players' hit volumes to where the shooter saw them, traces, then restores.** [Consensus]
- Source: `CLagCompensationManager` records history and, for each shot, `BacktrackPlayer`s every other player ("Don't lag compensate yourself you loser…"). A `WantsLagCompensationOnEntity` hook lets the game skip irrelevant targets ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)). Bernier's 2001 paper describes the method ([Valve wiki](https://developer.valvesoftware.com/wiki/Latency_Compensating_Methods_in_Client/Server_In-game_Protocol_Design_and_Optimization), not fetched; summarised by [Claypool survey](https://dl.acm.org/doi/full/10.1145/3519023)).
- Gambetta: the client sends "the exact timestamp of your shot, and the exact aim of the weapon"; the server reconstructs the world at that instant ([Part IV](https://www.gabrielgambetta.com/lag-compensation.html)).
- NfE: `PhysicsWorldHistorySingleton` + `CommandDataInterpolationDelay` ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)).
- Fusion: `HitboxManager` history and `Runner.LagCompensation.Raycast(…, PlayerRef …)` resolved "from the perspective of that player's timeframe" ([Fusion lag compensation](https://doc.photonengine.com/fusion/current/manual/advanced/lag-compensation)).
- Mirror: `LagCompensator` component plus a generic `LagCompensation.cs` history ([lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)).
- PurrNet: `ColliderRollback` + `predictionManager.lagCompensation` ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)).
- FishNet Pro: RollbackManager / lag-compensation "states" ([FishNet lag compensation states](https://fish-networking.gitbook.io/docs/guides/features/lag-compensation/states)).
- *Why*: without it, players must lead targets by about RTT + interp delay. Mirror: "players will immediately complain that they don't hit where they aim" ([lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)).
- *Cost*: the victim can be hit "just after reaching cover". NGO frames it as "it's way more frustrating for an attacker to always miss their shots than for a target to get shot behind a wall once in a while" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).

## 2. What tick/time the client reports, and interpolation accounting

**Practice: the rewind target is "the remote-player state the shooter was *looking at* when the shot input was generated". That is the client's interpolation (view) time, not its predicted tick.** [Consensus]

How each system derives it:
- **Source**: the usercmd carries `tick_count` (the client's tick when the command was created). The server rewinds to `cmd->tick_count − lerpTicks` (client interp). It sanity-checks this against `latency + lerp`: "if difference … is too big > 200ms, use time correction based on latency". It clamps the total to `[0, sv_maxunlag]` ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)). The well-known wiki formula is "Command Execution Time = Current Server Time − Packet Latency − Client View Interpolation" ([community summary](https://steamcommunity.com/sharedfiles/filedetails/?id=501119397)).
- **NfE**: the server reads the client's interpolation delay from `CommandDataInterpolationDelay` and looks up `PhysicsWorldHistory` at that offset ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)).
- **PurrNet**: the client uploads its *view offset* with each input. The server clamps it to **Max Lag Compensation Seconds**; "a modified client cannot rewind further than you allow". With tick-stamped inputs, "round trip is not what needs compensating. What needs compensating is how far behind the live tick the player's screen was" ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md), [security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).
- **Mirror**: the client does its own raycast and sends `(target, originPoint, hitPoint)`. The server checks the hit point against the target's *bounds history* at the client's estimated time (`RaycastCheck(connectionToClient, …)`) ([lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)). This "client claims, server verifies" variant is cheap, but only as strict as the tolerance.
- **Valorant**: the server "rewinds the simulation to the client's timestamp, evaluates the shot, and sends back the result" ([State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/), via search).

**Tick-aligned (run-ahead) clocks: which offset?** In family-(a) clocks ([01 §2](01-Time-and-Ticks.md#2-clock-and-tick-synchronisation--two-families)), the shot input is stamped with predicted tick `P` and executes at server tick `P`. The remotes the shooter saw were at interpolation tick `I`. Using NfE's timeline table, `P − I ≈ RTT + slack + interpolation buffer` ([NfE interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)).
- **The simplest robust approach is to have the client put `I` (its view tick, with a fraction) in the input.** The server then validates `P − I` against what it expects (≈ measured RTT + buffers) and clamps it, as Source does with its 200 ms sanity check *(derived from the sources above)*.

**Practice: interpolate the hitbox history between the two recorded ticks around the target time** (Source interpolates position/angles/pose; "add one to the newer so it is lerping from .9 to 1.1") ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)). With a fractional view tick and ≥ 30 Hz history, the error is small. Snapping to the nearest tick is acceptable for a first version. [Single-source detail]

## 3. Maximum rewind

| Source | Max rewind |
|---|---|
| Source SDK 2013 | `sv_maxunlag` **1.0 s** (dev-only cvar) ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)) |
| NfE guidance | "consider limiting the server's state history to between **250-500 ms** … otherwise you risk players … being shot behind a wall" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)) |
| PurrNet | **0.15 s** default (view offset only, because ticks already absorb RTT) ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)) |
| Fusion | `HitboxBufferLengthInMs` default **200 ms** (2.1.1 API ref) ([LagCompensationSettings](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_lag_compensation_settings.html)) |
| Mirror | `historyLimit` 6 × `captureInterval` 100 ms = **600 ms** ([LagCompensationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/LagCompensation/LagCompensationSettings.cs)) |
| Overwatch | Generally favours the shooter, with ability exceptions favouring the defender (dev video via [PCGamesN](https://www.pcgamesn.com/overwatch/cute-stickers-and-little-cups-help-explain-the-overwatch-netcode), secondary). A ping cap is *(unverified)*. |

- The numbers differ because they measure different things. Source's limit includes full latency (family (b) clock). PurrNet's covers only the view offset. For a family-(a) design where the rewind is `RTT + buffers`, a cap of **~200–300 ms** (≈ 150–200 ms RTT player) is a reasonable middle ground. Above the cap, clamp: the high-ping shooter must lead targets, which protects everyone else. See [12 §5](12-Contested-Points.md#5-how-far-to-favour-the-shooter-max-rewind).
- **Disable rewind for state that can't be reproduced** unless you also record it. NfE: "Netcode for Entities only saves backups of the server's physics state. If players have some other state influencing the result of an action, such as being invincible, you need to track the history of those states" ([server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md)). Examples: crouch/hitbox size, invulnerability, alive/dead.

## 4. Hitboxes vs colliders history

**Practice: record a dedicated, small set of hit volumes (capsules/boxes per bone or a few per body), not the full physics scene. Rewind them by moving the volumes, not by re-simulating physics.** [Consensus]
- Source records origin, angles, mins/maxs, animation layers and pose parameters per player, and flushes the bone cache when rewinding ([player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp)).
- Fusion uses separate `Hitbox`/`HitboxRoot` components (not Unity colliders) in its own history buffer. Both must be active to be hit ([HitboxRoot API](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_hitbox_root.html)).
- PurrNet: "The queries do not physically move colliders … the ray is bent per collider against its recorded history, so no physics events fire" ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)).
- Mirror stores bounds history (cheap broad phase), then you do the precise check ([HistoryBounds.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/LagCompensation/HistoryBounds.cs), [lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md)).
- *In Unity specifically*: if you do move real colliders for the query, call `Physics.SyncTransforms()` after moving and after restoring, and keep these colliders on a dedicated layer ([Unity autoSyncTransforms](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Physics-autoSyncTransforms.html)). Pure math (ray vs capsule against recorded transforms) avoids PhysX state changes entirely and is easier to debug.
- **Animation-driven hitboxes**: the server must produce the same pose the client rendered. That means server-side animation (Source records pose params) or a simplified set of hitboxes driven by stance only. For a simple system, capsules driven by stance (stand/crouch) plus head/body split are common *(unverified as a cited norm)*.

## 5. Peeker's advantage

- *What*: a player moving around a corner sees the stationary defender before the defender sees them. Riot gives a formula covering both clients' frame times, one-way latency, server tick and interpolation buffering ([Riot tech blog](https://technology.riotgames.com/news/peeking-valorants-netcode), [04: On Peeker's Advantage](https://playvalorant.com/en-us/news/game-updates/04-on-peeker-s-advantage-ranked)). Secondary summaries report ~141 ms reduced to ~100 ms by 128 Hz ticks, minimal buffering and Riot Direct *(numbers not confirmed from the Riot text)*.
- *Levers*: lower interpolation buffer, higher tick/send rate, higher client frame rate (an IEEE ToG study found frame rate most influential, [spotlight](https://transactions.games/spotlight/august-2026-spotlight-paper)), and a lower lag-compensation cap. You can't remove it. You can only trade it against smoothness and fairness for high-ping players.
- *For 16–32p FPS on a budget*: 60 Hz ticks, 2-tick interpolation buffer (~33 ms) and a ~200 ms rewind cap are in line with the references *(synthesis)*.

## 6. Client-side hit prediction: hit markers, blood, decals

**Practice: predict *cosmetic* impacts locally (tracer, muzzle flash, wall decal, impact sound, possibly blood puff). Show *damage-bearing* feedback (hit marker, damage numbers, kill feed) only on server confirmation.** [Consensus, with a known tension]
- A detailed write-up: decals, impact sounds and blood particles may be shown client-side, but "Effects with permanent consequences, like decreasing health or shot confirmations represented as a hitmarker, must wait until the client receives the server's response" ([The art of Hit Registration](https://danieljimenezmorales.github.io/2023-10-29-the-art-of-hit-registration/), via search).
- Valorant waits for the server for hit effects: "there is a delay between the tracer and the hit effects equal to your connection's round trip time" ([State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/), via search).
- Counter-example / risk: Black Ops 6 players saw blood with no damage. Treyarch called it "erroneous visual blood effects" ([PCGamesN](https://www.pcgamesn.com/call-of-duty-black-ops-6/hit-registration)). Predicted blood that disagrees with server results erodes trust.
- Source predicts weapon effects client-side under `cl_predictweapons`, gated by `IsFirstTimePredicted` ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)).
- PurrNet runs the lag-compensated query inside `Simulate` on the shooter's client against its own recorded history, "so the hit is predicted locally with no round trip". The verified frame corrects it if it differs ([lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md)).
- *For a simple system*: predict tracer/muzzle/impact-on-world; show blood/hitmarker on confirmation. Optionally add a subtle "predicted hit" cue, which is a design choice ([12 §8](12-Contested-Points.md#8-client-predicted-hit-markers)).

## 7. Hitscan vs projectiles

- **Hitscan**: lag-compensated ray at fire time (all of the above).
- **Slow projectiles** (rockets, grenades): usually simulated forward on the server in the present. The client predicts the spawn and flight locally for the shooter, and other clients see the interpolated server projectile ([07 §3](07-Weapons-and-Predicted-Gameplay.md#3-predicted-spawns-projectiles-and-what-to-do-on-mispredict)).
  - Overwatch predicts projectiles by default (secondary: [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback)). A GameDev.net thread discusses Ford's Q&A comment on predicted rockets ([GameDev.net](https://www.gamedev.net/forums/topic/701388-overwatch-predicted-rockets-analysis/), secondary).
  - Whether to lag-compensate projectile *collisions* against players is a design choice. Common hybrid (unverified as a cited norm): rewind for the first few ticks of flight (up to the shooter's latency), then present-time collisions.
- **Fast projectiles** (bullet travel time): Fusion samples model them as data in a ring buffer, not network objects. Hitscan-like buffers store "fire position, direction, and impact data" ([Projectiles Essentials](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials), [Projectiles Advanced](https://doc.photonengine.com/fusion/current/technical-samples/projectiles-advanced)).

---

## Sources
- Valve Source SDK 2013 – [player_lagcompensation.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_lagcompensation.cpp), [prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp); [Latency Compensating Methods (Bernier 2001)](https://developer.valvesoftware.com/wiki/Latency_Compensating_Methods_in_Client/Server_In-game_Protocol_Design_and_Optimization) (not fetched); [Steam community guide](https://steamcommunity.com/sharedfiles/filedetails/?id=501119397) (secondary)
- [Claypool et al., Survey and Taxonomy of Latency Compensation (ACM)](https://dl.acm.org/doi/full/10.1145/3519023)
- Gabriel Gambetta – [Part IV: Lag Compensation](https://www.gabrielgambetta.com/lag-compensation.html)
- Unity NfE – [server-rewind.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/server-rewind.md), [interpolation.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/interpolation.md)
- Unity NGO docs – [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)
- Unity – [Physics.autoSyncTransforms](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Physics-autoSyncTransforms.html)
- Photon Fusion – [Lag Compensation](https://doc.photonengine.com/fusion/current/manual/advanced/lag-compensation), [LagCompensationSettings](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_lag_compensation_settings.html), [HitboxRoot](https://doc-api.photonengine.com/en/fusion/current/class_fusion_1_1_hitbox_root.html), [Projectiles Essentials](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials), [Projectiles Advanced](https://doc.photonengine.com/fusion/current/technical-samples/projectiles-advanced)
- Mirror – [lag-compensation.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/lag-compensation.md), [LagCompensationSettings.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/LagCompensation/LagCompensationSettings.cs), [HistoryBounds.cs](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/LagCompensation/HistoryBounds.cs)
- PurrNet – [lag-compensation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/lag-compensation.md), [security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)
- FishNet – [Lag compensation states](https://fish-networking.gitbook.io/docs/guides/features/lag-compensation/states)
- Riot – [Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode), [The State of Hit Registration](https://playvalorant.com/en-us/news/dev/the-state-of-hit-registration/), [04: On Peeker's Advantage & Ranked](https://playvalorant.com/en-us/news/game-updates/04-on-peeker-s-advantage-ranked)
- [The art of Hit Registration (D. Jiménez Morales)](https://danieljimenezmorales.github.io/2023-10-29-the-art-of-hit-registration/)
- [PCGamesN – Black Ops 6 hit registration](https://www.pcgamesn.com/call-of-duty-black-ops-6/hit-registration); [PCGamesN – Overwatch netcode video](https://www.pcgamesn.com/overwatch/cute-stickers-and-little-cups-help-explain-the-overwatch-netcode)
- Overwatch secondary – [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback), [GameDev.net predicted rockets](https://www.gamedev.net/forums/topic/701388-overwatch-predicted-rockets-analysis/)
- [IEEE Transactions on Games spotlight](https://transactions.games/spotlight/august-2026-spotlight-paper)
