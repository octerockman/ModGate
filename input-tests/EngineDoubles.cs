using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class HarmonyPriority : Attribute { public HarmonyPriority(int value) { } }
    internal static class Priority { public const int First = 800; }
    internal static class AccessTools
    {
        internal delegate ref TField FieldRef<in T, TField>(T instance);
        internal static FieldRef<T, TField> FieldRefAccess<T, TField>(string name)
        {
            var field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(T).FullName, name);
            if (field.FieldType != typeof(TField)) throw new InvalidOperationException("Unexpected field-ref fixture type.");
            var getter = new DynamicMethod("FixtureFieldRef", typeof(TField).MakeByRefType(), new[] { typeof(T) }, typeof(AccessTools).Module, true);
            var body = getter.GetILGenerator(); body.Emit(OpCodes.Ldarg_0); body.Emit(OpCodes.Ldflda, field); body.Emit(OpCodes.Ret);
            return (FieldRef<T, TField>)getter.CreateDelegate(typeof(FieldRef<T, TField>));
        }
    }
}

namespace UnityEngine
{
    internal readonly record struct Vector3(float x, float y, float z) { public static Vector3 zero => new(0, 0, 0); }
    internal static class Time { public static int frameCount; }
    internal static class Application { public static bool isBatchMode = false; }
}

internal sealed class ControlState
{
    public UnityEngine.Vector3 Move;
    public bool[] Buttons;
    public ControlState(UnityEngine.Vector3 move, params bool[] buttons) { Move = move; Buttons = buttons; }
    public bool Neutral => Move == UnityEngine.Vector3.zero && Buttons.Length == 11 && Buttons.All(value => !value);
    public static ControlState Moving() => new(new UnityEngine.Vector3(1, 0, 1), Enumerable.Repeat(true, 11).ToArray());
}

internal class Character
{
    private bool m_blocking;
    public bool Blocking => m_blocking;
    public void SetBlocking(bool value) => m_blocking = value;
}
internal sealed class Player : Character
{
    public static Player? m_localPlayer = null;
    public bool m_autoRun = false;
    public int ControlsCalls = 0;
    public ControlState LastControls = new(UnityEngine.Vector3.zero, new bool[11]);
    public UnityEngine.Vector3 PhysicsVelocity = new(2, 0, 3);
    public void SetControls(UnityEngine.Vector3 movedir, bool attack, bool attackHold, bool secondaryAttack,
        bool secondaryAttackHold, bool block, bool blockHold, bool jump, bool crouch, bool run, bool autoRun, bool dodge)
    {
        ControlsCalls++;
        LastControls = new ControlState(movedir, attack, attackHold, secondaryAttack, secondaryAttackHold, block, blockHold, jump, crouch, run, autoRun, dodge);
        // The fixture retains sticky autorun/block state when their control flags are false.
        // The production guard must clear these fields explicitly, as well as neutralizing controls.
    }
}
internal sealed class PlayerController { }
internal sealed class GameCamera { }
internal sealed class ZInput { }
internal sealed class ZNet
{
    public static ZNet? instance;
    public bool Server = true, StatsThrows;
    public int Players = 2, StatsCalls;
    public float InRate = 123, OutRate = 456;
    public readonly List<ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    public int GetNrOfPlayers() => Players;
    public List<ZNetPeer> GetConnectedPeers() => Peers;
    public void GetNetStats(out float localQuality, out float remoteQuality, out int ping, out float outgoing, out float incoming)
    {
        StatsCalls++; localQuality = remoteQuality = 0; ping = 0; outgoing = OutRate; incoming = InRate;
        if (StatsThrows) throw new NotSupportedException();
    }
}
internal sealed class ZNetPeer
{
    public bool Ready = true;
    public ZRpc m_rpc = new();
    public bool IsReady() => Ready;
}
internal sealed class ZRpc
{
    public bool Connected = true;
    public TestSocket Socket = new();
    public bool IsConnected() => Connected;
    public TestSocket GetSocket() => Socket;
}
internal sealed class TestSocket { public int Queued = 10; public int GetSendQueueSize() => Queued; }

namespace BepInEx
{
    internal static class Paths { public static string BepInExRootPath = ""; }
    internal sealed class Metadata { public string GUID = ""; }
    internal sealed class PluginInfo { public Metadata Metadata = new(); public object? Instance = null; }
    internal class BaseUnityPlugin
    {
        public PluginInfo Info;
        public Configuration.ConfigFile Config;
        public BaseUnityPlugin(string guid, Configuration.ConfigFile config)
        { Config = config; Info = new PluginInfo { Instance = this, Metadata = new Metadata { GUID = guid } }; }
    }
}
namespace BepInEx.Bootstrap
{
    internal static class Chainloader { public static readonly Dictionary<string, BepInEx.PluginInfo> PluginInfos = new(); }
}
namespace BepInEx.Configuration
{
    internal sealed class ConfigFile
    {
        public string ConfigFilePath { get; }
        public int ReloadCalls = 0, SaveCalls = 0;
        public Action? OnReload = null;
        public ConfigFile(string path) => ConfigFilePath = path;
        public void Reload() { ReloadCalls++; OnReload?.Invoke(); }
        public void Save() => SaveCalls++;
    }
}

namespace ModGate
{
    internal sealed class Prompt
    {
        public bool IsVisible = false, ReadingPromptInput = false;
        public int Forgotten;
        public void ForgetServerConfigAccess() => Forgotten++;
    }
    internal sealed class FixtureLogger
    {
        public readonly List<string> Warnings = new();
        public void LogWarning(object message) => Warnings.Add(message.ToString() ?? "");
    }
    public sealed partial class Plugin
    {
        internal static Plugin Instance = null!;
        private readonly Prompt? prompt;
        internal FixtureLogger Logger { get; } = new();
        internal Plugin(Prompt? window) { prompt = window; Instance = this; }
        internal static bool ControllerPrefix(ref bool result) => KeepControllerInput(ref result);
        internal static bool CameraPrefix() => KeepCameraStill();
        internal static bool ButtonPrefix(int edge, ref bool result) => edge == 0 ? KeepGameButton(ref result) : edge == 1 ? KeepGameButtonDown(ref result) : KeepGameButtonUp(ref result);
        internal static bool KeyPrefix(int edge, ref bool result) => edge == 0 ? KeepGameKey(ref result) : edge == 1 ? KeepGameKeyDown(ref result) : KeepGameKeyUp(ref result);
        internal static void ApplyControls(Player player, ControlState state)
        {
            KeepLocalControlsNeutral(player, ref state.Move, ref state.Buttons[0], ref state.Buttons[1], ref state.Buttons[2], ref state.Buttons[3],
                ref state.Buttons[4], ref state.Buttons[5], ref state.Buttons[6], ref state.Buttons[7], ref state.Buttons[8], ref state.Buttons[9], ref state.Buttons[10]);
            player.SetControls(state.Move, state.Buttons[0], state.Buttons[1], state.Buttons[2], state.Buttons[3], state.Buttons[4], state.Buttons[5],
                state.Buttons[6], state.Buttons[7], state.Buttons[8], state.Buttons[9], state.Buttons[10]);
        }
        internal string ReloadProfile(string slot) => ReloadProfileConfigs(slot);
        internal string ReloadFiles(params string[] files) => ReloadLoadedModConfigs(files);
        internal void ObserveHealth() => ObserveServerHealth();
        internal ServerHealthSnapshot ReadHealth() => GetServerHealthSnapshot();
    }
}
