# Simple Client-Side Prediction (CSP) Plan

The goal is to create a lightweight, per-object Client-Side Prediction (CSP) system for Mirage. It operates on the principle that the client owning an object simulates its movement ahead of time, sends inputs to the server, and the server validates and broadcasts authoritative states. If a misprediction occurs, the client seamlessly rewinds and replays its inputs.

The focus here is on **simplicity and autonomy**. Instead of a massive global simulation rollback, each predicted object manages its own history and reconciliation independently.

## User Review Required

> [!IMPORTANT]
> **Key Design Decisions to Review**:
> 1. **FixedUpdate vs Custom Tick**: The plan proposes running the prediction step inside Unity's standard `FixedUpdate` loop. This is much simpler than managing a custom `TickRunner` clock, as it aligns perfectly with Unity physics.
> 2. **Generic Base Class**: We propose using an abstract `PredictionBehaviour<TInput, TState>` class. Developers inherit from this and define their struct types. This prevents boxing/allocations while maintaining strong typing. 
> 3. **Proxy Interpolation Strategy**: For other clients (non-owners), the plan relies on simple state interpolation with a slight delay buffer, rather than simulating inputs on their end.

## Open Questions

> [!NOTE]
> - Do you want to support predicting object creation (spawning) as part of this system, or keep it strictly for continuous movement/state of already spawned objects? (Recommending the latter for simplicity).
> - How much manual control do you want over the "Redundant Input History" size? (E.g., packing the last 3 ticks vs 5 ticks in the unreliable input RPC).

---

## Proposed Architecture & Components

The system is built on a few independent, self-contained components.

### 1. Data Structures
- **`RingBuffer<T>`**: A zero-allocation circular array used to store the history of `TInput` and `TState`. It will have a fixed size (e.g., 64 or 128 ticks). It uses the current tick number modulo the buffer size to index data.

### 2. `PredictionBehaviour<TInput, TState>`
This is the core abstract `NetworkBehaviour` that developers will inherit from. It handles the boilerplate of buffering, network RPCs, and checking authority.

**Abstract Methods the Developer Must Implement**:
- `TInput GetInput()`: Reads Unity input and returns it.
- `TState ProcessInput(TInput input, TState previousState)`: The deterministic logic. Returns the new state based on input.
- `void ApplyState(TState state)`: Applies the state to the Unity Transform/Rigidbody.
- `TState GatherState()`: Collects the current state from the Transform/Rigidbody.
- `bool CompareState(TState a, TState b)`: Checks if the difference between the authoritative state and the predicted state is within an acceptable epsilon.

**Internal Responsibilities**:
- Maintains `RingBuffer<TInput>` and `RingBuffer<TState>`.
- Calls `GetInput` and `ProcessInput` during `FixedUpdate` (if Owner).
- Sends `[ServerRpc(channel = Channel.Unreliable)]` containing the current tick and an array of the last N inputs.
- Receives `[ClientRpc(channel = Channel.Unreliable)]` containing the authoritative state and tick.
- Handles the **Rollback and Replay** logic if a misprediction is detected.

### 3. Visual Smoothing (`PredictionVisualSmoother`)
A separate, optional component attached to the visual representation of the object (e.g., the mesh child of the physics root).
- **Why?** When a misprediction occurs, the physics root is instantly snapped to the correct position (Rollback). This causes a visual stutter.
- **How it works:** This component detects the distance of the snap. It detaches visually and smoothly interpolates its local offset back to zero over a few frames (e.g., `Update` loop via a decay function), completely hiding the server correction from the player.

### 4. Remote Interpolator (`RemoteInterpolator`)
A separate component for clients who observe the object but do not own it (Observers).
- Instead of running prediction, this component maintains an interpolation buffer of received authoritative states.
- It slightly delays rendering (e.g., by 100ms) to ensure it always has a "next" state to safely interpolate towards, ensuring smooth movement for other players even if packets are dropped.

---

## Network Message Flow

### 1. Client (Owner) to Server
- **Tick Generation:** The client maintains a local `CurrentTick`.
- **Payload:** On every `FixedUpdate`, the client sends an unreliable `ServerRpc`. The payload contains:
  - `uint Tick`: The tick number of the newest input.
  - `TInput[] Inputs`: A small array (e.g., length 3) containing `[Tick]`, `[Tick - 1]`, and `[Tick - 2]`. This redundancy guarantees that even if a packet is dropped, the server can recover the missing inputs from the next packet.

### 2. Server to Clients
- **Server Tick:** The server processes inputs on its own `FixedUpdate`.
- **Payload:** After processing, the server sends an unreliable `ClientRpc`. The payload contains:
  - `uint Tick`: The tick that was just simulated.
  - `TState State`: The resulting authoritative state.

---

## Lifecycle & State Machine

### For the Owner Client (`IsClient && HasAuthority`)
1. **`FixedUpdate`**:
   - `input = GetInput()`
   - Save `input` to `InputBuffer[CurrentTick]`.
   - Send redundant inputs to Server.
   - `currentState = ProcessInput(input, currentState)`
   - `ApplyState(currentState)`
   - Save `currentState` to `StateBuffer[CurrentTick]`.
   - `CurrentTick++`
2. **On Receive Authoritative State (from ServerRpc)**:
   - Check if `CompareState(ServerState, StateBuffer[ServerTick])` is true.
   - If **Match**: Do nothing. Discard older history to free up memory.
   - If **Mismatch (Misprediction)**:
     - `ApplyState(ServerState)` (Snap back to reality).
     - Loop `t` from `ServerTick + 1` up to `CurrentTick - 1`:
       - `state_t = ProcessInput(InputBuffer[t], previous_state)`
       - Save `state_t` to `StateBuffer[t]`
     - `ApplyState(state_t)` (Return to present).
     - Fire an event so `PredictionVisualSmoother` knows a snap occurred.

### For the Server (`IsServer`)
1. **On Receive Inputs (from ClientRpc)**:
   - Extract the redundant inputs.
   - Insert them into the client's dedicated `ServerInputBuffer` at their respective ticks. (This fills any gaps caused by dropped packets).
2. **`FixedUpdate`**:
   - Fetch the input for the server's current processing tick from the buffer.
   - If no input exists (e.g., severe packet loss), duplicate the last known input.
   - `currentState = ProcessInput(input, currentState)`
   - `ApplyState(currentState)`
   - Send `currentState` and current tick to all clients.
   
### For the Observer Client (`IsClient && !HasAuthority`)
1. **On Receive Authoritative State (from ServerRpc)**:
   - Push the state and its timestamp into the `RemoteInterpolator` buffer.
2. **`Update`**:
   - Calculate `RenderTime = Time.time - InterpolationDelay`.
   - Find the two states in the buffer that bracket `RenderTime`.
   - Interpolate (Lerp) position/rotation and `ApplyState()`.
