using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static string ConnectorFixture(string root, string relative, string identity = "ValheimModGate")
    {
        var path = SafePath.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(identity), typeof(object).Assembly);
        assembly.DefineDynamicModule("Fixture").DefineType("NoPluginGuid", TypeAttributes.Public).CreateType();
        assembly.Save(path); return path;
    }
    private static void PublishingLayoutChecks()
    {
        Test("manager package connector metadata is protected even when the DLL and directory are renamed", () =>
        {
            var root = ProfileRoot(); var connector = ConnectorFixture(root, "plugins/SomeAuthor-UnknownPackage/nested/renamed.dll");
            Put(root, "plugins/SomeAuthor-UnknownPackage/manifest.json", "package-metadata"); Put(root, "plugins/SomeAuthor-UnknownPackage/assets/skin.bundle", "connector-assets");
            var before = SafePath.Hash(connector);
            Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            Check(SafePath.Hash(connector) == before && File.ReadAllText(SafePath.Under(root, "plugins/SomeAuthor-UnknownPackage/assets/skin.bundle")) == "connector-assets");
            Check(File.Exists(SafePath.Under(root, "ModGate/profiles/offline/plugins/Original/original.dll")));
            Check(!Directory.Exists(SafePath.Under(root, "ModGate/profiles/offline/plugins/SomeAuthor-UnknownPackage")));
            Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll" }); Profiles.Apply(root, _ => { });
            Check(SafePath.Hash(connector) == before && File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
        Test("ordinary assembly packages still switch while a loose renamed connector stays active", () =>
        {
            var root = ProfileRoot(); var connector = ConnectorFixture(root, "plugins/connector-renamed.DLL");
            var preloader = ConnectorFixture(root, "plugins/RenamedPatcherPackage/also-renamed.dll", "ValheimModGate.Preloader");
            ConnectorFixture(root, "plugins/OtherTeam-Ordinary/ordinary.dll", "AnOrdinaryMod");
            Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            Check(File.Exists(connector) && File.Exists(preloader) && File.Exists(SafePath.Under(root, "ModGate/profiles/offline/plugins/OtherTeam-Ordinary/ordinary.dll")));
            Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "offline" }); Profiles.Apply(root, _ => { });
            Check(File.Exists(connector) && File.Exists(SafePath.Under(root, "plugins/OtherTeam-Ordinary/ordinary.dll")));
        });
        Test("canonical connector filenames remain protected when metadata is unavailable", () =>
        {
            var root = ProfileRoot(); var connector = Put(root, "plugins/Team-ModGate/ValheimModGate.dll", "unreadable-metadata");
            Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(connector) == "unreadable-metadata");
        });
        Test("restore point capture excludes a whole manager connector package including its assets", () =>
        {
            var root = ProfileRoot(); ConnectorFixture(root, "plugins/Team-ModGate/renamed.dll"); Put(root, "plugins/Team-ModGate/settings.txt", "global-connector-asset");
            var snapshot = ProfileSnapshots.Capture(root, "offline", "Manager fixture");
            Check(snapshot.Files.All(f => !f.Path.StartsWith("plugins/Team-ModGate/", StringComparison.Ordinal)));
            Check(snapshot.Directories.All(p => p != "plugins/Team-ModGate"));
            Check(snapshot.Files.Any(f => f.Path == "plugins/Original/original.dll"));
            var plan = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id);
            Profiles.Schedule(root, new ProfileCommand { Kind = "restore", Target = "offline", Restore = plan }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Team-ModGate/settings.txt")) == "global-connector-asset");
        });
        Test("restore stages cannot smuggle a renamed connector assembly through an unrelated package name", () =>
        {
            var root = ProfileRoot(); var plan = ProfileSnapshots.StageCurrent(root, "offline");
            ConnectorFixture(ProfileSnapshots.StagePath(root, plan), "plugins/UnrelatedName/ordinary.dll");
            Throws<InvalidDataException>(() => ProfileSnapshots.Seal(root, plan));
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
        Test("a stored renamed connector is rejected instead of being restored as a second connector", () =>
        {
            var root = ProfileRoot(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            var connector = ConnectorFixture(root, "ModGate/profiles/offline/plugins/Unexpected/renamed.dll");
            Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "offline" });
            Throws<InvalidDataException>(() => Profiles.Apply(root, _ => { }));
            Check(Profiles.Load(root).Active == "server-1" && File.Exists(connector));
            Check(File.Exists(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")));
        });
        Test("profile editor ignores a connector package before consulting incomplete plugin GUID metadata", () =>
        {
            var root = ProfileRoot(); ConnectorFixture(root, "plugins/Team-ModGate/renamed.dll");
            var inspected = new List<string>();
            var groups = ProfileEditing.Read(root, Profiles.Load(root), "offline", path => { inspected.Add(path); return new List<ModEntry> { Mod(Path.GetFileName(path)) }; });
            Check(groups.All(g => g.PluginPath != "Team-ModGate") && inspected.All(p => !p.EndsWith("Team-ModGate", StringComparison.Ordinal)));
        });
        Test("forged edit metadata cannot disable a connector whose reported plugin GUID was omitted", () =>
        {
            var root = ProfileRoot(); ConnectorFixture(root, "plugins/Team-ModGate/renamed.dll");
            var group = new ProfileEditGroup { PluginPath = "Team-ModGate", Directory = true, Enabled = false, WasEnabled = true, Mods = new List<ModEntry> { Mod("fake-guid") } };
            Throws<InvalidDataException>(() => ProfileEditing.Prepare(root, Profiles.Load(root), "offline", new[] { group }, null, new List<ProfileEditGroup>(), new List<ConfigBinding>(), CancellationToken.None));
            Check(File.Exists(SafePath.Under(root, "plugins/Team-ModGate/renamed.dll")) && !Profiles.Pending(root));
        });
    }
}
