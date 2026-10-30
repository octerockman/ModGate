using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;

namespace ModGate
{
    [DataContract] internal sealed class PasswordPreferences
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public List<string> DisabledServers = new List<string>();
    }
    internal sealed class PasswordVault
    {
        private readonly string root;
        public PasswordVault(string root) { this.root = root; }
        public static bool Supported => Environment.OSVersion.Platform == PlatformID.Win32NT;
        public PasswordPreferences Preferences()
        {
            var path = SafePath.Under(root, "config/ModGate/password-preferences.json");
            return File.Exists(path) ? Json.ReadLocal<PasswordPreferences>(path) : new PasswordPreferences();
        }
        public bool Enabled(string key) { var p = Preferences(); return Supported && p.Enabled && !(p.DisabledServers ?? new List<string>()).Contains(key); }
        public void Enable(string key, bool enabled)
        {
            var p = Preferences(); p.DisabledServers = p.DisabledServers ?? new List<string>(); p.DisabledServers.RemoveAll(s => s == key);
            if (!enabled) { p.DisabledServers.Add(key); Forget(key); }
            Json.Save(SafePath.Under(root, "config/ModGate/password-preferences.json"), p);
        }
        public void EnableAll(bool enabled)
        {
            var p = Preferences(); p.Enabled = enabled; Json.Save(SafePath.Under(root, "config/ModGate/password-preferences.json"), p);
            if (!enabled) foreach (var file in Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.secret") : Array.Empty<string>()) File.Delete(SafePath.Under(Folder, Path.GetFileName(file)));
        }
        private string Folder => SafePath.Under(root, "ModGate/private-passwords");
        private string PathFor(string key)
        {
            using var sha = SHA256.Create(); var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
            return SafePath.Under(Folder, name + ".secret");
        }
        public bool Has(string key) => File.Exists(PathFor(key));
        public void Forget(string key) { var path = PathFor(key); if (File.Exists(path)) File.Delete(path); }
        public void Save(string key, string password)
        {
            if (!Enabled(key) || string.IsNullOrEmpty(password)) return;
            var data = Encoding.UTF8.GetBytes(password);
            try { var encrypted = Protect(data, key, false); var path = PathFor(key); Directory.CreateDirectory(Folder); var temporary = path + ".tmp"; File.WriteAllBytes(temporary, encrypted); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { Array.Clear(data, 0, data.Length); }
        }
        public string? Read(string key)
        {
            if (!Enabled(key) || !Has(key)) return null;
            byte[]? clear = null;
            try { clear = Protect(File.ReadAllBytes(PathFor(key)), key, true); return Encoding.UTF8.GetString(clear); }
            catch (CryptographicException) { return null; }
            finally { if (clear != null) Array.Clear(clear, 0, clear.Length); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static byte[] Protect(byte[] bytes, string key, bool decrypt)
        {
            if (!Supported) throw new PlatformNotSupportedException("Password remembering requires Windows account encryption.");
            var purpose = Encoding.UTF8.GetBytes("ModGate/password/v1/" + key);
            var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            var entropy = new Blob { Length = purpose.Length, Data = Marshal.AllocHGlobal(purpose.Length) }; Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(purpose, 0, entropy.Data, purpose.Length);
                var ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, "ModGate server password", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("Windows could not access this saved server password (error " + Marshal.GetLastWin32Error() + ").");
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
                Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
                if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
            }
        }
    }
}
