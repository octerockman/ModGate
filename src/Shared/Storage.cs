using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ModGate
{
    internal static class SafePath
    {
        public static string Under(string root, string relative)
            => Resolve(root, relative, false);

        // Read deployed mod files through manager-created file links. Never use this
        // path for writes, extraction, deletion, or recursive directory traversal.
        public static string ReadSource(string root, string relative)
            => Resolve(root, relative, true);

        private static string Resolve(string root, string relative, bool readFileLink)
        {
            if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException("Invalid relative file path.");
            var parts = relative.Replace('\\', '/').Split('/');
            if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ")))
                throw new InvalidDataException("Unsafe file path: " + relative);
            var normalizedRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(normalizedRoot, System.IO.Path.Combine(parts)));
            if (!path.StartsWith(normalizedRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("File path escapes its root.");
            var current = normalizedRoot;
            RejectLink(current);
            for (var index = 0; index < parts.Length; index++)
            {
                current = System.IO.Path.Combine(current, parts[index]);
                if (readFileLink && index == parts.Length - 1 && File.Exists(current) && !Directory.Exists(current)) continue;
                RejectLink(current);
            }
            return path;
        }
        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ModGate cannot install through symbolic links: " + path);
        }
        public static string Hash(string path)
        {
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal static class StageVerifier
    {
        internal static void VerifyStage(string stage, PendingInstall pending)
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in pending.Files)
            {
                var path = SafePath.Under(stage, item.Path);
                if (!listed.Add(path) || !File.Exists(path) || SafePath.Hash(path) != item.Sha256) throw new InvalidDataException("Staged file verification failed: " + item.Path);
            }
            foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                if (!listed.Contains(file)) throw new InvalidDataException("Unlisted file in staged installation.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullName in pending.Packages)
            {
                var id = PackageId.Parse(fullName);
                if (!seen.Add(id.Key) || !Directory.Exists(SafePath.Under(stage, "packages/" + id.Key))) throw new InvalidDataException("Missing or duplicate staged package.");
            }
            foreach (var key in pending.LocalPackages ?? new List<string>())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^(?:ManualNexus|LocalMod)_[A-F0-9]{64}$") || !seen.Add(key) || !Directory.Exists(SafePath.Under(stage, "packages/" + key))) throw new InvalidDataException("Invalid local mod package receipt.");
            }
            foreach (var file in pending.Files)
            {
                var name = file.Path.Replace('\\', '/');
                if (!seen.Any(key => name.StartsWith("packages/" + key + "/", StringComparison.OrdinalIgnoreCase)) && !name.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected staged file location.");
            }
        }

    }
}
