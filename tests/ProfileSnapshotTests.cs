using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static void ProfileSnapshotChecks()
    {
        string Fixture()
        {
            var root = ProfileRoot();
            var original = Mod("original");
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), new ProfileCatalog
            {
                OfflineMods = new List<ModEntry> { Connector(), original },
                OfflineBindings = new List<ConfigBinding> { new ConfigBinding { Guid = original.Guid, Files = new List<string> { "original.cfg" } } }
            });
            return root;
        }
        string Payload(string root, ProfileSnapshot snapshot) => SafePath.Under(root, "ModGate/profile-history/" + snapshot.Slot + "/" + snapshot.Id + "/payload");
        string PayloadFile(string root, ProfileSnapshot snapshot, string relative) => SafePath.Under(Payload(root, snapshot), snapshot.Schema == 2 ? snapshot.Files.Single(f => f.Path == relative).Sha256.ToLowerInvariant() : relative);
        ProfileCommand Restore(ProfileRestorePlan plan) => new ProfileCommand { Kind = "restore", Target = plan.Slot, Restore = plan };

        Test("compact restore points avoid the long PlanBuild backup path and reconstruct every file", () =>
        {
            var root = Fixture();
            var path = "plugins/ValheimModGate.Active/LocalMod_" + new string('A', 64) + "/Translations/Chinese/PlanBuild.chinese.json";
            Put(root, path, "translation-fixture"); Put(root, "plugins/Original/same-content.json", "translation-fixture");
            Directory.CreateDirectory(SafePath.Under(root, "plugins/Original/empty"));
            var snapshot = ProfileSnapshots.Capture(root, "offline", "Long-path fixture");
            Check(snapshot.Schema == 2 && snapshot.Files.Any(f => f.Path == path));
            Check(SafePath.Under(Payload(root, snapshot), path).Length > 260);
            Check(Directory.GetFiles(Payload(root, snapshot)).All(f => Path.GetFileName(f).Length == 64 && f.Length < 260));
            Check(snapshot.Files.Count > Directory.GetFiles(Payload(root, snapshot)).Length);
            ProfileSnapshots.Verify(root, "offline", snapshot.Id);
            var restore = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id);
            Check(File.ReadAllText(SafePath.Under(ProfileSnapshots.StagePath(root, restore), path)) == "translation-fixture");
            Check(Directory.Exists(SafePath.Under(ProfileSnapshots.StagePath(root, restore), "plugins/Original/empty")));
        });
        Test("existing tree-format restore points still verify and prepare correctly", () =>
        {
            var root = Fixture(); var snapshot = ProfileSnapshots.Capture(root, "offline", "Legacy fixture");
            var original = Payload(root, snapshot); var legacyId = Guid.NewGuid().ToString("N");
            var legacyFolder = SafePath.Under(root, "ModGate/profile-history/offline/" + legacyId);
            var tree = SafePath.Under(legacyFolder, "payload"); Directory.CreateDirectory(tree);
            foreach (var directory in snapshot.Directories) Directory.CreateDirectory(SafePath.Under(tree, directory));
            foreach (var file in snapshot.Files) File.Copy(SafePath.Under(original, file.Sha256), SafePath.Under(tree, file.Path));
            snapshot.Schema = 1; snapshot.Id = legacyId; Json.Save(SafePath.Under(legacyFolder, "snapshot.json"), snapshot);
            Check(ProfileSnapshots.Verify(root, "offline", legacyId).Schema == 1);
            var restore = ProfileSnapshots.PrepareRestore(root, "offline", legacyId);
            Check(File.ReadAllText(SafePath.Under(ProfileSnapshots.StagePath(root, restore), "plugins/Original/original.dll")) == "original");
        });

        Test("restore points preserve offline mods assets configs and metadata without global files or passwords", () =>
        {
            var root = Fixture(); Put(root, "plugins/Original/art.bundle", "original-art"); Put(root, "plugins/Original/a/deep/data.bin", "nested"); Put(root, "plugins/Original/a-other/data.bin", "sibling");
            Put(root, "plugins/BepInEx.dll", "loader-library"); Put(root, "config/studio.valheim.modgate.cfg", "connector-config"); Put(root, "ModGate/private-passwords/secret.bin", "secret");
            var snapshot = ProfileSnapshots.Capture(root, "offline", "Successful start", working: true);
            Check(snapshot.Working && snapshot.Slot == "offline" && snapshot.Profile.Mods.Any(m => m.Guid == "original") && snapshot.Profile.ConfigBindings.Single().Files.Single() == "original.cfg");
            Check(snapshot.Files.Any(f => f.Path == "plugins/Original/art.bundle") && snapshot.Files.Any(f => f.Path == "config/original.cfg"));
            Check(snapshot.Files.All(f => !f.Path.Contains("ValheimModGate") && !f.Path.Contains("BepInEx") && !f.Path.Contains("private-passwords") && !f.Path.StartsWith("config/ModGate/")));
            Check(snapshot.Bytes == snapshot.Files.Sum(f => new FileInfo(PayloadFile(root, snapshot, f.Path)).Length));
            Check(ProfileSnapshots.Verify(root, "offline", snapshot.Id).Id == snapshot.Id && File.ReadAllText(SafePath.Under(root, "config/BepInEx.cfg")) == "loader");
        });
        Test("inactive server restore points capture that server without copying the active setup", () =>
        {
            var root = Fixture(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { }); Profiles.Schedule(root, PrepareProfile(root, 2, 'b')); Profiles.Apply(root, _ => { });
            var catalog = Profiles.Load(root); catalog.Servers.Single(p => p.Slot == "server-1").CredentialKey = "private-credential-reference"; Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var snapshot = ProfileSnapshots.Capture(root, "server-1", "Before update");
            Check(snapshot.Profile.Name == "Server 1" && snapshot.Profile.ServerKey == new string('a', 64) && snapshot.Profile.CredentialKey == "");
            Check(File.ReadAllText(PayloadFile(root, snapshot, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-1");
            Check(Profiles.Load(root).Active == "server-2" && File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-2");
        });
        Test("identical working captures reuse verified bytes while changed setups and reasons retain history", () =>
        {
            var root = Fixture(); var first = ProfileSnapshots.Capture(root, "offline", "Successful join", true);
            Check(ProfileSnapshots.Capture(root, "offline", "Successful join", true).Id == first.Id && ProfileSnapshots.List(root, "offline").Count == 1);
            var beforeEdit = ProfileSnapshots.Capture(root, "offline", "Before profile edits"); Check(beforeEdit.Id != first.Id);
            Put(root, "config/original.cfg", "changed-config"); var changed = ProfileSnapshots.Capture(root, "offline", "Successful join", true);
            Check(changed.Id != first.Id && ProfileSnapshots.List(root, "offline").Count == 3);
            File.WriteAllText(PayloadFile(root, changed, "config/original.cfg"), "damaged-history");
            var replacement = ProfileSnapshots.Capture(root, "offline", "Successful join", true);
            Check(replacement.Id != changed.Id && ProfileSnapshots.Verify(root, "offline", replacement.Id).Working);
        });
        Test("legacy profiles without optional config and disabled metadata can still save restore points", () =>
        {
            var root = Fixture(); var catalog = Profiles.Load(root); catalog.OfflineBindings = null!; catalog.OfflineDisabledGroups = null!;
            catalog.Servers.Add(new SavedProfile { Slot = "server-1", Name = "Legacy", ServerKey = new string('a', 64), ConfigBindings = null!, DisabledGroups = null! });
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var offline = ProfileSnapshots.Capture(root, "offline", "Legacy offline"); var server = ProfileSnapshots.Capture(root, "server-1", "Legacy server");
            Check(offline.Profile.ConfigBindings.Count == 0 && offline.Profile.DisabledGroups.Count == 0 && server.Profile.ConfigBindings.Count == 0 && server.Profile.DisabledGroups.Count == 0);
        });
        Test("snapshot verification rejects changed payloads before any restore is prepared", () =>
        {
            var root = Fixture(); var snapshot = ProfileSnapshots.Capture(root, "offline", "Known setup");
            File.WriteAllText(PayloadFile(root, snapshot, "plugins/Original/original.dll"), "corrupt");
            Throws<InvalidDataException>(() => ProfileSnapshots.Verify(root, "offline", snapshot.Id));
            Throws<InvalidDataException>(() => ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id));
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original" && !Profiles.Pending(root));
        });
        Test("snapshot receipts reject extra files traversal and protected replacement paths", () =>
        {
            foreach (var change in new[] { "extra", "traversal", "protected", "duplicate" })
            {
                var root = Fixture(); var snapshot = ProfileSnapshots.Capture(root, "offline", "Known setup");
                if (change == "extra") File.WriteAllText(SafePath.Under(Payload(root, snapshot), "unlisted.dll"), "unlisted");
                else
                {
                    if (change == "duplicate") snapshot.Files.Add(Json.CloneLocal(snapshot.Files[0]));
                    else snapshot.Files[0].Path = change == "traversal" ? "../outside.dll" : "config/BepInEx.cfg";
                    Json.Save(SafePath.Under(root, "ModGate/profile-history/offline/" + snapshot.Id + "/snapshot.json"), snapshot);
                }
                Throws<InvalidDataException>(() => ProfileSnapshots.Verify(root, "offline", snapshot.Id));
                Check(File.ReadAllText(SafePath.Under(root, "config/BepInEx.cfg")) == "loader");
            }
        });
        Test("restore staging refuses current files or metadata changed after review", () =>
        {
            foreach (var metadata in new[] { false, true })
            {
                var root = Fixture(); var snapshot = ProfileSnapshots.Capture(root, "offline", "Known setup"); var plan = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id);
                if (metadata)
                {
                    var catalog = Profiles.Load(root); catalog.OfflineMods.Single(m => m.Guid == "original").Version = "2.0.0"; Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
                }
                else Put(root, "plugins/Original/original.dll", "changed-after-review");
                Throws<InvalidDataException>(() => ProfileSnapshots.ValidateRestore(root, Profiles.Load(root), plan));
                Check(!Profiles.Pending(root));
            }
        });
        Test("restore staging detects changes to a sealed payload without changing live files", () =>
        {
            var root = Fixture(); var plan = ProfileSnapshots.StageCurrent(root, "offline");
            File.WriteAllText(SafePath.Under(ProfileSnapshots.StagePath(root, plan), "plugins/Original/original.dll"), "altered-staging");
            Throws<InvalidDataException>(() => ProfileSnapshots.ValidateRestore(root, Profiles.Load(root), plan));
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
        Test("offline restore uses the profile transaction and preserves loader and connector settings", () =>
        {
            var root = Fixture(); Put(root, "plugins/Original/art.bundle", "old-art"); var snapshot = ProfileSnapshots.Capture(root, "offline", "Last working", true);
            Put(root, "plugins/Original/original.dll", "new-code"); Put(root, "plugins/Original/art.bundle", "new-art"); Put(root, "config/original.cfg", "new-config");
            var catalog = Profiles.Load(root); catalog.OfflineMods.Single(m => m.Guid == "original").Version = "2.0.0"; Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var plan = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id); Profiles.Schedule(root, Restore(plan));
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "new-code"); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "offline" && Profiles.Load(root).OfflineMods.Single(m => m.Guid == "original").Version == "1.0.0");
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original" && File.ReadAllText(SafePath.Under(root, "plugins/Original/art.bundle")) == "old-art" && File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate/ValheimModGate.dll")) == "connector" && File.ReadAllText(SafePath.Under(root, "config/BepInEx.cfg")) == "loader" && File.ReadAllText(SafePath.Under(root, "config/ModGate/server-mods.json")) == "policy");
            Check(!Profiles.Pending(root) && !Directory.Exists(SafePath.Under(root, "ModGate/staging/" + plan.StageId)));
        });
        Test("restoring an inactive server activates it while retaining other profiles and credential references", () =>
        {
            var root = Fixture(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { }); Profiles.Schedule(root, PrepareProfile(root, 2, 'b')); Profiles.Apply(root, _ => { });
            var catalog = Profiles.Load(root); catalog.Servers.Single(p => p.Slot == "server-1").CredentialKey = "current-credential-reference"; Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var snapshot = ProfileSnapshots.Capture(root, "server-1", "Last working", true);
            Put(root, "ModGate/profiles/server-1/plugins/ValheimModGate.Active/A-Mod/mod.dll", "changed-server-one"); Put(root, "ModGate/profiles/server-1/config/server.cfg", "changed-config");
            var plan = ProfileSnapshots.PrepareRestore(root, "server-1", snapshot.Id); Profiles.Schedule(root, Restore(plan)); Profiles.Apply(root, _ => { });
            Check(Profiles.Load(root).Active == "server-1" && Profiles.Load(root).Servers.Count == 2 && Profiles.Load(root).Servers.Single(p => p.Slot == "server-1").CredentialKey == "current-credential-reference");
            Check(File.ReadAllText(SafePath.Under(root, "plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-1" && File.ReadAllText(SafePath.Under(root, "config/server.cfg")) == "default");
            Check(File.ReadAllText(SafePath.Under(root, "ModGate/profiles/server-2/plugins/ValheimModGate.Active/A-Mod/mod.dll")) == "server-2");
        });
        Test("restore points include disabled bundles and restore their enabled state honestly", () =>
        {
            var root = Fixture(); var hiddenPath = ProfileEditing.DisabledPath("offline", "Hidden"); Put(root, hiddenPath + "/hidden.dll", "hidden-code");
            var catalog = Profiles.Load(root); catalog.OfflineDisabledGroups.Add(new ProfileEditGroup { PluginPath = "Hidden", Directory = true, Mods = new List<ModEntry> { Mod("hidden") } }); Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var snapshot = ProfileSnapshots.Capture(root, "offline", "Disabled bundle saved");
            Directory.Move(SafePath.Under(root, hiddenPath), SafePath.Under(root, "plugins/Hidden")); catalog.OfflineDisabledGroups.Clear(); catalog.OfflineMods.Add(Mod("hidden")); Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var plan = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id); Profiles.Schedule(root, Restore(plan)); Profiles.Apply(root, _ => { });
            Check(!Directory.Exists(SafePath.Under(root, "plugins/Hidden")) && File.ReadAllText(SafePath.Under(root, hiddenPath + "/hidden.dll")) == "hidden-code");
            Check(Profiles.Load(root).OfflineDisabledGroups.Single().Mods.Single().Guid == "hidden" && Profiles.Load(root).OfflineMods.All(m => m.Guid != "hidden"));
        });
        Test("interrupted restore rolls back current files and can retry the same reviewed stage", () =>
        {
            foreach (var failAt in new[] { 0, 3, 5, 8 })
            {
                var root = Fixture(); var snapshot = ProfileSnapshots.Capture(root, "offline", "Last working", true); Put(root, "plugins/Original/original.dll", "current-code"); Put(root, "config/original.cfg", "current-config");
                var plan = ProfileSnapshots.PrepareRestore(root, "offline", snapshot.Id); Profiles.Schedule(root, Restore(plan));
                Throws<IOException>(() => Profiles.Apply(root, _ => { }, index => { if (index == failAt) throw new IOException("Interrupted restore fixture"); }));
                Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "current-code" && File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "current-config");
                Check(Profiles.Load(root).Active == "offline" && !File.Exists(SafePath.Under(root, "ModGate/profile-journal.json")));
                Profiles.Apply(root, _ => { }); Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
            }
        });
        Test("failed before-change backup unlocks controls and retains the request and staged payload", () =>
        {
            var root = Fixture(); Profiles.Schedule(root, PrepareProfile(root, 1, 'a')); Profiles.Apply(root, _ => { });
            var command = PrepareProfile(root, 1, 'a'); Profiles.Schedule(root, command);
            var request = File.ReadAllText(SafePath.Under(root, "ModGate/profile-request.json"));
            Put(root, "ModGate/profile-history", "blocked-history-directory");
            Throws<IOException>(() => Profiles.Apply(root, _ => { }));
            Check(!Profiles.Pending(root) && Profiles.Load(root).Active == "server-1");
            Check(File.ReadAllText(Directory.GetFiles(SafePath.Under(root, "ModGate/failed-profile-requests")).Single()) == request);
            Check(Directory.Exists(SafePath.Under(root, "ModGate/staging/" + command.Install!.Id)));
            Check(!File.Exists(SafePath.Under(root, "ModGate/profile-journal.json")));
            Profiles.Apply(root, _ => { }); Check(!Profiles.Pending(root));
        });
        Test("before-change snapshots cover existing mutations without snapshotting unrelated switches", () =>
        {
            var root = Fixture();
            foreach (var kind in new[] { "switch", "delete", "removeAll" }) ProfileSnapshots.BeforeChange(root, new ProfileCommand { Kind = kind, Target = "offline" });
            ProfileSnapshots.BeforeChange(root, new ProfileCommand { Kind = "install", Target = "server-1" }); Check(ProfileSnapshots.List(root, "offline").Count == 0);
            ProfileSnapshots.BeforeChange(root, new ProfileCommand { Kind = "edit", Target = "offline" });
            ProfileSnapshots.BeforeChange(root, new ProfileCommand { Kind = "restore", Target = "offline" });
            ProfileSnapshots.Capture(root, "offline", "Successful join", true);
            var list = ProfileSnapshots.List(root, "offline"); Check(list.Count == 3 && list.Count(s => s.Working) == 1 && list.All(s => s.Profile.Name == "Offline"));
        });
        Test("repair replacement staging preserves unselected files and configs until the transaction commits", () =>
        {
            var root = Fixture(); Put(root, "plugins/Unselected/other.dll", "keep-other"); var plan = ProfileSnapshots.StageCurrent(root, "offline");
            File.WriteAllText(SafePath.Under(ProfileSnapshots.StagePath(root, plan), "plugins/Original/original.dll"), "repaired"); ProfileSnapshots.Seal(root, plan);
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
            ProfileSnapshots.ValidateRestore(root, Profiles.Load(root), plan); Profiles.Schedule(root, Restore(plan)); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "repaired" && File.ReadAllText(SafePath.Under(root, "plugins/Unselected/other.dll")) == "keep-other" && File.ReadAllText(SafePath.Under(root, "config/original.cfg")) == "original-settings");
        });
        Test("snapshot operations reject invalid slots identifiers and cancellation before changing live files", () =>
        {
            var root = Fixture();
            Throws<InvalidDataException>(() => ProfileSnapshots.Capture(root, "../outside", "Invalid"));
            Throws<InvalidDataException>(() => ProfileSnapshots.PrepareRestore(root, "offline", "../outside"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Throws<OperationCanceledException>(() => ProfileSnapshots.Capture(root, "offline", "Cancelled", token: cancelled.Token));
            Throws<OperationCanceledException>(() => ProfileSnapshots.StageCurrent(root, "offline", cancelled.Token));
            Check(ProfileSnapshots.List(root, "offline").Count == 0 && !Profiles.Pending(root) && File.ReadAllText(SafePath.Under(root, "plugins/Original/original.dll")) == "original");
        });
    }
}
