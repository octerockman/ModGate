using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ModGate;

internal static partial class Program
{
    private const string EditorFixtureText = "# Keep this file\r\n[General]\r\n## Enable the feature.\r\n# Setting type: Boolean\r\n# Default value: true\r\nEnabled = true\r\n\r\n## Distance in metres.\r\n# Setting type: Int32\r\n# Default value: 20\r\n# Acceptable value range: From 1 to 100\r\nRange = 20\r\n\r\n# Setting type: Quality\r\n# Default value: Normal\r\n# Acceptable values: Off, Reduced, Normal\r\nEffects = Normal\r\n\r\n# Setting type: String\r\n# Default value: café\r\nGreeting = café = ready\\nnow\r\nPassword = keep-private\r\n";
    private static (string root, string path) EditorFixture(bool active, string? text = null)
    {
        var root = Root(); var catalog = new ProfileCatalog { Active = active ? "server-1" : "offline", Servers = new List<SavedProfile> { new SavedProfile { Slot = "server-1", Name = "Editor server", ServerKey = new string('a', 64), Mods = new List<ModEntry> { Mod("Editor.Mod") }, ConfigBindings = new List<ConfigBinding> { new ConfigBinding { Guid = "Editor.Mod", Files = new List<string> { "Editor.Mod.cfg" } } } } } };
        Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
        var path = Put(root, active ? "config/Editor.Mod.cfg" : "ModGate/profiles/server-1/config/Editor.Mod.cfg", text ?? EditorFixtureText);
        return (root, path);
    }
    private static Dictionary<string, string> EditorValues(ConfigEditorDocument document, string key, string value)
        => new Dictionary<string, string> { [document.Entries.Single(e => e.Key == key).Id] = value };
    private static void ConfigEditorChecks()
    {
        Test("config parser reads BepInEx descriptions defaults choices numeric limits and secrets", () =>
        {
            var parsed = ConfigEditor.Parse(EditorFixtureText);
            Check(parsed.Entries.Count == 4 && parsed.Entries.All(e => e.Section == "General" && e.Key != "Password"));
            var enabled = parsed.Entries.Single(e => e.Key == "Enabled"); var range = parsed.Entries.Single(e => e.Key == "Range"); var effects = parsed.Entries.Single(e => e.Key == "Effects");
            Check(enabled.Kind == ConfigEditorKind.Boolean && enabled.HasDefault && enabled.DefaultValue == "true" && enabled.Description == "Enable the feature.");
            Check(range.Kind == ConfigEditorKind.Number && range.Minimum == "1" && range.Maximum == "100" && effects.Kind == ConfigEditorKind.Choice && effects.Choices.SequenceEqual(new[] { "Off", "Reduced", "Normal" }));
        });
        Test("config validation rejects invalid booleans enum values numeric ranges overflow and line injection", () =>
        {
            var entries = ConfigEditor.Parse(EditorFixtureText).Entries;
            Check(ConfigEditor.Validate(entries.Single(e => e.Key == "Enabled"), "false") == "");
            Check(ConfigEditor.Validate(entries.Single(e => e.Key == "Enabled"), "yes").Length > 0);
            foreach (var invalid in new[] { "0", "101", "1.5", "NaN", "Infinity", "2147483648", "1,000" }) Check(ConfigEditor.Validate(entries.Single(e => e.Key == "Range"), invalid).Length > 0);
            Check(ConfigEditor.Validate(entries.Single(e => e.Key == "Effects"), "Ultra").Length > 0 && ConfigEditor.Validate(entries.Single(e => e.Key == "Greeting"), "one\nInjected=true").Length > 0);
        });
        Test("flags metadata permits only advertised combinations and unknown typed values remain read only", () =>
        {
            var doc = ConfigEditor.Parse("[Keys]\n# Setting type: ExampleFlags\n# Acceptable values: A, B, C\n# Multiple values can be set at the same time by separating them with ,\nFlags = A, B\n# Setting type: UnityEngine.Vector3\nPosition = 1,2,3\n");
            Check(doc.Entries[0].Kind == ConfigEditorKind.Flags && ConfigEditor.Validate(doc.Entries[0], "A, C") == "" && ConfigEditor.Validate(doc.Entries[0], "D").Length > 0);
            Check(!doc.Entries[1].Editable && doc.Entries[1].Problem.Length > 0);
        });
        Test("floating config values retain their actual type range instead of decimal precision limits", () =>
        {
            var doc = ConfigEditor.Parse("[Numbers]\n# Setting type: Double\nLarge = 1e100\n# Setting type: Single\nSmall = 1e-30\n");
            Check(ConfigEditor.Validate(doc.Entries[0], "1e100") == "" && ConfigEditor.Validate(doc.Entries[0], "1e500").Length > 0);
            Check(ConfigEditor.Validate(doc.Entries[1], "1e-30") == "" && ConfigEditor.Validate(doc.Entries[1], "1e100").Length > 0);
        });
        Test("duplicate config definitions are preserved as read-only rather than editing an ambiguous value", () =>
        {
            var doc = ConfigEditor.Parse("[General]\nValue = first\nValue = second\n");
            Check(doc.Entries.Count == 2 && doc.Entries.All(e => !e.Editable && e.Problem.Contains("Duplicate")));
        });
        Test("config list excludes global private unbound and unsupported files", () =>
        {
            var (root, _) = EditorFixture(false); Put(root, "ModGate/profiles/server-1/config/BepInEx.cfg", "Loader = true"); Put(root, "ModGate/profiles/server-1/config/credentials.cfg", "Password = secret"); Put(root, "ModGate/profiles/server-1/config/Other.cfg", "Value = true");
            Check(ConfigEditor.Files(root, "server-1").SequenceEqual(new[] { "Editor.Mod.cfg" }));
            foreach (var relative in new[] { "BepInEx.cfg", "credentials.cfg", "Other.cfg", "../Editor.Mod.cfg", "ModGate/server-mods.cfg", "Editor.Mod.json" }) Throws<InvalidDataException>(() => ConfigEditor.Open(root, "server-1", relative));
        });
        Test("inactive config edits are atomic backed up and retain exact comments line endings and secret values", () =>
        {
            var (root, path) = EditorFixture(false); var document = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            var result = ConfigEditor.Save(root, document, EditorValues(document, "Range", "35"));
            Check(result.Changed && !result.RestartRequired && !result.Deferred && File.ReadAllText(path) == EditorFixtureText.Replace("Range = 20", "Range = 35"));
            Check(File.ReadAllText(SafePath.Under(root, result.BackupPath)) == EditorFixtureText && !Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
        });
        Test("unchanged invalid existing values do not block editing another supported setting", () =>
        {
            var original = EditorFixtureText.Replace("Effects = Normal", "Effects = UnknownOldValue"); var (root, path) = EditorFixture(false, original); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            ConfigEditor.Save(root, doc, doc.Entries.ToDictionary(e => e.Id, e => e.Key == "Range" ? "25" : e.Value));
            Check(File.ReadAllText(path) == original.Replace("Range = 20", "Range = 25"));
        });
        Test("invalid config changes never write or create backups", () =>
        {
            var (root, path) = EditorFixture(false); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            Throws<InvalidDataException>(() => ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "1000")));
            Check(File.ReadAllText(path) == EditorFixtureText && !Directory.Exists(SafePath.Under(root, "ModGate/config-backups")));
        });
        Test("config edits reject stale documents after external changes or profile movement", () =>
        {
            var (root, path) = EditorFixture(false); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); File.AppendAllText(path, "# external\r\n");
            Throws<IOException>(() => ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "21")));
            Check(File.ReadAllText(path).EndsWith("# external\r\n", StringComparison.Ordinal));
            var other = EditorFixture(true); var activeDoc = ConfigEditor.Open(other.root, "server-1", "Editor.Mod.cfg"); var catalog = Profiles.Load(other.root); catalog.Active = "offline"; Json.Save(SafePath.Under(other.root, "ModGate/profiles.json"), catalog);
            Put(other.root, "ModGate/profiles/server-1/config/Editor.Mod.cfg", EditorFixtureText);
            Throws<IOException>(() => ConfigEditor.Save(other.root, activeDoc, EditorValues(activeDoc, "Range", "22")));
        });
        Test("active config edits queue until restart and reopening shows the queued values", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); var result = ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            Check(result.Changed && result.RestartRequired && result.Deferred && File.ReadAllText(path) == EditorFixtureText);
            var reopen = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); Check(reopen.Pending && reopen.Active && reopen.Entries.Single(e => e.Key == "Range").Value == "42");
            Check(ConfigEditor.ApplyPending(root, _ => { }) == 1 && File.ReadAllText(path) == EditorFixtureText.Replace("Range = 20", "Range = 42") && !ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending);
        });
        Test("queued config edits merge unrelated settings written by a mod during shutdown", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            File.WriteAllText(path, EditorFixtureText.Replace("Enabled = true", "Enabled = false") + "# shutdown saved\r\n");
            Check(ConfigEditor.ApplyPending(root, _ => { }) == 1);
            Check(File.ReadAllText(path) == EditorFixtureText.Replace("Enabled = true", "Enabled = false").Replace("Range = 20", "Range = 42") + "# shutdown saved\r\n");
        });
        Test("conflicting queued setting changes preserve current files and keep pending edits for review", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            var external = EditorFixtureText.Replace("Range = 20", "Range = 50"); File.WriteAllText(path, external); var messages = new List<string>();
            Check(ConfigEditor.ApplyPending(root, messages.Add) == 0 && File.ReadAllText(path) == external && ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending && messages.Any(m => m.Contains("changed externally")));
        });
        Test("pending edits can be revised or reverted and concurrent editors cannot overwrite them", () =>
        {
            var (root, _) = EditorFixture(true); var first = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); var stale = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            ConfigEditor.Save(root, first, EditorValues(first, "Range", "42")); Throws<IOException>(() => ConfigEditor.Save(root, stale, EditorValues(stale, "Range", "43")));
            ConfigEditor.Save(root, first, EditorValues(first, "Range", "44")); Check(ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Entries.Single(e => e.Key == "Range").Value == "44");
            ConfigEditor.Save(root, first, EditorValues(first, "Range", "20")); Check(!ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending && ConfigEditor.ApplyPending(root, _ => { }) == 0);
        });
        Test("UTF-8 BOM and UTF-16 config edits preserve their encoding and special text", () =>
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
            {
                var (root, path) = EditorFixture(false); File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(EditorFixtureText)).ToArray());
                var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Greeting", "雪 = café\\nnext")); var bytes = File.ReadAllBytes(path);
                Check(bytes.Take(encoding.GetPreamble().Length).SequenceEqual(encoding.GetPreamble()));
                Check(encoding.GetString(bytes.Skip(encoding.GetPreamble().Length).ToArray()) == EditorFixtureText.Replace("Greeting = café = ready\\nnow", "Greeting = 雪 = café\\nnext"));
            }
        });
        Test("config editor preserves LF CRLF mixed lines and files without a final newline", () =>
        {
            var text = "# first\n[Section]\r\nValue  =  original\r# last without newline"; var (root, path) = EditorFixture(false, text); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            ConfigEditor.Save(root, doc, EditorValues(doc, "Value", "new = text")); Check(File.ReadAllText(path) == text.Replace("Value  =  original", "Value  =  new = text"));
        });
        Test("pending profile changes block config writes and secret sections never appear in editable metadata", () =>
        {
            var (root, path) = EditorFixture(false, EditorFixtureText + "[Credentials]\nUser = private-user\n[General]\nApiKey = secret-key\n"); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg");
            Check(doc.Entries.All(e => e.Section != "Credentials" && e.Key != "ApiKey")); Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "server-1" });
            Throws<InvalidOperationException>(() => ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "22"))); Check(File.ReadAllText(path).Contains("Range = 20"));
        });
        Test("queued config edits follow the same server profile when a switch parks its config files", () =>
        {
            var (root, _) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            Profiles.Schedule(root, new ProfileCommand { Kind = "switch", Target = "offline" }); Profiles.Apply(root, _ => { });
            var parked = SafePath.Under(root, "ModGate/profiles/server-1/config/Editor.Mod.cfg");
            Check(Profiles.Load(root).Active == "offline" && File.ReadAllText(parked).Contains("Range = 20"));
            Check(ConfigEditor.ApplyPending(root, _ => { }) == 1 && File.ReadAllText(parked).Contains("Range = 42") && !File.Exists(SafePath.Under(root, "config/Editor.Mod.cfg")));
        });
        Test("queued config edits apply after a staged repair transaction without invalidating its state receipt", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            var repair = ProfileSnapshots.StageCurrent(root, "server-1");
            Profiles.Schedule(root, new ProfileCommand { Kind = "restore", Target = "server-1", Restore = repair }); Profiles.Apply(root, _ => { });
            Check(File.ReadAllText(path).Contains("Range = 20") && ConfigEditor.ApplyPending(root, _ => { }) == 1 && File.ReadAllText(path).Contains("Range = 42"));
        });
        Test("queued config edits never transfer to a different server replacing the same slot", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            var catalog = Profiles.Load(root); catalog.Servers.Single().ServerKey = new string('b', 64); Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var messages = new List<string>(); Check(ConfigEditor.ApplyPending(root, messages.Add) == 0 && File.ReadAllText(path).Contains("Range = 20") && messages.Any(m => m.Contains("identity changed")));
            Throws<IOException>(() => ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "43")));
            Throws<InvalidDataException>(() => ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"));
        });
        Test("explicit restore discards queued edits only after backing up their complete receipts", () =>
        {
            var (root, path) = EditorFixture(true); var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            Check(ConfigEditor.DiscardPending(root, "server-1") == 1 && ConfigEditor.ApplyPending(root, _ => { }) == 0 && File.ReadAllText(path) == EditorFixtureText);
            var backups = Directory.GetFiles(SafePath.Under(root, "ModGate/config-backups"), "*.json", SearchOption.AllDirectories);
            Check(backups.Length == 1); var receipt = Json.ReadLocal<ConfigEditorPending>(backups.Single()); Check(receipt.Slot == "server-1" && receipt.ProfileIdentity == new string('a', 64) && receipt.Changes.Single().Value == "42");
        });
        Test("canceling restore preparation preserves queued edits and leaves the active config unchanged", () =>
        {
            var (root, path) = EditorFixture(true); var snapshot = ProfileSnapshots.Capture(root, "server-1", "Working fixture", true);
            var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            Throws<OperationCanceledException>(() => ProfileSnapshots.PrepareRestore(root, "server-1", snapshot.Id, new System.Threading.CancellationToken(true)));
            Check(File.ReadAllText(path) == EditorFixtureText && ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending && !Profiles.Pending(root));
            Check(!Directory.Exists(SafePath.Under(root, "ModGate/config-backups")));
        });
        Test("snapshot restore journals queued edit removal so a failed move rolls back and a later commit replaces it", () =>
        {
            var (root, path) = EditorFixture(true); var snapshot = ProfileSnapshots.Capture(root, "server-1", "Working fixture", true);
            var doc = ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg"); ConfigEditor.Save(root, doc, EditorValues(doc, "Range", "42"));
            var restore = ProfileSnapshots.PrepareRestore(root, "server-1", snapshot.Id); Profiles.Schedule(root, new ProfileCommand { Kind = "restore", Target = "server-1", Restore = restore });
            var faultedAfterQueueMove = false;
            Throws<IOException>(() => Profiles.Apply(root, _ => { }, _ =>
            {
                var journal = Json.ReadLocal<ProfileJournal>(SafePath.Under(root, "ModGate/profile-journal.json"));
                if (journal.Moves.Last().Source.StartsWith("ModGate/config-edits/pending/", StringComparison.Ordinal)) { faultedAfterQueueMove = true; throw new IOException("fixture interruption after queued edit move"); }
            }));
            Check(faultedAfterQueueMove && ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending && File.ReadAllText(path) == EditorFixtureText);
            Check(Directory.GetFiles(SafePath.Under(root, "ModGate/config-backups"), "*.json", SearchOption.AllDirectories).Any());
            Profiles.Apply(root, _ => { });
            Check(!ConfigEditor.Open(root, "server-1", "Editor.Mod.cfg").Pending && ConfigEditor.ApplyPending(root, _ => { }) == 0 && File.ReadAllText(path) == EditorFixtureText);
        });
    }
}
