using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LedImageUpdaterService.Services;

/// <summary>Local-network helpers for finding a Huidu card: per-interface UDP broadcast and a TCP subnet scan.</summary>
internal static class HuiduNet
{
    public readonly record struct LocalIf(string Name, IPAddress Ip, IPAddress Mask)
    {
        public int Bits => MaskBits(Mask);
        public IPAddress Broadcast => BroadcastOf(Ip, Mask);
    }

    /// <summary>Active IPv4 interfaces (no loopback, no AnyDesk/VPN-style adapters).</summary>
    public static List<LocalIf> LocalInterfaces()
    {
        var list = new List<LocalIf>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var f = ($"{ni.Name} {ni.Description}").ToLowerInvariant();
            if (f.Contains("anydesk") || f.Contains("teamviewer") || f.Contains("vpn") || f.Contains("wintun")
                || f.Contains("wireguard") || f.Contains("vmware") || f.Contains("vbox") || f.Contains("vethernet")
                || f.Contains("hyper-v") || f.Contains("tap-")) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null || Equals(ua.IPv4Mask, IPAddress.Any)) continue;
                list.Add(new LocalIf(ni.Name, ua.Address, ua.IPv4Mask));
            }
        }
        return list;
    }

    public static IPAddress BroadcastOf(IPAddress ip, IPAddress mask)
    {
        var a = ip.GetAddressBytes(); var m = mask.GetAddressBytes(); var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    public static int MaskBits(IPAddress mask)
    {
        int bits = 0;
        foreach (var b in mask.GetAddressBytes())
            for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) bits++;
        return bits;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// Finds hosts with a card port open (TCP connect only — no protocol traffic) in every private subnet of
    /// this PC (/22 or smaller). Catches cards that ignore UDP broadcast (other VLAN-less switch settings,
    /// firewalls, wrong discovery port). Callers verify the hits with the protocol handshake.
    /// </summary>
    public static List<(IPAddress Ip, int Port)> TcpScan(int[] ports, int connectTimeoutMs, Action<string> log, int budgetMs = 8000)
    {
        var hits = new ConcurrentBag<(IPAddress, int)>();
        var own = new HashSet<string>();
        var hosts = new List<IPAddress>();
        foreach (var li in LocalInterfaces())
        {
            own.Add(li.Ip.ToString());
            if (!IsPrivate(li.Ip) || li.Bits < 22 || li.Bits > 30) continue;
            uint ip = ToUInt(li.Ip), mask = ToUInt(li.Mask);
            uint net = ip & mask, bc = net | ~mask;
            for (uint h = net + 1; h < bc; h++)
            {
                var a = FromUInt(h);
                if (h != ip && !hosts.Any(x => x.Equals(a))) hosts.Add(a);
            }
        }
        if (hosts.Count == 0) { log("TCP-скан: нет подходящих подсетей."); return []; }
        log($"TCP-скан: {hosts.Count} адресов, порты {string.Join("/", ports)}.");

        using var cts = new CancellationTokenSource(budgetMs);
        try
        {
            Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 128, CancellationToken = cts.Token },
                async (host, ct) =>
                {
                    foreach (var port in ports)
                    {
                        if (await ConnectAsync(host, port, connectTimeoutMs, ct)) hits.Add((host, port));
                    }
                }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { log("TCP-скан: время вышло, беру найденное."); }
        return hits.OrderBy(h => ToUInt(h.Item1)).ThenBy(h => h.Item2).ToList();
    }

    private static async Task<bool> ConnectAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(ip, port, ct).AsTask();
            if (await Task.WhenAny(t, Task.Delay(timeoutMs, ct)) != t) return false;
            await t;
            return c.Connected;
        }
        catch { return false; }
    }

    private static uint ToUInt(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
