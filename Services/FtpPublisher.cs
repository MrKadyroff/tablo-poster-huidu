using System.Net.Sockets;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class FtpPublisher : IPublishStrategy
{
    private readonly ILogger<FtpPublisher> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ControllerDiscovery _discovery;

    public FtpPublisher(
        ILogger<FtpPublisher> logger,
        IHttpClientFactory httpClientFactory,
        ControllerDiscovery discovery)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _discovery = discovery;
    }

    public string Mode => ServiceOptions.PublishModeWifiFtp;

    public async Task PublishAsync(ScreenInfo screen, ServiceOptions options, PublishPayload payload, CancellationToken ct)
    {
        // Auto-discover the reachable IP among all known candidates
        string resolvedIp;
        try
        {
            resolvedIp = await _discovery.ResolveAsync(screen.FtpIpCandidates, options.FtpPort, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"FTP: контроллер не найден. {ex.Message}", ex);
        }

        await ConnectAndUploadAsync(screen, options, payload, resolvedIp, attempt: 1, ct);
    }

    private async Task ConnectAndUploadAsync(
        ScreenInfo screen, ServiceOptions options, PublishPayload payload,
        string resolvedIp, int attempt, CancellationToken ct)
    {
        using var ftp = new AsyncFtpClient(resolvedIp, options.FtpUser, options.FtpPassword, options.FtpPort)
        {
            Config =
            {
                EncryptionMode = options.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                ValidateAnyCertificate = true,
                ConnectTimeout = 10000,
                ReadTimeout = 10000,
                DataConnectionConnectTimeout = 10000,
                DataConnectionReadTimeout = 10000
            }
        };

        _logger.LogInformation("FTP: подключаюсь к {Ip}:{Port} (пользователь '{User}')...",
            resolvedIp, options.FtpPort, options.FtpUser);

        try
        {
            await ftp.Connect(ct);
        }
        catch (SocketException ex) when (attempt == 1)
        {
            // Stale cache or network change — reset, re-discover, and retry once.
            _logger.LogWarning(
                "FTP: соединение отказано ({Ip}:{Port}), сбрасываю кэш и ищу заново...",
                resolvedIp, options.FtpPort);
            _discovery.Reset();
            string retryIp;
            try
            {
                retryIp = await _discovery.ResolveAsync(screen.FtpIpCandidates, options.FtpPort, ct);
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"FTP: контроллер не найден при повторном поиске (первый IP: {resolvedIp}). " +
                    $"Причина: {ex.Message}", ex);
            }
            await ConnectAndUploadAsync(screen, options, payload, retryIp, attempt: 2, ct);
            return;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"FTP: не удаётся подключиться к {resolvedIp}:{options.FtpPort}. " +
                $"Ошибка: {ex.Message}", ex);
        }
        catch (FtpAuthenticationException ex)
        {
            throw new InvalidOperationException(
                $"FTP: контроллер {resolvedIp} отклонил авторизацию (пользователь '{options.FtpUser}'). " +
                $"Проверьте FtpUser/FtpPassword в appsettings.json (обычно guest/guest). " +
                $"Ошибка: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"FTP: таймаут подключения к {resolvedIp}:{options.FtpPort} (10 сек). " +
                $"Контроллер недоступен или занят. Попробуйте позже. " +
                $"Ошибка: {ex.Message}", ex);
        }

        _logger.LogInformation("FTP: подключён. Загружаю файлы в /{Root}/...", screen.RemoteRoot);

        string root = "/" + screen.RemoteRoot.Trim('/');
        string remoteShareDir = root + "/share";
        string remoteProgramsDir = root + "/programs/program_0";
        string remoteListsDir = root + "/lists";

        await ftp.CreateDirectory(remoteShareDir, true, ct);
        await ftp.CreateDirectory(remoteProgramsDir, true, ct);
        await ftp.CreateDirectory(remoteListsDir, true, ct);

        _logger.LogInformation("FTP: [1/3] загружаю изображение {File} ({Size} байт)...",
            payload.ShareFileName, new FileInfo(payload.LocalImagePath).Length);
        await ftp.UploadFile(payload.LocalImagePath, remoteShareDir + "/" + payload.ShareFileName,
            FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct);

        _logger.LogInformation("FTP: [2/3] загружаю program.xml...");
        byte[] programXmlBytes = Encoding.UTF8.GetBytes(payload.ProgramXml);
        await using (var programStream = new MemoryStream(programXmlBytes))
        {
            await ftp.UploadStream(programStream, remoteProgramsDir + "/" + payload.ProgramFileName,
                FtpRemoteExists.Overwrite, true, null, ct);
        }

        _logger.LogInformation("FTP: [3/3] загружаю list.xml...");
        byte[] listXmlBytes = Encoding.UTF8.GetBytes(payload.ListXml);
        await using (var listStream = new MemoryStream(listXmlBytes))
        {
            await ftp.UploadStream(listStream, remoteListsDir + "/" + payload.ListFileName,
                FtpRemoteExists.Overwrite, true, null, ct);
        }

        await ftp.Disconnect(ct);
        _logger.LogInformation("FTP: все файлы загружены успешно → контроллер {Ip}", resolvedIp);

        await TryReloadControllerAsync(options, payload.ListFileName, screen.RemoteRoot, ct);
    }

    private async Task TryReloadControllerAsync(ServiceOptions options, string listFileName, string remoteRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ControllerReloadUrl))
            return;

        var url = options.ControllerReloadUrl
            .Replace("{list}", listFileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{root}", remoteRoot.Trim('/'), StringComparison.OrdinalIgnoreCase);

        try
        {
            using var http = _httpClientFactory.CreateClient("controller-reload");
            using var response = await http.GetAsync(url, ct);
            _logger.LogInformation("Reload ping → {Url} → HTTP {Status}", url, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Reload ping не удался (некритично): {Url} — {Msg}", url, ex.Message);
        }
    }
}
