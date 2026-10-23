# Phase 1 — Review of the input planning docs

Summary of the four planning files supplied for SimpleCSP. It records where each one landed, how the timing debate moved, and the gaps that matter for the target game. The target is a **server-authoritative FPS with 16–32 players**, where the character controller and weapons are predicted per network object.

Copies of the inputs are in [`inputs/`](inputs/):

| File | What it is |
|---|---|
| [`inputs/omp-session-2026-09-02.md`](inputs/omp-session-2026-09-02.md) | A 4-round design session with an agent. It embeds the **original 4 plan docs**, which are extracted to [`inputs/original-plan-from-session/`](inputs/original-plan-from-session/): `OverallPlan.md`, `Decision_FixedUpdate_vs_CustomTick.md`, `Decision_GenericBaseClass.md`, `Decision_ProxyInterpolation.md` |
| [`inputs/TimingSystem_ApproachA_vs_Legacy.md`](inputs/TimingSystem_ApproachA_vs_Legacy.md) | Distils session round 3: the "self-clocking" client-labelled tick stream compared with the legacy `ClientTickRunner` |
| [`inputs/Decision_FixedTick_vs_VariableTime.md`](inputs/Decision_FixedTick_vs_VariableTime.md) | Fixed tick vs variable dt, with steel-man pseudocode. Also covers the FPS camera: rotation is never predicted |
| [`inputs/Decision_Fixed_vs_variable-glm.md`](inputs/Decision_Fixed_vs_variable-glm.md) | Distils session round 4: Option A (fixed) vs Option C (variable dt), both using epsilon reconcile and re-baselining |

---

## 1. What the docs agree on (the current proposed shape)

> The owner predicts on a fixed tick and streams its last N inputs (unreliable) to the server. The server consumes that object's inputs in order, simulates authoritatively, and sends back `{tick, state}`. The owner compares that state against its history with an epsilon, re-baselines, and replays on mismatch. Observers interpolate server states about 100 ms behind.

| Topic | Proposal | Source | Status |
|---|---|---|---|
| Scope | Per-object CSP, no whole-world rollback | OverallPlan | Agreed |
| API | `PredictionBehaviour<TInput, TState>`: `GetInput`, `ProcessInput(input, prev[, dt])`, `ApplyState`, `GatherState`, `CompareState` | Decision_GenericBaseClass | Proposed. **Weaver support for generic NetworkBehaviour/RPCs is unverified** (pending the Mirage research) |
| History | Fixed-size ring buffers of structs, indexed by `tick % N` (64–128) | OverallPlan | Agreed |
| Input transport | Unreliable ServerRpc, `{latestTick, [in_t, in_t-1, in_t-2]}`, redundancy fixed at 3 (not user-tunable) | OverallPlan + session r1 | Agreed |
| Correction transport | Unreliable ClientRpc `{tick, state}` every server tick | OverallPlan | Agreed in principle. Unclear whether to send to the owner only or to everyone (see §3.4) |
| Reconcile | `CompareState` epsilon. **Re-baseline** `StateBuffer[ack] = serverState` on both paths. Replay `ack+1 .. now` on mismatch. Hard reset if `ack >= tick`; ignore if the slot was overwritten | glm doc, session r4 | Agreed |
| Determinism | Not required. "Bounded divergence" from the same code path, same dt, epsilon and re-baseline | session r4 | Agreed |
| `CompareState` | Must include velocity, not just position | glm doc | Agreed |
| Sim time model | **Fixed tick** (Option A) over variable dt (Option C). Variable dt only in the render layer | both Decision_* docs, session r4 | Recommended, not yet signed off |
| Tick driver | Unity `FixedUpdate` | Decision_FixedUpdate_vs_CustomTick, glm doc | **Contested**: you raised that FixedUpdate is skipped or clamped under frame lag and asked for a Unity-independent timer (session r2) |
| Clock ownership | "Self-clocking": the owner numbers its ticks, the server follows that object's stream with `lastProcessedTick`, and the correction echoes the **input tick**, so the client needs no clock offset | TimingSystem doc, session r2–r3 | Proposed. **This is the central open question** (see §3) |
| Server validation | Monotonic ticks, token bucket (about 1.02–1.1× tickRate, burst ≈ buffer/2), never apply past a gap, pause on starvation (do not duplicate the last input), catch up at most about 5 ticks per frame, semantic clamps in `ProcessInput` | TimingSystem doc | Proposed |
| Observers | Snapshot interpolation about 100 ms behind, on the **tick timeline** (not `Time.time`) | Decision_ProxyInterpolation + session r1 bug fix | Agreed |
| Visual smoother | Separate optional component in phase 2. Keep the `OnCorrection` hook in v1 | session r1 | Agreed |
| Physics | v1 targets kinematic and CharacterController-style movement. Replay does not re-run Unity physics, and world queries during replay use the *current* world | session r1 | Agreed |
| Camera | Rotation is client-authoritative input (sent with the input, never rolled back). Position follows the predicted body | Decision_FixedTick_vs_VariableTime | Agreed |
| Out of scope (v1) | Spawn prediction, delta compression, time dilation, multiple predictors per GameObject (compose structs instead), physics re-sim | session r1 | Agreed |
| Host mode | Host runs the authoritative path directly, with no RPC round-trip or reconcile | session r1 | Agreed, details TBD |

