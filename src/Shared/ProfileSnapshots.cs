using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModGate
{
    [DataContract] internal sealed class ProfileSnapshot
    {
        [DataMember] public int Schema = 1;
        [DataMember] public string Id = "", Slot = "", CreatedUtc = "", Reason = "";
        [DataMember] public bool Working;
        [DataMember] public SavedProfile Profile = new SavedProfile();
        [DataMember] public List<FileDigest> Files = new List<FileDigest>();
        [DataMember] public List<string> Directories = new List<string>();
        [DataMember] public long Bytes;
        [DataMember] public string StateHash = "";
    }
    [DataContract] internal sealed class ProfileRestorePlan
    {
        [DataMember] public int Schema = 1;
        [DataMember] public string Slot = "", StageId = "", SnapshotId = "", ExpectedActive = "", ExpectedState = "";
        [DataMember] public SavedProfile Profile = new SavedProfile();
        [DataMember] public List<FileDigest> Files = new List<FileDigest>();
        [DataMember] public List<string> Directories = new List<string>();
    }
    internal static class ProfileSnapshots
    {
        private static readonly string[] Categories = { "plugins", "config", "disabled" };
        private static readonly string[] ProtectedFiles = { "ValheimModGate.dll", "ValheimModGate.pdb", "ValheimModGate.Preloader.dll", "ValheimModGate.Preloader.pdb", "BepInEx.dll", "BepInEx.Preloader.dll", "0Harmony.dll", "Mono.Cecil.dll", "winhttp.dll", "doorstop_config.ini", "BepInEx.cfg", "studio.valheim.modgate.cfg" };

        public static ProfileSnapshot Capture(string root, string slot, string reason, bool working = false, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(SafePath.Under(root, "ModGate/profile-journal.json"))) throw new InvalidOperationException("Finish profile recovery before saving a restore point.");
            var catalog = Profiles.Load(root); var profile = CurrentProfile(catalog, slot); var expected = State(root, catalog, slot, token);
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200) throw new InvalidDataException("Invalid restore-point description.");
            foreach (var previous in List(root, slot).Where(s => s.Reason == reason && s.Working == working && s.StateHash == expected).Take(1))
            {
                try { return Verify(root, slot, previous.Id, token); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is SerializationException) { }
            }
            var id = Guid.NewGuid().ToString("N"); var relative = History(slot) + "/" + id;
            var folder = SafePath.Under(root, relative);
            Directory.CreateDirectory(folder);
            try
            {
                var destination = SafePath.Under(folder, "payload"); Directory.CreateDirectory(destination);
                CopyCurrent(root, catalog, slot, destination, token);
                if (State(root, Profiles.Load(root), slot, token) != expected) throw new InvalidDataException("The profile changed while its restore point was being saved. Try again.");
                var receipt = Inspect(destination, token);
                if (Digest(profile, receipt.Files, receipt.Directories) != expected) throw new InvalidDataException("Profile files changed while the restore point was being copied.");
                var snapshot = new ProfileSnapshot { Id = id, Slot = slot, CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Reason = reason, Working = working, Profile = profile, Files = receipt.Files, Directories = receipt.Directories, Bytes = receipt.Bytes, StateHash = expected };
                VerifyPayload(destination, snapshot.Files, snapshot.Directories, token);
                Json.Save(SafePath.Under(folder, "snapshot.json"), snapshot);
                return snapshot;
            }
            catch { RemoveOwned(root, relative); throw; }
        }
        public static List<ProfileSnapshot> List(string root, string slot)
        {
            var history = SafePath.Under(root, History(slot)); var result = new List<ProfileSnapshot>();
            if (!Directory.Exists(history)) return result;
            foreach (var directory in Directory.GetDirectories(history))
            {
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try { result.Add(Read(root, slot, id)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is SerializationException || error is ArgumentException) { }
            }
            return result.OrderByDescending(s => s.CreatedUtc, StringComparer.Ordinal).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
        }
        public static ProfileSnapshot Verify(string root, string slot, string snapshotId, CancellationToken token = default)
        {
            var snapshot = Read(root, slot, snapshotId);
            VerifyPayload(SafePath.Under(root, History(slot) + "/" + snapshotId + "/payload"), snapshot.Files, snapshot.Directories, token);
            return snapshot;
        }
        public static void BeforeChange(string root, ProfileCommand command)
        {
            if (command.Kind != "install" && command.Kind != "edit" && command.Kind != "restore") return;
            var catalog = Profiles.Load(root);
            if (command.Target != "offline" && catalog.Servers.All(p => p.Slot != command.Target)) return;
            Capture(root, command.Target, command.Kind == "restore" ? "Before restoring profile" : command.Kind == "edit" ? "Before profile edits" : "Before profile update");
        }
        public static ProfileRestorePlan PrepareRestore(string root, string slot, string snapshotId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            EnsureAvailable(root);
            var catalog = Profiles.Load(root); var state = State(root, catalog, slot, token);
            var snapshot = Verify(root, slot, snapshotId, token);
            var plan = NewPlan(catalog, slot, state); plan.SnapshotId = snapshotId; plan.Profile = Json.CloneLocal(snapshot.Profile);
            var relative = "ModGate/staging/" + plan.StageId;
            try
            {
                var source = SafePath.Under(root, History(slot) + "/" + snapshotId + "/payload");
                CopyPayload(source, StagePath(root, plan), token);
                Seal(root, plan, token);
                if (Digest(plan.Profile, plan.Files, plan.Directories) != snapshot.StateHash) throw new InvalidDataException("The restore point changed while its files were being prepared.");
                if (State(root, Profiles.Load(root), slot, token) != state) throw new InvalidDataException("The profile changed while its restore was being prepared. Review it again.");
                return plan;
            }
            catch { RemoveOwned(root, relative); throw; }
        }
        // Repair uses the same complete replacement transaction, changing only its selected staged groups.
        public static ProfileRestorePlan StageCurrent(string root, string slot, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            EnsureAvailable(root);
            var catalog = Profiles.Load(root); var state = State(root, catalog, slot, token); var plan = NewPlan(catalog, slot, state);
            try
            {
                CopyCurrent(root, catalog, slot, StagePath(root, plan), token); Seal(root, plan, token);
                if (Digest(plan.Profile, plan.Files, plan.Directories) != state) throw new InvalidDataException("Profile files changed while the repair copy was being prepared.");
                if (State(root, Profiles.Load(root), slot, token) != state) throw new InvalidDataException("The profile changed while its repair was being prepared. Review it again.");
                return plan;
            }
            catch { RemoveOwned(root, "ModGate/staging/" + plan.StageId); throw; }
        }
        public static string StagePath(string root, ProfileRestorePlan plan)
        {
            if (plan == null || !Guid.TryParseExact(plan.StageId, "N", out _)) throw new InvalidDataException("Invalid profile restore stage.");
            return SafePath.Under(root, "ModGate/staging/" + plan.StageId + "/restore");
        }
        public static void Seal(string root, ProfileRestorePlan plan, CancellationToken token = default)
        {
            ValidateProfile(plan.Profile, plan.Slot); plan.Profile.CredentialKey = "";
            var receipt = Inspect(StagePath(root, plan), token); plan.Files = receipt.Files; plan.Directories = receipt.Directories;
        }
        public static void ValidateRestore(string root, ProfileCatalog catalog, ProfileRestorePlan plan)
        {
            if (plan == null || plan.Schema != 1 || !ValidSlot(plan.Slot) || !ValidSlot(plan.ExpectedActive) || plan.ExpectedActive != catalog.Active ||
                !Regex.IsMatch(plan.ExpectedState ?? "", @"^[a-f0-9]{64}$") || plan.SnapshotId == null || plan.SnapshotId.Length > 0 && !Guid.TryParseExact(plan.SnapshotId, "N", out _))
                throw new InvalidDataException("Invalid profile restore request.");
            ValidateProfile(plan.Profile, plan.Slot);
            if (plan.Slot != "offline" && catalog.Servers.Any(p => p.Slot != plan.Slot && p.ServerKey == plan.Profile.ServerKey)) throw new InvalidDataException("The restored server already has another saved slot.");
            if (State(root, catalog, plan.Slot, CancellationToken.None) != plan.ExpectedState) throw new InvalidDataException("Profile files or metadata changed after review. Prepare the restore again.");
            VerifyPayload(StagePath(root, plan), plan.Files, plan.Directories, CancellationToken.None);
        }
        // Profiles.Apply parks the active setup before this call and activates the target afterward.
        // Every replacement goes through its existing journaled mover so rollback stays atomic.
        public static void ApplyRestore(string root, ProfileCatalog next, ProfileRestorePlan plan, Action<string, string> move, string trash)
        {
            ValidateProfile(plan.Profile, plan.Slot);
            if (string.IsNullOrEmpty(trash) || !Regex.IsMatch(trash, @"^ModGate/trash/[a-fA-F0-9]{32}/[A-Za-z0-9_-]+$")) throw new InvalidDataException("Invalid profile restore rollback path.");
            SafePath.Under(root, trash); VerifyPayload(StagePath(root, plan), plan.Files, plan.Directories, CancellationToken.None);
            var storage = "ModGate/profiles/" + plan.Slot;
            foreach (var category in Categories)
            {
                var current = storage + "/" + category;
                if (Directory.Exists(SafePath.Under(root, current))) move(current, trash + "/" + category);
                var source = "ModGate/staging/" + plan.StageId + "/restore/" + category;
                if (Directory.Exists(SafePath.Under(root, source))) move(source, current);
            }
            var profile = Json.CloneLocal(plan.Profile);
            if (plan.Slot == "offline") { next.OfflineMods = profile.Mods; next.OfflineBindings = profile.ConfigBindings; next.OfflineDisabledGroups = profile.DisabledGroups; }
            else
            {
                var previous = next.Servers.SingleOrDefault(p => p.Slot == plan.Slot);
                profile.CredentialKey = previous?.ServerKey == profile.ServerKey ? previous.CredentialKey : "";
                next.Servers.RemoveAll(p => p.Slot == plan.Slot); next.Servers.Add(profile);
            }
        }

        private static ProfileRestorePlan NewPlan(ProfileCatalog catalog, string slot, string state) => new ProfileRestorePlan { Slot = slot, StageId = Guid.NewGuid().ToString("N"), ExpectedActive = catalog.Active, ExpectedState = state, Profile = CurrentProfile(catalog, slot) };
        private static bool ValidSlot(string slot) => slot == "offline" || Enumerable.Range(1, 4).Any(i => slot == "server-" + i);
        private static string History(string slot) => ValidSlot(slot) ? "ModGate/profile-history/" + slot : throw new InvalidDataException("Invalid snapshot profile slot.");
        private static void EnsureAvailable(string root)
        {
            if (Profiles.Pending(root) || File.Exists(SafePath.Under(root, "ModGate/profile-journal.json"))) throw new InvalidOperationException("Apply the pending profile change before preparing another restore or repair.");
        }
        private static SavedProfile CurrentProfile(ProfileCatalog catalog, string slot)
        {
            if (!ValidSlot(slot)) throw new InvalidDataException("Invalid profile slot.");
            var profile = slot == "offline" ? new SavedProfile { Slot = slot, Name = "Offline", Mods = catalog.OfflineMods ?? new List<ModEntry>(), ConfigBindings = catalog.OfflineBindings ?? new List<ConfigBinding>(), DisabledGroups = catalog.OfflineDisabledGroups ?? new List<ProfileEditGroup>() }
                : catalog.Servers.SingleOrDefault(p => p.Slot == slot) ?? throw new InvalidDataException("That server profile no longer exists.");
            profile = Json.CloneLocal(profile); profile.CredentialKey = "";
            profile.ConfigBindings = profile.ConfigBindings ?? new List<ConfigBinding>(); profile.DisabledGroups = profile.DisabledGroups ?? new List<ProfileEditGroup>();
            ValidateProfile(profile, slot); return profile;
        }
        private static void ValidateProfile(SavedProfile profile, string slot)
        {
            if (!ValidSlot(slot) || profile == null || profile.Slot != slot || profile.Name == null || profile.Name.Length > 200 || profile.Mods == null || profile.Packages == null || profile.ConfigBindings == null || profile.DisabledGroups == null ||
                slot != "offline" && !Regex.IsMatch(profile.ServerKey ?? "", @"^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid snapshot profile metadata.");
            PolicyEngine.ValidateInventory(profile.Mods);
            foreach (var package in profile.Packages) { var id = PackageId.Parse(package); if (DownloadsBootstrap(id)) throw new InvalidDataException("Restore points cannot install loader packages."); }
            foreach (var binding in profile.ConfigBindings)
            {
                if (binding == null || binding.Files == null) throw new InvalidDataException("Invalid snapshot config metadata.");
                foreach (var file in binding.Files) ValidatePayloadPath("config/" + file);
            }
            foreach (var group in profile.DisabledGroups)
            {
                if (group == null || group.Mods == null || group.Mods.Any(m => m.Guid == Constants.Guid)) throw new InvalidDataException("Invalid snapshot disabled-mod metadata.");
                ValidatePayloadPath("plugins/" + group.PluginPath); PolicyEngine.ValidateInventory(group.Mods);
            }
        }
        private static bool DownloadsBootstrap(PackageId id) => id.Name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase) || id.Name.Equals("BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase);
        private static ProfileSnapshot Read(string root, string slot, string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid restore-point identifier.");
            var snapshot = Json.ReadLocal<ProfileSnapshot>(SafePath.Under(root, History(slot) + "/" + id + "/snapshot.json"));
            if (snapshot == null || snapshot.Schema != 1 || snapshot.Id != id || snapshot.Slot != slot || snapshot.Reason == null || snapshot.Reason.Length > 200 || snapshot.Bytes < 0 ||
                !DateTime.TryParse(snapshot.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) throw new InvalidDataException("Invalid restore-point receipt.");
            ValidateProfile(snapshot.Profile, slot); ValidateReceipts(snapshot.Files, snapshot.Directories);
            var state = Digest(snapshot.Profile, snapshot.Files, snapshot.Directories);
            if (!string.IsNullOrEmpty(snapshot.StateHash) && snapshot.StateHash != state) throw new InvalidDataException("Restore-point metadata failed verification.");
            snapshot.StateHash = state; return snapshot;
        }
        private static bool Excluded(string relative)
        {
            var parts = relative.Replace('\\', '/').Split('/');
            if (parts.Any(p => p.Equals("private-passwords", StringComparison.OrdinalIgnoreCase) || p.Equals(".aws", StringComparison.OrdinalIgnoreCase) || p.Equals(".ssh", StringComparison.OrdinalIgnoreCase))) return true;
            if (parts.Length < 2) return false;
            return ProtectedFiles.Contains(parts.Last(), StringComparer.OrdinalIgnoreCase) ||
                parts[0] == "config" && parts[1].Equals("ModGate", StringComparison.OrdinalIgnoreCase) ||
                parts[0] == "plugins" && parts[1].Equals("ValheimModGate", StringComparison.OrdinalIgnoreCase);
        }
        private static void ValidatePayloadPath(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Contains("\\") || !Categories.Contains(relative.Split('/')[0]) || Excluded(relative)) throw new InvalidDataException("Restore-point file is outside owned profile storage.");
            SafePath.Under(Path.GetTempPath(), relative);
        }
        private static void ValidateReceipts(List<FileDigest> files, List<string> directories)
        {
            if (files == null || directories == null) throw new InvalidDataException("Missing restore-point file receipts.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories) { ValidatePayloadPath(directory); if (!seen.Add(directory)) throw new InvalidDataException("Duplicate restore-point directory."); }
            foreach (var file in files)
            {
                if (file == null || !Regex.IsMatch(file.Sha256 ?? "", @"^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid restore-point hash.");
                ValidatePayloadPath(file.Path); if (!file.Path.Contains("/") || !seen.Add(file.Path)) throw new InvalidDataException("Duplicate or invalid restore-point file.");
            }
        }
        private static void CopyCurrent(string root, ProfileCatalog catalog, string slot, string destination, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            foreach (var category in Categories)
            {
                var relative = category == "disabled" || catalog.Active != slot ? "ModGate/profiles/" + slot + "/" + category : category;
                var source = SafePath.Under(root, relative); var output = SafePath.Under(destination, category); Directory.CreateDirectory(output);
                if (Directory.Exists(source)) CopyTree(source, destination, category, token, true);
            }
        }
        private static void CopyPayload(string source, string destination, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            foreach (var category in Categories)
            {
                var from = SafePath.Under(source, category); Directory.CreateDirectory(SafePath.Under(destination, category));
                if (Directory.Exists(from)) CopyTree(from, destination, category, token, false);
            }
        }
        private static void CopyTree(string source, string destination, string relative, CancellationToken token, bool filter)
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.GetFileSystemEntries(source).OrderBy(p => p, StringComparer.Ordinal))
            {
                var child = relative + "/" + Path.GetFileName(entry);
                if (filter && (Excluded(child) || relative == "plugins" && Profiles.ConnectorPackage(entry))) continue;
                ValidatePayloadPath(child); SafePath.Under(source, Path.GetFileName(entry));
                var target = SafePath.Under(destination, child);
                if (Directory.Exists(entry)) { Directory.CreateDirectory(target); CopyTree(entry, destination, child, token, filter); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = File.OpenRead(entry)) using (var output = new FileStream(target, FileMode.CreateNew))
                    {
                        var buffer = new byte[65536]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                    }
                    if (SafePath.Hash(entry) != SafePath.Hash(target)) throw new InvalidDataException("Profile file changed while copying: " + child);
                }
            }
        }
        private sealed class Receipt
        {
            public List<FileDigest> Files = new List<FileDigest>(); public List<string> Directories = new List<string>(); public long Bytes;
        }
        private static Receipt Inspect(string folder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new Receipt();
            if (!Directory.Exists(folder)) throw new InvalidDataException("Restore-point payload is missing.");
            void Visit(string directory, string prefix)
            {
                foreach (var entry in Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); var relative = prefix + Path.GetFileName(entry); ValidatePayloadPath(relative); SafePath.Under(folder, relative);
                    if (prefix == "plugins/" && Profiles.ConnectorPackage(entry)) throw new InvalidDataException("Restore points cannot contain the ModGate connector package.");
                    if (Directory.Exists(entry)) { result.Directories.Add(relative); Visit(entry, relative + "/"); }
                    else { result.Files.Add(new FileDigest { Path = relative, Sha256 = SafePath.Hash(entry) }); result.Bytes = checked(result.Bytes + new FileInfo(entry).Length); }
                }
            }
            Visit(folder, "");
            result.Files = result.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(); result.Directories.Sort(StringComparer.Ordinal);
            ValidateReceipts(result.Files, result.Directories); return result;
        }
        private static void VerifyPayload(string folder, List<FileDigest> files, List<string> directories, CancellationToken token)
        {
            ValidateReceipts(files, directories); var actual = Inspect(folder, token);
            if (!actual.Directories.SequenceEqual(directories.OrderBy(p => p, StringComparer.Ordinal)) ||
                !actual.Files.Select(f => f.Path + ":" + f.Sha256.ToLowerInvariant()).SequenceEqual(files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + ":" + f.Sha256.ToLowerInvariant())))
                throw new InvalidDataException("Restore-point files failed verification. No profile files were changed.");
        }
        [DataContract] private sealed class StateReceipt
        {
            [DataMember] public SavedProfile Profile = new SavedProfile();
            [DataMember] public List<FileDigest> Files = new List<FileDigest>();
            [DataMember] public List<string> Directories = new List<string>();
        }
        private static string State(string root, ProfileCatalog catalog, string slot, CancellationToken token)
        {
            var profile = CurrentProfile(catalog, slot); var receipt = new Receipt();
            foreach (var category in Categories)
            {
                receipt.Directories.Add(category);
                var relative = category == "disabled" || catalog.Active != slot ? "ModGate/profiles/" + slot + "/" + category : category;
                var source = SafePath.Under(root, relative);
                void Visit(string directory, string prefix)
                {
                    foreach (var entry in Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested(); var child = prefix + "/" + Path.GetFileName(entry); if (Excluded(child) || prefix == "plugins" && Profiles.ConnectorPackage(entry)) continue;
                        ValidatePayloadPath(child); SafePath.Under(root, relative + entry.Substring(source.Length).Replace('\\', '/'));
                        if (Directory.Exists(entry)) { receipt.Directories.Add(child); Visit(entry, child); }
                        else receipt.Files.Add(new FileDigest { Path = child, Sha256 = SafePath.Hash(entry) });
                    }
                }
                if (Directory.Exists(source)) Visit(source, category);
            }
            return Digest(profile, receipt.Files, receipt.Directories);
        }
        private static string Digest(SavedProfile profile, List<FileDigest> files, List<string> directories)
        {
            using (var sha = SHA256.Create()) using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                new DataContractJsonSerializer(typeof(StateReceipt), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue }).WriteObject(stream,
                    new StateReceipt { Profile = profile, Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), Directories = directories.OrderBy(p => p, StringComparer.Ordinal).ToList() });
                stream.FlushFinalBlock(); return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
            }
        }
        private static void RemoveOwned(string root, string relative)
        {
            var path = SafePath.Under(root, relative); if (!Directory.Exists(path)) return;
            void Check(string directory)
            {
                foreach (var child in Directory.GetFileSystemEntries(directory))
                {
                    SafePath.Under(root, relative + child.Substring(path.Length).Replace('\\', '/'));
                    if (Directory.Exists(child)) Check(child);
                }
            }
            Check(path); Directory.Delete(path, true);
        }
    }
}
