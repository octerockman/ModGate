using System;
using System.Linq;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private bool installedExtrasExpanded;
        private void DrawJoinMods()
        {
            var current = policy!;
            if (catalog.Servers.Any(p => p.Slot == catalog.Active && p.Incomplete))
                Notice("Incomplete profile", "This profile is active but was saved as partial.", "yellow");
            if (ConnectorMismatch())
                Notice("Update ModGate first", "This server requires ModGate " + current.Mods.First(m => m.Guid == Constants.Guid).Version + ". Update both DLLs on the server and client.", "yellow");

            var required = DependencyRules.Required(current, selectedMods);
            var requiredMods = current.Mods.Where(m => m.Guid != Constants.Guid && required.Contains(m.Guid)).ToArray();
            var missing = requiredMods.Count(m => local.All(l => l.Guid != m.Guid));
            var different = requiredMods.Count(m => local.Any(l => l.Guid == m.Guid) && ModStatuses.For(m, local, current) != ModStatus.Installed);
            var attention = missing > 0 || different > 0;
            var description = attention ? missing + " required mods missing · " + different + " version changes" : "Your required mods match this server.";
            Notice(attention ? "A few mods need attention" : "Ready for this server", description, attention ? "yellow" : "green");
            DrawDependencySuggestions();

            DrawSearch(); DrawJoinFilters();
            var headings = Reserve(30);
            LabelAt(new Rect(headings.x + 34, headings.y, headings.width - 34, 24), "MOD", "small", "muted");
            if (headings.width >= 640)
            {
                LabelAt(new Rect(headings.x + headings.width - 320, headings.y, 118, 24), "VERSIONS", "small", "muted");
                LabelAt(new Rect(headings.x + headings.width - 182, headings.y, 182, 24), "STATUS", "small", "muted");
            }
            Divider(new Rect(headings.x, headings.y + 29, headings.width, 1));
            var visibleRequired = requiredMods.Where(VisibleMod).OrderBy(m => ModStatuses.For(m, local, current) == ModStatus.Installed).ThenBy(m => m.Name).ToArray();
            var matching = requiredMods.Where(m => ModStatuses.For(m, local, current) == ModStatus.Installed).ToArray();
            foreach (var mod in visibleRequired.Where(m => ModStatuses.For(m, local, current) != ModStatus.Installed)) DrawModChoice(mod, required, false);

            if (modFilter == "Needs attention" && matching.Length > 0)
            {
                var area = Reserve(42);
                if (Action(new Rect(area.x, area.y + 4, area.width, 32), (matchingExpanded ? "Hide" : "Show") + " matching mods (" + matching.Length + ")", "link-green")) matchingExpanded = !matchingExpanded;
                Divider(new Rect(area.x, area.y + area.height - 1, area.width, 1));
                if (matchingExpanded) foreach (var mod in matching.Where(SearchMatches)) DrawModChoice(mod, required, false);
            }
            else foreach (var mod in visibleRequired.Where(m => ModStatuses.For(m, local, current) == ModStatus.Installed)) DrawModChoice(mod, required, false);

            var optional = current.Mods.Where(m => m.Guid != Constants.Guid && m.Side == "optional" && !required.Contains(m.Guid)).OrderBy(m => m.Name).ToArray();
            if (optional.Length > 0)
            {
                var area = Reserve(46);
                if (Action(new Rect(area.x, area.y + 5, area.width, 32), (optionalExpanded ? "▼ " : "▶ ") + "Optional mods (" + optional.Length + ") · " + optional.Count(m => selectedMods.Contains(m.Guid)) + " selected", "link")) optionalExpanded = !optionalExpanded;
                Divider(new Rect(area.x, area.y + area.height - 1, area.width, 1));
                if (optionalExpanded)
                    foreach (var mod in optional.Where(SearchMatches).Where(m => modFilter != "Required" && (modFilter != "Dependencies" || DependencyRules.RequiredBy(current, m.Guid).Length > 0)))
                        DrawModChoice(mod, required, true);
            }

            var extras = local.Where(m => m.Guid != Constants.Guid && current.Mods.All(w => w.Guid != m.Guid)).Where(SearchMatches).ToArray();
            if (extras.Length > 0)
            {
                var area = Reserve(44);
                if (Action(new Rect(area.x, area.y + 4, area.width, 32), (installedExtrasExpanded ? "▼ " : "▶ ") + "Other installed mods (" + extras.Length + ")", "link")) installedExtrasExpanded = !installedExtrasExpanded;
                if (installedExtrasExpanded) foreach (var mod in extras) DrawInstalledExtra(mod);
            }

            foreach (var mod in current.Mods.Where(m => (required.Contains(m.Guid) || selectedMods.Contains(m.Guid)) && !string.IsNullOrWhiteSpace(m.NexusUrl) && !local.Any(l => l.Guid == m.Guid && PolicyEngine.AcceptsVersion(current, m, l.Version))))
            {
                var area = Reserve(44);
                if (Action(new Rect(area.x, area.y + 4, area.width, 34), "Open Nexus download page: " + mod.Name, "secondary")) Application.OpenURL(NexusLinks.Validate(mod.NexusUrl));
            }
        }
    }
}
