using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static void AuthorityChecks()
    {
        ModEntry Octo()
        {
            var dll = Mod("OctoPack", version: "1.7.4");
            dll.LocalManifest = PackageManifest.Parse("{\"name\":\"OctoPack\",\"version_number\":\"1.7.3\",\"website_url\":\"https://thunderstore.io/c/valheim/p/Bascinet/OctoPack/\",\"dependencies\":[]}");
            return dll;
        }
        Test("DLL identity and required version remain authoritative while package versions stay independent", () =>
        {
            var old = Mod("OctoPack", "optional", "1.7.3", "Bascinet-OctoPack-1.7.3"); old.Automatic = false; old.AutoUpdate = true;
            var settings = new ServerManifest { Mods = new() { old } }; var before = Json.Write(settings);
            var result = AutoSetup.Reconcile(settings, new[] { Octo() }, Array.Empty<string>());
            var row = result.Mods.Single(); Check(row.Guid == "OctoPack" && row.Version == "1.7.4" && row.Package == "Bascinet-OctoPack-1.7.3" && row.Side == "optional" && !row.Automatic && row.AutoUpdate == true && Json.Write(settings) == before);
            for (var i = 0; i < 3; i++) result = AutoSetup.Reconcile(result, new[] { Octo() }, Array.Empty<string>());
            Check(result.Mods.Count == 1 && result.Mods[0].Version == "1.7.4");
        });
        Test("manifest package versions identify releases without replacing the DLL required version", () =>
        {
            var dll = Octo(); dll.LocalPackageHints.Add("Bascinet-OctoPack-1.7.3");
            var source = new FakeLookup(); source.Catalogs["thunderstore"] = new() { new CatalogEntry { Owner = "Bascinet", Name = "OctoPack", Versions = new() { new PackageMetadata { FullName = "Bascinet-OctoPack-1.7.3" }, new PackageMetadata { FullName = "Bascinet-OctoPack-1.7.4" } } } };
            var report = AutoLookup.Run(new[] { dll }, Root(), source, (_, _) => throw new Exception("Trusted lookup must not inspect archives"), CancellationToken.None).GetAwaiter().GetResult();
            Check(report.Mods.Single().Package == "Bascinet-OctoPack-1.7.3");
        });
        Test("a manifest alias and existing DLL entry merge without losing owner choices", () =>
        {
            var dll = Octo(); dll.Guid = "author.octopack";
            var alias = Mod("OctoPack", "required", "1.7.3"); alias.Name = "OctoPack"; alias.Automatic = false; alias.DownloadUrl = "https://thunderstore.io/package/download/Bascinet/OctoPack/1.7.4/";
            var canonical = Mod(dll.Guid, "optional", "1.7.3"); canonical.AutoUpdate = false;
            var next = AutoSetup.Reconcile(new ServerManifest { Mods = new() { alias, canonical } }, new[] { dll }, Array.Empty<string>());
            Check(next.Mods.Count == 1 && next.Mods[0].Guid == dll.Guid && next.Mods[0].Side == "optional" && next.Mods[0].AutoUpdate == false && next.Mods[0].DownloadUrl == alias.DownloadUrl);
        });
        Test("a single old manifest identity is updated to its actual DLL identity", () =>
        {
            var dll = Octo(); dll.Guid = "author.octopack"; var alias = Mod("OctoPack", "serverOnly", "1.7.3"); alias.AutoUpdate = false; alias.NexusUrl = "https://www.nexusmods.com/valheim/mods/123";
            var row = AutoSetup.Reconcile(new ServerManifest { Mods = new() { alias } }, new[] { dll }, Array.Empty<string>()).Mods.Single();
            Check(row.Guid == dll.Guid && row.Name == dll.Name && row.Version == dll.Version && row.Side == "serverOnly" && row.NexusUrl == alias.NexusUrl && row.AutoUpdate == false);
        });
        Test("duplicate exact config entries collapse before policy validation", () =>
        {
            var dll = Octo(); var rows = new ServerManifest { Mods = new() { Mod(dll.Guid), Mod(dll.Guid, version: "0.0.1") } };
            var result = AutoSetup.Reconcile(rows, new[] { dll }, Array.Empty<string>());
            Check(result.Mods.Count == 1 && PolicyEngine.Publish(result, new[] { dll }).Mods.Count == 2);
        });
        Test("different DLL GUIDs sharing a bundle manifest remain separate plugins", () =>
        {
            var first = Octo(); var second = Octo(); second.Guid = "OtherPlugin";
            var next = AutoSetup.Reconcile(new ServerManifest { Mods = new() { Mod(first.Guid), Mod(second.Guid) } }, new[] { first, second }, Array.Empty<string>());
            Check(next.Mods.Count == 2 && next.Mods.Select(m => m.Guid).Distinct().Count() == 2);
        });
        Test("package versions remain separate for all DLL version formats", () =>
        {
            var dll = Octo(); dll.Version = "1.7.4.0"; Check(AutoSetup.DllPackage("Bascinet-OctoPack-1.7.3", dll) == "Bascinet-OctoPack-1.7.3");
            dll.Version = "1.7.4.1"; Check(AutoSetup.DllPackage("Bascinet-OctoPack-1.7.3", dll) == "Bascinet-OctoPack-1.7.3");
        });
        Test("manifest source version stays separate and cannot override the DLL requirement", () =>
        {
            var dll = Octo(); dll.LocalManifest = PackageManifest.Parse("{\"name\":\"OctoPack\",\"version_number\":\"1.7.3\",\"website_url\":\"https://github.com/octerockman/OctoPack\",\"dependencies\":[]}");
            var row = AutoSetup.Reconcile(new ServerManifest(), new[] { dll }, Array.Empty<string>()).Mods.Single(); Check(row.Version == "1.7.4" && row.ManifestSource?.Version == "1.7.3");
        });
        Test("duplicate cleanup keeps one download method instead of combining incompatible links", () =>
        {
            var dll = Octo(); var primary = Mod("OctoPack", version: "1.7.4"); primary.DownloadUrl = "https://thunderstore.io/package/download/Bascinet/OctoPack/1.7.4/";
            var alias = Mod("old.alias"); alias.Name = "OctoPack"; alias.NexusUrl = "https://www.nexusmods.com/valheim/mods/123";
            var next = AutoSetup.Reconcile(new ServerManifest { Mods = new() { primary, alias } }, new[] { dll }, Array.Empty<string>());
            Check(next.Mods.Single().NexusUrl == "" && next.Mods[0].DownloadUrl == primary.DownloadUrl); Throws<System.IO.InvalidDataException>(() => PolicyEngine.Publish(next, new[] { dll }));
        });
    }
}
