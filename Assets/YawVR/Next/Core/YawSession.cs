// The session: what the messages mean (docs/PROTOCOL.md §3, §6, §8).
//
//  - Handshake: Hello (padded >= 256 bytes) -> HelloAck -> derive session keys. A refusal
//    (BUSY, VERSION_MISMATCH) ends the attempt.
//  - Liveness: we send a Heartbeat whenever we have sent nothing for HelloAck.heartbeat_interval_ms
//    (250 ms). The server sends Telemetry at 20 Hz; if we hear nothing for ServerSilenceTimeoutMs
//    (3 s) the transport is torn down and a FRESH one is created after a backoff.
//  - Motion gate (safety): poses are only sent when (1) the session is connected, (2) the game
//    called Start() in THIS session and (3) the rig reports ACTIVE. Leaving ACTIVE, a disconnect
//    or a reconnect closes the gate; the game has to call Start() again. A reconnect therefore
//    never moves the rig by itself, and there is no pose queue that could be replayed.
//  - E-stop: see EmergencyStop().
//
// Threads: Pump() on the network thread (YawNetworkRunner). Public methods may be called from
// any thread (Unity's main thread in practice); everything is guarded by _sync. Events go to
// IEventSink (MainThreadQueue) and are raised on the main thread by the Unity layer.
// Lock order is always: _sync, then the transport's lock. EmergencyStop's first step takes no lock.
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using YawVR.Proto;

namespace YawVR.Next
{
    public sealed class YawSessionConfig
    {
        public const uint ProtocolVersion = 2;
        /// <summary>Default session port of yawvr-server (network.port).</summary>
        public const int DefaultPort = 50050;

        public IPEndPoint Server;
        /// <summary>The 32-byte pre-shared key (see OPEN_QUESTIONS.md: how it gets onto the headset).</summary>
        public byte[] Psk;
        public string ClientName = "Unity";
        public int HandshakeTimeoutMs = 3000;
        /// <summary>No datagram from the server for this long = the link is dead.</summary>
        public int ServerSilenceTimeoutMs = 3000;
        public bool AutoReconnect = true;
        public int BackoffInitialMs = 500;
        public int BackoffMaxMs = 10000;
        /// <summary>Used until the HelloAck tells us the real value.</summary>
        public int DefaultHeartbeatIntervalMs = 250;
        /// <summary>ESTOP packets are repeated this many times, EstopRepeatIntervalMs apart.</summary>
        public int EstopRepeats = 10;
        public int EstopRepeatIntervalMs = 50;
        /// <summary>No confirmation within this time -> EmergencyStopUnconfirmed (loud).</summary>
        public int EstopConfirmTimeoutMs = 1000;
        /// <summary>Monotonic milliseconds. Tests inject a fake clock.</summary>
        public Func<long> Clock;
        /// <summary>16 random bytes per connection attempt. Tests inject a fixed nonce.</summary>
        public Func<byte[]> NonceSource;
        /// <summary>Creates the socket for one attempt (a fresh one every time).</summary>
        public Func<IPEndPoint, IDatagramSocket> SocketFactory;
    }

    public enum PoseResult
    {
        Sent,
        SentClamped,
        /// <summary>Not connected, not started in this session, or the rig is not ACTIVE: nothing sent.</summary>
        NotStreaming,
        /// <summary>NaN or infinity: nothing sent.</summary>
        RejectedNonFinite,
    }

    public sealed class YawSession : ITransportHandler, IDisposable
    {
        readonly object _sync = new object();
        readonly YawSessionConfig _cfg;
        readonly IEventSink _events;
        readonly RateLimitedLog _log;
        readonly YawStateModel _model = new YawStateModel();

        ReliableUdpTransport _transport;   // read without the lock only by EmergencyStop (volatile access)
        SessionPhase _phase = SessionPhase.Idle;
        long _attemptStartMs;
        long _nextAttemptMs;
        int _attempt;
        byte[] _clientNonce;
        bool _wasConnected;
        int _heartbeatMs;

        // Motion gate.
        bool _startRequested;
        // A Start of the current start request was accepted by the server (see the CommandRejected case).
        bool _startAccepted;
        bool _streamingAllowed;

        // Commands awaiting CommandAccepted/CommandRejected.
        uint _nextCommandId;
        readonly Dictionary<uint, string> _pending = new Dictionary<uint, string>();

