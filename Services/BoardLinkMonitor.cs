using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Background "watchdog" that handles the AnyDesk / on-site scenario where an operator
/// switches the PC's Wi-Fi onto the LED card's own access point (an "island" network
/// with no internet). When that happens this monitor:
///
///   1. Detects the network switch (NetworkChange events + a safety poll).
///   2. Recognises the board AP: a Wi-Fi interface on a private subnet with no internet.
///   3. Auto-discovers the card (UDP broadcast on port 9527) and/or falls back to the
///      interface gateway (in AP mode the card IS the gateway, e.g. 192.168.43.1).
///   4. Runs connectivity diagnostics: ping, TCP connect + HDPlayer hello handshake.
///   5. Compares the found IP with the configured <c>HuiduLed:CardIp</c> and, when they
///      differ, applies the discovered IP at runtime (<see cref="BoardLinkState"/>) and
///      patches appsettings.json — gated by <see cref="HuiduOptions.AutoApplyCardIp"/>
///      and only on an island AP so a deliberate static LAN IP is never overwritten.
///
/// The structured verdict is published to <see cref="BoardLinkState"/> (for the API/UI)
/// and the key lines are written to <see cref="InMemoryLogStore"/> with the
/// <c>[BoardLink]</c> tag — but only when the state changes, to avoid log spam.
/// </summary>
public sealed class BoardLinkMonitor : BackgroundService
{
    private const string Tag = "BoardLink";

    private readonly ILogger<BoardLinkMonitor> _logger;
    private readonly HuiduOptions _options;
    private readonly BoardLinkState _state;
    private readonly InMemoryLogStore _logStore;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private string? _lastSignature;

    public BoardLinkMonitor(
        ILogger<BoardLinkMonitor> logger,
        IOptions<HuiduOptions> options,
        BoardLinkState state,
        InMemoryLogStore logStore)
    {
        _logger = logger;
        _options = options.Value;
        _state = state;
        _logStore = logStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.AutoDetectOnApLink)
        {
            Log(LogLevel.Information,
                "[BoardLink] Отключён (HuiduLed:Enabled=false или AutoDetectOnApLink=false). Мониторинг не запущен.");
            return;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        Log(LogLevel.Information,
            $"[BoardLink] Запущен. Слежу за переключением Wi-Fi на сеть табло (safety-poll каждые {_options.BoardLinkPollSeconds}с).");

        try
        {
            // Small initial settle so the network stack is up before the first probe.
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"[BoardLink] Ошибка цикла: {ex.Message}");
                }

