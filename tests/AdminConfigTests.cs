using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ModGate;

internal static partial class Program
{
    private static void AdminConfigChecks()
    {
        const string cfg = "[General]\r\n## Enable the feature\r\n# Setting type: Boolean\r\n# Default value: false\r\nEnabled = false\r\n# Setting type: Int32\r\n# Acceptable value range: From 1 to 10\r\nRange = 3\r\n# Setting type: Vector3\r\nComplex = 1,2,3\r\nOther = retained\r\nPassword = hidden-fixture-password\r\n[Secret]\r\nInnocentName = hidden-fixture-token\r\n; untouched trailing comment\r\n";
        string Fixture()
        {
            var root = Root(); Put(root, "config/Example.cfg", cfg);
            Put(root, "config/ModGate/server-mods.json", Json.WriteReadable(new ServerManifest { Mods = new List<ModEntry> { Mod("actual.guid") } }));
            return root;
        }
        AdminConfigRequest SaveRequest(AdminConfigDocument document, params (string key, string value)[] fields) => new AdminConfigRequest
        {
            Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "save", Path = document.Path, ExpectedHash = document.Hash,
            Changes = fields.Select(f => new AdminConfigChange { Id = document.Entries.Single(e => e.Key == f.key).Id, Value = f.value }).ToList()
        };
        AdminConfigRequest JsonRequest(AdminConfigDocument document, string text) => new AdminConfigRequest { Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "save", Path = document.Path, ExpectedHash = document.Hash, Text = text };

        Test("admin config protocol validates identities operations field patches and canonical relative paths", () =>
        {
            var request = new AdminConfigRequest { Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "read", Path = "Example.cfg" };
            Check(AdminConfigProtocol.ReadRequest(Json.Write(request)).Path == "Example.cfg");
            foreach (var path in new[] { "../outside.cfg", "a\\b.cfg", "/absolute.cfg", "C:/escape.cfg", "a%2fb.cfg", "file.cfg:secret", "CON.cfg", "a./file.cfg", "BepInEx.cfg", "studio.valheim.modgate.cfg", "ModGate/private.cfg", "passwords.cfg", "a.json" })
            {
                request.Path = path; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request));
            }
            request.Path = "Example.cfg"; request.Nonce = "client-claims-admin"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request));
            request.Nonce = Guid.NewGuid().ToString("N"); request.Operation = "execute"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request));
        });
        Test("admin config reload requests reject paths edits and document responses while retaining pending listings", () =>
        {
            var request = new AdminConfigRequest { Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"), Operation = "reload" };
            Check(AdminConfigProtocol.ReadRequest(Json.Write(request)).Operation == "reload");
            request.Path = "Example.cfg"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request)); request.Path = "";
            request.Text = "do not apply"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request)); request.Text = "";
            request.ExpectedHash = new string('a', 64); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request)); request.ExpectedHash = "";
            request.Changes.Add(new AdminConfigChange { Id = "0", Value = "true" }); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request)); request.Changes.Clear();
            var response = new AdminConfigResponse { Authorized = true, RequestId = request.RequestId, Operation = "reload", Message = "Reloaded one running mod config.", Files = new List<AdminConfigFileInfo> { new AdminConfigFileInfo { Path = "Example.cfg", Kind = "cfg", Pending = true, Length = cfg.Length } } };
            var roundTrip = AdminConfigProtocol.ReadResponse(AdminConfigProtocol.WriteResponse(response));
            Check(roundTrip.Files.Single().Pending && roundTrip.Message == response.Message && roundTrip.Document == null);
            response.Message = new string('x', 2049); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response)); response.Message = "";
            response.Document = ServerConfigFiles.Read(Fixture(), "Example.cfg", true); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
        });
        Test("server config backend denies unauthorized list read and writes before touching a path", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, "Example.cfg", true); var request = SaveRequest(document, ("Enabled", "true"));
            Throws<UnauthorizedAccessException>(() => ServerConfigFiles.List(root, false));
            Throws<UnauthorizedAccessException>(() => ServerConfigFiles.Read(root, "../outside.cfg", false));
            Throws<UnauthorizedAccessException>(() => ServerConfigFiles.Save(root, request, false));
            Check(!Directory.Exists(SafePath.Under(root, "ModGate/server-config-edits")) && File.ReadAllText(SafePath.Under(root, "config/Example.cfg")) == cfg);
        });
        Test("admin server config listing includes ordinary mod cfg and exact owner policy only", () =>
        {
            var root = Fixture(); Put(root, "config/Nested/Extra.cfg", "Enabled = true"); Put(root, "config/BepInEx.cfg", "loader"); Put(root, "config/ModGate/owner-access-key.txt", "private-key"); Put(root, "config/private-token.cfg", "credential"); Put(root, "config/other.json", "{}");
            var files = ServerConfigFiles.List(root, true);
            Check(files.Count == 3 && files.Any(f => f.Path == "Nested/Extra.cfg") && files.Single(f => f.Kind == "json").Path == AdminConfigProtocol.ServerModsPath);
        });
        Test("admin cfg documents expose typed settings while excluding raw lines and secret values", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, "Example.cfg", true); var wire = Json.Write(document);
            Check(document.Kind == "cfg" && document.Text == "" && document.Entries.Single(e => e.Key == "Enabled").Kind == "Boolean" && document.Entries.Single(e => e.Key == "Range").Minimum == "1");
            Check(!document.Entries.Single(e => e.Key == "Complex").Editable && !wire.Contains("hidden-fixture-password") && !wire.Contains("hidden-fixture-token") && !wire.Contains("untouched trailing comment"));
            var response = new AdminConfigResponse { Authorized = true, RequestId = Guid.NewGuid().ToString("N"), Operation = "read", Document = document };
            Check(AdminConfigProtocol.ReadResponse(AdminConfigProtocol.WriteResponse(response)).Document!.Entries.Count == document.Entries.Count);
        });
        Test("admin cfg saves queue restart edits and reject stale administrator revisions", () =>
        {
            var root = Fixture(); var initial = ServerConfigFiles.Read(root, "Example.cfg", true); var first = ServerConfigFiles.Save(root, SaveRequest(initial, ("Enabled", "true")), true);
            Check(first.Pending && first.RestartRequired && first.Hash != initial.Hash && first.Entries.Single(e => e.Key == "Enabled").Value == "true");
            Check(File.ReadAllText(SafePath.Under(root, "config/Example.cfg")) == cfg);
            Throws<IOException>(() => ServerConfigFiles.Save(root, SaveRequest(initial, ("Range", "4")), true));
            var second = ServerConfigFiles.Save(root, SaveRequest(first, ("Range", "4")), true);
            Check(second.Entries.Single(e => e.Key == "Enabled").Value == "true" && second.Entries.Single(e => e.Key == "Range").Value == "4");
        });
        Test("queued server cfg edits merge unrelated shutdown writes and preserve comments secrets and encoding", () =>
        {
            var root = Fixture(); var path = SafePath.Under(root, "config/Example.cfg");
            File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(cfg)).ToArray());
            var document = ServerConfigFiles.Read(root, "Example.cfg", true); ServerConfigFiles.Save(root, SaveRequest(document, ("Enabled", "true")), true);
            var shutdown = cfg.Replace("Other = retained", "Other = changed-at-shutdown"); File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(shutdown)).ToArray());
            Check(ServerConfigFiles.ApplyPending(root, _ => { }) == 1);
            var expected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(shutdown.Replace("Enabled = false", "Enabled = true"))).ToArray();
            Check(File.ReadAllBytes(path).SequenceEqual(expected) && !ServerConfigFiles.Read(root, "Example.cfg", true).Pending);
            Check(Directory.GetFiles(SafePath.Under(root, "ModGate/config-backups"), "Example.cfg", SearchOption.AllDirectories).Length == 1);
        });
        Test("server cfg conflicts keep both the original file and queued edits for review", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, "Example.cfg", true); ServerConfigFiles.Save(root, SaveRequest(document, ("Range", "4")), true);
            Put(root, "config/Example.cfg", cfg.Replace("Range = 3", "Range = 5")); var logs = new List<string>();
            Check(ServerConfigFiles.ApplyPending(root, logs.Add) == 0 && File.ReadAllText(SafePath.Under(root, "config/Example.cfg")).Contains("Range = 5"));
            Check(ServerConfigFiles.List(root, true).Single(f => f.Path == "Example.cfg").Pending && logs.Any(m => m.Contains("changed externally")));
        });
        Test("admin cfg saves reject private nonexistent unsupported multiline and out-of-range settings", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, "Example.cfg", true);
            foreach (var request in new[] { SaveRequest(document, ("Complex", "4,5,6")), SaveRequest(document, ("Range", "11")), SaveRequest(document, ("Other", "one\ntwo")), SaveRequest(document, ("Enabled", "not-a-bool")) })
                Throws<InvalidDataException>(() => ServerConfigFiles.Save(root, request, true));
            var unknown = SaveRequest(document, ("Enabled", "true")); unknown.Changes[0].Id = "999999";
            Throws<InvalidDataException>(() => ServerConfigFiles.Save(root, unknown, true));
            Check(File.ReadAllText(SafePath.Under(root, "config/Example.cfg")) == cfg && !Directory.Exists(SafePath.Under(root, "ModGate/server-config-edits")));
        });
        Test("admin owner JSON saves preserve field markers links choices and unresolved mod mappings", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true);
            var unresolved = Mod("legacy.alias*"); unresolved.Name = "Custom* name"; unresolved.Version = "1.2.3*"; unresolved.Side = "clientOnly*";
            var linked = Mod("other", package: "Author-Other-1.0.0*"); linked.DownloadUrl = "https://thunderstore.io/package/download/Author/Other/1.0.0/*";
            var settings = new ServerManifest { ServerName = "Owner* server", AllowNewerClientVersions = true, AutoUpdateOnRestart = true, Mods = new List<ModEntry> { unresolved, linked } };
            var saved = ServerConfigFiles.Save(root, JsonRequest(document, Json.WriteReadable(settings)), true);
            Check(saved.Pending && saved.Text.Contains("legacy.alias*") && ServerConfigFiles.ApplyPending(root, _ => { }) == 1);
            var restored = Json.Read<ServerManifest>(File.ReadAllText(SafePath.Under(root, "config/ModGate/server-mods.json")));
            Check(restored.Mods.Count == 2 && restored.Mods[0].Guid == unresolved.Guid && restored.Mods[0].Side == unresolved.Side && restored.Mods[0].Name == unresolved.Name && restored.Mods[1].DownloadUrl == linked.DownloadUrl && restored.AllowNewerClientVersions && restored.AutoUpdateOnRestart);
        });
        Test("admin owner JSON validation rejects bad schema duplicates classifications and download routes", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true);
            var badSide = new ServerManifest { Mods = new List<ModEntry> { Mod("a", "unclassified") } };
            var badVersion = new ServerManifest { Mods = new List<ModEntry> { Mod("a", version: "not-a-version") } };
            var badLink = Mod("a"); badLink.DownloadUrl = "https://evil.test/download";
            foreach (var text in new[] { "null", "{\"schema\":1,\"privatePassword\":\"must-not-be-added\",\"mods\":[]}", Json.Write(new ServerManifest { Schema = 99 }), Json.Write(badSide), Json.Write(badVersion), Json.Write(new ServerManifest { Mods = new List<ModEntry> { Mod("same"), Mod("same") } }), Json.Write(new ServerManifest { Mods = new List<ModEntry> { badLink } }) })
                Throws<Exception>(() => ServerConfigFiles.Save(root, JsonRequest(document, text), true));
            Check(!ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true).Pending);
        });
        Test("server owner JSON conflicts never overwrite later owner edits", () =>
        {
            var root = Fixture(); var document = ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true);
            ServerConfigFiles.Save(root, JsonRequest(document, Json.Write(new ServerManifest { ServerName = "Queued", Mods = new List<ModEntry> { Mod("a") } })), true);
            Put(root, "config/ModGate/server-mods.json", Json.Write(new ServerManifest { ServerName = "Changed on host", Mods = new List<ModEntry> { Mod("a") } }));
            Check(ServerConfigFiles.ApplyPending(root, _ => { }) == 0 && File.ReadAllText(SafePath.Under(root, "config/ModGate/server-mods.json")).Contains("Changed on host"));
            Check(ServerConfigFiles.List(root, true).Single(f => f.Kind == "json").Pending);
        });
        Test("invalid current classifications remain readable so an administrator can correct them", () =>
        {
            var root = Fixture(); Put(root, "config/ModGate/server-mods.json", Json.Write(new ServerManifest { Mods = new List<ModEntry> { Mod("a", "unclassified") } }));
            var document = ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true); Check(document.Text.Contains("unclassified"));
            var settings = Json.Read<ServerManifest>(document.Text); settings.Mods[0].Side = "optional";
            Check(ServerConfigFiles.Save(root, JsonRequest(document, Json.Write(settings)), true).Pending);
        });
        Test("queued server cfg edits stay bound to the active slot and server identity while owner JSON remains global", () =>
        {
            var root = Fixture(); var cfgDocument = ServerConfigFiles.Read(root, "Example.cfg", true); var owner = ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true);
            ServerConfigFiles.Save(root, SaveRequest(cfgDocument, ("Enabled", "true")), true);
            ServerConfigFiles.Save(root, JsonRequest(owner, Json.Write(new ServerManifest { ServerName = "Global queued policy", Mods = new List<ModEntry> { Mod("a") } })), true);
            var catalog = new ProfileCatalog { Active = "server-1", Servers = new List<SavedProfile> { new SavedProfile { Slot = "server-1", Name = "Other profile", ServerKey = new string('a', 64) } } };
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog); var before = File.ReadAllText(SafePath.Under(root, "config/Example.cfg"));
            Throws<IOException>(() => ServerConfigFiles.Read(root, "Example.cfg", true));
            Throws<IOException>(() => ServerConfigFiles.Save(root, SaveRequest(cfgDocument, ("Range", "4")), true));
            Check(ServerConfigFiles.Read(root, AdminConfigProtocol.ServerModsPath, true).Pending && ServerConfigFiles.ApplyPending(root, _ => { }) == 1);
            Check(File.ReadAllText(SafePath.Under(root, "config/Example.cfg")) == before && ServerConfigFiles.List(root, true).Single(f => f.Path == "Example.cfg").Pending);
            Check(File.ReadAllText(SafePath.Under(root, "config/ModGate/server-mods.json")).Contains("Global queued policy"));
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), new ProfileCatalog()); Check(ServerConfigFiles.ApplyPending(root, _ => { }) == 1);
            Check(ServerConfigFiles.Read(root, "Example.cfg", true).Entries.Single(e => e.Key == "Enabled").Value == "true");
            Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            var serverDocument = ServerConfigFiles.Read(root, "Example.cfg", true); ServerConfigFiles.Save(root, SaveRequest(serverDocument, ("Range", "4")), true);
            catalog.Servers[0].ServerKey = new string('b', 64); Json.Save(SafePath.Under(root, "ModGate/profiles.json"), catalog);
            Throws<IOException>(() => ServerConfigFiles.Read(root, "Example.cfg", true)); Check(ServerConfigFiles.ApplyPending(root, _ => { }) == 0);
            Check(File.ReadAllText(SafePath.Under(root, "config/Example.cfg")).Contains("Range = 3"));
        });
        Test("admin config limits and unauthorized response validation prevent oversized or private frames", () =>
        {
            var root = Fixture(); Put(root, "config/Large.cfg", new string('x', AdminConfigProtocol.MaxDocumentBytes + 1));
            Throws<InvalidDataException>(() => ServerConfigFiles.Read(root, "Large.cfg", true));
            Throws<InvalidDataException>(() => AdminConfigProtocol.ReadRequest(new string('x', AdminConfigProtocol.MaxMessageBytes + 1)));
            var response = new AdminConfigResponse { RequestId = Guid.NewGuid().ToString("N"), Operation = "read", Authorized = false, Document = ServerConfigFiles.Read(root, "Example.cfg", true) };
            Throws<InvalidDataException>(() => AdminConfigProtocol.WriteResponse(response));
        });
    }
}