## 2. How the timing debate evolved (session rounds)

1. **r1**: FixedUpdate was accepted. The server had its own tick and duplicated the last input when starved.
2. **r2**: you challenged it: FixedUpdate is not synced and frame lag skips ticks, so you wanted a Unity-independent timer like TickRunner. The agent reframed: "nothing in CSP needs wall-clock sync". If the server *follows* the owner's stream, then `ClientTickRunner` (RTT average, jitter stddev, ±1% timescale, skip-ahead) is not needed. Starvation now pauses the object instead of duplicating input.
3. **r3**: you raised that server authority makes a client-owned timeline worrying. The agent split "authority over state" from "authority over the clock". It said the tick in the correction message decides everything: echoing the input tick removes clock sync. The only cheat vector is rate inflation, handled by a token bucket. It also argued against variable dt.
4. **r4**: you said determinism isn't realistic, use a "close enough" check, and suggested exploring variable dt. The agent agreed epsilon makes both options workable, added re-baselining, and retracted "Fusion uses variable dt". Its recommendation stayed A (fixed tick) on the grounds of physics, framerate independence, cheat surface and bandwidth.

What was **never resolved**: whether a *pure* self-clocking design is enough for an FPS, or whether some shared server timeline is still needed. Your current brief ("the tricky part is the time syncing … we do have TickRunner") suggests it isn't settled. §3 explains why I think a server timeline is still needed, but for observers and hit registration, not for owner reconciliation.

## 3. Critical review — gaps that matter for a 16–32 player FPS

These come from reading the docs against the FPS use case. Points marked *(general knowledge)* come from my knowledge of other engines and were not verified in this session.

### 3.1 Lag compensation (hitscan) needs a server timeline

The docs never mention lag compensation, but a server-authoritative FPS needs it. When the shooter fires on their input tick N, the server must rewind the *other* players to where the shooter saw them. That position is the observer interpolation time the shooter was rendering.

- That time has to live on one timeline shared by every object: the **server's** tick. It cannot live on each owner's private input-tick timeline.
- So observers must interpolate snapshots labelled with the **server tick**, and the owner must report "the server tick I was rendering" with each input. Source engine does this with `CUserCmd::tick_count` plus the client's interp *(general knowledge)*.
- The TimingSystem doc's claim that the self-clocking design "eliminates the clock sync layer entirely" holds **only for owner reconciliation**. Clients still need an estimate of server time for interpolation and lag-comp stamping. That estimate is less demanding: it must be smooth and consistent, but it does not have to be "ahead of the server".

A likely clean split for phase 2 to evaluate:

- **To the owner**: `{lastAppliedInputTick, state}`. Owner reconcile needs no clock.
- **To observers**: `{serverTick, state}`. Observers interpolate on an estimated server timeline.
- **In the input**: `{inputTick, renderServerTick (+ interp fraction), inputs[]}`. The server uses this for lag comp.

### 3.2 Arrival jitter and the server-side input buffer

With "process as soon as it arrives, pause when starved", the server keeps **no jitter buffer**.

- Mirage batches sends per frame (pending confirmation from the Mirage research). A client running at 30 fps with a 60 Hz tick sends 2 inputs per packet. The server then processes 2 ticks of that player in one server tick and 0 in the next, so the server-side motion is uneven.
- Because observers and lag comp see the server-side motion (§3.1), that unevenness reaches everyone. Network jitter does the same thing.
- The standard fix is a small per-player **server input buffer** of 1–2 ticks. The client's lead is then tuned so that buffer stays small but non-empty: the server reports buffer depth, and the client nudges its tick rate. That is what Overwatch describes (GDC 2017, *general knowledge*), and it is essentially what `ClientTickRunner` + `PlayerTimeTracker` do.
- **Takeaway:** some form of time feedback probably comes back for good-quality observer motion. It may be simpler than the legacy one, for example driven by measured server buffer depth instead of an RTT model. The TickRunner research report has the detail on what the legacy code actually does.

### 3.3 "Pause on starvation" combined with bounded catch-up enables lag switching

- A client can stop sending (a lag switch) and its object freezes on the server. Then it bursts up to the token-bucket capacity (≈ buffer/2 ≈ 64 ticks ≈ 1.3 s), which the server drains at 5 ticks per frame.
- Observers see a frozen player who then zooms. The cheater's shots inside the burst are lag-compensated against old times.
- This needs a **maximum allowed lag of the player's stream behind server time** (drop or skip inputs that are too old). That limit is a server-timeline concept again.
- Source handles this with `sv_maxusrcmdprocessticks` *(general knowledge)*. Unreal's CharacterMovementComponent does it with time-discrepancy detection *(general knowledge)*.