                // Wake on a network change (debounced) or after the safety-poll interval.
                var pollDelay = Task.Delay(TimeSpan.FromSeconds(_options.BoardLinkPollSeconds), stoppingToken);
                var woken = _wake.WaitAsync(stoppingToken);
                var done = await Task.WhenAny(pollDelay, woken);
                if (done == woken)
                {
                    // Debounce: a switch often fires several events in a burst.
                    try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { }
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            Log(LogLevel.Information, "[BoardLink] Остановлен.");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Nudge();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Nudge();

    private void Nudge()
    {
        // Release the wait if it isn't already signalled; ignore if it is.
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
    }

    /// <summary>Force a re-evaluation now (used by the API recheck endpoint).</summary>
    public async Task<BoardLinkReport> RecheckAsync(CancellationToken ct)
        => await EvaluateAsync(ct);

    // ─── Core evaluation ─────────────────────────────────────────────────────

    private async Task<BoardLinkReport> EvaluateAsync(CancellationToken ct)
    {
        var steps = new List<string>();

        var ap = FindBoardApInterface(steps);
        if (ap is null)
        {
            var idle = BoardLinkReport.NotOnBoardWifi(steps);
            Publish(idle);
            return idle;
        }

        steps.Add($"Сеть-«остров» табло: интерфейс '{ap.Name}', локальный IP {ap.LocalIp}, " +
                  $"шлюз {ap.Gateway?.ToString() ?? "—"}, интернет={(ap.HasInternet ? "есть" : "нет")}.");

        // 1) Discover the card over UDP, fall back to the interface gateway (AP mode).
        string? discoveredId = null;
        string? candidate = null;

        var cards = HuiduHdPlayerDiscovery.Search(1500, s => steps.Add(s.TrimStart()), _options.UdpDiscoveryPort);
        if (cards.Count > 0)
        {
            discoveredId = cards[0].Id;
            candidate = cards[0].Ip.ToString();
            steps.Add($"UDP-поиск (порт 9527): найдено карт {cards.Count}; беру первую id='{discoveredId}' ip={candidate}.");
            if (cards.Count > 1)
                steps.Add("⚠ Найдено больше одной карты — авто-применение IP отключено для безопасности.");
        }
        else if (ap.Gateway is not null)
        {
            candidate = ap.Gateway.ToString();
            steps.Add($"UDP-поиск ничего не дал — пробую шлюз интерфейса {candidate} (в режиме AP карта = шлюз).");
        }
        else
        {
            steps.Add("✗ Карта не найдена по UDP и шлюз неизвестен — определить IP невозможно.");
        }

        // 2) Connectivity diagnostics on the candidate.
        bool pingOk = false, tcpOk = false, helloOk = false;
        if (candidate is not null)
        {
            pingOk = TryPing(candidate, 1000);
            steps.Add($"ICMP ping {candidate}: {(pingOk ? "ответ есть" : "нет ответа")}.");

            (tcpOk, helloOk) = TryTcpHello(candidate, _options.CardPort, Math.Min(_options.IoTimeoutMs, 4000));
            steps.Add($"TCP {candidate}:{_options.CardPort}: {(tcpOk ? "подключение OK" : "не подключается")}" +
                      (tcpOk ? $", hello-handshake: {(helloOk ? "OK" : "нет ответа")}." : "."));
        }

        // 3) Panel-size read-back (best effort; currently unavailable over the
        //    implemented protocols, so this stays null and only warns).
        var detected = TryDetectScreenSize(candidate, steps);

        // 4) Compare with config and decide.
        var configuredIp = string.IsNullOrWhiteSpace(_options.CardIp) ? null : _options.CardIp.Trim();
        bool ipMatches = candidate is not null && string.Equals(candidate, configuredIp, StringComparison.OrdinalIgnoreCase);
        string? appliedIp = null;

        bool singleCardOnIsland = !ap.HasInternet && cards.Count <= 1;
        bool reachable = tcpOk; // TCP+hello is the real proof the card answers

        if (candidate is not null && reachable && !ipMatches && singleCardOnIsland && IsPrivate(candidate))
        {
            if (_options.AutoApplyCardIp)
            {
                _state.SetCardIpOverride(candidate);
                bool persisted = TryPatchAppSettings(candidate, detected, steps);
                appliedIp = candidate;
                steps.Add($"✓ Авто-применён CardIp={candidate} (рантайм{(persisted ? " + appsettings.json" : "")}).");
            }
            else
            {
                steps.Add($"→ Рекомендуется поставить HuiduLed:CardIp={candidate} (AutoApplyCardIp=false).");
            }
        }

        // 5) Detected panel size auto-apply.
        if (detected is not null && _options.AutoApplyScreenSize &&
            (detected.Value.w != _options.ScreenWidth || detected.Value.h != _options.ScreenHeight))
        {
            _state.SetScreenOverride(detected.Value.w, detected.Value.h);
            steps.Add($"✓ Авто-применён размер панели {detected.Value.w}x{detected.Value.h}.");
        }
        else if (detected is not null &&
                 (detected.Value.w != _options.ScreenWidth || detected.Value.h != _options.ScreenHeight))
        {
            steps.Add($"⚠ Размер панели карты {detected.Value.w}x{detected.Value.h} ≠ настроек " +
                      $"{_options.ScreenWidth}x{_options.ScreenHeight}.");
        }

        var (verdict, recommendation) = BuildVerdict(candidate, reachable, ipMatches, appliedIp);

        var report = new BoardLinkReport(
            CheckedAt: DateTimeOffset.UtcNow,
            OnBoardWifi: true,
            InterfaceName: ap.Name,
            LocalIp: ap.LocalIp.ToString(),
            Gateway: ap.Gateway?.ToString(),
            HasInternet: ap.HasInternet,
            DiscoveredCardId: discoveredId,
            CandidateCardIp: candidate,
            PingOk: pingOk,
            TcpOk: tcpOk,
            HelloOk: helloOk,
            ConfiguredCardIp: configuredIp,
            CardIpMatches: ipMatches,
            AppliedCardIp: appliedIp,
            ConfiguredWidth: _options.ScreenWidth,
            ConfiguredHeight: _options.ScreenHeight,
            DetectedWidth: detected?.w,
            DetectedHeight: detected?.h,
            Verdict: verdict,
            Recommendation: recommendation,
            Steps: steps);

        Publish(report);
        await Task.CompletedTask;
        return report;
    }

    private static (string verdict, string recommendation) BuildVerdict(
        string? candidate, bool reachable, bool ipMatches, string? appliedIp)
    {
        if (candidate is null)
            return ("card-not-found",
                "Карта не найдена на Wi-Fi табло. Проверьте, что ПК подключён именно к точке доступа табло, " +
                "и что карта включена (UDP-поиск порт 9527).");

        if (!reachable)
            return ("unreachable",
                $"Карта {candidate} найдена, но не отвечает по TCP {candidate}:10001. " +
                "Проверьте firewall и что это действительно адрес карты.");

        if (appliedIp is not null)
            return ("fixed",
                $"Готово: связь с картой есть, CardIp автоматически установлен в {appliedIp}. Можно отправлять на табло.");

        if (ipMatches)
            return ("ok", $"Всё в порядке: карта {candidate} доступна и совпадает с настройками. Можно отправлять.");

        return ("needs-config",
            $"Карта доступна на {candidate}, но в настройках другой CardIp. " +
            $"Установите HuiduLed:CardIp={candidate} (или включите AutoApplyCardIp).");
    }

    // ─── Publish (dedup + log) ───────────────────────────────────────────────

    private void Publish(BoardLinkReport report)
    {
        _state.SetReport(report);

        // Signature ignores the timestamp so we only log on a meaningful change.
        var sig = $"{report.OnBoardWifi}|{report.Verdict}|{report.CandidateCardIp}|{report.AppliedCardIp}|{report.CardIpMatches}";
        if (sig == _lastSignature) return;
        _lastSignature = sig;

        var level = report.Verdict switch
        {
            "ok" or "fixed" or "idle" => LogLevel.Information,
            "needs-config" => LogLevel.Warning,
            _ => LogLevel.Error,
        };
        Log(level, $"[BoardLink] {report.Recommendation}");
    }

    // ─── Network detection ───────────────────────────────────────────────────

    private sealed record ApInterface(string Name, IPAddress LocalIp, IPAddress? Gateway, bool HasInternet);

    /// <summary>
    /// Returns the active Wi-Fi interface that looks like the board's AP (private IPv4,
    /// preferring one with no internet), or null if the PC is on a normal network.
    /// </summary>
    private ApInterface? FindBoardApInterface(List<string> steps)
    {
        ApInterface? best = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsVirtualAdapter(ni)) continue;            // skip AnyDesk/VPN/etc.
            if (!IsWifiInterface(ni)) continue;            // board AP is Wi-Fi

            var props = ni.GetIPProperties();
            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4?.Address is null || !IsPrivate(ipv4.Address)) continue;

