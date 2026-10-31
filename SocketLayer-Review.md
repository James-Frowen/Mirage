# Mirage SocketLayer – Code Review

**Scope:** `Assets/Mirage/Runtime/SocketLayer/` (Peer, Connection, ReliableConnection, NoReliableConnection, AckSystem, RingBuffer, Sequencer, Batch, Pool, Config, ConnectKeyValidator, trackers). I followed into the built-in UDP sockets and `NetworkServer`/`NetworkClient`/`NetworkPlayer` only to judge real-world impact.

**Method:** I read every file in the SocketLayer. Each Bug/Security/Performance candidate went to a separate subagent, which tried to refute it with code tracing and, where useful, Python simulations of the sequence/ring-buffer and fragment-reassembly logic. Every item below was **confirmed** (✅) or **partially confirmed** (◐); nothing that was refuted is listed. I checked the Docs/Nitpick items directly; they did not go to subagents. This environment has no Unity or dotnet, so no item has a runtime repro.

**Transport scope tags**

- **ALL** – affects every socket, including Steam. Most of these need Mirage's own reliable layer (`DisableReliableLayer = false`, which is the default for the `RawUdp`/`StatefulUdp` profiles). The `WebSocket` profile turns that layer off and is not affected by those items.
- **UDP** – affects only stateless sockets (the built-in UDP/NanoSocket), where a connection is identified purely by source IP:port. Steam authenticates the peer's identity, routes by handle and hides IPs behind relays, so it is not affected.

---

## Summary

| # | Issue | Category | Score | Scope |
|---|-------|----------|:-----:|:-----:|
| 1 | `FinishDisconnect` raises the event before cleanup, so a throwing handler leaks the connection slot permanently | Bug | **6** | ALL |
| 2 | Fragment reassembly leaks stale bytes from the shared buffer (cross-player memory disclosure) | Security | **6** | ALL |
| 3 | No receive-side limit on out-of-order reliable buffering (~2.5 MB per connection, about 65× amplification) | Security | **5** | ALL |
| 4 | The ring-buffer window is really only half the capacity; past that, acking stops for good; config not validated | Bug | **5** | ALL |
| 5 | A connected peer can flood Error logs with 2-byte `ConnectionRejected` commands | Security | **4** | ALL |
| 6 | O(window) ack scan for every received packet, and no per-frame receive budget | Performance | **4** | ALL |
| 7 | `UpdateSent` has no per-connection exception isolation | Bug | **4** | ALL |
| 8 | `SendAck` sends every ack packet twice | Performance | **3** | ALL |
| 9 | A fragmented send that fails part-way uses up order numbers and leaks pooled objects | Bug | **2** | ALL |
| 10 | `AckSystem.Dispose` never fires `OnLost` and doesn't return `ReliablePacket`s to the pool | Bug | **2** | ALL |
| 11 | A failed `Bind`/`Connect` leaves the Peer `_active` and pinned by `Application.quitting` | Bug | **2** | ALL |
| 12 | `HandleConnectionRejected` reads `Span[2]` without a length check | Bug | **1** | ALL |
| 13 | A zero-length packet with Log-level logging enabled throws and disconnects | Bug | **1** | ALL |
| 14 | The optional `logger = null` crashes on many unguarded call sites | Bug | **1** | ALL |
| 15 | Stateful `handle.SocketLayerConnection` is never cleared | Bug | **1** | ALL |
| U1 | Connection-slot exhaustion: no handshake cookie, public key, no per-IP limit | Security | **7** | UDP |
| U2 | A **client** Peer accepts inbound connections (managed `UdpSocket`), allowing a remote kick or server impersonation | Security | **6** | UDP |
| U3 | No session token: spoofed Disconnect, message injection, forged acks | Security | **6** | UDP |
| U4 | A negative receive length aborts the whole receive loop for the frame (Windows + NanoSocket) | Security | **5** | UDP |
| U5 | Unauthenticated Warning-log flood (bad key / server full) plus 3-byte reject replies | Security | **4** | UDP |
| D1–D8 | Docs/Comments | Docs | 1–2 | – |
| N1–N6 | Nitpick/Style | Nitpick | 0–1 | – |