### 3.4 One tick counter per owner, not per object

- The FPS has at least two predicted parts: the character controller and the weapon. The weapon's fire origin and aim at tick N depend on the movement state at tick N.
- If each NetworkBehaviour keeps its own independent tick counter and reconciles independently, a movement correction does not replay weapon logic in the same order.
- The docs' answer is to compose one big struct per GameObject. That works but is clumsy if the weapon is a separate object or component.
- **Suggestion for phase 2:** keep "per-object state and buffers", but use **one tick clock per local player/connection**. Replay iterates over all predicted behaviours owned by that client, in a fixed order, once per tick. That is still far simpler than whole-world rollback.

### 3.5 Side effects during replay

- Replaying `ProcessInput` must not re-trigger sounds, VFX, hit markers, ammo UI or RPCs.
- The legacy repo has `IsResimulation` / `UpdateMethod` for this.
- SimpleCSP needs at least an `IsReplaying` flag, and a rule that predicted events (fire, reload) fire only on the *first* simulation of a tick.

### 3.6 FixedUpdate as the tick driver

This is the concern you raised in r2. Points from general Unity knowledge:

- `Time.maximumDeltaTime` (default 0.333 s; the session said 0.1 s) clamps the accumulator, so FixedUpdate *discards* time under a hitch.
- `Time.timeScale` changes the FixedUpdate frequency.
- Several fixed steps run back-to-back inside one frame before any network send or receive.

That is fine for a self-clocking owner. It is a problem for a **server** whose tick defines the shared timeline (§3.1–3.3): a server hitch silently loses time, and every client's server-time estimate then drifts. That argues for the server tick (and probably the client's estimate of it) coming from a Stopwatch-based runner like `TickRunner`. You can still *call* the predicted step from that runner and use `Physics.Simulate` only if physics prediction is ever needed (currently out of scope). This is pending the TickRunner report.

### 3.7 Precedents: both clock models ship *(general knowledge, worth verifying)*

- **Server is the clock master; the client runs ahead with dilation and feedback.** Photon Fusion, Overwatch, and the legacy `ClientTickRunner`. The glm doc says Option A is "identical to Photon Fusion's design". That is true for fixed-sim/variable-render, but **Fusion is not self-clocking**: its server owns the tick and clients sync to it.
- **The server follows the client's command stream.** Source engine (per-player `tickbase` driven by usercmds, with a processing cap) and Unreal CMC (client timestamps plus **variable** `DeltaTime` per move, server time-discrepancy checks). Unreal CMC contradicts the session's line that "No production CSP system I know of runs its predicted simulation on variable dt". It does, though only for the character.
- Both families still keep a server timeline for **lag compensation and observers**.

### 3.8 Smaller items

- **Tick rate agreement**: `fixedDeltaTime` or the tick rate must match between builds. Enforce it with a handshake value, not just documentation.
- **Initial state handshake** on spawn or authority change. Late-join observers wait for 2 snapshots before rendering.
- **Bandwidth at 32 players**: a `ClientRpc` state per player per tick to all observers is 32 × 31 × tickRate messages per second. Mirage's batching and visibility rules matter here (pending the Mirage research). A lower send rate to observers (for example every 2nd tick) may be needed.
- **Redundancy factor**: if Mirage packs several ticks per packet (§3.2), redundancy should be counted in *packets*, not ticks.
- **Hosts and listen servers**: the host's own player has zero latency. Remote players hit by the host still need lag comp against the host's view.

## 4. Open questions to settle in phase 2

1. **Clock model.** Choose one:
   - (a) pure self-clocking for owners, with a separate server-time estimate for observers and lag comp;
   - (b) a server-master tick with client lead feedback (simplified `ClientTickRunner`, possibly driven by input-buffer depth);
   - (c) hybrid: self-clocked owner reconcile plus server input buffer depth feedback.
2. **Tick driver.** `FixedUpdate` or a Stopwatch `TickRunner` on the server and on the client?
3. **Tick ownership.** Per object or per owner/connection? How are multiple predicted behaviours on one player ordered during replay?
4. **Transport.** Generic RPCs, `byte[]`/segment RPCs, or custom per-object messages? Do corrections and observer snapshots use one message or two? Do SyncVars carry any of the predicted state?
5. **Lag compensation.** Is it in SimpleCSP scope, or only a hook (input carries `renderServerTick`)?
6. **Anti-cheat bounds.** Token bucket values and the max stream lag behind server time.
7. **Replay side effects.** The `IsReplaying` contract and first-time-only events.
8. **Host mode** and server-only objects (bots).

The research reports in [`research/`](research/) cover what the legacy CSP and Mirage already provide for each of these.
