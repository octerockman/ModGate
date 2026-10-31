using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;

namespace ModGate
{
    internal static class Inventory
    {
        private static Task<LookupReport>? lookup;
        private static LookupReport resolved = new LookupReport();
        private static bool cacheLoaded;
        private static DateTime nextLookup = DateTime.MinValue;
        private static BepInEx.Logging.ManualLogSource? manifestLog;
        public static List<ModEntry> Loaded()
        {
            var mods = Chainloader.PluginInfos.Values.Select(info => new ModEntry
        {
            Guid = info.Metadata.GUID, Name = info.Metadata.Name, Version = info.Metadata.Version.ToString(), LocalFile = info.Location,
            Dependencies = info.Dependencies.Select(d => new PluginDependency { Guid = d.DependencyGUID, Optional = (d.Flags & BepInDependency.DependencyFlags.SoftDependency) != 0, MinimumVersion = d.MinimumVersion?.ToString() ?? "" }).ToList(),
            Package = GuessPackage(info.Location), LocalManifest = LocalManifestReader.Read(info.Location, Paths.PluginPath), LocalConfigFiles = ConfigFiles(info.Instance as BaseUnityPlugin)
        }).OrderBy(m => m.Guid, StringComparer.Ordinal).ToList();
            ManifestCandidates.Enrich(mods); return mods;
        }

        private static List<string> ConfigFiles(BaseUnityPlugin? plugin)
        {
            if (plugin == null) return new List<string>();
            var root = Path.GetFullPath(Paths.ConfigPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(plugin.Config.ConfigFilePath);
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? new List<string> { file.Substring(root.Length).Replace('\\', '/') } : new List<string>();
        }
        public static void SaveConfigFiles()
        {
            foreach (var plugin in Chainloader.PluginInfos.Values.Select(i => i.Instance as BaseUnityPlugin).Where(i => i != null && i.Info.Metadata.GUID != Constants.Guid)) plugin!.Config.Save();
        }

        private static string GuessPackage(string dll, string? pluginRoot = null)
        {
            pluginRoot = pluginRoot ?? Paths.PluginPath;
            var current = Path.GetDirectoryName(dll);
            while (current != null && current.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase))
            {
                try { return PackageId.Parse(Path.GetFileName(current)).FullName; }
                catch (InvalidDataException) { current = Path.GetDirectoryName(current); }
            }
            return "";
        }

