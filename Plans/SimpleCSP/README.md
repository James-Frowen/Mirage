# SimpleCSP

SimpleCSP is a planned per-network-object client-side prediction system for Mirage, aimed at a server-authoritative FPS with 16-32 players. Each predicted object owns its own input, state history and reconciliation, instead of the whole world being snapshotted as one block. It replaces the complex whole-world `ClientSidePrediction` repo.

## Status

- **Phase 1: information gathering.** Complete. Review of the old draft docs (`planning/00`), research on the legacy CSP repo, Mirage and external best practice (`research/`), and a cross-checked summary (`planning/01`).
- **Phase 2: full system planning.** Not started. It begins with the decisions D1–D11 listed in [`planning/01-Phase1-Summary.md`](planning/01-Phase1-Summary.md) §4.

## old-draft/

Earlier draft material, kept for history.

- [`1-original-plan/`](old-draft/1-original-plan/): the first-round plan docs
  - [`OverallPlan.md`](old-draft/1-original-plan/OverallPlan.md): overall plan
  - [`Decision_FixedUpdate_vs_CustomTick.md`](old-draft/1-original-plan/Decision_FixedUpdate_vs_CustomTick.md): FixedUpdate vs a custom tick
  - [`Decision_GenericBaseClass.md`](old-draft/1-original-plan/Decision_GenericBaseClass.md): generic base class design
  - [`Decision_ProxyInterpolation.md`](old-draft/1-original-plan/Decision_ProxyInterpolation.md): interpolation of non-owned (proxy) objects
- [`2-timing-decisions/`](old-draft/2-timing-decisions/): second-round timing docs
  - [`Decision_FixedTick_vs_VariableTime.md`](old-draft/2-timing-decisions/Decision_FixedTick_vs_VariableTime.md): fixed tick vs variable dt, and the FPS camera
  - [`Decision_Fixed_vs_variable-glm.md`](old-draft/2-timing-decisions/Decision_Fixed_vs_variable-glm.md): fixed vs variable dt, distilled from session round 4
  - [`TimingSystem_ApproachA_vs_Legacy.md`](old-draft/2-timing-decisions/TimingSystem_ApproachA_vs_Legacy.md): self-clocking Approach A compared with the legacy tick runner
- [`design-session-2026-09-02.md`](old-draft/design-session-2026-09-02.md): full design-session transcript that produced the docs above

## research/

Background reports on existing systems. Treat these as facts about the current code; opinion is marked in the files.

### research/ClientSidePrediction/

- [`README.md`](research/ClientSidePrediction/README.md): CSP research report overview
- [`01-Architecture-and-Lifecycle.md`](research/ClientSidePrediction/01-Architecture-and-Lifecycle.md): PredictionManager, server and client managers
- [`02-PredictionBehaviour-API.md`](research/ClientSidePrediction/02-PredictionBehaviour-API.md): the `PredictionBehaviour` API a user writes
- [`03-Inputs-and-Server-Input-Handling.md`](research/ClientSidePrediction/03-Inputs-and-Server-Input-Handling.md): tick-addressed inputs and how the server handles them
- [`04-State-Snapshots-and-DeltaSnapshot.md`](research/ClientSidePrediction/04-State-Snapshots-and-DeltaSnapshot.md): unmanaged state history, snapshots and delta snapshots
- [`05-Reconciliation-and-Resimulation.md`](research/ClientSidePrediction/05-Reconciliation-and-Resimulation.md): misprediction detection, rollback and replay
- [`06-Interpolation-and-Remote-Objects.md`](research/ClientSidePrediction/06-Interpolation-and-Remote-Objects.md): how non-owned objects are treated
- [`07-Messages-and-Protocol.md`](research/ClientSidePrediction/07-Messages-and-Protocol.md): the three network messages and the protocol
- [`08-Debugging-Tools.md`](research/ClientSidePrediction/08-Debugging-Tools.md): test harnesses and latency simulation tools
- [`09-Utils.md`](research/ClientSidePrediction/09-Utils.md): ring buffers and other helpers, with reuse potential
- [`10-Pain-Points-and-Reuse-Assessment.md`](research/ClientSidePrediction/10-Pain-Points-and-Reuse-Assessment.md): opinion: pain points and what to reuse for SimpleCSP
- [`TickRunner-and-TimeSync.md`](research/ClientSidePrediction/TickRunner-and-TimeSync.md): deep-dive on `TickRunner`, `ClientTickRunner` and clock sync
- [`TickRunner-and-TimeSync-Mirage.md`](research/ClientSidePrediction/TickRunner-and-TimeSync-Mirage.md): Mirage's built-in time sync and whether it can align CSP ticks

