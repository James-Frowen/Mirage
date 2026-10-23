# Decision: Fixed-Tick vs. Variable dt Client-Side Prediction

## Summary & Core Foundations

When evaluating Client-Side Prediction (CSP) architectures for Unity, the central dilemma is whether the simulation should run on a **fixed tick** (Option A) or **variable delta time** (Option C).

Reviewing the requirements and operational realities of Unity yields three foundational insights:

- **Determinism & Bounded Divergence**: Bit-exact floating-point determinism is neither realistic nor necessary for Unity games. CSP does not need lockstep determinism; it requires **bounded divergence**. This is achieved through:
  - Identical simulation code paths.
  - Identical $\Delta t$ per step.
  - An epsilon tolerance check (`CompareState`) to absorb small float divergences (e.g., $x = 12.1$ vs $12.11$).
  - **Re-baselining on accept**: When state is "close enough", the authoritative server state is still written back into the client's history buffer slot. This ensures accepted drift affects the visual presentation once without compounding into future replay loops.
- **Photon Fusion Architecture**: Fusion relies on a fixed simulation tick (`Runner.Tick`, `FixedUpdateNetwork`, fixed `DeltaTime`; Quantum uses deterministic lockstep). In Fusion and similar production engines, variable $\Delta t$ is strictly isolated to the **render and interpolation layer** (`Render()`, visual smoothers, remote proxy interpolation), never the predicted simulation replay core.
- **Variable dt Viability**: With epsilon reconciliation, variable dt becomes technically viable for kinematic controllers because minor replay drift is absorbed. However, the decision shifts to practical constraints: physics engine compatibility, framerate-dependent movement behavior, cheat surface expansion, and unnecessary machinery.

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

---

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

Option C is not simpler or smaller in code size; it is a strict superset of Option A's state machine (index counter, ring buffers, replay loop, epsilon checks, re-baseline, safety guards) with additional storage for deltas, a seconds-denominated token bucket, a shared clamping rule, and timestamp synchronization.

---

## Comparison of Surviving Differences

| Axis | Option A: Fixed Tick | Option C: Variable dt |
|---|---|---|
| **Unity Physics / CharacterController** | Native: simulation naturally executes in `FixedUpdate` alongside physics. | Incompatible: requires kinematic transforms and manual collision routines (or manual `Physics.Simulate`). |
| **Gameplay vs Framerate** | Identical across all hardware and framerates. | Jump heights, acceleration, and tunneling vary with framerate; clamping and substepping to resolve this re-derives fixed ticks. |
| **Replay Fidelity** | Server and client replay always execute with the exact same $\Delta t$ by definition. | Matches only while clamp rules and budgets stay in lockstep; server drops cause local divergence. |
| **Cheat Surface** | Single integer token bucket tracking allowed ticks per second. | Multi-layered surface: $\Delta t$ per-step clamp, cumulative seconds budget, and input packet count limits. |
| **Bandwidth** | Constant, predictable bandwidth determined by configured tick rate. | Tied to client framerate (e.g., 144Hz generates ~3× packets) unless decoupled by a batching layer. |
| **High FPS Local Feel** | 50Hz/60Hz simulation paired with per-frame visual interpolation in `Update()`. | Per-frame simulation integration; visually indistinguishable from Option A once render interpolation is active. |
| **Bit-Exact Determinism** | **Not required**: developer epsilon comparison + buffer re-baselining absorbs float variation. | **Not required**: developer epsilon comparison + buffer re-baselining absorbs float variation. |

---

## Recommendation & Architecture Contracts

**Adopt Option A (Fixed Tick) for simulation, retaining variable $\Delta t$ strictly for the render layer.**

The optimal hybrid model is identical to Photon Fusion's design:
- **Simulation**: Fixed-tick loop in `FixedUpdate` using constant `tickDt`.
- **Presentation**: Variable $\Delta t$ in `Update()` for local visual smoothing and remote proxy snapshot interpolation (`RemoteInterpolator`).

### Required Contract Updates
- **Velocity in Comparison**: `CompareState` must evaluate velocity in addition to position; position-only epsilon checks can miss divergent motion vectors before they turn into large spatial errors.
- **Re-baseline on Match**: On state match, do not "do nothing". Always write the authoritative server state into the corresponding buffer slot (`StateBuffer[ackTick] = serverState`) so accepted tolerances do not compound across future replay operations.
