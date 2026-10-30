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
using ModGate;

internal static partial class Program
{
    private static async Task StagingReceiptChecks()
    {
        var root = ProfileRoot();
        var archive = Path.Combine(root, "many.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            for (var i = 0; i < 10001; i++) zip.CreateEntry("assets/file-" + i.ToString("D5") + ".bundle");
        var pending = await Downloads.Stage(root, new[] { PackageId.Parse("A-Many-1.0.0") }, new FileSource(archive), _ => { }, CancellationToken.None);
        Test("more than ten thousand files stage with a receipt above the old size limit", () =>
        {
            Check(pending.Files.Count == 10001 && Json.Write(pending).Length > Constants.MaxJson);
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
        });
        Test("large file receipts survive scheduling startup activation and offline restoration", () =>
        {
            Profiles.Schedule(root, Command(pending));
            var stored = Json.ReadLocal<ProfileCommand>(SafePath.Under(root, "ModGate/profile-request.json"));
            Check(stored.Install!.Files.Count == 10001);
            Profiles.Apply(root, _ => { });
            Check(File.Exists(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Many/assets/file-10000.bundle")));
            Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll" }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
        Test("local install requests larger than eight MiB stream without changing network bounds", () =>
        {
            var localRoot = ProfileRoot(); var command = PrepareProfile(localRoot, 1, 'd');
            command.Install!.Files = Enumerable.Range(0, 80000).Select(i => new FileDigest { Path = "packages/A-Mod/long-file-name-for-large-receipt-" + i + ".bundle", Sha256 = new string('a', 64) }).ToList();
            Profiles.Schedule(localRoot, command);
            var path = SafePath.Under(localRoot, "ModGate/profile-request.json");
            Check(new FileInfo(path).Length > Constants.MaxLocalJson);
            Check(Json.ReadLocal<ProfileCommand>(path).Install!.Files.Count == 80000);
            try { Profiles.Apply(localRoot, _ => { }); throw new Exception("Unverified files accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("Staged file verification failed")); }
            Check(File.ReadAllText(SafePath.Under(localRoot, "plugins/Original/original.dll")) == "original");
            Throws<InvalidDataException>(() => Json.Read<Policy>(new string(' ', Constants.MaxJson + 1)));
        });
        Test("profile config staging accepts large files and more than thirty two bindings", () =>
        {
            var localRoot = Root(); var mod = Mod("large.config"); mod.LocalConfigFiles = Enumerable.Range(0, 40).Select(i => "Custom/file-" + i + ".cfg").ToList();
            foreach (var file in mod.LocalConfigFiles) Put(localRoot, "config/" + file, "setting");
            mod.LocalConfigFiles.Add("Custom/large.cfg"); var large = Put(localRoot, "config/Custom/large.cfg", "");
            using (var file = File.OpenWrite(large)) file.SetLength(17L * 1024 * 1024);
            ProfileConfigs.RecordActive(localRoot, new[] { mod });
            var staged = Downloads.Stage(localRoot, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            var bindings = ProfileConfigs.Stage(localRoot, new[] { mod }, "active", staged);
            Check(bindings.Single().Files.Count == 41);
            Check(new FileInfo(SafePath.Under(localRoot, "ModGate/staging/" + staged.Id + "/config/Custom/large.cfg")).Length == 17L * 1024 * 1024);
            StageVerifier.VerifyStage(SafePath.Under(localRoot, "ModGate/staging/" + staged.Id), staged);
        });
        Test("streaming extraction remains cancellable", () =>
        {
            using var input = new ZeroStream(1024 * 1024); using var output = new MemoryStream(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Throws<OperationCanceledException>(() => Downloads.CopyPayload(input, output, cancellation.Token)); Check(output.Length == 0);
        });
    }

    private static ProfileCommand Command(PendingInstall install) => new ProfileCommand
    {
        Kind = "install", Target = "server-1", ReplaceConfig = true, Install = install,
        Profile = new SavedProfile { Slot = "server-1", ServerKey = new string('c', 64), Name = "Large fixture", Mods = new List<ModEntry> { Connector(), Mod("fixture") }, Packages = install.Packages }
    };
    private static void ClearStaging(string root)
    {
        Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll" }); Profiles.Apply(root, _ => { });
    }
    private static async Task LargeStagingChecks()
    {
        const long size = 2L * 1024 * 1024 * 1024 + 1;
        var root = ProfileRoot(); var archive = Path.Combine(root, "large.zip");
        Console.WriteLine("Creating a compressed fixture with a " + size + " byte file.");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("assets/large.bundle", CompressionLevel.Fastest).Open())
        using (var input = new ZeroStream(size)) Downloads.CopyPayload(input, output, CancellationToken.None);
        var package = PackageId.Parse("A-Large-1.0.0"); var source = new FileSource(archive);
        var pending = await Downloads.Stage(root, new[] { package }, source, Console.WriteLine, CancellationToken.None);
        Test("a file and profile above two GiB stage and activate with validated receipts", () =>
        {
            Check(new FileInfo(SafePath.Under(root, "ModGate/staging/" + pending.Id + "/packages/A-Large/assets/large.bundle")).Length == size);
            Profiles.Schedule(root, Command(pending)); Profiles.Apply(root, _ => { });
            Check(new FileInfo(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Large/assets/large.bundle")).Length == size);
            ClearStaging(root); Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
        var issues = new List<string>();
        pending = await PartialSetup.Stage(root, new[] { package }, source, Console.WriteLine, CancellationToken.None, issues);
        Test("partial setup merges a profile above two GiB without omitting its package", () =>
        {
            Check(issues.Count == 0 && pending.Packages.Single() == package.FullName);
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
            Check(new FileInfo(SafePath.Under(root, "ModGate/staging/" + pending.Id + "/packages/A-Large/assets/large.bundle")).Length == size);
            ClearStaging(root);
        });
        Test("installed local folders and individual assets above two GiB can be reused", () =>
        {
            var localRoot = Root(); var mod = Mod("local.big"); mod.LocalFile = Put(localRoot, "plugins/Big/mod.dll", "fixture");
            var asset = Put(localRoot, "plugins/Big/large.bundle", ""); using (var file = File.OpenWrite(asset)) file.SetLength(size);
            var local = Downloads.Stage(localRoot, Array.Empty<PackageId>(), source, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            ManualMods.Stage(localRoot, new[] { mod }, new[] { mod }, local, CancellationToken.None);
            Check(local.LocalPackages.Count == 1);
            StageVerifier.VerifyStage(SafePath.Under(localRoot, "ModGate/staging/" + local.Id), local);
            Check(new FileInfo(SafePath.Under(localRoot, "ModGate/staging/" + local.Id + "/packages/" + local.LocalPackages.Single() + "/large.bundle")).Length == size);
            ClearStaging(localRoot); Check(File.ReadAllText(mod.LocalFile) == "fixture"); File.Delete(asset);
        });
        const long downloadSize = 256L * 1024 * 1024 + 1;
        foreach (var repository in new[] { "thunderstore", "hexium" })
        {
            var file = Path.Combine(root, repository + ".zip"); var handler = new LargeDownloadHandler(downloadSize);
            using (var transport = new RepositorySource(handler)) await transport.Download(PackageId.Parse((repository == "hexium" ? "hexium:" : "") + "A-Large-1.0.0"), file, CancellationToken.None);
            Test(repository + " downloads accept an archive larger than 256 MiB", () => { Check(new FileInfo(file).Length == downloadSize); if (repository == "hexium") Check(handler.Ranges > 1024); File.Delete(file); });
        }
        Test("nested ZIP payloads larger than 128 MiB use disk-backed extraction", () =>
        {
            const long nestedSize = 129L * 1024 * 1024;
            var inner = Path.Combine(root, "inner.zip");
            using (var zip = ZipFile.Open(inner, ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("assets/nested.bundle", CompressionLevel.NoCompression).Open())
            using (var input = new ZeroStream(nestedSize)) Downloads.CopyPayload(input, output, CancellationToken.None);
            var outer = Wrap(root, inner); var nested = Downloads.Stage(root, new[] { PackageId.Parse("A-Nested-1.0.0") }, new FileSource(outer), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + nested.Id), nested);
            Check(new FileInfo(SafePath.Under(root, "ModGate/staging/" + nested.Id + "/packages/A-Nested/assets/nested.bundle")).Length == nestedSize);
            ClearStaging(root); File.Delete(inner); File.Delete(outer);
        });
    }
    private sealed class FileSource : IPackageSource
    {
        private readonly string archive;
        public FileSource(string archive) { this.archive = archive; }
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token) => Task.FromResult(new PackageMetadata { FullName = id.FullName });
        public Task Download(PackageId id, string destination, CancellationToken token) { token.ThrowIfCancellationRequested(); File.Copy(archive, destination, false); return Task.CompletedTask; }
    }
    private sealed class ZeroStream : Stream
    {
        private readonly long length; private long position;
        public ZeroStream(long length) { this.length = length; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { count = (int)Math.Min(count, length - position); Array.Clear(buffer, offset, count); position += count; return count; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class LargeDownloadHandler : HttpMessageHandler
    {
        private readonly long size; public int Ranges;
        public LargeDownloadHandler(long size) { this.size = size; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "hexium.gg") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Write(new PackageMetadata { FullName = "A-Large-1.0.0", DownloadUrl = "https://cdn.hexium.gg/large.zip" })) });
            var range = request.Headers.Range?.Ranges.Single(); var start = range?.From ?? 0; var end = Math.Min(range?.To ?? size - 1, size - 1);
            var content = new StreamContent(new ZeroStream(end - start + 1)); content.Headers.ContentLength = end - start + 1;
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = content };
            if (range != null) { Interlocked.Increment(ref Ranges); content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, size); }
            return Task.FromResult(response);
        }
    }
}
