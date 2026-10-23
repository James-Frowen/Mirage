# Design Decision: Fixed-Tick vs Variable-Time CSP

## 1. Overview & Context

When building a lightweight, reusable Client-Side Prediction (CSP) system for first-person shooters (FPS), a fundamental architectural choice is how time and simulation steps are structured:

1. **Fixed-Tick CSP**: Simulation advances in discrete, constant time steps (e.g. 60Hz, $\Delta t = 16.66\text{ms}$), decoupled from rendering.
2. **Variable-Time CSP**: Simulation advances dynamically per frame in `Update()` using the frame's actual `Time.deltaTime`.

A common initial intuition is that variable time might be simpler because it appears to avoid the complexities of "clock synchronization" and tick alignment. This document explores the real trade-offs, how the camera behaves under CSP, steel-man pseudocode for both approaches, and the core deciding factors.

---

## 2. The Scope of CSP: Where Does the Camera Fit?

In a first-person shooter, it is essential to distinguish between **camera rotation** and **camera position**:

### Camera Rotation (Pitch / Yaw)
* **Camera rotation is pure client-authoritative input.**
* Mouse movement translates instantly into view angles without prediction or rollback.
* The player's look angles are packaged inside the input payload sent to the server (used for raycast shooting, view frustum checks, and movement orientation).
* *CSP does not predict or roll back camera rotation.* Rolling back view angles would cause severe player disorientation and motion sickness.

### Camera Position
* The camera is parented to the player's head or eyes.
* The character body's position is simulated and authoritative on the server.
* If a prediction error occurs and the character's body position snaps or reconciles, **the camera's position will snap with it** unless visual smoothing or error decay is applied.

---

## 3. Approach A: Fixed-Tick CSP (The Industry Standard)

### Core Mechanics
* Both client and server execute the character simulation using an identical, fixed time delta (`FIXED_DT = 1f / 60f`).
* Inputs and states are stored in circular ring buffers indexed by an integer tick number (`Tick % BUFFER_SIZE`).
* Visual rendering is decoupled: the simulation steps in `FixedUpdate` (or a fixed network loop), while `Update()` interpolates the visual transform between the previous and current simulated tick.

### Pseudocode (Best-Case Steel Man)

```csharp
public struct PlayerState
{
    public Vector3 Position;
    public Vector3 Velocity;
}

public struct PlayerInput
{
    public Vector2 Move;
    public bool Jump;
    public float Yaw; // Client-authoritative view rotation
}

public class FixedTickPredictor
{
    const float FIXED_DT = 1f / 60f;
    const int BUFFER_SIZE = 1024;

    // Fixed ring buffers indexed by tick modulo
    readonly PlayerInput[] inputBuffer = new PlayerInput[BUFFER_SIZE];
    readonly PlayerState[] stateBuffer = new PlayerState[BUFFER_SIZE];

    int clientTick;

    // Pure deterministic step method shared between client prediction, client replay, and server simulation
    public static PlayerState Step(PlayerState state, PlayerInput input, float dt)
    {
        Vector3 forward = Quaternion.Euler(0, input.Yaw, 0) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0, input.Yaw, 0) * Vector3.right;
        Vector3 wishDir = (forward * input.Move.y + right * input.Move.x).normalized;

        state.Velocity = wishDir * 6f;
        state.Position += state.Velocity * dt;
        return state;
    }

    // Called during FixedUpdate (or fixed network tick)
    public void OnFixedUpdate(PlayerInput input)
    {
        clientTick++;
        int slot = clientTick % BUFFER_SIZE;

        inputBuffer[slot] = input;

        // Advance simulation deterministically with identical step size
        PlayerState prevState = stateBuffer[(clientTick - 1) % BUFFER_SIZE];
        stateBuffer[slot] = Step(prevState, input, FIXED_DT);

        SendInputToServer(clientTick, input);
    }

    // Called when server echoes back authoritative state
    public void OnServerStateReceived(int serverTick, PlayerState serverState)
    {
        int slot = serverTick % BUFFER_SIZE;
        PlayerState predictedState = stateBuffer[slot];

        // Zero correction needed if simulation matches
        if (Vector3.Distance(predictedState.Position, serverState.Position) < 0.001f)
            return;

        // Replay history from the corrected server tick back up to the current client tick
        stateBuffer[slot] = serverState;
        for (int t = serverTick + 1; t <= clientTick; t++)
        {
            int prev = (t - 1) % BUFFER_SIZE;
            int curr = t % BUFFER_SIZE;
            stateBuffer[curr] = Step(stateBuffer[prev], inputBuffer[curr], FIXED_DT);
        }
    }

    // Called in Update() to deliver smooth 144Hz+ visuals without micro-stutter
    public void Render(Transform visualModel, float alpha)
    {
        PlayerState prev = stateBuffer[(clientTick - 1) % BUFFER_SIZE];
        PlayerState curr = stateBuffer[clientTick % BUFFER_SIZE];
        visualModel.position = Vector3.Lerp(prev.Position, curr.Position, alpha);
    }
}
```

### Strengths
* **Identical Integration Math**: Because $\Delta t$ is constant on both client and server, numerical integration yields the exact same position down to the millimeter during standard movement.
* **Virtually Zero Reconciliation Under Normal Conditions**: Replay is only triggered by external events (bumping another player, server-side push, or obstacle changes).
* **Self-Clocking Architecture**: When combined with an input-following server (Approach A), complex wall-clock synchronization is completely eliminated.

