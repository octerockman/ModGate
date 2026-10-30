using System.Reflection;
using ModGate;
using UnityEngine;

internal static class Visual041Checks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Get(Prompt prompt, string name) => typeof(Prompt).GetField(name, Private)!.GetValue(prompt);
    private static bool Contains(Rect outer, Rect inner) => inner.x >= outer.x && inner.y >= outer.y && inner.xMax <= outer.xMax + .01f && inner.yMax <= outer.yMax + .01f;
    private static bool Overlaps(Rect a, Rect b) => a.x < b.xMax && b.x < a.xMax && a.y < b.yMax && b.y < a.yMax;
    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Mods has a separate details tab and no per-row details or mod-name links", () =>
        {
            using var fixture = VisualPreview.Create("mods"); VisualPreview.Capture(fixture.Prompt);
            check(GUI.Buttons.Any(b => b.Text == "Mod details" && !b.InScroll));
            var names = new[] { "Valheim Plus", "Cartography", "TeleportEverything", "TeleportBoats", "Camping" };
            check(GUI.Buttons.All(b => !b.Text.StartsWith("Details:", StringComparison.Ordinal) && !b.Text.StartsWith("View:", StringComparison.Ordinal) && !names.Contains(b.Text)));
            check(names.All(n => DrawCapture.Commands.Any(c => c.Kind == "label" && c.Text == n)));
        });
        test("browsing a mod and its dependencies preserves selected optional mods", () =>
        {
            using var fixture = VisualPreview.Create("mods"); var prompt = fixture.Prompt;
            var selected = (HashSet<string>)Get(prompt, "selectedMods")!; var before = selected.OrderBy(x => x).ToArray();
            GUI.Click = "Mod details"; VisualPreview.Capture(prompt);
            check((string)Get(prompt, "navigationTab")! == "Mod details" && GUI.Buttons.Any(b => b.Text == "View: TeleportEverything"));
            GUI.Click = "View: TeleportEverything"; VisualPreview.Capture(prompt); VisualPreview.Capture(prompt);
            check(((ModEntry)Get(prompt, "detailMod")!).Guid == "org.example.teleport");
            check(DrawCapture.Commands.Any(c => c.Kind == "label" && c.Text.Contains("DLL version: 2.8.0", StringComparison.Ordinal)));
            GUI.Click = "Dependencies"; VisualPreview.Capture(prompt);
            check(DrawCapture.Commands.Any(c => c.Text.Contains("org.example.teleportboats", StringComparison.Ordinal)));
            GUI.Click = "Mods"; VisualPreview.Capture(prompt);
            check(before.SequenceEqual(selected.OrderBy(x => x)) && (string)Get(prompt, "navigationTab")! == "Mods");
        });
        test("required rows use a compact locked checkbox and fully visible informational text", () =>
        {
            using var fixture = VisualPreview.Create("mods"); VisualPreview.Capture(fixture.Prompt);
            var toggle = DrawCapture.Commands.Single(c => c.Kind == "toggle" && c.Text == "Required: TeleportEverything");
            var label = DrawCapture.Commands.Single(c => c.Kind == "label" && c.Text == "TeleportEverything");
            check(toggle.Selected && !toggle.Enabled && toggle.Area.width == 21 && toggle.Area.height == 21);
            check(label.Enabled && label.TextColor.a == 1 && label.TextColor == new Color(.945f, .631f, .596f, 1));
            check(!Overlaps(toggle.Area, label.Area));
        });
        test("optional rows remain expanded and selectable without opening a module group", () =>
        {
            using var fixture = VisualPreview.Create("mods"); VisualPreview.Capture(fixture.Prompt);
            check(GUILayout.Toggles.Any(t => t.Text.Contains("Optional: TeleportBoats", StringComparison.Ordinal) && t.Enabled));
            check(GUILayout.Toggles.Any(t => t.Text.Contains("Optional: Camping", StringComparison.Ordinal) && t.Enabled));
        });
        foreach (var size in new[] { (1280, 1000), (960, 768), (640, 600) })
            test("profile cards and their controls stay separated at " + size, () =>
            {
                using var fixture = VisualPreview.Create("profiles", size.Item1, size.Item2); VisualPreview.Capture(fixture.Prompt);
                var cards = DrawCapture.Commands.Where(c => c.Kind == "box" && c.Area.width > 200 && c.Area.height >= 100).ToArray();
                check(cards.Length == 5);
                for (var i = 0; i < cards.Length; i++) for (var j = i + 1; j < cards.Length; j++) check(!Overlaps(cards[i].Area, cards[j].Area));
                foreach (var button in GUI.Buttons.Where(b => b.Text.StartsWith("Open profile (", StringComparison.Ordinal) || b.Text.StartsWith("Activate & restart (", StringComparison.Ordinal)))
                    check(cards.Count(c => Contains(c.Area, button.Area)) == 1);
                foreach (var caption in DrawCapture.Commands.Where(c => c.Text == "Join a server to set up this profile."))
                    check(cards.Count(c => Contains(c.Area, caption.Area)) == 1);
                var actions = GUI.Buttons.Where(b => b.Text.StartsWith("Open profile (", StringComparison.Ordinal) || b.Text.StartsWith("Activate & restart (", StringComparison.Ordinal)).ToArray();
                for (var i = 0; i < actions.Length; i++) for (var j = i + 1; j < actions.Length; j++) check(!Overlaps(actions[i].Area, actions[j].Area));
            });
    }
}
