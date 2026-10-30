using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ModGate.Preloader
{
    internal static class ClientOnlyLoader
    {
        private static HashSet<string> excluded = new HashSet<string>(StringComparer.Ordinal);
        private static Action<string> log = _ => { };
        private static bool patched;

        public static void Configure(string root, bool dedicated, Action<string> logger)
        {
            excluded.Clear(); log = logger;
            if (!dedicated) return;
            var path = SafePath.Under(root, "config/ModGate/server-mods.json");
            if (!File.Exists(path)) return;
            excluded = ClientOnlyRules.Guids(Json.Read<ServerManifest>(File.ReadAllText(path)));
            if (excluded.Count == 0 || patched) return;
            var target = typeof(TypeLoader).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "FindPluginTypes" && m.IsGenericMethodDefinition && m.GetParameters().Length == 4).MakeGenericMethod(typeof(PluginInfo));
            new Harmony(Constants.Guid + ".client-only").Patch(target, postfix: new HarmonyMethod(typeof(ClientOnlyLoader), nameof(Filter)));
            patched = true;
        }

        // This runs after cache lookup/save, before Chainloader creates plugin instances. New lists
        // keep BepInEx's cached records intact so changing side works on the next startup too.
        private static void Filter(string __0, object __result)
        {
            if (excluded.Count == 0 || !Path.GetFullPath(__0).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !(__result is Dictionary<string, List<PluginInfo>> plugins)) return;
            ClientOnlyRules.Filter(plugins, excluded, p => p.Metadata.GUID, plugin => log("Client-only: not loading " + plugin.Metadata.Name + " [" + plugin.Metadata.GUID + "] on this dedicated server; files remain available for discovery and updates."));
        }
    }
}
