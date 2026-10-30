using ModGate;

internal static partial class Program
{
    private static void HealthAccessChecks()
    {
        Test("observing server updates does not collect process or transport readings before a request", () => {
            var host = Active(); var network = new ZNet(); ZNet.instance = network;
            for (var i = 0; i < 10; i++) host.Plugin.ObserveHealth();
            Check(network.StatsCalls == 0);
            var sample = host.Plugin.ReadHealth(); ServerHealth.Validate(sample);
            Check(sample.Players == 2 && sample.InBytesPerSecond == 123 && sample.OutBytesPerSecond == 456 && sample.ProcessCpuPercent == null);
            Check(network.StatsCalls == 1);
        });
        Test("repeated health requests reuse one cached snapshot and sum only ready connected peer queues", () => {
            var host = Active(); var network = new ZNet(); ZNet.instance = network;
            network.Peers.Add(new ZNetPeer()); network.Peers.Add(new ZNetPeer());
            network.Peers.Add(new ZNetPeer { Ready = false }); network.Peers.Add(new ZNetPeer { m_rpc = new ZRpc { Connected = false } });
            var first = host.Plugin.ReadHealth(); var second = host.Plugin.ReadHealth();
            Check(ReferenceEquals(first, second) && network.StatsCalls == 1 && first.SendQueueBytes == 20);
        });
        Test("unsupported transport metrics stay unavailable instead of copying crossplay zero ping", () => {
            var host = Active(); var network = new ZNet { StatsThrows = true }; ZNet.instance = network;
            network.Peers.Add(new ZNetPeer { m_rpc = new ZRpc { Socket = new TestSocket { Queued = -1 } } });
            var sample = host.Plugin.ReadHealth();
            Check(sample.InBytesPerSecond == null && sample.OutBytesPerSecond == null && sample.SendQueueBytes == null);
            Check(sample.Players == 2);
        });
        Test("invalid transport rates are unavailable while a genuine idle zero remains valid", () => {
            var host = Active(); ZNet.instance = new ZNet { InRate = float.NaN, OutRate = 0 };
            var sample = host.Plugin.ReadHealth();
            Check(sample.InBytesPerSecond == null && sample.OutBytesPerSecond == 0 && sample.SendQueueBytes == 0);
        });
        Test("a new local server instance discards the old sample and clears admin views", () => {
            var host = Active(); ZNet.instance = new ZNet { Players = 8 };
            var first = host.Plugin.ReadHealth(); ZNet.instance = new ZNet { Players = 1 };
            var next = host.Plugin.ReadHealth();
            Check(!ReferenceEquals(first, next) && first.Players == 8 && next.Players == 1 && host.Prompt.Forgotten == 1 && next.ProcessCpuPercent == null);
        });
        Test("client or disconnected contexts cannot return a cached server sample", () => {
            var host = Active(); ZNet.instance = new ZNet(); host.Plugin.ReadHealth();
            ZNet.instance = new ZNet { Server = false }; host.Plugin.ObserveHealth();
            Check(host.Prompt.Forgotten == 1);
            try { host.Plugin.ReadHealth(); throw new Exception("Unexpected server data on a client."); }
            catch (InvalidOperationException) { }
            ZNet.instance = null; host.Plugin.ObserveHealth(); Check(host.Prompt.Forgotten == 1);
        });
    }
}
