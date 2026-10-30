using System.Reflection;
using ModGate;
using UnityEngine;

internal static class AdminConfigUiChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static void Set(Prompt prompt, string name, object? value) => typeof(Prompt).GetField(name, Private)!.SetValue(prompt, value);
    private static object? Call(Prompt prompt, string name, params object[] args)
    {
        if (name == "SetRemoteDocument" && args[0] is AdminConfigDocument document)
            Set(prompt, "remoteFiles", new List<AdminConfigFileInfo> { new() { Path = document.Path, Kind = document.Kind, Pending = document.Pending } });
        return typeof(Prompt).GetMethod(name, Private)!.Invoke(prompt, args);
    }
    private static bool Dirty(Prompt prompt) => (bool)typeof(Prompt).GetProperty("RemoteDirty", Private)!.GetValue(prompt)!;
    private static void Fixture()
    {
        BepInEx.Paths.BepInExRootPath = Path.Combine(Path.GetTempPath(), "ModGate-admin-ui-" + Guid.NewGuid().ToString("N"));
        Screen.width = 1920; Screen.height = 1080; ZNet.instance = null;
    }
    private static AdminConfigDocument Document(string value = "true", bool pending = true) => new AdminConfigDocument
    {
        Path = "Example.Mod.cfg", Kind = "cfg", Hash = new string('a', 64), Pending = pending, RestartRequired = true,
        Entries = new() { new AdminConfigEntry { Id = "1", Section = "General", Key = "Enabled", Value = value, Kind = "Boolean", TypeName = "Boolean", Editable = true } }
    };
    private static void Finish(Prompt prompt, Action<bool> check)
    {
        var task = (Task<Action>?)Get(prompt, "featureTask");
        check(task != null && SpinWait.SpinUntil(() => task.IsCompleted, 3000)); prompt.Update();
    }
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("opening and drawing the server config page never initiates a request without a button", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); var calls = 0;
            prompt.ServerConfigAvailable = () => true; prompt.ServerConfigRequest = (_, _) => { calls++; return Task.FromResult(new AdminConfigResponse { Authorized = true }); };
            Call(prompt, "OpenServerConfigs"); prompt.Draw(); prompt.Draw();
            check(calls == 0 && GUI.Buttons.Any(b => b.Text == "Connect / refresh server files" && b.Enabled));
        });
        test("server RPC starts on the UI thread and its result is applied only by the UI update", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs");
            var completion = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously); var uiThread = Environment.CurrentManagedThreadId; var requestThread = 0; var applyThread = 0;
            prompt.ServerConfigAvailable = () => true;
            prompt.ServerConfigRequest = (request, code) => { requestThread = Environment.CurrentManagedThreadId; check(request.Operation == "list" && code == "fixture-admin-code"); return completion.Task; };
            Set(prompt, "adminAccessCode", "fixture-admin-code");
            Call(prompt, "RunServerRequest", new AdminConfigRequest { Operation = "list" }, (Action<AdminConfigResponse>)(_ => applyThread = Environment.CurrentManagedThreadId));
            check(requestThread == uiThread && applyThread == 0 && Get(prompt, "featureTask") != null);
            completion.SetResult(new AdminConfigResponse { Authorized = true });
            var work = (Task<Action>)Get(prompt, "featureTask")!; check(SpinWait.SpinUntil(() => work.IsCompleted, 3000) && applyThread == 0);
            prompt.Update(); check(applyThread == uiThread && Get(prompt, "featureTask") == null);
        });
        test("server role rejection is shown without creating a config or sending a save", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs"); var operations = new List<string>();
            prompt.ServerConfigAvailable = () => true;
            prompt.ServerConfigRequest = (request, _) => { operations.Add(request.Operation); return Task.FromResult(new AdminConfigResponse { Authorized = false, Error = "Only server admins can edit configs." }); };
            GUI.Click = "Connect / refresh server files"; prompt.Draw(); Finish(prompt, check); GUILayout.Labels.Clear(); prompt.Draw();
            check(operations.SequenceEqual(new[] { "list" }) && Get(prompt, "remoteDocument") == null && GUILayout.Labels.Any(l => l.Contains("Only server admins can edit configs.")));
            check(!Directory.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, "config")));
        });
        test("pending server configs keep unsaved edits behind the navigation guard and discard performs no save", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs"); var calls = 0;
            prompt.ServerConfigAvailable = () => true; prompt.ServerConfigRequest = (_, _) => { calls++; return Task.FromResult(new AdminConfigResponse { Authorized = true }); };
            var document = Document(); Call(prompt, "SetRemoteDocument", document); prompt.Draw(); check(GUILayout.Labels.Any(l => l.Contains("Saved for server restart")));
            document.Entries.Single().Value = "false"; var navigated = 0; Call(prompt, "RequestNavigation", (Action)(() => navigated++));
            check(Dirty(prompt) && navigated == 0 && Get(prompt, "pendingNavigation") != null);
            GUI.Click = "Discard changes"; prompt.Draw(); check(!Dirty(prompt) && document.Entries.Single().Value == "true" && navigated == 1 && calls == 0);
        });
        test("canceling a server wait preserves local edits and never retries or applies a late save response", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs");
            var completion = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
            prompt.ServerConfigAvailable = () => true; prompt.ServerConfigRequest = (request, _) => { calls++; check(request.Operation == "save"); return completion.Task; };
            var document = Document(); Call(prompt, "SetRemoteDocument", document); document.Entries.Single().Value = "false";
            Call(prompt, "SaveRemoteConfig"); GUI.Click = "Cancel waiting for server"; prompt.Draw(); Finish(prompt, check);
            check(calls == 1 && Dirty(prompt) && ((string)Get(prompt, "progress")!).Contains("Stopped waiting"));
            completion.SetResult(new AdminConfigResponse { Authorized = true, Document = Document("false") }); prompt.Update();
            check(calls == 1 && Dirty(prompt) && ReferenceEquals(Get(prompt, "remoteDocument"), document));
        });
        test("Save and continue exposes wait cancellation and navigates only after an acknowledged save is finalized", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs");
            var completion = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var navigated = 0; var finalizedAtNavigation = false;
            prompt.ServerConfigAvailable = () => true; prompt.ServerConfigRequest = (request, _) => { calls++; check(request.Operation == "save" && request.ExpectedHash == new string('a', 64) && request.Changes.Single().Value == "false"); return completion.Task; };
            var document = Document(); Call(prompt, "SetRemoteDocument", document); document.Entries.Single().Value = "false";
            Call(prompt, "RequestNavigation", (Action)(() => { navigated++; finalizedAtNavigation = Get(prompt, "featureTask") == null; }));
            GUI.Click = "Save & continue"; prompt.Draw(); check(calls == 1 && navigated == 0 && Get(prompt, "pendingNavigation") == null);
            GUI.Buttons.Clear(); prompt.Draw(); check(GUI.Buttons.Any(b => b.Text == "Cancel waiting for server" && b.Enabled));
            completion.SetResult(new AdminConfigResponse { Authorized = true, Document = Document("false") }); Finish(prompt, check);
            check(navigated == 1 && finalizedAtNavigation && !Dirty(prompt) && ((string)Get(prompt, "progress")!).Contains("Restart the server"));
        });
        test("the ordinary Offline config editor remains accessible without any server admin connection", () =>
        {
            Fixture(); var config = Path.Combine(BepInEx.Paths.BepInExRootPath, "config"); Directory.CreateDirectory(config); File.WriteAllText(Path.Combine(config, "Example.Mod.cfg"), "[General]\n# Setting type: Boolean\nEnabled = true\n");
            using var prompt = new Prompt(_ => { }, () => { }, () => { }); var calls = 0;
            prompt.ServerConfigAvailable = () => false; prompt.ServerConfigRequest = (_, _) => { calls++; return Task.FromResult(new AdminConfigResponse()); };
            ZInput.ManagerShortcut(); prompt.Update(); GUI.Click = "Open profile (offline)"; prompt.Draw(); GUI.Click = "Configs"; prompt.Draw();
            check(calls == 0 && GUILayout.Labels.Any(l => l.Contains("Config editor")) && Get(prompt, "configDocument") == null);
        });
        test("disconnect cleanup forgets access and prevents a completed old-server read from restoring stale edits", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Call(prompt, "OpenServerConfigs"); var operations = new List<string>(); var connected = true;
            prompt.ServerConfigAvailable = () => connected; prompt.ServerConfigRequest = (request, _) => { operations.Add(request.Operation); return Task.FromResult(new AdminConfigResponse { Authorized = true, Document = Document("false") }); };
            Set(prompt, "adminAccessCode", "fixture-admin-code"); var document = Document(); Call(prompt, "SetRemoteDocument", document); document.Entries.Single().Value = "false";
            Call(prompt, "RunServerRequest", new AdminConfigRequest { Operation = "read", Path = document.Path }, (Action<AdminConfigResponse>)(response => Call(prompt, "SetRemoteDocument", response.Document!)));
            check(((Task<Action>)Get(prompt, "featureTask")!).IsCompleted);
            connected = false; prompt.ForgetServerConfigAccess(); prompt.Update(); prompt.Draw();
            check((string)Get(prompt, "adminAccessCode")! == "" && Get(prompt, "remoteDocument") == null && !Dirty(prompt) && operations.SequenceEqual(new[] { "read" }));
            check(GUILayout.Labels.Any(l => l.Contains("Join a server")));
        });
    }
}
