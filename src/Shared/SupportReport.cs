using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ModGate
{
    internal sealed class SupportReportOptions
    {
        public bool IncludeLogs = true, IncludeMods = true, IncludeHealth = true;
        public string Stage = "", LastError = "";
        public List<string> SelectedLogs = new List<string>();
    }
    internal static class SupportReport
    {
        private static readonly string[] KnownLogs = { "LogOutput.log", "ModGate/logs/profile-setup.txt", "ModGate/install-error.txt", "ModGate/server-updates/report.json" };
        public static List<string> AvailableLogs(string root)
        {
            var result = KnownLogs.Where(name => File.Exists(SafePath.Under(root, name))).ToList();
            var folder = SafePath.Under(root, "ModGate/logs");
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, "profile-setup-*.txt").OrderBy(p => p, StringComparer.Ordinal))
                {
                    var relative = "ModGate/logs/" + Path.GetFileName(file);
                    SafePath.Under(root, relative); result.Add(relative);
                }
            return result;
        }
        public static string Create(string root, string destination, SupportReportOptions options, IReadOnlyList<ModEntry>? mods = null, ProfileHealthReport? health = null)
        {
            if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Support reports must use a ZIP filename.");
            var available = AvailableLogs(root);
            var selected = options.IncludeLogs ? (options.SelectedLogs.Count == 0 ? available : options.SelectedLogs).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (selected.Any(name => !available.Contains(name, StringComparer.Ordinal))) throw new InvalidDataException("Only known diagnostic logs can be included in a support report.");
            var output = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var created = false;
            try
            {
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    created = true;
                    Write(zip, "report.txt", "ModGate " + Constants.Version + "\nCreated UTC: " + DateTime.UtcNow.ToString("O") +
                        "\nStage: " + Clean(root, options.Stage) + "\nLast error: " + Clean(root, options.LastError) +
                        "\nThis report contains selected diagnostic data. Password vaults, configuration files and profile credentials are excluded.\n");
                    if (options.IncludeMods)
                    {
                        var text = new StringBuilder("Installed mod inventory\n");
                        foreach (var mod in (mods ?? Array.Empty<ModEntry>()).OrderBy(m => m.Guid, StringComparer.Ordinal))
                        {
                            var version = System.Version.TryParse(mod.Version, out var parsed) ? parsed.ToString() : "unknown";
                            text.AppendLine(Clean(root, mod.Name) + " | " + Clean(root, mod.Guid) + " | " + version + " | " + Clean(root, mod.Package) + " | " + Clean(root, mod.Side));
                        }
                        Write(zip, "mods.txt", text.ToString());
                    }
                    if (options.IncludeHealth && health != null)
                    {
                        var text = new StringBuilder("Profile health\nSlot: " + health.Slot + "\nTrusted baseline: " + health.HasReceipt + "\nLoaded evidence: " + health.LoadedEvidenceAvailable + "\n");
                        foreach (var issue in health.Issues) text.AppendLine((issue.Warning ? "NOTICE " : "ISSUE ") + issue.Code + ": " + issue.Message);
                        Write(zip, "health.txt", Clean(root, text.ToString()));
                    }
                    foreach (var name in selected)
                    {
                        var file = SafePath.Under(root, name);
                        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(input, Encoding.UTF8, true))
                        using (var writer = new StreamWriter(zip.CreateEntry("logs/" + name.Replace('/', '_'), CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null) writer.WriteLine(Clean(root, line));
                        }
                    }
                }
                return output;
            }
            catch { if (created && File.Exists(output)) File.Delete(output); throw; }
        }
        private static void Write(ZipArchive zip, string name, string content)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) writer.Write(content);
        }
        private static string Clean(string root, string text) => Redact(text).Replace(Path.GetFullPath(root), "[BepInEx]");
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var value = Regex.Replace(text, @"https?://[^\s\""'<>]+", match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[URL]";
                var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
                return safe.Uri.GetLeftPart(UriPartial.Path);
            }, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_=.\-]+", "[AUTH REDACTED]", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"(?<prefix>[\""']?\b(?:password|passwd|pwd|server[_-]?password|token|secret|secret[_-]?key|client[_-]?secret|credential|authorization|api[_-]?(?:key|token)|auth[_-]?token|entity[_-]?token|access[_-]?(?:key|token)|refresh[_-]?token|session[_-]?(?:ticket|key)|connectionstring|cookie|credentialkey|serverkey|join[_ -]?code)[\""']?\s*(?::|=|\bis\b|\s)\s*)(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\r\n,;]+)", "${prefix}[REDACTED]", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"(?<![A-Za-z0-9+/_=\-])[A-Za-z0-9+/_=\-]{64,}(?![A-Za-z0-9+/_=\-])", "[LONG TOKEN REDACTED]");
            value = Regex.Replace(value, @"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\w.])", match =>
            {
                var host = match.Value.Split(':')[0];
                return IPAddress.TryParse(host, out _) ? "[IP REDACTED]" : match.Value;
            });
            value = Regex.Replace(value, @"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{0,4}:){2,}[A-Fa-f0-9:.%]+", match => IPAddress.TryParse(match.Value, out _) ? "[IP REDACTED]" : match.Value);
            value = Regex.Replace(value, @"\b(?:Steam|Xbox|PlayStation)_\d+\b", "[PLAYER ID]", RegexOptions.IgnoreCase);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) value = Regex.Replace(value, Regex.Escape(home), "[USER]", RegexOptions.IgnoreCase);
            return value;
        }
    }
}
