using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModGate;

internal static partial class Program
{
    private static List<ModEntry> HealthInspect(string path)
    {
        if (!Directory.Exists(path)) return new List<ModEntry>();
        return Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories).Select(file =>
        {
            var fields = File.ReadAllText(file).Split('|');
            return new ModEntry { Guid = fields[0], Name = fields[0], Version = fields.Length > 1 ? fields[1] : "0.0.0", LocalFile = file };
        }).ToList();
    }
    private static (string root, ProfileCatalog catalog) HealthFixture(bool offline = false)
    {
        var root = Root();
        var mods = new List<ModEntry> { Connector(), Mod("test", package: "A-Mod-1.0.0") };
        var catalog = new ProfileCatalog { Active = offline ? "offline" : "server-1", OfflineMods = offline ? mods : new List<ModEntry>() };
        if (!offline) catalog.Servers.Add(new SavedProfile { Slot = "server-1", Name = "Health fixture", ServerKey = new string('a', 64), Mods = mods, Packages = new List<string> { "A-Mod-1.0.0" } });
        Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|1.0.0");
        Put(root, "plugins/ValheimModGate.Active/A-Mod/asset.bundle", "asset");
        Json.Save(Path.Combine(root, "ModGate", "profiles.json"), catalog);
        ProfileHealth.Capture(root, catalog, catalog.Active, HealthInspect);
        return (root, catalog);
    }
    private static void ProfileHealthChecks()
    {
        Test("health detects changed and missing owned files without rewriting its trusted baseline", () =>
        {
            var (root, catalog) = HealthFixture();
            var receipt = File.ReadAllText(Path.Combine(root, "ModGate/health/server-1.json"));
            Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|2.0.0");
            File.Delete(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/asset.bundle"));
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            Check(report.HasReceipt && !report.Healthy && report.CanRepair && report.Issues.Any(i => i.Code == "ChangedFile") && report.Issues.Any(i => i.Code == "MissingFile") && report.Issues.Any(i => i.Code == "InstalledVersion"));
            Check(receipt == File.ReadAllText(Path.Combine(root, "ModGate/health/server-1.json")));
            ProfileHealth.Capture(root, catalog, catalog.Active, HealthInspect);
            Check(ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect).Issues.Any(i => i.Code == "ChangedFile"));
        });
        Test("health gives dependency reasons and distinguishes loaded from installed versions", () =>
        {
            var (root, catalog) = HealthFixture();
            var mod = catalog.Servers[0].Mods.Single(m => m.Guid == "test");
            mod.Dependencies.Add(new PluginDependency { Guid = "library", MinimumVersion = "2.0.0" });
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect, new[] { Mod("test", version: "0.9.0") });
            Check(report.LoadedEvidenceAvailable && report.Issues.Any(i => i.Code == "MissingDependency" && i.Warning && !i.CanRepair && i.Message.Contains("library") && i.Message.Contains("test")) && report.Issues.Any(i => i.Code == "RestartRequired") && report.Healthy);
            Put(root, "plugins/Library/library.dll", "library|1.0.0");
            Check(ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect).Issues.Any(i => i.Code == "DependencyVersion" && i.Warning && !i.CanRepair));
        });
        Test("health never offers automatic repair for untracked package additions or external mods", () =>
        {
            var (root, catalog) = HealthFixture();
            Put(root, "plugins/ValheimModGate.Active/A-Mod/untracked.bundle", "manual addition");
            Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|2.0.0");
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            Check(!report.CanRepair && report.Issues.Any(i => i.Code == "UntrackedFile"));
            Throws<InvalidDataException>(() => ProfileHealth.PlanRepair(report, report.Issues.Select(i => i.Id)));
            File.Delete(SafePath.Under(root, "ModGate/health/server-1.json"));
            Check(!ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect).HasReceipt);
        });
        Test("targeted repair re-fetches exact bytes and stages without changing live files", () =>
        {
            var (root, catalog) = HealthFixture();
            Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|2.0.0");
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            var plan = ProfileHealth.PlanRepair(report, report.Issues.Where(i => i.CanRepair).Select(i => i.Id));
            var source = new HealthSource();
            var pending = ProfileHealth.StageRepair(root, catalog, plan, source, HealthInspect, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Check(source.Downloads == 1 && pending.Packages.SequenceEqual(new[] { "A-Mod-1.0.0" }) && File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/test.dll")) == "test|2.0.0");
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
        });
        Test("repair rejects changed release contents and removes the untrusted stage", () =>
        {
            var (root, catalog) = HealthFixture();
            File.Delete(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/test.dll"));
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            var plan = ProfileHealth.PlanRepair(report, report.Issues.Where(i => i.CanRepair).Select(i => i.Id));
            Throws<InvalidDataException>(() => ProfileHealth.StageRepair(root, catalog, plan, new HealthSource { Asset = "different release bytes" }, HealthInspect, _ => { }, CancellationToken.None).GetAwaiter().GetResult());
            Check(!Directory.GetDirectories(SafePath.Under(root, "ModGate/staging")).Any() && !File.Exists(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/test.dll")));
        });
        Test("repair refuses stale profile metadata before downloading", () =>
        {
            var (root, catalog) = HealthFixture();
            Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|2.0.0");
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            var plan = ProfileHealth.PlanRepair(report, report.Issues.Where(i => i.CanRepair).Select(i => i.Id));
            var updated = Json.CloneLocal(catalog); updated.Servers[0].Mods.Single(m => m.Guid == "test").Package = "A-Mod-2.0.0";
            Json.Save(Path.Combine(root, "ModGate/profiles.json"), updated);
            var source = new HealthSource();
            Throws<InvalidDataException>(() => ProfileHealth.StageRepair(root, catalog, plan, source, HealthInspect, _ => { }, CancellationToken.None).GetAwaiter().GetResult());
            Check(source.Downloads == 0);
        });
        foreach (var offline in new[] { false, true }) Test("repair restore preserves unrelated plugins and configs for " + (offline ? "offline" : "server") + " profile", () =>
        {
            var (root, catalog) = HealthFixture(offline);
            Put(root, "plugins/External/extra.dll", "external|1.0.0"); Put(root, "config/test.cfg", "keep my settings");
            Put(root, "plugins/ValheimModGate.Active/A-Mod/test.dll", "test|2.0.0");
            var report = ProfileHealth.Verify(root, catalog, catalog.Active, HealthInspect);
            var plan = ProfileHealth.PlanRepair(report, report.Issues.Where(i => i.CanRepair).Select(i => i.Id));
            var pending = ProfileHealth.StageRepair(root, catalog, plan, new HealthSource(), HealthInspect, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            var command = ProfileHealth.PrepareRepairCommand(root, catalog, plan, pending);
            Profiles.Schedule(root, command); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/test.dll")) == "test|1.0.0" && File.ReadAllText(SafePath.Under(root, "plugins/External/extra.dll")) == "external|1.0.0" && File.ReadAllText(SafePath.Under(root, "config/test.cfg")) == "keep my settings");
        });
        Test("support ZIP includes selected diagnostics and redacts credentials and addresses", () =>
        {
            var root = Root();
            Put(root, "LogOutput.log", "stable diagnostic\npassword: secret phrase\nAuthorization: Bearer abc123\nhttps://user:pass@example.com/file?token=private\n170.23.69.121:21344\n[2001:db8::1]\n{\"access_token\":\"private-token\"}\nsecretKey=short-value\napi_key = another-value\nConnectionString \"cv2:hidden-connection\"");
            Put(root, "ModGate/password-vault.json", "vault-secret"); Put(root, "config/test.cfg", "config-secret");
            var destination = Path.Combine(root, "ModGate/reports/support.zip");
            SupportReport.Create(root, destination, new SupportReportOptions { Stage = "download", LastError = "token=last-secret" }, new[] { Mod("test") });
            using var zip = ZipFile.OpenRead(destination);
            var text = string.Join("\n", zip.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
            Check(text.Contains("stable diagnostic") && text.Contains("download") && text.Contains("1.0.0") && !text.Contains("secret phrase") && !text.Contains("abc123") && !text.Contains("user:pass") && !text.Contains("private") && !text.Contains("170.23.69.121") && !text.Contains("2001:db8") && !text.Contains("last-secret") && !text.Contains("vault-secret") && !text.Contains("config-secret") && !text.Contains("short-value") && !text.Contains("another-value") && !text.Contains("hidden-connection"));
            Check(zip.Entries.All(e => !e.FullName.Contains("vault") && !e.FullName.Contains("config")));
        });
        Test("support export rejects arbitrary selected files and never overwrites an existing report", () =>
        {
            var root = Root(); var file = Put(root, "existing.zip", "keep this"); Put(root, "config/test.cfg", "private");
            Throws<InvalidDataException>(() => SupportReport.Create(root, Path.Combine(root, "bad.zip"), new SupportReportOptions { SelectedLogs = new List<string> { "config/test.cfg" } }));
            Throws<IOException>(() => SupportReport.Create(root, file, new SupportReportOptions()));
            Check(File.ReadAllText(file) == "keep this" && !File.Exists(Path.Combine(root, "bad.zip")));
        });
    }
    private sealed class HealthSource : IPackageSource
    {
        public string Asset = "asset"; public int Downloads;
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token) => Task.FromResult(new PackageMetadata { FullName = id.RawFullName });
        public Task Download(PackageId id, string destination, CancellationToken token)
        {
            Downloads++; token.ThrowIfCancellationRequested();
            using (var zip = ZipFile.Open(destination, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("test.dll").Open())) writer.Write("test|1.0.0");
                using (var writer = new StreamWriter(zip.CreateEntry("asset.bundle").Open())) writer.Write(Asset);
            }
            return Task.CompletedTask;
        }
    }
}
