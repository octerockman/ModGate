using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModGate
{
    internal static class AutoSetup
    {
        public static string Normalize(string value) => Regex.Replace(value ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
        public static bool ServerOnlyName(string name) => Normalize(name).Contains("serverside");
        public static ServerManifest Reconcile(ServerManifest settings, IReadOnlyList<ModEntry> loaded, IEnumerable<string> exclusions)
        {
            settings = CanonicalEntries(settings, loaded);
            var result = ReconcileValues(ConfigFields.Runtime(settings), loaded, exclusions);
            ConfigFields.Preserve(settings, result);
            return result;
        }
        // One row per actual plugin GUID. Package/manifest names are aliases, never extra plugins.
        public static ServerManifest CanonicalEntries(ServerManifest settings, IReadOnlyList<ModEntry> loaded)
        {
            if (settings.Mods == null || settings.Mods.Count > Constants.MaxMods || settings.Mods.Any(m => m == null)) throw new System.IO.InvalidDataException("Invalid server mod list.");
            var copy = Json.CloneLocal(settings);
            var known = new HashSet<string>(loaded.Select(m => m.Guid), StringComparer.Ordinal);
            var rows = new List<Tuple<ModEntry, bool>>();
            foreach (var row in copy.Mods)
            {
                var guid = ConfigFields.Value(row.Guid);
                var exact = known.Contains(guid);
                if (!exact && !ConfigFields.Locked(row.Guid))
                {
                    var matches = loaded.Where(m => m.Guid != Constants.Guid && Alias(row, m)).ToArray();
                    if (matches.Length == 1) row.Guid = matches[0].Guid;
                }
                rows.Add(Tuple.Create(row, exact));
            }
            copy.Mods = rows.GroupBy(r => ConfigFields.Value(r.Item1.Guid), StringComparer.Ordinal).Select(g =>
            {
                var ordered = g.OrderBy(r => r.Item1.Automatic).ThenByDescending(r => r.Item2).Select(r => r.Item1).ToArray();
                var chosen = ordered[0];
                // Keep owner links when an older alias row is the only place they were entered.
                if (string.IsNullOrWhiteSpace(chosen.DownloadUrl) && string.IsNullOrWhiteSpace(chosen.NexusUrl))
                {
                    var links = ordered.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DownloadUrl) || !string.IsNullOrWhiteSpace(m.NexusUrl));
                    if (links != null) { chosen.DownloadUrl = links.DownloadUrl; chosen.NexusUrl = links.NexusUrl; }
                }
                return chosen;
            }).ToList();
            return copy;
        }
        private static bool Alias(ModEntry row, ModEntry dll)
        {
            var names = new[] { dll.Guid, dll.Name, dll.LocalManifest?.Name ?? "" }.Select(Normalize).Where(n => n.Length > 0).ToArray();
            return names.Contains(Normalize(ConfigFields.Value(row.Guid))) || names.Contains(Normalize(ConfigFields.Value(row.Name)));
        }
        public static string? DllPackageVersion(ModEntry mod)
        {
            if (!System.Version.TryParse(mod.Version, out var version) || version.Build < 0 || version.Revision > 0) return null;
            return version.Major + "." + version.Minor + "." + version.Build;
        }
        public static string? DllPackage(string? hint, ModEntry mod)
        {
            if (string.IsNullOrWhiteSpace(hint) || !PackageId.Allowed(hint!)) return null;
            var id = PackageId.Parse(hint!);
            return id.Repository == "github" ? null : id.FullName;
        }
        private static ServerManifest ReconcileValues(ServerManifest settings, IReadOnlyList<ModEntry> loaded, IEnumerable<string> exclusions)
        {
            if (settings.Schema != Constants.ManifestSchema) throw new System.IO.InvalidDataException("Unsupported server-mods.json schema.");
            var excluded = new HashSet<string>(exclusions.Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("#")), StringComparer.OrdinalIgnoreCase);
            var previous = settings.Mods.ToDictionary(m => m.Guid, StringComparer.Ordinal);
            var result = new ServerManifest { Schema = Constants.ManifestSchema, ServerName = settings.ServerName, AllowExtraClientMods = settings.AllowExtraClientMods, AllowNewerClientVersions = settings.AllowNewerClientVersions, AutoUpdateOnRestart = settings.AutoUpdateOnRestart, AutoUpdateAllMods = settings.AutoUpdateAllMods, AutoUpdateModGate = settings.AutoUpdateModGate, ModGateIncludePrereleases = settings.ModGateIncludePrereleases };
            foreach (var mod in loaded.Where(m => m.Guid != Constants.Guid))
            {
                previous.TryGetValue(mod.Guid, out var old);
                var optionalModule = loaded.Any(parent => DependencyRules.Of(parent).Any(d => d.Guid == mod.Guid));
                var side = old != null ? old.Side : ServerOnlyName(mod.Name) || excluded.Contains(mod.Guid) || excluded.Contains(mod.Name) ? "serverOnly" : optionalModule ? "optional" : "required";
                var package = old != null && PolicyEngine.VersionsEqual(old.Version, mod.Version) ? old.Package ?? "" : "";
                if (!string.IsNullOrEmpty(package) && !PackageId.Allowed(package)) package = "";
                var link = old?.DownloadUrl ?? "";
                var nexus = old?.NexusUrl ?? "";
                var observed = ObservedPackage(mod, package, link);
                if (!string.IsNullOrEmpty(observed)) package = observed;
                var origin = mod.LocalManifest?.GitOrigin();
                if (!string.IsNullOrWhiteSpace(package) && PackageId.Parse(package).Repository == "github")
                {
                    if (origin != null && old?.ManifestSource?.Digest == origin.Digest && old.ManifestSource.Verified(PackageId.Parse(package))) { origin.AssetId = old.ManifestSource.AssetId; origin.AssetSha256 = old.ManifestSource.AssetSha256; }
                    if (origin?.Verified(PackageId.Parse(package)) != true) package = "";
                }
                if (!string.IsNullOrWhiteSpace(nexus)) { NexusLinks.Validate(nexus); package = ""; }
                if (!string.IsNullOrWhiteSpace(link) && string.IsNullOrEmpty(package) && old != null && PolicyEngine.VersionsEqual(old.Version, mod.Version))
                {
                    var pinned = PackageId.FromDownloadUrl(link);
                    package = pinned.FullName;
                }
                result.Mods.Add(new ModEntry { Guid = mod.Guid, Name = mod.Name, Version = mod.Version, Side = side!, Package = package, DownloadUrl = link, NexusUrl = nexus, Automatic = old?.Automatic ?? true, AutoUpdate = old?.AutoUpdate, AllowNewerVersion = old?.AllowNewerVersion, LocalFile = mod.LocalFile, LocalManifest = mod.LocalManifest, Dependencies = mod.Dependencies, LocalPackageHints = mod.LocalPackageHints, ManifestSource = origin, LocalPackageObserved = !string.IsNullOrEmpty(observed) });
            }
            // Preserve explicit client-only requirements; remove vanished automatically discovered server mods.
            result.Mods.AddRange(settings.Mods.Where(m => (!m.Automatic || m.Side == "clientOnly") && loaded.All(l => l.Guid != m.Guid) && m.Guid != Constants.Guid));
            foreach (var entry in result.Mods)
            {
                entry.Package = entry.Package ?? "";
                if (!string.IsNullOrEmpty(entry.Package) && PackageId.Allowed(entry.Package) && PackageId.Parse(entry.Package).Repository == "github" && entry.LocalManifest == null) { entry.Package = ""; entry.ManifestSource = null; }
                entry.DownloadUrl = entry.DownloadUrl ?? ""; entry.NexusUrl = entry.NexusUrl ?? "";
                if (!string.IsNullOrEmpty(entry.Package) && !PackageId.Allowed(entry.Package)) entry.Package = "";
            }
            DependencyRules.Link(result.Mods);
            return result;
        }
        private static string ObservedPackage(ModEntry mod, string previous, string link)
        {
            var repositories = new List<string>();
            foreach (var hint in new[] { link, mod.Package, previous })
                if (!string.IsNullOrWhiteSpace(hint) && PackageId.Allowed(hint) && PackageId.Parse(hint).Repository != "github") repositories.Add(PackageId.Parse(hint).Repository);
            foreach (var repository in repositories.Concat(new[] { "hexium", "thunderstore" }).Distinct())
            {
                var exact = mod.LocalManifest?.ExactPackage(repository);
                if (exact != null) return DllPackage(exact, mod) ?? "";
            }
            if (!string.IsNullOrWhiteSpace(mod.Package) && PackageId.Allowed(mod.Package))
            {
                var id = PackageId.Parse(mod.Package);
                if (id.Repository != "github") return DllPackage(id.FullName, mod) ?? "";
            }
            if (!string.IsNullOrWhiteSpace(previous) && PackageId.Allowed(previous) && mod.LocalManifest != null)
            {
                var id = PackageId.Parse(previous);
                if (id.Repository != "github" && Normalize(id.Name) == Normalize(mod.LocalManifest.Name))
                    return PackageId.Parse((id.Repository == "hexium" ? "hexium:" : "") + id.Key + "-" + mod.LocalManifest.Version).FullName;
            }
            return "";
        }
        public static void ApplyLookup(ServerManifest settings, LookupReport report)
        {
            var updated = ConfigFields.Runtime(settings);
            ApplyLookupValues(updated, report);
            ConfigFields.Preserve(settings, updated);
            foreach (var mod in settings.Mods)
            {
                var refreshed = updated.Mods.Single(m => ConfigFields.Value(m.Guid) == ConfigFields.Value(mod.Guid));
                mod.Package = refreshed.Package; mod.ManifestSource = refreshed.ManifestSource;
            }
        }
        private static void ApplyLookupValues(ServerManifest settings, LookupReport report)
        {
            foreach (var mod in settings.Mods.Where(m => string.IsNullOrWhiteSpace(m.NexusUrl) && !m.LocalPackageObserved))
            {
                var match = report.Mods?.FirstOrDefault(r => r.Guid == mod.Guid && PolicyEngine.VersionsEqual(r.Version, mod.Version) && !string.IsNullOrWhiteSpace(r.Package) && PackageId.Allowed(r.Package));
                if (match == null) continue;
                var id = PackageId.Parse(match.Package);
                if (id.Repository != "github" && mod.LocalManifest != null && !PolicyEngine.VersionsEqual(id.Version, mod.LocalManifest.Version)) continue;
                if (!string.IsNullOrWhiteSpace(mod.DownloadUrl) && !PackageId.FromDownloadUrl(mod.DownloadUrl).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Repository == "github")
                {
                    if (mod.ManifestSource?.Allows(id) != true || !Regex.IsMatch(match.AssetSha256 ?? "", @"^[A-Fa-f0-9]{64}$")) continue;
                    mod.ManifestSource.AssetId = match.Package; mod.ManifestSource.AssetSha256 = match.AssetSha256 ?? "";
                }
                mod.Package = id.FullName;
            }
        }
        public static bool Candidate(CatalogEntry package, ModEntry mod)
        {
            var name = Normalize(package.Name);
            return mod.LocalPackageHints.Any(h => h.StartsWith(package.Owner + "-" + package.Name + "-", StringComparison.OrdinalIgnoreCase)) || (mod.LocalManifest != null && name == Normalize(mod.LocalManifest.Name)) || (package.PluginGuids != null && package.PluginGuids.Contains(mod.Guid)) || name.Length > 0 && (name == Normalize(mod.Name) || name == Normalize(mod.Guid.Split('.').Last()) ||
                (!string.IsNullOrEmpty(mod.LocalFile) && name == Normalize(System.IO.Path.GetFileNameWithoutExtension(mod.LocalFile))));
        }
    }
}
