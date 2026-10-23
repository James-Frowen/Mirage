# Mirage built-in time sync: what it is, how accurate it is, and whether it can align CSP ticks

Companion to [`TickRunner-and-TimeSync.md`](TickRunner-and-TimeSync.md) (section 4 there is the short version).
Code reviewed: this repo at Mirage package `158.0.0` (`Mirage:Assets/Mirage/package.json:4`). Citation convention: `Mirage:`-prefixed or bare paths are relative to the Mirage repo root (`Assets/Mirage/Runtime/...`, bare names such as `AckSystem.cs:314` are under `Assets/Mirage/Runtime/SocketLayer/Connection/`); `CSP:File.cs:N` is `clientsideprediction/Assets/source/Runtime/File.cs:N`.
Method: read the code; accuracy numbers come from a throw-away Python port of `NetworkTime.OnClientPong` driven by a simple link model (section 3.3). **No Unity run.** Anything from the port is labelled "model".

---

## 1. What exists

| Piece | File | Role |
|---|---|---|
| `NetworkTime` | `Mirage:Assets/Mirage/Runtime/NetworkTime.cs` | ping/pong RTT and clock-offset estimator, one per `NetworkWorld` (`NetworkWorld.cs:49`) |
| `NetworkPingMessage{double ClientTime}`, `NetworkPongMessage{double ClientTime, double ServerTime}` | `Mirage:Assets/Mirage/Runtime/Messages.cs:170-185` | the only two time messages |
| `ExponentialMovingAverage` | `Mirage:Assets/Mirage/Runtime/ExponentialMovingAverage.cs` | `alpha = 2/(n+1)`; EMA of value and of variance (`:14,:24-39`) |
| Registration | `NetworkServer.cs:310` (`OnServerPing`, allowed unauthenticated), `NetworkClient.cs:384` (`OnClientPong`) | |
| Driver | `NetworkClient.Update` calls `World.Time.UpdateClient(this)` every frame while connected and not host, **before** the `ManualUpdate` early return (`NetworkClient.cs:359-372`); one immediate `PingNow` on connect (`:193`) | |
| Consumers inside Mirage | `NetworkPingDisplay` and `NetworkManagerGUI` read `Time.Rtt` (`Mirage:Assets/Mirage/Components/NetworkPingDisplay.cs:20`, `Mirage:Assets/Mirage/Components/NetworkManagerGUI.cs:280`). `NetworkTransformBase` uses `UnityEngine.Time.time`, not `NetworkTime` (`Mirage:Assets/Mirage/Components/NetworkTransformBase.cs:140,153`) | |
| Docs | `doc/docs/guides/clock-sync.md` | says RTT "will also be affected by the frame rate" |

Public API: `Time` (server clock estimate, double), `Offset`, `TimeVar`/`TimeSd`, `Rtt`, `RttVar`/`RttSd`, `PingInterval` (default **2.0 s**, `NetworkTime.cs:19`), `PingWindowSize` (10, `:24`), `PingNow`, `Reset` (`:49-77`).

In host mode there is no ping (`NetworkClient.cs:191-194` guards on `!IsHost`) and the server world's offset stays 0.

## 2. How it works

Client, in `UpdateClient` (frame-bound): if `unscaledTime - lastPing >= PingInterval` send `Ping{ClientTime = Stopwatch now}` on `Channel.Unreliable` (`:57-77`). `Unreliable` messages are batched and flushed at `UpdateSent`, not sent immediately (`Mirage:Assets/Mirage/Runtime/SocketLayer/Connection/ReliableConnection.cs:77-88,286-289`), unlike Notify, which goes out at once (`Mirage:Assets/Mirage/Runtime/SocketLayer/Connection/AckSystem.cs:314-340`).

Server, `OnServerPing` (`:82-93`): reply `Pong{ClientTime = echoed, ServerTime = Stopwatch now}` at the moment the message is handled (frame start in `UpdateReceive`), also `Unreliable`, so the pong waits for the next `UpdateSent`.

