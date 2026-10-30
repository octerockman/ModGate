using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModGate
{
    internal static class PolicyEngine
    {
        public static Policy Publish(ServerManifest manifest, IReadOnlyList<ModEntry> loaded)
        {
            manifest = ConfigFields.Runtime(manifest);
            if (manifest.Schema != Constants.ManifestSchema || manifest.Mods == null || manifest.Mods.Count > Constants.MaxMods)
                throw new InvalidDataException("Unsupported server manifest schema or mod count.");
            ValidateInventory(manifest.Mods);
            DependencyRules.Link(manifest.Mods);
            var declared = manifest.Mods.ToDictionary(m => m.Guid, StringComparer.Ordinal);
            foreach (var item in loaded.Where(m => m.Guid != Constants.Guid))
            {
                if (!declared.ContainsKey(item.Guid)) throw new InvalidDataException("Classify the newly loaded mod: " + item.Name);
            }
            var policy = new Policy { ServerName = manifest.ServerName, AllowExtras = manifest.AllowExtraClientMods, AllowNewerClientVersions = manifest.AllowNewerClientVersions };
            foreach (var configured in manifest.Mods)
            {
                var mod = Json.Read<ModEntry>(Json.Write(configured));
                if (!new[] { "required", "optional", "serverOnly", "clientOnly" }.Contains(mod.Side))
                    throw new InvalidDataException("Classify " + mod.Name + " as required, optional, serverOnly, or clientOnly.");
                if (mod.Guid == Constants.Guid) continue;
                if (mod.Side == "serverOnly")
                {
                    policy.ForbiddenPluginGuids.Add(mod.Guid);
                    var serverPackage = !string.IsNullOrWhiteSpace(mod.Package) ? PackageId.Parse(mod.Package) : !string.IsNullOrWhiteSpace(mod.DownloadUrl) ? PackageId.FromDownloadUrl(mod.DownloadUrl) : null;
                    if (serverPackage != null) policy.ForbiddenPackages.Add(serverPackage.Key);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(mod.NexusUrl))
                {
                    NexusLinks.Validate(mod.NexusUrl);
                    if (!string.IsNullOrWhiteSpace(mod.DownloadUrl)) throw new InvalidDataException("Choose downloadUrl or nexusUrl for " + mod.Name + ", not both.");
                    mod.Package = "";
                }
                if (!string.IsNullOrWhiteSpace(mod.DownloadUrl))
                {
                    var url = PackageId.FromDownloadUrl(mod.DownloadUrl);
                    if (!string.IsNullOrWhiteSpace(mod.Package))
                    {
                        var detected = PackageId.Parse(mod.Package);
                        if (!detected.Key.Equals(url.Key, StringComparison.OrdinalIgnoreCase) || !VersionsEqual(detected.Version, url.Version))
                        {
                            if (detected.Key.Equals(url.Key, StringComparison.OrdinalIgnoreCase) && detected.Repository == url.Repository &&
                                manifest.AutoUpdateOnRestart && (mod.AutoUpdate ?? manifest.AutoUpdateAllMods)) mod.DownloadUrl = "";
                            else throw new InvalidDataException("The preserved downloadUrl for " + mod.Name + " points to " + url.FullName + ", but the detected package is " + detected.FullName + ". Update or clear downloadUrl manually; ModGate has kept your URL unchanged.");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(mod.DownloadUrl)) mod.Package = url.FullName;
                }
                PackageId? package = string.IsNullOrWhiteSpace(mod.Package) ? null : PackageId.Parse(mod.Package);
                if (package?.Repository == "github" && mod.ManifestSource?.Verified(package) != true) throw new InvalidDataException("GitHub package is missing a matching manifest source: " + mod.Name);
                var local = loaded.FirstOrDefault(m => m.Guid == mod.Guid);
                // A required client-only mod may deliberately be absent from the dedicated server.
                if (local != null && !VersionsEqual(local.Version, mod.Version))
                    throw new InvalidDataException("The server's loaded version differs from its manifest for " + mod.Name);
                policy.Mods.Add(mod);
            }
            foreach (var mod in policy.Mods.Where(m => m.Package.Length > 0))
                if (policy.ForbiddenPackages.Contains(PackageId.Parse(mod.Package).Key, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("One package contains both client and serverOnly entries: " + mod.Package);
            policy.Mods.Add(new ModEntry { Guid = Constants.Guid, Name = "Valheim ModGate", Version = Constants.Version, Side = "required" });
            return policy;
        }

        public static void ValidatePolicy(Policy policy)
        {
            if (policy.Protocol != Constants.Protocol || policy.Mods == null || policy.ForbiddenPackages == null || policy.ForbiddenPluginGuids == null || policy.ForbiddenPluginGuids.Count > Constants.MaxMods || policy.ServerName == null || policy.ServerName.Length > 200)
                throw new InvalidDataException("Incompatible ModGate protocol.");
            ValidateInventory(policy.Mods);
            foreach (var mod in policy.Mods)
            {
                var dependencies = DependencyRules.Of(mod).ToArray();
                if (dependencies.Length > 128 || dependencies.Any(d => d.Guid == null || d.Guid.Length == 0 || d.Guid.Length > 200 || (d.MinimumVersion?.Length ?? 0) > 64 || !string.IsNullOrEmpty(d.MinimumVersion) && !System.Version.TryParse(d.MinimumVersion, out _) || !string.IsNullOrEmpty(d.Package) && !PackageId.Allowed(d.Package))) throw new InvalidDataException("Invalid DLL dependency metadata for " + mod.Name);
                if (!Required(mod) && mod.Side != "optional") throw new InvalidDataException("Server sent a non-client mod in its install list.");
                if (!string.IsNullOrWhiteSpace(mod.NexusUrl))
                {
                    NexusLinks.Validate(mod.NexusUrl);
                    if (!string.IsNullOrEmpty(mod.Package) || !string.IsNullOrWhiteSpace(mod.DownloadUrl)) throw new InvalidDataException("Manual Nexus mod cannot also request an automatic package.");
                }
                if (!string.IsNullOrWhiteSpace(mod.DownloadUrl) && PackageId.FromDownloadUrl(mod.DownloadUrl).FullName != mod.Package) throw new InvalidDataException("Server downloadUrl does not match its pinned package.");
                if (!string.IsNullOrEmpty(mod.Package))
                {
                    var id = PackageId.Parse(mod.Package);
                    if (id.Repository == "github" && mod.ManifestSource?.Verified(id) != true) throw new InvalidDataException("GitHub download lacks a matching source manifest.");
                    if (policy.ForbiddenPackages.Contains(id.Key, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Server-only package was requested for a client.");
                }
            }
        }

        public static Comparison Compare(Policy policy, IReadOnlyList<ModEntry> inventory)
        {
            ValidatePolicy(policy);
            ValidateInventory(inventory);
            var local = inventory.ToDictionary(m => m.Guid, StringComparer.Ordinal);
            var expected = policy.Mods.ToDictionary(m => m.Guid, StringComparer.Ordinal);
            var result = new Comparison();
            foreach (var wanted in policy.Mods)
            {
                if (!local.TryGetValue(wanted.Guid, out var found))
                {
                    if (Required(wanted)) result.Missing.Add(wanted);
                }
                else if (!AcceptsVersion(policy, wanted, found.Version)) result.WrongVersion.Add(wanted);
            }
            if (!policy.AllowExtras) result.Extra.AddRange(inventory.Where(m => !expected.ContainsKey(m.Guid) && m.Guid != Constants.Guid));
            return result;
        }

        public static bool VersionsEqual(string left, string right)
        {
            if (!System.Version.TryParse(left, out var a) || !System.Version.TryParse(right, out var b)) return left == right;
            return a.Major == b.Major && a.Minor == b.Minor && Math.Max(0, a.Build) == Math.Max(0, b.Build) && Math.Max(0, a.Revision) == Math.Max(0, b.Revision);
        }

        public static bool Required(ModEntry mod) => mod.Side == "required" || mod.Side == "clientOnly";
        public static bool AcceptsVersion(Policy policy, ModEntry wanted, string installed)
        {
            if (VersionsEqual(wanted.Version, installed)) return true;
            if (wanted.Guid == Constants.Guid || !(wanted.AllowNewerVersion ?? policy.AllowNewerClientVersions) ||
                !System.Version.TryParse(wanted.Version, out var minimum) || !System.Version.TryParse(installed, out var actual)) return false;
            return new System.Version(actual.Major, actual.Minor, Math.Max(0, actual.Build), Math.Max(0, actual.Revision)) >
                new System.Version(minimum.Major, minimum.Minor, Math.Max(0, minimum.Build), Math.Max(0, minimum.Revision));
        }

        public static void ValidateInventory(IReadOnlyList<ModEntry> inventory)
        {
            if (inventory == null || inventory.Count > Constants.MaxMods) throw new InvalidDataException("Invalid mod inventory.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in inventory)
                if (mod == null || string.IsNullOrWhiteSpace(mod.Guid) || mod.Guid.Length > 200 || mod.Name == null || mod.Name.Length > 200 || mod.Version == null || mod.Version.Length > 64 || !System.Version.TryParse(mod.Version, out _) || !seen.Add(mod.Guid))
                    throw new InvalidDataException("Invalid or duplicate mod identifier/version.");
        }
    }
}
