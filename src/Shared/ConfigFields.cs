using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace ModGate
{
    // Markers belong only to owner configuration. Runtime copies contain their effective text.
    internal static class ConfigFields
    {
        public static bool Locked(string? value) => value?.IndexOf('*') >= 0;
        public static string Value(string? value) => (value ?? "").Replace("*", "");
        private static IEnumerable<FieldInfo> Fields(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => Attribute.IsDefined(f, typeof(DataMemberAttribute)));
        private static void Strip(object value)
        {
            foreach (var field in Fields(value.GetType()))
            {
                if (field.FieldType == typeof(string) && field.GetValue(value) is string text) field.SetValue(value, Value(text));
                else if (field.GetValue(value) is ManifestOrigin origin) Strip(origin);
                else if (field.GetValue(value) is List<PluginDependency> dependencies) { foreach (var dependency in dependencies) Strip(dependency); }
                else if (field.GetValue(value) is List<string> list) field.SetValue(value, list.Select(Value).ToList());
            }
        }
        public static ServerManifest Runtime(ServerManifest source)
        {
            if (source.Mods == null || source.Mods.Count > Constants.MaxMods || source.Mods.Any(m => m == null)) throw new System.IO.InvalidDataException("Invalid server mod list.");
            var copy = Json.Read<ServerManifest>(Json.Write(source));
            Strip(copy);
            for (var i = 0; i < copy.Mods.Count; i++)
            {
                Strip(copy.Mods[i]);
                // Local evidence is not serialized and must survive configuration normalization.
                var old = source.Mods[i]; var mod = copy.Mods[i];
                mod.LocalFile = old.LocalFile; mod.LocalManifest = old.LocalManifest; mod.LocalPackageObserved = old.LocalPackageObserved;
                mod.LocalPackageHints = old.LocalPackageHints; mod.LocalConfigFiles = old.LocalConfigFiles;
            }
            return copy;
        }
        private static void PreserveContract(object original, object updated)
        {
            foreach (var field in Fields(original.GetType()))
            {
                var old = field.GetValue(original);
                if (old is string text && Locked(text)) field.SetValue(updated, text);
                else if (old is List<string> list && list.Any(Locked)) field.SetValue(updated, list.ToList());
                else if (old is List<PluginDependency> dependencies)
                {
                    var nextDependencies = field.GetValue(updated) as List<PluginDependency> ?? new List<PluginDependency>();
                    field.SetValue(updated, nextDependencies);
                    foreach (var dependency in dependencies)
                    {
                        var nextDependency = nextDependencies.FirstOrDefault(d => Value(d.Guid) == Value(dependency.Guid));
                        if (nextDependency != null) PreserveContract(dependency, nextDependency);
                        else if (Marked((object)dependency)) nextDependencies.Add(Json.CloneLocal(dependency));
                    }
                }
                else if (old is ManifestOrigin origin)
                {
                    var next = field.GetValue(updated) as ManifestOrigin;
                    if (next == null && Marked(origin)) { next = Json.Read<ManifestOrigin>(Json.Write(origin)); field.SetValue(updated, next); }
                    if (next != null) PreserveContract(origin, next);
                }
            }
        }
        private static bool Marked(object value) => Fields(value.GetType()).Any(f => f.GetValue(value) is string s ? Locked(s) : f.GetValue(value) is ManifestOrigin origin ? Marked(origin) : f.GetValue(value) is List<PluginDependency> dependencies ? dependencies.Any(d => Marked((object)d)) : f.GetValue(value) is List<string> list && list.Any(Locked));
        public static bool Marked(ModEntry mod) => Marked((object)mod);
        public static void Preserve(ServerManifest original, ServerManifest updated)
        {
            PreserveContract(original, updated);
            foreach (var old in original.Mods)
            {
                var next = updated.Mods.SingleOrDefault(m => Value(m.Guid) == Value(old.Guid));
                if (next != null) PreserveContract(old, next);
                else if (Marked(old)) updated.Mods.Add(Json.Read<ModEntry>(Json.Write(old)));
            }
        }
    }
}
