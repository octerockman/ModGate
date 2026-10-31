using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModGate
{
    internal sealed class CreatorLink
    {
        public string Host = "", Url = "";
    }
    internal sealed class CreatorMod
    {
        public string Id = "", Name = "", Author = "";
        public bool ModGate, Bascinet;
        public readonly List<CreatorLink> Links = new List<CreatorLink>();
        public readonly HashSet<string> Profiles = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Versions = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Icons = new List<string>();
    }
    internal sealed class CreatorSupportResult
    {
        public List<CreatorMod> Mods = new List<CreatorMod>();
        public int Skipped;
    }
    internal static class CreatorSupport
    {
        private sealed class Known
        {
            public string Package, Name, Nexus, Git;
            public bool Thunderstore, Hexium;
            public Known(string package, string name, string nexus, string git, bool thunderstore = true, bool hexium = true)
            { Package = package; Name = name; Nexus = nexus; Git = git; Thunderstore = thunderstore; Hexium = hexium; }
        }
        private static readonly Known[] Catalog = {
            new Known("ModGate", "ModGate", "4725", "ModGate", false),
            new Known("OctoPack", "OctoPack", "4412", "OctoPack"),
            new Known("OctoPack_addon_Lantern", "OctoPack Lantern", "4428", "OctoPack-Lantern"),
            new Known("OctoArsenal", "Octo Arsenal", "4510", "OctoArsenal"),
            new Known("OctoPack_Camping_Kit", "OctoPack Camping Kit", "4726", "OctoPack-Camping-Kit"),
            new Known("", "Octo tweaks", "4534", "", false, false),
            new Known("", "Lantern Arm (legacy)", "4487", "", false, false)
        };
        private sealed class Installed
        {
            public ModEntry Mod = null!;
            public string Slot = "", Folder = "";
        }
        internal static CreatorMod Pinned()
        {
            var result = new CreatorMod { Id = Constants.Guid, Name = "ModGate", Author = "Studio Bascinet", ModGate = true, Bascinet = true };
            AddKnown(result, Catalog[0]); return result;
        }
        // A page destination, never an API endpoint or a vote URL. Drop queries/download paths.
        internal static CreatorLink? Page(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 600 || value.IndexOfAny(new[] { '\\', '%', '\r', '\n' }) >= 0 ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0) return null;
            var path = uri.AbsolutePath;
            Match match;
            if ((uri.Host == "nexusmods.com" || uri.Host == "www.nexusmods.com") && (match = Regex.Match(path, @"^/valheim/mods/([1-9][0-9]*)/?$")).Success)
                return new CreatorLink { Host = "Nexus Mods", Url = "https://www.nexusmods.com/valheim/mods/" + match.Groups[1].Value };
            if (uri.Host == "thunderstore.io" && (match = Regex.Match(path, @"^/(?:c/valheim/p|package)/([A-Za-z0-9_]+)/([A-Za-z0-9_]+)(?:/[0-9]+\.[0-9]+\.[0-9]+)?/?$")).Success)
                return new CreatorLink { Host = "Thunderstore", Url = "https://thunderstore.io/c/valheim/p/" + match.Groups[1].Value + "/" + match.Groups[2].Value + "/" };
            if ((uri.Host == "valheim.hexium.gg" || uri.Host == "hexium.gg") && (match = Regex.Match(path, @"^/(?:mods|package|c/valheim/p)/([A-Za-z0-9_]+)/([A-Za-z0-9_]+)(?:/[0-9]+\.[0-9]+\.[0-9]+)?/?$")).Success)
                return new CreatorLink { Host = "Hexium", Url = "https://valheim.hexium.gg/mods/" + match.Groups[1].Value + "/" + match.Groups[2].Value };
            if (uri.Host == "github.com" && (match = Regex.Match(path, @"^/([A-Za-z0-9_-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$")).Success && !new[] { "login", "settings", "users", "orgs", "apps", "features", "marketplace", "sponsors", "topics", "collections", "search", "explore", "notifications" }.Contains(match.Groups[1].Value.ToLowerInvariant()) && match.Groups[2].Value != "." && match.Groups[2].Value != "..")
                return new CreatorLink { Host = "GitHub", Url = "https://github.com/" + match.Groups[1].Value + "/" + match.Groups[2].Value };
            return null;
        }
        private static PackageId? Package(string? value) { try { return string.IsNullOrEmpty(value) ? null : PackageId.Parse(ConfigFields.Value(value)); } catch (InvalidDataException) { return null; } }
        private static void Link(CreatorMod target, CreatorLink? link)
        {
            if (link != null && !target.Links.Any(l => l.Url.Equals(link.Url, StringComparison.OrdinalIgnoreCase))) target.Links.Add(link);
        }
        private static void AddKnown(CreatorMod result, Known known)
        {
            Link(result, Page("https://www.nexusmods.com/valheim/mods/" + known.Nexus));
            if (known.Thunderstore) Link(result, Page("https://thunderstore.io/c/valheim/p/Bascinet/" + known.Package + "/"));
            if (known.Hexium) Link(result, Page("https://valheim.hexium.gg/mods/Bascinet/" + known.Package));
            if (known.Git.Length > 0) Link(result, Page("https://github.com/octerockman/" + known.Git));
        }
        private static List<CreatorLink> Evidence(ModEntry mod)
        {
            var links = new List<CreatorLink>();
            foreach (var value in new[] { mod.NexusUrl, mod.LocalManifest?.Website, mod.ManifestSource?.Website }) { var link = Page(value); if (link != null) links.Add(link); }
            var package = Package(mod.Package) ?? Package(mod.DownloadUrl);
            if (package != null)
            {
                var value = package.Repository == "github" ? "https://github.com/" + package.Owner + "/" + package.Name : package.Repository == "hexium" ? "https://valheim.hexium.gg/mods/" + package.Owner + "/" + package.Name : "https://thunderstore.io/c/valheim/p/" + package.Owner + "/" + package.Name + "/";
                var link = Page(value); if (link != null) links.Add(link);
            }
            return links;
        }
        private static Known? MatchKnown(PackageId? package, List<CreatorLink> links)
            => Catalog.Skip(1).FirstOrDefault(k => k.Package.Length > 0 && package != null && package.Repository != "github" && package.Owner.Equals("Bascinet", StringComparison.OrdinalIgnoreCase) && package.Name.Equals(k.Package, StringComparison.OrdinalIgnoreCase) ||
                links.Any(l => l.Url.Equals("https://www.nexusmods.com/valheim/mods/" + k.Nexus, StringComparison.OrdinalIgnoreCase) || k.Git.Length > 0 && l.Url.Equals("https://github.com/octerockman/" + k.Git, StringComparison.OrdinalIgnoreCase)));
        internal static CreatorSupportResult Read(string root, ProfileCatalog catalog, bool allProfiles, Func<string, List<ModEntry>> inspect, CancellationToken token)
        {
            var result = new CreatorSupportResult(); var installed = new List<Installed>();
            var slots = allProfiles ? new[] { "offline" }.Concat(catalog.Servers.Select(s => s.Slot)).Distinct() : new[] { catalog.Active };
            foreach (var slot in slots)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var branch = ProfileEditing.Plugins(catalog, slot); var path = SafePath.Under(root, branch);
                    var entries = Directory.Exists(path) ? Directory.GetFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal).ToList() : new List<string>();
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = branch + "/" + Path.GetFileName(entry);
                        if (Path.GetFileName(entry) == "ValheimModGate.Active" && Directory.Exists(entry))
                        {
                            try { foreach (var child in Directory.GetDirectories(SafePath.Under(root, relative)).OrderBy(p => p, StringComparer.Ordinal)) ReadPayload(relative + "/" + Path.GetFileName(child)); }
                            catch (Exception error) when (FileError(error)) { result.Skipped++; }
                        }
                        else if (Directory.Exists(entry) || Path.GetExtension(entry).Equals(".dll", StringComparison.OrdinalIgnoreCase)) ReadPayload(relative);
                    }
                    foreach (var group in ProfileEditing.Disabled(catalog, slot)) ReadPayload(ProfileEditing.DisabledPath(slot, group.PluginPath));
                }
                catch (Exception error) when (FileError(error)) { result.Skipped++; }

                void ReadPayload(string relative)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var payload = SafePath.ReadSource(root, relative);
                        if (!File.Exists(payload) && !Directory.Exists(payload)) return;
                        var saved = slot == "offline" ? catalog.OfflineMods : catalog.Servers.Single(s => s.Slot == slot).Mods;
                        foreach (var actual in inspect(payload))
                        {
                            token.ThrowIfCancellationRequested();
                            if (string.IsNullOrEmpty(actual.LocalFile) || !File.Exists(actual.LocalFile)) continue;
                            var file = Path.GetFullPath(actual.LocalFile); var basePath = Directory.Exists(payload) ? payload.TrimEnd('\\', '/') + Path.DirectorySeparatorChar : payload;
                            if (Directory.Exists(payload) ? !file.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) : !file.Equals(payload, StringComparison.OrdinalIgnoreCase)) continue;
                            SafePath.ReadSource(root, file.Substring(Path.GetFullPath(root).TrimEnd('\\', '/').Length + 1));
                            var copy = Json.CloneLocal(actual); copy.LocalFile = actual.LocalFile; copy.LocalManifest = actual.LocalManifest;
                            var metadata = saved?.FirstOrDefault(m => m.Guid == actual.Guid);
                            if (copy.Package.Length == 0) copy.Package = metadata?.Package ?? "";
                            if (copy.NexusUrl.Length == 0) copy.NexusUrl = metadata?.NexusUrl ?? "";
                            if (copy.ManifestSource == null) copy.ManifestSource = metadata?.ManifestSource;
                            if (copy.DownloadUrl.Length == 0) copy.DownloadUrl = metadata?.DownloadUrl ?? "";
                            installed.Add(new Installed { Mod = copy, Slot = slot, Folder = Directory.Exists(payload) ? payload : "" });
                        }
                    }
                    catch (Exception error) when (FileError(error)) { result.Skipped++; }
                }
            }
            var byId = new Dictionary<string, CreatorMod>(StringComparer.OrdinalIgnoreCase) { [Constants.Guid] = Pinned() };
            var assignments = installed.Select(i => { var p = Package(i.Mod.Package) ?? Package(i.Mod.DownloadUrl); var links = Evidence(i.Mod); var known = MatchKnown(p, links) ?? (i.Mod.LocalManifest?.Namespace.Equals("Bascinet", StringComparison.OrdinalIgnoreCase) == true ? Catalog.Skip(1).FirstOrDefault(k => k.Package.Length > 0 && k.Package == i.Mod.LocalManifest.Name) : null); return new { Item = i, Package = p, Links = links, Known = known, Key = i.Mod.Guid == Constants.Guid ? Constants.Guid : known != null ? "bascinet:" + known.Nexus : p != null ? p.Repository + ":" + p.Owner + "/" + p.Name : !string.IsNullOrEmpty(i.Mod.LocalManifest?.Namespace) ? "manifest:" + i.Mod.LocalManifest!.Namespace + "/" + i.Mod.LocalManifest.Name : "guid:" + i.Mod.Guid }; }).ToList();
            foreach (var found in assignments)
            {
                token.ThrowIfCancellationRequested(); var item = found.Item; var key = found.Key;
                if (key.StartsWith("guid:", StringComparison.Ordinal))
                {
                    var same = assignments.Where(a => a.Item.Mod.Guid == item.Mod.Guid && !a.Key.StartsWith("guid:", StringComparison.Ordinal)).Select(a => a.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (same.Length == 1) key = same[0];
                }
                if (!byId.TryGetValue(key, out var aggregate))
                {
                    var author = found.Package?.Owner ?? item.Mod.LocalManifest?.Namespace ?? "";
                    aggregate = new CreatorMod { Id = key, Name = found.Known?.Name ?? item.Mod.LocalManifest?.Name ?? item.Mod.Name, Author = found.Known != null || author.Equals("Bascinet", StringComparison.OrdinalIgnoreCase) ? "Studio Bascinet" : author, Bascinet = found.Known != null || author.Equals("Bascinet", StringComparison.OrdinalIgnoreCase) };
                    if (aggregate.Name.Length == 0) aggregate.Name = item.Mod.Guid;
                    byId.Add(key, aggregate);
                }
                aggregate.Profiles.Add(item.Slot); aggregate.Versions.Add(item.Mod.Version);
                if (!aggregate.ModGate) { foreach (var link in found.Links) Link(aggregate, link); if (found.Known != null) AddKnown(aggregate, found.Known); }
                foreach (var icon in Icons(root, item.Folder, item.Mod.LocalFile)) if (!aggregate.Icons.Contains(icon)) aggregate.Icons.Add(icon);
            }
            foreach (var mod in byId.Values) mod.Links.Sort((a, b) => Array.IndexOf(new[] { "Nexus Mods", "Thunderstore", "Hexium", "GitHub" }, a.Host).CompareTo(Array.IndexOf(new[] { "Nexus Mods", "Thunderstore", "Hexium", "GitHub" }, b.Host)));
            result.Mods = byId.Values.OrderBy(m => m.ModGate ? 0 : m.Bascinet ? 1 : 2).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            return result;
        }
        private static bool FileError(Exception error) => error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is System.Runtime.Serialization.SerializationException || error is BadImageFormatException;
        private static IEnumerable<string> Icons(string root, string folder, string dll)
        {
            if (folder.Length == 0) yield break; // Never borrow a shared plugins/icon.png for a loose DLL.
            var directory = Path.GetDirectoryName(dll);
            for (var depth = 0; directory != null && depth < 5 && (directory.Equals(folder, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(folder.TrimEnd('\\','/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); depth++, directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, "icon.png");
                try { if (File.Exists(SafePath.Under(root, candidate.Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length + 1)))) { } else continue; }
                catch (Exception error) when (FileError(error)) { continue; }
                yield return candidate;
            }
        }
        internal static byte[]? IconBytes(string root, string path)
        {
            try
            {
                var prefix = Path.GetFullPath(root).TrimEnd('\\','/') + Path.DirectorySeparatorChar;
                path = Path.GetFullPath(path); if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                path = SafePath.Under(root, path.Substring(prefix.Length));
                using (var file = File.OpenRead(path))
                {
                    const int max = 1024 * 1024; if (file.Length < 33 || file.Length > max) return null;
                    var bytes = new byte[(int)file.Length]; var offset = 0;
                    while (offset < bytes.Length) { var read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) return null; offset += read; }
                    if (file.ReadByte() != -1 || !bytes.Take(8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }) || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82) return null;
                    long width = 0, height = 0; for (var i = 16; i < 20; i++) width = width * 256 + bytes[i]; for (var i = 20; i < 24; i++) height = height * 256 + bytes[i];
                    return width >= 1 && width <= 512 && height >= 1 && height <= 512 ? bytes : null;
                }
            }
            catch (Exception error) when (FileError(error)) { return null; }
        }
    }
}
