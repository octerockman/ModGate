using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ModGate
{
    internal static class Constants
    {
        public const string Guid = "studio.valheim.modgate";
        public const string Version = "0.4.4";
        public const int Protocol = 6;
        public const int ManifestSchema = 1;
        public const int MaxJson = 256 * 1024;
        public const int MaxLocalJson = 8 * 1024 * 1024;
        public const int MaxMods = 512;
    }

    [DataContract] internal sealed class PluginDependency
    {
        [DataMember] public string Guid = "";
        [DataMember] public string MinimumVersion = "";
        [DataMember] public bool Optional = false;
        [DataMember] public string Package = "";
    }

    [DataContract] internal sealed class ModEntry
    {
        [DataMember(Name = "guid")] public string Guid = "";
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "version")] public string Version = "";
        [DataMember(Name = "side")] public string Side = "unclassified";
        [DataMember(Name = "package")] public string Package = "";
        [DataMember(Name = "downloadUrl")] public string DownloadUrl = "";
        [DataMember(Name = "nexusUrl")] public string NexusUrl = "";
        [DataMember(Name = "automatic")] public bool Automatic = false;
        [DataMember(Name = "autoUpdate")] public bool? AutoUpdate = null;
        [DataMember(Name = "allowNewerVersion")] public bool? AllowNewerVersion = null;
        [DataMember(Name = "manifestSource", EmitDefaultValue = false)] public ManifestOrigin? ManifestSource = null;
        [DataMember(Name = "dependencies", EmitDefaultValue = false)] public List<PluginDependency> Dependencies = new List<PluginDependency>();
        // Local inventory only. Never sent to another machine.
        public string LocalFile = "";
        public PackageManifest? LocalManifest = null;
        public bool LocalPackageObserved = false;
        public List<string> LocalPackageHints = new List<string>();
        public List<string> LocalConfigFiles = new List<string>();
    }

    [DataContract] internal sealed class ServerManifest
    {
        [DataMember(Name = "schema")] public int Schema = Constants.ManifestSchema;
        [DataMember(Name = "serverName")] public string ServerName = "Valheim server";
        [DataMember(Name = "allowExtraClientMods")] public bool AllowExtraClientMods = false;
        [DataMember(Name = "allowNewerClientVersions")] public bool AllowNewerClientVersions = false;
        [DataMember(Name = "autoUpdateOnRestart")] public bool AutoUpdateOnRestart = false;
        [DataMember(Name = "autoUpdateAllMods")] public bool AutoUpdateAllMods = false;
        [DataMember(Name = "mods")] public List<ModEntry> Mods = new List<ModEntry>();
    }

    [DataContract] internal sealed class Policy
    {
        [DataMember] public int Protocol = Constants.Protocol;
        [DataMember] public string Nonce = "";
        [DataMember] public string ServerName = "";
        [DataMember] public string ServerId = "";
        [DataMember] public bool AllowExtras = false;
        [DataMember] public bool AllowNewerClientVersions = false;
        [DataMember] public string Error = "";
        [DataMember] public List<ModEntry> Mods = new List<ModEntry>();
        [DataMember] public List<string> ForbiddenPackages = new List<string>();
        [DataMember] public List<string> ForbiddenPluginGuids = new List<string>();
    }

    [DataContract] internal sealed class InventoryReport
    {
        [DataMember] public int Protocol = Constants.Protocol;
        [DataMember] public string Nonce = "";
        [DataMember] public List<ModEntry> Mods = new List<ModEntry>();
    }

    internal sealed class Comparison
    {
        public readonly List<ModEntry> Missing = new List<ModEntry>();
        public readonly List<ModEntry> WrongVersion = new List<ModEntry>();
        public readonly List<ModEntry> Extra = new List<ModEntry>();
        public bool Matches => Missing.Count + WrongVersion.Count + Extra.Count == 0;
    }

    internal sealed class PackageId
    {
        private static readonly Regex Pattern = new Regex(@"^([A-Za-z0-9_]+)-([A-Za-z0-9_]+)-(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
        public readonly string Owner, Name, Version;
        public readonly string Repository;
        public string Asset = "";
        public string Key => Repository == "github" ? "GitHub_" + Hash(Owner.ToLowerInvariant() + "/" + Name.ToLowerInvariant()) : Owner + "-" + Name;
        public string RawFullName => Key + "-" + Version;
        public string FullName => Repository == "github" ? "github:" + Owner + "/" + Name + "/" + Version + "/" + Asset : Repository == "hexium" ? "hexium:" + RawFullName : RawFullName;
        public string DownloadUrl => (Repository == "hexium" ? "https://hexium.gg" : "https://thunderstore.io") + "/package/download/" + Owner + "/" + Name + "/" + Version + "/";
        public string CacheName => Repository + "-" + (Repository == "github" ? Hash(FullName) : RawFullName) + ".zip";
        private static string Hash(string value) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
        private PackageId(string owner, string name, string version, string repository) { Owner = owner; Name = name; Version = version; Repository = repository; }
        public PackageId FromRepository(string repository) => Repository == "github" ? this : repository == "github" ? throw new InvalidDataException("GitHub requires a manifest-authorized release asset.") : Parse((repository == "hexium" ? "hexium:" : "") + RawFullName);
        public static PackageId Parse(string value)
        {
            if (value != null && value.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Substring(7).Split('/');
                if (parts.Length != 4 || value.Length > 400 || parts.Any(part => part == "." || part == "..") || !Regex.IsMatch(parts[0], @"^[A-Za-z0-9_-]+$") || !Regex.IsMatch(parts[1], @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(parts[2], @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(parts[3], @"^[A-Za-z0-9_.-]+\.(zip|dll)$", RegexOptions.IgnoreCase)) throw new InvalidDataException("Invalid manifest-authorized release asset ID.");
                return new PackageId(parts[0], parts[1], parts[2], "github") { Asset = parts[3] };
            }
            if (value != null && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return FromDownloadUrl(value);
            var repository = "thunderstore";
            if (value != null && value.StartsWith("hexium:", StringComparison.OrdinalIgnoreCase)) { repository = "hexium"; value = value.Substring(7); }
            else if (value != null && value.StartsWith("thunderstore:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(13);
            var match = Pattern.Match(value ?? "");
            if (!match.Success || value!.Length > 200) throw new InvalidDataException("Only Thunderstore or Hexium packages are allowed. Use Owner-Name-1.2.3 or a version-specific https://thunderstore.io download link.");
            return new PackageId(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, repository);
        }
        public static PackageId FromDownloadUrl(string value)
        {
            if (value == null || value.Length > 600 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || (uri.Host != "thunderstore.io" && uri.Host != "hexium.gg") || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value.Contains("%") || value.Contains("\\")) throw new InvalidDataException("downloadUrl must be a version-specific HTTPS link on thunderstore.io or hexium.gg only.");
            var match = Regex.Match(uri.AbsolutePath, @"^/(?:package/download|package|c/valheim/p)/([A-Za-z0-9_]+)/([A-Za-z0-9_]+)/(\d+\.\d+\.\d+)/?$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("Use a version-specific Thunderstore or Hexium download or package page link, including its version.");
            return Parse((uri.Host == "hexium.gg" ? "hexium:" : "") + match.Groups[1].Value + "-" + match.Groups[2].Value + "-" + match.Groups[3].Value);
        }
        public static bool Allowed(string value) { try { Parse(value); return true; } catch (InvalidDataException) { return false; } }
    }

    internal static class NexusLinks
    {
        public static string Validate(string value)
        {
            if (value == null || value.Length > 600 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || (uri.Host != "www.nexusmods.com" && uri.Host != "nexusmods.com") || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || value.Contains("%") || value.Contains("\\") || !Regex.IsMatch(uri.AbsolutePath, @"^/valheim/mods/[1-9][0-9]*/?$") || (uri.Query.Length > 0 && !Regex.IsMatch(uri.Query, @"^\?(?:tab=files|file_id=[0-9]+|nmm=1)(?:&(?:tab=files|file_id=[0-9]+|nmm=1))*$"))) throw new InvalidDataException("nexusUrl must be an HTTPS Valheim mod page on nexusmods.com, optionally with ?tab=files or file_id.");
            return value;
        }
    }
    [DataContract] internal sealed class PackageMetadata
    {
        [DataMember(Name = "full_name")] public string FullName = "";
        [DataMember(Name = "is_active")] public bool? IsActive = null;
        [DataMember(Name = "dependencies")] public List<string> Dependencies = new List<string>();
        [DataMember(Name = "download_url")] public string DownloadUrl = "";
    }
    [DataContract] internal sealed class LatestPackage
    {
        [DataMember(Name = "is_deprecated")] public bool Deprecated = false;
        [DataMember(Name = "latest")] public PackageMetadata Latest = new PackageMetadata();
    }
    [DataContract] internal sealed class CatalogEntry
    {
        [DataMember(Name = "owner")] public string Owner = "";
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "versions")] public List<PackageMetadata> Versions = new List<PackageMetadata>();
        public List<string> PluginGuids = new List<string>();
    }
    [DataContract] internal sealed class LookupResult
    {
        [DataMember] public string Guid = "";
        [DataMember] public string Version = "";
        [DataMember] public string Package = "";
        [DataMember] public string Status = "";
        [DataMember] public string AssetSha256 = "";
    }
    [DataContract] internal sealed class LookupReport
    {
        [DataMember] public string Status = "";
        [DataMember] public List<LookupResult> Mods = new List<LookupResult>();
    }

    [DataContract] internal sealed class FileDigest
    {
        [DataMember] public string Path = "";
        [DataMember] public string Sha256 = "";
    }
    [DataContract] internal sealed class PendingInstall
    {
        [DataMember] public string Id = "";
        [DataMember] public List<string> Packages = new List<string>();
        [DataMember] public List<string> LocalPackages = new List<string>();
        [DataMember] public List<FileDigest> Files = new List<FileDigest>();
    }
    [DataContract] internal sealed class MoveOperation
    {
        [DataMember] public string Source = "";
        [DataMember] public string Destination = "";
    }
    [DataContract] internal sealed class SavedProfile
    {
        [DataMember] public string Slot = "";
        [DataMember] public bool Incomplete = false;
        [DataMember] public List<ProfileEditGroup> DisabledGroups = new List<ProfileEditGroup>();
        [DataMember] public Policy? ServerPolicy = null;
        [DataMember] public string ServerKey = "";
        [DataMember] public string CredentialKey = "";
        [DataMember] public string Name = "";
        [DataMember] public List<ModEntry> Mods = new List<ModEntry>();
        [DataMember] public List<string> Packages = new List<string>();
        [DataMember] public List<ConfigBinding> ConfigBindings = new List<ConfigBinding>();
    }
    [DataContract] internal sealed class ProfileCatalog
    {
        [DataMember] public string Active = "offline";
        [DataMember] public List<SavedProfile> Servers = new List<SavedProfile>();
        [DataMember] public List<ModEntry> OfflineMods = new List<ModEntry>();
        [DataMember] public List<ConfigBinding> OfflineBindings = new List<ConfigBinding>();
        [DataMember] public List<ProfileEditGroup> OfflineDisabledGroups = new List<ProfileEditGroup>();
    }
    [DataContract] internal sealed class ConfigBinding
    {
        [DataMember] public string Guid = "";
        [DataMember] public List<string> Files = new List<string>();
    }
    [DataContract] internal sealed class ProfileCommand
    {
        [DataMember] public string Kind = "";
        [DataMember] public string Target = "";
        [DataMember] public SavedProfile? Profile = null;
        [DataMember] public PendingInstall? Install = null;
        [DataMember] public bool ReplaceConfig = false;
        [DataMember] public ProfileEdit? Edit = null;
        [DataMember(EmitDefaultValue = false)] public ProfileRestorePlan? Restore = null;
    }
    [DataContract] internal sealed class ProfileJournal
    {
        [DataMember] public string Id = "";
        [DataMember] public bool Committed = false;
        [DataMember] public ProfileCatalog Next = new ProfileCatalog();
        [DataMember] public List<MoveOperation> Moves = new List<MoveOperation>();
        [DataMember] public List<string> Cleanup = new List<string>();
    }

    internal static class Json
    {
        public static T Read<T>(string text, int maxLength = Constants.MaxJson, int maxItems = 50000)
        {
            if (text.Length > maxLength) throw new InvalidDataException("Mod data is too large.");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = maxItems }).ReadObject(stream)!;
        }
        // Local receipts can describe arbitrarily large profiles; network JSON keeps its own bounds.
        private static DataContractJsonSerializer LocalSerializer<T>() => new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue });
        public static T ReadLocal<T>(string path)
        {
            using (var stream = File.OpenRead(path)) return (T)LocalSerializer<T>().ReadObject(stream)!;
        }
        public static T CloneLocal<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = LocalSerializer<T>(); serializer.WriteObject(stream, value); stream.Position = 0;
                return (T)serializer.ReadObject(stream)!;
            }
        }
        public static string Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public static string WriteReadable<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value);
                    writer.Flush();
                }
                var formatted = Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n");
                return Regex.Replace(formatted, @"(?m)^(    \},)\n(?=    \{)", "$1\n\n") + "\n";
            }
        }
        public static void Save<T>(string path, T value, bool readable = false)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (readable) File.WriteAllText(temp, WriteReadable(value), new UTF8Encoding(false));
                else using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) LocalSerializer<T>().WriteObject(stream, value);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
