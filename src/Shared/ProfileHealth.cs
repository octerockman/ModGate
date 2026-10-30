using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    [DataContract] internal sealed class ProfileHealthGroup
    {
        [DataMember] public string PluginPath = "";
        [DataMember] public List<ModEntry> Mods = new List<ModEntry>();
        [DataMember] public List<FileDigest> Files = new List<FileDigest>();
    }
    [DataContract] internal sealed class ProfileHealthReceipt
    {
        [DataMember] public string Slot = "";
        [DataMember] public List<ProfileHealthGroup> Groups = new List<ProfileHealthGroup>();
    }
    internal sealed class ProfileHealthIssue
    {
        public string Code = "", PluginPath = "", Guid = "", Package = "", Message = "";
        public bool Warning, CanRepair;
        public string Id => Code + "|" + PluginPath + "|" + Guid + "|" + Message;
    }
    internal sealed class ProfileHealthReport
    {
        public string Slot = "", ActiveSlot = "";
        public bool HasReceipt, LoadedEvidenceAvailable;
        public List<ProfileHealthGroup> Groups = new List<ProfileHealthGroup>();
        public List<ProfileHealthIssue> Issues = new List<ProfileHealthIssue>();
        public List<ModEntry> Installed = new List<ModEntry>();
        public bool Healthy => Issues.All(i => i.Warning);
        public bool CanRepair => Issues.Any(i => i.CanRepair);
    }
    internal sealed class ProfileRepairPlan
    {
        public string Slot = "", ActiveSlot = "";
        public List<ProfileHealthGroup> Groups = new List<ProfileHealthGroup>();
        public List<PackageId> Packages = new List<PackageId>();
    }
    internal static class ProfileHealth
    {
        private const string Owned = "ValheimModGate.Active/";
        private static List<ModEntry> Expected(ProfileCatalog catalog, string slot)
        {
            if (slot == "offline") return catalog.OfflineMods;
            return catalog.Servers.SingleOrDefault(p => p.Slot == slot)?.Mods ?? throw new InvalidDataException("Unknown profile.");
        }
        private static string ReceiptPath(string root, string slot)
        {
            if (slot != "offline" && !new[] { "server-1", "server-2", "server-3", "server-4" }.Contains(slot)) throw new InvalidDataException("Unknown profile slot.");
            return SafePath.Under(root, "ModGate/health/" + slot + ".json");
        }
        private static bool OwnedGroup(string path) => path.StartsWith(Owned, StringComparison.Ordinal) && path.Length > Owned.Length && path.Substring(Owned.Length).IndexOfAny(new[] { '/', '\\' }) < 0;
        private static string GroupSource(ProfileCatalog catalog, string slot, string group) => ProfileEditing.Plugins(catalog, slot) + "/" + group;
        private static List<FileDigest> Files(string root, string relative, CancellationToken token)
        {
            var directory = SafePath.Under(root, relative);
            if (!Directory.Exists(directory)) return new List<FileDigest>();
            return ProfileEditing.PayloadFiles(directory).Select(file =>
            {
                token.ThrowIfCancellationRequested();
                var name = file.Substring(directory.Length + 1).Replace('\\', '/');
                SafePath.ReadSource(directory, name);
                return new FileDigest { Path = name, Sha256 = SafePath.Hash(file) };
            }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }
        // Capture only after a successful, trusted install/edit/restore. Verification never blesses changed files.
        public static void Capture(string root, ProfileCatalog catalog, string slot, Func<string, List<ModEntry>> inspect, CancellationToken token = default)
        {
            var expected = Expected(catalog, slot);
            var receipt = new ProfileHealthReceipt { Slot = slot };
            var path = ReceiptPath(root, slot);
            var previous = File.Exists(path) ? Json.ReadLocal<ProfileHealthReceipt>(path) : new ProfileHealthReceipt { Slot = slot };
            if (previous.Slot != slot || previous.Groups == null || previous.Groups.Any(g => !OwnedGroup(g.PluginPath))) throw new InvalidDataException("Invalid existing profile health receipt.");
            var owned = SafePath.Under(root, ProfileEditing.Plugins(catalog, slot) + "/ValheimModGate.Active");
            if (Directory.Exists(owned))
                foreach (var directory in Directory.GetDirectories(owned).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var group = Owned + Path.GetFileName(directory);
                    SafePath.Under(root, GroupSource(catalog, slot, group));
                    var actual = inspect(directory);
                    var mods = actual.Select(m => expected.FirstOrDefault(e => e.Guid == m.Guid) ?? m).Where(m => m.Guid != Constants.Guid).ToList();
                    if (actual.Any(m => m.Guid == Constants.Guid)) throw new InvalidDataException("A profile health receipt cannot own ModGate.");
                    var next = new ProfileHealthGroup { PluginPath = group, Mods = Json.CloneLocal(mods), Files = Files(root, GroupSource(catalog, slot, group), token) };
                    var trusted = previous.Groups.FirstOrDefault(g => g.PluginPath == group && StillExpected(g, expected));
                    if (trusted != null) { next.Mods = Json.CloneLocal(trusted.Mods); next.Files = Json.CloneLocal(trusted.Files); }
                    receipt.Groups.Add(next);
                }
            foreach (var missing in previous.Groups.Where(g => receipt.Groups.All(n => n.PluginPath != g.PluginPath) && StillExpected(g, expected)))
                receipt.Groups.Add(Json.CloneLocal(missing));
            Json.Save(path, receipt);
        }
        public static ProfileHealthReport Verify(string root, ProfileCatalog catalog, string slot, Func<string, List<ModEntry>> inspect, IReadOnlyList<ModEntry>? loaded = null, CancellationToken token = default)
        {
            var expected = Expected(catalog, slot);
            var report = new ProfileHealthReport { Slot = slot, ActiveSlot = catalog.Active, LoadedEvidenceAvailable = loaded != null && slot == catalog.Active };
            var receiptPath = ReceiptPath(root, slot);
            if (File.Exists(receiptPath))
            {
                var receipt = Json.ReadLocal<ProfileHealthReceipt>(receiptPath);
                if (receipt.Slot != slot || receipt.Groups == null || receipt.Groups.Any(g => !OwnedGroup(g.PluginPath)) || receipt.Groups.Select(g => g.PluginPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Groups.Count) throw new InvalidDataException("Invalid profile health receipt.");
                report.HasReceipt = true; report.Groups = receipt.Groups;
            }
            else report.Issues.Add(new ProfileHealthIssue { Code = "NoBaseline", Warning = true, Message = "No trusted file baseline is saved for this profile. Versions can be checked; file integrity cannot yet be confirmed." });
            var plugins = SafePath.Under(root, ProfileEditing.Plugins(catalog, slot));
            if (Directory.Exists(plugins))
            {
                try { report.Installed = inspect(plugins); }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is BadImageFormatException)
                { report.Issues.Add(new ProfileHealthIssue { Code = "UnreadableInventory", Message = "Installed mod metadata could not be read: " + error.Message }); }
            }
            foreach (var group in report.Groups)
            {
                token.ThrowIfCancellationRequested();
                var actual = Files(root, GroupSource(catalog, slot, group.PluginPath), token);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in group.Files)
                {
                    SafePath.Under(root, GroupSource(catalog, slot, group.PluginPath) + "/" + file.Path);
                    if (!names.Add(file.Path) || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid file hash receipt.");
                    var current = actual.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                    if (current == null) Add(report, group, "MissingFile", "Missing file: " + group.PluginPath + "/" + file.Path);
                    else if (!current.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) Add(report, group, "ChangedFile", "Changed file: " + group.PluginPath + "/" + file.Path);
                }
                foreach (var extra in actual.Where(f => !names.Contains(f.Path))) Add(report, group, "UntrackedFile", "A file is outside the saved package receipt: " + group.PluginPath + "/" + extra.Path, warning: true);
            }
            foreach (var wanted in expected.Where(m => m.Guid != Constants.Guid))
            {
                token.ThrowIfCancellationRequested();
                var actual = report.Installed.FirstOrDefault(m => m.Guid == wanted.Guid);
                var group = report.Groups.FirstOrDefault(g => g.Mods.Any(m => m.Guid == wanted.Guid));
                if (actual == null) Add(report, group, "MissingPlugin", wanted.Name + " is recorded in this profile but its plugin is missing.", wanted);
                else if (!PolicyEngine.VersionsEqual(actual.Version, wanted.Version)) Add(report, group, "InstalledVersion", wanted.Name + ": recorded " + wanted.Version + ", installed " + actual.Version + ".", wanted);
                if (actual != null)
                    foreach (var dependency in DependencyRules.Of(wanted).Where(d => !d.Optional || report.Installed.Any(m => m.Guid == d.Guid)))
                    {
                        var found = report.Installed.FirstOrDefault(m => m.Guid == dependency.Guid);
                        if (found == null) Add(report, null, "MissingDependency", wanted.Name + " declares " + dependency.Guid + (dependency.MinimumVersion.Length > 0 ? " " + dependency.MinimumVersion + " or newer" : "") + ". This is a suggestion; BepInEx may refuse to load the mod without it.", wanted, warning: true);
                        else if (dependency.MinimumVersion.Length > 0 && System.Version.TryParse(dependency.MinimumVersion, out var minimum) && (!System.Version.TryParse(found.Version, out var version) || version < minimum))
                            Add(report, null, "DependencyVersion", wanted.Name + " declares " + dependency.Guid + " " + dependency.MinimumVersion + " or newer; installed " + found.Version + ". Setup can continue, but the mod may not load.", wanted, warning: true);
                    }
                if (report.LoadedEvidenceAvailable && actual != null)
                {
                    var running = loaded!.FirstOrDefault(m => m.Guid == wanted.Guid);
                    if (running == null || !PolicyEngine.VersionsEqual(running.Version, actual.Version))
                        Add(report, null, "RestartRequired", wanted.Name + ": installed " + actual.Version + ", loaded " + (running?.Version ?? "not loaded") + ". Restart to load the installed profile.", wanted, warning: true);
                }
            }
            foreach (var issue in report.Issues)
            {
                var group = report.Groups.FirstOrDefault(g => g.PluginPath == issue.PluginPath);
                issue.CanRepair = group != null && issue.Code != "UntrackedFile" && !report.Issues.Any(i => i.PluginPath == group.PluginPath && i.Code == "UntrackedFile") && RepairPackage(group) != null;
            }
            return report;
        }
        private static void Add(ProfileHealthReport report, ProfileHealthGroup? group, string code, string message, ModEntry? mod = null, bool warning = false)
        {
            report.Issues.Add(new ProfileHealthIssue { Code = code, PluginPath = group?.PluginPath ?? "", Guid = mod?.Guid ?? "", Package = mod?.Package ?? group?.Mods.FirstOrDefault()?.Package ?? "", Message = message, Warning = warning });
        }
        private static PackageId? RepairPackage(ProfileHealthGroup group)
        {
            try
            {
                var packages = group.Mods.Select(m => m.Package).Distinct(StringComparer.Ordinal).ToArray();
                if (!OwnedGroup(group.PluginPath) || group.Mods.Count == 0 || group.Mods.Any(m => m.Guid == Constants.Guid) || packages.Length != 1 || string.IsNullOrWhiteSpace(packages[0])) return null;
                var id = PackageId.Parse(packages[0]);
                return !Downloads.IsBootstrap(id) && group.PluginPath == Owned + id.Key ? id : null;
            }
            catch (InvalidDataException) { return null; }
        }
        public static ProfileRepairPlan PlanRepair(ProfileHealthReport report, IEnumerable<string> selectedIssueIds)
        {
            var selected = new HashSet<string>(selectedIssueIds, StringComparer.Ordinal);
            var issues = report.Issues.Where(i => selected.Contains(i.Id)).ToArray();
            if (issues.Length == 0 || issues.Any(i => !i.CanRepair) || selected.Any(id => report.Issues.All(i => i.Id != id))) throw new InvalidDataException("Select repairable issues from the current health report.");
            var groups = issues.Select(i => report.Groups.Single(g => g.PluginPath == i.PluginPath)).Distinct().ToList();
            return new ProfileRepairPlan { Slot = report.Slot, ActiveSlot = report.ActiveSlot, Groups = Json.CloneLocal(groups), Packages = groups.Select(g => RepairPackage(g)!).ToList() };
        }
#if !MODGATE_PRELOADER
        public static async Task<PendingInstall> StageRepair(string root, ProfileCatalog catalog, ProfileRepairPlan plan, IPackageSource source, Func<string, List<ModEntry>> inspect, Action<string> progress, CancellationToken token)
        {
            ValidateRepairPlan(root, catalog, plan);
            var packages = plan.Groups.Select(g => RepairPackage(g)!).ToArray();
            if (packages.Length == 0 || packages.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Length) throw new InvalidDataException("Invalid repair package selection.");
            foreach (var id in packages) DownloadCache.Invalidate(root, id);
            var pending = await Downloads.Stage(root, packages, source, progress, token).ConfigureAwait(false);
            try
            {
                foreach (var group in plan.Groups)
                {
                    var id = RepairPackage(group)!;
                    var actual = inspect(SafePath.Under(root, "ModGate/staging/" + pending.Id + "/packages/" + id.Key));
                    if (actual.Any(m => m.Guid == Constants.Guid) || !actual.Select(m => m.Guid).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(group.Mods.Select(m => m.Guid).OrderBy(s => s, StringComparer.Ordinal)) || group.Mods.Any(m => actual.All(a => a.Guid != m.Guid || !PolicyEngine.VersionsEqual(a.Version, m.Version))))
                        throw new InvalidDataException("Re-downloaded package does not match the saved plugin identities and versions.");
                    var fresh = Files(root, "ModGate/staging/" + pending.Id + "/packages/" + id.Key, token);
                    if (!fresh.Select(f => f.Path + ":" + f.Sha256).SequenceEqual(group.Files.Select(f => f.Path + ":" + f.Sha256)))
                        throw new InvalidDataException("Re-downloaded files differ from the trusted receipt. Review the release before replacing this package.");
                }
                StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + pending.Id), pending);
                return pending;
            }
            catch { DownloadCache.DiscardStage(root, pending.Id); throw; }
        }
