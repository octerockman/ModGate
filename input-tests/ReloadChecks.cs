using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using ModGate;

internal static partial class Program
{
    private static void ReloadChecks()
    {
        Test("runtime reload reads an active bound cfg without calling Save", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("allowed.cfg"); fixture.Put("config/allowed.cfg", "Value = disk\n");
            var config = fixture.AddPlugin("fixture.allowed", "config/allowed.cfg"); var read = "";
            config.OnReload = () => read = File.ReadAllText(config.ConfigFilePath);
            var result = fixture.Plugin.ReloadProfile("offline");
            Check(config.ReloadCalls == 1 && config.SaveCalls == 0 && read == "Value = disk\n" && result.Contains("Reloaded 1"));
        });
        Test("inactive profile refresh does not reload the running profile", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("allowed.cfg"); fixture.Put("config/allowed.cfg", "Value = active\n");
            var config = fixture.AddPlugin("fixture.allowed", "config/allowed.cfg");
            var catalog = Profiles.Load(fixture.Root);
            catalog.Servers.Add(new SavedProfile { Slot = "server-1", Name = "Saved", ServerKey = new string('a', 64), Mods = new List<ModEntry> { new ModEntry { Guid = "fixture.saved", Name = "Saved", Version = "1.0.0" } } });
            Json.Save(fixture.Path("ModGate/profiles.json"), catalog);
            var result = fixture.Plugin.ReloadProfile("server-1");
            Check(config.ReloadCalls == 0 && config.SaveCalls == 0 && result.Contains("Activate this profile"));
        });
        Test("runtime reload excludes unbound global private connector and outside-root config files", () => {
            using var fixture = new ReloadFixture();
            fixture.Bind("allowed.cfg", "BepInEx.cfg", "studio.valheim.modgate.cfg", "passwords.cfg", "ModGate/private.cfg");
            foreach (var path in new[] { "config/allowed.cfg", "config/unbound.cfg", "config/BepInEx.cfg", "config/studio.valheim.modgate.cfg", "config/passwords.cfg", "config/ModGate/private.cfg", "outside/allowed.cfg", "config-neighbor/allowed.cfg" }) fixture.Put(path, "Value = original\n");
            var allowed = fixture.AddPlugin("fixture.allowed", "config/allowed.cfg");
            var rejected = new[] {
                fixture.AddPlugin("fixture.unbound", "config/unbound.cfg"), fixture.AddPlugin("fixture.loader", "config/BepInEx.cfg"),
                fixture.AddPlugin("fixture.global", "config/studio.valheim.modgate.cfg"), fixture.AddPlugin("fixture.private", "config/passwords.cfg"),
                fixture.AddPlugin("fixture.private-folder", "config/ModGate/private.cfg"), fixture.AddPlugin(Constants.Guid, "config/allowed.cfg"),
                fixture.AddPlugin("fixture.outside", "outside/allowed.cfg"), fixture.AddPlugin("fixture.neighbor", "config-neighbor/allowed.cfg") };
            fixture.Plugin.ReloadProfile("offline");
            Check(allowed.ReloadCalls == 1 && allowed.SaveCalls == 0 && rejected.All(config => config.ReloadCalls == 0 && config.SaveCalls == 0));
        });
        Test("two loaded plugins sharing a normalized config path reload that file once", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("allowed.cfg"); fixture.Put("config/allowed.cfg", "Value = shared\n");
            var first = fixture.AddPlugin("fixture.first", "config/allowed.cfg");
            var second = fixture.AddPlugin("fixture.second", "config/./allowed.cfg");
            var result = fixture.Plugin.ReloadProfile("offline");
            Check(first.ReloadCalls + second.ReloadCalls == 1 && first.SaveCalls + second.SaveCalls == 0 && result.Contains("Reloaded 1"));
        });
        Test("runtime reload keeps local and server queued edits byte-identical and reads current disk values", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("allowed.cfg");
            fixture.Put("config/allowed.cfg", "[General]\n# Setting type: Int32\nRange = 3\n");
            var local = ConfigEditor.Open(fixture.Root, "offline", "allowed.cfg");
            var values = local.Entries.ToDictionary(entry => entry.Id, entry => entry.Key == "Range" ? "8" : entry.Value);
            Check(ConfigEditor.Save(fixture.Root, local, values).RestartRequired);
            var server = ServerConfigFiles.Read(fixture.Root, "allowed.cfg", true);
            ServerConfigFiles.Save(fixture.Root, new AdminConfigRequest { Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "save", Path = server.Path,
                ExpectedHash = server.Hash, Changes = new List<AdminConfigChange> { new AdminConfigChange { Id = server.Entries.Single().Id, Value = "9" } } }, true);
            var disk = File.ReadAllBytes(fixture.Path("config/allowed.cfg"));
            var receipts = Directory.GetFiles(fixture.Path("ModGate"), "*.json", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            Check(receipts.Keys.Any(path => path.Contains("config-edits")) && receipts.Keys.Any(path => path.Contains("server-config-edits")));
            var config = fixture.AddPlugin("fixture.allowed", "config/allowed.cfg"); var actual = "";
            config.OnReload = () => actual = File.ReadAllText(config.ConfigFilePath);
            fixture.Plugin.ReloadProfile("offline");
            Check(config.ReloadCalls == 1 && config.SaveCalls == 0 && actual.Contains("Range = 3"));
            Check(File.ReadAllBytes(config.ConfigFilePath).SequenceEqual(disk) && receipts.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)));
            Check(ConfigEditor.Open(fixture.Root, "offline", "allowed.cfg").Entries.Single().Value == "8" && ServerConfigFiles.Read(fixture.Root, "allowed.cfg", true).Entries.Single().Value == "9");
        });
        Test("reload callbacks can change loaded plugins without invalidating the reload iteration", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("first.cfg", "second.cfg"); fixture.Put("config/first.cfg", "Value = first\n"); fixture.Put("config/second.cfg", "Value = second\n");
            var first = fixture.AddPlugin("fixture.first", "config/first.cfg"); var second = fixture.AddPlugin("fixture.second", "config/second.cfg");
            ConfigFile? added = null;
            first.OnReload = () => { Chainloader.PluginInfos.Clear(); added = fixture.AddPlugin("fixture.added", "config/first.cfg"); };
            var result = fixture.Plugin.ReloadProfile("offline");
            Check(first.ReloadCalls == 1 && second.ReloadCalls == 1 && added?.ReloadCalls == 0 && result.Contains("Reloaded 2"));
        });
        Test("a failing mod reload is reported without leaking error values or preventing other reloads", () => {
            using var fixture = new ReloadFixture(); fixture.Bind("bad.cfg", "good.cfg"); fixture.Put("config/bad.cfg", "Value = bad\n"); fixture.Put("config/good.cfg", "Value = good\n");
            var bad = fixture.AddPlugin("fixture.bad", "config/bad.cfg"); var good = fixture.AddPlugin("fixture.good", "config/good.cfg");
            bad.OnReload = () => throw new InvalidOperationException("private-fixture-credential");
            var result = fixture.Plugin.ReloadProfile("offline");
            Check(bad.ReloadCalls == 1 && good.ReloadCalls == 1 && bad.SaveCalls + good.SaveCalls == 0 && result.Contains("Reloaded 1") && result.Contains("1 failed"));
            Check(fixture.Plugin.Logger.Warnings.Count == 1 && fixture.Plugin.Logger.Warnings.All(value => !value.Contains("private-fixture-credential")) && !result.Contains("private-fixture-credential"));
        });
        Test("missing allowed config files are skipped without saving or reloading", () => {
            using var fixture = new ReloadFixture(); var config = fixture.AddPlugin("fixture.missing", "config/missing.cfg");
            var result = fixture.Plugin.ReloadFiles("missing.cfg");
            Check(config.ReloadCalls == 0 && config.SaveCalls == 0 && result.Contains("Reloaded 0"));
        });
        Test("linked config files are rejected before a reload callback can follow them", () => {
            using var fixture = new ReloadFixture(); fixture.Put("outside/target.cfg", "Value = protected\n");
            try { File.CreateSymbolicLink(fixture.Path("config/linked.cfg"), fixture.Path("outside/target.cfg")); }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException || error is PlatformNotSupportedException)
            { throw new OptionalFixtureUnavailable("symbolic-link creation is unavailable in this environment"); }
            var config = fixture.AddPlugin("fixture.linked", "config/linked.cfg"); fixture.Plugin.ReloadFiles("linked.cfg");
            Check(config.ReloadCalls == 0 && config.SaveCalls == 0 && File.ReadAllText(fixture.Path("outside/target.cfg")) == "Value = protected\n");
        });
    }

    private sealed class ReloadFixture : IDisposable
    {
        private static readonly string SuiteRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModGate.Input.Tests"));
        public string Root { get; } = System.IO.Path.Combine(SuiteRoot, Guid.NewGuid().ToString("N"));
        public Plugin Plugin { get; }
        public ReloadFixture()
        {
            Directory.CreateDirectory(Path("config")); Paths.BepInExRootPath = Root; Plugin = new Plugin(new Prompt()); Bind();
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Put(string relative, string contents)
        { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
        public void Bind(params string[] files)
        {
            Json.Save(Path("ModGate/profiles.json"), new ProfileCatalog {
                OfflineMods = new List<ModEntry> { new ModEntry { Guid = "fixture.profile", Name = "Fixture", Version = "1.0.0" } },
                OfflineBindings = new List<ConfigBinding> { new ConfigBinding { Guid = "fixture.profile", Files = files.ToList() } }
            });
        }
        public ConfigFile AddPlugin(string guid, string relative)
        {
            var config = new ConfigFile(Path(relative)); var plugin = new BaseUnityPlugin(guid, config);
            Chainloader.PluginInfos.Add(guid, plugin.Info); return config;
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Root);
            if (!resolved.StartsWith(SuiteRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || resolved == SuiteRoot)
                throw new InvalidOperationException("Refusing to remove a fixture outside the test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
