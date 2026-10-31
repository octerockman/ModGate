using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private CancellationTokenSource? serverSelfUpdateCancellation;
        private Task? serverSelfUpdate;
        private void StartSelfUpdates()
        {
            var dedicated = System.Diagnostics.Process.GetCurrentProcess().ProcessName.StartsWith("valheim_server", StringComparison.OrdinalIgnoreCase) ||
                Environment.GetCommandLineArgs().Contains("-batchmode") && Environment.GetCommandLineArgs().Contains("-world");
            if (!dedicated) { prompt.StartSelfUpdateCheckOnLaunch(); return; }
            var root = Paths.BepInExRootPath;
            try
            {
                var path = SafePath.Under(root, "config/ModGate/server-mods.json");
                if (!File.Exists(path)) return;
                var settings = ConfigFields.Runtime(Json.Read<ServerManifest>(File.ReadAllText(path)));
                if (!settings.AutoUpdateOnRestart || !settings.AutoUpdateModGate) return;
                if (!SelfUpdateInstaller.Supported) { Logger.LogWarning("ModGate self-updating is not supported by this operating-system helper setup. Install updates manually."); return; }
                var locations = SelfUpdateRuntime.Locations();
                serverSelfUpdateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var token = serverSelfUpdateCancellation.Token;
                serverSelfUpdate = Task.Run(async () =>
                {
                    try
                    {
                        var source = new SelfUpdateSource();
                        var release = await source.Find(null, settings.ModGateIncludePrereleases, token).ConfigureAwait(false);
                        if (System.Version.Parse(release.Version) <= System.Version.Parse(Constants.Version)) { Logger.LogInfo("ModGate self-update: this server is current for its release channel."); return; }
                        var plan = await source.Prepare(root, release, locations.plugin, locations.preloader, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        SelfUpdateInstaller.Arm(root, plan, false);
                        Logger.LogInfo("ModGate " + release.Version + " is verified and queued for this server’s next normal shutdown. It will not stop a running world. Wait for self-update-status.txt before relaunching.");
                    }
                    catch (OperationCanceledException) { Logger.LogInfo("ModGate self-update cancelled before installation."); }
                    catch (Exception error) { Logger.LogWarning("ModGate self-update did not schedule a change: " + SystemHttps.Details(error)); }
                }, token);
            }
            catch (Exception error) { Logger.LogWarning("Cannot check ModGate server self-update settings: " + error.Message); }
        }
    }
}
