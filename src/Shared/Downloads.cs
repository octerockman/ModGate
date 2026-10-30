using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal interface IPackageSource
    {
        Task<PackageMetadata> Metadata(PackageId id, CancellationToken token);
        Task Download(PackageId id, string destination, CancellationToken token);
    }

    internal static class Downloads
    {
        public static async Task<List<PackageId>> Resolve(Policy policy, Comparison comparison, IPackageSource source, CancellationToken token, IReadOnlyList<ModEntry>? reusable = null, ISet<string>? packageOptIns = null, Action<DependencySuggestion>? suggest = null, Action<string>? warn = null)
        {
            PolicyEngine.ValidatePolicy(policy);
            var result = new List<PackageId>();
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var suggestedRequests = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);
            var dependencyPasses = 0;
            var pins = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in policy.Mods.Where(m => !string.IsNullOrEmpty(m.Package)))
            {
                var pin = PackageId.Parse(mod.Package);
                if (pins.TryGetValue(pin.Key, out var previous) && previous.Version != pin.Version) throw new InvalidDataException("The server pins two versions of " + pin.Key);
                pins[pin.Key] = pin;
            }
            foreach (var pin in pins.ToArray()) pins[pin.Key] = ReusedPackage(policy, pin.Value, reusable);
            var queue = new Queue<Tuple<string, bool>>(comparison.Missing.Concat(comparison.WrongVersion).Concat(policy.Mods.Where(m => m.Guid != Constants.Guid && reusable != null && reusable.Any(r => r.Guid == m.Guid) && !string.IsNullOrWhiteSpace(m.Package))).GroupBy(m => m.Guid).Select(g => g.First()).Select(m =>
                Tuple.Create(string.IsNullOrWhiteSpace(m.Package) ? throw new InvalidDataException("No verified download is available yet for " + m.Name + ". Reconnect after the server lookup completes, or review config/ModGate/lookup-report.json on the server.") : m.Package, false)));
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var request = queue.Dequeue();
                var id = PackageId.Parse(request.Item1);
                if (request.Item2 && suggestedRequests.TryGetValue(id.Key, out var preferred)) id = preferred;
                id = ReusedPackage(policy, id, reusable);
                if (source is ProfilePackageSource profileSource) id = profileSource.Actual(id);
                if (IsBootstrap(id)) continue;
                if (policy.ForbiddenPackages.Contains(id.Key, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("A client mod depends on a server-only package: " + id.Key + ". The server owner must correct the manifest.");
                if (selected.TryGetValue(id.Key, out var version))
                {
                    if (version == id.FullName || pins.ContainsKey(id.Key) || new System.Version(PackageId.Parse(version).Version) >= new System.Version(id.Version)) continue;
                    // Both requests were explicitly opted in. Replace an earlier suggestion
                    // with the highest declared version instead of producing two packages.
                    selected.Remove(id.Key); result.RemoveAll(p => p.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase));
                }
                if (++dependencyPasses > 512) { warn?.Invoke("Dependency metadata kept introducing new versions. Remaining suggestions were omitted; review the selected packages."); break; }
                var reuse = CanReusePackage(policy, id, reusable);
                PackageMetadata metadata;
                try
                {
                    var localManifest = reuse ? reusable?.FirstOrDefault(m => !string.IsNullOrEmpty(m.Package) && PackageId.Parse(m.Package).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase))?.LocalManifest : null;
                    if (localManifest != null && PolicyEngine.VersionsEqual(localManifest.Version, id.Version)) metadata = new PackageMetadata { FullName = id.RawFullName, Dependencies = localManifest.Dependencies };
                    else if (source is ProfilePackageSource setup) { var resolved = await setup.Resolve(id, request.Item2, token).ConfigureAwait(false); id = resolved.Item1; metadata = resolved.Item2; }
                    else metadata = await source.Metadata(id, token).ConfigureAwait(false);
                }
                catch (PackageNotFoundException) when (request.Item2)
                {
                    id = id.FromRepository(id.Repository == "hexium" ? "thunderstore" : "hexium");
                    metadata = await source.Metadata(id, token).ConfigureAwait(false);
                }
                selected.Add(id.Key, id.FullName);
                if (selected.Count > 128) throw new InvalidDataException("Too many package dependencies.");
                foreach (var dependency in metadata.Dependencies)
                {
                    PackageId dependencyId;
                    try { dependencyId = DependencyRules.PackageSuggestion(id, dependency); }
                    catch (InvalidDataException) { warn?.Invoke(id.FullName + ": an invalid dependency package declaration was ignored."); continue; }
                    if (IsBootstrap(dependencyId)) continue;
                    if (policy.ForbiddenPackages.Contains(dependencyId.Key, StringComparer.OrdinalIgnoreCase))
                    { warn?.Invoke(id.FullName + ": suggested dependency " + dependencyId.FullName + " was excluded; it is a server-only package."); continue; }
                    if (pins.TryGetValue(dependencyId.Key, out var pin))
                    {
                        if (new System.Version(pin.Version) < new System.Version(dependencyId.Version))
                            warn?.Invoke(id.FullName + ": declares " + dependencyId.Key + " >= " + dependencyId.Version + ", but the selected server package is " + pin.FullName + ". Setup can continue; the mod may not load.");
                        queue.Enqueue(Tuple.Create(pin.FullName, false));
                    }
                    else
                    {
                        if (dependencyId.Repository == "github") { warn?.Invoke(id.FullName + ": a dependency release link lacks its own verified source manifest; install it manually if needed."); continue; }
                        suggest?.Invoke(new DependencySuggestion { Package = dependencyId.FullName, Parent = id.FullName });
                        if (policy.AllowExtras && DependencyRules.PackageOptedIn(dependencyId, packageOptIns))
                        {
                            var choice = DependencyRules.PreferredSuggestion(dependencyId, packageOptIns);
                            if (!DependencyRules.ReusesSuggestion(choice, reusable) && (!suggestedRequests.TryGetValue(choice.Key, out var previous) || new System.Version(choice.Version) > new System.Version(previous.Version)))
                            { suggestedRequests[choice.Key] = choice; queue.Enqueue(Tuple.Create(choice.FullName, true)); }
                        }
                        else warn?.Invoke(id.FullName + ": suggested dependency " + dependencyId.FullName + " was not downloaded. " + (policy.AllowExtras ? "Check its suggestion to include it." : "This server does not allow unapproved extra mods."));
                    }
                }
                if (!reuse) result.Add(id);
            }
            return result;
        }

        internal static bool CanReusePackage(Policy policy, PackageId id, IReadOnlyList<ModEntry>? reusable)
        {
            if (reusable == null) return false;
            var declared = policy.Mods.Where(m => !string.IsNullOrWhiteSpace(m.Package) && PackageId.Parse(m.Package).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            return declared.Length > 0 && declared.All(m => ReusedPackage(policy, PackageId.Parse(m.Package), reusable).FullName == id.FullName && reusable.Any(l => l.Guid == m.Guid && !string.IsNullOrWhiteSpace(l.LocalFile) && PolicyEngine.AcceptsVersion(policy, m, l.Version)));
        }

        internal static PackageId ReusedPackage(Policy policy, PackageId pin, IReadOnlyList<ModEntry>? reusable)
        {
            if (reusable == null || pin.Repository == "github") return pin;
            var declared = policy.Mods.Where(m => !string.IsNullOrWhiteSpace(m.Package) && PackageId.Parse(m.Package).Key.Equals(pin.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (declared.Length == 0 || !declared.All(m => reusable.Any(l => l.Guid == m.Guid && !string.IsNullOrEmpty(l.LocalFile) && PolicyEngine.AcceptsVersion(policy, m, l.Version))) ||
                !declared.Any(m => reusable.Any(l => l.Guid == m.Guid && !PolicyEngine.VersionsEqual(l.Version, m.Version)))) return pin;
            var observed = declared.Select(m => reusable.First(l => l.Guid == m.Guid).LocalManifest?.ExactPackage(pin.Repository)).Distinct().ToArray();
            if (observed.Length != 1 || string.IsNullOrEmpty(observed[0])) return pin;
            var actual = PackageId.Parse(observed[0]!);
            return actual.Key.Equals(pin.Key, StringComparison.OrdinalIgnoreCase) && new System.Version(actual.Version) >= new System.Version(pin.Version) ? actual : pin;
        }

        internal static bool IsBootstrap(PackageId id) => id.Key.Equals("denikson-BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase) || id.Name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase) || id.Name.Equals("BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase);

#if !MODGATE_PRELOADER
        public static async Task<PendingInstall> Stage(string root, IReadOnlyList<PackageId> packages, IPackageSource source, Action<string> progress, CancellationToken token, string? operationId = null)
        {
            if (operationId != null && (!Guid.TryParseExact(operationId, "N", out _) || Directory.Exists(SafePath.Under(root, "ModGate/staging/" + operationId)))) throw new InvalidDataException("Invalid staging operation.");
            if (Profiles.Pending(root)) throw new InvalidOperationException("A profile change is already scheduled. Restart Valheim first.");
            var pending = new PendingInstall { Id = operationId ?? System.Guid.NewGuid().ToString("N"), Packages = packages.Select(p => p.FullName).ToList() };
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(System.IO.Path.Combine(stage, "packages"));
            try
            {
            foreach (var id in packages)
            {
                progress("Downloading " + id.FullName);
                var archive = await DownloadCache.Get(root, id, source, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                try { Extract(archive, stage, id, token: token); }
                catch (InvalidDataException) { DownloadCache.Invalidate(root, id); throw; }
                progress("Prepared " + id.FullName);
            }
            foreach (var path in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                pending.Files.Add(new FileDigest { Path = path.Substring(stage.Length + 1).Replace('\\', '/'), Sha256 = SafePath.Hash(path) });
            return pending;
            }
            catch { DownloadCache.DiscardStage(root, pending.Id); throw; }
        }

#endif
        public static long Extract(string archive, string stage, PackageId package, long remainingBytes = long.MaxValue, bool validateManifest = true, CancellationToken token = default)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            using (var payload = PackageArchive.Open(archive, token))
            {
                var zip = payload.Zip;
                foreach (var manifest in validateManifest ? payload.Manifests : new List<PackageManifest>())
                    if (manifest.Version != package.Version.TrimStart('v', 'V') || (package.Repository != "github" && AutoSetup.Normalize(manifest.Name) != AutoSetup.Normalize(package.Name))) throw new InvalidDataException("Archive manifest identity/version does not match the selected package: " + package.FullName);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    var directory = name.EndsWith("/");
                    var normalized = directory ? name.TrimEnd('/') : name;
                    SafePath.Under(stage, normalized);
                    if (((ExternalAttributes(entry) >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Package contains a symbolic link.");
                    if (directory) continue;
                    expanded = checked(expanded + entry.Length);
                    if (expanded > remainingBytes) throw new InvalidDataException("Package expands beyond the size limit.");
                    var relative = MapEntry(name, package);
                    if (relative == null) continue;
                    var destination = SafePath.Under(stage, relative);
                    if (!seen.Add(destination) || File.Exists(destination)) throw new InvalidDataException("Packages contain conflicting files: " + relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew)) CopyPayload(input, output, token);
                }
                var retained = payload.Manifests.LastOrDefault();
                if (retained != null && !string.IsNullOrEmpty(retained.RawText))
                {
                    var metadataFile = SafePath.Under(stage, "packages/" + package.Key + "/manifest.json");
                    if (File.Exists(metadataFile)) throw new InvalidDataException("Package contains conflicting root manifest files.");
                    expanded += System.Text.Encoding.UTF8.GetByteCount(retained.RawText);
                    if (expanded > remainingBytes) throw new InvalidDataException("Package metadata expands beyond its size limit.");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(metadataFile)!);
                    File.WriteAllText(metadataFile, retained.RawText, new System.Text.UTF8Encoding(false));
                }
            }
            Directory.CreateDirectory(SafePath.Under(stage, "packages/" + package.Key));
            return expanded;
        }

        internal static void CopyPayload(Stream input, Stream output, CancellationToken token)
        {
            var buffer = new byte[65536]; int count;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) return;
                output.Write(buffer, 0, count);
            }
        }

        private static int ExternalAttributes(ZipArchiveEntry entry)
        {
#if NETSTANDARD2_0
            // Valheim exposes this runtime property although the preloader's 2.0 reference contract omits it.
            var property = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
            if (property == null) throw new NotSupportedException("This runtime cannot validate archive file attributes.");
            return (int)property.GetValue(entry, null)!;
#else
            return entry.ExternalAttributes;
#endif
        }

        private static string? MapEntry(string name, PackageId package)
        {
            if (!name.Contains("/") && new[] { "manifest.json", "readme.md", "changelog.md", "icon.png", "license", "license.txt", "license.md" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(8);
            if (name.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            {
                if (!name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsupported configuration file: " + name);
                if (name.StartsWith("config/ModGate/", StringComparison.OrdinalIgnoreCase) || name.Equals("config/studio.valheim.modgate.cfg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A package cannot change ModGate's own policy.");
                return name;
            }
            if (name.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(8);
            if (new[] { "patchers/", "core/", "doorstop_libs/" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                new[] { ".exe", ".bat", ".cmd", ".ps1", ".sh" }.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
                name.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This package needs a loader, patcher, or root-level installer. Install it manually: " + package.FullName);
            return "packages/" + package.Key + "/" + name;
        }
    }
}
