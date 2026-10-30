using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace ModGate
{
    [DataContract] internal sealed class AdminConfigChange
    {
        [DataMember] public string Id = "", Value = "";
    }
    [DataContract] internal sealed class AdminConfigRequest
    {
        [DataMember] public int Version = 1;
        [DataMember(EmitDefaultValue = false)] public long Sequence = 0;
        [DataMember] public string Nonce = "", RequestId = "", Operation = "", Path = "", ExpectedHash = "", Proof = "", Text = "";
        [DataMember] public List<AdminConfigChange> Changes = new List<AdminConfigChange>();
    }
    [DataContract] internal sealed class AdminConfigFileInfo
    {
        [DataMember] public string Path = "", Kind = "";
        [DataMember] public long Length = 0;
        [DataMember] public bool Pending = false;
    }
    [DataContract] internal sealed class AdminConfigEntry
    {
        [DataMember] public string Id = "", Section = "", Key = "", Value = "", Description = "", TypeName = "", Kind = "", DefaultValue = "", Minimum = "", Maximum = "", Problem = "";
        [DataMember] public bool Editable = false, HasDefault = false;
        [DataMember] public List<string> Choices = new List<string>();
    }
    [DataContract] internal sealed class AdminConfigDocument
    {
        [DataMember] public string Path = "", Kind = "", Hash = "", Text = "", Problem = "";
        [DataMember] public bool Pending = false, RestartRequired = false;
        [DataMember] public List<AdminConfigEntry> Entries = new List<AdminConfigEntry>();
    }
    [DataContract] internal sealed class AdminConfigResponse
    {
        [DataMember] public int Version = 1;
        [DataMember] public string RequestId = "", Operation = "", Error = "", Message = "";
        [DataMember] public bool Authorized = false, RestartRequired = false;
        [DataMember] public List<AdminConfigFileInfo> Files = new List<AdminConfigFileInfo>();
        [DataMember] public AdminConfigDocument? Document = null;
        [DataMember(EmitDefaultValue = false)] public ServerHealthSnapshot? Health = null;
    }
    internal static class AdminConfigProtocol
    {
        public const int Version = 1, MaxMessageBytes = 1024 * 1024, MaxDocumentBytes = 256 * 1024, MaxFiles = 1024, MaxEntries = 1024, MaxChanges = 512, MaxValueChars = 16384;
        public const string ServerModsPath = "ModGate/server-mods.json";
        public static bool Secret(string text)
        {
            var name = Regex.Replace(text ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
            return new[] { "password", "passwd", "secret", "token", "apikey", "credential", "privatekey", "accesskey", "authkey" }.Any(name.Contains);
        }
        public static string NormalizePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || relative.IndexOfAny(new[] { '\\', ':', '%', '\0' }) >= 0 || relative.Any(char.IsControl) || System.IO.Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid server config path.");
            var parts = relative.Split('/');
            if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) ||
                p.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || Regex.IsMatch(p.Split('.')[0].TrimEnd(' ', '.'), @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]|CONIN\$|CONOUT\$)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw new InvalidDataException("Unsafe server config path.");
            if (relative.Equals(ServerModsPath, StringComparison.OrdinalIgnoreCase)) return ServerModsPath;
            if (!System.IO.Path.GetExtension(relative).Equals(".cfg", StringComparison.OrdinalIgnoreCase) || parts.Any(Secret) || parts.Any(p => p.Equals("ModGate", StringComparison.OrdinalIgnoreCase) || p.Equals("private-passwords", StringComparison.OrdinalIgnoreCase) || p.StartsWith(".", StringComparison.Ordinal)) ||
                parts.Last().Equals("BepInEx.cfg", StringComparison.OrdinalIgnoreCase) || parts.Last().Equals("studio.valheim.modgate.cfg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This server config is global, private, or unsupported.");
            return relative;
        }
        public static AdminConfigRequest ReadRequest(string json)
        {
            CheckSize(json, MaxMessageBytes); var request = Json.Read<AdminConfigRequest>(json, MaxMessageBytes, 50000);
            ValidateRequest(request); return request;
        }
        public static AdminConfigResponse ReadResponse(string json)
        {
            CheckSize(json, MaxMessageBytes); var response = Json.Read<AdminConfigResponse>(json, MaxMessageBytes, 100000);
            ValidateResponse(response); return response;
        }
        public static string WriteResponse(AdminConfigResponse response)
        {
            ValidateResponse(response); var json = Json.Write(response); CheckSize(json, MaxMessageBytes); return json;
        }
        public static void ValidateRequest(AdminConfigRequest request)
        {
            if (request == null || request.Version != Version || request.Sequence < 0 || !Guid.TryParseExact(request.Nonce, "N", out _) || !Guid.TryParseExact(request.RequestId, "N", out _) ||
                request.Proof == null || request.Proof.Length > 0 && !Regex.IsMatch(request.Proof, @"^[A-Fa-f0-9]{64}$") || request.Changes == null || request.Changes.Count > MaxChanges || request.Text == null || request.ExpectedHash == null)
                throw new InvalidDataException("Invalid server config request.");
            if (!new[] { "list", "read", "save", "reload", "health" }.Contains(request.Operation)) throw new InvalidDataException("Unknown server config operation.");
            if (request.Operation == "list" || request.Operation == "reload" || request.Operation == "health")
            {
                if (request.Path != "" || request.ExpectedHash.Length != 0 || request.Changes.Count != 0 || request.Text.Length != 0) throw new InvalidDataException("A config list or reload request must not contain a path or edit.");
                return;
            }
            if (request.Path != NormalizePath(request.Path)) throw new InvalidDataException("Server config path is not canonical.");
            if (request.Operation == "read")
            {
                if (request.ExpectedHash.Length != 0 || request.Changes.Count != 0 || request.Text.Length != 0) throw new InvalidDataException("A config read request must not contain an edit.");
                return;
            }
            if (!Regex.IsMatch(request.ExpectedHash, @"^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("A saved config needs its reviewed revision.");
            if (request.Path == ServerModsPath)
            {
                if (request.Changes.Count != 0 || request.Text.Length == 0) throw new InvalidDataException("Server policy edits must contain only JSON text.");
                CheckSize(request.Text, MaxDocumentBytes);
            }
            else
            {
                if (request.Text.Length != 0 || request.Changes.Count == 0 || request.Changes.Any(p => p == null || !Regex.IsMatch(p.Id ?? "", @"^[0-9]{1,8}$") || p.Value == null || p.Value.Length > MaxValueChars || p.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) || request.Changes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != request.Changes.Count)
                    throw new InvalidDataException("Invalid server config field patches.");
            }
        }
        public static void ValidateDocument(AdminConfigDocument document)
        {
            if (document == null || document.Path != NormalizePath(document.Path) || !Regex.IsMatch(document.Hash ?? "", @"^[A-Fa-f0-9]{64}$") || document.Entries == null || document.Entries.Count > MaxEntries || document.Text == null || document.Problem == null || document.Problem.Length > 2048 ||
                document.Kind != (document.Path == ServerModsPath ? "json" : "cfg")) throw new InvalidDataException("Invalid server config document.");
            CheckSize(document.Text, MaxDocumentBytes);
            if (document.Kind == "cfg" && document.Text.Length != 0 || document.Kind == "json" && document.Entries.Count != 0) throw new InvalidDataException("Server config document contains unsupported content.");
            foreach (var entry in document.Entries)
            {
                if (entry == null || !Regex.IsMatch(entry.Id ?? "", @"^[0-9]{1,8}$") || entry.Section == null || entry.Key == null || Secret(entry.Section) || Secret(entry.Key) || entry.Value == null || entry.Value.Length > MaxValueChars ||
                    entry.Description == null || entry.Description.Length > 4096 || entry.Choices == null || entry.Choices.Count > 256 || entry.Choices.Any(v => v == null || v.Length > MaxValueChars) || entry.DefaultValue == null || entry.DefaultValue.Length > MaxValueChars ||
                    entry.TypeName == null || entry.TypeName.Length > 256 || entry.Minimum == null || entry.Minimum.Length > 128 || entry.Maximum == null || entry.Maximum.Length > 128 || entry.Problem == null || entry.Problem.Length > 2048 || !new[] { "Text", "Boolean", "Number", "Choice", "Flags" }.Contains(entry.Kind))
                    throw new InvalidDataException("Invalid or private server config setting.");
            }
            if (document.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != document.Entries.Count) throw new InvalidDataException("Duplicate server config setting identifiers.");
        }
        public static void ValidateResponse(AdminConfigResponse response)
        {
            if (response == null || response.Version != Version || !Guid.TryParseExact(response.RequestId, "N", out _) || !new[] { "list", "read", "save", "reload", "health" }.Contains(response.Operation) || response.Error == null || response.Error.Length > 2048 || response.Message == null || response.Message.Length > 2048 || response.Files == null || response.Files.Count > MaxFiles ||
                !response.Authorized && (response.Document != null || response.Files.Count != 0 || response.Health != null)) throw new InvalidDataException("Invalid server config response.");
            if (response.Operation == "health")
            {
                if (response.Document != null || response.Files.Count != 0 || response.Error.Length > 0 && response.Health != null || response.Authorized && response.Error.Length == 0 && response.Health == null)
                    throw new InvalidDataException("A server health response must contain only its authorized snapshot.");
                if (response.Health != null) ServerHealth.Validate(response.Health);
            }
            else if (response.Health != null) throw new InvalidDataException("A config response must not contain server health data.");
            if (response.Operation == "reload" && response.Document != null) throw new InvalidDataException("A config reload response must contain only the refreshed file listing.");
            foreach (var file in response.Files)
                if (file == null || file.Path != NormalizePath(file.Path) || file.Length < 0 || file.Kind != (file.Path == ServerModsPath ? "json" : "cfg")) throw new InvalidDataException("Invalid server config listing.");
            if (response.Document != null) ValidateDocument(response.Document);
        }
        private static void CheckSize(string text, int limit)
        {
            if (text == null || text.Length > limit || Encoding.UTF8.GetByteCount(text) > limit) throw new InvalidDataException("Server config data exceeds the remote editor size limit.");
        }
    }
}
