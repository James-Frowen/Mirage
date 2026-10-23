# SimpleCSP

SimpleCSP is a planned per-network-object client-side prediction system for Mirage, aimed at a server-authoritative FPS with 16-32 players. Each predicted object owns its own input, state history and reconciliation, instead of the whole world being snapshotted as one block. It replaces the complex whole-world `ClientSidePrediction` repo.

## Status

- **Phase 1: information gathering.** Review of the old draft docs (`planning/00`) and research on the existing systems (`research/`). Mirage research may still be in progress: `research/Mirage/` has no `README.md` or `07-*` file yet.
- **Phase 2: full system planning.** Not started. It needs the decisions listed in `planning/00` first.

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

In progress: no `README.md` or `07-*` file yet.

- [`01-Objects-Ownership-Lifecycle.md`](research/Mirage/01-Objects-Ownership-Lifecycle.md): NetworkIdentity, NetworkBehaviour, ownership, lifecycle hooks
- [`02-SyncVars-and-State-Sync.md`](research/Mirage/02-SyncVars-and-State-Sync.md): SyncVar dirty tracking and per-frame sending
- [`03-RPCs-and-Messages.md`](research/Mirage/03-RPCs-and-Messages.md): ServerRpc / ClientRpc and messages
- [`04-Update-Loop-and-Timing.md`](research/Mirage/04-Update-Loop-and-Timing.md): update loop and timing
- [`05-Serialization.md`](research/Mirage/05-Serialization.md): bit-level NetworkWriter/NetworkReader and weaver-generated serializers
- [`06-Existing-Components.md`](research/Mirage/06-Existing-Components.md): existing components and precedents, mainly NetworkTransform

## planning/

Planning the new system and the decisions needed before starting.

- [`00-Draft-Review-and-Open-Questions.md`](planning/00-Draft-Review-and-Open-Questions.md): review of the old draft docs, gaps for the target game, and open questions

## Suggested reading order

1. [`planning/00-Draft-Review-and-Open-Questions.md`](planning/00-Draft-Review-and-Open-Questions.md)
2. The research READMEs, starting with [`research/ClientSidePrediction/README.md`](research/ClientSidePrediction/README.md), then the numbered files you need
3. `old-draft/` for history
