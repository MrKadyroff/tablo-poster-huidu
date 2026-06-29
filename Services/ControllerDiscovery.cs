using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Finds the LED controller's reachable IP automatically:
///
///   Phase 1 — probe all known candidates (from screen.xml + network.json) in parallel.
///             Typically completes in &lt;1.5 s.
///
///   Phase 2 — if Phase 1 fails, scan every host on every local /24 subnet the PC
///             is currently on (e.g. 192.168.11.0/24). Skips the PC's own address.
///             With 254 hosts × 1 s timeout all in parallel, completes in ~1–2 s.
///
/// The resolved address is cached for 5 minutes; call Reset() to force re-discovery.
/// </summary>
public sealed class ControllerDiscovery
{
    private readonly ILogger<ControllerDiscovery> _logger;

    private string? _cachedIp;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan CacheTtl       = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KnownTimeout   = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ScanTimeout    = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan Phase1Budget   = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Phase2Budget   = TimeSpan.FromSeconds(10);

    public ControllerDiscovery(ILogger<ControllerDiscovery> logger)
    {
        _logger = logger;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<string> ResolveAsync(
        IReadOnlyList<string> candidates, int port, CancellationToken ct)
    {
        // Fast path — no lock needed for cache read
        if (_cachedIp is not null && DateTime.UtcNow < _cacheExpiry)
        {
            _logger.LogDebug("Контроллер (кэш): {Ip}:{Port}", _cachedIp, port);
            return _cachedIp;
        }

        // Serialise discovery so two concurrent callers don't both scan
        await _lock.WaitAsync(ct);
        try
        {
            // Re-check cache after acquiring lock (another caller may have just resolved it)
            if (_cachedIp is not null && DateTime.UtcNow < _cacheExpiry)
            {
                _logger.LogDebug("Контроллер (кэш, после блокировки): {Ip}:{Port}", _cachedIp, port);
                return _cachedIp;
            }

            return await ResolveInternalAsync(candidates, port, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> ResolveInternalAsync(
        IReadOnlyList<string> candidates, int port, CancellationToken ct)
    {
        var known = candidates
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct()
            .ToList();

        if (known.Count > 0)
        {
            _logger.LogInformation(
                "Контроллер: проверяю известные IP [{IPs}] на порту {Port}...",
                string.Join(", ", known), port);

            using var p1Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            p1Cts.CancelAfter(Phase1Budget);

            var found = await ProbeAllAsync(known, port, KnownTimeout, p1Cts.Token);
            if (found is not null)
                return Cache(found, port);
        }

        // ── Phase 2: scan all local /24 subnets ───────────────────────────────
        var subnets = GetLocalSubnets();
        if (subnets.Count == 0)
            throw new InvalidOperationException(
                "Контроллер не найден на известных IP и нет локальных сетевых интерфейсов для сканирования.");

        _logger.LogInformation(
            "Контроллер не отвечает на известных IP. Сканирую подсети: {Subnets} (порт {Port})...",
            string.Join(", ", subnets.Select(s => s.Item1 + "/24")), port);

        using var p2Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        p2Cts.CancelAfter(Phase2Budget);

        foreach (var (subnet, ownIp) in subnets)
        {
            var hostsToScan = Enumerable.Range(1, 254)
                .Select(i => $"{subnet}.{i}")
                .Where(ip => ip != ownIp)
                .ToList();

            var result = await ProbeAllAsync(hostsToScan, port, ScanTimeout, p2Cts.Token);
            if (result is not null)
                return Cache(result, port);
        }

        throw new InvalidOperationException(
            $"Контроллер не найден ни на одном IP. Проверьте:\n" +
            $"  • ПК подключён к сети контроллера (Wi-Fi или LAN)\n" +
            $"  • Контроллер включён\n" +
            $"  • FTP-порт {port} не заблокирован\n" +
            $"  Проверенные известные IP: [{string.Join(", ", known)}]\n" +
            $"  Просканированные подсети: [{string.Join(", ", subnets.Select(s => s.Item1 + ".0/24"))}]");
    }

    public void Reset()
    {
        _cachedIp = null;
        _cacheExpiry = DateTime.MinValue;
        _logger.LogDebug("Кэш IP контроллера сброшен — следующий цикл проведёт повторный поиск");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string Cache(string ip, int port)
    {
        _logger.LogInformation("Контроллер найден: {Ip}:{Port} (кэш 5 мин)", ip, port);
        _cachedIp = ip;
        _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
        return ip;
    }

    /// <summary>
    /// Returns (subnet prefix like "192.168.11", own IP string) for each active
    /// IPv4 unicast address whose prefix length is 24 (or whose mask is 255.255.255.x).
    /// </summary>
    private static List<(string Subnet, string OwnIp)> GetLocalSubnets()
    {
        var result = new List<(string, string)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                        or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = addr.Address.ToString();
                var parts = ip.Split('.');
                if (parts.Length != 4) continue;

                // Accept /24 or any mask whose first 3 octets are 255
                var mask = addr.IPv4Mask.ToString();
                var maskParts = mask.Split('.');
                if (maskParts.Length != 4) continue;
                if (maskParts[0] != "255" || maskParts[1] != "255" || maskParts[2] != "255") continue;

                var subnet = $"{parts[0]}.{parts[1]}.{parts[2]}";
                if (!result.Any(r => r.Item1 == subnet))
                    result.Add((subnet, ip));
            }
        }
        return result;
    }

    private static async Task<string?> ProbeAllAsync(
        List<string> ips, int port, TimeSpan perProbeTimeout, CancellationToken ct)
    {
        using var winnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = ips
            .Select(ip => ProbeAsync(ip, port, perProbeTimeout, winnerCts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            try
            {
                var result = await done;
                if (result is not null)
                {
                    winnerCts.Cancel();
                    return result;
                }
            }
            catch { /* faulted probe — continue */ }
        }

        return null;
    }

    private static async Task<string?> ProbeAsync(
        string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var probeCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);
            await tcp.ConnectAsync(ip, port, linked.Token);
            return ip;
        }
        catch
        {
            return null;
        }
    }
}
