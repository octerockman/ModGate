using System;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private const string BugReportUrl = "https://discord.gg/AaVzBfPcYH";
        private float contentWidth = 1000;
        private GUIStyle? headingStyle, captionStyle, primaryStyle, panelStyle;
        private Texture2D? sidebarBackground, panelBackground, goldBackground, separatorBackground;
        private string featurePage = "";
        private bool optionalExpanded = true;
        private readonly System.Collections.Generic.HashSet<string> collapsedModules = new System.Collections.Generic.HashSet<string>();

        private void InitializeShell()
        {
            if (headingStyle != null) return;
            sidebarBackground = Solid(new Color(.082f, .11f, .106f, 1));
            panelBackground = Solid(Panel); goldBackground = Solid(Gold); separatorBackground = Solid(Line);
            InitializeVisuals();
            headingStyle = TextStyle("title", "ink"); captionStyle = TextStyle("small", "muted"); panelStyle = cardBox;
        }
        private bool WorkflowBusy => task != null || featureTask != null || scheduled || Profiles.Pending(Paths.BepInExRootPath);
        private bool Busy => WorkflowBusy || ServerHealthRequestActive;
        private string CurrentSlot => editingSlot ?? (!manager && targetSlot.Length > 0 ? targetSlot : catalog.Active);
        private void DrawWindow(float width, float height)
        {
            InitializeShell();
            var sidebar = width >= 850 && height >= 600 ? 200f : 0f;
            var left = sidebar + 28; contentWidth = Mathf.Max(1, width - left - 28);
            var showProfileTabs = !settingsPage && featurePage != "Server" && (!(manager && editingSlot == null) || ServerHealthSelected);
            var narrowServerLinks = ServerConfigAvailable?.Invoke() == true && !showProfileTabs;
            GUI.DrawTexture(new Rect(0, 0, width, 84), panelBackground!);
            LabelAt(new Rect(28, 17, width - 180, 34), "ModGate", "title");
            LabelAt(new Rect(28, 50, width - 180, 20), "Your worlds. Your mods.", "small", "muted");
            if (Action(new Rect(width - 104, 23, 76, 38), "Close", "quiet")) RequestNavigation(Dismiss);
            Divider(new Rect(0, 83, width, 1));
            if (sidebar > 0) DrawSidebar(sidebar, height);
            else
            {
                var compact = contentWidth < 430; var navWidth = compact ? (contentWidth - 8) / 2 : 96;
                GUI.enabled = !WorkflowBusy;
                if (Action(new Rect(left, 96, navWidth, 34), "Profiles", "nav", manager && editingSlot == null && !settingsPage && !ServerHealthSelected)) RequestNavigation(OpenManager);
                if (Action(new Rect(left + navWidth + 8, 96, navWidth, 34), "Settings", "nav", settingsPage)) RequestNavigation(() => { settingsPage = true; featurePage = ""; });
                GUI.enabled = true;
                if (Action(new Rect(compact ? left : width - 178, compact ? 138 : 96, compact ? contentWidth : 150, 34), "Report a bug ↗", "quiet")) OpenBugReport();
                if (narrowServerLinks)
                {
                    var linkTop = compact ? 180 : 138; var half = (contentWidth - 8) / 2;
                    GUI.enabled = !WorkflowBusy;
                    if (Action(new Rect(left, linkTop, half, 34), "Server configs", "nav", featurePage == "Server")) RequestNavigation(OpenServerConfigs);
                    if (Action(new Rect(left + half + 8, linkTop, half, 34), "Server health", "nav", ServerHealthSelected)) RequestNavigation(OpenServerHealth);
                    GUI.enabled = true;
                }
            }
            var top = sidebar > 0 ? 107f : 150f + (contentWidth < 430 ? 42 : 0) + (narrowServerLinks ? 42 : 0);
            var context = ServerHealthSelected && !settingsPage ? "Connected server" : featurePage == "Server" ? "Server configs" : settingsPage ? "Settings" : manager && editingSlot == null ? "Your profiles" : editingSlot != null ? ProfileName(editingSlot) : policy?.ServerName ?? "Unable to join this server";
            var hasProfileActions = !settingsPage && !ServerHealthSelected && featurePage != "Server" && (editingSlot != null || !manager && policy != null);
            LabelAt(new Rect(left, top, Mathf.Max(1, contentWidth - (hasProfileActions ? 268 : 0)), 40), context, "title");
            if (hasProfileActions)
            {
                GUI.enabled = !Busy;
                if (Action(new Rect(left + contentWidth - 252, top, 144, 38), "Check profile")) RequestNavigation(() => OpenHealth(CurrentSlot));
                if (Action(new Rect(left + contentWidth - 98, top, 98, 38), "Restore…")) RequestNavigation(() => OpenHistory(CurrentSlot));
                GUI.enabled = true;
            }
            var subtitle = ServerHealthSelected && !settingsPage ? "Server process, connection and main-loop measurements. Admin access is required." : featurePage == "Server" ? "Edit the connected server’s settings. Admin access is required." : settingsPage ? "Your preferences across every profile." : manager && editingSlot == null ? "A separate setup for every server. Your settings stay with it." : scheduled ? "Your changes are saved. Restart to apply this profile." : !manager ? "Choose your mods, then prepare this server’s profile." : "Manage the mods and settings saved in this profile.";
            var subtitleHeight = contentWidth < 620 ? 44 : 24;
            if (ServerHealthSelected) HealthLabel(new Rect(left, top + 46, contentWidth, subtitleHeight), subtitle, "body", "muted");
            else LabelAt(new Rect(left, top + 46, contentWidth, subtitleHeight), subtitle, "body", "muted");
            var contentTop = top + 66 + subtitleHeight;
            if (showProfileTabs)
            {
                var tabs = ServerConfigAvailable?.Invoke() == true ? new[] { "Mods", "Configs", "Mod details", "Activity", "Server health" } : new[] { "Mods", "Configs", "Mod details", "Activity" };
                var columns = Math.Max(1, Math.Min(tabs.Length, (int)((contentWidth + 8) / 130)));
                var tabWidth = Mathf.Min(144, (contentWidth - (columns - 1) * 8) / columns);
                for (var i = 0; i < tabs.Length; i++)
                {
                    var selected = navigationTab == tabs[i] && (featurePage.Length == 0 || featurePage == "Details");
                    GUI.enabled = !WorkflowBusy || tabs[i] == "Activity" || tabs[i] == "Mods";
                    var tab = tabs[i];
                    if (Action(new Rect(left + (i % columns) * (tabWidth + 8), contentTop + (i / columns) * 42, tabWidth, 40), tab + (selected ? " •" : ""), "tab", selected))
                        RequestNavigation(() => OpenMainTab(tab));
                }
                var tabHeight = ((tabs.Length + columns - 1) / columns) * 42 - 2;
                GUI.enabled = true; Divider(new Rect(left, contentTop + tabHeight, contentWidth, 1)); contentTop += tabHeight + 18;
            }
            var area = new Rect(left, contentTop, contentWidth, Mathf.Max(1, height - contentTop - 140));
            scrollBounds = GUIUtility.GUIToScreenRect(area);
            GUILayout.BeginArea(area);
            try
            {
                scroll = GUILayout.BeginScrollView(scroll); drawingContent = true;
                try
                {
                    if (featurePage.Length > 0) DrawFeatureContent();
                    else if (task != null && navigationTab == "Mods") DrawProcessContent();
                    else DrawContent();
                }
                finally { drawingContent = false; GUILayout.EndScrollView(); }
            }
            catch (Exception failure) when (!(failure is ExitGUIException)) { error = "Unable to display these details: " + failure.Message; log(error); }
            finally { GUILayout.EndArea(); }
            GUI.enabled = true; DrawFooter(left, contentWidth, height);
        }
        private void DrawSidebar(float width, float height)
        {
            GUI.DrawTexture(new Rect(0, 84, width, height - 84), sidebarBackground!);
            GUI.DrawTexture(new Rect(width - 1, 84, 1, height - 84), separatorBackground!);
            GUI.enabled = !WorkflowBusy;
            if (Action(new Rect(12, 106, width - 24, 42), "Profiles", "nav", manager && editingSlot == null && !settingsPage && featurePage.Length == 0 && !ServerHealthSelected)) RequestNavigation(OpenManager);
            if (Action(new Rect(12, 155, width - 24, 42), "Settings", "nav", settingsPage)) RequestNavigation(() => { settingsPage = true; featurePage = ""; scroll = Vector2.zero; });
            if (ServerConfigAvailable?.Invoke() == true && Action(new Rect(12, 204, width - 24, 38), "Server configs", "nav", featurePage == "Server")) RequestNavigation(OpenServerConfigs);
            if (ServerConfigAvailable?.Invoke() == true && Action(new Rect(12, 246, width - 24, 38), "Server health", "nav", ServerHealthSelected)) RequestNavigation(OpenServerHealth);
            var profileTop = ServerConfigAvailable?.Invoke() == true ? 304f : 254f;
            LabelAt(new Rect(24, profileTop, width - 40, 24), "YOUR PROFILES", "small", "muted");
            var slots = new[] { "offline", "server-1", "server-2", "server-3", "server-4" }.Where(s => s == "offline" || catalog.Servers.Any(p => p.Slot == s)).ToArray();
            var y = profileTop + 36; var step = Mathf.Min(58, Mathf.Max(26, (height - 142 - y) / slots.Length));
            foreach (var slot in slots)
            {
                var chosen = slot;
                var viewing = !ServerHealthSelected && (editingSlot == slot || !manager && targetSlot == slot && policy != null);
                if (Action(new Rect(12, y, width - 24, step - 6), ProfileName(slot), "nav", viewing))
                    RequestNavigation(() => { manager = true; featurePage = ""; OpenEditor(chosen); });
                if (catalog.Active == slot) LabelAt(new Rect(width - 32, y, 16, step - 6), "•", "body", "green");
                y += step;
            }
            GUI.enabled = true;
            if (Action(new Rect(12, height - 120, width - 24, 38), "Report a bug ↗", "quiet")) OpenBugReport();
            LabelAt(new Rect(24, height - 72, width - 48, 20), ProfileName(catalog.Active) + " active", "small", "green");
            LabelAt(new Rect(24, height - 50, width - 48, 20), "Changes apply after restart", "small", "muted");
        }
        private void DrawFooter(float left, float width, float height)
        {
            Divider(new Rect(left, height - 126, width, 1));
            var backLabel = ServerHealthSelected ? editingSlot != null ? "Back to profile" : "Back to profiles" : editingSlot != null && !scheduled ? "Back to profiles (Esc)" : manager ? "Close (Esc)" : prepared != null && !scheduled ? "Back to selection" : "Back to menu (Esc)";
            if (Action(new Rect(left, height - 110, Mathf.Min(210, width * .3f), 40), backLabel, "quiet")) RequestNavigation(NavigateBack);
            var split = prepared != null || preparedEdit != null || overrideAvailable;
            var actionWidth = Mathf.Min(split ? 490 : 292, width * (split ? .69f : .58f));
            var action = new Rect(left + width - actionWidth, height - 110, actionWidth, 40);
            var normal = button; button = primaryStyle;
            try { if (!DrawFeaturePrimary(action)) DrawPrimaryAction(action); }
            catch (Exception failure) when (!(failure is ExitGUIException)) { error = failure.Message; log("ModGate action: " + failure); }
            finally { button = normal; GUI.enabled = true; }
            if (Action(new Rect(left, height - 57, 136, 32), "Open setup log", "quiet")) OpenSetupLog();
            if (width > 560) LabelAt(new Rect(left + 150, height - 55, width - 300, 28), "Esc closes ModGate · Changes apply after restart", "small", "muted");
            if (Action(new Rect(left + width - 128, height - 57, 128, 32), "Quit Valheim", "quiet")) RequestNavigation(() => { cancellation?.Cancel(); featureCancellation?.Cancel(); RestoreMenuInput(); ReleaseCursor(); quitGame(); });
        }
        private void OpenBugReport()
        {
            try { Application.OpenURL(BugReportUrl); }
            catch (Exception failure) { error = "Could not open Discord: " + failure.Message; }
        }
        private void DrawProcessContent()
        {
            var stages = new[] { "Check", "Prepare", "Download", "Review", "Install" };
            var steps = Reserve(44); var stepWidth = steps.width / stages.Length;
            for (var i = 0; i < stages.Length; i++) Badge(new Rect(steps.x + i * stepWidth, steps.y, stepWidth - 8, 30), (i + 1) + "  " + stages[i], workflow == stages[i] ? "gold" : "muted");
            GUILayout.Space(18);
            var box = Reserve(206); Card(box);
            var titleText = workflow == "Download" ? "Downloading mods" : workflow == "Install" ? "Preparing installation" : "Preparing your profile";
            LabelAt(new Rect(box.x + 22, box.y + 18, box.width - 44, 36), titleText, "heading");
            LabelAt(new Rect(box.x + 22, box.y + 62, box.width - 44, 28), progress.Length > 0 ? progress : "Checking packages and dependencies…", "body", "muted");
            var track = new Rect(box.x + 22, box.y + 118, box.width - 44, 8);
            GUI.DrawTexture(track, buttonHover!);
            if (downloadFraction > 0) GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp(downloadFraction, 0, 1), track.height), goldBackground!);
            LabelAt(new Rect(box.x + 22, box.y + 143, box.width - 44, 24), downloadCount > 0 ? downloadedCount + " of " + downloadCount + " packages prepared · " + (int)(downloadFraction * 100) + "%" : "Resolving the selected setup", "small", "muted");
            Notice("Your current setup is kept", "Matching installed mods are reused. Installation applies after Valheim closes.", "green");
            if (error.Length > 0) Notice("Setup needs attention", error, "red");
        }
    }
}