        // E-stop tracking.
        int _estopRepeatsLeft;
        long _nextEstopMs;
        uint _estopCommandId;
        long _estopDeadlineMs;
        bool _estopAwaitingConfirm;

        // Reused objects for the pose hot path (no allocation per call; see SendPose).
        readonly ClientMessage _poseMsg = new ClientMessage();
        readonly Pose _pose = new Pose();
        readonly Vibration _vibration = new Vibration();
        readonly byte[] _poseBuf = new byte[256];
        readonly byte[] _heartbeatBytes = new ClientMessage { Heartbeat = new Heartbeat() }.ToByteArray();

        public YawSession(YawSessionConfig cfg, IEventSink events, ILog log)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _log = new RateLimitedLog(log ?? new NullLog());
            if (_cfg.Clock == null) throw new ArgumentException("config.Clock is required");
            if (_cfg.SocketFactory == null) _cfg.SocketFactory = ep => new UdpDatagramSocket(ep);
            if (_cfg.NonceSource == null) _cfg.NonceSource = RandomNonce;
            _poseMsg.Pose = _pose; // the oneof keeps pointing at the same Pose object
        }

        static byte[] RandomNonce()
        {
            var n = new byte[KeyDerivation.NonceLen];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(n);
            return n;
        }

        public YawStateModel Model => _model;
        public SessionPhase Phase { get { lock (_sync) return _phase; } }
        /// <summary>True when poses would currently be sent.</summary>
        public bool IsStreaming { get { lock (_sync) return _streamingAllowed; } }

        // ------------------------------------------------------------------ connect / disconnect

        /// <summary>Starts connecting (and keeps reconnecting if AutoReconnect) to <paramref name="server"/>.</summary>
        public void Connect(IPEndPoint server, byte[] psk)
        {
            lock (_sync)
            {
                CloseTransportLocked();
                _cfg.Server = server ?? throw new ArgumentNullException(nameof(server));
                _cfg.Psk = psk ?? throw new ArgumentNullException(nameof(psk));
                _attempt = 0;
                _wasConnected = false;
                StartAttemptLocked(_cfg.Clock());
            }
        }

        /// <summary>
        /// Ends the session and stops reconnecting. If a session is up, a Disconnect message is sent
        /// first (best effort). The server applies its link-loss policy (default: park) if the rig was active.
        /// </summary>
        public void Disconnect(string reason = "disconnect requested")
        {
            lock (_sync)
            {
                long now = _cfg.Clock();
                if (_phase == SessionPhase.Connected && _transport != null)
                {
                    _transport.SendReliable(new ClientMessage { Disconnect = new YawVR.Proto.Disconnect() }.ToByteArray(), now);
                    _transport.Pump(now);
                }
                bool wasConnected = _phase == SessionPhase.Connected;
                CloseTransportLocked();
                SetPhaseLocked(SessionPhase.Idle);
                if (wasConnected) Post(new YawEvent { Kind = YawEventKind.Disconnected, Message = reason, Permanent = true });
            }
        }

        void StartAttemptLocked(long now)
        {
            _attempt++;
            _attemptStartMs = now;
            _clientNonce = _cfg.NonceSource();
            _startRequested = false;
            _startAccepted = false;
            _streamingAllowed = false;
            _pending.Clear();
            _model.ResetForNewSession();
            _heartbeatMs = _cfg.DefaultHeartbeatIntervalMs;
            IDatagramSocket socket;
            try
            {
                socket = _cfg.SocketFactory(_cfg.Server);
            }
            catch (Exception e)
            {
                FailLocked(now, "cannot open socket: " + e.Message, retry: true);
                return;
            }
            // A fresh transport = fresh sequence numbers starting at 0 (the server requires that).
            Volatile.Write(ref _transport, new ReliableUdpTransport(socket, _cfg.Psk, this, now));
            SetPhaseLocked(SessionPhase.Connecting);
            _transport.SendReliable(BuildHello(_cfg.ClientName, _clientNonce).ToByteArray(), now);
            _log.Write(now, LogLevel.Info, "connect", $"[YawSession] connecting to {_cfg.Server} (attempt {_attempt})");
        }

