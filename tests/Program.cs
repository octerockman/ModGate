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
    private static int passed;
    private static readonly string scratch = Path.Combine(Path.GetTempPath(), "ValheimModGate-tests-" + Guid.NewGuid().ToString("N"));
    private static ModEntry Mod(string id, string side = "required", string version = "1.0.0", string package = "") => new ModEntry { Guid = id, Name = id, Version = version, Side = side, Package = package };
    private static ModEntry Connector() => Mod(Constants.Guid, version: Constants.Version);
    private static Policy Policy(params ModEntry[] mods) => new Policy { ServerName = "Test server", Mods = mods.ToList() };
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static string Root() { var path = Path.Combine(scratch, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static string Put(string root, string relative, string text)
    {
        var path = SafePath.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }
    private static string Archive(string root, params (string name, string text)[] files)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var file in files) using (var writer = new StreamWriter(zip.CreateEntry(file.name).Open())) writer.Write(file.text);
        return path;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--native-http-fixture") return NativeFixtureOutput(args[1]);
        try { await Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run(string[] args)
    {
        Directory.CreateDirectory(scratch);
        if (args.Contains("--creator-support-only")) { CreatorSupportChecks(); Console.WriteLine("All " + passed + " creator support checks passed."); return; }
        if (args.Contains("--self-update-only")) { await SelfUpdateChecks(); Console.WriteLine("All " + passed + " self-update checks passed."); return; }
        if (args.Contains("--self-update-helper-only")) { SelfUpdateHelperChecks(); Console.WriteLine("All " + passed + " self-update helper checks passed."); return; }
        if (args.Length == 2 && args[0] == "--self-update-live") { await SelfUpdateLiveCheck(args[1]); return; }
        if (args.Contains("--publishing-layout-only")) { PublishingLayoutChecks(); Console.WriteLine("All " + passed + " publishing layout checks passed."); return; }
        if (args.Contains("--admin-config-only")) { AdminConfigChecks(); Console.WriteLine("All " + passed + " server config checks passed."); return; }
        if (args.Contains("--admin-security-only")) { AdminConfigSecurityChecks(); Console.WriteLine("All " + passed + " admin security checks passed."); return; }
        if (args.Contains("--health-only")) { ProfileHealthChecks(); Console.WriteLine("All " + passed + " health checks passed."); return; }
        if (args.Contains("--server-health-only")) { ServerHealthChecks(); Console.WriteLine("All " + passed + " server health checks passed."); return; }
        if (args.Contains("--config-editor-only")) { ConfigEditorChecks(); Console.WriteLine("All " + passed + " config editor checks passed."); return; }
        if (args.Contains("--snapshots-only")) { ProfileSnapshotChecks(); Console.WriteLine("All " + passed + " snapshot checks passed."); return; }
        if (args.Contains("--manifests-only")) { ManualManifestChecks(); Console.WriteLine("All " + passed + " manual manifest checks passed."); return; }
        if (args.Contains("--config-only")) { ServerConfigChecks(); Console.WriteLine("All " + passed + " server config checks passed."); return; }
        if (args.Contains("--031-only")) { await Product031Checks(); Console.WriteLine("All " + passed + " new product checks passed."); return; }
        if (args.Contains("--product-only")) { await Product030Checks(); Console.WriteLine("All " + passed + " product checks passed."); return; }
        if (args.Contains("--dependencies-only")) { DependencySelectionChecks(); Console.WriteLine("All " + passed + " dependency selection checks passed."); return; }
        if (args.Contains("--authority-only")) { AuthorityChecks(); Console.WriteLine("All " + passed + " DLL authority checks passed."); return; }
        if (args.Contains("--large-staging")) { await LargeStagingChecks(); Console.WriteLine("All " + passed + " large staging checks passed."); return; }
        if (args.Contains("--restart-only")) { RestartChecks(); Console.WriteLine("All " + passed + " restart checks passed."); return; }
        if (args.Contains("--editor-only")) { ProfileEditChecks(); Console.WriteLine("All " + passed + " profile editor checks passed."); return; }
        if (args.Contains("--fallback-only")) { await FallbackChecks(); Console.WriteLine("All " + passed + " fallback/log checks passed."); return; }
        await SelfUpdateChecks();
        CreatorSupportChecks();
        AdminConfigChecks();
        PublishingLayoutChecks();
        AdminConfigSecurityChecks();
        ServerHealthChecks();
        ConfigEditorChecks();
        ProfileHealthChecks();
        ProfileSnapshotChecks();
        ManualManifestChecks();
        ServerConfigChecks();
        await Product031Checks();
        await Product030Checks();
        await FallbackChecks();
        DependencySelectionChecks();
        AuthorityChecks();
        ProfileEditChecks();
        if (!args.Contains("--quick")) await StagingReceiptChecks();
        RestartChecks();
        Test("server-only mods are omitted from client requirements", () =>
        {
            var required = Mod("shared", package: "Author-Shared-1.0.0");
            var server = Mod("admin", "serverOnly", package: "Author-Admin-1.0.0");
            var policy = PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { required, server } }, new[] { required, server });
            Check(policy.Mods.All(m => m.Guid != "admin"));
            Check(policy.ForbiddenPackages.SequenceEqual(new[] { "Author-Admin" }));
            Check(PolicyEngine.Compare(policy, new[] { required, Connector() }).Matches);
        });
        Test("server-only mods work without a Thunderstore package", () =>
        {
            var server = Mod("private-admin", "serverOnly");
            var policy = PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { server } }, new[] { server });
            Check(policy.Mods.Count == 1 && policy.Mods[0].Guid == Constants.Guid);
        });
        Test("unknown mod classification fails closed", () => Throws<InvalidDataException>(() => PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { Mod("unknown", "unclassified") } }, Array.Empty<ModEntry>())));
        Test("new server mods require classification", () => Throws<InvalidDataException>(() => PolicyEngine.Publish(new ServerManifest(), new[] { Mod("new-server-mod") })));
        Test("mixed client and server-only package is rejected", () => Throws<InvalidDataException>(() => PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { Mod("shared", package: "A-Pack-1.0.0"), Mod("server", "serverOnly", package: "A-Pack-1.0.0") } }, Array.Empty<ModEntry>())));
        Test("server runtime version must match declared version", () => Throws<InvalidDataException>(() => PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { Mod("a") } }, new[] { Mod("a", version: "2.0.0") })));
        Test("client-only requirements can be absent from dedicated server", () => Check(PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { Mod("client-only", package: "A-Client-1.0.0") } }, Array.Empty<ModEntry>()).Mods.Count == 2));
        Test("optional client mods are not offered when absent", () => Check(PolicyEngine.Compare(Policy(Mod("optional", "optional")), Array.Empty<ModEntry>()).Matches));
        Test("optional installed mods still require the declared version", () => Check(PolicyEngine.Compare(Policy(Mod("optional", "optional")), new[] { Mod("optional", version: "2.0.0") }).WrongVersion.Count == 1));
        Test("missing mods are detected", () => Check(PolicyEngine.Compare(Policy(Mod("a")), Array.Empty<ModEntry>()).Missing.Count == 1));
        Test("wrong versions are detected", () => Check(PolicyEngine.Compare(Policy(Mod("a")), new[] { Mod("a", version: "1.0.1") }).WrongVersion.Count == 1));
        Test("three and four component versions normalize", () => Check(PolicyEngine.Compare(Policy(Mod("a")), new[] { Mod("a", version: "1.0.0.0") }).Matches));
        Test("exact mode detects extra mods", () => Check(PolicyEngine.Compare(Policy(), new[] { Mod("extra") }).Extra.Count == 1));
        Test("allow-extra mode permits client conveniences", () => Check(PolicyEngine.Compare(new Policy { AllowExtras = true }, new[] { Mod("extra") }).Matches));
        Test("duplicate plugin identities are rejected", () => Throws<InvalidDataException>(() => PolicyEngine.Compare(Policy(), new[] { Mod("a"), Mod("a") })));
        Test("client rejects wire policies containing server-only mods", () => Throws<InvalidDataException>(() => PolicyEngine.ValidatePolicy(Policy(Mod("admin", "serverOnly")))));
        Test("local paths are never sent over the network", () =>
        {
            var entry = Mod("a"); entry.LocalFile = "C:/private/path.dll";
            var json = Json.Write(new InventoryReport { Mods = new List<ModEntry> { entry } });
            Check(!json.Contains("private")); Check(Json.Read<InventoryReport>(json).Mods[0].Guid == "a");
        });
        Test("oversized messages are rejected", () => Throws<InvalidDataException>(() => Json.Read<Policy>(new string(' ', Constants.MaxJson + 1))));
        Test("generated server JSON is indented with blank lines between mods", () =>
        {
            var manifest = new ServerManifest { Mods = new List<ModEntry> { Mod("one"), Mod("two", "serverOnly") } };
            var formatted = Json.WriteReadable(manifest);
            Check(formatted.Contains("    },\n\n    {"));
            var parsed = Json.Read<ServerManifest>(formatted);
            Check(parsed.Mods.Count == 2 && parsed.Mods[1].Side == "serverOnly");
        });
        foreach (var bad in new[] { "../escape", "a/../escape", "C:/escape", "/escape", "a//b", "a/./b", "a/b.", "a/b ", "a:b" })
            Test("reject unsafe path " + bad, () => Throws<InvalidDataException>(() => SafePath.Under(scratch, bad)));
        Test("only exact package IDs are accepted", () =>
        {
            Check(PackageId.Parse("Author-Package_Name-1.2.3").Key == "Author-Package_Name");
            foreach (var bad in new[] { "https://evil/a.zip", "A-P-latest", "A-P-1.0", "A-P-1.0.0/../a" }) Throws<InvalidDataException>(() => PackageId.Parse(bad));
        });
        Test("plugin/config archives retain assets and skip package metadata", () =>
        {
            var root = Root(); var stage = Path.Combine(root, "stage");
            Downloads.Extract(Archive(root, ("BepInEx/plugins/MyMod/a.dll", "code"), ("BepInEx/plugins/MyMod/art.bundle", "art"), ("BepInEx/config/a.cfg", "setting"), ("manifest.json", "{}")), stage, PackageId.Parse("A-Mod-1.0.0"), 100000);
            Check(File.Exists(Path.Combine(stage, "packages/A-Mod/MyMod/a.dll")) && File.Exists(Path.Combine(stage, "config/a.cfg")));
            Check(!File.Exists(Path.Combine(stage, "packages/A-Mod/manifest.json")));
        });
        Test("archive traversal is blocked", () => { var root = Root(); Throws<InvalidDataException>(() => Downloads.Extract(Archive(root, ("../escape.dll", "bad")), Path.Combine(root, "stage"), PackageId.Parse("A-P-1.0.0"), 1000)); });
        Test("archive expansion is bounded", () => { var root = Root(); Throws<InvalidDataException>(() => Downloads.Extract(Archive(root, ("large.dll", new string('a', 100))), Path.Combine(root, "stage"), PackageId.Parse("A-P-1.0.0"), 50)); });
        foreach (var bad in new[] { "BepInEx/patchers/a.dll", "BepInEx/core/a.dll", "start.cmd", "BepInEx/config/ModGate/server-mods.json" })
            Test("unsupported or protected package content " + bad, () => { var root = Root(); Throws<InvalidDataException>(() => Downloads.Extract(Archive(root, (bad, "x")), Path.Combine(root, "stage"), PackageId.Parse("A-P-1.0.0"), 1000)); });
        Test("case-insensitive archive collisions are blocked", () => { var root = Root(); Throws<InvalidDataException>(() => Downloads.Extract(Archive(root, ("a.dll", "x"), ("A.dll", "y")), Path.Combine(root, "stage"), PackageId.Parse("A-P-1.0.0"), 1000)); });

        var fake = new FakeSource();
        fake.Dependencies["A-Shared-1.0.0"] = new[] { "A-ServerAdmin-1.0.0" };
        var blocked = Policy(Mod("shared", package: "A-Shared-1.0.0")); blocked.ForbiddenPackages.Add("A-ServerAdmin");
        Test("server-only transitive dependency is excluded without blocking the selected root", () => Check(Downloads.Resolve(blocked, PolicyEngine.Compare(blocked, Array.Empty<ModEntry>()), fake, CancellationToken.None).GetAwaiter().GetResult().Single().FullName == "A-Shared-1.0.0"));
        Check(!fake.Requested.Contains("A-ServerAdmin-1.0.0"));
        fake = new FakeSource(); fake.Dependencies["A-Shared-1.0.0"] = new[] { "A-Library-1.0.0", "denikson-BepInExPack_Valheim-5.4.2333" };
        var shared = Policy(Mod("shared", package: "A-Shared-1.0.0"), Mod("library", package: "A-Library-2.0.0"));
        var resolved = await Downloads.Resolve(shared, PolicyEngine.Compare(shared, Array.Empty<ModEntry>()), fake, CancellationToken.None);
        Test("dependencies follow server pins and skip installed bootstrap loader", () => Check(resolved.Select(p => p.FullName).SequenceEqual(new[] { "A-Shared-1.0.0", "A-Library-2.0.0" })));
        Test("BepInEx dependency is never fetched", () => Check(fake.Requested.All(p => !p.Contains("BepInEx"))));
        fake.Dependencies["A-Library-2.0.0"] = new[] { "denikson-BepInExPack_Valheim-5.4.2202" };
        resolved = await Downloads.Resolve(shared, PolicyEngine.Compare(shared, Array.Empty<ModEntry>()), fake, CancellationToken.None);
        Test("different BepInEx dependency versions are all ignored", () => Check(resolved.All(p => !p.Name.Contains("BepInEx"))));

        Test("four server slots plus offline switch without downloading again", () =>
        {
            var root = ProfileRoot();
            for (var slot = 1; slot <= 4; slot++)
            {
                var command = PrepareProfile(root, slot, (char)('a' + slot - 1));
                Profiles.Schedule(root, command); Profiles.Apply(root, _ => { });
                Check(Profiles.Load(root).Active == "server-" + slot);
                Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-" + slot);
                Put(root, "config/server.cfg", "settings-" + slot);
            }
            var catalog = Profiles.Load(root);
            Check(catalog.Servers.Count == 4 && Profiles.SlotFor(catalog, new string('z', 64)) == null);
            Check(Profiles.SlotFor(catalog, new string('b', 64)) == "server-2");
            Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "server-1" }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-1");
            Check(File.ReadAllText(SafePath.Under(root, "config/server.cfg")) == "settings-1");
            Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "offline" }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
            Check(File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
            Check(!Directory.Exists(SafePath.Under(root, "plugins/ValheimModGate.Active")));
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate/ValheimModGate.dll")) == "connector");
            Check(File.ReadAllText(SafePath.Under(root, "config/BepInEx.cfg")) == "loader");
        });
        Test("replacing a full server slot preserves offline and other slots", () =>
        {
            var root = ProfileRoot();
            for (var slot = 1; slot <= 4; slot++) { Profiles.Schedule(root, PrepareProfile(root, slot, (char)('a' + slot - 1))); Profiles.Apply(root, _ => { }); }
            Profiles.Schedule(root, PrepareProfile(root, 2, 'z')); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Servers.Count == 4 && Profiles.Load(root).Servers.Single(s => s.Slot == "server-2").ServerKey == new string('z', 64));
            Check(File.Exists(SafePath.Under(root, "ModGate/profiles/server-1/plugins/ValheimModGate.Active/A-Mod/mod.dll")));
            Check(File.ReadAllText(SafePath.Under(root, "ModGate/profiles/offline/plugins/Original/original.dll")) == "original");
        });
        Test("remove-all clears downloaded mods and restores original offline files", () =>
        {
            var root = ProfileRoot();
            Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            Profiles.Schedule(root, PrepareProfile(root, 2, 'b')); Profiles.Apply(root, _ => { });
            Put(root, "ModGate/staging/unconfirmed/package.zip", "partial-download");
            Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll" }); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "offline" && Profiles.Load(root).Servers.Count == 0);
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
            Check(File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
            Check(File.Exists(SafePath.Under(root, "plugins/ValheimModGate/ValheimModGate.dll")));
            Check(!Directory.Exists(SafePath.Under(root, "ModGate/profiles/server-1")) && !Directory.Exists(SafePath.Under(root, "ModGate/profiles/server-2")));
            Check(!Directory.Exists(SafePath.Under(root, "ModGate/staging")));
        });
        Test("fifth server slot is rejected", () => Throws<InvalidDataException>(() => Profiles.Schedule(ProfileRoot(), new ProfileCommand { Kind = "switch", Target = "server-5" })));
        Test("BepInEx cannot be inserted into a staged profile", () =>
        {
            var root = ProfileRoot(); var command = PrepareProfile(root, 1, 'a'); command.Install!.Packages.Add("denikson-BepInExPack_Valheim-5.4.2333");
            Throws<InvalidDataException>(() => Profiles.Schedule(root, command));
        });
        for (var failAt = 0; failAt < 7; failAt++)
        {
            var fail = failAt;
            Test("profile install rolls back after move " + fail, () =>
            {
                var root = ProfileRoot(); var command = PrepareProfile(root, 1, 'a'); Profiles.Schedule(root, command);
                var count = 0;
                try { Profiles.Apply(root, _ => { }, i => { count++; if (i == fail) throw new IOException("test interruption"); }); }
                catch (IOException)
                {
                    Check(Profiles.Load(root).Active == "offline");
                    Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
                    Check(File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
                    Profiles.Apply(root, _ => { });
                }
                Check(count > 0 && Profiles.Load(root).Active == "server-1");
            });
        }
        Test("active server updates preserve its own config and offline setup", () =>
        {
            var root = ProfileRoot(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            Put(root, "config/server.cfg", "personalized");
            var update = PrepareProfile(root, 1, 'a');
            var stagedFile = SafePath.Under(root, "ModGate/staging/" + update.Install!.Id + "/packages/A-Mod/mod.dll");
            File.WriteAllText(stagedFile, "updated");
            update.Install.Files.Single(f => f.Path == "packages/A-Mod/mod.dll").Sha256 = SafePath.Hash(stagedFile);
            Profiles.Schedule(root, update); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "config/server.cfg")) == "personalized");
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "updated");
            Check(File.ReadAllText(SafePath.Under(root, "ModGate/profiles/offline/plugins/Original/original.dll")) == "original");
        });
        Test("tampered staged profile fails before moving offline files", () =>
        {
            var root = ProfileRoot(); var command = PrepareProfile(root, 1, 'a'); Profiles.Schedule(root, command);
            Put(root, "ModGate/staging/" + command.Install!.Id + "/packages/A-Mod/mod.dll", "tampered");
            Throws<InvalidDataException>(() => Profiles.Apply(root, _ => { }));
            Check(Profiles.Load(root).Active == "offline" && File.Exists(SafePath.Under(root, "plugins/Original/original.dll")));
        });
        Test("unlisted staged profile files are rejected", () =>
        {
            var root = ProfileRoot(); var command = PrepareProfile(root, 1, 'a'); Profiles.Schedule(root, command);
            Put(root, "ModGate/staging/" + command.Install!.Id + "/packages/A-Mod/unlisted.dll", "extra");
            Throws<InvalidDataException>(() => Profiles.Apply(root, _ => { }));
        });
        foreach (var kind in new[] { "switch", "removeAll" })
        {
            for (var failAt = 0; failAt < 5; failAt++)
            {
                var fail = failAt;
                Test(kind + " restores previous profile after interrupted move " + fail, () =>
                {
                    var root = ProfileRoot(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
                    Put(root, "config/server.cfg", "kept");
                    Profiles.Schedule(root, new ProfileCommand { Kind = kind, Target = "offline" });
                    try { Profiles.Apply(root, _ => { }, i => { if (i == fail) throw new IOException("interrupted"); }); }
                    catch (IOException)
                    {
                        Check(Profiles.Load(root).Active == "server-1");
                        Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-1");
                        Check(File.ReadAllText(SafePath.Under(root, "config/server.cfg")) == "kept");
                        Profiles.Apply(root, _ => { });
                    }
                    Check(Profiles.Load(root).Active == "offline" && File.Exists(SafePath.Under(root, "plugins/Original/original.dll")));
                });
            }
        }
        Test("startup recovery rolls back an unfinished profile journal then retries", () =>
        {
            var root = ProfileRoot(); var command = PrepareProfile(root, 1, 'a'); Profiles.Schedule(root, command);
            var destination = "ModGate/profiles/offline/plugins/Original";
            Directory.CreateDirectory(Path.GetDirectoryName(SafePath.Under(root, destination))!);
            Directory.Move(SafePath.Under(root, "plugins/Original"), SafePath.Under(root, destination));
            var journal = new ProfileJournal { Id = Guid.NewGuid().ToString("N"), Moves = new List<MoveOperation> { new MoveOperation { Source = "plugins/Original", Destination = destination } } };
            Json.Save(SafePath.Under(root, "ModGate/profile-journal.json"), journal);
            Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "server-1" && File.Exists(SafePath.Under(root, destination + "/original.dll")));
        });
        Test("a committed profile journal finishes cleanup without reversing files", () =>
        {
            var root = ProfileRoot(); var next = new ProfileCatalog(); var journal = new ProfileJournal { Id = Guid.NewGuid().ToString("N"), Committed = true, Next = next };
            Json.Save(SafePath.Under(root, "ModGate/profile-journal.json"), journal);
            Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "offline" && !File.Exists(SafePath.Under(root, "ModGate/profile-journal.json")));
        });
        foreach (var name in new[] { "ServersideQoL", "Server Side Tools", "SERVER-SIDE Admin", "server_side_rules", "Some ServerSide Mod" })
            Test("server-side name classification: " + name, () => Check(AutoSetup.ServerOnlyName(name)));
        Test("ordinary client names are not flagged as server-only", () => Check(!AutoSetup.ServerOnlyName("Client QoL") && !AutoSetup.ServerOnlyName("ServerBrowser")));
        Test("existing sides are preserved while new server-side names receive defaults", () =>
        {
            var old = new ServerManifest { Mods = new List<ModEntry> { Mod("admin", "required"), Mod("shared", "optional") } };
            var loaded = new[] { new ModEntry { Guid = "admin", Name = "ServersideQoL", Version = "1.0.0" }, Mod("shared"), new ModEntry{Guid="new",Name="Server Side Tools",Version="1.0.0"} };
            var settings = AutoSetup.Reconcile(old, loaded, Array.Empty<string>());
            Check(settings.Mods.Single(m => m.Guid == "admin").Side == "required" && settings.Mods.Single(m => m.Guid == "shared").Side == "optional"&&settings.Mods.Single(m=>m.Guid=="new").Side=="serverOnly");
        });
        Test("simple server-only exclusions accept GUIDs and exact display names", () =>
        {
            var settings = AutoSetup.Reconcile(new ServerManifest(), new[] { Mod("admin"), Mod("other") }, new[] { "# comment", " ADMIN ", "other" });
            Check(settings.Mods.All(m => m.Side == "serverOnly"));
        });
        Test("installed version changes clear stale package pins", () =>
        {
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { Mod("shared", package: "A-Shared-1.0.0") } }, new[] { Mod("shared", version: "2.0.0") }, Array.Empty<string>());
            Check(settings.Mods.Single().Version == "2.0.0" && settings.Mods.Single().Package == "");
        });
        Test("removed automatic mods disappear while explicit absent client requirements remain", () =>
        {
            var automatic = Mod("removed"); automatic.Automatic = true;
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { automatic, Mod("client-only") } }, Array.Empty<ModEntry>(), Array.Empty<string>());
            Check(settings.Mods.Count == 1 && settings.Mods[0].Guid == "client-only");
        });
        Test("repository-qualified package IDs retain provider and safe file key", () =>
        {
            var id = PackageId.Parse("hexium:A-Mod-1.0.0"); Check(id.Repository == "hexium" && id.Key == "A-Mod" && id.RawFullName == "A-Mod-1.0.0" && id.FullName == "hexium:A-Mod-1.0.0");
            Throws<InvalidDataException>(() => PackageId.Parse("nexus:A-Mod-1.0.0"));
        });
        Test("repository downloads reject deceptive domains, credentials, HTTP and foreign hosts", () =>
        {
            Check(RepositorySource.Trusted(new Uri("https://cdn.hexium.gg/upload/a.zip"), "hexium"));
            foreach (var url in new[] { "http://cdn.hexium.gg/a.zip", "https://hexium.gg.evil.test/a.zip", "https://user@hexium.gg/a.zip", "https://hexium.gg:8443/a.zip", "https://thunderstore.io/a.zip", "https://other.hexium.gg/a.zip" }) Check(!RepositorySource.Trusted(new Uri(url), "hexium"));
        });
        Test("cached server profiles use the globally updated connector version", () =>
        {
            var old = new[] { Mod(Constants.Guid, version: "0.1.0"), Mod("shared") };
            Check(Profiles.WithCurrentConnector(old).Single(m => m.Guid == Constants.Guid).Version == Constants.Version);
        });
        var repositories = new FakeLookup();
        repositories.Packages["hexium:A-Shared-1.0.0"] = new PackageMetadata { FullName = "A-Shared-1.0.0", Dependencies = new List<string> { "A-Lib-1.0.0", "denikson-BepInExPack_Valheim-5.4.2351" } };
        repositories.Packages["A-Lib-1.0.0"] = new PackageMetadata { FullName = "A-Lib-1.0.0" };
        var hexPolicy = Policy(Mod("shared", package: "hexium:A-Shared-1.0.0")); hexPolicy.AllowExtras = true;
        var hexResolved = await Downloads.Resolve(hexPolicy, PolicyEngine.Compare(hexPolicy, Array.Empty<ModEntry>()), repositories, CancellationToken.None, packageOptIns: new HashSet<string> { "hexium:A-Lib-1.0.0" });
        Test("checked Hexium dependencies can fall back to Thunderstore on 404 while BepInEx is skipped", () => Check(hexResolved.Select(p => p.FullName).SequenceEqual(new[] { "hexium:A-Shared-1.0.0", "A-Lib-1.0.0" }) && repositories.Requested.All(s => !s.Contains("BepInEx"))));
        Test("explicit Hexium pins do not silently fall back to another repository", () => Throws<PackageNotFoundException>(() => Downloads.Resolve(Policy(Mod("missing", package: "hexium:A-Missing-1.0.0")), new Comparison { Missing = { Mod("missing", package: "hexium:A-Missing-1.0.0") } }, repositories, CancellationToken.None).GetAwaiter().GetResult()));
        var lookupSource = new FakeLookup();
        lookupSource.Catalogs["hexium"] = new List<CatalogEntry> { new CatalogEntry { Owner = "A", Name = "Shared", Versions = new List<PackageMetadata> { new PackageMetadata { FullName = "A-Shared-1.0.0" } } } };
        lookupSource.Data["hexium:A-Shared-1.0.0"] = System.Text.Encoding.UTF8.GetBytes("matching-dll");
        var lookupReport = await AutoLookup.Run(new[] { Mod("shared") }, Root(), lookupSource, (file, mod) => File.ReadAllText(file) == "matching-dll", CancellationToken.None);
        Test("automatic lookup accepts trusted registry name and version metadata", () => Check(lookupReport.Mods.Single().Package == "hexium:A-Shared-1.0.0"));
        lookupSource.Catalogs["hexium"].Add(new CatalogEntry { Owner = "B", Name = "Shared", Versions = new List<PackageMetadata> { new PackageMetadata { FullName = "B-Shared-1.0.0" } } });
        lookupReport = await AutoLookup.Run(new[] { Mod("shared") }, Root(), lookupSource, (_, _) => throw new Exception("Trusted packages must not be scanned"), CancellationToken.None);
        Test("ambiguous trusted metadata requires an owner choice", () => Check(lookupReport.Mods.Single().Package == "" && lookupReport.Mods.Single().Status.StartsWith("Multiple packages")));
        lookupSource.Catalogs["hexium"].RemoveAt(1);
        lookupReport = await AutoLookup.Run(new[] { Mod("shared") }, Root(), lookupSource, (_, _) => throw new Exception("Trusted packages must not be scanned"), CancellationToken.None);
        Test("trusted matches never invoke DLL inspection or archive download", () => Check(lookupReport.Mods.Single().Package == "hexium:A-Shared-1.0.0" && lookupSource.Downloaded.Count == 0));
        var hexStageRoot = Root(); var hexZipRoot = Root();
        lookupSource.Data["hexium:A-Shared-1.0.0"] = File.ReadAllBytes(Archive(hexZipRoot, ("shared.dll", "fake-code")));
        var hexPending = await Downloads.Stage(hexStageRoot, new[] { PackageId.Parse("hexium:A-Shared-1.0.0") }, lookupSource, _ => { }, CancellationToken.None);
        Test("Hexium archives stage with filesystem-safe names and retain repository receipts", () => Check(hexPending.Packages.Single() == "hexium:A-Shared-1.0.0" && File.Exists(SafePath.Under(hexStageRoot, "ModGate/staging/" + hexPending.Id + "/packages/A-Shared/shared.dll"))));
        Test("server-only GUIDs are published separately from client requirements", () =>
        {
            var serverMod = Mod("secret-admin", "serverOnly");
            var published = PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { serverMod } }, new[] { serverMod });
            Check(published.ForbiddenPluginGuids.Contains("secret-admin") && published.Mods.All(m => m.Guid != "secret-admin"));
        });
        var rangedHandler = new RangeHandler();
        using (var rangedSource = new RepositorySource(rangedHandler))
        {
            var file = Path.Combine(Root(), "hexium.zip");
            await rangedSource.Download(PackageId.Parse("hexium:A-Mod-1.0.0"), file, CancellationToken.None);
            Test("Hexium ranged downloads assemble exact bytes across bounded chunks", () => Check(File.ReadAllBytes(file).SequenceEqual(rangedHandler.Bytes) && rangedHandler.Ranges.Count == 3));
        }
        using (var invalidRange = new RepositorySource(new RangeHandler { BadRange = true }))
            Test("inconsistent download ranges are rejected", () => Throws<InvalidDataException>(() => invalidRange.Download(PackageId.Parse("hexium:A-Mod-1.0.0"), Path.Combine(Root(), "bad.zip"), CancellationToken.None).GetAwaiter().GetResult()));
        var plainHandler = new RangeHandler { IgnoreRanges = true };
        using (var plainSource = new RepositorySource(plainHandler))
        {
            var file = Path.Combine(Root(), "plain.zip"); await plainSource.Download(PackageId.Parse("hexium:A-Mod-1.0.0"), file, CancellationToken.None);
            Test("a server that ignores Range can still provide a bounded full archive", () => Check(File.ReadAllBytes(file).SequenceEqual(plainHandler.Bytes)));
        }
        Test("metadata wrapper ZIPs expose and install the actual nested mod payload", () =>
        {
            var root = Root(); var inner = Archive(root, ("plugins/MyMod/mod.dll", "actual-code")); var outer = Wrap(root, inner);
            var stage = Path.Combine(root, "stage"); Downloads.Extract(outer, stage, PackageId.Parse("hexium:A-Mod-1.0.0"), 100000);
            Check(File.ReadAllText(Path.Combine(stage, "packages/A-Mod/MyMod/mod.dll")) == "actual-code");
        });
        Test("excessive ZIP wrapper nesting is rejected", () =>
        {
            var root = Root(); var inner = Archive(root, ("mod.dll", "code"));
            var nested = Wrap(root, Wrap(root, Wrap(root, inner)));
            Throws<InvalidDataException>(() => { using var payload = PackageArchive.Open(nested); });
        });
        using (var compressedMetadata = new RepositorySource(new RangeHandler { GzipJson = true }))
        {
            var metadata = await compressedMetadata.Metadata(PackageId.Parse("hexium:A-Mod-1.0.0"), CancellationToken.None);
            Test("gzip JSON blobs without Content-Encoding headers are decoded", () => Check(metadata.FullName == "A-Mod-1.0.0"));
        }
        Test("owner download links are pinned to an exact package", () =>
        {
            foreach (var url in new[] { "https://thunderstore.io/package/download/A/Shared/1.0.0/", "https://thunderstore.io/c/valheim/p/A/Shared/1.0.0/", "https://thunderstore.io/package/A/Shared/1.0.0/" }) Check(PackageId.FromDownloadUrl(url).FullName == "A-Shared-1.0.0");
            Check(PackageId.FromDownloadUrl("https://hexium.gg/package/download/A/Shared/1.0.0/").FullName == "hexium:A-Shared-1.0.0");
            var mod = Mod("shared"); mod.DownloadUrl = "https://thunderstore.io/package/download/A/Shared/1.0.0/";
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { mod } }, new[] { Mod("shared") }, Array.Empty<string>());
            Check(settings.Mods.Single().Package == "A-Shared-1.0.0" && settings.Mods.Single().DownloadUrl == mod.DownloadUrl);
        });
        foreach (var url in new[] { "https://github.com/A/Shared/releases/v1.0.0", "https://thunderstore.io.evil.test/package/download/A/Shared/1.0.0/", "http://thunderstore.io/package/download/A/Shared/1.0.0/", "https://user@thunderstore.io/package/download/A/Shared/1.0.0/", "https://thunderstore.io:8443/package/download/A/Shared/1.0.0/", "https://ccdn.thunderstore.io/package/download/A/Shared/1.0.0/", "https://thunderstore.io/c/valheim/p/A/Shared/", "https://thunderstore.io/package/download/A/Shared/1.0.0/?url=https://evil.test", "https://thunderstore.io/package/download/A/Shared/1.0.0/#fragment", "https://hexium.gg.evil.test/package/download/A/Shared/1.0.0/" })
            Test("reject owner download link " + url, () => Throws<InvalidDataException>(() => PackageId.FromDownloadUrl(url)));
        Test("GitHub pins without manifest authorization and stale priorities are blocked", () =>
        {
            Throws<InvalidDataException>(() => PolicyEngine.ValidatePolicy(Policy(Mod("git", package: "github:Author/Mod/v1.0.0/Mod.zip"))));
            Check(SourcePreferences.Parse("github,thunderstore,hexium,github").SequenceEqual(new[] { "thunderstore", "hexium" }));
            var root = Root(); SourcePreferences.First(root, "thunderstore"); Check(SourcePreferences.Load(root).First() == "thunderstore");
        });
        Test("GitHub pins are cleared on automatic server migration", () =>
        {
            var mod = Mod("shared", package: "github:Author/Mod/v1.0.0/Mod.zip");
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { mod } }, new[] { Mod("shared") }, Array.Empty<string>());
            Check(settings.Mods.Single().Package == "");
        });
        Test("changing plugin versions preserves owner download links while clearing stale package data", () =>
        {
            var mod = Mod("shared"); mod.DownloadUrl = "https://thunderstore.io/package/download/A/Shared/1.0.0/";
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { mod } }, new[] { Mod("shared", version: "2.0.0") }, Array.Empty<string>());
            Check(settings.Mods.Single().DownloadUrl == mod.DownloadUrl && settings.Mods.Single().Package == "");
        });
        Test("client rejects mismatched package and download URL", () =>
        {
            var mod = Mod("shared", package: "A-Other-1.0.0"); mod.DownloadUrl = "https://thunderstore.io/package/download/A/Shared/1.0.0/";
            Throws<InvalidDataException>(() => PolicyEngine.ValidatePolicy(Policy(mod)));
        });
        Test("foreign redirects are stopped before the second request", () =>
        {
            var handler = new RedirectHandler("https://github.com/Author/Mod/mod.zip");
            using var source = new RepositorySource(handler);
            Throws<InvalidDataException>(() => source.Download(PackageId.Parse("A-Shared-1.0.0"), Path.Combine(Root(), "bad.zip"), CancellationToken.None).GetAwaiter().GetResult());
            Check(handler.Requests == 1);
        });
        Test("official Thunderstore CDN redirects deliver bounded downloads", () =>
        {
            var handler = new RedirectHandler("https://ccdn.thunderstore.io/live/mod.zip");
            using var source = new RepositorySource(handler); var file = Path.Combine(Root(), "ok.zip");
            source.Download(PackageId.Parse("A-Shared-1.0.0"), file, CancellationToken.None).GetAwaiter().GetResult();
            Check(handler.Requests == 2 && File.ReadAllText(file) == "trusted archive");
        });
        Test("Nexus pages accept files-tab links but reject other hosts, games and redirects", () =>
        {
            Check(NexusLinks.Validate("https://www.nexusmods.com/valheim/mods/123?tab=files&file_id=456").Contains("123"));
            foreach (var url in new[] { "https://nexusmods.com.evil.test/valheim/mods/123", "https://www.nexusmods.com/skyrim/mods/123", "https://www.nexusmods.com/valheim/mods/123?redirect=https://evil.test", "http://www.nexusmods.com/valheim/mods/123", "https://user@www.nexusmods.com/valheim/mods/123" }) Throws<InvalidDataException>(() => NexusLinks.Validate(url));
        });
        Test("Nexus owner links publish manual requirements without automatic packages", () =>
        {
            var mod = Mod("manual", package: "A-Manual-1.0.0"); mod.NexusUrl = "https://www.nexusmods.com/valheim/mods/123?tab=files";
            var settings = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { mod } }, new[] { Mod("manual") }, Array.Empty<string>());
            var policy = PolicyEngine.Publish(settings, new[] { Mod("manual") });
            Check(policy.Mods.Single(m => m.Guid == "manual").NexusUrl == mod.NexusUrl && policy.Mods.Single(m => m.Guid == "manual").Package == "");
            PolicyEngine.ValidatePolicy(policy);
        });
        Test("manual and automatic download links cannot conflict", () =>
        {
            var mod = Mod("manual"); mod.NexusUrl = "https://www.nexusmods.com/valheim/mods/123"; mod.DownloadUrl = "https://thunderstore.io/package/download/A/Shared/1.0.0/";
            Throws<InvalidDataException>(() => PolicyEngine.Publish(new ServerManifest { Mods = new List<ModEntry> { mod } }, new[] { mod }));
        });
        Test("local manual mod folders and assets stage with validated receipts", () =>
        {
            var root = Root(); var dll = Put(root, "plugins/NexusMod/MyMod.dll", "local-code"); Put(root, "plugins/NexusMod/assets/icon.png", "asset");
            var needed = Mod("manual"); needed.NexusUrl = "https://www.nexusmods.com/valheim/mods/123";
            var installed = Mod("manual"); installed.LocalFile = dll;
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            ManualMods.Stage(root, new[] { needed }, new[] { installed }, pending, CancellationToken.None);
            var stage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            StageVerifier.VerifyStage(stage, pending);
            Check(pending.LocalPackages.Count == 1 && pending.Packages.Count == 0 && pending.Files.Count == 2 && File.Exists(dll));
            var changed = SafePath.Under(stage, pending.Files.First().Path); File.WriteAllText(changed, "tampered");
            Throws<InvalidDataException>(() => StageVerifier.VerifyStage(stage, pending));
        });
        Test("manual mod staging rejects root-wide copying and wrong versions", () =>
        {
            var root = Root(); var dll = Put(root, "plugins/Flat.dll", "code");
            var needed = Mod("manual"); needed.NexusUrl = "https://www.nexusmods.com/valheim/mods/123";
            var installed = Mod("manual"); installed.LocalFile = dll;
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Throws<InvalidDataException>(() => ManualMods.Stage(root, new[] { needed }, new[] { installed }, pending, CancellationToken.None));
            installed.Version = "2.0.0";
            Throws<InvalidDataException>(() => ManualMods.Stage(root, new[] { needed }, new[] { installed }, pending, CancellationToken.None));
        });
        Test("manual folders cannot stage scripts or loader installers", () =>
        {
            var root = Root(); var dll = Put(root, "plugins/NexusMod/Mod.dll", "code"); Put(root, "plugins/NexusMod/setup.exe", "installer");
            var needed = Mod("manual"); needed.NexusUrl = "https://www.nexusmods.com/valheim/mods/123";
            var installed = Mod("manual"); installed.LocalFile = dll;
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Throws<InvalidDataException>(() => ManualMods.Stage(root, new[] { needed }, new[] { installed }, pending, CancellationToken.None));
        });
        Test("unchecked optional mods never enter the download or local-copy plan", () =>
        {
            var policy = Policy(Mod("optional", "optional", package: "A-Optional-1.0.0"));
            var plan = ProfileSelection.Build(policy, new[] { Mod("optional", version: "2.0.0") }, new HashSet<string>());
            Check(plan.Downloads.Count == 0 && plan.Local.Count == 0);
            Check(ProfileSelection.Matches(policy, Array.Empty<ModEntry>(), new HashSet<string>()));
        });
        Test("selected optional mods update only when their installed version differs", () =>
        {
            var policy = Policy(Mod("optional", "optional", package: "A-Optional-1.0.0"));
            var selected = new HashSet<string> { "optional" };
            var plan = ProfileSelection.Build(policy, new[] { Mod("optional", version: "2.0.0") }, selected);
            Check(plan.Downloads.Count == 1 && plan.Local.Count == 0);
            var current = Mod("optional"); current.LocalFile = "local.dll";
            plan = ProfileSelection.Build(policy, new[] { current }, selected);
            Check(plan.Downloads.Count == 0 && plan.Local.Count == 1);
        });
        Test("required installed mods reuse local copies without repository mappings", () =>
        {
            var mod = Mod("required"); mod.LocalFile = "local.dll";
            var plan = ProfileSelection.Build(Policy(Mod("required")), new[] { mod }, new HashSet<string>());
            Check(plan.Local.Count == 1 && plan.Downloads.Count == 0);
            Check(ProfileSelection.Build(Policy(Mod("required")), Array.Empty<ModEntry>(), new HashSet<string>()).Downloads.Count == 1);
        });
        Test("extras respect server permission and server-only exclusions", () =>
        {
            var extra = Mod("extra"); extra.LocalFile = "local.dll";
            var selected = new HashSet<string> { "extra" }; var policy = Policy();
            Throws<InvalidDataException>(() => ProfileSelection.Build(policy, new[] { extra }, selected));
            policy.AllowExtras = true;
            Check(ProfileSelection.Build(policy, new[] { extra }, selected).Local.Count == 1);
            policy.ForbiddenPluginGuids.Add("extra");
            Throws<InvalidDataException>(() => ProfileSelection.Build(policy, new[] { extra }, selected));
        });
        Test("cached profiles must match the checkbox choices", () =>
        {
            var policy = Policy(Mod("optional", "optional"));
            Check(!ProfileSelection.Matches(policy, new[] { Mod("optional") }, new HashSet<string>()));
            Check(ProfileSelection.Matches(policy, new[] { Mod("optional") }, new HashSet<string> { "optional" }));
        });
        Test("unselected optional pins do not conflict with required dependencies", () =>
        {
            var policy = Policy(Mod("required", package: "A-Pack-1.0.0"), Mod("optional", "optional", package: "A-Pack-2.0.0"));
            var filtered = ProfileSelection.ForSelection(policy, new HashSet<string>());
            var source = new FakeSource();
            var result = Downloads.Resolve(filtered, new Comparison { Missing = { policy.Mods[0] } }, source, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Single().FullName == "A-Pack-1.0.0");
        });
        Test("shared installed folders omit unchecked mod DLLs while preserving assets", () =>
        {
            var root = Root(); var first = Mod("first"); var other = Mod("other");
            first.LocalFile = Put(root, "plugins/Shared/First.dll", "first"); other.LocalFile = Put(root, "plugins/Shared/Other.dll", "other"); Put(root, "plugins/Shared/assets/data", "assets");
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            ManualMods.Stage(root, new[] { first }, new[] { first, other }, pending, CancellationToken.None);
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
            Check(pending.Files.Count == 2 && pending.Files.Any(f => f.Path.EndsWith("First.dll")) && pending.Files.All(f => !f.Path.EndsWith("Other.dll")));
        });
        Test("standalone DLLs copy only themselves and preserve offline files", () =>
        {
            var root = Root(); var mod = Mod("flat"); mod.LocalFile = Put(root, "plugins/Flat.dll", "flat"); Put(root, "plugins/Other.dll", "other");
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            ManualMods.Stage(root, new[] { mod }, new[] { mod }, pending, CancellationToken.None);
            StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
            Check(pending.Files.Count == 1 && pending.Files[0].Path.EndsWith("Flat.dll"));
            Profiles.Schedule(root, new ProfileCommand { Kind = "install", Target = "server-1", Install = pending, Profile = new SavedProfile { Slot = "server-1", ServerKey = new string('b',64), Name = "Selected", Mods = new List<ModEntry> { Connector(), mod }, Packages = pending.Packages } });
            Profiles.Apply(root, _ => { });
            Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll", Target = "offline" }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(mod.LocalFile) == "flat" && File.Exists(Path.Combine(root,"plugins","Other.dll")));
        });
        Test("multiple identities in a shared DLL cannot silently include unchecked mods", () =>
        {
            var root = Root(); var first = Mod("first"); var other = Mod("other"); first.LocalFile = other.LocalFile = Put(root, "plugins/Shared/Multi.dll", "code");
            var pending = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Throws<InvalidDataException>(() => ManualMods.Stage(root, new[] { first }, new[] { first, other }, pending, CancellationToken.None));
        });
        Test("source-read paths retain containment and traversal checks", () =>
        {
            var root = Root(); var file = Put(root, "plugins/Mod/Mod.dll", "code");
            Check(SafePath.ReadSource(root, "plugins/Mod/Mod.dll") == file);
            foreach (var bad in new[] { "../outside.dll", "plugins/../outside.dll", "C:/outside.dll", "plugins/mod:stream", "plugins//Mod.dll" }) Throws<InvalidDataException>(() => SafePath.ReadSource(root, bad));
        });
        Test("mod status colors distinguish matching missing required and attention states", () =>
        {
            var required = Mod("a"); var optional = Mod("b", "optional");
            Check(ModStatuses.For(required, new[] { Mod("a", version: "1.0.0.0") }) == ModStatus.Installed);
            Check(ModStatuses.For(required, Array.Empty<ModEntry>()) == ModStatus.MissingRequired);
            Check(ModStatuses.For(required, new[] { Mod("a", version: "2.0.0") }) == ModStatus.Attention);
            Check(ModStatuses.For(optional, Array.Empty<ModEntry>()) == ModStatus.Attention);
            Check(ModStatuses.For(optional, new[] { Mod("b") }) == ModStatus.Installed);
        });
        Test("matching installed dependency packages are checked but not downloaded again", () =>
        {
            var required = Mod("new", package: "A-New-1.0.0"); var library = Mod("lib", package: "A-Lib-1.0.0");
            var installed = Mod("lib"); installed.LocalFile = "installed.dll";
            var source = new FakeSource(); source.Dependencies["A-New-1.0.0"] = new[] { "A-Lib-1.0.0" };
            var packages = Downloads.Resolve(Policy(required, library), new Comparison { Missing = { required } }, source, CancellationToken.None, new[] { installed }).GetAwaiter().GetResult();
            Check(packages.Single().FullName == "A-New-1.0.0" && source.Requested.SequenceEqual(new[] { "A-New-1.0.0", "A-Lib-1.0.0" }));
        });
        Test("a partial installed package or wrong plugin version is still downloaded", () =>
        {
            var first = Mod("first", package: "A-Pack-1.0.0"); var second = Mod("second", package: "A-Pack-1.0.0");
            var installed = Mod("first"); installed.LocalFile = "installed.dll";
            var policy = Policy(first,second); var source = new FakeSource();
            var packages = Downloads.Resolve(policy, new Comparison { Missing = { second } }, source, CancellationToken.None, new[] { installed }).GetAwaiter().GetResult();
            Check(packages.Count == 1);
            installed.Version = "2.0.0";
            packages = Downloads.Resolve(Policy(first), new Comparison { WrongVersion = { first } }, source, CancellationToken.None, new[] { installed }).GetAwaiter().GetResult();
            Check(packages.Count == 1);
        });
        Test("restart command line quoting preserves spaces quotes and trailing slashes", () =>
        {
            Check(RestartLauncher.QuoteArgument("") == "\"\"");
            Check(RestartLauncher.QuoteArgument("hello world") == "\"hello world\"");
            Check(RestartLauncher.QuoteArgument("a\"b") == "\"a\\\"b\"");
            Check(RestartLauncher.QuoteArgument("C:\\Mods\\") == "\"C:\\Mods\\\\\"");
        });
        Test("profile creation copies applicable active configs including custom bindings", () =>
        {
            var root=Root();var mod=Mod("shared");mod.LocalConfigFiles.Add("custom/shared-options.cfg");
            Put(root,"config/shared.cfg","active");Put(root,"config/custom/shared-options.cfg","custom");Put(root,"config/other.cfg","other");Put(root,"config/BepInEx.cfg","global");
            ProfileConfigs.RecordActive(root,new[]{mod});
            var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new FakeSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();
            ProfileConfigs.Stage(root,new[]{Mod("shared")},"active",pending);
            var stage=SafePath.Under(root,"ModGate/staging/"+pending.Id);StageVerifier.VerifyStage(stage,pending);
            Check(File.ReadAllText(Path.Combine(stage,"config","shared.cfg"))=="active"&&File.Exists(Path.Combine(stage,"config","custom","shared-options.cfg"))&&!File.Exists(Path.Combine(stage,"config","other.cfg"))&&!File.Exists(Path.Combine(stage,"config","BepInEx.cfg")));
        });
        Test("profile creation can choose offline configs instead of active", () =>
        {
            var root=Root();Put(root,"config/shared.cfg","active");Put(root,"ModGate/profiles/offline/config/shared.cfg","offline");
            Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Active="server-1",Servers=new List<SavedProfile>{new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One",Mods=new List<ModEntry>{Mod("shared")}}},OfflineMods=new List<ModEntry>{Mod("shared")}});
            var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new FakeSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();ProfileConfigs.Stage(root,new[]{Mod("shared")},"offline",pending);
            Check(File.ReadAllText(SafePath.Under(root,"ModGate/staging/"+pending.Id+"/config/shared.cfg"))=="offline");
        });
        Test("start fresh excludes active offline and packaged configuration files", () =>
        {
            var root=Root();Put(root,"config/shared.cfg","active");var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new FakeSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();
            Put(root,"ModGate/staging/"+pending.Id+"/config/shared.cfg","packaged");ProfileConfigs.Stage(root,new[]{Mod("shared")},"fresh",pending);
            Check(pending.Files.Count==0&&!Directory.Exists(SafePath.Under(root,"ModGate/staging/"+pending.Id+"/config")));
        });
        Test("sync copies only shared mod configs and keeps backups and globals separate", () =>
        {
            var root=Root();Put(root,"config/shared.cfg","new");Put(root,"config/BepInEx.cfg","global");Put(root,"ModGate/profiles/server-2/config/shared.cfg","old");Put(root,"ModGate/profiles/server-3/config/unique.cfg","unique");Put(root,"ModGate/profiles/offline/config/shared.cfg","offline");
            var catalog=new ProfileCatalog{Active="server-1",OfflineMods=new List<ModEntry>{Mod("shared")},Servers=new List<SavedProfile>{new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One",Mods=new List<ModEntry>{Mod("shared")}},new SavedProfile{Slot="server-2",ServerKey=new string('2',64),Name="Two",Mods=new List<ModEntry>{Mod("shared")}},new SavedProfile{Slot="server-3",ServerKey=new string('3',64),Name="Three",Mods=new List<ModEntry>{Mod("unique")}}}};
            Json.Save(SafePath.Under(root,"ModGate/profiles.json"),catalog);var count=ProfileConfigs.SyncAll(root,new[]{Mod("shared")});
            Check(count==2&&File.ReadAllText(SafePath.Under(root,"ModGate/profiles/server-2/config/shared.cfg"))=="new"&&File.ReadAllText(SafePath.Under(root,"ModGate/profiles/offline/config/shared.cfg"))=="new");
            Check(!File.Exists(SafePath.Under(root,"ModGate/profiles/server-3/config/shared.cfg"))&&File.ReadAllText(SafePath.Under(root,"ModGate/profiles/server-3/config/unique.cfg"))=="unique");
            Check(Directory.GetFiles(SafePath.Under(root,"ModGate/config-backups"),"shared.cfg",SearchOption.AllDirectories).Count(f=>File.ReadAllText(f)=="old")==1&&File.ReadAllText(Path.Combine(root,"config","BepInEx.cfg"))=="global");
        });
        Test("config copy source toggles can replace an existing server profile's settings", () =>
        {
            var root=ProfileRoot();var first=PrepareProfile(root,1,'a');Profiles.Schedule(root,first);Profiles.Apply(root,_=>{});Put(root,"config/server.cfg","old-target");
            var next=PrepareProfile(root,1,'a');next.ReplaceConfig=true;ProfileConfigs.Stage(root,next.Profile!.Mods,"fresh",next.Install!);Profiles.Schedule(root,next);Profiles.Apply(root,_=>{});
            Check(!File.Exists(Path.Combine(root,"config","server.cfg"))&&File.Exists(Path.Combine(root,"config","BepInEx.cfg")));
        });
        Test("active profile deletion restores offline and frees its slot", () =>
        {
            var root=ProfileRoot();Profiles.Schedule(root,PrepareProfile(root,1,'a'));Profiles.Apply(root,_=>{});Profiles.Schedule(root,new ProfileCommand{Kind="delete",Target="server-1"});Profiles.Apply(root,_=>{});
            Check(Profiles.Load(root).Active=="offline"&&Profiles.Load(root).Servers.Count==0&&File.Exists(Path.Combine(root,"plugins","Original","original.dll"))&&!Directory.Exists(SafePath.Under(root,"ModGate/profiles/server-1")));
        });
        Test("inactive profile deletion preserves the current and offline setups", () =>
        {
            var root=ProfileRoot();Profiles.Schedule(root,PrepareProfile(root,1,'a'));Profiles.Apply(root,_=>{});Profiles.Schedule(root,PrepareProfile(root,2,'b'));Profiles.Apply(root,_=>{});
            Profiles.Schedule(root,new ProfileCommand{Kind="delete",Target="server-1"});Profiles.Apply(root,_=>{});Check(Profiles.Load(root).Active=="server-2"&&Profiles.Load(root).Servers.Single().Slot=="server-2");
            Profiles.Schedule(root,new ProfileCommand{Kind="switch",Target="offline"});Profiles.Apply(root,_=>{});Check(File.Exists(Path.Combine(root,"plugins","Original","original.dll")));
        });
        Test("offline and unknown profiles cannot be deleted", () =>
        {
            var root=ProfileRoot();Throws<InvalidDataException>(()=>Profiles.Schedule(root,new ProfileCommand{Kind="delete",Target="offline"}));Throws<InvalidDataException>(()=>Profiles.Schedule(root,new ProfileCommand{Kind="delete",Target="server-4"}));
        });
        Test("auto update master defaults off and overrides survive discovery", () =>
        {
            var settings=new ServerManifest();Check(!settings.AutoUpdateOnRestart&&!settings.AutoUpdateAllMods);settings.AutoUpdateOnRestart=true;settings.AutoUpdateAllMods=true;var mod=Mod("shared");mod.AutoUpdate=false;settings.Mods.Add(mod);
            var next=AutoSetup.Reconcile(settings,new[]{Mod("shared")},Array.Empty<string>());Check(next.AutoUpdateOnRestart&&next.AutoUpdateAllMods&&next.Mods.Single().AutoUpdate==false);
        });
        for (int fault=0;fault<5;fault++)
        {
            var failAt=fault;
            Test("active server profile deletion rolls back move "+fault,()=>
            {
                var root=ProfileRoot();Profiles.Schedule(root,PrepareProfile(root,1,'a'));Profiles.Apply(root,_=>{});Profiles.Schedule(root,new ProfileCommand{Kind="delete",Target="server-1"});
                Throws<IOException>(()=>Profiles.Apply(root,_=>{},index=>{if(index==failAt)throw new IOException("delete interruption");}));
                Check(Profiles.Load(root).Active=="server-1"&&Profiles.Load(root).Servers.Single().Slot=="server-1");
                Profiles.Apply(root,_=>{});Check(Profiles.Load(root).Active=="offline"&&File.Exists(Path.Combine(root,"plugins","Original","original.dll")));
            });
        }
        Test("legacy offline sync only touches already applicable existing files",()=>
        {
            var root=Root();Put(root,"config/shared.cfg","current");Put(root,"ModGate/profiles/offline/config/shared.cfg","offline-old");
            Json.Save(SafePath.Under(root,"ModGate/profiles.json"),new ProfileCatalog{Active="server-1",Servers=new List<SavedProfile>{new SavedProfile{Slot="server-1",ServerKey=new string('1',64),Name="One",Mods=new List<ModEntry>{Mod("shared")}}}});
            Check(ProfileConfigs.SyncAll(root,new[]{Mod("shared")})==1&&File.ReadAllText(SafePath.Under(root,"ModGate/profiles/offline/config/shared.cfg"))=="current");
        });
        Test("installed manifest identifies package name version dependencies and supported URL",()=>
        {
            var manifest=PackageManifest.Parse("{\"name\":\"Package_Name\",\"version_number\":\"3.4.5\",\"website_url\":\"https://thunderstore.io/c/valheim/p/Owner/Package_Name/\",\"dependencies\":[\"A-Lib-1.0.0\"]}");
            Check(manifest.ExactPackage("thunderstore")=="Owner-Package_Name-3.4.5"&&manifest.Dependencies.Single()=="A-Lib-1.0.0");
            var mod=Mod("unrelated.guid");mod.Name="Human title";mod.LocalManifest=manifest;
            Check(AutoSetup.Candidate(new CatalogEntry{Name="Package_Name"},mod));
        });
        Test("local manifest reader finds a nearby package but ignores a shared root manifest",()=>
        {
            var root=Root();var dll=Put(root,"plugins/Package/plugins/Mod.dll","dll");
            Put(root,"plugins/Package/manifest.json",Json.Write(new PackageManifest{Name="Package",Version="1.0.0",Website="https://hexium.gg/c/valheim/p/A/Package/"}));
            Check(LocalManifestReader.Read(dll,Path.Combine(root,"plugins"))!.ExactPackage("hexium")=="hexium:A-Package-1.0.0");
            var flat=Put(root,"plugins/Flat.dll","dll");Put(root,"plugins/manifest.json",Json.Write(new PackageManifest{Name="WrongShared",Version="1.0.0"}));Check(LocalManifestReader.Read(flat,Path.Combine(root,"plugins"))==null);
        });
        Test("malformed or oversized manifests are ignored without breaking inventory",()=>
        {
            var root=Root();var dll=Put(root,"plugins/Mod/Mod.dll","dll");Put(root,"plugins/Mod/manifest.json","{}");Check(LocalManifestReader.Read(dll,Path.Combine(root,"plugins"))==null);
            Put(root,"plugins/Mod/manifest.json",new string('x',65537));Check(LocalManifestReader.Read(dll,Path.Combine(root,"plugins"))==null);
        });
        Test("manifest package version remains distinct from DLL version",()=>
        {
            var source=new FakeLookup();source.Catalogs["hexium"]=new List<CatalogEntry>{new CatalogEntry{Owner="A",Name="Shared",Versions=new List<PackageMetadata>{new PackageMetadata{FullName="A-Shared-1.0.0"},new PackageMetadata{FullName="A-Shared-1.0.1"}}}};
            source.Data["hexium:A-Shared-1.0.0"]=new byte[]{1};source.Data["hexium:A-Shared-1.0.1"]=new byte[]{1};var mod=Mod("shared");mod.LocalManifest=new PackageManifest{Name="Shared",Version="1.0.1",Dependencies=new List<string>()};
            var report=AutoLookup.Run(new[]{mod},Root(),source,(_,_)=>true,CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Package=="hexium:A-Shared-1.0.1");
        });
        var fallbackManifest=PackageManifest.Parse("{\"name\":\"Shared\",\"version_number\":\"1.0.0\",\"website_url\":\"https://github.com/Author/Shared\",\"dependencies\":[]}");
        var fallbackMod=Mod("shared");fallbackMod.LocalManifest=fallbackManifest;fallbackMod.ManifestSource=fallbackManifest.GitOrigin();
        CatalogEntry Entry(string owner,string full)=>new CatalogEntry{Owner=owner,Name="Shared",PluginGuids=new List<string>{"shared"},Versions=new List<PackageMetadata>{new PackageMetadata{FullName=full}}};
        Test("primary trusted repository metadata match prevents fallback requests",()=>
        {
            var source=new FakeLookup();source.Catalogs["thunderstore"]=new List<CatalogEntry>{Entry("A","A-Shared-1.0.0")};source.Data["A-Shared-1.0.0"]=new byte[]{1};source.Catalogs["github"]=new List<CatalogEntry>{Entry("Author","github:Author/Shared/v1.0.0/Shared.zip")};
            var report=AutoLookup.Run(new[]{fallbackMod},Root(),source,(_,_)=>true,CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Package=="A-Shared-1.0.0"&&!source.CatalogRequests.Contains("github"));
        });
        Test("manifest source is requested last only when both primary sources lack a match",()=>
        {
            var source=new FakeLookup();source.Catalogs["github"]=new List<CatalogEntry>{Entry("Author","github:Author/Shared/v1.0.0/Shared.zip")};source.Data["github:Author/Shared/v1.0.0/Shared.zip"]=new byte[]{1,2,3};
            var report=AutoLookup.Run(new[]{fallbackMod},Root(),source,(_,_)=>true,CancellationToken.None).GetAwaiter().GetResult();Check(source.CatalogRequests.SequenceEqual(new[]{"hexium","thunderstore","github"})&&report.Mods.Single().Package.StartsWith("github:")&&report.Mods.Single().AssetSha256.Length==64);
        });
        Test("ambiguous primary metadata requires owner choice instead of fallback",()=>
        {
            var source=new FakeLookup();source.Catalogs["hexium"]=new List<CatalogEntry>{Entry("A","A-Shared-1.0.0"),Entry("B","B-Shared-1.0.0")};source.Data["hexium:A-Shared-1.0.0"]=new byte[]{1};source.Data["hexium:B-Shared-1.0.0"]=new byte[]{1};
            var report=AutoLookup.Run(new[]{fallbackMod},Root(),source,(_,_)=>true,CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Status.StartsWith("Multiple packages")&&!source.CatalogRequests.Contains("github"));
        });
        Test("no manifest link means no fallback repository search",()=>
        {
            var source=new FakeLookup();AutoLookup.Run(new[]{Mod("shared")},Root(),source,(_,_)=>false,CancellationToken.None).GetAwaiter().GetResult();Check(!source.CatalogRequests.Contains("github"));
        });
        Test("manifest authorization is scoped to exact repository version and pinned asset",()=>
        {
            var proof=fallbackManifest.GitOrigin()!;var id=PackageId.Parse("github:Author/Shared/v1.0.0/Shared.zip");
            Check(proof.Allows(id)&&!proof.Verified(id)&&!proof.Allows(PackageId.Parse("github:Other/Shared/v1.0.0/Shared.zip"))&&!proof.Allows(PackageId.Parse("github:Author/Shared/v2.0.0/Shared.zip")));
            proof.AssetId=id.FullName;proof.AssetSha256=new string('a',64);var mod=Mod("shared",package:id.FullName);mod.ManifestSource=proof;PolicyEngine.ValidatePolicy(Policy(mod));
            mod.Package="github:Author/Shared/v1.0.0/Other.zip";Throws<InvalidDataException>(()=>PolicyEngine.ValidatePolicy(Policy(mod)));
            foreach(var bad in new[]{"https://github.com.evil.test/Author/Shared","http://github.com/Author/Shared","https://user@github.com/Author/Shared","https://github.com/Author/Shared?redirect=https://evil.test"})Check(!GitManifestRepository.TryParse(bad,out _));
        });
        Test("archive manifest versions are cross-checked before extraction",()=>
        {
            var root=Root();var archive=Archive(root,("manifest.json",Json.Write(new PackageManifest{Name="Mod",Version="2.0.0",Dependencies=new List<string>()})),("Mod.dll","code"));
            Throws<InvalidDataException>(()=>Downloads.Extract(archive,Path.Combine(root,"stage"),PackageId.Parse("A-Mod-1.0.0"),100000));
        });
        Test("manifest dependencies supply extra catalog identity hints for installed mods",()=>
        {
            var parent=Mod("parent");parent.LocalManifest=PackageManifest.Parse("{\"name\":\"Parent\",\"version_number\":\"1.0.0\",\"website_url\":\"\",\"dependencies\":[\"Owner-Library-2.0.0\",\"denikson-BepInExPack_Valheim-5.4.2351\"]}");
            var lib=Mod("different.guid");lib.Name="Library";ManifestCandidates.Enrich(new[]{parent,lib});Check(lib.LocalPackageHints.Single()=="Owner-Library-2.0.0"&&AutoSetup.Candidate(new CatalogEntry{Owner="Owner",Name="Library"},lib));
        });
        Test("manifest-scoped release source requires authorization before any HTTP request",()=>
        {
            var handler=new ManifestGitHandler();using var source=new RepositorySource(handler);var id=PackageId.Parse("github:Author/Shared/v1.0.0/Shared.zip");
            Throws<InvalidDataException>(()=>source.Metadata(id,CancellationToken.None).GetAwaiter().GetResult());Check(handler.Requests.Count==0);
        });
        Test("manifest-scoped release metadata and binary redirects preserve pinned contents",()=>
        {
            var handler=new ManifestGitHandler();var proof=fallbackManifest.GitOrigin()!;var id=PackageId.Parse("github:Author/Shared/v1.0.0/Shared.zip");
            using(var source=new RepositorySource(handler,new[]{proof}))
            {
                var candidates=source.Candidates("github",new[]{fallbackMod},CancellationToken.None).GetAwaiter().GetResult();Check(candidates.Single().Versions.Single().FullName==id.FullName);
                var file=Path.Combine(Root(),"asset.zip");source.Download(id,file,CancellationToken.None).GetAwaiter().GetResult();proof.AssetId=id.FullName;proof.AssetSha256=PackageArchive.AssetHash(file,id);
                Downloads.Extract(file,Path.Combine(Root(),"stage"),id,1000000);
            }
            var changed=new ManifestGitHandler{Changed=true};using var locked=new RepositorySource(changed,new[]{proof});
            Throws<InvalidDataException>(()=>locked.Download(id,Path.Combine(Root(),"changed.zip"),CancellationToken.None).GetAwaiter().GetResult());
            Check(changed.Requests.All(u=>u.Host=="api.github.com"||u.Host=="github.com"||u.Host=="release-assets.githubusercontent.com"));
        });
        Test("standalone release DLLs use content hashes independent of ZIP wrapping",()=>
        {
            var proof=fallbackManifest.GitOrigin()!;var id=PackageId.Parse("github:Author/Shared/v1.0.0/Shared.dll");var handler=new ManifestGitHandler{Dll=true};
            using var source=new RepositorySource(handler,new[]{proof});var a=Path.Combine(Root(),"a.zip");source.Download(id,a,CancellationToken.None).GetAwaiter().GetResult();proof.AssetId=id.FullName;proof.AssetSha256=PackageArchive.AssetHash(a,id);
            using var locked=new RepositorySource(new ManifestGitHandler{Dll=true},new[]{proof});var b=Path.Combine(Root(),"b.zip");locked.Download(id,b,CancellationToken.None).GetAwaiter().GetResult();Check(PackageArchive.AssetHash(a,id)==PackageArchive.AssetHash(b,id));
        });
        Test("fallback proof is retained only while the installed manifest is unchanged",()=>
        {
            var loaded=Mod("shared");loaded.LocalManifest=fallbackManifest;var id=PackageId.Parse("github:Author/Shared/v1.0.0/Shared.zip");var old=Mod("shared",package:id.FullName);old.ManifestSource=fallbackManifest.GitOrigin();old.ManifestSource!.AssetId=id.FullName;old.ManifestSource.AssetSha256=new string('a',64);
            var effective=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},Array.Empty<string>());Check(effective.Mods.Single().Package==id.FullName);
            loaded.LocalManifest=PackageManifest.Parse("{\"name\":\"Shared\",\"version_number\":\"1.0.0\",\"website_url\":\"https://github.com/Other/Shared\",\"dependencies\":[]}");
            effective=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},Array.Empty<string>());Check(effective.Mods.Single().Package=="");
        });
        Test("valid manifests are retained through downloads and installed-copy profiles",()=>
        {
            var root=Root();var text=Json.Write(new PackageManifest{Name="Mod",Version="1.0.0",Website="https://thunderstore.io/c/valheim/p/A/Mod/",Dependencies=new List<string>()});
            var archive=Archive(root,("manifest.json",text),("Mod.dll","code"));var plugins=Path.Combine(root,"plugins");Downloads.Extract(archive,plugins,PackageId.Parse("A-Mod-1.0.0"),1000000);
            var mod=Mod("mod");mod.LocalFile=Path.Combine(plugins,"packages","A-Mod","Mod.dll");Check(LocalManifestReader.Read(mod.LocalFile,plugins)!.Name=="Mod");
            var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new FakeSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();ManualMods.Stage(root,new[]{mod},new[]{mod},pending,CancellationToken.None);
            Check(pending.Files.Any(f=>f.Path.EndsWith("/manifest.json")));
        });
        Test("trusted four-component plugin version matches normalized package version",()=>
        {
            var source=new FakeLookup();source.Catalogs["hexium"]=new List<CatalogEntry>{new CatalogEntry{Owner="Bascinet",Name="OctoArsenal",PluginGuids=new List<string>{"Octodad.OctoArsenal"},Versions=new List<PackageMetadata>{new PackageMetadata{FullName="Bascinet-OctoArsenal-0.5.27"}}}};
            var mod=Mod("Octodad.OctoArsenal",version:"0.5.27.0");var report=AutoLookup.Run(new[]{mod},Root(),source,(_,_)=>throw new Exception("No DLL scan allowed"),CancellationToken.None).GetAwaiter().GetResult();
            Check(report.Mods.Single().Package=="hexium:Bascinet-OctoArsenal-0.5.27"&&source.Downloaded.Count==0&&!report.Status.Contains("oversized"));
        });
        Test("trusted metadata never substitutes a different package version",()=>
        {
            var source=new FakeLookup();source.Catalogs["hexium"]=new List<CatalogEntry>{new CatalogEntry{Owner="A",Name="Shared",Versions=new List<PackageMetadata>{new PackageMetadata{FullName="A-Shared-2.0.0"}}}};
            var report=AutoLookup.Run(new[]{Mod("shared",version:"1.0.0")},Root(),source,(_,_)=>throw new Exception("No DLL scan allowed"),CancellationToken.None).GetAwaiter().GetResult();
            Check(report.Mods.Single().Package==""&&source.Downloaded.Count==0);
        });
        Test("manifest-only fallback still downloads and rejects an unverified DLL",()=>
        {
            var source=new FakeLookup();source.Catalogs["github"]=new List<CatalogEntry>{Entry("Author","github:Author/Shared/v1.0.0/Shared.zip")};source.Data["github:Author/Shared/v1.0.0/Shared.zip"]=new byte[]{1};var calls=0;
            var report=AutoLookup.Run(new[]{fallbackMod},Root(),source,(_,_)=>{calls++;return false;},CancellationToken.None).GetAwaiter().GetResult();
            Check(calls==1&&report.Mods.Single().Package==""&&source.Downloaded.Count==1);
        });
        Test("inactive and inconsistent trusted catalog entries are not selected",()=>
        {
            var source=new FakeLookup();source.Catalogs["hexium"]=new List<CatalogEntry>{new CatalogEntry{Owner="A",Name="Shared",Versions=new List<PackageMetadata>{new PackageMetadata{FullName="B-Other-1.0.0"},new PackageMetadata{FullName="A-Shared-1.0.0",IsActive=false}}}};
            var report=AutoLookup.Run(new[]{Mod("shared")},Root(),source,(_,_)=>throw new Exception("No DLL scan allowed"),CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Package==""&&source.Downloaded.Count==0);
        });
        Test("exact manifest package links disambiguate trusted package owners",()=>
        {
            var source=new FakeLookup();source.Catalogs["thunderstore"]=new List<CatalogEntry>{new CatalogEntry{Owner="A",Name="Shared",Versions=new List<PackageMetadata>{new PackageMetadata{FullName="A-Shared-1.0.0"}}},new CatalogEntry{Owner="B",Name="Shared",Versions=new List<PackageMetadata>{new PackageMetadata{FullName="B-Shared-1.0.0"}}}};
            var mod=Mod("shared");mod.LocalManifest=PackageManifest.Parse("{\"name\":\"Shared\",\"version_number\":\"1.0.0\",\"website_url\":\"https://thunderstore.io/c/valheim/p/B/Shared/\",\"dependencies\":[]}");
            var report=AutoLookup.Run(new[]{mod},Root(),source,(_,_)=>throw new Exception("No DLL scan allowed"),CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Package=="B-Shared-1.0.0"&&source.Downloaded.Count==0);
        });
        Test("normalized trusted version matching prioritizes older releases beyond the candidate cap",()=>
        {
            var source=new FakeLookup();var versions=Enumerable.Range(1,20).Reverse().Select(i=>new PackageMetadata{FullName="A-Shared-1.0."+i}).ToList();source.Catalogs["hexium"]=new List<CatalogEntry>{new CatalogEntry{Owner="A",Name="Shared",Versions=versions}};
            var report=AutoLookup.Run(new[]{Mod("shared",version:"1.0.1.0")},Root(),source,(_,_)=>throw new Exception("No DLL scan allowed"),CancellationToken.None).GetAwaiter().GetResult();Check(report.Mods.Single().Package=="hexium:A-Shared-1.0.1");
        });
        Test("existing protected fields survive name and version refresh",()=>
        {
            foreach(var side in new[]{"required","optional","serverOnly"})foreach(var automatic in new[]{false,true})foreach(bool? update in new bool?[]{null,false,true})
            {
                var old=Mod("same",side);old.Name="Old name";old.Automatic=automatic;old.AutoUpdate=update;old.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/";
                var loaded=Mod("same",version:"2.0.0");loaded.Name="Server Side New Name";loaded.LocalManifest=PackageManifest.Parse("{\"name\":\"Current\",\"version_number\":\"2.0.0\",\"website_url\":\"https://thunderstore.io/c/valheim/p/A/Current/\",\"dependencies\":[]}");
                var next=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},new[]{"same"}).Mods.Single();
                Check(next.Name==loaded.Name&&next.Version=="2.0.0"&&next.Package=="A-Current-2.0.0"&&next.DownloadUrl==old.DownloadUrl&&next.NexusUrl==old.NexusUrl&&next.Side==side&&next.Automatic==automatic&&next.AutoUpdate==update);
            }
        });
        Test("Nexus URLs and automatic false remain untouched across detected upgrades",()=>
        {
            var old=Mod("same","optional");old.NexusUrl="https://www.nexusmods.com/valheim/mods/123?tab=files";old.Automatic=false;old.AutoUpdate=false;
            var loaded=Mod("same",version:"2.0.0");loaded.Name="New";var next=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},Array.Empty<string>()).Mods.Single();
            Check(next.NexusUrl==old.NexusUrl&&next.DownloadUrl==""&&next.Side=="optional"&&!next.Automatic&&next.AutoUpdate==false&&next.Name=="New"&&next.Version=="2.0.0");
        });
        Test("fresh installed package metadata takes precedence over stale lookup cache",()=>
        {
            var old=Mod("same",package:"Old-Wrong-1.0.0");var loaded=Mod("same");loaded.LocalManifest=PackageManifest.Parse("{\"name\":\"Current\",\"version_number\":\"1.0.0\",\"website_url\":\"https://hexium.gg/c/valheim/p/New/Current/\",\"dependencies\":[]}");
            var next=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},Array.Empty<string>());
            AutoSetup.ApplyLookup(next,new LookupReport{Mods=new List<LookupResult>{new LookupResult{Guid="same",Version="1.0.0",Package="Old-Wrong-1.0.0"}}});Check(next.Mods.Single().Package=="hexium:New-Current-1.0.0");
        });
        Test("matching lookup results refresh existing package data without editing settings",()=>
        {
            var mod=Mod("same","optional",package:"A-Current-1.0.0");mod.Automatic=false;mod.AutoUpdate=false;
            var settings=new ServerManifest{Mods=new List<ModEntry>{mod}};AutoSetup.ApplyLookup(settings,new LookupReport{Mods=new List<LookupResult>{new LookupResult{Guid="same",Version="1.0.0",Package="B-Current-1.0.0"}}});
            Check(mod.Package=="B-Current-1.0.0"&&mod.Side=="optional"&&!mod.Automatic&&mod.AutoUpdate==false&&mod.DownloadUrl==""&&mod.NexusUrl=="");
        });
        Test("publishing policy never mutates server config fields",()=>
        {
            var mod=Mod("same","optional");mod.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/";mod.Automatic=false;mod.AutoUpdate=false;
            var settings=new ServerManifest{Mods=new List<ModEntry>{mod}};var before=Json.Write(settings);var policy=PolicyEngine.Publish(settings,new[]{Mod("same")});
            Check(Json.Write(settings)==before&&policy.Mods.Single(m=>m.Guid=="same").Package=="A-Current-1.0.0");
        });
        Test("stale preserved URL is reported without overwriting URL or detected package",()=>
        {
            var mod=Mod("same",version:"2.0.0",package:"A-Current-2.0.0");mod.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/";
            var settings=new ServerManifest{Mods=new List<ModEntry>{mod}};var before=Json.Write(settings);Throws<InvalidDataException>(()=>PolicyEngine.Publish(settings,new[]{Mod("same",version:"2.0.0")}));Check(Json.Write(settings)==before);
        });
        Test("changed package versions refresh without changing the required DLL version",()=>
        {
            var old=Mod("same",package:"A-Current-1.0.0");old.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/";var loaded=Mod("same");loaded.LocalManifest=PackageManifest.Parse("{\"name\":\"Current\",\"version_number\":\"2.0.0\",\"website_url\":\"\",\"dependencies\":[]}");
            var next=AutoSetup.Reconcile(new ServerManifest{Mods=new List<ModEntry>{old}},new[]{loaded},Array.Empty<string>());
            AutoSetup.ApplyLookup(next,new LookupReport{Mods=new List<LookupResult>{new LookupResult{Guid="same",Version="1.0.0",Package="A-Current-1.0.0"}}});Check(next.Mods.Single().Package=="A-Current-2.0.0");
            AutoSetup.ApplyLookup(next,new LookupReport{Mods=new List<LookupResult>{new LookupResult{Guid="same",Version="1.0.0",Package="A-Current-2.0.0"}}});Check(next.Mods.Single().Package=="A-Current-2.0.0");
        });
        Test("preserved server-only URLs never become client download requirements",()=>
        {
            var mod=Mod("admin","serverOnly",version:"2.0.0",package:"A-Admin-2.0.0");mod.DownloadUrl="https://thunderstore.io/package/download/A/Admin/1.0.0/";var settings=new ServerManifest{Mods=new List<ModEntry>{mod}};var before=Json.Write(settings);
            var policy=PolicyEngine.Publish(settings,new[]{Mod("admin",version:"2.0.0")});Check(policy.Mods.All(m=>m.Guid!="admin")&&policy.ForbiddenPackages.Contains("A-Admin")&&Json.Write(settings)==before);
        });
        Test("override skips unavailable metadata and keeps resolvable mods", () =>
        {
            var bad=Mod("bad",package:"A-Bad-1.0.0");var good=Mod("good",package:"A-Good-1.0.0");var missing=Mod("unmapped");var source=new FakeLookup();source.Packages[good.Package]=new PackageMetadata{FullName=good.Package};
            var policy=Policy(bad,good,missing);var issues=new List<string>();var diff=new Comparison();diff.Missing.AddRange(policy.Mods);
            var result=PartialSetup.Resolve(policy,diff,source,CancellationToken.None,Array.Empty<ModEntry>(),issues).GetAwaiter().GetResult();
            Check(result.Select(p=>p.FullName).SequenceEqual(new[]{good.Package})&&issues.Any(i=>i.Contains("unmapped"))&&issues.Any(i=>i.Contains("A-Bad")));
            Check(!PolicyEngine.Compare(policy,new[]{good}).Matches);
        });
        Test("override records dependency conflicts while retaining server pins and skipping bootstrap",()=>
        {
            var app=Mod("app",package:"A-App-1.0.0");var dep=Mod("dep",package:"A-Dep-1.0.0");var source=new FakeSource();source.Dependencies[app.Package]=new[]{"A-Dep-2.0.0","denikson-BepInExPack_Valheim-5.4.2333"};var diff=new Comparison();diff.Missing.AddRange(new[]{app,dep});var issues=new List<string>();
            var result=PartialSetup.Resolve(Policy(app,dep),diff,source,CancellationToken.None,Array.Empty<ModEntry>(),issues).GetAwaiter().GetResult();
            Check(result.Any(p=>p.FullName==dep.Package)&&result.All(p=>!Downloads.IsBootstrap(p))&&source.Requested.All(p=>!p.Contains("BepInEx"))&&issues.Any(i=>i.Contains("newer version")));
        });
        Test("override excludes forbidden dependencies and never relaxes policy source validation",()=>
        {
            var app=Mod("app",package:"A-App-1.0.0");var policy=Policy(app);policy.ForbiddenPackages.Add("A-Admin");var source=new FakeSource();source.Dependencies[app.Package]=new[]{"A-Admin-1.0.0"};var issues=new List<string>();var diff=new Comparison();diff.Missing.Add(app);
            var result=PartialSetup.Resolve(policy,diff,source,CancellationToken.None,Array.Empty<ModEntry>(),issues).GetAwaiter().GetResult();Check(result.Count==1&&source.Requested.All(p=>!p.Contains("Admin"))&&issues.Any(i=>i.Contains("server-only")));
            app.Package="github:Author/App/v1.0.0/App.zip";Throws<InvalidDataException>(()=>PartialSetup.Resolve(policy,diff,source,CancellationToken.None,Array.Empty<ModEntry>(),issues).GetAwaiter().GetResult());
        });
        Test("override respects cancellation",()=>
        {
            var app=Mod("app",package:"A-App-1.0.0");var diff=new Comparison();diff.Missing.Add(app);using var cancel=new CancellationTokenSource();cancel.Cancel();
            Throws<OperationCanceledException>(()=>PartialSetup.Resolve(Policy(app),diff,new FakeSource(),cancel.Token,Array.Empty<ModEntry>(),new List<string>()).GetAwaiter().GetResult());
        });
        Test("override stages good packages and removes failed isolated archives",()=>
        {
            var root=Root();var source=new FakeLookup();var good=PackageId.Parse("A-Good-1.0.0");var bad=PackageId.Parse("A-Bad-1.0.0");var unsafeId=PackageId.Parse("A-Unsafe-1.0.0");
            source.Data[good.FullName]=File.ReadAllBytes(Archive(root,("plugins/good.dll","good")));
            source.Data[bad.FullName]=new byte[]{1,2,3};source.Data[unsafeId.FullName]=File.ReadAllBytes(Archive(root,("plugins/fragment.dll","fragment"),("../escape.dll","bad")));
            var issues=new List<string>();var pending=PartialSetup.Stage(root,new[]{good,bad,unsafeId},source,_=>{},CancellationToken.None,issues).GetAwaiter().GetResult();var stage=SafePath.Under(root,"ModGate/staging/"+pending.Id);
            StageVerifier.VerifyStage(stage,pending);Check(pending.Packages.SequenceEqual(new[]{good.FullName})&&pending.Files.Count==1&&issues.Count==2);
            Check(Directory.GetDirectories(SafePath.Under(root,"ModGate/staging")).Length==1&&!File.Exists(SafePath.Under(root,"escape.dll")));
        });
        Test("override skips failed local copies and keeps valid installed copies unchanged",()=>
        {
            var root=Root();var good=Mod("good");good.LocalFile=Put(root,"plugins/Good/good.dll","original");var bad=Mod("bad");bad.LocalFile=Path.Combine(root,"plugins","Missing","bad.dll");
            var pending=Downloads.Stage(root,Array.Empty<PackageId>(),new FakeSource(),_=>{},CancellationToken.None).GetAwaiter().GetResult();var issues=new List<string>();
            PartialSetup.CopyLocal(root,new[]{good,bad},new[]{good,bad},pending,CancellationToken.None,issues);
            StageVerifier.VerifyStage(SafePath.Under(root,"ModGate/staging/"+pending.Id),pending);Check(pending.LocalPackages.Count==1&&issues.Count==1&&File.ReadAllText(good.LocalFile)=="original");
        });
        Test("override can omit missing manual Nexus mods without bypassing invalid URLs",()=>
        {
            var mod=Mod("manual");mod.NexusUrl="https://www.nexusmods.com/valheim/mods/123?tab=files";var issues=new List<string>();
            Throws<InvalidDataException>(()=>ProfileSelection.Build(Policy(mod),Array.Empty<ModEntry>(),new HashSet<string>()));
            var plan=ProfileSelection.Build(Policy(mod),Array.Empty<ModEntry>(),new HashSet<string>(),true,issues);Check(plan.Downloads.Count==0&&plan.Local.Count==0&&issues.Count==1);
            mod.NexusUrl="https://example.com/evil";Throws<InvalidDataException>(()=>ProfileSelection.Build(Policy(mod),Array.Empty<ModEntry>(),new HashSet<string>(),true,issues));
        });
        Test("partial profile activation preserves offline files and records incomplete status",()=>
        {
            var root=ProfileRoot();var command=PrepareProfile(root,1,'f');command.Profile!.Incomplete=true;
            Profiles.Schedule(root,command);Profiles.Apply(root,_=>{});Check(Profiles.Load(root).Servers.Single().Incomplete);
            Profiles.Schedule(root,new ProfileCommand{Kind="removeAll"});Profiles.Apply(root,_=>{});Check(File.ReadAllText(SafePath.Under(root,"plugins/Original/original.dll"))=="original");
        });
        Test("asterisk locks individual text fields while other detected values refresh",()=>
        {
            var old=Mod("same*");old.Name="Custom* Name";old.Version="1.0.0*";old.Package="A-Current-1.0.0*";old.Side="optional*";old.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/*";
            var loaded=Mod("same",version:"2.0.0",package:"A-Current-2.0.0");loaded.Name="Detected";
            var settings=new ServerManifest{ServerName="My* server",Mods=new(){old}};var next=AutoSetup.Reconcile(settings,new[]{loaded},Array.Empty<string>());var mod=next.Mods.Single();
            Check(mod.Guid==old.Guid&&mod.Name==old.Name&&mod.Version==old.Version&&mod.Package==old.Package&&mod.Side==old.Side&&mod.DownloadUrl==old.DownloadUrl&&next.ServerName==settings.ServerName);
            old.Version="1.0.0";old.Package="A-Current-1.0.0";var unlocked=AutoSetup.Reconcile(settings,new[]{loaded},Array.Empty<string>()).Mods.Single();Check(unlocked.Version=="2.0.0"&&unlocked.Package=="A-Current-2.0.0"&&unlocked.Name==old.Name);
        });
        Test("asterisks are absent from published policy matching URLs and metadata",()=>
        {
            var mod=Mod("sa*me",version:"1.0.0*",package:"A-Current-1.0.0*");mod.Name="Cust*om";mod.Side="required*";mod.DownloadUrl="https://thunderstore.io/package/download/A/Current/1.0.0/*";
            var settings=new ServerManifest{ServerName="Test*",Mods=new(){mod}};var before=Json.Write(settings);var policy=PolicyEngine.Publish(settings,new[]{Mod("same")});
            PolicyEngine.ValidatePolicy(policy);Check(!Json.Write(policy).Contains('*')&&PolicyEngine.Compare(policy,new[]{Mod("same"),Connector()}).Matches&&Json.Write(settings)==before);
        });
        Test("starred package pin survives lookup while unlocked package fields refresh",()=>
        {
            var locked=Mod("locked",package:"A-Keep-1.0.0*");var other=Mod("other",package:"A-Old-1.0.0");var settings=new ServerManifest{Mods=new(){locked,other}};
            AutoSetup.ApplyLookup(settings,new LookupReport{Mods=new(){new LookupResult{Guid="locked",Version="1.0.0",Package="A-New-1.0.0"},new LookupResult{Guid="other",Version="1.0.0",Package="A-New-1.0.0"}}});
            Check(locked.Package=="A-Keep-1.0.0*"&&other.Package=="A-New-1.0.0");
        });
        Test("locked fields retain an absent automatically discovered entry",()=>
        {
            var mod=Mod("absent");mod.Name="Keep*";mod.Automatic=true;var next=AutoSetup.Reconcile(new ServerManifest{Mods=new(){mod}},Array.Empty<ModEntry>(),Array.Empty<string>());Check(next.Mods.Single().Name=="Keep*");
        });
        Test("normalizing field markers still rejects duplicate identities and foreign links",()=>
        {
            var settings=new ServerManifest{Mods=new(){Mod("same"),Mod("sa*me")}};Throws<InvalidDataException>(()=>PolicyEngine.Publish(settings,Array.Empty<ModEntry>()));
            var mod=Mod("bad");mod.DownloadUrl="https://evil*.example/download.zip";Throws<InvalidDataException>(()=>PolicyEngine.Publish(new ServerManifest{Mods=new(){mod}},Array.Empty<ModEntry>()));
        });
        Test("locked versions remain exact requirements rather than wildcard matches",()=>
        {
            var mod=Mod("same",version:"1.0.0*");var settings=new ServerManifest{Mods=new(){mod}};Throws<InvalidDataException>(()=>PolicyEngine.Publish(settings,new[]{Mod("same",version:"2.0.0")}));
            Check(!PolicyEngine.Compare(PolicyEngine.Publish(settings,Array.Empty<ModEntry>()),new[]{Mod("same",version:"2.0.0"),Connector()}).Matches);
        });
        Test("runtime normalization retains local inventory evidence and strips nested manifest markers",()=>
        {
            var mod=Mod("same*");mod.LocalFile="local.dll";mod.LocalManifest=PackageManifest.Parse("{\"name\":\"Current\",\"version_number\":\"1.0.0\",\"dependencies\":[]}");mod.LocalPackageObserved=true;mod.ManifestSource=new ManifestOrigin{Name="Current*",Version="1.0.0*",Website="https://github.com/A/Current*",Dependencies=new(){"A-Lib-1.0.0*"}};
            var settings=new ServerManifest{Mods=new(){mod}};var runtime=ConfigFields.Runtime(settings);var next=runtime.Mods.Single();Check(next.LocalFile==mod.LocalFile&&next.LocalManifest==mod.LocalManifest&&next.LocalPackageObserved&&!Json.Write(runtime).Contains('*')&&Json.Write(settings).Contains('*'));
            next.ManifestSource!.Name="Changed";ConfigFields.Preserve(settings,runtime);Check(runtime.Mods.Single().ManifestSource!.Name=="Current*");
        });
        if (args.Length > 0 && args[0] == "--online")
        {
            using var source = new RepositorySource();
            var id = PackageId.Parse("ValheimModding-Jotunn-2.29.2");
            var metadata = await source.Metadata(id, CancellationToken.None);
            var root = Root(); var path = Path.Combine(root, "jotunn.zip");
            await source.Download(id, path, CancellationToken.None);
            Downloads.Extract(path, Path.Combine(root, "stage"), id, 1024L * 1024 * 1024);
            Test("live Thunderstore metadata, redirects, download and extraction", () => Check(metadata.FullName == id.FullName && Directory.GetFiles(Path.Combine(root, "stage/packages/ValheimModding-Jotunn"), "*.dll", SearchOption.AllDirectories).Length > 0));
        }
        Console.WriteLine("All " + passed + " checks passed. Test files: " + scratch);
    }

    private static string ProfileRoot()
    {
        var root = Root(); Put(root, "plugins/Original/original.dll", "original"); Put(root, "plugins/ValheimModGate/ValheimModGate.dll", "connector");
        Put(root, "config/original.cfg", "original-settings"); Put(root, "config/BepInEx.cfg", "loader"); Put(root, "config/ModGate/server-mods.json", "policy");
        return root;
    }
    private static string Wrap(string root, string inner)
    {
        var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write("{}");
        using (var input = File.OpenRead(inner)) using (var output = zip.CreateEntry("payload.zip").Open()) input.CopyTo(output);
        return file;
    }
    private static ProfileCommand PrepareProfile(string root, int slot, char key)
    {
        var pending = new PendingInstall { Id = Guid.NewGuid().ToString("N"), Packages = new List<string> { "A-Mod-1.0.0" } };
        var stage = "ModGate/staging/" + pending.Id;
        Put(root, stage + "/packages/A-Mod/mod.dll", "server-" + slot); Put(root, stage + "/config/server.cfg", "default");
        var full = SafePath.Under(root, stage);
        foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories)) pending.Files.Add(new FileDigest { Path = file.Substring(full.Length + 1).Replace('\\', '/'), Sha256 = SafePath.Hash(file) });
        return new ProfileCommand { Kind = "install", Target = "server-" + slot, Install = pending, Profile = new SavedProfile { Slot = "server-" + slot, ServerKey = new string(key, 64), Name = "Server " + slot, Mods = new List<ModEntry> { Connector(), Mod("test") }, Packages = pending.Packages } };
    }
    private sealed class FakeSource : IPackageSource
    {
        public readonly Dictionary<string, string[]> Dependencies = new Dictionary<string, string[]>();
        public readonly List<string> Requested = new List<string>();
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token)
        {
            Requested.Add(id.FullName);
            return Task.FromResult(new PackageMetadata { FullName = id.FullName, Dependencies = Dependencies.TryGetValue(id.FullName, out var dependencies) ? dependencies.ToList() : new List<string>() });
        }
        public Task Download(PackageId id, string destination, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class FakeLookup : IPackageLookup
    {
        public readonly Dictionary<string, PackageMetadata> Packages = new();
        public readonly Dictionary<string, List<CatalogEntry>> Catalogs = new();
        public readonly Dictionary<string, byte[]> Data = new();
        public readonly List<string> Requested = new();
        public readonly List<string> CatalogRequests = new();
        public readonly List<string> Downloaded = new();
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token)
        {
            Requested.Add(id.FullName);
            if (!Packages.TryGetValue(id.FullName, out var metadata)) throw new PackageNotFoundException(id.FullName);
            return Task.FromResult(metadata);
        }
        public Task<List<CatalogEntry>> Candidates(string repository, IReadOnlyList<ModEntry> mods, CancellationToken token) { CatalogRequests.Add(repository); return Task.FromResult(Catalogs.TryGetValue(repository, out var entries) ? entries : new List<CatalogEntry>()); }
        public Task Download(PackageId id, string destination, CancellationToken token) { Downloaded.Add(id.FullName); File.WriteAllBytes(destination, Data[id.FullName]); return Task.CompletedTask; }
    }
    private sealed class RedirectHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly string location;
        public int Requests;
        public RedirectHandler(string location) { this.location = location; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Requests == 1)
            {
                var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found);
                response.Headers.Location = new Uri(location); return Task.FromResult(response);
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("trusted archive") });
        }
    }
    private sealed class ManifestGitHandler : System.Net.Http.HttpMessageHandler
    {
        public bool Changed, Dll; public readonly List<Uri> Requests=new();
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)
        {
            var uri=request.RequestUri!;Requests.Add(uri);var asset=Dll?"Shared.dll":"Shared.zip";
            if(uri.Host=="api.github.com")
            {
                if(!uri.AbsolutePath.EndsWith("/v1.0.0"))return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(Json.Write(new GitRelease{Tag="v1.0.0",Assets=new List<GitAsset>{new GitAsset{Name=asset,Url="https://github.com/Author/Shared/releases/download/v1.0.0/"+asset}}}))});
            }
            if(uri.Host=="github.com")
            {
                var response=new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found);response.Headers.Location=new Uri("https://release-assets.githubusercontent.com/asset");return Task.FromResult(response);
            }
            if(uri.Host!="release-assets.githubusercontent.com")throw new Exception("Unapproved host requested");
            byte[] bytes;
            if(Dll)bytes=System.Text.Encoding.UTF8.GetBytes(Changed?"changed-dll":"same-dll");
            else
            {
                using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
                {
                    var entry=zip.CreateEntry("Shared.dll");entry.LastWriteTime=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);using(var writer=new StreamWriter(entry.Open()))writer.Write(Changed?"changed":"same");
                    var manifest=zip.CreateEntry("manifest.json");manifest.LastWriteTime=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);using(var writer=new StreamWriter(manifest.Open()))writer.Write(Json.Write(new PackageManifest{Name="Shared",Version="1.0.0",Dependencies=new List<string>()}));
                }
                bytes=memory.ToArray();
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(bytes)});
        }
    }
    private sealed class RangeHandler : System.Net.Http.HttpMessageHandler
    {
        public readonly byte[] Bytes = Enumerable.Range(0, 2 * 256 * 1024 + 123).Select(i => (byte)(i % 251)).ToArray();
        public readonly System.Collections.Concurrent.ConcurrentBag<long> Ranges = new();
        public bool BadRange, IgnoreRanges, GzipJson;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "hexium.gg")
            {
                var text = Json.Write(new PackageMetadata { FullName = "A-Mod-1.0.0", DownloadUrl = "https://cdn.hexium.gg/mod.zip" });
                if (!GzipJson) return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(text) });
                using var stream = new MemoryStream();
                using (var gzip = new GZipStream(stream, CompressionMode.Compress, true)) { var bytes = System.Text.Encoding.UTF8.GetBytes(text); gzip.Write(bytes); }
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(stream.ToArray()) });
            }
            if (IgnoreRanges) return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(Bytes) });
            var range = request.Headers.Range!.Ranges.Single(); var start = range.From!.Value; var end = Math.Min(range.To!.Value, Bytes.Length - 1); Ranges.Add(start);
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new System.Net.Http.ByteArrayContent(Bytes.Skip((int)start).Take((int)(end - start + 1)).ToArray()) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(BadRange ? start + 1 : start, end, Bytes.Length);
            return Task.FromResult(response);
        }
    }
}
