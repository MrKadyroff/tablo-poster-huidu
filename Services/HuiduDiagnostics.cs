using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Configuration;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Stand-alone, read-only diagnostic for Huidu full-color cards (e.g. BX A3L).
/// Run it with <c>LedImageUpdaterService.exe --huidu-diag</c>. It does NOT touch the
/// running service or change anything on the card except telling it which SDK server
/// to dial into (the same harmless command HDPlayer/HDSet uses). It then:
///
///   1. Searches for cards over UDP on EVERY local network interface (port 10001).
///   2. Asks each found card for its network config and its current "SDK server"
///      target — this reveals exactly how/where the card connects today.
///   3. Starts a temporary TCP SDK server, points the card at us, waits for it to dial
///      in, and on success reads back the file list already stored on the card.
///   4. Writes a timestamped report next to the exe (and opens it).
///
/// The goal is to "let the card explain itself" without Wireshark: the read-back tells
/// us whether connectivity + file transfer work, and what program/file format the card
/// (and HDPlayer) actually use.
/// </summary>
internal static class HuiduDiagnostics
{
    private const int UdpPort = 10001;
    private const int DeviceIdLen = 15;
    private const uint ProtocolVersion = 0x1000005;
    private const ushort CmdSearchAsk = 0x1001;
    private const ushort CmdSearchAnswer = 0x1002;
    private const ushort CmdSdkAsk = 0x2003;
    private const ushort CmdSdkAnswer = 0x2004;

    public static bool IsDiagInvocation(string[] args) =>
        args.Any(a => string.Equals(a, "--huidu-diag", StringComparison.OrdinalIgnoreCase));

    /// <summary>CLI entry point (<c>--huidu-diag</c>): runs in a console window.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        AllocConsole();
        var path = await RunToReportAsync(line =>
        {
            try { Console.WriteLine(line); } catch { /* no console */ }
        });

