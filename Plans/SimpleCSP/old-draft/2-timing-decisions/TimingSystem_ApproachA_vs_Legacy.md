# Timing System Analysis: Approach A (Self-Clocking Stream) vs Legacy CSP

This document analyzes the prototype timing architecture (**Approach A: FixedUpdate + Following Server + Server Rate Validation**) against the legacy system (**`ClientTickRunner` + Server Master**).

---

## 1. The Core Insight: Authority over State vs Authority over Clock

The legacy system fused two separate concepts:
1. **Authority over state** (the server decides what actually happened).
2. **Authority over the clock** (the server forces everyone onto a single wall-clock timeline).

By decoupling them:
- **The server retains 100% authority over state**. It simulates every tick, enforces physics/game rules in `ProcessInput`, and produces every broadcast state. Observers never receive anything client-authored.
- **The owner client merely labels its proposed input stream**.
- When the server confirms state, it echoes back `{lastAppliedInputTick, state}` — meaning *"here is the resulting state after applying your input #N"*.
- **Why this eliminates the clock sync layer**: If the server echoed its *own* internal tick $M \neq N$, the client would have to constantly measure RTT and jitter to translate server ticks to its local buffer slots. Echoing the input's tick eliminates the translation requirement entirely.

---

## 2. Server-Side Anti-Cheat & Stream Validation

Because the client numbers its ticks, the only real cheat vector is **rate inflation** (a hacked client running at 100Hz so the server executes 100 inputs/sec and the player moves $2\times$ faster).

We defeat this with a 4-point validation contract on the server:

1. **Monotonicity**: Any input with `tick <= lastProcessedTick` is ignored as new work.
2. **Token Bucket Rate Limiter**:
   - Refills at $\approx 1.1\times \text{tickRate}$ using unscaled server wall time.
   - Burst capacity of $\approx \text{BufferCapacity} / 2$ to absorb post-stall message bursts.
   - Excess inputs are dropped. A cheater attempting speedhacks exhausts tokens and rubber-bands against the server-simulated truth.
3. **Strict Ordering Invariant**:
   - Invariant: Never simulate tick $t+1$ before tick $t$.
   - Redundancy (3 inputs/packet) automatically fills 1–2 packet drops.
   - If $t+1$ is missing: **pause** simulation for that object. Do not extrapolate with fabricated fake inputs.
4. **Semantic Clamping in `ProcessInput`**:
   - CSP ensures the channel is well-formed.
   - Game rules (max speed, collision checks) are enforced inside `ProcessInput`.

---

## 3. Comparative Matrix

| Feature | Legacy System (`ClientTickRunner`) | Approach A Prototype (Self-Clocking + Rate Validated) |
| :--- | :--- | :--- |
| **Correction Echo** | Echoes server tick; requires client to estimate RTT offset. | Echoes **`lastAppliedInputTick`**; zero client clock offset needed. |
| **Anti-Cheat Enforcement** | Clock sync prevents inflation, but sync layer itself is vulnerable to jitter spoofing. | **Server Token Bucket** restricts sustained tick arrival to $\le 1.1\times$ tick rate. |
| **Client Hitch Recovery** | Server extrapolates fake inputs, causing severe rubber-banding on resume. | Server **pauses** on starvation; both sides resume coherently on identical ticks. |
| **Server Hitch Recovery** | Distorts client RTT averages and triggers timescale drift. | Server catches up via bounded loop ($\le 5$ inputs/frame) in ~200ms. |
| **Code Footprint** | ~400+ lines across `TickRunner`, `MovingAverage`, and controllers. | **~30 lines** of validation logic inside `PredictionBehaviour`. |
