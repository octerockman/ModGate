using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using ModGate;

internal static class ServerPackageSourceTests
{
    public static void Run(Action<string, Action> test, Action<bool> check, Func<string> newRoot)
    {
        void Case(string name, Func<Task> body) => test(name, () => body().GetAwaiter().GetResult());
        var token = CancellationToken.None;

        Case("system server source resolves latest and exact metadata for both approved repositories", async () =>
        {
            foreach (var repository in new[] { "thunderstore", "hexium" })
            {
                var id = PackageId.Parse((repository == "hexium" ? "hexium:" : "") + "Author-Mod-1.0.0");
                var requests = new List<SystemRequest>(); var bodies = new List<TrackingStream>();
                using var source = new ServerPackageSource((request, _) =>
                {
                    requests.Add(request);
                    var response = request.Url.AbsolutePath.EndsWith("/2.0.0/")
                        ? JsonResponse(new PackageMetadata { FullName = "Author-Mod-2.0.0", Dependencies = new() { "Author-Library-1.0.0", "denikson-BepInExPack_Valheim-5.4.2351" } })
                        : JsonResponse(new LatestPackage { Latest = new PackageMetadata { FullName = "Author-Mod-2.0.0", IsActive = true } });
                    bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
                });
                var latest = await source.Latest(id, token); var metadata = await source.Metadata(latest, token);
                check(latest.Repository == repository && latest.RawFullName == "Author-Mod-2.0.0" && metadata.Dependencies.Count == 2);
                check(requests.Count == 2 && requests.All(r => r.Url.Host == (repository == "hexium" ? "hexium.gg" : "thunderstore.io") && r.UserAgent == "ValheimModGate/" + Constants.Version));
                check(requests[0].Url.AbsolutePath == "/api/experimental/package/Author/Mod/" && requests[1].Url.AbsolutePath == "/api/experimental/package/Author/Mod/2.0.0/" && bodies.All(b => b.Disposed));
            }
        });
        Case("system server latest rejects absent inactive deprecated and mismatched releases", async () =>
        {
            var id = PackageId.Parse("Author-Mod-1.0.0");
            foreach (var text in new[]
            {
                "null", Json.Write(new LatestPackage { Deprecated = true, Latest = new() { FullName = "Author-Mod-2.0.0" } }),
                Json.Write(new LatestPackage { Latest = null! }), Json.Write(new LatestPackage { Latest = new() { FullName = "Author-Mod-2.0.0", IsActive = false } }),
                Json.Write(new LatestPackage { Latest = new() { FullName = "Other-Mod-2.0.0" } }),
                Json.Write(new LatestPackage { Latest = new() { FullName = "github:Author/Mod/v2.0.0/Mod.zip" } })
            })
            {
                var response = Response(Encoding.UTF8.GetBytes(text));
                using var source = new ServerPackageSource((_, _) => Task.FromResult(response));
                await Throws<InvalidDataException>(() => source.Latest(id, token)); check(((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system server metadata rejects wrong identities versions and unauthorized dependencies", async () =>
        {
            foreach (var metadata in new[]
            {
                new PackageMetadata { FullName = "Other-Mod-1.0.0" }, new PackageMetadata { FullName = "Author-Mod-2.0.0" },
                new PackageMetadata { FullName = "Author-Mod-1.0.0", Dependencies = null! },
                new PackageMetadata { FullName = "Author-Mod-1.0.0", Dependencies = Enumerable.Repeat("Author-Lib-1.0.0", 129).ToList() },
                new PackageMetadata { FullName = "Author-Mod-1.0.0", Dependencies = new() { "github:Author/Library/v1.0.0/Library.zip" } }
            })
            {
                var response = JsonResponse(metadata);
                using var source = new ServerPackageSource((_, _) => Task.FromResult(response));
                await Throws<InvalidDataException>(() => source.Metadata(PackageId.Parse("Author-Mod-1.0.0"), token));
                check(((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system server source rejects GitHub before making any transport call", async () =>
        {
            var calls = 0;
            using var source = new ServerPackageSource((_, _) => { calls++; throw new Exception("Unexpected transport call"); });
            var id = PackageId.Parse("github:Author/Mod/v1.0.0/Mod.zip");
            await Throws<InvalidDataException>(() => source.Latest(id, token));
            await Throws<InvalidDataException>(() => source.Metadata(id, token));
            await Throws<InvalidDataException>(() => source.Download(id, Path.Combine(newRoot(), "mod.zip"), token));
            check(calls == 0);
        });
        Case("system server redirects preserve approved relative and CDN addresses and dispose each response", async () =>
        {
            var requests = new List<SystemRequest>(); var bodies = new List<TrackingStream>();
            using var source = new ServerPackageSource((request, _) =>
            {
                requests.Add(request);
                var response = requests.Count == 1 ? Response(Array.Empty<byte>(), 302, ("Location", "/redirect"))
                    : requests.Count == 2 ? Response(Array.Empty<byte>(), 307, ("Location", "https://ccdn.thunderstore.io/metadata.json"))
                    : JsonResponse(new PackageMetadata { FullName = "Author-Mod-1.0.0" });
                bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
            });
            await source.Metadata(PackageId.Parse("Author-Mod-1.0.0"), token);
            check(requests.Count == 3 && requests[1].Url.AbsoluteUri == "https://thunderstore.io/redirect" && requests[2].Url.Host == "ccdn.thunderstore.io" && bodies.All(b => b.Disposed));
        });
        Case("system server source rejects unsafe redirect hosts schemes ports and credentials", async () =>
        {
            foreach (var location in new[] { "http://thunderstore.io/file", "https://thunderstore.io.evil.test/file", "https://user@thunderstore.io/file", "https://thunderstore.io:444/file", "https://hexium.gg/file" })
            {
                var calls = 0; var response = Response(Array.Empty<byte>(), 302, ("Location", location));
                using var source = new ServerPackageSource((_, _) => { calls++; return Task.FromResult(response); });
                await Throws<InvalidDataException>(() => source.Metadata(PackageId.Parse("Author-Mod-1.0.0"), token));
                check(calls == 1 && ((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system server redirect loops and redirects without locations stop safely", async () =>
        {
            var bodies = new List<TrackingStream>();
            using (var source = new ServerPackageSource((_, _) =>
            {
                var response = Response(Array.Empty<byte>(), 302, ("Location", "/again")); bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
            })) await Throws<IOException>(() => source.Latest(PackageId.Parse("Author-Mod-1.0.0"), token));
            check(bodies.Count == 6 && bodies.All(b => b.Disposed));
            var missing = Response(Array.Empty<byte>(), 302);
            using var incomplete = new ServerPackageSource((_, _) => Task.FromResult(missing));
            await Throws<InvalidDataException>(() => incomplete.Metadata(PackageId.Parse("Author-Mod-1.0.0"), token));
            check(((TrackingStream)missing.Body).Disposed);
        });
        Case("system server 404 responses retain package-not-found behavior for all operations", async () =>
        {
            var bodies = new List<TrackingStream>();
            using var source = new ServerPackageSource((_, _) =>
            {
                var response = Response(Array.Empty<byte>(), 404); bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
            });
            var id = PackageId.Parse("Author-Mod-1.0.0"); var destination = Path.Combine(newRoot(), "missing.zip");
            await Throws<PackageNotFoundException>(() => source.Latest(id, token)); await Throws<PackageNotFoundException>(() => source.Metadata(id, token));
            await Throws<PackageNotFoundException>(() => source.Download(id, destination, token));
            check(bodies.Count == 3 && bodies.All(b => b.Disposed) && !File.Exists(destination));
        });
        Case("system server metadata caps declared streamed and decompressed response sizes", async () =>
        {
            var excessive = Enumerable.Repeat((byte)' ', Constants.MaxJson + 1).ToArray();
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionMode.Compress, true)) gzip.Write(excessive);
            var declared = Response(excessive); var streamed = Response(excessive); streamed.Headers.Remove("Content-Length");
            foreach (var response in new[] { declared, streamed, Response(packed.ToArray()) })
            {
                using var source = new ServerPackageSource((_, _) => Task.FromResult(response));
                await Throws<InvalidDataException>(() => source.Metadata(PackageId.Parse("Author-Mod-1.0.0"), token));
                check(((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system Thunderstore downloads stream exact bytes and remove successful checkpoints", async () =>
        {
            var bytes = Enumerable.Range(0, 150000).Select(i => (byte)(i % 251)).ToArray();
            var response = Response(bytes, 200, ("ETag", "\"release-one\"")); SystemRequest? seen = null;
            using var source = new ServerPackageSource((request, _) => { seen = request; return Task.FromResult(response); });
            var destination = Path.Combine(newRoot(), "mod.zip"); await source.Download(PackageId.Parse("Author-Mod-1.0.0"), destination, token);
            check(File.ReadAllBytes(destination).SequenceEqual(bytes) && !File.Exists(destination + ".resume") && ((TrackingStream)response.Body).Disposed);
            check(seen != null && seen.Url.AbsolutePath == "/package/download/Author/Mod/1.0.0/" && !seen.RangeStart.HasValue);
        });
        Case("system Thunderstore downloads reject truncated and oversized response bodies", async () =>
        {
            foreach (var expected in new[] { 2, 4 })
            {
                var response = Response(new byte[] { 1, 2, 3 }); response.Headers["Content-Length"] = expected.ToString(CultureInfo.InvariantCulture);
                using var source = new ServerPackageSource((_, _) => Task.FromResult(response));
                var destination = Path.Combine(newRoot(), "mod.zip");
                if (expected == 2) await Throws<InvalidDataException>(() => source.Download(PackageId.Parse("Author-Mod-1.0.0"), destination, token));
                else await Throws<IOException>(() => source.Download(PackageId.Parse("Author-Mod-1.0.0"), destination, token));
                check(((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system Thunderstore resumption verifies byte ranges and release validators", async () =>
        {
            foreach (var changed in new[] { false, true })
            {
                var destination = Path.Combine(newRoot(), "mod.zip"); File.WriteAllBytes(destination, new byte[] { 1, 2, 3 }); File.WriteAllText(destination + ".resume", "\"same\"");
                var response = Response(new byte[] { 4, 5, 6 }, 206, ("Content-Range", "bytes 3-5/6"), ("ETag", changed ? "\"changed\"" : "\"same\""));
                using var source = new ServerPackageSource((request, _) =>
                {
                    check(request.RangeStart == 3 && request.RangeEnd == null && request.IfRange == "\"same\""); return Task.FromResult(response);
                });
                if (changed)
                {
                    await Throws<InvalidDataException>(() => source.Download(PackageId.Parse("Author-Mod-1.0.0"), destination, token));
                    check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3 }));
                }
                else
                {
                    await source.Download(PackageId.Parse("Author-Mod-1.0.0"), destination, token);
                    check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }) && !File.Exists(destination + ".resume"));
                }
                check(((TrackingStream)response.Body).Disposed);
            }
        });
        Case("system Hexium downloads use four workers and assemble validated ranges exactly", async () =>
        {
            const int chunk = 256 * 1024;
            var bytes = Enumerable.Range(0, chunk * 6 + 17).Select(i => (byte)(i % 251)).ToArray();
            var bodies = new ConcurrentBag<TrackingStream>(); var requests = new ConcurrentBag<SystemRequest>();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new object(); var active = 0; var maximum = 0;
            using var source = new ServerPackageSource(async (request, requestToken) =>
            {
                requests.Add(request); SystemResponse response;
                if (!request.RangeStart.HasValue) response = HexiumMetadata();
                else
                {
                    if (request.RangeStart > 0)
                    {
                        lock (gate) { active++; maximum = Math.Max(maximum, active); if (active == 4) ready.TrySetResult(true); }
                        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), requestToken); }
                        finally { lock (gate) active--; }
                    }
                    var start = request.RangeStart.Value; var end = Math.Min(request.RangeEnd!.Value, bytes.Length - 1);
                    check(request.Url.Host == "cdn.hexium.gg" && (start == 0 ? request.IfRange == "" : request.IfRange == "\"same\""));
                    response = Response(bytes.AsSpan((int)start, (int)(end - start + 1)).ToArray(), 206, ("Content-Range", "bytes " + start + "-" + end + "/" + bytes.Length), ("ETag", "\"same\""));
                }
                bodies.Add((TrackingStream)response.Body); return response;
            });
            var destination = Path.Combine(newRoot(), "mod.zip"); await source.Download(PackageId.Parse("hexium:Author-Mod-1.0.0"), destination, token);
            check(maximum == 4 && requests.Count(r => r.RangeStart.HasValue) == 7 && bodies.All(b => b.Disposed));
            check(File.ReadAllBytes(destination).SequenceEqual(bytes) && !File.Exists(destination + ".resume"));
        });
        Case("system Hexium rejects inconsistent ranges and changing release identities", async () =>
        {
            const int chunk = 256 * 1024; const int total = chunk + 3;
            foreach (var failure in new[] { "first", "range", "etag" })
            {
                var bodies = new List<TrackingStream>();
                using var source = new ServerPackageSource((request, _) =>
                {
                    SystemResponse response;
                    if (!request.RangeStart.HasValue) response = HexiumMetadata();
                    else
                    {
                        var start = request.RangeStart.Value; var end = Math.Min(request.RangeEnd!.Value, total - 1);
                        var rangeStart = start == 0 && failure == "first" ? 1 : start;
                        var rangeTotal = start > 0 && failure == "range" ? total + 1 : total;
                        response = Response(new byte[(int)(end - start + 1)], 206, ("Content-Range", "bytes " + rangeStart + "-" + end + "/" + rangeTotal), ("ETag", start > 0 && failure == "etag" ? "\"changed\"" : "\"same\""));
                    }
                    bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
                });
                await Throws<InvalidDataException>(() => source.Download(PackageId.Parse("hexium:Author-Mod-1.0.0"), Path.Combine(newRoot(), "mod.zip"), token));
                check(bodies.All(b => b.Disposed));
            }
        });
        Case("system Hexium accepts a complete response when range requests are unsupported", async () =>
        {
            var bytes = new byte[] { 1, 2, 3, 4 }; var calls = 0; var bodies = new List<TrackingStream>();
            using var source = new ServerPackageSource((request, _) =>
            {
                calls++; var response = request.RangeStart.HasValue ? Response(bytes) : HexiumMetadata(); bodies.Add((TrackingStream)response.Body); return Task.FromResult(response);
            });
            var destination = Path.Combine(newRoot(), "mod.zip"); await source.Download(PackageId.Parse("hexium:Author-Mod-1.0.0"), destination, token);
            check(calls == 2 && File.ReadAllBytes(destination).SequenceEqual(bytes) && bodies.All(b => b.Disposed));
        });
        Case("system server cancellation avoids new requests and disposes an interrupted body", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var calls = 0;
            using (var source = new ServerPackageSource((_, _) => { calls++; throw new Exception("Unexpected transport call"); }))
                await Throws<OperationCanceledException>(() => source.Metadata(PackageId.Parse("Author-Mod-1.0.0"), cancelled.Token));
            check(calls == 0);
            using var duringRead = new CancellationTokenSource(); var body = new CancelOnReadStream(new byte[] { 1, 2, 3 }, duringRead);
            var response = new SystemResponse { StatusCode = 200, Body = body }; response.Headers["Content-Length"] = "3";
            using (var source = new ServerPackageSource((_, _) => Task.FromResult(response)))
                await Throws<OperationCanceledException>(() => source.Download(PackageId.Parse("Author-Mod-1.0.0"), Path.Combine(newRoot(), "mod.zip"), duringRead.Token));
            check(body.Disposed && duringRead.IsCancellationRequested);
        });
    }

    private static SystemResponse HexiumMetadata() => JsonResponse(new PackageMetadata { FullName = "Author-Mod-1.0.0", DownloadUrl = "https://cdn.hexium.gg/files/mod.zip" });
    private static SystemResponse JsonResponse<T>(T value) => Response(Encoding.UTF8.GetBytes(Json.Write(value)));
    private static SystemResponse Response(byte[] bytes, int status = 200, params (string name, string value)[] headers)
    {
        var response = new SystemResponse { StatusCode = status, Body = new TrackingStream(bytes) };
        response.Headers["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture);
        foreach (var header in headers) response.Headers[header.name] = header.value;
        return response;
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private class TrackingStream : MemoryStream
    {
        public bool Disposed;
        public TrackingStream(byte[] bytes) : base(bytes, false) { }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CancelOnReadStream : TrackingStream
    {
        private readonly CancellationTokenSource cancellation;
        public CancelOnReadStream(byte[] bytes, CancellationTokenSource cancellation) : base(bytes) { this.cancellation = cancellation; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            var read = await base.ReadAsync(buffer, offset, count, token);
            if (read > 0) cancellation.Cancel();
            return read;
        }
    }
}
