using System.Reflection;
using ModGate;
using UnityEngine;

internal static class SelfUpdateUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static void Finish(Prompt prompt)
    {
        var task = (Task)Get(prompt, "selfUpdateTask")!;
        if (!SpinWait.SpinUntil(() => task.IsCompleted, 5000)) throw new Exception("Update UI fixture timed out.");
        typeof(Prompt).GetMethod("UpdateSelfUpdates", Private)!.Invoke(prompt, null);
    }
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("server connector mismatch opens update Settings and requests the exact official version", () =>
        {
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            string? requested = null;
            prompt.FindSelfUpdate = (version, early, token) => { requested = version; return Task.FromResult(new SelfUpdateRelease { Version = version!, Asset = "official fixture" }); };
            prompt.ShowConnectorUpdate("Test server", "0.4.8"); Finish(prompt); prompt.Draw();
            check(requested == "0.4.8" && (bool)Get(prompt, "settingsPage")!);
            check(GUI.Buttons.Any(b => b.Text == "Download ModGate 0.4.8 for review"));
            check(GUILayout.Labels.Any(l => l.Contains("Test server requires ModGate 0.4.8")));
            check(!File.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, SelfUpdateInstaller.Active)));
        });
        test("launch check reports a release without installing and respects the 12-hour check interval", () =>
        {
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            var calls = 0;
            prompt.FindSelfUpdate = (version, early, token) => { Interlocked.Increment(ref calls); return Task.FromResult(new SelfUpdateRelease { Version = "0.4.8" }); };
            prompt.StartSelfUpdateCheckOnLaunch(); Finish(prompt); prompt.StartSelfUpdateCheckOnLaunch();
            check(calls == 1 && Get(prompt, "selfUpdateTask") == null && Get(prompt, "selfUpdatePrepared") == null);
            check(!File.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, SelfUpdateInstaller.Active)));
        });
        test("disabled launch checks do not contact GitHub", () =>
        {
            SelfUpdateSource.SavePreferences(BepInEx.Paths.BepInExRootPath, new SelfUpdatePreferences { CheckOnLaunch = false });
            using var prompt = new Prompt(_ => { }, () => { }, () => { });
            prompt.FindSelfUpdate = (_, _, _) => throw new Exception("Unexpected update request");
            prompt.StartSelfUpdateCheckOnLaunch(); check(Get(prompt, "selfUpdateTask") == null);
        });
    }
}
