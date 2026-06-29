using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Client for the modern Huidu <b>HDPlayer</b> protocol used by cards such as the BX A3L,
/// reverse-engineered from an HDPlayer ⇄ card packet capture (see <c>wirreshark.txt</c>).
///
/// This is the protocol the card actually speaks, and it is the opposite of the older
/// "SDK server" XML protocol in <see cref="HuiduSdkClient"/>:
///   • <b>The PC is the TCP client</b> — it connects to the card at <c>cardIp:10001</c>
///     (in Wi-Fi AP mode the card is the gateway, e.g. 192.168.43.1).
///   • Commands are <b>JSON</b>, not XML (<c>{"module":"PlayTask", …}</c>).
///   • Discovery is a UDP broadcast on port <b>9527</b>.
///
/// Wire format (all integers little-endian):
///   • JSON message:  [totalLen u16][type u8][0x21][jsonLen u32][reserved u32][utf8 json]
///       type: 0x01 = hello-ask, 0x02 = hello-answer, 0x03 = command-ask, 0x04 = command-answer
///   • Hello (short): [len u16 = 8][0x01][0x21][version u32]
///   • File sync sub-protocol (after a PlayTask is accepted):
///       begin   PC→card  [len u16][0x80fc][fileCount u16][totalSize i64][taskUuid][0x00]
///       answer  card→PC  [len u16][0x80fd][status u16]
///       start   PC→card  [len u16][0x8001][md5 32 + 0x00][size i64][flag u16=1][name][0x00]
///       exists  card→PC  [len u16][0x8002][status u16][existingSize i64]
///       content PC→card  [len u16][0x8003][raw bytes]            (only if existingSize < size)
///       end     PC→card  [len u16][0x8005]
///       endAns  card→PC  [len u16][0x8006][status u16]
///       finish  PC→card  [len u16][0x80fe]
///       finAns  card→PC  [len u16][0x80ff][status u16]
/// </summary>
internal sealed class HuiduHdPlayerClient : IDisposable
{
    public const int DefaultPort = 10001;
    private const uint HelloVersion = 0x01000000;
    private const int FileChunk = 8 * 1024;

    private readonly Socket _socket;
    private readonly Action<string> _log;

    private HuiduHdPlayerClient(Socket socket, Action<string> log)
    {
        _socket = socket;
        _log = log;
    }

