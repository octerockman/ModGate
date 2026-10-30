using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace ModGate
{
    internal sealed partial class Prompt
    {
        internal Func<double> ServerHealthClock = () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        private ServerHealthSnapshot? serverHealth;
        private readonly List<ServerHealthPoint> serverHealthHistory = new List<ServerHealthPoint>();
        private Task<ServerHealthReply>? serverHealthTask;
        private bool serverHealthAutoRefresh, serverHealthWasVisible, serverHealthHadConnection;
        private int serverHealthEpoch, serverHealthRequestEpoch, serverHealthConnectionEpoch;
        private double serverHealthReceivedAt = double.NaN, serverHealthNextPoll = double.PositiveInfinity;
        private string serverHealthError = "";
        private bool serverHealthShowAccessCode;
        private readonly Dictionary<string, GUIStyle> serverHealthWrappedStyles = new Dictionary<string, GUIStyle>();
        private bool ServerHealthView => visible && ServerHealthSelected;
        private bool ServerHealthSelected => !settingsPage && (featurePage == "ServerHealth" || featurePage.Length == 0 && navigationTab == "Server health");
        private bool ServerHealthRequestActive => serverHealthTask != null && serverHealthRequestEpoch == serverHealthEpoch && ServerHealthView;

        private sealed class ServerHealthPoint
        {
            public readonly double? RoundTripMs;
            public readonly double? JitterMs;
            public ServerHealthPoint(double? roundTrip, double? jitter) { RoundTripMs = roundTrip; JitterMs = jitter; }
        }
        private sealed class ServerHealthReply
        {
            public readonly AdminConfigResponse? Response;
            public readonly double RoundTripMs;
            public readonly string Error;
            public ServerHealthReply(AdminConfigResponse? response, double roundTrip, string error = "") { Response = response; RoundTripMs = roundTrip; Error = error; }
        }
        private void OpenServerHealth()
        {
            if (!ServerHealthView) { serverHealthEpoch++; serverHealthAutoRefresh = false; serverHealthNextPoll = double.PositiveInfinity; }
            navigationTab = "Server health"; featurePage = ""; settingsPage = false; serverHealthWasVisible = true; scroll = Vector2.zero;
        }
        private void StopServerHealthView()
        {
            if (serverHealthWasVisible || serverHealthAutoRefresh || serverHealthTask != null && serverHealthRequestEpoch == serverHealthEpoch) serverHealthEpoch++;
            serverHealthAutoRefresh = false; serverHealthWasVisible = false; serverHealthNextPoll = double.PositiveInfinity;
        }
        internal void ForgetServerHealth()
        {
            serverHealthEpoch++; serverHealthAutoRefresh = false; serverHealthWasVisible = false; serverHealthHadConnection = false;
            serverHealth = null; serverHealthHistory.Clear(); serverHealthReceivedAt = double.NaN; serverHealthNextPoll = double.PositiveInfinity; serverHealthError = ""; serverHealthShowAccessCode = false;
            // An in-flight request is drained by Update. Its old epoch can never repopulate this view.
        }
        private void ToggleServerHealthAutoRefresh()
        {
            if (serverHealthAutoRefresh) { serverHealthAutoRefresh = false; serverHealthNextPoll = double.PositiveInfinity; return; }
            if (!CanRequestServerHealth()) return;
            serverHealthAutoRefresh = true; serverHealthNextPoll = ServerHealthClock(); RequestServerHealth();
        }
        private bool CanRequestServerHealth() => ServerHealthView && ServerConfigAvailable?.Invoke() == true && ServerConfigRequest != null && !WorkflowBusy && serverHealthTask == null;
        private void RequestServerHealth()
        {
            if (!CanRequestServerHealth()) return;
            serverHealthWasVisible = true; serverHealthHadConnection = true;
            serverHealthRequestEpoch = serverHealthEpoch; serverHealthConnectionEpoch = serverConfigEpoch;
            serverHealthNextPoll = ServerHealthClock() + 2;
            var watch = Stopwatch.StartNew();
            try
            {
                // Invoke ZRpc on the game thread. Only the response wait runs asynchronously.
                var request = ServerConfigRequest!(new AdminConfigRequest { Operation = "health" }, adminAccessCode);
                serverHealthTask = AwaitServerHealth(request, watch);
            }
            catch (Exception failure)
            {
                watch.Stop(); serverHealthTask = Task.FromResult(new ServerHealthReply(null, watch.Elapsed.TotalMilliseconds, failure.Message));
            }
        }
        private static async Task<ServerHealthReply> AwaitServerHealth(Task<AdminConfigResponse> request, Stopwatch watch)
        {
            try
            {
                var response = await request.ConfigureAwait(false); watch.Stop();
                return new ServerHealthReply(response, watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception failure) { watch.Stop(); return new ServerHealthReply(null, watch.Elapsed.TotalMilliseconds, failure.Message); }
        }
        private void UpdateServerHealth()
        {
            var connected = ServerConfigAvailable?.Invoke() == true;
            if (!connected && (serverHealthHadConnection || serverHealth != null || serverHealthHistory.Count > 0 || serverHealthAutoRefresh || serverHealthTask != null && serverHealthRequestEpoch == serverHealthEpoch)) ForgetServerHealth();
            var viewing = ServerHealthView;
            if (!viewing && serverHealthWasVisible) StopServerHealthView();
            serverHealthWasVisible = viewing; serverHealthHadConnection = connected;
            if (serverHealthTask != null && serverHealthTask.IsCompleted)
            {
                var completed = serverHealthTask; serverHealthTask = null;
                var reply = completed.GetAwaiter().GetResult();
                if (viewing && connected && serverHealthRequestEpoch == serverHealthEpoch && serverHealthConnectionEpoch == serverConfigEpoch)
                {
                    try
                    {
                        var response = reply.Response;
                        if (reply.Error.Length > 0) throw new InvalidOperationException(reply.Error);
                        if (response == null || !response.Authorized) throw new InvalidOperationException(!string.IsNullOrEmpty(response?.Error) ? response!.Error : "Server admin access was denied. Check your admin-list entry and owner-issued access code.");
                        if (response.Error.Length > 0) throw new InvalidOperationException(response.Error);
                        if (response.Health == null) throw new InvalidOperationException("The server returned no health measurements.");
                        ServerHealth.Validate(response.Health);
                        var previous = serverHealthHistory.LastOrDefault();
                        var roundTrip = ServerConfigLocalOwner?.Invoke() == true ? (double?)null : reply.RoundTripMs;
                        serverHealthHistory.Add(new ServerHealthPoint(roundTrip, roundTrip.HasValue && previous?.RoundTripMs is double previousTrip ? Math.Abs(roundTrip.Value - previousTrip) : (double?)null));
                        if (serverHealthHistory.Count > 60) serverHealthHistory.RemoveAt(0);
                        serverHealth = response.Health; serverHealthReceivedAt = ServerHealthClock(); serverHealthError = "";
                        serverHealthNextPoll = serverHealthReceivedAt + 2;
                    }
                    catch (Exception failure)
                    {
                        serverHealth = null; serverHealthHistory.Clear(); serverHealthReceivedAt = double.NaN; serverHealthError = failure.Message;
                        serverHealthAutoRefresh = false; serverHealthNextPoll = double.PositiveInfinity;
                    }
                }
            }
            if (viewing && connected && serverHealthAutoRefresh && ServerHealthClock() >= serverHealthNextPoll) RequestServerHealth();
        }
        private void DrawServerHealth()
        {
            if (ServerConfigAvailable?.Invoke() != true) { ServerHealthNotice("Not connected", "Join a server to view its health measurements.", "muted"); return; }
            if (ServerConfigLocalOwner?.Invoke() == true) Label("Local server owner access");
            else if (serverHealth == null || serverHealthShowAccessCode)
            {
                HealthParagraph("A server admin-list entry and the owner-issued access code are required.");
                Label("Admin access code (kept only until disconnect)");
                adminAccessCode = EditText(adminAccessCode, "admin-access-code", password: true);
            }
            else
            {
                var access = Reserve(34);
                LabelAt(new Rect(access.x, access.y + 5, Math.Max(1, access.width - 190), 24), access.width < 400 ? "Verified" : "Admin access verified", "small", "muted");
                if (Action(new Rect(access.x + access.width - 184, access.y, 184, 30), "Change access code", "quiet")) serverHealthShowAccessCode = true;
            }
            var tools = Reserve(contentWidth < 430 ? 88 : 44);
            var narrow = tools.width < 400; var half = narrow ? tools.width : (tools.width - 10) / 2;
            var enabled = GUI.enabled; GUI.enabled = enabled && CanRequestServerHealth();
            try { if (Action(new Rect(tools.x, tools.y, half, 38), "Refresh server health")) RequestServerHealth(); }
            finally { GUI.enabled = enabled; }
            GUI.enabled = enabled && (serverHealthAutoRefresh || CanRequestServerHealth());
            try { if (Action(new Rect(narrow ? tools.x : tools.x + half + 10, narrow ? tools.y + 44 : tools.y, half, 38), serverHealthAutoRefresh ? "Stop auto refresh" : "Start auto refresh", "secondary", serverHealthAutoRefresh)) ToggleServerHealthAutoRefresh(); }
            finally { GUI.enabled = enabled; }
            HealthParagraph("Auto refresh: every 2 seconds while this view is open.", "small");
            if (serverHealthError.Length > 0) ServerHealthNotice("Server health unavailable", serverHealthError, "red");
            if (serverHealthTask != null) Label("Waiting for server health…");
            if (serverHealth == null)
            {
                if (serverHealthError.Length == 0) ServerHealthNotice("No sample yet", "Refresh server health to view measurements. Unsupported values are shown as unavailable.", "muted");
                return;
            }
            var age = Math.Max(0, ServerHealthClock() - serverHealthReceivedAt); var stale = age > 6;
            var status = Reserve(38);
            Badge(new Rect(status.x, status.y, stale ? 92 : 102, 28), stale ? "Stale" : "Received", stale ? "yellow" : "green");
            LabelAt(new Rect(status.x + 114, status.y, Math.Max(1, status.width - 114), 30), "Received " + age.ToString("0.0", CultureInfo.InvariantCulture) + " seconds ago", "small", "muted");
            if (stale) ServerHealthNotice("This sample is stale", "Refresh to check current conditions. These values describe an earlier sample.", "yellow");
            LabelAt(Reserve(24), "Server sample: " + serverHealth.CollectedUtc, "small", "muted");
            if (!serverHealth.ProcessCpuPercent.HasValue) HealthParagraph("CPU needs a second sample after at least 2 seconds when supported.", "small");
            var latest = serverHealthHistory.LastOrDefault();
            var localOwner = ServerConfigLocalOwner?.Invoke() == true;
            var metrics = new[] {
                new HealthMetric("Valheim process CPU", HealthNumber(serverHealth.ProcessCpuPercent, "%"), "100%=one core; listen hosts include client work"),
                new HealthMetric("Resident memory", HealthBytes(serverHealth.ProcessRssBytes), "Server process RSS"),
                new HealthMetric("Managed memory", HealthBytes(serverHealth.ManagedBytes), "Managed heap; do not add to RSS"),
                new HealthMetric("Request round trip", localOwner ? "Local host" : HealthNumber(latest?.RoundTripMs, " ms"), localOwner ? "Network latency is not applicable" : "Includes server processing"),
                new HealthMetric("RTT change / jitter", localOwner ? "Not applicable" : HealthNumber(latest?.JitterMs, " ms"), localOwner ? "Health is handled in this process" : "Change since previous response"),
                new HealthMetric("Update interval · average", HealthNumber(serverHealth.LoopAverageMs, " ms"), "Elapsed time between game updates"),
                new HealthMetric("Update interval · maximum", HealthNumber(serverHealth.LoopMaxMs, " ms"), "Recent responsiveness sample"),
                new HealthMetric("Update cadence", HealthNumber(serverHealth.UpdateRate, " /s"), "Observed main-loop updates, not TPS"),
                new HealthMetric("Session uptime", HealthUptime(serverHealth.UptimeSeconds), "Since this server session started"),
                new HealthMetric("Connected players", serverHealth.Players.ToString(CultureInfo.InvariantCulture), "Reported by the server"),
                new HealthMetric("Network in", HealthRate(serverHealth.InBytesPerSecond), "Server-side traffic rate"),
                new HealthMetric("Network out", HealthRate(serverHealth.OutBytesPerSecond), "Server-side traffic rate"),
                new HealthMetric("Send queue", HealthBytes(serverHealth.SendQueueBytes), "Reported outgoing queue"),
                new HealthMetric("GPU / temperature", "Unavailable", "Not collected by ModGate")
            };
            var columns = contentWidth >= 760 ? 3 : contentWidth >= 480 ? 2 : 1;
            for (var first = 0; first < metrics.Length; first += columns)
            {
                var row = Reserve(136); var width = (row.width - (columns - 1) * 12) / columns;
                for (var column = 0; column < columns && first + column < metrics.Length; column++)
                {
                    var metric = metrics[first + column]; var box = new Rect(row.x + column * (width + 12), row.y, width, 126); Card(box);
                    LabelAt(new Rect(box.x + 16, box.y + 12, width - 32, 24), metric.Name, "small", "muted");
                    LabelAt(new Rect(box.x + 16, box.y + 37, width - 32, 30), metric.Value, "heading", stale ? "muted" : "ink");
                    HealthLabel(new Rect(box.x + 16, box.y + 73, width - 32, 42), metric.Detail, "small", "muted");
                }
            }
            DrawServerHealthHistory(stale);
        }
        private sealed class HealthMetric
        {
            public readonly string Name, Value, Detail;
            public HealthMetric(string name, string value, string detail) { Name = name; Value = value; Detail = detail; }
        }
        private void DrawServerHealthHistory(bool stale)
        {
            if (ServerConfigLocalOwner?.Invoke() == true) { ServerHealthNotice("Local host", "Network round trip and jitter are not applicable to same-process owner requests.", "muted"); return; }
            Section("Connection history"); HealthParagraph("Last " + serverHealthHistory.Count + " samples · this connection only · up to 60 kept in memory", "small");
            var box = Reserve(320); Card(box);
            HealthLabel(new Rect(box.x + 18, box.y + 12, box.width - 36, 40), "RPC round trip · includes server processing", "small", "muted");
            DrawHealthBars(new Rect(box.x + 18, box.y + 58, box.width - 36, 70), serverHealthHistory.Select(point => point.RoundTripMs).ToArray(), stale);
            HealthLabel(new Rect(box.x + 18, box.y + 142, box.width - 36, 40), "Jitter · absolute change between responses", "small", "muted");
            DrawHealthBars(new Rect(box.x + 18, box.y + 188, box.width - 36, 54), serverHealthHistory.Select(point => point.JitterMs).ToArray(), stale);
            HealthLabel(new Rect(box.x + 18, box.y + 256, box.width - 36, 48), "Older samples → latest · each chart scales to its own peak", "small", "muted");
        }
        private void ServerHealthNotice(string titleText, string description, string tone)
        {
            var width = Mathf.Max(1, contentWidth - 54);
            var textHeight = Mathf.Max(22, HealthWrappedStyle("small", "ink").CalcHeight(new GUIContent(description), width));
            var area = Reserve(textHeight + 52); Badge(new Rect(area.x, area.y, area.width, area.height - 8), "", tone);
            LabelAt(new Rect(area.x + 17, area.y + 9, area.width - 34, 25), titleText, "body", tone);
            HealthLabel(new Rect(area.x + 17, area.y + 36, area.width - 34, textHeight), description, "small");
        }
        private GUIStyle HealthWrappedStyle(string kind, string tone)
        {
            var key = kind + "/" + tone;
            if (!serverHealthWrappedStyles.TryGetValue(key, out var style)) { style = new GUIStyle(TextStyle(kind, tone)) { wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.UpperLeft }; serverHealthWrappedStyles[key] = style; }
            return style;
        }
        private void HealthLabel(Rect area, string text, string kind = "body", string tone = "ink")
        {
            var enabled = GUI.enabled; GUI.enabled = true;
            try { GUI.Label(area, text, HealthWrappedStyle(kind, tone)); } finally { GUI.enabled = enabled; }
        }
        private void HealthParagraph(string text, string kind = "body", string tone = "muted")
        {
            var width = Mathf.Max(1, contentWidth - 20); var height = HealthWrappedStyle(kind, tone).CalcHeight(new GUIContent(text), width);
            var area = Reserve(height + 6); HealthLabel(new Rect(area.x, area.y, area.width, height), text, kind, tone);
        }
        private void DrawHealthBars(Rect area, double?[] values, bool stale)
        {
            Divider(new Rect(area.x, area.y + area.height, area.width, 1));
            var maximum = Math.Max(1, values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(1).Max());
            var width = area.width / 60;
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue) continue;
                var height = (float)Math.Max(2, values[i]!.Value / maximum * area.height);
                GUI.DrawTexture(new Rect(area.x + (60 - values.Length + i) * width, area.y + area.height - height, Math.Max(1, width - 1), height), stale ? separatorBackground! : goldBackground!);
            }
        }
        private static string HealthNumber(double? value, string unit) => value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + unit : "Unavailable";
        private static string HealthBytes(long? value) => value.HasValue ? HealthSize(value.Value) : "Unavailable";
        private static string HealthRate(double? value) => value.HasValue ? HealthSize(value.Value) + "/s" : "Unavailable";
        private static string HealthSize(double bytes) => bytes >= 1073741824 ? (bytes / 1073741824).ToString("0.0", CultureInfo.InvariantCulture) + " GiB" : bytes >= 1048576 ? (bytes / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MiB" : bytes >= 1024 ? (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KiB" : bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
        private static string HealthUptime(double seconds)
        {
            var elapsed = TimeSpan.FromSeconds(seconds);
            return elapsed.TotalDays >= 1 ? (int)elapsed.TotalDays + "d " + elapsed.Hours + "h" : elapsed.TotalHours >= 1 ? (int)elapsed.TotalHours + "h " + elapsed.Minutes + "m" : elapsed.Minutes + "m " + elapsed.Seconds + "s";
        }
    }
}
