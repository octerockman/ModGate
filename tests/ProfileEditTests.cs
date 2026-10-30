using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static List<ModEntry> EditInspect(string path) => ProfileEditing.PayloadFiles(path).Where(f => Path.GetExtension(f) == ".dll").Select(f => new ModEntry { Guid = File.ReadAllText(f).Split('|')[0], Name = File.ReadAllText(f).Split('|')[0], Version = "1.0.0", LocalFile = f }).ToList();
    private static List<ProfileEditGroup> EditRead(string root, string slot) => ProfileEditing.Read(root, Profiles.Load(root), slot, EditInspect);
    private static ProfileCommand EditCommand(string root, string slot, List<ProfileEditGroup> groups) => ProfileEditing.Prepare(root, Profiles.Load(root), slot, groups, null, new List<ProfileEditGroup>(), new List<ConfigBinding>(), CancellationToken.None);
    private static void ProfileEditChecks()
    {
        Test("offline mods disable and re-enable while preserving their assets and configs", () =>
        {
            var root = ProfileRoot(); Put(root, "plugins/Original/art.bundle", "assets"); var groups = EditRead(root, "offline"); Check(groups.Count == 1); groups[0].Enabled = false;
            Profiles.Schedule(root, EditCommand(root, "offline", groups)); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "offline" && Profiles.Load(root).OfflineDisabledGroups.Count == 1);
            Check(!Directory.Exists(SafePath.Under(root, "plugins/Original")) && File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
            groups = EditRead(root, "offline"); Check(!groups.Single().Enabled && groups.Single().Mods.Single().Guid == "original"); groups[0].Enabled = true;
            Profiles.Schedule(root, EditCommand(root, "offline", groups)); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/art.bundle")) == "assets" && Profiles.Load(root).OfflineDisabledGroups.Count == 0);
        });
        Test("editing an inactive server profile leaves the active profile unchanged", () =>
        {
            var root = ProfileRoot(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { }); Profiles.Schedule(root, PrepareProfile(root, 2, 'b')); Profiles.Apply(root, _ => { });
            var groups = EditRead(root, "server-1"); groups.Single().Enabled = false;
            Profiles.Schedule(root, EditCommand(root, "server-1", groups)); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "server-2" && File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-2");
            Check(Profiles.Load(root).Servers.Single(p => p.Slot == "server-1").DisabledGroups.Count == 1);
        });
        Test("editing the active server profile preserves its active selection", () =>
        {
            var root = ProfileRoot(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            var groups = EditRead(root, "server-1"); groups.Single().Enabled = false;
            Profiles.Schedule(root, EditCommand(root, "server-1", groups)); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "server-1" && Profiles.Load(root).Servers.Single().Mods.All(m => m.Guid != "server-1"));
            groups = EditRead(root, "server-1"); groups.Single().Enabled = true; Profiles.Schedule(root, EditCommand(root, "server-1", groups)); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-1");
        });
        Test("mod groups retain bundled plugins and root-level DLLs remain separately editable", () =>
        {
            var root = ProfileRoot(); Put(root, "plugins/Original/second.dll", "second"); Put(root, "plugins/solo.dll", "solo");
            var groups = EditRead(root, "offline"); Check(groups.Count == 2 && groups.Single(g => g.PluginPath == "Original").Mods.Count == 2);
            groups.Single(g => g.PluginPath == "solo.dll").Enabled = false; Profiles.Schedule(root, EditCommand(root, "offline", groups)); Profiles.Apply(root, _ => { });
            Check(!File.Exists(SafePath.Under(root, "plugins/solo.dll")) && File.Exists(SafePath.Under(root, "plugins/Original/second.dll")));
        });
        Test("changed mod files reject a scheduled edit before any originals move", () =>
        {
            var root = ProfileRoot(); var groups = EditRead(root, "offline"); groups.Single().Enabled = false; Profiles.Schedule(root, EditCommand(root, "offline", groups));
            Put(root, "plugins/Original/original.dll", "changed"); Throws<InvalidDataException>(() => Profiles.Apply(root, _ => { }));
            Check(File.Exists(SafePath.Under(root, "plugins/Original/original.dll")) && Profiles.Load(root).Active == "offline");
        });
        Test("profile edits cannot target ModGate or traverse outside plugin storage", () =>
        {
            foreach (var path in new[] { "../outside", "ValheimModGate", "ValheimModGate.dll", "ValheimModGate.Active", "a/b/c" })
            {
                var root = ProfileRoot(); var groups = EditRead(root, "offline"); groups[0].PluginPath = path; groups[0].Enabled = false;
                Throws<InvalidDataException>(() => EditCommand(root, "offline", groups));
            }
        });
        Test("profile edit rollback restores a disabled bundle after interruption", () =>
        {
            var root = ProfileRoot(); var groups = EditRead(root, "offline"); groups.Single().Enabled = false;
            Profiles.Schedule(root, EditCommand(root, "offline", groups)); Throws<IOException>(() => Profiles.Apply(root, _ => { }, _ => throw new IOException("fixture interruption")));
            Check(File.Exists(SafePath.Under(root, "plugins/Original/original.dll")) && (Profiles.Load(root).OfflineDisabledGroups?.Count ?? 0) == 0);
            Profiles.Apply(root, _ => { }); Check(Profiles.Load(root).OfflineDisabledGroups.Count == 1);
        });
        Test("removing server profiles keeps disabled original offline mods", () =>
        {
            var root = ProfileRoot(); var groups = EditRead(root, "offline"); groups.Single().Enabled = false;
            Profiles.Schedule(root, EditCommand(root, "offline", groups)); Profiles.Apply(root, _ => { });
            Profiles.Schedule(root, new ProfileCommand { Kind = "removeAll" }); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).OfflineDisabledGroups.Count == 1 && EditRead(root, "offline").Single().Mods.Single().Guid == "original");
        });
        Test("adding copied installed mods preserves the source and activates only on restart", () =>
        {
            var root = ProfileRoot(); var groups = EditRead(root, "offline"); var mod = Mod("addition"); mod.LocalFile = Put(root, "plugins/Addition/add.dll", "addition");
            var staged = Downloads.Stage(root, Array.Empty<PackageId>(), new FakeSource(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            ManualMods.Stage(root, new[] { mod }, new[] { mod }, staged, CancellationToken.None);
            // An inactive saved server provides the target for the staged copy.
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), new ProfileCatalog { Servers = new List<SavedProfile> { new SavedProfile { Slot = "server-1", ServerKey = new string('c', 64), Name = "Target" } } });
            var added = new ProfileEditGroup { PluginPath = "ValheimModGate.Active/" + staged.LocalPackages.Single(), Directory = true, Enabled = true, Mods = new List<ModEntry> { mod } };
            var command = ProfileEditing.Prepare(root, Profiles.Load(root), "server-1", Array.Empty<ProfileEditGroup>(), staged, new List<ProfileEditGroup> { added }, new List<ConfigBinding>(), CancellationToken.None);
            Profiles.Schedule(root, command); Check(!Directory.Exists(SafePath.Under(root, "ModGate/profiles/server-1/plugins")));
            Profiles.Apply(root, _ => { }); Check(Profiles.Load(root).Active == "offline" && File.ReadAllText(mod.LocalFile) == "addition");
            Check(File.Exists(SafePath.Under(root, "ModGate/profiles/server-1/plugins/" + added.PluginPath + "/add.dll")));
        });
        Test("server rules still exclude prohibited installed additions", () =>
        {
            var profile = new SavedProfile { Slot = "server-1", ServerPolicy = new Policy { AllowExtras = false, ForbiddenPluginGuids = new List<string> { "admin" }, Mods = new List<ModEntry> { Mod("client") } } };
            var catalog = new ProfileCatalog { Servers = new List<SavedProfile> { profile } };
            Check(!ProfileEditing.CanAdd(catalog, "server-1", Mod("extra")) && !ProfileEditing.CanAdd(catalog, "server-1", Mod("admin")) && ProfileEditing.CanAdd(catalog, "server-1", Mod("client")));
        });
    }
}
