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
        private string navigationTab = "Mods", search = "", modFilter = "Needs attention", moreProfile = "", passwordEdit = "";
        private bool settingsPage, showDetails, matchingExpanded;
        private string editorFilter = "All";
        private Texture2D? progressBar;
        private readonly HashSet<string> expandedModules = new HashSet<string>();
        private volatile string workflow = "Choose";
        private volatile float downloadFraction;
        private int downloadCount, downloadedCount;
        private PasswordVault Passwords => new PasswordVault(Paths.BepInExRootPath);
        private void NavigateBack()
        {
            if (ServerHealthSelected) { navigationTab = "Mods"; featurePage = ""; StopServerHealthView(); scroll = Vector2.zero; return; }
            if (featurePage.Length > 0) { featurePage = ""; scroll = Vector2.zero; return; }
            if (settingsPage) { settingsPage = false; navigationTab = "Mods"; return; }
            if (navigationTab != "Mods") { navigationTab = "Mods"; return; }
            if (!manager && !scheduled && prepared != null)
            {
                DownloadCache.DiscardStage(Paths.BepInExRootPath, prepared.Id);
                prepared = null; preparedProfile = null; preparedIssues.Clear(); workflow = "Choose"; progress = "";
                return;
            }
            if (editingSlot != null && !scheduled) { CloseEditor(); return; }
            Dismiss();
        }
        private void DrawContent()
        {
            if (ServerHealthSelected && !settingsPage) { DrawServerHealth(); return; }
            if (settingsPage) { DrawSettings(); return; }
            if (navigationTab == "Mod details" && (editingSlot != null || !manager && policy != null)) { DrawModBrowser(); return; }
            if (manager && !scheduled)
            {
                if (editingSlot == null) { DrawProfileCards(); return; }
                if (navigationTab == "Configs") DrawProfileConfigs(editingSlot);
                else if (navigationTab == "Activity") DrawActivity();
                else { DrawSearch(); DrawEditorFilters(); DrawEditor(); }
                return;
            }
            if (policy != null && difference != null)
            {
                if (navigationTab == "Configs") DrawJoinConfigs();
                else if (navigationTab == "Activity") DrawActivity();
                else DrawJoinMods();
                if (prepared != null) DrawReview();
            }
            else if (error.Length > 0) Label(error);
            if (progress.Length > 0) Label(progress);
            if (error.Length > 0 && policy != null) { Label("Setup stopped: " + error); if (LayoutButton("Copy error")) GUIUtility.systemCopyBuffer = error; }
            if (scheduled) Label("The reviewed change is saved. Restart Valheim to apply it.");
        }
        private void OpenMainTab(string tab)
        {
            if (tab == "Server health") { OpenServerHealth(); return; }
            if (ServerHealthSelected && manager && editingSlot == null) OpenEditor(catalog.Active);
            navigationTab = tab; featurePage = ""; settingsPage = false; scroll = Vector2.zero;
        }
        private void DrawEditorFilters()
        {
            var filters = new[] { "All", "Enabled", "Disabled", "Required", "Optional", "Dependencies" };
            var area = Reserve(contentWidth < 660 ? 82 : 44); var x = area.x; var y = area.y;
            foreach (var filter in filters)
            {
                var width = Mathf.Min(area.width, filter.Length * 7 + 44);
                if (x > area.x && x + width > area.x + area.width) { x = area.x; y += 38; }
                if (Action(new Rect(x, y, width, 32), filter + (editorFilter == filter ? " •" : ""), "filter", editorFilter == filter)) editorFilter = filter;
                x += width + 8;
            }
        }
        private bool SearchMatches(ModEntry mod) => search.Length == 0 || mod.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || mod.Guid.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        private bool EditorVisible(ProfileEditGroup group)
        {
            if (!group.Mods.Any(SearchMatches)) return false;
            if (editorFilter == "Enabled") return group.Enabled;
            if (editorFilter == "Disabled") return !group.Enabled;
            var snapshot = catalog.Servers.FirstOrDefault(s => s.Slot == editingSlot)?.ServerPolicy;
            if (editorFilter == "Required" || editorFilter == "Optional") return snapshot != null && group.Mods.Any(m => snapshot.Mods.Any(w => w.Guid == m.Guid && w.Side == editorFilter.ToLowerInvariant()));
            if (editorFilter == "Dependencies") return snapshot != null && group.Mods.Any(m => DependencyRules.RequiredBy(snapshot, m.Guid).Length > 0);
            return true;
        }
        private bool VisibleMod(ModEntry mod)
        {
            if (!SearchMatches(mod)) return false;
            var module = policy != null && policy.Mods.Any(p => DependencyRules.Of(p).Any(d => d.Optional && d.Guid == mod.Guid));
            if (modFilter == "Required") return PolicyEngine.Required(mod);
            if (modFilter == "Optional") return mod.Side == "optional";
            if (modFilter == "Dependencies") return module || policy != null && DependencyRules.RequiredBy(policy, mod.Guid).Length > 0;
            return modFilter != "Needs attention" || matchingExpanded || ModStatuses.For(mod, local, policy) != ModStatus.Installed;
        }
        private void DrawJoinConfigs()
        {
            Label("Configuration for this profile");
            GUI.enabled = task == null && prepared == null && !scheduled;
            var saved = catalog.Servers.FirstOrDefault(p => p.Slot == targetSlot && (p.ServerKey == serverKey || p.ServerPolicy?.ServerId == policy?.ServerId));
            if (saved != null && Toggle(configSource == "saved:" + saved.Slot, "Keep this server's existing configs (recommended)")) configSource = "saved:" + saved.Slot;
            if (Toggle(configSource == "active", "Copy current profile configs")) configSource = "active";
            if (Toggle(configSource == "offline", "Copy offline profile configs")) configSource = "offline";
            if (Toggle(configSource == "fresh", "Start fresh with default configs")) configSource = "fresh";
            Label("Choose a server slot");
            foreach (var slot in Enumerable.Range(1, 4).Select(i => "server-" + i))
                if (Toggle(targetSlot == slot, slot + ": " + (catalog.Servers.FirstOrDefault(p => p.Slot == slot)?.Name ?? "Empty")) && targetSlot != slot)
                { targetSlot = slot; configSource = catalog.Servers.Any(p => p.Slot == slot && p.ServerKey == serverKey) ? "saved:" + slot : "active"; }
            GUI.enabled = true;
            Label("Replacing configs keeps a backup. Existing server configs are kept by default when updating a saved profile.");
        }
        private void DrawReview()
        {
            Label(preparedProfile?.Incomplete == true ? "PARTIAL PROFILE — the server may still reject it." : "Ready for review");
            foreach (var issue in preparedIssues) Label("• " + issue);
            Label(prepared!.Packages.Count == 0 ? "No downloads were needed. This profile reuses installed mod copies." : "Prepared packages:");
            foreach (var package in prepared.Packages) Label("• " + package + (PackageId.Parse(package).Repository == "github" ? " (GitHub release; source is the installed mod manifest)" : ""));
            if (prepared.LocalPackages?.Count > 0) Label("Reusing selected installed mod copies.");
        }
        private string State(SavedProfile saved)
        {
            if (catalog.Active == saved.Slot) return "Active" + (saved.Incomplete ? " · Incomplete" : "");
            if (saved.Incomplete) return "Incomplete";
            if (saved.ServerPolicy != null)
            {
                var snapshot = Json.CloneLocal(saved.ServerPolicy); snapshot.Protocol = Constants.Protocol; snapshot.Mods = Profiles.WithCurrentConnector(snapshot.Mods);
                if (!PolicyEngine.Compare(snapshot, Profiles.WithCurrentConnector(saved.Mods)).Matches) return "Needs update";
            }
            return "Ready";
        }
        private void DrawProfileConfigs(string slot)
        {
            if (ServerConfigAvailable?.Invoke() == true && LayoutButton("Server configs (admin)")) RequestNavigation(OpenServerConfigs);
            DrawConfigEditor(slot);
            Label("Config file tools");
            Label("This profile keeps its own mod settings. Added mods copy current configs only when no saved config exists.");
            var path = SafePath.Under(Paths.BepInExRootPath, catalog.Active == slot ? "config" : "ModGate/profiles/" + slot + "/config");
            if (LayoutButton("Open config folder")) { Directory.CreateDirectory(path); Application.OpenURL(new Uri(path).AbsoluteUri); }
            DrawConfigSync();
            if (slot != "offline") DrawPasswordSettings(slot);
        }
        private void DrawConfigSync()
        {
            Label("Copy applicable settings from the current profile to the others. Previous settings are backed up.");
            if (LayoutButton("Sync applicable configs to all profiles"))
            {
                try { Inventory.SaveConfigFiles(); var count = ProfileConfigs.SyncAll(Paths.BepInExRootPath, Inventory.Loaded()); progress = "Synced " + count + " applicable config files. Backups are kept."; }
                catch (Exception failure) { error = failure.Message; }
            }
        }
        private void DrawPasswordSettings(string slot)
        {
            var saved = catalog.Servers.First(s => s.Slot == slot);
            var key = string.IsNullOrEmpty(saved.CredentialKey) ? saved.ServerKey : saved.CredentialKey;
            Label("Server password — stored separately using Windows account encryption");
            if (!PasswordVault.Supported) { Label("Password remembering is available on Windows."); return; }
            var enabled = Passwords.Enabled(key); var next = Toggle(enabled, "Remember password for this server");
            if (next != enabled) Passwords.Enable(key, next);
            Label(Passwords.Has(key) ? "A password is saved." : "No password saved. Enter it when joining.");
            passwordEdit = GUILayout.PasswordField(passwordEdit, '•');
            if (LayoutButton("Change password") && passwordEdit.Length > 0) { Passwords.Save(key, passwordEdit); passwordEdit = ""; progress = "Password saved securely."; }
            if (LayoutButton("Forget password")) { Passwords.Forget(key); passwordEdit = ""; progress = "Password forgotten. Enter it on your next join."; }
        }
        private void DrawSettings()
        {
            Label("Settings");
            DrawSelfUpdates();
            var order = SourcePreferences.Load(Paths.BepInExRootPath);
            Label("Download priority: " + string.Join(" > ", order));
            foreach (var source in order) if (Toggle(false, "Prefer " + source)) SourcePreferences.First(Paths.BepInExRootPath, source);
            var enabled = Passwords.Preferences().Enabled; var next = Toggle(enabled, "Remember server passwords by default");
            if (next != enabled) Passwords.EnableAll(next);
            Label("Passwords stay encrypted on this computer and are excluded from profile configuration.");
            GUI.enabled = task == null && prepared == null && preparedEdit == null && !Profiles.Pending(Paths.BepInExRootPath);
            if (LayoutButton("Clear download cache")) { try { DownloadCache.Clear(Paths.BepInExRootPath); progress = "Failed attempts and cached downloads cleared."; } catch (Exception failure) { error = failure.Message; } }
            GUI.enabled = true;
            DrawConfigSync();
            if (LayoutButton("Remove all downloaded mods")) confirmRemoval = true;
            if (confirmRemoval)
            {
                Label("Restore offline and remove ModGate's server profile copies on next launch?");
                if (LayoutButton("Confirm removal on next launch")) ScheduleCommand(new ProfileCommand { Kind = "removeAll" });
                if (LayoutButton("Keep profiles")) confirmRemoval = false;
            }
        }
        private void DrawActivity()
        {
            Label("Activity");
            if (LayoutButton("Create support report")) featurePage = "Support";
            if (LayoutButton("Report a bug on Discord")) OpenBugReport();
            Label(progress.Length > 0 ? progress : "Waiting for your next action.");
            if (error.Length > 0) { Label(error); if (LayoutButton("Copy error")) GUIUtility.systemCopyBuffer = error; }
            foreach (var issue in preparedIssues) Label("• " + issue);
            if (LayoutButton(showDetails ? "Hide technical details" : "Show technical details")) showDetails = !showDetails;
            if (showDetails && File.Exists(setupLog.Path)) foreach (var line in File.ReadLines(setupLog.Path).Reverse().Take(120).Reverse()) Label(line);
        }
    }
}
