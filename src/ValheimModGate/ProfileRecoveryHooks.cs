using System;
using System.Threading.Tasks;
using BepInEx;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private bool workingProfileCaptured;
        private void RecordWorkingProfile()
        {
            if (workingProfileCaptured || ApplicationIsDedicated() || Player.m_localPlayer == null || ZNet.instance == null) return;
            workingProfileCaptured = true;
            try
            {
                var root = Paths.BepInExRootPath;
                if (Profiles.Pending(root)) return;
                var inventory = Inventory.Loaded(); var catalog = Profiles.Load(root);
                if (catalog.Active == "offline" && catalog.OfflineMods.Count == 0) { ProfileConfigs.RecordActive(root, inventory); catalog = Profiles.Load(root); }
                var slot = catalog.Active;
                Task.Run(() => {
                    try
                    {
                        ProfileHealth.Capture(root, catalog, slot, Inventory.InspectProfilePayload);
                        ProfileSnapshots.Capture(root, slot, "Last successful world entry", true);
                        Logger.LogInfo("Recorded a working profile restore point for " + slot + ".");
                    }
                    catch (Exception failure) { Logger.LogWarning("Could not record working profile: " + failure.Message); }
                });
            }
            catch (Exception failure) { Logger.LogWarning("Working profile capture: " + failure.Message); }
        }
        private static bool ApplicationIsDedicated() => UnityEngine.Application.isBatchMode;
    }
}
