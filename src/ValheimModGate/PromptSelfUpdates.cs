using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        private Task<Action>? selfUpdateTask;
        private CancellationTokenSource? selfUpdateCancellation;
        private SelfUpdateRelease? selfUpdateRelease;
        private SelfUpdatePlan? selfUpdatePrepared;
        private string selfUpdateRequired = "", selfUpdateServer = "";
        private bool selfUpdateCheckQueued;
        private volatile string selfUpdateStatus = "";
        internal Func<string?, bool, CancellationToken, Task<SelfUpdateRelease>>? FindSelfUpdate = null;
        private bool SelfUpdateBusy => selfUpdateTask != null;
        private bool SelfUpdateQueued => File.Exists(Path.Combine(Paths.BepInExRootPath, SelfUpdateInstaller.Active));

        internal void StartSelfUpdateCheckOnLaunch()
        {
            try
            {
                var prefs = SelfUpdateSource.Preferences(Paths.BepInExRootPath);
                if (!prefs.CheckOnLaunch || SelfUpdateQueued) return;
                if (DateTime.TryParse(prefs.LastCheckedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromHours(12)) return;
                CheckSelfUpdate();
            }
            catch (Exception failure) { selfUpdateStatus = "Update check: " + failure.Message; log(selfUpdateStatus); }
        }
        internal void ShowConnectorUpdate(string server, string version)
        {
            if (task != null || scheduled) { ShowError("This server requires ModGate " + version + ". Finish the current operation, then check ModGate updates in Settings."); return; }
            selfUpdateRequired = SelfUpdateSource.NormalizeVersion(version);
            selfUpdateServer = server ?? "Valheim server";
            ShowError("The server requires ModGate " + selfUpdateRequired + "; this client has " + Constants.Version + ".");
            error = ""; settingsPage = true; featurePage = ""; manager = true; editingSlot = null;
            selfUpdateRelease = null; selfUpdatePrepared = null;
            if (SelfUpdateBusy) { selfUpdateCheckQueued = true; selfUpdateCancellation?.Cancel(); }
            else CheckSelfUpdate();
        }
        private void RunSelfUpdate(Func<CancellationToken, Task<Action>> work, string message)
        {
            if (SelfUpdateBusy || SelfUpdateQueued || task != null || featureTask != null || scheduled || Profiles.Pending(Paths.BepInExRootPath)) return;
            selfUpdateStatus = message; selfUpdateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = selfUpdateCancellation.Token;
            selfUpdateTask = Task.Run(async () => { var action = await work(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return action; }, token);
        }
        private void CheckSelfUpdate()
        {
            var prefs = SelfUpdateSource.Preferences(Paths.BepInExRootPath);
            var required = selfUpdateRequired.Length == 0 ? null : selfUpdateRequired;
            RunSelfUpdate(async token =>
            {
                var release = await (FindSelfUpdate?.Invoke(required, prefs.IncludePrereleases, token) ?? new SelfUpdateSource().Find(required, prefs.IncludePrereleases, token)).ConfigureAwait(false);
                return () => { selfUpdateRelease = release; selfUpdatePrepared = null; selfUpdateStatus = "Official release found: ModGate " + release.Version + (release.Prerelease ? " · Early release" : "") + ". Download and review before installing."; };
            }, required == null ? "Checking official ModGate GitHub releases…" : "Finding the server’s exact ModGate " + required + " release…");
            if (SelfUpdateBusy) { prefs.LastCheckedUtc = DateTime.UtcNow.ToString("O"); SelfUpdateSource.SavePreferences(Paths.BepInExRootPath, prefs); }
        }
        private void UpdateSelfUpdates()
        {
            if (selfUpdateTask == null || !selfUpdateTask.IsCompleted) return;
            try
            {
                if (selfUpdateTask.IsCanceled) selfUpdateStatus = "ModGate update cancelled. Installed DLLs are unchanged.";
                else if (selfUpdateTask.IsFaulted) selfUpdateStatus = "ModGate update stopped: " + selfUpdateTask.Exception!.GetBaseException().Message;
                else selfUpdateTask.Result();
                log(selfUpdateStatus); setupLog.Write(selfUpdateStatus);
            }
            catch (Exception failure) { selfUpdateStatus = "ModGate update stopped: " + failure.Message; log(selfUpdateStatus); }
            finally { selfUpdateTask = null; selfUpdateCancellation?.Dispose(); selfUpdateCancellation = null; }
            if (selfUpdateCheckQueued) { selfUpdateCheckQueued = false; selfUpdateRelease = null; selfUpdatePrepared = null; CheckSelfUpdate(); }
        }
        private void DownloadSelfUpdate()
        {
            var release = selfUpdateRelease ?? throw new InvalidOperationException("Check for a release first.");
            var root = Paths.BepInExRootPath;
            var locations = SelfUpdateRuntime.Locations();
            RunSelfUpdate(async token =>
            {
                var plan = await new SelfUpdateSource().Prepare(root, release, locations.plugin, locations.preloader, token).ConfigureAwait(false);
                return () => { selfUpdatePrepared = plan; selfUpdateStatus = "Both ModGate " + plan.Version + " DLLs are verified and ready. Installed DLLs are unchanged."; };
            }, "Downloading and verifying ModGate " + release.Version + "…");
        }
        private void InstallSelfUpdate(bool restart)
        {
            var plan = selfUpdatePrepared ?? throw new InvalidOperationException("Download and review the update first.");
            SelfUpdateInstaller.Arm(Paths.BepInExRootPath, plan, restart);
            selfUpdateStatus = "ModGate " + plan.Version + " is queued. Both DLLs will update after this process closes. Configs and profiles are kept.";
            log(selfUpdateStatus); setupLog.Write(selfUpdateStatus);
            if (restart) { selfUpdateStatus += " Restarting…"; RestoreMenuInput(); ReleaseCursor(); quitGame(); }
        }
        private void DrawSelfUpdates()
        {
            Label("ModGate updates · Installed " + Constants.Version);
            Label("Official source: github.com/octerockman/ModGate. Only the two connector DLLs are replaced; profiles, configs and BepInEx are kept.");
            try
            {
                var prefs = SelfUpdateSource.Preferences(Paths.BepInExRootPath);
                var before = GUI.enabled;
                GUI.enabled = before && !SelfUpdateBusy && !SelfUpdateQueued;
                var check = Toggle(prefs.CheckOnLaunch, "Check official ModGate updates on launch (at most every 12 hours)");
                var early = Toggle(prefs.IncludePrereleases, "Include official early releases");
                if (check != prefs.CheckOnLaunch || early != prefs.IncludePrereleases)
                {
                    prefs.CheckOnLaunch = check; prefs.IncludePrereleases = early; SelfUpdateSource.SavePreferences(Paths.BepInExRootPath, prefs);
                    if (selfUpdateRelease?.Prerelease == true && !early) { selfUpdateRelease = null; selfUpdatePrepared = null; }
                }
                if (selfUpdateRequired.Length > 0) Label(selfUpdateServer + " requires ModGate " + selfUpdateRequired + ". Update the connector, restart, then join again to prepare that server’s mods.");
                if (LayoutButton(selfUpdateRequired.Length > 0 ? "Find server’s required ModGate version" : "Check ModGate updates now")) CheckSelfUpdate();
                if (selfUpdateRequired.Length > 0 && LayoutButton("Check newest official release instead")) { selfUpdateRequired = selfUpdateServer = ""; selfUpdateRelease = null; selfUpdatePrepared = null; CheckSelfUpdate(); }
                var release = selfUpdateRelease;
                var available = release != null && (selfUpdateRequired.Length > 0 ? !PolicyEngine.VersionsEqual(release.Version, Constants.Version) : System.Version.Parse(release.Version) > System.Version.Parse(Constants.Version));
                if (release != null)
                {
                    Label("Release: " + release.Version + (release.Prerelease ? " (early release)" : "") + " · " + release.Asset);
                    if (!available) Label("No newer release is available in this update channel.");
                    if (available && System.Version.Parse(release.Version) < System.Version.Parse(Constants.Version)) Label("This server needs an older connector. Changing to it can remove newer features, including self-updating. Only continue for a server you trust.");
                }
                GUI.enabled = before && !WorkflowBusy && prepared == null && preparedEdit == null;
                if (available && selfUpdatePrepared == null && LayoutButton("Download ModGate " + release!.Version + " for review")) DownloadSelfUpdate();
                if (selfUpdatePrepared != null && !SelfUpdateQueued)
                {
                    Label("Ready: plugin and preloader " + selfUpdatePrepared.Version + ". SHA-256 and assembly versions verified. A backup of both existing DLLs is retained.");
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT && !Application.isBatchMode && LayoutButton("Install ModGate " + selfUpdatePrepared.Version + " & restart")) InstallSelfUpdate(true);
                    if (LayoutButton("Install ModGate when Valheim closes")) InstallSelfUpdate(false);
                }
                GUI.enabled = before;
                if (selfUpdateStatus.Length > 0) Label(selfUpdateStatus);
                if (SelfUpdateBusy && LayoutButton("Cancel ModGate update operation")) selfUpdateCancellation?.Cancel();
                if (SelfUpdateQueued)
                {
                    Label("A connector update is queued for normal shutdown. Wait for it to finish before starting Valheim again.");
                    if (LayoutButton("Cancel queued ModGate update")) { var plan = Json.Read<SelfUpdatePlan>(File.ReadAllText(SafePath.Under(Paths.BepInExRootPath, SelfUpdateInstaller.Active))); SelfUpdateInstaller.Cancel(Paths.BepInExRootPath, plan.Id); selfUpdateStatus = "Queued update cancelled; installed DLLs were kept."; }
                }
                var report = SafePath.Under(Paths.BepInExRootPath, "ModGate/self-update-error.txt");
                if (File.Exists(report)) Label("Last self-update error: " + File.ReadAllText(report));
            }
            catch (Exception failure) { GUI.enabled = true; selfUpdateStatus = "ModGate update: " + failure.Message; Label(selfUpdateStatus); }
            if (LayoutButton("View official ModGate releases ↗")) Application.OpenURL(SelfUpdateSource.Repository + "/releases");
            GUILayout.Space(18);
        }
    }
}
