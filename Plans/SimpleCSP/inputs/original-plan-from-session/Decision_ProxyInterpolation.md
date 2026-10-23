# Design Decision: Proxy Interpolation Strategy

## Overview
"Proxy" or "Observer" interpolation deals with how a client views a predicted object that they do **not** own (e.g., viewing other players). Since the observer does not have the input data ahead of time, how do we make the movement look smooth despite network jitter and packet loss?

---

## Approach A: Snapshot Interpolation (Delay Buffer)
The observer receives authoritative states from the server. Instead of rendering them immediately, the observer buffers them and intentionally delays the rendering by a small amount (e.g., `RenderTime = ServerTime - 100ms`). It then lerps between the two states that bracket the `RenderTime`.

### Pros
- **Guaranteed Smoothness**: By delaying the render, the system guarantees it always has a "future" point to interpolate towards, completely eliminating stutter caused by jitter.
- **Zero Prediction Code for Observers**: The observer doesn't need to run `ProcessInput` or interact with the physics engine. It just mathematically lerps a position. Extremely cheap on the CPU.
- **Tolerates Packet Loss**: If a packet is lost, the buffer usually still has enough data (or the delay allows time for the next packet to arrive) to interpolate across the gap smoothly.

### Cons
- **Built-in Delay**: Other players are rendered slightly in the past (e.g., 100ms behind). In a competitive shooter, this means when you shoot exactly where a player is on your screen, they may have actually moved on the server. (This is usually solved with Server-Side Lag Compensation).

### Rating: 9 / 10
*(The absolute standard for most multiplayer games, offering the best visual experience for the lowest CPU cost).*

---

## Approach B: Extrapolation (Dead Reckoning)
When a state arrives, the observer calculates the velocity, applies it immediately, and continues moving the object in that direction until a new state arrives.

### Pros
- **Closer to Real-time**: Removes the artificial delay. The visual representation is much closer to the server's present time.

### Cons
- **Erratic Jumps (Rubber-banding)**: Players rarely move in straight lines at constant speeds. If a player stops moving, the observer will continue extrapolating them forward until the next packet arrives, at which point it violently snaps them backward.
- **Visually Disorienting**: Constant micro-stutters and corrections make tracking targets impossible.

### Rating: 3 / 10
*(Only useful for highly predictable objects with massive inertia, like large ships or vehicles, terrible for characters).*

---

## Approach C: Full Prediction for Observers (Lockstep/Input-sharing)
The server broadcasts the inputs of every player to every other player. Observers run the exact same `PredictionBehaviour` logic for all players.

### Pros
- **Perfect Sync**: If done right, everyone sees exactly what is happening in real-time.

### Cons
- **Immensely Complex**: What happens if an input packet drops? The observer's simulation falls out of sync and has to roll back every other player in the game.
- **CPU Heavy**: Every client is simulating the logic and physics of every other client in the game.

### Rating: 2 / 10
*(Used in fighting games or RTS games (lockstep), but completely unsuitable for a simple general-purpose CSP plugin).*

---

## Recommendation for Simple CSP
**Go with Approach A (Snapshot Interpolation)**. 
It is robust, performant, and simple to implement. We will create a `RemoteInterpolator` component that handles this automatically for any `PredictionBehaviour` when `!HasAuthority` and `IsClient` are true.
