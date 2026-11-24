// The socket abstraction. The real implementation is a connected UDP socket; tests use
// InMemoryDatagramPair (with loss / duplication / reordering).
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace YawVR.Next
{
    public interface IDatagramSocket : IDisposable
    {
        /// <summary>Sends one datagram. Must not throw for transient network errors.</summary>
        void Send(byte[] buffer, int offset, int count);

        /// <summary>Copies the next datagram into <paramref name="buffer"/>; returns its length, or -1 if none is waiting. Never blocks.</summary>
        int TryReceive(byte[] buffer);

        /// <summary>Blocks up to <paramref name="timeoutMs"/> until a datagram may be available.</summary>
        void WaitReadable(int timeoutMs);
    }

    /// <summary>
    /// A UDP socket "connected" to one server (the OS then filters out datagrams from anyone else).
    ///
    /// Hardening compared to the old YawUDPClient:
    /// - an ICMP "port unreachable" (the server is restarting) shows up on a connected UDP socket
    ///   as SocketException ConnectionReset/ConnectionRefused on the NEXT receive or send. That is
    ///   transient: it is counted and ignored, the session's liveness timer decides about the link;
    /// - no exception ever escapes Send/TryReceive, so the network thread cannot die silently.
    /// </summary>
    public sealed class UdpDatagramSocket : IDatagramSocket
    {
        readonly Socket _socket;
        volatile bool _disposed;

        /// <summary>Transient errors seen so far (ICMP unreachable etc.), for diagnostics.</summary>
        public int TransientErrors { get; private set; }

        public UdpDatagramSocket(IPEndPoint server)
        {
            _socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socket.Blocking = false;
            _socket.Connect(server);
        }

        static bool IsTransient(SocketError e) =>
            e == SocketError.ConnectionReset || e == SocketError.ConnectionRefused
            || e == SocketError.WouldBlock || e == SocketError.NetworkUnreachable
            || e == SocketError.HostUnreachable || e == SocketError.Interrupted
            || e == SocketError.NoBufferSpaceAvailable;

        public void Send(byte[] buffer, int offset, int count)
        {
            if (_disposed) return;
            try
            {
                _socket.Send(buffer, offset, count, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                // Transient or not, a lost UDP datagram is handled by retries / liveness.
                TransientErrors++;
                if (!IsTransient(ex.SocketErrorCode)) LastError = ex.SocketErrorCode;
            }
            catch (ObjectDisposedException) { }
        }

        /// <summary>The last non-transient socket error (null if none).</summary>
        public SocketError? LastError { get; private set; }

        public int TryReceive(byte[] buffer)
        {
            if (_disposed) return -1;
            try
            {
                if (_socket.Available == 0) return -1;
                return _socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                TransientErrors++;
                if (!IsTransient(ex.SocketErrorCode)) LastError = ex.SocketErrorCode;
                return -1;
            }
            catch (ObjectDisposedException) { return -1; }
        }

        public void WaitReadable(int timeoutMs)
        {
            if (_disposed) return;
            try { _socket.Poll(timeoutMs * 1000, SelectMode.SelectRead); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _socket.Close(); } catch (SocketException) { }
        }
    }

    /// <summary>
    /// Two connected in-memory endpoints for tests: what one side sends, the other receives.
    /// Faults can be injected per direction. Not used at runtime.
    /// </summary>
    public sealed class InMemoryDatagramPair
    {
        public sealed class End : IDatagramSocket
        {
            internal End Peer;
            internal readonly Queue<byte[]> Inbox = new Queue<byte[]>();
            readonly object _lock;
            readonly Random _rng;

            /// <summary>Probability (0..1) that a datagram sent from this end is dropped.</summary>
            public double DropRate;
            /// <summary>Probability that a datagram sent from this end is delivered twice.</summary>
            public double DuplicateRate;
            /// <summary>Probability that a datagram is put at the front of the peer's inbox (reordering).</summary>
            public double ReorderRate;
            /// <summary>When true, everything sent from this end is dropped (cable cut).</summary>
            public bool Blackhole;
            /// <summary>Every datagram sent from this end, as sent (for assertions).</summary>
            public readonly List<byte[]> Sent = new List<byte[]>();
            public bool Disposed { get; private set; }

            internal End(object sharedLock, int seed) { _lock = sharedLock; _rng = new Random(seed); }

            public void Send(byte[] buffer, int offset, int count)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                lock (_lock)
                {
                    Sent.Add(copy);
                    if (Disposed || Blackhole || Peer.Disposed || _rng.NextDouble() < DropRate) return;
                    int n = _rng.NextDouble() < DuplicateRate ? 2 : 1;
                    for (int i = 0; i < n; i++)
                    {
                        if (_rng.NextDouble() < ReorderRate && Peer.Inbox.Count > 0)
                        {
                            var rest = Peer.Inbox.ToArray();
                            Peer.Inbox.Clear();
                            Peer.Inbox.Enqueue(copy);
                            foreach (var r in rest) Peer.Inbox.Enqueue(r);
                        }
                        else Peer.Inbox.Enqueue(copy);
                    }
                }
            }

            public int TryReceive(byte[] buffer)
            {
                lock (_lock)
                {
                    if (Disposed || Inbox.Count == 0) return -1;
                    var d = Inbox.Dequeue();
                    Buffer.BlockCopy(d, 0, buffer, 0, d.Length);
                    return d.Length;
                }
            }

            public void WaitReadable(int timeoutMs) { }

            public void Dispose() { lock (_lock) { Disposed = true; Inbox.Clear(); } }
        }

        public readonly End Client;
        public readonly End Server;

        public InMemoryDatagramPair(int seed = 1)
        {
            var l = new object();
            Client = new End(l, seed);
            Server = new End(l, seed + 1);
            Client.Peer = Server;
            Server.Peer = Client;
        }
    }
}
