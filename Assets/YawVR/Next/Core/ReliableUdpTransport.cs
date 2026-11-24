// The wire layer of ONE session (docs/PROTOCOL.md §2-5).
// Rust counterparts: crates/yawvr-net/src/client.rs (rules), channel.rs (channels), wire.rs (layout).
//
// What this class knows: bytes, sequence numbers, acks, retries, which key seals which packet.
// What it does NOT know: what a Hello or a Pose means. That is YawSession's job.
//
// One instance = one session: sequence numbers start at 0. A reconnect creates a NEW socket
// and a NEW transport (the server rejects sessions that do not start at seq 0).
//
// Threads: Pump() runs on the network thread. SendReliable/SendUnreliable may be called from any
// thread (they take _lock). SendEstopPacket uses its own key instance and its own lock, so the
// e-stop never waits for anything else (see EmergencyStop in YawSession).
//
// Key selection (PROTOCOL.md §4):
//   our RELIABLE seq 0 (the Hello)           -> hello_c2s
//   everything else we send (incl. ACK of 0) -> session_c2s   (nothing is sent before the keys exist)
//   their ACK seq 0 / RELIABLE seq 0         -> hello_s2c     (HelloAck or a refusal)
//   everything else they send                -> session_s2c
using System;

namespace YawVR.Next
{
    /// <summary>Receives what the transport decoded. Called on the network thread, inside Pump().</summary>
    public interface ITransportHandler
    {
        /// <summary>
        /// A reliable message. <paramref name="handshake"/> is true for the server's seq 0 (sealed with the
        /// hello key: HelloAck or a refusal). The span is only valid during the call.
        /// </summary>
        void OnReliable(ReadOnlySpan<byte> payload, bool handshake);

        /// <summary>An unreliable message (newer than everything before it). Span valid during the call.</summary>
        void OnUnreliable(ReadOnlySpan<byte> payload);

        /// <summary>A reliable message was not acknowledged after all retries: the server is gone.</summary>
        void OnTransportFailed();
    }

    public sealed class ReliableUdpTransport : IDisposable
    {
        readonly object _lock = new object();
        readonly object _estopLock = new object();
        readonly IDatagramSocket _socket;
        readonly ITransportHandler _handler;

        readonly PacketKey _helloC2s;
        readonly PacketKey _helloS2c;
        PacketKey _sessionC2s;
        PacketKey _sessionS2c;
        PacketKey _estopKey;            // separate instance of session_c2s for the lock-free e-stop path

        readonly ReliableReceiver _rxReliable = new ReliableReceiver(0);
        readonly UnreliableReceiver _rxUnreliable = new UnreliableReceiver();
        readonly ReliableSender _txReliable = new ReliableSender(0, 32);
        uint _txUnreliableSeq;
        uint _estopSeq;

        // Reused buffers: nothing on the send/receive path allocates per packet.
        readonly byte[] _sendBuf = new byte[WireFormat.MaxPacket];
        readonly byte[] _ackBuf = new byte[WireFormat.MinPacket];
        readonly byte[] _estopBuf = new byte[WireFormat.MinPacket + 8];
        readonly byte[] _recvBuf = new byte[2048];

        bool _failed;
        bool _disposed;

        /// <summary>Monotonic ms of the last authentic datagram from the server (liveness).</summary>
        public long LastReceiveMs { get; private set; }
        /// <summary>Monotonic ms of the last datagram we sent (heartbeat scheduling).</summary>
        public long LastSendMs { get; private set; }
        public bool HasSessionKeys { get { lock (_lock) return _sessionC2s != null; } }
        /// <summary>Datagrams that failed parsing or authentication (diagnostics).</summary>
        public int RejectedDatagrams { get; private set; }

        public ReliableUdpTransport(IDatagramSocket socket, byte[] psk, ITransportHandler handler, long nowMs)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _helloC2s = new PacketKey(KeyDerivation.HelloKey(psk, Direction.ClientToServer));
            _helloS2c = new PacketKey(KeyDerivation.HelloKey(psk, Direction.ServerToClient));
            LastReceiveMs = nowMs;
            LastSendMs = nowMs;
        }

        /// <summary>Called by the session after it has derived the keys from the HelloAck.</summary>
        public void InstallSessionKeys(byte[] c2s, byte[] s2c)
        {
            lock (_lock)
            {
                _sessionC2s = new PacketKey(c2s);
                _sessionS2c = new PacketKey(s2c);
                lock (_estopLock) _estopKey = new PacketKey(c2s);
            }
        }

        /// <summary>
        /// Queues a reliable message (an encoded ClientMessage). The first one ever must be the Hello.
        /// <paramref name="urgent"/> puts it at the front of the queue (e-stop).
        /// </summary>
        public bool SendReliable(byte[] payload, long nowMs, bool urgent = false)
        {
            lock (_lock)
            {
                if (_disposed || _failed) return false;
                if (!_txReliable.Enqueue(payload, urgent)) return false;
                PollSenderLocked(nowMs);
                return true;
            }
        }

        public bool ReliableIdle { get { lock (_lock) return _txReliable.IsIdle; } }

