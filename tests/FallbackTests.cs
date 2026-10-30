using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModGate;

internal static partial class Program
{
    private static async Task FallbackChecks()
    {
        var app = Mod("app", package: "A-App-1.0.0"); var policy = Policy(app); var missing = new Comparison(); missing.Missing.Add(app);
        var source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package };
        var issues = new List<string>(); var lines = new List<string>(); var wrapped = new ProfilePackageSource(source, lines.Add, issues);
        var resolved = await Downloads.Resolve(policy, missing, wrapped, CancellationToken.None);
        Test("available exact releases do not invoke latest fallback", () => Check(resolved.Single().FullName == app.Package && source.LatestCalls == 0 && issues.Count == 0));
        source = new LatestFixture(); source.LatestIds[app.Package] = "A-App-2.0.0"; source.Entries["A-App-2.0.0"] = new PackageMetadata { FullName = "A-App-2.0.0", Dependencies = new List<string> { "A-Library-2.0.0" } }; source.Entries["A-Library-2.0.0"] = new PackageMetadata { FullName = "A-Library-2.0.0" };
        issues = new List<string>(); lines = new List<string>(); wrapped = new ProfilePackageSource(source, lines.Add, issues);
        var suggestions = new List<DependencySuggestion>();
        resolved = await Downloads.Resolve(policy, missing, wrapped, CancellationToken.None, suggest: suggestions.Add);
        Test("unavailable exact releases use latest metadata and suggest its new dependencies without downloading them", () => Check(resolved.Single().FullName == "A-App-2.0.0" && suggestions.Single().Package == "A-Library-2.0.0" && issues.Single().Contains("A-App-1.0.0") && issues.Single().Contains("A-App-2.0.0")));
        var actual = new[] { Mod("app", version: "2.0.0"), Connector() }; var review = wrapped.ReviewPolicy(policy, actual);
        Test("fallback review accepts the replacement without rewriting the server requirement", () => Check(PolicyEngine.Compare(review, actual).Matches && !PolicyEngine.Compare(policy, actual).Matches && policy.Mods.Single().Version == "1.0.0"));
        source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package, Dependencies = new List<string> { "A-Library-2.0.0" } }; source.Entries["A-Library-1.0.0"] = new PackageMetadata { FullName = "A-Library-1.0.0" };
        var library = Mod("library", package: "A-Library-1.0.0"); issues = new List<string>(); lines = new List<string>(); wrapped = new ProfilePackageSource(source, lines.Add, issues);
        resolved = await Downloads.Resolve(Policy(app, library), missing, wrapped, CancellationToken.None, warn: lines.Add);
        Test("dependency warnings identify the requesting package minimum and selected server pin without blocking", () => Check(resolved.Any(p => p.FullName == library.Package) && lines.Any(l => l.Contains("A-App-1.0.0") && l.Contains("A-Library >= 2.0.0") && l.Contains("A-Library-1.0.0") && l.Contains("Setup can continue"))));
        source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package, Dependencies = new List<string> { "A-Library-2.0.0" } }; source.LatestIds[library.Package] = "A-Library-3.0.0"; source.Entries["A-Library-3.0.0"] = new PackageMetadata { FullName = "A-Library-3.0.0" };
        wrapped = new ProfilePackageSource(source, _ => { }, new List<string>()); resolved = await Downloads.Resolve(Policy(app, library), missing, wrapped, CancellationToken.None);
        Test("a missing old dependency pin can fall forward to a sufficient latest release", () => Check(resolved.Any(p => p.FullName == "A-Library-3.0.0")));
        source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package }; source.LatestIds[app.Package] = "A-App-2.0.0"; source.Entries["A-App-2.0.0"] = new PackageMetadata { FullName = "A-App-2.0.0" }; source.MissingDownloads.Add(app.Package);
        wrapped = new ProfilePackageSource(source, _ => { }, new List<string>());
        var root = Root(); var operation = Guid.NewGuid().ToString("N");
        Throws<PackageResolutionChangedException>(() => Downloads.Stage(root, new[] { PackageId.Parse(app.Package) }, wrapped, _ => { }, CancellationToken.None, operation).GetAwaiter().GetResult());
        PartialSetup.Discard(root, operation); resolved = await Downloads.Resolve(policy, missing, wrapped, CancellationToken.None);
        Test("a removed download triggers fresh dependency resolution for the replacement", () => Check(resolved.Single().FullName == "A-App-2.0.0" && !Directory.Exists(SafePath.Under(root, "ModGate/staging/" + operation))));
        source = new LatestFixture(); source.LatestIds[app.Package] = "Other-Foreign-2.0.0"; source.Entries["Other-Foreign-2.0.0"] = new PackageMetadata { FullName = "Other-Foreign-2.0.0" }; wrapped = new ProfilePackageSource(source, _ => { }, new List<string>());
        Throws<InvalidDataException>(() => wrapped.Resolve(PackageId.Parse(app.Package), false, CancellationToken.None).GetAwaiter().GetResult());
        Test("latest fallback cannot replace the package owner or identity", () => Check(source.LatestCalls == 1));
        source = new LatestFixture(); wrapped = new ProfilePackageSource(source, _ => { }, new List<string>());
        Throws<PackageNotFoundException>(() => wrapped.Resolve(PackageId.Parse("github:Author/App/v1.0.0/App.zip"), false, CancellationToken.None).GetAwaiter().GetResult());
        Test("manifest fallback assets cannot silently switch to another release", () => Check(source.LatestCalls == 0));
        source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package, IsActive = false }; source.LatestIds[app.Package] = "A-App-2.0.0"; source.Entries["A-App-2.0.0"] = new PackageMetadata { FullName = "A-App-2.0.0" };
        wrapped = new ProfilePackageSource(source, _ => { }, new List<string>()); resolved = await Downloads.Resolve(policy, missing, wrapped, CancellationToken.None);
        Test("inactive exact releases use an available latest release", () => Check(resolved.Single().FullName == "A-App-2.0.0"));
        source = new LatestFixture(); source.Entries[app.Package] = new PackageMetadata { FullName = app.Package }; source.LatestIds[app.Package] = "A-App-2.0.0"; source.Entries["A-App-2.0.0"] = new PackageMetadata { FullName = "A-App-2.0.0" };
        lines = new List<string>(); wrapped = new ProfilePackageSource(source, lines.Add, new List<string>());
        var wanted = Mod("app", version: "2.0.0", package: app.Package);
        var repaired = await wrapped.RepairPluginVersions(Policy(wanted), new[] { Mod("app") }, CancellationToken.None);
        Test("an available archive with the wrong DLL version tries the latest package", () => Check(repaired && wrapped.Actual(PackageId.Parse(app.Package)).FullName == "A-App-2.0.0" && lines.Any(l => l.Contains("plugin 1.0.0") && l.Contains("requires 2.0.0"))));
        repaired = await wrapped.RepairPluginVersions(Policy(wanted), new[] { Mod("app") }, CancellationToken.None);
        Test("a substituted package is not downloaded repeatedly for a persistent DLL mismatch", () => Check(!repaired && source.LatestCalls == 1));
        source = new LatestFixture(); wrapped = new ProfilePackageSource(source, _ => { }, new List<string>());
        repaired = await wrapped.RepairPluginVersions(policy, new[] { Mod("app") }, CancellationToken.None);
        Test("a matching DLL needs no replacement or repository request", () => Check(!repaired && source.LatestCalls == 0));
        Test("setup log records dependency details and the complete stop exception", () =>
        {
            var log = new SetupLog(Root()); log.Start("server profile"); log.Write("A-App-1.0.0 requires A-Library >= 2.0.0; server pins 1.0.0"); log.Failure(new IOException("fixture stopped", new InvalidDataException("missing exact release")));
            var text = File.ReadAllText(log.Path); Check(text.Contains("A-Library >= 2.0.0") && text.Contains("STOPPED") && text.Contains("missing exact release"));
        });
    }
    private sealed class LatestFixture : IUpdateSource
    {
        public readonly Dictionary<string, PackageMetadata> Entries = new(); public readonly Dictionary<string, string> LatestIds = new(); public readonly HashSet<string> MissingDownloads = new(); public int LatestCalls;
        public Task<PackageId> Latest(PackageId id, CancellationToken token) { LatestCalls++; if (!LatestIds.TryGetValue(id.FullName, out var next)) throw new PackageNotFoundException(id.FullName); return Task.FromResult(PackageId.Parse(next)); }
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token) { token.ThrowIfCancellationRequested(); if (!Entries.TryGetValue(id.FullName, out var entry)) throw new PackageNotFoundException(id.FullName); return Task.FromResult(entry); }
        public Task Download(PackageId id, string path, CancellationToken token) { if (MissingDownloads.Contains(id.FullName)) throw new PackageNotFoundException(id.FullName); File.WriteAllText(path, "fixture"); return Task.CompletedTask; }
    }
}
