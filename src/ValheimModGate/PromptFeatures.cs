using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private Task<Action>? featureTask;
        private CancellationTokenSource? featureCancellation;
        private ProfileHealthReport? healthReport;
        private readonly HashSet<string> repairSelection = new HashSet<string>();
        private List<ProfileSnapshot> snapshots = new List<ProfileSnapshot>();
        private string snapshotSelection = "", toolSlot = "", configSearch = "", configFile = "", detailTab = "Overview";
        private string configEditorSlot = "";
        internal Func<string, string>? ReloadConfigs = null;
        private ConfigEditorDocument? configDocument;
        private List<string> configFiles = new List<string>();
        private ModEntry? detailMod;
        private string confirmTitle = "", confirmText = "";
        private Action? confirmAction, pendingNavigation;
        private bool reportLogs = true, reportMods = true, reportHealth = true;
        private readonly object transferGate = new object();
        private readonly List<string> transferSteps = new List<string>();
        private string transferPackage = "";
        private readonly List<string> featureStages = new List<string>();
        private Action? afterFeature;
        private string expandedConfigChoice = "", expandedServerChoice = "";
        private bool ConfigDirty => configDocument != null && configDocument.Entries.Any(e => e.Value != e.OriginalValue) || RemoteDirty;

        private void RequestNavigation(Action navigation)
        {
            Action apply = () => { var wasHealth = ServerHealthView; navigation(); if (wasHealth && !ServerHealthView) StopServerHealthView(); };
            if (ConfigDirty) { pendingNavigation = apply; return; }
            apply();
        }
        private void HandleEscape()
        {
            if (InstallationReady) { SavePreparationForLater(); return; }
            if (creatorPopup != null) { creatorPopup = null; return; }
            if (pendingNavigation != null) { pendingNavigation = null; return; }
            if (confirmAction != null) { confirmAction = null; return; }
            if (deleteSlot != null) { deleteSlot = null; return; }
            if (ServerHealthSelected) { RequestNavigation(Dismiss); return; }
            RequestNavigation(() => { if (editingSlot != null && !scheduled) CloseEditor(); else Dismiss(); });
        }
        private void Confirm(string titleText, string description, Action action) { confirmTitle = titleText; confirmText = description; confirmAction = action; }
        private void DrawFeatureModal(float width, float height)
        {
            GUI.Label(new Rect(20, 32, width - 40, 42), pendingNavigation != null ? "Keep your config changes?" : confirmTitle, title!);
            GUI.Label(new Rect(20, 86, width - 40, height - 170), pendingNavigation != null ? RemoteDirty ? "Save these server config edits before leaving, or discard your unsaved changes." : "Save this profile’s settings before leaving, or discard your unsaved edits." : confirmText, body!);
            if (pendingNavigation != null)
            {
                var third = (width - 64) / 3;
                if (Button(new Rect(20, height - 62, third, 40), "Keep editing")) pendingNavigation = null;
                if (Button(new Rect(32 + third, height - 62, third, 40), "Discard changes"))
                {
                    if (configDocument != null) foreach (var entry in configDocument.Entries) entry.Value = entry.OriginalValue;
                    DiscardRemoteEdits();
                    var action = pendingNavigation; pendingNavigation = null; action?.Invoke();
                }
                if (Button(new Rect(44 + third * 2, height - 62, third, 40), "Save & continue") && SaveConfigDocument()) { var action = pendingNavigation; pendingNavigation = null; action?.Invoke(); }
            }
            else
            {
                var half = (width - 52) / 2;
                if (Button(new Rect(20, height - 62, half, 40), "Cancel")) confirmAction = null;
                if (Button(new Rect(32 + half, height - 62, half, 40), "Confirm")) { var action = confirmAction; confirmAction = null; action?.Invoke(); }
            }
        }
        private void RunFeature(string message, Func<CancellationToken, Action> work)
        {
            if (task != null || featureTask != null || ServerHealthRequestActive || scheduled) return;
            featureCancellation = new CancellationTokenSource(); var token = featureCancellation.Token;
            progress = message; error = "";
            featureTask = Task.Run(() => work(token), token);
        }
        private void UpdateFeatures()
        {
            if (featureTask == null || !featureTask.IsCompleted) return;
            try { var action = featureTask.GetAwaiter().GetResult(); if (featureCancellation?.IsCancellationRequested != true) action(); }
            catch (OperationCanceledException) { progress = featurePage == "Server" ? "Stopped waiting. Refresh server files to confirm whether the server saved this request." : "Operation canceled. Your active profile is kept."; }
            catch (Exception failure) { error = failure.Message; setupLog.Failure(failure); log("ModGate profile tool: " + failure); }
            finally
            {
                lock (featureStages)
                {
                    foreach (var id in featureStages.Where(id => !scheduled && preparedEdit?.Restore?.StageId != id))
                        try { DownloadCache.DiscardStage(Paths.BepInExRootPath, id); } catch (Exception failure) { log("Deferred feature staging cleanup: " + failure.Message); }
                    featureStages.Clear();
                }
                featureTask = null; featureCancellation?.Dispose(); featureCancellation = null;
            }
            var navigation = afterFeature; afterFeature = null; navigation?.Invoke();
        }
        private void OpenHealth(string slot)
        {
            toolSlot = slot; featurePage = "Health"; scroll = Vector2.zero; healthReport = null;
            var root = Paths.BepInExRootPath; var state = Profiles.Load(root); var loaded = Inventory.Loaded().ToArray();
            RunFeature("Checking saved profile files and dependencies…", token => {
                var result = ProfileHealth.Verify(root, state, slot, Inventory.InspectProfilePayload, loaded, token);
                return () => { healthReport = result; repairSelection.Clear(); foreach (var issue in result.Issues.Where(i => i.CanRepair)) repairSelection.Add(issue.Id); progress = result.Healthy ? "Everything looks good." : result.Issues.Count + " profile issues found."; };
            });
        }
        private void OpenHistory(string slot)
        {
            toolSlot = slot; featurePage = "History"; scroll = Vector2.zero;
            try { snapshots = ProfileSnapshots.List(Paths.BepInExRootPath, slot); snapshotSelection = snapshots.FirstOrDefault(s => s.Working)?.Id ?? snapshots.FirstOrDefault()?.Id ?? ""; }
            catch (Exception failure) { error = failure.Message; snapshots.Clear(); }
        }
        private void DrawFeatureContent()
        {
            if (featurePage == "Creators") { DrawCreators(); return; }
            if (featurePage == "ServerHealth") { DrawServerHealth(); return; }
            if (featureTask != null) { Label(progress); Label("Your active setup stays in place while this operation runs."); return; }
            if (featurePage == "Server") { DrawServerConfigs(); if (progress.Length > 0) Label(progress); }
            else if (featurePage == "Health")
            {
                Label("Check & repair profile: " + ProfileName(toolSlot));
                Label(progress);
                if (healthReport != null)
                {
                    foreach (var issue in healthReport.Issues)
                    {
                        if (issue.CanRepair)
                        {
                            if (Toggle(repairSelection.Contains(issue.Id), issue.Message)) repairSelection.Add(issue.Id); else repairSelection.Remove(issue.Id);
                        }
                        else Label(issue.Message);
                    }
                    if (!healthReport.HasReceipt) Label("No saved file baseline is available yet. Only metadata and dependency checks can be performed.");
                }
                Label("Existing BepInEx is checked, never installed or updated by ModGate.");
                if (LayoutButton("Create support report")) { featurePage = "Support"; }
            }
            else if (featurePage == "History")
            {
                Label("Restore a working setup: " + ProfileName(toolSlot));
                Label("Restore points keep mods and applicable config files. Server requirements are checked again on join.");
                foreach (var snapshot in snapshots)
                {
                    if (Toggle(snapshotSelection == snapshot.Id, (snapshot.Working ? "Last working · " : "") + snapshot.Reason + "\n" + snapshot.CreatedUtc + " · " + snapshot.Profile.Mods.Count + " mods")) snapshotSelection = snapshot.Id;
                }
                if (snapshots.Count == 0) Label("No restore points yet. One will be saved before the next profile installation or edit.");
                if (LayoutButton("Save restore point now"))
                {
                    var root = Paths.BepInExRootPath; var slot = toolSlot;
                    RunFeature("Saving a restore point…", token => { ProfileSnapshots.Capture(root, slot, "Manual restore point", false, token); return () => { OpenHistory(slot); progress = "Restore point saved successfully."; }; });
                }
            }
            else if (featurePage == "Details") DrawModDetails();
            else if (featurePage == "Support")
            {
                Label("Create support report"); Label("Choose what to include. Passwords and config file contents are excluded.");
                reportLogs = Toggle(reportLogs, "Setup logs and errors"); reportMods = Toggle(reportMods, "Mod names, versions and sources"); reportHealth = Toggle(reportHealth, "Profile health results");
                Label("Review the saved report before sharing it in Discord.");
                if (LayoutButton("Report a bug on Discord")) OpenBugReport();
                if (progress.Length > 0) Label(progress);
            }
            if (error.Length > 0) Label(error);
        }
        private bool DrawFeaturePrimary(Rect area)
        {
            if (featurePage == "Creators") { if (featureTask != null && Button(area, "Cancel reading mods")) featureCancellation?.Cancel(); return true; }
            if (ServerHealthSelected) return true;
            if (featureTask != null) { if (Button(area, featurePage == "Server" ? "Cancel waiting for server" : "Cancel profile operation")) featureCancellation?.Cancel(); return true; }
            if (featurePage == "Server") { GUI.enabled = RemoteDirty && !Busy; if (Button(area, "Save server changes")) SaveRemoteConfig(); GUI.enabled = true; return true; }
            if (featurePage == "Health")
            {
                GUI.enabled = healthReport != null && repairSelection.Count > 0 && !Busy;
                if (Button(area, "Repair selected files")) RepairProfile(); GUI.enabled = true; return true;
            }
            if (featurePage == "History")
            {
                GUI.enabled = snapshotSelection.Length > 0 && !Busy;
                if (Button(area, "Restore & restart")) Confirm("Restore this working setup?", "Restore the selected mods and config files. A restore point keeps your current setup. Queued config edits for this profile will be replaced; their pending records are backed up.", RestoreProfile);
                GUI.enabled = true; return true;
            }
            if (featurePage == "Support") { if (Button(area, "Save support report")) SaveSupportReport(); return true; }
            if (featurePage == "Details") { if (Button(area, "Back to mods")) featurePage = ""; return true; }
            if (navigationTab == "Mod details" && featurePage.Length == 0) { if (Button(area, "Back to mods")) RequestNavigation(() => { navigationTab = "Mods"; scroll = Vector2.zero; }); return true; }
            if (manager && editingSlot != null && navigationTab == "Configs")
            {
                GUI.enabled = (ConfigDirty || configDocument?.Pending == true) && !Busy;
                if (configDocument?.Pending == true && !ConfigDirty)
                {
                    if (Button(area, "Restart to apply config changes")) Confirm("Restart Valheim?", "Your saved config edits will be applied after the game exits. The active profile stays selected.", () => {
                        try { if (!Ready()) return; if (restartGame == null) throw new InvalidOperationException("Automatic restart is unavailable. Relaunch Valheim manually."); restartGame(); progress = "Restarting Valheim to apply saved config edits."; }
                        catch (Exception failure) { error = failure.Message; }
                    });
                }
                else if (Button(area, "Save config changes")) SaveConfigDocument();
                GUI.enabled = true; return true;
            }
            return false;
        }
        private void RestoreProfile()
        {
            var root = Paths.BepInExRootPath; var slot = toolSlot; var id = snapshotSelection;
            RunFeature("Preparing restore…", token => {
                var restore = ProfileSnapshots.PrepareRestore(root, slot, id, token);
                lock (featureStages) featureStages.Add(restore.StageId);
                return () => { ScheduleCommand(new ProfileCommand { Kind = "restore", Target = slot, Restore = restore }); featurePage = ""; if (scheduled) Restart(); };
            });
        }
        private void RepairProfile()
        {
            if (healthReport == null) return;
            try
            {
                var plan = ProfileHealth.PlanRepair(healthReport, repairSelection); var root = Paths.BepInExRootPath; var state = Profiles.Load(root);
                RunFeature("Preparing selected repairs…", token => {
                    using (var source = new RepositorySource())
                    {
                        var pending = ProfileHealth.StageRepair(root, state, plan, source, Inventory.InspectProfilePayload, SetupProgress, token).GetAwaiter().GetResult();
                        var command = ProfileHealth.PrepareRepairCommand(root, state, plan, pending, token);
                        if (command.Restore != null) lock (featureStages) featureStages.Add(command.Restore.StageId);
                        return () => { preparedEdit = command; preparedIssues = plan.Groups.Select(g => "Repair: " + g.PluginPath).ToList(); editingSlot = plan.Slot; manager = true; featurePage = ""; navigationTab = "Mods"; workflow = "Review"; progress = "Repair files prepared. Review and restart to apply."; };
                    }
                });
            }
            catch (Exception failure) { error = failure.Message; setupLog.Failure(failure); }
        }
        private void SaveSupportReport()
        {
            var root = Paths.BepInExRootPath; var inventory = local.ToArray(); var report = healthReport;
            var options = new SupportReportOptions { IncludeLogs = reportLogs, IncludeMods = reportMods, IncludeHealth = reportHealth, Stage = workflow, LastError = error };
            var destination = SafePath.Under(root, "ModGate/reports/ModGate-support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            RunFeature("Creating support report…", token => { token.ThrowIfCancellationRequested(); var path = SupportReport.Create(root, destination, options, inventory, report); return () => { progress = "Support report saved: " + path; Application.OpenURL(new Uri(Path.GetDirectoryName(path)!).AbsoluteUri); }; });
        }
        private void DrawConfigEditor(string slot)
        {
            try
            {
                if (configEditorSlot != slot || configDocument != null && configDocument.Slot != slot || configDocument == null && configFile.Length > 0) RefreshConfigFiles(slot);
                Label("Config editor · " + ProfileName(slot) + (ConfigDirty ? " · Unsaved changes" : ""));
                var before = GUI.enabled;
                GUI.enabled = before && !Busy;
                try { if (LayoutButton("Reload configs")) RequestNavigation(() => ReloadProfileConfigs(slot)); }
                finally { GUI.enabled = before; }
                Label("Reload reads files from disk. Saved edits queued for restart stay queued; some mods apply settings only at startup.");
                Label("Search settings in the open file");
                configSearch = EditText(configSearch, "config-search");
                if (configFiles.Count == 0) { Label("No editable BepInEx .cfg files are associated with this profile yet. Launch its mods once to create their configs."); return; }
                if (configDocument == null) Label("Choose a config file to expand its settings.");
                foreach (var file in configFiles)
                {
                    var chosen = file;
                    GUI.enabled = before && !Busy;
                    try
                    {
                        if (LayoutButton((configDocument?.RelativePath == file ? "● " : "") + file)) RequestNavigation(() => {
                            if (configDocument?.RelativePath == chosen && configDocument.Slot == slot) { configDocument = null; configFile = ""; }
                            else { configDocument = ConfigEditor.Open(Paths.BepInExRootPath, slot, chosen); configFile = chosen; }
                            expandedConfigChoice = "";
                        });
                    }
                    finally { GUI.enabled = before; }
                    if (configDocument?.RelativePath == file && configDocument.Slot == slot) DrawConfigDocument();
                }
            }
            catch (Exception failure) { error = "Config editor: " + failure.Message; Label(error); }
        }
        private void RefreshConfigFiles(string slot)
        {
            var files = ConfigEditor.Files(Paths.BepInExRootPath, slot);
            configFiles = files; configEditorSlot = slot; configFile = ""; configDocument = null; expandedConfigChoice = "";
        }
        private void ReloadProfileConfigs(string slot)
        {
            try
            {
                var message = ReloadConfigs?.Invoke(slot) ?? "Config files refreshed; runtime reload is unavailable in this session.";
                RefreshConfigFiles(slot);
                progress = message + " Saved edits queued for restart stay queued; some mods apply settings only at startup.";
                error = "";
            }
            catch (Exception failure) { error = "Reload configs: " + failure.Message; }
        }
        private void DrawConfigDocument()
        {
            if (configDocument == null) return;
            var before = GUI.enabled;
            try
            {
                GUILayout.Space(8);
                if (configDocument.Pending) Label("Saved for next launch · Restart to apply these config changes.");
                foreach (var entry in configDocument.Entries.Where(e => configSearch.Length == 0 || (e.Section + " " + e.Key + " " + e.Description).IndexOf(configSearch, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    GUILayout.Space(10); Label(entry.Section + " / " + entry.Key); if (entry.Description.Length > 0) Label(entry.Description);
                    GUI.enabled = before && entry.Editable && !Busy;
                    if (entry.Kind.ToString() == "Boolean")
                    {
                        var wasOn = entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        var isOn = Toggle(wasOn, "Enabled"); if (wasOn != isOn) entry.Value = isOn ? "true" : "false";
                    }
                    else if (entry.Kind.ToString() == "Choice" && entry.Choices.Count > 0)
                    {
                        entry.Value = DrawChoices(entry.Value, entry.Choices, entry.Id, ref expandedConfigChoice);
                    }
                    else entry.Value = EditText(entry.Value, "config-value-" + entry.Id);
                    if (entry.HasDefault && LayoutButton("Reset " + entry.Key + " to default")) entry.Value = entry.DefaultValue;
                    GUI.enabled = before;
                    if (entry.Problem.Length > 0) Label(entry.Problem);
                }
                GUI.enabled = before && !Busy;
                if (configDocument.Entries.Any(e => e.Value != e.OriginalValue) && LayoutButton("Revert unsaved changes")) foreach (var entry in configDocument.Entries) entry.Value = entry.OriginalValue;
                GUILayout.Space(12);
            }
            finally { GUI.enabled = before; }
        }
        private bool SaveConfigDocument()
        {
            if (RemoteDirty) { SaveRemoteConfig(); return false; }
            if (configDocument == null) return true;
            try
            {
                var result = ConfigEditor.Save(Paths.BepInExRootPath, configDocument, configDocument.Entries.ToDictionary(e => e.Id, e => e.Value));
                progress = result.RestartRequired ? "Config changes saved for the next launch. Restart to apply." : "Profile config saved. A backup is kept.";
                configDocument = ConfigEditor.Open(Paths.BepInExRootPath, configDocument.Slot, configDocument.RelativePath); error = ""; return true;
            }
            catch (Exception failure) { error = failure.Message; return false; }
        }
        private string DrawChoices(string current, IList<string> choices, string id, ref string expanded)
        {
            if (LayoutButton(current + (expanded == id ? " ▲" : " ▼"))) expanded = expanded == id ? "" : id;
            if (expanded == id)
                foreach (var choice in choices)
                {
                    var selected = choice == current;
                    if (Toggle(selected, choice) && !selected) { current = choice; expanded = ""; break; }
                }
            return current;
        }
        private string EditText(string value, string control, bool multiline = false, bool password = false)
        {
            GUI.SetNextControlName(control);
            var updated = password ? GUILayout.PasswordField(value, '•') : multiline ? GUILayout.TextArea(value) : GUILayout.TextField(value);
            if (PointerClicked(GUILayoutUtility.GetLastRect())) GUI.FocusControl(control);
            return updated;
        }
        private void DiscardUnscheduledPreparation()
        {
            if (scheduled || Profiles.Pending(Paths.BepInExRootPath)) return;
            foreach (var id in new[] { prepared?.Id, preparedEdit?.Install?.Id, preparedEdit?.Restore?.StageId }.Where(id => !string.IsNullOrEmpty(id)).Distinct())
                try { DownloadCache.DiscardStage(Paths.BepInExRootPath, id!); } catch (Exception failure) { log("Deferred profile staging cleanup: " + failure.Message); }
        }
        private void OpenModDetails(ModEntry mod) { detailMod = mod; detailTab = "Overview"; navigationTab = "Mod details"; featurePage = ""; scroll = Vector2.zero; }
        private void DrawModDetails()
        {
            if (detailMod == null) return;
            var tabs = new[] { "Overview", "Dependencies", "Release notes" }; var row = Reserve(50);
            for (var i = 0; i < tabs.Length; i++) if (Action(new Rect(row.x + i * 144, row.y, 136, 36), tabs[i] + (detailTab == tabs[i] ? " •" : ""), "tab", detailTab == tabs[i])) detailTab = tabs[i];
            var mod = detailMod; Section(mod.Name, "Information for this profile. Viewing details does not change your selection.");
            if (detailTab == "Overview")
            {
                Label("Plugin GUID: " + mod.Guid); Label("DLL version: " + mod.Version);
                var loaded = local.FirstOrDefault(m => m.Guid == mod.Guid); Label("Loaded: " + (loaded?.Version ?? "Not loaded"));
                Label("Package: " + (mod.Package.Length > 0 ? mod.Package : "No exact mapping yet")); Label("Side: " + mod.Side);
                if (mod.ManifestSource != null) Label("Manifest/package version: " + mod.ManifestSource.Version);
                DrawLocalPackageText(mod, "README.md");
            }
            else if (detailTab == "Dependencies")
            {
                var current = editingSlot != null ? catalog.Servers.FirstOrDefault(s => s.Slot == editingSlot)?.ServerPolicy : policy;
                foreach (var dependency in DependencyRules.Of(mod))
                {
                    var installed = local.FirstOrDefault(m => m.Guid == dependency.Guid);
                    Label((dependency.Optional ? "Optional: " : "Required: ") + dependency.Guid + (dependency.MinimumVersion.Length > 0 ? " >= " + dependency.MinimumVersion : "") + " · Loaded " + (installed?.Version ?? "not installed"));
                }
                if (current != null) Label("Required by: " + DependencyRules.RequiredBy(current, mod.Guid));
                Label("BepInEx is assumed to be installed and is not downloaded.");
            }
            else DrawLocalPackageText(mod, "CHANGELOG.md");
        }
        private void DrawLocalPackageText(ModEntry mod, string name)
        {
            try
            {
                var localFile = !string.IsNullOrEmpty(mod.LocalFile) ? mod.LocalFile : local.FirstOrDefault(m => m.Guid == mod.Guid && PolicyEngine.VersionsEqual(m.Version, mod.Version))?.LocalFile;
                var folder = Path.GetDirectoryName(localFile ?? "");
                if (string.IsNullOrEmpty(folder)) { Label(name + " is not available locally."); return; }
                var path = SafePath.ReadSource(folder, name);
                if (!File.Exists(path) || new FileInfo(path).Length > 131072) { Label(name + " is not available locally. Check the package’s download page for release notes."); return; }
                Label(File.ReadAllText(path));
            }
            catch (Exception failure) { Label("Could not read local package notes: " + failure.Message); }
        }
    }
}
