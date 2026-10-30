using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ModGate
{
    [DataContract] internal sealed class ProfileEditGroup
    {
        [DataMember] public string PluginPath = "";
        [DataMember] public bool Directory;
        [DataMember] public bool Enabled;
        [DataMember] public bool WasEnabled;
        [DataMember] public List<ModEntry> Mods = new List<ModEntry>();
        [DataMember] public List<FileDigest> Files = new List<FileDigest>();
    }
    [DataContract] internal sealed class ProfileEdit
    {
        [DataMember] public string ActiveSlot = "";
        [DataMember] public List<ProfileEditGroup> Groups = new List<ProfileEditGroup>();
        [DataMember] public List<ProfileEditGroup> Additions = new List<ProfileEditGroup>();
        [DataMember] public List<ConfigBinding> AddedBindings = new List<ConfigBinding>();
    }
    internal static class ProfileEditing
    {
        private static readonly string[] Pinned = { "ValheimModGate", "ValheimModGate.dll", "ValheimModGate.pdb" };
        public static string Plugins(ProfileCatalog catalog, string slot) => catalog.Active == slot ? "plugins" : "ModGate/profiles/" + slot + "/plugins";
        public static List<ProfileEditGroup> Disabled(ProfileCatalog catalog, string slot) => slot == "offline" ? catalog.OfflineDisabledGroups ?? new List<ProfileEditGroup>() : catalog.Servers.Single(s => s.Slot == slot).DisabledGroups ?? new List<ProfileEditGroup>();
        public static string DisabledPath(string slot, string path)
        {
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant()))).Replace("-", "");
            return "ModGate/profiles/" + slot + "/disabled/" + key + "/" + Path.GetFileName(path);
        }
        private static void CheckPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Empty profile mod location.");
            var parts = path.Replace('\\', '/').Split('/');
            SafePath.Under(Path.GetTempPath(), path);
            if (Pinned.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || (parts.Length != 1 && !(parts.Length == 2 && parts[0] == "ValheimModGate.Active")) || path == "ValheimModGate.Active") throw new InvalidDataException("Profile edit cannot move ModGate or an arbitrary plugin location.");
        }
        public static IEnumerable<string> PayloadFiles(string path)
        {
            if (!Directory.Exists(path)) { if (File.Exists(path)) yield return path; else throw new IOException("Profile mod files are missing: " + path); yield break; }
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal))
            {
                SafePath.ReadSource(path, Path.GetFileName(entry));
                if (System.IO.Directory.Exists(entry)) { foreach (var file in PayloadFiles(entry)) yield return file; }
                else yield return entry;
            }
        }
        public static List<ProfileEditGroup> Read(string root, ProfileCatalog catalog, string slot, Func<string, List<ModEntry>> inspect)
        {
            if (slot != "offline" && !catalog.Servers.Any(s => s.Slot == slot)) throw new InvalidDataException("Unknown server profile.");
            var result = new List<ProfileEditGroup>(); var basePath = SafePath.Under(root, Plugins(catalog, slot));
            if (System.IO.Directory.Exists(basePath))
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(basePath).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(entry);
                    if (Pinned.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    SafePath.ReadSource(basePath, name);
                    if (Profiles.ConnectorPackage(entry)) continue;
                    var candidates = name == "ValheimModGate.Active" && System.IO.Directory.Exists(entry) ? System.IO.Directory.GetDirectories(entry) : new[] { entry };
                    foreach (var candidate in candidates)
                    {
                        var relative = candidate.Substring(basePath.Length + 1).Replace('\\', '/'); CheckPath(relative); SafePath.ReadSource(basePath, relative);
                        var mods = inspect(candidate);
                        if (mods.Any(m => m.Guid == Constants.Guid)) continue;
                        if (mods.Count == 0) continue;
                        result.Add(new ProfileEditGroup { PluginPath = relative, Directory = System.IO.Directory.Exists(candidate), Enabled = true, WasEnabled = true, Mods = mods });
                    }
                }
            foreach (var old in Disabled(catalog, slot))
            {
                CheckPath(old.PluginPath); var source = SafePath.ReadSource(root, DisabledPath(slot, old.PluginPath));
                var mods = inspect(source); if (mods.Any(m => m.Guid == Constants.Guid)) throw new InvalidDataException("ModGate cannot be disabled in a profile.");
                result.Add(new ProfileEditGroup { PluginPath = old.PluginPath, Directory = System.IO.Directory.Exists(source), Mods = mods, Enabled = false, WasEnabled = false });
            }
            PolicyEngine.ValidateInventory(result.SelectMany(g => g.Mods).ToList());
            return result;
        }
        public static bool CanAdd(ProfileCatalog catalog, string slot, ModEntry mod)
        {
            if (mod.Guid == Constants.Guid) return false;
            if (slot == "offline") return true;
            var policy = catalog.Servers.Single(s => s.Slot == slot).ServerPolicy;
            return policy == null ? !AutoSetup.ServerOnlyName(mod.Name) : !policy.ForbiddenPluginGuids.Contains(mod.Guid) && (policy.Mods.Any(m => m.Guid == mod.Guid) || policy.AllowExtras && !AutoSetup.ServerOnlyName(mod.Name));
        }
        private static string Source(ProfileCatalog catalog, string slot, ProfileEditGroup group) => group.WasEnabled ? Plugins(catalog, slot) + "/" + group.PluginPath : DisabledPath(slot, group.PluginPath);
        private static List<FileDigest> Snapshot(string root, string relative, CancellationToken token)
        {
            var source = SafePath.ReadSource(root, relative); var directory = System.IO.Directory.Exists(source);
            return PayloadFiles(source).Select(file =>
            {
                token.ThrowIfCancellationRequested(); SafePath.ReadSource(root, file.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1));
                return new FileDigest { Path = directory ? file.Substring(source.Length + 1).Replace('\\', '/') : Path.GetFileName(file), Sha256 = SafePath.Hash(file) };
            }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }
        public static ProfileCommand Prepare(string root, ProfileCatalog catalog, string slot, IReadOnlyList<ProfileEditGroup> groups, PendingInstall? additions, List<ProfileEditGroup> added, List<ConfigBinding> bindings, CancellationToken token)
        {
            var edit = new ProfileEdit { ActiveSlot = catalog.Active, Groups = Json.CloneLocal(groups.ToList()), Additions = Json.CloneLocal(added), AddedBindings = bindings };
            foreach (var group in edit.Groups.Concat(edit.Additions)) CheckPath(group.PluginPath);
            foreach (var group in edit.Groups.Where(g => g.Enabled != g.WasEnabled)) group.Files = Snapshot(root, Source(catalog, slot, group), token);
            var command = new ProfileCommand { Kind = "edit", Target = slot, Edit = edit, Install = additions };
            Validate(root, catalog, command); return command;
        }
        public static void Validate(string root, ProfileCatalog catalog, ProfileCommand command)
        {
            var edit = command.Edit ?? throw new InvalidDataException("Missing profile edit.");
            if (command.Target != "offline" && !catalog.Servers.Any(s => s.Slot == command.Target)) throw new InvalidDataException("Unknown profile for editing.");
            if (edit.ActiveSlot != catalog.Active) throw new InvalidDataException("The active profile changed. Open the editor again.");
            if (edit.Groups == null || edit.Additions == null || edit.AddedBindings == null) throw new InvalidDataException("Invalid profile edit.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in edit.Groups.Concat(edit.Additions))
            {
                CheckPath(group.PluginPath); if (!paths.Add(group.PluginPath)) throw new InvalidDataException("Two profile mod groups use the same location.");
                PolicyEngine.ValidateInventory(group.Mods);
                if (group.Mods.Count == 0 || group.Mods.Any(m => m.Guid == Constants.Guid)) throw new InvalidDataException("ModGate cannot be edited or disabled.");
            }
            PolicyEngine.ValidateInventory(edit.Groups.Where(g => g.Enabled).Concat(edit.Additions).SelectMany(g => g.Mods).ToList());
            var disabled = Disabled(catalog, command.Target);
            foreach (var group in edit.Groups)
            {
                if (disabled.Any(g => g.PluginPath.Equals(group.PluginPath, StringComparison.OrdinalIgnoreCase)) == group.WasEnabled) throw new InvalidDataException("The profile's disabled mod list changed. Open the editor again.");
                if (group.Enabled == group.WasEnabled) continue;
                var source = SafePath.Under(root, Source(catalog, command.Target, group));
                if (Profiles.ConnectorPackage(source)) throw new InvalidDataException("Profile edits cannot move the ModGate connector package.");
                if (System.IO.Directory.Exists(source) != group.Directory) throw new InvalidDataException("Profile mod location changed type.");
                var actual = Snapshot(root, Source(catalog, command.Target, group), CancellationToken.None);
                if (!actual.Select(f => f.Path + ":" + f.Sha256).SequenceEqual(group.Files.Select(f => f.Path + ":" + f.Sha256))) throw new InvalidDataException("Profile mod files changed after review: " + string.Join(", ", group.Mods.Select(m => m.Name)));
                var destination = group.Enabled ? Plugins(catalog, command.Target) + "/" + group.PluginPath : DisabledPath(command.Target, group.PluginPath);
                var to = SafePath.Under(root, destination);
                if (File.Exists(to) || System.IO.Directory.Exists(to) && System.IO.Directory.GetFileSystemEntries(to).Length > 0) throw new InvalidDataException("The edited mod destination already contains files.");
            }
            if (edit.Additions.Count > 0 && command.Install == null) throw new InvalidDataException("Missing staged profile additions.");
            if (command.Install != null)
            {
                var stage = SafePath.Under(root, "ModGate/staging/" + command.Install.Id); StageVerifier.VerifyStage(stage, command.Install);
                if (command.Install.Packages.Any(p => Downloads.IsBootstrap(PackageId.Parse(p)))) throw new InvalidDataException("Profile edits never install BepInEx.");
                if (edit.Additions.Any(g => !g.PluginPath.StartsWith("ValheimModGate.Active/", StringComparison.Ordinal))) throw new InvalidDataException("Invalid added package location.");
                var keys = command.Install.Packages.Select(p => PackageId.Parse(p).Key).Concat(command.Install.LocalPackages).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
                if (!keys.SequenceEqual(edit.Additions.Select(g => g.PluginPath.Substring("ValheimModGate.Active/".Length)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Profile additions do not match their staged receipt.");
                foreach (var group in edit.Additions)
                {
                    if (Profiles.ConnectorPackage(SafePath.Under(stage, "packages/" + group.PluginPath.Substring("ValheimModGate.Active/".Length)))) throw new InvalidDataException("Profile additions cannot install the ModGate connector package.");
                    if (!group.Enabled || group.WasEnabled || !group.Directory || !group.PluginPath.StartsWith("ValheimModGate.Active/", StringComparison.Ordinal) || group.Mods.Any(m => !CanAdd(catalog, command.Target, m))) throw new InvalidDataException("This profile cannot include the selected added mods.");
                    var destination = SafePath.Under(root, Plugins(catalog, command.Target) + "/" + group.PluginPath);
                    if (File.Exists(destination) || System.IO.Directory.Exists(destination)) throw new InvalidDataException("Added mods share an existing profile package location. Keep that package together or rebuild the profile.");
                }
                foreach (var file in command.Install.Files.Where(f => f.Path.StartsWith("config/", StringComparison.Ordinal)))
                {
                    var name = file.Path.Substring(7);
                    if (name.Equals("BepInEx.cfg", StringComparison.OrdinalIgnoreCase) || name.Equals("studio.valheim.modgate.cfg", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ModGate/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Profile edit cannot replace global configuration.");
                }
            }
        }
        public static void Apply(string root, ProfileCatalog catalog, ProfileCatalog next, ProfileCommand command, Action<string, string> move)
        {
            var edit = command.Edit!; var target = command.Target;
            foreach (var group in edit.Groups.Where(g => g.Enabled != g.WasEnabled))
                move(Source(catalog, target, group), group.Enabled ? Plugins(catalog, target) + "/" + group.PluginPath : DisabledPath(target, group.PluginPath));
            if (command.Install != null)
            {
                var stage = "ModGate/staging/" + command.Install.Id;
                foreach (var group in edit.Additions) move(stage + "/packages/" + group.PluginPath.Substring("ValheimModGate.Active/".Length), Plugins(catalog, target) + "/" + group.PluginPath);
                foreach (var file in command.Install.Files.Where(f => f.Path.StartsWith("config/", StringComparison.Ordinal)))
                {
                    var destination = (target == catalog.Active ? "config" : "ModGate/profiles/" + target + "/config") + "/" + file.Path.Substring(7);
                    if (!File.Exists(SafePath.Under(root, destination))) move(stage + "/" + file.Path, destination);
                }
            }
            var mods = Profiles.WithCurrentConnector(edit.Groups.Where(g => g.Enabled).Concat(edit.Additions).SelectMany(g => g.Mods));
            var hidden = edit.Groups.Where(g => !g.Enabled).Select(g => { var copy = Json.CloneLocal(g); copy.Files.Clear(); copy.WasEnabled = false; return copy; }).ToList();
            if (target == "offline") { next.OfflineMods = mods; next.OfflineDisabledGroups = hidden; next.OfflineBindings = Merge(next.OfflineBindings, edit.AddedBindings); }
            else
            {
                var profile = next.Servers.Single(s => s.Slot == target); profile.Mods = mods; profile.DisabledGroups = hidden; profile.ConfigBindings = Merge(profile.ConfigBindings, edit.AddedBindings);
                if (profile.ServerPolicy != null) { var policy = Json.CloneLocal(profile.ServerPolicy); policy.Protocol = Constants.Protocol; policy.Mods = Profiles.WithCurrentConnector(policy.Mods); profile.Incomplete = !PolicyEngine.Compare(policy, mods).Matches; }
                else profile.Incomplete = true;
            }
        }
        private static List<ConfigBinding> Merge(List<ConfigBinding>? original, List<ConfigBinding> additions) => (original ?? new List<ConfigBinding>()).Where(b => additions.All(a => a.Guid != b.Guid)).Concat(additions).ToList();
    }
}
