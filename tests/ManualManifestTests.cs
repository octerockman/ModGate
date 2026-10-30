using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ModGate;

internal static partial class Program
{
    private static void ManualManifestChecks()
    {
        (string root, string plugins, string config, List<ModEntry> mods, List<string> logs) Fixture(params (string guid, string file)[] plugins)
        {
            var root = Root();
            var mods = plugins.Select(p =>
            {
                var mod = Mod(p.guid);
                mod.LocalFile = Put(root, "plugins/" + p.file, "fixture DLL bytes; never executed");
                return mod;
            }).ToList();
            return (root, Path.Combine(root, "plugins"), Path.Combine(root, "config"), mods, new List<string>());
        }
        string Text(string name = "Package", string version = "1.0.0") => Json.Write(new PackageManifest
        {
            Name = name, Version = version, Website = "https://thunderstore.io/c/valheim/p/Author/" + name + "/"
        });
        string WriteManifest(string config, ModEntry mod, IReadOnlyList<ModEntry> inventory, string text)
        {
            var folder = LocalManifestReader.ServerManifestFolder(config, mod.Guid, inventory);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "manifest.json");
            File.WriteAllText(file, text);
            return file;
        }

        Test("server manifest setup creates instructions only for discovered loose plugins", () =>
        {
            var f = Fixture(("author.first", "First.dll"), ("author.second", "Second.dll"), ("nested", "Bundle/Nested.dll"), (Constants.Guid, "ModGate.dll"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            var folder = Path.Combine(f.config, "ModGate", "manifests");
            Check(File.Exists(Path.Combine(folder, "README.txt")));
            Check(Directory.GetDirectories(folder).Length == 2);
            foreach (var guid in new[] { "author.first", "author.second" })
                Check(File.Exists(Path.Combine(folder, guid, "README.txt")));
            Check(!Directory.Exists(Path.Combine(folder, "nested")) && !Directory.Exists(Path.Combine(folder, Constants.Guid)));
            Check(Directory.GetFiles(folder, "manifest.json", SearchOption.AllDirectories).Length == 0 && f.mods.All(m => m.LocalManifest == null));
        });

        Test("server fallback is scoped to its exact GUID and ignores a shared plugins manifest", () =>
        {
            var f = Fixture(("author.first", "First.dll"), ("author.second", "Second.dll"));
            Put(f.root, "plugins/manifest.json", Text("WrongShared"));
            foreach (var mod in f.mods) mod.LocalManifest = LocalManifestReader.Read(mod.LocalFile, f.plugins);
            Check(f.mods.All(m => m.LocalManifest == null));
            WriteManifest(f.config, f.mods[0], f.mods, Text("FirstPackage"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(f.mods[0].LocalManifest?.Name == "FirstPackage" && f.mods[1].LocalManifest == null);
        });

        Test("a valid nearby package manifest takes priority over a conflicting server fallback", () =>
        {
            var f = Fixture(("author.mod", "Bundle/Mod.dll"));
            Put(f.root, "plugins/Bundle/manifest.json", Text("NearbyPackage", "2.0.0"));
            var mod = f.mods.Single();
            mod.LocalManifest = LocalManifestReader.Read(mod.LocalFile, f.plugins);
            var nearby = mod.LocalManifest;
            WriteManifest(f.config, mod, f.mods, Text("WrongFallback", "9.0.0"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(nearby != null && ReferenceEquals(mod.LocalManifest, nearby) && mod.LocalManifest.Name == "NearbyPackage");
        });

        Test("nested installed plugins can use fallback without generating unrelated nested folders", () =>
        {
            var f = Fixture(("author.mod", "Bundle/Mod.dll"), ("author.other", "OtherBundle/Other.dll"));
            Put(f.root, "plugins/Bundle/manifest.json", "{}");
            f.mods[0].LocalManifest = LocalManifestReader.Read(f.mods[0].LocalFile, f.plugins);
            Check(f.mods[0].LocalManifest == null);
            WriteManifest(f.config, f.mods[0], f.mods, Text("RecoveredPackage"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(f.mods[0].LocalManifest?.Name == "RecoveredPackage" && f.mods[1].LocalManifest == null);
            Check(!Directory.Exists(LocalManifestReader.ServerManifestFolder(f.config, f.mods[1].Guid, f.mods)));
        });

        Test("clientOnly fallback preserves DLL identity and required version through reconciliation", () =>
        {
            var f = Fixture(("author.visual", "Visual.dll"));
            var mod = f.mods.Single();
            mod.Name = "Actual Visual Plugin"; mod.Version = "2.7.8"; mod.Side = "clientOnly";
            var before = Json.Write(mod); var dll = mod.LocalFile;
            WriteManifest(f.config, mod, f.mods, Text("VisualPackage", "8.9.0"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(Json.Write(mod) == before && mod.LocalFile == dll && mod.LocalManifest?.Version == "8.9.0");
            ManifestCandidates.Enrich(f.mods);
            var previous = Mod(mod.Guid, "clientOnly"); previous.Automatic = true;
            var row = AutoSetup.Reconcile(new ServerManifest { Mods = new List<ModEntry> { previous } }, f.mods, Array.Empty<string>()).Mods.Single();
            Check(row.Guid == "author.visual" && row.Name == "Actual Visual Plugin" && row.Version == "2.7.8" && row.Side == "clientOnly" && row.Package == "Author-VisualPackage-8.9.0");
        });

        Test("malformed oversized and unreadable fallbacks log failures without stopping other mods", () =>
        {
            var f = Fixture(("bad", "Bad.dll"), ("large", "Large.dll"), ("locked", "Locked.dll"), ("null", "Null.dll"), ("good", "Good.dll"));
            WriteManifest(f.config, f.mods[0], f.mods, "{}");
            WriteManifest(f.config, f.mods[1], f.mods, new string('x', 64 * 1024 + 1));
            var locked = WriteManifest(f.config, f.mods[2], f.mods, Text("Locked"));
            WriteManifest(f.config, f.mods[3], f.mods, "null");
            WriteManifest(f.config, f.mods[4], f.mods, Text("Good"));
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(f.mods.Take(4).All(m => m.LocalManifest == null) && f.mods[4].LocalManifest?.Name == "Good" && f.logs.Count >= 4);
        });

        Test("repeated server manifest setup preserves owner instructions and manifest contents", () =>
        {
            var f = Fixture(("author.mod", "Mod.dll"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            var rootReadme = Path.Combine(f.config, "ModGate", "manifests", "README.txt");
            var pluginReadme = Path.Combine(LocalManifestReader.ServerManifestFolder(f.config, f.mods[0].Guid, f.mods), "README.txt");
            File.WriteAllText(rootReadme, "Owner root instructions\n"); File.WriteAllText(pluginReadme, "Owner plugin instructions\n");
            var text = Text("OwnerPackage"); var manifest = WriteManifest(f.config, f.mods[0], f.mods, text);
            for (var i = 0; i < 2; i++) LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(File.ReadAllText(rootReadme) == "Owner root instructions\n" && File.ReadAllText(pluginReadme) == "Owner plugin instructions\n" && File.ReadAllText(manifest) == text);
        });

        Test("unsafe and reserved GUIDs get distinct deterministic contained manifest directories", () =>
        {
            var f = Fixture();
            var root = Path.GetFullPath(Path.Combine(f.config, "ModGate", "manifests")) + Path.DirectorySeparatorChar;
            Check(LocalManifestReader.ServerManifestFolder(f.config, "author.Safe-Mod_1") == Path.Combine(root, "author.Safe-Mod_1"));
            var unsafeGuids = new[] { "../outside", "a/b", "a\\b", "a:b", "trailing.", "trailing ", "CON", "aux.txt", "COM1", ".." };
            var paths = unsafeGuids.Select(guid => LocalManifestReader.ServerManifestFolder(f.config, guid)).ToArray();
            for (var i = 0; i < paths.Length; i++)
                Check(paths[i].StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(paths[i]) == root.TrimEnd(Path.DirectorySeparatorChar) && Regex.IsMatch(Path.GetFileName(paths[i]), "^guid-[A-Fa-f0-9]{64}$") && paths[i] == LocalManifestReader.ServerManifestFolder(f.config, unsafeGuids[i]));
            Check(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == unsafeGuids.Length);
        });

        Test("case-colliding GUIDs receive stable separate folders without sharing fallback contents", () =>
        {
            var f = Fixture(("Author.Mod", "First.dll"), ("author.mod", "Second.dll"));
            var first = LocalManifestReader.ServerManifestFolder(f.config, f.mods[0].Guid, f.mods);
            var second = LocalManifestReader.ServerManifestFolder(f.config, f.mods[1].Guid, f.mods);
            Check(!first.Equals(second, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(first).StartsWith("guid-") && Path.GetFileName(second).StartsWith("guid-"));
            Check(first == LocalManifestReader.ServerManifestFolder(f.config, f.mods[0].Guid, f.mods.AsEnumerable().Reverse().ToArray()));
            WriteManifest(f.config, f.mods[0], f.mods, Text("OnlyFirst"));
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(f.mods[0].LocalManifest?.Name == "OnlyFirst" && f.mods[1].LocalManifest == null);
        });

        Test("an unavailable manifest directory is nonfatal and never replaces the blocking file", () =>
        {
            var f = Fixture(("author.mod", "Mod.dll"));
            var blocking = Put(f.root, "config/ModGate/manifests", "Owner file");
            LocalManifestReader.ApplyServerManifests(f.mods, f.plugins, f.config, f.logs.Add);
            Check(File.ReadAllText(blocking) == "Owner file" && f.mods[0].LocalManifest == null && f.logs.Count > 0);
        });
    }
}
