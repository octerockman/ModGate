using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ModGate;

internal static partial class Program
{
    private static void RestartChecks()
    {
        Test("restart clears inherited Doorstop runtime guards but keeps manager settings", () =>
        {
            var start = new ProcessStartInfo { UseShellExecute = false };
            start.EnvironmentVariables["DOORSTOP_INITIALIZED"] = "TRUE"; start.EnvironmentVariables["DOORSTOP_DISABLE"] = "TRUE";
            start.EnvironmentVariables["DOORSTOP_TARGET_ASSEMBLY"] = "active-profile-preloader"; start.EnvironmentVariables["MODGATE_FIXTURE_MANAGER"] = "profile";
            RestartLauncher.CleanRuntimeFlags(start);
            Check(!start.EnvironmentVariables.ContainsKey("DOORSTOP_INITIALIZED") && !start.EnvironmentVariables.ContainsKey("DOORSTOP_DISABLE"));
            Check(start.EnvironmentVariables["DOORSTOP_TARGET_ASSEMBLY"] == "active-profile-preloader" && start.EnvironmentVariables["MODGATE_FIXTURE_MANAGER"] == "profile");
        });
        Test("modern restart explicitly enables the active BepInEx target and retains game options", () =>
        {
            var args = RestartLauncher.ModdedArguments(new[] { "-force-vulkan", "--doorstop-enabled", "false", "--doorstop-target-assembly", "old.dll", "-screen-width", "1920" }, "C:\\Mod Manager\\BepInEx\\core\\BepInEx.Preloader.dll", true);
            Check(args.Contains("\"--doorstop-enabled\" \"true\"") && args.Contains("\"--doorstop-target-assembly\" \"C:\\Mod Manager\\BepInEx\\core\\BepInEx.Preloader.dll\""));
            Check(args.Contains("\"-force-vulkan\"") && args.Contains("\"-screen-width\" \"1920\"") && !args.Contains("old.dll") && !args.Contains("false"));
        });
        Test("legacy restart uses Doorstop three arguments and replaces legacy aliases", () =>
        {
            var args = RestartLauncher.ModdedArguments(new[] { "--doorstop_enabled", "false", "--doorstop_target_assembly", "old.dll", "-console" }, "BepInEx.Preloader.dll", false);
            Check(args.Contains("\"--doorstop-enable\" \"true\"") && args.Contains("\"--doorstop-target\" \"BepInEx.Preloader.dll\"") && args.Contains("\"-console\"") && !args.Contains("old.dll"));
        });
        Test("restart plan keeps the running manager profile bootstrap and requires confirmation", () =>
        {
            var root = Root(); var game = Put(root, "game/valheim.exe", "fixture"); Put(root, "game/.doorstop_version", "4.4.0");
            var profile = SafePath.Under(root, "manager-profile/BepInEx"); var bootstrap = Put(profile, "core/BepInEx.Preloader.dll", "active-loader");
            var plan = RestartLauncher.CurrentPlan(profile, game, 42, 123, new[] { "-force-vulkan" }, bootstrap);
            Check(plan.Executable == game && plan.WorkingDirectory == Path.GetDirectoryName(game) && plan.BootstrapAssembly == bootstrap);
            Check(plan.Arguments.Contains(RestartLauncher.QuoteArgument(bootstrap)) && plan.ExpectedVersion == Constants.Version && Guid.TryParseExact(plan.ConfirmationId, "N", out _));
        });
        Test("restart stops before quitting when its active preloader cannot be located", () =>
        {
            var root = Root(); var game = Put(root, "game/valheim.exe", "fixture");
            Throws<IOException>(() => RestartLauncher.CurrentPlan(root, game, 42, 123, Array.Empty<string>(), null));
        });
        Test("restart plan supports a relative active bootstrap without changing its installation", () =>
        {
            var root = Root(); var game = Put(root, "game/valheim.exe", "fixture"); Put(root, "game/.doorstop_version", "3.4.0");
            var bootstrap = Put(root, "game/BepInEx/core/BepInEx.Preloader.dll", "loader");
            var plan = RestartLauncher.CurrentPlan(Path.GetDirectoryName(Path.GetDirectoryName(bootstrap)!)!, game, 42, 123, Array.Empty<string>(), "BepInEx/core/BepInEx.Preloader.dll");
            Check(plan.BootstrapAssembly == bootstrap && plan.Arguments.Contains("\"--doorstop-target\""));
        });
        Test("loaded ModGate acknowledges its version and the completed profile change", () => WithRestartEnvironment((root, token) =>
        {
            RestartLauncher.ConfirmCurrent(root);
            var confirmation = Json.ReadLocal<RestartConfirmation>(SafePath.Under(root, "ModGate/restart-confirmed-" + token + ".json"));
            Check(confirmation.Ready && confirmation.Version == Constants.Version && confirmation.Error == "");
            Check(Environment.GetEnvironmentVariable("MODGATE_RESTART_TOKEN") == null);
        }));
        Test("restart confirmation reports an unfinished profile without removing its request", () => WithRestartEnvironment((root, token) =>
        {
            Put(root, "ModGate/profile-request.json", "pending"); RestartLauncher.ConfirmCurrent(root);
            var confirmation = Json.ReadLocal<RestartConfirmation>(SafePath.Under(root, "ModGate/restart-confirmed-" + token + ".json"));
            Check(!confirmation.Ready && confirmation.Error.Contains("profile change did not finish") && Profiles.Pending(root));
        }));
        Test("restart confirmation cannot be written by a different BepInEx installation", () => WithRestartEnvironment((root, token) =>
        {
            RestartLauncher.ConfirmCurrent(Root()); Check(!File.Exists(SafePath.Under(root, "ModGate/restart-confirmed-" + token + ".json")));
        }));
    }
    private static void WithRestartEnvironment(Action<string, string> check)
    {
        var oldToken = Environment.GetEnvironmentVariable("MODGATE_RESTART_TOKEN"); var oldRoot = Environment.GetEnvironmentVariable("MODGATE_RESTART_ROOT");
        var root = Root(); var token = Guid.NewGuid().ToString("N");
        try { Environment.SetEnvironmentVariable("MODGATE_RESTART_TOKEN", token); Environment.SetEnvironmentVariable("MODGATE_RESTART_ROOT", root); check(root, token); }
        finally { Environment.SetEnvironmentVariable("MODGATE_RESTART_TOKEN", oldToken); Environment.SetEnvironmentVariable("MODGATE_RESTART_ROOT", oldRoot); }
    }
}
