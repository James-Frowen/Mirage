# Design Decision: FixedUpdate vs Custom Tick

## Overview
A Client-Side Prediction (CSP) system operates by running a deterministic logic loop (a "Tick"). The client ticks ahead of the server to mask latency. The fundamental architectural question is: do we tie our tick system to Unity's native `FixedUpdate` physics loop, or do we build a custom standalone `TickRunner`?

---

## Approach A: Native Unity `FixedUpdate`
Use Unity's built-in `FixedUpdate` as the network tick. The tick rate is strictly dictated by `Time.fixedDeltaTime`.

### Pros
- **Dead Simple Integration**: Developers are already used to `FixedUpdate` for physics and rigidbodies. Code placed here natively syncs with Unity's internal engine.
- **Native Physics Harmony**: Unity runs its internal `Physics.Simulate()` immediately after all `FixedUpdate` calls. This means we can predict rigidbodies simply by applying forces in `FixedUpdate` without needing to manually step the entire physics scene.
- **Zero Boilerplate**: No need to write a custom time-accumulator loop or deal with `Update` vs `FixedUpdate` timing mismatch bugs.

### Cons
- **Rigid Tick Rate**: The network tick rate *must* match the physics tick rate (often 50Hz or 60Hz). You cannot run the network at 20Hz while keeping physics at 60Hz.
- **No Native Time Dilation**: To keep the client and server clocks smoothly synchronized, CSP systems often speed up or slow down the client clock by 1% (Time Dilation). Unity's `Time.fixedDeltaTime` is global. Changing it to dilate time affects all physics in the scene, not just the predicted object.
- **Hard to "Catch Up"**: If the client needs to rapidly simulate 5 missed ticks (e.g., after a massive lag spike), it cannot easily run `FixedUpdate` 5 times in a single frame without forcing a global `Physics.Simulate()` call, which moves *everything* in the game world ahead.

### Rating: 7 / 10
*(Great for a "Simple CSP" system that prioritizes ease-of-use and single-rigidbody kinematics, but struggles with advanced clock synchronization).*

---

## Approach B: Custom `TickRunner` / `PredictionClock`
Create a custom loop that accumulates `Time.deltaTime` and manually fires a `Tick()` event at a designated rate.

### Pros
- **Decoupled Rates**: You can run prediction networking at 20Hz, saving bandwidth, while rendering or local physics run at higher rates.
- **Easy Time Dilation**: To sync client/server, you simply add or subtract small amounts of time to your accumulator.
- **Fast-Forwarding (Catch Up)**: You can easily run `while (timeAccumulator > tickRate) { Tick(); }` multiple times in a single frame to catch up without affecting the global Unity Physics engine.

### Cons
- **Physics Nightmare**: If your predicted object is a Rigidbody, you cannot easily move it using forces in a custom tick. Unity's physics will interpolate/simulate on its own schedule. To fix this, you must set Unity to `Physics.autoSimulation = false` and manually call `Physics.Simulate()` during your custom tick. This is incredibly complex and affects non-networked objects.
- **Complexity**: Requires building and maintaining a reliable time accumulator that avoids floating-point drift.

### Rating: 8 / 10
*(The industry standard for AAA FPS games, but directly opposes the goal of making a "Simple" plugin because of the Unity Physics integration headache).*

---

## Recommendation for Simple CSP
**Go with Approach A (`FixedUpdate`)**. 
Since the goal of this system is simplicity and independent per-object prediction, battling Unity's physics engine is out of scope. Using `FixedUpdate` allows developers to easily apply velocity to a `Rigidbody` or `CharacterController` with zero setup. We can handle slight clock drifts by snapping the client tick rather than smoothly dilating, relying on the `PredictionVisualSmoother` to hide any resultant judder.