        /// <summary>
        /// Sends an unreliable message. The payload is copied into a reused buffer, sealed and sent
        /// immediately: no allocation. Dropped (returns false) before the session keys exist.
        /// </summary>
        public bool SendUnreliable(ReadOnlySpan<byte> payload, long nowMs)
        {
            lock (_lock)
            {
                if (_disposed || _sessionC2s == null || payload.Length > WireFormat.MaxPacket - WireFormat.MinPacket) return false;
                int len = WireFormat.Seal(WireFormat.KindUnreliable, _txUnreliableSeq++, payload, _sessionC2s, _sendBuf);
                _socket.Send(_sendBuf, 0, len);
                LastSendMs = nowMs;
                return true;
            }
        }

        /// <summary>
        /// Sends one ESTOP packet (kind 3) right now, from the calling thread, without touching the
        /// main transport lock. Authenticated if the session keys exist, otherwise with a zero tag
        /// (accepted only if the server allows unauthenticated e-stops). Never answered by the server.
        /// </summary>
        public void SendEstopPacket()
        {
            lock (_estopLock)
            {
                if (_disposed) return;
                int len = WireFormat.Seal(WireFormat.KindEstop, _estopSeq++, WireFormat.EstopMagic, _estopKey, _estopBuf);
                _socket.Send(_estopBuf, 0, len);
            }
        }

        /// <summary>Blocks up to <paramref name="timeoutMs"/> for incoming data (network thread only).</summary>
        public void WaitReadable(int timeoutMs) => _socket.WaitReadable(timeoutMs);

        /// <summary>Processes everything that arrived, then (re)sends what is due. Network thread.</summary>
        public void Pump(long nowMs)
        {
            lock (_lock)
            {
                if (_disposed) return;
                for (int i = 0; i < 256; i++) // bounded: a flood cannot starve the rest of the loop
                {
                    int n = _socket.TryReceive(_recvBuf);
                    if (n < 0) break;
                    HandleDatagram(new ReadOnlySpan<byte>(_recvBuf, 0, n), nowMs);
                    // A handler callback may have ended the session (and disposed us) just now.
                    if (_disposed) return;
                }
                PollSenderLocked(nowMs);
            }
        }

        void HandleDatagram(ReadOnlySpan<byte> d, long nowMs)
        {
            if (!WireFormat.TryParse(d, out byte kind, out uint seq, out int payloadLen))
            {
                RejectedDatagrams++;
                return;
            }
            var payload = d.Slice(WireFormat.HeaderLen, payloadLen);

            // Server packets under the hello key: the ack of our Hello and its seq 0 (HelloAck/refusal).
            if (seq == 0 && (kind == WireFormat.KindAck || kind == WireFormat.KindReliable) && WireFormat.Authentic(d, _helloS2c))
            {
                LastReceiveMs = nowMs;
                if (kind == WireFormat.KindAck)
                {
                    _txReliable.OnAck(0);
                    return;
                }
                bool hadKeys = _sessionC2s != null;
                if (!hadKeys) _handler.OnReliable(payload, handshake: true);
                // The handler installs the session keys when it accepts the HelloAck. Our Hello was
                // obviously received, and the server's seq 0 is consumed: ack it under the SESSION key.
                // That ack proves to the server that we know the PSK and makes the session active.
                // (A retransmitted HelloAck, after our ack was lost, is simply acked again.)
                if (_sessionC2s != null)
                {
                    if (!hadKeys)
                    {
                        _txReliable.OnAck(0);
                        _rxReliable.OnData(0);
                    }
                    SendAckLocked(0, nowMs);
                }
                return;
            }

            if (_sessionS2c == null || !WireFormat.Authentic(d, _sessionS2c))
            {
                RejectedDatagrams++;
                return;
            }
            LastReceiveMs = nowMs;
            switch (kind)
            {
                case WireFormat.KindAck:
                    _txReliable.OnAck(seq);
                    break;
                case WireFormat.KindReliable:
                    var delivery = _rxReliable.OnData(seq);
                    if (delivery != Delivery.Future) SendAckLocked(seq, nowMs); // always ack duplicates
                    if (delivery == Delivery.New) _handler.OnReliable(payload, handshake: false);
                    break;
                case WireFormat.KindUnreliable:
                    if (_rxUnreliable.Accept(seq)) _handler.OnUnreliable(payload);
                    break;
                // KindEstop from the server: meaningless, ignored.
            }
        }

        void SendAckLocked(uint seq, long nowMs)
        {
            int len = WireFormat.Seal(WireFormat.KindAck, seq, ReadOnlySpan<byte>.Empty, _sessionC2s, _ackBuf);
            _socket.Send(_ackBuf, 0, len);
            LastSendMs = nowMs;
        }

        void PollSenderLocked(long nowMs)
        {
            if (_failed) return;
            // Before the session keys exist only the Hello (seq 0) may go out.
            if (_sessionC2s == null && _txReliable.InFlight && _txReliable.InFlightSeq != 0) return;
            var a = _txReliable.Poll(nowMs);
            if (a.Kind == SendKind.Send)
            {
                var key = a.Seq == 0 ? _helloC2s : _sessionC2s;
                if (key == null) return;
                int len = WireFormat.Seal(WireFormat.KindReliable, a.Seq, a.Payload, key, _sendBuf);
                _socket.Send(_sendBuf, 0, len);
                LastSendMs = nowMs;
            }
            else if (a.Kind == SendKind.Failed)
            {
                _failed = true;
                _handler.OnTransportFailed();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _socket.Dispose();
                _helloC2s.Dispose();
                _helloS2c.Dispose();
                _sessionC2s?.Dispose();
                _sessionS2c?.Dispose();
                lock (_estopLock) _estopKey?.Dispose();
            }
        }
    }
}
