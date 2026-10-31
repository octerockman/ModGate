using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private readonly List<DependencySuggestion> dependencySuggestions = new List<DependencySuggestion>();
        private readonly HashSet<string> selectedDependencyPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool dependencySuggestionsExpanded = true;

        private void SuggestDependency(DependencySuggestion suggestion)
        {
            var id = PackageId.Parse(suggestion.Package);
            if (Downloads.IsBootstrap(id) || id.Repository == "github" || policy == null || policy.ForbiddenPackages.Contains(id.Key, StringComparer.OrdinalIgnoreCase)) return;
            // A server-declared mod already has its own checkbox (or explicit required lock).
            if (policy.Mods.Any(m => m.Package.Length > 0 && PackageId.Parse(m.Package).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase))) return;
            lock (dependencySuggestions)
                if (!dependencySuggestions.Any(s => s.Package == suggestion.Package && s.Parent == suggestion.Parent)) dependencySuggestions.Add(suggestion);
        }
        private void InitializeDependencyChoices()
        {
            lock (dependencySuggestions) dependencySuggestions.Clear();
            selectedDependencyPackages.Clear(); dependencySuggestionsExpanded = true;
            if (policy == null) return;
            foreach (var mod in policy.Mods)
            {
                foreach (var dependency in DependencyRules.Of(mod).Where(d => d.Package.Length > 0))
                    SuggestDependency(new DependencySuggestion { Package = dependency.Package, Parent = mod.Name });
                foreach (var dependency in mod.ManifestSource?.Dependencies ?? new List<string>())
                {
                    try
                    {
                        var parent = PackageId.Parse(mod.Package);
                        SuggestDependency(new DependencySuggestion { Package = DependencyRules.PackageSuggestion(parent, dependency).FullName, Parent = mod.Name });
                    }
                    catch (System.IO.InvalidDataException) { }
                }
            }
        }
        private bool SelectedDependencyPackagesMatch(SavedProfile saved)
        {
            var stored = saved.Packages.Select(PackageId.Parse).ToArray();
            return selectedDependencyPackages.All(p => stored.Any(s => DependencyRules.PackageOptedIn(s, new HashSet<string> { p }))) &&
                dependencySuggestions.All(s => DependencyRules.PackageOptedIn(PackageId.Parse(s.Package), selectedDependencyPackages) || !stored.Any(p => p.Key.Equals(PackageId.Parse(s.Package).Key, StringComparison.OrdinalIgnoreCase)));
        }
        private void ChangeDependencyChoice(string package, bool selected)
        {
            if (selected)
            {
                selectedDependencyPackages.Add(package);
                var key = PackageId.Parse(package).Key;
                foreach (var mod in local.Where(m => ProfileSelection.CanCarry(policy!, m) && m.Package.Length > 0 && PackageId.Parse(m.Package).Key.Equals(key, StringComparison.OrdinalIgnoreCase))) selectedMods.Add(mod.Guid);
            }
            else
            {
                var key = PackageId.Parse(package).Key;
                selectedDependencyPackages.RemoveWhere(p => PackageId.Parse(p).Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                foreach (var mod in local.Concat(catalog.Servers.SelectMany(s => s.Mods)).Where(m => m.Package.Length > 0 && PackageId.Parse(m.Package).Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    if (policy!.Mods.All(m => m.Guid != mod.Guid)) selectedMods.Remove(mod.Guid);
            }
            // An existing staged receipt reflects the old checkboxes and must never be installed.
            DiscardUnscheduledPreparation(); prepared = null; preparedProfile = null; preparedIssues.Clear();
            workflow = "Choose"; progress = "Dependency choices changed. Prepare the profile again to review the selected downloads.";
        }
        private bool DependencyParentIncluded(string parent)
        {
            var included = policy!.Mods.Where(m => PolicyEngine.Required(m) || selectedMods.Contains(m.Guid)).Concat(local.Where(m => selectedMods.Contains(m.Guid))).ToArray();
            if (included.Any(m => m.Name == parent)) return true;
            if (!PackageId.Allowed(parent)) return false;
            var id = PackageId.Parse(parent);
            return DependencyRules.PackageOptedIn(id, selectedDependencyPackages) || included.Any(m => m.Package.Length > 0 && PackageId.Parse(m.Package).Key.Equals(id.Key, StringComparison.OrdinalIgnoreCase));
        }
        private void DrawDependencySuggestions()
        {
            var current = policy!;
            DependencySuggestion[] suggestions;
            lock (dependencySuggestions) suggestions = dependencySuggestions.Where(s => DependencyParentIncluded(s.Parent)).GroupBy(s => PackageId.Parse(s.Package).Key, StringComparer.OrdinalIgnoreCase).Select(g => new DependencySuggestion { Package = g.OrderByDescending(s => new System.Version(PackageId.Parse(s.Package).Version)).First().Package, Parent = string.Join(", ", g.Select(s => s.Parent).Distinct()) }).ToArray();
            var selected = current.Mods.Where(m => PolicyEngine.Required(m) || selectedMods.Contains(m.Guid)).Concat(local.Where(m => selectedMods.Contains(m.Guid) && current.Mods.All(w => w.Guid != m.Guid))).ToArray();
            var warnings = DependencyRules.Warnings(selected).ToArray();
            if (suggestions.Length == 0 && warnings.Length == 0) return;
            var header = Reserve(46);
            if (Action(new Rect(header.x, header.y + 5, header.width, 32), (dependencySuggestionsExpanded ? "▼ " : "▶ ") + "Suggested dependencies (" + suggestions.Length + ")", "link")) dependencySuggestionsExpanded = !dependencySuggestionsExpanded;
            if (!dependencySuggestionsExpanded) return;
            Label("Dependencies are suggestions. Check a download to include it; unchecked suggestions do not stop setup. BepInEx may refuse to load a mod without its declared dependencies.");
            Label("A checked download is used only when an included mod lists it. Unchecked parent mods do not add dependencies.");
            if (!current.AllowExtras) Label("This server does not allow unapproved extra mods. The owner must add these dependencies to its client mod list before they can be included.");
            foreach (var suggestion in suggestions)
            {
                var id = PackageId.Parse(suggestion.Package); var area = Reserve(74);
                var chosen = DependencyRules.PackageOptedIn(id, selectedDependencyPackages);
                var next = Check(new Rect(area.x + 3, area.y + 15, 21, 21), chosen, current.AllowExtras && !Busy, "Download suggested dependency: " + suggestion.Label);
                LabelAt(new Rect(area.x + 34, area.y + 7, area.width - 42, 25), suggestion.Label, "body", "yellow");
                LabelAt(new Rect(area.x + 34, area.y + 36, area.width - 42, 24), "Suggested by " + suggestion.Parent, "small", "muted");
                Divider(new Rect(area.x, area.y + 73, area.width, 1));
                if (next != chosen) ChangeDependencyChoice(suggestion.Package, next);
            }
            foreach (var warning in warnings) Label(warning);
        }
    }
}
