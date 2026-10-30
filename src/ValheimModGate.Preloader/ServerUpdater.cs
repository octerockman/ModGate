using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;

namespace ModGate
{
    [DataContract] internal sealed class ServerUpdateReport
    {
        [DataMember] public string Status = "";
        [DataMember] public string CheckedUtc = DateTime.UtcNow.ToString("O");
        [DataMember] public List<string> Entries = new List<string>();
    }
    [DataContract] internal sealed class ServerUpdateMove
    {
        [DataMember] public string Target = "";
        [DataMember] public string Prepared = "";
        [DataMember] public string Backup = "";
    }
    [DataContract] internal sealed class ServerUpdateJournal
    {
        [DataMember] public bool Committed = false;
        [DataMember] public string ManifestBackup = "";
        [DataMember] public List<ServerUpdateMove> Moves = new List<ServerUpdateMove>();
    }
    internal static class ServerUpdater
    {
        private const string Journal = "ModGate/server-updates/journal.json";
        private const string Manifest = "config/ModGate/server-mods.json";
        private sealed class Candidate
        {
            public PackageId Id = null!;
            public ModEntry[] Declared = Array.Empty<ModEntry>();
            public PackageMetadata Metadata = null!;
            public ServerUpdateMove Move = new ServerUpdateMove();
            public List<ModEntry> Next = new List<ModEntry>();
        }
        public static bool IsDedicated(string process, IEnumerable<string> args) => process.StartsWith("valheim_server", StringComparison.OrdinalIgnoreCase) || (args.Contains("-batchmode") && args.Contains("-world"));
        public static bool Configured(string root)
        {
            var path = SafePath.Under(root, Manifest);
            if (!File.Exists(path)) return false;
            var settings = ConfigFields.Runtime(Json.Read<ServerManifest>(File.ReadAllText(path)));
            return settings.AutoUpdateOnRestart && settings.Mods.Any(m => Enabled(settings, m));
        }
        public static bool Enabled(ServerManifest settings, ModEntry mod) => settings.AutoUpdateOnRestart && (mod.AutoUpdate ?? settings.AutoUpdateAllMods) && mod.Guid != Constants.Guid;
        public static void Recover(string root)
        {
            var path = SafePath.Under(root, Journal);
            if (!File.Exists(path)) return;
            var journal = Json.Read<ServerUpdateJournal>(File.ReadAllText(path), Constants.MaxLocalJson);
            if (journal.Moves == null || journal.Moves.Count > 128 || !journal.ManifestBackup.StartsWith("ModGate/server-updates/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid server update recovery journal.");
            foreach (var move in journal.Moves)
            {
                if (!move.Target.StartsWith("plugins/", StringComparison.Ordinal) || !move.Prepared.StartsWith("ModGate/server-updates/", StringComparison.Ordinal) || !move.Backup.StartsWith("ModGate/server-updates/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid server update recovery path.");
                SafePath.Under(root, move.Target); SafePath.Under(root, move.Prepared); SafePath.Under(root, move.Backup);
            }
            if (!journal.Committed)
            {
                foreach (var move in journal.Moves.AsEnumerable().Reverse())
                {
                    var target = SafePath.Under(root, move.Target); var prepared = SafePath.Under(root, move.Prepared); var backup = SafePath.Under(root, move.Backup);
                    if (!Directory.Exists(backup)) continue;
                    if (Directory.Exists(target))
                    {
                        if (Directory.Exists(prepared)) throw new IOException("Recovery preserves conflicting server update files: " + move.Target);
                        Directory.CreateDirectory(Path.GetDirectoryName(prepared)!); Directory.Move(target, prepared);
                    }
                    Directory.Move(backup, target);
                }
                var manifestBackup = SafePath.Under(root, journal.ManifestBackup);
                if (File.Exists(manifestBackup)) File.Copy(manifestBackup, SafePath.Under(root, Manifest), true);
            }
            File.Delete(path);
        }
        public static async Task<ServerUpdateReport> Run(string root, bool dedicated, IUpdateSource source, Action<string> log, CancellationToken token, Action<int>? afterMove = null)
        {
            var report = new ServerUpdateReport { Status = "Disabled" };
            if (!dedicated) return report;
            Recover(root);
            var manifestPath = SafePath.Under(root, Manifest);
            if (!File.Exists(manifestPath)) return report;
            var storedSettings = ServerConfig.Load(manifestPath, root);
            var settings = ConfigFields.Runtime(storedSettings);
            if (!settings.AutoUpdateOnRestart) return report;
            if (!settings.Mods.Any(m => Enabled(settings, m))) { report.Status = "No mods selected for updates"; return report; }
            if (Profiles.Pending(root)) throw new InvalidOperationException("Apply the pending profile change before server updates.");
            var plugins = SafePath.Under(root, "plugins"); var installed = Inspect(plugins);
            storedSettings = AutoSetup.CanonicalEntries(storedSettings, installed);
            settings = ConfigFields.Runtime(storedSettings);
            var batch = "ModGate/server-updates/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            var mappings = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in settings.Mods)
            {
                if (mod.Guid == Constants.Guid) continue;
                if (!string.IsNullOrWhiteSpace(mod.NexusUrl)) { if (Enabled(settings, mod)) report.Entries.Add(mod.Name + ": manual Nexus entry; not updated automatically."); continue; }
                var pin = !string.IsNullOrWhiteSpace(mod.DownloadUrl) ? mod.DownloadUrl : mod.Package;
                if (!string.IsNullOrWhiteSpace(mod.Package) && !string.IsNullOrWhiteSpace(mod.DownloadUrl))
                {
                    var known = PackageId.Parse(mod.Package); var url = PackageId.FromDownloadUrl(mod.DownloadUrl);
                    if (known.Key == url.Key && known.Repository == url.Repository) pin = known.FullName;
                }
                if (!string.IsNullOrWhiteSpace(pin)) { var id = PackageId.Parse(pin); if (id.Repository == "github") { if (Enabled(settings, mod)) report.Entries.Add(mod.Name + ": manifest release fallback requires manual server updating."); } else if (!Downloads.IsBootstrap(id)) mappings[mod.Guid] = id; }
                else if (Enabled(settings, mod)) report.Entries.Add(mod.Name + ": skipped; configure a Thunderstore/Hexium package or downloadUrl first.");
            }
            var availableMappings = mappings.Where(pair => settings.Mods.Any(m => m.Guid == pair.Key && installed.Any(i => i.Guid == m.Guid && PolicyEngine.VersionsEqual(i.Version, m.Version)))).ToDictionary(pair => pair.Key, pair => pair.Value);
            var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var group in settings.Mods.Where(m => mappings.ContainsKey(m.Guid)).GroupBy(m => mappings[m.Guid].Key, StringComparer.OrdinalIgnoreCase).Take(128))
                {
                    var members = group.ToArray(); var current = mappings[members[0].Guid];
                    if (members.Any(m => storedSettings.Mods.Any(old => ConfigFields.Value(old.Guid) == m.Guid && (ConfigFields.Locked(old.Version) || ConfigFields.Locked(old.Package))))) { report.Entries.Add(current.Key + ": skipped; version or package field is locked with *."); continue; }
                    if (members.Any(m => mappings[m.Guid].FullName != current.FullName)) { report.Entries.Add(current.Key + ": skipped; inconsistent package pins."); continue; }
                    if (members.Any(m => !Enabled(settings, m))) { if (members.Any(m => Enabled(settings, m))) report.Entries.Add(current.Key + ": skipped; another plugin in this package has updates disabled."); continue; }
                    if (members.Any(m => installed.All(i => i.Guid != m.Guid || !PolicyEngine.VersionsEqual(i.Version, m.Version)))) { report.Entries.Add(current.Key + ": skipped; installed plugins do not match the recorded server version."); continue; }
                    try
                    {
                        var latest = await source.Latest(current, token).ConfigureAwait(false);
                        if (new Version(latest.Version) <= new Version(current.Version)) { report.Entries.Add(current.Key + ": current."); continue; }
                        var metadata = await source.Metadata(latest, token).ConfigureAwait(false);
                        candidates.Add(current.Key, new Candidate { Id = latest, Declared = members, Metadata = metadata });
                    }
                    catch (Exception error) when (!(error is OperationCanceledException)) { report.Entries.Add(current.Key + ": skipped; " + SystemHttps.Details(error)); }
                }
                long downloaded = 0; long expanded = 0;
                foreach (var item in candidates.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (downloaded >= 512L * 1024 * 1024) throw new InvalidDataException("Server update download budget exceeded.");
                    var candidate = item.Value;
                    try
                    {
                        log("Preparing server update " + candidate.Id.FullName);
                        var archive = SafePath.Under(root, batch + "/downloads/" + candidate.Id.CacheName); Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                        await source.Download(candidate.Id, archive, token).ConfigureAwait(false);
                        downloaded += new FileInfo(archive).Length;
                        if (downloaded > 512L * 1024 * 1024) throw new InvalidDataException("Server update download budget exceeded.");
                        var stage = SafePath.Under(root, batch + "/prepared");
                        expanded += Downloads.Extract(archive, stage, candidate.Id, 2L * 1024 * 1024 * 1024 - expanded);
                        var payload = SafePath.Under(stage, "packages/" + candidate.Id.Key);
                        var roots = Directory.GetFileSystemEntries(payload);
                        if (roots.Length == 1 && Directory.Exists(roots[0])) payload = roots[0];
                        var next = Inspect(payload);
                        if (next.Any(m => m.Guid == Constants.Guid) || candidate.Declared.Any(m => next.All(n => n.Guid != m.Guid))) throw new InvalidDataException("Updated package does not contain the expected plugins, or tries to replace ModGate.");
                        var oldFiles = candidate.Declared.Select(m => installed.Single(i => i.Guid == m.Guid).LocalFile).ToArray();
                        var relative = oldFiles[0].Substring(plugins.Length + 1).Replace('\\', '/');
                        if (!relative.Contains("/")) throw new InvalidDataException("Automatic updates require each package in its own plugins subfolder.");
                        var targetRelative = "plugins/" + relative.Split('/')[0]; var target = SafePath.Under(root, targetRelative);
                        var owned = Inspect(target);
                        foreach (var existing in Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories)) SafePath.Under(target, existing.Substring(target.Length + 1));
                        if (owned.Any(m => candidate.Declared.All(d => d.Guid != m.Guid)) || oldFiles.Any(f => !f.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Installed package folder is shared with other mods.");
                        if (next.Any(n => installed.Any(i => i.Guid == n.Guid && !i.LocalFile.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Updated package duplicates a plugin in another folder.");
                        candidate.Move = new ServerUpdateMove { Target = targetRelative, Prepared = payload.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'), Backup = batch + "/backup/" + relative.Split('/')[0] };
                        candidate.Next = next;
                    }
                    catch (Exception error) when (!(error is OperationCanceledException)) { report.Entries.Add(item.Key + ": skipped; " + SystemHttps.Details(error)); candidates.Remove(item.Key); }
                }
                WarnDependencies(candidates, availableMappings, report);
                if (candidates.Values.Select(c => c.Move.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count) throw new InvalidDataException("Update packages overlap installed folders.");
                var duplicate = candidates.Values.SelectMany(c => c.Next).GroupBy(m => m.Guid).Any(g => g.Count() > 1);
                if (duplicate) throw new InvalidDataException("Update packages contain duplicate plugin GUIDs.");
                if (candidates.Count > 0)
                {
                    var journal = new ServerUpdateJournal { ManifestBackup = batch + "/server-mods.before.json", Moves = candidates.Values.Select(c => c.Move).ToList() };
                    var backupManifest = SafePath.Under(root, journal.ManifestBackup); Directory.CreateDirectory(Path.GetDirectoryName(backupManifest)!); File.Copy(manifestPath, backupManifest, false);
                    Json.Save(SafePath.Under(root, Journal), journal);
                    try
                    {
                        var index = 0;
                        foreach (var move in journal.Moves)
                        {
                            token.ThrowIfCancellationRequested();
                            var target = SafePath.Under(root, move.Target); var backup = SafePath.Under(root, move.Backup); var prepared = SafePath.Under(root, move.Prepared);
                            Directory.CreateDirectory(Path.GetDirectoryName(backup)!); Directory.Move(target, backup); afterMove?.Invoke(index++);
                            Directory.Move(prepared, target); afterMove?.Invoke(index++);
                        }
                        foreach (var candidate in candidates.Values)
                            foreach (var next in candidate.Next)
                            {
                                var mod = settings.Mods.SingleOrDefault(m => m.Guid == next.Guid);
                                if (mod == null) { mod = new ModEntry { Guid = next.Guid, Automatic = true, Side = AutoSetup.ServerOnlyName(next.Name) ? "serverOnly" : candidate.Declared[0].Side, AutoUpdate = candidate.Declared[0].AutoUpdate }; settings.Mods.Add(mod); }
                                mod.Name = next.Name; mod.Version = next.Version; mod.Package = candidate.Id.FullName;
                            }
                        ConfigFields.Preserve(storedSettings, settings);
                        ServerConfig.Save(manifestPath, root, settings); journal.Committed = true; Json.Save(SafePath.Under(root, Journal), journal);
                        Recover(root);
                    }
                    catch { Recover(root); throw; }
                    foreach (var candidate in candidates.Values) report.Entries.Add(candidate.Id.FullName + ": updated; previous files retained in " + candidate.Move.Backup);
                }
                report.Status = candidates.Count == 0 ? "No updates applied" : "Updated " + candidates.Count + " packages";
            }
            catch (Exception error) { report.Status = "Updates stopped; inspect the report and any recovery journal: " + error.Message; }
            Json.Save(SafePath.Under(root, "config/ModGate/server-update-report.json"), report, true);
            log(report.Status); return report;
        }
        private static void WarnDependencies(Dictionary<string, Candidate> candidates, Dictionary<string, PackageId> mappings, ServerUpdateReport report)
        {
            foreach (var item in candidates)
                foreach (var dependency in item.Value.Metadata.Dependencies)
            {
                PackageId needed;
                try { needed = PackageId.Parse(dependency); }
                catch (InvalidDataException) { report.Entries.Add(item.Key + ": warning; invalid dependency package declaration ignored."); continue; }
                if (Downloads.IsBootstrap(needed)) continue;
                // An updated package replaces its old mapping; do not count the old files as available.
                var available = candidates.TryGetValue(needed.Key, out var candidate) ? new[] { new Version(candidate.Id.Version) } :
                    mappings.Values.Where(p => p.Key.Equals(needed.Key, StringComparison.OrdinalIgnoreCase)).Select(p => new Version(p.Version));
                if (available.Any(v => v >= new Version(needed.Version))) continue;
                report.Entries.Add(item.Key + ": warning; declared dependency " + needed.FullName + " is missing or older. The authorized update will continue; no dependency will be downloaded automatically and BepInEx may refuse to load the mod.");
            }
        }
        internal static List<ModEntry> Inspect(string directory)
        {
            var result = new List<ModEntry>(); if (!Directory.Exists(directory)) return result;
            foreach (var entry in Directory.GetFileSystemEntries(directory))
            {
                SafePath.ReadSource(directory, Path.GetFileName(entry));
                if (Directory.Exists(entry)) { result.AddRange(Inspect(entry)); continue; }
                if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                AssemblyDefinition assembly;
                try { assembly = AssemblyDefinition.ReadAssembly(entry); } catch (BadImageFormatException) { continue; }
                using (assembly)
                    foreach (var type in Types(assembly.MainModule.Types))
                        foreach (var attribute in type.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"))
                        {
                            var values = attribute.ConstructorArguments;
                            if (values.Count != 3) throw new InvalidDataException("Invalid plugin metadata in " + entry);
                            result.Add(new ModEntry { Guid = (string)values[0].Value, Name = (string)values[1].Value, Version = (string)values[2].Value, LocalFile = entry });
                        }
            }
            return result;
        }
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types) { yield return type; foreach (var nested in Types(type.NestedTypes)) yield return nested; }
        }
    }
}
