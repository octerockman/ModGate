using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ModGate
{
    internal static class AdminConfigSecurity
    {
        internal const string OwnerKeyPath = "config/ModGate/admin-access.key";
        private static readonly object KeyGate = new object();
        public static string EnsureOwnerKey(string root)
        {
            lock (KeyGate)
            {
                var path = SafePath.Under(root, OwnerKeyPath);
                if (File.Exists(path)) return ReadOwnerKey(root);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = new byte[32];
                try
                {
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                    var key = Hex(bytes);
                    var content = Encoding.ASCII.GetBytes(key + Environment.NewLine);
                    try
                    {
                        using (var stream = CreateOwnerFile(path)) { stream.Write(content, 0, content.Length); stream.Flush(); }
                    }
                    catch (IOException) when (File.Exists(path)) { return ReadOwnerKey(root); }
                    finally { Array.Clear(content, 0, content.Length); }
                    return key;
                }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
        }
        public static string ReadOwnerKey(string root)
        {
            var path = SafePath.Under(root, OwnerKeyPath);
            if (!File.Exists(path) || new FileInfo(path).Length > 256) throw new InvalidDataException("The server owner access key is missing or invalid.");
            var key = File.ReadAllText(path, Encoding.ASCII).Trim();
            if (!TryHex(key, out var bytes)) throw new InvalidDataException("The server owner access key is invalid. Keep the existing file and ask the owner to inspect it.");
            Array.Clear(bytes, 0, bytes.Length);
            return key;
        }
        public static string Sign(AdminConfigRequest request, string key)
        {
            ValidateSigningRequest(request);
            if (!TryHex((key ?? "").Trim(), out var secret)) throw new InvalidDataException("Enter the server owner's access key.");
            byte[]? payload = null;
            try
            {
                var unsigned = Json.CloneLocal(request); unsigned.Proof = "";
                payload = Encoding.UTF8.GetBytes("ModGate.AdminConfig.v1\n" + Json.Write(unsigned));
                using (var hmac = new HMACSHA256(secret)) return Hex(hmac.ComputeHash(payload));
            }
            finally { Array.Clear(secret, 0, secret.Length); if (payload != null) Array.Clear(payload, 0, payload.Length); }
        }
        public static bool Verify(AdminConfigRequest request, string key, string expectedSessionNonce)
        {
            if (request == null || string.IsNullOrEmpty(expectedSessionNonce) || request.Nonce != expectedSessionNonce || !TryHex(request.Proof ?? "", out var supplied)) return false;
            byte[] expected = Array.Empty<byte>();
            try
            {
                if (!TryHex(Sign(request, key), out expected)) return false;
                var different = supplied.Length ^ expected.Length;
                for (var index = 0; index < supplied.Length; index++) different |= supplied[index] ^ expected[index];
                return different == 0;
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is SerializationException || error is NullReferenceException) { return false; }
            finally { Array.Clear(supplied, 0, supplied.Length); Array.Clear(expected, 0, expected.Length); }
        }
        private static void ValidateSigningRequest(AdminConfigRequest request)
        {
            if (request == null || request.Version != 1 || !Guid.TryParseExact(request.Nonce, "N", out _) || !Guid.TryParseExact(request.RequestId, "N", out _)) throw new InvalidDataException("Invalid admin configuration request identity.");
            AdminConfigProtocol.ValidateRequest(request);
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static bool TryHex(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text == null || text.Length != 64) return false;
            var output = new byte[32];
            for (var index = 0; index < output.Length; index++)
            {
                var high = Digit(text[index * 2]); var low = Digit(text[index * 2 + 1]);
                if (high < 0 || low < 0) { Array.Clear(output, 0, output.Length); return false; }
                output[index] = (byte)((high << 4) | low);
            }
            bytes = output; return true;
        }
        private static int Digit(char value) => value >= '0' && value <= '9' ? value - '0' : value >= 'a' && value <= 'f' ? value - 'a' + 10 : value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;

        // Create with private permissions from the first open, rather than exposing a key until chmod runs.
        private static FileStream CreateOwnerFile(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;FA;;;OW)(A;;FA;;;SY)(A;;FA;;;BA)", 1, out var descriptor, out _)) throw new IOException("Could not create private access-key permissions.");
                try
                {
                    var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                    var handle = CreateFile(path, 0x40000000, 0, ref attributes, 1, 0x80, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Could not create the private owner access-key file."); }
                    return new FileStream(handle, FileAccess.Write);
                }
                finally { LocalFree(descriptor); }
            }
            var flags = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? 1 | 64 | 128 | 524288 : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 1 | 512 | 2048 | 16777216 : throw new PlatformNotSupportedException("Private owner-key creation requires Windows, Linux or macOS.");
            var fd = Open(path, flags, 384); // 0600: server process owner only.
            if (fd < 0) throw new IOException("Could not create the private owner access-key file.");
            return new FileStream(new SafeFileHandle(new IntPtr(fd), true), FileAccess.Write);
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags, uint mode);
    }

    internal sealed class AdminConfigRequestGuard
    {
        private readonly object gate = new object();
        private readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<DateTime> attempts = new Queue<DateTime>();
        private readonly int rateLimit, replayCapacity;
        private readonly TimeSpan window;
        private string nonce = "";
        private long lastPositiveSequence;
        public AdminConfigRequestGuard(int maxRequestsPerWindow = 30, TimeSpan? rateWindow = null, int maxSessionRequests = 4096)
        {
            if (maxRequestsPerWindow < 1 || maxSessionRequests < 1 || rateWindow.HasValue && rateWindow.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("Invalid request guard settings.");
            rateLimit = maxRequestsPerWindow; window = rateWindow ?? TimeSpan.FromSeconds(10); replayCapacity = maxSessionRequests;
        }
        public bool Authorize(AdminConfigRequest request, string key, string expectedSessionNonce, bool serverVerifiedAdmin, bool connected, DateTime? utcNow = null)
        {
            lock (gate)
            {
                if (!connected || !serverVerifiedAdmin || request == null) return false;
                var now = utcNow ?? DateTime.UtcNow;
                while (attempts.Count > 0 && now - attempts.Peek() >= window) attempts.Dequeue();
                if (attempts.Count >= rateLimit || nonce.Length > 0 && nonce != expectedSessionNonce) return false;
                attempts.Enqueue(now);
                if (request.Sequence > 0)
                {
                    // Reliable ordered requests need only a signed high-watermark, so health
                    // polling can continue indefinitely without evicting replay protection.
                    if (request.Sequence <= lastPositiveSequence || !AdminConfigSecurity.Verify(request, key, expectedSessionNonce)) return false;
                    nonce = expectedSessionNonce; lastPositiveSequence = request.Sequence;
                    used.Clear(); return true;
                }
                // A sequence-aware connection cannot downgrade to the legacy replay cache.
                if (request.Sequence < 0 || lastPositiveSequence > 0 || used.Count >= replayCapacity) return false;
                var requestId = request.RequestId ?? "";
                if (used.Contains(requestId) || !AdminConfigSecurity.Verify(request, key, expectedSessionNonce)) return false;
                nonce = expectedSessionNonce; used.Add(requestId); return true;
            }
        }
    }
}
