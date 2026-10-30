using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal static class PartialSetup
    {
        public static async Task<List<PackageId>> Resolve(Policy policy, Comparison comparison, IPackageSource source, CancellationToken token, IReadOnlyList<ModEntry> reusable, List<string> issues, ISet<string>? packageOptIns = null, Action<DependencySuggestion>? suggest = null)
        {
            PolicyEngine.ValidatePolicy(policy);
            var pins = policy.Mods.Where(m => !string.IsNullOrWhiteSpace(m.Package)).Select(m => Downloads.ReusedPackage(policy, PackageId.Parse(m.Package), reusable)).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in policy.Mods.Where(m => !string.IsNullOrWhiteSpace(m.Package)).Select(m => PackageId.Parse(m.Package)).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                if (group.Select(p => p.FullName).Distinct().Count() > 1) issues.Add(group.Key + ": server pins conflicting versions; some requirements may remain unmet.");
            var queue = new Queue<PackageId>();
            foreach (var mod in comparison.Missing.Concat(comparison.WrongVersion).Concat(policy.Mods.Where(m => m.Guid != Constants.Guid && reusable.Any(r => r.Guid == m.Guid) && !string.IsNullOrEmpty(m.Package))).GroupBy(m => m.Guid).Select(g => g.First()))
            {
                if (string.IsNullOrWhiteSpace(mod.Package)) { issues.Add(mod.Name + ": omitted; no download mapping is available."); continue; }
                queue.Enqueue(PackageId.Parse(mod.Package));
            }
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var suggestedRequests = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);
            var dependencyPasses = 0;
            var result = new List<PackageId>();
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (selected.Count >= 128) { issues.Add("Remaining dependencies omitted; package limit reached."); break; }
                var id = queue.Dequeue();
                if (!pins.ContainsKey(id.Key) && suggestedRequests.TryGetValue(id.Key, out var preferred)) id = preferred;
                id = Downloads.ReusedPackage(policy, id, reusable);
                if (source is ProfilePackageSource profileSource) id = profileSource.Actual(id);
                if (Downloads.IsBootstrap(id)) continue;
                if (policy.ForbiddenPackages.Contains(id.Key, StringComparer.OrdinalIgnoreCase)) { issues.Add(id.FullName + ": omitted; server-only package."); continue; }
                if (selected.TryGetValue(id.Key, out var existing))
                {
                    if (existing == id.FullName || pins.ContainsKey(id.Key) || new System.Version(PackageId.Parse(existing).Version) >= new System.Version(id.Version)) continue;
                    selected.Remove(id.Key); result.RemoveAll(p => p.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase));
                }
                if (++dependencyPasses > 512) { issues.Add("Dependency metadata kept introducing new versions. Remaining suggestions omitted; review the selected packages."); break; }
                var reuse = Downloads.CanReusePackage(policy, id, reusable);
                try
                {
                    PackageMetadata metadata;
                    try
                    {
                        var localManifest = reuse ? reusable.FirstOrDefault(m => !string.IsNullOrEmpty(m.Package) && PackageId.Parse(m.Package).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase))?.LocalManifest : null;
                        if (localManifest != null && PolicyEngine.VersionsEqual(localManifest.Version, id.Version)) metadata = new PackageMetadata { FullName = id.RawFullName, Dependencies = localManifest.Dependencies };
                        else if (source is ProfilePackageSource setup) { var resolved = await setup.Resolve(id, !pins.ContainsKey(id.Key), token).ConfigureAwait(false); id = resolved.Item1; metadata = resolved.Item2; }
                        else metadata = await source.Metadata(id, token).ConfigureAwait(false);
                    }
                    catch (PackageNotFoundException) when (id.Repository != "github")
                    {
                        // Explicit server pins stay on their selected source.
                        if (pins.ContainsKey(id.Key)) throw;
                        id = id.FromRepository(id.Repository == "hexium" ? "thunderstore" : "hexium");
                        metadata = await source.Metadata(id, token).ConfigureAwait(false);
                    }
                    selected.Add(id.Key, id.FullName); if (!reuse) result.Add(id);
                    foreach (var dependency in metadata.Dependencies)
                    {
                        PackageId needed;
                        try { needed = DependencyRules.PackageSuggestion(id, dependency); }
                        catch (InvalidDataException) { issues.Add(id.FullName + ": invalid dependency package declaration ignored."); continue; }
                        if (Downloads.IsBootstrap(needed)) continue;
                        if (policy.ForbiddenPackages.Contains(needed.Key, StringComparer.OrdinalIgnoreCase)) { issues.Add(id.FullName + ": suggested dependency " + needed.FullName + " excluded; server-only package."); continue; }
                        if (pins.TryGetValue(needed.Key, out var pin))
                        {
                            if (source is ProfilePackageSource tracked) pin = tracked.Actual(pin);
                            if (new System.Version(pin.Version) < new System.Version(needed.Version)) issues.Add(id.FullName + ": dependency " + needed.FullName + " requires a newer version than the server pin; continuing with " + pin.FullName);
                            queue.Enqueue(pin);
                        }
                        else
                        {
                            if (needed.Repository == "github") { issues.Add(id.FullName + ": dependency release link lacks its own verified source manifest; install it manually if needed."); continue; }
                            suggest?.Invoke(new DependencySuggestion { Package = needed.FullName, Parent = id.FullName });
                            if (policy.AllowExtras && DependencyRules.PackageOptedIn(needed, packageOptIns))
                            {
                                var choice = DependencyRules.PreferredSuggestion(needed, packageOptIns);
                                if (!DependencyRules.ReusesSuggestion(choice, reusable) && (!suggestedRequests.TryGetValue(choice.Key, out var previous) || new System.Version(choice.Version) > new System.Version(previous.Version)))
                                { suggestedRequests[choice.Key] = choice; queue.Enqueue(choice); }
                            }
                            else issues.Add(id.FullName + ": suggested dependency " + needed.FullName + " not downloaded; " + (policy.AllowExtras ? "its download was not checked." : "server does not allow unapproved extra mods."));
                        }
                    }
                }
                catch (Exception error) when (!(error is OperationCanceledException)) { issues.Add(id.FullName + (result.Any(p => p.FullName == id.FullName) ? ": dependency resolution incomplete; " : ": omitted; ") + error.Message); }
            }
            return result;
        }

        public static async Task<PendingInstall> Stage(string root, IReadOnlyList<PackageId> packages, IPackageSource source, Action<string> progress, CancellationToken token, List<string> issues, string? operationId = null)
        {
            var pending = await Downloads.Stage(root, Array.Empty<PackageId>(), source, progress, token, operationId).ConfigureAwait(false);
            foreach (var package in packages)
            {
                token.ThrowIfCancellationRequested();
                var id = Guid.NewGuid().ToString("N");
                try
                {
                    var part = await Downloads.Stage(root, new[] { package }, source, progress, token, id).ConfigureAwait(false);
                    Merge(root, pending, part);
                }
                catch (PackageResolutionChangedException) { throw; }
                catch (Exception error) when (!(error is OperationCanceledException)) { issues.Add(package.FullName + ": omitted after staging failure; " + error.Message); }
                finally { Discard(root, id); }
            }
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
            return pending;
        }

        public static void CopyLocal(string root, IReadOnlyList<ModEntry> wanted, IReadOnlyList<ModEntry> installed, PendingInstall pending, CancellationToken token, List<string> issues)
        {
            foreach (var group in wanted.GroupBy(m => Path.GetDirectoryName(m.LocalFile) ?? m.LocalFile, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var part = new PendingInstall { Id = Guid.NewGuid().ToString("N") };
                Directory.CreateDirectory(SafePath.Under(root, "ModGate/staging/" + part.Id + "/packages"));
                try { ManualMods.Stage(root, group.ToArray(), installed, part, token); Merge(root, pending, part); }
                catch (Exception error) when (!(error is OperationCanceledException)) { issues.Add(string.Join(", ", group.Select(m => m.Name)) + ": installed copies omitted; " + error.Message); }
                finally { Discard(root, part.Id); }
            }
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
        }

        private static void Merge(string root, PendingInstall target, PendingInstall part)
        {
            var from = SafePath.Under(root, "ModGate/staging/" + part.Id);
            var to = SafePath.Under(root, "ModGate/staging/" + target.Id);
            StageVerifier.VerifyStage(from, part);
            StageVerifier.VerifyStage(to, target);
            foreach (var file in part.Files)
            {
                var destination = SafePath.Under(to, file.Path);
                if (File.Exists(destination) && SafePath.Hash(destination) != file.Sha256) throw new InvalidDataException("Conflicting staged file: " + file.Path);
            }
            var copied = new List<string>();
            var previousFiles = target.Files; var packageCount = target.Packages.Count; var localCount = target.LocalPackages.Count;
            try
            {
                foreach (var file in part.Files)
                {
                    var destination = SafePath.Under(to, file.Path);
                    if (File.Exists(destination)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    copied.Add(destination); File.Copy(SafePath.Under(from, file.Path), destination, false);
                }
                foreach (var package in part.Packages) Directory.CreateDirectory(SafePath.Under(to, "packages/" + PackageId.Parse(package).Key));
                target.Packages.AddRange(part.Packages);
                target.LocalPackages.AddRange(part.LocalPackages ?? new List<string>());
                ProfileConfigs.RefreshReceipt(to, target);
            }
            catch
            {
                foreach (var file in copied) if (File.Exists(file)) File.Delete(file);
                target.Files = previousFiles;
                target.Packages.RemoveRange(packageCount, target.Packages.Count - packageCount);
                target.LocalPackages.RemoveRange(localCount, target.LocalPackages.Count - localCount);
                throw;
            }
        }

        internal static void Discard(string root, string id)
        {
            var stage = SafePath.Under(root, "ModGate/staging/" + id);
            if (!Directory.Exists(stage)) return;
            foreach (var file in Directory.GetFileSystemEntries(stage, "*", SearchOption.AllDirectories)) SafePath.Under(stage, file.Substring(stage.Length + 1));
            Directory.Delete(stage, true);
        }
    }
}
