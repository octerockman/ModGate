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
        private bool completionBackground;
        private Texture2D? completionShade;
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
        private bool WorkflowBusy => task != null || featureTask != null || SelfUpdateBusy || SelfUpdateQueued || scheduled || Profiles.Pending(Paths.BepInExRootPath);
        private bool Busy => WorkflowBusy || ServerHealthRequestActive;
        private bool InstallationReady => !manager && !scheduled && task == null && featureTask == null && prepared != null && preparedProfile?.Incomplete != true && error.Length == 0 && !settingsPage && featurePage.Length == 0;
        private string CurrentSlot => editingSlot ?? (!manager && targetSlot.Length > 0 ? targetSlot : catalog.Active);
        private void DrawWindow(float width, float height)
        {
            InitializeShell();
            var overlay = InstallationReady;
            completionBackground = overlay;
            try { DrawMainWindow(width, height); }
            finally { completionBackground = false; }
            if (overlay)
            {
                if (completionShade == null) completionShade = Solid(new Color(0, 0, 0, .65f));
                GUI.DrawTexture(new Rect(0, 0, width, height), completionShade);
                DrawInstallationReady(width, height);
            }
        }
        private void DrawMainWindow(float width, float height)
        {
            var sidebar = width >= 850 && height >= 680 ? 200f : 0f;
            var left = sidebar + 28; contentWidth = Mathf.Max(1, width - left - 28);
            var showProfileTabs = !settingsPage && featurePage != "Server" && featurePage != "Creators" && (!(manager && editingSlot == null) || ServerHealthSelected);
            var narrowServerLinks = ServerConfigAvailable?.Invoke() == true && !showProfileTabs;
            GUI.DrawTexture(new Rect(0, 0, width, 84), panelBackground!);
            LabelAt(new Rect(28, 17, width - 180, 34), "ModGate", "title");
            LabelAt(new Rect(28, 50, width - 180, 20), "Your worlds. Your mods.", "small", "muted");
            if (Action(new Rect(width - 104, 23, 76, 38), "Close", "quiet")) RequestNavigation(Dismiss);
            Divider(new Rect(0, 83, width, 1));
            if (sidebar > 0) DrawSidebar(sidebar, height);
            else
            {
                var compact = contentWidth < 620; var navWidth = compact ? (contentWidth - 8) / 2 : 96;
                GUI.enabled = !WorkflowBusy;
                if (Action(new Rect(left, 96, navWidth, 34), "Profiles", "nav", manager && editingSlot == null && !settingsPage && featurePage.Length == 0 && !ServerHealthSelected)) RequestNavigation(OpenManager);
                if (Action(new Rect(left + navWidth + 8, 96, navWidth, 34), "Settings", "nav", settingsPage)) RequestNavigation(() => { settingsPage = true; featurePage = ""; });
                if (Action(new Rect(compact ? left : left + 208, compact ? 138 : 96, compact ? navWidth : 196, 34), "Support Mod Creators", "nav", featurePage == "Creators")) RequestNavigation(OpenCreators);
                GUI.enabled = true;
                if (Action(new Rect(compact ? left + navWidth + 8 : width - 178, compact ? 138 : 96, compact ? navWidth : 150, 34), "Report a bug ↗", "quiet")) OpenBugReport();
                if (narrowServerLinks)
                {
                    var linkTop = compact ? 180 : 138; var half = (contentWidth - 8) / 2;
                    GUI.enabled = !WorkflowBusy;
                    if (Action(new Rect(left, linkTop, half, 34), "Server configs", "nav", featurePage == "Server")) RequestNavigation(OpenServerConfigs);
                    if (Action(new Rect(left + half + 8, linkTop, half, 34), "Server health", "nav", ServerHealthSelected)) RequestNavigation(OpenServerHealth);
                    GUI.enabled = true;
                }
            }
            var top = sidebar > 0 ? 107f : 150f + (contentWidth < 620 ? 42 : 0) + (narrowServerLinks ? 42 : 0);
            var context = featurePage == "Creators" ? "Support Mod Creators" : ServerHealthSelected && !settingsPage ? "Connected server" : featurePage == "Server" ? "Server configs" : settingsPage ? "Settings" : manager && editingSlot == null ? "Your profiles" : editingSlot != null ? ProfileName(editingSlot) : policy?.ServerName ?? "Unable to join this server";
            var hasProfileActions = !settingsPage && !ServerHealthSelected && featurePage != "Server" && featurePage != "Creators" && (editingSlot != null || !manager && policy != null);
            LabelAt(new Rect(left, top, Mathf.Max(1, contentWidth - (hasProfileActions ? 268 : 0)), 40), context, "title");
            if (hasProfileActions)
            {
                GUI.enabled = !Busy;
                if (Action(new Rect(left + contentWidth - 252, top, 144, 38), "Check profile")) RequestNavigation(() => OpenHealth(CurrentSlot));
                if (Action(new Rect(left + contentWidth - 98, top, 98, 38), "Restore…")) RequestNavigation(() => OpenHistory(CurrentSlot));
                GUI.enabled = true;
            }
            var subtitle = featurePage == "Creators" ? "A little appreciation goes a long way." : ServerHealthSelected && !settingsPage ? "Server process, connection and main-loop measurements. Admin access is required." : featurePage == "Server" ? "Edit the connected server’s settings. Admin access is required." : settingsPage ? "Your preferences across every profile." : manager && editingSlot == null ? "A separate setup for every server. Your settings stay with it." : scheduled ? "Your changes are saved. Restart to apply this profile." : !manager ? "Choose your mods, then prepare this server’s profile." : "Manage the mods and settings saved in this profile.";
            var subtitleHeight = contentWidth < 620 ? 44 : 24;
            if (ServerHealthSelected) HealthLabel(new Rect(left, top + 46, contentWidth, subtitleHeight), subtitle, "body", "muted");
            else LabelAt(new Rect(left, top + 46, contentWidth, subtitleHeight), subtitle, "body", "muted");
            var contentTop = top + 66 + subtitleHeight;
            var status = error.Length > 0 ? "Could not complete action: " + error : progress;
            if (status.Length > 0)
            {
                var working = task != null || featureTask != null || SelfUpdateBusy;
                GUI.DrawTexture(new Rect(left, contentTop, contentWidth, 64), panelBackground!);
                LabelAt(new Rect(left + 12, contentTop + 8, contentWidth - 24, 52), (working && error.Length == 0 ? "Working… " : "") + status, "body", error.Length > 0 ? "red" : working ? "gold" : "green");
                contentTop += 76;
            }
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
            DrawCreatorPopup(width, height);
        }
        private void DrawSidebar(float width, float height)
        {
            GUI.DrawTexture(new Rect(0, 84, width, height - 84), sidebarBackground!);
            GUI.DrawTexture(new Rect(width - 1, 84, 1, height - 84), separatorBackground!);
            GUI.enabled = !WorkflowBusy;
            if (Action(new Rect(12, 106, width - 24, 42), "Profiles", "nav", manager && editingSlot == null && !settingsPage && featurePage.Length == 0 && !ServerHealthSelected)) RequestNavigation(OpenManager);
            if (Action(new Rect(12, 155, width - 24, 42), "Settings", "nav", settingsPage)) RequestNavigation(() => { settingsPage = true; featurePage = ""; scroll = Vector2.zero; });
            if (Action(new Rect(12, 204, width - 24, 42), "Support Mod Creators", "nav", featurePage == "Creators")) RequestNavigation(OpenCreators);
            if (ServerConfigAvailable?.Invoke() == true && Action(new Rect(12, 253, width - 24, 38), "Server configs", "nav", featurePage == "Server")) RequestNavigation(OpenServerConfigs);
            if (ServerConfigAvailable?.Invoke() == true && Action(new Rect(12, 295, width - 24, 38), "Server health", "nav", ServerHealthSelected)) RequestNavigation(OpenServerHealth);
            var profileTop = ServerConfigAvailable?.Invoke() == true ? 353f : 286f;
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
        private void DrawInstallationReady(float width, float height)
        {
            var cardWidth = Mathf.Min(820, width - 48); var stacked = cardWidth < 680;
            var cardHeight = Mathf.Min(stacked ? 300 : 280, height - 32);
            var card = new Rect((width - cardWidth) / 2, (height - cardHeight) / 2, cardWidth, cardHeight);
            Card(card); GUI.DrawTexture(new Rect(card.x, card.y, card.width, 3), goldBackground!);
            var left = card.x + 24; var available = card.width - 48;
            Badge(new Rect(left, card.y + 20, 166, 26), "READY TO INSTALL", "green");
            if (Action(new Rect(card.x + card.width - 48, card.y + 12, 32, 32), "×", "quiet")) SavePreparationForLater();
            LabelAt(new Rect(left, card.y + 58, available, 44), "Profile prepared successfully", "title");
            LabelAt(new Rect(left, card.y + 108, available, 44), "Your mods and configs are ready.\nInstall now and restart, or apply them on your next launch.", "body", "muted");
            var buttonWidth = stacked ? available : (available - 16) / 2;
            var buttonTop = card.y + card.height - 24 - (stacked ? 104 : 46);
            var normal = button; button = primaryStyle; GUI.enabled = true;
            try
            {
                if (Button(new Rect(left, buttonTop, buttonWidth, 46), "Install and restart")) { Schedule(); if (scheduled) Restart(); }
                button = normal;
                if (Button(new Rect(stacked ? left : left + buttonWidth + 16, buttonTop + (stacked ? 58 : 0), buttonWidth, 46), "Install on next launch")) Schedule();
            }
            catch (Exception failure) when (!(failure is ExitGUIException)) { error = failure.Message; log("ModGate action: " + failure); }
            finally { button = normal; GUI.enabled = true; }
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
            var stages = new[] { "Check", "Download", "Prepare", "Review", "Install" };
            var steps = Reserve(44); var stepWidth = steps.width / stages.Length;
            for (var i = 0; i < stages.Length; i++) Badge(new Rect(steps.x + i * stepWidth, steps.y, stepWidth - 8, 30), (i + 1) + "  " + stages[i], workflow == stages[i] ? "gold" : "muted");
            GUILayout.Space(18);
            var box = Reserve(206); Card(box);
            var preparing = workflow == "Prepare" || workflow == "Install";
            var titleText = workflow == "Download" ? "Downloading mods" : preparing ? "Preparing your profile" : "Checking your profile";
            LabelAt(new Rect(box.x + 22, box.y + 18, box.width - 44, 36), titleText, "heading");
            LabelAt(new Rect(box.x + 22, box.y + 62, box.width - 44, 28), progress.Length > 0 ? progress : "Checking packages and dependencies…", "body", "muted");
            var track = new Rect(box.x + 22, box.y + 118, box.width - 44, 8);
            GUI.DrawTexture(track, buttonHover!);
            if (preparing)
            {
                var pulse = (float)(.5 + .5 * Math.Sin(Time.realtimeSinceStartup * 2));
                GUI.DrawTexture(new Rect(track.x + track.width * .72f * pulse, track.y, track.width * .28f, track.height), goldBackground!);
            }
            else if (downloadFraction > 0) GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp(downloadFraction, 0, 1), track.height), goldBackground!);
            LabelAt(new Rect(box.x + 22, box.y + 143, box.width - 44, 40), preparing ? "Still working · unpacking, checking files and preparing configs" : downloadCount > 0 ? downloadedCount + " of " + downloadCount + " packages prepared · " + (int)(downloadFraction * 100) + "%" : "Resolving the selected setup", "small", "muted");
            Notice("Your current setup is kept", "Matching installed mods are reused. Installation applies after Valheim closes.", "green");
            if (error.Length > 0) Notice("Setup needs attention", error, "red");
        }
    }
}
