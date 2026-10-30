using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ModGate
{
    internal static class LocalManifestReader
    {
        private const string ManualRoot = "ModGate/manifests";
        public static string ServerManifestFolder(string config, string guid, IReadOnlyList<ModEntry>? inventory = null)
        {
            var safe = Regex.IsMatch(guid, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$") && !guid.EndsWith(".", StringComparison.Ordinal) &&
                !guid.StartsWith("guid-", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(guid.Split('.')[0], @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !(inventory?.Any(m => m.Guid != guid && string.Equals(m.Guid, guid, StringComparison.OrdinalIgnoreCase)) ?? false);
            string key;
            if (safe) key = guid;
            else using (var sha = SHA256.Create()) key = "guid-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(guid))).Replace("-", "").ToLowerInvariant();
            return SafePath.Under(config, ManualRoot + "/" + key);
        }

        public static void ApplyServerManifests(IReadOnlyList<ModEntry> mods, string plugins, string config, Action<string> log)
        {
            var comparer = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var pluginRoot = Path.GetFullPath(plugins).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                var root = SafePath.Under(config, ManualRoot); Directory.CreateDirectory(root);
                WriteInstructions(SafePath.Under(root, "README.txt"),
                    "ModGate server manifests\n\n" +
                    "Each plugin has its own GUID folder. Copy its original package manifest.json into that folder as manifest.json.\n" +
                    "Example: OctoPack/manifest.json for plugin GUID OctoPack. Do not put one shared manifest in the plugins folder or this root.\n" +
                    "Loose plugins receive folders automatically when the server starts. Other installed plugins can use the same layout when their package manifest is missing.\n" +
                    "Existing package manifests take priority. DLL GUID, display name and plugin version remain authoritative.\n" +
                    "The manifest describes the package version, website and dependencies. Do not change its package version just to match a differing DLL version.\n" +
                    "Restart or reconnect after adding a manifest so the server can reread it; background lookup may require another reconnect.\n" +
                    "Unusual, reserved or case-colliding GUIDs use guid-<hash> folders; each generated folder's README identifies its plugin.\n");
            }
            catch (Exception error) when (FileError(error)) { log("Could not prepare the server manifest folder: " + error.Message); return; }

            foreach (var mod in mods.Where(m => m.Guid != Constants.Guid && !string.IsNullOrWhiteSpace(m.Guid) && !string.IsNullOrWhiteSpace(m.LocalFile)))
            {
                try
                {
                    var dll = Path.GetFullPath(mod.LocalFile);
                    if (!File.Exists(dll) || !dll.StartsWith(pluginRoot + Path.DirectorySeparatorChar, comparer)) continue;
                    var folder = ServerManifestFolder(config, mod.Guid, mods);
                    if (Path.GetDirectoryName(dll)!.Equals(pluginRoot, comparer))
                    {
                        Directory.CreateDirectory(folder);
                        WriteInstructions(SafePath.Under(folder, "README.txt"), "Mod: " + mod.Name + "\nPlugin GUID: " + mod.Guid + "\nDLL: " + Path.GetFileName(dll) + "\n\nCopy this mod's original package manifest here as manifest.json.\nDLL identity/version and existing package manifests remain authoritative.\n");
                    }
                    // Recheck the ordinary/linked package first if this inventory has not read it.
                    if (mod.LocalManifest == null) mod.LocalManifest = Read(dll, plugins);
                    if (mod.LocalManifest != null) continue;
                    var manifest = SafePath.Under(folder, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    if (new FileInfo(manifest).Length > 64 * 1024) throw new InvalidDataException("Server-supplied manifest exceeds 64 KiB: " + manifest);
                    mod.LocalManifest = PackageManifest.Read(File.OpenRead(manifest));
                }
                catch (Exception error) when (FileError(error)) { log("Manifest for " + mod.Name + " [" + mod.Guid + "] was not used: " + error.Message); }
            }
        }
        private static bool FileError(Exception error) => error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is System.Runtime.Serialization.SerializationException || error is ArgumentException || error is NotSupportedException;
        private static void WriteInstructions(string path, string content)
        {
            try { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content); }
            catch (IOException) when (File.Exists(path)) { } // Keep owner edits and handle concurrent setup safely.
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        public static PackageManifest? Read(string dll, string plugins)
        {
            var found = Nearby(dll, plugins);
            if (found != null) return found;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                using (var stream = File.OpenRead(dll))
                {
                    var buffer = new StringBuilder(32768);
                    var size = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                    if (size == 0 || size >= buffer.Capacity) return null;
                    var path = buffer.ToString();
                    if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
                    else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
                    return path.Equals(Path.GetFullPath(dll), StringComparison.OrdinalIgnoreCase) ? null : Nearby(path, null);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        private static PackageManifest? Nearby(string dll, string? stop)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(dll));
            var limit = stop == null ? null : Path.GetFullPath(stop).TrimEnd(Path.DirectorySeparatorChar);
            for (var depth = 0; directory != null && depth < 4; depth++, directory = Path.GetDirectoryName(directory))
            {
                if (directory.Equals(limit, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase)) break;
                var file = Path.Combine(directory, "manifest.json");
                try
                {
                    if (!File.Exists(file)) continue;
                    if (new FileInfo(file).Length > 64 * 1024) continue;
                    return PackageManifest.Read(File.OpenRead(file));
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is System.Runtime.Serialization.SerializationException) { continue; }
            }
            return null;
        }
    }
}
