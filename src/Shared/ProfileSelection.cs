using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModGate
{
    internal sealed class ProfileSelection
    {
        public readonly List<ModEntry> Downloads = new List<ModEntry>();
        public readonly List<ModEntry> Local = new List<ModEntry>();

        public static bool CanCarry(Policy policy, ModEntry mod) => mod.Guid != Constants.Guid && !policy.ForbiddenPluginGuids.Contains(mod.Guid) &&
            (policy.Mods.Any(m => m.Guid == mod.Guid) || (policy.AllowExtras && !AutoSetup.ServerOnlyName(mod.Name)));

        public static ProfileSelection Build(Policy policy, IReadOnlyList<ModEntry> installed, ISet<string> selected, bool allowIncomplete = false, List<string>? issues = null)
        {
            PolicyEngine.ValidatePolicy(policy);
            selected = new HashSet<string>(selected.Concat(DependencyRules.Required(policy, selected)), StringComparer.Ordinal);
            var plan = new ProfileSelection();
            foreach (var wanted in policy.Mods.Where(m => m.Guid != Constants.Guid && (PolicyEngine.Required(m) || selected.Contains(m.Guid))))
            {
                var local = installed.SingleOrDefault(m => m.Guid == wanted.Guid && PolicyEngine.AcceptsVersion(policy, wanted, m.Version) && !string.IsNullOrWhiteSpace(m.LocalFile));
                if (local != null)
                    plan.Local.Add(new ModEntry { Guid = wanted.Guid, Name = wanted.Name, Version = local.Version, NexusUrl = wanted.NexusUrl ?? "", Package = wanted.Package, Dependencies = local.Dependencies, LocalFile = local.LocalFile, LocalManifest = local.LocalManifest });
                else if (!string.IsNullOrWhiteSpace(wanted.NexusUrl))
                {
                    if (!allowIncomplete) throw new InvalidDataException("Install " + wanted.Name + " " + wanted.Version + " from its Nexus page, restart and reconnect, or uncheck it if optional.");
                    issues?.Add(wanted.Name + ": omitted; its matching Nexus download is not installed.");
                }
                else plan.Downloads.Add(wanted);
            }
            foreach (var mod in installed.Where(m => selected.Contains(m.Guid) && policy.Mods.All(w => w.Guid != m.Guid)))
            {
                if (!CanCarry(policy, mod)) throw new InvalidDataException("This server does not allow carrying " + mod.Name + " into its profile.");
                plan.Local.Add(mod);
            }
            return plan;
        }

        public static Policy ForSelection(Policy policy, ISet<string> selected)
        {
            selected = new HashSet<string>(selected.Concat(DependencyRules.Required(policy, selected)), StringComparer.Ordinal);
            return new Policy { Protocol = policy.Protocol, Nonce = policy.Nonce, ServerName = policy.ServerName, ServerId = policy.ServerId, AllowExtras = policy.AllowExtras, AllowNewerClientVersions = policy.AllowNewerClientVersions, Error = policy.Error,
                ForbiddenPackages = policy.ForbiddenPackages, ForbiddenPluginGuids = policy.ForbiddenPluginGuids,
                Mods = policy.Mods.Where(m => PolicyEngine.Required(m) || selected.Contains(m.Guid)).ToList() };
        }

        public static bool Matches(Policy policy, IReadOnlyList<ModEntry> mods, ISet<string> selected)
        {
            selected = new HashSet<string>(selected.Concat(DependencyRules.Required(policy, selected)), StringComparer.Ordinal);
            if (!PolicyEngine.Compare(policy, mods).Matches) return false;
            if (policy.Mods.Where(m => m.Side == "optional").Any(m => mods.Any(l => l.Guid == m.Guid) != selected.Contains(m.Guid))) return false;
            return selected.Where(g => policy.Mods.All(m => m.Guid != g)).All(g => mods.Any(m => m.Guid == g)) &&
                mods.Where(m => m.Guid != Constants.Guid && policy.Mods.All(w => w.Guid != m.Guid)).All(m => selected.Contains(m.Guid));
        }

        public static HashSet<string> WithDependencies(Policy policy, IReadOnlyList<PackageId> resolved, IReadOnlyList<ModEntry> requested, ISet<string> selected, Func<PackageId, IReadOnlyList<ModEntry>> inspect, Action<string> report, ISet<string>? packageOptIns = null)
        {
            var result = new HashSet<string>(selected, StringComparer.Ordinal);
            if (!policy.AllowExtras) return result;
            var roots = new HashSet<string>(requested.Where(m => !string.IsNullOrWhiteSpace(m.Package)).Select(m => PackageId.Parse(m.Package).Key), StringComparer.OrdinalIgnoreCase);
            foreach (var package in resolved.Where(p => DependencyRules.PackageOptedIn(p, packageOptIns) && !roots.Contains(p.Key) && !ModGate.Downloads.IsBootstrap(p) && !policy.ForbiddenPackages.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
                foreach (var mod in inspect(package))
                {
                    // Permission applies only to a checked, separate dependency package. A root
                    // bundle's extra plugins and unchecked declared server mods stay blocked.
                    if (policy.Mods.Any(m => m.Guid == mod.Guid) || !CanCarry(policy, mod) || !result.Add(mod.Guid)) continue;
                    report("Selected suggested dependency: " + mod.Name + " " + mod.Version + " [" + mod.Guid + "] from " + package.FullName + "; included because its download was checked.");
                }
            return result;
        }

        public static void ValidatePrepared(Policy policy, IReadOnlyList<ModEntry> mods, ISet<string> selected)
        {
            if (Matches(policy, mods, selected)) return;
            var unexpected = mods.Where(m => m.Guid != Constants.Guid && !selected.Contains(m.Guid) &&
                (policy.Mods.Any(w => w.Guid == m.Guid && w.Side == "optional") || policy.Mods.All(w => w.Guid != m.Guid)))
                .Select(m => m.Name + " " + m.Version + " [" + m.Guid + "]").ToArray();
            var missing = selected.Where(g => mods.All(m => m.Guid != g)).ToArray();
            var details = unexpected.Length > 0 ? "Unselected plugins: " + string.Join(", ", unexpected) + ". " : "";
            if (missing.Length > 0) details += "Selected plugins missing: " + string.Join(", ", missing) + ". ";
            throw new InvalidDataException(details + "The prepared profile does not match the checkbox selection. Select the listed bundled mods if offered, or ask the server owner for separate packages.");
        }
    }
}