#endif
        private static bool SameBaseline(ProfileHealthGroup first, ProfileHealthGroup second) => first.PluginPath == second.PluginPath &&
            first.Files.Select(f => f.Path + ":" + f.Sha256).SequenceEqual(second.Files.Select(f => f.Path + ":" + f.Sha256)) &&
            SameMods(first.Mods, second.Mods);
        private static bool SameMods(IEnumerable<ModEntry> first, IEnumerable<ModEntry> second) =>
            first.Select(m => m.Guid + ":" + m.Version + ":" + m.Package).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(second.Select(m => m.Guid + ":" + m.Version + ":" + m.Package).OrderBy(s => s, StringComparer.Ordinal));
        private static bool StillExpected(ProfileHealthGroup group, IReadOnlyList<ModEntry> expected) => group.Mods.Count > 0 && group.Mods.All(m => expected.Any(e => e.Guid == m.Guid && e.Version == m.Version && e.Package == m.Package));

        private static void ValidateRepairPlan(string root, ProfileCatalog catalog, ProfileRepairPlan plan)
        {
            var live = Profiles.Load(root);
            var expected = Expected(live, plan.Slot);
            if (catalog.Active != plan.ActiveSlot || live.Active != plan.ActiveSlot || Profiles.Pending(root)) throw new InvalidOperationException("The active profile changed or another change is pending. Verify the profile again.");
            var receipt = Json.ReadLocal<ProfileHealthReceipt>(ReceiptPath(root, plan.Slot));
            foreach (var group in plan.Groups)
            {
                if (receipt.Slot != plan.Slot || !receipt.Groups.Any(g => SameBaseline(g, group)) || RepairPackage(group) == null ||
                    group.Mods.Any(m => expected.All(e => e.Guid != m.Guid || e.Version != m.Version || e.Package != m.Package))) throw new InvalidDataException("The repair baseline changed. Verify the profile again.");
                var names = new HashSet<string>(group.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                if (Files(root, GroupSource(catalog, plan.Slot, group.PluginPath), CancellationToken.None).Any(f => !names.Contains(f.Path)))
                    throw new InvalidDataException("The package contains untracked files. Review them before replacing it.");
            }
        }

#if !MODGATE_PRELOADER
        public static ProfileCommand PrepareRepairCommand(string root, ProfileCatalog catalog, ProfileRepairPlan plan, PendingInstall pending, CancellationToken token = default)
        {
            ValidateRepairPlan(root, catalog, plan);
            var packages = plan.Groups.Select(g => RepairPackage(g) ?? throw new InvalidDataException("This package cannot be repaired automatically.")).ToArray();
            if (!pending.Packages.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(packages.Select(p => p.FullName).OrderBy(p => p, StringComparer.Ordinal))) throw new InvalidDataException("Staged packages do not match the reviewed repair.");
            var packageStage = SafePath.Under(root, "ModGate/staging/" + pending.Id);
            StageVerifier.VerifyStage(packageStage, pending);
            var restore = ProfileSnapshots.StageCurrent(root, plan.Slot, token);
            try
            {
                var target = ProfileSnapshots.StagePath(root, restore);
                foreach (var group in plan.Groups)
                {
                    var id = RepairPackage(group)!;
                    var destination = SafePath.Under(target, "plugins/" + group.PluginPath);
                    if (Directory.Exists(destination))
                    {
                        foreach (var file in ProfileEditing.PayloadFiles(destination)) SafePath.Under(destination, file.Substring(destination.Length + 1));
                        Directory.Delete(destination, true);
                    }
                    var source = SafePath.Under(packageStage, "packages/" + id.Key);
                    foreach (var file in ProfileEditing.PayloadFiles(source))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = file.Substring(source.Length + 1).Replace('\\', '/');
                        var output = SafePath.Under(destination, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output, false);
                    }
                }
                ProfileSnapshots.Seal(root, restore, token);
                DownloadCache.DiscardStage(root, pending.Id);
                return new ProfileCommand { Kind = "restore", Target = plan.Slot, Restore = restore };
            }
            catch { DownloadCache.DiscardStage(root, restore.StageId); throw; }
        }
#endif
    }
}
