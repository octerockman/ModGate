using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace ModGate
{
    [DataContract] internal sealed class DependencyCache
    {
        [DataMember] public List<DependencyCacheEntry> Mods = new List<DependencyCacheEntry>();
    }
    [DataContract] internal sealed class DependencyCacheEntry
    {
        [DataMember] public string Guid = "";
        [DataMember] public string Version = "";
        [DataMember] public List<PluginDependency> Dependencies = new List<PluginDependency>();
    }

    // The owner's mod list contains editable settings. Generated DLL dependency declarations
    // remain available for policy/module handling without looking like additional mod entries.
    internal static class ServerConfig
    {
        internal const string CachePath = "ModGate/plugin-dependencies.json";

        public static ServerManifest OwnerView(ServerManifest settings)
        {
            var copy = Json.CloneLocal(settings);
            foreach (var mod in copy.Mods) mod.Dependencies = null!; // EmitDefaultValue=false omits this generated field.
            return copy;
        }
        public static string WriteReadable(ServerManifest settings) => Json.WriteReadable(OwnerView(settings));

        public static ServerManifest Load(string path, string root)
        {
            var settings = Json.Read<ServerManifest>(File.ReadAllText(path));
            var cachePath = SafePath.Under(root, CachePath);
            if (!File.Exists(cachePath)) return settings;
            DependencyCache cache;
            try
            {
                cache = Json.Read<DependencyCache>(File.ReadAllText(cachePath), Constants.MaxLocalJson);
                if (cache.Mods == null || cache.Mods.Count > Constants.MaxMods || cache.Mods.Any(m => m == null || m.Dependencies == null || m.Dependencies.Count > 128 || m.Dependencies.Any(d => d == null))) return settings;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SerializationException) { return settings; }
            foreach (var mod in settings.Mods)
            {
                // Accept old files with inline declarations during migration. A cache only fills
                // missing metadata for exactly the same GUID and DLL version, never new mod rows.
                if (mod.Dependencies != null && mod.Dependencies.Count > 0) continue;
                var saved = cache.Mods.FirstOrDefault(m => m.Guid == ConfigFields.Value(mod.Guid) && PolicyEngine.VersionsEqual(m.Version, ConfigFields.Value(mod.Version)));
                if (saved != null) mod.Dependencies = Json.CloneLocal(saved.Dependencies);
            }
            return settings;
        }

        public static void Save(string path, string root, ServerManifest settings)
        {
            var cache = new DependencyCache
            {
                Mods = settings.Mods.Where(m => DependencyRules.Of(m).Any()).Select(m => new DependencyCacheEntry
                {
                    Guid = ConfigFields.Value(m.Guid), Version = ConfigFields.Value(m.Version), Dependencies = Json.CloneLocal(m.Dependencies)
                }).ToList()
            };
            // Save the generated data first: a failed owner-file replacement leaves the old
            // inline declarations intact. Both individual writes use atomic replacement.
            var cachePath = SafePath.Under(root, CachePath);
            if (!File.Exists(cachePath) || File.ReadAllText(cachePath) != Json.Write(cache)) Json.Save(cachePath, cache);
            var view = OwnerView(settings);
            if (!File.Exists(path) || File.ReadAllText(path) != Json.WriteReadable(view)) Json.Save(path, view, readable: true);
        }
    }
}
