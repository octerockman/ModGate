using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    // Server startup uses the operating system transport without initializing managed networking.
    internal sealed class ServerPackageSource : IUpdateSource, IDisposable
    {
        private readonly Func<SystemRequest, CancellationToken, Task<SystemResponse>> send;
        public ServerPackageSource(Func<SystemRequest, CancellationToken, Task<SystemResponse>>? transport = null)
        { send = transport ?? SystemHttps.Send; }

        private static string Origin(string repository) => repository == "hexium" ? "https://hexium.gg" : "https://thunderstore.io";
        private static void ValidateSource(PackageId id)
        {
            if (id.Repository != "thunderstore" && id.Repository != "hexium")
                throw new InvalidDataException("Only Thunderstore and Hexium are server auto-update sources.");
        }
        internal static bool Trusted(Uri url, string repository) => url.IsAbsoluteUri && url.Scheme == "https" && url.Port == 443 && url.UserInfo.Length == 0 &&
            (repository == "hexium" ? url.Host == "hexium.gg" || url.Host == "cdn.hexium.gg" : repository == "thunderstore" && (url.Host == "thunderstore.io" || url.Host == "ccdn.thunderstore.io"));

        public async Task<PackageId> Latest(PackageId id, CancellationToken token)
        {
            ValidateSource(id);
            var listing = await Read<LatestPackage>(new Uri(Origin(id.Repository) + "/api/experimental/package/" + id.Owner + "/" + id.Name + "/"), id.Repository, token).ConfigureAwait(false);
            if (listing.Deprecated || listing.Latest == null || listing.Latest.IsActive == false) throw new InvalidDataException("Package is deprecated or has no latest release: " + id.Key);
            var latest = PackageId.Parse(listing.Latest.FullName);
            ValidateSource(latest);
            latest = latest.FromRepository(id.Repository);
            if (!latest.Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Latest release returned a different package.");
            return latest;
        }
        public async Task<PackageMetadata> Metadata(PackageId id, CancellationToken token)
        {
            ValidateSource(id);
            var url = new Uri(Origin(id.Repository) + "/api/experimental/package/" + id.Owner + "/" + id.Name + "/" + id.Version + "/");
            var result = await Read<PackageMetadata>(url, id.Repository, token).ConfigureAwait(false);
            if (result.FullName != id.RawFullName || result.Dependencies == null || result.Dependencies.Count > 128) throw new InvalidDataException("Invalid repository metadata for " + id.FullName);
            foreach (var dependency in result.Dependencies) ValidateSource(PackageId.Parse(dependency));
            return result;
        }
        public async Task Download(PackageId id, string destination, CancellationToken token)
        {
            ValidateSource(id);
            if (id.Repository == "hexium")
            {
                var metadata = await Metadata(id, token).ConfigureAwait(false);
                if (!Uri.TryCreate(metadata.DownloadUrl, UriKind.Absolute, out var url) || !Trusted(url, id.Repository)) throw new InvalidDataException("Repository returned an invalid download address.");
                await DownloadRanges(url, id.Repository, destination, token).ConfigureAwait(false);
            }
            else await DownloadStream(new Uri(Origin(id.Repository) + "/package/download/" + id.Owner + "/" + id.Name + "/" + id.Version + "/"), id.Repository, destination, token).ConfigureAwait(false);
        }

        private async Task<T> Read<T>(Uri url, string repository, CancellationToken token) where T : class
        {
            using (var response = await Get(url, repository, token).ConfigureAwait(false))
            using (var memory = new MemoryStream())
            {
                await Copy(response.Body, memory, Constants.MaxJson, response.ContentLength, token).ConfigureAwait(false);
                var bytes = memory.ToArray();
                if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                {
                    using (var packed = new MemoryStream(bytes))
                    using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
                    using (var decoded = new MemoryStream())
                    {
                        await Copy(gzip, decoded, Constants.MaxJson, null, token).ConfigureAwait(false);
                        bytes = decoded.ToArray();
                    }
                }
                return Json.Read<T>(Encoding.UTF8.GetString(bytes), Constants.MaxJson) ?? throw new InvalidDataException("Repository returned empty metadata.");
            }
        }
        private async Task DownloadStream(Uri url, string repository, string destination, CancellationToken token)
        {
            var checkpoint = destination + ".resume";
            var validator = File.Exists(checkpoint) ? File.ReadAllText(checkpoint) : "";
            var offset = File.Exists(destination) && validator.Length > 0 ? new FileInfo(destination).Length : 0;
            using (var response = await Get(url, repository, token, offset > 0 ? (long?)offset : null, null, validator).ConfigureAwait(false))
            {
                var append = offset > 0 && response.StatusCode == 206;
                if (append)
                {
                    var range = Range(response);
                    if (range.start != offset || range.end != range.total - 1) throw new InvalidDataException("Invalid resumed download range.");
                    ValidateRange(response, offset, range.end, range.total);
                }
                else if (response.StatusCode == 206) throw new InvalidDataException("Repository returned an unrequested partial download.");
                var tag = Header(response, "ETag");
                if (tag.Length == 0) tag = Header(response, "Last-Modified");
                if (append && tag != validator) throw new InvalidDataException("Download changed while resuming. Clear failed downloads and retry.");
                File.WriteAllText(checkpoint, tag);
                using (var file = new FileStream(destination, append ? FileMode.Append : FileMode.Create))
                    await Copy(response.Body, file, null, response.ContentLength ?? (append ? (long?)(Range(response).end - offset + 1) : null), token).ConfigureAwait(false);
            }
            File.Delete(checkpoint);
        }
        private async Task DownloadRanges(Uri url, string repository, string destination, CancellationToken token)
        {
            const int chunk = 256 * 1024;
            using (var first = await Get(url, repository, token, 0, chunk - 1).ConfigureAwait(false))
            {
                if (first.StatusCode != 206)
                {
                    using (var file = new FileStream(destination, FileMode.Create))
                        await Copy(first.Body, file, null, first.ContentLength, token).ConfigureAwait(false);
                    File.Delete(destination + ".resume");
                    return;
                }
                var total = Range(first).total;
                if (total <= 0) throw new InvalidDataException("Invalid Hexium download length.");
                var validator = Header(first, "ETag");
                var checkpoint = destination + ".resume";
                var done = new HashSet<long>();
                if (validator.Length > 0 && File.Exists(checkpoint) && File.Exists(destination) && new FileInfo(destination).Length == total)
                {
                    try
                    {
                        var saved = Json.ReadLocal<RangeCheckpoint>(checkpoint);
                        if (saved != null && saved.Validator == validator && saved.Length == total && saved.Done != null)
                            done.UnionWith(saved.Done.Where(n => n >= 0 && n < total && n % chunk == 0));
                    }
                    catch (Exception error) when (error is IOException || error is SerializationException) { done.Clear(); }
                }
                var firstEnd = Math.Min(total - 1, chunk - 1);
                ValidateRange(first, 0, firstEnd, total);
                using (var file = new FileStream(destination, done.Count > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var workers = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    file.SetLength(total);
                    await Copy(first.Body, file, chunk, firstEnd + 1, token).ConfigureAwait(false);
                    done.Add(0); long next = firstEnd + 1;
                    void Save() { file.Flush(); Json.Save(checkpoint, new RangeCheckpoint { Validator = validator, Length = total, Done = done.ToList() }); }
                    Save();
                    async Task Worker()
                    {
                        try
                        {
                            while (true)
                            {
                                workers.Token.ThrowIfCancellationRequested(); long start, end;
                                lock (file)
                                {
                                    while (next < total && done.Contains(next)) next += Math.Min(chunk, total - next);
                                    start = next; if (start >= total) return;
                                    end = start + Math.Min(chunk, total - start) - 1; next = end + 1;
                                }
                                using (var response = await Get(url, repository, workers.Token, start, end, validator).ConfigureAwait(false))
                                {
                                    ValidateRange(response, start, end, total);
                                    if (validator.Length > 0 && Header(response, "ETag") != validator) throw new InvalidDataException("Hexium download changed during resumption.");
                                    using (var memory = new MemoryStream())
                                    {
                                        await Copy(response.Body, memory, chunk, end - start + 1, workers.Token).ConfigureAwait(false);
                                        var bytes = memory.ToArray();
                                        lock (file)
                                        {
                                            file.Position = start; file.Write(bytes, 0, bytes.Length); done.Add(start);
                                            if (done.Count % 16 == 0) Save();
                                        }
                                    }
                                }
                            }
                        }
                        catch { workers.Cancel(); throw; }
                    }
                    try { await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Worker())).ConfigureAwait(false); }
                    finally { lock (file) Save(); }
                }
                File.Delete(checkpoint);
            }
        }
        [DataContract] private sealed class RangeCheckpoint
        {
            [DataMember] public string Validator = "";
            [DataMember] public long Length;
            [DataMember] public List<long> Done = new List<long>();
        }
        private static string Header(SystemResponse response, string name) => response.Headers.TryGetValue(name, out var value) ? value : "";
        private static (long start, long end, long total) Range(SystemResponse response)
        {
            var match = Regex.Match(Header(response, "Content-Range"), @"^bytes ([0-9]+)-([0-9]+)/([0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
                !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) || start > end || end >= total)
                throw new InvalidDataException("Repository returned an inconsistent download range.");
            return (start, end, total);
        }
        private static void ValidateRange(SystemResponse response, long start, long end, long total)
        {
            var range = Range(response);
            if (response.StatusCode != 206 || range.start != start || range.end != end || range.total != total ||
                response.ContentLength.HasValue && response.ContentLength.Value != end - start + 1)
                throw new InvalidDataException("Repository returned an inconsistent download range.");
        }
        private async Task<SystemResponse> Get(Uri url, string repository, CancellationToken token, long? start = null, long? end = null, string validator = "")
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!Trusted(url, repository)) throw new InvalidDataException("Repository address or redirect is outside its trusted HTTPS hosts.");
                SystemResponse response;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    response = await send(new SystemRequest { Url = url, UserAgent = "ValheimModGate/" + Constants.Version, RangeStart = start, RangeEnd = end, IfRange = start.HasValue ? validator : "" }, deadline.Token).ConfigureAwait(false);
                }
                if (response == null) throw new InvalidDataException("Repository returned no response.");
                if (response.StatusCode == 404) { response.Dispose(); throw new PackageNotFoundException(url.AbsolutePath); }
                if (response.StatusCode >= 300 && response.StatusCode <= 399)
                {
                    var location = Header(response, "Location"); response.Dispose();
                    if (location.Length == 0 || !Uri.TryCreate(url, location, out var redirect)) throw new InvalidDataException("Missing or invalid repository redirect.");
                    url = redirect; continue;
                }
                if (response.StatusCode >= 200 && response.StatusCode <= 299) return response;
                var status = response.StatusCode; response.Dispose();
                throw new IOException("Repository returned HTTP " + status + " for " + url.AbsolutePath + ".");
            }
            throw new IOException("Too many repository redirects.");
        }
        private static async Task<long> Copy(Stream input, Stream output, long? limit, long? expected, CancellationToken token)
        {
            if (expected.HasValue && (expected.Value < 0 || limit.HasValue && expected.Value > limit.Value)) throw new InvalidDataException("Repository response exceeds its size limit.");
            var buffer = new byte[65536]; long total = 0; int count;
            token.ThrowIfCancellationRequested();
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                total = checked(total + count);
                if (limit.HasValue && total > limit.Value || expected.HasValue && total > expected.Value) throw new InvalidDataException("Repository response exceeds its expected size.");
                await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (expected.HasValue && total != expected.Value) throw new IOException("Download was interrupted before all bytes arrived.");
            return total;
        }
        public void Dispose() { }
    }
}
