using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Clean port of the official Huidu full-color SDK TCP protocol
/// (github.com/huidutech/sdk → fullcolor/demo/csharp/RemoteServer).
///
/// Connection model is "server mode": the service listens on a TCP port, the card
/// dials in (configured via HDPlayer / HDSet "SDK server"), and the service drives
/// the exchange — version handshake, GUID read, XML commands and file transfer.
///
/// Wire format (all integers little-endian):
///   • Every packet is length-prefixed: first ushort = total packet byte length.
///   • XML command packet:  [len u16][cmd u16=0x2003][total u32][index u32][utf8 xml chunk]
///   • File start ask:      [len u16][cmd u16=0x8001][md5 33 bytes][size i64][type i16][name…][0]
///   • File content ask:    [len u16][cmd u16=0x8003][raw bytes]
///   • File end ask:        [len u16][cmd u16=0x8005]
/// </summary>
internal sealed class HuiduSdkClient
{
    public const int MaxTcpPacket = 9 * 1024;
    public const uint TcpVersion = 0x1000005;

    private enum Cmd : ushort
    {
        ServiceAsk = 0x2001,
        ServiceAnswer = 0x2002,
        CmdAsk = 0x2003,
        CmdAnswer = 0x2004,
        FileStartAsk = 0x8001,
        FileStartAnswer = 0x8002,
        FileContentAsk = 0x8003,
        FileContentAnswer = 0x8004,
        FileEndAsk = 0x8005,
        FileEndAnswer = 0x8006,
    }

    public enum HFileType : short
    {
        Image = 0,
        Video = 1,
        Font = 2,
        Firmware = 3,
        FpgaConfig = 4,
        SettingConfig = 5,
    }

    private readonly Socket _socket;
    private readonly byte[] _send = new byte[MaxTcpPacket];

    public string Guid { get; private set; } = "";

    public HuiduSdkClient(Socket socket, int ioTimeoutMs)
    {
        _socket = socket;
        _socket.ReceiveTimeout = ioTimeoutMs;
        _socket.SendTimeout = ioTimeoutMs;
    }

    // ─── Handshake ───────────────────────────────────────────────────────────

    /// <summary>Performs version handshake and reads the card GUID. Returns false on failure.</summary>
    public bool Handshake()
    {
        // 1) Version ask (8-byte struct: len u16, cmd u16, version u32)
        int index = 0;
        SetShort(_send, ref index, 8);
        SetShort(_send, ref index, (ushort)Cmd.ServiceAsk);
        SetInt(_send, ref index, (int)TcpVersion);
        SendRaw(_send, 8);

        // 2) Version answer (10 bytes) — content not needed beyond a sane length.
        var answer = ReadPacket();
        if (answer.Length < 10) return false;

        // 3) GetIFVersion — guid still unknown, sent literally as "##GUID" like the SDK demo.
        string getVer =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<sdk guid=\"##GUID\">\n"
            + "    <in method=\"GetIFVersion\">\n"
            + "        <version value=\"1000000\"/>\n"
            + "    </in>\n"
            + "</sdk>\n";
        SendXmlCmd(getVer);

        // 4) Parse the card's real GUID from the answer.
        try
        {
            var xml = RecvXmlCmd();
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var sdk = doc.SelectSingleNode("sdk");
            Guid = sdk?.Attributes?["guid"]?.InnerText ?? "";
            return !string.IsNullOrEmpty(Guid);
        }
        catch
        {
            return false;
        }
    }

    // ─── XML commands ────────────────────────────────────────────────────────

    public void SendXmlCmd(string xml)
    {
        byte[] data = Encoding.UTF8.GetBytes(xml);
        int len = data.Length;
        int validLen = MaxTcpPacket - 12;
        int packets = (len + validLen - 1) / validLen;
        int dataLeft = len;

        for (int i = 0; i < packets; i++)
        {
            int packetLen = dataLeft > validLen ? validLen : dataLeft;
            int dataIndex = len - dataLeft;
            Array.Copy(data, dataIndex, _send, 12, packetLen);
            dataLeft -= packetLen;
            int total = packetLen + 12;

            int index = 0;
            SetShort(_send, ref index, (ushort)total);
            SetShort(_send, ref index, (ushort)Cmd.CmdAsk);
            SetInt(_send, ref index, len);
            SetInt(_send, ref index, dataIndex);
            SendRaw(_send, total);
        }
    }

    public string RecvXmlCmd()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var packet = ReadPacket();
            if (packet.Length < 12)
                throw new IOException("Huidu: malformed XML answer packet.");

