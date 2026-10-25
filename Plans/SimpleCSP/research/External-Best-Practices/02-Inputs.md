# 02 – Input Handling

How inputs are sampled, packed, sent, buffered on the server, and what happens when they are late, missing or early. Also covers the speed-hack and lag-switch defences that live in the input path.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Input = the only thing the client is authoritative over

**Practice: the client sends *intent* (buttons, move axes, view angles), never results (position, hits, ammo).** [Consensus]
- Gambetta: clients send inputs, the server runs the game, "you don't trust the player with its position in the world" ([Part I](https://www.gabrielgambetta.com/client-server-game-architecture.html)).
- PurrNet: an owning client "cannot directly declare an authoritative position, health value, spawn, hit, or physics result through the prediction state pipeline" ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).
- Gaffer 2004: "Character physics are completely driven from input data", and state lives in a separate struct ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)).
- *Exception*: **view angles** are effectively client-authoritative in every FPS reference. Source copies `ucmd->viewangles` into the player's view after a sanity check ([player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp)). Unity's NGO docs: "your look direction can easily be client driven … Having a correction on where you look would feel weird" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).

**Practice: send absolute view angles in each input, not mouse deltas.** [Consensus by example]
- Source `CUserCmd` carries `viewangles` (absolute) and also `mousedx/mousedy` ([usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h)). UE CMC packs control-rotation yaw/pitch into each move ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)).
- *Why*: with deltas, a lost or repeated input permanently rotates the server's view relative to the client's. Absolute angles are self-healing *(reasoning, not a cited quote)*.

**Typical FPS input struct** (Source `CUserCmd`, trimmed): `command_number`, `tick_count`, `viewangles`, `forwardmove/sidemove/upmove`, `buttons` bitmask, `weaponselect`, `random_seed` ([usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h)). The `random_seed` is derived from the command number (`MD5_PseudoRandom(sequence_number)`) so weapon spread is predictable ([in_main.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/in_main.cpp)).

```csharp
// Illustrative only
struct FpsInput {          // ~12-16 bytes if quantised
    uint  Tick;            // or implicit from packet header
    short Yaw, Pitch;      // absolute, quantised
    sbyte MoveX, MoveY;    // -127..127
    ushort Buttons;        // Fire, AltFire, Jump, Crouch, Reload, Use...
    byte  WeaponSlot;
}
```

---

## 2. Input sampling vs tick rate

