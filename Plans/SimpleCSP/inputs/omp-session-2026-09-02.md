# Design client tick validation

- **Session ID:** `01a06293-cbb7-70cf-8274-94d8404093e6`
- **Timestamp:** 2026-09-02T14:44:08.247Z

---

## 👤 User

We are planning a Simple Client Side Prediction system, this folder so far has 4 markdown files about planning. Please check the 4 files and help me make the decisions about best approach. avoid complex code search and focus on the task of planning high level of how we will make this work. The goal is a simple CSP system, so please also critically think about parts of the plan that are current complex and likely part of our legacy very complex system that we are replacing. The goal is new simple and basic CSP.

---

## 👤 User

We are planning a Simple Client Side Prediction system, this folder so far has 4 markdown files about planning. Please check the 4 files and help me make the decisions about best approach. avoid complex code search and focus on the task of planning high level of how we will make this work. The goal is a simple CSP system, so please also critically think about parts of the plan that are current complex and likely part of our legacy very complex system that we are replacing. The goal is new simple and basic CSP.

---

## 🤖 Assistant

<details>
<summary>Thinking Process</summary>

Let me read the 4 markdown files in the current directory first.

</details>

---

## Toolresult

Decision_ProxyInterpolation.md
Decision_GenericBaseClass.md
Decision_FixedUpdate_vs_CustomTick.md
OverallPlan.md

---

## Toolresult

