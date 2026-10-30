using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ModGate
{
    internal static class ManualMods
    {
        public static void Stage(string root, IReadOnlyList<ModEntry> required, IReadOnlyList<ModEntry> installed, PendingInstall pending, CancellationToken token)
        {
            var plugins = Path.GetFullPath(Path.Combine(root, "plugins")).TrimEnd(Path.DirectorySeparatorChar);
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wanted in required)
            {
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(wanted.NexusUrl)) NexusLinks.Validate(wanted.NexusUrl);
                var local = installed.SingleOrDefault(m => m.Guid == wanted.Guid && PolicyEngine.VersionsEqual(m.Version, wanted.Version));
                if (local == null || !File.Exists(local.LocalFile)) throw new InvalidDataException("The installed copy of " + wanted.Name + " " + wanted.Version + " is unavailable. Restart and reconnect before preparing the profile.");
                var dll = Path.GetFullPath(local.LocalFile);
                var folder = Path.GetDirectoryName(dll)!;
                var flat = folder.Equals(plugins, StringComparison.OrdinalIgnoreCase);
                if (flat && (File.GetAttributes(dll) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Keep linked mod DLLs in their own subfolder under BepInEx/plugins so their original links can be preserved when switching profiles: " + wanted.Name);
                if (flat && !string.IsNullOrWhiteSpace(wanted.NexusUrl)) throw new InvalidDataException("Put " + wanted.Name + " and its assets in their own folder under BepInEx/plugins before preparing the profile.");
                if (!flat && !folder.StartsWith(plugins + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Put " + wanted.Name + " and its assets in their own folder under BepInEx/plugins before preparing the profile.");
                var relative = (flat ? Path.GetFileName(dll) : folder.Substring(plugins.Length + 1)).Replace('\\', '/');
                SafePath.ReadSource(plugins, relative);
                if (!copied.Add(flat ? dll : folder)) continue;
                string key;
                using (var sha = SHA256.Create()) key = (string.IsNullOrWhiteSpace(wanted.NexusUrl) ? "LocalMod_" : "ManualNexus_") + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(relative.ToLowerInvariant()))).Replace("-", "");
                var before = SafePath.Hash(dll);
                var temporary = SafePath.Under(root, "ModGate/" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                        foreach (var file in flat ? new[] { dll } : Files(plugins, folder))
                        {
                            token.ThrowIfCancellationRequested();
                            var identities = installed.Where(m => !string.IsNullOrWhiteSpace(m.LocalFile) && Path.GetFullPath(m.LocalFile).Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (identities.Any(m => required.All(r => r.Guid != m.Guid)))
                            {
                                if (identities.Any(m => required.Any(r => r.Guid == m.Guid))) throw new InvalidDataException("Selected and unchecked mods share a DLL in " + wanted.Name + ". They cannot be separated into different profile selections.");
                                continue;
                            }
                            using (var input = File.OpenRead(file))
                            using (var output = zip.CreateEntry(file.Substring(folder.Length + 1).Replace('\\', '/'), CompressionLevel.NoCompression).Open()) Downloads.CopyPayload(input, output, token);
                        }
                    // Reuse archive checks for scripts, protected files and collisions.
                    var package = PackageId.Parse("Local-" + key + "-0.0.0");
                    Downloads.Extract(temporary, stage, package, validateManifest: false, token: token);
                    var source = SafePath.Under(stage, "packages/" + package.Key);
                    var destination = SafePath.Under(stage, "packages/" + key);
                    Directory.Move(source, destination);
                    var stagedDll = SafePath.Under(destination, dll.Substring(folder.Length + 1).Replace('\\', '/'));
                    if (SafePath.Hash(stagedDll) != before || SafePath.Hash(dll) != before) throw new InvalidDataException("A local mod changed during preparation. Restart and retry.");
                    pending.LocalPackages.Add(key);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            pending.Files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(f => new FileDigest { Path = f.Substring(stage.Length + 1).Replace('\\', '/'), Sha256 = SafePath.Hash(f) }).ToList();
        }

        private static IEnumerable<string> Files(string plugins, string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SafePath.ReadSource(plugins, path.Substring(plugins.Length + 1).Replace('\\', '/'));
                if (Directory.Exists(path)) { foreach (var file in Files(plugins, path)) yield return file; }
                else yield return path;
            }
        }
    }
}
