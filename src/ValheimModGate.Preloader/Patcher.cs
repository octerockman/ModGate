using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace ModGate.Preloader
{
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs => Array.Empty<string>();
        public static void Patch(AssemblyDefinition assembly) { }
        public static void Initialize()
        {
            var log = Logger.CreateLogSource("ModGate Installer");
            if (SelfUpdateInstaller.GuardStartup(Paths.BepInExRootPath, message => log.LogWarning(message)))
            {
                Environment.Exit(0);
                return;
            }
            log.LogInfo("HTTP libraries before ModGate startup: " + NetworkAssemblyInfo.LoadedHttp());
            try
            {
                ServerUpdater.Recover(Paths.BepInExRootPath);
                var profileChange = Profiles.Pending(Paths.BepInExRootPath);
                if (profileChange) log.LogInfo("Preparing profile change and restore point. Large profiles may take a while to copy.");
                Profiles.Apply(Paths.BepInExRootPath, message => log.LogInfo(message));
                ConfigEditor.ApplyPending(Paths.BepInExRootPath, message => log.LogInfo(message));
                ServerConfigFiles.ApplyPending(Paths.BepInExRootPath, message => log.LogInfo(message));
                if (profileChange)
                {
                    try { var catalog = Profiles.Load(Paths.BepInExRootPath); ProfileHealth.Capture(Paths.BepInExRootPath, catalog, catalog.Active, ServerUpdater.Inspect); }
                    catch (Exception failure) { log.LogWarning("Profile changed, but the health receipt could not be saved: " + failure.Message); }
                }
                var error = Path.Combine(Paths.BepInExRootPath, "ModGate", "install-error.txt");
                if (File.Exists(error)) File.Delete(error);
            }
            catch (Exception error)
            {
                log.LogError("Profile change did not finish. Keep the recovery journal and inspect install-error.txt. " + error);
                Directory.CreateDirectory(Path.Combine(Paths.BepInExRootPath, "ModGate"));
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "ModGate", "install-error.txt"), error.ToString());
                return;
            }
            if (ServerUpdater.IsDedicated(System.Diagnostics.Process.GetCurrentProcess().ProcessName, Environment.GetCommandLineArgs()))
            {
                try
                {
                    if (ServerUpdater.Configured(Paths.BepInExRootPath))
                    {
                        log.LogInfo("Server updates use the operating system HTTPS transport (curl); managed HTTP is not initialized by the updater.");
                        using (var source = new ServerPackageSource())
                        using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10)))
                            ServerUpdater.Run(Paths.BepInExRootPath, true, source, message => log.LogInfo(message), timeout.Token).GetAwaiter().GetResult();
                    }
                }
                catch (Exception error) { log.LogError("Server auto-update did not run; installed files were preserved. " + error); }
            }
            log.LogInfo("HTTP libraries after ModGate startup: " + NetworkAssemblyInfo.LoadedHttp());
        }
        // BepInEx calls Finish after its assembly patches are loaded and before Chainloader.Start.
        // Delaying the filter avoids resolving PluginInfo/Harmony types during assembly patching.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Finish()
        {
            if (!ServerUpdater.IsDedicated(System.Diagnostics.Process.GetCurrentProcess().ProcessName, Environment.GetCommandLineArgs())) return;
            var log = Logger.CreateLogSource("ModGate Client-only Filter");
            try { ClientOnlyLoader.Configure(Paths.BepInExRootPath, true, message => log.LogInfo(message)); }
            catch (Exception error) { log.LogError("Unable to install the clientOnly load filter. Those mods may load on the server; inspect server-mods.json and the loader version. " + error); }
        }
    }
}
