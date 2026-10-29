# Phase 1 summary — what we know before planning SimpleCSP

Phase 1 collected the old draft (your earlier plan docs and design session) and researched three things: the legacy ClientSidePrediction repo, Mirage, and external best practice. This file cross-checks them against each other. It ends with the list of decisions phase 2 needs to make, each with the evidence that bears on it.

The target throughout: **per-network-object client-side prediction for Mirage**, aimed at a **server-authoritative FPS with 16–32 players**, where the character controller and weapons are predicted. It should be easy to use and debug alongside SyncVars, RPCs and custom messages.

## 1. Sources

| Folder | What it is | Start with |
|---|---|---|
| [`../old-draft/`](../old-draft/) | Your earlier plan docs and the design session that produced them | [`00-Draft-Review-and-Open-Questions.md`](00-Draft-Review-and-Open-Questions.md) (my review of them) |
| [`../research/ClientSidePrediction/`](../research/ClientSidePrediction/) | The legacy whole-world CSP repo, read from code (`f8bc7b5`) | [README](../research/ClientSidePrediction/README.md), then [10-Pain-Points-and-Reuse](../research/ClientSidePrediction/10-Pain-Points-and-Reuse-Assessment.md) |
| [`../research/ClientSidePrediction/TickRunner-and-TimeSync.md`](../research/ClientSidePrediction/TickRunner-and-TimeSync.md) | Deep-dive on the legacy clock, plus a fact-check of the old-draft timing claims | §1 summary and §5 fact-check table |
| [`../research/Mirage/`](../research/Mirage/) | Mirage features that matter for CSP, verified against `main` @ `0638bf8e5` (v159.1.1) | [README](../research/Mirage/README.md) (18 key facts), then [07-Gaps-and-Hooks](../research/Mirage/07-Gaps-and-Hooks-for-CSP.md) |
| [`../research/External-Best-Practices/`](../research/External-Best-Practices/) | Independent baseline from outside sources only. It never read our files | [README](../research/External-Best-Practices/README.md) (ranked checklist of 46 practices), then [12-Contested-Points](../research/External-Best-Practices/12-Contested-Points.md) |

All research was static reading; nothing was run in Unity. The external report could not fetch some primary sources (Valve wiki, GDC Vault, Unity manual, Photon docs), so for those it used search summaries and marked them as such.

## 2. Headline findings

1. **The old draft's core loop matches industry consensus.** That loop is: fixed tick, struct ring buffers, redundant unreliable inputs, reconcile by restore → replay → re-baseline, snapshot interpolation for remotes, a visual smoother separate from the sim, and camera rotation never rolled back. The external checklist marks all of these Essential (#2–4, #8, #11–14).
2. **Clock ownership is a genuine fork in the road, and the external research confirms both families ship.**
   - The **server is the tick master** and the client runs ahead with feedback: Netcode for Entities, Overwatch, Rocket League, Fusion, and the legacy `ClientTickRunner`.
   - The **server runs each player's command stream** with a budget: Source, Unreal's CharacterMovementComponent, Quake 3. This is close to the old draft's "self-clocking" Approach A.
   - Either way, **an FPS still needs a server-tick timeline** for remote interpolation and lag compensation. External #14–15 rate this Essential, and the TickRunner report §6 and my review §3.1 reach the same conclusion. Self-clocking removes clock sync only from *owner reconciliation*.
