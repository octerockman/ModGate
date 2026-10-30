using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static void DependencySelectionChecks()
    {
        Test("a newly required server mod invalidates an earlier matching profile on the next comparison", () =>
        {
            var old = Mod("installed"); var inventory = new[] { old, Connector() }; var before = Policy(old); before.AllowExtras = true;
            Check(PolicyEngine.Compare(before, inventory).Matches);
            var current = Policy(old, Mod("new-required")); current.AllowExtras = true; var difference = PolicyEngine.Compare(current, inventory);
            Check(!difference.Matches && difference.Missing.Single().Guid == "new-required" && !ProfileSelection.Matches(current, inventory, new HashSet<string>()));
        });
        var app = Mod("app", package: "A-App-1.0.0"); var dependency = Mod("dep"); dependency.Name = "Dependency plugin";
        var policy = Policy(app); policy.AllowExtras = true;
        var packages = new[] { PackageId.Parse(app.Package), PackageId.Parse("A-Library-1.0.0") };
        var original = new HashSet<string>(); var lines = new List<string>();
        var optIns = new HashSet<string> { "A-Library-1.0.0" };
        var selected = ProfileSelection.WithDependencies(policy, packages, policy.Mods, original, _ => new[] { dependency }, lines.Add, optIns);
        Test("explicitly checked dependency packages join the selection and name the consent in review", () =>
        {
            Check(!ProfileSelection.Matches(policy, new[] { app, dependency }, original));
            Check(ProfileSelection.Matches(policy, new[] { app, dependency }, selected));
            Check(original.Count == 0 && lines.Single().Contains("Dependency plugin 1.0.0 [dep]") && lines[0].Contains("A-Library-1.0.0"));
            Check(lines[0].Contains("because its download was checked"));
        });
        Test("resolved dependency plugins cannot join the selection without a checked package", () =>
        {
            var choice = ProfileSelection.WithDependencies(policy, packages, policy.Mods, original, _ => throw new Exception("Unchecked package inspected"), _ => { });
            Check(choice.Count == 0 && !ProfileSelection.Matches(policy, new[] { app, dependency }, choice));
        });
        Test("unselected plugins inside a requested root bundle remain rejected by name", () =>
        {
            var choice = ProfileSelection.WithDependencies(policy, new[] { packages[0] }, policy.Mods, original, _ => throw new Exception("Root bundles are not dependency packages"), _ => { });
            try { ProfileSelection.ValidatePrepared(policy, new[] { app, dependency }, choice); throw new Exception("Unexpected bundle accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("Dependency plugin 1.0.0 [dep]")); }
        });
        Test("a dependency cannot silently select a declared unchecked optional mod", () =>
        {
            var optional = Mod("dep", "optional"); var server = Policy(app, optional); server.AllowExtras = true;
            var choice = ProfileSelection.WithDependencies(server, packages, new[] { app }, original, _ => new[] { optional }, _ => { }, optIns);
            Check(!choice.Contains(optional.Guid) && !ProfileSelection.Matches(server, new[] { app, optional }, choice));
        });
        Test("dependency auto selection retains server-only GUID and name exclusions", () =>
        {
            var server = Policy(app); server.AllowExtras = true; server.ForbiddenPluginGuids.Add("forbidden");
            var byName = Mod("named"); byName.Name = "Server Side Admin";
            var choice = ProfileSelection.WithDependencies(server, packages, server.Mods, original, _ => new[] { Mod("forbidden"), byName }, _ => { }, optIns);
            Check(choice.Count == 0);
        });
        Test("server-forbidden dependency packages are never considered for auto selection", () =>
        {
            var server = Policy(app); server.AllowExtras = true; server.ForbiddenPackages.Add("A-Library");
            var choice = ProfileSelection.WithDependencies(server, packages, server.Mods, original, _ => throw new Exception("Forbidden package inspected"), _ => { }, optIns); Check(choice.Count == 0);
        });
        Test("a server that disallows extras keeps that restriction for dependency plugins", () =>
        {
            var server = Policy(app); var choice = ProfileSelection.WithDependencies(server, packages, server.Mods, original, _ => throw new Exception("Closed server must not auto include"), _ => { }, optIns);
            Check(choice.Count == 0 && !ProfileSelection.Matches(server, new[] { app, dependency }, choice));
        });
        Test("a substituted root version still cannot disguise bundled plugins as dependencies", () =>
        {
            var choice = ProfileSelection.WithDependencies(policy, new[] { PackageId.Parse("hexium:A-App-2.0.0") }, policy.Mods, original, _ => throw new Exception("Root identity changed"), _ => { }); Check(choice.Count == 0);
        });
        Test("each transitive dependency package needs its own checked download", () =>
        {
            var nested = packages.Concat(new[] { PackageId.Parse("A-Detector-1.0.0") }).ToArray();
            var choice = ProfileSelection.WithDependencies(policy, nested, policy.Mods, original, p => new[] { Mod(p.Name) }, _ => { }, optIns);
            Check(choice.SetEquals(new[] { "Library" }));
        });
        Test("dependency handling continues to skip BepInEx bootstrap packages", () =>
        {
            var choice = ProfileSelection.WithDependencies(policy, new[] { PackageId.Parse("denikson-BepInExPack_Valheim-5.4.2351") }, policy.Mods, original, _ => throw new Exception("Bootstrap inspected"), _ => { }); Check(choice.Count == 0);
        });
        Test("selection validation identifies selected but missing plugin GUIDs", () =>
        {
            try { ProfileSelection.ValidatePrepared(policy, new[] { app }, new HashSet<string> { "missing-guid" }); throw new Exception("Missing selection accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("Selected plugins missing: missing-guid")); }
        });
        Test("hard DLL declarations never override an optional checkbox or explicit server requirements", () =>
        {
            var parent = Mod("parent"); var optional = Mod("optional", "optional"); var client = Mod("client", "clientOnly");
            parent.Dependencies.Add(new PluginDependency { Guid = optional.Guid });
            var server = Policy(parent, optional, client);
            Check(DependencyRules.Required(server, new HashSet<string>()).SetEquals(new[] { parent.Guid, client.Guid }));
            Check(ProfileSelection.Build(server, Array.Empty<ModEntry>(), new HashSet<string>()).Downloads.Select(m => m.Guid).SequenceEqual(new[] { parent.Guid, client.Guid }));
        });
        Test("unselected package dependencies are discoverable suggestions and cause no requests", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0", "denikson-BepInExPack_Valheim-5.4.2351" };
            var suggestions = new List<DependencySuggestion>(); var warnings = new List<string>();
            var resolved = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, suggest: suggestions.Add, warn: warnings.Add).GetAwaiter().GetResult();
            Check(resolved.Single().FullName == app.Package && source.Requested.SequenceEqual(new[] { app.Package }));
            Check(suggestions.Single().Package == "A-Library-1.0.0" && warnings.Single().Contains("was not downloaded"));
        });
        Test("checked package dependencies download while their unchecked children remain suggestions", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0" }; source.Dependencies["A-Library-1.0.0"] = new[] { "A-Child-1.0.0" };
            var suggestions = new List<DependencySuggestion>();
            var resolved = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, packageOptIns: optIns, suggest: suggestions.Add).GetAwaiter().GetResult();
            Check(resolved.Select(p => p.FullName).SequenceEqual(new[] { app.Package, "A-Library-1.0.0" }));
            Check(suggestions.Any(s => s.Package == "A-Child-1.0.0") && !source.Requested.Contains("A-Child-1.0.0"));
        });
        Test("dependency suggestions cannot bypass forbidden packages or closed server extras", () =>
        {
            foreach (var forbidden in new[] { false, true })
            {
                var server = Policy(app); server.AllowExtras = forbidden; if (forbidden) server.ForbiddenPackages.Add("A-Library");
                var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0" }; var warnings = new List<string>();
                var resolved = Downloads.Resolve(server, new Comparison { Missing = { app } }, source, CancellationToken.None, packageOptIns: optIns, warn: warnings.Add).GetAwaiter().GetResult();
                Check(resolved.Single().FullName == app.Package && source.Requested.All(p => p != "A-Library-1.0.0") && warnings.Count == 1);
            }
        });
        Test("too-old explicit server pins warn without downloading a replacement or changing the pin", () =>
        {
            var library = Mod("library", package: "A-Library-1.0.0"); var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-2.0.0" }; var warnings = new List<string>();
            var resolved = Downloads.Resolve(Policy(app, library), new Comparison { Missing = { app, library } }, source, CancellationToken.None, warn: warnings.Add).GetAwaiter().GetResult();
            Check(resolved.Any(p => p.FullName == library.Package) && !source.Requested.Contains("A-Library-2.0.0"));
            Check(warnings.Single().Contains("Setup can continue") && library.Package == "A-Library-1.0.0");
        });
        Test("malformed and unverifiable dependency links cannot block or expand a root download", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "not a package", "github:Owner/Repo/v1.0.0/Repo.zip" }; var warnings = new List<string>();
            var resolved = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, warn: warnings.Add).GetAwaiter().GetResult();
            Check(resolved.Single().FullName == app.Package && source.Requested.Count == 1 && warnings.Count == 2);
        });
        Test("missing and old hard DLL dependencies warn but present compatible dependencies do not", () =>
        {
            var parent = Mod("parent"); parent.Dependencies.Add(new PluginDependency { Guid = "library", MinimumVersion = "2.0.0" });
            Check(DependencyRules.Warnings(new[] { parent }).Single().Contains("not selected"));
            Check(DependencyRules.Warnings(new[] { parent, Mod("library") }).Single().Contains("selected version is 1.0.0"));
            Check(!DependencyRules.Warnings(new[] { parent, Mod("library", version: "2.0.0") }).Any());
        });
        Test("a checked dependency already selected from the installation is reused without another download", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0" };
            var library = Mod("library", package: "A-Library-1.0.0"); library.LocalFile = "installed/library.dll";
            var result = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, new[] { library }, optIns).GetAwaiter().GetResult();
            Check(result.Single().FullName == app.Package && source.Requested.SequenceEqual(new[] { app.Package }));
        });
        Test("multiple parents coalesce a checked package to their highest declared version in normal and override setup", () =>
        {
            var other = Mod("other", package: "A-Other-1.0.0"); var server = Policy(app, other); server.AllowExtras = true;
            foreach (var partial in new[] { false, true })
            {
                var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0" }; source.Dependencies[other.Package] = new[] { "A-Library-2.0.0" };
                var diff = new Comparison { Missing = { app, other } };
                var resolved = partial ? PartialSetup.Resolve(server, diff, source, CancellationToken.None, Array.Empty<ModEntry>(), new List<string>(), optIns).GetAwaiter().GetResult() :
                    Downloads.Resolve(server, diff, source, CancellationToken.None, packageOptIns: optIns).GetAwaiter().GetResult();
                Check(resolved.Count == 3 && resolved.Single(p => p.Key == "A-Library").Version == "2.0.0" && !source.Requested.Contains("A-Library-1.0.0"));
            }
        });
        Test("a later transitive recommendation replaces an earlier selected dependency version without duplicate downloads", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0", "A-Chain-1.0.0" };
            source.Dependencies["A-Chain-1.0.0"] = new[] { "A-Next-1.0.0" }; source.Dependencies["A-Next-1.0.0"] = new[] { "A-Library-2.0.0" };
            var choices = new HashSet<string>(optIns) { "A-Chain-1.0.0", "A-Next-1.0.0" };
            var resolved = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, packageOptIns: choices).GetAwaiter().GetResult();
            Check(resolved.Single(p => p.Key == "A-Library").Version == "2.0.0" && resolved.Select(p => p.Key).Distinct().Count() == resolved.Count);
            Check(source.Requested.Contains("A-Library-1.0.0") && source.Requested.Contains("A-Library-2.0.0"));
        });
        Test("a higher checked recommendation is respected without opting in any other package", () =>
        {
            var source = new FakeSource(); source.Dependencies[app.Package] = new[] { "A-Library-1.0.0", "A-Unchecked-1.0.0" };
            var resolved = Downloads.Resolve(policy, new Comparison { Missing = { app } }, source, CancellationToken.None, packageOptIns: new HashSet<string> { "A-Library-3.0.0" }).GetAwaiter().GetResult();
            Check(resolved.Single(p => p.Key == "A-Library").Version == "3.0.0" && source.Requested.All(p => !p.Contains("Unchecked")));
        });
    }
}
