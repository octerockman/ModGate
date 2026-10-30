using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModGate;

internal static partial class Program
{
    private static void ServerConfigChecks()
    {
        Test("owner-facing server config omits only generated DLL dependencies and preserves settings", () =>
        {
            var mod = Mod("visual*", "clientOnly*", package: "A-Visual-1.0.0*");
            mod.Name = "Custom* name"; mod.DownloadUrl = "https://thunderstore.io/package/download/A/Visual/1.0.0/*"; mod.AllowNewerVersion = true; mod.AutoUpdate = false;
            mod.Dependencies.Add(new PluginDependency { Guid = "kg.FindOutdatedStuff", Optional = true });
            var settings = new ServerManifest { ServerName = "My* server", AllowExtraClientMods = true, AllowNewerClientVersions = true, AutoUpdateOnRestart = true, Mods = new List<ModEntry> { mod } };
            var before = Json.Write(settings); var text = ServerConfig.WriteReadable(settings);
            Check(!text.Contains("\"dependencies\"") && !text.Contains("kg.FindOutdatedStuff") && Json.Write(settings) == before);
            var view = Json.Read<ServerManifest>(text); var next = view.Mods.Single();
            Check(next.Guid == mod.Guid && next.Name == mod.Name && next.Side == mod.Side && next.DownloadUrl == mod.DownloadUrl && next.AllowNewerVersion == true && next.AutoUpdate == false);
            Check(view.ServerName == settings.ServerName && view.AllowNewerClientVersions && view.AutoUpdateOnRestart);
        });
        Test("generated metadata survives save and reload outside the owner's mod list", () =>
        {
            var root = Root(); var path = SafePath.Under(root, "config/ModGate/server-mods.json");
            var parent = Mod("teleport", package: "A-Teleport-1.0.0"); parent.Dependencies.Add(new PluginDependency { Guid = "boats", Optional = true, Package = "A-Boats-1.0.0" });
            var child = Mod("boats", "optional", package: "A-Boats-1.0.0");
            var settings = new ServerManifest { Mods = new List<ModEntry> { parent, child } };
            ServerConfig.Save(path, root, settings);
            Check(!File.ReadAllText(path).Contains("\"dependencies\"") && File.Exists(SafePath.Under(root, ServerConfig.CachePath)) && parent.Dependencies.Count == 1);
            var loaded = ServerConfig.Load(path, root); var policy = PolicyEngine.Publish(loaded, Array.Empty<ModEntry>());
            Check(DependencyRules.Modules(policy, policy.Mods.First(m => m.Guid == "teleport")).Single().Guid == "boats");
            var time = File.GetLastWriteTimeUtc(path); ServerConfig.Save(path, root, loaded); Check(File.GetLastWriteTimeUtc(path) == time);
        });
        Test("legacy inline dependency fields migrate automatically while preserving locked metadata", () =>
        {
            var root = Root(); var path = SafePath.Under(root, "config/ModGate/server-mods.json");
            var mod = Mod("root"); mod.Dependencies.Add(new PluginDependency { Guid = "soft*", Optional = true, MinimumVersion = "1.0*" });
            Json.Save(path, new ServerManifest { Mods = new List<ModEntry> { mod } }, true);
            var legacy = ServerConfig.Load(path, root); ServerConfig.Save(path, root, legacy);
            Check(!File.ReadAllText(path).Contains("soft*") && !File.ReadAllText(path).Contains("\"dependencies\""));
            var loaded = ServerConfig.Load(path, root); Check(loaded.Mods.Single().Dependencies.Single().Guid == "soft*");
            var effective = ConfigFields.Runtime(loaded); Check(effective.Mods.Single().Dependencies.Single().Guid == "soft" && effective.Mods[0].Dependencies[0].MinimumVersion == "1.0");
        });
        Test("stale or broken dependency cache never adds mod entries or overrides inline declarations", () =>
        {
            var root = Root(); var path = SafePath.Under(root, "config/ModGate/server-mods.json");
            var mod = Mod("root"); mod.Dependencies.Add(new PluginDependency { Guid = "old", Optional = true });
            ServerConfig.Save(path, root, new ServerManifest { Mods = new List<ModEntry> { mod, Mod("removed") } });
            var updated = Mod("root", version: "2.0.0"); Json.Save(path, new ServerManifest { Mods = new List<ModEntry> { updated } });
            var loaded = ServerConfig.Load(path, root); Check(loaded.Mods.Count == 1 && !DependencyRules.Of(loaded.Mods[0]).Any());
            updated.Dependencies.Add(new PluginDependency { Guid = "explicit", Optional = false }); Json.Save(path, new ServerManifest { Mods = new List<ModEntry> { updated } });
            Check(ServerConfig.Load(path, root).Mods.Single().Dependencies.Single().Guid == "explicit");
            File.WriteAllText(SafePath.Under(root, ServerConfig.CachePath), "broken fixture"); Check(ServerConfig.Load(path, root).Mods.Count == 1);
        });
        Test("owner config cleanup retains hard and soft dependencies in the published policy", () =>
        {
            var root = Root(); var path = SafePath.Under(root, "config/ModGate/server-mods.json");
            var parent = Mod("parent"); parent.Dependencies.Add(new PluginDependency { Guid = "lib", Optional = false, MinimumVersion = "1.0.0" }); parent.Dependencies.Add(new PluginDependency { Guid = "not-installed", Optional = true });
            var settings = new ServerManifest { Mods = new List<ModEntry> { parent, Mod("lib", "optional") } };
            ServerConfig.Save(path, root, settings); var policy = PolicyEngine.Publish(ServerConfig.Load(path, root), Array.Empty<ModEntry>());
            Check(!File.ReadAllText(path).Contains("not-installed") && !DependencyRules.Required(policy, new HashSet<string>()).Contains("lib"));
            Check(policy.Mods.Single(m => m.Guid == "parent").Dependencies.Count == 2 && Json.Read<Policy>(Json.Write(policy)).Mods.Single(m => m.Guid == "parent").Dependencies.Count == 2);
        });
    }
}
