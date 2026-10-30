using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModGate
{
    internal static class ProfileConfigs
    {
        internal static bool Allowed(string relative)
        {
            var path = relative.Replace('\\', '/');
            return !path.StartsWith("ModGate/", StringComparison.OrdinalIgnoreCase) && !path.Equals("BepInEx.cfg", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("studio.valheim.modgate.cfg", StringComparison.OrdinalIgnoreCase) &&
                new[] { ".cfg", ".json", ".yml", ".yaml", ".toml", ".ini", ".conf" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }
        public static void RecordActive(string root, IReadOnlyList<ModEntry> mods)
        {
            if (Profiles.Pending(root) || File.Exists(SafePath.Under(root, "ModGate/profile-journal.json"))) return;
            var catalog = Profiles.Load(root); var config = SafePath.Under(root, "config");
            var bindings = Bindings(config, mods, Array.Empty<ConfigBinding>());
            if (catalog.Active == "offline") { catalog.OfflineMods = mods.ToList(); catalog.OfflineBindings = bindings; }
            else
            {
                var active = catalog.Servers.Single(s => s.Slot == catalog.Active);
                active.Mods = mods.ToList(); active.ConfigBindings = bindings;
            }
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
        }
        public static List<ConfigBinding> Bindings(string source, IReadOnlyList<ModEntry> mods, IEnumerable<ConfigBinding> known)
        {
            var existing = known.ToArray(); var result = new List<ConfigBinding>();
            foreach (var mod in mods.Where(m => m.Guid != Constants.Guid))
            {
                var files = (mod.LocalConfigFiles ?? new List<string>()).Concat(existing.Where(b => b.Guid == mod.Guid).SelectMany(b => b.Files ?? new List<string>()))
                    .Concat(new[] { mod.Guid + ".cfg", mod.Guid + ".json", mod.Guid + ".yml", mod.Guid + ".yaml" }).Where(Allowed).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(f => File.Exists(SafePath.ReadSource(source, f))).ToList();
                result.Add(new ConfigBinding { Guid = mod.Guid, Files = files });
            }
            return result;
        }
        public static List<ConfigBinding> Stage(string root, IReadOnlyList<ModEntry> selectedMods, string mode, PendingInstall pending)
        {
            if (!new[] { "active", "offline", "fresh" }.Contains(mode) && !System.Text.RegularExpressions.Regex.IsMatch(mode, @"^saved:server-[1-4]$")) throw new InvalidDataException("Unknown configuration source.");
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            var destination = SafePath.Under(stage, "config");
            if (Directory.Exists(destination))
            {
                foreach (var file in Directory.GetFileSystemEntries(destination, "*", SearchOption.AllDirectories)) SafePath.Under(stage, file.Substring(stage.Length + 1));
                Directory.Delete(destination, true);
            }
            var copied = new List<ConfigBinding>();
            if (mode != "fresh")
            {
                var catalog = Profiles.Load(root);
                var saved = mode.StartsWith("saved:", StringComparison.Ordinal) ? catalog.Servers.Single(s => s.Slot == mode.Substring(6)) : null;
                var offline = mode == "offline";
                var source = SafePath.Under(root, saved != null && saved.Slot != catalog.Active ? "ModGate/profiles/" + saved.Slot + "/config" : offline && catalog.Active != "offline" ? "ModGate/profiles/offline/config" : "config");
                var known = saved != null ? saved.ConfigBindings : offline ? catalog.OfflineBindings : catalog.Active == "offline" ? catalog.OfflineBindings : catalog.Servers.Single(s => s.Slot == catalog.Active).ConfigBindings;
                copied = Bindings(source, selectedMods, known ?? new List<ConfigBinding>());
                Copy(source, destination, copied);
            }
            RefreshReceipt(stage, pending);
            return copied;
        }
        public static void RefreshReceipt(string stage, PendingInstall pending)
        {
            pending.Files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(f => new FileDigest { Path = f.Substring(stage.Length + 1).Replace('\\', '/'), Sha256 = SafePath.Hash(f) }).ToList();
        }
        private static void Copy(string source, string destination, IEnumerable<ConfigBinding> bindings)
        {
            foreach (var file in bindings.SelectMany(b => b.Files).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Allowed(file)) throw new InvalidDataException("Global or unsupported config cannot be copied: " + file);
                var from = SafePath.ReadSource(source, file); var to = SafePath.Under(destination, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, true);
            }
        }
        public static int SyncAll(string root, IReadOnlyList<ModEntry> activeMods)
        {
            if (Profiles.Pending(root)) throw new InvalidOperationException("Apply the pending profile change before synchronizing configs.");
            RecordActive(root, activeMods); var catalog = Profiles.Load(root);
            var source = SafePath.Under(root, "config");
            var sourceBindings = Bindings(source, activeMods, catalog.Active == "offline" ? catalog.OfflineBindings ?? new List<ConfigBinding>() : catalog.Servers.Single(s => s.Slot == catalog.Active).ConfigBindings ?? new List<ConfigBinding>());
            var batch = "ModGate/config-backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            var writes = new List<Tuple<string, string, string, bool>>(); long total = 0;
            foreach (var slot in catalog.Servers.Select(s => s.Slot).Concat(new[] { "offline" }).Where(s => s != catalog.Active))
            {
                var targetMods = slot == "offline" ? catalog.OfflineMods ?? new List<ModEntry>() : catalog.Servers.Single(s => s.Slot == slot).Mods;
                var destination = SafePath.Under(root, "ModGate/profiles/" + slot + "/config");
                foreach (var binding in sourceBindings.Where(b => targetMods.Any(m => m.Guid == b.Guid) || (slot == "offline" && targetMods.Count == 0 && b.Files.Any(f => File.Exists(SafePath.Under(destination, f))))))
                    foreach (var file in binding.Files)
                    {
                        var length = new FileInfo(SafePath.ReadSource(source, file)).Length; total += length;
                        if (length > 16L * 1024 * 1024 || total > 256L * 1024 * 1024) throw new InvalidDataException("Config sync exceeds its size limit.");
                        var to = SafePath.Under(destination, file);
                        if (writes.Any(w => w.Item1.Equals(to, StringComparison.OrdinalIgnoreCase))) continue;
                        var snapshot = SafePath.Under(root, batch + "/new/" + slot + "/" + file);
                        var backup = SafePath.Under(root, batch + "/old/" + slot + "/" + file);
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!); File.Copy(SafePath.ReadSource(source, file), snapshot, true);
                        var existed = File.Exists(to);
                        if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(to, backup, false); }
                        if (writes.All(w => !w.Item1.Equals(to, StringComparison.OrdinalIgnoreCase))) writes.Add(Tuple.Create(to, snapshot, backup, existed));
                    }
            }
            var applied = new List<Tuple<string, string, string, bool>>();
            try
            {
                foreach (var write in writes)
                {
                    applied.Add(write); Directory.CreateDirectory(Path.GetDirectoryName(write.Item1)!); File.Copy(write.Item2, write.Item1, true);
                }
            }
            catch
            {
                foreach (var write in applied.AsEnumerable().Reverse())
                    if (write.Item4) File.Copy(write.Item3, write.Item1, true); else if (File.Exists(write.Item1)) File.Delete(write.Item1);
                throw;
            }
            File.WriteAllText(SafePath.Under(root, "ModGate/config-sync-report.txt"), "Copied " + writes.Count + " applicable config files from " + catalog.Active + ". Backups: " + batch);
            return writes.Count;
        }
    }
}
