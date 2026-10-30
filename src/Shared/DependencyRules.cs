using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModGate
{
    internal sealed class DependencySuggestion
    {
        public string Package = "", Parent = "";
        public string Label => PackageId.Parse(Package).RawFullName;
    }

    internal static class DependencyRules
    {
        public static IEnumerable<PluginDependency> Of(ModEntry mod) => mod.Dependencies ?? new List<PluginDependency>();
        public static HashSet<string> Required(Policy policy, ISet<string> selected)
        {
            // DLL declarations describe what BepInEx may need to load a mod. They do not
            // grant permission to select another mod or override the server's side setting.
            return new HashSet<string>(policy.Mods.Where(PolicyEngine.Required).Select(m => m.Guid), StringComparer.Ordinal);
        }
        public static PackageId PackageSuggestion(PackageId parent, string dependency)
        {
            var id = PackageId.Parse(dependency);
            return dependency.Contains(":") ? id : id.FromRepository(parent.Repository == "github" ? "thunderstore" : parent.Repository);
        }
        public static bool PackageOptedIn(PackageId package, ISet<string>? optIns) => package.Repository != "github" && optIns != null &&
            optIns.Select(PackageId.Parse).Any(p => p.Repository != "github" && p.Key.Equals(package.Key, StringComparison.OrdinalIgnoreCase));
        public static PackageId PreferredSuggestion(PackageId package, ISet<string>? optIns)
        {
            if (optIns == null) return package;
            return optIns.Select(PackageId.Parse).Where(p => p.Repository != "github" && p.Key.Equals(package.Key, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { package }).OrderByDescending(p => new System.Version(p.Version)).First();
        }
        public static bool ReusesSuggestion(PackageId package, IReadOnlyList<ModEntry>? reusable) => reusable != null && reusable.Any(m =>
            !string.IsNullOrEmpty(m.LocalFile) && !string.IsNullOrEmpty(m.Package) &&
            PackageId.Parse(m.Package).Key.Equals(package.Key, StringComparison.OrdinalIgnoreCase) &&
            System.Version.TryParse(PackageId.Parse(m.Package).Version, out var actual) && System.Version.TryParse(package.Version, out var needed) && actual >= needed);
        public static IEnumerable<string> Warnings(IReadOnlyList<ModEntry> mods)
        {
            foreach (var parent in mods)
                foreach (var dependency in Of(parent).Where(d => !d.Optional && d.Guid != Constants.Guid))
                {
                    var present = mods.FirstOrDefault(m => m.Guid == dependency.Guid);
                    var minimum = string.IsNullOrEmpty(dependency.MinimumVersion) ? "" : " >= " + dependency.MinimumVersion;
                    if (present == null || !string.IsNullOrEmpty(dependency.MinimumVersion) && System.Version.TryParse(dependency.MinimumVersion, out var needed) && (!System.Version.TryParse(present.Version, out var actual) || actual < needed))
                        yield return parent.Name + " declares plugin " + dependency.Guid + minimum + "; " + (present == null ? "it is not selected" : "selected version is " + present.Version) + ". Setup can continue, but BepInEx may refuse to load this mod.";
                }
        }
        public static bool SkipOptionalPackage(Policy policy, PackageId parent, PackageId package)
        {
            var declarations = policy.Mods.Where(m => !string.IsNullOrEmpty(m.Package) && PackageId.Parse(m.Package).Key.Equals(parent.Key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(Of).Where(d => (!string.IsNullOrWhiteSpace(d.Package) && PackageId.Parse(d.Package).Key.Equals(package.Key, StringComparison.OrdinalIgnoreCase)) || AutoSetup.Normalize(d.Guid.Split('.').Last()) == AutoSetup.Normalize(package.Name)).ToArray();
            return declarations.Length > 0 && declarations.All(d => d.Optional) && !declarations.Any(d => policy.Mods.Any(m => m.Guid == d.Guid));
        }
        public static IEnumerable<ModEntry> Modules(Policy policy, ModEntry parent) => policy.Mods.Where(m => Of(parent).Any(d => d.Optional && d.Guid == m.Guid));
        public static string RequiredBy(Policy policy, string guid) => string.Join(", ", policy.Mods.Where(m => Of(m).Any(d => d.Guid == guid)).Select(m => m.Name));
        public static void Link(IReadOnlyList<ModEntry> mods)
        {
            foreach (var mod in mods) foreach (var dependency in Of(mod))
                dependency.Package = mods.FirstOrDefault(m => m.Guid == dependency.Guid)?.Package ?? dependency.Package ?? "";
        }
    }
}