[Decision_ProxyInterpolation.md#EC8C]
1:# Design Decision: Proxy Interpolation Strategy
2:
3:## Overview
4:"Proxy" or "Observer" interpolation deals with how a client views a predicted object that they do **not** own (e.g., viewing other players). Since the observer does not have the input data ahead of time, how do we make the movement look smooth despite network jitter and packet loss?
5:
6:---
7:
8:## Approach A: Snapshot Interpolation (Delay Buffer)
9:The observer receives authoritative states from the server. Instead of rendering them immediately, the observer buffers them and intentionally delays the rendering by a small amount (e.g., `RenderTime = ServerTime - 100ms`). It then lerps between the two states that bracket the `RenderTime`.
10:
11:### Pros
12:- **Guaranteed Smoothness**: By delaying the render, the system guarantees it always has a "future" point to interpolate towards, completely eliminating stutter caused by jitter.
13:- **Zero Prediction Code for Observers**: The observer doesn't need to run `ProcessInput` or interact with the physics engine. It just mathematically lerps a position. Extremely cheap on the CPU.
14:- **Tolerates Packet Loss**: If a packet is lost, the buffer usually still has enough data (or the delay allows time for the next packet to arrive) to interpolate across the gap smoothly.
15:
16:### Cons
17:- **Built-in Delay**: Other players are rendered slightly in the past (e.g., 100ms behind). In a competitive shooter, this means when you shoot exactly where a player is on your screen, they may have actually moved on the server. (This is usually solved with Server-Side Lag Compensation).
18:
19:### Rating: 9 / 10
20:*(The absolute standard for most multiplayer games, offering the best visual experience for the lowest CPU cost).*
21:
22:---
23:
24:## Approach B: Extrapolation (Dead Reckoning)
25:When a state arrives, the observer calculates the velocity, applies it immediately, and continues moving the object in that direction until a new state arrives.
26:
27:### Pros
28:- **Closer to Real-time**: Removes the artificial delay. The visual representation is much closer to the server's present time.
29:
30:### Cons
31:- **Erratic Jumps (Rubber-banding)**: Players rarely move in straight lines at constant speeds. If a player stops moving, the observer will continue extrapolating them forward until the next packet arrives, at which point it violently snaps them backward.
32:- **Visually Disorienting**: Constant micro-stutters and corrections make tracking targets impossible.
33:
34:### Rating: 3 / 10
35:*(Only useful for highly predictable objects with massive inertia, like large ships or vehicles, terrible for characters).*
36:
37:---
38:
39:## Approach C: Full Prediction for Observers (Lockstep/Input-sharing)
40:The server broadcasts the inputs of every player to every other player. Observers run the exact same `PredictionBehaviour` logic for all players.
41:
42:### Pros
43:- **Perfect Sync**: If done right, everyone sees exactly what is happening in real-time.
44:
45:### Cons
46:- **Immensely Complex**: What happens if an input packet drops? The observer's simulation falls out of sync and has to roll back every other player in the game.
47:- **CPU Heavy**: Every client is simulating the logic and physics of every other client in the game.
48:
49:### Rating: 2 / 10
50:*(Used in fighting games or RTS games (lockstep), but completely unsuitable for a simple general-purpose CSP plugin).*
51:
52:---
53:
54:## Recommendation for Simple CSP
55:**Go with Approach A (Snapshot Interpolation)**. 
56:It is robust, performant, and simple to implement. We will create a `RemoteInterpolator` component that handles this automatically for any `PredictionBehaviour` when `!HasAuthority` and `IsClient` are true.

---

## Toolresult

[Decision_FixedUpdate_vs_CustomTick.md#6CBC]
1:# Design Decision: FixedUpdate vs Custom Tick
2:
3:## Overview
4:A Client-Side Prediction (CSP) system operates by running a deterministic logic loop (a "Tick"). The client ticks ahead of the server to mask latency. The fundamental architectural question is: do we tie our tick system to Unity's native `FixedUpdate` physics loop, or do we build a custom standalone `TickRunner`?
5:
6:---
7:
8:## Approach A: Native Unity `FixedUpdate`
9:Use Unity's built-in `FixedUpdate` as the network tick. The tick rate is strictly dictated by `Time.fixedDeltaTime`.
10:
11:### Pros
12:- **Dead Simple Integration**: Developers are already used to `FixedUpdate` for physics and rigidbodies. Code placed here natively syncs with Unity's internal engine.
13:- **Native Physics Harmony**: Unity runs its internal `Physics.Simulate()` immediately after all `FixedUpdate` calls. This means we can predict rigidbodies simply by applying forces in `FixedUpdate` without needing to manually step the entire physics scene.
14:- **Zero Boilerplate**: No need to write a custom time-accumulator loop or deal with `Update` vs `FixedUpdate` timing mismatch bugs.
15:
16:### Cons
17:- **Rigid Tick Rate**: The network tick rate *must* match the physics tick rate (often 50Hz or 60Hz). You cannot run the network at 20Hz while keeping physics at 60Hz.
18:- **No Native Time Dilation**: To keep the client and server clocks smoothly synchronized, CSP systems often speed up or slow down the client clock by 1% (Time Dilation). Unity's `Time.fixedDeltaTime` is global. Changing it to dilate time affects all physics in the scene, not just the predicted object.
19:- **Hard to "Catch Up"**: If the client needs to rapidly simulate 5 missed ticks (e.g., after a massive lag spike), it cannot easily run `FixedUpdate` 5 times in a single frame without forcing a global `Physics.Simulate()` call, which moves *everything* in the game world ahead.
20:
21:### Rating: 7 / 10
22:*(Great for a "Simple CSP" system that prioritizes ease-of-use and single-rigidbody kinematics, but struggles with advanced clock synchronization).*
23:
24:---
25:
26:## Approach B: Custom `TickRunner` / `PredictionClock`
27:Create a custom loop that accumulates `Time.deltaTime` and manually fires a `Tick()` event at a designated rate.
28:
29:### Pros
30:- **Decoupled Rates**: You can run prediction networking at 20Hz, saving bandwidth, while rendering or local physics run at higher rates.
31:- **Easy Time Dilation**: To sync client/server, you simply add or subtract small amounts of time to your accumulator.
32:- **Fast-Forwarding (Catch Up)**: You can easily run `while (timeAccumulator > tickRate) { Tick(); }` multiple times in a single frame to catch up without affecting the global Unity Physics engine.
33:
34:### Cons
35:- **Physics Nightmare**: If your predicted object is a Rigidbody, you cannot easily move it using forces in a custom tick. Unity's physics will interpolate/simulate on its own schedule. To fix this, you must set Unity to `Physics.autoSimulation = false` and manually call `Physics.Simulate()` during your custom tick. This is incredibly complex and affects non-networked objects.
36:- **Complexity**: Requires building and maintaining a reliable time accumulator that avoids floating-point drift.
37:
38:### Rating: 8 / 10
39:*(The industry standard for AAA FPS games, but directly opposes the goal of making a "Simple" plugin because of the Unity Physics integration headache).*
40:
41:---
42:
43:## Recommendation for Simple CSP
44:**Go with Approach A (`FixedUpdate`)**. 
45:Since the goal of this system is simplicity and independent per-object prediction, battling Unity's physics engine is out of scope. Using `FixedUpdate` allows developers to easily apply velocity to a `Rigidbody` or `CharacterController` with zero setup. We can handle slight clock drifts by snapping the client tick rather than smoothly dilating, relying on the `PredictionVisualSmoother` to hide any resultant judder.

---

## Toolresult

[Decision_GenericBaseClass.md#BEEE]
1:# Design Decision: Generic Base Class vs Interface/Component Architecture
2:
3:## Overview
4:How do we expose the CSP functionality to the developer? We need a way for the system to manage history buffers and execute rollbacks, while the developer provides the actual inputs and state logic.
5:
6:---
7:
8:## Approach A: Generic Base Class (`PredictionBehaviour<TInput, TState>`)
9:The developer creates a class like `public class PlayerMovement : PredictionBehaviour<MyInput, MyState>` and overrides methods.
10:
11:### Pros
12:- **Type Safety & Zero Allocation**: By defining `TInput` and `TState` as generic constraints (structs), the history buffers inside the base class (`RingBuffer<TInput>`) are strongly typed value arrays. There is zero boxing/unboxing allocation when reading or writing state.
13:- **Encapsulated Flow**: The base class entirely hides the complexity of RPCs, Tick counting, and the rollback `for` loop. The developer only sees `ProcessInput` and `ApplyState`.
14:- **Clear Contract**: It forces the developer to explicitly define what constitutes an "Input" and a "State", which naturally leads to better deterministic design.
15:
16:### Cons
17:- **Single Inheritance Limit**: The developer cannot inherit from another class (e.g., they can't inherit from `HealthBehaviour` if they also want to be a `PredictionBehaviour`). 
18:- **Slightly Verbose**: Requires defining two structs for every predicted component.
19:- **Multiple Predictors**: If an object needs to predict movement *and* weapon firing, putting them in one giant `PredictionBehaviour` state gets messy, but adding two generic components might cause RPC conflicts if not routed carefully.
20:
21:### Rating: 9 / 10
22:*(Highly performant and enforces good habits).*
23:
24:---
25:
26:## Approach B: Interface-based / Component Manager (`IPredictable`)
27:A master `PredictionManager` component sits on the object, and loops through all attached `IPredictable` components on that GameObject.
28:
29:### Pros
30:- **Modular**: A developer can attach a `MovementPredictor` and a `WeaponPredictor` as separate small components.
31:- **No Inheritance Issues**: Components just implement `IPredictable` and inherit from whatever they want.
32:
33:### Cons
34:- **Boxing/Serialization Overhead**: If the `PredictionManager` is generic, how does it store the history of *all* `IPredictable` components? It usually has to serialize the state to byte arrays (`NetworkWriter`) on every tick, or box them into `object` arrays. Both create garbage (GC spikes) or burn CPU cycles.
35:- **Complex Syncing**: Rolling back requires iterating through components in a specific deterministic order.
36:
37:### Rating: 5 / 10
38:*(Great for flexibility, terrible for CPU/Memory performance in a hot loop).*
39:
40:---
41:
42:## Approach C: Byte Array State (The Old Mirage CSP Way)
43:State is written directly to a `NetworkWriter` buffer on every tick. 
44:
45:### Pros
46:- **Highly Flexible**: Can serialize anything, including dynamically changing list sizes.
47:
48:### Cons
49:- **Very Slow**: Writing to a byte stream and reading from it 60 times a second per object, and then doing it 60 times over during a single rollback frame, completely tanks CPU performance.
50:
51:### Rating: 4 / 10
52:*(Too heavy for a modern, simple CSP).*
53:
54:---
55:
56:## Recommendation for Simple CSP
57:**Go with Approach A (Generic Base Class)**. 
58:For Unity C# networking, avoiding garbage allocation is paramount. The generic base class allows us to create zero-allocation ring buffers of structs. To solve the "multiple predictors" issue, the developer can simply compose a larger struct that contains both MovementState and WeaponState.

---

## Toolresult

[OverallPlan.md#4FDA]
1:# Simple Client-Side Prediction (CSP) Plan
2:
3:The goal is to create a lightweight, per-object Client-Side Prediction (CSP) system for Mirage. It operates on the principle that the client owning an object simulates its movement ahead of time, sends inputs to the server, and the server validates and broadcasts authoritative states. If a misprediction occurs, the client seamlessly rewinds and replays its inputs.
4:
5:The focus here is on **simplicity and autonomy**. Instead of a massive global simulation rollback, each predicted object manages its own history and reconciliation independently.
6:
7:## User Review Required
8:
9:> [!IMPORTANT]
10:> **Key Design Decisions to Review**:
11:> 1. **FixedUpdate vs Custom Tick**: The plan proposes running the prediction step inside Unity's standard `FixedUpdate` loop. This is much simpler than managing a custom `TickRunner` clock, as it aligns perfectly with Unity physics.
12:> 2. **Generic Base Class**: We propose using an abstract `PredictionBehaviour<TInput, TState>` class. Developers inherit from this and define their struct types. This prevents boxing/allocations while maintaining strong typing. 
13:> 3. **Proxy Interpolation Strategy**: For other clients (non-owners), the plan relies on simple state interpolation with a slight delay buffer, rather than simulating inputs on their end.
14:
15:## Open Questions
16:
17:> [!NOTE]
18:> - Do you want to support predicting object creation (spawning) as part of this system, or keep it strictly for continuous movement/state of already spawned objects? (Recommending the latter for simplicity).
19:> - How much manual control do you want over the "Redundant Input History" size? (E.g., packing the last 3 ticks vs 5 ticks in the unreliable input RPC).
20:
21:---
22:
23:## Proposed Architecture & Components
24:
25:The system is built on a few independent, self-contained components.
26:
27:### 1. Data Structures
28:- **`RingBuffer<T>`**: A zero-allocation circular array used to store the history of `TInput` and `TState`. It will have a fixed size (e.g., 64 or 128 ticks). It uses the current tick number modulo the buffer size to index data.
29:
30:### 2. `PredictionBehaviour<TInput, TState>`
31:This is the core abstract `NetworkBehaviour` that developers will inherit from. It handles the boilerplate of buffering, network RPCs, and checking authority.
32:
33:**Abstract Methods the Developer Must Implement**:
34:- `TInput GetInput()`: Reads Unity input and returns it.
35:- `TState ProcessInput(TInput input, TState previousState)`: The deterministic logic. Returns the new state based on input.
36:- `void ApplyState(TState state)`: Applies the state to the Unity Transform/Rigidbody.
37:- `TState GatherState()`: Collects the current state from the Transform/Rigidbody.
38:- `bool CompareState(TState a, TState b)`: Checks if the difference between the authoritative state and the predicted state is within an acceptable epsilon.
39:
40:**Internal Responsibilities**:
41:- Maintains `RingBuffer<TInput>` and `RingBuffer<TState>`.
42:- Calls `GetInput` and `ProcessInput` during `FixedUpdate` (if Owner).
43:- Sends `[ServerRpc(channel = Channel.Unreliable)]` containing the current tick and an array of the last N inputs.
44:- Receives `[ClientRpc(channel = Channel.Unreliable)]` containing the authoritative state and tick.
45:- Handles the **Rollback and Replay** logic if a misprediction is detected.
46:
47:### 3. Visual Smoothing (`PredictionVisualSmoother`)
48:A separate, optional component attached to the visual representation of the object (e.g., the mesh child of the physics root).
49:- **Why?** When a misprediction occurs, the physics root is instantly snapped to the correct position (Rollback). This causes a visual stutter.
50:- **How it works:** This component detects the distance of the snap. It detaches visually and smoothly interpolates its local offset back to zero over a few frames (e.g., `Update` loop via a decay function), completely hiding the server correction from the player.
51:
52:### 4. Remote Interpolator (`RemoteInterpolator`)
53:A separate component for clients who observe the object but do not own it (Observers).
54:- Instead of running prediction, this component maintains an interpolation buffer of received authoritative states.
55:- It slightly delays rendering (e.g., by 100ms) to ensure it always has a "next" state to safely interpolate towards, ensuring smooth movement for other players even if packets are dropped.
56:
57:---
58:
59:## Network Message Flow
60:
61:### 1. Client (Owner) to Server
62:- **Tick Generation:** The client maintains a local `CurrentTick`.
63:- **Payload:** On every `FixedUpdate`, the client sends an unreliable `ServerRpc`. The payload contains:
64:  - `uint Tick`: The tick number of the newest input.
65:  - `TInput[] Inputs`: A small array (e.g., length 3) containing `[Tick]`, `[Tick - 1]`, and `[Tick - 2]`. This redundancy guarantees that even if a packet is dropped, the server can recover the missing inputs from the next packet.
66:
67:### 2. Server to Clients
68:- **Server Tick:** The server processes inputs on its own `FixedUpdate`.
69:- **Payload:** After processing, the server sends an unreliable `ClientRpc`. The payload contains:
70:  - `uint Tick`: The tick that was just simulated.
71:  - `TState State`: The resulting authoritative state.
72:
73:---
74:
75:## Lifecycle & State Machine
76:
77:### For the Owner Client (`IsClient && HasAuthority`)
78:1. **`FixedUpdate`**:
79:   - `input = GetInput()`
80:   - Save `input` to `InputBuffer[CurrentTick]`.
81:   - Send redundant inputs to Server.
82:   - `currentState = ProcessInput(input, currentState)`
83:   - `ApplyState(currentState)`
84:   - Save `currentState` to `StateBuffer[CurrentTick]`.
85:   - `CurrentTick++`
86:2. **On Receive Authoritative State (from ServerRpc)**:
87:   - Check if `CompareState(ServerState, StateBuffer[ServerTick])` is true.
88:   - If **Match**: Do nothing. Discard older history to free up memory.
89:   - If **Mismatch (Misprediction)**:
90:     - `ApplyState(ServerState)` (Snap back to reality).
91:     - Loop `t` from `ServerTick + 1` up to `CurrentTick - 1`:
92:       - `state_t = ProcessInput(InputBuffer[t], previous_state)`
93:       - Save `state_t` to `StateBuffer[t]`
94:     - `ApplyState(state_t)` (Return to present).
95:     - Fire an event so `PredictionVisualSmoother` knows a snap occurred.
96:
97:### For the Server (`IsServer`)
98:1. **On Receive Inputs (from ClientRpc)**:
99:   - Extract the redundant inputs.
100:   - Insert them into the client's dedicated `ServerInputBuffer` at their respective ticks. (This fills any gaps caused by dropped packets).
101:2. **`FixedUpdate`**:
102:   - Fetch the input for the server's current processing tick from the buffer.
103:   - If no input exists (e.g., severe packet loss), duplicate the last known input.
104:   - `currentState = ProcessInput(input, currentState)`
105:   - `ApplyState(currentState)`
106:   - Send `currentState` and current tick to all clients.
107:   
108:### For the Observer Client (`IsClient && !HasAuthority`)
109:1. **On Receive Authoritative State (from ServerRpc)**:
110:   - Push the state and its timestamp into the `RemoteInterpolator` buffer.
111:2. **`Update`**:
112:   - Calculate `RenderTime = Time.time - InterpolationDelay`.
113:   - Find the two states in the buffer that bracket `RenderTime`.
114:   - Interpolate (Lerp) position/rotation and `ApplyState()`.

---

## 🤖 Assistant

<details>
<summary>Thinking Process</summary>

Let me analyze the four documents critically.

The user wants:
1. Help making decisions about best approach
2. Critically think about parts of the plan that are currently complex and likely part of legacy very complex system being replaced
3. Goal: new, simple, basic CSP

Let me think through each document critically:

## OverallPlan.md

Core flow: client predicts, sends inputs, server validates + broadcasts state, client reconciles on mismatch. That's classic CSP — good, simple.

Critical analysis for simplification:

1. **RingBuffer<T> with tick % size indexing** — reasonable, but is it needed as a separate component? A simple fixed-size array with a moving index IS a ring buffer. Keep it minimal — one small struct, no fancy API. Actually this is fine and simple. But maybe question: do we even need a ring buffer vs just arrays with a head index? They're the same thing. Keep it as a tiny utility. Fine.

2. **Redundant input packing (last 3 ticks in each RPC)** — This is a complexity question. For a SIMPLE system, is redundant input history needed? The plan says unreliable channel for inputs. If you send inputs unreliably and a packet drops, the server misses that tick's input. With redundancy (send last 3), one dropped packet is recovered by the next. Alternative simpler approach: send inputs reliably (TCP-like channel in Mirage? Mirage uses unreliable + reliable channels in Telepathy/websockets). Actually for CSP, inputs MUST eventually reach the server — you can't skip inputs since simulation is sequential. So options:
   - Send reliably (simple, but head-of-line blocking risk on loss — a lost packet stalls all)
   - Send unreliably with redundancy (the plan's choice — this is actually the standard and not that complex; it's just an array of the last N inputs)
   
   The redundancy is actually pretty simple to implement: server stores latest inputs per tick, applies them in order. I'd keep it but maybe simplify: just always send last N inputs; server ignores ones it already has. That IS the simple version. Keep. But note: N=3 at 50Hz means each packet has 3 inputs — 150% overhead bandwidth. Alternative: adaptive (only send history when a gap detected) is MORE complex. Keep fixed N, make it configurable constant, default maybe 2-3. Fine.

   Actually, an even simpler consideration: Mirage's reliable channel. If using Mirror-style, sending inputs on reliable channel means packet loss stalls. For a "simple" system targeting LAN or decent connections, reliable inputs are actually acceptable and simpler. But input redundancy is genuinely simple (append array), and I'd argue it's worth keeping because packet loss with reliable channel causes input delay pileup. Hmm. But the user asked to critically find complex parts. Redundant inputs is a modest complexity with big robustness win. I'd keep it but simplify the open question: fix N as a constant (e.g., 3), don't expose user control. Answer the open question: no user control, hardcode.

3. **Server input buffer + "duplicate last input on missing"** — needed for unreliable inputs. Simple enough. Keep.

4. **`CompareState` with epsilon** — needed. Fine. Could simplify: always reconcile (apply server state and replay) vs compare-then-reconcile. Compare with epsilon avoids needless replays. Keep — it's one method. Actually even simpler: compare with default equality? No — floats need epsilon. Keep.

5. **PredictionVisualSmoother** — is this essential for v1? It's a nice-to-have. For SIMPLE, I'd mark it optional/phase 2. The snap will be visible occasionally but the system works. Actually visual smoothing is what makes CSP feel good... but it's genuinely optional and separable. Recommendation: build core first, smoother as optional add-on component. It's already designed as separate component, so just phase it.

6. **RemoteInterpolator** — needed (observers need something). Snapshot interpolation = correct choice per decision doc. Keep.

7. **Server "ProcessInput" on its own FixedUpdate** — here's a subtlety: server tick vs client tick alignment. The plan has client sending tick numbers, server having its own processing tick. There's a mismatch danger: server's FixedUpdate rate must match client's fixed rate (both use Time.fixedDeltaTime — same project settings required). The server processes input for "server's current processing tick" — but server tick and client tick are different counters. Wait, re-reading: "Fetch the input for the server's current processing tick from the buffer." The buffer is indexed by client tick numbers. So server needs to track which client tick to process next — it should just process the highest contiguous tick available, one per FixedUpdate (or catch up if behind). This needs clarification in the plan — the server should not have its own independent tick counter; it should follow the client's tick stream: process ticks in order as they arrive, at most one per FixedUpdate, and if it's behind (inputs arrived in burst), it could process multiple to catch up. Simplest: server keeps `lastProcessedTick`, each FixedUpdate processes one pending tick if available (or catch-up loop while behind). Also server→owner state messages: the state includes the tick it corresponds to, so client can compare StateBuffer[serverTick]. That's in the plan. Good.

   Simplification: drop the notion of a "server tick" entirely — server just tracks `lastProcessedTick` per object. The doc kind of implies a server tick; clarify.

8. **Timing/rate matching**: client and server must have the same fixedDeltaTime — document as requirement. Fine.

9. **State sync to observers via ClientRpc broadcast** — the server broadcasts state every FixedUpdate (50Hz) to ALL clients including owner. For owner, that's the reconciliation stream; for observers, the interpolation stream. Two different consumers of one message. Fine — or two messages? One broadcast, both consume. Simple.

   But wait — there's an important detail: interpolation timestamps. RemoteInterpolator uses "timestamp" — but server ticks are the natural timestamp. Use tick as time unit (tick * fixedDeltaTime) rather than Time.time. The plan says "Push the state and its timestamp" — should say tick. Minor fix.

10. **`[ClientRpc]`/`[ServerRpc]` on NetworkBehaviour with generics** — technical Mirage detail: Mirage weaver supports generic NetworkBehaviour? I believe Mirage does NOT support generic NetworkBehaviours directly (weaver issues). Hmm, this is a real implementation concern. Mirror had issues with generic NetworkBehaviours historically. Mirage... I recall Mirage added support for generic NetworkBehaviour? Not sure. This is a critical implementation risk: RPCs in generic classes. Workaround: non-generic core class with RPCs, generic subclass without RPCs. That's a design detail worth flagging. Let me not over-assert; flag as verification item: "verify Mirage weaver supports generic NetworkBehaviour subscripts; if not, put RPCs in non-generic base and pass buffers via generic wrapper writing to NetworkWriter/Reader". Actually the user said "avoid complex code search" — I shouldn't go dig through Mirage source. I can flag it as a risk to verify early with a spike. Actually let me think about what I know: Mirage (vis2k's Mirror fork, now Mirage) — I believe generic NetworkBehaviours are still unsupported by the weaver (as of my knowledge). Mirror does not support generic NetworkBehaviour with RPCs. So this is a real risk. The mitigation is well-known: abstract non-generic `PredictionBehaviour` (NetworkBehaviour, has the RPCs, uses NetworkWriter/Reader for payloads) + generic `PredictionBehaviour<TInput,TState>` subclass that implements serialization hooks (SerializeInput/DeserializeInput etc.). That keeps zero-alloc mostly (write structs to writer directly — NetworkWriter writes blittable structs fairly efficiently, though not truly zero-alloc... Actually Mirage NetworkWriter.WriteBlittable is zero-ish alloc). Hmm, but the decision doc's Approach C (byte arrays) was rated 4/10 as "old Mirage CSP way" — but writing to NetworkWriter for the WIRE format is unavoidable regardless; the ring buffers stay typed structs. The difference: RPC payload must be serialized to wire anyway; what matters is history buffers are typed structs, which holds in both layouts.

    So my recommendation: keep the generic API, but structure as non-generic base carrying RPCs + generic subclass. This is a known pattern. I should flag it as an implementation risk with mitigation rather than asserting Mirage can't do it. I could quickly check Mirage docs... The user said avoid complex code search, focus on planning. I could do a quick check of Mirage docs on generics? Let me consider: this is a planning task; flagging "verify weaver generic support early via a 1-hour spike; if unsupported use the non-generic base + generic subclass split" is the right planning move. Actually, I recall pretty strongly that Mirror's weaver errors on generic NetworkBehaviours ("NetworkBehaviour cannot be generic" — Mirror logs an error). Mirage forked from Mirror; likely same limitation. I'll state it as a known constraint (high confidence) with the mitigation pattern, and recommend the split regardless since it's harmless.

## Decision_FixedUpdate_vs_CustomTick.md

Recommendation: FixedUpdate. For simple CSP, agreed. Cons listed are real but acceptable. One more con not listed: FixedUpdate runs 0..N times per rendered frame depending on framerate; if the client's framerate is low (30fps) with fixed rate 50Hz, you get 1-2 fixed steps per frame — fine. If framerate is very high, multiple frames have 0 fixed steps — prediction input sampling in FixedUpdate is fine (input sampled at tick granularity). OK.

Also: both client and server MUST use the same fixedDeltaTime. Document.

Decision: A (FixedUpdate). Agreed, keep.

## Decision_GenericBaseClass.md

Recommendation A (generic base class). Agreed with the weaver caveat above. Also note: the "compose a larger struct" answer for multiple predictors is right — one prediction stream per object keeps RPC routing trivial. Enforce: one PredictionBehaviour per GameObject (document as constraint). That resolves the "multiple predictors RPC conflicts" con cleanly.

## Decision_ProxyInterpolation.md

Approach A snapshot interpolation. Agreed, standard. Simplifications for observers:
- Interpolation delay: 100ms at 50Hz = 5 ticks buffer. Could default to 2×tick interval (100ms at 50Hz? tick=20ms, 2×=40ms... hmm 100ms = 5 ticks). Keep configurable constant, default ~2-3× tick interval... The doc says 100ms which at 20ms ticks = 5 ticks. Fine as default.
- Observer side: should observers even receive via the same ClientRpc? Yes.
- Should observers run PredictionBehaviour at all? The design: PredictionBehaviour exists on the object for all; when !owner && !server, it acts as passive receiver feeding RemoteInterpolator. Fine — one component, three roles by authority. Simple.

## Now: legacy-complexity audit — things in the plan that smell like the old complex system or are over-engineered for "simple":

1. **RingBuffer as a named component with "64 or 128 ticks"** — trivial, fine. Not legacy-smell. Keep minimal.

2. **Redundant input array in RPC** — mild complexity, worth it for unreliable channel. Keep but fix N=constant. OR: simplify further — send single input unreliably; on gap, server duplicates last input; client-side reconciliation naturally fixes what the server got wrong. Wait — is redundancy actually needed? Think: input packet for tick 42 drops. Server never gets tick 42's input. Server duplicates tick 41's input for its slot. Client predicted with 42's real input → mismatch → reconcile. So the system still works without redundancy! Redundancy reduces correction frequency under loss, but the reconcile loop already handles server-side mistakes gracefully. So for SIMPLE v1: single-input unreliable RPC, server duplicates last input on gap. That's a genuine simplification with acceptable robustness (works correctly, just more frequent small corrections under packet loss). Hmm, but it's also near-trivial to add the array (send last N). The complexity isn't the wire format, it's the server-side gap-filling bookkeeping (per-tick input buffer with lastProcessedTick, applying in order, handling out-of-order arrival). That bookkeeping is needed even with single inputs (out-of-order delivery on unreliable channel). With single-input RPC: if packet arrives out of order, server must buffer it until its turn. Same bookkeeping. So redundancy costs almost nothing extra on top: same buffer, just also insert the N-1 older inputs (ignore already-processed). Keep redundancy with N as const (3), zero user-facing knobs. I'll recommend: keep, hardcode, don't expose. Actually the open question asks "how much manual control over history size" — answer: none, constant.

   Hmm, actually let me reconsider once more. For true simplicity, I lean: v1 sends a small fixed array (tick + last 3 inputs). It's ~the same code. Fine.

3. **Time dilation / clock sync** — correctly excluded already (snap instead of dilate). Good — that's legacy-complexity already rejected.

4. **PredictionVisualSmoother** — phase 2 / optional. It's separable. Core v1: snap directly. Actually a minimal smoother is ~30 lines (offset decay in Update). But it also adds "detach visual child" GameObject requirements. Mark optional component, phase 2. Keep the plan's component design (separate, optional) — just don't block v1 on it. Hmm — but wait, mispredict snaps at low frequency might be acceptable for v1. Yes: phase it.

5. **"Discard older history to free up memory" on match** — ring buffer overwrites automatically; no explicit discard logic needed. Simplify: ring buffer handles it implicitly. Minor plan cleanup.

6. **Server-side: per-client ServerInputBuffer** — the doc says "client's dedicated ServerInputBuffer" — per predicted object. Fine, it's just a ring buffer on the server object. Simple.

7. **Two structs per predicted object (TInput, TState)** — developer-facing cost, but core to the design. Keep.

8. **Missing from plan (gaps to flag)**:
   - **Ownership/authority gating**: who is allowed to send input ServerRpc — server must validate the sender owns the object (Mirage: `identity.connectionToClient == conn` check / requireAuthority). Mention.
   - **Spawning prediction**: open question — recommend: NO spawn prediction in v1 (agree with doc's recommendation). Objects must exist before prediction. Answer the open question.
   - **Initial state on late join**: observer joins mid-game — server should send current state snapshot on spawn (standard Mirage sync handles or send state on client enter). The ClientRpc broadcast will refresh them quickly anyway (50Hz stream). Note: new observer's buffer fills in ~100ms; render holds until enough samples. Mention briefly.
   - **Snapshot for reconciliation must include tick** — plan has it. Good.
   - **What if server state arrives for tick the client hasn't predicted yet** (client behind server — shouldn't happen since client leads; client tick >= server processed tick always... actually if client pauses/hitches, server may process ahead using duplicated inputs; then client receives state for tick >= its CurrentTick → just apply + fast-forward inputs it has? If client hasn't simulated that tick yet, it can apply state directly when it gets there. Edge case; mention briefly: clamp/ignore states for ticks >= CurrentTick, apply on arrival... Simplest: if ServerTick >= CurrentTick, apply state and set CurrentTick = ServerTick + 1, clear pending? Keep brief — one line in plan: handle gracefully by treating it as authoritative reset.)
   - **Determinism requirement**: ProcessInput must be deterministic — no Time.deltaTime, no physics queries that vary. Document as contract. Important: using Unity physics inside ProcessInput (raycasts etc.) is allowed but results must be deterministic-ish; true determinism of PhysX isn't guaranteed cross-machine. The doc says FixedUpdate aligns with physics — but reconciliation REPLAYS multiple ticks in one frame; physics doesn't re-run per replayed tick! That's a real correctness point: replay loop calls ProcessInput(input, state) pure-function style — so ProcessInput must be a pure state transition (no reliance on Unity's physics simulation state). If it queries rigidbody velocity, that's fine as long as state carries it. The API design (pure ProcessInput(input, prev) → state) already enforces this. Good — worth stating explicitly as the key contract.
   - **Bandwidth**: state broadcast at 50Hz to all clients per object — fine for simple; delta compression is legacy complexity, skip.

9. **`GatherState` vs `ApplyState`** — Gather needed only on server (to broadcast) — actually server computes state via ProcessInput; GatherState used to... hmm, on server, state comes from ProcessInput directly. Where's GatherState used? On the owner client to seed StateBuffer initially, and on server at spawn to capture initial state. Keep — needed for initialization. Fine. Actually the initial state handshake: server's state and client's starting state must match at spawn (client spawns → server sends initial state → client seeds buffers). Mention: initial sync step. The plan lacks an explicit "initial state handshake" step — flag it.

   Wait, actually GatherState on owner client: after ApplyState(currentState), the actual transform might differ (e.g., physics collision response moved the rigidbody). Hmm — should the client GatherState after applying (to capture collision response) and store that? If ProcessInput applies forces and Unity physics resolves collisions, the true state is post-physics. Design choice: (a) ProcessInput is fully pure/kinoematic — state is exactly what ProcessInput returns; physics only cosmetic. (b) state = post-physics gathered state. The plan lists both ProcessInput→state and GatherState. For rigidbody prediction: apply velocity in ProcessInput (which also ApplyStates?), then after physics step, GatherState to capture what physics did, store that as the tick's state. This is a subtle ordering issue: FixedUpdate order — our ProcessInput applies velocity → Unity simulates → where do we capture? Next FixedUpdate's start? That'd be tick N's result captured at tick N+1 start. This is the classic ordering dance. For SIMPLE: recommend ProcessInput to be kinematic/self-contained (character controller style movement: compute position directly). Rigidbody-with-real-physics prediction (bouncing off things) is where legacy complexity lives. v1 contract: ProcessInput must fully determine state (queries like Physics.OverlapSphere for ground check OK — deterministic enough per machine? Not cross-machine deterministic!). Hmm. Ground check via raycast — both client and server run same query, same physics world (roughly synced), results usually match; mismatch = reconcile. That's acceptable in practice — corrections handle divergence. So contract: ProcessInput(input, prevState) → newState, may ApplyState internally? No — base class calls ApplyState(new). And physics queries inside ProcessInput operate on the world as-is; replay re-queries current world (slight inaccuracy vs original tick — standard acceptable compromise in simple CSP; perfect replay would need physics history = legacy complexity). State this explicitly as accepted limitation. I think this is an important "clarity" addition to the plan, stated simply.

   Ordering with FixedUpdate + physics: base class should run at a defined script execution order — e.g., predict step early in FixedUpdate (before default), physics simulates after FixedUpdate, collisions modify rigidbody, next tick's GatherState... For v1 simplicity: state is what ProcessInput computes (position set directly / velocity set on rigidbody then... ugh). Simplest robust contract for v1: **kinematic prediction** — ProcessInput computes state purely (e.g., move = input * speed), ApplyState writes to transform/CharacterController/Rigidbody.velocity. Collisions during replay are approximated. If user wants physics-driven prediction, that's where the old complex system lived. I'll flag: v1 targets kinematic/CC movement; full rigidbody physics prediction is explicitly out of scope (that's the legacy system's territory). This is probably the single most valuable "keep it simple" decision to add.

Let me also reconsider: should server ApplyState? Yes — server is authoritative, applies to its transform so rest of game logic sees it, and observers' interpolation mirrors it. Fine.

10. **Multiple PredictionBehaviours per object** — constrain to one per GameObject for v1. Simple.

11. **RPC channel choice**: inputs + states on Unreliable — with redundancy for inputs, and states: latest state wins; a dropped state just means older interpolation data — fine. But reconciliation stream for owner: dropped state message = missed correction until next (50Hz, so 20ms later). Fine.

12. **Mirage specifics**: `[ServerRpc(requireAuthority = ...)]` — actually input RPC from owner client: owner has authority by default (client owns), requireAuthority true works since the client owns the object. Observers never call it. Good. ClientRpc includes host? Host is both — need to handle host mode! Host = server + owner client in one process: prediction with zero latency — the tick loops run once; must not double-process (server FixedUpdate + client FixedUpdate both firing on same object). Host mode handling: if IsServer && owner, skip network round-trip — just run server path... but then no prediction needed (zero latency). Simplest: host runs the authoritative path directly, no reconciliation (never mispredicts since same process... unless client-side input applied before server tick — same loop, same tick). Flag host-mode as a supported trivial path: client&server same tick in one FixedUpdate — prediction == authority, skip RPCs locally. Worth one bullet.

Also: is there an existing Mirage CSP asset? Mirage has... I don't need to research; user has legacy system being replaced ("our legacy very complex system"). Fine.

Now, per decision docs, my recommendations:

1. **FixedUpdate vs Custom Tick → A: FixedUpdate.** Agree. Add constraint: client & server fixedDeltaTime must match; document requirement. Accepted limitations: no dilation (snap), no catch-up burst (replay loop is the catch-up, minus physics re-sim — fine because ProcessInput is pure).

2. **Generic base → A: generic API, but implementation split**: non-generic abstract `NetworkBehaviour` base (holds the two RPCs, tick fields, non-generic engine) + generic `PredictionBehaviour<TInput,TState>` subclass providing typed ring buffers + (de)serialization of one input/state to NetworkWriter. Reason: Mirage weaver historically doesn't support generic NetworkBehaviours with RPCs (verify with quick spike; the split is safe either way). Developer-facing API unchanged (still inherit generic class). This is an implementation detail refinement, not a change of decision.

3. **Proxy interpolation → A: snapshot interpolation.** Agree. Use tick numbers (not Time.time) as interpolation timestamps. Buffer target = interpolation delay in ticks (default ~100ms / 2×... pick default 100ms, configurable const).

4. Answer open questions:
   - **No spawn prediction in v1.** Strictly already-spawned objects. (Agree with doc's lean.)
   - **No user control of redundancy size**: constant (e.g., 3), internal.

5. Complexity cuts for "simple v1" (legacy smells to drop/defer):
   - **PredictionVisualSmoother → optional Phase 2.** Core works without it; occasional snap acceptable.
   - No time dilation / clock sync machinery (already agreed — snap).
   - No delta compression, no interest management customizations, no physics re-simulation during replay (ProcessInput pure contract), no spawn prediction, no multi-predictor routing (one PredictionBehaviour per object; compose structs).
   - Ring buffer: plain fixed arrays with modulo — no fancy "discard history" logic (overwrite is the discard).
   - Server: no independent "server tick" — just `lastProcessedTick` following client's stream, one input per FixedUpdate, catch-up loop if behind (bounded).

6. Gaps to add to plan (small):
   - Initial state handshake at spawn (server → owner: current state + tick to seed buffers).
   - Host mode trivial path.
   - Authority validation on input RPC (Mirage requireAuthority default handles owner-only).
   - Determinism contract for ProcessInput (no Time.deltaTime, no randomness unless seeded, world queries may diverge → corrections handle it).
   - Replay re-queries current world (no physics history) — accepted limitation.
   - Late-join observer: wait until buffer has 2 samples before rendering (or hold last).
   - Edge: server state for tick ≥ client CurrentTick (client hitched) → treat as authoritative reset.

Also one more simplification thought on the **State broadcast**: server sends state every tick (50Hz). Could halve by sending every 2nd tick? No — keep simple, every tick, it's fine bandwidth-wise for small player counts.

And **CompareState epsilon**: expose as virtual with sensible default (distance threshold). Fine.

Should I write these decisions into the files? The user asked: "check the 4 files and help me make the decisions about best approach" + "critically think about parts of the plan that are currently complex". Deliverable: a decision review. I think the right move: present the decision summary + critique concisely in chat, and offer/do updates to the docs? The instruction says avoid complex code search and focus on planning. I could update the OverallPlan with the resolved decisions... The user says "help me make the decisions" — so the chat answer is the decisions. I might append a "Decisions" record? Better: give the analysis in chat, ask nothing, and offer to write it in. Actually — per my constraints: default to action, don't ask when I can act reasonably. Writing decision outcomes into the planning docs is a natural completion. But the user may want to discuss first ("help me make the decisions" — collaborative). The decisions in the docs already have recommendations; my job: confirm/adjust + critique. I'll deliver the full analysis in chat (that's the ask) and update the docs to reflect final decisions? Hmm. "Please check the 4 files and help me make the decisions about best approach" — I read this as: produce the decision. I'll give the analysis. Then also write a concise "Final Decisions" section? I think editing their docs unprompted mid-discussion is presumptuous for a planning conversation; better to present decisions clearly, then offer to bake into docs. But my system prompt pushes: default action, don't ask for confirmation. The user explicitly framed this as a planning/review task, not "edit the files". The deliverable is the decision help. I'll present it; end with offer to write decisions into the docs. Actually — one clarifying question exists in the docs (open questions) which I'm answering myself with recommendations. The user asked me to help decide — so decide, with rationale, and note what needs their sign-off (spawn prediction: no; that's my call to recommend).

There are genuinely no material user-choice tradeoffs left that I can't recommend defaults for. No `ask` needed. Deliver analysis.

Wait — one more thing to verify my claim about Mirage generic NetworkBehaviour. I'm fairly confident Mirror's weaver rejects generic NetworkBehaviours (error: "... cannot be generic"). Mirage is a fork; I believe it inherits this. But rather than assert, I'll phrase: "Mirage's weaver (like Mirror's) historically doesn't support generic NetworkBehaviours with RPCs — verify with a 5-minute spike; the split design below is safe either way." That's honest. Actually, hmm, I genuinely recall Mirage may have added generic support... Not certain. Spike recommendation is right. Also note: even if generics work, arrays of structs in RPC payloads — Mirage supports T[] params fine.

Also the RPC payload `[ServerRpc]` with `TInput[]` — generic type param in RPC signature — even if generic classes were supported, serializing generic arrays might need custom writers. The split solves this too: base class RPC takes NetworkWriter payload... wait, Mirage RPCs can't take NetworkWriter as param directly; typical pattern: RPC(byte[] payload) or use Mirage's generic support... Mirror supports passing custom structs if blittable via generated writers. For a non-generic base, RPC signature could be `void RpcServerInput(uint tick, byte[] inputPayload)`? byte[] allocs per message — for 50Hz this is GC pressure (though small). Alternative: Mirage supports `ArraySegment<byte>`? Hmm. Simplest robust: define RPC with a `NetworkWriter`-serialized struct — in Mirror you can pass any struct with a generated writer (weaver auto-generates for blittable structs). But non-generic base doesn't know TInput... 

Standard solution in Mirror-land for this exact problem (e.g., in prediction libs): the generic subclass declares the RPCs IF weaver supports generic NetworkBehaviour; else byte[] payloads. I'll keep this at the right planning altitude: "wire format: fixed-size blittable input packed into a small payload; exact mechanism (generic RPC vs byte[]) decided by a 10-minute weaver spike — doesn't change architecture." Good — that's planning-level, defers implementation detail appropriately.

Actually wait, I should double check one thing: Mirage (the org Weavers/MirageNet) — I recall an issue "Support generic NetworkBehaviour" in Mirror repo that was closed as "won't do" years ago... and some forks added it. Vis2k's Mirror definitely errors: "NetworkBehaviour %s cannot be generic" (I'm fairly sure this exists in Mirror's weaver). Mirage forked at ~2020. Likely retains. OK — phrase as above.

One more architectural simplification to consider: **does the server need to ApplyState & run ProcessInput at all, or just validate?** Server must run ProcessInput (authoritative simulation). Yes. Fine.

**Interpolation delay default**: at 50Hz tick=20ms; typical delay 2-3 ticks (40-60ms) minimum to cover jitter; 100ms is safe. Default 100ms, const. Fine — doc already says 100ms.

**The open question about input redundancy**: my answer — no manual control; constant 3.

Now structure my reply:
1. Verdict on each of the 3 decisions (agree: A, A-with-implementation-split, A) — brief.
2. Answers to the 2 open questions.
3. Legacy-complexity audit: what to cut/defer (the critical thinking they asked for): VisualSmoother→phase 2, no dilation, no physics re-sim, no spawn prediction, no multi-predictor, ring buffer trivial, no server tick counter (follow client stream).
4. Gaps the plan is missing (small list).
5. Offer: write these into the docs (or a v1 scope section).

Keep it tight and evidence-grounded. Use the file contents as references. Format: this is a chat deliverable — prose + bullets, no huge headers needed, but some structure helps. Terminal chat, markdown OK.

Length: this is a substantive planning review; aim for thorough but tight. Maybe ~60-80 lines. Let me draft.

Actually also worth stating the core "shape" of v1 in one sentence: "owner predicts in FixedUpdate, streams redundant inputs via unreliable ServerRpc; server follows the client's tick stream, simulates, broadcasts state; owner reconciles on mismatch; observers lerp behind a delay." That's the whole system.

One more critical look at the reconciliation loop in OverallPlan lines 86-95: on mismatch it applies server state, replays inputs ServerTick+1..CurrentTick-1. Correct. Note: must ALSO re-store replayed states (it does, line 93). Good. Event for smoother (line 95) — keep hook even if smoother is phase 2? A tiny event is harmless and avoids retrofit. Keep event, smoother optional. Fine.

Also line 100-103 server: "Fetch the input for the server's current processing tick" — rework to lastProcessedTick semantics + bounded catch-up. State that.

Also: state message to owner should include the *input tick* processed (same thing). And when server duplicates input on gap, the state it broadcasts corresponds to tick with duplicated input — client compares against its StateBuffer[tick] which used real input → mismatch → reconcile. All fine.

Edge: what if ClientRpc state arrives for tick whose StateBuffer slot was overwritten (very old)? Guard: if ServerTick < CurrentTick - bufferSize, ignore/soft-reset. One-line gap to add.

Late-join owner: object spawns mid-game for new player — initial state handshake covers it.

OK. Also think: is there anything in the 4 docs that is *outright wrong*? 
- OverallPlan line 43: "[ServerRpc(channel = Channel.Unreliable)] containing... an array of the last N inputs" — fine.
- Line 112: "RenderTime = Time.time - InterpolationDelay" — wrong clock: should be based on server tick timeline (tick * fixedDeltaTime), not local Time.time. Local Time.time doesn't correspond to when states were produced. Fix: maintain remote timeline from received ticks. I'll flag this as a real bug in the plan.
- Line 11 claim "aligns perfectly with Unity physics" — qualified later; fine with the purity contract.
- Decision docs ratings: fine.

Good — flagging the Time.time bug is concrete value.

Also: interpolation buffer keyed by tick; drop states older than render point (overwrite via small ring/dict). Implementation detail, skip.

Draft the response now. Keep personality: terse, conclusion first. I'll use compact sections. No need for tools beyond what I've read. No file edits unless... I'll offer at the end. Actually, should I just do the doc updates? The user said "help me make the decisions" — presenting decisions IS the deliverable; updating docs is a natural follow-up they may want after agreeing. But per "default to action" — hmm, the risk of editing: they may disagree with e.g. dropping VisualSmoother from v1 core. These are recommendations seeking their review (docs literally say "User Review Required"). I'll present and offer to bake in. That respects the review gate they set up. I think that's right — the docs explicitly mark these as decisions awaiting user review, so unilateral doc edits would bypass their stated process. But I can be decisive in recommendations.

Format check: terminal chat, markdown fine, keep tables minimal. I'll write it now.

</details>

All four read. Verdict up front: the three decision docs all point the right way — **FixedUpdate (A), generic base class (A, with one implementation caveat), snapshot interpolation (A)**. The plan is sound; the real work is cutting scope, not adding it. Here's the review.

## The three decisions

**1. Tick: FixedUpdate — agree, lock it in.** The custom TickRunner's only wins are rate decoupling, dilation, and catch-up bursts — all AAA problems. The replay loop already *is* your catch-up (it re-runs ticks in one frame without touching physics), and dilation is correctly replaced by snap + smoother. One hard requirement to document: **client and server must share the same `Time.fixedDeltaTime`** — the whole tick-number contract depends on it.

**2. Generic base class — agree on the API, one caveat on implementation.** Mirage's weaver (inherited from Mirror) has historically rejected generic `NetworkBehaviour`s with RPCs. Verify with a 10-minute spike; if it fails, the standard fix is harmless either way: non-generic abstract base (`NetworkBehaviour`, owns the two RPCs and tick engine) + generic `PredictionBehaviour<TInput, TState>` subclass owning the typed buffers and input/state (de)serialization. Developer-facing API is unchanged — still inherit one generic class. This doesn't alter the decision, just the class layout.

**3. Proxy interpolation: Approach A — agree.** One actual bug in `OverallPlan.md` line 112: `RenderTime = Time.time - InterpolationDelay` is the wrong clock. Local `Time.time` has no relationship to when the server produced states. Use the **tick number as the timestamp** (`tick * fixedDeltaTime`), interpolate on that timeline. Otherwise the plan is right.

## Answers to the open questions

- **Spawn prediction: no.** Strictly already-spawned objects. Spawn prediction drags in object lifetime, ownership handoff, and spawn-mismatch reconciliation — that's the legacy system's territory.
- **Redundant input history size: no user control.** Hardcode a constant (3). It's not a tuning knob anyone should touch; exposing it just adds documentation burden.

## Legacy-complexity audit (what to cut or defer)

1. **`PredictionVisualSmoother` → Phase 2, optional.** It's already designed as a separate component, so deferring costs nothing. Core CSP works without it — you just occasionally see a snap. Keep the misprediction *event hook* in v1 so it doesn't need retrofitting.
2. **No physics re-simulation during replay.** The `ProcessInput(input, previousState) → TState` signature is a pure function — that's the single most important simplification in the plan, and it should be stated as *the contract*: replay never re-runs Unity physics, it only re-runs your logic. Corollary: **v1 targets kinematic/CharacterController-style movement**. Full rigidbody-force prediction (collision responses inside the predicted step) is exactly where the old system's complexity lives. Explicitly out of scope.
3. **Drop the "server tick."** `OverallPlan.md` lines 101–103 imply the server has its own tick counter — that's a second clock to keep in sync, i.e., legacy complexity. The server just tracks `lastProcessedTick` *per object* and follows the client's stream: apply the next buffered input each `FixedUpdate`, bounded catch-up loop if behind. One clock in the system: the owner's.
4. **No "discard history to free memory" (line 88).** A ring buffer overwriting old slots *is* the discard. Delete that step — explicit pruning logic is waste.
5. Already correctly excluded, keep it that way: time dilation, delta compression, interest-management hooks, multi-predictor RPC routing (enforce **one `PredictionBehaviour` per GameObject**; compose structs as the decision doc says).
6. **`RingBuffer<T>`**: keep, but it's just a fixed array + modulo index. No growable, no rich API. If it grows features, it's legacy creep.

## Gaps the plan doesn't cover (small, all cheap)

- **Initial state handshake**: on spawn, server sends current state + tick to the owner so buffers seed correctly. Without it, the first reconciliation is guaranteed garbage.
- **Host mode**: server and owner in one process — prediction is meaningless at zero latency. Trivial path: host runs the authoritative loop directly, skips RPC round-trip and reconciliation.
- **Authority validation**: input `ServerRpc` must only accept the owner (Mirage's `requireAuthority` default covers this — just don't disable it).
- **Stale/garbage guards**: ignore server states for `ServerTick < CurrentTick - bufferSize` (slot overwritten) and treat `ServerTick >= CurrentTick` (client hitched, server ran ahead on duplicated inputs) as an authoritative reset.
- **Late-join observers**: hold rendering until the interpolation buffer has 2 samples (~2 ticks after join). One `if`.
- **Determinism contract for `ProcessInput`**: no `Time.deltaTime`, no unseeded randomness. World queries (raycast ground checks) may diverge between machines — that's fine, corrections are the safety net. Say so explicitly so users don't panic when a correction fires.

## The whole v1 in one sentence

Owner predicts in `FixedUpdate`, streams the last 3 inputs via unreliable `ServerRpc`; server follows the client's tick stream, simulates, broadcasts `{tick, state}` via unreliable `ClientRpc`; owner compares against its buffer and replays on mismatch; observers lerp ~100ms behind on the tick timeline. Phase 2: visual smoother. Everything else in the old system: not ours.

If you're happy with these calls, I'll bake them into the docs — resolve the three decision files as final, answer the open questions, fix the `Time.time` bug and the server-tick section in `OverallPlan.md`, and add a short "v1 scope / out of scope" list.

---

## 👤 User

I am unsure if using unity' Time.FixedUpdateTime is a good idea as it will not be correctly synced between server/host/client, if any of them have frame lag FixedUpdates might be skipped causing issues in the frame times. Because of that we need a unity independant timer, one old complex system had a TickRunner, the server side is fairly simple, but the client side to keep it in sync is tricky to get right and deal with edge caes, including how to smoothly update time on join, how to update time (and rest of state) when client runs too far out of sync.  Focusing just this timing issue and Reply with suggestions and best practice, weight the ideas and compare complexity vs what is required for good quality.  Here is the old complex TickRunner file : <attachment>
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.Assertions;

#if CLIENT_TICK_RUNNER_VERBOSE
using System.IO;
#endif

namespace JamesFrowen.CSP
{
    public delegate void OnTick(int tick);


    public class PredictionTime : IPredictionTime
    {
        private readonly TickRunner _runner;

        public PredictionTime(TickRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public float FixedDeltaTime => _runner.FixedDeltaTime;
        public double UnscaledTime => _runner.UnscaledTime;
        public float FixedTime => Tick * FixedDeltaTime;
        public double Time => _runner.Time;
        public double DeltaTime => _runner.DeltaTime;

        public int Tick { get; set; }
        public bool IsResimulation { get; set; } = false;
        public UpdateMethod Method { get; set; } = UpdateMethod.None;
    }
    public class TickRunner
    {
        private static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public float TickRate = 50;

        /// <summary>
        /// Max milliseconds per frame to process. Wont start new Ticks if current frame is over this limit.
        /// <para>
        /// This can avoid freezes if ticks start to take a long time.
        /// </para>
        /// <para>
        /// The runner will try to run <see cref="TickRate"/> per second, but if they take longer than 1 second then each frame will get longer and longer.
        /// This limit will stops extra ticks in that frame from being processed, allowing other parts of the applications (eg message processing).
        /// <para>
        /// Any stopped ticks will run next frame instead
        /// </para>
        /// </para>
        /// </summary>
        public float MaxFrameTime = 200;

        /// <summary>
        /// Limit number of ticks per frame
        /// <para>
        /// This is to avoid running too long per frame. Higher number will catch up faster, but will cause longer frame times
        /// </para>
        /// </summary>
        public int MaxTickPerFrame = 5;

        protected int _tick;
        protected double _time;
        protected double _deltaTime;

        /// <summary>
        /// Used by client to keep up with server
        /// <para>always 1 on server</para>
        /// </summary>
        public float TimeScaleMultiple { get; protected set; } = 1;

        private readonly Stopwatch stopwatch;
        private double tickTimer;
        private double lastFrame;
        private bool _isRunning;

        /// <summary>
        /// keep track of last tick invoked on event, incase client jumps to line up with server
        /// </summary>
        protected int lastInvokedTick;


        /// <summary>
        /// Called once a frame, before any ticks
        /// </summary>
        public event Action BeforeAllTicks;

        /// <summary>
        /// Main tick update event, Called before <see cref="Tick"/>
        /// </summary>
        public event OnTick BeforeTick;
        /// <summary>
        /// Main tick update event
        /// </summary>
        public event OnTick OnTick;
        /// <summary>
        /// Late tick update event, Called after <see cref="Tick"/>
        /// </summary>
        public event OnTick AfterTick;

        /// <summary>
        /// Called every frame, after all ticks (even if there were no ticks run this frame)
        /// </summary>
        public event Action AfterAllTicks;

        public TickRunner()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public virtual bool IsRunning
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _isRunning;
        }

        public float FixedDeltaTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 1f / TickRate;
        }

        public int Tick
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _tick;
        }

        public double Time
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _time;
        }
        public double DeltaTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _deltaTime;
        }


        public double UnscaledTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => stopwatch.Elapsed.TotalSeconds;
        }

        public void SetRunning(bool running)
        {
            _isRunning = running;
        }

        private double GetCurrentTime()
        {
            return stopwatch.Elapsed.TotalSeconds;
        }

        public virtual void OnUpdate()
        {
            var now = GetCurrentTime();
            var startTick = _tick;
            var max = now + (MaxFrameTime / 1000f);
            var delta = now - lastFrame;
            lastFrame = now;

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                return;
#endif
            // store last frame above even if we are not running
            // so that when we start running again the delta will not be huge
            if (!_isRunning)
                return;

            var timeDelta = delta * UnityEngine.Time.timeScale * TimeScaleMultiple;

            _time += timeDelta;
            _deltaTime = timeDelta;
            tickTimer += timeDelta;

            BeforeAllTicks?.Invoke();

            while (tickTimer > FixedDeltaTime)
            {
                tickTimer -= FixedDeltaTime;
                _tick++;

                // only invoke is tick is later, see lastInvokedTick
                // todo what if we jump back, do we not need to resimulate?
                if (_tick > lastInvokedTick)
                {
                    BeforeTick?.Invoke(_tick);
                    OnTick?.Invoke(_tick);
                    AfterTick?.Invoke(_tick);
                    lastInvokedTick = _tick;
                }

                // todo improve this. Maybe have a max for tickTimer incase we get too far ahead of it.
                //      eg if we are slow for a few frames, and get 200 ticks behind, we could maybe drop 120 frames and continue from there?
                //      would we need to tell client about this

                // todo should we reset tickTimer if we stop the while loop? otherwise next frame might also be long
                if (GetCurrentTime() > max)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Took longer than {MaxFrameTime}ms to process frame. Processed {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f}ms");
                    break;
                }

                if (_tick > startTick + MaxTickPerFrame)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Reached max ticks per frame ({MaxTickPerFrame}). Time taken {(GetCurrentTime() - now) * 1000f}ms");

                    // todo check if resetting this is bad
                    // in single player mode it should be fine as we will just continue as normal from new time
                    // in multiplayer it might cause syncing problems 
                    tickTimer = 0;
                    break;
                }
            }

            if (logger.LogEnabled()) logger.Log($"TickRunner (tick={_tick}): {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f}ms");



            AfterAllTicks?.Invoke();
        }

        // have this virtual methods here, just so we have use 1 field for TickRunner.
        // we will only call this method on client so it should be a ClientTickRunner
        public virtual void OnMessage(int serverTick, double clientTime) => throw new NotSupportedException("OnMessage is not supported for default tick runner. See ClientTickRunner");
    }

    public class ClientTickRunner : TickRunner
    {
        private static readonly ILogger logger = LogFactory.GetLogger<ClientTickRunner>();

        // this number neeeds to be less than buffer size in order for resimulation to work correctly
        // this number will clamp RTT average to a max value, so it should recover faster after RTT is back to normal
        private const float MAX_RTT = 1.0f;
        // ring buffers are 64, so set 60 as max to be safe
        // todo make this a field, not const
        private const int MAX_TICK_DELAY = 60;
        private readonly MovingAverage _RTTAverage;
        private readonly float _fastScale;
        private readonly float _normalScale = 1f;
        private readonly float _slowScale;
        private readonly float _positiveThreshold;
        private readonly float _negativeThreshold;
        private readonly float _skipAheadThreshold;
        private bool _initialized;
        private int _latestServerTick;

        //public float ClientDelaySeconds => ClientDelay * FixedDeltaTime;

#if DEBUG
        public float Debug_DelayInTicks { get; private set; }
        public MovingAverage Debug_RTT => _RTTAverage;
#endif

#if CLIENT_TICK_RUNNER_VERBOSE
        private StreamWriter _writer;
#endif

        /// <summary>
        /// Invoked at start AND if client gets too get away from server
        /// </summary>
        public event Action OnTickSkip;

        /// <param name="diffThreshold">how many ticks off the client time can be before changing speed, In ticks</param>
        /// <param name="timeScaleModifier">how much to speed up/slow down by is behind/ahead</param>
        /// <param name="skipThreshold">skip ahead to server tick if this far behind</param>
        /// <param name="movingAverageCount">how many ticks used in average, increase or decrease with framerate</param>
        public ClientTickRunner(float diffThreshold = 1.5f, float timeScaleModifier = 0.01f, float skipThreshold = 10f, int movingAverageCount = 100)
        {
            // IMPORTANT: most of these values are in tick NOT seconds, so careful when using them

            // if client is off by 0.5 then speed up/slow down
            _positiveThreshold = diffThreshold;
            _negativeThreshold = -_positiveThreshold;

            // skip ahead if client fall behind by this many ticks
            _skipAheadThreshold = skipThreshold;

            // speed up/slow down up by 0.01 if after/behind
            // we never want to be behind so catch up faster
            _fastScale = _normalScale + (timeScaleModifier * 5);
            _slowScale = _normalScale - timeScaleModifier;

            _RTTAverage = new MovingAverage(movingAverageCount);

#if CLIENT_TICK_RUNNER_VERBOSE
            try
            {
                _writer = new StreamWriter(Path.Combine(Application.persistentDataPath, "ClientTickRunner.csv")) { AutoFlush = true };
            }
            catch (IOException e)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"Fail to create ClientTickRunner.csv because: {e}");
                // clear ref just incase. It will stop Debug() from trying to write 
                _writer = null;
            }
            Debug("serverTick,serverGuess,localTick,delayInTicks,delayInSeconds,delayFromLag,delayFromJitter,diff,newRTT,intialized");
#endif
        }

        public void ResetTime()
        {
            _RTTAverage.Reset();
            _initialized = false;
        }

        public override bool IsRunning => base.IsRunning && _initialized;

        public override void OnUpdate()
        {
            // only update client tick if server has sent first state
            if (_initialized)
                base.OnUpdate();
        }

        private bool CheckOrder(int serverTick)
        {
            if (serverTick <= _latestServerTick)
            {
                logger.LogError($"Received message out of order server:{_latestServerTick}, new:{serverTick}");
                return false;
            }
            _latestServerTick = serverTick;
            return true;
        }

        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public override void OnMessage(int serverTick, double clientSendTime)
        {
            if (!CheckOrder(serverTick))
                return;

            AddTimeToAverage(clientSendTime);
#if CLIENT_TICK_RUNNER_VERBOSE
            VerboseLog(serverTick, clientSendTime);
#endif

            // if first message set client time to server-diff
            // reset stuff if too far behind
            // todo check this is correct
            if (!_initialized)
            {
                InitNew(serverTick);
                return;
            }

            // guess what tick we have to be to reach serever in time
            var serverGuess = _tick - DelayInTicks();
            // how far was out guess off?
            var diff = serverTick - serverGuess;

            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > _skipAheadThreshold)
            {
                (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();
                logger.LogWarning($"Client fell behind, skipping ahead. server:{serverTick:0.00} serverGuess:{serverGuess} diff:{diff:0.00}. RTT[lag={lag},jitter={jitter}]");
                InitNew(serverTick);
                return;
            }

            // apply timescale to try get closer to server
            AdjustClientTimeScale(diff);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {serverGuess:0.00} ct {_tick:0.00} diff {diff * 1000:0.0}, wanted:{diff * 1000:0.0}, scale:{TimeScaleMultiple}");
        }

        private float DelayInTicks()
        {
            (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();

            // *2 so we have 2 stdDev worth of range
            var delayFromJitter = jitter * 2;
            var delayFromLag = lag;
            var delayInSeconds = delayFromLag + delayFromJitter;
            // +1 tick to make sure we are always ahead
            var delayInTicks = (delayInSeconds * TickRate) + 1;


            if (delayInTicks > MAX_TICK_DELAY)
            {
                if (logger.WarnEnabled())
                    logger.LogWarning($"delay in ticks over max of {MAX_TICK_DELAY}, value:{delayInTicks:0.0} ticks");
                delayInTicks = MAX_TICK_DELAY;
            }

#if DEBUG
            Debug_DelayInTicks = delayInTicks;
#endif
            return delayInTicks;
        }

        private void AddTimeToAverage(double clientSendTime)
        {
            // only add if client time was returned from server
            // it will be zero before client sends first input
            if (clientSendTime != 0)
            {
                var newRTT = UnscaledTime - clientSendTime;
                if (newRTT > MAX_RTT)
                {
                    if (logger.WarnEnabled())
                        logger.LogWarning($"return trip time is over max of {MAX_RTT}s, value:{newRTT * 1000:0.0}ms");
                    newRTT = MAX_RTT;
                }
                Assert.IsTrue(newRTT > 0);
                _RTTAverage.Add((float)newRTT);
            }
            else
            {
                // just add 150 ms as tick RTT
                _RTTAverage.Add(0.150f);
            }
        }

        private void InitNew(int serverTick)
        {
            _tick = Mathf.CeilToInt(serverTick + DelayInTicks());
            // todo do we need to also set _time here?
            TimeScaleMultiple = _normalScale;
            _initialized = true;
            // todo do we need to invoke this at start as well as skip?
            OnTickSkip?.Invoke();
        }

        private void AdjustClientTimeScale(float diff)
        {
            // diff is server-client,
            // if positive then server is ahead, => we can run client faster to catch up
            // if negative then server is behind, => we need to run client slow to not run out of spanshots

            // we want diffVsGoal to be as close to 0 as possible

            // server ahead, speed up client
            if (diff > _positiveThreshold)
                TimeScaleMultiple = _fastScale;
            // server behind, slow down client
            else if (diff < _negativeThreshold)
                TimeScaleMultiple = _slowScale;
            // close enough
            else
                TimeScaleMultiple = _normalScale;
        }


#if CLIENT_TICK_RUNNER_VERBOSE
        private void VerboseLog(int serverTick, double clientSendTime)
        {
            (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();
            var delayFromJitter = jitter * 2;
            var delayFromLag = lag;
            var delayInSeconds = delayFromLag + delayFromJitter;

            var serverGuess = _tick - DelayInTicks();
            var diff = serverTick - serverGuess;

            var newRTT = UnscaledTime - clientSendTime;
            Debug($"{serverTick},{serverGuess},{_tick},{(float)DelayInTicks()},{delayInSeconds},{delayFromLag},{delayFromJitter},{diff},{newRTT},{intialized}");
        }

        private void Debug(string line)
        {
            if (_writer == null)
                return;

            _writer.WriteLine(line);
        }
#endif
    }
}

</attachment>

---

## 🤖 Assistant

<details>
<summary>Thinking Process</summary>

The user is now questioning the FixedUpdate decision. They're worried:
1. Unity's `Time.fixedDeltaTime` won't be correctly synced between server/host/client
2. Frame lag can cause FixedUpdate skips, causing issues in frame times
3. They think we need a Unity-independent timer
4. They reference the old complex TickRunner (JamesFrowen CSP) which has a server-side (simple) and client-side (tricky: sync, join-time smoothing, out-of-sync recovery)

They want me to focus ONLY on the timing issue, reply with suggestions and best practice, weigh the ideas, compare complexity vs what's required for good quality.

Let me think hard about this. This is a pure design/architecture reasoning question. Let me be rigorous.

First, let me examine the user's claims critically, because I should push back where they're wrong:

**Claim 1: FixedUpdate is "not correctly synced between server/host/client".**

Actually, this reveals a misconception worth addressing: in ANY CSP system, client and server tick counters are NEVER "synced" in the sense of running in lockstep. The whole premise of CSP is that the client runs AHEAD of the server (by ~RTT + jitter buffer). The tick *numbers* are shared via messages (client sends tick N's input, server acknowledges tick N in state). The *rate* must match (both run at 50Hz), but the *phase* is deliberately offset. This is true whether you use FixedUpdate or a custom runner.

What actually needs to hold:
- **Rate agreement**: both sides step the simulation at the same nominal rate (1/50s per tick). FixedUpdate gives this for free — `Time.fixedDeltaTime` is a project setting, and `Time.deltaTime` inside FixedUpdate is always exactly `fixedDeltaTime`... well, with one caveat: if a frame is very long, Unity can run more FixedUpdate iterations. Actually Unity's fixed timestep accumulator: `Time.fixedDeltaTime` is constant. FixedUpdate is called based on accumulated time. Unity 2020+: there's a maxAllowedTimeStep and the "dynamic fixed delta" option? No — modern Unity has `Time.maximumDeltaTime` (default 0.1s) which caps how much time is accumulated per frame — beyond that, Unity *drops* time (the accumulator is clamped), which means **time is lost**: FixedUpdate effectively runs fewer times than wall-clock time elapsed. This is the real issue the user is gesturing at: under frame spikes, Unity clamps `deltaTime` to `maximumDeltaTime` (default 1/10s), so the game's `Time.time` falls behind wall-clock time. On the server, that means the server's simulation falls behind wall clock — but does that actually matter?

Key insight: **CSP does not require wall-clock alignment between client and server ticks.** What it requires:
1. Client tick counter must lead server's last-acked tick by enough margin that inputs arrive before the server needs them (client leads by ~RTT/2 + jitter margin).
2. The server consumes inputs as fast as they arrive and simulates at the tick rate. If the server's FixedUpdate hiccups (clamped deltaTime), the server falls behind wall-clock but keeps processing the client's input stream in order — it just processes them later. The client's corrections get delayed, but the *simulation stays consistent* because everything is tick-indexed.

So the failure modes of FixedUpdate:
- **Frame spike → clamped accumulation → lost time**: the tick counter increments by (clamped delta / fixedDelta) instead of (real delta / fixedDelta). Both server and client experience this independently. Since neither side needs wall-clock alignment, this mostly doesn't break correctness — but it DOES break the client's "lead" calculation if the client bases its lead on wall-clock RTT. Wait, in the simple design, the client doesn't compute a lead at all — the client just runs its own tick counter and sends inputs. The server processes inputs as they arrive. There's no target offset to maintain! The client naturally runs at ~wall-clock rate, the server runs at ~wall-clock rate (with hiccups), inputs flow, states flow back. Nothing to sync!

Hold on, this is the crux. Let me think about what the old ClientTickRunner was actually FOR. In the old JamesFrowen CSP, the client keeps a target: `clientTick = latestServerTick + DelayInTicks()` where DelayInTicks is derived from RTT average + jitter. This is needed because... the old system is a *global* system: it uses server timestamps and wants the client's tick to be positioned such that inputs for a given tick arrive before the server executes that tick, and so the client maintains a smooth timescale (±1%) to hover at the right offset. That's the complex part: RTT moving average, standard deviation, timescale feedback control, skip-ahead thresholds.

Why did the old system need that? Because it's a *tick-authoritative* design: the SERVER owns the tick timeline, the client must align to `serverTick + delay`. The server processes one tick per server tick event. If the client runs too fast, it floods the server with future inputs (buffered, fine); too slow, the server starves and has to duplicate inputs → mispredictions. So the client wants to track the offset.

But in the SIMPLE design we sketched, the server doesn't own the timeline — **each object's owner owns its input timeline, and the server follows** (`lastProcessedTick` per object). The client can never "run ahead of what the server needs" in a harmful way — future inputs just get buffered. The client can never be "behind" in a harmful way either — the server waits (duplicates last input if starved, which causes a reconciliation when the real input eventually arrives... wait, no).

Hmm wait, let's re-examine: server follows client stream. If client stalls (frame spike, clamped FixedUpdate, whatever), server starves for inputs. What does the server do? Options: (a) wait — don't advance the object's sim until input arrives; (b) duplicate last input. If the server waits, then the server's simulation of that object just pauses — no harm at all, observers see it pause briefly, the client sees its own prediction continue... but now client tick 100 input arrives, server processes it. Everything consistent. If the server duplicates, we get a misprediction and correction once real input arrives — visible stutter for the owner.

So actually, the client-spike case: **the client itself stopped ticking** — its own local prediction pauses too (FixedUpdate didn't run). So client and server pause *together*. When the client resumes, it resumes its tick counter and sends inputs; server resumes consuming. Nothing desyncs. This is a genuinely important property: since tick rate is driven by the same mechanism (FixedUpdate accumulator) on both sides, and both sides lose the same *wall-clock* time but never lose *tick counter* coherence, there is no drift problem to solve.

What about *rate* drift: FixedUpdate always steps exactly `fixedDeltaTime` per tick (Time.deltaTime == fixedDeltaTime inside FixedUpdate). So each tick is exactly 1/50s of *simulated* time on both machines. Simulated time never drifts between machines because ticks are exact. The only mismatch is how simulated time maps to wall clock (which hiccups per machine) — and NOTHING in the simple CSP design depends on that mapping! States are tagged by tick, inputs by tick, interpolation by tick. Wall clock is never used.

This is the key argument the user is missing: **the dependency on wall-clock alignment is what the old ClientTickRunner existed to maintain, and the simple design has no wall-clock dependency at all.** The tick timeline is a logical timeline shared via tick-numbered messages. FixedUpdate hiccups don't desynchronize tick *counters* — they desynchronize tick-vs-wallclock, which we don't care about.

Let me now enumerate the actual edge cases the user raised and answer each with the simple design:

1. **"FixedUpdate is not correctly synced between server/host/client"** — True but irrelevant: no side needs wall-clock sync; only tick-indexed exchange matters. All three (host, client, server) advance tick counters via the same fixed rate; exchanges are by tick number. The one real requirement: same `fixedDeltaTime` project setting on all builds (or a shared config constant). That's a build-config requirement, not a runtime sync problem.

2. **"if any of them have frame lag, FixedUpdates might be skipped causing issues in frame times"** — Two cases:
   - **Server frame lag**: server's FixedUpdate may run fewer ticks than wall clock (clamped accumulator). Consequence: server sim lags behind wall clock; inputs queue up (client ran 50 ticks while server did 40). Server then... if the server has a catch-up loop (process up to N inputs per FixedUpdate to drain the queue — bounded), it catches up quickly. If not, the queue drains at 1-per-FixedUpdate, so server permanently runs behind by the spike amount until... hmm, that's a real design point: server should process up to K inputs per tick to drain backlog. That's a tiny bounded loop, not a clock sync system.
     - Actually wait — with FixedUpdate on server and a client running full speed: server gets 50 inputs/sec, processes 50/sec at steady state. A 1-second server freeze → 50 inputs queued; without catch-up, the server is now permanently 50 ticks (1 second) behind; corrections arrive 1s late forever, and since the client leads, everything still *works* (client sends input tick 1000; server is at 950; buffers hold; eventually... no, it never recovers! The queue stays 50 deep forever — the server processes input 951, 952... while client sends 1001, 1002...). Hmm, so permanent 1-second-late corrections — bad but not broken. With a bounded catch-up (up to 5 extra ticks per FixedUpdate when backlog exists), it drains 50 ticks in ~12.5 ticks of time (~250ms at 50Hz with 5x). Fine. This is the "MaxTickPerFrame" concept from the old runner, minus all the clock machinery. ~10 lines.
   - **Client frame lag**: client's FixedUpdate skips → client tick counter pauses; server starves → server waits for inputs (or duplicates). If server *waits* (my recommendation): both pause coherently, zero artifacts. If server duplicates: owner gets one correction on resume; smoothing hides it (or not, phase 2). Either is acceptable; waiting is simpler and more correct.
   
   Also worth noting: `Time.timeScale` affects FixedUpdate rate (paused game = no ticks). The old runner multiplied by `Time.timeScale` — a complexity we don't need if we say "ticks run in game time, not wall time; pausing the game pauses the world coherently on that machine"... but server can't pause. Eh — for a game server, timescale is 1; document "don't change Time.timeScale on the server; prediction runs in game time".

   There's one more subtlety: Unity clamps the fixed accumulator to `Time.maximumDeltaTime` (default 0.1s). So after a 2s freeze, only 0.1s worth of FixedUpdates run (5 ticks at 50Hz); the other 1.9s is discarded — `Time.time` jumps to wall-aligned-ish. Wait, actually: Unity docs: "maximumDeltaTime: The maximum time a frame can take. Physics and FixedUpdate will only be run for this duration even if the frame takes longer" — so yes, ticks are dropped relative to wall clock. For our design this is FINE and actually good: it caps catch-up work automatically. The tick counter advances 5, the client's own prediction advances 5, server advances 5 (or starves+waits). Coherent.

3. **"how to smoothly update time on join"** — in the old system: client receives first server state, sets `_tick = ceil(serverTick + DelayInTicks())` using RTT average, fires OnTickSkip. In the simple design: on join/ownership, the owner client initializes `CurrentTick` from the initial state handshake (server sends `{tick, state}`; client sets CurrentTick = serverTick + 1... but wait — should the client lead by RTT? Let's think about what happens if the client starts at serverTick+1 with zero lead:
   - Client simulates tick T+1, sends input. Server receives input for T+1 *after* it already needs it (server processes T+1 when it gets there — server is following the client's stream, so the server just *waits* for the input for T+1; it has nothing to starve on... it does starve: server FixedUpdate fires, wants input T+1, doesn't have it yet (it's in flight, RTT/2 in transit). Server waits/duplicates.
   - If server waits: the object's sim effectively runs at `client_rate - RTT-induced-hitch`... no — the server just processes each input when it arrives, one per FixedUpdate or catch-up. The result: the server sim runs ~RTT/2 *behind* the client in tick terms, but that's FINE. The catch: corrections (server state for tick X) arrive at the client ~RTT after the client simulated tick X. The client replays inputs X+1..current. This works at ANY lead — including zero lead! The lead only matters for *how far ahead the client predicts* vs how late corrections arrive relative to the client's *rendered* present.
   
   Hmm wait, actually let me re-derive. The client simulates tick X at wall time `W(X)`. The input arrives at server at `W(X) + RTT/2`. The server processes X at the first FixedUpdate ≥ that (plus queue). The server's state for X arrives back at client at `W(X) + RTT + processing`. By then the client has simulated up to tick `X + (RTT+ε)*50` roughly. So the client predicts ~RTT worth of ticks ahead of the last correction — that's inherent to CSP at any "lead" setting; the lead (client tick = serverTick + delay) in the old system exists because the SERVER is the tick master and the client must schedule its simulation so that inputs arrive *before the server's tick event for that tick* to avoid the server starving/duplicating. In the server-follows-client design, the server never needs inputs "on time" — it just processes what's arrived. So no lead computation, no RTT average, no jitter stddev, NO CLIENT TICK SYNC AT ALL.
   
   The cost of zero-lead + server-waits: the server's simulation of object O lags the client by ~RTT/2 in tick terms, and more importantly the *observer's rendered view* of the object lags by the interpolation delay measured against the server's sim time... Observers interpolate on the tick timeline with a delay; their render point is `latestServerTickForO - delay`. That's all fine — observers are always in the past anyway (the doc's 100ms). The delay for observers doesn't depend on the owner's lead.
   
   One more check — the reconciliation correctness with zero lead: server processes input X (arrived at W+RTT/2). Fine. But what if the client is sending inputs *faster* than the server processes? Both run at 50 ticks/sec nominal. Server FixedUpdate at 50Hz processes one input per FixedUpdate. Client sends 50/sec. Steady state: balanced. Server hiccup → backlog → bounded catch-up drains it. Client hiccup → server starves → waits. Balanced. 
   
   So on join: `CurrentTick = serverTickFromHandshake + 1`, buffers cleared, done. No RTT estimate, no moving average, no delay computation. If you *want* a small lead (to reduce server-starve duplication), the dumb version: client adds a fixed constant (e.g., +3 ticks) — no measurement. Or server-duplicates-on-starve for a few ticks then waits. All simple.

4. **"how to update time (and rest of state) when client runs too far out of sync"** — old system: skip-ahead threshold (10 ticks) → InitNew(serverTick) → OnTickSkip event → presumably full state resync. Simple design: the equivalent is a guard, not a mechanism: if the server receives an input for tick ≤ lastProcessedTick (already processed — client must have reset or skipped), or a state arrives for tick ≥ CurrentTick (server ran way ahead), treat as authoritative reset: apply state, set CurrentTick = stateTick + 1, clear buffers. That's the "hard reset" path which is ~10 lines and covers all unrecoverable cases. The 90% solution: you rarely hit it, and when you do, a visible snap is acceptable (that's what AAA games do too — they hard-reset too, the smooth path just makes it rarer).

   The old system's ±1% TimeScaleMultiple feedback control exists to avoid those hard resets by drifting smoothly. Cost: RTT moving average + stddev, 3 timescale states, thresholds, and a feedback loop that itself has failure modes (the todos in the file admit: "todo what if we jump back", "todo check this is correct", "todo will skipping behind cause negative effects"). For a SIMPLE system: accept the occasional hard snap.

So my recommendation crystallizes: **Hybrid — keep FixedUpdate as the tick driver (rate is exact, physics aligned, zero machinery), and remove the *need* for clock sync by making the server follow each client's input stream instead of owning the timeline.** The old ClientTickRunner's entire job was maintaining a client↔server tick offset; that job only exists if the server is the tick master. Change the ownership of the timeline and the entire problem class disappears. This isn't a compromise — it's eliminating the requirement.

But I should steelman the user's position and the TickRunner approach fairly, and present honest options with complexity/quality tradeoffs. The user explicitly asked: "Reply with suggestions and best practice, weigh the ideas and compare complexity vs what is required for good quality."

Let me enumerate the option space for the timing layer:

**Option A: Pure FixedUpdate, server follows client (recommended)**
- Tick driver: Unity FixedUpdate both sides. Tick = logical counter, exchanged in messages. No wall-clock anything.
- Server: per-object `lastProcessedTick`; each FixedUpdate process next input if present; bounded catch-up (≤5) if backlog; starve → wait (or duplicate last input for 1-2 ticks then wait — I'd pick "duplicate immediately", matches old plan, simpler mental model... hmm, which is better for quality? If server waits: object pauses on server for RTT every time... no wait — starve happens only when client hiccups or at join (zero lead). Steady state with zero lead: does the server starve EVERY tick? Let me check: client simulates tick X at W(X) and sends immediately. Server receives at W(X)+RTT/2. Server's FixedUpdate at 50Hz wants input X at its own schedule. Server tick events happen every 20ms wall time. Input X arrives at W(X)+RTT/2. When did the server "want" X? The server processes inputs in order as available — it processed X-1 at whatever time it arrived +queue. So the server is perpetually ~RTT/2 behind the client, processing each input on arrival. It NEVER starves in steady state — starvation only when client pauses or a packet drops... wait, packet drop with redundancy: input X's packet drops; next packet (20ms later) carries X (redundancy) — server waits ~20ms for X then proceeds. So starvation events are rare; duplicate-then-wait policy: duplicate only if starved, i.e., on drops/hiccups. Good: default = "if no input for current tick, reuse last input for at most N (say 2) ticks, then pause waiting." Actually simpler still and completely fine: "if starved, duplicate last input" (old plan's choice). When real input arrives later, correction fixes it. The owner sees a small snap on packet loss — acceptable for simple CSP, smoothed in phase 2.
- Join: handshake sets CurrentTick = serverTick + 1 (+ fixed constant lead if desired, e.g. +2).
- Out of sync: guards → hard reset (apply state, jump tick, clear buffers). Rare, visible snap, acceptable.
- Complexity: ~30-50 lines total beyond the base loop. No RTT measurement, no averaging, no feedback.
- Quality: CSP-grade prediction quality; corrections arrive RTT-late (inherent); server hiccups self-heal via bounded catch-up. The ONLY quality loss vs the old runner: (a) after a client freeze, the client's tick counter is behind wall-clock, so its inputs are 'late' relative to real time — but since the server follows, the whole object just shifts ~in game-sim time; observers' delay hides it. (b) no smooth ±1% drift → occasional hard snaps instead of graceful drift. For most games (co-op, casual PvP, non-FPS-competitive), that's fine.

Wait — one more honest quality analysis on (a): client freezes 2s (OS stall, alt-tab). Unity clamps accumulator: client loses 1.9s of *simulation* time. Its tick counter is now 1.9s behind the server's processing of... no — server was starved the whole 2s (no inputs arrived), so server was ALSO paused on that object (duplicating after 2 ticks of starvation... hmm, server duplicates last input while starved — so the server extrapolates the player forward at last input for 2s?! That's bad: server simulates 100 ticks of duplicated input, e.g., running forward. When the client unfreezes, its tick counter is ~1.9s behind the server's `lastProcessedTick`. Client sends tick X (=old + 5). Server already processed way past X → stale input. Client receives state for tick ≥ its CurrentTick → authoritative reset path → client jumps to server tick, replays nothing, snap. The player rubber-bands to wherever duplicated-input carried them.
  - Fix: server starve policy should be **wait, not duplicate** (pause the object). Then server's lastProcessedTick == client's frozen tick - epsilon; on unfreeze, client continues at tick X+5 (it lost 1.9s of sim time), server resumes from there. Coherent pause, like lag compensation-free games handle AFK: player frozen in world, resumes. MUCH better failure mode, and SIMPLER (wait = do nothing; duplicate = extra logic). Only on genuine packet loss (not client freeze) does wait cost a 20ms stall — invisible under the observer interpolation delay.
  - So: **server starve → pause object (no duplication)**. This also removes the "duplicate last input" machinery from the plan. Even simpler. The misprediction risk from duplication disappears too.
  - Hmm, but one edge: with zero lead and RTT/2 in-flight, is there a steady-state starve-wait that adds jitter? Server processes input X at arrival+queue. Inputs arrive every 20ms steadily. FixedUpdate events every 20ms. Two independent 20ms clocks (client machine and server machine, both ~50Hz real but with hiccups) — server event vs input arrival race each tick: if server event fires 1ms before input X arrives, server waits 1ms (same FixedUpdate? no — the event already passed; it waits for NEXT FixedUpdate = up to 20ms late!). Uh oh — that could desynchronize: server waits a full tick, now permanently one tick behind, and then the next input arrives during... let me simulate: server FixedUpdate at t=0,20,40,60,80. Inputs X arrives at t=11 (RTT/2=10ms with jitter). At t=20, server wants X (it's lastProcessed+1)? It has X (arrived t=11). Processes X at t=20. Input X+1 arrives t=31. At t=40, processes X+1. Fine. If input X arrives at t=21 (1ms late): at t=20 server starved → waits. At t=40 it processes X. Input X+1 arrived t=41 (steady 20ms spacing)... at t=60 processes X+1. The server is now permanently 20ms+RTT/2 behind — but consistently so, processing one input per FixedUpdate, no starvation anymore (each input has 20ms to arrive and arrives at RTT/2≈10-15ms after the previous was processed... wait: processed X at t=40, X+1 arrived at t=41, want it at t=60 — arrives in time. So it self-stabilizes one tick behind. No stutter, no starvation loop. Good — the pipelining works: server falls behind by however much it needs, then runs cleanly. That's the beauty of follow-the-stream: **self-clocking**. Like TCP. The server's rate automatically matches the client's rate, phase offset absorbs jitter, backlog absorbs hiccups. This is a really strong argument.
  - And corrections: server state for tick X arrives back at client at W(X)+RTT+40ms whatever — client replays X+1..CurrentTick. Unchanged.

So Option A is self-clocking and eliminates every hard problem the old ClientTickRunner solved. I'm now quite confident this is the right recommendation, and it's *simpler* than the old plan's "duplicate last input" too.

**Option B: Custom TickRunner (Unity-independent accumulator), server still follows client**
- Same follow-the-stream protocol as A; only the driver differs: `OnUpdate` accumulates wall-clock and fires Tick events. Physics problem: predicted objects using Rigidbody forces can't rely on FixedUpdate anymore; you'd need autoSimulation=false + Physics.Simulate (global) — the old decision doc's "physics nightmare". For kinematic/CC prediction (our stated v1 contract), a custom runner is actually *workable*... but what does it buy over FixedUpdate? Decoupled rates (20Hz net/50Hz physics) — not needed for simple; wall-clock-true time (no Unity clamp) — buys nothing in a self-clocking design since nothing depends on wall clock. So B costs the physics headache + accumulator code and buys ~nothing. Reject, unless we want decoupled rates later.
- Old runner's wall-clock Stopwatch + maximumDeltaTime clamp avoidance: the accumulator version has its own edge cases (the todos in the file). Not worth it.

**Option C: Old-style server-mastered timeline with client sync machinery (the JamesFrowen ClientTickRunner)**
- Server owns tick; client tracks `serverTick + delay(RTT,jitter)` with moving average, stddev, ±1% timescale feedback, skip-ahead, OnTickSkip resync.
- Quality: graceful drift, minimal snaps, tighter correction latency (corrections arrive "on schedule").
- Complexity: the whole file. Feedback loops need tuning (diffThreshold, timeScaleModifier, skipThreshold, movingAverageCount), edge cases explicitly unresolved in the source (`todo` comments at the jump-back and skip paths). And it *requires* the custom runner (timescale multiplication is per-runner, not global fixedDeltaTime — you can't multiply just your object's FixedUpdate rate without the global `Time.fixedDeltaTime`, which the decision doc already rejected).
- Verdict: this is exactly the legacy complexity the user wants to delete. Its quality edge (smooth ±1% drift) only matters at high tick rates with jitter-sensitive competitive play.

There's a subtle interplay worth flagging honestly: the old system's design is "server tick master + client aligns". Our simple design flips mastership per-object to the owner client. That inversion is what deletes the sync problem. I should make that explicit because it's the actual architectural insight, more than "FixedUpdate vs custom".

Also worth addressing the user's specific fear list one by one in a small table or bullets:
- frame lag skips FixedUpdates → true, but tick counters stay coherent (both sides exchange by tick number; lost wall-clock time is invisible to the protocol). Client stall → server stalls same object (self-clocking). Server stall → backlog → bounded catch-up.
- "not correctly synced between server/host/client" → was never required; only *rate* (same fixedDeltaTime) is required, and that's a build setting, not runtime.
- join → handshake, CurrentTick = state.tick + 1.
- too far out of sync → guards + hard reset (snap), acceptable rarity; phase-2 smoother hides most.

Host mode: trivial (same-process, authoritative path).

One more best-practice point: **tick time source inside ProcessInput** — the determinism contract: use `tick * fixedDeltaTime` (or pass fixedDeltaTime) rather than Time.deltaTime/time — we already had that in the plan.

And maybe recommend: keep a thin seam — an `IPredictionTime`-like abstraction? NO — that's legacy creep; the old file shows PredictionTime with UpdateMethod/IsResimulation etc. Don't build the abstraction until a second driver exists. YAGNI. But maybe one small concession: keep the per-object tick fields in one place so a future custom driver could slot in. Don't say much.

Let me also double-check the claim "Time.deltaTime is always fixedDeltaTime inside FixedUpdate": yes, inside FixedUpdate, Time.deltaTime == Time.fixedDeltaTime (except during catch-up with dynamic? no). Unity 2022's "dynamic fixed delta" — `Time.fixedDeltaTimeMax` etc. is opt-in; default behavior is constant step. Fine. And Unity 6 has FixedUpdate rate control options — still opt-in.

Also the maximumDeltaTime clamp: default 0.1s; a 2s stall → only 5 ticks run (at 50Hz), 1.9s discarded. Coherent pause as analyzed. Good — actually this is a *feature* for us (built-in bounded catch-up, the thing MaxTickPerFrame did manually).

Edge: client leads by fixed +2 lead constant on join — worth it? With self-clocking, zero lead is fine; server just runs RTT/2 behind. The only reason for lead: none really. Keep `+1` (start at next tick after handshake state). Simple. Hmm — but there is one quality consideration: corrections for tick X arrive RTT after client simulated X; client replays ~RTT*50 inputs each correction. That's inherent to CSP regardless of lead. Lead doesn't change correction depth (correction depth = RTT in ticks, because client simulates RTT ahead of what it hears back). Right — lead in the old system is about server tick alignment, not correction depth. OK.

One more potential gotcha in Option A worth mentioning: **input sampling jitter** — FixedUpdate sometimes runs 0 times per rendered frame (high fps) and multiple times per frame (low fps). GetInput() per tick samples at tick granularity — standard, fine. And interpolation for the *local* player: predicted root moves in discrete 20ms steps; render smoothing between ticks (interpolate transform in Update between last two predicted states, or rely on physics interpolation). The old decision docs didn't cover local-player render smoothing — that's actually a real quality item: without it, predicted movement looks 50Hz-steppy at high fps. Options: (1) camera/render interpolation of the visual child between predicted states (like the VisualSmoother does for snaps, extended to always-on); (2) Rigidbody interpolation setting (interpolate) if using RB; (3) ignore (many simple games fine at 50-60Hz ticks). I'll flag it as the *one* quality item worth stealing from the smoother: render interpolation for the local player. Keep brief — user asked focus on timing though. One line.

Structure of reply (focus: timing only, per user):
1. Reframe: what actually needs to be true (rate match + tick-indexed exchange), what doesn't (wall-clock alignment). FixedUpdate's alleged failure is a non-failure in this design. The old ClientTickRunner existed to solve a problem created by making the server the tick master; flipping mastership deletes the problem.
2. Self-clocking server (follow the stream) — the key trick: starvation = pause (not duplicate), backlog = bounded catch-up. 10 lines, no RTT math. Explain pipelining/phase absorb.
3. The three questions answered: join (handshake), out-of-sync (guards + hard reset snap, rare), frame lag (coherent pause both sides).
4. Options table: A FixedUpdate+self-clocking (recommended), B custom runner + self-clocking (cost, buys nothing for kinematic), C old ClientTickRunner (what it buys: graceful ±1% drift; what it costs: the file + tuning + todos; when justified).
5. Verdict + concrete contract lines to add to plan (rate-matching requirement, starve policy, catch-up bound, reset guards, tick-derived time in ProcessInput, timescale warning). Keep honest about quality delta: occasional visible snap on long stalls vs old system's smooth drift; acceptable for simple CSP; smoother (phase 2) hides small ones.

Length: substantial but focused. Use a small comparison table. Cite the old file's specifics (RTT moving average, stddev*2 jitter, ±1% scale, 10-tick skip, todos) as evidence of complexity, and note which of its jobs are still needed (none of the sync ones; only the bounded-tick-budget idea survives as the catch-up bound).

I might also mention `Time.captureDeltaTime`... no. Keep tight.

One more sanity check on the "server pauses object on starve" and observers: observers' interpolation buffer drains during pause → they hold last state (freeze) briefly. Standard. Fine.

Also: what about the server's OTHER simulation (game logic, physics) that expects the object to move? Pausing object sim means transform stays; server logic reads transform — sees a frozen player during their stall. Correct behavior, matches reality.

And a note on multiple predicted objects sharing the client's single FixedUpdate: ticks of different objects on the same client share the client's FixedUpdate cadence naturally — per-object counters derived from one local clock... in our per-object design each PredictionBehaviour counts its own CurrentTick in its own FixedUpdate. They'll stay in step since same driver. Fine, no global runner needed. Actually a per-object tick counter incremented per FixedUpdate call — identical across objects — could later be hoisted to a shared per-client counter; not needed now (per-object autonomy is the design). OK.

Should I write this into a doc? The user said "Reply with suggestions and best practice, weigh the ideas and compare complexity vs what is required for good quality." — a chat reply. They're mid-decision. I'll reply in chat. Maybe offer to fold the outcome into Decision_FixedUpdate_vs_CustomTick.md afterward. Don't edit files unprompted during an active decision discussion — they asked for a reply, not doc updates.

No tools needed — I have the file content from the attachment and prior reads. Actually should I re-read Decision_FixedUpdate_vs_CustomTick.md? I have it from earlier this conversation; no changes indicated. Fine without.

Let me draft.

Key sections:
- The core reframe (server follows the client; no wall-clock dependency)
- Why FixedUpdate's weaknesses don't bite (rate exact, counters coherent, clamp = bounded catch-up for free)
- Edge cases (join / stall / out-of-sync / packet loss)
- Options weighed (table)
- What survives from the old TickRunner (catch-up bound, tick budget; nothing else)
- Quality honesty: what you give up (smooth drift), when it matters
- Concrete contract additions

I'll write it now, terse but complete. Aim ~70-90 lines of markdown.

</details>

The old `ClientTickRunner` is a solution to a problem we can delete instead of solve. Here's the analysis.

## The reframe: nothing in CSP needs wall-clock sync

Walk through what the timing layer actually must guarantee:

1. **Same tick *rate*** on every machine (1 tick = 1/50s of simulated time). FixedUpdate gives this exactly — `Time.deltaTime` inside `FixedUpdate` is always literally `fixedDeltaTime`. Rate is a project setting, not a runtime sync problem.
2. **Tick-indexed exchange**: inputs and states carry tick numbers; buffers are keyed by tick.

That's the entire requirement list. Now what the old `ClientTickRunner` maintains — client tick hovering at `serverTick + (RTTavg + 2·jitterStdDev)` with ±1% timescale feedback — is **wall-clock alignment between the client's tick counter and the server's tick counter**. That requirement only exists because the old design makes the **server the tick master**: the server fires tick N on its own schedule, so the client must be positioned so input N lands before tick N fires. Hence the RTT moving average (100 samples), the standard deviation, the three timescale states, the 10-tick skip-ahead, and `OnTickSkip` resync.

Flip the mastership and the problem class vanishes: **the owner client owns its tick timeline; the server follows the stream.** This is what `OverallPlan.md` already half-says with `lastProcessedTick`, but it doesn't draw the consequence — with a following server, the client can never be "too fast" (future inputs just buffer) and never meaningfully "too slow" (the server waits). It's self-clocking, like TCP: the server's consumption rate automatically matches the client's production rate, and the phase offset between two independent 50Hz clocks absorbs jitter for free.

## Why the FixedUpdate fears don't bite in this design

**"FixedUpdate isn't synced between machines"** — it never needs to be. Both sides only exchange tick numbers. Nobody compares `Time.time` to anything. (The one real requirement: `fixedDeltaTime` must match across builds — a build config check, not runtime code.)

**"Frame lag skips FixedUpdates"** — trace both cases with a following server:

- *Client stalls 2s*: Unity clamps the fixed accumulator to `maximumDeltaTime` (0.1s default), so the client runs ~5 ticks and discards 1.9s. Its counter pauses. The server receives no inputs → **pause that object** and wait. Both sides freeze coherently on the same tick; client unfreezes at tick N+6 and the server resumes at N+6. Zero desync, zero machinery. Note Unity's clamp is doing the old `MaxTickPerFrame`/`MaxFrameTime` job for free — built-in bounded catch-up.
- *Server stalls 2s*: client's inputs queue up (~100 deep in a 128 buffer). Server drains with a bounded catch-up loop (process up to ~5 inputs per `FixedUpdate` while backlog exists) — the one idea worth keeping from the old file, ~6 lines. Without it the server stays 2s behind forever; with it, it recovers in ~250ms.

**Packet loss** (the only real starvation source): with the 3-input redundancy, a dropped packet means the server waits ~20ms for the input to arrive in the next one. Under the observers' 100ms interpolation delay, invisible. No RTT math needed to know this — the wait is one tick by construction.

## The three edge cases you named

- **Join / ownership gain**: server sends the initial `{tick, state}` handshake; client sets `CurrentTick = tick + 1`, seeds buffers, goes. No RTT estimate, no delay computation. The client starts with zero lead — the server just processes each input on arrival and settles ~RTT/2 behind the client in phase. That's fine; corrections are tick-indexed and work at any phase offset.
- **Server starves**: **pause the object, don't duplicate last input.** This is a change from the current plan (lines 101–104), and it's both simpler and better: duplication under a client freeze extrapolates the player forward for 2 seconds of fake input and forces a rubber-band on resume; pausing is correct and is *less* code. Reserve duplication for nothing — it has no case where it beats waiting, given redundancy covers transient loss.
- **Genuinely out of sync** (state arrives for `tick ≥ CurrentTick`, or input arrives for `tick ≤ lastProcessedTick`): hard reset — apply authoritative state, set `CurrentTick = stateTick + 1`, clear buffers. Visible snap, rare by construction, and it's the same thing AAA netcode does when its smooth path fails. The old system's ±1% drift existed to make this path rare; for a simple CSP, accept the snap and let the (phase-2) smoother hide the small ones.

## Options weighed

| | A: FixedUpdate + following server | B: Custom runner + following server | C: Old-style server master + client sync |
|---|---|---|---|
| Tick driver | Unity builtin | Manual accumulator, Stopwatch, drift handling | Same as B (required — timescale is per-runner, can't be per-object in FixedUpdate) |
| Physics | Native harmony | `autoSimulation = false` + `Physics.Simulate`, or kinematic-only | Same as B |
| Clock sync code | **None** | None | RTT moving average, jitter stddev, 3-state timescale feedback, thresholds, skip-ahead, resync event |
| Tuning surface | `fixedDeltaTime`, catch-up bound | + accumulator edge cases | + 4 interdependent constants; unresolved edge cases in the source itself (`todo` on jump-back, skip-behind, timer reset) |
| Quality | Full CSP quality; occasional visible snap after long stalls | Same as A | Marginally better: smooth drift avoids snaps, marginally tighter correction schedule |
| Verdict | **Recommended** | Costs the physics nightmare, buys nothing for a self-clocking design | The quality edge only matters for competitive-tight, jitter-sensitive play |

Honest quality delta of A vs C: after multi-second stalls you get a snap instead of a graceful ±1% re-drift, and correction latency jitters slightly with server phase. If this were a 128-tick competitive shooter, that delta might justify C's machinery. For a simple general-purpose CSP at 30–60Hz, it doesn't — C is the legacy weight you're replacing, and the source file's own `todo` list is evidence of how expensive that path is to get *right*, not just to write.

## Concrete contract additions for the plan

- Tick rate = `1 / Time.fixedDeltaTime`; all builds must share the value. Tick duration handed to `ProcessInput` as `tick * fixedDeltaTime` — never `Time.time`/`deltaTime` (also part of the determinism contract).
- Server: per-object `lastProcessedTick`, starve → pause, backlog → catch-up bounded at ~5 ticks/frame.
- Hard-reset guards both directions, as above.
- Don't change `Time.timeScale` on the server; a global timescale multiplies FixedUpdate frequency (that's the *one* wall-clock-ish coupling left, and the old runner's `timeScale` multiplication is a trap worth avoiding entirely).
- Don't build an `IPredictionTime`/runner abstraction now — the old file's `PredictionTime` with `UpdateMethod`/`IsResimulation` fields is exactly the premature seam to not copy. Per-object tick fields in the behaviour are enough; hoist to a shared counter only if a real second driver appears.

One adjacent quality item worth keeping on the radar (not timing, but the same "smoothness" budget): the predicted root moves in 20ms steps, so at high frame rates the local player renders steppy. The phase-2 visual smoother's always-on form (render-position interpolation in `Update`) covers it — same component, one more reason it's phase 2 rather than never.

If you're sold, the doc outcome is: `Decision_FixedUpdate_vs_CustomTick.md` resolves to A with the "following server / self-clocking" reasoning added (it changes the strongest con on that page — clock sync — from a real cost to a non-problem), and `OverallPlan.md` gets the starve-pause policy, the catch-up bound, the join handshake, and the hard-reset guards. Want me to write those in?

---

## 👤 User

The primary goal is a CSP system is that the state is all Server auth, so "the owner client owns its tick timeline; the server follows the stream" is kind of concerning. If a system like this how can we ensure that the client is giving correct tick timeline? Server forwarding state to other clients is fine, it will just apply inputs on its tick and sync to others like with any other non-player object. The important part will be how it check input and tick values from owner, applies them, and sends back confirmed state. I have heard of systems that avoid fixed times for this and instead use update time instead, so its not as rigid to exact ticks. Please consider all this.

---

## 🤖 Assistant

<details>
<summary>Thinking Process</summary>

The user is pushing back on my "owner client owns its tick timeline; the server follows the stream" framing. Their concern: the primary goal is server authority. If the client owns the tick timeline, how do we ensure the client is giving a correct tick timeline? They're worried about trusting client tick numbers.

They also mention: systems that avoid fixed times and use update time instead — "not as rigid to exact ticks."

Let me think carefully about what's actually true here.

## The core question: is a client-driven tick stream compatible with server authority?

Server authority in CSP means:
1. The server decides what the true state is — clients can't write state directly, they can only send inputs.
2. The server must validate inputs (anti-cheat / sanity) before applying.
3. The server is the source of truth broadcast to everyone.

Client-driven *timeline* (tick numbering) does NOT mean client-driven *state*. This is the key distinction I need to make crisply. Let me enumerate what a malicious client can do with a client-owned tick counter:

1. **Send tick numbers that don't advance** (replay old ticks, or send tick N twice). Server guard: reject `tick <= lastProcessedTick` (duplicate/stale). This is a trivial monotonic check.
2. **Skip ticks** (send tick 1,2,3, then 7). Server sees a gap. Options: pause waiting for the gap to be filled (redundancy will fill it if it was just packet loss), or after a timeout, treat as a skip and just apply the latest. Skipping ticks doesn't gain the client anything except self-inflicted teleport-less movement (they'd move less input → less movement). Actually wait — could skipping ticks gain something? If the client skips ticks, the server applies fewer inputs, meaning the player moves less... that's a self-penalty. Could the client *speed up* its tick rate to move faster? Send tick 1,2,3,4,5,6,7... at 100Hz instead of 50Hz. Each input applies one tick's worth of movement, so more ticks per second = faster movement! THIS is the real cheat vector of a client-driven timeline: **rate inflation**.

So the server must enforce: tick numbers advance at approximately real time. I.e., tick rate limiting. Server-side check: over a sliding window (say 1 second of wall clock), the number of processed ticks for that object must not exceed tickRate × (1 + tolerance). If the client streams 2× tick rate, server clamps (process at most tickRate+ε per real second) or flags. This is a standard "input rate limiting" concern in every CSP system — even in server-master systems! Wait, in server-master systems, the server just doesn't have ticks available for inputs that didn't arrive in time, so rate inflation is impossible... but actually in server-master, the client can send 2× inputs per second and the server drops the extras (only one input per tick slot). Hmm, so server-master gives you rate enforcement for free. With client-master, we need explicit rate limiting.

But note how cheap that is: server counts processed ticks per object per wall-clock window, clamps to tickRate × (1+10%). It's a few lines. Compare: server-master requires the entire ClientTickRunner sync machinery. So the cost of client-driven timeline is "a rate limiter," and the cost of server-driven timeline is "the whole sync layer." That's a very good trade.

3. **Send garbage inputs** (teleport commands, speed hacks in the input values themselves). This is orthogonal to timeline — the server must validate input *semantics* regardless of who owns the timeline. Input validation (max move speed, max turn rate, bounds) is a server-authority feature that exists in BOTH designs. Important: state authority ≠ input validation; the server applies inputs through ProcessInput, and if ProcessInput is written with clamps, the client cannot exceed game rules no matter what timeline shenanigans it does. Actually — one subtlety: if the server's ProcessInput clamps movement per tick, then rate inflation is ALSO neutralized automatically (more ticks × clamped per-tick movement still = faster movement... no wait, clamped per-tick movement × more ticks per second = more movement per second. So per-tick clamping does NOT neutralize rate inflation. The rate limiter is genuinely needed). Hmm, but actually there's another subtle point: with rate limiting in place + per-tick clamps in ProcessInput, the maximum speed is bounded by tickRate × perTickClamp, which is the same bound as server-master. Good.

4. **Send huge redundant arrays** (N=1000 inputs per message) to bloat server processing. Server caps: ignore any input with tick ≤ lastProcessedTick, so redundant history beyond the gap is just ignored — bounded work. Also cap array size in the RPC.

So "how can we ensure the client is giving correct tick timeline" has a concrete answer:
- Monotonicity check (reject non-advancing ticks) — trivial.
- Rate limiting (wall-clock budget per object) — few lines, and note this check uses the server's *wall clock* to bound the client's *simulated time* — that's the only place wall clock enters, and it's a one-way bound, not a sync.
- Gap handling (redundancy fills loss; persistent gap → timeout → apply latest / hard reset).
- Everything else (semantic input validation) is required in any design anyway.

And crucially: the authoritative state and the broadcast remain 100% server-owned. The client's tick numbers only *label* inputs; the server's simulation results are what observers and the owner see. The client can lie about labels only within the rate budget, and lying gains nothing because every input still passes through server ProcessInput.

There's a deeper conceptual point worth making: **"server authority" in CSP is about state, not about clocks.** The tick counter is just a sequence number for the input stream. TCP is a good analogy again... maybe better: this is exactly how most production CSP systems work actually. Let me think about real systems:

- **Source engine**: server-driven ticks, client sends usercmds tagged with the client's tickcount... actually Source servers tick and clients send commands with tick numbers; the server applies them in order received (with lag compensation). The server is the master clock. Client runs its own simulation ahead by (RTT/2 + interpolation) — client-side prediction layer, but tick numbers come from server's tick that the client knows about from the last snapshot. So Source is server-master.
- **Quake 3**: client sends cl_cmds with cmd times (milliseconds, client's own clock); server runs them in order, with pmove_fixed or not. Q3 clients frame-rate dependent movement — the client's own timing drove movement speed, famously enabling the 125fps jump trick. That's actually an example of the client-timeline problem: client framerate affected physics! Good cautionary tale for "update time instead of fixed ticks" — Q3 physics depended on client frame time, which is exactly the exploit the user is describing ("systems that avoid fixed times and use update time instead, so it's not as rigid to exact ticks"). Quake 3's com_maxspeed and framerate-dependent physics bugs are the canonical argument AGAINST variable-delta input processing for deterministic server validation. Actually, let me be careful: Q3 moved to fixed pmove_msec (8ms) eventually. The framerate-dependence was a bug/exploit, later clamped.

So the user's "I have heard of systems that avoid fixed times and use update time instead" — what are those systems really? I think they might be thinking of:
- **Rigidbody/Physics-based sync**: send timestamps, server stamps states with its time, interpolation by time not tick (e.g., Photon/Netcode for GameObjects' approach: NetworkTransform sends snapshots with server timestamps; clients interpolate by time). That's snapshot interpolation for REMOTE objects (our observers already do this), not prediction.
- **"Timedelta" / variable-tick systems**: some engines run simulation with variable dt, exchange states+timestamps, and reconcile by time rather than tick. This is much harder to make deterministic (floating-point, order), and determinism is the lifeblood of replay-based reconciliation. If your ProcessInput isn't deterministic, you can't replay inputs client-side to reconstruct — you'd need full state replay, which is heavy.
- Actually, there IS a legit family: **input-driven with timestamps, server simulates at its own rate** (e.g., some physics-heavy games like "Rocket league"-style — servers simulate state with the client's input + dt from the client's frame timestamps). Psyonix's RL netcode talk: clients send input + timestamp, server simulates each input with the client's dt... Actually Rocket League: fixed 60Hz tick on server, clients send inputs for specific ticks. Hmm.

The honest answer about variable-dt: variable timestep breaks bit-exact replay (FP non-associativity across different dt values, iteration counts differ), breaks input validation bounds (a 3× dt means 3× movement per input — you must clamp dt per input server-side, i.e., you end up re-imposing a fixed-step budget anyway), and it reintroduces the rate-inflation problem in its worst form (client sends 5 inputs per frame with dt=frameTime each → movement scales with input count, not with real time — unless server clamps by timestamps... which is a wall-clock budget again, i.e., the rate limiter in disguise).

So the recommendation stays: fixed tick = deterministic, replayable, validatable. The "rigidity" the user worries about (exact ticks) is actually the *feature*: tick N has exactly one meaning everywhere (N × fixedDeltaTime of simulated time), which is what makes server validation and client replay share the same arithmetic.

But wait — I should steelman the update-time idea more. Is there a version that's simple and good? "Timestamp instead of tick number": client sends inputs stamped with client send-time; server simulates on its FixedUpdate applying whatever inputs arrived that frame (0, 1, or 2+), clamping to at most one input's worth... That degenerates to: server applies ≤1 input per fixed step, ignoring extras = input dropping under high send rate = lossy. Or applies each with dt = time between client stamps → variable dt again. Every variant either re-imposes fixed step or breaks determinism/validation. Also reconciliation on the client: to replay, the client needs to know exactly which inputs the server applied in which order with which dt — so the server would have to echo that mapping back. With fixed ticks, the mapping is implicit (tick number). With timestamps it must be explicit. Fixed ticks are the *simpler* representation of the same information. I'm confident: ticks are timestamps with a quantum, and quanta make everything (buffers, replay, validation, redundancy) integer-simple.

Now — the user's concern also has a legitimate kernel I should concede and address: with a client-driven stream, the SERVER must be defensive about the stream's shape (monotonic, rate-limited, gap-managed). That's real work, but it's ~30 lines and it's a *validation* problem (server checking data), which is squarely in "server authority" territory — arguably it IS the server-authority feature list. Meanwhile server-master costs the whole ClientTickRunner. And note: even in server-master designs, the server must validate input *contents*; timeline validation is just one more check in the same place.

Also worth conceding: "server follows the stream" does NOT mean server follows blindly. Let me re-frame the mastership language: it's not that the client owns time and the server obeys; it's that **the client proposes, the server disposes**. The tick number is a sequence label the client assigns; the server enforces the rules (monotonic, in-budget, gaps filled or timed out) and produces all state. "Authority over state" vs "authority over sequencing" — sequencing is per-object and local; state is global and broadcast. There's no authority leak: a bad actor can only desync *itself* (and it's the one who suffers the corrections), never other clients, because others only ever see server state.

Hmm, actually let me double-check that claim: can a malicious owner desync observers? Observers see server-simulated states only. If the owner floods inputs, server rate-limits → observers see rate-limited (correct) movement. If owner pauses inputs, server pauses object — observers see a frozen player, which is... correct, the player is (as far as the server can tell) stalled. Same as any player standing still. If owner sends semantically-valid-but-weird inputs (spin in circles), observers see exactly that — same as server-master. So no, observers can't be desynced beyond what the input content itself commands. Good — that's a strong argument.

One more angle: the user said "Server forwarding state to other clients is fine, it will just apply inputs on its tick and sync to others like with any other non-player object." — Wait, "apply inputs on its tick". That suggests they picture the server having its own tick on which it applies whatever inputs it has (possibly duplicating last input when starved — the original plan's lines 101–104). That's a hybrid: server-master consumption, client-labeled inputs. Let me think about whether THAT is better than "follow the stream"...

Design D: "Server-tick consumption": server ticks at its own rate; each server tick, take the next unconsumed input from the buffer; if none, duplicate last (or pause). Input tick labels map onto server ticks... but then the mapping between input-tick-label and server-tick drifts! The client predicted input #N produces state at client tick N; the server applies input N at server tick M ≠ N. The correction message says "after applying input N, state is S" — for the client to reconcile, it must know which input N the state corresponds to. So the server must echo the *input tick label*, not the server tick. Then the client's timeline is still the reference for reconciliation! The server's own tick becomes almost irrelevant — it's just a consumption scheduler. So design D converges to design A (follow the stream) but with extra bookkeeping: server tick, mapping, duplication policy, and the duplication-induced rubber-banding I flagged last turn. The only genuine difference: in D the object always moves (duplicated input) even when the owner is silent; in A it pauses. For a simple CSP, pause-when-starved is more honest and simpler. I should present this convergence explicitly because it directly addresses their sentence — their mental model ("apply inputs on its tick") already requires echoing input labels back, so the client timeline is ALREADY the spine of their own design. That's a persuasive observation: the tick that matters for reconciliation is the input's tick, whoever's clock consumed it.

Let me also reconsider: is there a case for the server stamping states with ITS tick and the client reconciling against that? If server echoes server-tick, the client must translate server-tick ↔ its own ticks — requires knowing the offset — requires sync — that's the ClientTickRunner problem again (client must predict ahead of server tick by RTT+jitter so inputs arrive in time). That's the exact reason the old system needed the RTT machine. So: echo-input-tick = no sync needed; echo-server-tick = need offset sync. The choice is literally "which number do we put in the correction message," and it decides whether we need a clock-sync subsystem. That's a beautiful way to frame it. The correction payload should be: {lastAppliedInputTick, state}. This makes it concrete.

Also address "how to smoothly update time on join" from last message — join handshake: server sends {lastProcessedTick, state}; client starts at that+1. No offset estimation because there's no server clock to align to. The client isn't "ahead" or "behind" — it just is; server consumes on arrival. The concept of client being "ahead of the server" doesn't even exist in this design; the client is ahead only in the sense that its later inputs are still in flight.

And "how to update time (and rest of state) when client runs too far out of sync" — in this design, "out of sync" reduces to: backlog (server behind client's stream — bounded catch-up drains it) or starvation (owner stalled — pause). Plus the hard-reset for garbage. All per-object, all local, no global clock surgery.

Now let me also weigh their alternative properly in the comparison table format. Options:

A) Fixed ticks, client-labeled stream, server validates + follows (my recommendation).
B) Fixed ticks, server-master clock, client syncs ahead (old system / Source-style). Server ticks, client must align its counter to serverTick + RTT+jitter so inputs land in slots on time. Needs ClientTickRunner-ish machinery. Server-side rate enforcement free.
C) Variable dt (update-time / timestamped inputs), server simulates with client dt. Non-deterministic replay → can't do input-replay reconciliation (need state-based or full-history replay); dt must be clamped server-side (rate limiting in disguise); Q3 framerate-physics precedent; echo-mapping complexity.
D) Server-tick consumption with input labels echoed (the user's phrasing) — converges to A with extra bookkeeping; differs only in starve policy (duplicate vs pause).

Also there's E) "no ticks at all, send state + timestamp, trust-but-verify (server clamps)": that's not prediction, that's client-authoritative with server clamp-down — a legit simple genre ("client-side movement with server validation", like many casual games / MMOs do) but it forfeits the CSP contract (server IS authority). Worth mentioning as the honest "simplest possible" sibling: if you don't need server authority, timestamped state sync + server sanity clamp is simpler than any CSP. But the user explicitly stated primary goal = server auth state, so E is out, but it's worth one line because it clarifies what CSP buys: server decides, client predicts for latency hiding only.

Ratings/weights: I'll do a table with complexity, determinism/replayability, cheat resistance, bandwidth, edge-case surface.

Anti-cheat detail list for A (the "ensure correct tick timeline" ask) — concrete server-side checks:
1. Reject `tick <= lastProcessedTick` (also makes duplicates idempotent — redundancy overlap is naturally absorbed: store into buffer, skip already-applied).
2. Rate budget: processed ticks per object ≤ tickRate × (1+ε) measured on server wall clock (e.g., token bucket: refill 50/s, capacity ~5). Overflow → drop extra (client self-penalizes; its own corrections go weird → cheater hurts only itself) + optional warning log.
3. Gap: rely on redundancy (next packets fill ticks t-2..t); if gap persists for > ~6 ticks... options: pause object (consistent with starve policy!) — actually gap = starvation for those ticks; pause until filled or until gap ages past buffer window, then hard reset to latest. Simplest: treat like starve — pause. But careful: a gap in the middle with newer inputs arriving — buffer them, don't apply past the gap; if gap exceeds window (~64), hard reset dropping the gap (apply from latest). Keep it: "never apply tick t+1 before t" is the invariant; pause until filled or window exceeded → hard reset. That's clean.
4. Input semantics validation happens inside ProcessInput on server (clamp speeds etc.) — required in ALL designs, not a cost of A.
5. Cap redundant array length (≤4).

Also: where does the rate check's wall clock come from — server Stopwatch/Time.unscaledTime. Fine.

And the owner-side edge cases from the previous turn stay (hard reset on state.tick ≥ currentTick etc.).

One more thing to consider: the user says "it will just apply inputs on its tick and sync to others like with any other non-player object" — also worth noting the broadcast can stay on the server's natural cadence (its FixedUpdate / batching) regardless; the state payload labeled with input-tick. Broadcasting every applied tick at 50Hz to everyone is fine for simple CSP; could batch (send state every N ticks) as a bandwidth option but that's an optimization knob, default per-tick... Actually for simple: send one state per applied tick; mirage unreliable batch. Keep simple: every tick, unreliable. Fine. Maybe note batching as future knob. Eh — keep the reply focused; bandwidth was previous topic. Skip or one clause.

Format: This is a planning/advisory reply, no code. User asked "Please consider all this" and previously the pattern is analytical replies with headers, tables, verdict-first. Length: substantial but focused on the timing/authority question. I'll structure:

1. Verdict + the reframe: authority over state vs authority over sequencing. Client proposes (labels), server disposes (validates, simulates, broadcasts). "Follows the stream" ≠ "obeys the client" — it's "consumes the client's labeled queue at its own pace with validation."
2. The key insight about WHICH tick goes in the correction message (input-tick echo vs server-tick echo) deciding whether a sync subsystem exists — and showing their own mental model (server applies on its tick, syncs to others) already requires input-tick echo.
3. "How do we ensure the client gives a correct tick timeline" — the concrete server-side validation list (monotonic, rate token bucket, gap policy, array cap), and why none of this leaks authority: cheater can only desync itself; observers see only server state.
4. Rate-inflation as THE real threat model of client-labeled ticks, and the token bucket neutralizing it; note per-tick semantic clamps don't neutralize rate, so the bucket is non-optional. (This is the honest concession — client timeline needs exactly one check that server-master gives free; it's ~10 lines vs a sync subsystem.)
5. The update-time/variable-dt idea: what those systems are, why they're not simpler for CSP (determinism/replay, dt clamping = rate limiting in disguise, server must echo dt-mapping, Q3 framerate-physics cautionary tale). Ticks = quantized timestamps; the quantum is what makes replay and validation integer-simple. Also concede where time-based IS right in our plan: observer interpolation (already timestamped by tick×dt) and visual smoothing.
6. Comparison table A/B/C/D(E).
7. Contract additions (server validation section for OverallPlan.md): rejection rule, token bucket params, gap policy, correction payload = {lastAppliedInputTick, state}, starve=pause, join handshake. Offer to write into docs again? Previous turns ended with an offer; user keeps coming with new considerations — keep a short closing offer.

Also should I mention: the client's "correct timeline" is self-enforcing via corrections! If a malicious client sends a bogus timeline (weird jumps), the server's validation catches rule violations; if it sends a valid-but-shifted timeline, nothing happens (labels are arbitrary integers to the server; only deltas matter). That's a nice point: the tick's absolute value is meaningless to the server; only *rate* and *ordering* matter. So there's very little "correctness" to enforce — ordering + rate. The client can't lie about time in a way that matters because the server never compares the client's time to the server's time (except rate). Absolute tick value meaningless except modulo buffer indexing. 

Anti-cheat: one caveat to note honestly — input *contents* validation is the real anti-cheat surface (speedhacks via input values, aimbot-ish stuff is out of scope). Timeline checks only make the channel well-formed. In ANY CSP, a hacked client can send garbage inputs; server ProcessInput clamps are the defense. State authority means the worst case is "player does something invalid → server clamps → corrections rubber-band them." Also full anti-cheat (movement policy) is game-specific and stays in the developer's ProcessInput (server copy). Good.

Let me also sanity-check the token bucket: capacity ~5 (burst for catch-up after server stall — wait, catch-up drains backlog that accumulated while inputs arrived but weren't processed... during server stall inputs queue in transport/buffer; on resume we process up to 5/frame — that consumes backlog quickly; the token bucket on *arrival* vs *processing*? Put the bucket on arrival/acceptance of new ticks: accept at most tickRate×(1+10%) new ticks per second sustained, burst 10-20. Hmm, but catch-up doesn't need arrival-burst because inputs already arrived during the stall (they were just unprocessed). The bucket should gate acceptance of NEW highest-ticks. Burst capacity small like 5. If client hitches (accumulator clamp discards time), client loses ticks — never gains. Can client legitimately produce a burst of new ticks? After its own stall, Unity clamps so no. Editor pause? Local single-player-ish... keep bucket generous-ish: capacity ~15, refill 55/s. Numbers: fine as "e.g." values. Don't over-specify.

Wait, one more edge: server stall 2s → 100 inputs arrive during stall? They arrive and sit in the UDP receive/NetworkServer buffer, or in our per-object ring if we insert on arrival. Either way acceptance-time bucket sees them spread over the stall (they arrived over 2s) — fine, no bucket trip. On resume, drain 5/frame. Then steady state. But what if transport delivers a big burst on un-stall (server main thread frozen → all 100 processed at once in one frame's message pump)? Bucket capacity 15 would drop ~85 inputs → owner gets hard-reset corrections. Hmm — mitigate: bucket counts *new tick advance*, and on drop we don't drop silently... Simplest robust rule: bucket is generous (capacity = buffer window / e.g. 30), refill 55/s — enough to absorb message-pump bursts, still kills sustained 2× inflation. Sustained inflation is the cheat; bursts are hitches. I'll say capacity ~32 (= half the input buffer), refill tickRate×1.1. Rationale: cheap insurance, tune later. Good — keep it brief in reply: "bucket sized to absorb un-stall bursts, refill 10% over tick rate."

Also the "duplicate last input on starve" from the original plan vs pause — I already argued pause last turn; user's mental model still says "apply inputs on its tick ... like any other non-player object" which implies server keeps ticking the object. I'll keep pause recommendation but present it as: starve → pause object (it's per-object; other objects unaffected; server scene tick keeps running for everything else — "pause" is per-object simulation pause, not world pause). Make that explicit so "server has its own tick" isn't needed for anything global.

Actually — clarify terminology once: "server tick" can mean (a) global scene tick for non-player objects/timers — fine, exists, untouched; (b) per-predicted-object consumption clock — what we're simplifying to "follow the stream". Non-player objects don't need CSP at all (server sim in its FixedUpdate, broadcast states, observers interpolate — plain Mirage sync). So the user's sentence "sync to others like with any other non-player object" = exactly our observer path. Consistent.

Let me draft the reply. Structure:

**Verdict**: Client-labeled stream + server validation is server-authoritative; "server follows" describes consumption scheduling, not authority. The authority question reduces to what the server checks before applying — and that list is short. Also: the number in the correction message decides whether a sync subsystem exists.

Then sections. Keep tight. Table at end. Offer to write docs.

Length check: this is a meaty design topic, user expects depth (their asks are detailed). ~600-900 words + table. Use headers, verdict first, concrete checks. No code needed, maybe tiny payload snippet inline.

</details>

The concern is legitimate but it dissolves once you separate two things the old system had fused: **authority over state** and **authority over the clock**. "The server follows the stream" was sloppy phrasing on my part — it describes *consumption scheduling*, not authority. The precise claim: the client proposes inputs with labels; the server validates, disposes, and produces every state anyone ever sees. Nothing the client says about time is trusted — it's *checked*, and the check list turns out to be short.

## The one design choice that decides everything: which tick goes in the correction message

The reconciliation contract needs the client to know "the state I give you is the result of applying your input #N". Two options:

- **Echo the input's tick** (`{lastAppliedInputTick, state}`): the client needs no knowledge of any server clock. Its timeline is the only one that exists for this object.
- **Echo the server's tick**: the client must translate server-tick ↔ own-tick to find the buffer slot → it needs the offset → it must run ahead by RTT+jitter so inputs land in slots on time → you need the RTT moving average, the jitter stddev, the ±1% feedback loop. That is the *entire reason* `ClientTickRunner` exists.

Note that your own mental model — "server applies inputs on its tick and syncs to others" — already requires the first option: if the server applies input N at server tick M≠N, the correction still has to say "this is after input N" or the owner can't reconcile. The server's own tick becomes a private consumption scheduler with no role in the protocol. Once that's explicit, the client-labeled stream isn't a concession to the client; it's just the protocol with the redundant second clock removed.

The other half of the reassenment: to the server, the tick's **absolute value is meaningless** — only *ordering* and *rate* carry information. There is no "correct timeline" to verify beyond those two, and a client can't gain anything from a shifted-but-consistent timeline because the server never compares it to its own clock.

## How we ensure a correct tick stream (the server-side validation list)

The one real cheat vector of client-labeled ticks is **rate inflation**: a hacked client runs its counter at 100Hz so the server applies 100 inputs/second and the player moves 2× fast. Note that per-tick semantic clamps inside `ProcessInput` (max speed etc.) do *not* neutralize this — clamped-per-tick × double ticks = double speed. This is the single check that server-master designs get for free and we must write explicitly:

1. **Monotonicity**: reject `tick ≤ lastProcessedTick` as new work (already-processed ticks in the redundant array are ignored — this also makes the redundancy overlap naturally idempotent).
2. **Rate budget on arrival** (token bucket, server wall clock): accept new ticks at up to ~1.1 × tickRate sustained, with burst capacity ~half the input buffer (generous on purpose — it must absorb the message-pump burst after a server stall, while still killing sustained inflation). Overflow: drop the excess. A cheater overflow only desyncs *themselves* — their own corrections rubber-band; observers keep seeing the rate-limited, server-simulated truth.
3. **Gap policy** (invariant: never apply tick t+1 before t): redundancy fills 1–2 tick losses automatically; a persistent gap just pauses that object (same as starvation below); a gap older than the buffer window → hard reset to latest. No extrapolation from fabricated inputs.
4. **Array cap** on the redundant payload (≤4) and **input semantics validation** — the latter lives inside `ProcessInput` on the server (clamp move speed, bounds, etc.) and is required in *every* CSP design, server-master included. Timeline checks only keep the channel well-formed; `ProcessInput` clamps are the actual game-rule enforcement. That division of labor is worth stating in the plan because it's where game-specific anti-cheat (movement policy) plugs in without the CSP system knowing anything about it.

That's the full cost of client-labeled ticks: roughly 30 lines of validation. Compare with the sync subsystem it replaces.

The no-leak argument, stated plainly: **observers only ever receive server-simulated states.** A malicious owner can stall its object, move it oddly, or desync itself — none of which differ from a server-master design where a cheater sends weird inputs. It cannot corrupt anyone else's view because nothing client-authored is ever broadcast.

## On "update time instead of exact ticks"

Worth naming what those systems actually are, because there are two families:

- **Timestamped snapshot sync** (Netcode-for-GameObjects/Photon style, variable-rate sends, lerp by time): real, simple, widely used — for **remote objects**. That's exactly our observer `RemoteInterpolator`, already timestamped. It does no input replay, so non-determinism costs nothing.
- **Variable-dt input simulation** (client stamps inputs with frame times, server applies with that dt): this one is a trap for CSP. Determinism dies first — bit-exact replay of `ProcessInput(input, prev, dt)` requires identical dt, and floating-point behavior varies with dt and iteration counts, so owner-side rewind/replay can't reconstruct the server's result and every correction is a mismatch. Second, server-side dt clamping is the rate limiter in disguise — a client sending 5 inputs per frame each tagged dt=frameTime moves at input-count speed, so the server must budget total dt per wall-clock second, which is a token bucket again but with floats instead of integers. Third, the server would have to echo the exact (input, dt) application order back for the client to replay — state it gets for free with integer ticks. Quake 3 is the canonical cautionary tale: client-framerate-dependent physics (the 125Hz jump trick) came from exactly this looseness, and it was later locked to fixed 8ms steps.

A tick **is** a timestamp with a quantum, and the quantum is the feature: tick N means "N × fixedDeltaTime of simulated time" identically on every machine, which is what makes the server's validation and the client's replay execute the same arithmetic with integer indexing. The rigidity you're wary of is the same property as the correctness.

One reframe on your server-side picture, too: "server applies on its tick like any other non-player object" — non-player objects don't need any of this machinery (server simulates in its own `FixedUpdate`, broadcasts, observers interpolate: plain Mirage sync). For predicted objects, the server's `FixedUpdate` remains the *consumption scheduler* (drain up to ~5 buffered inputs per step when backlogged, pause the object when starved) — the world keeps ticking normally; only that object's simulation waits. What the server's tick no longer needs to be is a protocol-visible clock that anyone aligns against.

## Options weighed

| | A: ticks, client-labeled, server validates | B: ticks, server-master (old system) | C: variable dt / update time | D: server-tick consumption + input echo |
|---|---|---|---|---|
| Clock-sync code | None | RTT avg, jitter, 3-state timescale, skip/resync (the old file, incl. its unresolved `todo`s) | None, but must echo (input, dt) application order back | None |
| Deterministic replay | Yes (integer ticks, fixed dt) | Yes | **No** — corrections mismatch by construction | Yes |
| Cheat surface | Monotonic check + token bucket + `ProcessInput` clamps | Rate enforcement free; sync layer itself is attack surface (jitter spoofing to drift the clock) | dt-clamp bucket + contents; plus replay mismatch = permanent correction spam | Same as A |
| Join | Handshake `{tick, state}`, start at tick+1 | Must estimate RTT+jitter before first correct tick | Timestamp alignment anyway | Handshake |
| Out-of-sync recovery | Pause / bounded catch-up / hard reset, all per-object | Global re-init + drift, `OnTickSkip` | Undefined — no stable reference | Same as A |
| Complexity vs quality | **~30 lines of validation buys full CSP quality** | One extra guarantee: rate enforcement by construction | Loses the core CSP property (replay) | A + dead bookkeeping |
| Verdict | **Recommended** | Legacy weight | Simplest only if you don't do prediction at all | Converges to A |

Honest residual risk of A vs B: the token bucket is a heuristic bound, not a structural one — a client inflating 9% under a 1.1 budget moves 9% fast until `ProcessInput` clamps catch what they can. If that margin matters for your game, tighten the refill to 1.02× and accept that legit clients never trip it (their production rate is fixed by `FixedUpdate`, which cannot exceed tickRate — the accumulator only ever discards time, never banks it). That's the whole tuning surface.

## Contract additions for `OverallPlan.md`

- Correction payload: `{lastAppliedInputTick, TState}` — the input tick, never a server tick.
- Server per-object rules: reject `≤ lastProcessedTick`; token bucket (~1.02–1.1× tickRate, burst ≈ buffer/2); never apply past a gap; starve → pause object; backlog → ≤5 inputs per `FixedUpdate`; gap/hitch beyond buffer window → hard reset.
- `ProcessInput` on the server is the game-rule authority (clamps); the CSP layer guarantees only channel well-formedness.
- Non-predicted objects: ordinary server sim + snapshot broadcast; this system touches them not at all.

If this resolves the authority concern, I'll fold it into `Decision_FixedUpdate_vs_CustomTick.md` (a "who owns the clock and why it's safe" section replacing the sync discussion) and add the validation contract to `OverallPlan.md`'s server section — including rewriting lines 101–106 (drop the duplicate-last-input policy for pause).

---

## 👤 User

/switch h I dont think Determinism is all that important or realistic for unity game. you said that professional netcode like photon fusion use variable dt, that seems like an approach to explore some more. Because unity will be difficult to make deterministic, we can add "state is close enough" function that can be used to check state values to see if client needs to rollback to server state and apply inputs again, or if its close enough to keep going, for example if it is x=12.1 vs x=12.11, that is probably close enough for most games, that 100th of a unit will not make a difference for any gameplay, and the rare case it does next state will be incorrect by much more and we replay.

---

## 👤 User

I dont think Determinism is all that important or realistic for unity game. you said that professional netcode like photon fusion use variable dt, that seems like an approach to explore some more. Because unity will be difficult to make deterministic, we can add "state is close enough" function that can be used to check state values to see if client needs to rollback to server state and apply inputs again, or if its close enough to keep going, for example if it is x=12.1 vs x=12.11, that is probably close enough for most games, that 100th of a unit will not make a difference for any gameplay, and the rare case it does next state will be incorrect by much more and we replay. Can you give pseudocode for both fixed time (option A) and variable time (option C). make sure both options are the best possible they can be so we can decide what to go forward with

---

## 🤖 Assistant

<details>
<summary>Thinking Process</summary>

Let me think carefully about this. The user is challenging my determinism claim and asking for pseudocode for both Option A (fixed tick) and Option C (variable dt), both "at their best possible", to decide.

Key points to address:

1. **The user's determinism skepticism is partially right and partially wrong — and I need to be honest and precise here.** My earlier claim was that bit-exact determinism is needed for replay. The user correctly points out that:
   - Full bit-exact determinism is unrealistic in Unity (floating point across platforms, physics, etc.)
   - A tolerance-based "close enough" comparison can absorb small divergence.
   
   What I need to clarify: **epsilon-comparison (the user's "close enough" function) is not an alternative to the fixed-tick architecture — it's orthogonal to it, and in fact the plan ALREADY includes `CompareState(a, b)` with epsilon in `OverallPlan.md`.** The user is conflating two separate things:
   - (a) The time architecture: fixed tick vs variable dt
   - (b) The reconciliation comparison: bit-exact vs epsilon tolerance
   
   Epsilon comparison works with BOTH time architectures. Actually epsilon comparison is what makes fixed-tick CSP work in Unity WITHOUT bit-exact determinism. This is the crucial insight I failed to convey last turn: I overstated the determinism requirement. The correction loop with epsilon comparison is exactly how you avoid needing bit-exact determinism. What you DO need is: same dt value so the replay executes the same *arithmetic shape*, and error stays bounded rather than compounding at 2x rate.

2. **What did Photon Fusion actually do?** I should be careful here. The user says "you said that professional netcode like photon fusion use variable dt". Actually, I mentioned "Netcode-for-GameObjects/Photon style, variable-rate sends" for snapshot sync. Let me be accurate about Fusion: Photon Fusion (especially the tick-based versions, Fusion 1.2+ / Quantum) actually moved TO fixed tick-based simulation. Fusion has a fixed tick rate simulation. Quantum is fully deterministic lockstep with fixed ticks. Fusion's main mode: server-authoritative, fixed tick rate (e.g., 60Hz), client prediction with input ring buffers indexed by tick. Older Photon (PUN) is just snapshot interpolation, no prediction. So the user's premise "professional netcode like photon fusion uses variable dt" is actually wrong — Fusion is tick-based! I should correct this honestly, since I'm the one who gave the impression. My earlier statement said "Netcode-for-GameObjects/Photon style, variable-rate sends, lerp by time — for remote objects". So I said "Photon" in the context of snapshot sync for remote objects, which is accurate for PUN. But Fusion specifically is fixed-tick. I must correct this clearly — it matters for the decision.

Let me verify my memory of Fusion: Photon Fusion architecture — "Simulation Tick" fixed rate, `Runner.Tick`, input authority, state authority delta compression, client-side prediction with resimulation between `fromTick` and `toTick`. Yes, Fusion is fixed-tick with resimulation. Unity Netcode for Game Objects (NGO) doesn't have built-in prediction (snapshot interpolation only, plus a new "client sided physics movement" approach... actually NGO added prediction experiments with fixed ticks too). The new Unity Netcode prediction ("NetworkTransform prediction") is based on fixed ticks (60Hz) — Unity's CSP sample (ClientDrivenPhysics / PredictedPlayer) uses fixed network ticks. MLAPI had "lag compensation" ... The general industry consensus: CSP systems use fixed tick simulation. Variable dt is used for interpolation/rendering, not for the predicted simulation core.

So the correction: Fusion is NOT variable dt. It's fixed-tick with epsilon-ish reconciliation? Actually Fusion does full resimulation from the last confirmed tick whenever there's a mismatch — it uses delta-compressed state snapshots and resimulates from the oldest changed tick. Fusion doesn't require bit-exact determinism for its non-quantum mode — it relies on "close enough" corrections continuously? Let me think... Fusion "Host/Server mode" (shared mode is different): server simulates at fixed ticks, sends state deltas, client resimulates from the received authoritative tick forward using its stored inputs. Divergence between client resim and server is expected (non-deterministic physics), so the client just adopts the server state as the new baseline each time and replays. The key: because both run the SAME dt (fixed tick), the replayed inputs produce approximately the same trajectory; small divergence gets corrected by the next state message. Fusion calls this "prediction error correction" — they don't need bit-exact, they just re-baseline every time. Actually I believe Fusion resimulates when it receives a state that differs beyond... hmm, in Fusion, the client receives the authoritative state for tick T, compares with its predicted state at T, and if different, rewinds to T and resimulates to current tick. "If different" — Fusion may use exact comparison for its delta compression but for gameplay objects developers typically compare with tolerance... I'm not 100% sure of the internal details. I should be careful not to fabricate specifics. I can say: Fusion's simulation runs at a fixed tick rate; Quantum is explicitly deterministic. I shouldn't overclaim details about Fusion's comparison policy.

3. **The epsilon insight the user has is actually correct and important — but it CHANGES the failure mode analysis, not the time architecture.** With epsilon comparison:
   - Small divergence (x=12.1 vs 12.11) → keep going. Error doesn't trigger replay.
   - BUT: the divergence is now *state carried forward*. The client's next prediction continues from 12.1, server from 12.11. If the next input is "move forward", both add ~same delta, so the error stays ~0.01 — it doesn't compound per tick in normal movement because the delta applied is input-driven, not state-driven... Actually it CAN compound if the dynamics are state-dependent (e.g., acceleration: v depends on state, position depends on v). With multiplicative error growth, 0.01 error can grow. But empirically it stays small for most gameplay. This is exactly why "close enough" works in practice: error growth is slow, and each authoritative message re-baselines, keeping drift bounded.
   - The important subtlety: with epsilon tolerance, you DON'T replay from the server state on small mismatches — you accept the drift and continue. The server state becomes the new baseline for the buffer slot (you should still STORE the authoritative state in the buffer as the corrected baseline for future replays! Important detail: if you "keep going", you should update your stored state at serverTick to the server's value, so any future replay starts from truth, not from your drifted prediction. Otherwise errors compound across replays.)

4. **Why does variable dt still break even WITH epsilon comparison?** Let me think very carefully, because the user's proposal makes variable dt *more* viable than my last turn claimed. I claimed variable dt breaks determinism → replay can't reconstruct → every correction mismatches → correction spam. With epsilon comparison, that argument weakens: if client replays with a slightly different result and it's "close enough", it keeps going. So does variable dt work now?

Let me think through variable dt CSP properly (best possible version):
- Client at frame i: dt_i = Time.deltaTime (capped). Reads input_i. Simulates: state_{i+1} = ProcessInput(input_i, state_i, dt_i). Sends {input_i, dt_i, t_i} to server (t_i = client timestamp).
- Server receives (input_i, dt_i, t_i) at wall time T. Simulates with the SAME dt_i (client-provided) — so arithmetic is identical *if* the server uses the same dt and same starting state. Server then broadcasts {t_i (or i), state} to all clients including owner.
- Owner receives {t_i, serverState}: compares against predicted state at t_i with epsilon. If close → continue. If far → rewind to serverState and replay buffered (input_j, dt_j) pairs.
- Determinism requirement: none bit-exact — but the REPLAY must produce approximately the same trajectory. With variable dt, replaying (input, dt) pairs with the same dt values reproduces approximately the same trajectory as the server IF the server used the same dt values. So the server must trust/use the client's dt values. That's a cheat vector: client can send dt=0.5 for one input = fast movement. Server must clamp: dt ∈ [0, min(0.1, ...)]. Clamped dt per input × input count per second = speed budget, so server needs the token bucket on *total dt per second* (as I said last turn: "the rate limiter in disguise").

Now the real problems with variable dt CSP, being maximally fair:

**Problem 1 — physics.** The user's game will use Rigidbody/CharacterController. Unity physics runs in FixedUpdate. A variable-dt simulation step doesn't line up with physics steps. If predicted objects use physics at all, variable dt forces manual physics stepping (autoSimulation=false + Physics.Simulate(dt)) — the nightmare. With FixedUpdate CSP, physics just works. This remains THE dominant practical issue for Unity regardless of epsilon.

**Problem 2 — replay quality with divergent dt sequences.** When the server clamps or modifies dt (or the client hitches and Unity clamps its dt), the server's applied dt differs from what the client used locally. Then the client's stored (input, dt) pairs replay to a different trajectory than the server's. Errors are proportional to (dt difference × velocity) — and unlike the fixed case, these errors occur at *every* replay where clamping happened, and clamping happens precisely during hitches (when replays happen). Epsilon absorbs small ones. So with epsilon, variable dt becomes... roughly workable? The quality question becomes how often replays produce visible snaps.

Hmm, actually, let me reconsider. Let me think about what "the best possible variable dt CSP" looks like and be honest about its viability:

Actually there IS a class of systems that does "variable dt CSP": they're rare. Let me think... The "Saints & Sailors"... no. Source engine? Source runs fixed ticks (cl_cmdrate). Overwatch fixed 60Hz... hmm. Actually let me think about games: all major CSP implementations use fixed ticks. The variable-dt approach appears in some indie netcode and in "state sync + client extrapolation" hybrids. The user heard of systems using "update time" — probably referring to interpolation-by-timestamp or to Photon PUN's non-predicted sync. Or possibly they mean things like Unity's "ClientNetworkTransform" (no prediction, direct client authority) or the approach where you just lerp to server state and keep simulating with Time.deltaTime locally (no true reconciliation — i.e., "correction smoothing" style, like NGO's old approach or Mirror's old "ClientSidePrediction with smoothing" — actually JamesFrowen's simple CSP in Mirror examples did fixed ticks...).

Wait — important: the old complex system being replaced is JamesFrowen.CSP (per the file namespace). There's ALSO a known "simple" CSP that JamesFrowen prototyped for Mirror (the "SimplePrediction" or the "basic prediction" from the Mirror examples repo?) Not sure. Not important.

So for the comparison, I need:
- Option A pseudocode: fixed tick, FixedUpdate-driven, epsilon comparison (upgraded: this is the key fix to my earlier position — no bit-exact requirement, `CompareState` with epsilon, re-baseline on accept).
- Option C pseudocode: variable dt, best possible: client stamps dt, server clamps, token bucket on dt budget, replay of (input,dt) pairs, epsilon comparison, characterizing where it degrades.

And I must be scrupulously fair to C, then show where they differ concretely so the user can decide. My honest assessment: C works for kinematic-only, no-physics games, at real cost in cheat surface and replay fidelity; A works for everything Unity and has essentially no downside once epsilon replaces bit-exactness. The epsilon insight the user contributed actually *strengthens* A (removes the one theoretical weakness: Unity non-determinism would cause replay mismatches — with epsilon, it doesn't matter).

Let me nail the technical details for each pseudocode.

**Option A: Fixed tick, FixedUpdate, epsilon reconciliation**

Client (owner), per FixedUpdate:
```
input = GetInput()
InputBuffer[tick] = input
state = ProcessInput(input, state, tickDeltaTime)   // pure logic; tickDeltaTime constant
ApplyState(state)
StateBuffer[tick] = state
Send Unreliable {tick, inputs[tick-2..tick]}
tick++
```
On receiving {ackTick, serverState}:
```
if ackTick + bufferSize <= currentTick: return (stale, slot overwritten — hard reset case actually)
if ackTick >= currentTick: hard reset (server ran ahead)
predicted = StateBuffer[ackTick]
if CompareState(predicted, serverState, eps):
    // ACCEPT: re-baseline so drift doesn't compound
    StateBuffer[ackTick] = serverState  // optional but wise
    return
// MISMATCH: rewind & replay
state = serverState
StateBuffer[ackTick] = state
for t in ackTick+1 .. currentTick-1:
    state = ProcessInput(InputBuffer[t], state, tickDt)
    StateBuffer[t] = state
ApplyState(state)
emit CorrectionEvent(offset = state - previousPresentState) // for smoother
```

Key design points for best-A:
- Epsilon comparison is developer-defined per-state (CompareState) — already in plan.
- Re-baseline on accept: store serverState into the buffer slot even on accept. Hmm — subtle: if we re-baseline on accept, then a LATER replay starting from that slot uses truth. Good. But it also means the "accept" path isn't free of writes. Fine, one struct copy.
- Actually wait, there's a subtlety: if we accept and re-baseline StateBuffer[ackTick] = serverState, but the current *live* state continues from the predicted one — the buffer and live state diverge at that slot... The live present state was derived from predicted-at-ack. The buffer slot now holds server truth. If a future replay occurs starting from ackTick, it replays from truth — fine, that's the point.
- Threshold design: two thresholds actually? The user's "close enough" — single epsilon. But there's a refinement: position error that's small but *velocity* error large can matter. Keep it developer-owned: CompareState is fully theirs; system calls it with (predicted, authoritative).
- One more critical point: what does CompareState failure cost? Replay of ~(RTT × tickRate) ticks — bounded by buffer. Standard.

Also worth adding for best-A: since we're epsilon-based, replay executes ProcessInput with the same constant dt — divergence between client replay and server sim comes only from float non-determinism and world queries (physics raycasts etc.) — small. So replays converge.

**Option C: Variable dt, best possible**

Design goals: free the client from FixedUpdate; sim in Update with Time.deltaTime (capped); server applies client's dt (clamped); owner reconciles against server states by matching client frame indices/timestamps.

Mechanics:
- Client numbers its local frames (still an integer counter! — you need *some* index to key the buffers). Variable dt does NOT remove the need for input indexing; it removes the fixed dt. So: frame index i, dt_i.
- Client sends {i, dt_i, input_i} (plus redundancy window of previous frames).
- Server: maintains budget bucket: total dt accepted per wall-clock second ≤ (1 + margin) × 1s. Clamps each dt_i to [0, maxStep] (e.g., 0.033s or 0.05s). Applies input_i with clamped dt_i'. Tracks lastAppliedFrame; never applies i+1 before i.
- Server broadcasts {i, state_i} (and for observers, timestamp = client time of frame i).
- Owner reconciliation: same as A but replay uses stored (input_j, dt_j') where dt_j' is the *clamped* value the server used — client must pre-clamp identically so replay matches (client clamps its local dt the same way before simulating; then server's clamp is a no-op unless the client lies, in which case the server's clamp causes divergence for the cheater only — actually that's a nice property: honest clients always replay exactly what the server applied).
- Observers: interpolate by timestamp t_i = client frame time — but wait, observers need a smooth clock; variable dt snapshots arriving at variable rate with variable spacing — interpolation by timestamps handles it (RenderTime = now - delay, find bracketing frames). Fine.

The REAL costs of C, concretely (not determinism anymore — epsilon handles that — but):

1. **Physics integration.** C-predicted objects can't use Unity physics/CharacterController naturally: physics steps at fixed rate; your sim steps at frame rate. CharacterController.Move must be called per physics step... you'd drive everything from Update with deltaTime and not use FixedUpdate physics at all → kinematic transforms only, or manual Physics.Simulate. A uses physics natively. THIS is the big one for Unity. (CharacterController.Move in Update with variable dt is actually okay-ish — it's not physics-solver-based. But colliders/rigidbodies are out; and collision detection at variable steps = tunneling risk: fast object, dt spikes to 0.05 → moves further per step → clip through thin walls. Fixed small steps have less tunneling; and you can't do CCD cheaply. Mitigation: cap dt low (0.033) and multi-substep = reimplementing fixed ticks, badly.)

2. **Movement feel ties to frame rate.** Integration error varies with dt (Euler integration: error scales with dt² per step for acceleration). Client at 30fps vs server... wait, server uses client's dt so they match. But the SAME player with frame spikes gets different movement behavior (jump distance changes with framerate — the Quake 125fps issue). You can mitigate by clamping dt to a max — but then during hitches, sim time < wall time = character moves slower during lag (a hitch slows your character). Or substep: multiple steps of ≤maxStep per frame = fixed ticks again.
   - Actually this is the deep truth: "variable dt with clamp + substep" CONVERGES to fixed ticks. The only genuinely variable-dt version is one with unclamped dt and all its pathologies.

3. **Cheat surface.** Server must budget Σdt (token bucket in seconds), clamp per-input dt, AND cap inputs/second. Each clamp is a divergence source for the cheater (fine) but also for an honest client whose local dt differed from the server's clamp (e.g., client hitches: Unity deltaTime comes clamped to maximumDeltaTime (0.1s? actually Time.deltaTime in Update is clamped to maximumDeltaTime = 0.1 by default... wait no — Time.maximumDeltaTime affects both fixed and frame delta clamp? Time.deltaTime per frame is clamped to maximumDeltaTime, yes, default 0.1s. Hmm, actually I believe frame deltaTime is not hard-clamped by maximumDeltaTime — maximumDeltaTime is documented as the max for frame delta "the maximum amount of time the frame can take"... Let me recall: Time.maximumDeltaTime: "The maximum time a frame can spend on frame processing... if a frame takes longer, it's clamped". Yes frame time is clamped to maximumDeltaTime for Time.deltaTime. OK not critical to be precise; I'll just say cap dt in code.)
   In A, the cheat surface is just "ticks/second" (one integer token bucket). In C, it's dt-clamp + dt-budget + count budget, and honest-client divergence happens whenever the client's own dt exceeded the server's cap while the client simulated with the uncapped value... no wait — honest client pre-clamps locally with the same rule, so it simulates with capped dt, matching the server. Then during a hitch, the client's character slows down (sim covers less than wall time) while others keep moving = local slow-motion for the hobbled player. In A, Unity's fixed accumulator also clamps (maximumDeltaTime 0.1 → max 5 ticks per frame, but it *catches up* over subsequent frames via accumulated... no — Unity clamps the accumulator too, discarding beyond maxDeltaTime. So A also gives slow-motion during long hitches. Equivalent here.)

4. **Bandwidth.** C needs dt_i per input (4 bytes) + timestamps for observers. A packs (tick delta implicit, inputs only) — actually A sends tick (4B) too. Roughly equal. Not a real differentiator. Actually — one subtle differentiator: in C the redundancy window must include the frame's dt too, or assume the repeated frame has same dt — meh, equal-ish.

5. **Replay cadence.** In A, replay re-runs ProcessInput with fixed dt; the *number* of replay steps is bounded by buffer. In C, same, with stored dt's. Equivalent machinery. Fine.

6. **Interpolation of observers** — equivalent (timestamps vs ticks).

So the honest verdict: with epsilon reconciliation (user's contribution), C's *reconciliation* machinery is actually fine — I should concede that genuinely. My last-turn claim "corrections mismatch by construction" was overstated: with epsilon, they don't necessarily mismatch. The REAL blockers for C in Unity are (a) physics incompatibility, (b) framerate-dependent movement feel/tunneling, and the fixed-dt clamp/substep convergence argument. And C's costs that remain: bigger cheat surface, framerate-dependent gameplay.

And the honest verdict for A with epsilon: my "needs determinism" claim downgrades to "needs same dt" — which fixed ticks give by construction. Float non-determinism between machines is absorbed by epsilon + re-baseline. So A loses nothing from dropping bit-exactness, and A never required bit-exactness to begin with once CompareState exists — the plan already had CompareState! So actually the user's epsilon idea is ALREADY in their plan (OverallPlan.md line 38: `CompareState(TState a, TState b)`: checks if within acceptable epsilon). I should point that out — their instinct is already the plan's design.

Now, how do corrections behave in A without bit-exactness? Consider: client replays with its own float paths; server sim has slightly different floats; states differ by ~1e-3 typically; epsilon (say 0.01–0.05) absorbs; re-baseline on accept keeps drift bounded. Physics-driven predictions (velocity from solver) can diverge more — epsilon catches it as a "mismatch" → replay → replay ALSO produces the client's variant... → potential correction loops if the error is systematic and above epsilon. Mitigations: raise epsilon for physics-sourced fields; or snap-accept the server value when error is below a *soft* threshold and replay only above it. Two-threshold design: `SnapThreshold` (always re-baseline silently, no replay) vs `CorrectionThreshold` (replay). Hmm — actually simpler: single CompareState boolean = "do I need to replay?" and ALWAYS re-baseline the buffer slot with the server state (on both paths). That's the clean design. Let me put that in the A pseudocode: on accept, still write server state into the slot. This kills drift compounding AND makes future replays start from truth. 

For C's pseudocode, be maximally fair — include: clamp rule shared client/server, dt budget, frame-indexed buffers, replay with server-effective dt's, epsilon compare, observers by timestamp. Then the comparison table focuses on the REAL differentiators.

Also the user asked me to correct the Fusion claim. I said "Photon style" for snapshot interpolation (PUN), and the user heard "Fusion uses variable dt". Fusion: fixed tick (Runner.Tick, SimulationTickRate 60 default... actually default tick rate in Fusion is 60Hz server mode). Quantum: deterministic fixed ticks. NGO prediction (new, 2023-2024, "Client-Side Prediction" package / netcode for gameobjects added "prediction" with fixed tick 60Hz). So: every production CSP system the user might cite uses fixed ticks; variable dt appears in snapshot sync (interpolation) — the confusion is real and worth 3 sentences to fix, since the user said "I have heard of systems that avoid fixed times... use update time". Those systems = snapshot interpolation + extrapolation for REMOTE objects (no replay). No production CSP uses variable-dt replay. Should I hedge? Let me be careful about NGO's newer prediction: it's fixed-tick (tunneling... no). Unity's new "client prediction" in NGO v2... The "NetworkObject prediction" samples use fixed 60Hz ticks. OK.

But the user asked "make sure both options are the best possible they can be so we can decide". So deliver: two solid pseudocode blocks, with the epsilon logic in both, honest failure-mode analysis, comparison table, recommendation.

One more thing to get right in A: with epsilon + re-baseline, do we still replay on mismatch? Yes — replay from server state using buffered inputs (that's CSP). The user said "the rare case it does [matter], next state will be incorrect by much more and we replay" — exactly matches.

Also address: "Determinism is not all that important or realistic for Unity" — agree, with precision: what CSP actually needs is (1) same dt, (2) same code path, (3) bounded divergence — epsilon absorbs the rest. Unity gives you 1 and 2 for free with fixed ticks; 3 is handled by re-baseline. Bit-exactness is a Quantum-style lockstep requirement, not CSP's. So we agree — and this agreement doesn't hurt A at all.

Let me also reconsider: is there any case where C is genuinely better? Fair weighing:
- If the game is UI-ish, kinematic, no physics, wants sim tied to render frames (e.g., smooth 144Hz local movement without interpolation), C gives per-frame prediction steps — but A + visual interpolation in Update gives the same smoothness (predict at 50Hz physics, render-interpolate at 144Hz). Actually for LOCAL feel, C has an advantage: prediction integrates every render frame, so local control feels perfectly frame-locked (no FixedUpdate quantization of input sampling). In A, inputs are sampled at FixedUpdate rate — at 30Hz simulation, a 144Hz player's mouse movements are quantized... for movement WASD this is imperceptible; for aiming, you don't predict aim through the sim (camera is local, unsimulated). So the local-feel advantage of C is mostly illusory for typical character movement, and A+Update-interp covers visual smoothness.
- C avoids the FixedUpdate-rate coupling (50Hz sim when you want 20Hz net? — but C sends every frame = MORE bandwidth; A can subsample: send input every tick, tick=net rate, physics runs 50Hz... no wait in A sim rate = send rate = physics rate. To send at 20Hz in A you'd tick at 20Hz and physics misaligns... or tick 50 and batch. Meh — keep tick=physics rate, this is fine for v1 and simpler).
- C's honest best case: pure kinematic games (top-down, no Unity physics, no CharacterController) — it CAN work. But it still has framerate-dependent integration and a wider cheat surface for zero quality gain. So even there A wins.

Frame-rate-dependent movement: the Quake point. In C, jump arcs depend on fps unless you substep → substepping with cap = fixed dt. This is the "C converges to A" argument — the strongest single argument: any fix for C's problems re-derives fixed ticks. I'll include the "maxStep substep" pseudocode line to show the convergence.

Let me now also make sure the A pseudocode is complete and BEST: include
- FixedUpdate owner loop
- redundancy send
- server receive: order/monotonic, gap handling, token bucket (integer), pause on starvation, bounded catchup
- server FixedUpdate consume
- correction ClientRpc {ackTick, state}
- owner reconcile: accept-accept-vs-replay both re-baseline; replay loop; apply; event for smoother
- observers: buffer + interpolated render by tick time (tick × tickDt)
- hard resets both directions
- join handshake

And C pseudocode:
- Update loop: dt = min(Time.deltaTime, MaxStep) [shared clamp], maybe substep while remaining>0 (showing convergence), frame counter
- send {frame, dt, input} × redundancy
- server: dt budget bucket (float seconds), clamp, monotonic, gap → pause, broadcast {frame, state, clientTime}
- owner reconcile with epsilon, replay with stored effective dts
- observers interpolate by clientTime timestamps
- hard resets

Both must show epsilon clearly since that's the user's addition — it's in BOTH, so it stops being a differentiator (key point!).

Also correct my earlier statement explicitly: "I overstated: with your epsilon threshold, variable-dt replay doesn't necessarily mismatch. The comparison shifts to these axes: physics integration, framerate-dependent feel, cheat surface, machinery size."

Also the Fusion correction: I must fix the record — Fusion is fixed-tick (Runner.Tick, fixed tickrate); Quantum is fully deterministic. What uses variable-dt: PUN/NGO snapshot sync = remote-object interpolation, no prediction/replay. So "professional netcode uses variable dt for CSP" is a misremembering; variable dt lives in the *rendering/interpolation* layer of every system (including ours — RemoteInterpolator and visual smoother run in Update with Time.deltaTime).

Wait — actually let me double-check Fusion. Photon Fusion 1.x: "Fusion simulates at a fixed tick rate"... yes, `Runner.DeltaTime` fixed; `Runner.Tick` integer. Simulation Behaviour callbacks: `FixedUpdateNetwork` — the name literally says Fixed. Confirmed. And there's `Render()` for variable-dt visual layer. Good — the A/C split maps exactly: fixed sim layer + variable render layer. Fusion does A's architecture + C-style render layer. That's a strong supporting point for the recommendation.

Also, the user's epsilon example: x=12.1 vs x=12.11 → 0.01 apart. With character at 5 m/s and 50Hz: per-tick movement 0.1m. A 0.01 error = 10% of a tick-step — imperceptible. Good example validation. Also note: re-baseline-on-accept is what makes "keep going" SAFE — without it, each accepted error persists and can compound through state-dependent dynamics; with it, the accepted error only affects the present visual (once), not the future trajectory. This is the subtle piece that makes the user's idea robust. Their plan's CompareState exists but the re-baseline-on-accept is the missing piece I should add — actually their plan says on Match: "Do nothing." — that's the bug to fix in OverallPlan (line 88: "If Match: Do nothing. Discard older history"). My earlier review already flagged the "discard history" part; now the "do nothing" part also needs the re-baseline tweak.

Present-state divergence subtlety on accept: if we accept at ackTick but don't replay, the LIVE current state still derives from our drifted prediction. Do we snap the live object to server state? No — we keep going from our current (slightly drifted) present. The re-baseline only fixes the BUFFER slot. So the live object carries the drift until the next mismatch/replay — that's fine and intended (invisible). But then: future replay starts at ackTick+1 from server truth and produces a present that may differ from the live object by the accepted epsilon — on mismatch we ApplyState(replayed present) which snaps by that epsilon — visible? Small. The smoother absorbs. Alternatively always re-replay from ackTick on every accept... no, that's a replay per packet = expensive. Keep: accept = buffer re-baseline only. Good.

One more C-specific failure to mention: input sampling at render rate in C means dt varies per frame; storing (input, dt) pairs — the redundancy window re-sends older frames' (input,dt). Fine.

And the bandwidth point: C naturally sends one input per render frame (e.g., 144/s) vs A 50/s → 3× bandwidth unless C decouples send rate from sim rate (batch every 50ms) — which re-introduces... no actually C can batch: send every N frames with all pairs — fine, minor complexity. I'll mention briefly.

Now the structure of my reply:
1. Corrections & concessions up front: (a) agree on determinism — and show it was already the plan's design (CompareState), plus the missing re-baseline piece their idea needs; (b) fix the Fusion misattribution (I caused it — own it) — Fusion/Quantum/NGO prediction are fixed-tick; variable-dt lives in interpolation layers; (c) concede that with epsilon, my "replay mismatches by construction" argument against C is withdrawn — C becomes viable in kinematic-only games; the real differentiators are now physics, framerate-dependence, cheat surface.
2. Shared insight: what CSP actually requires instead of bit-exactness (same dt + same code + epsilon + re-baseline).
3. Option A pseudocode (complete: owner loop, send, server receive/consume, reconcile with epsilon+re-baseline, observers, resets, join).
4. Option C pseudocode (best possible: shared clamp, budget bucket, frame-indexed replay, epsilon, timestamps; show the substep convergence line).
5. Comparison table on the axes that survived.
6. Recommendation: A for sim + C-style Update layer for render (which is what the plan already has: RemoteInterpolator + smoother in Update). And note epsilon is already in the plan; add re-baseline.

Length: this needs pseudocode for both — it'll be a longer reply, but pseudocode is what was asked for. Keep prose tight, make the code carry the weight. The user said "make sure both options are the best possible they can be so we can decide what to go forward with" — so code must be complete-ish but readable, not a full implementation. C#-ish pseudocode.

Also per instructions: user's word is ground truth — they say determinism isn't realistic for Unity. I agree with precision, not sycophancy: agree that bit-exactness is unnecessary, clarify what IS needed (which is cheap in A), and correct the record on Fusion factually since they explicitly cite it ("I have heard of systems..."). They asked me to explore it more, which I'll do honestly.

Let me draft the pseudocode carefully.

**Shared piece — the epsilon reconciliation (user's contribution, fixed up):**

```csharp
// Called when an authoritative state arrives for tick T.
// Reconcile(StateBuffer[T], serverState) — three zones:
//   match   (<= eps):     keep live sim; RE-BASELINE buffer slot to truth
//   mismatch (> eps):     rewind to truth, replay buffered inputs
// On BOTH paths the buffer slot ends up holding the authoritative value,
// so accepted drift can never compound through future replays.
```

**Option A:**

```csharp
// ==== OWNER CLIENT (per-object state machine) ====

// constants
tickDt = 1f / tickRate;                 // = Time.fixedDeltaTime, same in all builds

void FixedUpdate()                        // owner only
{
    if (!HasAuthority || !IsClient) return;

    TInput input = GetInput();
    InputBuffer[tick] = input;

    state = ProcessInput(input, state, tickDt);   // pure; no Time.deltaTime inside
    ApplyState(state);                    // move transform/rigidbody
    StateBuffer[tick] = state;

    SendInputs(tick, last: 3);            // unreliable: {tick, [in_t, in_t-1, in_t-2]}
    tick++;
}

[ClientRpc] // from server, unreliable: {ackTick, state}
void OnAuthoritativeState(uint ackTick, TState serverState)
{
    // -- guards --
    if (ackTick + BufferSize <= tick) return;      // slot already overwritten → ignore (rare)
    if (ackTick >= tick) { HardReset(ackTick, serverState); return; }  // server ahead → adopt truth

    // -- epsilon reconciliation --
    if (!CompareState(StateBuffer[ackTick], serverState))   // developer-defined epsilon
    {
        // MISMATCH: rewind to truth, replay our inputs
        state = serverState;
        for (uint t = ackTick + 1; t < tick; t++)
        {
            state = ProcessInput(InputBuffer[t], state, tickDt);
            StateBuffer[t] = state;
        }
        ApplyState(state);
        OnCorrection?.Invoke();          // visual smoother hook
    }
    // BOTH paths: re-baseline so drift never compounds into future replays
    StateBuffer[ackTick] = serverState;
}

// ==== SERVER (per predicted object) ====

Dictionary<uint, TInput> inputQueue;      // keyed by client tick
uint lastProcessedTick;                   // per object
TokenBucket rateBudget;                   // refill ~1.02..1.1 × tickRate per sec, burst ≈ BufferSize/2

[ServerRpc(requireAuthority = true)]      // unreliable: {tick, inputs[0..2]}
void OnInputs(uint latestTick, TInput[] inputs)
{
    for (i, input in inputs)
    {
        uint t = latestTick - i;
        if (t <= lastProcessedTick) continue;              // duplicate/overlap → idempotent
        if (!rateBudget.TryTake()) { /* drop; sender will self-correct */ continue; }
        inputQueue[t] = input;
    }
}

void FixedUpdate()                        // server
{
    int steps = 0;
    while (steps < MaxCatchupPerTick && inputQueue.ContainsKey(lastProcessedTick + 1))
    {
        lastProcessedTick++;
        state = ProcessInput(inputQueue[lastProcessedTick], state, tickDt);
        ApplyState(state);                                    // sim = authoritative truth
        steps++;
    }
    // starvation (gap or nothing buffered): PAUSE object this tick — no extrapolation
    // gap persists past buffer window → HardReset: jump lastProcessedTick past gap

    if (steps > 0)
        SendCorrection(lastProcessedTick, state);            // unreliable ClientRpc, every N ticks ok
}

// ==== OBSERVER (non-owner client) ====
// RemoteInterpolator: buffer {tick, state} by arrival; renderTime = serverTimeNow - delay(100ms);
// lerp between bracketing snapshots; state timeline = tick × tickDt.
```

Hmm — for the server's FixedUpdate drain: the normal case is exactly one new input per FixedUpdate (steps==1). The while-loop with MaxCatchup 5 handles backlog. And "SendCorrection every N ticks ok" — actually send each processed tick's state? Bandwidth: 50/s states per object. Common optimization: send corrections only every K ticks (e.g., every 2-5) — keep simple: send each tick, mention dedup option in a comment.

Also note: the server should also send corrections even when PAUSED? No — if paused, no new authoritative state. Owner keeps predicting with its own inputs — divergence grows only if the server disagrees with inputs applied... when paused there's nothing to disagree with; owner's inputs remain buffered server-side. On resume the server catches up and corrections resume. Fine.

One subtlety on server pause: while starved, owner keeps ticking ahead (client can't know) — buffered inputs queue server-side; bounded catch-up drains. If client got > BufferSize ahead during long starvation, server hard-resets the object: jump lastProcessedTick to newest, replay nothing, send state; owner receives ackTick >= its oldest unacked... the owner-side guard `ackTick + BufferSize <= tick → ignore` hmm — when server hard-jumps FORWARD, ackTick might exceed owner tick? No — owner ticks at real time; server processed fewer ticks; ackTick ≤ owner tick always (server can't process inputs the client hasn't sent). Server jump-forward lands at newest received = still ≤ owner tick. OK. The `ackTick >= tick` hard reset on owner triggers only if... server processed at same rate but owner's buffer slot overwritten: guard 1. Fine — keep both guards, they're cheap.

**Option C:**

```csharp
// ==== OWNER CLIENT ====
const float MaxStep = 1f / 30f;            // shared clamp rule (client + server)
int frame;                                 // still an integer index — buffers need keys

void Update()                              // sim at render rate, not physics
{
    float dt = Math.Min(Time.deltaTime, MaxStep);   // same clamp the server applies
    // NOTE: no Unity physics usable here — kinematic transforms / manual collision only.
    // (Adding `while (remaining > 0) Step(MaxStep)` here = reinventing fixed ticks.)

    TInput input = GetInput();
    InputBuffer[frame] = input;
    Deltas[frame] = dt;

    state = ProcessInput(input, state, dt);
    ApplyState(state);
    StateBuffer[frame] = state;

    SendInputs(frame);                     // unreliable {frame, dt, input} + redundancy window
    frame++;
}

[ClientRpc] void OnAuthoritativeState(uint ackFrame, TState serverState)
{
    if (ackFrame >= frame) { HardReset(ackFrame, serverState); return; }
    if (!CompareState(StateBuffer[ackFrame], serverState))
    {
        state = serverState;
        for (uint f = ackFrame + 1; f < frame; f++)
            state = ProcessInput(InputBuffer[f], state, Deltas[f]);   // replay EXACT dt the server used
        ApplyState(state);
        OnCorrection?.Invoke();
    }
    StateBuffer[ackFrame] = serverState;               // re-baseline (same as A)
}

// ==== SERVER ====
float dtBudget;                            // token bucket in SECONDS, not ticks
void OnInputs(uint frame, float dt, TInput input)
{
    if (frame <= lastProcessedFrame) return;             // monotonic
    dt = Math.Min(dt, MaxStep);                         // rule must match client's clamp
    if (dtBudget < dt) { Drop(); return; }               // Σdt per wall-second ≤ 1s (+margin)
    dtBudget -= dt;
    inputQueue[frame] = (input, dt);
    frameTime[frame] = accumulatedClientTime;           // timestamp for observers
}
void Update() { ...consume queue in order, pause on gap, budget refill from wall clock... }

// Observers: identical interpolation, but bracket by clientTime stamps instead of tick×tickDt.
```

The key C-specific points: (1) dt must travel with the input, (2) clamp rule shared so honest replays match exactly, (3) budget in seconds, (4) no physics, (5) framerate-dependent integration (jump arcs vary with fps — Euler error ∝ dt²... more precisely for acceleration: error per step ∝ dt² × a, total over fixed time ∝ dt × a × ... eh, simply: integration error depends on step size → gameplay depends on framerate), (6) substepping to fix 5 = fixed ticks.

Also bandwidth: C sim rate = frame rate → sends per frame unless batched.

Now the comparison table (post-concession axes):

| Axis | A fixed tick | C variable dt |
- Physics (Rigidbody/CharacterController): native / incompatible (manual sim only)
- Movement behavior vs framerate: identical everywhere / varies (jump arcs, tunneling at high dt)
- Replay fidelity: exact same dt by construction / matches only while clamps agree; server drops = owner divergence (still epsilon-absorbed usually)
- Cheat surface: 1 integer bucket / dt clamp + seconds budget + count budget
- Bandwidth: 50 msg/s fixed / frame-rate-coupled unless batching added
- Starvation/catchup: pause + bounded drain (same both) — equivalent
- Machinery: tick counter / frame counter + dt array + budget in seconds — C is a strict superset (~+20 lines) for zero quality gain
- Local feel at high fps: 50Hz sim + Update interpolation (smoother) / per-frame integration (marginally tighter, invisible below ~144Hz with interp)
- Determinism requirement: neither requires bit-exact — epsilon + re-baseline in both

Verdict: A. And note: A already contains C where C is good — the *render* layer (RemoteInterpolator, visual smoother) runs in Update with Time.deltaTime. Fixed sim + variable render = Fusion's split (FixedUpdateNetwork vs Render). So the user's instinct "update time is good for rendering" is incorporated; the sim core stays fixed.

Also add: with epsilon, drop my earlier "ProcessInput must be pure/deterministic" hard line to: pure of Time queries (no Time.deltaTime reads — pass dt explicitly), world queries allowed and absorbed by epsilon. That's a relaxation worth stating: A without bit-exactness = "bounded-divergence CSP". The one rule that stays: ProcessInput receives dt as a parameter, never reads Time.* — in A dt is the constant tickDt; that's the entire determinism requirement in practice.

Also their point "next state will be incorrect by much more and we replay" — validate: error growth through input-driven deltas is typically slow (error persists ~constant, doesn't multiply per tick unless dynamics are unstable, e.g., balancing on a ledge corner); epsilon + re-baseline bounds it. One caveat to note: epsilon comparison on the WRONG field (position only) can miss velocity divergence — recommend CompareState include velocity when the state has it.

I think I should also answer "so we can decide what to go forward with" with a clear recommendation: A, and enumerate exactly what from C survives into the design (variable-dt render layer, epsilon idea — already present, timestamped interpolation). Make the decision easy.

Format: intro concessions (short), "what CSP needs instead of determinism" (short), A pseudocode, C pseudocode, table, recommendation. Use ### headers. Pseudocode in C#-flavored blocks. Keep prose lean. No tools needed this turn (planning discussion). The user asked for pseudocode — deliver it in-message.

Length check: two ~40-line pseudocode blocks + table + short prose. That's appropriate given the explicit ask.

</details>

Three corrections to my own record before the code, because they change the comparison:

**1. You're right about determinism — and your plan already agreed.** `OverallPlan.md` line 38 has `CompareState(a, b)` with an epsilon. Bit-exact float reproduction was never actually required by that design; I overstated it. What CSP truly needs is much weaker: **same dt, same code path, epsilon tolerance, and one missing piece your version needs** — on "close enough", don't just keep going: **write the server state into the buffer slot anyway** (re-baseline). Otherwise accepted drift (your 12.1 vs 12.11) persists in the history and compounds through state-dependent dynamics; with re-baseline it affects the present visual once and dies. With that fix, your 12.1/12.11 example behaves exactly as you say: keep simulating, next authoritative message re-baselines, no replay. Bit-exactness is a lockstep requirement (Quantum-style); CSP needs "bounded divergence," which epsilon + re-baseline gives you in Unity.

**2. I mis-sourced the Fusion claim — my fault.** Photon **Fusion is fixed-tick** (`Runner.Tick`, fixed `DeltaTime`, the sim callback is literally named `FixedUpdateNetwork`; Quantum is fully deterministic lockstep). What's variable-dt in Fusion — and in NGO, and in our plan — is the **render layer**: `Render()` runs per-frame with real frame time and interpolates. The systems you've heard of that "use update time" are doing that: snapshot interpolation for remote objects, no input replay anywhere. No production CSP system I know of runs its predicted simulation on variable dt.

**3. Concession on C:** with epsilon comparison, my previous "replay mismatches by construction" argument against variable dt is withdrawn. C's reconciliation becomes workable. So the decision genuinely rests on the axes that survive: physics, framerate-dependence, cheat surface, machinery. Both options below include your epsilon design, so it's not a differentiator — it's shared infrastructure.

---

### Option A — fixed tick (FullUpdate, constant dt, epsilon reconcile)

```csharp
// ---------- shared ----------
const int BufferSize = 128;               // ring buffers, tick % BufferSize
readonly float tickDt = 1f / tickRate;     // == Time.fixedDeltaTime, must match in all builds

// ---------- OWNER CLIENT ----------
void FixedUpdate()                         // owner only
{
    TInput input = GetInput();             // sampled at tick rate
    InputBuffer[tick] = input;

    state = ProcessInput(input, state, tickDt);   // dt is a PARAMETER; no Time.* reads inside
    ApplyState(state);
    StateBuffer[tick] = state;

    SendInputs(tick, redundancy: 3);       // unreliable: {tick, [in_t, in_t-1, in_t-2]}
    tick++;
}

void OnAuthoritativeState(uint ackTick, TState serverState)   // unreliable ClientRpc
{
    if (ackTick >= tick) { HardReset(serverState, ackTick); return; }   // server ahead: adopt truth
    if (ackTick + BufferSize <= tick) return;                           // slot overwritten: ignore

    if (!CompareState(StateBuffer[ackTick], serverState))               // developer epsilon
    {
        state = serverState;                                            // rewind to truth
        for (uint t = ackTick + 1; t < tick; t++)
        {
            state = ProcessInput(InputBuffer[t], state, tickDt);        // replay, same dt
            StateBuffer[t] = state;
        }
        ApplyState(state);
        OnCorrection?.Invoke();                                         // smoother hook
    }
    StateBuffer[ackTick] = serverState;   // re-baseline on BOTH paths — drift never compounds
}

// ---------- SERVER (per predicted object) ----------
uint lastProcessedTick;                    // follows the owner's stream; no server clock in protocol
TokenBucket budget;                        // refill 1.02–1.1 × tickRate per sec, burst ≈ BufferSize/2

void OnInputs(uint latestTick, TInput[] inputs)     // ServerRpc, requireAuthority, unreliable
{
    for (i, input in inputs)
    {
        uint t = latestTick - i;
        if (t <= lastProcessedTick) continue;        // overlap/duplicate → idempotent
        if (!budget.TryTake()) continue;             // rate cap: drop, owner self-corrects
        inputQueue[t] = input;                        // redundancy fills gaps for free
    }
}

void FixedUpdate()                         // server consumption scheduler
{
    int steps = 0;
    while (steps < MaxCatchupPerTick /*5*/ && inputQueue.TryGet(lastProcessedTick + 1))
    {
        lastProcessedTick++;
        state = ProcessInput(inputQueue[lastProcessedTick], state, tickDt);
        ApplyState(state);                            // server sim IS the truth
        steps++;
    }
    // starved (gap or empty): PAUSE the object — never extrapolate fake inputs
    // gap persists beyond BufferSize: hard-jump lastProcessedTick past it
    if (steps > 0) SendCorrection(lastProcessedTick, state);   // unreliable; every tick (or /K)
}

// ---------- OBSERVER ----------
// buffer {tick, state} by arrival; renderTime = latestTickTime - 100ms;
// lerp between snapshots bracketing renderTime on the tick×tickDt timeline.
```

### Option C — variable dt (Update-driven, dt travels with input, epsilon reconcile)

```csharp
// ---------- shared ----------
const float MaxStep = 1f / 30f;            // clamp rule MUST be identical client & server

// ---------- OWNER CLIENT ----------
int frame;                                  // still an integer index — buffers need keys

void Update()                               // sim runs at render rate
{
    float dt = Math.Min(Time.deltaTime, MaxStep);    // pre-clamp exactly like the server
    // NOTE: no Unity physics usable here — kinematic transforms, manual collision only.
    // (Fixing tunneling/feel with `while (remaining > MaxStep) Step(MaxStep)`
    //  re-derives fixed ticks — see comparison.)

    TInput input = GetInput();               // sampled at frame rate
    InputBuffer[frame] = input;
    Deltas[frame] = dt;                      // dt must be stored for replay

    state = ProcessInput(input, state, dt);
    ApplyState(state);
    StateBuffer[frame] = state;

    SendInputs(frame);                        // {frame, dt, input} + redundancy window
    frame++;
}

void OnAuthoritativeState(uint ackFrame, TState serverState)
{
    if (ackFrame >= frame) { HardReset(serverState, ackFrame); return; }

    if (!CompareState(StateBuffer[ackFrame], serverState))
    {
        state = serverState;
        for (uint f = ackFrame + 1; f < frame; f++)
            state = ProcessInput(InputBuffer[f], state, Deltas[f]);   // replay the EXACT
        ApplyState(state);                                             // dt the server used
        OnCorrection?.Invoke();
    }
    StateBuffer[ackFrame] = serverState;      // re-baseline, same as A
}

// ---------- SERVER ----------
float dtBudget = 1.0f;                       // token bucket in SECONDS, refilled by wall clock (×1.05)

void OnInputs(uint frame, float dt, TInput input)
{
    if (frame <= lastProcessedFrame) return;               // monotonic
    dt = Math.Min(dt, MaxStep);                            // must match client clamp
    if (dtBudget < dt) { Drop(); return; }                // Σdt ≤ 1s per wall-second (+margin)
    dtBudget -= dt;
    inputQueue[frame] = (input, dt);
    frameClock[frame] = accumulatedClientTime;             // timestamps for observers
}
// consume in order, pause on gap, catch up bounded — same shape as A.
// Observers: identical interpolation, bracketed by clientTime stamps instead of tick×tickDt.
```

Note what C *isn't* smaller on: it's A's exact state machine (index counter, buffers, replay loop, epsilon, re-baseline, guards) **plus** a dt array, a seconds-denominated budget, a shared clamp rule, and timestamp bookkeeping. C is a strict superset.

### The surviving differences

| Axis | A: fixed tick | C: variable dt |
|---|---|---|
| Unity physics / CharacterController | Native — sim lives in FixedUpdate | Incompatible — kinematic transforms + manual collision only |
| Gameplay vs framerate | Identical on every machine | Jump arcs, accel, tunneling all vary with fps (Quake 125Hz bug class); the fix — clamped substepping — *is* fixed ticks |
| Replay fidelity | Server used the exact dt the client will replay, always | Matches only while clamps/budgets agree; every server-side drop or clamp diverges the owner (epsilon usually absorbs it) |
| Cheat surface | One integer token bucket | dt clamp + Σdt budget + count budget, all interlocking |
| Bandwidth | Fixed: tickRate msg/s, known | Frame-rate-coupled (144fps = 3× A) unless you add batching |
| Local feel at high fps | 50Hz sim + per-frame render interpolation (smoother component) | Marginally tighter integration; indistinguishable once render interp exists |
| Bit-exact determinism needed | **No** — epsilon + re-baseline | **No** — epsilon + re-baseline |

Both rows of the last line are the point of your insight: epsilon makes both *work*; it doesn't make C *better*.

### Recommendation

**A, with your epsilon design as the reconciliation core** — and C's genuinely good idea is already in A where it belongs: the **render layer runs on update time** (`RemoteInterpolator`, visual smoother, `Time.deltaTime` lerp), exactly Fusion's `FixedUpdateNetwork`/`Render` split. Fixed quantum for the simulation, variable dt for everything you see. C as a simulation core only wins in a kinematic-only game with no physics, no framerate-sensitivity concerns, and no cheat concerns — and even there it costs more code for zero quality gain over A.

Two contract updates this forces in the docs, whichever you pick: (1) `CompareState` should compare **velocity too** when the state has it — position-only epsilon can accept a state that's drifting in direction; (2) the "On Match: do nothing" line in `OverallPlan.md` becomes "on match: re-baseline the buffer slot, keep simulating." Want me to write the resolved decision into `Decision_FixedUpdate_vs_CustomTick.md` and update `OverallPlan.md` (reconcile section, server section, determinism contract) accordingly?

---
