using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using ModGate;
using UnityEngine;

internal static partial class Program
{
    private static int passed, failed, skipped;
    private static int Main()
    {
        InputChecks(); ReloadChecks(); HealthAccessChecks();
        Console.WriteLine($"{passed} production input/reload checks passed, {failed} failed, {skipped} optional checks skipped.");
        return failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action)
    {
        Plugin.Instance = null!; Player.m_localPlayer = null; Time.frameCount = 0; Application.isBatchMode = false; Chainloader.PluginInfos.Clear(); Paths.BepInExRootPath = "";
        ZNet.instance = null;
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (OptionalFixtureUnavailable error) { skipped++; Console.WriteLine("SKIP " + name + ": " + error.Message); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        finally { Plugin.Instance = null!; Player.m_localPlayer = null; Chainloader.PluginInfos.Clear(); Paths.BepInExRootPath = ""; Application.isBatchMode = false; }
    }
    private static void Check(bool value, string message = "Unexpected production behavior")
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class OptionalFixtureUnavailable(string message) : Exception(message) { }
    private static (Plugin Plugin, Prompt Prompt, Player Player) Active(bool visible = true)
    {
        var window = new Prompt { IsVisible = visible };
        var player = new Player { m_autoRun = true }; player.SetBlocking(true); Player.m_localPlayer = player;
        return (new Plugin(window), window, player);
    }
    private static void InputChecks()
    {
        Test("visible ModGate rejects controller input and clears movement autorun and toggled block", () => {
            var host = Active(); var result = true;
            Check(!Plugin.ControllerPrefix(ref result) && !result);
            Check(host.Player.ControlsCalls == 1 && host.Player.LastControls.Neutral);
            Check(!host.Player.m_autoRun && !host.Player.Blocking);
        });
        Test("local SetControls prefix neutralizes movement and all eleven action flags", () => {
            var host = Active(); var controls = ControlState.Moving();
            Plugin.ApplyControls(host.Player, controls);
            Check(controls.Neutral && host.Player.LastControls.Neutral && host.Player.ControlsCalls == 1);
            Check(!host.Player.m_autoRun && !host.Player.Blocking);
        });
        Test("local input blocking preserves physics velocity", () => {
            var host = Active(); var velocity = host.Player.PhysicsVelocity;
            var result = true; Plugin.ControllerPrefix(ref result);
            Check(host.Player.PhysicsVelocity == velocity);
        });
        Test("remote player controls and sticky actions remain untouched", () => {
            Active(); var remote = new Player { m_autoRun = true }; remote.SetBlocking(true); var controls = ControlState.Moving();
            Plugin.ApplyControls(remote, controls);
            Check(controls.Move == new Vector3(1, 0, 1) && controls.Buttons.All(value => value));
            Check(remote.m_autoRun && remote.Blocking && remote.ControlsCalls == 1);
        });
        Test("closed ModGate leaves local controller and controls unchanged", () => {
            var host = Active(false); var result = true; var controls = ControlState.Moving();
            Check(Plugin.ControllerPrefix(ref result) && result && host.Player.ControlsCalls == 0);
            Plugin.ApplyControls(host.Player, controls);
            Check(controls.Buttons.All(value => value) && controls.Move != Vector3.zero && host.Player.m_autoRun && host.Player.Blocking);
        });
        Test("missing plugin and not-yet-created prompt allow normal game input", () => {
            var result = true; Check(Plugin.ControllerPrefix(ref result) && result && Plugin.CameraPrefix());
            _ = new Plugin(null); result = true; Check(Plugin.ControllerPrefix(ref result) && result && Plugin.CameraPrefix());
            for (var edge = 0; edge < 3; edge++) { result = true; Check(Plugin.ButtonPrefix(edge, ref result) && result); result = true; Check(Plugin.KeyPrefix(edge, ref result) && result); }
        });
        Test("visible ModGate without a local player safely suppresses controller input", () => {
            _ = new Plugin(new Prompt { IsVisible = true }); var result = true;
            Check(!Plugin.ControllerPrefix(ref result) && !result);
        });
        Test("dedicated-server batch mode leaves game input untouched even with a visible prompt", () => {
            var host = Active(); Application.isBatchMode = true; var result = true;
            Check(Plugin.ControllerPrefix(ref result) && result && host.Player.ControlsCalls == 0 && host.Player.m_autoRun && host.Player.Blocking);
            Check(Plugin.CameraPrefix());
        });
        Test("visible ModGate suppresses button held down and up results", () => {
            Active(); for (var edge = 0; edge < 3; edge++) { var result = true; Check(!Plugin.ButtonPrefix(edge, ref result) && !result); }
        });
        Test("visible ModGate suppresses key held down and up results", () => {
            Active(); for (var edge = 0; edge < 3; edge++) { var result = true; Check(!Plugin.KeyPrefix(edge, ref result) && !result); }
        });
        Test("prompt-owned key reads remain available while gameplay buttons and controls stay blocked", () => {
            var host = Active(); host.Prompt.ReadingPromptInput = true;
            for (var edge = 0; edge < 3; edge++) { var result = true; Check(Plugin.KeyPrefix(edge, ref result) && result); result = true; Check(!Plugin.ButtonPrefix(edge, ref result) && !result); }
            var controller = true; Check(!Plugin.ControllerPrefix(ref controller) && !controller && !Plugin.CameraPrefix());
            host.Prompt.ReadingPromptInput = false; var key = true; Check(!Plugin.KeyPrefix(1, ref key) && !key);
        });
        Test("closing ModGate blocks the close frame and following frame then restores every input path", () => {
            var host = Active(); Time.frameCount = 50; Check(!Plugin.CameraPrefix()); host.Prompt.IsVisible = false;
            foreach (var frame in new[] { 50, 51 }) { Time.frameCount = frame; var result = true; Check(!Plugin.ControllerPrefix(ref result) && !result && !Plugin.CameraPrefix()); }
            Time.frameCount = 52; var controller = true; Check(Plugin.ControllerPrefix(ref controller) && controller && Plugin.CameraPrefix());
            for (var edge = 0; edge < 3; edge++) { var result = true; Check(Plugin.ButtonPrefix(edge, ref result) && result); result = true; Check(Plugin.KeyPrefix(edge, ref result) && result); }
            var moving = ControlState.Moving(); Plugin.ApplyControls(host.Player, moving); Check(moving.Move != Vector3.zero && moving.Buttons.All(value => value));
        });
        Test("a visible window extends its latch each frame and a later closed frame releases it", () => {
            var host = Active(); Time.frameCount = 20; Check(!Plugin.CameraPrefix()); Time.frameCount = 200; Check(!Plugin.CameraPrefix());
            host.Prompt.IsVisible = false; Time.frameCount = 201; Check(!Plugin.CameraPrefix()); Time.frameCount = 202; Check(Plugin.CameraPrefix());
        });
    }
}
