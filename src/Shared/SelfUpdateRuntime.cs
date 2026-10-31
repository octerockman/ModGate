using System;
using System.IO;
using System.Linq;

namespace ModGate
{
    internal static class SelfUpdateRuntime
    {
        internal static (string plugin, string preloader) Locations()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var plugin = assemblies.FirstOrDefault(a => a.GetName().Name == "ValheimModGate")?.Location;
            var preloader = assemblies.FirstOrDefault(a => a.GetName().Name == "ValheimModGate.Preloader")?.Location;
            if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(preloader)) throw new IOException("Both ModGate components must be loaded from the active installation before self-updating.");
            return (plugin!, preloader!);
        }
    }
}
