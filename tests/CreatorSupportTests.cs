using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ModGate;

internal static partial class Program
{
    private static void CreatorSupportChecks()
    {
        var root = Root();
        var catalog = new ProfileCatalog { Active = "offline", Servers = new List<SavedProfile> { new SavedProfile { Slot = "server-1", Name = "First" }, new SavedProfile { Slot = "server-2", Name = "Empty" } } };
        var files = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        ModEntry Add(string path, string guid, string name, string package = "", string nexus = "")
        {
            var file = Put(root, path, "metadata fixture only");
            var mod = new ModEntry { Guid = guid, Name = name, Version = "1.0.0", Package = package, NexusUrl = nexus, LocalFile = file };
            files[file] = mod; return mod;
        }
        List<ModEntry> Inspect(string path) => files.Where(f => File.Exists(f.Key) && (f.Key == path || f.Key.StartsWith(path.TrimEnd('\\','/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Select(f => f.Value).ToList();
        CreatorSupportResult Read(bool all = false) => CreatorSupport.Read(root, catalog, all, Inspect, CancellationToken.None);
        var pack = Add("plugins/Bascinet-OctoPack-1.0.0/pack.dll", "pack", "OctoPack", "Bascinet-OctoPack-1.0.0");
        Add("plugins/Other-Apple-1.0.0/apple.dll", "apple", "Apple", "Other-Apple-1.0.0");
        Add("ModGate/profiles/server-1/plugins/ValheimModGate.Active/Bascinet-OctoPack/pack.dll", "pack", "OctoPack", "hexium:Bascinet-OctoPack-2.0.0");
        Add("ModGate/profiles/server-1/plugins/Other-Banana-1.0.0/banana.dll", "banana", "Banana", "Other-Banana-1.0.0");
        catalog.Servers[0].Mods.Add(new ModEntry { Guid = "removed", Name = "Removed history", Package = "Other-Removed-1.0.0" });
        Add("ModGate/restore-points/old/plugins/Old/old.dll", "old", "Old restore point");
        Test("creator support defaults to physical active-profile mods, pins ModGate and excludes saved requirements and restore history", () =>
        {
            var result = Read(); Check(result.Mods.Select(m => m.Name).SequenceEqual(new[] { "ModGate", "OctoPack", "Apple" }));
            Check(result.Mods[0].Links.All(l => l.Host != "Thunderstore") && result.Mods[0].Links.Count == 3);
        });
        Test("all-profile support combines verified Bascinet package identities across versions and hosts", () =>
        {
            var result = Read(true); var octo = result.Mods.Single(m => m.Name == "OctoPack");
            Check(result.Mods.Count == 4 && octo.Profiles.SetEquals(new[] { "offline", "server-1" }) && octo.Links.Count == 4);
            Check(result.Mods.Select(m => m.Name).SequenceEqual(new[] { "ModGate", "OctoPack", "Apple", "Banana" }));
        });
        Test("creator support never promotes similar names or merges different authors and packages", () =>
        {
            Add("plugins/Imposter-OctoPack-1.0.0/fake.dll", "fake", "OctoPack", "Imposter-OctoPack-1.0.0");
            Add("plugins/Other-AppleAddon-1.0.0/addon.dll", "apple-addon", "Apple", "Other-AppleAddon-1.0.0");
            var result = Read(); Check(result.Mods.Count(m => m.Name == "OctoPack") == 2 && result.Mods.Count(m => m.Name == "Apple") == 2);
            Check(!result.Mods.Single(m => m.Id.Contains("Imposter")).Bascinet);
        });
        Test("only explicit package sources produce host pages and shared repositories do not merge different packages", () =>
        {
            var first = Add("plugins/Team-First-1.0.0/first.dll", "first", "First", "Team-First-1.0.0");
            var second = Add("plugins/Team-Second-1.0.0/second.dll", "second", "Second", "Team-Second-1.0.0");
            first.ManifestSource = second.ManifestSource = new ManifestOrigin { Website = "https://github.com/Team/Monorepo" };
            var entries = Read().Mods.Where(m => m.Author == "Team").ToArray();
            Check(entries.Length == 2 && entries.All(m => m.Links.Count == 2 && m.Links.All(l => l.Host != "Hexium")));
        });
        Test("explicit legacy Lantern Arm remains separate from the current Lantern add-on", () =>
        {
            Add("plugins/Current/lantern.dll", "current-lantern", "Lantern", "Bascinet-OctoPack_addon_Lantern-1.0.0");
            Add("plugins/Legacy/arm.dll", "legacy-arm", "Lantern Arm", nexus: "https://www.nexusmods.com/valheim/mods/4487");
            var entries = Read().Mods.Where(m => m.Name.Contains("Lantern")).ToArray();
            Check(entries.Length == 2 && entries.Single(m => m.Name.Contains("legacy")).Links.Count == 1);
            Check(entries.Single(m => !m.Name.Contains("legacy")).Links.Any(l => l.Url.EndsWith("4428")));
        });
        Test("deleting installed files removes their support entry even when profile metadata remains", () =>
        {
            var file = files.Values.Single(m => m.Guid == "banana").LocalFile; File.Delete(file);
            Check(Read(true).Mods.All(m => m.Name != "Banana"));
            Check(CreatorSupport.Read(Root(), new ProfileCatalog(), false, Inspect, CancellationToken.None).Mods.Count == 1);
        });
        Test("disabled profile payloads count as installed while missing disabled files do not", () =>
        {
            var group = new ProfileEditGroup { PluginPath = "Disabled" }; catalog.OfflineDisabledGroups.Add(group);
            Add(ProfileEditing.DisabledPath("offline", group.PluginPath) + "/disabled.dll", "disabled", "Disabled mod");
            Check(Read().Mods.Any(m => m.Name == "Disabled mod"));
            File.Delete(files.Values.Single(m => m.Guid == "disabled").LocalFile); Check(Read().Mods.All(m => m.Name != "Disabled mod"));
        });
        Test("unreadable payloads are isolated and cancellation does not return a misleading partial list", () =>
        {
            var result = CreatorSupport.Read(root, catalog, false, path => path.Contains("Apple") ? throw new IOException("fixture") : Inspect(path), CancellationToken.None);
            Check(result.Skipped > 0 && result.Mods.Any(m => m.ModGate));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Throws<OperationCanceledException>(() => CreatorSupport.Read(root, catalog, true, Inspect, cancelled.Token));
        });
        Test("support links accept only canonical public mod pages and never API, download, sign-in or lookalike destinations", () =>
        {
            Check(CreatorSupport.Page("https://www.nexusmods.com/valheim/mods/4412?tab=files")!.Url.EndsWith("4412"));
            Check(CreatorSupport.Page("https://github.com/octerockman/OctoPack.git")!.Url == "https://github.com/octerockman/OctoPack");
            foreach (var value in new[] { "http://github.com/a/b", "https://github.com.evil.test/a/b", "https://user@github.com/a/b", "https://github.com/login/device", "https://api.github.com/user/starred/a/b", "https://thunderstore.io/package/download/A/B/1.0.0/", "https://nexusmods.com/skyrim/mods/1", "https://valheim.hexium.gg/api/v1/package/123/rate/", "https://github.com:444/a/b", "https://github.com/a/%62" }) Check(CreatorSupport.Page(value) == null);
        });
        Test("local icons are bounded PNGs within the installation and originals remain unchanged", () =>
        {
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZlS8AAAAASUVORK5CYII=");
            var path = Path.Combine(Path.GetDirectoryName(pack.LocalFile)!, "icon.png"); File.WriteAllBytes(path, bytes);
            Check(Read().Mods.Single(m => m.Id == "bascinet:4412").Icons.Contains(path));
            Check(CreatorSupport.IconBytes(root,path)!.SequenceEqual(bytes) && File.ReadAllBytes(path).SequenceEqual(bytes));
            var wide = bytes.ToArray(); wide[16] = 1; File.WriteAllBytes(path,wide); Check(CreatorSupport.IconBytes(root,path) == null);
            File.WriteAllBytes(path,new byte[1024 * 1024 + 1]); Check(CreatorSupport.IconBytes(root,path) == null);
            Check(CreatorSupport.IconBytes(root, Put(Root(),"external.png","outside")) == null);
        });
    }
}
