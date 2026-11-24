// Keys and tags (docs/PROTOCOL.md §4; Rust: crates/yawvr-net/src/auth.rs).
//
//   hello_c2s   = HMAC(PSK, "yawvr/v2/hello/c2s")
//   hello_s2c   = HMAC(PSK, "yawvr/v2/hello/s2c")
//   session_c2s = HMAC(PSK, "yawvr/v2/session/c2s" || client_nonce || server_nonce || session_id_be32)
//   session_s2c = HMAC(PSK, "yawvr/v2/session/s2c" || ...)
//   tag         = HMAC-SHA256(key, header || payload)[0..16]
using System;
using System.Security.Cryptography;
using System.Text;

namespace YawVR.Next
{
    public enum Direction { ClientToServer, ServerToClient }

    /// <summary>
    /// One derived key plus a reusable HMAC instance, so computing a tag allocates nothing
    /// (TryComputeHash writes into a buffer we own). Not thread-safe: one user at a time.
    /// </summary>
    public sealed class PacketKey : IDisposable
    {
        readonly HMACSHA256 _hmac;
        readonly byte[] _scratch = new byte[32];
        readonly byte[] _keyBytes;

        public PacketKey(byte[] key)
        {
            _keyBytes = (byte[])key.Clone();
            _hmac = new HMACSHA256(_keyBytes);
        }

        /// <summary>A second, independent instance of the same key (for a different thread).</summary>
        public PacketKey Clone() => new PacketKey(_keyBytes);

        /// <summary>The raw key (for tests and test vectors only).</summary>
        public byte[] KeyBytes => (byte[])_keyBytes.Clone();

        public void Tag(ReadOnlySpan<byte> data, Span<byte> tag16)
        {
            _hmac.TryComputeHash(data, _scratch, out _);
            new ReadOnlySpan<byte>(_scratch, 0, WireFormat.TagLen).CopyTo(tag16);
        }

        public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
        {
            if (tag.Length != WireFormat.TagLen) return false;
            _hmac.TryComputeHash(data, _scratch, out _);
            // Constant time: always compare all 16 bytes, no early exit (no timing hint to an attacker).
            int diff = 0;
            for (int i = 0; i < WireFormat.TagLen; i++) diff |= _scratch[i] ^ tag[i];
            return diff == 0;
        }

        public void Dispose() => _hmac.Dispose();
    }

    public static class KeyDerivation
    {
        public const int PskLen = 32;
        public const int NonceLen = 16;

        /// <summary>Parses the 64-hex-character pre-shared key (OPEN_QUESTIONS: how it reaches the headset).</summary>
        /// <summary>Prefix hashed in front of a password (part of the protocol, same as the Rust server).</summary>
        public static readonly byte[] PasswordKeyPrefix = Encoding.ASCII.GetBytes("yawvr-server password v1\0");

        /// <summary>
        /// The key from a simple password (server: auth.password): SHA-256(prefix || UTF-8(password)).
        /// Easier than a 64-hex key, but a short password can be guessed offline from recorded traffic.
        /// Used exactly as given (no trimming); empty or whitespace-only is refused.
        /// </summary>
        public static byte[] FromPassword(string password)
        {
            if (password == null || password.Trim().Length == 0) throw new ArgumentException("the password is empty");
            byte[] pw = Encoding.UTF8.GetBytes(password);
            var buf = new byte[PasswordKeyPrefix.Length + pw.Length];
            Buffer.BlockCopy(PasswordKeyPrefix, 0, buf, 0, PasswordKeyPrefix.Length);
            Buffer.BlockCopy(pw, 0, buf, PasswordKeyPrefix.Length, pw.Length);
            using (var sha = SHA256.Create()) return sha.ComputeHash(buf);
        }

        /// <summary>64 hex characters = a key; anything else = a password (FromPassword).</summary>
        public static byte[] KeyFromKeyOrPassword(string text)
        {
            if (text != null && text.Trim().Length == PskLen * 2)
            {
                try { return ParsePskHex(text); } catch (FormatException) { }
            }
            return FromPassword(text);
        }

        public static byte[] ParsePskHex(string hex)
        {
            if (hex == null) throw new ArgumentNullException(nameof(hex));
            string t = hex.Trim();
            if (t.Length != PskLen * 2) throw new FormatException("the key must be exactly 64 hexadecimal characters");
            var psk = new byte[PskLen];
            bool allZero = true;
            for (int i = 0; i < PskLen; i++)
            {
                psk[i] = Convert.ToByte(t.Substring(i * 2, 2), 16);
                allZero &= psk[i] == 0;
            }
            if (allZero) throw new FormatException("the key is all zeros");
            return psk;
        }

        static string Label(Direction d) => d == Direction.ClientToServer ? "c2s" : "s2c";

        static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            int len = 0;
            foreach (var p in parts) len += p.Length;
            var all = new byte[len];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, o, p.Length); o += p.Length; }
            using (var h = new HMACSHA256(key)) return h.ComputeHash(all);
        }

        public static byte[] HelloKey(byte[] psk, Direction d) =>
            Hmac(psk, Encoding.ASCII.GetBytes("yawvr/v2/hello/" + Label(d)));

        public static byte[] SessionKey(byte[] psk, Direction d, byte[] clientNonce, byte[] serverNonce, uint sessionId)
        {
            var sid = new[] { (byte)(sessionId >> 24), (byte)(sessionId >> 16), (byte)(sessionId >> 8), (byte)sessionId };
            return Hmac(psk, Encoding.ASCII.GetBytes("yawvr/v2/session/" + Label(d)), clientNonce, serverNonce, sid);
        }
    }
}
