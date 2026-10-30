using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ModGate
{
    [DataContract] internal sealed class PackageManifest
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "version_number")] public string Version = "";
        [DataMember(Name = "website_url")] public string Website = "";
        [DataMember(Name = "dependencies")] public List<string> Dependencies = new List<string>();
        [DataMember(Name = "full_name")] public string FullName = "";
        [DataMember(Name = "namespace")] public string Namespace = "";
        public string Digest = "";
        public string RawText = "";
        public static PackageManifest Read(Stream input)
        {
            using (var reader = new StreamReader(input))
            {
                var buffer = new char[64 * 1024 + 1];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                if (length > 64 * 1024) throw new InvalidDataException("Package manifest exceeds its size limit.");
                return Parse(new string(buffer, 0, length));
            }
        }
        public static PackageManifest Parse(string text)
        {
            var result = Json.Read<PackageManifest>(text, 64 * 1024);
            if (result == null) throw new InvalidDataException("Package manifest must contain a JSON object.");
            result.Website = result.Website ?? ""; result.FullName = result.FullName ?? ""; result.Namespace = result.Namespace ?? ""; result.Dependencies = result.Dependencies ?? new List<string>();
            if (!Regex.IsMatch(result.Name ?? "", @"^[A-Za-z0-9_]{1,128}$") || !Regex.IsMatch(result.Version ?? "", @"^\d+\.\d+\.\d+$") || (result.Website?.Length ?? 0) > 600 || result.Dependencies == null || result.Dependencies.Count > 128) throw new InvalidDataException("Invalid package manifest identity or dependency list.");
            foreach (var dependency in result.Dependencies) if (PackageId.Parse(dependency).Repository == "github") throw new InvalidDataException("Manifest dependencies must use Thunderstore or Hexium package IDs.");
            using (var sha = SHA256.Create()) result.Digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
            result.RawText = text; return result;
        }
        public ManifestOrigin? GitOrigin()
        {
            return GitManifestRepository.TryParse(Website, out _) ? new ManifestOrigin { Name = Name, Version = Version, Website = Website, Digest = Digest, Dependencies = Dependencies.ToList() } : null;
        }
        public string? ExactPackage(string repository)
        {
            if (!string.IsNullOrEmpty(FullName))
                try { var full = PackageId.Parse(FullName); if (full.Repository != "github" && full.Version == Version) return full.FromRepository(repository).FullName; } catch (InvalidDataException) { }
            try
            {
                var pin = PackageId.Parse(Website);
                if (pin.Repository == repository && pin.Repository != "github" && pin.Version == Version) return pin.FullName;
            }
            catch (InvalidDataException) { }
            try
            {
                var uri = new Uri(Website);
                if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || Website.Contains("%")) return null;
                var expected = repository == "hexium" ? "hexium.gg" : "thunderstore.io";
                if (uri.Host != expected) return null;
                var match = Regex.Match(uri.AbsolutePath, @"^/(?:c/valheim/p|package)/([A-Za-z0-9_]+)/([A-Za-z0-9_]+)/?$");
                if (match.Success) return PackageId.Parse((repository == "hexium" ? "hexium:" : "") + match.Groups[1].Value + "-" + match.Groups[2].Value + "-" + Version).FullName;
            }
            catch (UriFormatException) { }
            if (!string.IsNullOrEmpty(Namespace))
                try { return PackageId.Parse((repository == "hexium" ? "hexium:" : "") + Namespace + "-" + Name + "-" + Version).FullName; } catch (InvalidDataException) { }
            return null;
        }
    }
    internal static class ManifestCandidates
    {
        public static void Enrich(IReadOnlyList<ModEntry> mods)
        {
            var dependencies = mods.Where(m => m.LocalManifest != null).SelectMany(m => m.LocalManifest!.Dependencies).Distinct().Take(2048).Select(PackageId.Parse).Where(p => p.Repository != "github" && !Downloads.IsBootstrap(p)).ToArray();
            foreach (var mod in mods)
            {
                var names = new[] { AutoSetup.Normalize(mod.Name), AutoSetup.Normalize(mod.Guid.Split('.').Last()), AutoSetup.Normalize(Path.GetFileNameWithoutExtension(mod.LocalFile)), AutoSetup.Normalize(mod.LocalManifest?.Name ?? "") };
                mod.LocalPackageHints = dependencies.Where(p => names.Contains(AutoSetup.Normalize(p.Name))).Select(p => p.RawFullName).Distinct().Take(16).ToList();
            }
        }
    }
    [DataContract] internal sealed class ManifestOrigin
    {
        [DataMember] public string Name = "";
        [DataMember] public string Version = "";
        [DataMember] public string Website = "";
        [DataMember] public string Digest = "";
        [DataMember] public string AssetId = "";
        [DataMember] public string AssetSha256 = "";
        [DataMember] public List<string> Dependencies = new List<string>();
        public bool Verified(PackageId id) => Allows(id) && AssetId == id.FullName && Regex.IsMatch(AssetSha256 ?? "", @"^[A-Fa-f0-9]{64}$");
        public bool Allows(PackageId id)
        {
            if (id.Repository != "github" || !Regex.IsMatch(Digest ?? "", @"^[A-Fa-f0-9]{64}$") || !Regex.IsMatch(Name ?? "", @"^[A-Za-z0-9_]{1,128}$") || !Regex.IsMatch(Version ?? "", @"^\d+\.\d+\.\d+$") || Dependencies == null || Dependencies.Count > 128 || !GitManifestRepository.TryParse(Website, out var repo)) return false;
            foreach (var dependency in Dependencies) { try { if (PackageId.Parse(dependency).Repository == "github") return false; } catch (InvalidDataException) { return false; } }
            return repo!.Owner.Equals(id.Owner, StringComparison.OrdinalIgnoreCase) && repo.Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase) &&
                (id.Version.TrimStart('v', 'V') == Version && (repo.Tag.Length == 0 || repo.Tag == id.Version)) && (repo.Asset.Length == 0 || repo.Asset == id.Asset);
        }
    }
    internal sealed class GitManifestRepository
    {
        public string Owner = "", Name = "", Tag = "", Asset = "";
        public static bool TryParse(string? value, out GitManifestRepository? repo)
        {
            repo = null;
            if (string.IsNullOrEmpty(value) || value!.Length > 600 || value.Contains("%") || value.Contains("\\") || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            var match = Regex.Match(uri.AbsolutePath, @"^/([A-Za-z0-9_-]+)/([A-Za-z0-9_.-]+)(?:/releases(?:/tag/([A-Za-z0-9_.-]+)|/download/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+\.(?:zip|dll)))?)?/?$");
            if (!match.Success || match.Groups[2].Value == "." || match.Groups[2].Value == "..") return false;
            repo = new GitManifestRepository { Owner = match.Groups[1].Value, Name = match.Groups[2].Value, Tag = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value, Asset = match.Groups[5].Value };
            return true;
        }
    }
    [DataContract] internal sealed class GitRelease
    {
        [DataMember(Name = "tag_name")] public string Tag = "";
        [DataMember(Name = "draft")] public bool Draft = false;
        [DataMember(Name = "prerelease")] public bool Prerelease = false;
        [DataMember(Name = "assets")] public List<GitAsset> Assets = new List<GitAsset>();
    }
    [DataContract] internal sealed class GitAsset
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "browser_download_url")] public string Url = "";
    }
}