Client, `OnClientPong` (`:98-127`), with `c0 = ClientTime`, `s1 = ServerTime`, `c3 = now` (all Stopwatch seconds, each side's own epoch):

    rtt        = c3 - c0                         -> _rtt EMA(10)
    offset^    = c3 - rtt/2 - s1                 (true offset = client clock - server clock)
    bracket    : offset in [c0 - s1 , c3 - s1]   (true offset is guaranteed inside, width = rtt)
    _offsetMin = max(_offsetMin, c0 - s1)        (never decays)
    _offsetMax = min(_offsetMax, c3 - s1)        (never decays)
    if EMA value outside [min,max]: throw EMA away, restart it with this sample
    elif (offset^ >= min || offset^ <= max): add to EMA(10)       <- always true, see 3.4

`Time = Stopwatch - Offset`, recomputed once per Unity frame and cached (`:158-175`). `TimeVar` is the EMA variance of the offset samples (`:181`), `RttVar` of the RTT samples (`:207`).

So this is a Cristian-style one-sample-at-a-time estimator with an interval-intersection sanity check (similar in spirit to NTP's min-RTT filtering), with no outlier rejection beyond the bracket, no slewing (the offset steps by `alpha * error` per pong), and a default sample rate of 0.5 Hz.

## 3. What it measures and how accurate it is

### 3.1 What it measures

- **RTT as the application sees it**, not transport RTT. A sample includes: uplink delay `d1`; the wait until the server's next frame start `w_s`; the wait between handling and flush on the server (in CSP's server loop the flush is `AfterAllTicks`, i.e. after the frame's ticks have run, `CSP:PredictionManager.cs:129-130`); downlink `d2`; and the wait until the client's next frame start `w_c`.
- **Clock offset** assuming the one-way delays are equal and including the same frame waits.

### 3.2 Error structure (derivation, not measured)

Let `t0` be the true send time, `t1 = t0 + d1 + w_s` the true time the server stamps, `t3 = t1 + d2 + w_c` the true time the client stamps. Then

    offset^ - offset = ( (d2 - d1) + (w_c - w_s) ) / 2

So the offset error is half the difference between the downlink and uplink delays (path asymmetry, *not* removable by averaging) plus half the difference of the two frame waits (zero-mean if both sides run at similar frame rates, biased if not). Two stamping details add small biases: the client stamps in `NetworkClient.Update` but the ping leaves at the next `UpdateSent` (up to one frame in a CSP client, where `ManualUpdate = true` and the flush is in `AfterAllTicks`); the server stamps at handling but the pong leaves after the frame's ticks. Both inflate RTT; each shifts the offset estimate by half of its gap, in opposite directions (a client-side gap acts like extra uplink delay, a server-side gap like extra downlink delay).

### 3.3 Model results

Port of `OnClientPong` with `ExponentialMovingAverage(10)`, link one-way = 40 ms + U[0,10] ms, frame times jittered +-10%, 20 minutes unless stated, first 20% discarded as warm-up. Error = `Time` minus true server time.

| Scenario | Mean error | Std dev | Max abs |
|---|---|---|---|
| 60 fps both, ping every 2 s (default) | +0.2..+0.5 ms | 1.1-1.2 ms | 2.8-3.3 ms |
| 60 fps both, ping every 0.2 s | +0.1 ms | 1.2 ms | 3.4 ms |
| client 144 fps, server 50 fps | +3.5 ms | 1.2 ms | 6.5 ms |
| client 30 fps, server 60 fps | -3.9 ms | 1.8 ms | 9.1 ms |
| path asymmetry 60 ms up / 30 ms down | +15.5 ms (= half the asymmetry) | 1.5 ms | - |
| relative clock drift 20 ppm | +0.2 ms | 1.2 ms | 3.5 ms |
| relative clock drift 100 ppm | +0.5 ms | 3.6 ms | 10.5 ms (bracket inverted, EMA reset on every pong: 351 resets) |

RTT EMA read 107-113 ms for a 90 ms wire RTT: the frame waits add about 17-23 ms here.

Reading: with symmetric paths the offset is good to a few ms, which is small against a 20 ms tick. So "very rough" is fair for **RTT** (frame-inflated, 0.5 Hz, 20 s time constant) and for **reaction time**, less so for steady-state offset. Real asymmetry (Wi-Fi uplink, mobile) is the dominant offset error and is invisible to this method.

### 3.4 Quirks in `NetworkTime`

1. `else if (newOffset >= _offsetMin || newOffset <= _offsetMax)` (`:122`) is always true (should be `&&`), so the bracket is used only to *reset* the EMA, never to reject a bad sample.
2. The bracket never ages (`:113-114`). With enough relative clock drift it becomes inconsistent (min > max) and every pong resets the EMA, so `Time` degrades to the raw latest sample (noisier, still unbiased in the model). Only matters for long sessions with a drifting client clock.
3. `PingWindowSize` is documented as tuning smoothness, but `_rtt` and the first `_offset` are constructed with a literal 10 (`:35-36`). `PingWindowSize` is only used in `Reset()` (`:51-52`) and when the offset EMA is thrown away (`:119`). `Reset()` has no callers in Mirage. So setting `PingWindowSize` normally has no effect on `Rtt`.
4. Before the first pong `Offset` is 0, so `Time` on a client is just its own Stopwatch.
5. `Time` is cached per frame by `frameCount` (`:166-171`), so it is consistent within a frame and steps between frames.
6. Pings and pongs are queued on the shared `Unreliable` batch and leave at the next flush (once per frame), so their wire time is later than the stamp by up to a frame (section 3.2).

## 4. RTT and ack information in SocketLayer

- **No RTT or latency value exists anywhere in `SocketLayer`.** A search for rtt/latency/ping finds only a comment (`Mirage:Assets/Mirage/Runtime/SocketLayer/Enums/PacketType.cs:41`, "Similar to ping/pong"). `Config` has `KeepAliveInterval = 2`, `TimeoutDuration = 10`, `TimeBeforeEmptyAck = 0.11`, `ReceivesBeforeEmptyAck = 8`, `EmptyAckLimit = 8` (`Mirage:Assets/Mirage/Runtime/SocketLayer/Config.cs:36,41,72,78,85`). `ITime.Now = Time.unscaledTimeAsDouble` (`Mirage:Assets/Mirage/Runtime/SocketLayer/Peer.cs:7-13`) is only used for ack/keep-alive/timeout scheduling. `Metrics` is counters only (`Metrics.cs`).
- **Notify (`AckSystem`) is the only per-packet delivery signal.** `NetworkPlayer.Send<T>(msg, INotifyCallBack)` (`NetworkPlayer.cs:298-322`) -> `AckSystem.SendNotify` (`AckSystem.cs:314-340`): each packet gets a 16-bit sequence, a latest-received ack sequence and a 64-bit received mask (13-byte header, `AckSystem.cs:12-15`), and is written to the socket immediately. Acks ride on any packet going the other way, plus empty acks after 0.11 s of silence (up to 8) (`AckSystem.cs:229-241,261-266`).
- Delivered/lost is decided when a later ack arrives: `lost = distance >= 64 || bit not set in mask` (`AckSystem.cs:636-660,725-740`). **There is no timer and no timestamp**, so loss detection takes about one return packet (about one tick at 50 Hz in CSP). The callback fires inside the sender's `UpdateReceive`, so it is frame-quantised.
- Consequence: a custom `INotifyCallBack` (CSP's `TickNotifyToken`, `CSP:ITickNotifyTracker.cs:22-52`, is the example) can store a send timestamp and compute an **ack RTT** in `OnDelivered` (`now - sentAt`). It includes the peer's return-packet cadence (up to one peer tick plus a frame) and frame quantisation, so it is not better than CSP's echo in accuracy. Its advantages: samples exist only for packets that were sent (no stale-echo poisoning when the sender stalls), and `OnLost` counts give a free loss rate per stream.
- Caveat: with `DisableReliableLayer` (`NoReliableConnection`, for TCP-like sockets) Notify is simulated: `SendNotify` sends reliably and immediately calls `OnDelivered` (`Mirage:Assets/Mirage/Runtime/SocketLayer/Connection/NoReliableConnection.cs:34-45`), so ack-based timing and CSP's input-redundancy bookkeeping degenerate on those transports.

## 5. Is it adequate for CSP tick alignment?

What CSP needs: at each client tick, a tick number `N` such that the input arrives at the server a small margin before the server simulates `N`, maintained under jitter, loss bursts and hitches, with the margin tracked at tick timescale.

| Need | `NetworkTime` provides | Gap |
|---|---|---|
| Server-to-client clock offset | yes, a few ms in steady state (model); half the path asymmetry is invisible | fine as a seed |
| One-way delay | `Rtt/2`, frame-inflated by about 20 ms, 0.5 Hz | too coarse alone; fine as a prior |
| Jitter for the safety margin | `RttVar`/`RttSd` over 10 pings (20 s at default) | too slow; CSP's controller uses about 25 samples at 50 Hz (0.5 s) |
| Reaction to hitches / loss bursts | none (EMA time constant of seconds; no outlier handling) | CSP's controller needs sub-second reaction |
| Mapping to server **ticks** | none. The server tick counter is the `TickRunner` accumulator, which can drop time (`CSP:TickRunner.cs:211-219`), so `NetworkTime.Time` and server tick can diverge | would need ticks defined as `floor((Time - t0) / dt)` on the server, with catch-up instead of drop, and an epoch/rate handshake |
| A control action | none; it only returns numbers | something still has to steer the client tick (timescale or schedule) |

Verdict: **not adequate as the controller, but it is a usable seed and a usable UI ping.** The authors' description is right about RTT and reaction time and slightly harsh about steady-state offset.

Opinion on how it could still be used: if the server tick is made a pure function of a server Stopwatch (catch-up, never drop), the client could compute its target tick directly as `(NetworkTime.Time + Rtt/2 + margin) / dt` and re-seed only on large errors, instead of steering with 1.05x/0.99x. The `margin` would still need a per-packet jitter estimate (from ack RTT or state inter-arrival at tick rate), because `NetworkTime` cannot supply that. That is a different design from the legacy "observe the received server tick and nudge" loop, and it shifts the failure mode from "controller oscillation after a hitch" to "offset error and server catch-up policy".

CSP itself does not use `NetworkTime` for any of this: it sets `Client.World.Time.PingInterval = PingInterval` (0.2 s) in `CSP:PredictionManager.cs:177` (and `CSP:Debugging/SingleInstanceDebugStart.cs:179`), and no CSP code reads `Rtt` or `Time` (grep). Its RTT comes from the `ClientTime` echo instead (main file, section 3.3).