3. **The old draft is missing several things the external research marks Essential:**
   - lag compensation (#15);
   - side effects that fire only once, not again during replay (#10);
   - stripping one-shot inputs such as jump and fire when an input is repeated (#6);
   - render interpolation between ticks (#13);
   - simulated-network testing and misprediction counters (#18–19);
   - all weapon timers and RNG kept in the predicted state (#9).
4. **External practice contradicts the old draft in three places:**

   | Topic | Old draft | External best practice | Notes |
   |---|---|---|---|
   | Missing input on the server | **Pause** the object; never repeat (TimingSystem doc) | Repeat continuous input for a few ticks with one-shots stripped, then neutral; drop late inputs (#6, Essential) | Pausing freezes players for observers and lag comp, and enables lag-switch bursts (my review §3.3) |
   | Tick driver | Unity `FixedUpdate` (Decision_FixedUpdate_vs_CustomTick, glm doc) | Own tick loop on unscaled time, not `FixedUpdate` (#28, Recommended) | Matches your session-r2 instinct. Mirage itself has no FixedUpdate hook (below) |
   | Reconcile trigger | Compare with an epsilon, replay only on mismatch | Start with **always-replay**; add compare-with-tolerance later as an optimisation (#21) | Contested (external 12 §3). Re-baselining is required either way |
5. **Mirage has no tick concept. Its networking is frame-bound** ([Mirage README](../research/Mirage/README.md), [04-Update-Loop](../research/Mirage/04-Update-Loop-and-Timing.md)):
   - Receive then send runs in `Update`. Nothing runs in `FixedUpdate`, and no execution order is set.
   - A ServerRpc body therefore runs *between* fixed steps and can only enqueue input.
   - Messages queued in a frame are batched into shared packets, so several ticks per frame go out together. `ManualUpdate` lets a tick loop pump receive and send itself.
6. **Mirage channels constrain the transport choice:**
   - SyncVars, `OnSerialize` and spawn data are **reliable-only**, in one ordered stream shared with reliable RPCs. Head-of-line blocking is real.
   - Unreliable traffic has no sequence numbers (it can be lost, duplicated or reordered) and is limited to one MTU.
   - Notify gives delivered/lost callbacks.
   - Conclusion: predicted inputs and corrections must be unreliable or Notify payloads that carry their own tick. **SyncVars are not suitable for predicted state.** They remain fine for non-predicted state such as health display, team and score.
   - Mirage's server error budget (200 tokens, refilling 50/s, disconnect when exceeded) charges for ownership failures and RPCs to unknown objects. A 60 Hz input stream that hits these, for example after an authority change or a despawn, can disconnect an honest player (Mirage README fact 16).
7. **Generic `PredictionBehaviour<TInput, TState>` with RPCs is feasible**, with limits ([Mirage 07](../research/Mirage/07-Gaps-and-Hooks-for-CSP.md)). I verified the key Weaver checks myself.
   - Generic classes with `T`-typed RPC parameters are covered by runtime tests.
   - Abstract RPCs and RPCs that are themselves generic are Weaver errors (`RpcProcessor.cs:256-274`). Use virtual RPCs.
   - `T[]` / `ArraySegment<T>` parameters inside a generic class are untested and probably broken.
   - The safe pattern: plain `TInput`/`TState` parameters, or an `ArraySegment<byte>` payload (allocation-free, valid only inside the RPC body).
   - Each concrete `T` needs a registered serializer.
8. **Host mode in Mirage runs everything inline.** A ServerRpc runs directly, a ClientRpc body runs locally, the setter runs SyncVar hooks, and there is no ping. SimpleCSP needs an explicit host path. The legacy repo special-cased this too.
9. **The legacy clock (`ClientTickRunner`) is sound in idea but has real bugs:**
   - `MovingAverage` never counts its last slot.
   - `lastFrame` is uninitialised, so the client bursts at startup.
   - Stale RTT echoes poison the average.
   - A backward skip silences the client.
   - Correction is bang-bang with no hysteresis. In the model, start-up takes 10–12 s to settle.
   - The legacy repo already computes a better feedback signal, server-measured input-buffer depth (`ServerInputHandler.cs:92-96`), but never sends it to the client. That is the "command age" feedback external #20 recommends.
10. **The legacy repo has reusable pieces:** `NetworkInputs<T>{Current, Previous}`, the `InputState` message with ack-driven redundancy (needs bounds checks), the `ClientController`/`ServerController` concepts, `TickRunner` once fixed, `LagSocket`, and some debug tools. Its world snapshot, delta compression, unmanaged allocator and global resim are what to drop. The table is in [10-Pain-Points §5](../research/ClientSidePrediction/10-Pain-Points-and-Reuse-Assessment.md).
11. **Lessons from legacy bugs** (10-Pain-Points §3):
    - Smoothing must not write into sim history (#12 there).
    - Simulation order must be stable (#19).
    - The replay flag must be exception-safe (#10).
    - The input tick needs an upper bound (#6).
    - Weapon-spread RNG helpers are wrong (#24).
    - Do not overwrite global `Time.timeScale` (#18).
12. **Delta compression is not needed at this scale.** External estimates about 170–340 kbit/s per client at 32 players with quantisation alone (#34, Nice-to-have). This removes the hardest part of the legacy system.

## 3. Draft vs evidence, topic by topic

| Topic | Old draft position | External best practice | Legacy CSP / Mirage facts | Phase 1 leaning (to confirm in phase 2) |
|---|---|---|---|---|
| Sim time model | Fixed tick; variable dt only for rendering | Fixed tick (#2); variable dt is contested but rare for FPS (Unreal CMC is the exception) | Legacy: fixed 50 Hz | **Fixed tick** (settled in practice) |
| Tick driver | `FixedUpdate` | Own loop on unscaled time (#28) | Legacy `TickRunner` = Stopwatch loop from `Update`; Mirage has no FixedUpdate hook; `ManualUpdate` lets the loop pump the network | **Own `TickRunner`-style loop** that also drives Mirage receive/send |
| Clock ownership | Self-clocking: owner numbers ticks, server follows | Choose explicitly (#5); both families ship; server-tick timeline needed for remotes and lag comp either way | Legacy: server master, client ahead by RTT + 2σ | **Open: the main phase 2 decision** (§4 D1) |
| Client lead control | None (self-clocking) | Server buffer feedback, ±5–10% dilation (#20); never run the clock backwards (#17) | Legacy: RTT model, bang-bang ±1–5%; buffer depth already measured but unused | If the server is master: drive the lead from **server input-buffer depth**, not RTT |
| Input transport | Unreliable ServerRpc, last 3 inputs | Unreliable + redundancy ≥ 3 or all unacked (#4) | Legacy: Notify + ack-driven redundancy, cap 8; Mirage: unreliable has no seq and is MTU-limited; Notify exists | Unreliable or Notify with a tick in every payload; redundancy counted in packets, not ticks |
| Missing input | Pause the object | Repeat continuous input, strip one-shots, then neutral; drop late (#6) | Legacy: `MissingInput` hook, default repeat-last | **Repeat with one-shots stripped** (reverses the draft) |
| Rate limit / anti-cheat | Token bucket 1.02–1.1×, catch-up ≤ 5 per frame | ~1 input per server tick, bounded catch-up, clamp far-future/past ticks (#7) | Legacy: one input per server tick by construction; no upper bound on the tick (bug) | Bounded catch-up plus tick-window clamp; exact numbers in phase 2 |
| Reconcile trigger | Epsilon compare, then replay | Always replay first; compare as an optimisation (#21) | Legacy: bitwise whole-world compare | Open (§4 D5): cheap either way at this scale |
| Re-baseline | Yes, on both paths | Yes (#8) | Legacy: yes (copies server state) | **Yes** |
| Predicted state contents | Position, velocity | Every sim-affecting field: stance, grounded, ammo, `nextFireTick`, `reloadEndTick`, RNG counter (#9) | Example3 lessons in 10-Pain-Points §3.2 | Contract: timers as ticks, RNG seeded by tick |
| Replay side effects | Not covered | Run once only (`IsFirstTimePredicted`-style) or diff in the view layer (#10) | Legacy: `IsResimulation` flag (not exception-safe) | **Add `IsReplaying` + first-time-only events** |
| Visual smoothing | Phase 2 optional component | Essential: error offset decaying over ~100 ms, snap threshold (#11), render interpolation (#13) | Legacy: `ResimulationTransition` wrote into sim history (bug) | Keep it separate from the sim; consider moving it into v1 |
| Camera | Rotation is client input, never rolled back | Same (#12) | — | **Agreed** |
| Remote objects | Snapshot interpolation ~100 ms on the tick timeline | Server-tick timeline, ~2 send intervals + jitter, capped extrapolation (#14); adaptive buffer (#30) | Legacy interpolation unfinished (clock never fed) | Build new, on the **server tick** timeline |
| Lag compensation | Not covered | Essential (#15): per-tick hitbox history, rewind to the shooter's reported view tick, clamp | Legacy Example3: none (see 10-Pain-Points §3.2) | **In scope**, at least the hook: input carries the view tick |
| Ticks per owner | Per object, independent | Reconcile one owner's interacting predicted objects at the same tick (#24) | Legacy: one global tick | One tick numbering (global, or per owner); replay all of that owner's predicted behaviours together |
| Transport for state | ClientRpc `{tick, state}` | — | Mirage: SyncVars reliable-only, so unsuitable for predicted state | Unreliable RPC or custom message; owner `{inputTick, state}` and observer `{serverTick, state}` may be separate |
| Physics | Kinematic / CharacterController only | Kinematic character; no dynamic Rigidbody in the predicted set (#27); full-scene resim is overkill (#44) | Legacy: whole-scene `Physics.Simulate` | **Kinematic only** (settled) |
| Delta compression | Out of scope | Not needed at 32 players (#34) | Legacy: the main complexity source | **Out** (settled) |
| Spawn prediction | Out of scope | Projectiles: nice-to-have (#35) | — | **Out for v1** |
| Host mode | Run the authoritative path directly | — | Mirage runs RPCs inline in host mode; the legacy repo special-cased host | Explicit host path needed (design in phase 2) |
| Testing | Not covered | Essential: simulated lag/jitter/loss always on; correction counters, expect zero (#18–19) | Legacy `LagSocket` (fix bug #26) + `TickDebugger` reusable | Reuse `LagSocket`; add a correction counter |

## 4. Decisions for phase 2

Ordered by how much else depends on them.

1. **D1 — Clock model.**
   - Options:
     - (a) Server tick master with the client running ahead, its lead steered by server input-buffer depth. This is a simplified and fixed `ClientTickRunner`.
     - (b) Source-style: the owner's command stream is consumed per player with a budget, and the client estimates server time for interpolation and lag comp.
     - (c) A hybrid of the two.
   - Evidence: external [01](../research/External-Best-Practices/01-Time-and-Ticks.md) and [12 §1](../research/External-Best-Practices/12-Contested-Points.md); [TickRunner report](../research/ClientSidePrediction/TickRunner-and-TimeSync.md) §5–6; my review §3.1–3.3.
   - Note: with (a), tick numbering is global, so D3 and partial rollback (#24) are solved for free.
2. **D2 — Tick driver and Mirage integration.**
   - Should a `TickRunner` loop own the fixed step and pump Mirage via `ManualUpdate`? Or should SimpleCSP run beside Mirage's `Update`?
   - How do ServerRpc-delivered inputs reach the right tick?
   - Evidence: [Mirage 04](../research/Mirage/04-Update-Loop-and-Timing.md), [TickRunner report](../research/ClientSidePrediction/TickRunner-and-TimeSync.md), legacy bug #2 (`ManualUpdate` never set).
3. **D3 — Tick scope and replay grouping.** Per object, per owner, or global? How do movement and weapon behaviours on one player order and replay together?
4. **D4 — Transport.**
   - Generic RPCs with plain `TInput`/`TState`, an `ArraySegment<byte>` payload, or custom messages?
   - Unreliable or Notify?
   - One correction message, or separate owner and observer messages?
   - Evidence: [Mirage 03](../research/Mirage/03-RPCs-and-Messages.md), [Mirage 07](../research/Mirage/07-Gaps-and-Hooks-for-CSP.md), legacy [07-Messages](../research/ClientSidePrediction/07-Messages-and-Protocol.md).
5. **D5 — Reconcile trigger.** Always replay, or compare with an epsilon? What goes into `CompareState`?
6. **D6 — Missing input and anti-cheat bounds.** Repeat-then-neutral with one-shot stripping, the catch-up budget, the tick-window clamp, and the maximum rewind.
7. **D7 — API shape.**
   - `PredictionBehaviour<TInput, TState>` methods and the side-effect contract (`IsReplaying`, first-time events).
   - What carries over from the legacy API (`NetworkInputs<T>`, `MissingInput`).
8. **D8 — Remote interpolation and lag compensation scope.** The interpolation clock, buffer sizing, and whether lag comp is in SimpleCSP or only a hook.
9. **D9 — Smoothing.** Is the visual smoother and render interpolation in v1 or later? External marks both Essential.
10. **D10 — Host mode and server-owned predicted objects** (bots).
11. **D11 — Debug and test tooling.** Reuse `LagSocket` and `TickDebugger`; add correction counters and a forced-rollback test mode.

## 5. Caveats

- **`main` vs `master`.** The SyncVar-as-properties work (and the analyzer work) exists only on `origin/master` @ `7116e9f`, not on `main`. On `main`, `[SyncVar]` is still field-only (`CustomAttributes.cs:10`, `AttributeTargets.Field`). The Mirage research describes `main`. If SimpleCSP is built on a branch that includes the `master` work, recheck [Mirage 02](../research/Mirage/02-SyncVars-and-State-Sync.md) and [05](../research/Mirage/05-Serialization.md).
- The Mirage report lists its unverified items in [07 §8](../research/Mirage/07-Gaps-and-Hooks-for-CSP.md). The most relevant is `T[]` RPC parameters inside a generic class; a short Weaver test would settle it.
- The TickRunner report's dynamics numbers (settling time, hitch effects) come from a Python model of the controller, not from Unity.
- External items marked *(unverified)* or secondary-source in that folder should be checked before a phase 2 decision rests on them alone.
