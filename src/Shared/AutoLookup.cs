using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal static class AutoLookup
    {
        public static async Task<LookupReport> Run(IReadOnlyList<ModEntry> mods, string cache, IPackageLookup source, Func<string, ModEntry, bool> verifyArchive, CancellationToken token, string[]? priority = null)
        {
            Directory.CreateDirectory(cache);
            var report = new LookupReport { Status = "Complete", Mods = mods.Select(m => new LookupResult { Guid = m.Guid, Version = m.Version, Status = "No matching package metadata found; specify an exact download link if the package and plugin versions differ." }).ToList() };
            var errors = new List<string>();
            long budget = 512L * 1024 * 1024;
            var counted = new HashSet<string>();
            try
            {
            foreach (var repository in SourcePreferences.Parse(string.Join(",", priority ?? new[] { "hexium", "thunderstore" })).Concat(mods.Any(m => m.ManifestSource != null) ? new[] { "github" } : Array.Empty<string>()))
            {
                var remaining = mods.Where(m => report.Mods.Single(r => r.Guid == m.Guid).Package.Length == 0 && !report.Mods.Single(r => r.Guid == m.Guid).Status.StartsWith("Multiple packages", StringComparison.Ordinal)).ToArray();
                if (remaining.Length == 0) break;
                List<CatalogEntry> candidates;
                try { candidates = await source.Candidates(repository, remaining, token).ConfigureAwait(false); }
                catch (Exception error) when (!(error is OperationCanceledException)) { errors.Add(repository + ": " + error.Message); continue; }
                foreach (var mod in remaining)
                {
                    var matches = new HashSet<string>(StringComparer.Ordinal);
                    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                    // Registry/manifest identity and version choose trusted packages. Only fallback assets need full DLL verification.
                    var exactHint = repository == "github" ? null : AutoSetup.DllPackage(mod.LocalManifest?.ExactPackage(repository), mod);
                    if (exactHint == null && repository != "github" && !string.IsNullOrWhiteSpace(mod.DownloadUrl)) exactHint = PackageId.FromDownloadUrl(mod.DownloadUrl).FromRepository(repository).FullName;
                    var exactId = exactHint == null ? null : PackageId.Parse(exactHint);
                    var packages = candidates.Where(p => AutoSetup.Candidate(p, mod)).Where(p => exactId == null || (string.Equals(p.Owner, exactId.Owner, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Name, exactId.Name, StringComparison.OrdinalIgnoreCase))).Take(6).ToArray();
                    foreach (var package in packages)
                        foreach (var version in package.Versions.Where(v => v.IsActive != false && (repository == "github" || PolicyEngine.VersionsEqual(PackageId.Parse(v.FullName).Version, mod.LocalManifest?.Version ?? mod.Version))).OrderByDescending(v => mod.LocalPackageHints.Contains(v.FullName)).Take(12))
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                var id = PackageId.Parse(version.FullName).FromRepository(repository);
                                if (Downloads.IsBootstrap(id)) continue;
                                if (!id.Owner.Equals(package.Owner, StringComparison.OrdinalIgnoreCase) || !id.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)) continue;
                                if (repository != "github")
                                {
                                    // DLL metadata decides the expected version; manifests provide source identity only.
                                    if (!PolicyEngine.VersionsEqual(id.Version, mod.LocalManifest?.Version ?? mod.Version)) continue;
                                    matches.Add(id.FullName);
                                    continue;
                                }
                                if (!string.IsNullOrWhiteSpace(mod.DownloadUrl) || mod.ManifestSource?.Allows(id) != true) continue;
                                var archive = SafePath.Under(cache, id.CacheName);
                                if (!File.Exists(archive))
                                {
                                    if (budget <= 0) throw new IOException("Automatic lookup download budget reached.");
                                    var partial = archive + "." + Guid.NewGuid().ToString("N") + ".partial";
                                    try { await source.Download(id, partial, token).ConfigureAwait(false); File.Move(partial, archive); }
                                    finally { if (File.Exists(partial)) File.Delete(partial); }
                                }
                                if (counted.Add(archive)) budget -= new FileInfo(archive).Length;
                                if (budget < 0) throw new IOException("Automatic lookup download budget reached.");
                                if (verifyArchive(archive, mod)) { matches.Add(id.FullName); if (id.Repository == "github") hashes[id.FullName] = PackageArchive.AssetHash(archive, id); }
                            }
                            catch (Exception error) when (!(error is OperationCanceledException))
                            {
                                errors.Add(repository + "/" + package.Name + ": " + error.Message);
                            }
                        }
                    var entry = report.Mods.Single(r => r.Guid == mod.Guid);
                    if (matches.Count == 1) { entry.Package = matches.Single(); entry.AssetSha256 = hashes.TryGetValue(entry.Package, out var digest) ? digest : ""; entry.Status = repository == "github" ? "Verified manifest fallback DLL identity, version and SHA-256." : "Matched trusted repository package/manifest metadata; DLL inspection skipped."; }
                    else if (matches.Count > 1) entry.Status = "Multiple packages match the package metadata. Set an exact package or downloadUrl to choose the intended source.";
                }
            }
            }
            catch (OperationCanceledException) { report.Status = "Lookup timed out or was cancelled. Verified matches were retained; review unmatched entries and retry later."; return report; }
            if (errors.Count > 0) report.Status = "Completed with lookup errors: " + string.Join("; ", errors.Distinct().Take(8));
            return report;
        }
    }
}
