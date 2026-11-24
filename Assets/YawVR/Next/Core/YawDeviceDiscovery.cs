// Finding the rig on the network (docs/PROTOCOL.md §7).
//   request:  "YAWD" + DiscoveryRequest{protocol_version, nonce, padding}, at least 192 bytes
//   reply:    "YAWD" + DiscoveryReply{protocol_version, device_uuid, device_name, port, availability, nonce}
// UDP port 50051 (network.discovery.port). The server only answers requests that are at least as
// large as its reply (no amplification) and at most 20 per second. It can be disabled on the
// server, so a manual IP/port path always exists (see YawDeviceConnector).
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using YawVR.Proto;

namespace YawVR.Next
{
    public interface IDeviceDiscovery : IDisposable
    {
        /// <summary>Sends one discovery request (broadcast or to a given host).</summary>
        void Probe();
        /// <summary>Reads replies that have arrived (non-blocking) and returns the devices found.</summary>
        void Poll(List<DiscoveredDevice> found);
    }

    /// <summary>Encoding/decoding only (pure, unit-tested).</summary>
    public static class DiscoveryCodec
    {
        public const int DefaultPort = 50051;
        public const int MinRequestBytes = 192;
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("YAWD");

        public static byte[] BuildRequest(byte[] nonce)
        {
            var req = new DiscoveryRequest { ProtocolVersion = YawSessionConfig.ProtocolVersion, Nonce = ByteString.CopyFrom(nonce) };
            while (true)
            {
                int total = Magic.Length + req.CalculateSize();
                if (total >= MinRequestBytes)
                {
                    var outp = new byte[total];
                    Buffer.BlockCopy(Magic, 0, outp, 0, Magic.Length);
                    req.WriteTo(new Span<byte>(outp, Magic.Length, total - Magic.Length));
                    return outp;
                }
                int missing = Math.Max(MinRequestBytes - total, 1);
                req.Padding = ByteString.CopyFrom(new byte[req.Padding.Length + missing]);
            }
        }

        public static bool TryParseReply(ReadOnlySpan<byte> d, IPAddress from, out DiscoveredDevice device)
        {
            device = null;
            if (d.Length < Magic.Length || !d.Slice(0, Magic.Length).SequenceEqual(Magic)) return false;
            DiscoveryReply r;
            try { r = DiscoveryReply.Parser.ParseFrom(d.Slice(Magic.Length)); }
            catch (InvalidProtocolBufferException) { return false; }
            if (r.Port == 0 || r.Port > 65535) return false;
            device = new DiscoveredDevice
            {
                Address = from,
                Port = (int)r.Port,
                Uuid = r.DeviceUuid,
                Name = r.DeviceName,
                Available = r.Availability == Availability.Available,
                ProtocolVersion = r.ProtocolVersion,
            };
            return true;
        }
    }

    /// <summary>UDP discovery: broadcasts (or unicasts) requests and collects replies.</summary>
    public sealed class YawDeviceDiscovery : IDeviceDiscovery
    {
        readonly Socket _socket;
        readonly IPEndPoint _target;
        readonly byte[] _request;
        readonly byte[] _buf = new byte[1500];
        EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

        /// <param name="target">Where to send requests; default 255.255.255.255:50051.</param>
        public YawDeviceDiscovery(IPEndPoint target = null)
        {
            _target = target ?? new IPEndPoint(IPAddress.Broadcast, DiscoveryCodec.DefaultPort);
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true, Blocking = false };
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // any free local port (no fixed port to collide on)
            var nonce = new byte[8];
            new Random().NextBytes(nonce); // the nonce only matches replies to requests; it is not a secret
            _request = DiscoveryCodec.BuildRequest(nonce);
        }

        public void Probe()
        {
            try { _socket.SendTo(_request, _target); }
            catch (SocketException) { } // no network yet: the next probe retries
            catch (ObjectDisposedException) { }
        }

        public void Poll(List<DiscoveredDevice> found)
        {
            for (int i = 0; i < 32; i++)
            {
                int n;
                try
                {
                    if (_socket.Available == 0) return;
                    n = _socket.ReceiveFrom(_buf, ref _from);
                }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                if (DiscoveryCodec.TryParseReply(new ReadOnlySpan<byte>(_buf, 0, n), ((IPEndPoint)_from).Address, out var d)) found.Add(d);
            }
        }

        public void Dispose()
        {
            try { _socket.Close(); } catch (SocketException) { }
        }
    }
}
