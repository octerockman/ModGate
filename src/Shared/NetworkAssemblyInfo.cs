using System;
using System.Collections.Generic;

namespace ModGate
{
    internal static class NetworkAssemblyInfo
    {
        // Inspect only assemblies already present; diagnostics must never select an HTTP library.
        internal static string LoadedHttp()
        {
            var matches = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetName().Name != "System.Net.Http") continue;
                    string location;
                    try { location = assembly.Location; } catch { location = "location unavailable"; }
                    matches.Add(assembly.FullName + "; path=" + location + "; module=" + assembly.ManifestModule.ModuleVersionId.ToString("N"));
                }
                catch { matches.Add("HTTP assembly metadata unavailable"); }
            }
            return matches.Count == 0 ? "System.Net.Http is not loaded" : string.Join(" | ", matches);
        }
    }
}
