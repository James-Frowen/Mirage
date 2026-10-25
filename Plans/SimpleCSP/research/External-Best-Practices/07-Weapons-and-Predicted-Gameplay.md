# 07 – Weapons and Other Predicted Gameplay

Predicting fire, ammo, reload and cooldowns. Predicted spawns (projectiles) and mispredicts. How a predicted player interacts with non-predicted and remote objects (pushing, platforms, player collisions).

Legend: **[Consensus]**, **[Single-source]**, *(unverified)* as in [01](01-Time-and-Ticks.md).

---

## 1. Predict the weapon as part of the player's tick

**Practice: the weapon state machine (fire, ammo, reload, cooldown, switch) runs inside the same predicted `Tick(input, state)` as movement, with the same rollback/replay.** [Consensus]
- Source predicts weapons (`cl_predictweapons`, "Perform client side prediction of weapon effects") on the same usercmd stream as movement ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)).
- Overwatch predicts "all abilities and projectiles by default, opting out only when necessary" (secondary: [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback)).
- PurrNet lists cooldowns, counters and random state as state that must be predicted ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)). It ships a predicted timer module ([predicted-timer-module.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/predicted-modules/predicted-timer-module.md)).
- *Why*: firing must react on the same frame as the click, and ammo/cooldown must stay consistent with movement (for example "can't fire while sprinting" or "reload cancelled by switching").

**Practices for the weapon state**
- **Cooldowns, reload end and burst timing as ticks** (`nextFireTick`, `reloadEndTick`). They are exact under replay. Fire rate is enforced by the same code on the server, which is also your rate-limit check. [Recommendation, consistent with PurrNet "read time … from the predicted world" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md))]
- **Fire rates that aren't a multiple of the tick** (600 RPM = 10 Hz fine at 60 Hz; 750 RPM = 12.5 Hz isn't): carry a fractional accumulator in state, or allow N shots per tick. NGO's "super bullet" diagram shows the problem when updates are coarser than the fire rate ([ticks-and-update-rates.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/ticks-and-update-rates.md)).
- **Spread/recoil randomness must be reproducible**: seed from the command/tick number. Source sets `cmd->random_seed = MD5_PseudoRandom(sequence_number)` so client and server roll the same spread ([in_main.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/in_main.cpp)). PurrNet: use `PredictedRandom`, never `UnityEngine.Random` ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)). [Consensus]
  - Source also has an optional `sv_usercmd_custom_random_seed` that adds a server-only seed, so clients can't predict (and exploit) future spread ([player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)). That trades a little prediction accuracy for anti-cheat. [Single-source]
- **Ammo and reserve are predicted state, validated on the server.** PurrNet: "a sanitized fire button still needs a server-side cooldown, ammo check, and ownership check" ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
- **Weapon switch**: predicted. `weaponselect` is part of Source's usercmd ([usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h)).
- **Pick-ups and ammo grants from the world** are *not* predictable (another object decides). Let the server grant them; the client sees them via reconcile.

## 2. Feedback without double-firing