            int idx = 4;
            int total = GetInt(packet, ref idx);
            sb.Append(Encoding.UTF8.GetString(packet, 12, packet.Length - 12));
            if (sb.Length >= total) break;
        }
        return sb.ToString();
    }

    /// <summary>Sends an XML command and returns true when the answer contains result="kSuccess".</summary>
    public bool SendXmlCmdExpectSuccess(string xml, out string answer)
    {
        SendXmlCmd(xml);
        answer = RecvXmlCmd();
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(answer);
            foreach (XmlNode node in doc.SelectNodes("sdk/out")!)
            {
                var result = node.Attributes?["result"]?.InnerText;
                if (!string.Equals(result, "kSuccess", StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─── File transfer ───────────────────────────────────────────────────────

    /// <summary>Uploads a single file to the card. Returns false on any protocol failure.</summary>
    public bool SendFile(string path, HFileType type)
    {
        var info = new FileInfo(path);
        string name = Path.GetFileName(path);
        string md5 = ComputeMd5Hex(path);
        long size = info.Length;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // File start ask
        int index = 2;
        SetShort(_send, ref index, (ushort)Cmd.FileStartAsk);
        SetString(_send, ref index, md5, 33);
        _send[index - 1] = 0; // null-terminate md5 field
        SetLong(_send, ref index, size);
        SetShort(_send, ref index, (ushort)type);
        SetString(_send, ref index, name);
        int startLen = index + 1;
        _send[startLen - 1] = 0; // null-terminate the name field (buffer is reused)
        int zero = 0;
        SetShort(_send, ref zero, (ushort)startLen);
        SendRaw(_send, startLen);

        // File start answer: [len u16][cmd u16][status i16][existSize i64]
        var ans = ReadPacket();
        if (ans.Length < 14) return false;
        int ai = 4;
        short status = GetShort(ans, ref ai);
        long existSize = GetLong(ans, ref ai);
        if (status != 0) return false;
        if (existSize == size) { SendFileEnd(); return RecvFileEndOk(); } // already present
        fs.Seek(existSize, SeekOrigin.Begin);

        // File content ask — stream the remaining bytes in chunks.
        int chunk = MaxTcpPacket - 4;
        while (true)
        {
            int reads = fs.Read(_send, 4, chunk);
            if (reads <= 0) break;
            int ci = 0;
            int len = 4 + reads;
            SetShort(_send, ref ci, (ushort)len);
            SetShort(_send, ref ci, (ushort)Cmd.FileContentAsk);
            SendRaw(_send, len);
        }

        SendFileEnd();
        return RecvFileEndOk();
    }

    private void SendFileEnd()
    {
        int index = 0;
        SetShort(_send, ref index, 4);
        SetShort(_send, ref index, (ushort)Cmd.FileEndAsk);
        SendRaw(_send, 4);
    }

    private bool RecvFileEndOk()
    {
        var ans = ReadPacket();
        if (ans.Length < 6) return false;
        int ai = 4;
        short status = GetShort(ans, ref ai);
        return status == 0;
    }

    // ─── Low-level framed socket I/O ─────────────────────────────────────────

    private void SendRaw(byte[] buffer, int len)
    {
        int sent = 0;
        while (sent < len)
            sent += _socket.Send(buffer, sent, len - sent, SocketFlags.None);
    }

    /// <summary>Reads one length-prefixed packet (first u16 = total byte length).</summary>
    private byte[] ReadPacket()
    {
        var header = ReadExact(2);
        int total = header[0] | (header[1] << 8);
        if (total < 2 || total > MaxTcpPacket)
            throw new IOException($"Huidu: invalid packet length {total}.");

        var packet = new byte[total];
        packet[0] = header[0];
        packet[1] = header[1];
        if (total > 2)
        {
            var body = ReadExact(total - 2);
            Array.Copy(body, 0, packet, 2, total - 2);
        }
        return packet;
    }

    private byte[] ReadExact(int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _socket.Receive(buf, read, count - read, SocketFlags.None);
            if (n <= 0) throw new IOException("Huidu: connection closed by card.");
            read += n;
        }
        return buf;
    }

    private static string ComputeMd5Hex(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    // ─── Little-endian primitives (mirror SDK Tools.cs) ──────────────────────

    private static void SetShort(byte[] d, ref int i, ushort v)
    {
        d[i] = (byte)(v & 0xff);
        d[i + 1] = (byte)((v >> 8) & 0xff);
        i += 2;
    }

    private static void SetInt(byte[] d, ref int i, int v)
    {
        d[i] = (byte)(v & 0xff);
        d[i + 1] = (byte)((v >> 8) & 0xff);
        d[i + 2] = (byte)((v >> 16) & 0xff);
        d[i + 3] = (byte)((v >> 24) & 0xff);
        i += 4;
    }

    private static void SetLong(byte[] d, ref int i, long v)
    {
        ulong u = (ulong)v;
        for (int k = 0; k < 8; k++) d[i + k] = (byte)((u >> (8 * k)) & 0xff);
        i += 8;
    }

    private static void SetString(byte[] d, ref int i, string s, int len = 0)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Buffer.BlockCopy(bytes, 0, d, i, bytes.Length);
        i += len == 0 ? bytes.Length : len;
    }

    private static short GetShort(byte[] d, ref int i)
    {
        short v = (short)(d[i] | (d[i + 1] << 8));
        i += 2;
        return v;
    }

    private static int GetInt(byte[] d, ref int i)
    {
        int v = d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24);
        i += 4;
        return v;
    }

    private static long GetLong(byte[] d, ref int i)
    {
        long v = 0;
        for (int k = 0; k < 8; k++) v |= (long)d[i + k] << (8 * k);
        i += 8;
        return v;
    }
}

/// <summary>
/// UDP auto-configuration (port 10001) — ported from the official SDK LocalClient.
/// Broadcasts a device search, then tells the card(s) to dial into our SDK TCP server
/// via the <c>SetSDKTcpServer</c> command. This removes the manual "set SDK server on
/// the card in HDPlayer/HDSet" step: the card connects to us on its own.
/// </summary>
internal static class HuiduUdpConfig
{
    private const int UdpPort = 10001;
    private const int DeviceIdLen = 15;
    private const uint ProtocolVersion = 0x1000005;

    private const ushort CmdSearchAsk = 0x1001;
    private const ushort CmdSearchAnswer = 0x1002;
    private const ushort CmdSdkAsk = 0x2003;

    public readonly record struct Device(string Id, IPAddress Ip);

    /// <summary>
    /// Finds cards on the LAN and points each (optionally filtered by id) at host:listenPort.
    /// If <paramref name="knownCardIp"/> is set, also sends the command unicast to that IP
    /// directly — useful when broadcast doesn't cross subnet/VLAN boundaries.
    /// Returns the number of cards configured. Safe to call repeatedly.
    /// </summary>
    public static int EnsureDialIn(string? deviceIdFilter, int listenPort, Action<string> log,
        string? knownCardIp = null)
    {
        try
        {
            int configured = 0;

            // ── Direct unicast to known IP (bypasses broadcast domain limits) ──
            if (!string.IsNullOrWhiteSpace(knownCardIp) &&
                IPAddress.TryParse(knownCardIp, out var directIp))
            {
                var host = LocalIpToReach(directIp);
                if (host is not null)
                {
                    var dev = new Device(deviceIdFilter ?? "", directIp);
                    SetSdkServer(dev, host, listenPort);
                    log($"[Huidu] Unicast: told card at {directIp} to connect to SDK server {host}:{listenPort}.");
                    configured++;
                }
                else
                {
                    log($"[Huidu] Unicast: could not determine local IP to reach {directIp}.");
                }
            }

            // ── Broadcast discovery (finds cards without known IP) ────────────
            var devices = Search(800, log);
            if (devices.Count == 0)
            {
                if (configured == 0)
                    log("[Huidu] UDP search found no cards on the LAN (port 10001).");
            }
            else
            {
                foreach (var dev in devices)
                {
                    if (!string.IsNullOrWhiteSpace(deviceIdFilter) &&
                        !string.Equals(dev.Id, deviceIdFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var host = LocalIpToReach(dev.Ip);
                    if (host is null) continue;

                    SetSdkServer(dev, host, listenPort);
                    log($"[Huidu] Told card {dev.Id} ({dev.Ip}) to connect to SDK server {host}:{listenPort}.");
                    configured++;
                }

                if (configured == 0)
                    log($"[Huidu] UDP search saw {devices.Count} card(s) but none matched filter '{deviceIdFilter}'.");
            }

            return configured;
        }
        catch (Exception ex)
        {
            log($"[Huidu] UDP auto-config error: {ex.Message}");
            return 0;
        }
    }

    public static List<Device> Search(int timeoutMs, Action<string> log)
    {
        var found = new List<Device>();
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.ReceiveTimeout = 300;

        // Search ask: [version u32][cmd u16] = 6 bytes
        var ask = new byte[6];
        ask[0] = (byte)(ProtocolVersion & 0xff);
        ask[1] = (byte)((ProtocolVersion >> 8) & 0xff);
        ask[2] = (byte)((ProtocolVersion >> 16) & 0xff);
        ask[3] = (byte)((ProtocolVersion >> 24) & 0xff);
        ask[4] = (byte)(CmdSearchAsk & 0xff);
        ask[5] = (byte)((CmdSearchAsk >> 8) & 0xff);
        udp.SendTo(ask, new IPEndPoint(IPAddress.Broadcast, UdpPort));

        var buf = new byte[2048];
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = udp.ReceiveFrom(buf, ref remote); }
            catch (SocketException) { continue; } // timeout tick

            if (n < 6 + DeviceIdLen) continue;
            ushort cmd = (ushort)(buf[4] | (buf[5] << 8));
            if (cmd != CmdSearchAnswer) continue;

            string id = Encoding.UTF8.GetString(buf, 6, DeviceIdLen);
            int z = id.IndexOf('\0');
            if (z >= 0) id = id[..z];
            id = id.Trim();

            var ip = ((IPEndPoint)remote).Address;
            if (!string.IsNullOrEmpty(id) && !found.Any(d => d.Id == id))
                found.Add(new Device(id, ip));
        }

        return found;
    }

    private static void SetSdkServer(Device dev, IPAddress host, int port)
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<sdk>\n"
            + "    <in method=\"SetSDKTcpServer\">\n"
            + $"        <server host=\"{host}\" port=\"{port}\"/>\n"
            + "    </in>\n"
            + "</sdk>\n";

        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
        var packet = new byte[6 + DeviceIdLen + xmlBytes.Length];
        packet[0] = (byte)(ProtocolVersion & 0xff);
        packet[1] = (byte)((ProtocolVersion >> 8) & 0xff);
        packet[2] = (byte)((ProtocolVersion >> 16) & 0xff);
        packet[3] = (byte)((ProtocolVersion >> 24) & 0xff);
        packet[4] = (byte)(CmdSdkAsk & 0xff);
        packet[5] = (byte)((CmdSdkAsk >> 8) & 0xff);
        var idBytes = Encoding.UTF8.GetBytes(dev.Id);
        Array.Copy(idBytes, 0, packet, 6, Math.Min(idBytes.Length, DeviceIdLen));
        Array.Copy(xmlBytes, 0, packet, 6 + DeviceIdLen, xmlBytes.Length);

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.SendTo(packet, new IPEndPoint(dev.Ip, UdpPort));
        udp.SendTo(packet, new IPEndPoint(IPAddress.Broadcast, UdpPort)); // belt-and-suspenders
    }

    private static IPAddress? LocalIpToReach(IPAddress target)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, UdpPort);
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Owns the TCP listener that Huidu cards dial into (SDK server mode) and exposes the
/// single most-recently-connected card session. One instance per process (the tray app
/// rebuilds the host on Restart; reusing the listener avoids "port already in use").
/// </summary>
internal sealed class HuiduSdkServer : IDisposable
{
    private static readonly object _gate = new();
    private static HuiduSdkServer? _shared;

