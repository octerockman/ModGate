using System;
using System.Collections.Generic;
using System.Linq;

namespace ModGate
{
    internal static class ClientOnlyRules
    {
        public static HashSet<string> Guids(ServerManifest manifest) => new HashSet<string>(
            ConfigFields.Runtime(manifest).Mods.Where(m => m.Side == "clientOnly" && m.Guid != Constants.Guid).Select(m => m.Guid), StringComparer.Ordinal);
        public static void Filter<T>(Dictionary<string, List<T>> records, ISet<string> excluded, Func<T, string> guid, Action<T> removed)
        {
            foreach (var file in records.Keys.ToArray())
            {
                var original = records[file];
                if (!original.Any(p => guid(p) != Constants.Guid && excluded.Contains(guid(p)))) continue;
                records[file] = original.Where(p => guid(p) == Constants.Guid || !excluded.Contains(guid(p))).ToList();
                foreach (var plugin in original.Where(p => guid(p) != Constants.Guid && excluded.Contains(guid(p)))) removed(plugin);
            }
        }
    }
}