### Weaknesses
* Requires separating the visual transform from the simulation root so `Update()` can interpolate between the last two ticks.

---

## 4. Approach B: Variable-Time CSP (Direct Frame Update)

### Core Mechanics
* The client runs prediction directly inside `Update()` using the variable `Time.deltaTime`.
* Every frame logs a sequence number, input, delta time, and predicted state into a dynamic history buffer.
* Because varying step sizes produce floating-point divergence against the server, an **error decay offset** vector is added to camera/visual position and decayed toward zero over time.
* The server uses a time bank to ensure clients do not submit more simulated delta time than real wall-clock time has passed.

### Pseudocode (Best-Case Steel Man)

```csharp
public struct FrameRecord
{
    public uint Sequence;
    public float DeltaTime;
    public PlayerInput Input;
    public PlayerState State;
}

public class VariableTimePredictor
{
    uint sequence;
    PlayerState currentState;
    readonly List<FrameRecord> history = new List<FrameRecord>();

    // Visual offset applied to the camera holder to mask floating-point drift
    Vector3 visualErrorOffset;

    // Called in Update() every render frame
    public void OnUpdate(float dt, PlayerInput input, Transform cameraHolder)
    {
        sequence++;

        // Step simulation using this exact frame's delta time
        currentState = FixedTickPredictor.Step(currentState, input, dt);

        history.Add(new FrameRecord
        {
            Sequence = sequence,
            DeltaTime = dt,
            Input = input,
            State = currentState
        });

        // Decay error offset smoothly toward zero so corrections are imperceptible
        visualErrorOffset = Vector3.MoveTowards(visualErrorOffset, Vector3.zero, dt * 2f);
        cameraHolder.position = currentState.Position + visualErrorOffset;

        SendInputToServer(sequence, dt, input);
    }

    // Called when server echoes back authoritative state
    public void OnServerStateReceived(uint serverSeq, PlayerState serverState)
    {
        int index = history.FindIndex(f => f.Sequence == serverSeq);
        if (index == -1)
            return;

        Vector3 predictedPos = history[index].State.Position;
        Vector3 error = serverState.Position - predictedPos;

        // Small drift is expected due to float differences, ignore insignificant sub-millimeter noise
        if (error.sqrMagnitude < 0.0001f)
        {
            history.RemoveRange(0, index + 1);
            return;
        }

        // Absorb discrepancy into the visual offset to prevent hard camera snapping
        visualErrorOffset += error;

        // Replay history forward using each past frame's specific delta time
        PlayerState replayState = serverState;
        for (int i = index + 1; i < history.Count; i++)
        {
            FrameRecord frame = history[i];
            replayState = FixedTickPredictor.Step(replayState, frame.Input, frame.DeltaTime);
            frame.State = replayState;
            history[i] = frame;
        }

        currentState = replayState;
        history.RemoveRange(0, index + 1);
    }
}
```

### Strengths
* **Zero Input Latency**: Input is applied on the exact frame it is sampled without waiting for a fixed tick boundary.
* **No Transform Interpolation Needed**: Simulation transform is updated directly in `Update()`.

### Weaknesses
* **Constant Mathematical Drift**: Simulating $1 \times 16.66\text{ms}$ on the server does not equal simulating $2 \times 8.33\text{ms}$ on a 120Hz client due to non-linear friction and acceleration curves.
* **Requires Drift Masking**: Every server snapshot produces small corrections that must be continuously absorbed by an error decay offset.
* **Anti-Cheat Burden**: The server must strictly meter and validate dynamic `dt` intervals to prevent client speedhacks.

---

## 5. Architectural Comparison Matrix

| Dimension | Fixed-Tick CSP | Variable-Time CSP |
| :--- | :--- | :--- |
| **Simulation Step Size** | Strictly constant (`FIXED_DT`) | Fluctuates per frame (`Time.deltaTime`) |
| **Numerical Consistency** | Exact match between client and server | Continuous floating-point and integration drift |
| **Reconciliation Frequency** | Rare (only on collisions or external forces) | Frequent (almost every packet has slight math divergence) |
| **Visual Smoothness Mechanism** | Transform interpolation between tick $t-1$ and $t$ | Error offset decay vector (`visualErrorOffset`) |
| **Data Structures** | Fixed-size ring buffers (`T[1024]`, zero allocation) | Dynamic history list (requires allocations or custom queue) |
| **Server Anti-Cheat Model** | Count ticks against rate limit | Track time bank / cumulative elapsed delta time |
| **Replay Execution** | Fast loop over constant `FIXED_DT` steps | Loop over dynamic stored delta time floats |

---

## 6. Deciding

The choice between Fixed-Tick and Variable-Time boils down to which fundamental problem you prefer to solve in your codebase:

1. **Fixed-Tick Problem Space**:
   * Implementing a fixed timing loop and a clean render interpolation component (`TransformInterpolator`) to bridge the fixed simulation rate and arbitrary monitor refresh rates (144Hz+).
   * *The simulation itself is robust and deterministic with zero mathematical drift.*

2. **Variable-Time Problem Space**:
   * Managing persistent floating-point divergence, building camera/visual error decay smoothing to hide constant micro-reconciliations, and validating client-reported `dt` on the server to prevent speedhacks.
   * *You avoid a fixed tick loop, but push the complexity into error-mitigation math.*
