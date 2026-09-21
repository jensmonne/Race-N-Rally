# YAW 3 SDK — Transport Architecture Review

**Scope:** `Assets/YawVR/Scripts/` (14 C# files, ~2.3 kLOC). Reviewed at commit `eeb1b86` plus uncommitted changes to `ActionBus.cs` and `YawUDPClient.cs`.
**Date:** 2026-09-18

---

## 1. How the current TCP + UDP setup works

### 1.1 Components

| File | Role |
|---|---|
| `YawTCPClient.cs` | One `TcpClient`, async connect, single read loop, fire-and-forget writes |
| `YawUDPClient.cs` | One `UdpClient` bound to a fixed local port; does broadcast, unicast send, and receive |
| `YawController.cs` | State machine, protocol parsing for *both* transports, motion pump, heartbeat |
| `Commands.cs` | Wire encoders + command ID table |
| `ActionBus.cs` | Marshals every received packet to the Unity main thread via `ConcurrentQueue<Action>` |

Both transports are owned by `YawController`, which implements `IYawTCPClientDelegate` and `IYawUDPClientDelegate` (`YawController.cs:138`). There is no transport abstraction — call sites choose `tcpCLient.BeginSend(...)` or `udpClient.Send(...)` directly.

### 1.2 Traffic inventory (what actually goes over each stream)

**TCP — control plane, request/response, big-endian binary, command byte first** (`Commands.cs:15-20`)

| Msg | ID | Direction | Sent from |
|---|---|---|---|
| `CHECK_IN` + udp port + game name | `0x30` | → sim | `YawController.cs:311` |
| `CHECK_IN_ANS` ("AVAILABLE" / in-use) | `0x31` | ← sim | `:442` |
| `START` | `0xA1` | ↔ | `:345`, `:472` |
| `STOP` + park flag | `0xA2` | ↔ | `:358`, `:501` |
| `CALIBRATE` + allAxis | `0x55` | → sim | `:367` |
| `EXIT` | `0xA3` | ↔ | `:384`, `:528` |
| `GET_ALL_APP_PARAMS` | `0xF6` | → sim | `:775` (once per connect) |
| param dump *(answered under id `0x32`)* | `0x32` | ← sim | `:546` |
| `GET_STATE` → state string | `0xE5` | ↔ | `:778`, `:564` |
| `GET_TEMPS` → 3 bytes | `0xE4` | ↔ | `:779`, `:586` |

That's the entire TCP workload: **10 message types, all under ~70 bytes, no bulk transfer.** Polling of `GET_STATE` + `GET_TEMPS` runs at 1 Hz forever (`DeviceHeartbeat`, `:771-782`).

**UDP — data plane, ASCII strings** (`Commands.cs:12-13`)

| Msg | Direction | Rate | Code |
|---|---|---|---|
| `YAW_CALLING` broadcast | → broadcast | 1 Hz while disconnected | `:765`, `Commands.cs:55` |
| `YAWDEVICE;id;name;tcpPort;AVAILABLE` | ← sim | reply to discovery | `:407-416` |
| `Y[..]P[..]R[..]V[..]F[..]` motion | → sim | **every `FixedUpdate` (~50 Hz)** | `:237-247`, `:649-652` |
| `Y[..]P[..]R[..]U[..]` position + battery | ← sim | unspecified | `:391-405` |
| LED frame, 390 B binary, 16-bit counter | → sim | on demand | `Commands.cs:64-95` |

### 1.3 Lifecycle

```
UDP broadcast YAW_CALLING :50010  ──┐  (1 Hz, YawController.cs:761)
      ← UDP YAWDEVICE;…;tcpPort ────┘  → builds YawDevice(addr, tcp, discoveryPort)
TCP connect(ip, tcpPort)              (YawTCPClient.cs:38)
TCP → CHECK_IN(udpClientPort, gameName)   ← tells the sim where to send UDP
TCP ← CHECK_IN_ANS "AVAILABLE"
      udpClient.SetRemoteEndPoint(device.IP, device.UDPPort)   (YawController.cs:454)
      state = Connected  → FixedUpdate starts pumping UDP motion
TCP → START ; TCP ← START            → state = Started
TCP → GET_STATE / GET_TEMPS @1 Hz (forever)
TCP → STOP / EXIT
```

### 1.4 Where the split itself adds complexity

1. **UDP is bootstrapped by TCP, in both directions.** The sim learns the game's UDP port from the TCP `CHECK_IN` payload (`Commands.cs:111-118`); the game learns the sim's UDP port from the *discovery* reply, and then only by assuming `device.UDPPort == discoveryPort` (`YawController.cs:413`, hardcoded `50010` at `:765`, and hardcoded `50020/50010` in the debug path at `:233`). Three separate places must agree on the port model, and one of them is a guess.
2. **The data path has no liveness signal of its own; the control path has no data-path visibility.** TCP liveness (broken, §2.1) is the *only* disconnect detector. Motion can stop flowing while the SDK reports `Started` and the state machine is perfectly happy.
3. **Two failure domains, one state machine.** `ControllerState` (`Enums.cs:206`) is driven exclusively by TCP events. UDP has no representation in it at all.
4. **Two encodings for the same concepts.** Orientation is big-endian binary over TCP and fixed-width ASCII over UDP; `Commands.cs` and `Helpers.cs` each carry their own duplicate copy of `ByteArrayToFloat`/`IntToByteArray`.
5. **Two sockets, two firewall holes, two NAT/Wi-Fi-AP behaviours** to diagnose when a customer says "it doesn't connect."
6. **Ports were meant to be negotiable and aren't.** `RESET_PORTS`, `SET_SIMU_INPUT_PORT`, `SET_GAME_INPUT_PORT`, `SET_GAME_IP_ADDRESS`, `SET_OUTPUT_PORT`, `ERROR` are defined in `CommandIds` (`Commands.cs:35-42`) and **never referenced anywhere** — all call sites were grepped. Dead protocol surface that the firmware presumably still implements.

---

## 2. Concrete weaknesses found in the code

### 2.1 Disconnect detection is inverted — unexpected drops are never reported

`YawTCPClient.cs:94-104`:

```csharp
finally
{
    if (!connected)          // ← wrong sense
    {
        CloseConnection();
        ActionBus.Instance.Add(() => tcpDelegate?.DidLostServerConnection());
    }
}
```

`connected` is set `true` on connect (`:42`) and cleared only by `CloseConnection()` (`:124`).

- **Sim powers off / cable pulled / Wi-Fi drops** → `ReadAsync` returns 0 or throws → `connected` is still `true` → the branch is skipped → **`DidLostServerConnection()` never fires.** The SDK stays in `Started`, `FixedUpdate` keeps blasting UDP motion at a dead endpoint, and `DeviceHeartbeat` (`:776`) exits silently. No UI notification, no `onDisconnected`.
- **Deliberate disconnect** (`StopConnecting`/`CloseConnection`) → `connected` already `false` → the branch *does* run → fires a **spurious "lost connection"** through `DidLostServerConnection()` (`:600-609`), invoking `onDisconnected` and `DidDisconnectFrom` on a connection the app closed on purpose.

So the only watchdog in the system is exactly backwards. This is the single most serious defect in the file set, and it's a safety property on a machine that moves a person.

There is also no reconnect logic anywhere: `DidLostServerConnection` just sets `Initial` (`:608`). Recovery depends on `connectType == CONNECT_FIRST_FOUND_DEVICE` restarting discovery — but `DeviceDiscoveryCoroutine` (`:761-769`) only loops `while (state == ControllerState.Initial)` and is started once from `Start()`, so after it exits on the first connect it is never restarted.

### 2.2 No TCP message framing — the protocol assumes one read == one message

`DidRecieveTCPMessage` (`:435-595`) takes the raw chunk from `ReadAsync` and does `byte commandId = data[0]`, then reads fixed offsets. TCP is a byte stream; there is no length prefix, no delimiter, and no accumulation buffer (`YawTCPClient.cs:66-88` hands each `bytesRead` slice straight to the delegate).

Two concrete failures, both load-dependent:

- **Coalescing.** `DeviceHeartbeat` issues `GET_STATE` and `GET_TEMPS` as two separate writes back-to-back every second (`:778-779`). The sim's two replies will routinely land in one TCP segment. Then `data[0]` is `0xE5`, the `GET_STATE` case parses `Encoding.ASCII.GetString(data, 2, data.Length - 2)` over *both* replies — so `statestring` becomes `"simulation mode" + garbage`, matches none of the cases at `:567-581`, and silently falls through to `DeviceState.STOPPED`. **The rig reports "stopped" while running.** The temps reply is discarded entirely. Whether you see this depends on Nagle timing, which is why it will look like a rare intermittent bug.
- **Splitting.** A reply straddling two reads yields a fragment whose `data[0]` is a payload byte → interpreted as an arbitrary command ID.

`TcpClient.NoDelay` is never set (grepped for `NoDelay`, `KeepAlive`, `SendTimeout`, `SetSocketOption` — **zero hits in the whole folder**). So Nagle is on for both 1-byte heartbeat commands and for `STOP`/`EXIT`, and Nagle-plus-delayed-ACK can add ~40 ms to a safety command.

### 2.3 Unsynchronized concurrent writes to the same `NetworkStream`

`BeginSend` is `async void` with no queue or lock (`YawTCPClient.cs:107-120`). `DeviceHeartbeat` fires two of them without awaiting (`:778-779`), and a user command (`START`, `STOP`) can land in the same frame. Concurrent `WriteAsync` on one `NetworkStream` is not thread-safe and can interleave bytes — which, with no framing (§2.2), unrecoverably corrupts the stream. `async void` also means a write fault can't be observed by the caller; it only logs (`:118`).

### 2.4 Fixed-offset parsing with no length validation → crash vectors

- `case SET_POWER` (`:546-562`): guarded by `if (data.Length > 5)`, then reads `data[13]`, `data[17]`, `Helpers.ReadInt(data, 19, …)`, `data[24]`. A 6-byte packet passes the guard and throws `IndexOutOfRangeException`. The `else` branch reads `data[4]` on a packet that could be 1 byte long.
- `case GET_STATE` (`:565`): `GetString(data, 2, data.Length - 2)` throws on a 1-byte packet.
- `case GET_TEMPS` (`:587-589`): reads `data[1..3]` unguarded.

The exception surfaces inside an `ActionBus` action, gets swallowed by `ActionBus.cs:68`, and the packet is lost with only a console error. Given §2.2 makes lengths unpredictable, these aren't theoretical.

`Helpers.ReadInt` (`Helpers.cs:180-192`) byte-swaps **in place**, mutating the received buffer as a side effect of reading it.

### 2.5 Motion data has no sequence number — reordering causes physical jolts

`MOTION_DATA` (`Commands.cs:98-108`) is pure state with no seq, no timestamp. UDP reorders (and duplicates). A packet that arrives late overwrites the newer orientation the firmware already applied, so the platform commands *backwards* for one frame. At 50 Hz that's a visible/felt jolt, and it's indistinguishable from a physics bug in the game.

Telling detail: the LED command **does** carry a 16-bit counter (`Commands.cs:56, 68`) — someone already hit this and fixed it for LEDs only. The identical fix was never applied to the motion stream, which is the one that moves the user.

The inbound telemetry path has the same gap: `DidRecieveUDPMessage` (`:391-405`) applies whatever arrives to `device.ActualPosition` with no freshness check.

### 2.6 Cross-stream synchronization hazards

- **No ordering guarantee between the streams.** `SetRemoteEndPoint` is called on the TCP `CHECK_IN_ANS` (`:454`) and `state` flips to `Connected` on the same line, so `FixedUpdate` can send a motion packet before the firmware has finished its own check-in bookkeeping. Conversely `STOP`/`EXIT` go out on TCP (`:358`, `:384`) while UDP motion keeps flowing until the *TCP reply* arrives — a window of unbounded length where the sim has been told to stop but is still being fed setpoints.
- **`device` can be null under in-flight telemetry.** `SetState(Initial)` nulls `device` (`:685`), but UDP packets already queued in `ActionBus` then dereference `device.ActualPosition` (`:397`) → `NullReferenceException`. The uncommitted `catch (NullReferenceException)` added to `YawUDPClient.cs:111-114` suggests this class of shutdown race is already being fought symptomatically rather than structurally.
- **Response correlation is guesswork.** There are no transaction IDs. `case STOP` distinguishes "reply to my STOP" from "user pressed stop on the rig" by checking whether a callback happens to be non-null (`:514-524`); `case START` does the same (`:484-498`). One in-flight command per type is assumed, with a flat 10 s `ResponseTimeout` (`:697-702`). And `GET_ALL_APP_PARAMS` (`0xF6`) is *answered* under ID `0x32` (`SET_POWER`) — request and response share an ID namespace with no direction bit.

### 2.7 Head-of-line blocking where it matters least — and most

TCP's ordering is doing nothing useful here: all ten messages are independent, tiny, and none benefits from a stream. But it does impose HOL coupling between 1 Hz diagnostic polling and safety commands. If a `GET_TEMPS` reply is lost, the TCP RTO (≥200 ms, backing off exponentially) blocks the `STOP` reply behind it. On a lossy 2.4 GHz link a retransmit storm can stall the control channel for a second or more, with no application-visible signal — the SDK just sits in `Stopping` until the 10 s timeout.

So the current design pays the full cost of a reliable ordered stream, gets HOL blocking on its safety path, **and** still has a broken message layer on top of it (§2.2). That's the worst of both.

### 2.8 Per-packet allocation and main-thread marshalling

Everything on the hot path allocates, at 50 Hz outbound plus whatever the sim's telemetry rate is:

- `Commands.MOTION_DATA` (`Commands.cs:100-107`): 3× `ToString`, 3× `string.Format`, string concat, `GetBytes` → ~8 allocations per packet.
- `YawTCPClient.cs:79-80` allocates a fresh `byte[]` per read; `YawUDPClient.cs:97` allocates a string per datagram.
- Every single packet allocates a **closure + queue node** to hop through `ActionBus` (`YawUDPClient.cs:101`, `YawTCPClient.cs:82`).
- Inbound parsing uses `IndexOf`/`Substring`/`float.TryParse` per field (`:419-433`).

Order of ~600–800 short-lived allocations/sec on the networking path alone. In a VR title, that's avoidable GC pressure feeding exactly the frame-time spikes this SDK exists to avoid. `ActionBus.Update` (`:55-75`) also drains an unbounded queue in one frame, so a telemetry burst converts directly into a frame hitch.

### 2.9 Silent-failure modes

- If the UDP bind fails (port in use — e.g. two instances, or an editor + build), `InitializeUdpClient` logs and leaves `udpClient == null` (`YawUDPClient.cs:40-43`). `Send` then early-returns forever (`:143`) with **no error surfaced** — TCP connects fine, UI says connected, the rig never moves.
- The SDK binds and broadcasts on the same socket, so it receives its own broadcast; this is filtered by string match on `"YAW_CALLING"` (`YawUDPClient.cs:99`). Any sender on the LAN can inject `Y[..]P[..]R[..]` into `device.ActualPosition` — there's no session or origin check.

### 2.10 Protocol/spec mismatches and adjacent bugs

- `Commands.cs:97` documents the motion format as unsigned `0.00–359.99`, but `SendMotionData` passes values through `SignedForm` (`:637-639`, `:719-722`), sending `-180..180`. `FormatRotation`'s `"000.000"` mask yields `-012.345` (8 chars) where the doc implies a fixed 7. `UnsignedForm` (`:724-727`) is dead code. **Worth confirming against the firmware parser before changing anything else.**
- `Commands.cs:102`: `string.Format("F[{0},{0}]", smartPlug)` — `{0}` twice, so both smart-plug fields always carry the same value. Almost certainly meant to be two independent values.
- `MotionCompensation.cs:31`: `if (State != Started || State != Connected) return;` is a tautology — always true, so `LateUpdate` always returns and **camera motion compensation never runs at all**. Not a transport bug, but it means the inbound telemetry stream currently has exactly one consumer (`device.ActualPosition` → `UpdateIMUOffset` at `:443`/`:669-672`) and is otherwise unused.

---

## 3. Evaluating unified UDP + RUDP

### 3.1 First, an honest framing of what it does and doesn't buy

The latency argument for RUDP is weaker here than it looks, because **motion data is already on UDP**. Moving control commands off TCP will not reduce motion latency by one microsecond. Anyone selling this change on "lower latency for the motion loop" is wrong.

The real, code-grounded justifications are different, and stronger:

1. **TCP's features are unused.** Ten small independent messages, zero bulk transfer, and the ordered-stream guarantee actively discarded by the missing framing layer (§2.2). You are paying for a stream and consuming datagrams.
2. **One liveness model instead of zero.** Today the control path's watchdog is inverted (§2.1) and the data path has none (§1.4). A unified transport has exactly one keepalive/timeout to get right, and it naturally covers the path that actually moves the platform.
3. **Safety commands stop sharing a queue with diagnostics** (§2.7). Reliable-but-*unordered* delivery lets `STOP` retransmit independently of a stalled `GET_TEMPS`.
4. **The bootstrap cycle disappears.** No more "TCP tells the sim which UDP port to use, discovery guesses the sim's" (§1.4). One socket pair, one handshake, one firewall rule.
5. **You're already halfway there.** The LED counter (`Commands.cs:56`) is a sequence number; `ResponseTimeout` (`:697`) is a crude retransmit timer without the retransmit; discovery is already a UDP request/response. A real RUDP layer replaces three ad-hoc mechanisms with one.

### 3.2 What a practical RUDP layer needs — and what it can safely omit

Scope discipline is everything. **Do not build a TCP clone.** Given this traffic profile you need five things and can skip the rest.

**Common header** (16 bytes; trim to 8 for the realtime channels if you care — at 50 Hz × ~70 B it's ~3.5 kB/s, so probably not worth it):

```
off sz  field
0   1   version          (0x03)
1   1   channel          0=Control(rel) 1=Motion 2=Telemetry 3=LED 4=Discovery
2   1   flags            bit0 RELIABLE  bit1 ACK_ONLY  bit2 KEEPALIVE
3   1   msgType          reuse existing CommandIds
4   2   seq              per-channel uint16, wraps
6   2   ack              highest reliable seq seen from peer
8   4   ackBits          bitmask of the 32 reliable seqs before `ack`
12  4   sessionId        from handshake; reject anything else
16  ..  payload          (cap total packet at 1200 B — no fragmentation)
```

**1. Acks.** Only channel 0 is reliable. Ack + ackBits ride on **every** outgoing packet of any channel — which means the 50 Hz motion/telemetry streams acknowledge control messages within ≤20 ms **for free**, with zero extra packets. Send a standalone `ACK_ONLY` only if nothing else goes out within ~20 ms. The 32-bit redundant ackBits mean a single lost ack costs nothing.

**2. Retransmit.** Unacked control messages retransmit on a timer: `RTO = clamp(1.5·SRTT + 4·RTTVAR, 30 ms, 250 ms)`, backing off ×1.5 to a 500 ms ceiling. After ~1.5 s unacked, declare the link down and fail the command — vs. today's flat 10 s timeout with no retry at all. Control traffic is a handful of messages per session, so a plain `List` of unacked entries is sufficient; no windowing, no congestion control.

For `STOP`/`EXIT` specifically: **send a burst of 3 copies immediately**, then fall into normal retransmit. Three duplicate datagrams cost nothing and drop the probability of a 250 ms-delayed emergency stop by orders of magnitude.

**3. Dedup — mandatory, not optional.** Retransmits and network duplication mean `START` can arrive twice. Keep a 64-bit sliding bitmask of received reliable seqs per direction and drop anything already seen. Getting this wrong means the rig moves when it shouldn't; see §3.3 for the design that makes it much harder to get wrong.

**4. Ordering.** Channel 0 needs *sequenced* delivery — `START` then `STOP` must not swap. A small reorder buffer (8 slots, flush on gap-fill or 100 ms timeout) covers it. Unreliable channels need **no** buffer: accept a packet only if `(short)(seq - lastSeq) > 0`, else discard. That one line of wrapping-comparison logic fixes §2.5 on both ends.

**5. Session + watchdog.** The handshake (`CHECK_IN`, reliable) establishes a random 32-bit `sessionId`; both sides reject mismatched packets, which kills stale packets from a previous run, a second game instance, and the self-broadcast echo hack (`YawUDPClient.cs:99`) in one move. Then the bit that doesn't exist today:

| Condition | Firmware action | SDK action |
|---|---|---|
| no motion packet for 150 ms | hold last setpoint, flag `STALE` in telemetry | — |
| no motion packet for 500 ms | controlled ramp to neutral / park | — |
| no telemetry for 500 ms | — | fire `DidLostServerConnection`, begin reconnect |

Both sides guarantee ≥10 Hz traffic (the motion and telemetry streams already satisfy this; add a `KEEPALIVE` when idle). This is the property a motion platform actually needs and the current code does not have in either direction.

**Explicitly out of scope:** congestion control, flow control, fragmentation (cap payloads; the 390 B LED frame fits fine), selective-ack ranges, encryption. If you ever need OTA firmware upload, that's a bulk transfer — keep TCP for it rather than growing the RUDP layer.

### 3.3 Separating "must arrive" from "latest value wins"

The channel byte does the mechanical separation:

| Traffic | Channel | Semantics |
|---|---|---|
| Motion setpoints (50 Hz) | 1 | unreliable, newer-seq-wins, never retransmit |
| Telemetry: Y/P/R, battery, **state, temps** | 2 | unreliable, newer-seq-wins |
| LED frames | 3 | unreliable + idle refresh (below) |
| Handshake, mode/config, calibrate, param dump | 0 | reliable + sequenced + dedup |
| Discovery | 4 | unreliable broadcast, unchanged |

Two refinements matter more than the table:

**(a) Model config as versioned state, not as events.** This is the highest-leverage idea in the report. Instead of sending a `START` *event* and needing guaranteed exactly-once delivery, send a `desiredConfig { mode: IDLE|RUN|PARK, power, limits, calibrateEpoch }` **plus a monotonically increasing `configVersion`**. The firmware echoes `appliedConfigVersion` in every telemetry packet. The SDK re-sends the desired config at ~5 Hz until the echo matches.

The consequences are worth spelling out:

- Retransmission becomes **idempotent by construction** — a duplicate `desiredConfig` is a no-op, so a dedup bug can no longer make the rig move unexpectedly.
- The ack mechanism falls out of the telemetry stream you already have; no separate ack path for config.
- It is **self-healing**: any lost packet, and any state divergence after a reconnect, converges automatically within ~200 ms with no special-case recovery code.
- It deletes §2.6's whole correlation problem. "Did the user press stop on the rig, or is this a reply to my stop?" stops being a question, because you're comparing state, not matching replies. `CallBacks`/`CallbackTimeouts` (`:730-750`) and the 10 s `ResponseTimeout` largely disappear.

With this in place, the only genuinely must-arrive-exactly-once traffic left is the initial handshake and one-shot actions like `CALIBRATE` (handle it as a `calibrateEpoch` counter — same trick).

**(b) "Latest wins" is wrong for rarely-sent state.** LED colour is set on demand, not streamed; if that packet is lost, the colour is wrong indefinitely. Either put LED on the reliable channel, or have the SDK re-send the current LED state at 2–5 Hz when idle. The same reasoning applies to anything else that is "state, sent on change."

**(c) Fold the 1 Hz polling into telemetry.** Put `state` and `temps` in the channel-2 telemetry packet and delete `GET_STATE`/`GET_TEMPS` entirely. That removes two request/response round-trips per second, the string-matching state parser (`:564-584`), and both §2.2 coalescing victims. Do this one regardless of which transport you land on — it's a pure simplification.

### 3.4 Realistic risks and downsides

| Risk | Severity | Mitigation |
|---|---|---|
| **A bug in your reliability layer is now a safety bug.** TCP's correctness was free and battle-tested. | High | Non-negotiable: a fault-injection harness (configurable loss/dup/reorder/delay on both ends) and a loopback fake-firmware for CI. Budget this as a first-class deliverable, not an afterthought. |
| **No fallback if UDP is blocked.** Some corporate/hotel APs rate-limit or block UDP; a single transport has no plan B. | Medium | This is a LAN-attached device; if you're nervous, keep a TCP path for the handshake only. Note one socket = one firewall rule, which is a net *improvement* over two. |
| **Debuggability regresses.** Today `Y[012.345]P[...]` is human-readable in Wireshark and greppable in logs. Binary+custom is not. | Medium | Write a Wireshark Lua dissector (~100 lines) alongside the spec, and a `--protocol-trace` ring buffer in the SDK. Do it during, not after. |
| **You own the protocol forever** — versioning, backward compat with older firmware in the field. | Medium | Version byte in every header, and a firmware-version exchange in the handshake with explicit refusal on mismatch. Decide *now* whether old firmware must keep working. |
| **No congestion control.** A 50 Hz fixed-rate stream on a LAN is fine; a bug that unbounds the send rate is a self-DoS. | Low | Hard rate cap in the send path. |
| **Reordering/dup bugs are timing-dependent** and won't show up on your desk with a wired switch. | Medium | Same harness as above, plus a soak test over real 2.4 GHz Wi-Fi with interference. |
| **No authentication** — anyone on the LAN can drive the rig. | Medium | *Already true today* (§2.9). `sessionId` raises the bar against accidents, not attackers. If this matters, it's a separate pre-shared-key decision, not an RUDP feature. |

### 3.5 Implementation complexity

**SDK side (~800 LOC net new; `YawTCPClient.cs` deleted):**

| Work | LOC | Notes |
|---|---|---|
| `YawPacket.cs` — header codec, binary encode/decode, pooled buffers | ~180 | Replaces the ASCII formatting in `Commands.cs` and the duplicated byte helpers in `Helpers.cs` |
| `YawTransport.cs` — single socket, rx/tx loops, channel demux, ring buffers | ~220 | Replaces `YawUDPClient.cs` |
| `ReliableChannel.cs` — seq, ack/ackBits, retransmit, dedup, reorder buffer | ~180 | The only genuinely new logic |
| `YawSession.cs` — handshake, sessionId, watchdog, reconnect/backoff | ~160 | Reconnect doesn't exist today at all (§2.1) |
| Rewire `YawController.cs` | ~150 changed | Most of `CallBacks`/`CallbackTimeouts` and the `DidRecieveTCPMessage` switch are **deleted** if you adopt §3.3(a) |
| Fault-injection harness + fake firmware | ~250 | Test-only, but mandatory |

~3–5 days for the transport, ~3–5 days for integration and soak. Net effect on the SDK is roughly **LOC-neutral or slightly negative** — you delete the TCP client, the duplicate codecs, most of the callback/timeout scaffolding, and the request/response correlation heuristics.

**Firmware side (~600–900 LOC C, 1–2 weeks):** the header codec and ack/dedup/seq logic must be a **bit-exact mirror** of the SDK's, which is where the real risk lives. Plus: retire the TCP listener and its command dispatcher, push telemetry at a fixed rate with `state`/`temps`/`appliedConfigVersion` folded in, implement the 150 ms/500 ms watchdog with a *controlled* ramp to neutral (not an abrupt cut — this is the most safety-sensitive new code on either side), and apply config idempotently by version. Cost depends heavily on how entangled the existing TCP handler is with the motion control task and whether you're on lwIP/an RTOS.

**Shared, and easy to under-budget:** one wire-format spec document as the single source of truth, plus **golden test vectors** (byte-exact encoded packets committed to both repos) so the two implementations can be validated independently. Without these, a header mismatch costs you a week of oscilloscope-and-Wireshark archaeology.

**Realistic total: 3–4 weeks to production confidence**, dominated by fault injection and Wi-Fi soak testing rather than by writing the protocol.

**For comparison — fixing the current split in place:** length-prefix framing + accumulation buffer, `NoDelay`, fix the inverted `connected` check, add length validation to the four parse cases, serialize TCP writes through a queue, add a seq to motion + a firmware-side watchdog. ~150 LOC in the SDK, 1–2 days, plus a small firmware change (framing, motion seq, watchdog). **That captures most of the reliability and safety benefit for roughly a tenth of the effort** — and you need every one of those fixes on the way to the unified design anyway.

---

## 4. Recommendation

**Move to unified UDP with a minimal RUDP layer — but get there in three phases, and do Phase 0 this week regardless of what you decide about the rest.**

The code justifies the destination. TCP is carrying ten small independent messages with no bulk transfer, its ordering guarantee is already thrown away by the missing framing layer (§2.2), its liveness check is inverted so it doesn't detect the one failure it exists to detect (§2.1), and it's the bootstrap dependency that makes the UDP path's configuration fragile (§1.4). Meanwhile the stream that actually moves the user has no sequencing (§2.5), no watchdog, and no representation in the state machine. You're paying TCP's full cost — HOL blocking on the `STOP` path included — for benefits the implementation discards.

What the code does *not* justify is the usual reason people reach for RUDP. Motion is already UDP; unification buys architectural coherence and a working safety watchdog, not motion latency. If someone challenges the change on latency grounds, they're right and it doesn't matter — argue it on the watchdog and the single failure domain.

"Keep the split and just fix it" is not a good end state, for one specific reason: fixing TCP properly means hand-writing framing, write serialization, keepalive, and reconnect — which is most of the work of an RUDP layer — and you'd still have two failure domains, two liveness models, and the port-bootstrap cycle. The marginal cost of finishing the job is smaller than it looks, and §3.3(a) makes the resulting SDK *simpler* than today's.

### Phase 0 — do now, independent of the architecture decision (1–2 days)

These are live defects, not refactors. Every one is also a prerequisite for the migration.

1. `YawTCPClient.cs:96` — flip `if (!connected)` to `if (connected)`. Disconnect detection is currently backwards.
2. Add length-prefix framing + an accumulation buffer to the TCP read path, and validate lengths in all four parse cases (§2.4). Set `NoDelay = true`.
3. Serialize TCP writes through a single queue/`SemaphoreSlim` (§2.3).
4. Add a 16-bit sequence number to `MOTION_DATA` and a newer-wins check in the firmware — the LED path already has this (`Commands.cs:56`); copy it to the stream that moves the user.
5. Add the firmware-side motion watchdog (150 ms hold / 500 ms controlled park). **This is the safety gap**; nothing else on this list matters as much.
6. Null-guard `device` in `DidRecieveUDPMessage` (`:397`) and surface the UDP bind failure (`YawUDPClient.cs:40`) instead of silently no-opping forever.
7. Confirm signed-vs-unsigned motion encoding against the firmware parser (§2.10) before touching the format, and fix `F[{0},{0}]`.
8. Fix `MotionCompensation.cs:31` — the tautology means compensation has never run.

### Phase 1 — shrink TCP to the handshake (2–3 days, mostly firmware)

Fold `state`, `temps`, and battery into a fixed-rate UDP telemetry packet; delete `GET_STATE`/`GET_TEMPS` polling and their parse cases. This removes two round-trips per second, both §2.2 coalescing victims, and a chunk of the correlation heuristics. TCP drops to five messages. **Valuable on its own even if you stop here.**

### Phase 2 — unify (3–4 weeks, both sides)

Build the header, channels, and reliable control channel per §3.2; convert commands to versioned config state per §3.3(a); one socket, one handshake, one watchdog; retire the TCP listener. Keep TCP only if you have a bulk-transfer need such as OTA firmware update — nothing in the current code suggests you do.

Two things to treat as deliverables rather than nice-to-haves, because skipping them is how this kind of migration goes wrong: **the fault-injection harness** (loss/dup/reorder/delay, on both ends, in CI) and **the shared spec with golden byte-exact test vectors** committed to both repos.

While you're in the firmware: `RESET_PORTS`, `SET_SIMU_INPUT_PORT`, `SET_GAME_INPUT_PORT`, `SET_GAME_IP_ADDRESS`, `SET_OUTPUT_PORT`, and `ERROR` have no SDK caller at all (§1.4). Either wire them up deliberately or drop them from the new protocol rather than reimplementing dead surface.