        /// <summary>
        /// ClientMessage{hello}, padded so that the sealed datagram is at least 256 bytes (the server
        /// ignores shorter Hellos: anti-amplification, PROTOCOL.md §3). Same algorithm as the Rust client.
        /// </summary>
        public static ClientMessage BuildHello(string clientName, byte[] nonce)
        {
            const int minHelloBytes = 256;
            const int overhead = WireFormat.HeaderLen + WireFormat.TagLen;
            string name = clientName ?? "";
            if (name.Length > 64) name = name.Substring(0, 64);
            var hello = new Hello { ProtocolVersion = YawSessionConfig.ProtocolVersion, ClientName = name, ClientNonce = ByteString.CopyFrom(nonce) };
            var msg = new ClientMessage { Hello = hello };
            while (true)
            {
                int total = msg.CalculateSize() + overhead;
                if (total >= minHelloBytes) return msg;
                int missing = Math.Max(minHelloBytes - total, 1);
                hello.Padding = ByteString.CopyFrom(new byte[hello.Padding.Length + missing]);
            }
        }

        void CloseTransportLocked()
        {
            var t = _transport;
            Volatile.Write(ref _transport, null);
            t?.Dispose();
            _startRequested = false;
            _startAccepted = false;
            _streamingAllowed = false;
            _estopRepeatsLeft = 0;
        }

        void FailLocked(long now, string reason, bool retry, bool permanent = false)
        {
            bool wasConnected = _phase == SessionPhase.Connected;
            CloseTransportLocked();
            bool willRetry = retry && !permanent && _cfg.AutoReconnect;
            if (willRetry)
            {
                // Exponential backoff with a cap: 0.5 s, 1 s, 2 s, ... 10 s. No tight loops.
                int shift = Math.Min(_attempt - 1, 16);
                long delay = Math.Min((long)_cfg.BackoffInitialMs << shift, _cfg.BackoffMaxMs);
                _nextAttemptMs = now + delay;
                SetPhaseLocked(SessionPhase.Backoff);
            }
            else
            {
                SetPhaseLocked(permanent ? SessionPhase.Failed : SessionPhase.Idle);
            }
            var kind = wasConnected ? YawEventKind.Disconnected : YawEventKind.ConnectFailed;
            if (wasConnected) _log.Always(LogLevel.Safety, $"[YawSession] DISCONNECTED: {reason}. Pose streaming stopped.");
            else _log.Write(now, LogLevel.Warning, "connectfail", $"[YawSession] connection attempt failed: {reason}");
            Post(new YawEvent { Kind = kind, Message = reason, Permanent = !willRetry });
        }

        void SetPhaseLocked(SessionPhase p)
        {
            _phase = p;
            _model.SetPhase(p);
        }

        // ------------------------------------------------------------------ the pump

        /// <summary>Network thread: waits up to <paramref name="timeoutMs"/> for incoming data.</summary>
        public void WaitForTraffic(int timeoutMs)
        {
            var t = Volatile.Read(ref _transport);
            if (t != null) t.WaitReadable(timeoutMs);
            else Thread.Sleep(timeoutMs);
        }