    public static HuiduSdkServer GetOrCreate(int port, int ioTimeoutMs, Action<string> log)
    {
        lock (_gate)
        {
            if (_shared is not null && _shared._port == port)
                return _shared;

            _shared?.Dispose();
            _shared = new HuiduSdkServer(port, ioTimeoutMs, log);
            _shared.Start();
            return _shared;
        }
    }

    private readonly int _port;
    private readonly int _ioTimeoutMs;
    private readonly Action<string> _log;
    private readonly object _sessionLock = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running;
    private HuiduSdkClient? _session;

    private HuiduSdkServer(int port, int ioTimeoutMs, Action<string> log)
    {
        _port = port;
        _ioTimeoutMs = ioTimeoutMs;
        _log = log;
    }

    public int Port => _port;

    public bool HasSession
    {
        get { lock (_sessionLock) return _session is not null; }
    }

    public string? SessionGuid
    {
        get { lock (_sessionLock) return _session?.Guid; }
    }

    private void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HuiduSdkAccept" };
            _acceptThread.Start();
            _log($"[Huidu] SDK server listening on TCP {_port}. Waiting for card to connect…");
        }
        catch (Exception ex)
        {
            _log($"[Huidu] Failed to start SDK server on port {_port}: {ex.Message}");
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            Socket socket;
            try
            {
                socket = _listener!.AcceptSocket();
            }
            catch when (!_running)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[Huidu] Accept error: {ex.Message}");
                continue;
            }

            try
            {
                var client = new HuiduSdkClient(socket, _ioTimeoutMs);
                if (client.Handshake())
                {
                    lock (_sessionLock) _session = client;
                    _log($"[Huidu] Card connected. GUID={client.Guid} from {socket.RemoteEndPoint}.");
                }
                else
                {
                    _log("[Huidu] Card connected but handshake failed; dropping.");
                    try { socket.Close(); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _log($"[Huidu] Handshake error: {ex.Message}");
                try { socket.Close(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> against the connected card session under a lock.
    /// On any socket/IO error the session is dropped so the card can reconnect.
    /// </summary>
    public T WithSession<T>(Func<HuiduSdkClient, T> action, T whenNoSession)
    {
        lock (_sessionLock)
        {
            if (_session is null) return whenNoSession;
            try
            {
                return action(_session);
            }
            catch (Exception ex)
            {
                _log($"[Huidu] Session error, dropping connection: {ex.Message}");
                _session = null;
                return whenNoSession;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { /* ignore */ }
        lock (_sessionLock) _session = null;
    }
}
