using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ModGate
{
    [BepInPlugin(Constants.Guid, "Valheim ModGate", Constants.Version)]
    [HarmonyPatch]
    [DefaultExecutionOrder(10000)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        private const string PolicyRpc = "ModGate.Policy.v1", ReportRpc = "ModGate.Inventory.v1", DecisionRpc = "ModGate.Decision.v1";
        private readonly Dictionary<ZRpc, Session> sessions = new Dictionary<ZRpc, Session>();
        private Harmony harmony = null!;
        private Prompt prompt = null!;
        private bool returnToMenuRequested, quitRequested, restartQueued;

        private sealed class Session
        {
            public ZNet Network = null!;
            public Policy Policy = null!;
            public float Deadline;
            public bool Accepted, ReportReceived;
            public string? DeferredSecret;
        }

        private void Awake()
        {
            Instance = this;
            prompt = new Prompt(message => Logger.LogInfo(message), () => returnToMenuRequested = true, () => quitRequested = true, RequestRestart);
            prompt.ServerConfigAvailable = ServerConfigAvailable;
            prompt.ServerConfigLocalOwner = () => ZNet.instance != null && ZNet.instance.IsServer() && Player.m_localPlayer != null;
            prompt.ServerConfigRequest = RequestServerConfig;
            prompt.ReloadConfigs = ReloadProfileConfigs;
            harmony = new Harmony(Constants.Guid);
            harmony.PatchAll(typeof(Plugin));
            var installError = Path.Combine(Paths.BepInExRootPath, "ModGate", "install-error.txt");
            if (File.Exists(installError)) prompt.ShowError("The last profile change could not finish. See BepInEx/ModGate/install-error.txt and keep the recovery journal while diagnosing it.");
            else if (Profiles.Pending(Paths.BepInExRootPath)) prompt.ShowError("A profile change is still pending. Ensure ValheimModGate.Preloader.dll is in BepInEx/patchers, then restart Valheim.");
            Logger.LogInfo("ModGate " + Constants.Version + " ready. Server policy: " + Path.Combine(Paths.ConfigPath, "ModGate", "server-mods.json"));
            try { RestartLauncher.ConfirmCurrent(Paths.BepInExRootPath); }
            catch (Exception failure) { Logger.LogWarning("Restart confirmation failed: " + failure.Message); }
            StartSelfUpdates();
        }

        private void RequestRestart()
        {
            if (restartQueued) return;
            if (Application.isBatchMode) throw new InvalidOperationException("Restart the dedicated server manually.");
            RestartLauncher.BeginCurrent(Paths.BepInExRootPath);
            restartQueued = true; quitRequested = true;
        }

        private void Update()
        {
            prompt.Update();
            ObserveServerHealth();
            if (GameplayInputBlocked) ClearLocalGameplayInput();
            UpdateAdminRequests();
            RecordWorkingProfile();
            foreach (var rpc in passwordContexts.Keys.Where(r => !r.IsConnected()).ToArray()) passwordContexts.Remove(rpc);
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                try { if (Inventory.PollLookups()) { Inventory.ServerPolicy(); Logger.LogInfo("Repository lookup finished. See config/ModGate/lookup-report.json."); } }
                catch (Exception error) { Logger.LogWarning("Repository lookup: " + error.Message); }
            }
            if (quitRequested) { quitRequested = false; Application.Quit(); }
            if (returnToMenuRequested)
            {
                returnToMenuRequested = false;
                // Use the game's normal shutdown path after leaving the RPC/GUI callback.
                if (ZNet.instance != null && !ZNet.instance.IsServer() && Player.m_localPlayer == null && Game.instance != null && !Game.instance.IsShuttingDown()) Game.instance.Logout();
                else if (ZNet.instance == null && FejdStartup.instance != null) FejdStartup.instance.OnConnectionFailedOk();
            }
            foreach (var pair in sessions.ToArray())
            {
                if (!pair.Key.IsConnected()) { sessions.Remove(pair.Key); continue; }
                if (!pair.Value.Accepted && Time.realtimeSinceStartup >= pair.Value.Deadline)
                {
                    pair.Key.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                    pair.Key.GetSocket().Close();
                    sessions.Remove(pair.Key);
                }
            }
        }
        private void OnGUI() { DrawPasswordMemory(); prompt.Draw(); }
        private void LateUpdate() => prompt.EnsureCursor();
        private void OnApplicationFocus(bool focused) { if (focused) prompt?.EnsureCursor(); }
        private void OnDestroy() { serverSelfUpdateCancellation?.Cancel(); serverSelfUpdateCancellation?.Dispose(); prompt?.Dispose(); harmony?.UnpatchSelf(); }

        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepPromptMouseAvailable()
        {
            if (Instance?.prompt?.IsVisible != true) return true;
            Instance.prompt.EnsureCursor(); return false;
        }

        [HarmonyPatch(typeof(FejdStartup), "UpdateCursor"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepMenuPromptMouseAvailable()
        {
            if (Instance?.prompt?.IsVisible != true) return true;
            Instance.prompt.EnsureCursor(); return false;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void BeforeConnection(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                Policy policy;
                try { policy = Inventory.ServerPolicy(); }
                catch (Exception error) { policy = new Policy { Error = "Unable to read the server mod policy. Ask the server owner to check its log." }; Instance.Logger.LogError(error); }
                policy.Nonce = System.Guid.NewGuid().ToString("N");
                Instance.sessions[peer.m_rpc] = new Session { Network = __instance, Policy = policy, Deadline = Time.realtimeSinceStartup + 20 };
                peer.m_rpc.Register<string>(ReportRpc, Instance.ReceiveInventory);
                peer.m_rpc.Register<string>(AdminRequestRpc, Instance.ReceiveAdminRequest);
                Instance.adminGuards[peer.m_rpc] = new AdminConfigRequestGuard();
            }
            else
            {
                peer.m_rpc.Register<string>(AdminResponseRpc, Instance.ReceiveAdminResponse);
                peer.m_rpc.Register<string>(PolicyRpc, Instance.ReceivePolicy);
                peer.m_rpc.Register<string>(DecisionRpc, (rpc, decision) =>
                {
                    if (decision != "accepted") Instance.Logger.LogInfo("The server rejected this mod inventory.");
                });
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void AfterConnection(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer() && Instance.sessions.TryGetValue(peer.m_rpc, out var session))
            {
                var json = Json.Write(session.Policy);
                if (json.Length > Constants.MaxJson) { peer.m_rpc.GetSocket().Close(); return; }
                peer.m_rpc.Invoke(PolicyRpc, json);
                if (session.Policy.Error.Length > 0) Instance.Logger.LogWarning(session.Policy.Error);
            }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_ServerHandshake"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool GateHandshake(ZNet __instance, ZRpc rpc, string secretKey)
        {
            if (!__instance.IsServer()) return true;
            if (!Instance.sessions.TryGetValue(rpc, out var session)) return false;
            if (session.Accepted) return true;
            session.DeferredSecret = secretKey;
            return false;
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool GatePeerInfo(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return true;
            return Instance.sessions.TryGetValue(rpc, out var session) && session.Accepted;
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect)), HarmonyPrefix]
        private static void RemoveSession(ZNetPeer peer) { Instance.sessions.Remove(peer.m_rpc); Instance.passwordContexts.Remove(peer.m_rpc); Instance.ForgetAdminConnection(peer.m_rpc); }

        [HarmonyPatch(typeof(ZNet), "Awake"), HarmonyPostfix]
        private static void DiscoverServerMods(ZNet __instance)
        {
            if (!__instance.IsServer()) return;
            Instance.Logger.LogInfo("HTTP libraries after Valheim server startup: " + NetworkAssemblyInfo.LoadedHttp());
            try
            {
                AdminConfigSecurity.TryEnsureOwnerKey(Paths.BepInExRootPath, message => Instance.Logger.LogError(message));
                var policy = Inventory.ServerPolicy();
                if (policy.Error.Length > 0) Instance.Logger.LogWarning(policy.Error);
            }
            catch (Exception error) { Instance.Logger.LogError(error); }
        }

        private void ReceivePolicy(ZRpc rpc, string json)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                var policy = Json.Read<Policy>(json);
                if (policy.Error == null || policy.Nonce == null || !System.Guid.TryParseExact(policy.Nonce, "N", out _)) throw new InvalidDataException("Invalid server response.");
                if (policy.Error.Length > 0) { prompt.ShowError(policy.Error); rpc.GetSocket().Close(); returnToMenuRequested = true; return; }
                var connectorVersion = SelfUpdateSource.RequiredByServer(policy);
                if (connectorVersion != null)
                {
                    rpc.GetSocket().Close();
                    prompt.ShowConnectorUpdate((policy.ServerName ?? "Valheim server").Substring(0, Math.Min(200, (policy.ServerName ?? "Valheim server").Length)), connectorVersion);
                    returnToMenuRequested = true;
                    return;
                }
                PolicyEngine.ValidatePolicy(policy);
                if (!System.Guid.TryParseExact(policy.ServerId, "N", out _)) throw new InvalidDataException("The server needs a valid ModGate server identity.");
                if (!ReferenceEquals(adminServer, rpc) || adminNonce != policy.Nonce) prompt.ForgetServerConfigAccess();
                adminServer = rpc; adminNonce = policy.Nonce;
                passwordContexts[rpc] = new PasswordContext { Key = Profiles.ServerKey(policy.ServerId, rpc.GetSocket().GetHostName()), ServerId = policy.ServerId };
                var local = Inventory.Loaded();
                var comparison = PolicyEngine.Compare(policy, local);
                rpc.Invoke(ReportRpc, Json.Write(new InventoryReport { Nonce = policy.Nonce, Mods = local }));
                if (!comparison.Matches)
                {
                    if (!System.Guid.TryParseExact(policy.ServerId, "N", out _)) throw new InvalidDataException("The server needs a valid ModGate server-id.txt.");
                    prompt.Show(policy, comparison, local, Profiles.ServerKey(policy.ServerId, rpc.GetSocket().GetHostName()));
                    returnToMenuRequested = true;
                }
            }
            catch (Exception error) { Logger.LogWarning(error); prompt.ShowError("Cannot check this server's mods: " + error.Message); rpc.GetSocket().Close(); returnToMenuRequested = true; }
        }

        private void ReceiveInventory(ZRpc rpc, string json)
        {
            if (!sessions.TryGetValue(rpc, out var session) || session.ReportReceived) return;
            session.ReportReceived = true;
            try
            {
                var report = Json.Read<InventoryReport>(json);
                if (report.Protocol != Constants.Protocol || report.Nonce != session.Policy.Nonce || session.Policy.Error.Length > 0) throw new InvalidDataException("Invalid handshake.");
                var result = PolicyEngine.Compare(session.Policy, report.Mods);
                session.Accepted = result.Matches;
                rpc.Invoke(DecisionRpc, result.Matches ? "accepted" : "mismatch");
                if (result.Matches)
                {
                    Logger.LogInfo("Client mod list matched the server's client requirements.");
                    if (session.DeferredSecret != null)
                    {
                        AccessTools.Method(typeof(ZNet), "RPC_ServerHandshake").Invoke(session.Network, new object[] { rpc, session.DeferredSecret });
                        session.DeferredSecret = null;
                    }
                }
                else
                {
                    Logger.LogInfo("Client requires " + result.Missing.Count + " missing mods, " + result.WrongVersion.Count + " version changes, and " + result.Extra.Count + " extra mods disabled.");
                    session.Deadline = Time.realtimeSinceStartup + 2;
                }
            }
            catch (Exception error) { session.Accepted = false; Logger.LogWarning("Rejected mod handshake: " + error.Message); session.Deadline = Time.realtimeSinceStartup + 1; }
        }
    }
}
