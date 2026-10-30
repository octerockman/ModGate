using System;
using System.Diagnostics;
using System.Linq;

namespace ModGate
{
    public sealed partial class Plugin
    {
        private ZNet? healthNetwork;
        private Stopwatch? healthClock;
        private ServerHealthSampler? healthSampler;
        private ServerHealthSnapshot? healthSnapshot;

        private ZNet? HealthContext()
        {
            var current = ZNet.instance;
            if (current == null || !current.IsServer())
            {
                if (!ReferenceEquals(healthNetwork, null)) prompt?.ForgetServerConfigAccess();
                healthNetwork = null; healthClock = null; healthSampler = null; healthSnapshot = null;
                return null;
            }
            if (!ReferenceEquals(current, healthNetwork))
            {
                if (!ReferenceEquals(healthNetwork, null)) prompt?.ForgetServerConfigAccess();
                healthNetwork = current; healthClock = Stopwatch.StartNew();
                healthSampler = new ServerHealthSampler(0); healthSnapshot = null;
            }
            return current;
        }
        private void ObserveServerHealth()
        {
            if (HealthContext() != null) healthSampler!.ObserveFrame(healthClock!.Elapsed.TotalSeconds);
        }
        private ServerHealthSnapshot GetServerHealthSnapshot()
        {
            var network = HealthContext() ?? throw new InvalidOperationException("Server health is unavailable after disconnect.");
            var now = healthClock!.Elapsed.TotalSeconds;
            if (healthSnapshot != null && !healthSampler!.NeedsSample(now)) return healthSnapshot;
            var readings = ServerHealth.ReadCurrentProcess();
            readings.Players = network.GetNrOfPlayers();
            try
            {
                // This accessor does not reset the game's transport counters. Crossplay's
                // built-in ping is not an RTT; the UI times its own health request instead.
                network.GetNetStats(out _, out _, out _, out var outgoing, out var incoming);
                readings.OutBytesPerSecond = ValidRate(outgoing);
                readings.InBytesPerSecond = ValidRate(incoming);
            }
            catch { readings.OutBytesPerSecond = readings.InBytesPerSecond = null; }
            try
            {
                long queued = 0;
                foreach (var peer in network.GetConnectedPeers().Where(p => p.IsReady() && p.m_rpc != null && p.m_rpc.IsConnected()).ToArray())
                {
                    var amount = peer.m_rpc.GetSocket().GetSendQueueSize();
                    if (amount < 0) throw new InvalidOperationException("Send queue is unavailable.");
                    queued = checked(queued + amount);
                }
                readings.SendQueueBytes = queued;
            }
            catch { readings.SendQueueBytes = null; }
            healthSnapshot = healthSampler!.Sample(now, DateTime.UtcNow, readings);
            ServerHealth.Validate(healthSnapshot);
            return healthSnapshot;
        }
        private static double? ValidRate(double value) => value < 0 || double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
    }
}
