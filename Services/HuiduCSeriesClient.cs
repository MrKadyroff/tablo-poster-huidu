using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Client for the Huidu <b>C-series</b> ("C10L"…"C35L", e.g. <c>C16L</c>) HDPlayer protocol,
/// reverse-engineered from an HDPlayer 7.11.8 ⇄ C16L packet capture (<c>newLog.txt</c>).
///
/// This is a DIFFERENT protocol from the A-series JSON "PlayTask" one in
/// <see cref="HuiduHdPlayerClient"/>:
///   • The PC is the TCP client and connects to the card at <c>cardIp:9527</c>
///     (C-series serves both UDP discovery and TCP on 9527; A-series uses 10001).
///   • Messages are a flat binary opcode frame, not JSON:
///       <c>[totalLen u16 LE][cmd u16 LE][body…]</c>  (totalLen includes the 4-byte header)
///     The card answers every request <c>cmd</c> with <c>cmd + 1</c>.
///   • Instead of a JSON program the PC uploads an XML scene description (a "<c>.boo</c>"
///     file) plus the image. Both files are stored on the card under <c>&lt;md5&gt;.&lt;ext&gt;</c>;
///     the XML references the image by its md5.
///
/// Send sequence (opcodes, request → response):
///   0x000b connect      body 09 00 00 01        → 0x000c (echo)
///   0x0730 session-begin body 02 00 00 00 00…    → 0x0731 (status u16)
///   0x0410 client-info  body "Windows,HDPlayer,…\0" → 0x0411 (status u16)
///   0x000d              (empty)                  → 0x000e
///   0x040a              (empty)                  → 0x040b
///   0x000f transfer-begin body [totalSize u32][0 u32] → 0x0010
///   0x0011 query-files  (empty, sent twice)      → 0x0012 (md5 list already on card)
///   0x0013              (empty)                  → 0x0014
///   0x0015              body 00 00 00 00 01 00 00 00 → 0x0016
///   per file:
///     0x0017 file-start body "&lt;md5&gt;.&lt;ext&gt;\0" → 0x0018 (existingSize u32)
///     0x0019 file-data  body [raw chunk]          → 0x001a   (one ack per chunk)
///     0x001b file-end   (empty)                  → 0x001c
///   0x001d              (empty)                  → 0x001e
///   0x001f commit       (empty)                  → 0x0020
/// </summary>
internal sealed class HuiduCSeriesClient : IDisposable
{
    /// <summary>C-series cards serve the HDPlayer protocol on UDP/TCP 9527.</summary>
    public const int DefaultPort = 9527;
    private const int FileChunk = 9212; // matches the capture (frame body of a 9216-byte frame)

    private static readonly byte[] ConnectBody = { 0x09, 0x00, 0x00, 0x01 };

    private readonly Socket _socket;
    private readonly Action<string> _log;

    private HuiduCSeriesClient(Socket socket, Action<string> log)
    {
        _socket = socket;
        _log = log;
    }