        Console.WriteLine();
        Console.WriteLine($"Отчёт сохранён: {path}");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* ignore */ }

        Console.WriteLine("Готово. Нажмите Enter, чтобы закрыть…");
        try { Console.ReadLine(); } catch { /* no console */ }
        return 0;
    }

    /// <summary>
    /// UI/programmatic entry point. Runs the full probe, saves the report, and returns
    /// the report file path. <paramref name="liveLog"/> (optional) receives each line as
    /// it is produced so a UI can show progress. Never throws.
    /// </summary>
    public static async Task<string> RunToReportAsync(Action<string>? liveLog = null)
    {
        var report = new Reporter(liveLog);
        try
        {
            await RunCoreAsync(Array.Empty<string>(), report);
        }
        catch (Exception ex)
        {
            report.Line($"FATAL: {ex}");
        }
        return report.Save();
    }

    private static async Task RunCoreAsync(string[] args, Reporter r)
    {
        var opts = LoadOptions(r);

        r.Header("HUIDU DIAGNOSTIC");
        r.Line($"Время:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        r.Line($"Файл отчёта:  {r.Path}");
        r.Line($"ListenPort:   {opts.ListenPort}  (TCP SDK server — куда должна звонить карта)");
        r.Line($"DeviceId:     {(string.IsNullOrWhiteSpace(opts.DeviceId) ? "(любой)" : opts.DeviceId)}");
        r.Line($"CardIp:       {(string.IsNullOrWhiteSpace(opts.CardIp) ? "(не задан)" : opts.CardIp)}");
        r.Line($"Screen:       {opts.ScreenWidth}x{opts.ScreenHeight}");

        // ── Phase 0: local network picture ────────────────────────────────────
        r.Header("СЕТЕВЫЕ ИНТЕРФЕЙСЫ ЭТОГО ПК");
        var locals = LocalIPv4Interfaces();
        if (locals.Count == 0)
            r.Line("⚠ Не найдено ни одного активного IPv4-интерфейса.");
        foreach (var (nic, ip, mask) in locals)
            r.Line($"  • {ip}/{MaskBits(mask)}  ({nic})  broadcast={Broadcast(ip, mask)}");

        // ── Phase 1: UDP discovery on every interface ─────────────────────────
        r.Header("ПОИСК КАРТ ПО UDP (порт 10001, все интерфейсы)");
        var found = SearchAllInterfaces(locals, opts.CardIp, r);
        if (found.Count == 0)
        {
            r.Line("✗ Карты не найдены по UDP-броадкасту.");
            r.Line("  Причины: карта в другой подсети/VLAN, отделена WiFi↔LAN, либо");
            r.Line("  файрвол блокирует UDP 10001. Если HDPlayer её видит — запускайте");
            r.Line("  этот диагностик на ТОМ ЖЕ ПК и в той же сети, где работает HDPlayer.");
        }
        else
        {
            r.Line($"✓ Найдено карт: {found.Count}");
            foreach (var d in found)
                r.Line($"  • id='{d.Id}'  ip={d.Ip}  (ответ на интерфейсе {d.ViaLocalIp})");
        }

        // If no card found by broadcast but an explicit CardIp is set, probe it directly.
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(opts.CardIp)
            && IPAddress.TryParse(opts.CardIp, out var manualIp))
        {
            r.Line($"  Пробуем напрямую заданный CardIp={manualIp} (unicast-запросы)…");
            found.Add(new Found(opts.DeviceId ?? "", manualIp, LocalIpToReach(manualIp)));
        }

        // ── Phase 2: interrogate each card over UDP ───────────────────────────
        foreach (var d in found)
        {
            r.Header($"ОПРОС КАРТЫ '{d.Id}' ({d.Ip}) ПО UDP");
            QueryAndReport(d, "GetEth0Info", "<sdk>\n    <in method=\"GetEth0Info\"/>\n</sdk>\n", r);
            QueryAndReport(d, "GetSDKTcpServer", "<sdk>\n    <in method=\"GetSDKTcpServer\"/>\n</sdk>\n", r);
            QueryAndReport(d, "GetNetworkInfo", "<sdk>\n    <in method=\"GetNetworkInfo\"/>\n</sdk>\n", r);
        }

        // ── Phase 3: start TCP SDK server and ask cards to dial in ────────────
        r.Header($"TCP SDK-СЕРВЕР (порт {opts.ListenPort}) — ждём, пока карта подключится");
        HuiduSdkServer server;
        try
        {
            server = HuiduSdkServer.GetOrCreate(opts.ListenPort, opts.IoTimeoutMs, r.Line);
        }
        catch (Exception ex)
        {
            r.Line($"✗ Не удалось поднять TCP-сервер на порту {opts.ListenPort}: {ex.Message}");
            r.Line("  Возможно, порт занят уже запущенным сервисом eCash Tablo — остановите его и повторите.");
            return;
        }

        // Tell every candidate (and the broadcast) to connect to us.
        foreach (var d in found)
        {
            var host = d.ViaLocalIp ?? LocalIpToReach(d.Ip);
            if (host is null) { r.Line($"  ⚠ Не удалось определить локальный IP для {d.Ip}."); continue; }
            SetSdkServer(d.Id, d.Ip, host, opts.ListenPort);
            r.Line($"  → Сказали карте '{d.Id}' ({d.Ip}) подключиться к {host}:{opts.ListenPort}.");
        }
        if (found.Count == 0)
        {
            // Last resort: broadcast the dial-in request on all broadcast-capable interfaces.
            foreach (var (_, ip, mask) in locals)
            {
                if (MaskBits(mask) >= 31) continue; // skip point-to-point (WireGuard etc.)
                SetSdkServerBroadcast(Broadcast(ip, mask), opts.DeviceId ?? "", ip, opts.ListenPort);
                r.Line($"  → Броадкаст dial-in на {Broadcast(ip, mask)} → {ip}:{opts.ListenPort}.");
            }
        }

        int waitSec = Math.Max(20, opts.ConnectWaitSeconds);
        r.Line($"  Ожидание подключения до {waitSec} c…");
        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        while (!server.HasSession && DateTime.UtcNow < deadline)
            await Task.Delay(300);

        if (!server.HasSession)
        {
            r.Line("✗ Карта не подключилась к TCP-серверу за отведённое время.");
            r.Line("  Файл на карту отправить нельзя, пока она не дозвонится сюда.");
            r.Line("  Проверьте: тот же ПК/подсеть что и HDPlayer; firewall на входящий TCP "
                   + $"{opts.ListenPort}; что карта реально получила SetSDKTcpServer (см. GetSDKTcpServer выше).");
            return;
        }

        r.Line($"✓ Карта подключилась. GUID={server.SessionGuid}");

        // ── Phase 4: read back what's on the card (proves transport works) ────
        r.Header("ЧТЕНИЕ СОДЕРЖИМОГО КАРТЫ (TCP read-back)");
        ReadBack(server, "GetFiles (файлы на карте)",
            g => $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<sdk guid=\"{g}\">\n    <in method=\"GetFiles\"/>\n</sdk>\n", r);
        ReadBack(server, "GetNetworkInfo",
            g => $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<sdk guid=\"{g}\">\n    <in method=\"GetNetworkInfo\"/>\n</sdk>\n", r);

        r.Header("ИТОГ");
        r.Line("✓ Подключение и обмен по TCP работают — карта дозвонилась и ответила.");
        r.Line("  Список файлов выше показывает, что карта уже хранит (включая залитое HDPlayer)");
        r.Line("  и в каком формате/с какими именами. Это и нужно, чтобы привести наш");
        r.Line("  program-template.xml к реальному формату A3L.");
    }

    // ─── Phase 4 helper ──────────────────────────────────────────────────────

    private static void ReadBack(HuiduSdkServer server, string label, Func<string, string> buildXml, Reporter r)
    {
        r.Line($"— {label}:");
        var answer = server.WithSession(client =>
        {
            client.SendXmlCmd(buildXml(client.Guid));
            return client.RecvXmlCmd();
        }, (string?)null);

        if (answer is null) { r.Line("  (нет активной сессии)"); return; }
        r.Line(Indent(PrettyXml(answer), "  "));
    }

    // ─── UDP discovery / query ───────────────────────────────────────────────

    private readonly record struct Found(string Id, IPAddress Ip, IPAddress? ViaLocalIp);

    private static List<Found> SearchAllInterfaces(
        List<(string nic, IPAddress ip, IPAddress mask)> locals, string? cardIp, Reporter r)
    {
        var found = new List<Found>();
        var seen = new HashSet<string>();

        foreach (var (nic, localIp, mask) in locals)
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Bind(new IPEndPoint(localIp, 0));
                udp.ReceiveTimeout = 300;

                var ask = new byte[6];
                WriteU32(ask, 0, ProtocolVersion);
                WriteU16(ask, 4, CmdSearchAsk);
                if (MaskBits(mask) < 31) // /31, /32 (e.g. WireGuard) have no usable broadcast
                    udp.SendTo(ask, new IPEndPoint(Broadcast(localIp, mask), UdpPort));
                udp.SendTo(ask, new IPEndPoint(IPAddress.Broadcast, UdpPort));
                if (!string.IsNullOrWhiteSpace(cardIp) && IPAddress.TryParse(cardIp, out var ci))
                    udp.SendTo(ask, new IPEndPoint(ci, UdpPort));

                var buf = new byte[2048];
                var deadline = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < deadline)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = udp.ReceiveFrom(buf, ref remote); }
                    catch (SocketException) { continue; }
                    if (n < 6 + DeviceIdLen) continue;
                    if (ReadU16(buf, 4) != CmdSearchAnswer) continue;

                    string id = ReadId(buf, 6);
                    var ip = ((IPEndPoint)remote).Address;
                    var key = id + "@" + ip;
                    if (id.Length == 0 || !seen.Add(key)) continue;
                    found.Add(new Found(id, ip, localIp));
                }
            }
            catch (Exception ex)
            {
                r.Line($"  ⚠ Интерфейс {localIp}: {ex.Message}");
            }
        }

        return found;
    }

    private static void QueryAndReport(Found d, string label, string xml, Reporter r)
    {
        r.Line($"— {label}:");
        var answer = UdpQuery(d.Id, d.Ip, d.ViaLocalIp, xml, 1500);
        if (answer is null) { r.Line("  (нет ответа)"); return; }
        r.Line(Indent(PrettyXml(answer), "  "));
    }

    private static string? UdpQuery(string deviceId, IPAddress ip, IPAddress? viaLocal, string xml, int timeoutMs)
    {
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.Bind(new IPEndPoint(viaLocal ?? IPAddress.Any, 0));
            udp.ReceiveTimeout = timeoutMs;

            udp.SendTo(BuildSdkPacket(deviceId, xml), new IPEndPoint(ip, UdpPort));

            var buf = new byte[9 * 1024];
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, ref remote); }
                catch (SocketException) { return null; }
                if (n < 6 + DeviceIdLen) continue;
                if (ReadU16(buf, 4) != CmdSdkAnswer) continue;
                return Encoding.UTF8.GetString(buf, 6 + DeviceIdLen, n - 6 - DeviceIdLen).TrimEnd('\0');
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static void SetSdkServer(string deviceId, IPAddress cardIp, IPAddress host, int port)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<sdk>\n    <in method=\"SetSDKTcpServer\">\n"
            + $"        <server host=\"{host}\" port=\"{port}\"/>\n    </in>\n</sdk>\n";
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.SendTo(BuildSdkPacket(deviceId, xml), new IPEndPoint(cardIp, UdpPort));
        }
        catch { /* ignore */ }
    }

    private static void SetSdkServerBroadcast(IPAddress broadcast, string deviceId, IPAddress host, int port)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<sdk>\n    <in method=\"SetSDKTcpServer\">\n"
            + $"        <server host=\"{host}\" port=\"{port}\"/>\n    </in>\n</sdk>\n";
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.SendTo(BuildSdkPacket(deviceId, xml), new IPEndPoint(broadcast, UdpPort));
        }
        catch { /* ignore */ }
    }

    private static byte[] BuildSdkPacket(string deviceId, string xml)
    {
        var xmlBytes = Encoding.UTF8.GetBytes(xml);
        var packet = new byte[6 + DeviceIdLen + xmlBytes.Length];
        WriteU32(packet, 0, ProtocolVersion);
        WriteU16(packet, 4, CmdSdkAsk);
        var id = Encoding.UTF8.GetBytes(deviceId ?? "");
        Array.Copy(id, 0, packet, 6, Math.Min(id.Length, DeviceIdLen));
        Array.Copy(xmlBytes, 0, packet, 6 + DeviceIdLen, xmlBytes.Length);
        return packet;
    }

    // ─── Config / network helpers ────────────────────────────────────────────

    private static HuiduOptions LoadOptions(Reporter r)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var cfgBuilder = new ConfigurationBuilder()
                .SetBasePath(baseDir)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

            // Overlay the active point config so per-point HuiduLed values are picked up.
            var rootCfg = cfgBuilder.Build();
            var activePoint = rootCfg["ActivePointId"];
            if (!string.IsNullOrWhiteSpace(activePoint))
            {
                var pointPath = Path.Combine(baseDir, "config", "points", $"{activePoint}.json");
                if (File.Exists(pointPath))
                    cfgBuilder.AddJsonFile(pointPath, optional: true, reloadOnChange: false);
            }

            var cfg = cfgBuilder.Build();
            var opts = cfg.GetSection(HuiduOptions.SectionName).Get<HuiduOptions>() ?? new HuiduOptions();
            r.Line($"(конфиг прочитан из {baseDir}, активная точка: {activePoint ?? "—"})");
            return opts;
        }
        catch (Exception ex)
        {
            r.Line($"(не удалось прочитать конфиг — беру значения по умолчанию: {ex.Message})");
            return new HuiduOptions();
        }
    }

    private static List<(string nic, IPAddress ip, IPAddress mask)> LocalIPv4Interfaces()
    {
        var list = new List<(string, IPAddress, IPAddress)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null || Equals(ua.IPv4Mask, IPAddress.Any)) continue;
                list.Add((ni.Name, ua.Address, ua.IPv4Mask));
            }
        }
        return list;
    }

    private static IPAddress Broadcast(IPAddress ip, IPAddress mask)
    {
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    private static int MaskBits(IPAddress mask)
    {
        int bits = 0;
        foreach (var b in mask.GetAddressBytes())
            for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) bits++;
        return bits;
    }

    private static IPAddress? LocalIpToReach(IPAddress target)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, UdpPort);
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { return null; }
    }

    // ─── byte / xml utilities ────────────────────────────────────────────────

    private static void WriteU16(byte[] d, int i, ushort v) { d[i] = (byte)(v & 0xff); d[i + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] d, int i, uint v)
    {
        d[i] = (byte)(v & 0xff); d[i + 1] = (byte)((v >> 8) & 0xff);
        d[i + 2] = (byte)((v >> 16) & 0xff); d[i + 3] = (byte)((v >> 24) & 0xff);
    }
    private static ushort ReadU16(byte[] d, int i) => (ushort)(d[i] | (d[i + 1] << 8));
    private static string ReadId(byte[] d, int off)
    {
        string id = Encoding.UTF8.GetString(d, off, DeviceIdLen);
        int z = id.IndexOf('\0');
        if (z >= 0) id = id[..z];
        return id.Trim();
    }

    private static string PrettyXml(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var sb = new StringBuilder();
            using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true });
            doc.Save(w);
            return sb.ToString();
        }
        catch { return xml.Trim(); }
    }

    private static string Indent(string text, string pad) =>
        string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => pad + l));

    // ─── console + report ────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    private sealed class Reporter
    {
        private readonly Action<string>? _sink;
        private readonly StreamWriter? _writer;

        public string Path { get; }

        public Reporter(Action<string>? sink = null)
        {
            _sink = sink;

            // Stream straight to a file from the start so a partial report survives
            // even if the process is interrupted mid-probe. Prefer the Desktop (easy to
            // find and send), then the exe folder, then the temp folder.
            var name = $"huidu-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            foreach (var dir in CandidateDirs())
            {
                try
                {
                    var p = System.IO.Path.Combine(dir, name);
                    _writer = new StreamWriter(p, append: false, new UTF8Encoding(true)) { AutoFlush = true };
                    Path = p;
                    return;
                }
                catch { /* try next location */ }
            }

            // Last resort: no file writer (UI/console still get lines via the sink).
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        }

        private static IEnumerable<string> CandidateDirs()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop)) yield return desktop;
            yield return AppContext.BaseDirectory;
            yield return System.IO.Path.GetTempPath();
        }

        public void Line(string s)
        {
            try { _writer?.WriteLine(s); } catch { /* disk error must not break the probe */ }
            try { _sink?.Invoke(s); } catch { /* sink must never break the probe */ }
        }

        public void Header(string title)
        {
            Line("");
            Line(new string('═', 70));
            Line("  " + title);
            Line(new string('═', 70));
        }

        public string Save()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* ignore */ }
            return Path;
        }
    }
}
