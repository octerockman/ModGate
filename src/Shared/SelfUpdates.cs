using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    [DataContract] internal sealed class SelfUpdatePreferences
    {
        [DataMember] public bool CheckOnLaunch = true;
        [DataMember] public bool IncludePrereleases = Constants.Version.StartsWith("0.", StringComparison.Ordinal);
        [DataMember] public string LastCheckedUtc = "";
    }
    [DataContract] internal sealed class SelfUpdateRelease
    {
        [DataMember] public string Version = "", Tag = "", Asset = "", Url = "", Sha256 = "";
        [DataMember] public long Size;
        [DataMember] public bool Prerelease;
    }
    [DataContract] internal sealed class SelfUpdateFile
    {
        [DataMember] public string Target = "", Staged = "", Sha256 = "", PreviousSha256 = "", Backup = "";
    }
    [DataContract] internal sealed class SelfUpdatePlan
    {
        [DataMember] public string Id = "", Root = "", Version = "", PreviousVersion = "", ProcessStart = "";
        [DataMember] public int ProcessId;
        [DataMember] public List<SelfUpdateFile> Files = new List<SelfUpdateFile>();
    }
    [DataContract] internal sealed class SelfUpdateApiRelease
    {
        [DataMember(Name = "tag_name")] public string Tag = "";
        [DataMember(Name = "draft")] public bool Draft = false;
        [DataMember(Name = "prerelease")] public bool Prerelease = false;
        [DataMember(Name = "assets")] public List<SelfUpdateApiAsset> Assets = new List<SelfUpdateApiAsset>();
    }
    [DataContract] internal sealed class SelfUpdateApiAsset
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "browser_download_url")] public string Url = "";
        [DataMember(Name = "digest")] public string Digest = "";
        [DataMember(Name = "size")] public long Size = 0;
        [DataMember(Name = "state")] public string State = "";
    }
    internal sealed class SelfUpdateSource
    {
        internal const string Repository = "https://github.com/octerockman/ModGate";
        internal const string Api = "https://api.github.com/repos/octerockman/ModGate/releases";
        internal const long MaxArchiveBytes = 32 * 1024 * 1024;
        internal const long MaxDllBytes = 32 * 1024 * 1024;
        private readonly Func<SystemRequest, CancellationToken, Task<SystemResponse>> send;
        public SelfUpdateSource(Func<SystemRequest, CancellationToken, Task<SystemResponse>>? transport = null) { send = transport ?? SystemHttps.Send; }
        internal static string NormalizeVersion(string text)
        {
            if (text == null || text.Length > 48 || !Regex.IsMatch(text, @"^\d+\.\d+\.\d+(?:\.0)?$") || !System.Version.TryParse(text, out var v)) throw new InvalidDataException("Invalid ModGate release version.");
            return v.Major + "." + v.Minor + "." + v.Build;
        }
        internal static string? RequiredByServer(Policy policy)
        {
            if (policy.Mods == null || policy.Mods.Count > Constants.MaxMods) return null;
            var matches = policy.Mods.Where(m => m != null && m.Guid == Constants.Guid).ToArray();
            if (matches.Length != 1 || PolicyEngine.VersionsEqual(matches[0].Version, Constants.Version)) return null;
            return NormalizeVersion(matches[0].Version);
        }
        internal static bool Trusted(Uri url) => url.IsAbsoluteUri && url.Scheme == "https" && url.Port == 443 && url.UserInfo.Length == 0 && url.Fragment.Length == 0 &&
            (url.Host == "api.github.com" || url.Host == "github.com" || url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com");
        internal static SelfUpdateRelease Select(SelfUpdateApiRelease release, bool includePrereleases, string? required = null)
        {
            if (release == null || release.Tag == null || release.Draft || release.Prerelease && !includePrereleases || release.Assets == null || release.Assets.Count > 128) throw new InvalidDataException("This official release is unavailable in the selected update channel.");
            var version = NormalizeVersion(release.Tag.StartsWith("v", StringComparison.Ordinal) ? release.Tag.Substring(1) : release.Tag);
            if (release.Tag != version && release.Tag != "v" + version || required != null && version != NormalizeVersion(required)) throw new InvalidDataException("The release tag does not match the requested ModGate version.");
            // Prefer the approved package. Source archives and arbitrary ZIPs are never candidates.
            var names = new[] { "ValheimModGate-" + version + "-publish.zip", "ValheimModGate-" + version + ".zip" };
            SelfUpdateApiAsset? asset = null;
            foreach (var name in names)
            {
                var found = release.Assets.Where(a => a != null && a.Name == name).ToArray();
                if (found.Length > 1) throw new InvalidDataException("Duplicate ModGate release assets.");
                if (found.Length == 1) { asset = found[0]; break; }
            }
            if (asset == null || asset.State != "uploaded" || asset.Size <= 0 || asset.Size > MaxArchiveBytes || !Regex.IsMatch(asset.Digest ?? "", @"^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("The official runtime ZIP or its GitHub SHA-256 digest is missing or invalid.");
            var expected = Repository + "/releases/download/" + release.Tag + "/" + asset.Name;
            if (asset.Url != expected) throw new InvalidDataException("The update asset is outside the official ModGate repository.");
            return new SelfUpdateRelease { Version = version, Tag = release.Tag, Asset = asset.Name, Url = expected, Sha256 = (asset.Digest ?? "").Substring(7).ToLowerInvariant(), Size = asset.Size, Prerelease = release.Prerelease };
        }
        public async Task<SelfUpdateRelease> Find(string? required, bool includePrereleases, CancellationToken token)
        {
            if (required != null)
            {
                required = NormalizeVersion(required);
                foreach (var tag in new[] { "v" + required, required })
                {
                    try { return Select(await Read<SelfUpdateApiRelease>(new Uri(Api + "/tags/" + tag), token).ConfigureAwait(false), includePrereleases, required); }
                    catch (PackageNotFoundException) { }
                }
                throw new InvalidDataException("The server’s ModGate " + required + " has no published official GitHub release. Ask its owner to publish that version or update the server.");
            }
            var releases = await Read<List<SelfUpdateApiRelease>>(new Uri(Api + "?per_page=30"), token).ConfigureAwait(false);
            if (releases == null || releases.Count > 30) throw new InvalidDataException("Invalid ModGate release listing.");
            var candidates = new List<SelfUpdateRelease>();
            foreach (var release in releases)
            {
                if (release == null || release.Draft || release.Prerelease && !includePrereleases) continue;
                try { candidates.Add(Select(release, includePrereleases)); } catch (InvalidDataException) { }
            }
            return candidates.OrderByDescending(r => System.Version.Parse(r.Version)).FirstOrDefault() ?? throw new InvalidDataException("No verified official ModGate release is available in this channel. Early releases can be enabled in Settings.");
        }
        private async Task<T> Read<T>(Uri uri, CancellationToken token)
        {
            using (var response = await Get(uri, token).ConfigureAwait(false))
            using (var data = new MemoryStream())
            {
                await Copy(response.Body, data, 4 * 1024 * 1024, response.ContentLength, token).ConfigureAwait(false);
                return Json.Read<T>(Encoding.UTF8.GetString(data.ToArray()), 4 * 1024 * 1024);
            }
        }
        private async Task<SystemResponse> Get(Uri uri, CancellationToken token)
        {
            for (var n = 0; n < 6; n++)
            {
                token.ThrowIfCancellationRequested();
                if (!Trusted(uri)) throw new InvalidDataException("Untrusted ModGate update redirect.");
                var response = await send(new SystemRequest { Url = uri }, token).ConfigureAwait(false);
                if (response.StatusCode == 404) { response.Dispose(); throw new PackageNotFoundException(uri.AbsolutePath); }
                if (response.StatusCode >= 300 && response.StatusCode < 400)
                {
                    response.Headers.TryGetValue("Location", out var location); response.Dispose();
                    if (string.IsNullOrEmpty(location) || !Uri.TryCreate(uri, location, out var next)) throw new InvalidDataException("Invalid ModGate update redirect.");
                    uri = next; continue;
                }
                if (response.StatusCode == 200) return response;
                var status = response.StatusCode; response.Dispose();
                throw new IOException("GitHub returned HTTP " + status + ". Wait before retrying if its public API rate limit was reached.");
            }
            throw new IOException("Too many ModGate update redirects.");
        }
        internal static async Task Copy(Stream input, Stream output, long limit, long? expected, CancellationToken token)
        {
            if (expected.HasValue && (expected <= 0 || expected > limit)) throw new InvalidDataException("Invalid update response size.");
            var buffer = new byte[65536]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                total = checked(total + read);
                if (total > limit || expected.HasValue && total > expected.Value) throw new InvalidDataException("Update response exceeded its expected size.");
                await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (expected.HasValue && total != expected.Value) throw new IOException("The ModGate download was incomplete.");
        }
        public async Task<SelfUpdatePlan> Prepare(string root, SelfUpdateRelease release, string plugin, string preloader, CancellationToken token)
        {
            ValidateRelease(release);
            var folder = SafePath.Under(root, "ModGate/self-updates/" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var archive = SafePath.Under(folder, "release.zip");
            try
            {
                using (var response = await Get(new Uri(release.Url), token).ConfigureAwait(false))
                using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await Copy(response.Body, file, MaxArchiveBytes, release.Size, token).ConfigureAwait(false);
                if (SafePath.Hash(archive) != release.Sha256) throw new InvalidDataException("The ModGate runtime ZIP failed SHA-256 verification.");
                token.ThrowIfCancellationRequested();
                return Extract(root, folder, archive, release.Version, plugin, preloader);
            }
            finally { if (File.Exists(archive)) File.Delete(archive); }
        }
        internal static void ValidateRelease(SelfUpdateRelease release)
        {
            var v = NormalizeVersion(release.Version);
            if (release.Tag != v && release.Tag != "v" + v || release.Asset != "ValheimModGate-" + v + "-publish.zip" && release.Asset != "ValheimModGate-" + v + ".zip" ||
                release.Url != Repository + "/releases/download/" + release.Tag + "/" + release.Asset || release.Size <= 0 || release.Size > MaxArchiveBytes || !Regex.IsMatch(release.Sha256 ?? "", @"^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid official ModGate update receipt.");
        }
        internal static SelfUpdatePlan Extract(string root, string folder, string archive, string version, string plugin, string preloader)
        {
            SelfUpdateInstaller.RequireSinglePair(root);
            var plan = new SelfUpdatePlan { Id = Path.GetFileName(folder), Root = Path.GetFullPath(root), Version = NormalizeVersion(version), PreviousVersion = Constants.Version };
            var targets = new[] { SelfUpdateInstaller.Target(root, plugin, false), SelfUpdateInstaller.Target(root, preloader, true) };
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 256) throw new InvalidDataException("Unexpected ModGate runtime archive contents.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
                    if (name.Length == 0) continue;
                    SafePath.Under(folder, name);
                    var attributes = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes")?.GetValue(entry);
                    if (!seen.Add(name) || attributes is int unixAttributes && ((unixAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Duplicate or linked update archive entry.");
                    var index = name.Equals("BepInEx/plugins/ValheimModGate/ValheimModGate.dll", StringComparison.OrdinalIgnoreCase) ? 0 : name.Equals("BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
                    if (index < 0) { if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected executable in the ModGate runtime ZIP."); continue; }
                    if (entry.Length <= 0 || entry.Length > MaxDllBytes) throw new InvalidDataException("Invalid ModGate DLL size.");
                    var relative = "ModGate/self-updates/" + plan.Id + "/" + Path.GetFileName(name);
                    var staged = SafePath.Under(root, relative);
                    using (var input = entry.Open()) using (var output = File.Create(staged)) Copy(input, output, MaxDllBytes, entry.Length, CancellationToken.None).GetAwaiter().GetResult();
                    VerifyAssembly(staged, index == 1, plan.Version);
                    VerifyAssembly(SafePath.Under(root, targets[index]), index == 1, Constants.Version);
                    plan.Files.Add(new SelfUpdateFile { Target = targets[index], Staged = relative, Sha256 = SafePath.Hash(staged), PreviousSha256 = SafePath.Hash(SafePath.Under(root, targets[index])), Backup = "ModGate/self-updates/" + plan.Id + "/backup-" + Path.GetFileName(name) });
                }
            }
            if (plan.Files.Count != 2) throw new InvalidDataException("The runtime ZIP must contain both ModGate DLLs.");
            plan.Files = plan.Files.OrderBy(f => f.Target.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            SelfUpdateInstaller.Validate(root, plan, true);
            Json.Save(SafePath.Under(folder, "prepared.json"), plan);
            return plan;
        }
        internal static void VerifyAssembly(string file, bool preloader, string version)
        {
            var name = AssemblyName.GetAssemblyName(file);
            if (name.Name != (preloader ? "ValheimModGate.Preloader" : "ValheimModGate") || !PolicyEngine.VersionsEqual(name.Version?.ToString() ?? "", version)) throw new InvalidDataException("Wrong ModGate DLL identity or version: " + Path.GetFileName(file));
        }
        internal static SelfUpdatePreferences Preferences(string root)
        {
            var path = SafePath.Under(root, "ModGate/self-update-preferences.json");
            return File.Exists(path) ? Json.Read<SelfUpdatePreferences>(File.ReadAllText(path)) : new SelfUpdatePreferences();
        }
        internal static void SavePreferences(string root, SelfUpdatePreferences preferences) => Json.Save(SafePath.Under(root, "ModGate/self-update-preferences.json"), preferences);
    }
}