    public static HuiduCSeriesClient Connect(string cardIp, int port, int ioTimeoutMs, Action<string> log)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveTimeout = ioTimeoutMs,
            SendTimeout = ioTimeoutMs,
            NoDelay = true,
        };

        var ar = socket.BeginConnect(IPAddress.Parse(cardIp), port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(ioTimeoutMs))
        {
            socket.Close();
            throw new SocketException((int)SocketError.TimedOut);
        }
        socket.EndConnect(ar);

        var client = new HuiduCSeriesClient(socket, log);
        client.Handshake();
        return client;
    }

    /// <summary>
    /// Connect + session-begin + client-info handshake. The card speaks first only after
    /// it receives 0x000b, so (unlike the A-series client) there is no greeting to discard.
    /// </summary>
    private void Handshake()
    {
        Exchange(0x000b, ConnectBody, 0x000c);
        Exchange(0x0730, new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, 0x0731);
        Exchange(0x0410, BuildCString(BuildClientInfo()), 0x0411);
        Exchange(0x000d, Array.Empty<byte>(), 0x000e);
        Exchange(0x040a, Array.Empty<byte>(), 0x040b);
    }

    // ─── Show one full-screen image ──────────────────────────────────────────

    /// <summary>
    /// Uploads the image and a generated XML scene that displays it full-screen, then commits.
    /// Returns (success, detail).
    /// </summary>
    public (bool ok, string detail) SendFullScreenImage(
        string imagePath, int screenWidth, int screenHeight, string model, string? cardId)
    {
        var imageBytes = File.ReadAllBytes(imagePath);
        string imageExt = NormalizeExt(Path.GetExtension(imagePath));
        string imageMd5 = Md5Hex(imageBytes);
        string imageName = imageMd5 + "." + imageExt;

        string xml = BuildSceneXml(screenWidth, screenHeight, model, cardId, imageMd5, imageExt,
            Path.GetFileNameWithoutExtension(imagePath));
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
        string xmlMd5 = Md5Hex(xmlBytes);
        string xmlName = xmlMd5 + ".boo";

        long totalSize = imageBytes.Length + xmlBytes.LongLength;

        // transfer-begin: announce the total payload size.
        Exchange(0x000f, BuildTransferBegin(totalSize), 0x0010);

        // query files already on the card (the card replies with an md5 list; we don't
        // need to act on it — per-file 0x0017 tells us the existing size). Sent twice,
        // matching HDPlayer.
        Exchange(0x0011, Array.Empty<byte>(), 0x0012);
        Exchange(0x0011, Array.Empty<byte>(), 0x0012);

        Exchange(0x0013, Array.Empty<byte>(), 0x0014);
        Exchange(0x0015, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }, 0x0016);

        // Upload the image first, then the scene that references it.
        UploadFile(imageName, imageBytes);
        UploadFile(xmlName, xmlBytes);

        Exchange(0x001d, Array.Empty<byte>(), 0x001e);
        Exchange(0x001f, Array.Empty<byte>(), 0x0020);

        return (true, "ok");
    }

    private void UploadFile(string name, byte[] data)
    {
        var startResp = Exchange(0x0017, BuildCString(name), 0x0018);
        long existing = startResp.Length >= 4 ? ReadU32(startResp, 0) : 0;

        if (existing < data.LongLength)
        {
            _log($"[Huidu] Uploading '{name}' from offset {existing} ({data.LongLength - existing} bytes)…");
            for (long off = existing; off < data.LongLength; off += FileChunk)
            {
                int len = (int)Math.Min(FileChunk, data.LongLength - off);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, (int)off, chunk, 0, len);
                Exchange(0x0019, chunk, 0x001a);
            }
        }
        else
        {
            _log($"[Huidu] Card already has '{name}' (size match); skipping upload.");
        }

        Exchange(0x001b, Array.Empty<byte>(), 0x001c);
    }

    // ─── Scene XML (.boo) ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the HDPlayer scene XML for a single full-screen photo, mirroring the structure
    /// produced by HDPlayer 7.11.8 for a C16L. The image is referenced by its md5; the card
    /// stores it as <c>&lt;md5&gt;.&lt;ext&gt;</c>.
    /// </summary>
    private static string BuildSceneXml(
        int width, int height, string model, string? cardId,
        string imageMd5, string imageExt, string imageDisplayName)
    {
        string sceneGuid = NewBraceGuid();
        string frameGuid = NewBraceGuid();
        string photoGuid = NewBraceGuid();
        string id = cardId ?? "";
        string fileName = $"C:/HDPlayer/Image/{{{Guid.NewGuid():D}}}.{imageExt}";

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
        sb.Append("<Node Level=\"1\" Type=\"HD_Controller_Plugin\">\r\n");
        Attr(sb, 1, "AppVersion", "7.11.8.0");
        Attr(sb, 1, "BindTypeEnable", "0");
        Attr(sb, 1, "DeviceModel", model);
        Attr(sb, 1, "Height", height);
        Attr(sb, 1, "InsertProject", "0");
        Attr(sb, 1, "NewSpecialEffect", "close");
        Attr(sb, 1, "Rotation", "0");
        Attr(sb, 1, "Stretch", "0");
        Attr(sb, 1, "SvnVersion", "15486");
        Attr(sb, 1, "TimeZone", "18000");
        Attr(sb, 1, "Width", width);
        Attr(sb, 1, "ZoomModulus", "1");
        Attr(sb, 1, "__NAME__", "Screen");
        Attr(sb, 1, "mimiScreen", "0");
        sb.Append("    <List Name=\"communication\" Index=\"0\">\r\n");
        sb.Append($"        <ListItem name=\"\" id=\"{Esc(id)}\"/>\r\n");
        sb.Append("    </List>\r\n");

        sb.Append("    <Node Level=\"2\" Type=\"HD_OrdinaryScene_Plugin\">\r\n");
        Attr(sb, 2, "AbsorbEnable", "1");
        Attr(sb, 2, "Alpha", "255");
        Attr(sb, 2, "BgColor", "-16777216");
        Attr(sb, 2, "BgMode", "BgImage");
        Attr(sb, 2, "Checked", "2");
        Attr(sb, 2, "FixedDuration", "30000");
        Attr(sb, 2, "FrameEffect", "0");
        Attr(sb, 2, "FrameSpeed", "4");
        Attr(sb, 2, "FrameType", "0");
        Attr(sb, 2, "Friday", "0");
        Attr(sb, 2, "Monday", "0");
        Attr(sb, 2, "MotleyIndex", "0");
        Attr(sb, 2, "OrdinarySceneVolume", "0");
        Attr(sb, 2, "PlayIndex", "0");
        Attr(sb, 2, "PlayMode", "LoopTime");
        Attr(sb, 2, "PlayTimes", "1");
        Attr(sb, 2, "PlayeTime", "30");
        Attr(sb, 2, "PurityColor", "255");
        Attr(sb, 2, "PurityIndex", "0");
        Attr(sb, 2, "Saturday", "0");
        Attr(sb, 2, "SpaceStartTime", "00:00:00");
        Attr(sb, 2, "SpaceStopTime", "23:59:59");
        Attr(sb, 2, "Sunday", "0");
        Attr(sb, 2, "Thursday", "0");
        Attr(sb, 2, "TricolorIndex", "0");
        Attr(sb, 2, "Tuesday", "0");
        Attr(sb, 2, "UseSpacifiled", "0");
        Attr(sb, 2, "Volume", "100");
        Attr(sb, 2, "Wednesday", "0");
        Attr(sb, 2, "__GUID__", sceneGuid);
        Attr(sb, 2, "__NAME__", "Program1");
        sb.Append("        <List Name=\"__FileList__\" Index=\"-1\"/>\r\n");

        sb.Append("        <Node Level=\"3\" Type=\"HD_Frame_Plugin\">\r\n");
        Attr(sb, 3, "Alpha", "255");
        Attr(sb, 3, "ChildType", "HD_Photo_Plugin");
        Attr(sb, 3, "FrameSpeed", "4");
        Attr(sb, 3, "FrameType", "0");
        Attr(sb, 3, "Height", height);
        Attr(sb, 3, "Index", "0");
        Attr(sb, 3, "LockArea", "0");
        Attr(sb, 3, "MotleyIndex", "0");
        Attr(sb, 3, "PurityColor", "255");
        Attr(sb, 3, "PurityIndex", "0");
        Attr(sb, 3, "TricolorIndex", "0");
        Attr(sb, 3, "Width", width);
        Attr(sb, 3, "X", "0");
        Attr(sb, 3, "Y", "0");
        Attr(sb, 3, "__GUID__", frameGuid);
        Attr(sb, 3, "__NAME__", "Frame1");

        sb.Append("            <Node Level=\"4\" Type=\"HD_Photo_Plugin\">\r\n");
        Attr(sb, 4, "ClearEffect", "0");
        Attr(sb, 4, "ClearTime", "4");
        Attr(sb, 4, "DispEffect", "0");
        Attr(sb, 4, "DispTime", "4");
        Attr(sb, 4, "HoldTime", "50");
        Attr(sb, 4, "KeepConvert", "0");
        Attr(sb, 4, "KeepRatio", "0");
        Attr(sb, 4, "PreloadFilePath", fileName);
        Attr(sb, 4, "SpeedTimeIndex", "4");
        Attr(sb, 4, "__GUID__", photoGuid);
        Attr(sb, 4, "__NAME__", imageDisplayName);
        sb.Append("                <List Name=\"__FileList__\" Index=\"0\">\r\n");
        sb.Append($"                    <ListItem FileName=\"{Esc(fileName)}\" FileKey=\"Photo\" MD5=\"{imageMd5}\"/>\r\n");
        sb.Append("                </List>\r\n");
        sb.Append("            </Node>\r\n");

        sb.Append("        </Node>\r\n");
        sb.Append("    </Node>\r\n");
        sb.Append("</Node>\r\n");
        return sb.ToString();
    }

    private static void Attr(StringBuilder sb, int level, string name, string value)
    {
        sb.Append(new string(' ', level * 4));
        sb.Append($"<Attribute Name=\"{name}\">{Esc(value)}</Attribute>\r\n");
    }

    private static void Attr(StringBuilder sb, int level, string name, int value)
        => Attr(sb, level, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private string BuildClientInfo()
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss");
        string now2 = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        string user = SafeField(Environment.UserName);
        string host = SafeField(Environment.MachineName);
        // OS,App,User,Host,CpuId,Field6,Field7,DateTime,<adapters…>,,GUID,DateTime
        return $"Windows,HDPlayer,{user},{host},,,,{now},,{Guid.NewGuid():D},{now2}";
    }

    private static string SafeField(string s) => s.Replace(',', '_');

    // ─── Frame I/O ───────────────────────────────────────────────────────────

    /// <summary>Sends <paramref name="cmd"/> with <paramref name="body"/>, then reads frames
    /// until one whose cmd equals <paramref name="expectResp"/>; returns that frame's body.</summary>
    private byte[] Exchange(ushort cmd, byte[] body, ushort expectResp)
    {
        SendCmd(cmd, body);
        for (int i = 0; i < 256; i++)
        {
            var (gotCmd, gotBody) = ReadCmd();
            if (gotCmd == expectResp) return gotBody;
            _log($"[Huidu] Skipping unexpected reply 0x{gotCmd:x4} (waiting for 0x{expectResp:x4}).");
        }
        throw new IOException($"Huidu: did not receive expected reply 0x{expectResp:x4} to 0x{cmd:x4}.");
    }

    private void SendCmd(ushort cmd, byte[] body)
    {
        int total = 4 + body.Length;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException($"Huidu: frame too large ({total} bytes).");
        var frame = new byte[total];
        frame[0] = (byte)(total & 0xff);
        frame[1] = (byte)((total >> 8) & 0xff);
        frame[2] = (byte)(cmd & 0xff);
        frame[3] = (byte)((cmd >> 8) & 0xff);
        if (body.Length > 0) Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        SendRaw(frame);
    }

    private (ushort cmd, byte[] body) ReadCmd()
    {
        var header = ReadExact(4);
        int total = header[0] | (header[1] << 8);
        ushort cmd = (ushort)(header[2] | (header[3] << 8));
        if (total < 4 || total > 16 * 1024 * 1024)
            throw new IOException($"Huidu: invalid frame length {total}.");
        var body = total > 4 ? ReadExact(total - 4) : Array.Empty<byte>();
        return (cmd, body);
    }

    private void SendRaw(byte[] buffer)
    {
        int sent = 0;
        while (sent < buffer.Length)
            sent += _socket.Send(buffer, sent, buffer.Length - sent, SocketFlags.None);
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

    // ─── Encoding helpers ────────────────────────────────────────────────────

    private static byte[] BuildTransferBegin(long totalSize)
    {
        var b = new byte[8];
        uint v = (uint)totalSize;
        b[0] = (byte)(v & 0xff);
        b[1] = (byte)((v >> 8) & 0xff);
        b[2] = (byte)((v >> 16) & 0xff);
        b[3] = (byte)((v >> 24) & 0xff);
        return b;
    }

    private static byte[] BuildCString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var buf = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);
        return buf; // trailing 0 from array init
    }

    private static long ReadU32(byte[] d, int i)
        => (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));

    private static string NewBraceGuid() => "{" + Guid.NewGuid().ToString("D") + "}";

    private static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
    }

    private static string NormalizeExt(string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? "png" : ext;
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { _socket.Close(); } catch { /* ignore */ }
    }
}