**Practice: poll devices every render frame, but *commit* one input per tick. Accumulate edge-triggered events (press/release) so a press between ticks is never lost.** [Consensus]
- NfE: `InputEvent` makes one-off events (like `GetKeyDown`) "registered exactly once, even when the exact input tick where the input event was first registered is dropped on its way to the server". Other values "will be overwritten by their latest value" ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md), [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- PurrNet: `UpdateInput` (per render frame) caches edges like `jump |= GetKeyDown(...)`, and `GetFinalInput` (per tick) fills the tick's input ([input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)).
- Fusion: `OnInput` runs at the fixed tick rate while Unity input refreshes in `Update`. "One Update frame might contain zero, one, or several OnInput calls", so brief inputs must be accumulated ([Fusion player input](https://doc.photonengine.com/fusion/current/manual/data-transfer/player-input), via search summary).
- *Edge cases*:
  - If several ticks run in one frame, send the press only in the first tick. Then "held" state is valid for the rest.
  - If no tick runs in a frame, keep accumulating.
  - Clear the accumulator only after a tick consumes it.
- **View rotation is the exception**: sample and apply it every render frame for the camera ([04 §4](04-Smoothing-and-Camera.md#4-camera-rules)). The tick input carries the angle *as of the tick*.

**Practice: inside the simulation, read only the input struct, never `UnityEngine.Input` or `Time.*`.** [Consensus]
- NfE: "you must rely only on the commands present in the `ICommandData` buffer … Polling input directly … can cause client mis-prediction" ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md)).
- PurrNet: "Do not use `Time.deltaTime`, `Time.time`, `UnityEngine.Random`, frame count, or wall-clock time in tick simulation" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
- FishNet: "if a value can affect your prediction, don't store it outside the replicate method unless you are also reconciling that value" ([FishNet advanced controls](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/advanced-controls), via search summary).

---

## 3. Redundant input sending and packet loss

**Practice: send inputs unreliably, and include the last *N* (unacknowledged) inputs in every packet.** [Consensus]

| Source | Redundancy |
|---|---|
| Quake 3 | `cl_packetdup` default **1**, max 5: each packet repeats the commands of the previous N packets. `cl_maxpackets` default 30, clamped 15–125 ([cl_input.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_input.c), [cl_main.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_main.c)) |
| Source | The server receives `numcmds` new commands plus backup commands and replays backups for dropped packets ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)) |
| NfE | Each packet carries the current tick + `TargetCommandSlack + NumAdditionalCommandsToSend` previous ticks (**2 + 2 = 4**), each "delta compressed against the command for the current tick". 1024-byte payload limit ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)) |
| PurrNet | Window of recent ticks "clamped between 4 and 32 ticks". An unchanged tick costs "a single repeat bit". The window is resent every 20 ms until a newer one replaces it ([input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)) |
| FishNet | Redundancy count = state-interpolation + 1 (default **3**) ([PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs)) |
| Gaffer | Include "*all inputs* in each UDP packet until we know for sure the other side has received them", with 1 bit per unchanged input ([Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)) |
| Rocket League | Up to 10 frames of input history per packet; confirmed inputs not resent (dev comments via [devtrackers](https://devtrackers.gg/rocket-league/p/c2e0e1bc-netcode-lag-explained-with-lots-of-examples-rocket-science-16), secondary) |

- *Why*: reliable/ordered delivery stalls on loss. Gaffer shows hitches every packet loss with TCP-style resend ([Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)). Redundancy repairs loss with zero extra RTT.
- *How many*: netfox gives the success probability as `1 − (1 − p)^r` for redundancy *r* and per-packet success *p* ([netfox network-rollback](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-rollback.md)). With 5% loss and r = 3 that is 99.99%. "Until acked" (Gaffer, PurrNet) is the adaptive version.
- *When it doesn't matter*: on LAN-only games. Even then it is nearly free because inputs are tiny.

---

## 4. Server-side input (jitter) buffer

**Practice (family (a) clocks): hold each client's inputs keyed by tick and consume exactly one per server tick. Size the buffer by *feedback*: keep 1–3 ticks of slack and let the client clock adjust.** [Consensus]
- NfE: default slack **2 ticks**; "Using command slack is an effective way to mitigate a small amount of network jitter" ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)).
- Rocket League: buffer size "is not fixed. It tries to grow or shrink depending on network jitter" (dev comment, secondary). Officially, "Higher values may increase input lag, rubber-banding, and latency" ([support page](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971)).
- FishNet: `MaximumServerReplicates` default **15** per object; `DropExcessiveReplicates` default **true**. When false, the server "will hold at most up to 3 seconds worth of replicates, consuming multiple per tick … potentially could allow speed hacking" ([PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs)).
- Valorant (secondary): Riot developers said the server "should be playing back your movement, triggers, and other inputs at a delay but otherwise synchronized to how you played them" ([devtrackers](https://devtrackers.gg/valorant/p/e1133afd-peekers-advantage-strafe-shooting)).
- *Family (b) clocks (Source/UE)*: no buffer. Commands run when they arrive and jitter becomes per-player phase shift.

**Report the buffer occupancy (or "command age") back to the client every snapshot.** This is the input for the clock controller in [01 §2](01-Time-and-Ticks.md#2-clock-and-tick-synchronisation--two-families). [Consensus within family (a): NfE, Rocket League, Overwatch (secondary)]

---

## 5. Missing-input policy (input not there when the server ticks)

Options: **repeat last input**, **pause** (don't simulate that player), or **drop** (simulate with neutral input).

| Source | Policy |
|---|---|
| NfE | Server: "Get input for tick 10 for all clients, or apply the last received input" ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)) |
| Source | For dropped packets with no backup: "run the last known cmd for each dropped cmd" (up to 24). If the client stops sending for longer than the budget: "Client … dropped too many packets, simulating last cmd". After a timeout: `RunNullCommand()` ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)) |
| PurrNet | Optional extrapolation with a **Repeat Input Factor** cap. "Strip edge-triggered actions in `ModifyExtrapolatedInput` so a missing frame cannot repeat jump, fire, interact, or purchase commands" ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md), [input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md)) |
| Fusion | `GetInput` returns false when the input missed its window; docs suggest caching "the last *good* input and re-use it"; `OnInputMissing` callback ([Fusion player input](https://doc.photonengine.com/fusion/current/manual/data-transfer/player-input), via search summary) |
| Quantum | Predicted frames repeat the last inputs ([Quantum mispredictions](https://doc.photonengine.com/quantum/v3/concepts-and-patterns/mispredictions-and-entity-views)) |
| Rocket League | Server assumes the input matches the last one received (community/dev comments, secondary) |
| Gaffer lockstep | Pauses (can't advance without input). This is the deterministic-lockstep model, not CSP ([Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)) |

**Recommended policy for an FPS** [Consensus across NfE/Source/PurrNet/Fusion/Quantum]:
1. Repeat the last input's **continuous** parts (move axes, held buttons, view angles) for at most a few ticks (cap). After the cap, use neutral input.
2. **Clear one-shot edges** (jump press, fire press, reload press, use) in the repeated copy. "Fire held" for automatic weapons is a design decision: repeating it may fire bullets the player didn't ask for.
3. When the real input for that tick arrives late, **discard it** (that tick is already simulated). The client's reconcile will correct it.
4. "Pause" is wrong for client/server CSP. It gives lag-switch users invulnerability windows and makes the player freeze for others *(reasoning; see [12 §4](12-Contested-Points.md#4-missing-input-repeat-vs-pause-vs-drop))*.

---

## 6. Late and early inputs

- **Late (target tick already simulated)**: drop it. NfE updates inputs for tick 10 "if it hasn't already simulated tick 10" ([command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md)). Gaffer 2004: "ignores any input received with a time value less than the current time" ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)). The reported command age then tells the client to run further ahead. [Consensus]
- **Early (target tick far in the future)**: buffer up to a limit, then drop or clamp. FishNet caps the queue (15). Q3 clamps `serverTime` to ≤ `level.time + 200` ms ([g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c)). [Consensus]
- **Family (b)**: Source runs late commands as catch-up, bounded by the tick budget (§7).

---

## 7. Rate limiting, speed-hack defence, lag switches, max catch-up

**Practice: the server decides how many input ticks a player may consume per server tick (on average one). Budget a small catch-up allowance and drop the rest.** [Consensus]
- **Source**: each server tick grants `m_nMovementTicksForUserCmdProcessingRemaining++`, and commands consume it. `sv_maxusrcmdprocessticks` (**24** in SDK 2013) caps the accumulated budget. The comment says this "prevents users from manipulating the game time by more than 0.25s" ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)).
- **UE CMC**: optional time-discrepancy detection compares client timestamp deltas with server elapsed time. Over a threshold (`MovementTimeDiscrepancyMaxTimeMargin`, example 0.25 s), the excess time is "paid back" by shortening later moves ([UE forum](https://forums.unrealengine.com/t/client-speed-hack/359179), via search summary).
- **Quake 3**: per-command `msec` capped at 200 and `serverTime` clamped ([bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c), [g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c)).
- **FishNet**: dropping excessive replicates is the default for attack resistance (§4).
- **Family (a) tick-keyed buffers** give this almost for free: one input per server tick is consumed, so a client sending faster only fills a bounded buffer.
- **Lag switches** (client deliberately withholds packets, then bursts): defended by (1) repeat-last-input with edges stripped while missing, (2) dropping late inputs instead of executing them in a burst, (3) the catch-up cap above, and (4) clamping lag-compensation rewind ([06](06-Lag-Compensation-and-Hit-Registration.md#3-maximum-rewind)). A public Valorant exploit let players "deliberately stutter" ([Dexerto](https://www.dexerto.com/valorant/major-valorant-netcode-exploit-lets-players-deliberately-stutter-in-game-1397671/), secondary). Details not verified.
- PurrNet's warning applies: "Input queue limits are networking safeguards, not gameplay validation" ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)).

**Practice: sanitise every input on the server before simulating** (NaN, out-of-range axes, impossible weapon slots). Source replaces invalid view angles "to prevent hacked clients from sending us invalid view angles to try to get leaf server code to crash" ([player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp)). PurrNet's `SanitizeInput` clamps the move vector ([security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md)). [Consensus]

---

## Sources
- Gabriel Gambetta – [Part I: Client-Server Game Architecture](https://www.gabrielgambetta.com/client-server-game-architecture.html)
- Glenn Fiedler – [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/), [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/)
- Valve Source SDK 2013 – [usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h), [in_main.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/in_main.cpp), [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp), [player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp)
- Quake III Arena – [cl_input.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_input.c), [cl_main.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_main.c), [g_active.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c), [bg_pmove.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/bg_pmove.c)
- Unity NfE – [command-stream.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/command-stream.md), [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)
- Unity NGO docs – [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)
- PurrNet – [input-handling.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/input-handling.md), [security.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/security.md), [best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)
- FishNet – [PredictionManager.cs](https://github.com/FirstGearGames/FishNet/blob/main/Assets/FishNet/Runtime/Managing/Prediction/PredictionManager.cs), [Advanced Controls](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/advanced-controls)
- Photon – [Fusion player input](https://doc.photonengine.com/fusion/current/manual/data-transfer/player-input), [Quantum mispredictions](https://doc.photonengine.com/quantum/v3/concepts-and-patterns/mispredictions-and-entity-views)
- netfox – [network-rollback.md](https://github.com/foxssake/netfox/blob/main/docs/netfox/guides/network-rollback.md)
- Epic – [CMC code copy (gist)](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897), [Client speed hack thread](https://forums.unrealengine.com/t/client-speed-hack/359179)
- Rocket League – [Input buffering support page](https://www.epicgames.com/help/c-202300000001622/c-202300000001682/rocket-league-input-buffering-settings-a202300000016971), [devtrackers (secondary)](https://devtrackers.gg/rocket-league/p/c2e0e1bc-netcode-lag-explained-with-lots-of-examples-rocket-science-16)
- Valorant – [devtrackers (secondary)](https://devtrackers.gg/valorant/p/e1133afd-peekers-advantage-strafe-shooting), [Dexerto (secondary)](https://www.dexerto.com/valorant/major-valorant-netcode-exploit-lets-players-deliberately-stutter-in-game-1397671/)
