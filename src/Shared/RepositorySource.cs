using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal sealed class RepositorySource : IPackageLookup, IUpdateSource, IDisposable
    {
        public Action<long, long?>? TransferProgress { get; set; }
        private readonly HttpClient client;
        private readonly List<ManifestOrigin> origins;
        public RepositorySource(HttpMessageHandler? handler = null, IEnumerable<ManifestOrigin>? origins = null)
        {
            this.origins = (origins ?? Array.Empty<ManifestOrigin>()).Take(Constants.MaxMods).ToList();
            client = new HttpClient(handler ?? NativeHttps.Handler()) { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimModGate/" + Constants.Version);
        }
        private static string Origin(string repository) => repository == "hexium" ? "https://hexium.gg" : "https://thunderstore.io";
        internal static bool Trusted(Uri uri, string repository)
        {
            if (repository == "github") return uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(uri.Host);
            return uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
                (repository == "hexium" ? uri.Host == "hexium.gg" || uri.Host == "cdn.hexium.gg" : repository == "thunderstore" && (uri.Host == "thunderstore.io" || uri.Host == "ccdn.thunderstore.io"));
        }
        public async Task<PackageId> Latest(PackageId id, CancellationToken token)
        {
            if (id.Repository == "github") throw new InvalidDataException("Manifest GitHub releases are a client lookup fallback, not a server auto-update source.");
            var listing = await Read<LatestPackage>(new Uri(Origin(id.Repository) + "/api/experimental/package/" + id.Owner + "/" + id.Name + "/"), id.Repository, Constants.MaxJson, token).ConfigureAwait(false);
            if (listing.Deprecated || listing.Latest == null || listing.Latest.IsActive == false) throw new InvalidDataException("Package is deprecated or has no latest release: " + id.Key);
            var latest = PackageId.Parse(listing.Latest.FullName).FromRepository(id.Repository);
            if (!latest.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Latest release returned a different package.");
            return latest;
        }
        public async Task<PackageMetadata> Metadata(PackageId id, CancellationToken token)
        {
            if (id.Repository == "github")
            {
                var approved = origins.Where(o => o.Allows(id)).ToArray();
                if (approved.Length == 0) throw new InvalidDataException("GitHub release is not authorized by a local/server mod manifest.");
                var release = await Read<GitRelease>(new Uri("https://api.github.com/repos/" + id.Owner + "/" + id.Name + "/releases/tags/" + Uri.EscapeDataString(id.Version)), "github", 4 * 1024 * 1024, token).ConfigureAwait(false);
                var asset = release.Assets?.SingleOrDefault(a => a.Name == id.Asset);
                if (release.Draft || release.Prerelease || release.Tag != id.Version || asset == null || !GitManifestRepository.TryParse(asset.Url, out var assetLink) || !assetLink!.Owner.Equals(id.Owner, StringComparison.OrdinalIgnoreCase) || !assetLink.Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase) || assetLink.Tag != id.Version || assetLink.Asset != id.Asset) throw new InvalidDataException("GitHub did not return the expected published release asset.");
                return new PackageMetadata { FullName = id.FullName, DownloadUrl = asset.Url, Dependencies = approved.SelectMany(o => o.Dependencies).Distinct().ToList() };
            }
            var url = new Uri(Origin(id.Repository) + "/api/experimental/package/" + id.Owner + "/" + id.Name + "/" + id.Version + "/");
            var result = await Read<PackageMetadata>(url, id.Repository, Constants.MaxJson, token).ConfigureAwait(false);
            if (result.FullName != id.RawFullName || result.Dependencies == null || result.Dependencies.Count > 128) throw new InvalidDataException("Invalid repository metadata for " + id.FullName);
            foreach (var dependency in result.Dependencies) if (PackageId.Parse(dependency).Repository == "github") throw new InvalidDataException("Repository dependencies cannot introduce GitHub links without manifest provenance.");
            return result;
        }
        public async Task Download(PackageId id, string destination, CancellationToken token)
        {
            Uri url;
            if (id.Repository == "hexium" || id.Repository == "github")
            {
                var metadata = await Metadata(id, token).ConfigureAwait(false);
                if (!Uri.TryCreate(metadata.DownloadUrl, UriKind.Absolute, out url!) || !Trusted(url, id.Repository)) throw new InvalidDataException("Repository returned an invalid download address.");
            }
            else url = new Uri(Origin(id.Repository) + "/package/download/" + id.Owner + "/" + id.Name + "/" + id.Version + "/");
            if (id.Repository == "github" && id.Asset.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destination)) File.Delete(destination);
                using (var response = await Get(url, "github", token).ConfigureAwait(false))
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = new FileStream(destination, FileMode.CreateNew))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                using (var output = archive.CreateEntry(id.Asset).Open()) await Copy(input, output, null, token).ConfigureAwait(false);
                VerifyAsset(id, destination);
                return;
            }
            if (id.Repository == "hexium") { await DownloadRanges(url, id.Repository, destination, token).ConfigureAwait(false); return; }
            await DownloadStream(url, id.Repository, destination, token).ConfigureAwait(false);
            if (id.Repository == "github") VerifyAsset(id, destination);
        }
        private void VerifyAsset(PackageId id, string file)
        {
            var pinned = origins.Where(o => o.Allows(id) && o.AssetId == id.FullName && !string.IsNullOrWhiteSpace(o.AssetSha256)).ToArray();
            if (pinned.Length == 0) return; // Server lookup has not pinned this candidate yet.
            var actual = PackageArchive.AssetHash(file, id);
            if (pinned.Any(o => !o.AssetSha256.Equals(actual, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("GitHub release contents changed after server verification. No profile install was scheduled.");
        }
        public async Task<List<CatalogEntry>> Candidates(string repository, IReadOnlyList<ModEntry> mods, CancellationToken token)
        {
            if (repository == "github") return await GitCandidates(mods, token).ConfigureAwait(false);
            if (repository != "thunderstore" && repository != "hexium") throw new InvalidDataException("Only Thunderstore and Hexium are enabled.");
            var result = new List<CatalogEntry>();
            var origin = Origin(repository);
            foreach (var mod in mods)
            {
                var hint = AutoSetup.DllPackage(mod.LocalManifest?.ExactPackage(repository), mod);
                if (hint == null) continue;
                try
                {
                    var id = PackageId.Parse(hint);
                    var metadata = await Metadata(id, token).ConfigureAwait(false);
                    result.Add(new CatalogEntry { Owner = id.Owner, Name = id.Name, PluginGuids = new List<string> { mod.Guid }, Versions = new List<PackageMetadata> { metadata } });
                }
                catch (PackageNotFoundException) { }
            }
            if (mods.All(m => result.Any(p => AutoSetup.Candidate(p, m)))) return result;
            if (repository == "hexium")
            {
                foreach (var mod in mods)
                    try
                    {
                        var package = await Read<CatalogEntry>(new Uri(origin + "/c/valheim/api/v1/package/guid/" + Uri.EscapeDataString(mod.Guid) + "/"), repository, 4 * 1024 * 1024, token).ConfigureAwait(false);
                        package.PluginGuids = new List<string> { mod.Guid }; result.Add(package);
                    }
                    catch (PackageNotFoundException) { }
                if (mods.All(m => result.Any(p => AutoSetup.Candidate(p, m)))) return result;
            }
            try
            {
                var chunks = await Read<string[]>(new Uri(origin + "/c/valheim/api/v1/package-listing-index/"), repository, Constants.MaxJson, token).ConfigureAwait(false);
                if (chunks.Length > 64) throw new InvalidDataException("Repository catalog has too many chunks.");
                foreach (var chunk in chunks)
                {
                    var entries = await Read<CatalogEntry[]>(new Uri(chunk), repository, 64 * 1024 * 1024, token).ConfigureAwait(false);
                    result.AddRange(entries.Where(p => mods.Any(m => AutoSetup.Candidate(p, m))));
                }
            }
            catch (PackageNotFoundException)
            {
                var entries = await Read<CatalogEntry[]>(new Uri(origin + "/c/valheim/api/v1/package/"), repository, 128 * 1024 * 1024, token).ConfigureAwait(false);
                result.AddRange(entries.Where(p => mods.Any(m => AutoSetup.Candidate(p, m))));
            }
            return result.GroupBy(p => p.Owner + "-" + p.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var entry = g.First(); entry.PluginGuids = g.SelectMany(p => p.PluginGuids ?? new List<string>()).Distinct().ToList(); entry.Versions = g.SelectMany(p => p.Versions).GroupBy(v => v.FullName, StringComparer.OrdinalIgnoreCase).Select(v => v.First()).ToList(); return entry;
            }).ToList();
        }
        private async Task<List<CatalogEntry>> GitCandidates(IReadOnlyList<ModEntry> mods, CancellationToken token)
        {
            var result = new List<CatalogEntry>();
            foreach (var mod in mods.Where(m => m.ManifestSource != null).Take(16))
            {
                var proof = mod.ManifestSource!;
                if (!GitManifestRepository.TryParse(proof.Website, out var repo)) continue;
                PackageId probe;
                try { probe = PackageId.Parse("github:" + repo!.Owner + "/" + repo.Name + "/" + (repo.Tag.Length > 0 ? repo.Tag : "v" + proof.Version) + "/" + (repo.Asset.Length > 0 ? repo.Asset : "probe.zip")); }
                catch (InvalidDataException) { continue; }
                if (!proof.Allows(probe) || !origins.Any(o => o.Digest == proof.Digest && o.Allows(probe))) continue;
                var tags = repo!.Tag.Length > 0 ? new[] { repo.Tag } : new[] { proof.Version, "v" + proof.Version, "V" + proof.Version };
                var entry = new CatalogEntry { Owner = repo.Owner, Name = repo.Name, PluginGuids = new List<string> { mod.Guid } };
                foreach (var tag in tags.Distinct())
                {
                    try
                    {
                        var release = await Read<GitRelease>(new Uri("https://api.github.com/repos/" + repo.Owner + "/" + repo.Name + "/releases/tags/" + Uri.EscapeDataString(tag)), "github", 4 * 1024 * 1024, token).ConfigureAwait(false);
                        if (release.Draft || release.Prerelease || release.Assets == null) continue;
                        foreach (var asset in release.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Take(12))
                        {
                            try
                            {
                                var id = PackageId.Parse("github:" + repo.Owner + "/" + repo.Name + "/" + release.Tag + "/" + asset.Name);
                                if (proof.Allows(id)) entry.Versions.Add(new PackageMetadata { FullName = id.FullName });
                            }
                            catch (InvalidDataException) { }
                        }
                    }
                    catch (PackageNotFoundException) { }
                }
                if (entry.Versions.Count > 0) result.Add(entry);
            }
            return result;
        }
        private async Task<T> Read<T>(Uri url, string repository, int limit, CancellationToken token)
        {
            using (var response = await Get(url, repository, token).ConfigureAwait(false))
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var memory = new MemoryStream())
            {
                await Copy(input, memory, limit, token).ConfigureAwait(false);
                var bytes = memory.ToArray();
                // Catalog blobs can be gzip files without a Content-Encoding response header.
                if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                {
                    using (var packed = new MemoryStream(bytes))
                    using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
                    using (var decoded = new MemoryStream())
                    {
                        await Copy(gzip, decoded, limit, token).ConfigureAwait(false); bytes = decoded.ToArray();
                    }
                }
                return Json.Read<T>(System.Text.Encoding.UTF8.GetString(bytes), limit, limit > Constants.MaxJson ? 2000000 : 50000);
            }
        }
        private async Task DownloadStream(Uri url, string repository, string destination, CancellationToken token)
        {
            var checkpoint = destination + ".resume";
            var validator = File.Exists(checkpoint) ? File.ReadAllText(checkpoint) : "";
            var offset = File.Exists(destination) && validator.Length > 0 ? new FileInfo(destination).Length : 0;
            using (var response = await Get(url, repository, token, offset > 0 ? (long?)offset : null, null, validator).ConfigureAwait(false))
            {
                var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (append && response.Content.Headers.ContentRange?.From != offset) throw new InvalidDataException("Invalid resumed download range.");
                var tag = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R") ?? "";
                if (append && tag != validator) throw new InvalidDataException("Download changed while resuming. Clear failed downloads and retry.");
                File.WriteAllText(checkpoint, tag);
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = new FileStream(destination, append ? FileMode.Append : FileMode.Create))
                {
                    var expected = response.Content.Headers.ContentLength;
                    var initial = append ? offset : 0;
                    var buffer = new byte[65536]; int count; long received = 0;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    { await file.WriteAsync(buffer, 0, count, token).ConfigureAwait(false); received += count; TransferProgress?.Invoke(initial + received, expected.HasValue ? initial + expected.Value : (long?)null); }
                    if (expected.HasValue && received != expected.Value) throw new IOException("Download was interrupted before all bytes arrived.");
                }
            }
            File.Delete(checkpoint);
        }
        private async Task DownloadRanges(Uri url, string repository, string destination, CancellationToken token)
        {
            const int chunk = 256 * 1024;
            using (var first = await Get(url, repository, token, 0, chunk - 1).ConfigureAwait(false))
            {
                if (first.StatusCode != HttpStatusCode.PartialContent)
                {
                    using (var file = new FileStream(destination, FileMode.Create))
                    using (var input = await first.Content.ReadAsStreamAsync().ConfigureAwait(false)) await Copy(input, file, null, token).ConfigureAwait(false);
                    return;
                }
                var total = first.Content.Headers.ContentRange?.Length;
                if (!total.HasValue || total.Value <= 0) throw new InvalidDataException("Invalid Hexium download length.");
                var validator = first.Headers.ETag?.ToString() ?? "";
                var checkpoint = destination + ".resume";
                var done = new HashSet<long>();
                if (validator.Length > 0 && File.Exists(checkpoint) && File.Exists(destination) && new FileInfo(destination).Length == total.Value)
                {
                    try
                    {
                        var saved = Json.ReadLocal<RangeCheckpoint>(checkpoint);
                        if (saved.Validator == validator && saved.Length == total.Value) done.UnionWith(saved.Done.Where(n => n >= 0 && n < total.Value && n % chunk == 0));
                    }
                    catch (Exception error) when (error is IOException || error is System.Runtime.Serialization.SerializationException) { done.Clear(); }
                }
                using (var file = new FileStream(destination, done.Count > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    file.SetLength(total.Value);
                    var firstEnd = Math.Min(total.Value - 1, chunk - 1);
                    ValidateRange(first, 0, firstEnd, total.Value);
                    using (var input = await first.Content.ReadAsStreamAsync().ConfigureAwait(false)) await Copy(input, file, chunk, token).ConfigureAwait(false);
                    if (file.Position != firstEnd + 1) throw new InvalidDataException("Truncated first download chunk.");
                    done.Add(0); long next = firstEnd + 1; long completedBytes = done.Sum(n => Math.Min(chunk, total.Value - n));
                    void Save() { file.Flush(); Json.Save(checkpoint, new RangeCheckpoint { Validator = validator, Length = total.Value, Done = done.ToList() }); }
                    Save();
                    async Task Worker()
                    {
                        while (true)
                        {
                            token.ThrowIfCancellationRequested(); long start, end;
                            lock (file)
                            {
                                while (next < total.Value && done.Contains(next)) next += Math.Min(chunk, total.Value - next);
                                start = next; if (start >= total.Value) return;
                                end = start + Math.Min(chunk, total.Value - start) - 1; next = end + 1;
                            }
                            using (var response = await Get(url, repository, token, start, end, validator).ConfigureAwait(false))
                            {
                                ValidateRange(response, start, end, total.Value);
                                if (validator.Length > 0 && response.Headers.ETag?.ToString() != validator) throw new InvalidDataException("Hexium download changed during resumption.");
                                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var memory = new MemoryStream())
                                {
                                    await Copy(input, memory, chunk, token).ConfigureAwait(false);
                                    if (memory.Length != end - start + 1) throw new IOException("Truncated download chunk.");
                                    var bytes = memory.ToArray();
                                    lock (file) { file.Position = start; file.Write(bytes, 0, bytes.Length); if (done.Add(start)) completedBytes += bytes.Length; if (done.Count % 16 == 0) Save(); TransferProgress?.Invoke(completedBytes, total.Value); }
                                }
                            }
                        }
                    }
                    try { await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Worker())).ConfigureAwait(false); }
                    finally { lock (file) Save(); }
                }
                File.Delete(checkpoint);
            }
        }
        [System.Runtime.Serialization.DataContract] private sealed class RangeCheckpoint
        {
            [System.Runtime.Serialization.DataMember] public string Validator = "";
            [System.Runtime.Serialization.DataMember] public long Length;
            [System.Runtime.Serialization.DataMember] public List<long> Done = new List<long>();
        }
        private static void ValidateRange(HttpResponseMessage response, long start, long end, long total)
        {
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != total) throw new InvalidDataException("Repository returned an inconsistent download range.");
        }
        private async Task<HttpResponseMessage> Get(Uri url, string repository, CancellationToken token, long? start = null, long? end = null, string validator = "")
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (!Trusted(url, repository)) throw new InvalidDataException("Repository address or redirect is outside its trusted HTTPS hosts.");
                HttpResponseMessage response;
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
                    if (start.HasValue && validator.Length > 0) request.Headers.TryAddWithoutValidation("If-Range", validator);
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); throw new PackageNotFoundException(url.AbsolutePath); }
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
                {
                    var location = response.Headers.Location; response.Dispose();
                    if (location == null) throw new InvalidDataException("Missing repository redirect.");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location); continue;
                }
                try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
            }
            throw new IOException("Too many repository redirects.");
        }
        private static async Task Copy(Stream input, Stream output, long? limit, CancellationToken token)
        {
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                total = checked(total + count); if (limit.HasValue && total > limit.Value) throw new InvalidDataException("Repository response exceeds its size limit.");
                await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
            }
        }
        public void Dispose() => client.Dispose();
    }
}
