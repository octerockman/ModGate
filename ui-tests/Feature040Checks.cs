using System.Reflection;
using ModGate;
using UnityEngine;

internal static class Feature040Checks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static void Set(Prompt prompt, string name, object? value) => typeof(Prompt).GetField(name, Private)!.SetValue(prompt, value);
    private static object? Call(Prompt prompt, string name, params object[] args) => typeof(Prompt).GetMethod(name, Private)!.Invoke(prompt, args);
    private static void Fixture()
    {
        BepInEx.Paths.BepInExRootPath = Path.Combine(Path.GetTempPath(), "ModGate-ui040-" + Guid.NewGuid().ToString("N"));
        Screen.width = 1920; Screen.height = 1080; ZNet.instance = null;
    }
    private static ConfigEditorDocument Dirty(Prompt prompt)
    {
        var document = ConfigEditor.Parse("[General]\n# Setting type: String\nValue = original\n");
        document.Slot = "offline"; document.Entries.Single().Value = "edited"; Set(prompt, "configDocument", document); return document;
    }
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("unsaved config navigation waits for a decision and discard executes the intended action", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture");
            var document = Dirty(prompt); var navigated = 0;
            Call(prompt, "RequestNavigation", (Action)(() => navigated++));
            check(navigated == 0 && Get(prompt, "pendingNavigation") != null);
            GUI.Click = "Discard changes"; prompt.Draw();
            check(navigated == 1 && document.Entries.Single().Value == "original" && Get(prompt, "pendingNavigation") == null);
        });
        test("Escape preserves unsaved edits until the player chooses and can dismiss the decision", () =>
        {
            Fixture(); var closed = 0; using var prompt = new Prompt(_ => { }, () => closed++, () => { }); prompt.ShowError("fixture"); Dirty(prompt);
            ZInput.Pressed.Add(KeyCode.Escape); prompt.Update(); check(prompt.IsVisible && closed == 0 && Get(prompt, "pendingNavigation") != null);
            ZInput.Pressed.Add(KeyCode.Escape); prompt.Update(); check(prompt.IsVisible && closed == 0 && Get(prompt, "pendingNavigation") == null);
        });
        test("Quit Valheim protects unsaved config edits with the same navigation decision", () =>
        {
            Fixture(); var quit = 0; using var prompt = new Prompt(_ => { }, () => { }, () => quit++); prompt.ShowError("fixture"); Dirty(prompt);
            GUI.Click = "Quit Valheim"; prompt.Draw(); check(quit == 0 && Get(prompt, "pendingNavigation") != null);
            GUI.Click = "Discard changes"; prompt.Draw(); check(quit == 1);
        });
        test("Shift+F6 preserves unsaved config changes through the navigation guard", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Dirty(prompt);
            ZInput.ManagerShortcut(); prompt.Update(); check(Get(prompt, "pendingNavigation") != null && !(bool)Get(prompt, "manager")!);
        });
        test("config editor repaint does not normalize a valid Boolean into an unsolicited edit", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); prompt.Draw();
            var document = ConfigEditor.Parse("[General]\n# Setting type: Boolean\nEnabled = True\n"); document.Slot = "offline"; document.RelativePath = "Example.cfg";
            Set(prompt, "configEditorSlot", "offline"); Set(prompt, "configFiles", new List<string> { "Example.cfg" }); Set(prompt, "configFile", "Example.cfg"); Set(prompt, "configDocument", document);
            Call(prompt, "DrawConfigEditor", "offline");
            check(document.Entries.Single().Value == "True" && document.Entries.Single().OriginalValue == "True");
        });
        test("config editor offers reset for an explicitly empty string default", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); prompt.Draw();
            var document = ConfigEditor.Parse("[General]\n# Setting type: String\n# Default value: \nValue = edited\n"); document.Slot = "offline"; document.RelativePath = "Example.cfg";
            Set(prompt, "configEditorSlot", "offline"); Set(prompt, "configFiles", new List<string> { "Example.cfg" }); Set(prompt, "configFile", "Example.cfg"); Set(prompt, "configDocument", document);
            Call(prompt, "DrawConfigEditor", "offline"); check(GUI.Buttons.Any(b => b.Text == "Reset Value to default"));
        });
        test("a profile operation disables conflicting tools while preserving Close Activity and Discord", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { });
            prompt.Show(new Policy { ServerName = "Fixture" }, new Comparison(), Array.Empty<ModEntry>(), new string('a', 64));
            Set(prompt, "featureTask", new TaskCompletionSource<Action>().Task); prompt.Draw();
            check(GUI.Buttons.Any(b => b.Text == "Check profile" && !b.Enabled));
            check(GUI.Buttons.Any(b => b.Text == "Configs" && !b.Enabled));
            check(GUI.Buttons.Any(b => b.Text == "Activity" && b.Enabled));
            check(GUI.Buttons.Any(b => b.Text == "Close" && b.Enabled));
            check(GUI.Buttons.Any(b => b.Text.Contains("Report a bug") && b.Enabled));
        });
        test("Report a bug opens the owner's exact Discord invitation", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }); prompt.ShowError("fixture"); Application.LastUrl = "";
            GUI.Click = "Report a bug ↗"; prompt.Draw(); check(Application.LastUrl == "https://discord.gg/AaVzBfPcYH");
        });
        test("optional DLL modules are visible by default before any expansion click", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { });
            var parent = new ModEntry { Guid = "teleport", Name = "TeleportEverything", Version = "1.0.0", Side = "required", Dependencies = new() { new PluginDependency { Guid = "boats", Optional = true } } };
            var child = new ModEntry { Guid = "boats", Name = "TeleportBoats", Version = "1.0.0", Side = "optional" };
            prompt.Show(new Policy { Mods = new() { parent, child } }, new Comparison(), Array.Empty<ModEntry>(), new string('b', 64)); prompt.Draw();
            check((bool)Get(prompt, "optionalExpanded")! && GUILayout.Toggles.Any(t => t.Text.Contains("Optional: TeleportBoats")));
        });
        test("the already active Offline profile still exposes an enabled activation and restart action", () =>
        {
            Fixture(); using var prompt = new Prompt(_ => { }, () => { }, () => { }, () => { });
            ZInput.ManagerShortcut(); prompt.Update(); prompt.Draw();
            check(GUI.Buttons.Any(b => b.Text.Contains("Activate", StringComparison.OrdinalIgnoreCase) && b.Text.Contains("offline", StringComparison.OrdinalIgnoreCase) && b.Enabled));
        });
    }
}
