# 04 – Error Smoothing, Render Interpolation and Camera

How to hide corrections and tick quantisation from the player without corrupting the simulation.

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Separate the visual from the simulation root

**Practice: the simulated object (collider/controller + state) and the rendered object (mesh, first-person arms, camera anchor) are different transforms. Corrections snap the simulation; only the visual is smoothed.** [Consensus]
- Gaffer: do not smooth "at the simulation level because it ruins the extrapolation". Keep "position and orientation error offsets … render them at the simulation position + error offset" ([State Synchronization](https://gafferongames.com/post/state_synchronization/)).
- FishNet: "all predicted objects must have their graphics as a child of the predicted object … set as the graphical object". NetworkObject interpolation "handles graphical smoothing after running states" ([Configuring NetworkObject](https://fish-networking.gitbook.io/docs/guides/features/prediction/configuring-networkobject), via search).
- Mirror `PredictedRigidbody`: in "Smooth" mode the Rigidbody and colliders move to an invisible ghost object while "the Renderer stays on the original object and smoothly interpolates behind". "Fast" mode keeps them together, which is snappier but harsher ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)).
- PurrNet: "simulate and reconcile state deterministically, then render a smooth view from that state" via `UpdateView` ([views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)).
- NfE: `GhostPredictionSmoothing` registers per-component smoothing actions, applied when a reconcile happens ([prediction-smoothing.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-smoothing.md)).
- Unity specifics: put the mesh, first-person rig and camera pivot under a child transform. Never parent gameplay colliders to the smoothed child. Mirror warns that in Smooth mode `GetComponent<Rigidbody>()`/`Collider` and `OnCollision*` may not be on the object you expect ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)).

## 2. Error offset and decay

**Practice: on a reconcile, compute `error = oldPresentedPos − newSimPos`, add it to the visual offset, and decay the offset towards zero over ~0.1 s. Snap above a distance threshold, and clear it on teleports.** [Consensus]

