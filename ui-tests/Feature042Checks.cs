using System.Reflection;
using ModGate;
using UnityEngine;

internal static class Feature042Checks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static void Set(Prompt prompt, string name, object? value) => typeof(Prompt).GetField(name, Private)!.SetValue(prompt, value);
    private static object? Call(Prompt prompt, string name, params object[] values) => typeof(Prompt).GetMethod(name, Private)!.Invoke(prompt, values);
    private static void Finish(Prompt prompt, Action<bool> check)
    {
        var work = (Task<Action>?)Get(prompt, "featureTask");
        check(work != null && SpinWait.SpinUntil(() => work.IsCompleted, 3000)); prompt.Update();
    }
    private static Prompt Local()
    {
        Screen.width = 1280; Screen.height = 1000; ZNet.instance = null;
        var folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "config"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Alpha.cfg"), "[General]\n# Setting type: String\nAlphaValue = original-alpha\n");
        File.WriteAllText(Path.Combine(folder, "Beta.cfg"), "[General]\n# Setting type: String\nBetaValue = original-beta\n");
        var prompt = new Prompt(_ => { }, () => { }, () => { });
        ZInput.ManagerShortcut(); prompt.Update(); GUI.Click = "Open profile (offline)"; prompt.Draw(); GUI.Click = "Configs"; prompt.Draw();
        return prompt;
    }
    private static AdminConfigDocument Document(string path) => new()
    {
        Path = path, Kind = "cfg", Hash = new string('a', 64), Pending = true,
        Entries = new() { new AdminConfigEntry { Id = "1", Section = "General", Key = path == "Alpha.cfg" ? "AlphaValue" : "BetaValue", Value = "original", Editable = true } }
    };
    private static Prompt Remote()
    {
        Screen.width = 1280; Screen.height = 1000;
        var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture");
        prompt.ServerConfigAvailable = () => true;
        Call(prompt, "OpenServerConfigs");
        Set(prompt, "remoteFiles", new List<AdminConfigFileInfo> { new() { Path = "Alpha.cfg", Kind = "cfg", Pending = true }, new() { Path = "Beta.cfg", Kind = "cfg" } });
        return prompt;
    }
    private static int Index(string kind, string text) => DrawCapture.Commands.FindIndex(c => c.Kind == kind && c.Text == text);
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Prompt input exemption is scoped to its own reads and resets even when ZInput throws", () =>
        {
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            foreach (var reader in new[] { "ReadKey", "ReadKeyDown", "ReadKeyUp" })
            {
                var sawOwnRead = false;
                ZInput.BeforeRead = (_, _) => { check(prompt.ReadingPromptInput); sawOwnRead = true; throw new InvalidOperationException("simulated input failure"); };
                try { Call(prompt, reader, KeyCode.LeftShift); throw new Exception("Expected ZInput failure."); }
                catch (TargetInvocationException failure) when (failure.InnerException is InvalidOperationException) { }
                finally { ZInput.BeforeRead = null; }
                check(sawOwnRead && !prompt.ReadingPromptInput);
                ZInput.BeforeRead = (_, _) => check(prompt.ReadingPromptInput);
                Call(prompt, reader, KeyCode.LeftShift); check(!prompt.ReadingPromptInput);
            }
            ZInput.BeforeRead = null;
        });
        test("plain F6 and the former F8 shortcut do not open ModGate", () =>
        {
            ZNet.instance = null; using var prompt = new Prompt(_ => { }, () => { }, () => { });
            ZInput.Pressed.Add(KeyCode.F6); prompt.Update(); check(!prompt.IsVisible);
            ZInput.Pressed.Add(KeyCode.F8); prompt.Update(); check(!prompt.IsVisible);
            ZInput.Held.Add(KeyCode.LeftShift); ZInput.Pressed.Add(KeyCode.F8); prompt.Update(); check(!prompt.IsVisible);
        });
        foreach (var shift in new[] { KeyCode.LeftShift, KeyCode.RightShift })
            test(shift + "+F6 opens in game and a second press closes without repeating while held", () =>
            {
                ZNet.instance = new ZNet(); using var prompt = new Prompt(_ => { }, () => { }, () => { });
                prompt.ServerConfigAvailable = () => false;
                ZInput.ManagerShortcut(shift); prompt.Update(); check(prompt.IsVisible);
                prompt.Update(); check(prompt.IsVisible);
                ZInput.Pressed.Add(KeyCode.F6); prompt.Update(); check(!prompt.IsVisible);
            });
        test("Shift+F6 closing keeps unsaved edits behind Save Discard or Keep editing", () =>
        {
            using var prompt = Local(); GUI.Click = "Alpha.cfg"; prompt.Draw();
            var doc = (ConfigEditorDocument)Get(prompt, "configDocument")!; doc.Entries.Single().Value = "unsaved";
            ZInput.ManagerShortcut(); prompt.Update(); check(prompt.IsVisible && Get(prompt, "pendingNavigation") != null && doc.Entries.Single().Value == "unsaved");
            GUI.Click = "Keep editing"; prompt.Draw(); check(prompt.IsVisible && Get(prompt, "pendingNavigation") == null && doc.Entries.Single().Value == "unsaved");
            ZInput.ManagerShortcut(); prompt.Update(); GUI.Click = "Discard changes"; prompt.Draw();
            check(!prompt.IsVisible && File.ReadAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "config", "Alpha.cfg")).Contains("original-alpha", StringComparison.Ordinal));
        });
        test("local config files start collapsed and selected fields render directly below their filename", () =>
        {
            using var prompt = Local();
            check(Get(prompt, "configDocument") == null);
            GUI.Click = "Alpha.cfg"; VisualPreview.Capture(prompt);
            var header = Index("button", "Alpha.cfg"); if (header < 0) header = Index("button", "● Alpha.cfg");
            var fields = Index("label", "General / AlphaValue"); var next = Index("button", "Beta.cfg");
            check(header >= 0 && fields > header && next > fields);
            GUI.Click = "● Alpha.cfg"; VisualPreview.Capture(prompt);
            check(Get(prompt, "configDocument") == null && DrawCapture.Commands.All(c => c.Text != "General / AlphaValue"));
        });
        test("local config collapse and switching protect dirty values until the user chooses", () =>
        {
            using var prompt = Local(); GUI.Click = "Alpha.cfg"; prompt.Draw(); var doc = (ConfigEditorDocument)Get(prompt, "configDocument")!;
            doc.Entries.Single().Value = "unsaved"; GUI.Click = "Beta.cfg"; prompt.Draw();
            check(ReferenceEquals(Get(prompt, "configDocument"), doc) && Get(prompt, "pendingNavigation") != null);
            GUI.Click = "Keep editing"; prompt.Draw(); check(doc.Entries.Single().Value == "unsaved");
            GUI.Click = "● Alpha.cfg"; prompt.Draw(); check(Get(prompt, "pendingNavigation") != null);
            GUI.Click = "Discard changes"; prompt.Draw(); check(Get(prompt, "configDocument") == null && doc.Entries.Single().Value == "original-alpha");
        });
        test("Reload configs refreshes the local list without consuming pending restart edits", () =>
        {
            using var prompt = Local();
            var root = BepInEx.Paths.BepInExRootPath; var doc = ConfigEditor.Open(root, "offline", "Alpha.cfg");
            ConfigEditor.Save(root, doc, doc.Entries.ToDictionary(e => e.Id, _ => "queued-next-launch"));
            var pendingFolder = Path.Combine(root, "ModGate", "config-edits", "pending");
            var queued = Directory.GetFiles(pendingFolder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
            var calls = 0; Set(prompt, "ReloadConfigs", (Func<string, string>)(slot => { check(slot == "offline"); calls++; return "Reloaded live configs."; }));
            GUI.Click = "Reload configs"; prompt.Draw();
            check(calls == 1 && Get(prompt, "configDocument") == null && queued.Count > 0 && queued.All(p => File.ReadAllText(p.Key) == p.Value));
            check(ConfigEditor.Open(root, "offline", "Alpha.cfg").Pending && File.ReadAllText(Path.Combine(root, "config", "Alpha.cfg")).Contains("original-alpha", StringComparison.Ordinal));
        });
        test("remote config fields appear between their header and the next file and collapse without a request", () =>
        {
            using var prompt = Remote(); var calls = 0;
            prompt.ServerConfigRequest = (request, _) => { calls++; check(request.Operation == "read" && request.Path == "Alpha.cfg"); return Task.FromResult(new AdminConfigResponse { Authorized = true, Document = Document(request.Path) }); };
            GUI.Click = "Alpha.cfg"; prompt.Draw(); Finish(prompt, check); VisualPreview.Capture(prompt);
            var header = Index("button", "● Alpha.cfg"); var fields = Index("label", "General / AlphaValue"); var next = Index("button", "Beta.cfg");
            check(header >= 0 && fields > header && next > fields);
            GUI.Click = "● Alpha.cfg"; prompt.Draw(); check(Get(prompt, "remoteDocument") == null && calls == 1);
        });
        test("remote Reload configs is guarded and sends only reload without losing pending receipts", () =>
        {
            using var prompt = Remote(); var document = Document("Alpha.cfg"); Call(prompt, "SetRemoteDocument", document); document.Entries.Single().Value = "unsaved";
            var operations = new List<string>();
            prompt.ServerConfigRequest = (request, _) =>
            {
                operations.Add(request.Operation); check(request.Operation == "reload" && request.Path.Length == 0 && request.Text.Length == 0 && request.Changes.Count == 0);
                return Task.FromResult(new AdminConfigResponse { Authorized = true, Files = new() { new() { Path = "Alpha.cfg", Kind = "cfg", Pending = true } } });
            };
            GUI.Click = "Reload configs"; prompt.Draw(); check(operations.Count == 0 && Get(prompt, "pendingNavigation") != null && document.Entries.Single().Value == "unsaved");
            GUI.Click = "Discard changes"; prompt.Draw(); Finish(prompt, check);
            check(operations.SequenceEqual(new[] { "reload" }) && Get(prompt, "remoteDocument") == null && ((List<AdminConfigFileInfo>)Get(prompt, "remoteFiles")!).Single().Pending);
        });
        test("DLL hard and soft declarations do not lock optional modules while explicit required stays locked", () =>
        {
            ZNet.instance = null;
            var parent = new ModEntry { Guid = "parent", Name = "Parent", Side = "required", Version = "1.0.0", Dependencies = new() { new() { Guid = "hard" }, new() { Guid = "soft", Optional = true } } };
            var hard = new ModEntry { Guid = "hard", Name = "Hard module", Side = "optional", Version = "1.0.0" };
            var soft = new ModEntry { Guid = "soft", Name = "Soft module", Side = "optional", Version = "1.0.0" };
            var required = new ModEntry { Guid = "explicit", Name = "Explicit required", Side = "required", Version = "1.0.0" };
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            prompt.Show(new Policy { Mods = new() { parent, hard, soft, required } }, new Comparison(), Array.Empty<ModEntry>(), new string('c', 64)); VisualPreview.Capture(prompt);
            check(GUILayout.Toggles.Any(t => t.Text.TrimStart() == "Optional: Hard module" && !t.Selected && t.Enabled));
            check(GUILayout.Toggles.Any(t => t.Text.TrimStart() == "Optional: Soft module" && !t.Selected && t.Enabled));
            check(GUILayout.Toggles.Any(t => t.Text == "Required: Explicit required" && t.Selected && !t.Enabled));
            check(DrawCapture.Commands.Any(c => c.Kind == "label" && c.Text.Contains("BepInEx may refuse", StringComparison.Ordinal)));
        });
        test("suggested package downloads start unchecked and changing a choice invalidates the old review", () =>
        {
            ZNet.instance = null; var package = "Author-Library-1.0.0";
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            var parent = new ModEntry { Guid = "parent", Name = "Parent", Side = "required", Version = "1.0.0", Dependencies = new() { new() { Guid = "library", Package = package } } };
            prompt.Show(new Policy { AllowExtras = true, Mods = new() { parent } }, new Comparison(), Array.Empty<ModEntry>(), new string('d', 64)); VisualPreview.Capture(prompt);
            var label = "Download suggested dependency: " + package;
            check(GUILayout.Toggles.Single(t => t.Text == label) is { Selected: false, Enabled: true });
            var id = Guid.NewGuid().ToString("N"); var stage = Path.Combine(BepInEx.Paths.BepInExRootPath, "ModGate", "staging", id); Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "fixture.txt"), "fixture");
            Set(prompt, "prepared", new PendingInstall { Id = id }); Set(prompt, "preparedProfile", new SavedProfile());
            Call(prompt, "ChangeDependencyChoice", package, true); VisualPreview.Capture(prompt);
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Contains(package) && Get(prompt, "prepared") == null && Get(prompt, "preparedProfile") == null && !Directory.Exists(stage));
            GUI.Click = label; VisualPreview.Capture(prompt);
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Count == 0);
        });
        test("old profile packages never create or preselect dependency suggestions", () =>
        {
            ZNet.instance = null;
            var root = Path.Combine(Path.GetTempPath(), "ModGate-old-suggestions-" + Guid.NewGuid().ToString("N")); BepInEx.Paths.BepInExRootPath = root;
            var key = new string('f', 64);
            var parent = new ModEntry { Guid = "parent", Name = "Current parent", Side = "required", Version = "1.0.0", Dependencies = new() { new() { Guid = "library", Package = "Author-Library-2.0.0" } } };
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), new ProfileCatalog { Servers = new() { new SavedProfile { Slot = "server-1", ServerKey = key, Name = "Saved", Packages = new() { "Author-RemovedMod-1.0.0", "Author-Library-1.0.0" } } } });
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            var policy = new Policy { AllowExtras = true, Mods = new() { parent } };
            prompt.Show(policy, new Comparison(), Array.Empty<ModEntry>(), key); VisualPreview.Capture(prompt);
            check(!GUILayout.Toggles.Any(t => t.Text.Contains("RemovedMod") || t.Text.Contains("Author-Library-1.0.0")));
            check(GUILayout.Toggles.Single(t => t.Text == "Download suggested dependency: Author-Library-2.0.0") is { Selected: false, Enabled: true });
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Count == 0);
            GUI.Click = "Download suggested dependency: Author-Library-2.0.0"; VisualPreview.Capture(prompt);
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Count == 1);
            prompt.Show(policy, new Comparison(), Array.Empty<ModEntry>(), key); VisualPreview.Capture(prompt);
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Count == 0);
            check(!GUILayout.Labels.Any(l => l.Contains("Previously selected for this profile")));
        });
        test("a server that disallows extras cannot opt into an undeclared suggested package", () =>
        {
            ZNet.instance = null; using var prompt = new Prompt(_ => { }, () => { }, () => { });
            var parent = new ModEntry { Guid = "parent", Name = "Parent", Side = "required", Version = "1.0.0", Dependencies = new() { new() { Guid = "library", Package = "Author-Library-1.0.0" } } };
            prompt.Show(new Policy { AllowExtras = false, Mods = new() { parent } }, new Comparison(), Array.Empty<ModEntry>(), new string('e', 64)); VisualPreview.Capture(prompt);
            check(GUILayout.Toggles.Single(t => t.Text == "Download suggested dependency: Author-Library-1.0.0") is { Selected: false, Enabled: false });
            check(((HashSet<string>)Get(prompt, "selectedDependencyPackages")!).Count == 0);
        });
    }
}
