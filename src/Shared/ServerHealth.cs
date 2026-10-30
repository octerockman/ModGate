using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
#if !MODGATE_PRELOADER
using System.Diagnostics;
#endif

namespace ModGate
{
    [DataContract] internal sealed class ServerHealthSnapshot
    {
        [DataMember] public string CollectedUtc = "";
        [DataMember] public double UptimeSeconds = 0;
        // 100% represents one logical CPU core, not the hosting machine's total capacity.
        [DataMember] public double? ProcessCpuPercent = null;
        [DataMember] public long? ProcessRssBytes = null, ManagedBytes = null;
        [DataMember] public double? UpdateRate = null, LoopAverageMs = null, LoopMaxMs = null;
        [DataMember] public int Players = 0;
        [DataMember] public double? InBytesPerSecond = null, OutBytesPerSecond = null;
        [DataMember] public long? SendQueueBytes = null;
    }

    internal static class ServerHealth
    {
        public const double SampleIntervalSeconds = 2;
        internal const double MaxSeconds = 10000000000, MaxCpuPercent = 1000000, MaxUpdateRate = 1000000, MaxLoopMs = 86400000, MaxByteRate = 1000000000000;
        internal const long MaxBytes = 1125899906842624; // One PiB; rejects corrupt values without assuming a host allocation.
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static bool InRange(double value, double maximum) => Finite(value) && value >= 0 && value <= maximum;
        internal static double? Metric(double? value, double maximum) => value.HasValue && InRange(value.Value, maximum) ? value : null;
        internal static long? Bytes(long? value) => value.HasValue && value.Value >= 0 && value.Value <= MaxBytes ? value : null;

        public static void Validate(ServerHealthSnapshot snapshot)
        {
            if (snapshot == null || snapshot.CollectedUtc == null || snapshot.CollectedUtc.Length > 40 || !snapshot.CollectedUtc.EndsWith("Z", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(snapshot.CollectedUtc, new[] { "O", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _) ||
                !InRange(snapshot.UptimeSeconds, MaxSeconds) || snapshot.Players < 0 || snapshot.Players > 65535 ||
                !Valid(snapshot.ProcessCpuPercent, MaxCpuPercent) || !Valid(snapshot.UpdateRate, MaxUpdateRate) || !Valid(snapshot.LoopAverageMs, MaxLoopMs) || !Valid(snapshot.LoopMaxMs, MaxLoopMs) ||
                !Valid(snapshot.InBytesPerSecond, MaxByteRate) || !Valid(snapshot.OutBytesPerSecond, MaxByteRate) ||
                !Valid(snapshot.ProcessRssBytes) || !Valid(snapshot.ManagedBytes) || !Valid(snapshot.SendQueueBytes) ||
                snapshot.LoopAverageMs.HasValue && snapshot.LoopMaxMs.HasValue && snapshot.LoopAverageMs.Value > snapshot.LoopMaxMs.Value)
                throw new InvalidDataException("Invalid server health measurements.");
        }
        private static bool Valid(double? value, double maximum) => !value.HasValue || InRange(value.Value, maximum);
        private static bool Valid(long? value) => !value.HasValue || value.Value >= 0 && value.Value <= MaxBytes;

#if !MODGATE_PRELOADER
        public static ServerHealthReadings ReadCurrentProcess()
        {
            var result = new ServerHealthReadings();
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    try { result.TotalProcessorSeconds = Metric(process.TotalProcessorTime.TotalSeconds, 10000000000000000); }
                    catch (Exception error) when (Unavailable(error)) { }
                    try
                    {
                        var resident = process.WorkingSet64;
                        result.ProcessRssBytes = resident > 0 ? Bytes(resident) : null;
                    }
                    catch (Exception error) when (Unavailable(error)) { }
                }
            }
            catch (Exception error) when (Unavailable(error)) { }
            // This is live managed data, not Unity's reserved heap or the server's RAM limit.
            try { result.ManagedBytes = Bytes(GC.GetTotalMemory(false)); }
            catch (Exception error) when (Unavailable(error)) { }
            return result;
        }
        private static bool Unavailable(Exception error) => error is NotSupportedException || error is NotImplementedException || error is InvalidOperationException || error is IOException ||
            error is System.ComponentModel.Win32Exception || error is UnauthorizedAccessException || error is System.Security.SecurityException;
#endif
    }

#if !MODGATE_PRELOADER
    internal sealed class ServerHealthReadings
    {
        public double? TotalProcessorSeconds = null;
        public long? ProcessRssBytes = null, ManagedBytes = null;
        public int Players = 0;
        public double? InBytesPerSecond = null, OutBytesPerSecond = null;
        public long? SendQueueBytes = null;
    }

