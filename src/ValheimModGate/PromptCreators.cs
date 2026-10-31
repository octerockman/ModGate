using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private bool creatorsAllProfiles, drawingCreatorPopup;
        private string creatorsSearch = "", creatorsMessage = "";
        private CreatorSupportResult creators = new CreatorSupportResult { Mods = new List<CreatorMod> { CreatorSupport.Pinned() } };
        private CreatorMod? creatorPopup;
        private readonly Dictionary<string, Texture2D?> creatorIcons = new Dictionary<string, Texture2D?>(StringComparer.Ordinal);
        private Texture2D? creatorStar, creatorScrim;
        private GUIStyle? creatorWrapped;
        private void OpenCreators()
        {
            featurePage = "Creators"; settingsPage = false; creatorPopup = null; creatorsSearch = ""; creatorsAllProfiles = false;
            scroll = Vector2.zero; RefreshCreators();
        }
        private void RefreshCreators()
        {
            var root = Paths.BepInExRootPath; var all = creatorsAllProfiles; ProfileCatalog state;
            creatorPopup = null; creatorsMessage = ""; ClearCreatorIcons(); creators = new CreatorSupportResult { Mods = new List<CreatorMod> { CreatorSupport.Pinned() } };
            try { state = Profiles.Load(root); }
            catch (Exception failure) { error = "Could not read the installed profiles: " + failure.Message; return; }
            catalog = state;
            RunFeature("Reading installed mods…", token =>
            {
                var result = CreatorSupport.Read(root, state, all, Inventory.InspectSupportPayload, token);
                return () => { ClearCreatorIcons(); creators = result; };
            });
        }
        private void CreatorText(Rect area, string text, string tone = "muted")
        {
            if (creatorWrapped == null) creatorWrapped = new GUIStyle(TextStyle("body", "muted")) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            creatorWrapped.normal.textColor = Tone(tone); GUI.Label(area, text, creatorWrapped);
        }
        private void DrawCreators()
        {
            CreatorText(Reserve(64), "Enjoying your mods? Liking, endorsing or starring them is a free way to thank their creators. Choose a star to visit the mod’s pages.");
            var filters = Reserve(contentWidth < 650 ? 88 : 44); var half = Mathf.Min(200, (filters.width - 8) / 2);
            GUI.enabled = featureTask == null;
            if (Action(new Rect(filters.x, filters.y, half, 36), "Active profile only", "filter", !creatorsAllProfiles) && creatorsAllProfiles) { creatorsAllProfiles = false; scroll = Vector2.zero; RefreshCreators(); }
            if (Action(new Rect(filters.x + half + 8, filters.y, half, 36), "All profiles", "filter", creatorsAllProfiles) && !creatorsAllProfiles) { creatorsAllProfiles = true; scroll = Vector2.zero; RefreshCreators(); }
            if (Action(new Rect(contentWidth < 650 ? filters.x : filters.x + filters.width - 100, contentWidth < 650 ? filters.y + 44 : filters.y, 100, 36), "Refresh", "quiet")) RefreshCreators();
            GUI.enabled = creatorPopup == null;
            var searchArea = Reserve(66);
            LabelAt(new Rect(searchArea.x, searchArea.y, searchArea.width, 20), "Search installed mods", "small", "muted");
            creatorsSearch = TextInput(new Rect(searchArea.x, searchArea.y + 25, searchArea.width, 36), creatorsSearch, "creator-search");
            GUI.enabled = true;
            if (featureTask != null) { Label("Reading installed mods…"); return; }
            var matches = creators.Mods.Where(m => m.ModGate || creatorsSearch.Trim().Length == 0 || m.Name.IndexOf(creatorsSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 || m.Author.IndexOf(creatorsSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var header = Reserve(36); LabelAt(header, creatorsAllProfiles ? "INSTALLED IN ALL PROFILES" : "INSTALLED IN " + ProfileName(catalog.Active).ToUpperInvariant(), "small", "muted");
            foreach (var mod in matches)
            {
                var row = Reserve(86); Divider(new Rect(row.x, row.y, row.width, 1));
                var iconArea = new Rect(row.x, row.y + 19, 44, 44); Card(iconArea);
                var icon = CreatorIcon(mod);
                if (icon != null) GUI.DrawTexture(new Rect(iconArea.x + 3, iconArea.y + 3, 38, 38), icon);
                else LabelAt(new Rect(iconArea.x + 6, iconArea.y, 32, 44), mod.ModGate ? "MG" : Initials(mod.Name), "heading", "gold");
                var textWidth = Mathf.Max(1, row.width - 124);
                LabelAt(new Rect(row.x + 58, row.y + 14, textWidth, 26), mod.Name, "heading");
                var info = mod.ModGate ? "Studio Bascinet · Always available" : (mod.Author.Length > 0 ? mod.Author + " · " : "") + string.Join(", ", mod.Profiles.OrderBy(p => p).Select(ProfileName));
                LabelAt(new Rect(row.x + 58, row.y + 44, textWidth, 22), info, "small", "muted");
                var star = new Rect(row.x + row.width - 48, row.y + 19, 48, 44);
                GUI.Box(star, "", ButtonStyle("secondary", false));
                var before = GUI.enabled; GUI.enabled = before && creatorPopup == null;
                var clicked = GUI.Button(star, "Support: " + mod.Id, invisibleControl!) || PointerClicked(star);
                GUI.enabled = before;
                GUI.DrawTexture(new Rect(star.x + 12, star.y + 10, 24, 24), CreatorStar());
                if (clicked) { pointerClick = pointerPress = null; creatorPopup = mod; creatorsMessage = ""; }
            }
            if (creators.Mods.Count == 1) CreatorText(Reserve(58), creatorsAllProfiles ? "No other installed mods were found in your current profiles." : "No other mods are installed in the active profile. Try All profiles to view your other installed setups.");
            else if (matches.Length == 1 && creatorsSearch.Trim().Length > 0) Label("No other installed mods match your search.");
            if (creators.Skipped > 0) CreatorText(Reserve(48), "Some unavailable or unreadable mod folders were skipped. Restore points and previously removed mods are not included.");
            CreatorText(Reserve(52), "The star opens page links only. Endorse or star on the site itself; you may need to sign in. ModGate does not submit or check votes.");
            if (error.Length > 0) CreatorText(Reserve(52), error, "red");
        }
        private static string Initials(string name)
        {
            var words = name.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 1 ? (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant() : name.Length > 0 ? name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant() : "?";
        }
        private Texture2D CreatorStar()
        {
            if (creatorStar != null) return creatorStar;
            creatorStar = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            var vertices = Enumerable.Range(0, 10).Select(i => { var angle = Math.PI / 2 + i * Math.PI / 5; var radius = i % 2 == 0 ? 14 : 6; return new Vector2(16 + (float)Math.Cos(angle) * radius, 16 + (float)Math.Sin(angle) * radius); }).ToArray();
            for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            {
                var distance = 100f;
                for (var i = 0; i < 10; i++) { var a = vertices[i]; var b = vertices[(i + 1) % 10]; var dx = b.x - a.x; var dy = b.y - a.y; var t = Mathf.Clamp(((x + .5f - a.x) * dx + (y + .5f - a.y) * dy) / (dx * dx + dy * dy), 0, 1); var xx = x + .5f - a.x - t * dx; var yy = y + .5f - a.y - t * dy; distance = Mathf.Min(distance, (float)Math.Sqrt(xx * xx + yy * yy)); }
                creatorStar.SetPixel(x, y, new Color(Gold.r, Gold.g, Gold.b, Mathf.Clamp(1.4f - distance, 0, 1)));
            }
            creatorStar.Apply(); return creatorStar;
        }
        private Texture2D? CreatorIcon(CreatorMod mod)
        {
            if (creatorIcons.TryGetValue(mod.Id, out var cached)) return cached;
            Texture2D? icon = null;
            foreach (var path in mod.Icons)
            {
                Texture2D? source = null;
                try
                {
                    var bytes = CreatorSupport.IconBytes(Paths.BepInExRootPath, path); if (bytes == null) continue;
                    source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(source, bytes, false)) continue;
                    var pixels = source.GetPixels(); var small = new Color[64 * 64];
                    for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) small[y * 64 + x] = pixels[Math.Min(source.height - 1, y * source.height / 64) * source.width + Math.Min(source.width - 1, x * source.width / 64)];
                    icon = new Texture2D(64, 64, TextureFormat.RGBA32, false); icon.SetPixels(small); icon.Apply(); break;
                }
                catch (Exception failure) { log("Could not read a local mod icon: " + failure.Message); }
                finally { if (source != null) UnityEngine.Object.Destroy(source); }
            }
            creatorIcons[mod.Id] = icon; return icon;
        }
        private void ClearCreatorIcons()
        {
            foreach (var icon in creatorIcons.Values) if (icon != null) UnityEngine.Object.Destroy(icon);
            creatorIcons.Clear();
        }
        private void DrawCreatorPopup(float width, float height)
        {
            var mod = creatorPopup; if (mod == null) return;
            if (creatorScrim == null) creatorScrim = Solid(new Color(.015f, .03f, .025f, .85f));
            GUI.DrawTexture(new Rect(0, 0, width, height), creatorScrim);
            var w = Mathf.Min(520, width - 32); var h = Mathf.Min(height - 24, 240 + mod.Links.Count * 52);
            var panel = new Rect((width - w) / 2, (height - h) / 2, w, h); Card(panel);
            var tight = h < 420; var linkTop = tight ? 112 : 162;
            var linkStep = tight ? Mathf.Min(52, (h - linkTop - 48) / Math.Max(1, mod.Links.Count)) : 52;
            drawingCreatorPopup = true;
            try
            {
                LabelAt(new Rect(panel.x + 24, panel.y + (tight ? 12 : 22), w - 90, 20), "SUPPORT THIS CREATOR", "small", "gold");
                if (Action(new Rect(panel.xMax - 58, panel.y + 12, 42, 36), "×", "quiet")) { creatorPopup = null; return; }
                LabelAt(new Rect(panel.x + 24, panel.y + (tight ? 32 : 50), w - 48, 38), mod.Name, "title");
                CreatorText(new Rect(panel.x + 24, panel.y + (tight ? 74 : 102), w - 48, tight ? 32 : 48), "Choose a site to open this mod’s page. You can endorse or star it there for free.");
                for (var i = 0; i < mod.Links.Count; i++)
                {
                    var link = mod.Links[i];
                    var linkArea = new Rect(panel.x + 24, panel.y + linkTop + i * linkStep, w - 48, Mathf.Max(18, linkStep - 8));
                    GUI.Box(linkArea, "", ButtonStyle("secondary", false));
                    if (Action(linkArea, link.Host + " ↗", "link"))
                    {
                        try { var page = CreatorSupport.Page(link.Url); if (page == null) throw new InvalidDataException("This mod page is not supported."); Application.OpenURL(page.Url); creatorsMessage = ""; }
                        catch (Exception failure) { creatorsMessage = "Could not open the browser. Please try again."; log("Mod creator page: " + failure.Message); }
                    }
                    if (w >= 440) LabelAt(new Rect(linkArea.xMax - 140, linkArea.y, 128, linkArea.height), "Open mod page", "small", "gold");
                }
                if (mod.Links.Count == 0) CreatorText(new Rect(panel.x + 24, panel.y + linkTop, w - 48, 46), "No known mod-page links were found in this installation.");
                CreatorText(new Rect(panel.x + 24, panel.yMax - (tight ? 42 : 58), w - 48, 40), creatorsMessage.Length > 0 ? creatorsMessage : "Opens in your browser. You may need to sign in.\nEsc closes this popup.", creatorsMessage.Length > 0 ? "red" : "muted");
            }
            finally { drawingCreatorPopup = false; }
        }
    }
}
