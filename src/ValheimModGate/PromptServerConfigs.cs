using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        internal Func<bool>? ServerConfigAvailable;
        internal Func<bool>? ServerConfigLocalOwner = null;
        internal Func<AdminConfigRequest, string, Task<AdminConfigResponse>>? ServerConfigRequest;
        private string adminAccessCode = "", remoteText = "", originalRemoteText = "";
        private List<AdminConfigFileInfo> remoteFiles = new List<AdminConfigFileInfo>();
        private AdminConfigDocument? remoteDocument;
        private int serverConfigEpoch;
        private readonly Dictionary<string, string> originalRemote = new Dictionary<string, string>();
        private bool RemoteDirty => remoteDocument != null && (remoteText != originalRemoteText || remoteDocument.Entries.Any(e => originalRemote.TryGetValue(e.Id, out var value) && e.Value != value));
        internal void ForgetServerConfigAccess()
        {
            serverConfigEpoch++; if (featurePage == "Server") featureCancellation?.Cancel();
            ForgetServerHealth();
            adminAccessCode = ""; remoteFiles.Clear(); ClearRemoteDocument();
        }
        private void OpenServerConfigs() { featurePage = "Server"; settingsPage = false; ClearRemoteDocument(); scroll = Vector2.zero; error = ""; }
        private void ClearRemoteDocument()
        {
            remoteDocument = null; originalRemote.Clear(); remoteText = originalRemoteText = ""; expandedServerChoice = "";
        }
        private void RunServerRequest(AdminConfigRequest request, Action<AdminConfigResponse> apply)
        {
            if (featureTask != null || task != null || ServerHealthRequestActive || scheduled) return;
            if (ServerConfigRequest == null || ServerConfigAvailable?.Invoke() != true) { error = "Join the server before opening its configs."; return; }
            try
            {
                // ZRpc invocation happens on the game thread; only awaiting the response is asynchronous.
                var response = ServerConfigRequest(request, adminAccessCode);
                progress = "Waiting for the server…"; error = "";
                featureCancellation = new CancellationTokenSource();
                featureTask = AwaitServer(response, apply, featureCancellation.Token, serverConfigEpoch);
            }
            catch (Exception failure) { error = failure.Message; }
        }
        private async Task<Action> AwaitServer(Task<AdminConfigResponse> response, Action<AdminConfigResponse> apply, CancellationToken token, int epoch)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(response, canceled.Task).ConfigureAwait(false) != response) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                var result = await response.ConfigureAwait(false);
                return () => { if (epoch != serverConfigEpoch) return; if (!result.Authorized || result.Error.Length > 0) { error = result.Error.Length > 0 ? result.Error : "Server admin access was denied."; return; } apply(result); };
            }
        }
        private void SetRemoteDocument(AdminConfigDocument document)
        {
            remoteDocument = document; originalRemote.Clear();
            foreach (var entry in document.Entries) originalRemote[entry.Id] = entry.Value;
            remoteText = originalRemoteText = document.Text ?? "";
            var file = remoteFiles.FirstOrDefault(f => f.Path == document.Path); if (file != null) file.Pending = document.Pending;
            progress = document.Pending ? "Saved on the server · Changes apply after the server restarts." : "Server config loaded.";
        }
        private void SaveRemoteConfig()
        {
            if (remoteDocument == null) return;
            var navigation = pendingNavigation; pendingNavigation = null;
            var request = new AdminConfigRequest { Operation = "save", Path = remoteDocument.Path, ExpectedHash = remoteDocument.Hash, Text = remoteText,
                Changes = remoteDocument.Entries.Where(e => originalRemote.TryGetValue(e.Id, out var before) && before != e.Value).Select(e => new AdminConfigChange { Id = e.Id, Value = e.Value }).ToList() };
            RunServerRequest(request, response => {
                if (response.Document != null) SetRemoteDocument(response.Document);
                progress = "Saved on the server · A backup is kept. Restart the server to apply changes.";
                if (navigation != null) afterFeature = navigation;
            });
        }
        private void DiscardRemoteEdits()
        {
            if (remoteDocument != null) foreach (var entry in remoteDocument.Entries) if (originalRemote.TryGetValue(entry.Id, out var value)) entry.Value = value;
            remoteText = originalRemoteText;
        }
        private void DrawServerConfigs()
        {
            Label("Server configs · Admin access");
            if (ServerConfigAvailable?.Invoke() != true) { Label("Join a server, then open ModGate with Shift+F6 to manage its configs."); return; }
            if (ServerConfigLocalOwner?.Invoke() == true) Label("Local server owner access · These changes apply to your hosted server.");
            else
            {
                Label("Access requires a server admin-list entry and the owner-issued access code.");
                Label("Admin access code (kept only until disconnect)");
                adminAccessCode = EditText(adminAccessCode, "admin-access-code", password: true);
            }
            var before = GUI.enabled;
            GUI.enabled = before && !Busy;
            try
            {
                if (LayoutButton("Connect / refresh server files")) RequestNavigation(() => RefreshServerConfigs(false));
                if (LayoutButton("Reload configs")) RequestNavigation(() => RefreshServerConfigs(true));
            }
            finally { GUI.enabled = before; }
            Label("Changes are saved on the server and applied on its next restart.");
            Label("Reload reads current server files and reloads supported running mod configs. Saved edits stay queued for restart; some mods apply settings only at startup.");
            foreach (var file in remoteFiles)
            {
                var selected = file;
                GUI.enabled = before && !Busy;
                try
                {
                    if (LayoutButton((remoteDocument?.Path == file.Path ? "● " : "") + file.Path)) RequestNavigation(() => {
                        if (remoteDocument?.Path == selected.Path) ClearRemoteDocument();
                        else RunServerRequest(new AdminConfigRequest { Operation = "read", Path = selected.Path }, response => { if (response.Document != null) SetRemoteDocument(response.Document); });
                    });
                }
                finally { GUI.enabled = before; }
                if (remoteDocument?.Path == file.Path) DrawServerConfigDocument();
            }
            if (remoteFiles.Count > 0 && remoteDocument == null) Label("Choose a server config file to expand its settings.");
        }
        private void RefreshServerConfigs(bool reload)
        {
            RunServerRequest(new AdminConfigRequest { Operation = reload ? "reload" : "list" }, response => {
                remoteFiles = response.Files; ClearRemoteDocument();
                progress = reload ? (response.Message.Length > 0 ? response.Message : "Server configs reloaded from disk.") + " Saved edits still require a server restart; some mods apply settings only at startup." : "Choose a server config file.";
            });
        }
        private void DrawServerConfigDocument()
        {
            if (remoteDocument == null) return;
            GUILayout.Space(12); Label(remoteDocument.Path + (RemoteDirty ? " · Unsaved changes" : remoteDocument.Pending ? " · Saved for server restart" : ""));
            if (remoteDocument.Problem.Length > 0) Label(remoteDocument.Problem);
            var before = GUI.enabled;
            GUI.enabled = before && !Busy;
            try
            {
                if (remoteDocument.Kind == "json")
                {
                    Label("ModGate server settings · Edit the JSON below. Existing field-lock (*) rules still apply.");
                    remoteText = EditText(remoteText, "server-policy-json", multiline: true);
                }
                else foreach (var entry in remoteDocument.Entries)
                {
                    GUILayout.Space(8); Label(entry.Section + " / " + entry.Key); if (entry.Description.Length > 0) Label(entry.Description);
                    GUI.enabled = before && entry.Editable && !Busy;
                    if (entry.Kind == "Boolean") { var wasOn = entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase); var isOn = Toggle(wasOn, "Enabled"); if (wasOn != isOn) entry.Value = isOn ? "true" : "false"; }
                    else if (entry.Kind == "Choice" && entry.Choices.Count > 0) entry.Value = DrawChoices(entry.Value, entry.Choices, entry.Id, ref expandedServerChoice);
                    else entry.Value = EditText(entry.Value, "server-value-" + entry.Id);
                    if (entry.HasDefault && LayoutButton("Reset " + entry.Key + " to default")) entry.Value = entry.DefaultValue;
                    GUI.enabled = before; if (entry.Problem.Length > 0) Label(entry.Problem);
                }
                GUI.enabled = before && !Busy;
                if (RemoteDirty && LayoutButton("Discard unsaved server edits")) DiscardRemoteEdits();
                GUILayout.Space(12);
            }
            finally { GUI.enabled = before; }
        }
    }
}
