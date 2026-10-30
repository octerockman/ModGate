using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal static class DownloadCache
    {
        private static string FilePath(string root, PackageId id) => SafePath.Under(root, "ModGate/download-cache/" + id.CacheName);
        public static async Task<string> Get(string root, PackageId id, IPackageSource source, Action<string> progress, CancellationToken token)
        {
            var path = FilePath(root, id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (id.Repository != "github" && File.Exists(path) && File.Exists(path + ".sha256") && SafePath.Hash(path) == File.ReadAllText(path + ".sha256")) { progress("Reusing downloaded package " + id.FullName); return path; }
            if (File.Exists(path)) File.Delete(path);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var partial = path + ".partial";
                    // RepositorySource supports safe byte-range resumption; simple fixture/other sources restart.
                    if (!(source is RepositorySource) && !(source is ProfilePackageSource) && File.Exists(partial)) File.Delete(partial);
                    await source.Download(id, partial, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); File.Move(partial, path);
                    File.WriteAllText(path + ".sha256", SafePath.Hash(path)); return path;
                }
                catch (Exception failure) when ((failure is IOException || failure is System.Net.Http.HttpRequestException) && !(failure is InvalidDataException) && !(failure is PackageResolutionChangedException) && !(failure is PackageNotFoundException) && attempt < 2)
                { progress("Retry " + (attempt + 1) + "/2: " + id.FullName); }
            }
        }
        public static void Invalidate(string root, PackageId id)
        {
            var path = FilePath(root, id); foreach (var suffix in new[] { "", ".sha256", ".partial", ".partial.resume" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
        public static void DiscardStage(string root, string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid staging cleanup identifier.");
            var path = SafePath.Under(root, "ModGate/staging/" + id); if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)) SafePath.Under(path, file.Substring(path.Length + 1));
            Directory.Delete(path, true);
        }
        public static void Clear(string root)
        {
            if (Profiles.Pending(root)) throw new InvalidOperationException("Restart before clearing downloads for a scheduled installation.");
            foreach (var folder in new[] { "ModGate/staging", "ModGate/download-cache" })
            {
                var path = SafePath.Under(root, folder); if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)) SafePath.Under(path, file.Substring(path.Length + 1));
                Directory.Delete(path, true);
            }
        }
    }
}
