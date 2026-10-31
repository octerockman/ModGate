using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ModGate
{
    internal static class Profiles
    {
        private const string CatalogPath = "ModGate/profiles.json", RequestPath = "ModGate/profile-request.json", JournalPath = "ModGate/profile-journal.json";
        public static ProfileCatalog Load(string root)
        {
            var path = SafePath.Under(root, CatalogPath);
            var catalog = File.Exists(path) ? Json.ReadLocal<ProfileCatalog>(path) : new ProfileCatalog();
            Validate(catalog);
            return catalog;
        }
        public static bool Pending(string root) => File.Exists(SafePath.Under(root, RequestPath));
        public static List<ModEntry> WithCurrentConnector(IEnumerable<ModEntry> mods) => mods.Where(m => m.Guid != Constants.Guid).Concat(new[] { new ModEntry { Guid = Constants.Guid, Name = "Valheim ModGate", Version = Constants.Version, Side = "required" } }).ToList();
        public static string ServerKey(string serverId, string host)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(serverId + "|" + host))).Replace("-", "").ToLowerInvariant();
        }
        public static string? SlotFor(ProfileCatalog catalog, string serverKey) => catalog.Servers.FirstOrDefault(s => s.ServerKey == serverKey)?.Slot ?? Enumerable.Range(1, 4).Select(i => "server-" + i).FirstOrDefault(slot => catalog.Servers.All(s => s.Slot != slot));
        internal static void ValidatePreparedCommand(string root, ProfileCommand command) => ValidateCommand(root, command, Load(root));
        public static void Schedule(string root, ProfileCommand command)
        {
            if (Pending(root)) throw new InvalidOperationException("A profile change is already scheduled. Restart Valheim first.");
            ValidateCommand(root, command, Load(root));
            Json.Save(SafePath.Under(root, RequestPath), command);
        }

        public static void Apply(string root, Action<string> log, Action<int>? afterMove = null)
        {
            var journalPath = SafePath.Under(root, JournalPath);
            if (File.Exists(journalPath))
            {
                var previous = Json.ReadLocal<ProfileJournal>(journalPath);
                ValidateJournal(root, previous);
                if (previous.Committed) { Finish(root, previous); log("Completed the saved profile change."); }
                else { Rollback(root, previous); File.Delete(journalPath); log("Recovered an interrupted profile switch."); }
            }
            var requestPath = SafePath.Under(root, RequestPath);
            if (!File.Exists(requestPath)) return;
            var command = Json.ReadLocal<ProfileCommand>(requestPath);
            var catalog = Load(root);
            ValidateCommand(root, command, catalog);
            if (command.Install != null)
                StageVerifier.VerifyStage(SafePath.Under(root, "ModGate/staging/" + command.Install.Id), command.Install);
            try { ProfileSnapshots.BeforeChange(root, command); }
            catch (Exception failure)
            {
                // No transaction or live moves have begun. Retain the reviewed request
                // and staging, but stop treating a failed backup as an active workflow.
                var recovery = SafePath.Under(root, "ModGate/failed-profile-requests");
                Directory.CreateDirectory(recovery);
                var saved = SafePath.Under(recovery, Guid.NewGuid().ToString("N") + ".json");
                File.Move(requestPath, saved);
                throw new IOException("Could not save the profile restore point. The failed request was backed up to " + saved + ". Installed profiles and staging were kept; ModGate controls are unlocked. Review the backup error before preparing this change again.", failure);
            }
            var journal = new ProfileJournal { Id = Guid.NewGuid().ToString("N"), Next = Json.CloneLocal(catalog) };
            Json.Save(journalPath, journal);
            var mover = new Mover(root, journal, afterMove);
            try
            {
                var target = command.Kind == "removeAll" || (command.Kind == "delete" && command.Target == catalog.Active) ? "offline" : command.Kind == "delete" ? catalog.Active : command.Target;
                if (command.Kind == "restore" && !string.IsNullOrEmpty(command.Restore!.SnapshotId))
                    ConfigEditor.StageDiscardPending(root, command.Target, mover.Move, "ModGate/trash/" + journal.Id);
                if (command.Kind == "edit")
                {
                    ProfileEditing.Apply(root, catalog, journal.Next, command, mover.Move);
                    if (command.Install != null) journal.Cleanup.Add("ModGate/staging/" + command.Install.Id);
                }
                else if (catalog.Active != target || command.Kind == "install" || command.Kind == "restore")
                {
                    Park(root, catalog.Active, mover);
                    if (command.Kind == "restore")
                    {
                        ProfileSnapshots.ApplyRestore(root, journal.Next, command.Restore!, mover.Move, "ModGate/trash/" + journal.Id + "/old-profile");
                        journal.Cleanup.Add("ModGate/staging/" + command.Restore!.StageId);
                    }
                    else if (command.Kind == "install")
                    {
                        var existing = catalog.Servers.FirstOrDefault(s => s.Slot == target);
                        var preserveConfig = existing != null && existing.ServerKey == command.Profile!.ServerKey && !command.ReplaceConfig;
                        var storage = "ModGate/profiles/" + target;
                        mover.IfExists(storage + "/plugins", "ModGate/trash/" + journal.Id + "/old-profile/plugins");
                        if (!preserveConfig) BackupConfigs(root, storage + "/config", journal.Id, target);
                        if (!preserveConfig) mover.IfExists(storage + "/config", "ModGate/trash/" + journal.Id + "/old-profile/config");
                        var stage = "ModGate/staging/" + command.Install!.Id;
                        // One owned top-level plugin folder makes repeated switches cheap.
                        mover.Move(stage + "/packages", storage + "/plugins/ValheimModGate.Active");
                        var configStage = SafePath.Under(root, stage + "/config");
                        if (Directory.Exists(configStage))
                            foreach (var file in Directory.GetFiles(configStage, "*", SearchOption.AllDirectories))
                            {
                                var relative = file.Substring(configStage.Length + 1).Replace('\\', '/');
                                var destination = storage + "/config/" + relative;
                                if (!File.Exists(SafePath.Under(root, destination))) mover.Move(stage + "/config/" + relative, destination);
                            }
                        journal.Next.Servers.RemoveAll(s => s.Slot == target);
                        journal.Next.Servers.Add(command.Profile!);
                        journal.Cleanup.Add(stage);
                    }
                    Restore(root, target, mover);
                }
                journal.Next.Active = command.Kind == "edit" ? catalog.Active : target;
                if (command.Kind == "delete")
                {
                    mover.IfExists("ModGate/profiles/" + command.Target, "ModGate/trash/" + journal.Id + "/" + command.Target);
                    journal.Next.Servers.RemoveAll(s => s.Slot == command.Target);
                }
                if (command.Kind == "removeAll")
                {
                    foreach (var profile in journal.Next.Servers)
                        mover.IfExists("ModGate/profiles/" + profile.Slot, "ModGate/trash/" + journal.Id + "/" + profile.Slot);
                    journal.Next.Servers.Clear();
                    // Unconfirmed downloads are also owned by ModGate.
                    mover.IfExists("ModGate/staging", "ModGate/trash/" + journal.Id + "/staging");
                }
                journal.Cleanup.Add("ModGate/trash/" + journal.Id);
                Validate(journal.Next);
                journal.Committed = true;
                Json.Save(journalPath, journal);
            }
            catch
            {
                Rollback(root, journal);
                File.Delete(journalPath);
                throw;
            }
            Finish(root, journal);
            log(command.Kind == "delete" ? "Deleted server profile " + command.Target + ". Active profile: " + journal.Next.Active : command.Kind == "removeAll" ? "Removed downloaded server mods and restored the offline profile." : "Active mod profile: " + journal.Next.Active);
        }

        private static void BackupConfigs(string root, string relative, string batch, string slot)
        {
            var source = SafePath.Under(root, relative); if (!Directory.Exists(source)) return;
            foreach (var file in ProfileEditing.PayloadFiles(source))
            {
                var destination = SafePath.Under(root, "ModGate/config-backups/" + batch + "/" + slot + "/" + file.Substring(source.Length + 1).Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, false);
            }
        }
        private static void Park(string root, string active, Mover mover)
        {
            foreach (var category in new[] { "plugins", "config" })
            {
                var path = SafePath.Under(root, category);
                if (!Directory.Exists(path)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(entry);
                    if (Pinned(category, name) || category == "plugins" && ConnectorPackage(entry)) continue;
                    mover.Move(category + "/" + name, "ModGate/profiles/" + active + "/" + category + "/" + name);
                }
            }
        }
        private static void Restore(string root, string target, Mover mover)
        {
            foreach (var category in new[] { "plugins", "config" })
            {
                var storage = "ModGate/profiles/" + target + "/" + category;
                var path = SafePath.Under(root, storage);
                if (!Directory.Exists(path)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(entry);
                    if (Pinned(category, name) || category == "plugins" && ConnectorPackage(entry)) throw new InvalidDataException("A saved profile attempts to replace ModGate or loader settings.");
                    mover.Move(storage + "/" + name, category + "/" + name);
                }
            }
        }
        private static bool Pinned(string category, string name) => category == "plugins"
            ? new[] { "ValheimModGate", "ValheimModGate.dll", "ValheimModGate.pdb" }.Contains(name, StringComparer.OrdinalIgnoreCase)
            : new[] { "ModGate", "BepInEx.cfg", "studio.valheim.modgate.cfg" }.Contains(name, StringComparer.OrdinalIgnoreCase);

        // Managers put plugins in Author-Package directories. Inspect metadata without
        // loading code so the connector and its package assets remain global there too.
        internal static bool ConnectorPackage(string path)
        {
            foreach (var file in ProfileEditing.PayloadFiles(path).Where(f => Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var filename = Path.GetFileName(file);
                if (filename.Equals("ValheimModGate.dll", StringComparison.OrdinalIgnoreCase) || filename.Equals("ValheimModGate.Preloader.dll", StringComparison.OrdinalIgnoreCase)) return true;
                try
                {
                    var identity = AssemblyName.GetAssemblyName(file).Name;
                    if (string.Equals(identity, "ValheimModGate", StringComparison.OrdinalIgnoreCase) || string.Equals(identity, "ValheimModGate.Preloader", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (BadImageFormatException) { } // Native libraries and non-assemblies are ordinary payloads.
                // An unreadable assembly or unsupported metadata API stops the operation;
                // it must not silently let a possible connector move into profile storage.
            }
            return false;
        }

        private static void Finish(string root, ProfileJournal journal)
        {
            Json.Save(SafePath.Under(root, CatalogPath), journal.Next);
            foreach (var relative in journal.Cleanup)
            {
                if (!(relative.StartsWith("ModGate/trash/" + journal.Id, StringComparison.Ordinal) || relative.StartsWith("ModGate/staging/", StringComparison.Ordinal))) throw new InvalidDataException("Invalid profile cleanup path.");
                var path = SafePath.Under(root, relative);
                if (Directory.Exists(path))
                {
                    // Check every descendant before recursive removal to refuse links.
                    foreach (var child in Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)) SafePath.Under(root, child.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1));
                    Directory.Delete(path, true);
                }
            }
            var request = SafePath.Under(root, RequestPath);
            if (File.Exists(request)) File.Delete(request);
            File.Delete(SafePath.Under(root, JournalPath));
        }
        private static void Rollback(string root, ProfileJournal journal)
        {
            foreach (var operation in journal.Moves.AsEnumerable().Reverse())
            {
                var source = SafePath.Under(root, operation.Source); var destination = SafePath.Under(root, operation.Destination);
                if (!File.Exists(destination) && !Directory.Exists(destination)) continue;
                if (Directory.Exists(source) && Directory.GetFileSystemEntries(source).Length == 0) Directory.Delete(source);
                if (File.Exists(source) || Directory.Exists(source)) throw new IOException("Profile recovery stopped to preserve conflicting files: " + source);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                if (Directory.Exists(destination)) Directory.Move(destination, source); else File.Move(destination, source);
            }
        }
        private static bool ValidSlot(string slot) => slot == "offline" || Enumerable.Range(1, 4).Any(i => slot == "server-" + i);
        private static void Validate(ProfileCatalog catalog)
        {
            if (!ValidSlot(catalog.Active) || catalog.Servers == null || catalog.Servers.Count > 4 || catalog.Servers.Any(s => s == null || s.Slot == "offline" || !ValidSlot(s.Slot) || s.ServerKey == null || s.ServerKey.Length != 64 || s.Name == null || s.Name.Length > 200 || s.Mods == null || s.Packages == null) || catalog.Servers.Select(s => s.Slot).Distinct().Count() != catalog.Servers.Count || catalog.Servers.Select(s => s.ServerKey).Distinct().Count() != catalog.Servers.Count)
                throw new InvalidDataException("Invalid saved profile catalog.");
            if (catalog.Active != "offline" && catalog.Servers.All(s => s.Slot != catalog.Active)) throw new InvalidDataException("Active profile is missing from the catalog.");
        }
        private static void ValidateCommand(string root, ProfileCommand command, ProfileCatalog catalog)
        {
            if (command.Kind == "removeAll") return;
            if (!ValidSlot(command.Target)) throw new InvalidDataException("Invalid profile slot.");
            if (command.Kind == "restore")
            {
                if (command.Restore == null || command.Target != command.Restore.Slot) throw new InvalidDataException("Invalid profile restore request.");
                ProfileSnapshots.ValidateRestore(root, catalog, command.Restore); return;
            }
            if (command.Kind == "edit") { ProfileEditing.Validate(root, catalog, command); return; }
            if (command.Kind == "delete")
            {
                if (command.Target == "offline" || catalog.Servers.All(s => s.Slot != command.Target)) throw new InvalidDataException("Only a saved server profile can be deleted.");
                return;
            }
            if (command.Kind == "switch")
            {
                if (command.Target != "offline" && catalog.Servers.All(s => s.Slot != command.Target)) throw new InvalidDataException("That server profile has not been downloaded.");
                return;
            }
            if (command.Kind != "install" || command.Profile == null || command.Install == null || command.Target == "offline" || command.Profile.Slot != command.Target || !Guid.TryParseExact(command.Install.Id, "N", out _) || command.Profile.ServerKey == null || command.Profile.ServerKey.Length != 64 || command.Profile.Name == null || command.Profile.Name.Length > 200)
                throw new InvalidDataException("Invalid profile installation request.");
            PolicyEngine.ValidateInventory(command.Profile.Mods);
            if (catalog.Servers.Any(s => s.ServerKey == command.Profile.ServerKey && s.Slot != command.Target)) throw new InvalidDataException("This server already has a saved slot. Select its existing slot.");
            foreach (var package in command.Install.Packages) if (DownloadsBootstrap(package)) throw new InvalidDataException("ModGate never installs BepInEx.");
        }
        private static bool DownloadsBootstrap(string package)
        {
            var id = PackageId.Parse(package);
            return id.Name.Equals("BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase) || id.Name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase);
        }
        private static void ValidateJournal(string root, ProfileJournal journal)
        {
            if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.Moves == null || journal.Cleanup == null) throw new InvalidDataException("Invalid profile journal.");
            Validate(journal.Next);
            foreach (var move in journal.Moves)
                foreach (var relative in new[] { move.Source, move.Destination })
                {
                    SafePath.Under(root, relative);
                    if (!new[] { "plugins/", "config/", "ModGate/profiles/", "ModGate/staging/", "ModGate/trash/" + journal.Id + "/" }.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(relative, @"^ModGate/config-edits/pending/[a-f0-9]{64}\.json$")) throw new InvalidDataException("Profile journal path is outside mod storage.");
                }
        }
        private sealed class Mover
        {
            private readonly string root;
            private readonly ProfileJournal journal;
            private readonly Action<int>? afterMove;
            public Mover(string root, ProfileJournal journal, Action<int>? afterMove) { this.root = root; this.journal = journal; this.afterMove = afterMove; }
            public void IfExists(string source, string destination)
            {
                var path = SafePath.Under(root, source);
                if (File.Exists(path) || Directory.Exists(path)) Move(source, destination);
            }
            public void Move(string source, string destination)
            {
                var from = SafePath.Under(root, source); var to = SafePath.Under(root, destination);
                if (source.StartsWith("plugins/", StringComparison.Ordinal) && ConnectorPackage(from)) throw new InvalidDataException("Profile changes cannot move the ModGate connector package.");
                if (Directory.Exists(to) && Directory.GetFileSystemEntries(to).Length == 0) Directory.Delete(to);
                if (File.Exists(to) || Directory.Exists(to)) throw new IOException("Saved profile location is not empty: " + destination);
                if (!File.Exists(from) && !Directory.Exists(from)) throw new IOException("Profile source is missing: " + source);
                journal.Moves.Add(new MoveOperation { Source = source, Destination = destination });
                Json.Save(SafePath.Under(root, JournalPath), journal);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                afterMove?.Invoke(journal.Moves.Count - 1);
            }
        }
    }
}
