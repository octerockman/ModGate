using System.Collections;
using System.Globalization;
using System.Reflection;
using ModGate;
using UnityEngine;

internal static class ServerHealthUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    internal static void Set(Prompt prompt, string name, object? value) => typeof(Prompt).GetField(name, Private)!.SetValue(prompt, value);
    internal static object? Call(Prompt prompt, string name, params object[] values) => typeof(Prompt).GetMethod(name, Private)!.Invoke(prompt, values);
    internal static ServerHealthSnapshot Sample(double? cpu = 150) => new()
    {
        CollectedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), UptimeSeconds = 1234,
        ProcessCpuPercent = cpu, ProcessRssBytes = 1536L * 1024 * 1024, ManagedBytes = 320L * 1024 * 1024,
        UpdateRate = 59.8, LoopAverageMs = 16.7, LoopMaxMs = 83.5, Players = 4,
        InBytesPerSecond = 32 * 1024, OutBytesPerSecond = 80 * 1024, SendQueueBytes = 1024
    };
    private static AdminConfigResponse Response(ServerHealthSnapshot? health = null) => new() { Authorized = true, Health = health ?? Sample() };
    private sealed class Fixture : IDisposable
    {
        internal readonly Prompt Prompt;
        internal double Now;
        internal bool Connected = true;
        internal readonly List<AdminConfigRequest> Requests = new();
        internal readonly List<string> Codes = new();
        internal Func<AdminConfigRequest, Task<AdminConfigResponse>> Reply = _ => Task.FromResult(Response());
        internal Fixture()
        {
            Screen.width = 1280; Screen.height = 1000; ZNet.instance = new ZNet();
            Prompt = new Prompt(_ => { }, () => { }, () => { }); Prompt.ShowError("fixture");
            Prompt.ServerConfigAvailable = () => Connected; Prompt.ServerConfigLocalOwner = () => false;
            Prompt.ServerHealthClock = () => Now;
            Prompt.ServerConfigRequest = (request, code) => { Requests.Add(request); Codes.Add(code); return Reply(request); };
            Call(Prompt, "OpenServerHealth");
            Set(Prompt, "adminAccessCode", "fixture-owner-code");
        }
        public void Dispose() => Prompt.Dispose();
    }
    internal static void Finish(Prompt prompt, Action<bool> check)
    {
        var work = (Task?)Get(prompt, "serverHealthTask");
        if (work != null) check(SpinWait.SpinUntil(() => work.IsCompleted, 3000));
        prompt.Update();
    }
    private static bool ContainsLabel(string value) => DrawCapture.Commands.Any(c => c.Kind == "label" && c.Text.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static int History(Prompt prompt) => ((ICollection)Get(prompt, "serverHealthHistory")!).Count;
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("opening and repainting Server health sends no request until explicit refresh", () =>
        {
            using var fixture = new Fixture();
            fixture.Prompt.Draw(); fixture.Prompt.Draw(); fixture.Prompt.Update();
            check(fixture.Requests.Count == 0 && Get(fixture.Prompt, "serverHealth") == null);
            check(GUI.Buttons.Any(b => b.Text == "Refresh server health" && b.Enabled));
            check(GUI.Buttons.Any(b => b.Text == "Start auto refresh" && b.Enabled));
        });
        test("health refresh uses the shared admin code and carries no config path values or edits", () =>
        {
            using var fixture = new Fixture(); Set(fixture.Prompt, "adminAccessCode", "fixture-owner-code");
            var callerThread = Environment.CurrentManagedThreadId; var requestThread = 0;
            fixture.Reply = _ => { requestThread = Environment.CurrentManagedThreadId; return Task.FromResult(Response()); };
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); VisualPreview.Capture(fixture.Prompt);
            var request = fixture.Requests.Single();
            check(requestThread == callerThread && fixture.Codes.Single() == "fixture-owner-code" && request.Operation == "health");
            check(request.Path.Length == 0 && request.Text.Length == 0 && request.ExpectedHash.Length == 0 && request.Changes.Count == 0);
            check(Get(fixture.Prompt, "serverHealth") is ServerHealthSnapshot && Get(fixture.Prompt, "remoteDocument") == null);
            check(!ContainsLabel("fixture-owner-code"));
        });
        test("remote health round trip includes time spent handling the request", () =>
        {
            using var fixture = new Fixture();
            fixture.Reply = _ => { var elapsed = System.Diagnostics.Stopwatch.StartNew(); SpinWait.SpinUntil(() => elapsed.ElapsedMilliseconds >= 20); return Task.FromResult(Response()); };
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check);
            var latest = ((IEnumerable)Get(fixture.Prompt, "serverHealthHistory")!).Cast<object>().Last();
            var measured = latest.GetType().GetField("RoundTripMs")!.GetValue(latest);
            check(measured != null && Convert.ToDouble(measured, CultureInfo.InvariantCulture) >= 15);
        });
        test("CPU values above one hundred percent remain valid and identify the one-core scale", () =>
        {
            using var fixture = new Fixture(); GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); VisualPreview.Capture(fixture.Prompt);
            check(ContainsLabel("150.0%") && ContainsLabel("one core"));
        });
        test("auto refresh observes the two-second cadence and stops on request", () =>
        {
            using var fixture = new Fixture();
            GUI.Click = "Start auto refresh"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); check(fixture.Requests.Count == 1);
            fixture.Now = 1.9; fixture.Prompt.Update(); check(fixture.Requests.Count == 1);
            fixture.Now = 2.01; fixture.Prompt.Update(); Finish(fixture.Prompt, check); check(fixture.Requests.Count == 2);
            GUI.Click = "Stop auto refresh"; fixture.Prompt.Draw(); fixture.Now += 10; fixture.Prompt.Update();
            check(!(bool)Get(fixture.Prompt, "serverHealthAutoRefresh")! && fixture.Requests.Count == 2);
        });
        test("auto refresh never overlaps an in-flight request even when several intervals pass", () =>
        {
            using var fixture = new Fixture(); var first = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Reply = _ => first.Task;
            GUI.Click = "Start auto refresh"; fixture.Prompt.Draw(); check(fixture.Requests.Count == 1);
            fixture.Now = 20; fixture.Prompt.Update(); fixture.Prompt.Update(); check(fixture.Requests.Count == 1);
            GUI.Click = "Stop auto refresh"; fixture.Prompt.Draw();
            first.SetResult(Response()); Finish(fixture.Prompt, check); fixture.Now += 10; fixture.Prompt.Update();
            check(!(bool)Get(fixture.Prompt, "serverHealthAutoRefresh")! && fixture.Requests.Count == 1);
        });
        test("health auto refresh does not overlap config or profile operations", () =>
        {
            using var fixture = new Fixture(); Set(fixture.Prompt, "featureTask", new TaskCompletionSource<Action>().Task);
            Call(fixture.Prompt, "RequestServerHealth"); check(fixture.Requests.Count == 0);
            Set(fixture.Prompt, "featureTask", null);
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); check(fixture.Requests.Count == 1);
        });
        test("leaving a pending health request releases local profile tools before its reply arrives", () =>
        {
            using var fixture = new Fixture(); var pending = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Reply = _ => pending.Task; GUI.Click = "Refresh server health"; fixture.Prompt.Draw();
            GUI.Click = "Settings"; fixture.Prompt.Draw(); var applied = 0;
            Call(fixture.Prompt, "RunFeature", "Local tool fixture", (Func<CancellationToken, Action>)(_ => () => applied++));
            var local = (Task<Action>?)Get(fixture.Prompt, "featureTask");
            check(local != null && SpinWait.SpinUntil(() => local.IsCompleted, 3000)); fixture.Prompt.Update();
            check(applied == 1 && !pending.Task.IsCompleted);
            pending.SetResult(Response()); Finish(fixture.Prompt, check); check(Get(fixture.Prompt, "serverHealth") == null);
        });
        test("rapid health re-entry still waits for the previous request to drain before refreshing", () =>
        {
            using var fixture = new Fixture(); var pending = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Reply = _ => pending.Task; GUI.Click = "Refresh server health"; fixture.Prompt.Draw();
            GUI.Click = "Settings"; fixture.Prompt.Draw(); Call(fixture.Prompt, "OpenServerHealth");
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); check(fixture.Requests.Count == 1); GUI.Click = null;
            pending.SetResult(Response()); Finish(fixture.Prompt, check); check(Get(fixture.Prompt, "serverHealth") == null);
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check);
            check(fixture.Requests.Count == 2 && Get(fixture.Prompt, "serverHealth") is ServerHealthSnapshot);
        });
        foreach (var departure in new[] { "tab", "close", "disconnect" })
            test("leaving health by " + departure + " stops polling and discards a late old response", () =>
            {
                using var fixture = new Fixture(); var pending = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Reply = _ => pending.Task;
                GUI.Click = "Start auto refresh"; fixture.Prompt.Draw(); check(fixture.Requests.Count == 1);
                if (departure == "tab") { GUI.Click = "Settings"; fixture.Prompt.Draw(); }
                else if (departure == "close") { GUI.Click = "Close"; fixture.Prompt.Draw(); }
                else { fixture.Connected = false; fixture.Prompt.ForgetServerConfigAccess(); }
                fixture.Prompt.Update(); fixture.Now = 30; fixture.Prompt.Update();
                pending.SetResult(Response()); Finish(fixture.Prompt, check);
                check(fixture.Requests.Count == 1 && Get(fixture.Prompt, "serverHealth") == null && History(fixture.Prompt) == 0);
                check(!(bool)Get(fixture.Prompt, "serverHealthAutoRefresh")!);
            });
        test("a completed old-server sample cannot repopulate health after reconnecting and reopening", () =>
        {
            using var fixture = new Fixture(); fixture.Reply = _ => Task.FromResult(Response());
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw();
            fixture.Connected = false; fixture.Prompt.ForgetServerConfigAccess(); fixture.Prompt.Update();
            fixture.Connected = true; Call(fixture.Prompt, "OpenServerHealth"); fixture.Prompt.Update(); VisualPreview.Capture(fixture.Prompt);
            check(Get(fixture.Prompt, "serverHealth") == null && History(fixture.Prompt) == 0 && fixture.Requests.Count == 1);
        });
        test("health samples become visibly stale and missing metrics are unavailable rather than zero", () =>
        {
            using var fixture = new Fixture(); var sample = Sample(null);
            sample.ProcessRssBytes = null; sample.ManagedBytes = null; sample.UpdateRate = null; sample.LoopAverageMs = null; sample.LoopMaxMs = null;
            sample.InBytesPerSecond = null; sample.OutBytesPerSecond = null; sample.SendQueueBytes = null;
            fixture.Reply = _ => Task.FromResult(Response(sample)); GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check);
            VisualPreview.Capture(fixture.Prompt); check(ContainsLabel("unavailable") && !ContainsLabel("0.0%") && !ContainsLabel("0 B/s"));
            fixture.Now += 6.1; VisualPreview.Capture(fixture.Prompt); check(ContainsLabel("stale"));
        });
        test("health access denial clears telemetry stops polling and leaves config data untouched", () =>
        {
            using var fixture = new Fixture(); fixture.Reply = _ => Task.FromResult(new AdminConfigResponse { Authorized = false, Error = "Only server admins can view health." });
            GUI.Click = "Start auto refresh"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); VisualPreview.Capture(fixture.Prompt);
            fixture.Now += 20; fixture.Prompt.Update();
            check(fixture.Requests.Count == 1 && Get(fixture.Prompt, "serverHealth") == null && History(fixture.Prompt) == 0 && !(bool)Get(fixture.Prompt, "serverHealthAutoRefresh")!);
            check(ContainsLabel("Only server admins") && Get(fixture.Prompt, "remoteDocument") == null && !Directory.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, "config")));
        });
        test("local owner health access uses the same request route without asking for an access code", () =>
        {
            using var fixture = new Fixture(); fixture.Prompt.ServerConfigLocalOwner = () => true; Set(fixture.Prompt, "adminAccessCode", "");
            VisualPreview.Capture(fixture.Prompt); check(!ContainsLabel("Admin access code"));
            GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check);
            VisualPreview.Capture(fixture.Prompt);
            check(fixture.Requests.Single().Operation == "health" && fixture.Codes.Single().Length == 0);
            check(ContainsLabel("Local host") && ContainsLabel("Not applicable"));
        });
        test("health history is bounded in memory and refreshing never persists telemetry", () =>
        {
            using var fixture = new Fixture(); var root = BepInEx.Paths.BepInExRootPath;
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
            GUI.Click = "Start auto refresh"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check);
            for (var i = 0; i < 70; i++) { fixture.Now += 2.1; fixture.Prompt.Update(); Finish(fixture.Prompt, check); }
            check(History(fixture.Prompt) == 60 && fixture.Requests.Count >= 60);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            check(files.Length == before.Count && files.All(p => before.TryGetValue(p, out var text) && File.ReadAllText(p) == text));
            fixture.Prompt.ForgetServerConfigAccess(); check(History(fixture.Prompt) == 0);
        });
        foreach (var size in new[] { (1280, 1000), (640, 900) })
            test("health metric cards and fixed tabs stay separated at " + size, () =>
            {
                using var fixture = new Fixture(); Screen.width = size.Item1; Screen.height = size.Item2;
                GUI.Click = "Refresh server health"; fixture.Prompt.Draw(); Finish(fixture.Prompt, check); VisualPreview.Capture(fixture.Prompt);
                var viewport = (Rect)Get(fixture.Prompt, "scrollBounds")!;
                var names = new[] { "Mods", "Configs", "Mod details", "Activity", "Server health •" };
                var tabs = GUI.Buttons.Where(b => names.Contains(b.Text) && !b.InScroll && b.Area.x >= viewport.x && b.Area.xMax <= viewport.xMax).ToArray();
                check(tabs.Length == 5 && tabs.All(b => b.Area.yMax <= viewport.y));
                var cards = DrawCapture.Commands.Where(c => c.Kind == "box" && c.Area.width >= 100 && c.Area.height >= 100 && c.Area.height < 200).ToArray(); check(cards.Length == 14);
                for (var i = 0; i < cards.Length; i++) for (var j = i + 1; j < cards.Length; j++)
                    check(cards[i].Area.x >= cards[j].Area.xMax || cards[j].Area.x >= cards[i].Area.xMax || cards[i].Area.y >= cards[j].Area.yMax || cards[j].Area.y >= cards[i].Area.yMax);
                check(DrawCapture.Commands.Any(c => c.Kind == "label" && c.FontSize == 29 && c.Text == "Connected server"));
                check(GUI.Buttons.Any(b => b.Text == "Close" && !b.InScroll && b.Enabled));
            });
    }
}
