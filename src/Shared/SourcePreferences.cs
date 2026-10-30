using System;
using System.IO;
using System.Linq;
namespace ModGate
{
    internal static class SourcePreferences
    {
        public static string[] Parse(string value)
        {
            var defaults = new[] { "hexium", "thunderstore" };
            return value.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => defaults.Contains(s)).Concat(defaults).Distinct().ToArray();
        }
        public static string[] Load(string root)
        {
            var file = Path.Combine(root, "config", "ModGate", "download-sources.txt");
            if (!File.Exists(file)) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "hexium,thunderstore"); }
            var text = File.ReadAllText(file);
            var order = Parse(text);
            var normalized = string.Join(",", order);
            if (text != normalized) File.WriteAllText(file, normalized);
            return order;
        }
        public static void First(string root, string source)
        {
            var order = new[] { source }.Concat(Load(root)).Distinct();
            File.WriteAllText(Path.Combine(root, "config", "ModGate", "download-sources.txt"), string.Join(",", order));
        }
    }
}
