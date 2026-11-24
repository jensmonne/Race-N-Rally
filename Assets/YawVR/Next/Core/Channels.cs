// Channel state machines (docs/PROTOCOL.md §5; Rust: crates/yawvr-net/src/channel.rs).
// Pure: no sockets, no clock reads; time comes in as "nowMs".
using System.Collections.Generic;

namespace YawVR.Next
{
    public enum Delivery
    {
        /// <summary>The expected packet: ack and deliver.</summary>
        New,
        /// <summary>Already delivered: ack again (our earlier ack may have been lost), do not deliver.</summary>
        Duplicate,
        /// <summary>Ahead of the expected seq: drop and do NOT ack (acking would hide a lost message).</summary>
        Future,
    }

    public sealed class ReliableReceiver
    {
        public uint Expected { get; private set; }
        public ReliableReceiver(uint first = 0) { Expected = first; }

        public Delivery OnData(uint seq)
        {
            if (seq == Expected) { Expected = unchecked(Expected + 1); return Delivery.New; }
            return seq < Expected ? Delivery.Duplicate : Delivery.Future;
        }
    }

    /// <summary>Unreliable channel: accept only strictly increasing seq (latest wins, replays dropped).</summary>
    public sealed class UnreliableReceiver
    {
        bool _any;
        uint _last;

        public bool Accept(uint seq)
        {
            if (_any && seq <= _last) return false;
            _any = true;
            _last = seq;
            return true;
        }
    }

    public enum SendKind { Idle, Send, Failed }

    public struct SendAction
    {
        public SendKind Kind;
        public uint Seq;
        public byte[] Payload;
    }

    /// <summary>
    /// Stop-and-wait sender: one message in flight; resend every 200 ms; after 5 retries the peer
    /// is considered gone (the session then reconnects). Urgent messages (e-stop) go to the FRONT
    /// of the queue. They still wait for a message that is already in flight, because stop-and-wait
    /// allows only one; that is why the e-stop also uses the separate ESTOP packet (see YawSession).
    /// </summary>
    public sealed class ReliableSender
    {
        public const long RetryMs = 200;
        public const int MaxRetries = 5;

        readonly LinkedList<byte[]> _queue = new LinkedList<byte[]>();
        readonly int _maxQueue;
        uint _nextSeq;
        bool _inFlight;
        uint _flightSeq;
        byte[] _flightPayload;
        long _sentAt;
        int _attempts;

        public ReliableSender(uint firstSeq = 0, int maxQueue = 32) { _nextSeq = firstSeq; _maxQueue = maxQueue; }

        public bool IsIdle => !_inFlight && _queue.Count == 0;
        public bool InFlight => _inFlight;
        public uint InFlightSeq => _flightSeq;

        /// <summary>Queues a payload. Urgent ones skip the queue (but not the message in flight).</summary>
        public bool Enqueue(byte[] payload, bool urgent = false)
        {
            if (_queue.Count >= _maxQueue && !urgent) return false;
            if (urgent) _queue.AddFirst(payload); else _queue.AddLast(payload);
            return true;
        }

        public bool OnAck(uint seq)
        {
            if (_inFlight && _flightSeq == seq) { _inFlight = false; _flightPayload = null; return true; }
            return false;
        }

        public SendAction Poll(long nowMs)
        {
            if (_inFlight)
            {
                if (nowMs - _sentAt < RetryMs) return new SendAction { Kind = SendKind.Idle };
                if (_attempts > MaxRetries)
                {
                    uint seq = _flightSeq;
                    _inFlight = false;
                    _queue.Clear();
                    return new SendAction { Kind = SendKind.Failed, Seq = seq };
                }
                _attempts++;
                _sentAt = nowMs;
                return new SendAction { Kind = SendKind.Send, Seq = _flightSeq, Payload = _flightPayload };
            }
            if (_queue.Count == 0) return new SendAction { Kind = SendKind.Idle };
            _flightPayload = _queue.First.Value;
            _queue.RemoveFirst();
            _flightSeq = _nextSeq;
            _nextSeq = unchecked(_nextSeq + 1);
            _inFlight = true;
            _sentAt = nowMs;
            _attempts = 1;
            return new SendAction { Kind = SendKind.Send, Seq = _flightSeq, Payload = _flightPayload };
        }
    }
}