    internal sealed class ServerHealthSampler
    {
        private const int FrameBuckets = 8;
        private const double BucketSeconds = .25, MaximumCpuGapSeconds = 30;
        private struct FrameBucket { public long Id, Count; public double TotalSeconds, MaxSeconds; }
        private readonly FrameBucket[] frames = new FrameBucket[FrameBuckets];
        private readonly double startedSeconds;
        private double? lastFrameSeconds, lastSampleSeconds, lastProcessorSeconds;
        public ServerHealthSnapshot? Latest { get; private set; }

        public ServerHealthSampler(double startedSeconds)
        {
            if (!ServerHealth.InRange(startedSeconds, ServerHealth.MaxSeconds)) throw new ArgumentOutOfRangeException(nameof(startedSeconds));
            this.startedSeconds = startedSeconds;
        }
        public bool NeedsSample(double nowSeconds) => ServerHealth.InRange(nowSeconds, ServerHealth.MaxSeconds) &&
            (!lastSampleSeconds.HasValue || nowSeconds < lastSampleSeconds.Value || nowSeconds - lastSampleSeconds.Value >= ServerHealth.SampleIntervalSeconds);

        public void ObserveFrame(double nowSeconds)
        {
            if (!ServerHealth.InRange(nowSeconds, ServerHealth.MaxSeconds)) return;
            if (lastFrameSeconds.HasValue)
            {
                var interval = nowSeconds - lastFrameSeconds.Value;
                if (interval < 0) { Array.Clear(frames, 0, frames.Length); lastProcessorSeconds = null; }
                else if (interval > 0 && interval <= ServerHealth.MaxLoopMs / 1000)
                {
                    var id = (long)Math.Floor(nowSeconds / BucketSeconds); var index = (int)(id % FrameBuckets);
                    if (frames[index].Id != id) frames[index] = new FrameBucket { Id = id };
                    frames[index].Count++; frames[index].TotalSeconds += interval;
                    frames[index].MaxSeconds = Math.Max(frames[index].MaxSeconds, interval);
                }
            }
            lastFrameSeconds = nowSeconds;
        }
        public bool TrySample(double nowSeconds, DateTime utcNow, ServerHealthReadings readings, out ServerHealthSnapshot? snapshot)
        {
            snapshot = Latest;
            if (!NeedsSample(nowSeconds)) return false;
            snapshot = Sample(nowSeconds, utcNow, readings); return true;
        }
        public ServerHealthSnapshot Sample(double nowSeconds, DateTime utcNow, ServerHealthReadings readings)
        {
            if (!ServerHealth.InRange(nowSeconds, ServerHealth.MaxSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
            if (readings == null) throw new ArgumentNullException(nameof(readings));
            if (!NeedsSample(nowSeconds) && Latest != null) return Latest;
            var cpu = ServerHealth.Metric(readings.TotalProcessorSeconds, 10000000000000000);
            double? cpuPercent = null;
            if (cpu.HasValue && lastProcessorSeconds.HasValue && lastSampleSeconds.HasValue)
            {
                var elapsed = nowSeconds - lastSampleSeconds.Value; var consumed = cpu.Value - lastProcessorSeconds.Value;
                if (elapsed > 0 && elapsed <= MaximumCpuGapSeconds && consumed >= 0) cpuPercent = ServerHealth.Metric(consumed / elapsed * 100, ServerHealth.MaxCpuPercent);
            }
            lastProcessorSeconds = cpu; lastSampleSeconds = nowSeconds;
            var latestId = (long)Math.Floor(nowSeconds / BucketSeconds); long count = 0; double total = 0, maximum = 0;
            foreach (var frame in frames)
                if (frame.Count > 0 && frame.Id <= latestId && frame.Id > latestId - FrameBuckets)
                { count += frame.Count; total += frame.TotalSeconds; maximum = Math.Max(maximum, frame.MaxSeconds); }
            var mean = count > 0 && total > 0 ? (double?)(total / count * 1000) : null;
            var snapshot = new ServerHealthSnapshot
            {
                CollectedUtc = (utcNow.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utcNow, DateTimeKind.Utc) : utcNow.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
                UptimeSeconds = Math.Max(0, nowSeconds - startedSeconds), ProcessCpuPercent = cpuPercent,
                ProcessRssBytes = ServerHealth.Bytes(readings.ProcessRssBytes), ManagedBytes = ServerHealth.Bytes(readings.ManagedBytes),
                UpdateRate = mean.HasValue ? ServerHealth.Metric(1000 / mean.Value, ServerHealth.MaxUpdateRate) : null,
                LoopAverageMs = mean, LoopMaxMs = count > 0 ? (double?)maximum * 1000 : null,
                Players = Math.Max(0, Math.Min(65535, readings.Players)),
                InBytesPerSecond = ServerHealth.Metric(readings.InBytesPerSecond, ServerHealth.MaxByteRate), OutBytesPerSecond = ServerHealth.Metric(readings.OutBytesPerSecond, ServerHealth.MaxByteRate),
                SendQueueBytes = ServerHealth.Bytes(readings.SendQueueBytes)
            };
            ServerHealth.Validate(snapshot); Latest = snapshot; return snapshot;
        }
    }
#endif
}
