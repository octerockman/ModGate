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
    internal sealed partial class Prompt : IDisposable
    {
        private readonly Action<string> log;
        private Policy? policy;
        private Comparison? difference;
        private IReadOnlyList<ModEntry> local = Array.Empty<ModEntry>();
        private PendingInstall? prepared;
        private SavedProfile? preparedProfile;
        private Task<PreparedProfile>? task;
        private string serverKey = "", targetSlot = "", configSource = "active";
        private ProfileCatalog catalog = new ProfileCatalog();
        private bool manager, confirmRemoval;
        private string? deleteSlot, editingSlot;
        private List<ProfileEditGroup> editGroups = new List<ProfileEditGroup>();
        private readonly HashSet<string> addedMods = new HashSet<string>(StringComparer.Ordinal);
        private ProfileCommand? preparedEdit;
        private readonly SetupLog setupLog = new SetupLog(Paths.BepInExRootPath);
        private readonly HashSet<string> selectedMods = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource? cancellation;
        private volatile string progress = "";
        private string error = "";
        private bool visible, scheduled, overrideAvailable;
        private bool ignoreTaskResult;
        private List<string> preparedIssues = new List<string>();
        private Vector2 scroll;
        private GUIStyle? title, body, frame, button, installedStyle, missingStyle, attentionStyle;
        private Texture2D? background, buttonBackground, buttonHover;
        private GUISkin? skin;
        private readonly Action returnToMenu, quitGame;
        private readonly Action? restartGame;
        private readonly List<UnityEngine.EventSystems.BaseInputModule> blockedMenus = new List<UnityEngine.EventSystems.BaseInputModule>();
        private Vector2? pointerPress, pointerClick;
        private Rect scrollBounds;
        private bool drawingContent;
        public bool IsVisible => visible;
        internal bool ReadingPromptInput { get; private set; }
        private bool ReadKeyDown(KeyCode key) { ReadingPromptInput = true; try { return ZInput.GetKeyDown(key); } finally { ReadingPromptInput = false; } }
        private bool ReadKeyUp(KeyCode key) { ReadingPromptInput = true; try { return ZInput.GetKeyUp(key); } finally { ReadingPromptInput = false; } }
        private bool ReadKey(KeyCode key) { ReadingPromptInput = true; try { return ZInput.GetKey(key); } finally { ReadingPromptInput = false; } }
        private sealed class PreparedProfile
        {
            public PendingInstall Install = null!;
            public SavedProfile Profile = null!;
            public List<string> Issues = new List<string>();
            public ProfileCommand? EditCommand;
        }

        public Prompt(Action<string> logger, Action close, Action quit, Action? restart = null) { log = logger; returnToMenu = close; quitGame = quit; restartGame = restart; }
        public void ShowError(string message)
        {
            if (task != null || scheduled) return;
            error = message; policy = null; manager = false; visible = true; deleteSlot = null; editingSlot = null; preparedEdit = null;
            setupLog.Start("Join/setup error"); setupLog.Write(message);
            prepared = null; preparedProfile = null; overrideAvailable = false; preparedIssues.Clear(); scroll = Vector2.zero;
            pointerPress = null; pointerClick = null;
            EnsureCursor();
        }
        public void Show(Policy serverPolicy, Comparison comparison, IReadOnlyList<ModEntry> inventory, string key)
        {
            if (task != null || scheduled) return;
            settingsPage = false; featurePage = ""; configDocument = null; optionalExpanded = true; navigationTab = "Mods"; search = ""; workflow = "Choose"; editingSlot = null; preparedEdit = null; setupLog.Start("Join " + serverPolicy.ServerName);
            setupLog.Write("Comparison: " + comparison.Missing.Count + " missing, " + comparison.WrongVersion.Count + " version changes, " + comparison.Extra.Count + " extra mods.");
            policy = serverPolicy; difference = comparison; local = inventory; selectedMods.Clear(); configSource = "active"; deleteSlot = null;
            ProfileConfigs.RecordActive(Paths.BepInExRootPath, inventory);
            serverKey = key; catalog = Profiles.Load(Paths.BepInExRootPath);
            var savedServer = catalog.Servers.FirstOrDefault(s => s.ServerKey == key || !string.IsNullOrWhiteSpace(serverPolicy.ServerId) && s.ServerPolicy?.ServerId == serverPolicy.ServerId);
            targetSlot = savedServer?.Slot ?? Profiles.SlotFor(catalog, key) ?? ""; manager = false;
            if (savedServer != null) { configSource = "saved:" + savedServer.Slot; serverKey = savedServer.ServerKey; }
            if (savedServer != null)
                foreach (var saved in savedServer.Mods.Where(m => m.Guid != Constants.Guid))
                    if (serverPolicy.Mods.Any(m => m.Guid == saved.Guid && m.Side == "optional") || serverPolicy.Mods.All(m => m.Guid != saved.Guid) && ProfileSelection.CanCarry(serverPolicy, saved)) selectedMods.Add(saved.Guid);
            InitializeDependencyChoices(savedServer);
            setupLog.Write("Active profile: " + catalog.Active);
            foreach (var mod in comparison.Missing) setupLog.Write("MISSING: " + mod.Name + " / " + mod.Guid + " / server requires " + mod.Version + " / package " + mod.Package);
            foreach (var mod in comparison.WrongVersion) setupLog.Write("VERSION MISMATCH: " + mod.Name + " / server requires " + mod.Version + "; loaded " + inventory.FirstOrDefault(m => m.Guid == mod.Guid)?.Version + " / package " + mod.Package);
            foreach (var mod in comparison.Extra) setupLog.Write("EXTRA: " + mod.Name + " " + mod.Version + " / " + mod.Guid);
            prepared = null; preparedProfile = null; overrideAvailable = false; preparedIssues.Clear(); error = ""; progress = ""; scroll = Vector2.zero; visible = true;
            pointerPress = null; pointerClick = null;
            EnsureCursor();
        }
        public void Update()
        {
            EnsureCursor();
            ReadPointer();
            UpdateFeatures();
            UpdateServerHealth();
            if (!Application.isBatchMode && visible && ReadKeyDown(KeyCode.Escape)) { HandleEscape(); return; }
            if (!Application.isBatchMode && ReadKeyDown(KeyCode.F6) && (ReadKey(KeyCode.LeftShift) || ReadKey(KeyCode.RightShift)) && (visible || !WorkflowBusy))
                RequestNavigation(visible ? (Action)Dismiss : OpenManager);
            if (task == null || !task.IsCompleted) return;
            if (ignoreTaskResult)
            {
                try { if (task.Status == TaskStatus.RanToCompletion) { var abandoned = task.Result.EditCommand?.Install ?? task.Result.Install; if (abandoned != null) DownloadCache.DiscardStage(Paths.BepInExRootPath, abandoned.Id); } }
                catch (Exception failure) { log("Deferred staging cleanup: " + failure.Message); }
                task = null; cancellation?.Dispose(); cancellation = null; ignoreTaskResult = false; return;
            }
            if (task.IsCanceled) { workflow = "Choose"; error = "Setup cancelled. Your installed mods have not changed."; overrideAvailable = editingSlot == null; setupLog.Write(error); }
            else if (task.IsFaulted) { workflow = "Choose"; var failure = task.Exception!.GetBaseException(); error = failure.Message; overrideAvailable = editingSlot == null; progress = "Setup stopped. Open the setup log for full details."; setupLog.Failure(failure); log("ModGate download failed: " + failure); }
            else
            {
                preparedEdit = task.Result.EditCommand;
                if (preparedEdit == null) { prepared = task.Result.Install; preparedProfile = task.Result.Profile; }
                preparedIssues = task.Result.Issues; overrideAvailable = false; workflow = "Review"; navigationTab = "Mods"; downloadFraction = 1; progress = "Ready for your review. Installed mods have not changed yet.";
                foreach (var issue in preparedIssues) setupLog.Write(issue);
                setupLog.Write("Preparation completed. Waiting for your review and restart choice.");
            }
            task = null;
            cancellation?.Dispose(); cancellation = null;
        }

        public void Draw()
        {
            if (Application.isBatchMode) return;
            // This path works even if a previous layout failed or the normal Update was interrupted.
            if (visible && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use(); HandleEscape(); return;
            }
            EnsureCursor();
            var oldSkin = GUI.skin; var oldMatrix = GUI.matrix; var oldColor = GUI.color;
            var oldBackground = GUI.backgroundColor; var oldContent = GUI.contentColor;
            var oldEnabled = GUI.enabled; var oldDepth = GUI.depth;
            try
            {
                InitializeStyles();
                GUI.skin = skin!; GUI.matrix = Matrix4x4.identity;
                GUI.color = Color.white; GUI.backgroundColor = Color.white; GUI.contentColor = Color.white;
                GUI.enabled = true; GUI.depth = -10000;
                if (!visible)
                {
                    if (ZNet.instance == null && Button(new Rect(20, Screen.height - 62, 280, 40), "Mod profiles (Shift+F6)")) OpenManager();
                    return;
                }
                if (deleteSlot != null)
                {
                    var confirmWidth = Mathf.Max(1, Mathf.Min(640, Screen.width - 24));
                    var confirmHeight = Mathf.Max(1, Mathf.Min(300, Screen.height - 24));
                    GUI.ModalWindow(728411, new Rect((Screen.width - confirmWidth) / 2, (Screen.height - confirmHeight) / 2, confirmWidth, confirmHeight), id => DrawDeleteConfirmation(confirmWidth, confirmHeight), "Delete server profile", frame!);
                    return;
                }
                if (pendingNavigation != null || confirmAction != null)
                {
                    var modalWidth = Mathf.Max(1, Mathf.Min(680, Screen.width - 24));
                    var modalHeight = Mathf.Max(1, Mathf.Min(300, Screen.height - 24));
                    GUI.ModalWindow(728412, new Rect((Screen.width - modalWidth) / 2, (Screen.height - modalHeight) / 2, modalWidth, modalHeight), id => DrawFeatureModal(modalWidth, modalHeight), "", frame!);
                    return;
                }
                var width = Mathf.Max(1, Mathf.Min(1320, Screen.width - 48));
                var height = Mathf.Max(1, Mathf.Min(960, Screen.height - 48));
                GUI.ModalWindow(728410, new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height), id => DrawWindow(width, height), "", frame!);
            }
            finally
            {
                GUI.skin = oldSkin; GUI.matrix = oldMatrix; GUI.color = oldColor;
                GUI.backgroundColor = oldBackground; GUI.contentColor = oldContent;
                GUI.enabled = oldEnabled; GUI.depth = oldDepth;
                if (Event.current.type == EventType.Repaint) { if (pointerClick.HasValue) pointerPress = null; pointerClick = null; }
            }
        }
        private void Label(string text) => GUILayout.Label(text, body!);
        private void DrawDeleteConfirmation(float width, float height)
        {
            var profile = catalog.Servers.FirstOrDefault(s => s.Slot == deleteSlot);
            GUI.Label(new Rect(16, 30, width - 32, 45), "Delete " + (profile?.Name ?? deleteSlot) + "?", title!);
            GUI.Label(new Rect(16, 82, width - 32, height - 154), "This removes this server profile's managed mods and configs on the next launch. Other profiles are kept." + (catalog.Active == deleteSlot ? " This profile is active, so your offline setup will be restored first." : " Your active profile is kept."), body!);
            var half = (width - 44) / 2;
            if (Button(new Rect(16, height - 58, half, 40), "Confirm deletion"))
            {
                var slot = deleteSlot; ScheduleCommand(new ProfileCommand { Kind = "delete", Target = slot! });
                deleteSlot = null;
            }
            if (Button(new Rect(28 + half, height - 58, half, 40), "Cancel deletion (Esc)")) deleteSlot = null;
        }
        private void DrawPrimaryAction(Rect area)
        {
            if (settingsPage) { if (Button(area, "Done")) settingsPage = false; return; }
            if (task != null)
            {
                if (Button(area, "Cancel download")) cancellation?.Cancel();
            }
            else if (!scheduled && editingSlot != null)
            {
                GUI.enabled = !Profiles.Pending(Paths.BepInExRootPath);
                if (preparedEdit != null)
                {
                    var half = (area.width - 12) / 2;
                    if (Button(new Rect(area.x, area.y, half, area.height), "Save and restart")) { ScheduleCommand(preparedEdit); if (scheduled) Restart(); }
                    if (Button(new Rect(area.x + half + 12, area.y, half, area.height), "Save on next launch")) ScheduleCommand(preparedEdit);
                }
                else
                {
                    GUI.enabled = GUI.enabled && (addedMods.Count > 0 || editGroups.Any(g => g.Enabled != g.WasEnabled));
                    if (Button(area, "Review profile changes")) PrepareEdit();
                }
                GUI.enabled = true;
            }
            else if (!scheduled && prepared != null)
            {
                var half = (area.width - 12) / 2;
                if (Button(new Rect(area.x, area.y, half, area.height), "Install and restart")) { Schedule(); if (scheduled) Restart(); }
                if (Button(new Rect(area.x + half + 12, area.y, half, area.height), "Install on next launch")) Schedule();
            }
            else if (scheduled) { if (Button(area, "Restart now")) Restart(); }
            else if (!scheduled && policy != null && !manager)
            {
                if (ConnectorMismatch())
                {
                    GUI.enabled = false; Button(area, "Update both ModGate DLLs on server and client first"); GUI.enabled = true; return;
                }
                var cached = catalog.Servers.FirstOrDefault(s => s.ServerKey == serverKey && s.Slot == targetSlot);
                if (cached != null && cached.Slot != catalog.Active && SelectedDependencyPackagesMatch(cached) && ProfileSelection.Matches(policy, Profiles.WithCurrentConnector(cached.Mods), selectedMods))
                {
                    if (Button(area, "Use saved server profile")) ScheduleCommand(new ProfileCommand { Kind = "switch", Target = cached.Slot });
                }
                else if (overrideAvailable)
                {
                    var half = (area.width - 12) / 2;
                    if (Button(new Rect(area.x, area.y, half, area.height), "Retry setup")) Prepare();
                    if (Button(new Rect(area.x + half + 12, area.y, half, area.height), "Override and continue anyway")) Prepare(true);
                }
                else if (Button(area, "Download and review")) Prepare();
            }
        }
        private void Restart()
        {
            if (!scheduled) return;
            workflow = "Install";
            try
            {
                if (restartGame == null) throw new InvalidOperationException("Automatic restart is unavailable. Quit and relaunch Valheim manually.");
                restartGame(); progress = "Closing Valheim normally. It will reopen automatically after this process exits.";
            }
            catch (Exception failure) { error = "The profile is scheduled, but restart could not start: " + failure.Message; log("ModGate restart: " + failure); }
        }
        private GUIStyle StatusStyle(ModEntry mod)
        {
            var status = ModStatuses.For(mod, local, policy);
            return status == ModStatus.Installed ? installedStyle! : status == ModStatus.MissingRequired ? missingStyle! : attentionStyle!;
        }
        private void SetSelection(string guid, bool selected)
        {
            if (selected) selectedMods.Add(guid); else selectedMods.Remove(guid);
        }
        private static string PackageLabel(ModEntry entry) => entry.Package.Length > 0 ? " (" + entry.Package + ")" : "";

        private bool ConnectorMismatch() => policy?.Mods.Any(m => m.Guid == Constants.Guid && !PolicyEngine.VersionsEqual(m.Version, Constants.Version)) == true;
        private void Prepare(bool allowIncomplete = false)
        {
            if (ServerHealthRequestActive) { error = "Wait for the server health request to finish before preparing profile changes."; return; }
            if (policy == null || difference == null) return;
            if (ConnectorMismatch()) { error = "Update both ModGate DLLs on server and client to the same release first."; setupLog.Write(error); return; }
            if (!Ready()) return;
            if (targetSlot.Length == 0) { error = "All four server slots are in use. Choose a server profile to replace."; return; }
            setupLog.Start("Prepare server profile: " + policy.ServerName + " / " + targetSlot);
            setupLog.Write("Config source: " + configSource + "; partial override: " + allowIncomplete);
            var issues = new List<string>();
            ProfileSelection plan;
            try { plan = ProfileSelection.Build(policy, local, selectedMods, allowIncomplete, issues); }
            catch (Exception failure) { error = failure.Message; overrideAvailable = true; setupLog.Failure(failure); return; }
            try { Inventory.SaveConfigFiles(); ProfileConfigs.RecordActive(Paths.BepInExRootPath, local); }
            catch (Exception failure) { error = "Could not save active configs: " + failure.Message; setupLog.Failure(failure); return; }
            error = ""; progress = "Preparing the selected profile…";
            workflow = "Check"; downloadFraction = 0; downloadCount = 0; downloadedCount = 0;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            selectedMods.UnionWith(DependencyRules.Required(policy, selectedMods));
            var serverPolicy = policy; var slot = targetSlot; var key = serverKey;
            var comparison = new Comparison(); comparison.Missing.AddRange(plan.Downloads);
            var installed = local.ToArray(); var selection = new HashSet<string>(selectedMods, StringComparer.Ordinal); var configMode = configSource;
            var dependencyPackages = new HashSet<string>(selectedDependencyPackages, StringComparer.OrdinalIgnoreCase);
            var root = Paths.BepInExRootPath;
            var priority = SourcePreferences.Load(root);
            task = Task.Run(async () =>
            {
                using (var source = new RepositorySource(origins: serverPolicy.Mods.Select(m => m.ManifestSource).Where(m => m != null).Cast<ManifestOrigin>()))
                {
                    source.TransferProgress = (received, total) => {
                        progress = "Downloading " + transferPackage + " · " + (received / 1048576d).ToString("0.0") + (total.HasValue ? " / " + (total.Value / 1048576d).ToString("0.0") : "") + " MiB";
                        if (total.HasValue && total.Value > 0 && downloadCount > 0) downloadFraction = (downloadedCount + (float)received / total.Value) / downloadCount;
                    };
                    var selectedPolicy = ProfileSelection.ForSelection(serverPolicy, selection);
                    var fallback = new ProfilePackageSource(source, setupLog.Write, issues);
                    foreach (var mod in plan.Downloads) setupLog.Write("Requested: " + mod.Name + " / " + mod.Guid + " / plugin " + mod.Version + " / package " + mod.Package);
                    PendingInstall staged = null!;
                    var effectiveSelection = new HashSet<string>(selection, StringComparer.Ordinal);
                    var dependencyMessages = new List<string>();
                    var attempts = 0;
                    try
                    {
                    while (true)
                    {
                        var packages = allowIncomplete
                            ? await PartialSetup.Resolve(selectedPolicy, comparison, fallback, token, plan.Local, issues, dependencyPackages, SuggestDependency).ConfigureAwait(false)
                            : await Downloads.Resolve(selectedPolicy, comparison, fallback, token, plan.Local, dependencyPackages, SuggestDependency, issues.Add).ConfigureAwait(false);
                        packages = packages.OrderBy(p => Array.IndexOf(priority, p.Repository)).ToList();
                        downloadCount = packages.Count; downloadedCount = 0; workflow = "Download";
                        var attempt = Guid.NewGuid().ToString("N");
                        setupLog.Write("Staging operation: " + attempt + "; packages: " + string.Join(", ", packages.Select(p => p.FullName)));
                        try
                        {
                            staged = allowIncomplete
                                ? await PartialSetup.Stage(root, packages, fallback, SetupProgress, token, issues, attempt).ConfigureAwait(false)
                                : await Downloads.Stage(root, packages, fallback, SetupProgress, token, attempt).ConfigureAwait(false);
                            var stagedPath = SafePath.Under(root, "ModGate/staging/" + staged.Id);
                            dependencyMessages.Clear();
                            effectiveSelection = ProfileSelection.WithDependencies(serverPolicy, staged.Packages.Select(PackageId.Parse).ToArray(), selectedPolicy.Mods, selection,
                                package =>
                                {
                                    try { return Inventory.InspectProfilePayload(SafePath.Under(stagedPath, "packages/" + package.Key)); }
                                    catch (Exception failure) when (allowIncomplete && !(failure is OperationCanceledException))
                                    { issues.Add(package.FullName + ": dependency inspection failed; " + failure.Message); return Array.Empty<ModEntry>(); }
                                }, dependencyMessages.Add, dependencyPackages);
                            foreach (var message in dependencyMessages) setupLog.Write(message);
                            if (allowIncomplete) Inventory.FilterPartialStage(root, serverPolicy, staged, effectiveSelection, issues);
                            if (await fallback.RepairPluginVersions(selectedPolicy, Inventory.InspectStaged(stagedPath), token).ConfigureAwait(false))
                            {
                                setupLog.Write("Retrying with a replacement for an available package containing the wrong plugin version.");
                                PartialSetup.Discard(root, attempt);
                                if (++attempts >= 128) throw new InvalidDataException("Package versions kept changing during setup.");
                                continue;
                            }
                            break;
                        }
                        catch (PackageResolutionChangedException changed)
                        {
                            setupLog.Write(changed.Message); PartialSetup.Discard(root, attempt);
                            if (++attempts >= 128) throw new InvalidDataException("Package releases kept changing during setup. Retry after the repository is stable.");
                        }
                        catch { DownloadCache.DiscardStage(root, attempt); throw; }
                    }
                    issues.AddRange(dependencyMessages);
                    if (allowIncomplete) Inventory.FilterPartialStage(root, serverPolicy, staged, effectiveSelection, issues);
                    var stage = SafePath.Under(root, "ModGate/staging/" + staged.Id);
                    var downloaded = Inventory.InspectStaged(stage);
                    var copies = plan.Local.Where(m => !downloaded.Any(d => d.Guid == m.Guid && (PolicyEngine.VersionsEqual(d.Version, m.Version) || serverPolicy.Mods.Any(w => w.Guid == m.Guid && fallback.Substituted(w.Package))))).ToArray();
                    foreach (var mod in copies) setupLog.Write("Reusing installed copy: " + mod.Name + " " + mod.Version);
                    if (allowIncomplete) PartialSetup.CopyLocal(root, copies, installed, staged, token, issues);
                    else ManualMods.Stage(root, copies, installed, staged, token);
                    if (allowIncomplete) Inventory.FilterPartialStage(root, serverPolicy, staged, effectiveSelection, issues);
                    token.ThrowIfCancellationRequested();
                    var reviewPolicy = fallback.ReviewPolicy(serverPolicy, Inventory.InspectStaged(stage));
                    var mods = Inventory.CompleteProfileProposal(root, reviewPolicy, staged, allowIncomplete, issues.Add);
                    if (!allowIncomplete) ProfileSelection.ValidatePrepared(reviewPolicy, mods, effectiveSelection);
                    foreach (var actual in mods) setupLog.Write("Prepared plugin: " + actual.Name + " / " + actual.Guid + " / " + actual.Version);
                    foreach (var wanted in serverPolicy.Mods.Where(m => PolicyEngine.Required(m) || selection.Contains(m.Guid)))
                    {
                        var actual = mods.FirstOrDefault(m => m.Guid == wanted.Guid);
                        if (actual == null) issues.Add(wanted.Name + " " + wanted.Version + ": missing from this profile.");
                        else if (!PolicyEngine.AcceptsVersion(serverPolicy, wanted, actual.Version)) issues.Add(wanted.Name + ": profile has " + actual.Version + "; server requests " + wanted.Version + ".");
                    }
                    foreach (var extra in installed.Where(m => selection.Contains(m.Guid) && mods.All(p => p.Guid != m.Guid))) issues.Add(extra.Name + ": selected installed copy is missing from this profile.");
                    List<ConfigBinding> bindings;
                    try { setupLog.Write("Preparing per-profile configs from " + configMode + "."); bindings = ProfileConfigs.Stage(root, mods, configMode, staged); }
                    catch (Exception failure) when (allowIncomplete && !(failure is OperationCanceledException))
                    {
                        issues.Add("Config copy failed; this profile will start with defaults: " + failure.Message);
                        bindings = ProfileConfigs.Stage(root, mods, "fresh", staged);
                    }
                    token.ThrowIfCancellationRequested();
                    return new PreparedProfile { Install = staged, Issues = issues.Distinct().ToList(), Profile = new SavedProfile { Incomplete = allowIncomplete || !PolicyEngine.Compare(serverPolicy, mods).Matches, ServerPolicy = ServerSnapshot(serverPolicy), Slot = slot, ServerKey = key, Name = serverPolicy.ServerName, Mods = mods, Packages = staged.Packages, ConfigBindings = bindings } };
                    }
                    catch { if (staged != null) DownloadCache.DiscardStage(root, staged.Id); throw; }
                }
            }, token);
        }
        private void Schedule()
        {
            if (prepared == null || preparedProfile == null) return;
            ScheduleCommand(new ProfileCommand { Kind = "install", Target = preparedProfile.Slot, Install = prepared, Profile = preparedProfile, ReplaceConfig = !configSource.StartsWith("saved:", StringComparison.Ordinal) });
        }
        private bool Ready()
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ValheimModGate.Preloader" && a.GetName().Version?.ToString(3) == Constants.Version)) return true;
            error = "Install the matching " + Constants.Version + " ValheimModGate.Preloader.dll in BepInEx/patchers and restart Valheim first.";
            return false;
        }
        private void ScheduleCommand(ProfileCommand command)
        {
            if (ServerHealthRequestActive) { error = "Wait for the server health request to finish before scheduling profile changes."; return; }
            if (!Ready()) return;
            try
            {
                Profiles.Schedule(Paths.BepInExRootPath, command);
                setupLog.Write("Scheduled " + command.Kind + " for profile " + command.Target + ". Applies on restart.");
                log("Scheduled " + command.Kind + " profile change for the next launch.");
                scheduled = true; workflow = "Restart";
                progress = command.Kind == "delete" ? "Server profile deletion scheduled. Restart to apply; an active deleted profile will return to offline." : command.Kind == "removeAll" ? "Removal scheduled. Downloaded server profiles will be cleared and the offline profile restored." : "Profile change scheduled. Your current profile will be saved before switching.";
            }
            catch (Exception exception) { error = exception.Message; setupLog.Failure(exception); }
        }
        private void OpenManager()
        {
            try
            {
                DiscardUnscheduledPreparation();
                local = Inventory.Loaded();
                ProfileConfigs.RecordActive(Paths.BepInExRootPath, local);
                catalog = Profiles.Load(Paths.BepInExRootPath);
                manager = true; settingsPage = false; featurePage = ""; configDocument = null; navigationTab = "Mods"; editingSlot = null; preparedEdit = null; prepared = null; preparedProfile = null; confirmRemoval = false; deleteSlot = null; visible = true; error = "";
                EnsureCursor();
                if (!scheduled) progress = "";
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
        private string ProfileName(string slot) => slot == "offline" ? "Offline" : catalog.Servers.FirstOrDefault(s => s.Slot == slot)?.Name ?? slot;
        private static Policy ServerSnapshot(Policy source) { var copy = Json.CloneLocal(source); copy.Nonce = ""; return copy; }
        private void SetupProgress(string message) { if (message.StartsWith("Downloading ", StringComparison.Ordinal)) transferPackage = message.Substring(12); lock (transferGate) { transferSteps.Add(message); if (transferSteps.Count > 80) transferSteps.RemoveAt(0); } progress = message; setupLog.Write(message); if (message.StartsWith("Prepared ", StringComparison.Ordinal)) { downloadedCount++; if (downloadCount > 0) downloadFraction = (float)downloadedCount / downloadCount; } }
        private void OpenSetupLog()
        {
            try
            {
                if (!File.Exists(setupLog.Path)) setupLog.Start("Profile setup details");
                if (!File.Exists(setupLog.Path)) { error = "Could not create the setup log: " + setupLog.LastError; return; }
                Application.OpenURL(new Uri(setupLog.Path).AbsoluteUri);
            }
            catch (Exception failure) { error = "Could not open the setup log: " + failure.Message; }
        }
        private void OpenEditor(string slot)
        {
            try
            {
                DiscardUnscheduledPreparation();
                catalog = Profiles.Load(Paths.BepInExRootPath);
                editingSlot = slot; editGroups.Clear();
                editGroups = Inventory.ProfileGroups(Paths.BepInExRootPath, catalog, slot);
                editingSlot = slot; featurePage = ""; configDocument = null; navigationTab = "Mods"; settingsPage = false; search = ""; preparedEdit = null; prepared = null; preparedProfile = null; addedMods.Clear();
                error = ""; progress = ""; scroll = Vector2.zero;
                setupLog.Start("View/edit profile: " + ProfileName(slot) + " / " + slot);
                foreach (var group in editGroups) setupLog.Write((group.Enabled ? "Enabled: " : "Disabled: ") + string.Join(", ", group.Mods.Select(m => m.Name + " " + m.Version)) + " / " + group.PluginPath);
            }
            catch (Exception failure) { error = "Could not open this profile: " + failure.Message; setupLog.Failure(failure); }
        }
        private void CloseEditor()
        {
            DiscardUnscheduledPreparation();
            if (task != null) ignoreTaskResult = true;
            if (!scheduled && preparedEdit?.Install != null)
                try { DownloadCache.DiscardStage(Paths.BepInExRootPath, preparedEdit.Install.Id); } catch (Exception failure) { log("Deferred staging cleanup: " + failure.Message); }
            cancellation?.Cancel(); editingSlot = null; preparedEdit = null; editGroups.Clear(); addedMods.Clear();
            error = ""; progress = ""; scroll = Vector2.zero;
        }
        private void DrawEditor()
        {
            var slot = editingSlot!;
            if (preparedEdit?.Kind == "restore")
            {
                Label("Repair files are ready for review.");
                foreach (var issue in preparedIssues) Label(issue);
                Label("The selected packages will be restored on restart. Other mod groups and config files are kept.");
                if (LayoutButton("Back to repair selection")) { DiscardUnscheduledPreparation(); preparedEdit = null; OpenHealth(slot); }
                return;
            }
            Notice("Your mods stay with this profile", "Enable or disable mods below. Bundled mods switch together. Changes apply after restart.", "green");
            if (Profiles.Pending(Paths.BepInExRootPath)) Notice("Restart required", "A profile change is already saved. Restart before editing again.", "yellow");
            var connectorRow = Reserve(52);
            Check(new Rect(connectorRow.x + 3, connectorRow.y + 14, 21, 21), true, false, "Valheim ModGate " + Constants.Version + " (always enabled)");
            LabelAt(new Rect(connectorRow.x + 34, connectorRow.y + 8, Mathf.Max(1, connectorRow.width - 152), 36), "ModGate " + Constants.Version, "body", "muted");
            Badge(new Rect(connectorRow.x + connectorRow.width - 108, connectorRow.y + 14, 108, 24), "Always on", "muted");
            Divider(new Rect(connectorRow.x, connectorRow.y + 51, connectorRow.width, 1));
            foreach (var group in editGroups.Where(EditorVisible))
            {
                GUI.enabled = task == null && preparedEdit == null && !Profiles.Pending(Paths.BepInExRootPath);
                DrawSavedModRow(group);
            }
            GUI.enabled = true;
            if (editGroups.Count == 0) Label("This profile has no editable mod folders yet.");
            Label("Add mods from the current installation:");
            var available = local.Where(m => m.Guid != Constants.Guid && editGroups.All(g => g.Mods.All(x => x.Guid != m.Guid))).ToArray();
            foreach (var mod in available)
            {
                var allowed = ProfileEditing.CanAdd(catalog, slot, mod);
                GUI.enabled = allowed && task == null && preparedEdit == null && !Profiles.Pending(Paths.BepInExRootPath);
                if (Toggle(addedMods.Contains(mod.Guid), "Add installed: " + mod.Name + " " + mod.Version + (allowed ? "" : " (not allowed by server)"))) addedMods.Add(mod.Guid); else addedMods.Remove(mod.Guid);
            }
            GUI.enabled = true;
            if (available.Length == 0) Label("No additional installed mods are available. Install a new mod into your current setup first to add it here.");
            if (preparedEdit != null)
            {
                Label("Review these changes before saving:");
                foreach (var issue in preparedIssues) Label("• " + issue);
                Label("Other profiles are kept. Existing profile configs are preserved. Added mods copy their applicable current configs when the target has none.");
                if (LayoutButton("Change selection")) { if (preparedEdit.Install != null) DownloadCache.DiscardStage(Paths.BepInExRootPath, preparedEdit.Install.Id); preparedEdit = null; preparedIssues.Clear(); progress = ""; }
            }
        }
        private void PrepareEdit()
        {
            if (ServerHealthRequestActive) { error = "Wait for the server health request to finish before preparing profile changes."; return; }
            if (editingSlot == null || !Ready()) return;
            try { Inventory.SaveConfigFiles(); ProfileConfigs.RecordActive(Paths.BepInExRootPath, local); }
            catch (Exception failure) { error = failure.Message; setupLog.Failure(failure); return; }
            var root = Paths.BepInExRootPath; var slot = editingSlot; var snapshot = Json.CloneLocal(catalog); var groups = Json.CloneLocal(editGroups);
            var installed = local.ToArray(); var wanted = installed.Where(m => addedMods.Contains(m.Guid)).ToArray();
            setupLog.Start("Prepare profile edit: " + ProfileName(slot)); error = ""; progress = "Preparing profile changes…";
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            task = Task.Run(async () =>
            {
                PendingInstall? staged = null; var additions = new List<ProfileEditGroup>(); var bindings = new List<ConfigBinding>();
                var issues = groups.Where(g => g.Enabled != g.WasEnabled).Select(g => (g.Enabled ? "Enable: " : "Disable: ") + string.Join(", ", g.Mods.Select(m => m.Name + " " + m.Version))).ToList();
                if (wanted.Length > 0)
                {
                    using (var source = new RepositorySource()) staged = await Downloads.Stage(root, Array.Empty<PackageId>(), source, SetupProgress, token).ConfigureAwait(false);
                    foreach (var mod in wanted) setupLog.Write("Copy installed mod: " + mod.Name + " " + mod.Version);
                    ManualMods.Stage(root, wanted, installed, staged, token);
                    var stage = SafePath.Under(root, "ModGate/staging/" + staged.Id);
                    foreach (var key in staged.LocalPackages)
                    {
                        var mods = Inventory.InspectProfilePayload(SafePath.Under(stage, "packages/" + key));
                        if (mods.Any(m => wanted.All(w => w.Guid != m.Guid)) || wanted.Any(w => !ProfileEditing.CanAdd(snapshot, slot, w))) throw new InvalidDataException("An added folder contains unchecked or prohibited plugins. Keep bundled selections together.");
                        additions.Add(new ProfileEditGroup { PluginPath = "ValheimModGate.Active/" + key, Directory = true, Enabled = true, Mods = mods });
                    }
                    var copied = additions.SelectMany(g => g.Mods).ToArray();
                    if (wanted.Any(w => copied.All(m => m.Guid != w.Guid || !PolicyEngine.VersionsEqual(m.Version, w.Version)))) throw new InvalidDataException("An installed mod changed while preparing the edit. Restart and try again.");
                    bindings = ProfileConfigs.Stage(root, copied, "active", staged);
                    issues.AddRange(copied.Select(m => "Add installed: " + m.Name + " " + m.Version));
                }
                token.ThrowIfCancellationRequested();
                var command = ProfileEditing.Prepare(root, snapshot, slot, groups, staged, additions, bindings, token);
                foreach (var issue in issues) setupLog.Write(issue);
                return new PreparedProfile { EditCommand = command, Issues = issues };
            }, token);
        }

        public void EnsureCursor()
        {
            if (!visible || Application.isBatchMode) return;
            var menu = UnityEngine.EventSystems.EventSystem.current?.currentInputModule;
            if (menu != null && menu.enabled && !blockedMenus.Contains(menu)) { blockedMenus.Add(menu); menu.enabled = false; }
            ReleaseCursor();
        }
        public void ReleaseCursor()
        {
            if (Application.isBatchMode) return;
            Cursor.lockState = CursorLockMode.None;
            if (ZInput.instance != null) ZCursor.Show();
            Cursor.visible = true;
        }
        private void Dismiss()
        {
            StopServerHealthView();
            featureCancellation?.Cancel();
            DiscardUnscheduledPreparation();
            if (task != null) ignoreTaskResult = true;
            if (!scheduled && prepared != null)
            {
                try { DownloadCache.DiscardStage(Paths.BepInExRootPath, prepared.Id); }
                catch (Exception failure) { log("Deferred staging cleanup: " + failure.Message); }
            }
            cancellation?.Cancel(); visible = false; manager = false; confirmRemoval = false; deleteSlot = null;
            prepared = null; preparedProfile = null; editingSlot = null; preparedEdit = null;
            pointerPress = null; pointerClick = null; RestoreMenuInput();
            GUIUtility.hotControl = 0; GUIUtility.keyboardControl = 0;
            // The game controls capture again on its next update; do not restore a stale loading-screen lock.
            ReleaseCursor(); returnToMenu();
        }
        private void RestoreMenuInput()
        {
            foreach (var menu in blockedMenus) if (menu != null) menu.enabled = true;
            blockedMenus.Clear();
        }
        private void ReadPointer()
        {
            if (Application.isBatchMode || ZInput.instance == null || UnityEngine.InputSystem.Mouse.current == null) return;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var position = mouse.position.ReadValue();
            var point = new Vector2(position.x, Screen.height - position.y);
            if (ReadKeyDown(KeyCode.Mouse0)) pointerPress = point;
            if (ReadKeyUp(KeyCode.Mouse0)) pointerClick = pointerPress.HasValue ? point : (Vector2?)null;
            if (visible && scrollBounds.Contains(point))
            {
                var wheel = mouse.scroll.ReadValue().y;
                if (wheel != 0) scroll = new Vector2(scroll.x, Mathf.Max(0, scroll.y - Mathf.Clamp(wheel, -1, 1) * 50));
            }
        }
        private bool PointerClicked(Rect local)
        {
            if (Event.current.type != EventType.Repaint || !GUI.enabled || !pointerClick.HasValue || !pointerPress.HasValue) return false;
            var screen = GUIUtility.GUIToScreenRect(local);
            if (!screen.Contains(pointerClick.Value) || !screen.Contains(pointerPress.Value)) return false;
            if (drawingContent && (!scrollBounds.Contains(pointerClick.Value) || !scrollBounds.Contains(pointerPress.Value))) return false;
            pointerClick = null; pointerPress = null; return true;
        }
        private bool Button(Rect area, string label)
        {
            return Action(area, label, ReferenceEquals(button, primaryStyle) && primaryStyle != null ? "primary" : "secondary");
        }
        internal bool PasswordRememberToggle(Rect area, bool selected)
        {
            InitializeStyles();
            InitializeVisuals();
            var value = Check(new Rect(area.x, area.y + (area.height - 21) / 2, 21, 21), selected, GUI.enabled, "Remember password for this server (Windows encrypted)");
            LabelAt(new Rect(area.x + 32, area.y, area.width - 32, area.height), "Remember password for this server", "body");
            return value;
        }
        private bool LayoutButton(string label, params GUILayoutOption[] options)
        {
            var area = options.Length == 0 ? GUILayoutUtility.GetRect(1, 38, GUILayout.ExpandWidth(true)) : GUILayoutUtility.GetRect(1, 38, options);
            return Action(area, label);
        }
        private bool Toggle(bool selected, string label, GUIStyle? style = null, params GUILayoutOption[] options)
        {
            var area = options.Length == 0 ? GUILayoutUtility.GetRect(1, 34, GUILayout.ExpandWidth(true)) : GUILayoutUtility.GetRect(1, 34, options);
            var value = Check(new Rect(area.x, area.y + 6, 21, 21), selected, GUI.enabled, label);
            var tone = ReferenceEquals(style, installedStyle) ? "green" : ReferenceEquals(style, missingStyle) ? "red" : ReferenceEquals(style, attentionStyle) ? "yellow" : "ink";
            LabelAt(new Rect(area.x + 32, area.y, Mathf.Max(1, area.width - 32), area.height), label, "body", tone);
            return value;
        }
        private void InitializeStyles()
        {
            if (skin != null) return;
            background = Solid(new Color(.067f, .09f, .09f, 1));
            progressBar = Solid(new Color(0.35f, 0.9f, 0.45f, 1));
            buttonBackground = Solid(new Color(.102f, .133f, .129f, 1));
            buttonHover = Solid(new Color(.15f, .19f, .17f, 1));
            skin = UnityEngine.Object.Instantiate(GUI.skin);
            title = new GUIStyle(skin.label) { fontSize = 24, wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 6), contentOffset = Vector2.zero };
            body = new GUIStyle(skin.label) { fontSize = 14, wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 6), contentOffset = Vector2.zero };
            title.normal.textColor = Ink; body.normal.textColor = Ink;
            button = new GUIStyle(skin.button) { fontSize = 15, wordWrap = true, richText = false, border = new RectOffset(0, 0, 0, 0) };
            button.normal.background = buttonBackground; button.normal.textColor = Color.white;
            button.hover.background = buttonHover; button.hover.textColor = Color.white;
            button.active.background = buttonHover; button.active.textColor = Color.white;
            button.focused.background = buttonHover; button.focused.textColor = Color.white;
            frame = new GUIStyle(skin.window) { border = new RectOffset(0, 0, 0, 0) };
            frame.normal.background = background; frame.normal.textColor = Color.white;
            frame.onNormal.background = background; frame.onNormal.textColor = Color.white;
            skin.toggle.fontSize = 15; skin.toggle.wordWrap = true; skin.toggle.richText = false;
            installedStyle = StatusToggle(Green);
            missingStyle = StatusToggle(Red);
            attentionStyle = StatusToggle(Yellow);
            skin.button = button; skin.label = body;
        }
        private GUIStyle StatusToggle(Color color)
        {
            var style = new GUIStyle(skin!.toggle);
            style.normal.textColor = style.onNormal.textColor = color;
            style.hover.textColor = style.onHover.textColor = color;
            style.active.textColor = style.onActive.textColor = color;
            style.focused.textColor = style.onFocused.textColor = color;
            return style;
        }
        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color); texture.Apply();
            return texture;
        }
        public void Dispose()
        {
            ForgetServerHealth();
            featureCancellation?.Cancel();
            cancellation?.Cancel(); visible = false; RestoreMenuInput(); ReleaseCursor();
            if (skin != null) UnityEngine.Object.Destroy(skin);
            if (background != null) UnityEngine.Object.Destroy(background);
            if (buttonBackground != null) UnityEngine.Object.Destroy(buttonBackground);
            if (buttonHover != null) UnityEngine.Object.Destroy(buttonHover);
            if (sidebarBackground != null) UnityEngine.Object.Destroy(sidebarBackground);
            if (panelBackground != null) UnityEngine.Object.Destroy(panelBackground);
            if (goldBackground != null) UnityEngine.Object.Destroy(goldBackground);
            if (separatorBackground != null) UnityEngine.Object.Destroy(separatorBackground);
            if (progressBar != null) UnityEngine.Object.Destroy(progressBar);
            foreach (var texture in visualTextures) UnityEngine.Object.Destroy(texture);
            visualTextures.Clear();
            if (displayFont != null) UnityEngine.Object.Destroy(displayFont);
        }
    }
}
