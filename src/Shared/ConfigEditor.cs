using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ModGate
{
    internal enum ConfigEditorKind { Text, Boolean, Number, Choice, Flags }

    internal sealed class ConfigEditorEntry
    {
        public string Id = "", Section = "", Key = "", Value = "", OriginalValue = "";
        public string DefaultValue = "", Description = "", TypeName = "", Minimum = "", Maximum = "", Problem = "";
        public bool HasDefault, Editable = true;
        public ConfigEditorKind Kind;
        public List<string> Choices = new List<string>();
        internal int Line;
        internal string Prefix = "";
    }

    internal sealed class ConfigEditorDocument
    {
        public string Slot = "", RelativePath = "";
        public bool Active, Pending;
        public List<ConfigEditorEntry> Entries = new List<ConfigEditorEntry>();
        internal string Root = "", SourcePath = "", SourceHash = "", PendingHash = "", ProfileIdentity = "";
        internal List<ConfigEditorLine> Lines = new List<ConfigEditorLine>();
        internal Encoding Encoding = new UTF8Encoding(false, true);
        internal byte[] Preamble = Array.Empty<byte>();
    }

    internal sealed class ConfigEditorSaveResult
    {
        public bool Changed, RestartRequired, Deferred;
        public string BackupPath = "";
    }

    internal sealed class ConfigEditorLine { public string Text = "", Ending = ""; }

    [DataContract] internal sealed class ConfigEditorPatch
    {
        [DataMember] public string Section = "", Key = "", Original = "", Value = "";
    }
    [DataContract] internal sealed class ConfigEditorPending
    {
        [DataMember] public string Slot = "", RelativePath = "", ProfileIdentity = "";
        [DataMember] public List<ConfigEditorPatch> Changes = new List<ConfigEditorPatch>();
    }

    // Operates on profile-owned .cfg files only. Secret values are retained in file lines,
    // never exposed as editable entries, queued in an edit, or included in UI metadata.
    internal static class ConfigEditor
    {
        private const int MaxConfigBytes = 16 * 1024 * 1024;
        private const string PendingFolder = "ModGate/config-edits/pending";
        private static readonly HashSet<string> IntegerTypes = new HashSet<string>(new[] { "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NumberTypes = new HashSet<string>(new[] { "Single", "Double", "Decimal", "Float" }, StringComparer.OrdinalIgnoreCase);

        private static bool Secret(string text)
        {
            var name = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");
            return new[] { "password", "passwd", "secret", "token", "apikey", "credential", "privatekey", "accesskey", "authkey" }.Any(name.Contains);
        }
        private static bool AllowedFile(string relative)
        {
            if (!ProfileConfigs.Allowed(relative) || !Path.GetExtension(relative).Equals(".cfg", StringComparison.OrdinalIgnoreCase)) return false;
            var file = Path.GetFileNameWithoutExtension(relative);
            return !Secret(file) && file.IndexOf("modgate", StringComparison.OrdinalIgnoreCase) < 0;
        }
        private static string ConfigRoot(string root, string slot, ProfileCatalog catalog)
        {
            if (slot != "offline" && (!Regex.IsMatch(slot, "^server-[1-4]$") || catalog.Servers.All(p => p.Slot != slot))) throw new InvalidDataException("Unknown config profile.");
            return SafePath.Under(root, catalog.Active == slot ? "config" : "ModGate/profiles/" + slot + "/config");
        }
        private static string Identity(ProfileCatalog catalog, string slot) => slot == "offline" ? "offline" : catalog.Servers.Single(p => p.Slot == slot).ServerKey;
        public static List<string> Files(string root, string slot)
        {
            var catalog = Profiles.Load(root); var source = ConfigRoot(root, slot, catalog);
            var profile = catalog.Servers.FirstOrDefault(p => p.Slot == slot);
            var mods = slot == "offline" ? catalog.OfflineMods : profile!.Mods;
            var known = slot == "offline" ? catalog.OfflineBindings : profile!.ConfigBindings;
            var candidates = (known ?? new List<ConfigBinding>()).SelectMany(b => b.Files ?? new List<string>())
                .Concat((mods ?? new List<ModEntry>()).Where(m => m.Guid != Constants.Guid).SelectMany(m => (m.LocalConfigFiles ?? new List<string>()).Concat(new[] { m.Guid + ".cfg" }))).ToList();
            // Before the first inventory record, Offline still owns ordinary configs.
            if (slot == "offline" && (mods == null || mods.Count == 0) && Directory.Exists(source)) Collect(source, source, candidates);
            var result = new List<string>();
            foreach (var relative in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!AllowedFile(relative)) continue;
                try { if (File.Exists(SafePath.Under(source, relative))) result.Add(relative.Replace('\\', '/')); }
                catch (InvalidDataException) { }
            }
            return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static void Collect(string root, string directory, List<string> files)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = path.Substring(root.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("ModGate/", StringComparison.OrdinalIgnoreCase) || relative.Equals("ModGate", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    SafePath.Under(root, relative);
                    if (Directory.Exists(path)) Collect(root, path, files); else if (AllowedFile(relative)) files.Add(relative);
                }
                catch (InvalidDataException) { }
            }
        }
        private static string ResolveFile(string root, string slot, string relative, ProfileCatalog catalog)
        {
            if (!AllowedFile(relative) || !Files(root, slot).Contains(relative.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("This config is global, private, unsupported, or does not belong to the selected profile.");
            return SafePath.Under(ConfigRoot(root, slot, catalog), relative);
        }
        private static string PendingPath(string root, string slot, string relative)
        {
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(slot + "|" + relative.Replace('\\', '/').ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
            return SafePath.Under(root, PendingFolder + "/" + key + ".json");
        }
        private static ConfigEditorPending? ReadPending(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaxConfigBytes) throw new InvalidDataException("Queued config edits are too large.");
            var result = Json.ReadLocal<ConfigEditorPending>(path);
            if (result == null || result.Changes == null || result.Changes.Count > 50000) throw new InvalidDataException("Invalid queued config edits.");
            return result;
        }
        public static ConfigEditorDocument Open(string root, string slot, string relative)
        {
            var catalog = Profiles.Load(root); var path = ResolveFile(root, slot, relative, catalog);
            var document = Read(path); document.Root = Path.GetFullPath(root); document.Slot = slot; document.RelativePath = relative.Replace('\\', '/');
            document.Active = catalog.Active == slot; document.SourcePath = path; document.SourceHash = SafePath.Hash(path);
            document.ProfileIdentity = Identity(catalog, slot);
            var pendingPath = PendingPath(root, slot, relative); var pending = ReadPending(pendingPath);
            if (pending != null)
            {
                ValidatePending(pending, slot, document.RelativePath, document.ProfileIdentity);
                foreach (var patch in pending.Changes)
                {
                    var entry = Find(document, patch.Section, patch.Key);
                    var error = Validate(entry, patch.Value);
                    if (error.Length > 0) throw new InvalidDataException("Queued config edit is no longer valid for " + patch.Key + ": " + error);
                    entry.Value = patch.Value; entry.OriginalValue = patch.Value;
                }
                document.Pending = true; document.PendingHash = SafePath.Hash(pendingPath);
            }
            return document;
        }
        private static ConfigEditorDocument Read(string path)
        {
            if (new FileInfo(path).Length > MaxConfigBytes) throw new InvalidDataException("Config file exceeds the editor's 16 MiB limit. Open it externally.");
            var bytes = File.ReadAllBytes(path); Encoding encoding; var skip = 0;
            if (bytes.Length >= 4 && (bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0 || bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)) throw new InvalidDataException("UTF-32 configs must be edited externally.");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) { encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else encoding = new UTF8Encoding(false, true);
            var document = Parse(encoding.GetString(bytes, skip, bytes.Length - skip)); document.Encoding = encoding; document.Preamble = bytes.Take(skip).ToArray(); return document;
        }
        public static ConfigEditorDocument Parse(string text)
        {
            if (text.Length > MaxConfigBytes) throw new InvalidDataException("Config file is too large for the editor.");
            var document = new ConfigEditorDocument(); var offset = 0;
            while (offset < text.Length)
            {
                var start = offset; while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n') offset++;
                var line = new ConfigEditorLine { Text = text.Substring(start, offset - start) };
                if (offset < text.Length) { var end = offset++; if (text[end] == '\r' && offset < text.Length && text[offset] == '\n') offset++; line.Ending = text.Substring(end, offset - end); }
                document.Lines.Add(line);
            }
            var section = ""; var comments = new List<string>();
            for (var index = 0; index < document.Lines.Count; index++)
            {
                var raw = document.Lines[index].Text; var trim = raw.Trim();
                if (trim.StartsWith("#", StringComparison.Ordinal) || trim.StartsWith(";", StringComparison.Ordinal)) { comments.Add(trim); continue; }
                if (trim.StartsWith("[", StringComparison.Ordinal) && trim.EndsWith("]", StringComparison.Ordinal)) { section = trim.Substring(1, trim.Length - 2).Trim(); comments.Clear(); continue; }
                var equal = raw.IndexOf('=');
                if (equal < 1) { comments.Clear(); continue; }
                var key = raw.Substring(0, equal).Trim();
                if (key.Length == 0 || Secret(key) || Secret(section)) { comments.Clear(); continue; }
                var valueStart = equal + 1; while (valueStart < raw.Length && (raw[valueStart] == ' ' || raw[valueStart] == '\t')) valueStart++;
                var entry = new ConfigEditorEntry { Id = index.ToString(CultureInfo.InvariantCulture), Line = index, Prefix = raw.Substring(0, valueStart), Section = section, Key = key, Value = raw.Substring(valueStart), OriginalValue = raw.Substring(valueStart) };
                Metadata(entry, comments); comments.Clear(); document.Entries.Add(entry);
            }
            foreach (var duplicates in document.Entries.GroupBy(e => e.Section + "\0" + e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
                foreach (var entry in duplicates) { entry.Editable = false; entry.Problem = "Duplicate section/key. Open this file externally to resolve it."; }
            return document;
        }
        private static void Metadata(ConfigEditorEntry entry, List<string> comments)
        {
            var descriptions = new List<string>(); var flags = false;
            foreach (var raw in comments)
            {
                var text = raw.TrimStart('#', ';').TrimStart();
                if (text.StartsWith("Setting type:", StringComparison.OrdinalIgnoreCase)) entry.TypeName = text.Substring(13).Trim();
                else if (text.StartsWith("Default value:", StringComparison.OrdinalIgnoreCase)) { entry.HasDefault = true; entry.DefaultValue = text.Substring(14).TrimStart(); }
                else if (text.StartsWith("Acceptable values:", StringComparison.OrdinalIgnoreCase)) entry.Choices = text.Substring(18).Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                else if (text.StartsWith("Acceptable value range:", StringComparison.OrdinalIgnoreCase))
                {
                    var range = Regex.Match(text, @"^Acceptable value range:\s*From\s+(.+?)\s+to\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (range.Success) { entry.Minimum = range.Groups[1].Value; entry.Maximum = range.Groups[2].Value; }
                }
                else if (text.IndexOf("Multiple values", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("separat", StringComparison.OrdinalIgnoreCase) >= 0) flags = true;
                else if (!text.StartsWith("Settings file was created", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Plugin GUID:", StringComparison.OrdinalIgnoreCase)) descriptions.Add(text);
            }
            entry.Description = string.Join("\n", descriptions).Trim();
            var type = entry.TypeName.StartsWith("System.", StringComparison.Ordinal) ? entry.TypeName.Substring(7) : entry.TypeName;
            if (type.Equals("Boolean", StringComparison.OrdinalIgnoreCase) || type.Equals("Bool", StringComparison.OrdinalIgnoreCase)) entry.Kind = ConfigEditorKind.Boolean;
            else if (IntegerTypes.Contains(type) || NumberTypes.Contains(type)) entry.Kind = ConfigEditorKind.Number;
            else if (entry.Choices.Count > 0) entry.Kind = flags ? ConfigEditorKind.Flags : ConfigEditorKind.Choice;
            else if (type.Length == 0 || type.Equals("String", StringComparison.OrdinalIgnoreCase) || type.Equals("Char", StringComparison.OrdinalIgnoreCase)) entry.Kind = ConfigEditorKind.Text;
            else { entry.Kind = ConfigEditorKind.Text; entry.Editable = false; entry.Problem = "This setting type must be edited using the mod's own controls or an external editor."; }
        }
        public static string Validate(ConfigEditorEntry entry, string value)
        {
            if (!entry.Editable) return entry.Problem.Length > 0 ? entry.Problem : "This setting is read-only.";
            if (value == null || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return "A setting must fit on one line. Use literal escape sequences for text.";
            var type = entry.TypeName.StartsWith("System.", StringComparison.Ordinal) ? entry.TypeName.Substring(7) : entry.TypeName;
            if (entry.Kind == ConfigEditorKind.Boolean && !bool.TryParse(value, out _)) return "Choose true or false.";
            if (entry.Kind == ConfigEditorKind.Choice && !entry.Choices.Contains(value, StringComparer.Ordinal)) return "Choose one of the listed acceptable values.";
            if (entry.Kind == ConfigEditorKind.Flags && value.Split(',').Select(v => v.Trim()).Any(v => !entry.Choices.Contains(v, StringComparer.Ordinal))) return "Choose only the listed acceptable values, separated by commas.";
            if (type.Equals("Char", StringComparison.OrdinalIgnoreCase) && value.Length != 1) return "Enter exactly one character.";
            if (entry.Kind == ConfigEditorKind.Number)
            {
                if (NumberTypes.Contains(type) && !type.Equals("Decimal", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) || double.IsInfinity(floating) || double.IsNaN(floating)) return "Enter a finite number using a decimal point.";
                    if (type.Equals("Single", StringComparison.OrdinalIgnoreCase) || type.Equals("Float", StringComparison.OrdinalIgnoreCase))
                        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) || float.IsInfinity(single) || float.IsNaN(single)) return "Enter a finite single-precision number.";
                    if (entry.Minimum.Length > 0 && (!double.TryParse(entry.Minimum, NumberStyles.Float, CultureInfo.InvariantCulture, out var lower) || double.IsNaN(lower) || floating < lower)) return "Enter a value at least " + entry.Minimum + ".";
                    if (entry.Maximum.Length > 0 && (!double.TryParse(entry.Maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out var upper) || double.IsNaN(upper) || floating > upper)) return "Enter a value no greater than " + entry.Maximum + ".";
                    return "";
                }
                decimal number;
                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return "Enter a finite number using a decimal point.";
                if (IntegerTypes.Contains(type))
                {
                    if (decimal.Truncate(number) != number || !Regex.IsMatch(value.Trim(), @"^[+-]?[0-9]+$")) return "Enter a whole number.";
                    var bounds = IntegerBounds(type);
                    if (number < bounds.Item1 || number > bounds.Item2) return "The number is outside this setting type's range.";
                }
                if (entry.Minimum.Length > 0 && (!decimal.TryParse(entry.Minimum, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum) || number < minimum)) return "Enter a value at least " + entry.Minimum + ".";
                if (entry.Maximum.Length > 0 && (!decimal.TryParse(entry.Maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) || number > maximum)) return "Enter a value no greater than " + entry.Maximum + ".";
            }
            return "";
        }
        private static Tuple<decimal, decimal> IntegerBounds(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "byte": return Tuple.Create((decimal)byte.MinValue, (decimal)byte.MaxValue);
                case "sbyte": return Tuple.Create((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue);
                case "int16": return Tuple.Create((decimal)short.MinValue, (decimal)short.MaxValue);
                case "uint16": return Tuple.Create((decimal)ushort.MinValue, (decimal)ushort.MaxValue);
                case "uint32": return Tuple.Create((decimal)uint.MinValue, (decimal)uint.MaxValue);
                case "int64": return Tuple.Create((decimal)long.MinValue, (decimal)long.MaxValue);
                case "uint64": return Tuple.Create((decimal)ulong.MinValue, (decimal)ulong.MaxValue);
                default: return Tuple.Create((decimal)int.MinValue, (decimal)int.MaxValue);
            }
        }
        private static ConfigEditorEntry Find(ConfigEditorDocument document, string section, string key)
        {
            var matches = document.Entries.Where(e => e.Section == section && e.Key == key).ToArray();
            if (matches.Length != 1 || !matches[0].Editable) throw new InvalidDataException("Config setting is missing, private, or ambiguous: " + key);
            return matches[0];
        }
        private static void ValidatePending(ConfigEditorPending pending, string slot, string relative, string identity)
        {
            if (pending.Slot != slot || pending.RelativePath != relative || pending.ProfileIdentity != identity || pending.Changes.Any(p => p == null || Secret(p.Key) || Secret(p.Section)) || pending.Changes.GroupBy(p => p.Section + "\0" + p.Key).Any(g => g.Count() > 1)) throw new InvalidDataException("Queued config identity changed. Edits were preserved for review.");
        }
        public static ConfigEditorSaveResult Save(string root, ConfigEditorDocument document, IDictionary<string, string> values)
        {
            if (!Path.GetFullPath(root).Equals(document.Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Config document belongs to another installation.");
            if (Profiles.Pending(root) || File.Exists(SafePath.Under(root, "ModGate/profile-journal.json"))) throw new InvalidOperationException("Apply the pending profile change before editing configs.");
            var catalog = Profiles.Load(root); var path = ResolveFile(root, document.Slot, document.RelativePath, catalog);
            if (Identity(catalog, document.Slot) != document.ProfileIdentity || !path.Equals(document.SourcePath, StringComparison.OrdinalIgnoreCase) || SafePath.Hash(path) != document.SourceHash) throw new IOException("The profile or config changed after opening it. Reload the file before saving.");
            if (values.Keys.Any(id => document.Entries.All(e => e.Id != id))) throw new InvalidDataException("Unknown config setting.");
            var pendingPath = PendingPath(root, document.Slot, document.RelativePath);
            if ((File.Exists(pendingPath) ? SafePath.Hash(pendingPath) : "") != document.PendingHash) throw new IOException("Queued config edits changed. Reload before saving.");
            var source = Read(path); var patches = new List<ConfigEditorPatch>();
            foreach (var entry in document.Entries)
            {
                var desired = values.TryGetValue(entry.Id, out var supplied) ? supplied : entry.OriginalValue;
                if (desired == entry.OriginalValue && !entry.Editable) continue;
                var current = Find(source, entry.Section, entry.Key);
                if (desired == current.OriginalValue) continue;
                var error = Validate(entry, desired); if (error.Length > 0) throw new InvalidDataException(entry.Section + " / " + entry.Key + ": " + error);
                patches.Add(new ConfigEditorPatch { Section = entry.Section, Key = entry.Key, Original = current.OriginalValue, Value = desired });
            }
            var result = new ConfigEditorSaveResult { RestartRequired = catalog.Active == document.Slot || document.Pending, Deferred = catalog.Active == document.Slot || document.Pending };
            if (result.Deferred)
            {
                var pending = new ConfigEditorPending { Slot = document.Slot, RelativePath = document.RelativePath, ProfileIdentity = document.ProfileIdentity, Changes = patches };
                var prior = ReadPending(pendingPath);
                result.Changed = (prior == null ? "" : Json.Write(prior)) != (patches.Count == 0 ? "" : Json.Write(pending));
                if (patches.Count == 0) { if (File.Exists(pendingPath)) File.Delete(pendingPath); }
                else Json.Save(pendingPath, pending);
                document.Pending = patches.Count > 0; document.PendingHash = File.Exists(pendingPath) ? SafePath.Hash(pendingPath) : "";
            }
            else if (patches.Count > 0)
            {
                foreach (var patch in patches) Replace(source, Find(source, patch.Section, patch.Key), patch.Value);
                result.BackupPath = Write(root, document.Slot, document.RelativePath, path, source, document.SourceHash); result.Changed = true;
                document.SourceHash = SafePath.Hash(path);
            }
            foreach (var entry in document.Entries) if (values.TryGetValue(entry.Id, out var value)) { entry.Value = value; entry.OriginalValue = value; }
            return result;
        }
        private static void Replace(ConfigEditorDocument document, ConfigEditorEntry entry, string value)
        {
            document.Lines[entry.Line].Text = entry.Prefix + value; entry.Value = value;
        }
        private static string Write(string root, string slot, string relative, string path, ConfigEditorDocument document, string expectedHash)
        {
            var backupRelative = "ModGate/config-backups/editor-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + "/" + slot + "/" + relative;
            var backup = SafePath.Under(root, backupRelative); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            var text = string.Concat(document.Lines.Select(l => l.Text + l.Ending)); var bytes = document.Preamble.Concat(document.Encoding.GetBytes(text)).ToArray();
            var temp = SafePath.Under(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (SafePath.Hash(path) != expectedHash) throw new IOException("Config changed while saving. Reload before retrying.");
                File.Copy(path, backup, false); File.Replace(temp, path, null); return backupRelative;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        // Called after Profiles.Apply, after the old game's plugins have exited.
        // Merge only edited fields so unrelated settings saved at shutdown survive.
        public static int ApplyPending(string root, Action<string> log)
        {
            var folder = SafePath.Under(root, PendingFolder); if (!Directory.Exists(folder)) return 0; var count = 0;
            foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var safeFile = SafePath.Under(folder, Path.GetFileName(file)); var pending = ReadPending(safeFile)!;
                    var catalog = Profiles.Load(root); var path = ResolveFile(root, pending.Slot, pending.RelativePath, catalog);
                    if (PendingPath(root, pending.Slot, pending.RelativePath) != safeFile) throw new InvalidDataException("Queued config filename does not match its identity.");
                    ValidatePending(pending, pending.Slot, pending.RelativePath, Identity(catalog, pending.Slot));
                    var hash = SafePath.Hash(path); var document = Read(path); var changed = false;
                    foreach (var patch in pending.Changes)
                    {
                        var entry = Find(document, patch.Section, patch.Key); var error = Validate(entry, patch.Value);
                        if (error.Length > 0) throw new InvalidDataException(patch.Key + ": " + error);
                        if (entry.OriginalValue != patch.Original && entry.OriginalValue != patch.Value) throw new IOException("A queued setting changed externally: " + patch.Key + ". Pending edits were kept for review.");
                        if (entry.OriginalValue != patch.Value) { Replace(document, entry, patch.Value); changed = true; }
                    }
                    if (changed) Write(root, pending.Slot, pending.RelativePath, path, document, hash);
                    File.Delete(safeFile); count++; log("Applied queued config edits for " + pending.Slot + ": " + pending.RelativePath);
                }
                catch (Exception error) { log("Config edits were not applied; files and pending edits were preserved. " + error.Message); }
            }
            return count;
        }
        // Explicit restore replaces queued edits for this slot. Keep complete receipts
        // in local backups first, so a failed restore never silently loses their values.
        public static int DiscardPending(string root, string slot)
        {
            var catalog = Profiles.Load(root); ConfigRoot(root, slot, catalog);
            var folder = SafePath.Under(root, PendingFolder); if (!Directory.Exists(folder)) return 0;
            var batch = "ModGate/config-backups/discarded-editor-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var receipts = new List<Tuple<string, string>>();
            foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var source = SafePath.Under(folder, Path.GetFileName(file)); var pending = ReadPending(source)!;
                if (pending.Slot != slot) continue;
                if (!AllowedFile(pending.RelativePath) || source != PendingPath(root, pending.Slot, pending.RelativePath)) throw new InvalidDataException("Queued config identity is invalid. No edits were discarded.");
                var backup = SafePath.Under(root, batch + "/" + Path.GetFileName(file)); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(source, backup, false);
                receipts.Add(Tuple.Create(source, backup));
            }
            try { foreach (var receipt in receipts) File.Delete(receipt.Item1); }
            catch
            {
                foreach (var receipt in receipts) if (!File.Exists(receipt.Item1)) File.Copy(receipt.Item2, receipt.Item1, false);
                throw;
            }
            return receipts.Count;
        }
        // Snapshot restoration is a profile transaction: permanent copies retain edits,
        // while the journal owns their removal so rollback restores the original queue.
        public static int StageDiscardPending(string root, string slot, Action<string, string> move, string trashBatch)
        {
            if (move == null) throw new ArgumentNullException(nameof(move));
            if (trashBatch == null || !Regex.IsMatch(trashBatch, @"^ModGate/trash/[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid profile restore trash batch.");
            SafePath.Under(root, trashBatch);
            var catalog = Profiles.Load(root); ConfigRoot(root, slot, catalog);
            var identity = Identity(catalog, slot); var folder = SafePath.Under(root, PendingFolder); if (!Directory.Exists(folder)) return 0;
            var backupBatch = "ModGate/config-backups/restore-edits-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var receipts = new List<Tuple<string, string, string>>();
            foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var source = SafePath.Under(folder, Path.GetFileName(file)); var pending = ReadPending(source)!;
                if (pending.Slot != slot) continue;
                ValidatePending(pending, slot, pending.RelativePath, identity);
                if (!AllowedFile(pending.RelativePath) || source != PendingPath(root, slot, pending.RelativePath)) throw new InvalidDataException("Queued config identity is invalid. No edits were discarded.");
                SafePath.Under(ConfigRoot(root, slot, catalog), pending.RelativePath);
                var backup = SafePath.Under(root, backupBatch + "/" + Path.GetFileName(file)); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                var hash = SafePath.Hash(source); File.Copy(source, backup, false);
                if (SafePath.Hash(backup) != hash) throw new IOException("Queued config edits changed while they were being backed up.");
                receipts.Add(Tuple.Create(PendingFolder + "/" + Path.GetFileName(file), trashBatch + "/pending-config-edits/" + Path.GetFileName(file), hash));
            }
            foreach (var receipt in receipts)
            {
                if (SafePath.Hash(SafePath.Under(root, receipt.Item1)) != receipt.Item3) throw new IOException("Queued config edits changed during profile restoration.");
                move(receipt.Item1, receipt.Item2);
            }
            return receipts.Count;
        }
    }
}
