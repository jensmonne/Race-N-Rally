// Packet layout (docs/PROTOCOL.md §2; Rust: crates/yawvr-net/src/wire.rs).
//
//   offset  size  field
//   0       1     kind     0 = RELIABLE, 1 = ACK, 2 = UNRELIABLE, 3 = ESTOP
//   1       4     seq      u32, big-endian
//   5       n     payload  protobuf (RELIABLE/UNRELIABLE), empty (ACK), "YAWESTOP" (ESTOP)
//   5+n     16    tag      first 16 bytes of HMAC-SHA256(key, bytes[0 .. 5+n])
//
// Everything here works on caller-provided buffers, so the hot path (pose packets) allocates nothing.
using System;
using System.Text;

namespace YawVR.Next
{
    public static class WireFormat
    {
        public const byte KindReliable = 0;
        public const byte KindAck = 1;
        public const byte KindUnreliable = 2;
        public const byte KindEstop = 3;

        public const int HeaderLen = 5;
        public const int TagLen = 16;
        public const int MinPacket = HeaderLen + TagLen;
        /// <summary>Largest datagram the server accepts (network.max_packet_bytes default).</summary>
        public const int MaxPacket = 1200;

        public static readonly byte[] EstopMagic = Encoding.ASCII.GetBytes("YAWESTOP");

        /// <summary>
        /// Writes header + payload + tag into <paramref name="dest"/> and returns the packet length.
        /// <paramref name="key"/> null means "16 zero bytes as tag" (only for the unauthenticated e-stop).
        /// </summary>
        public static int Seal(byte kind, uint seq, ReadOnlySpan<byte> payload, PacketKey key, byte[] dest)
        {
            int len = HeaderLen + payload.Length + TagLen;
            if (dest.Length < len) throw new ArgumentException("destination buffer too small");
            dest[0] = kind;
            dest[1] = (byte)(seq >> 24);
            dest[2] = (byte)(seq >> 16);
            dest[3] = (byte)(seq >> 8);
            dest[4] = (byte)seq;
            payload.CopyTo(new Span<byte>(dest, HeaderLen, payload.Length));
            var tag = new Span<byte>(dest, HeaderLen + payload.Length, TagLen);
            if (key == null) tag.Clear();
            else key.Tag(new ReadOnlySpan<byte>(dest, 0, HeaderLen + payload.Length), tag);
            return len;
        }

        /// <summary>
        /// Structural parse. Never throws. The tag is NOT checked here (see <see cref="Authentic"/>).
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> d, out byte kind, out uint seq, out int payloadLen)
        {
            kind = 0; seq = 0; payloadLen = 0;
            if (d.Length < MinPacket || d.Length > MaxPacket) return false;
            kind = d[0];
            if (kind > KindEstop) return false;
            payloadLen = d.Length - MinPacket;
            if (kind == KindAck && payloadLen != 0) return false;
            if (kind == KindEstop && !d.Slice(HeaderLen, payloadLen).SequenceEqual(EstopMagic)) return false;
            seq = ((uint)d[1] << 24) | ((uint)d[2] << 16) | ((uint)d[3] << 8) | d[4];
            return true;
        }

        public static bool Authentic(ReadOnlySpan<byte> d, PacketKey key)
        {
            int signed = d.Length - TagLen;
            return key != null && key.Verify(d.Slice(0, signed), d.Slice(signed, TagLen));
        }
    }
}
