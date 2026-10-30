using System;
using System.Collections.Generic;
using System.IO;
using ModGate;

internal static partial class Program
{
    private static readonly DateTime HealthUtc = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static ServerHealthSnapshot HealthSnapshot() => new ServerHealthSnapshot { CollectedUtc = HealthUtc.ToString("O"), UptimeSeconds = 120, Players = 2 };
    private static AdminConfigRequest HealthRequest() => new AdminConfigRequest { Operation = "health", Nonce = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N") };
    private static AdminConfigResponse HealthResponse() => new AdminConfigResponse { Operation = "health", RequestId = Guid.NewGuid().ToString("N"), Authorized = true, Health = HealthSnapshot() };

    private static void ServerHealthChecks()
    {
        Test("server health reports only measured values and leaves unsupported metrics unavailable", () =>
        {
            var sampler = new ServerHealthSampler(10);
            var snapshot = sampler.Sample(12, HealthUtc, new ServerHealthReadings { Players = 3, ProcessRssBytes = 123456, ManagedBytes = 65432, InBytesPerSecond = 1024, OutBytesPerSecond = 2048, SendQueueBytes = 99 });
            Check(snapshot.UptimeSeconds == 2 && snapshot.Players == 3 && snapshot.ProcessRssBytes == 123456 && snapshot.ManagedBytes == 65432);
            Check(snapshot.InBytesPerSecond == 1024 && snapshot.OutBytesPerSecond == 2048 && snapshot.SendQueueBytes == 99);
            Check(snapshot.ProcessCpuPercent == null && snapshot.UpdateRate == null && snapshot.LoopAverageMs == null && snapshot.LoopMaxMs == null);
            ServerHealth.Validate(snapshot);
        });
        Test("server process CPU uses one logical core as 100 percent and permits parallel CPU work above 100", () =>
        {
            var sampler = new ServerHealthSampler(10);
            Check(sampler.Sample(10, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 30 }).ProcessCpuPercent == null);
            Check(sampler.Sample(12, HealthUtc.AddSeconds(2), new ServerHealthReadings { TotalProcessorSeconds = 32 }).ProcessCpuPercent == 100);
            var snapshot = sampler.Sample(14, HealthUtc.AddSeconds(4), new ServerHealthReadings { TotalProcessorSeconds = 37 });
            Check(snapshot.ProcessCpuPercent == 250 && snapshot.UptimeSeconds == 4); ServerHealth.Validate(snapshot);
        });
        Test("server health sampling cadence reuses its existing snapshot until two seconds elapse", () =>
        {
            var sampler = new ServerHealthSampler(0); Check(sampler.NeedsSample(0));
            var first = sampler.Sample(0, HealthUtc, new ServerHealthReadings { Players = 1 });
            Check(!sampler.NeedsSample(1.999));
            Check(!sampler.TrySample(1, HealthUtc.AddSeconds(1), new ServerHealthReadings { Players = 99 }, out var cached) && ReferenceEquals(first, cached));
            Check(ReferenceEquals(first, sampler.Sample(1.5, HealthUtc, new ServerHealthReadings { Players = 99 })) && first.Players == 1);
            Check(sampler.TrySample(2, HealthUtc.AddSeconds(2), new ServerHealthReadings { Players = 2 }, out var next) && next != null && next.Players == 2 && !ReferenceEquals(first, next));
        });
        Test("a reset process CPU counter produces an unavailable sample then establishes a new baseline", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.Sample(0, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 10 });
            Check(sampler.Sample(2, HealthUtc.AddSeconds(2), new ServerHealthReadings { TotalProcessorSeconds = 9 }).ProcessCpuPercent == null);
            Check(sampler.Sample(4, HealthUtc.AddSeconds(4), new ServerHealthReadings { TotalProcessorSeconds = 11 }).ProcessCpuPercent == 100);
        });
        Test("unavailable CPU observations do not bridge a gap with an invented CPU percentage", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.Sample(0, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 10 });
            Check(sampler.Sample(2, HealthUtc, new ServerHealthReadings()).ProcessCpuPercent == null);
            Check(sampler.Sample(4, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 14 }).ProcessCpuPercent == null);
            Check(sampler.Sample(6, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 15 }).ProcessCpuPercent == 50);
        });
        Test("opening health after a long idle period waits for a current CPU baseline", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.Sample(0, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 10 });
            Check(sampler.Sample(60, HealthUtc.AddSeconds(60), new ServerHealthReadings { TotalProcessorSeconds = 70 }).ProcessCpuPercent == null);
            Check(sampler.Sample(62, HealthUtc.AddSeconds(62), new ServerHealthReadings { TotalProcessorSeconds = 72 }).ProcessCpuPercent == 100);
        });
        Test("observed server update rate is derived from real frame intervals rather than configured fixed ticks", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.ObserveFrame(0);
            for (var frame = 1; frame <= 100; frame++) sampler.ObserveFrame(frame * .02);
            var snapshot = sampler.Sample(2, HealthUtc, new ServerHealthReadings());
            Check(snapshot.UpdateRate.HasValue && Math.Abs(snapshot.UpdateRate.Value - 50) < .00001);
            Check(snapshot.LoopAverageMs.HasValue && Math.Abs(snapshot.LoopAverageMs.Value - 20) < .00001);
            Check(snapshot.LoopMaxMs.HasValue && Math.Abs(snapshot.LoopMaxMs.Value - 20) < .00001);
        });
        Test("frame summaries retain a real main-loop stall without presenting it as CPU work", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.ObserveFrame(0); sampler.ObserveFrame(.02); sampler.ObserveFrame(.04); sampler.ObserveFrame(.14);
            var snapshot = sampler.Sample(.14, HealthUtc, new ServerHealthReadings());
            Check(snapshot.LoopMaxMs.HasValue && Math.Abs(snapshot.LoopMaxMs.Value - 100) < .00001);
            Check(snapshot.LoopAverageMs.HasValue && Math.Abs(snapshot.LoopAverageMs.Value - (140d / 3)) < .00001 && snapshot.ProcessCpuPercent == null);
        });
        Test("old loop spikes expire from bounded recent summaries even while nobody polls the health tab", () =>
        {
            var sampler = new ServerHealthSampler(0); sampler.ObserveFrame(0); sampler.ObserveFrame(.5);
            Check(sampler.Sample(.5, HealthUtc, new ServerHealthReadings()).LoopMaxMs == 500);
            for (var frame = 1; frame <= 300; frame++) sampler.ObserveFrame(.5 + frame * .02);
            var snapshot = sampler.Sample(6.5, HealthUtc.AddSeconds(6), new ServerHealthReadings());
            Check(snapshot.LoopMaxMs.HasValue && Math.Abs(snapshot.LoopMaxMs.Value - 20) < .00001 && snapshot.UpdateRate.HasValue && Math.Abs(snapshot.UpdateRate.Value - 50) < .00001);
            Check(new ServerHealthSampler(0).Sample(100, HealthUtc, new ServerHealthReadings()).UpdateRate == null);
        });
        Test("a backward or invalid monotonic clock cannot create negative CPU elapsed time or stale frame statistics", () =>
        {
            var sampler = new ServerHealthSampler(100); sampler.ObserveFrame(100); sampler.ObserveFrame(100.1);
            sampler.Sample(100.1, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 10 });
            sampler.ObserveFrame(99); sampler.ObserveFrame(double.NaN); sampler.ObserveFrame(double.PositiveInfinity); sampler.ObserveFrame(-1);
            var snapshot = sampler.Sample(99, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = 20 });
            Check(snapshot.ProcessCpuPercent == null && snapshot.UpdateRate == null && snapshot.UptimeSeconds == 0);
            Check(!sampler.TrySample(double.NaN, HealthUtc, new ServerHealthReadings(), out _));
            Throws<ArgumentOutOfRangeException>(() => sampler.Sample(double.PositiveInfinity, HealthUtc, new ServerHealthReadings()));
            Throws<ArgumentOutOfRangeException>(() => new ServerHealthSampler(-1));
        });
        Test("bad local resource readings become unavailable instead of fake zero measurements", () =>
        {
            var sampler = new ServerHealthSampler(0);
            var snapshot = sampler.Sample(0, HealthUtc, new ServerHealthReadings { TotalProcessorSeconds = -1, ProcessRssBytes = -1, ManagedBytes = ServerHealth.MaxBytes + 1, Players = -1, InBytesPerSecond = double.NaN, OutBytesPerSecond = double.PositiveInfinity, SendQueueBytes = -1 });
            Check(snapshot.ProcessCpuPercent == null && snapshot.ProcessRssBytes == null && snapshot.ManagedBytes == null && snapshot.InBytesPerSecond == null && snapshot.OutBytesPerSecond == null && snapshot.SendQueueBytes == null && snapshot.Players == 0);
        });
        Test("server health wire validation rejects nonfinite negative excessive or inconsistent metrics", () =>
        {
            var invalid = new Action<ServerHealthSnapshot>[]
            {
                s => s.ProcessCpuPercent = double.NaN, s => s.ProcessCpuPercent = double.PositiveInfinity, s => s.ProcessCpuPercent = -1, s => s.ProcessCpuPercent = ServerHealth.MaxCpuPercent + 1,
                s => s.UptimeSeconds = double.NegativeInfinity, s => s.UptimeSeconds = -1, s => s.UptimeSeconds = ServerHealth.MaxSeconds + 1,
                s => s.UpdateRate = double.NaN, s => s.LoopAverageMs = -1, s => s.LoopMaxMs = double.MaxValue,
                s => { s.LoopAverageMs = 20; s.LoopMaxMs = 10; }, s => s.InBytesPerSecond = -1, s => s.OutBytesPerSecond = double.PositiveInfinity,
                s => s.OutBytesPerSecond = ServerHealth.MaxByteRate + 1, s => s.ProcessRssBytes = -1, s => s.ManagedBytes = ServerHealth.MaxBytes + 1,
                s => s.SendQueueBytes = long.MaxValue, s => s.Players = -1, s => s.Players = 65536
            };
            foreach (var change in invalid) { var snapshot = HealthSnapshot(); change(snapshot); Throws<InvalidDataException>(() => ServerHealth.Validate(snapshot)); }
            var valid = HealthSnapshot(); valid.ProcessCpuPercent = 800; valid.InBytesPerSecond = 0; valid.SendQueueBytes = 0; ServerHealth.Validate(valid);
        });
        Test("server health wire timestamps require valid UTC and preserve unavailable versus measured zero", () =>
        {
            foreach (var timestamp in new[] { "", "bad time", "2026-99-30T12:00:00Z", "2026-10-30T12:00:00+03:00", new string('x', 100) })
            { var bad = HealthSnapshot(); bad.CollectedUtc = timestamp; Throws<InvalidDataException>(() => ServerHealth.Validate(bad)); }
            var snapshot = HealthSnapshot(); snapshot.InBytesPerSecond = 0; snapshot.SendQueueBytes = 0;
            var copy = Json.Read<ServerHealthSnapshot>(Json.Write(snapshot)); ServerHealth.Validate(copy);
            Check(copy.ProcessCpuPercent == null && copy.ProcessRssBytes == null && copy.UpdateRate == null && copy.InBytesPerSecond == 0 && copy.SendQueueBytes == 0 && copy.CollectedUtc == snapshot.CollectedUtc);
        });
        Test("server health current-process sampling is read-only and does not require a hardware provider", () =>
        {
            var readings = ServerHealth.ReadCurrentProcess();
            Check(!readings.ManagedBytes.HasValue || readings.ManagedBytes.Value >= 0);
            Check(!readings.TotalProcessorSeconds.HasValue || readings.TotalProcessorSeconds.Value >= 0);
            Check(!readings.ProcessRssBytes.HasValue || readings.ProcessRssBytes.Value >= 0);
            ServerHealth.Validate(new ServerHealthSampler(0).Sample(0, HealthUtc, readings));
        });
        Test("authorized health requests and snapshots roundtrip without config content or edits", () =>
        {
            var request = HealthRequest(); request.Sequence = 9; var decoded = AdminConfigProtocol.ReadRequest(Json.Write(request)); Check(decoded.Operation == "health" && decoded.Path == "" && decoded.Changes.Count == 0 && decoded.Sequence == 9);
            var response = HealthResponse(); var copy = AdminConfigProtocol.ReadResponse(AdminConfigProtocol.WriteResponse(response));
            Check(copy.Authorized && copy.Health != null && copy.Health.Players == 2 && copy.Health.ProcessCpuPercent == null && copy.Files.Count == 0 && copy.Document == null);
        });
        Test("health requests reject every config edit payload field", () =>
        {
            foreach (var change in new Action<AdminConfigRequest>[] { r => r.Sequence = -1, r => r.Path = "example.cfg", r => r.Text = "edit", r => r.ExpectedHash = new string('a', 64), r => r.Changes.Add(new AdminConfigChange { Id = "1", Value = "edit" }) })
            { var request = HealthRequest(); change(request); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateRequest(request)); }
        });
        Test("unauthorized or failed health responses cannot disclose any metric snapshot", () =>
        {
            var response = HealthResponse(); response.Authorized = false; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
            response = HealthResponse(); response.Error = "Not available"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
            response.Health = null; response.Authorized = false; AdminConfigProtocol.ValidateResponse(response);
            response = HealthResponse(); response.Health = null; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
        });
        Test("health responses cannot mix config files documents or invalid metric data into the snapshot", () =>
        {
            var response = HealthResponse(); response.Files.Add(new AdminConfigFileInfo { Path = "example.cfg", Kind = "cfg" }); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
            response = HealthResponse(); response.Document = new AdminConfigDocument(); Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
            response = HealthResponse(); response.Operation = "list"; Throws<InvalidDataException>(() => AdminConfigProtocol.ValidateResponse(response));
            response = HealthResponse(); response.Health!.ProcessCpuPercent = double.NaN; Throws<InvalidDataException>(() => AdminConfigProtocol.WriteResponse(response));
        });
        Test("server health uses the same owner proof verified role nonce and replay guard as config operations", () =>
        {
            var key = new string('a', 64); var request = HealthRequest(); request.Sequence = 1; request.Proof = AdminConfigSecurity.Sign(request, key);
            var guard = new AdminConfigRequestGuard(); Check(!guard.Authorize(request, key, request.Nonce, false, true, HealthUtc));
            Check(guard.Authorize(request, key, request.Nonce, true, true, HealthUtc) && !guard.Authorize(request, key, request.Nonce, true, true, HealthUtc));
            var changed = Json.CloneLocal(request); changed.Operation = "list"; Check(!AdminConfigSecurity.Verify(changed, key, changed.Nonce));
            changed = Json.CloneLocal(request); changed.Nonce = Guid.NewGuid().ToString("N"); Check(!AdminConfigSecurity.Verify(changed, key, changed.Nonce));
        });
    }
}
