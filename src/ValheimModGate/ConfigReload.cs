using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private string ReloadProfileConfigs(string slot)
        {
            var root = Paths.BepInExRootPath;
            if (Profiles.Load(root).Active != slot) return "Saved profile configs refreshed. Activate this profile before reloading its running mods.";
            return ReloadLoadedModConfigs(ConfigEditor.Files(root, slot));
        }
        private string ReloadLoadedModConfigs(IEnumerable<string> files)
        {
            var allowed = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var configRoot = SafePath.Under(Paths.BepInExRootPath, "config");
            var prefix = configRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reloaded = 0; var failed = 0;
            foreach (var plugin in Chainloader.PluginInfos.Values.Select(p => p.Instance as BaseUnityPlugin).Where(p => p != null).ToArray())
            {
                if (plugin!.Info.Metadata.GUID == Constants.Guid) continue;
                try
                {
                    var full = Path.GetFullPath(plugin.Config.ConfigFilePath);
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = full.Substring(prefix.Length).Replace('\\', '/');
                    if (!allowed.Contains(relative) || !seen.Add(relative)) continue;
                    var safe = SafePath.Under(configRoot, relative);
                    if (!File.Exists(safe)) continue;
                    // Read disk directly: saving current runtime values first would overwrite
                    // changes made through host tools. Restart queues remain separate.
                    plugin.Config.Reload(); reloaded++;
                }
                catch
                {
                    failed++;
                    Logger.LogWarning("Could not reload the config for " + plugin!.Info.Metadata.GUID + ". Queued edits are retained; check that mod's config and log.");
                }
            }
            return "Reloaded " + reloaded + " running mod config" + (reloaded == 1 ? "" : "s") + (failed > 0 ? "; " + failed + " failed" : "") +
                ". Mods apply live changes only if they support reload. Queued edits and server settings still require a restart.";
        }
    }
}