| Source | Decay | Snap / ignore thresholds |
|---|---|---|
| Quake 3 | linear over `cg_errordecay` = **100 ms** | error < 0.1 units ignored. Teleport clears the error ("a teleport will not cause an error decay") ([cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c), [cg_view.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_view.c), [cg_main.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_main.c)) |
| Source | `cl_smoothtime` = **0.1 s**, `cl_smooth` 1 "Smooth view/eye origin after prediction errors" | — ([c_baseplayer.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_baseplayer.cpp)) |
| Gaffer | exponential: ×0.95/frame for small errors (≤25 cm), ×0.85 for large (≥1 m), blended between. Orientation via slerp to identity | "using a single smoothing factor gives unacceptable results"; large pops must resolve fast ([State Synchronization](https://gafferongames.com/post/state_synchronization/)) |
| Gaffer 2004 | move 10% of the difference per update | ignore < 10 cm, snap > 2 m. Smooth position/orientation and snap velocities ([Networked Physics 2004](https://gafferongames.com/post/networked_physics_2004/)) |
| PurrNet | `correctionBlendMinMax` scales rate by error size | `teleportThresholdMinMax`: below min ignore, above max snap; `ResetInterpolation()` for teleports ([views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)) |
| UE CMC (simulated proxies / listen-server remotes) | `NetworkSimulatedSmoothLocationTime` **0.1 s** (0.04 s on listen server); Linear or Exponential (`ENetworkSmoothingMode`) | `NetworkNoSmoothUpdateDistance` **384** cm teleports; `NetworkMaxSmoothUpdateDistance` 256 ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897), [ENetworkSmoothingMode](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/ENetworkSmoothingMode)) |

- **Accumulate, don't replace**: if a new correction arrives while an old offset is still decaying, add the new error to the remaining offset. Then the visual position is continuous (Gaffer: the new offset "gives an identical result to the current (smoothed) visual position"; Q3 adds `delta` to the decayed `predictedError`). [Consensus]
- **Smooth position and orientation; snap derivatives.** Gaffer 2004: smoothing "immediate quantities such as position and orientation, while directly snapping derivative quantities such as velocity". [Single-source but uncontroversial]
- **Should the *local* player's own body be smoothed at all?** Quake 3 and Source smooth the local view origin. UE CMC's `SmoothCorrection` only runs for simulated proxies and remote autonomous proxies on a listen server, so the owning client is not smoothed by it ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)). For an FPS, a short (~100 ms) decay on the camera/eye position is the established practice (Q3, Source). See [12 §6](12-Contested-Points.md#6-smooth-the-local-player-or-snap).

## 3. Render interpolation between ticks

With 60 Hz ticks on a 144 Hz monitor, rendering the latest tick's state judders. Gaffer calls it a "subtle but visually unpleasant stuttering" ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)).

**Option A (recommended for simplicity): render one tick behind, interpolating between the previous and current tick states by `alpha = accumulator / dt`.** [Consensus]
- Gaffer: "interpolate between the previous and current physics state based on how much time is left in the accumulator", using slerp for rotations ([Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/)).
- lightyear `FrameInterpolationPlugin`: "rendering one fixed tick behind the simulation: `lerp(previous_fixed_value, current_fixed_value, overstep_fraction)`". It is separate from network interpolation and has a `SkipFrameInterpolation` marker for teleports ([visual_interpolation.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/visual_interpolation.md)).
- Fusion KCC: render *interpolation* is "more performant and easier to maintain" and recommended for characters not controlled locally ([KCC render behaviour](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior), via search).
- Unity Rigidbody `interpolation = Interpolate` is the built-in version of this for non-networked rigidbodies ([Unity forum summary](https://discussions.unity.com/t/digital-foundry-unity-fixedtimestep-callout/857631), via search).
- *Cost*: up to one tick (~16 ms at 60 Hz) of added visual latency on the local body. The camera rotation must not wait for this (§4).

**Option B: extrapolate or "render-predict" the partial tick.**
- NfE *partial ticks* re-simulate from the last full tick with the fractional `dt` every frame ([intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)). The Fusion KCC "full prediction render mode" runs the whole KCC pipeline in render ([KCC render behaviour](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior)).
- *Pros*: no added latency. *Cons*: a second (variable-`dt`) code path, extra CPU, and more ways to mispredict. *Nice-to-have*, not essential.

**Combine with error offset**: `renderPos = Lerp(prevTickPos, curTickPos, alpha) + errorOffset`. After a replay, re-baseline *both* `prevTickPos` and `curTickPos` from the replayed history, so the interpolation doesn't lerp across the correction. Let the error offset carry the jump instead *(derived from Gaffer + lightyear; not one cited recipe)*.

## 4. Camera rules

**Practice 1: never roll back, correct or re-simulate the local view rotation (yaw/pitch). Apply mouse look every render frame, directly to the camera, and send the resulting absolute angles in the next tick's input.** [Consensus]
- Source: the server takes `ucmd->viewangles` as the player's view angles (after a validity check). It forces angles only through explicit `fixangle` events such as teleports/spawns ([player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp), [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)).
- NGO docs: look direction "can easily be client driven … Having a correction on where you look would feel weird" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).
- Fusion KCC: "Force Predicted Look Rotation … skips look rotation interpolation in render for local character … for extra look responsiveness" ([KCC render behaviour](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior)). The Fusion BR200 sample processes "render predicted movement and look rotation" from per-frame input ([BR200 player](https://doc.photonengine.com/fusion/v2/game-samples/br200/player), via search).
- Quake 3 applies the decayed prediction error to `refdef.vieworg` (position) only ([cg_view.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_view.c)).
- *Exception*: server-forced rotation (respawn facing, knockback that turns you, scripted cameras). Send these as an explicit, sequenced "set view angles" event that the client applies once, not as part of reconcile.

**Practice 2: camera *position* follows the smoothed visual eye position, not the raw sim position**, and is updated after movement (Unity: `LateUpdate`, or after your tick + interpolation step). [Consensus: Q3/Source smooth eye origin; Unity forum advice to move camera-follow to `LateUpdate` ([bugnet guide](https://bugnet.io/blog/fix-unity-rigidbody-interpolation-stutter-with-camera), secondary)]

**Practice 3: don't smooth raw mouse input more than the player asks for.** The Fusion Advanced KCC input-smoothing guide notes that feeding raw deltas straight to camera rotation can look uneven, so it offers optional smoothing over a time window ([input smoothing](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/input-smoothing)). This is a feel choice, not a netcode requirement. [Single-source]

**Practice 4: first-person weapon/viewmodel animation is presentation.** Drive it from the presented state and local input, not from the replayed sim (see [10 §8](10-Unity-Specific.md#8-animation-and-animator-under-prediction)).

---

## Sources
- Glenn Fiedler – [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/), [State Synchronization](https://gafferongames.com/post/state_synchronization/), [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/)
- Quake III Arena – [cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c), [cg_view.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_view.c), [cg_main.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_main.c)
- Valve Source SDK 2013 – [c_baseplayer.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/c_baseplayer.cpp), [player_command.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player_command.cpp), [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)
- Epic – [CMC code copy (gist)](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897), [ENetworkSmoothingMode](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/ENetworkSmoothingMode)
- Unity NfE – [prediction-smoothing.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-smoothing.md), [intro-to-prediction.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/intro-to-prediction.md)
- Unity NGO docs – [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)
- FishNet – [Configuring NetworkObject](https://fish-networking.gitbook.io/docs/guides/features/prediction/configuring-networkobject)
- Mirror – [client-side-prediction.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)
- PurrNet – [views-and-interpolation.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/views-and-interpolation.md)
- lightyear – [visual_interpolation.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/visual_interpolation.md)
- Photon Fusion – [Advanced KCC render behaviour](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior), [Advanced KCC input smoothing](https://doc.photonengine.com/fusion/current/addons/advanced-kcc/input-smoothing), [BR200 player](https://doc.photonengine.com/fusion/v2/game-samples/br200/player)
- Unity community – [Digital Foundry FixedTimestep thread](https://discussions.unity.com/t/digital-foundry-unity-fixedtimestep-callout/857631), [bugnet camera stutter guide](https://bugnet.io/blog/fix-unity-rigidbody-interpolation-stutter-with-camera)