### research/Mirage/

Verified against `main` @ `0638bf8e5` (v159.1.1).

- [`README.md`](research/Mirage/README.md): architecture on one page and 18 facts that matter for per-object CSP
- [`01-Objects-Ownership-Lifecycle.md`](research/Mirage/01-Objects-Ownership-Lifecycle.md): NetworkIdentity, NetworkBehaviour, ownership, lifecycle hooks
- [`02-SyncVars-and-State-Sync.md`](research/Mirage/02-SyncVars-and-State-Sync.md): SyncVar dirty tracking and per-frame sending
- [`03-RPCs-and-Messages.md`](research/Mirage/03-RPCs-and-Messages.md): ServerRpc / ClientRpc and messages
- [`04-Update-Loop-and-Timing.md`](research/Mirage/04-Update-Loop-and-Timing.md): update loop and timing
- [`05-Serialization.md`](research/Mirage/05-Serialization.md): bit-level NetworkWriter/NetworkReader and weaver-generated serializers
- [`06-Existing-Components.md`](research/Mirage/06-Existing-Components.md): existing components and precedents, mainly NetworkTransform
- [`07-Gaps-and-Hooks-for-CSP.md`](research/Mirage/07-Gaps-and-Hooks-for-CSP.md): assessment: what Mirage gives CSP, what's missing, generics support, gotchas

### research/External-Best-Practices/

An independent baseline from outside sources only (engine source, docs, talks). It was written without reading any of our files, so it can be compared against the old draft.

- [`README.md`](research/External-Best-Practices/README.md): ranked checklist of 46 practices (Essential / Recommended / Nice-to-have / Overkill) and main sources
- [`01-Time-and-Ticks.md`](research/External-Best-Practices/01-Time-and-Ticks.md): fixed vs variable dt, clock ownership families, lead control, hitches
- [`02-Inputs.md`](research/External-Best-Practices/02-Inputs.md): sampling, redundancy, server buffers, missing input, rate limits
- [`03-Reconciliation-and-Replay.md`](research/External-Best-Practices/03-Reconciliation-and-Replay.md): reconcile loop, what to compare, replay side effects
- [`04-Smoothing-and-Camera.md`](research/External-Best-Practices/04-Smoothing-and-Camera.md): visual/sim separation, error decay, render interpolation, camera rules
- [`05-Remote-Entities-Interpolation.md`](research/External-Best-Practices/05-Remote-Entities-Interpolation.md): snapshot interpolation, buffer sizing, adaptive delay
- [`06-Lag-Compensation-and-Hit-Registration.md`](research/External-Best-Practices/06-Lag-Compensation-and-Hit-Registration.md): server rewind, view tick, max rewind, hit markers
- [`07-Weapons-and-Predicted-Gameplay.md`](research/External-Best-Practices/07-Weapons-and-Predicted-Gameplay.md): predicting fire, ammo, reload, spawns, interactions
- [`08-Bandwidth-Messaging-AntiCheat.md`](research/External-Best-Practices/08-Bandwidth-Messaging-AntiCheat.md): send rates, channels, delta compression, validation
- [`09-Testing-and-Debugging.md`](research/External-Best-Practices/09-Testing-and-Debugging.md): network simulation, misprediction visibility, tests
- [`10-Unity-Specific.md`](research/External-Best-Practices/10-Unity-Specific.md): tick loop, physics, CharacterController, GC, Animator, and how Unity netcode libraries do it
- [`11-Reference-Architectures.md`](research/External-Best-Practices/11-Reference-Architectures.md): Quake 3, Source, Overwatch, Unreal, Fusion and others, with a comparison table
- [`12-Contested-Points.md`](research/External-Best-Practices/12-Contested-Points.md): where reputable sources disagree, and what decides it

## planning/

Planning the new system and the decisions needed before starting.

- [`00-Draft-Review-and-Open-Questions.md`](planning/00-Draft-Review-and-Open-Questions.md): review of the old draft docs, gaps for the target game, and open questions
- [`01-Phase1-Summary.md`](planning/01-Phase1-Summary.md): cross-check of old draft vs research vs external practice, and the decisions for phase 2

## Suggested reading order

1. [`planning/01-Phase1-Summary.md`](planning/01-Phase1-Summary.md): the overall picture and the phase 2 decision list
2. [`planning/00-Draft-Review-and-Open-Questions.md`](planning/00-Draft-Review-and-Open-Questions.md): detail on the gaps in the old draft
3. The research READMEs ([legacy CSP](research/ClientSidePrediction/README.md), [Mirage](research/Mirage/README.md), [external](research/External-Best-Practices/README.md)), then the numbered files you need
4. `old-draft/` for history