            var gw = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a is not null && a.AddressFamily == AddressFamily.InterNetwork
                                     && !a.Equals(IPAddress.Any));

            bool internet = HasInternet();

            // A board AP is a Wi-Fi island with NO internet. A Wi-Fi that has internet is
            // almost certainly the normal office/home network — don't treat it as the board.
            if (!internet)
                return new ApInterface(ni.Name, ipv4.Address, gw, false);

            best ??= new ApInterface(ni.Name, ipv4.Address, gw, true);
        }

        if (best is not null)
            steps.Add($"Wi-Fi '{best.Name}' с интернетом — это обычная сеть, не AP табло. Жду переключения.");
        return null;
    }

    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var f = ($"{ni.Name} {ni.Description}").ToLowerInvariant();
        return f.Contains("anydesk") || f.Contains("teamviewer") || f.Contains("virtual")
               || f.Contains("vpn") || f.Contains("vethernet") || f.Contains("hyper-v")
               || f.Contains("loopback") || f.Contains("tap-") || f.Contains("wintun")
               || f.Contains("wireguard") || f.Contains("vmware") || f.Contains("vbox");
    }

    private static bool IsWifiInterface(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return true;
        var f = ($"{ni.Name} {ni.Description}").ToLowerInvariant();
        return f.Contains("wifi") || f.Contains("wi-fi") || f.Contains("wlan") || f.Contains("802.11");
    }

    /// <summary>Quick internet probe: a 700 ms TCP connect to a public DNS resolver.</summary>
    private static bool HasInternet()
    {
        foreach (var host in new[] { "8.8.8.8", "1.1.1.1" })
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var ar = s.BeginConnect(IPAddress.Parse(host), 53, null, null);
                if (ar.AsyncWaitHandle.WaitOne(700) && s.Connected) { s.EndConnect(ar); return true; }
            }
            catch { /* try next */ }
        }
        return false;
    }

    private static bool IsPrivate(string ip)
        => IPAddress.TryParse(ip, out var a) && IsPrivate(a);

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    // ─── Probes ──────────────────────────────────────────────────────────────

    private static bool TryPing(string ip, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            return ping.Send(ip, timeoutMs).Status == IPStatus.Success;
        }
        catch { return false; }
    }

    /// <summary>TCP-connects and runs the HDPlayer hello handshake. Returns (tcpOk, helloOk).</summary>
    private (bool tcp, bool hello) TryTcpHello(string ip, int port, int timeoutMs)
    {
        try
        {
            // Pick the handshake matching the card family: C-series (e.g. C16L) serve the
            // binary protocol on 9527; A-series (e.g. A3L) the JSON protocol on 10001.
            if (port == HuiduCSeriesClient.DefaultPort)
                using (HuiduCSeriesClient.Connect(ip, port, timeoutMs, _ => { })) { }
            else
                using (HuiduHdPlayerClient.Connect(ip, port, timeoutMs, _ => { })) { }
            // Connect() completes the handshake; reaching here means both worked.
            return (true, true);
        }
        catch
        {
            // Distinguish a bare TCP open from a full handshake failure.
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var ar = s.BeginConnect(IPAddress.Parse(ip), port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs) && s.Connected;
                if (ok) s.EndConnect(ar);
                return (ok, false);
            }
            catch { return (false, false); }
        }
    }

    /// <summary>
    /// Best-effort panel-size read-back. The implemented HDPlayer/SDK transports do not
    /// expose a reliable "get screen size", so this returns null today and the caller
    /// only warns. Hook for a future protocol query.
    /// </summary>
    private (int w, int h)? TryDetectScreenSize(string? candidate, List<string> steps)
    {
        _ = candidate;
        steps.Add("Размер панели по сети надёжно не читается — оставляю значение из настроек " +
                  $"({_options.ScreenWidth}x{_options.ScreenHeight}); проверьте вручную при смене модели.");
        return null;
    }

    // ─── Persistence ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _writeOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private static readonly JsonDocumentOptions _readOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Patches HuiduLed:CardIp (and optionally ScreenWidth/Height) in appsettings.json.</summary>
    private bool TryPatchAppSettings(string cardIp, (int w, int h)? screen, List<string> steps)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) { steps.Add("⚠ appsettings.json не найден — записал только в рантайм."); return false; }

            var root = JsonNode.Parse(File.ReadAllText(path), null, _readOpts)!.AsObject();
            var hd = root["HuiduLed"]?.AsObject() ?? new JsonObject();
            hd["CardIp"] = cardIp;
            if (screen is not null)
            {
                hd["ScreenWidth"] = screen.Value.w;
                hd["ScreenHeight"] = screen.Value.h;
            }
            root["HuiduLed"] = hd;
            File.WriteAllText(path, root.ToJsonString(_writeOpts));
            return true;
        }
        catch (Exception ex)
        {
            steps.Add($"⚠ Не удалось записать appsettings.json: {ex.Message} (рантайм-значение применено).");
            return false;
        }
    }

    // ─── Logging ─────────────────────────────────────────────────────────────

    private void Log(LogLevel level, string message)
    {
        _logger.Log(level, "{Message}", message);
        _logStore.Add(level, Tag, message);
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