    public static HuiduHdPlayerClient Connect(string cardIp, int port, int ioTimeoutMs, Action<string> log)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveTimeout = ioTimeoutMs,
            SendTimeout = ioTimeoutMs,
            NoDelay = true,
        };

        // Connect with the IO timeout so an unreachable card fails fast instead of hanging.
        var ar = socket.BeginConnect(IPAddress.Parse(cardIp), port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(ioTimeoutMs))
        {
            socket.Close();
            throw new SocketException((int)SocketError.TimedOut);
        }
        socket.EndConnect(ar);

        var client = new HuiduHdPlayerClient(socket, log);
        client.Hello();
        return client;
    }

    /// <summary>Short version handshake the card expects before any JSON command.</summary>
    private void Hello()
    {
        var hello = new byte[8];
        WriteU16(hello, 0, 8);
        hello[2] = 0x01;        // hello-ask
        hello[3] = 0x21;
        WriteU32(hello, 4, HelloVersion);
        SendRaw(hello);

        var reply = ReadFrame();
        if (reply.Length < 4 || reply[2] != 0x02)
            _log($"[Huidu] Unexpected hello reply (type=0x{(reply.Length > 2 ? reply[2] : 0):x2}); continuing.");
    }

    // ─── PlayTask: show one full-screen image ────────────────────────────────

    /// <summary>
    /// Pushes a single full-screen image to the card: builds the PlayTask JSON, sends it,
    /// runs the file-sync upload, and waits for the final <c>kSuccess</c>.
    /// Returns (success, detail).
    /// </summary>
    public (bool ok, string detail) SendFullScreenImage(string imagePath, int screenWidth, int screenHeight)
    {
        var info = new FileInfo(imagePath);
        string name = Path.GetFileName(imagePath);
        string md5 = ComputeMd5Hex(imagePath);
        long size = info.Length;
        string taskUuid = NewBraceGuid();

        var playTask = new JsonObject
        {
            ["clearOld"] = true,
            ["displayOptions"] = new JsonObject
            {
                ["enableStretch"] = false,
                ["rotation"] = 0,
                ["scaleToFit"] = false,
            },
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["fileType"] = "Image",
                    ["md5"] = md5,
                    ["name"] = name,
                    ["path"] = imagePath.Replace('\\', '/'),
                    ["size"] = size,
                },
            },
            ["interlude"] = false,
            ["name"] = "tablo",
            ["programs"] = new JsonArray
            {
                new JsonObject
                {
                    ["areas"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["alpha"] = 1,
                            ["frame"] = new JsonObject
                            {
                                ["height"] = screenHeight,
                                ["width"] = screenWidth,
                                ["x"] = 0,
                                ["y"] = 0,
                            },
                            ["name"] = "area1",
                            ["rotation"] = 0,
                            ["uuid"] = NewBraceGuid(),
                            ["widgets"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["content"] = new JsonObject
                                    {
                                        ["effect"] = new JsonObject
                                        {
                                            ["displayTime"] = 5000,
                                            ["effectTime"] = 0,
                                            ["effectType"] = 0,
                                            ["exitEffectTime"] = 0,
                                            ["exitEffectType"] = 255,
                                            ["inSpeed"] = 5,
                                            ["moveByFrame"] = true,
                                            ["outSpeed"] = 5,
                                            ["speedType"] = 2,
                                        },
                                        ["image"] = name,
                                        ["keepAspectRatio"] = false,
                                    },
                                    ["name"] = name,
                                    ["type"] = "Image",
                                    ["uuid"] = NewBraceGuid(),
                                },
                            },
                        },
                    },
                    ["name"] = "Program1",
                    ["playControl"] = new JsonObject { ["playCount"] = 1 },
                    ["uuid"] = NewBraceGuid(),
                    ["version"] = 0,
                },
            },
            ["screenSize"] = new JsonObject
            {
                ["height"] = screenHeight,
                ["width"] = screenWidth,
            },
            ["uuid"] = taskUuid,
        };

        var message = new JsonObject
        {
            ["ask"] = new JsonObject
            {
                ["deviceList"] = "",
                ["key"] = "",
                ["log"] = "",
                ["playTask"] = playTask,
                ["reportProgress"] = false,
            },
            ["module"] = "PlayTask",
            ["uuid"] = NewBraceGuid(),
            ["version"] = 5,
        };

        // 1) Send the PlayTask command.
        SendJson(message);

        // 2) First reply: kTaskAccepted (the card then expects the files).
        var accepted = ReadJsonReply();
        var firstResult = ResultOf(accepted);
        if (firstResult is not ("kTaskAccepted" or "kSuccess"))
            return (false, $"Card did not accept PlayTask. Reply: {accepted}");

        // 3) File sync — upload the image (or skip if the card already has this md5).
        var files = new[] { new PendingFile(imagePath, name, md5, size) };
        SyncFiles(taskUuid, files);

        // 4) Final reply: kSuccess. (If the card already finished at accept time, accept that.)
        string detail;
        if (firstResult == "kSuccess")
        {
            detail = "ok";
        }
        else
        {
            var done = ReadJsonReply();
            var result = ResultOf(done);
            if (result != "kSuccess")
                return (false, $"Card rejected PlayTask after upload. Reply: {done}");
            detail = "ok";
        }

        return (true, detail);
    }

    /// <summary>Clears the screen by pushing a PlayTask with no programs and clearOld=true.</summary>
    public (bool ok, string detail) ClearScreen(int screenWidth, int screenHeight)
    {
        string taskUuid = NewBraceGuid();
        var message = new JsonObject
        {
            ["ask"] = new JsonObject
            {
                ["deviceList"] = "",
                ["key"] = "",
                ["log"] = "",
                ["playTask"] = new JsonObject
                {
                    ["clearOld"] = true,
                    ["files"] = new JsonArray(),
                    ["interlude"] = false,
                    ["name"] = "clear",
                    ["programs"] = new JsonArray(),
                    ["screenSize"] = new JsonObject { ["height"] = screenHeight, ["width"] = screenWidth },
                    ["uuid"] = taskUuid,
                },
                ["reportProgress"] = false,
            },
            ["module"] = "PlayTask",
            ["uuid"] = NewBraceGuid(),
            ["version"] = 5,
        };

        SendJson(message);
        var accepted = ReadJsonReply();
        var firstResult = ResultOf(accepted);
        if (firstResult is not ("kTaskAccepted" or "kSuccess"))
            return (false, $"Clear rejected: {accepted}");

        // No files → empty sync handshake, then final reply.
        SyncFiles(taskUuid, Array.Empty<PendingFile>());

        if (firstResult == "kSuccess") return (true, "ok");
        var done = ReadJsonReply();
        return ResultOf(done) == "kSuccess" ? (true, "ok") : (false, $"Clear failed: {done}");
    }

    // ─── File-sync sub-protocol ──────────────────────────────────────────────

    private readonly record struct PendingFile(string Path, string Name, string Md5, long Size);

    private void SyncFiles(string taskUuid, IReadOnlyList<PendingFile> files)
    {
        long totalSize = files.Sum(f => f.Size);

        // begin: 0x80fc
        var begin = BuildControl(0x80fc, body =>
        {
            WriteU16(body, (ushort)files.Count);
            WriteI64(body, totalSize);
            WriteCString(body, taskUuid);
        });
        SendRaw(begin);
        ReadFrameUntilCmd(0x80fd);

        foreach (var f in files)
        {
            // start: 0x8001
            var start = BuildControl(0x8001, body =>
            {
                WriteFixed(body, f.Md5, 33);      // 32 hex chars + null terminator
                WriteI64(body, f.Size);
                WriteU16(body, 1);                // flag (constant 1 in the capture)
                WriteCString(body, f.Name);
            });
            SendRaw(start);

            var ans = ReadFrameUntilCmd(0x8002);
            long existing = ans.Length >= 14 ? ReadI64(ans, 6) : 0;

            if (existing < f.Size)
            {
                _log($"[Huidu] Uploading '{f.Name}' from offset {existing} ({f.Size - existing} bytes)…");
                using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(existing, SeekOrigin.Begin);
                var buf = new byte[FileChunk];
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    var content = new byte[4 + read];
                    WriteU16(content, 0, (ushort)content.Length);
                    WriteU16(content, 2, 0x8003);
                    Buffer.BlockCopy(buf, 0, content, 4, read);
                    SendRaw(content);
                }
            }
            else
            {
                _log($"[Huidu] Card already has '{f.Name}' (md5 match); skipping upload.");
            }

            // end of this file: 0x8005 → 0x8006
            SendRaw(BuildControl(0x8005, _ => { }));
            ReadFrameUntilCmd(0x8006);
        }

        // finish: 0x80fe → 0x80ff
        SendRaw(BuildControl(0x80fe, _ => { }));
        ReadFrameUntilCmd(0x80ff);
    }

    // ─── JSON message I/O ────────────────────────────────────────────────────

    private void SendJson(JsonObject message)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message);
        int total = 12 + json.Length;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException($"Huidu: JSON command too large ({total} bytes).");

        var packet = new byte[total];
        WriteU16(packet, 0, (ushort)total);
        packet[2] = 0x03;       // command-ask
        packet[3] = 0x21;
        WriteU32(packet, 4, (uint)json.Length);
        // bytes 8..11 reserved = 0
        Buffer.BlockCopy(json, 0, packet, 12, json.Length);
        SendRaw(packet);
    }

    private string ReadJsonReply()
    {
        var frame = ReadFrame();
        if (frame.Length < 12)
            throw new IOException("Huidu: malformed JSON reply (short frame).");
        return Encoding.UTF8.GetString(frame, 12, frame.Length - 12);
    }

    private static string? ResultOf(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["reply"]?["result"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    // ─── Framed socket I/O ───────────────────────────────────────────────────

    /// <summary>Reads one length-prefixed frame (first u16 = total byte length, including itself).</summary>
    private byte[] ReadFrame()
    {
        var header = ReadExact(2);
        int total = header[0] | (header[1] << 8);
        if (total < 2 || total > 16 * 1024 * 1024)
            throw new IOException($"Huidu: invalid frame length {total}.");

        var frame = new byte[total];
        frame[0] = header[0];
        frame[1] = header[1];
        if (total > 2)
        {
            var rest = ReadExact(total - 2);
            Buffer.BlockCopy(rest, 0, frame, 2, total - 2);
        }
        return frame;
    }

    /// <summary>Reads frames until one with the given control command (at offset 2). Skips others.</summary>
    private byte[] ReadFrameUntilCmd(ushort cmd)
    {
        for (int i = 0; i < 64; i++)
        {
            var frame = ReadFrame();
            if (frame.Length >= 4)
            {
                ushort got = (ushort)(frame[2] | (frame[3] << 8));
                if (got == cmd) return frame;
            }
        }
        throw new IOException($"Huidu: did not receive expected control reply 0x{cmd:x4}.");
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

    /// <summary>Builds a control frame: [len u16][cmd u16][body…]; len is filled in automatically.</summary>
    private static byte[] BuildControl(ushort cmd, Action<List<byte>> writeBody)
    {
        var body = new List<byte>();
        writeBody(body);
        int total = 4 + body.Count;
        var frame = new byte[total];
        WriteU16(frame, 0, (ushort)total);
        WriteU16(frame, 2, cmd);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static void WriteU16(byte[] d, int i, ushort v)
    {
        d[i] = (byte)(v & 0xff);
        d[i + 1] = (byte)((v >> 8) & 0xff);
    }

    private static void WriteU32(byte[] d, int i, uint v)
    {
        d[i] = (byte)(v & 0xff);
        d[i + 1] = (byte)((v >> 8) & 0xff);
        d[i + 2] = (byte)((v >> 16) & 0xff);
        d[i + 3] = (byte)((v >> 24) & 0xff);
    }

    private static void WriteU16(List<byte> d, ushort v)
    {
        d.Add((byte)(v & 0xff));
        d.Add((byte)((v >> 8) & 0xff));
    }

    private static void WriteI64(List<byte> d, long v)
    {
        ulong u = (ulong)v;
        for (int k = 0; k < 8; k++) d.Add((byte)((u >> (8 * k)) & 0xff));
    }

    /// <summary>Writes a UTF-8 string followed by a single null terminator.</summary>
    private static void WriteCString(List<byte> d, string s)
    {
        d.AddRange(Encoding.UTF8.GetBytes(s));
        d.Add(0);
    }

    /// <summary>Writes a UTF-8 string into a fixed-width field, zero-padded (last byte always 0).</summary>
    private static void WriteFixed(List<byte> d, string s, int width)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        for (int k = 0; k < width; k++)
            d.Add(k < bytes.Length && k < width - 1 ? bytes[k] : (byte)0);
    }

    private static long ReadI64(byte[] d, int i)
    {
        long v = 0;
        for (int k = 0; k < 8; k++) v |= (long)d[i + k] << (8 * k);
        return v;
    }

    private static string NewBraceGuid() => "{" + Guid.NewGuid().ToString("D") + "}";

    private static string ComputeMd5Hex(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { _socket.Close(); } catch { /* ignore */ }
    }
}

/// <summary>
/// UDP discovery for HDPlayer cards (port 9527). Broadcasts the 6-byte search probe and
/// collects replies, each of which reveals the card's id and (from the source address) its IP.
/// In Wi-Fi AP mode the card answers from its gateway address (e.g. 192.168.43.1).
/// </summary>
internal static class HuiduHdPlayerDiscovery
{
    private const int CardUdpPort = 9527;
    private static readonly byte[] SearchProbe = { 0x00, 0x00, 0x00, 0x01, 0x01, 0x00 };

    public readonly record struct Card(string Id, IPAddress Ip);

    public static List<Card> Search(int timeoutMs, Action<string> log)
    {
        var found = new List<Card>();
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.ReceiveTimeout = 300;

            udp.SendTo(SearchProbe, new IPEndPoint(IPAddress.Broadcast, CardUdpPort));

            var buf = new byte[2048];
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, ref remote); }
                catch (SocketException) { continue; }

                // Reply: 00 00 00 01 02 00 <deviceId…>. Anything ≥ 6 bytes with type byte 0x02.
                if (n < 6 || buf[4] != 0x02) continue;

                string id = n > 6 ? Encoding.ASCII.GetString(buf, 6, n - 6).TrimEnd('\0', ' ') : "";
                var ip = ((IPEndPoint)remote).Address;
                if (!found.Any(c => c.Ip.Equals(ip)))
                    found.Add(new Card(id, ip));
            }
        }
        catch (Exception ex)
        {
            log($"[Huidu] HDPlayer UDP discovery error: {ex.Message}");
        }
        return found;
    }

    /// <summary>Returns the first discovered card IP (optionally matching a device id), or null.</summary>
    public static string? FindCardIp(string? deviceIdFilter, int timeoutMs, Action<string> log)
    {
        var cards = Search(timeoutMs, log);
        if (cards.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(deviceIdFilter))
        {
            var match = cards.FirstOrDefault(c =>
                string.Equals(c.Id, deviceIdFilter, StringComparison.OrdinalIgnoreCase));
            if (match.Ip is not null) return match.Ip.ToString();
        }
        return cards[0].Ip.ToString();
    }
}
