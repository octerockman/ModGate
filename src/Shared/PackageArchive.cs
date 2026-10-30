using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ModGate
{
    // Some repositories publish a metadata ZIP containing the actual mod ZIP.
    internal sealed class PackageArchive : IDisposable
    {
        public ZipArchive Zip { get; private set; } = null!;
        public readonly List<PackageManifest> Manifests = new List<PackageManifest>();
        private readonly List<IDisposable> owned = new List<IDisposable>();
        public static PackageArchive Open(string file, CancellationToken token = default)
        {
            var payload = new PackageArchive();
            try
            {
                payload.Zip = ZipFile.OpenRead(file); payload.owned.Add(payload.Zip);
                for (var depth = 0; ; depth++)
                {
                    var manifest = payload.Zip.Entries.FirstOrDefault(e => e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
                    if (manifest != null && manifest.Length <= 64 * 1024)
                    {
                        try { payload.Manifests.Add(PackageManifest.Read(manifest.Open())); }
                        catch (Exception error) when (error is IOException || error is InvalidDataException || error is System.Runtime.Serialization.SerializationException) { }
                    }
                    var entries = payload.Zip.Entries.Where(e => e.Name.Length > 0 && !Documentation(e.FullName)).ToArray();
                    if (entries.Length != 1 || !entries[0].Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) break;
                    if (depth >= 2) throw new InvalidDataException("Nested mod archive exceeds wrapper depth.");
                    SafePath.Under(Path.GetDirectoryName(Path.GetFullPath(file))!, entries[0].FullName);
                    // Large wrappers use a seekable temporary file, never a whole-archive memory buffer.
                    var temporary = new FileStream(Path.Combine(Path.GetTempPath(), "ModGate-wrapper-" + Guid.NewGuid().ToString("N") + ".zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                    payload.owned.Add(temporary);
                    using (var input = entries[0].Open()) Downloads.CopyPayload(input, temporary, token);
                    temporary.Position = 0; payload.Zip = new ZipArchive(temporary, ZipArchiveMode.Read, true); payload.owned.Add(payload.Zip);
                }
                return payload;
            }
            catch { payload.Dispose(); throw; }
        }
        public static string AssetHash(string file, PackageId id)
        {
            if (id.Repository != "github" || !id.Asset.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return SafePath.Hash(file);
            using (var payload = Open(file))
            using (var input = payload.Zip.Entries.Single(e => e.FullName == id.Asset).Open())
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        private static bool Documentation(string name) => new[] { "manifest.json", "readme.md", "changelog.md", "icon.png", "license", "license.txt", "license.md" }.Contains(name, StringComparer.OrdinalIgnoreCase);
        public void Dispose() { foreach (var item in owned.AsEnumerable().Reverse()) item.Dispose(); }
    }
}
