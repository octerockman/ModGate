using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using ModGate;

internal static partial class Program
{
    private static async Task Product031Checks()
    {
        Test("clientOnly is published, required on clients and is never a forbidden server-only entry", () =>
        {
            var mod = Mod("visual", "clientOnly", package: "A-Visual-1.0.0");
            var policy = PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { mod } }, Array.Empty<ModEntry>());
            var connector = Mod(Constants.Guid, version: Constants.Version);
            Check(PolicyEngine.Compare(policy, new[] { connector }).Missing.Single().Guid == "visual");
            Check(PolicyEngine.Compare(policy, new[] { connector, Mod("visual") }).Matches && policy.ForbiddenPackages.Count == 0);
            Check(DependencyRules.Required(policy, new HashSet<string>()).Contains("visual"));
            Check(ProfileSelection.ForSelection(policy, new HashSet<string>()).Mods.Any(m => m.Guid == "visual"));
            Check(ModStatuses.For(mod, Array.Empty<ModEntry>(), policy) == ModStatus.MissingRequired);
        });
        Test("automatic clientOnly entries survive an absent server instance and keep version allowances", () =>
        {
            var mod = Mod("visual", "clientOnly", package: "A-Visual-1.0.0"); mod.Automatic = true; mod.AllowNewerVersion = false;
            var settings = new ServerManifest { AllowNewerClientVersions = true, Mods = new List<ModEntry> { mod } };
            var next = AutoSetup.Reconcile(settings, Array.Empty<ModEntry>(), Array.Empty<string>());
            Check(next.Mods.Single().Side == "clientOnly" && next.Mods[0].AllowNewerVersion == false && next.AllowNewerClientVersions);
            var updated = AutoSetup.Reconcile(settings, new[] { Mod("visual", version: "2.0.0") }, Array.Empty<string>());
            Check(updated.Mods.Single().AllowNewerVersion == false && updated.Mods[0].Side == "clientOnly" && updated.Mods[0].Version == "2.0.0");
        });
        Test("auto-update publication uses the updated package while retaining a same-source manual URL in config", () =>
        {
            var mod = Mod("root", package: "A-Root-2.0.0", version: "2.0.0"); mod.DownloadUrl = "https://thunderstore.io/package/download/A/Root/1.0.0/";
            var settings = new ServerManifest { AutoUpdateOnRestart = true, AutoUpdateAllMods = true, Mods = new List<ModEntry> { mod } };
            var published = PolicyEngine.Publish(settings, new[] { Mod("root", version: "2.0.0") });
            Check(published.Mods.First().Package == "A-Root-2.0.0" && published.Mods.First().DownloadUrl == "" && mod.DownloadUrl.EndsWith("/1.0.0/"));
            mod.AutoUpdate = false;
            Throws<InvalidDataException>(() => PolicyEngine.Publish(settings, new[] { Mod("root", version: "2.0.0") }));
            mod.AutoUpdate = true; mod.DownloadUrl = "https://thunderstore.io/package/download/A/Unrelated/1.0.0/";
            Throws<InvalidDataException>(() => PolicyEngine.Publish(settings, new[] { Mod("root", version: "2.0.0") }));
        });
        Test("client-only filtering keeps cached lists, shared DLL siblings and ModGate intact", () =>
        {
            var cached = new List<string> { "visual", "server", Constants.Guid };
            var records = new Dictionary<string, List<string>> { ["shared.dll"] = cached };
            var removed = new List<string>();
            ClientOnlyRules.Filter(records, new HashSet<string> { "visual", Constants.Guid }, p => p, removed.Add);
            Check(cached.Count == 3 && records["shared.dll"].SequenceEqual(new[] { "server", Constants.Guid }) && removed.SequenceEqual(new[] { "visual" }));
            records["shared.dll"] = cached;
            ClientOnlyRules.Filter(records, new HashSet<string>(), p => p, removed.Add);
            Check(records["shared.dll"].Contains("visual"));
            var locked = Mod("visual*", "clientOnly*");
            Check(ClientOnlyRules.Guids(new ServerManifest { Mods = new List<ModEntry> { locked } }).SetEquals(new[] { "visual" }));
        });
        Test("newer client versions default to exact and require server permission", () =>
        {
            var mod = Mod("mod"); var policy = Policy(mod);
            Check(!PolicyEngine.Compare(policy, new[] { Mod("mod", version: "2.0.0") }).Matches);
            policy.AllowNewerClientVersions = true;
            Check(PolicyEngine.Compare(policy, new[] { Mod("mod", version: "2.0.0") }).Matches);
            Check(!PolicyEngine.Compare(policy, new[] { Mod("mod", version: "0.9.0") }).Matches);
            Check(PolicyEngine.AcceptsVersion(policy, mod, "1.0.0.0"));
            Check(!new ServerManifest().AllowNewerClientVersions);
        });
        Test("per-mod newer allowances override the master while ModGate always requires its exact version", () =>
        {
            var optional = Mod("optional", "optional"); optional.AllowNewerVersion = true;
            var client = Mod("client", "clientOnly"); client.AllowNewerVersion = false;
            var connector = Mod(Constants.Guid, version: Constants.Version); connector.AllowNewerVersion = true;
            var policy = Policy(optional, client, connector); policy.AllowNewerClientVersions = true;
            Check(PolicyEngine.AcceptsVersion(policy, optional, "2.0.0") && !PolicyEngine.AcceptsVersion(policy, client, "2.0.0"));
            Check(!PolicyEngine.AcceptsVersion(policy, connector, "9.0.0"));
            policy.AllowNewerClientVersions = false;
            Check(PolicyEngine.AcceptsVersion(policy, optional, "2.0.0") && !PolicyEngine.AcceptsVersion(policy, optional, "0.9.0"));
        });
        Test("accepted newer local copies retain actual versions and source files in the profile plan", () =>
        {
            var wanted = Mod("visual", "clientOnly", package: "A-Visual-1.0.0"); var policy = Policy(wanted); policy.AllowNewerClientVersions = true;
            var actual = Mod("visual", version: "2.0.0"); actual.LocalFile = "installed.dll";
            var plan = ProfileSelection.Build(policy, new[] { actual }, new HashSet<string>());
            Check(plan.Downloads.Count == 0 && plan.Local.Single().Version == "2.0.0" && wanted.Version == "1.0.0");
            var filtered = ProfileSelection.ForSelection(policy, new HashSet<string>());
            Check(filtered.AllowNewerClientVersions && ProfileSelection.Matches(filtered, plan.Local, new HashSet<string>()));
            Check(ModStatuses.For(wanted, new[] { actual }, policy) == ModStatus.Installed);
            policy.AllowNewerClientVersions = false;
            Check(ProfileSelection.Build(policy, new[] { actual }, new HashSet<string>()).Downloads.Count == 1);
        });
        Test("newer reused package resolves its actual manifest dependencies without downloading the old root", () =>
        {
            var policy = Policy(Mod("root", package: "A-Root-1.0.0")); policy.AllowNewerClientVersions = true; policy.AllowExtras = true;
            var actual = Mod("root", version: "2.0.0"); actual.LocalFile = "root.dll";
            actual.LocalManifest = PackageManifest.Parse("{\"name\":\"Root\",\"version_number\":\"2.0.0\",\"website_url\":\"https://thunderstore.io/c/valheim/p/A/Root/\",\"dependencies\":[\"A-Lib-2.0.0\"]}");
            var plan = ProfileSelection.Build(policy, new[] { actual }, new HashSet<string>());
            var source = new Product031Source();
            var resolved = Downloads.Resolve(policy, new Comparison(), source, CancellationToken.None, plan.Local, new HashSet<string> { "A-Lib-2.0.0" }).GetAwaiter().GetResult();
            Check(resolved.Single().FullName == "A-Lib-2.0.0" && source.Requests.SequenceEqual(new[] { "A-Lib-2.0.0" }));
            Check(actual.Version == "2.0.0" && policy.Mods[0].Version == "1.0.0");
        });
        Test("secure fallback runs on nested TLS failures but ordinary HTTP errors do not invoke it", () =>
        {
            var calls = 0;
            using var handler = new NativeHttps(new Product031Handler(_ => throw new HttpRequestException("outer", new AuthenticationException("bad certificate"))), (_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }); });
            using var client = new HttpClient(handler);
            Check(client.GetStringAsync("https://thunderstore.io/").GetAwaiter().GetResult() == "ok" && calls == 1);
            using var regular = new HttpClient(new NativeHttps(new Product031Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), (_, _) => throw new Exception("unexpected fallback")));
            Check(regular.GetAsync("https://thunderstore.io/").GetAwaiter().GetResult().StatusCode == HttpStatusCode.ServiceUnavailable);
        });
        Test("TLS report keeps both failure causes and cancellation never starts another transport", () =>
        {
            using var client = new HttpClient(new NativeHttps(new Product031Handler(_ => throw new AuthenticationException("mono trust chain")), (_, _) => throw new IOException("curl CA missing")));
            try { client.GetAsync("https://thunderstore.io/").GetAwaiter().GetResult(); throw new Exception("expected failure"); }
            catch (HttpRequestException error) { Check(NativeHttps.Details(error).Contains("mono trust chain") && error.Message.Contains("curl CA missing") && error.Message.Contains("CA certificates")); }
            Check(!NativeHttps.TlsFailure(new IOException("disk full")));
            using var cancelled = new HttpClient(new NativeHttps(new Product031Handler(_ => throw new OperationCanceledException()), (_, _) => throw new Exception("unexpected fallback")));
            Throws<OperationCanceledException>(() => cancelled.GetAsync("https://thunderstore.io/").GetAwaiter().GetResult());
        });
        Test("secure fallback responses still pass repository redirect restrictions", () =>
        {
            var calls = 0;
            using var source = new RepositorySource(new NativeHttps(new Product031Handler(_ => throw new AuthenticationException("TLS fixture")), (_, _) =>
            {
                calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://unapproved.invalid/file"); return Task.FromResult(response);
            }));
            Throws<InvalidDataException>(() => source.Latest(PackageId.Parse("A-Root-1.0.0"), CancellationToken.None).GetAwaiter().GetResult());
            Check(calls == 1);
        });
        Test("native HTTPS command forbids insecure connections and automatic redirects or curl configs", () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://thunderstore.io/package/download/A/Root/1.0.0/"); request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19); request.Headers.TryAddWithoutValidation("If-Range", "\"etag\"");
            var info = NativeHttps.StartInfo(request);
            Check(info.Arguments.StartsWith("\"--disable\"") && info.Arguments.Contains("--tlsv1.2") && info.Arguments.Contains("Range: bytes=10-19") && !info.Arguments.Contains("--location") && !info.Arguments.Contains("--insecure") && !info.UseShellExecute && info.CreateNoWindow);
            Throws<InvalidDataException>(() => NativeHttps.StartInfo(new HttpRequestMessage(HttpMethod.Get, "http://thunderstore.io/")));
        });
        Test("native response streams informational headers and preserves range identity", () =>
        {
            using var response = NativeHttps.ReadProcess(NativeFixture("range"), CancellationToken.None).GetAwaiter().GetResult();
            Check(response.StatusCode == HttpStatusCode.PartialContent && response.Headers.ETag?.Tag == "\"fixture\"" && response.Content.Headers.ContentRange?.From == 10);
            Check(response.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "hello");
        });
        Test("native transport rejects truncated transfers and drains bounded stderr without deadlock", () =>
        {
            using var response = NativeHttps.ReadProcess(NativeFixture("truncated"), CancellationToken.None).GetAwaiter().GetResult();
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            Throws<IOException>(() => input.CopyTo(Stream.Null));
            try { NativeHttps.ReadProcess(NativeFixture("error"), CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("expected failure"); }
            catch (IOException error) { Check(error.Message.Contains("curl exit 60") && error.Message.Length < 9000); }
        });
        using (var cancel = new CancellationTokenSource(250))
        {
            try { using var response = await NativeHttps.ReadProcess(NativeFixture("wait"), cancel.Token); throw new Exception("expected cancellation"); }
            catch (OperationCanceledException) { passed++; Console.WriteLine("PASS native HTTPS cancellation terminates only its own helper"); }
        }
    }
    private static ProcessStartInfo NativeFixture(string mode)
    {
        var executable = Environment.ProcessPath!;
        var args = "--native-http-fixture " + mode;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args = "\"" + typeof(Program).Assembly.Location + "\" " + args;
        return new ProcessStartInfo(executable, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
    }
    private static int NativeFixtureOutput(string mode)
    {
        if (mode == "wait") { Thread.Sleep(30000); return 0; }
        if (mode == "error") { Console.Error.Write(new string('x', 100000)); return 60; }
        Console.Write("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 206 Partial Content\r\nETag: \"fixture\"\r\nContent-Range: bytes 10-14/100\r\nContent-Length: 5\r\n\r\nhello");
        return mode == "truncated" ? 18 : 0;
    }
    private sealed class Product031Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public Product031Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class Product031Source : IPackageSource
    {
        public readonly List<string> Requests = new List<string>();
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token) { Requests.Add(id.FullName); return Task.FromResult(new PackageMetadata { FullName = id.RawFullName }); }
        public Task Download(PackageId id, string path, CancellationToken token) => throw new Exception("unexpected download");
    }
}
