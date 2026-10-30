using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private string detailSearch = "";

        private void DrawModBrowser()
        {
            var mods = DetailBrowserMods();
            var selectedGuid = detailMod?.Guid;
            if (selectedGuid != null)
            {
                var current = mods.FirstOrDefault(m => m.Guid == selectedGuid);
                if (current == null)
                {
                    detailMod = null; detailTab = "Overview"; scroll = Vector2.zero;
                }
                else detailMod = current;
            }
            if (detailMod != null)
            {
                if (Action(Reserve(38), "Choose another mod", "link"))
                { detailMod = null; detailTab = "Overview"; scroll = Vector2.zero; return; }
                GUILayout.Space(10);
                DrawModDetails();
                return;
            }

            Section("Mod details", "Browse versions, dependencies and locally available package notes.");
            var searchArea = Reserve(64);
            LabelAt(new Rect(searchArea.x, searchArea.y, searchArea.width, 22), "Search by mod name or GUID", "small", "muted");
            detailSearch = TextInput(new Rect(searchArea.x, searchArea.y + 25, searchArea.width, 36), detailSearch, "mod-details-search");

            var query = detailSearch.Trim();
            var matches = mods.Where(m => query.Length == 0 || m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || m.Guid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var count = Reserve(32);
            LabelAt(new Rect(count.x, count.y + 4, query.Length == 0 ? count.width : Mathf.Max(1, count.width - 112), 24), query.Length == 0 ? mods.Length + (mods.Length == 1 ? " mod" : " mods") : matches.Length + " of " + mods.Length + " mods", "small", "muted");
            if (query.Length > 0 && Action(new Rect(count.x + count.width - 104, count.y, 104, 30), "Clear search", "quiet")) detailSearch = "";
            if (matches.Length == 0)
            {
                LabelAt(Reserve(56), mods.Length == 0 ? "There are no mods to browse in this profile yet." : "No mods match your search.", "body", "muted");
                return;
            }

            foreach (var mod in matches)
            {
                var row = Reserve(76);
                Card(row);
                var versionWidth = Mathf.Clamp(mod.Version.Length * 6.5f + 20, 68, 136);
                var name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Guid : mod.Name;
                if (Action(new Rect(row.x + 6, row.y + 5, Mathf.Max(1, row.width - versionWidth - 32), 36), "View: " + name, "link"))
                {
                    detailMod = mod; detailTab = "Overview"; scroll = Vector2.zero;
                    return;
                }
                Badge(new Rect(row.x + row.width - versionWidth - 16, row.y + 12, versionWidth, 26), mod.Version.Length == 0 ? "Unknown" : mod.Version, "muted");
                LabelAt(new Rect(row.x + 18, row.y + 43, row.width - 36, 22), mod.Guid, "small", "muted");
                GUILayout.Space(8);
            }
        }

        private ModEntry[] DetailBrowserMods()
        {
            IEnumerable<ModEntry> source;
            if (editingSlot != null) source = editGroups.SelectMany(g => g.Mods);
            else if (!manager && policy != null) source = policy.Mods;
            else source = catalog.OfflineMods.Count > 0 ? catalog.OfflineMods : local;
            return source.Where(m => !string.IsNullOrWhiteSpace(m.Guid) && m.Guid != Constants.Guid)
                .GroupBy(m => m.Guid, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(m => string.IsNullOrWhiteSpace(m.Name) ? m.Guid : m.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Guid, StringComparer.Ordinal).ToArray();
        }
    }
}
