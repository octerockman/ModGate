using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private const string AdminRequestRpc = "ModGate.AdminConfig.Request.v1", AdminResponseRpc = "ModGate.AdminConfig.Response.v1";
        private const int AdminMessageLimit = 1048576;
        private readonly Dictionary<ZRpc, AdminConfigRequestGuard> adminGuards = new Dictionary<ZRpc, AdminConfigRequestGuard>();
        private readonly Dictionary<ZRpc, Queue<DateTime>> adminFrames = new Dictionary<ZRpc, Queue<DateTime>>();
        private readonly Dictionary<string, PendingAdminRequest> pendingAdmin = new Dictionary<string, PendingAdminRequest>();
        private ZRpc? adminServer;
        private string adminNonce = "";
        private long nextAdminSequence;
        private sealed class PendingAdminRequest
        {
            public ZRpc Connection = null!;
            public DateTime Deadline;
            public string Operation = "";
            public TaskCompletionSource<AdminConfigResponse> Completion = new TaskCompletionSource<AdminConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private bool ServerConfigAvailable() => ZNet.instance != null && Player.m_localPlayer != null;
        private Task<AdminConfigResponse> RequestServerConfig(AdminConfigRequest request, string accessCode)
        {
            try
            {
                if (ZNet.instance == null || Player.m_localPlayer == null) throw new InvalidOperationException("Join the server before opening its configs.");
                request.RequestId = Guid.NewGuid().ToString("N");
                request.Nonce = Guid.NewGuid().ToString("N");
                request.Sequence = checked(++nextAdminSequence);
                if (ZNet.instance.IsServer()) return Task.FromResult(ExecuteAdminRequest(request, true));
                var connection = ZNet.instance.GetServerPeer()?.m_rpc;
                if (connection == null || !ReferenceEquals(connection, adminServer) || !connection.IsConnected()) throw new InvalidOperationException("The server config connection is unavailable. Reconnect to the server.");
                if (pendingAdmin.Count >= 4) throw new InvalidOperationException("Wait for the current server config request to finish.");
                request.Nonce = adminNonce; request.Proof = AdminConfigSecurity.Sign(request, accessCode);
                var json = Json.Write(request); if (json.Length > AdminMessageLimit) throw new InvalidOperationException("This edit is too large for the remote config page.");
                var pending = new PendingAdminRequest { Connection = connection, Operation = request.Operation, Deadline = DateTime.UtcNow.AddSeconds(20) };
                pendingAdmin.Add(request.RequestId, pending);
                try { connection.Invoke(AdminRequestRpc, json); }
                catch { pendingAdmin.Remove(request.RequestId); throw; }
                return pending.Completion.Task;
            }
            catch (Exception failure) { return Task.FromException<AdminConfigResponse>(failure); }
        }
        private AdminConfigResponse ExecuteAdminRequest(AdminConfigRequest request, bool authorized)
        {
            var response = new AdminConfigResponse { RequestId = request.RequestId, Operation = request.Operation, Authorized = authorized };
            if (!authorized) { response.Error = "Server admin access was denied. Check your admin-list entry and owner-issued access code."; return response; }
            try
            {
                if (request.Operation == "list") response.Files = ServerConfigFiles.List(Paths.BepInExRootPath, true);
                else if (request.Operation == "health") response.Health = GetServerHealthSnapshot();
                else if (request.Operation == "reload")
                {
                    response.Files = ServerConfigFiles.List(Paths.BepInExRootPath, true);
                    response.Message = ReloadLoadedModConfigs(response.Files.Where(f => f.Kind == "cfg").Select(f => f.Path));
                }
                else if (request.Operation == "read") response.Document = ServerConfigFiles.Read(Paths.BepInExRootPath, request.Path, true);
                else if (request.Operation == "save") response.Document = ServerConfigFiles.Save(Paths.BepInExRootPath, request, true);
                else throw new InvalidOperationException("Unknown server config operation.");
            }
            catch (Exception failure) { response.Error = failure.Message; }
            return response;
        }
        private void ReceiveAdminRequest(ZRpc rpc, string json)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || json == null || json.Length > AdminMessageLimit) return;
            if (!adminFrames.TryGetValue(rpc, out var arrivals)) { arrivals = new Queue<DateTime>(); adminFrames[rpc] = arrivals; }
            var now = DateTime.UtcNow;
            while (arrivals.Count > 0 && now - arrivals.Peek() >= TimeSpan.FromSeconds(10)) arrivals.Dequeue();
            if (arrivals.Count >= 30) return;
            arrivals.Enqueue(now);
            AdminConfigRequest? request = null;
            try
            {
                request = AdminConfigProtocol.ReadRequest(json);
                var peer = ZNet.instance.GetConnectedPeers().FirstOrDefault(p => ReferenceEquals(p.m_rpc, rpc));
                var connected = rpc.IsConnected() && peer != null && peer.IsReady() && sessions.TryGetValue(rpc, out var session) && session.Accepted;
                var role = connected && ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName());
                if (!adminGuards.TryGetValue(rpc, out var guard)) { guard = new AdminConfigRequestGuard(); adminGuards[rpc] = guard; }
                var nonce = sessions.TryGetValue(rpc, out var current) ? current.Policy.Nonce : "";
                var key = role ? AdminConfigSecurity.ReadOwnerKey(Paths.BepInExRootPath) : "";
                var authorized = guard.Authorize(request, key, nonce, role, connected, DateTime.UtcNow);
                var response = ExecuteAdminRequest(request, authorized);
                string result;
                try { result = AdminConfigProtocol.WriteResponse(response); }
                catch { result = AdminConfigProtocol.WriteResponse(new AdminConfigResponse { RequestId = request.RequestId, Operation = request.Operation, Authorized = authorized, Error = "This config is too large or has unsupported metadata for the remote editor. Edit it through the server host." }); }
                rpc.Invoke(AdminResponseRpc, result);
            }
            catch
            {
                // Never log request bodies, proofs, config values, or owner access codes.
                if (request != null && Guid.TryParseExact(request.RequestId, "N", out _))
                    rpc.Invoke(AdminResponseRpc, Json.Write(new AdminConfigResponse { RequestId = request.RequestId, Operation = request.Operation, Error = "The server could not authorize or read this config request." }));
            }
        }
        private void ReceiveAdminResponse(ZRpc rpc, string json)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || !ReferenceEquals(rpc, adminServer) || json == null || json.Length > AdminMessageLimit) return;
            try
            {
                var response = AdminConfigProtocol.ReadResponse(json);
                if (response.Version != 1 || !pendingAdmin.TryGetValue(response.RequestId, out var pending) || !ReferenceEquals(rpc, pending.Connection) || response.Operation != pending.Operation) return;
                pendingAdmin.Remove(response.RequestId); pending.Completion.TrySetResult(response);
            }
            catch { Logger.LogWarning("Invalid server config response was ignored."); }
        }
        private void UpdateAdminRequests()
        {
            foreach (var item in pendingAdmin.Where(p => DateTime.UtcNow >= p.Value.Deadline || !p.Value.Connection.IsConnected()).ToArray())
            {
                pendingAdmin.Remove(item.Key); item.Value.Completion.TrySetException(new InvalidOperationException("The server config request timed out or disconnected. Your local edits are kept; retry or reconnect."));
            }
            foreach (var rpc in adminGuards.Keys.Where(rpc => !rpc.IsConnected()).ToArray()) adminGuards.Remove(rpc);
            foreach (var rpc in adminFrames.Keys.Where(rpc => !rpc.IsConnected()).ToArray()) adminFrames.Remove(rpc);
            if (adminServer != null && !adminServer.IsConnected()) { adminServer = null; adminNonce = ""; prompt.ForgetServerConfigAccess(); }
        }
        private void ForgetAdminConnection(ZRpc rpc)
        {
            adminGuards.Remove(rpc);
            adminFrames.Remove(rpc);
            foreach (var item in pendingAdmin.Where(p => ReferenceEquals(p.Value.Connection, rpc)).ToArray())
            {
                pendingAdmin.Remove(item.Key); item.Value.Completion.TrySetException(new InvalidOperationException("The server config connection closed."));
            }
            if (ReferenceEquals(adminServer, rpc)) { adminServer = null; adminNonce = ""; prompt.ForgetServerConfigAccess(); }
        }
        [HarmonyPatch(typeof(Player), "TakeInput"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool KeepProfileEditorInput(ref bool __result)
        {
            if (!BlockGameInput) return true;
            __result = false; return false;
        }
    }
}
