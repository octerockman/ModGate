using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ModGate
{
    [DataContract] internal sealed class ServerConfigFieldPatch
    {
        [DataMember] public string Section = "", Key = "", Original = "", Value = "";
    }
    [DataContract] internal sealed class ServerConfigPending
    {
        [DataMember] public string Path = "", SourceHash = "", Text = "", ProfileIdentity = "";
        [DataMember] public List<ServerConfigFieldPatch> Patches = new List<ServerConfigFieldPatch>();
    }
    // The caller derives authorization from the live server peer AND the owner-key proof.
    // There is deliberately no client-supplied administrator flag in the wire request.
    internal static class ServerConfigFiles
    {
        private const string PendingFolder = "ModGate/server-config-edits/pending";
        private static readonly object Gate = new object();
        public static List<AdminConfigFileInfo> List(string root, bool authorized)
        {
            Require(authorized);
            lock (Gate)
            {
                var config = SafePath.Under(root, "config"); var result = new List<AdminConfigFileInfo>();
                if (!Directory.Exists(config)) return result;
                void Add(string relative)
                {
                    try
                    {
                        relative = AdminConfigProtocol.NormalizePath(relative); var path = SafePath.Under(config, relative);
                        if (!File.Exists(path)) return;
                        result.Add(new AdminConfigFileInfo { Path = relative, Kind = Kind(relative), Length = new FileInfo(path).Length, Pending = File.Exists(PendingPath(root, relative)) });
                    }
                    catch (Exception error) when (error is InvalidDataException || error is UnauthorizedAccessException || error is IOException) { }
                }
                void Visit(string directory)
                {
                    foreach (var entry in Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        var relative = entry.Substring(config.Length + 1).Replace('\\', '/');
                        if (relative.Length > 240) continue;
                        try
                        {
                            SafePath.Under(config, relative);
                            if (Directory.Exists(entry))
                            {
                                if (!relative.Split('/').Any(p => AdminConfigProtocol.Secret(p) || p.Equals("ModGate", StringComparison.OrdinalIgnoreCase) || p.StartsWith(".", StringComparison.Ordinal))) Visit(entry);
                            }
                            else Add(relative);
                        }
                        catch (Exception error) when (error is InvalidDataException || error is UnauthorizedAccessException || error is IOException) { }
                    }
                }
                Visit(config); Add(AdminConfigProtocol.ServerModsPath);
                if (result.Count > AdminConfigProtocol.MaxFiles) throw new InvalidDataException("Too many server config files for one remote listing.");
                return result.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        public static AdminConfigDocument Read(string root, string relative, bool authorized)
        {
            Require(authorized); lock (Gate) return Document(Load(root, relative));
        }
        public static AdminConfigDocument Save(string root, AdminConfigRequest request, bool authorized)
        {
            Require(authorized); AdminConfigProtocol.ValidateRequest(request);
            if (request.Operation != "save") throw new InvalidDataException("Expected a server config save request.");
            lock (Gate)
            {
                var state = Load(root, request.Path);
                if (!state.Revision.Equals(request.ExpectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Server config changed after it was opened. Reload before saving.");
                var pending = new ServerConfigPending { Path = state.Relative, SourceHash = state.SourceHash, ProfileIdentity = state.ProfileIdentity };
                if (state.Relative == AdminConfigProtocol.ServerModsPath) pending.Text = ValidateServerJson(request.Text);
                else
                {
                    var document = state.Config!;
                    foreach (var patch in request.Changes)
                    {
                        var entry = document.Entries.SingleOrDefault(e => e.Id == patch.Id) ?? throw new InvalidDataException("Server config setting no longer exists.");
                        var problem = ConfigEditor.Validate(entry, patch.Value); if (problem.Length > 0) throw new InvalidDataException(entry.Key + ": " + problem);
                        Replace(document, entry, patch.Value);
                    }
                    if (Encode(document).Length > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Edited config exceeds the remote editor size limit.");
                    pending.Patches = document.Entries.Where(e => e.Editable && e.Value != e.OriginalValue).Select(e => new ServerConfigFieldPatch { Section = e.Section, Key = e.Key, Original = e.OriginalValue, Value = e.Value }).ToList();
                }
                var preview = Document(state); preview.Text = pending.Text;
                AdminConfigProtocol.WriteResponse(new AdminConfigResponse { RequestId = request.RequestId, Operation = "save", Authorized = true, Document = preview });
                if (state.Relative != AdminConfigProtocol.ServerModsPath && Identity(root) != state.ProfileIdentity) throw new IOException("The active server profile changed while saving. Reload its configs before retrying.");
                if (SafePath.Hash(state.FullPath) != state.SourceHash) throw new IOException("Server config changed while saving. Reload before retrying.");
                var queue = PendingPath(root, state.Relative);
                if (state.Relative != AdminConfigProtocol.ServerModsPath && pending.Patches.Count == 0) { if (File.Exists(queue)) File.Delete(queue); }
                else Json.Save(queue, pending);
                return Document(Load(root, state.Relative));
            }
        }
        // Runs in the preloader while plugins are stopped. Never reload arbitrary mod code.
        public static int ApplyPending(string root, Action<string> log)
        {
            lock (Gate)
            {
                var folder = SafePath.Under(root, PendingFolder); if (!Directory.Exists(folder)) return 0; var applied = 0;
                foreach (var candidate in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                {
                    try
                    {
                        var receipt = SafePath.Under(folder, System.IO.Path.GetFileName(candidate)); var pending = Pending(receipt);
                        if (PendingPath(root, pending.Path) != receipt) throw new InvalidDataException("Server config edit identity does not match its receipt.");
                        RequireIdentity(root, pending);
                        var full = Resolve(root, pending.Path); var bytes = ReadBytes(full); var hash = Hash(bytes); byte[] updated;
                        if (pending.Path == AdminConfigProtocol.ServerModsPath)
                        {
                            if (hash != pending.SourceHash) throw new IOException("Server policy changed externally. The queued edit was kept for review.");
                            updated = new UTF8Encoding(false).GetBytes(ValidateServerJson(pending.Text));
                        }
                        else
                        {
                            var document = Decode(bytes); ApplyPatches(document, pending.Patches); updated = Encode(document);
                        }
                        if (updated.Length > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Edited server config exceeds its size limit.");
                        if (!bytes.SequenceEqual(updated)) Write(root, pending.Path, full, updated, hash);
                        File.Delete(receipt); applied++; log("Applied server config changes: " + pending.Path + ".");
                    }
                    catch (Exception error) { log("Server config edits were not applied; originals and queued edits were kept. " + error.Message); }
                }
                return applied;
            }
        }
        private sealed class State
        {
            public string Relative = "", FullPath = "", SourceHash = "", Revision = "", Text = "", ProfileIdentity = "";
            public bool Pending;
            public ConfigEditorDocument? Config;
        }
        private static State Load(string root, string relative)
        {
            relative = AdminConfigProtocol.NormalizePath(relative); var full = Resolve(root, relative); var bytes = ReadBytes(full);
            var state = new State { Relative = relative, FullPath = full, SourceHash = Hash(bytes), ProfileIdentity = relative == AdminConfigProtocol.ServerModsPath ? "" : Identity(root) };
            var queue = PendingPath(root, relative); var pending = File.Exists(queue) ? Pending(queue) : null;
            if (pending != null && pending.Path != relative) throw new InvalidDataException("Server config edit identity changed.");
            if (pending != null) RequireIdentity(root, pending);
            state.Pending = pending != null;
            if (relative == AdminConfigProtocol.ServerModsPath)
            {
                if (pending != null && pending.SourceHash != state.SourceHash) throw new IOException("Server policy changed externally. Queued edits need review on the server.");
                state.Text = ValidateServerJson(pending?.Text ?? Decode(bytes).Lines.Aggregate(new StringBuilder(), (text, line) => text.Append(line.Text).Append(line.Ending)).ToString(), false);
            }
            else { state.Config = Decode(bytes); if (pending != null) ApplyPatches(state.Config, pending.Patches); }
            state.Revision = Hash(Encoding.UTF8.GetBytes(state.SourceHash + "|" + (state.Pending ? SafePath.Hash(queue) : ""))); return state;
        }
        private static AdminConfigDocument Document(State state)
        {
            var document = new AdminConfigDocument { Path = state.Relative, Kind = Kind(state.Relative), Hash = state.Revision, Pending = state.Pending, RestartRequired = state.Pending, Text = state.Text };
            if (state.Config != null)
                document.Entries = state.Config.Entries.Select(e => new AdminConfigEntry { Id = e.Id, Section = e.Section, Key = e.Key, Value = e.Value, Description = e.Description.Length > 4096 ? e.Description.Substring(0, 4096) : e.Description, TypeName = e.TypeName, Kind = e.Kind.ToString(), DefaultValue = e.DefaultValue, Minimum = e.Minimum, Maximum = e.Maximum, Problem = e.Problem, Editable = e.Editable, HasDefault = e.HasDefault, Choices = e.Choices.ToList() }).ToList();
            AdminConfigProtocol.ValidateDocument(document); return document;
        }
        private static string Kind(string relative) => relative == AdminConfigProtocol.ServerModsPath ? "json" : "cfg";
        private static string Identity(string root)
        {
            var catalog = Profiles.Load(root);
            return catalog.Active == "offline" ? "offline" : catalog.Active + "|" + catalog.Servers.Single(p => p.Slot == catalog.Active).ServerKey;
        }
        private static void RequireIdentity(string root, ServerConfigPending pending)
        {
            if (pending.Path != AdminConfigProtocol.ServerModsPath && pending.ProfileIdentity != Identity(root))
                throw new IOException("Queued server config edits belong to another profile. Return to that profile or review the pending edit on the host; no file was overwritten.");
        }
        private static void Require(bool authorized) { if (!authorized) throw new UnauthorizedAccessException("Server config access requires the authenticated server administrator and owner-key proof."); }
        private static string Resolve(string root, string relative)
        {
            relative = AdminConfigProtocol.NormalizePath(relative); var config = SafePath.Under(root, "config"); var full = SafePath.Under(config, relative);
            if (!File.Exists(full)) throw new FileNotFoundException("This server config does not exist."); return full;
        }
        private static string PendingPath(string root, string relative)
        {
            relative = AdminConfigProtocol.NormalizePath(relative);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) relative = relative.ToLowerInvariant();
            return SafePath.Under(root, PendingFolder + "/" + Hash(Encoding.UTF8.GetBytes(relative)) + ".json");
        }
        private static ServerConfigPending Pending(string path)
        {
            if (new FileInfo(path).Length > 4L * AdminConfigProtocol.MaxMessageBytes) throw new InvalidDataException("Queued server config is too large.");
            var pending = Json.ReadLocal<ServerConfigPending>(path);
            if (pending == null || pending.Path != AdminConfigProtocol.NormalizePath(pending.Path) || !Regex.IsMatch(pending.SourceHash ?? "", @"^[a-fA-F0-9]{64}$") || pending.Text == null || pending.Patches == null || pending.Patches.Count > AdminConfigProtocol.MaxEntries ||
                pending.Patches.Any(p => p == null || p.Section == null || p.Key == null || AdminConfigProtocol.Secret(p.Section) || AdminConfigProtocol.Secret(p.Key) || p.Original == null || p.Value == null || p.Value.Length > AdminConfigProtocol.MaxValueChars || p.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) ||
                pending.Patches.GroupBy(p => p.Section + "\0" + p.Key).Any(g => g.Count() > 1) || pending.Path == AdminConfigProtocol.ServerModsPath && pending.Patches.Count != 0 || pending.Path != AdminConfigProtocol.ServerModsPath && pending.Text.Length != 0)
                throw new InvalidDataException("Invalid queued server config changes.");
            return pending;
        }
        private static void ApplyPatches(ConfigEditorDocument document, List<ServerConfigFieldPatch> patches)
        {
            foreach (var patch in patches)
            {
                var matches = document.Entries.Where(e => e.Section == patch.Section && e.Key == patch.Key).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("A queued server setting is missing or ambiguous.");
                var entry = matches[0]; var problem = ConfigEditor.Validate(entry, patch.Value);
                if (problem.Length > 0) throw new InvalidDataException(entry.Key + ": " + problem);
                if (entry.OriginalValue != patch.Original && entry.OriginalValue != patch.Value) throw new IOException("A queued server setting changed externally: " + patch.Key + ".");
                Replace(document, entry, patch.Value);
            }
        }
        private static void Replace(ConfigEditorDocument document, ConfigEditorEntry entry, string value) { document.Lines[entry.Line].Text = entry.Prefix + value; entry.Value = value; }
        private static byte[] ReadBytes(string path)
        {
            if (new FileInfo(path).Length > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Server config exceeds the remote editor file-size limit.");
            using (var input = File.OpenRead(path)) using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { if (output.Length + count > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Server config grew beyond its size limit."); output.Write(buffer, 0, count); }
                return output.ToArray();
            }
        }
        private static ConfigEditorDocument Decode(byte[] bytes)
        {
            Encoding encoding; var skip = 0;
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else encoding = new UTF8Encoding(false, true);
            var document = ConfigEditor.Parse(encoding.GetString(bytes, skip, bytes.Length - skip)); document.Encoding = encoding; document.Preamble = bytes.Take(skip).ToArray(); return document;
        }
        private static byte[] Encode(ConfigEditorDocument document) => document.Preamble.Concat(document.Encoding.GetBytes(string.Concat(document.Lines.Select(l => l.Text + l.Ending)))).ToArray();
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void Write(string root, string relative, string path, byte[] bytes, string expected)
        {
            var backup = SafePath.Under(root, "ModGate/config-backups/server-editor-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + "/" + relative);
            var temporary = SafePath.Under(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (SafePath.Hash(path) != expected) throw new IOException("Server config changed while applying its edit.");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!); File.Copy(path, backup, false); File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string ValidateServerJson(string text, bool validateValues = true)
        {
            if (text == null || text.Length > AdminConfigProtocol.MaxDocumentBytes || Encoding.UTF8.GetByteCount(text) > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Server policy exceeds its size limit.");
            CheckSchemaFields(text);
            var settings = Json.Read<ServerManifest>(text, AdminConfigProtocol.MaxDocumentBytes);
            if (settings == null) throw new InvalidDataException("Missing server policy.");
            if (validateValues)
            {
                if (settings.Schema != Constants.ManifestSchema || settings.ServerName == null || settings.ServerName.Length > 200) throw new InvalidDataException("Invalid server policy schema or name.");
                var runtime = ConfigFields.Runtime(settings); PolicyEngine.ValidateInventory(runtime.Mods);
                foreach (var mod in runtime.Mods)
                {
                    if (!new[] { "required", "optional", "serverOnly", "clientOnly" }.Contains(mod.Side)) throw new InvalidDataException("Choose a valid side for " + mod.Name + ".");
                    if (!string.IsNullOrEmpty(mod.Package)) PackageId.Parse(mod.Package);
                    if (!string.IsNullOrEmpty(mod.DownloadUrl)) PackageId.FromDownloadUrl(mod.DownloadUrl);
                    if (!string.IsNullOrEmpty(mod.NexusUrl)) { NexusLinks.Validate(mod.NexusUrl); if (!string.IsNullOrEmpty(mod.DownloadUrl)) throw new InvalidDataException("Choose a Nexus page or an automatic download link, not both."); }
                    var dependencies = DependencyRules.Of(mod).ToArray();
                    if (dependencies.Length > 128 || dependencies.Any(d => d == null || string.IsNullOrWhiteSpace(d.Guid) || d.Guid.Length > 200 || (d.MinimumVersion?.Length ?? 0) > 64 || !string.IsNullOrEmpty(d.MinimumVersion) && !System.Version.TryParse(d.MinimumVersion, out _) || !string.IsNullOrEmpty(d.Package) && !PackageId.Allowed(d.Package))) throw new InvalidDataException("Invalid dependency metadata.");
                }
            }
            // Serialize the owner values, not the marker-stripped validation copy. No alias merge runs.
            var canonical = Json.WriteReadable(settings);
            if (Encoding.UTF8.GetByteCount(canonical) > AdminConfigProtocol.MaxDocumentBytes) throw new InvalidDataException("Formatted server policy exceeds its size limit.");
            return canonical;
        }
        private static void CheckSchemaFields(string text)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text), new XmlDictionaryReaderQuotas { MaxDepth = 32, MaxStringContentLength = AdminConfigProtocol.MaxDocumentBytes, MaxArrayLength = 4096 }))
            {
                var document = XDocument.Load(reader); var root = document.Root ?? throw new InvalidDataException("Missing server policy.");
                void Fields(XElement element, string[] allowed)
                {
                    if (element.Attribute("type")?.Value != "object" || element.Elements().Any(e => !allowed.Contains(e.Name.LocalName)) || element.Elements().GroupBy(e => e.Name.LocalName).Any(g => g.Count() > 1)) throw new InvalidDataException("Server policy contains an unknown or duplicate field.");
                }
                Fields(root, new[] { "schema", "serverName", "allowExtraClientMods", "allowNewerClientVersions", "autoUpdateOnRestart", "autoUpdateAllMods", "autoUpdateModGate", "modGateIncludePrereleases", "mods" });
                foreach (var mod in root.Element("mods")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    Fields(mod, new[] { "guid", "name", "version", "side", "package", "downloadUrl", "nexusUrl", "automatic", "autoUpdate", "allowNewerVersion", "manifestSource", "dependencies" });
                    foreach (var dependency in mod.Element("dependencies")?.Elements() ?? Enumerable.Empty<XElement>()) Fields(dependency, new[] { "Guid", "MinimumVersion", "Optional", "Package" });
                    var origin = mod.Element("manifestSource");
                    if (origin != null && origin.Attribute("type")?.Value != "null") Fields(origin, new[] { "Name", "Version", "Website", "Digest", "AssetId", "AssetSha256", "Dependencies" });
                }
            }
        }
    }
}