- Play local fire feedback (muzzle flash, sound, view kick, shell, ammo counter tick) **once**, the first time a tick is predicted, or from a view-layer state diff. Never play it during replays ([03 §7](03-Reconciliation-and-Replay.md#7-side-effects-during-replay-sounds-vfx-rpcs-events-fire-once)). [Consensus]
- **Third-person feedback for other players** comes from server state or events (for example a replicated "shots fired" counter or tick), excluding the owner, who already played it locally *(common practice; not separately cited)*.
- **If a predicted shot is rejected** (server says no ammo / still reloading): the next reconcile restores ammo and the cooldown. The muzzle flash already played. That is accepted as cosmetic, the same "worst case" NGO describes for action anticipation ("worst comes to worst we've played an animation for nothing") ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).

## 3. Predicted spawns (projectiles) and what to do on mispredict

Options, simplest first:

1. **Don't spawn a network object for hitscan.** Use the lag-compensated trace + cosmetic tracer ([06](06-Lag-Compensation-and-Hit-Registration.md)).
2. **Projectiles as data, not objects.** Fusion samples store shots in a ring buffer on the weapon/player (fire tick, origin, direction, impact). Clients spawn visuals locally from that data. "Visuals outlive data" ([Projectiles Essentials](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials), [Projectiles Advanced](https://doc.photonengine.com/fusion/current/technical-samples/projectiles-advanced)). It is deterministic from `(fireTick, origin, dir)`, so it replays well. [Single-source but very practical]
3. **Predicted spawn with later matching** (projectile is a networked object):
   - lightyear *prespawning*: the client and server run the same spawn system. The client hashes "components and the tick at which it was spawned"; when the server entity arrives it is matched; unmatched prespawns are despawned on a timeout ([prespawning.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prespawning.md)).
   - NfE: predicted spawns are classified against server ghosts within ±5 ticks by default, and can optionally roll back "to spawn tick" when others reconcile ([ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs), [prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)).
   - netfox "responsive projectiles": spawn locally and request the server. The server accepts or rejects ("If the client requests a projectile with an unlikely state, it will be rejected"), with a warning that over-strict tolerance rejects legitimate shots ([network-weapon.md](https://github.com/foxssake/netfox/blob/main/docs/netfox.extras/guides/network-weapon.md)).
   - FishNet supports predicted spawning ([Predicted Spawning](https://fish-networking.gitbook.io/docs/guides/features/networked-gameobjects-and-scripts/spawning/predicted-spawning); details not fetched).
   - Source simulates client-created predicted entities only on new commands ([prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp)).
   - PurrNet uses stable `PredictedObjectID`s in state so objects created during rollback resolve consistently ([best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md)).
4. **Don't predict; anticipate.** Play the throw animation immediately and wait for the server's grenade. NGO recommends this when "your simulation can be not deterministic enough … If I throw a grenade client side, I want to make sure the grenade's trajectory is the same server side" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).

**On mispredict** [Consensus]:
- *Spawned but server didn't*: destroy after a timeout (lightyear, NfE). Fade or "fizzle" instead of popping.
- *Server spawned but client didn't* (for example the client thought it was out of ammo): treat it as a normal remote spawn, starting at the server's current position.
- *Both spawned but positions differ*: hand over to the server object and smooth the visual offset ([04 §2](04-Smoothing-and-Camera.md#2-error-offset-and-decay)).
- **Deterministic IDs** (owner id + fire tick + per-tick counter) make matching trivial and avoid hash collisions. lightyear supports a salt for "entities spawned on the same tick by different players" ([prespawning.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prespawning.md)).
- **Predicted despawn** (projectile hits a wall): lightyear's option is to remove synced components but keep the entity, so that a rollback can restore it ([prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)). The simple alternative is to hide locally and let the server despawn.

## 4. Abilities with world consequences: predict, anticipate, or wait

- NGO's rule of thumb: "Can the server correct me on this? If it can, use server authority." For invulnerability-type abilities, anticipate (play the animation) and wait for the server, because "if a sniper shoots me … the client will receive both the sniper shot and the invulnerability messages on the same timeline" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)). [Single-source framing; widely echoed]
- Overwatch exceptions favour the defender for some abilities (dev video via [PCGamesN](https://www.pcgamesn.com/overwatch/cute-stickers-and-little-cups-help-explain-the-overwatch-netcode), secondary).

## 5. Predicted player vs non-predicted / remote objects

**Moving platforms / elevators**
- **Simulate the player relative to the platform (store ground entity + local offset in state), and predict the platform from a deterministic path/time if possible.** [Consensus by example]
  - Quake 3 adjusts the predicted position by the mover's motion between times (`CG_AdjustPositionForMover`, "Also called by client movement prediction code") ([cg_ents.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_ents.c), [cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)).
  - UE CMC sends the movement base and the location *relative to a dynamic base* in `ServerMove` ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)).
  - FishNet's character-controller demo includes a `MovingPlatform` script ([FishNet demos](https://github.com/FirstGearGames/FishNet/tree/main/Assets/FishNet/Demos/Prediction/CharacterController/Scripts)).
  - *Why*: a platform that is interpolated (in the past) while the player is predicted (in the future) causes constant corrections and sliding. Platforms driven by `f(serverTick)` can be evaluated at any tick, including during replay.

**Pushing / collision with other players**
- Remote players are interpolated ~RTT + buffer in the past, so a predicted local player colliding with them will mispredict ([05 §7](05-Remote-Entities-Interpolation.md#7-interaction-between-the-predicted-local-player-and-interpolated-remotes)). Common mitigations, roughly by simplicity:
  1. No hard player-player collision, or soft separation only (many FPS) *(unverified as a cited norm)*.
  2. Collide only on the server, with weak depenetration on clients. UE CMC uses `MaxDepenetrationWithPawn` = 100 for the authority but **2** "AsProxy" ([CMC code copy](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)). [Single-source]
  3. Accept corrections and smooth them.
- **Other players pushing *you*** (knockback, explosions) can't be predicted: let the reconcile apply it and smooth ([04](04-Smoothing-and-Camera.md)). Gaffer: "only when the player's character is affected by something external to the local player's input … will the player's position need to be corrected" ([What every programmer…](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)).

**Interacting with server objects** (doors, pickups, buttons)
- Send intent in input; anticipate visually; let the server decide. NGO "controlled desyncs" and "action anticipation" ([dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md)).
- NfE partial-rollback caveat: if a predicted object changes a non-rolled-back one, prefer that "ghost A update ghost C's position" rather than C updating itself ([prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md)).

**Rigidbody props**
- Pushing physics props with a predicted kinematic character is a classic source of jitter. Options: don't predict props (server-only, interpolated), or predict a few nearby props. Mirror's "PredictedRigidbody" approach is designed for players interacting with a few objects at a time ([Mirror CSP doc](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)). *Overkill for an FPS whose props are cosmetic.*

---

## Sources
- Valve Source SDK 2013 – [prediction.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/prediction.cpp), [in_main.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/client/in_main.cpp), [usercmd.h](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/shared/usercmd.h), [player.cpp](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/game/server/player.cpp)
- Quake III Arena – [cg_ents.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_ents.c), [cg_predict.c](https://github.com/id-Software/Quake-III-Arena/blob/master/code/cgame/cg_predict.c)
- Glenn Fiedler – [What every programmer needs to know about game networking](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/)
- Unity NfE – [prediction-details.md](https://github.com/needle-mirror/com.unity.netcode/blob/master/Documentation~/prediction-details.md), [ClientServerTickRate.cs](https://github.com/needle-mirror/com.unity.netcode/blob/master/Runtime/ClientServerWorld/ClientServerTickRate.cs)
- Unity NGO docs – [dealing-with-latency.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/dealing-with-latency.md), [ticks-and-update-rates.md](https://github.com/Unity-Technologies/com.unity.multiplayer.docs/blob/main/docs/learn/ticks-and-update-rates.md)
- PurrNet – [best-practices.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/best-practices.md), [predicted-timer-module.md](https://github.com/PurrNet/PurrDocs/blob/main/client-side-prediction/predicted-modules/predicted-timer-module.md)
- lightyear – [prespawning.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prespawning.md), [prediction.md](https://github.com/cBournhonesque/lightyear/blob/main/book/src/concepts/advanced_replication/prediction.md)
- netfox – [network-weapon.md](https://github.com/foxssake/netfox/blob/main/docs/netfox.extras/guides/network-weapon.md)
- FishNet – [Predicted Spawning](https://fish-networking.gitbook.io/docs/guides/features/networked-gameobjects-and-scripts/spawning/predicted-spawning), [CharacterController demo scripts](https://github.com/FirstGearGames/FishNet/tree/main/Assets/FishNet/Demos/Prediction/CharacterController/Scripts)
- Photon Fusion – [Projectiles Essentials](https://doc.photonengine.com/fusion/v2/technical-samples/projectiles-essentials), [Projectiles Advanced](https://doc.photonengine.com/fusion/current/technical-samples/projectiles-advanced)
- Epic – [CMC code copy (gist)](https://gist.github.com/NoirMorilec/1ebe1a6f742c193101265a928b90c897)
- Mirror – [client-side-prediction.md](https://github.com/MirrorNetworking/MirrorDocs/blob/main/manual/general/client-side-prediction.md)
- Overwatch secondary – [Edgegap](https://edgegap.com/blog/game-backend-deep-dive-overwatch-2016-netcode-architecture-rollback), [PCGamesN](https://www.pcgamesn.com/overwatch/cute-stickers-and-little-cups-help-explain-the-overwatch-netcode)