**Priority for a Steam-based game:** #1, #2, #3 and #4. **Additional priority if the built-in UDP socket ships:** U1, U2, U3 and U4.

---

## Good Changes
*none* (this is a review of the existing code, not of a diff)

## Fixed Issues
*none*

## NEW ISSUES
*none* (no code change was under review; everything is listed under Other Issues)

## Other Issues

### Affects all transports (including Steam)

#### 1. `FinishDisconnect` invokes `OnDisconnected` before cleanup — [Bug] (6/10) ✅
- **Where:** `Peer.cs:752-777`. The state is set to `Disconnected` at :763, the event fires at :766, and only after that does cleanup run (:768-776: `_disconnectedTracker.OnDisconnect()`, or `Handle.Disconnect` + `RemoveConnection` for stateful handles). Nothing wraps the event call in try/catch.
- **What can throw:**
  - `NetworkServer.Peer_OnDisconnected` → `OnDisconnected` (`NetworkServer.cs:698-716`). This raises `Disconnected`, a plain `UnityEvent` that does **not** catch exceptions.
  - `DestroyOwnedObjects`, which runs `World.onUnspawn` and `OnIdentityChanged` handlers.
  - Any user listener that throws, for example an NRE on `player.Identity` for someone who disconnects before spawning.
- **Impact:**
  - `_isDisonnected` is never set, so `TimeToRemove()` is never true. The connection stays in `_connections` / `_connectionLookup` for the rest of the Peer's life and counts against `MaxConnections`.
  - On UDP, packets from that IP:port go to the zombie connection, which ignores `ConnectRequest` while Disconnected.
  - On Steam, `Handle.Disconnect` is never called.
  - On the timeout path the exception also escapes `UpdateSent` (see #7).
  - On the receive path, `DisconnectFromReceiveError` → `DisconnectInternal` returns early because the state is already Disconnected.
  - If a client can reliably trigger the throwing condition, it can connect and disconnect repeatedly until the server reports "full".
- **Fix:** do the cleanup in a `finally` block (or before raising the event), and wrap the event call in try/catch with logging.

#### 2. Fragment reassembly delivers stale shared-buffer data — [Security] (6/10) ✅
- **Where:** `ReliableConnection.cs:154-264`.
  - Fragment `i` is copied to the fixed offset `SizePerFragment * i` (:239).
  - `messageLength` is the **sum of the actual lengths**, and the delivered range is `[0, messageLength)` (:245).
  - A short first or middle fragment is accepted. The `==` check at :215 is only a `_logger?.Assert`, and only `>` is rejected (:209, :233). `IsValidSize` allows a 1-byte fragment payload.
  - The destination `_peer._fragmentBuffer` is a single array shared by **all connections** and never cleared (`Peer.cs:63,106`).
- **Example (UDP MTU 1232, SPF = 1216):**
  - Player B sends a 3000-byte message.
  - The attacker then sends a 2-fragment message: 2 bytes, then 1216 bytes.
  - The attacker's message as delivered contains bytes `[2,1216)` of B's message (simulation-verified).
  - The best 50-fragment layout leaks about 30 KB per message.
- **Built-in reflection path:** `NetworkPingMessage` is `allowUnauthenticated` (`NetworkServer.cs:329`), and `OnServerPing` echoes `ClientTime` (`NetworkTime.cs:82-94`). A crafted fragmented ping therefore returns 8 stale bytes from the last large client→server message, even before authentication. Any app-level echo, such as chat relay, leaks far more.
- Legitimate senders always send full non-final fragments (`AckSystem.cs:398-401`), so normal traffic is unaffected. Only the server side is a real target; on the client the buffer only ever holds the server's own data.
- **Fix:** reject any non-final fragment whose length isn't exactly `SizePerFragment`, and require every follow-on packet to have `IsFragment` set. Optionally use a per-connection buffer, or clear the buffer after use.

#### 3. Unbounded out-of-order reliable receive buffering — [Security] (5/10) ✅
- **Where:** `AckSystem.cs:528-548`. Every new in-window order takes a pooled `MaxPacketSize` buffer and holds it until it can be delivered in order. Nothing limits this on the receive side; only the send side has `MaxReliablePacketsInSendBufferPerConnection`.
- **Attack:** a connected peer never sends order `read` but sends `read+1 … read+2047`. The bound is cap/2−1 = 2047 buffers at the default 12 bits.
- **Impact:**
  - About 2.52 MB per connection at UDP MTU, from about 39 KB of 19-byte packets (≈65× amplification). That is about 250 MB at 100 connections.
  - It scales **linearly with the socket's `MaxPacketSize`**, so check the value your Steam socket uses. It roughly doubles with each extra `SequenceSize` bit.
  - Once the pool exceeds `BufferPoolMaxSize`, there is also GC churn.
- **Fix:** cap the receive-side buffered count (or bytes) per connection and disconnect when it is exceeded.

#### 4. Usable ring-buffer window is half its capacity, with no enforcement or config validation — [Bug] (5/10) ◐
- **Where:**
  - `Sequencer.Distance` is signed in `[-cap/2, cap/2-1]`, but `RingBuffer.IsFull` (`RingBuffer.cs:57`) only fires at span `cap-1`.
  - Once the span reaches cap/2, `AckMessagesInSentQueue` computes `count` as negative and loops zero times (`AckSystem.cs:630-641`), and `CheckSentQueue` returns early (:619).
  - From then on **no acks are ever processed**. The connection either wedges until `BufferFullException`, or, with light traffic, stays "connected" indefinitely with a dead reliable channel, because keepalives stop the timeout from firing.
- **Triggers:**
  - The 2000 limit counts filled slots, not the span (`AckSystem.cs:448`).
  - One reliable packet that stays unacked pins `Read` while notifies and resends keep using new sequence numbers.
  - `SendNotify` only checks `IsFull` (:326), so more than 2048 notifies during a stall shorter than 10 s wedges the connection.
  - With a smaller `SequenceSize`, ordinary bursts are enough: 128 packets before the first ack at 8 bits, or 600 at 10 bits (simulation-verified).
  - Receive side (mostly theoretical): `ReceiveReliable` acks the sequence (`:523`) **before** dropping the packet as "old" (`:530`), so if it ever happens the loss is silent.
- `Config.cs:11` says "todo add validation"; `Peer.cs:93` only checks `SequenceSize > 16`.
- **Fix:**
  - Treat "span ≥ cap/2" as full: `IsFull` / `ThrowIfBufferLimitReached` should check the span as well as the count.
  - Validate `MaxReliablePacketsInSendBufferPerConnection < 2^(SequenceSize-1)`.
  - Check the order window before acking.

#### 5. Error-log flood from a connected peer — [Security] (4/10) ✅
- **Where:** `Peer.cs:610-623`. A connected peer sends `[Command, ConnectionRejected]`, which is 2 bytes. That reaches the `default:` branch and an unconditional `_logger?.Error(...)` with no disconnect, and it even refreshes the receive timeout (:544).
- **Impact:**
  - The Peer logger defaults to Warning (`LogFactory.cs:20`), so this logs, and it still logs if the filter is raised to Error.
  - Unity log calls capture stack traces and write to the player log, so this can be repeated at packet rate to burn CPU and fill the disk.
  - The same branch is hit by late packets on destroyed stateful connections (#15).
  - There are smaller bounded windows: the Warning at :504-506 (receives while not connected) and the Error at :585-586 (client side).
- **Fix:** ignore unexpected commands silently or at Log level, or disconnect on protocol violations; rate-limit per connection.

#### 6. O(window) ack processing per packet, and no receive budget — [Performance] (4/10) ✅
- **Where:**
  - Every received Notify, Reliable, Fragment or Ack packet calls `CheckSentQueue` → `AckMessagesInSentQueue`, which walks every slot from Read to Write (up to 2047) (`AckSystem.cs:616-642`).
  - `Peer.UpdateReceive` drains the socket without limit (`Peer.cs:303-342`).
  - There is no per-connection rate limit at this layer; `RateLimitBucket` is only used at the high level.
- **Impact:** a peer that keeps the server's in-flight window just under 2048 (by withholding acks), then floods 11-byte Ack packets, makes the main thread do roughly 5–10× the normal per-packet work. "Old" acks short-circuit, so the attacker has to keep the window positioned carefully. #8 doubles the legitimate cost.
- **Fix:** only scan from the previous ack position to the new one, or skip the scan when `ackSequence`/mask haven't changed. Consider a per-frame or per-connection receive budget.

#### 7. `UpdateSent` has no per-connection exception isolation — [Bug] (4/10) ✅
- **Where:** `Peer.cs:266-276` and `:802-814`; `NetworkServer.UpdateSent` (:439-445) doesn't catch either. `Connection.UpdateConnected` only catches `BufferFullException` (`Connection.cs:207`).
- **Throw sources:**
  - #1 (handler exceptions).
  - Managed `UdpSocket.Send` calls `SendTo` with no try/catch (`UdpSocket.cs:96-103`): WouldBlock, ENOBUFS, NetUnreachable. Since 2196689, this is the only socket on macOS, iOS, Android and ARM.
- **Impact:**
  - For the rest of that frame, later connections get no flush, timeout check or keepalive, and `Flush` and metrics are skipped.
  - If the exception repeats, the same tail of the list is starved every frame, because the list order never changes.
  - `Peer.Close` (:160-167) has the same shape: a throwing `Send` skips `_socket.Close()`.
- **Fix:** wrap each `connection.Update()` in try/catch → log → disconnect that connection. Wrap the per-connection disconnect in `Close` the same way.

#### 8. `SendAck` sends each ack twice — [Performance] (3/10) ✅
- **Where:** `AckSystem.cs:305-306`. It calls `_connection.SendRaw(...)` and then `Send(...)`, which calls `SendRaw` again (:281).
- **Impact:** every ack-only packet goes out twice: up to 16 per idle period, plus the "receives > 8" path. Send metrics are double-counted, and the receiver pays #6 twice. No test asserts this behaviour, so it looks accidental.
- **Fix:** delete line 305.

#### 9. A fragmented send that fails part-way consumes orders and leaks pooled objects — [Bug] (2/10) ◐
- **Where:**
  - `AckSystem.cs:350-408`. `SendReliable` checks `IsFull` once.
  - Each fragment runs `CreateReliableBuffer` (which uses up an order number and takes pooled objects) **before** `SendReliablePacket` can throw (:429/:448).
  - If it throws at fragment k, fragments 0..k-1 are already on the wire, order k is never sent (a permanent gap in the ordered stream), and fragment k's buffer is never released.
- **Mitigation:** `NetworkPlayer.Send` catches `BufferFullException` and disconnects (`NetworkPlayer.cs:283-287`), so Mirage's high level is safe. Only direct `IConnection` users who swallow the exception are exposed.
- **Fix:** check the count limit for all N fragments before sending any, and release the packet on failure.

#### 10. `AckSystem.Dispose` drops pending notify tokens — [Bug] (2/10) ✅
- **Where:** `AckSystem.cs:126-177`. Buffers are released, but pending `INotifyCallBack`s never get `OnLost()`, so `NotifyToken.Lost` never fires. `ReliablePacket`s are not returned to `_reliablePool`. Code waiting on Delivered/Lost can wait forever.
- **Fix:** call `OnLost()` for remaining notify entries on dispose (or document that it isn't called), and `Put` the reliable packets back.

#### 11. A failed `Bind`/`Connect` leaves the Peer active and pinned — [Bug] (2/10) ◐
- **Where:**
  - `Peer.cs:126-147` sets `_active = true` **before** `_socket.Bind/Connect`.
  - The ctor subscribes to the static `Application.quitting` (:115), and that is only removed in `Close()` when the Peer is active.
- **Impact:** if the port is already in use, `StartServer` throws, and the old Peer (buffer pool, fragment buffer, socket, and the server graph through `DataHandler`) stays reachable until quit. Each retry also adds another `Application.quitting += Stop`.
- `Close()` leaving stateless connections undisposed (:160-164) is harmless, because Mirage discards the Peer straight away.
- **Fix:** set `_active` only after a successful bind/connect, and unsubscribe `Application.quitting` on failure.

#### 12. `HandleConnectionRejected` reads `Span[2]` unchecked — [Bug] (1/10) ✅
- **Where:** `Peer.cs:615`. A Command only needs to be 2 bytes, so a 2-byte reject throws `IndexOutOfRangeException`. That is caught, logged as an Error with the full exception, and then disconnects. The outcome is the same as a valid reject, but noisier.
- **Fix:** handle it like `HandleConnectionDisconnect` does (`packet.Length >= 3 ? … : RejectReason.None`).

#### 13. A zero-length packet with Log-level enabled disconnects — [Bug] (1/10) ✅
- **Where:** `Peer.cs:474-480`. The "too small" branch logs `packet.Type`, which reads `Span[0]` of an empty span. This only happens with verbose logging on and a socket that delivers 0-byte datagrams (NanoSocket does; UdpSocket filters them).
- **Fix:** don't read `packet.Type` when `Length == 0`.

#### 14. The optional `logger = null` is not actually supported — [Bug] (1/10) ✅
- **Where:** many unguarded `_logger.Error/Warn/LogException` calls, for example `Peer.cs:326, 339, 347, 402, 415`, `Connection.cs:238`, `ReliableConnection.cs:287`.
- **Impact:** with a null logger, an exception path throws NREs from inside the `catch` blocks and escapes `UpdateReceive` without disconnecting the sender. Production callers always pass a logger.
- **Fix:** use `?.` throughout, or make the logger required (or default it to a null-object logger).

#### 15. Stateful `handle.SocketLayerConnection` is never cleared — [Bug] (1/10) ✅
- **Where:** `Peer.cs:735` is the only assignment. Late packets for a Destroyed connection are mostly ignored, but `ConnectionRejected` hits the Error log (#5).
- Keeping the reference actually prevents a stale handle from re-entering `HandleNewConnection`, so the fix is to make Destroyed connections drop everything silently, **not** to clear the reference.

### UDP only (stateless sockets: the built-in UDP / NanoSocket)

#### U1. Connection-slot exhaustion — [Security] (7/10) ✅
- **Where:**
  - `Peer.cs:637-741`. The **first** valid `ConnectRequest` allocates a full connection and raises `OnConnected`; there is no stateless cookie or challenge.
  - The key is public: SHA256 of `"Mirage V{major}"` (`ConnectKeyValidator.cs:23-38`). A custom key ships in the client binary anyway.
  - There is no per-IP limit (`Peer.cs:660-662` has a todo for it).
- **Impact:**
  - `NetworkServer.MaxConnections` defaults to **4** (`NetworkServer.cs:63`).
  - Without spoofing, an attacker opens N source ports and keeps them alive with 1-byte `KeepAlive` packets sent within every 10 s.
  - With no Authenticator, those slots are held forever. With one, the auth timeout (60 s by default) frees them, but the attacker simply reconnects.
  - With spoofing, 4 packets fill the server for about 11 s, and the server sends Accepted/KeepAlive to the victims (the accept path gives about 11× reflection, capped by the slot count).
  - Reject-path reflection (3 bytes back for ≥2 bytes in) is negligible.
- **Steam:** probably still possible with one account opening several connections, but the attacker is identifiable and can be banned. I couldn't check this; the Steam code isn't in this repo.
- **Fix:**
  - Use a stateless connect challenge (cookie) before allocating anything.
  - Add a per-IP connection limit.
  - Add a short unauthenticated-slot timeout or reserved slots.

#### U2. A client Peer accepts inbound connections — [Security] (6/10) ✅
- **Where:**
  - The Peer has no client/server mode. On a **client**, an unknown source still goes `OnData` → `HandleNewConnection` → `AcceptNewConnection` (`Peer.cs:456-467, 637-666`).
  - The managed `UdpSocket` client never calls OS `Connect()` and uses `ReceiveFrom` from any source (`UdpSocket.cs:72-77, 111`).
  - The managed socket is the **only** option on macOS (Intel + Apple Silicon), iOS and Android, because the `#if` at `NanoSocket.cs:2` excludes them. Since 2196689 it is also used on Windows/Linux ARM64. NanoSocket clients use `connect()` and are not affected.
- **Impact, at the high level:**
  - `NetworkClient.Peer_OnConnected` and `Peer_OnDisconnected` ignore `conn` (`NetworkClient.cs:202-228`).
  - The client `DataHandler` only has a debug `Assert(_connection == connection)` (:486) and then handles the message as if it came from the server.
  - So anyone who can reach the client's UDP port can either:
    - send `ConnectRequest` + `Disconnect` (2 datagrams), or simply wait for the 10 s timeout, to **kick the client** off the real server; or
    - inject arbitrary server messages, i.e. **impersonate the server**. `AuthSuccessMessage` is accepted unauthenticated.
- **Preconditions:** the attacker can get a datagram to the client's ephemeral port, i.e. same LAN, on-path, a public IP or unfirewalled IPv6, or a full-cone NAT. Port-restricted and symmetric NATs block off-path attackers. No IP spoofing is needed.
- **Fix:** in Peer, drop packets from unknown handles when the Peer was started with `Connect`; in `UdpSocket`, call `Socket.Connect`; in `NetworkClient`, check `conn` in the event handlers and in `DataHandler`.

#### U3. Connection identity is the source address only — [Security] (6/10) ✅
- **Where:** routing is `_connectionLookup.TryGetValue(handle)` on IP:port (`Peer.cs:458`). No header carries a session id, nonce or MAC.
- **What an attacker who can spoof a victim's IP:port can do:**
  - **(a)** Kick the victim with a 2–3 byte `Disconnect` command, or with any malformed packet (an Ack with `ackSequence ≥ capacity`, or a bad batch length).
  - **(b)** Inject Unreliable messages as an **authenticated** player (`MessageHandler` only checks `player.IsAuthenticated`).
  - **(c)** Hijack the reliable stream by pre-filling future order slots, so the genuine packets are dropped as duplicates (`AckSystem.cs:536-540`).
  - **(d)** Send 64 forged Acks with mask `~0`, which cover the whole 12-bit space, so lost reliable packets are treated as delivered and the victim's stream stalls permanently.
  - The same works in the server→client direction.
- **Preconditions:** knowledge of the victim's IP:port, plus either a network that allows source spoofing (no BCP38) or an on-path position. U2 removes the spoofing requirement for managed-socket clients.
- **Fix:** have the server issue a random session token in `ConnectionAccepted` and require it in every packet header (or use a per-connection MAC).

#### U4. A negative receive length aborts the receive loop — [Security] (5/10) ✅
- **Where:**
  - `Peer.cs:316-320`. On `length < 0` the loop **`return`s**, abandoning every queued packet for the frame.
  - The guard only runs when Warning logging is enabled. Otherwise `AsSpan(0, -1)` throws, is caught, and `DisconnectFromReceiveError` disconnects whatever stale address the shared receive handle still holds.
- **Trigger:**
  - `NanoSocket.Receive` returns `recvfrom`'s result unchanged (`NanoSocket.cs:84-86`).
  - On Windows, `recvfrom` returns -1 for a datagram larger than the buffer (WSAEMSGSIZE) and for ICMP port-unreachable (WSAECONNRESET). The native library (checked by disassembly) doesn't set `SIO_UDP_CONNRESET`; the managed `UdpSocket` does.
  - So an **unauthenticated** sender sending a few oversized datagrams per frame caps how many legitimate packets are processed each frame. The OS buffer then backs up and every player lags or times out.
  - Linux truncates silently and is not affected.
  - The equivalent guard in `OnDataEvent` (:392) is dead code, because a span length can never be negative.
- **Fix:** `continue` on negative lengths, don't gate the check on logging, never use the handle from a failed receive, and set `SIO_UDP_CONNRESET` in NanoSocket on Windows.

#### U5. Unauthenticated Warning-log flood — [Security] (4/10) ✅
- **Where:** `Peer.cs:646-659`. Any source, spoofed or not, sending `[Command, ConnectRequest]` (2 bytes) gets an "Invalid key" Warning and a 3-byte reject reply, at line rate. Once the server is full, a valid request gets "At max connections" instead. The capacity check runs **before** key validation.
- On Steam each reject also closes the transport connection, so the attacker pays one handshake per log line (≈2/10 there).
- **Fix:** log at Log level, or rate-limit/aggregate these warnings; validate the key before the capacity check.

### Docs/Comments
- **D1** — [Docs] (2/10) `Config.cs:97`: "can be set lower than SequenceSize's value" implies any value below `2^bits` is safe. The real limit is a span below `2^(bits-1)` (see #4).
- **D2** — [Docs] (2/10) `ConnectKeyValidator` summary: state explicitly that the key is **not** authentication (it is public by default and visible to anyone with the client).
- **D3** — [Docs] (1/10) `ISocket.cs:27,33,73-74,82,84`: the `<param>` names (`endPoint`, `buffer`, `length`) don't match the current signatures (`outBuffer`, `handle`, `packet`).
- **D4** — [Docs] (1/10) `DisconnectReason.cs:19`: `RequestedByRemotePeer` is documented as "Disconnect called by higher level", copy-pasted from `RequestedByLocalPeer`.
- **D5** — [Docs] (1/10) `Peer.cs:620`: the error says "should not be in Created state" for every state.
- **D6** — [Docs] (1/10) `AckSystem.cs:503`: `<returns>` on the `void ReceiveReliable`. `:585` says "63 packets" where it should be 64.
- **D7** — [Docs] (1/10) `RateLimitBucket.cs:16`: the message says "Interval can't be negative", but zero is rejected too.
- **D8** — [Docs] (1/10) `ReliableConnection.cs:226-228`: the comment says only the first fragment's index matters and that tampering "doesn't break anything extra". #2 shows that the *lengths* of non-final fragments do matter.

### Nitpick/Style
- **N1** — [Nitpick] (1/10) Typos: `_isDisonnected` (`DisconnectedTracker.cs:7`), `TryPeak` (`RingBuffer.cs:87`), "this token as already been notified" (`NotifyToken.cs:26,34`), "NoReliableConnetion", "assmebly".
- **N2** — [Nitpick] (1/10) The `Peer` ctor's minimum `maxPacketSize` (18) is below the 34 bytes a `ConnectRequest` needs, so `CopyTo` would throw for a `MaxPacketSize` between 18 and 33.
- **N3** — [Nitpick] (1/10) The trackers take the concrete `Time` instead of `ITime`, which makes them hard to unit-test.
- **N4** — [Nitpick] (1/10) `ReliablePacket.Equals` compares `Sequences` by reference, so it is effectively reference equality hidden behind an `Order` check. Use `ReferenceEquals` or remove the override.
- **N5** — [Nitpick] (1/10) `HandleFragmentedMessage` doesn't check `State == Connected`, unlike `HandleReliableBatched`. The high-level `DataHandler` guards it, but it is inconsistent.
- **N6** — [Nitpick] (0/10) `Connection.HandleReliableBatched(ReadOnlySpan)` and `ReceiveNotifyPacket` copy every unreliable/notify packet into a pooled buffer only to build an `ArraySegment`. This is an API constraint, not a bug.

---

### Verdict
The code under review is **not** clean: nothing has been fixed yet, so this is not a "good to merge" state.

For Steam deployments, fix these first:
- #1 (leaked slots on handler exceptions)
- #2 (cross-player stale-buffer leak)
- #3 / #4 (receive-buffer memory and the ack-window wedge)

Most of these are small, local fixes. If the built-in UDP socket ships to players, U1–U4 also need attention. U2 and U4 are cheap to fix, and U2 is exposed on every macOS, iOS and Android client.