        /// <summary>Network thread (or a test): processes input, timers, heartbeats, retries.</summary>
        public void Pump()
        {
            lock (_sync)
            {
                long now = _cfg.Clock();
                switch (_phase)
                {
                    case SessionPhase.Connecting:
                        _transport?.Pump(now);
                        if (_phase == SessionPhase.Connecting && now - _attemptStartMs > _cfg.HandshakeTimeoutMs)
                            FailLocked(now, "no HelloAck (wrong address, server down, or wrong key)", retry: true);
                        break;

                    case SessionPhase.Connected:
                        _transport?.Pump(now);
                        if (_phase != SessionPhase.Connected || _transport == null) break;
                        if (now - _transport.LastReceiveMs > _cfg.ServerSilenceTimeoutMs)
                        {
                            FailLocked(now, $"server silent for more than {_cfg.ServerSilenceTimeoutMs} ms", retry: true);
                            break;
                        }
                        if (now - _transport.LastSendMs >= _heartbeatMs)
                            _transport.SendUnreliable(_heartbeatBytes, now);
                        PumpEstopLocked(now);
                        break;

                    case SessionPhase.Backoff:
                        if (now >= _nextAttemptMs) StartAttemptLocked(now);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ transport callbacks

        void ITransportHandler.OnReliable(ReadOnlySpan<byte> payload, bool handshake)
        {
            long now = _cfg.Clock();
            ServerMessage msg;
            try { msg = ServerMessage.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { _log.Write(now, LogLevel.Warning, "badmsg", "[YawSession] undecodable reliable message dropped"); return; }

            if (handshake)
            {
                if (msg.BodyCase == ServerMessage.BodyOneofCase.HelloAck) AcceptHelloAckLocked(msg.HelloAck, now);
                else if (msg.BodyCase == ServerMessage.BodyOneofCase.CommandRejected) RefusedLocked(msg.CommandRejected, now);
                return;
            }

            switch (msg.BodyCase)
            {
                case ServerMessage.BodyOneofCase.CommandAccepted:
                    var name = TakePending(msg.CommandId);
                    if (name == "Start" && _startRequested) _startAccepted = true;
                    if (msg.CommandId != 0 && msg.CommandId == _estopCommandId) ConfirmEstopLocked("server accepted EmergencyStop");
                    Post(new YawEvent { Kind = YawEventKind.CommandAccepted, CommandId = msg.CommandId, CommandName = name });
                    break;
                case ServerMessage.BodyOneofCase.CommandRejected:
                    var rejected = TakePending(msg.CommandId);
                    var r = msg.CommandRejected;
                    // A refused Start closes the gate, so a later ACTIVE (from anywhere) cannot start motion.
                    // Exception: an earlier Start of the same request was already accepted (two quick
                    // Starts; the second is refused "cannot start from active"). Replies arrive in command
                    // order, so that acceptance is always seen first. Closing the gate then would silently
                    // stop streaming in a running session (seen on the rig, bench stage 4, 2026-10-28).
                    if (rejected == "Start" && !_startAccepted)
                    {
                        _startRequested = false;
                        _streamingAllowed = false;
                    }
                    // Never swallowed: always an event, and logged as a safety event.
                    _log.Always(LogLevel.Safety, $"[YawSession] command {rejected ?? "?"} (#{msg.CommandId}) REJECTED: {r.Reason} {r.Detail}");
                    Post(new YawEvent { Kind = YawEventKind.CommandRejected, CommandId = msg.CommandId, CommandName = rejected, Reason = r.Reason, Message = r.Detail });
                    break;
                case ServerMessage.BodyOneofCase.StateUpdate:
                    var old = _model.ApplyStateUpdate(msg.StateUpdate);
                    OnServerStateLocked(old, msg.StateUpdate.State);
                    break;
                case ServerMessage.BodyOneofCase.Telemetry:
                    ApplyTelemetryLocked(msg.Telemetry);
                    break;
            }
        }

        void ITransportHandler.OnUnreliable(ReadOnlySpan<byte> payload)
        {
            ServerMessage msg;
            try { msg = ServerMessage.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { return; }
            if (msg.BodyCase == ServerMessage.BodyOneofCase.Telemetry) ApplyTelemetryLocked(msg.Telemetry);
        }

        void ITransportHandler.OnTransportFailed()
        {
            FailLocked(_cfg.Clock(), "no ack from the server after all retries", retry: true);
        }

        void AcceptHelloAckLocked(HelloAck ack, long now)
        {
            if (_phase != SessionPhase.Connecting) return;
            bool nonceOk = ack.ClientNonce.Length == KeyDerivation.NonceLen && ack.ClientNonce.Span.SequenceEqual(_clientNonce);
            if (ack.ProtocolVersion != YawSessionConfig.ProtocolVersion || !nonceOk || ack.ServerNonce.Length != KeyDerivation.NonceLen)
            {
                // Not an answer to OUR Hello (or a different protocol): ignore, the timeout handles it.
                _log.Write(now, LogLevel.Warning, "badack", "[YawSession] HelloAck ignored (version or nonce mismatch)");
                return;
            }
            var sn = ack.ServerNonce.ToByteArray();
            _transport.InstallSessionKeys(
                KeyDerivation.SessionKey(_cfg.Psk, Direction.ClientToServer, _clientNonce, sn, ack.SessionId),
                KeyDerivation.SessionKey(_cfg.Psk, Direction.ServerToClient, _clientNonce, sn, ack.SessionId));
            _model.ApplyHelloAck(ack);
            if (ack.HeartbeatIntervalMs > 0) _heartbeatMs = (int)Math.Min(ack.HeartbeatIntervalMs, 1000u);
            _attempt = 0;
            bool reconnect = _wasConnected;
            _wasConnected = true;
            SetPhaseLocked(SessionPhase.Connected);
            _log.Always(LogLevel.Info, $"[YawSession] connected to {ack.ServerName} (session {ack.SessionId}, rig {ack.State}){(reconnect ? " after a reconnect: motion stays off until Start()" : "")}");
            Post(new YawEvent { Kind = YawEventKind.Connected, State = ack.State, Message = reconnect ? "reconnected" : "connected" });
        }

        void RefusedLocked(CommandRejected r, long now)
        {
            if (_phase != SessionPhase.Connecting) return;
            bool permanent = r.Reason == Reason.VersionMismatch;
            FailLocked(now, $"server refused the session: {r.Reason} {r.Detail}", retry: true, permanent: permanent);
        }

        void ApplyTelemetryLocked(Telemetry t)
        {
            var old = _model.ApplyTelemetry(t);
            OnServerStateLocked(old, t.State);
            Post(new YawEvent { Kind = YawEventKind.TelemetryUpdated });
        }

        void OnServerStateLocked(State old, State now)
        {
            if (now == State.Active)
            {
                if (_startRequested) _streamingAllowed = true;
            }
            else
            {
                // Leaving ACTIVE closes the motion gate; the game must call Start() again.
                if (_streamingAllowed) _log.Always(LogLevel.Info, $"[YawSession] rig left ACTIVE ({now}): pose streaming stopped");
                _streamingAllowed = false;
                // A pending Start survives stale non-ACTIVE telemetry that was in flight when Start() was
                // called (IDLE -> ACTIVE is still to come). It is dropped when the rig actually leaves
                // ACTIVE, or goes to a state from which a pending Start must never resume motion.
                if (old == State.Active || now == State.EmergencyStopped || now == State.Fault || now == State.Calibrating)
                    _startRequested = false;
                    _startAccepted = false;
            }
            if (now == State.EmergencyStopped && _estopAwaitingConfirm) ConfirmEstopLocked("rig reports EMERGENCY_STOPPED");
            if (old != now) Post(new YawEvent { Kind = YawEventKind.StateChanged, State = now });
        }

        string TakePending(uint id)
        {
            if (id == 0) return null;
            if (_pending.TryGetValue(id, out var n)) { _pending.Remove(id); return n; }
            return null;
        }

        void Post(YawEvent e) => _events.Post(e);

        // ------------------------------------------------------------------ commands

        uint SendCommandLocked(string name, ClientMessage msg, bool urgent = false)
        {
            long now = _cfg.Clock();
            if (_phase != SessionPhase.Connected || _transport == null)
            {
                _log.Always(LogLevel.Warning, $"[YawSession] {name} not sent: not connected");
                Post(new YawEvent { Kind = YawEventKind.CommandRejected, CommandName = name, Message = "not connected (client side)" });
                return 0;
            }
            if (++_nextCommandId == 0) _nextCommandId = 1;
            uint id = _nextCommandId;
            msg.CommandId = id;
            if (_pending.Count > 64) _pending.Clear(); // bounded; replies to very old commands are dropped
            _pending[id] = name;
            if (!_transport.SendReliable(msg.ToByteArray(), now, urgent))
            {
                _pending.Remove(id);
                Post(new YawEvent { Kind = YawEventKind.CommandRejected, CommandName = name, CommandId = id, Message = "reliable queue full (client side)" });
                return 0;
            }
            return id;
        }

        /// <summary>Asks the rig to start. Opens the motion gate once the rig reports ACTIVE.</summary>
        public uint Start()
        {
            lock (_sync)
            {
                // Already streaming: a second Start would only be refused by the server ("cannot start
                // from active"). Not sending it keeps a redundant Start from ever touching the gate.
                if (_streamingAllowed)
                {
                    _log.Always(LogLevel.Info, "[YawSession] Start ignored: already started and streaming");
                    return 0;
                }
                uint id = SendCommandLocked("Start", new ClientMessage { Start = new YawVR.Proto.Start() });
                if (id != 0) { _startRequested = true; if (_model.Snapshot.ServerState == State.Active) _streamingAllowed = true; }
                return id;
            }
        }

        /// <summary>Stop: park=true parks (only from ACTIVE), park=false goes idle (zero power).</summary>
        public uint Stop(bool park)
        {
            lock (_sync)
            {
                _startRequested = false;
                _startAccepted = false;
                _streamingAllowed = false; // stop streaming at once, before the server's answer
                return SendCommandLocked(park ? "Park" : "Stop", new ClientMessage { Stop = new YawVR.Proto.Stop { Park = park } });
            }
        }

        public uint Park() => Stop(true);

        /// <summary>The server answers NOT_IMPLEMENTED (calibration drives into a mechanical end; see the server's DEV-019).</summary>
        public uint Calibrate(bool allAxes)
        {
            lock (_sync) return SendCommandLocked("Calibrate", new ClientMessage { Calibrate = new YawVR.Proto.Calibrate { AllAxes = allAxes } });
        }

        /// <summary>Clears the e-stop latch (accepted only if the physical button is released and no fault remains).</summary>
        public uint ClearEmergencyStop()
        {
            lock (_sync) return SendCommandLocked("ClearEmergencyStop", new ClientMessage { ClearEmergencyStop = new YawVR.Proto.ClearEmergencyStop() });
        }

        /// <summary>Narrow (never widen) the limits for this session. Only accepted while idle or parked.</summary>
        public uint SetSessionLimits(float? pitchBack, float? pitchForward, float? roll, float? maxPower)
        {
            var l = new YawVR.Proto.SetSessionLimits();
            if (pitchBack.HasValue) l.PitchBack = pitchBack.Value;
            if (pitchForward.HasValue) l.PitchForward = pitchForward.Value;
            if (roll.HasValue) l.Roll = roll.Value;
            if (maxPower.HasValue) l.MaxPower = maxPower.Value;
            lock (_sync) return SendCommandLocked("SetSessionLimits", new ClientMessage { SetSessionLimits = l });
        }

        /// <summary>One colour for the whole LED strip.</summary>
        public uint SetLedColor(byte r, byte g, byte b)
        {
            lock (_sync) return SendCommandLocked("SetLedColor", new ClientMessage { SetLedColor = new SetLedColor { R = r, G = g, B = b } });
        }

        // ------------------------------------------------------------------ emergency stop

        /// <summary>
        /// EMERGENCY STOP. Callable from any thread, any time, connected or not.
        ///  1. Right now, without taking any lock: three ESTOP packets (kind 3) on the current
        ///     session (authenticated). Without a session they go to the last known server with a
        ///     zero tag; the server accepts those only if estop.accept_unauthenticated is true.
        ///  2. Then: an EmergencyStop message at the FRONT of the reliable queue (for a confirmed
        ///     answer), and the ESTOP packet repeated every 50 ms for 0.5 s.
        ///  3. EmergencyStopConfirmed when the server accepts the command or reports EMERGENCY_STOPPED;
        ///     EmergencyStopUnconfirmed after 1 s otherwise. Then the physical e-stop is the only safe option.
        /// </summary>
        public void EmergencyStop()
        {
            var t = Volatile.Read(ref _transport);
            bool authenticated = t != null && t.HasSessionKeys;
            if (t != null) { t.SendEstopPacket(); t.SendEstopPacket(); t.SendEstopPacket(); }
            else SendUnauthenticatedEstop();

            lock (_sync)
            {
                long now = _cfg.Clock();
                _startRequested = false;
                _startAccepted = false;
                _streamingAllowed = false;
                _log.Always(LogLevel.Safety, authenticated
                    ? "[YawSession] EMERGENCY STOP sent"
                    : "[YawSession] EMERGENCY STOP sent WITHOUT a session (unauthenticated): it only works if the server accepts unauthenticated e-stops. USE THE PHYSICAL E-STOP.");
                Post(new YawEvent { Kind = YawEventKind.EmergencyStopSent, Message = authenticated ? "authenticated" : "unauthenticated" });
                _estopRepeatsLeft = _transport != null ? _cfg.EstopRepeats : 0;
                _nextEstopMs = now + _cfg.EstopRepeatIntervalMs;
                _estopAwaitingConfirm = true;
                _estopDeadlineMs = now + _cfg.EstopConfirmTimeoutMs;
                _estopCommandId = 0;
                if (_phase == SessionPhase.Connected && _transport != null)
                {
                    if (++_nextCommandId == 0) _nextCommandId = 1;
                    _estopCommandId = _nextCommandId;
                    _pending[_estopCommandId] = "EmergencyStop";
                    var msg = new ClientMessage { Emergencystop = new YawVR.Proto.EmergencyStop(), CommandId = _estopCommandId };
                    _transport.SendReliable(msg.ToByteArray(), now, urgent: true);
                }
                else if (_phase != SessionPhase.Connected)
                {
                    // Nothing can confirm it: report that immediately.
                    _estopAwaitingConfirm = false;
                    Post(new YawEvent { Kind = YawEventKind.EmergencyStopUnconfirmed, Message = "no session: cannot confirm the e-stop. Use the physical e-stop." });
                }
            }
        }

        void SendUnauthenticatedEstop()
        {
            var server = _cfg.Server;
            if (server == null) return;
            try
            {
                using (var s = _cfg.SocketFactory(server))
                {
                    var buf = new byte[WireFormat.MinPacket + WireFormat.EstopMagic.Length];
                    for (uint i = 0; i < 3; i++)
                    {
                        int len = WireFormat.Seal(WireFormat.KindEstop, i, WireFormat.EstopMagic, null, buf);
                        s.Send(buf, 0, len);
                    }
                }
            }
            catch (Exception) { /* nothing else we can do; the event below says so */ }
        }

        void PumpEstopLocked(long now)
        {
            while (_estopRepeatsLeft > 0 && now >= _nextEstopMs)
            {
                _transport.SendEstopPacket();
                _estopRepeatsLeft--;
                _nextEstopMs += _cfg.EstopRepeatIntervalMs;
            }
            if (_estopAwaitingConfirm && now > _estopDeadlineMs)
            {
                _estopAwaitingConfirm = false;
                _log.Always(LogLevel.Safety, "[YawSession] EMERGENCY STOP NOT CONFIRMED by the server. USE THE PHYSICAL E-STOP.");
                Post(new YawEvent { Kind = YawEventKind.EmergencyStopUnconfirmed, Message = "no confirmation from the server within the deadline" });
            }
        }

        void ConfirmEstopLocked(string how)
        {
            if (!_estopAwaitingConfirm) return;
            _estopAwaitingConfirm = false;
            _log.Always(LogLevel.Safety, "[YawSession] emergency stop confirmed: " + how);
            Post(new YawEvent { Kind = YawEventKind.EmergencyStopConfirmed, Message = how });
        }

        // ------------------------------------------------------------------ poses (hot path)

        /// <summary>
        /// Sends one pose (degrees) if the motion gate is open (see the top of this file).
        ///
        /// No allocation per call: the ClientMessage/Pose/Vibration objects are reused, the message is
        /// serialized into a reused byte[] (WriteTo(Span)), and the transport seals and sends from its
        /// own reused buffer with a reused HMAC instance. At 60-90 Hz on a headset this matters: GC
        /// pauses cause frame hitches, and frame hitches in VR cause nausea.
        /// </summary>
        public PoseResult SendPose(float yaw, float pitch, float roll, byte vibPitchAmp = 0, byte vibRollAmp = 0, byte vibHz = 0)
        {
            lock (_sync)
            {
                if (_phase != SessionPhase.Connected || !_streamingAllowed || _transport == null) return PoseResult.NotStreaming;
                long now = _cfg.Clock();
                var snapshot = _model.Snapshot;
                var check = PoseValidator.Prepare(ref yaw, ref pitch, ref roll, in snapshot);
                if (check == PoseCheck.Rejected)
                {
                    _log.Write(now, LogLevel.Warning, "nanpose", "[YawSession] pose with NaN/infinity NOT sent");
                    return PoseResult.RejectedNonFinite;
                }
                _pose.Yaw = yaw;
                _pose.Pitch = pitch;
                _pose.Roll = roll;
                if (vibHz > 0 && (vibPitchAmp > 0 || vibRollAmp > 0))
                {
                    _vibration.PitchAmplitude = vibPitchAmp;
                    _vibration.RollAmplitude = vibRollAmp;
                    _vibration.FrequencyHz = vibHz;
                    _pose.Vibration = _vibration;
                }
                else _pose.Vibration = null;
                int size = _poseMsg.CalculateSize();
                _poseMsg.WriteTo(new Span<byte>(_poseBuf, 0, size));
                _transport.SendUnreliable(new ReadOnlySpan<byte>(_poseBuf, 0, size), now);
                return check == PoseCheck.Clamped ? PoseResult.SentClamped : PoseResult.Sent;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                CloseTransportLocked();
                SetPhaseLocked(SessionPhase.Idle);
            }
        }
    }
}
