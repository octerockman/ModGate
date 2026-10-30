using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private void DrawSearch()
        {
            var area = Reserve(48);
            var field = new Rect(area.x, area.y + 3, area.width, 40);
            search = TextInput(field, search, "mod-search");
            if (search.Length == 0) LabelAt(new Rect(field.x + 14, field.y + 8, Mathf.Max(1, field.width - 28), 24), "Search mods…", "body", "muted");
        }

        private string ModSource(ModEntry mod)
        {
            if (!string.IsNullOrWhiteSpace(mod.NexusUrl)) return "Manual Nexus";
            if (string.IsNullOrWhiteSpace(mod.Package)) return string.IsNullOrEmpty(mod.LocalFile) ? "Package lookup" : "Installed copy";
            try
            {
                var repository = PackageId.Parse(mod.Package).Repository;
                return repository == "thunderstore" ? "Thunderstore" : repository == "hexium" ? "Hexium" : "Manifest source";
            }
            catch (Exception) { return "Package lookup"; }
        }

        private void DrawModChoice(ModEntry mod, ISet<string> required, bool module)
        {
            var installed = local.FirstOrDefault(m => m.Guid == mod.Guid);
            var locked = required.Contains(mod.Guid);
            var status = ModStatuses.For(mod, local, policy);
            var tone = status == ModStatus.Installed ? "green" : installed == null && locked ? "red" : "yellow";
            var statusText = status == ModStatus.Installed
                ? installed != null && !PolicyEngine.VersionsEqual(installed.Version, mod.Version) ? "Newer accepted" : "Installed"
                : installed == null && locked ? "Missing required" : installed == null ? "Optional · Not installed" : "Version change";
            var area = Reserve(contentWidth >= 660 ? 78 : 112);
            var chosen = Check(new Rect(area.x + 3, area.y + (area.width < 640 ? 13 : 26), 21, 21), locked || selectedMods.Contains(mod.Guid),
                !locked && !Busy && prepared == null, (module ? "    " : "") + (locked ? "Required: " : "Optional: ") + mod.Name);
            if (!locked) SetSelection(mod.Guid, chosen);

            var source = ModSource(mod);
            var usedWith = policy == null ? "" : DependencyRules.RequiredBy(policy, mod.Guid);
            var subtitle = mod.Guid + " · " + source;
            if (module && usedWith.Length > 0) subtitle = (locked ? "Required by " : "Module for ") + usedWith + " · " + source;
            else if (mod.Side == "clientOnly") subtitle = "Client only · " + source;

            DrawChoiceContent(area, mod.Name, subtitle, installed?.Version ?? "—", mod.Version, statusText, tone);
            Divider(new Rect(area.x, area.y + area.height - 1, area.width, 1));
        }

        private void DrawChoiceContent(Rect area, string name, string subtitle, string installed, string server, string status, string tone)
        {
            var x = area.x + 34;
            if (area.width < 640)
            {
                LabelAt(new Rect(x, area.y + 6, Mathf.Max(1, area.width - 42), 24), name, "body", tone);
                LabelAt(new Rect(x, area.y + 31, Mathf.Max(1, area.width - 42), 20), subtitle, "small", "muted");
                LabelAt(new Rect(x, area.y + 53, Mathf.Max(1, area.width - 42), 20), "Installed " + installed + (server.Length > 0 ? "  ·  Server " + server : ""), "small", "muted");
                Badge(new Rect(x, area.y + 79, Mathf.Max(1, Mathf.Min(182, area.width - 42)), 24), status, tone);
                return;
            }
            const float badgeWidth = 182, versionWidth = 118;
            var badgeX = area.x + area.width - badgeWidth;
            var versionX = badgeX - versionWidth - 20;
            var nameWidth = Mathf.Max(1, versionX - x - 16);
            LabelAt(new Rect(x, area.y + 11, nameWidth, 25), name, "body", tone);
            LabelAt(new Rect(x, area.y + 39, nameWidth, 24), subtitle, "small", "muted");
            LabelAt(new Rect(versionX, area.y + 13, versionWidth, 21), "Installed " + installed, "small", "muted");
            if (server.Length > 0) LabelAt(new Rect(versionX, area.y + 36, versionWidth, 21), "Server " + server, "small", "muted");
            Badge(new Rect(badgeX, area.y + 26, badgeWidth, 24), status, tone);
        }

        private void DrawInstalledExtra(ModEntry mod)
        {
            var allowed = ProfileSelection.CanCarry(policy!, mod);
            var area = Reserve(contentWidth >= 660 ? 78 : 112);
            var chosen = Check(new Rect(area.x + 3, area.y + (area.width < 640 ? 13 : 26), 21, 21), allowed && selectedMods.Contains(mod.Guid),
                allowed && !Busy && prepared == null, "Keep installed: " + mod.Name + " " + mod.Version + (allowed ? "" : " (not allowed by server)"));
            SetSelection(mod.Guid, allowed && chosen);
            DrawChoiceContent(area, mod.Name, "Client addition · " + ModSource(mod), mod.Version, "", allowed ? "Installed" : "Not allowed", allowed ? "green" : "muted");
            Divider(new Rect(area.x, area.y + area.height - 1, area.width, 1));
        }

        private void DrawSavedModRow(ProfileEditGroup group)
        {
            var savedPolicy = catalog.Servers.FirstOrDefault(p => p.Slot == editingSlot)?.ServerPolicy;
            foreach (var mod in group.Mods)
            {
                var area = Reserve(contentWidth >= 660 ? 78 : 112);
                group.Enabled = Check(new Rect(area.x + 3, area.y + (area.width < 640 ? 13 : 26), 21, 21), group.Enabled,
                    !Busy && preparedEdit == null, mod.Name + " " + mod.Version + (group.Mods.Count > 1 ? " (bundled)" : ""));
                var wanted = savedPolicy?.Mods.FirstOrDefault(m => m.Guid == mod.Guid);
                var accepted = wanted == null || PolicyEngine.AcceptsVersion(savedPolicy!, wanted, mod.Version);
                var tone = !group.Enabled ? "muted" : accepted ? "green" : "yellow";
                var status = !group.Enabled ? "Disabled" : accepted ? "Enabled" : "Version change";
                var subtitle = group.Mods.Count > 1 ? "Bundle: " + string.Join(", ", group.Mods.Select(m => m.Name)) : mod.Guid + " · " + ModSource(mod);
                DrawChoiceContent(area, mod.Name, subtitle, mod.Version, wanted?.Version ?? "", status, tone);
                Divider(new Rect(area.x, area.y + area.height - 1, area.width, 1));
            }
        }

        private void DrawJoinFilters()
        {
            var filters = new[] { "Needs attention", "All", "Required", "Optional", "Dependencies" };
            var widths = filters.Select(label => Mathf.Max(68, label.Length * 7 + 38)).ToArray();
            var available = Mathf.Max(1, contentWidth - 20); var rows = 1; var used = 0f;
            foreach (var width in widths) { if (used > 0 && used + width > available) { rows++; used = 0; } used += width + 8; }
            var area = Reserve(rows * 38 + 5); var x = area.x; var y = area.y + 2;
            for (var i = 0; i < filters.Length; i++)
            {
                var width = Mathf.Min(widths[i], area.width);
                if (x > area.x && x + width > area.x + area.width) { x = area.x; y += 38; }
                if (Action(new Rect(x, y, width, 32), filters[i], "filter", modFilter == filters[i])) modFilter = filters[i];
                x += width + 8;
            }
        }
    }
}