        public static Policy ServerPolicy()
        {
            var loaded = Loaded();
            var folder = Path.Combine(Paths.ConfigPath, "ModGate");
            var path = Path.Combine(folder, "server-mods.json");
            Directory.CreateDirectory(folder);
            var excludePath = Path.Combine(folder, "server-only.txt");
            if (!File.Exists(excludePath)) File.WriteAllText(excludePath, "# Add a server-only plugin GUID or exact display name on each line.\n# Names containing Server Side, Server-side or Serverside are detected automatically.\n");
            Policy policy;
            try
            {
                var settings = File.Exists(path) ? ServerConfig.Load(path, Paths.BepInExRootPath) : new ServerManifest();
                var detected = ServerInstalledClientMods(settings, loaded);
                LocalManifestReader.ApplyServerManifests(detected, Paths.PluginPath, Paths.ConfigPath,
                    message => (manifestLog ??= BepInEx.Logging.Logger.CreateLogSource("ModGate Manifests")).LogWarning(message));
                ManifestCandidates.Enrich(detected);
                settings = AutoSetup.CanonicalEntries(settings, detected);
                PolicyEngine.ValidateInventory(ConfigFields.Runtime(settings).Mods);
                var effective = AutoSetup.Reconcile(settings, detected, File.ReadAllLines(excludePath));
                var reportPath = Path.Combine(folder, "lookup-report.json");
                if (!cacheLoaded)
                {
                    cacheLoaded = true;
                    try { if (File.Exists(reportPath)) resolved = Json.Read<LookupReport>(File.ReadAllText(reportPath)); } catch { resolved = new LookupReport(); }
                }
                PollLookups();
                AutoSetup.ApplyLookup(effective, resolved);
                // Persist generated metadata even when only a dependency declaration changed.
                // It is deliberately omitted from the owner-facing server-mods.json.
                ServerConfig.Save(path, Paths.BepInExRootPath, effective);
                var operational = ConfigFields.Runtime(effective);
                var missing = operational.Mods.Where(m => m.Side != "serverOnly" && string.IsNullOrWhiteSpace(m.NexusUrl) && string.IsNullOrWhiteSpace(m.Package) && !string.IsNullOrEmpty(m.LocalFile)).ToArray();
                if (missing.Length > 0 && lookup == null && DateTime.UtcNow >= nextLookup)
                {
                    nextLookup = DateTime.UtcNow.AddMinutes(10);
                    Json.Save(reportPath, new LookupReport { Status = "Searching Hexium and Thunderstore in the background. Reconnect after lookup completes.", Mods = resolved.Mods ?? new System.Collections.Generic.List<LookupResult>() }, readable: true);
                    var cache = Path.Combine(Paths.BepInExRootPath, "ModGate", "lookup-cache");
                    var priority = SourcePreferences.Load(Paths.BepInExRootPath);
                    var forbidden = operational.Mods.Where(m => m.Side == "serverOnly").Select(m => m.Guid).ToArray();
                    lookup = Task.Run(async () =>
                    {
                        using (var source = new RepositorySource(origins: missing.Select(m => m.ManifestSource).Where(m => m != null).Cast<ManifestOrigin>()))
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
                            return await AutoLookup.Run(missing, cache, source, (archive, mod) => ArchiveMatches(archive, mod, forbidden), deadline.Token, priority).ConfigureAwait(false);
                    });
                }
                policy = PolicyEngine.Publish(effective, loaded);
            }
            catch (Exception error) { policy = new Policy { Error = "The server owner needs to configure ModGate: " + error.Message, ServerName = "Valheim server" }; }
            var idPath = Path.Combine(folder, "server-id.txt");
            if (!File.Exists(idPath)) File.WriteAllText(idPath, System.Guid.NewGuid().ToString("N"));
            policy.ServerId = File.ReadAllText(idPath).Trim();
            return policy;
        }
        internal static List<ModEntry> ServerInstalledClientMods(ServerManifest settings, IReadOnlyList<ModEntry> loaded, string? pluginRoot = null)
        {
            pluginRoot = pluginRoot ?? Paths.PluginPath;
            var wanted = ClientOnlyRules.Guids(settings);
            wanted.ExceptWith(loaded.Select(m => m.Guid));
            var result = loaded.ToList();
            if (wanted.Count == 0 || !Directory.Exists(pluginRoot)) return result;
            var found = Directory.GetFiles(pluginRoot, "*.dll", SearchOption.AllDirectories)
                .SelectMany(file => InspectFiles(new[] { file }, false)).Where(m => wanted.Contains(m.Guid))
                .GroupBy(m => m.Guid).Select(g => g.OrderByDescending(m => new System.Version(m.Version)).First()).ToList();
            foreach (var mod in found) { mod.Package = GuessPackage(mod.LocalFile, pluginRoot); mod.LocalManifest = LocalManifestReader.Read(mod.LocalFile, pluginRoot); }
            ManifestCandidates.Enrich(found); result.AddRange(found); return result;
        }
        public static bool PollLookups()
        {
            if (lookup == null || !lookup.IsCompleted) return false;
            if (lookup.Status == TaskStatus.RanToCompletion)
            {
                var fresh = lookup.Result;
                fresh.Mods.AddRange((resolved.Mods ?? new System.Collections.Generic.List<LookupResult>()).Where(old => fresh.Mods.All(m => m.Guid != old.Guid)));
                resolved = fresh;
            }
            else resolved.Status = "Lookup did not finish: " + (lookup.Exception?.GetBaseException().Message ?? "Request timed out. Check connectivity and reconnect to retry.");
            lookup = null;
            Json.Save(Path.Combine(Paths.ConfigPath, "ModGate", "lookup-report.json"), resolved, readable: true);
            return true;
        }
        internal static bool ArchiveMatches(string archive, ModEntry mod, string[] forbidden)
        {
            var installedHash = SafePath.Hash(mod.LocalFile);
            var found = false;
            using (var payload = PackageArchive.Open(archive))
                foreach (var entry in payload.Zip.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    if (entry.Length > 64L * 1024 * 1024) throw new InvalidDataException("Lookup candidate contains an oversized DLL.");
                    using (var input = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        input.CopyTo(memory); memory.Position = 0;
                        AssemblyDefinition assembly;
                        try { assembly = AssemblyDefinition.ReadAssembly(memory); } catch (BadImageFormatException) { continue; }
                        using (assembly)
                        {
                            foreach (var type in AllTypes(assembly.MainModule.Types))
                                foreach (var attr in type.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"))
                                {
                                    var args = attr.ConstructorArguments;
                                    var guid = (string)args[0].Value; var name = (string)args[1].Value; var version = (string)args[2].Value;
                                    if (forbidden.Contains(guid) || (AutoSetup.ServerOnlyName(name) && guid != mod.Guid)) return false;
                                    if (guid != mod.Guid || !PolicyEngine.VersionsEqual(version, mod.Version)) continue;
                                    memory.Position = 0;
                                    using (var sha = SHA256.Create())
                                        if (BitConverter.ToString(sha.ComputeHash(memory)).Replace("-", "").ToLowerInvariant() == installedHash) found = true;
                                }
                        }
                    }
                }
            return found;
        }

        public static List<ModEntry> InspectStaged(string stage)
        {
            return InspectStagedPackage(Path.Combine(stage, "packages"));
        }
        private static List<ModEntry> InspectStagedPackage(string packageRoot)
        {
            return InspectFiles(Directory.Exists(packageRoot) ? Directory.GetFiles(packageRoot, "*.dll", SearchOption.AllDirectories) : Array.Empty<string>(), true);
        }
        public static List<ModEntry> InspectProfilePayload(string path) => InspectFiles(ProfileEditing.PayloadFiles(path).Where(f => Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase)), false);
        public static List<ProfileEditGroup> ProfileGroups(string root, ProfileCatalog catalog, string slot) => ProfileEditing.Read(root, catalog, slot, InspectProfilePayload);
        public static List<ModEntry> InspectSupportPayload(string path)
        {
            var mods = InspectProfilePayload(path);
            var boundary = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var mod in mods)
            {
                mod.Package = GuessPackage(mod.LocalFile, boundary);
                mod.LocalManifest = LocalManifestReader.Read(mod.LocalFile, boundary);
                if (mod.Package.Length == 0 && mod.LocalManifest != null)
                {
                    var page = CreatorSupport.Page(mod.LocalManifest.Website);
                    var repository = page?.Host == "Hexium" ? "hexium" : page?.Host == "Thunderstore" ? "thunderstore" : null;
                    if (repository != null) mod.Package = mod.LocalManifest.ExactPackage(repository) ?? "";
                }
            }
            return mods;
        }
        private static List<ModEntry> InspectFiles(IEnumerable<string> files, bool rejectConnector)
        {
            var entries = new List<ModEntry>();
            using var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(BepInEx.Paths).Assembly.Location));
            foreach (var file in files)
            {
                AssemblyDefinition assembly;
                try { assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver }); }
                catch (BadImageFormatException) { continue; } // Native libraries are not BepInEx plugins.
                using (assembly)
                    foreach (var type in AllTypes(assembly.MainModule.Types))
                        foreach (var attr in type.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"))
                        {
                            var args = attr.ConstructorArguments;
                            if (args.Count != 3) throw new InvalidDataException("Unsupported plugin metadata in " + Path.GetFileName(file));
                            entries.Add(new ModEntry { Guid = (string)args[0].Value, Name = (string)args[1].Value, Version = (string)args[2].Value, LocalFile = file, Dependencies = type.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").Select(a => new PluginDependency { Guid = (string)a.ConstructorArguments[0].Value, Optional = a.ConstructorArguments.Count > 1 && a.ConstructorArguments[1].Value is int flags && (flags & 2) != 0, MinimumVersion = a.ConstructorArguments.Count > 1 && a.ConstructorArguments[1].Value is string minimum ? minimum : "" }).ToList() });
                        }
            }
            PolicyEngine.ValidateInventory(entries);
            if (rejectConnector && entries.Any(m => m.Guid == Constants.Guid)) throw new InvalidDataException("Update ModGate itself manually on both server and client.");
            return entries;
        }
        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var child in AllTypes(type.NestedTypes)) yield return child;
            }
        }

        public static void FilterPartialStage(string root, Policy policy, PendingInstall pending, ISet<string> selected, List<string> issues)
        {
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            var packageRoot = SafePath.Under(stage, "packages");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in Directory.GetDirectories(packageRoot).OrderBy(f => f, StringComparer.Ordinal))
            {
                var key = Path.GetFileName(folder);
                try
                {
                    foreach (var path in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)) SafePath.Under(stage, path.Substring(stage.Length + 1));
                    // Inspect one isolated package using the same plugin/identity validation as normal setup.
                    var mods = InspectStagedPackage(folder);
                    if (mods.Any(m => policy.ForbiddenPluginGuids.Contains(m.Guid) || (AutoSetup.ServerOnlyName(m.Name) && policy.Mods.All(w => w.Guid != m.Guid)))) throw new InvalidDataException("contains a server-only plugin");
                    if (mods.Any(m => policy.Mods.Any(w => w.Guid == m.Guid && w.Side == "optional") ? !selected.Contains(m.Guid) : policy.Mods.All(w => w.Guid != m.Guid) && !selected.Contains(m.Guid))) throw new InvalidDataException("contains an unchecked or undeclared plugin");
                    if (mods.Any(m => seen.Contains(m.Guid))) throw new InvalidDataException("contains a duplicate plugin identity");
                    foreach (var mod in mods) seen.Add(mod.Guid);
                }
                catch (Exception failure)
                {
                    // Only generated staging folders can be removed; original profiles are untouched.
                    SafePath.Under(stage, "packages/" + key);
                    foreach (var path in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)) SafePath.Under(stage, path.Substring(stage.Length + 1));
                    Directory.Delete(folder, true);
                    pending.Packages.RemoveAll(p => PackageId.Parse(p).Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    pending.LocalPackages.RemoveAll(p => p.Equals(key, StringComparison.OrdinalIgnoreCase));
                    issues.Add(key + ": omitted; " + failure.Message);
                }
            }
            ProfileConfigs.RefreshReceipt(stage, pending);
        }

        public static List<ModEntry> CompleteProfileProposal(string root, Policy policy, PendingInstall pending, bool allowIncomplete = false, Action<string>? warn = null)
        {
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            var downloaded = InspectStaged(stage);
            if (downloaded.Any(m => policy.ForbiddenPluginGuids.Contains(m.Guid) || (AutoSetup.ServerOnlyName(m.Name) && policy.Mods.All(w => w.Guid != m.Guid)))) throw new InvalidDataException("The selected packages include a server-only plugin. Correct the package selection on the server.");
            var simulated = downloaded.Concat(new[] { new ModEntry { Guid = Constants.Guid, Name = "Valheim ModGate", Version = Constants.Version, Side = "required" } }).ToList();
            var remaining = PolicyEngine.Compare(policy, simulated);
            foreach (var warning in DependencyRules.Warnings(simulated)) warn?.Invoke(warning);
            if (!remaining.Matches && !allowIncomplete)
            {
                var details = remaining.Missing.Select(m => "Missing: " + m.Name + " " + m.Version + " [" + m.Guid + "]")
                    .Concat(remaining.WrongVersion.Select(m => "Version mismatch: " + m.Name + " [" + m.Guid + "] — server requests " + m.Version + ", prepared " + simulated.First(l => l.Guid == m.Guid).Version))
                    .Concat(remaining.Extra.Select(m => "Extra: " + m.Name + " " + m.Version + " [" + m.Guid + "]"));
                throw new InvalidDataException("These packages would still leave a mismatch:" + Environment.NewLine + string.Join(Environment.NewLine, details));
            }
            return simulated;
        }
    }
}
