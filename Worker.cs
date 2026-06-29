using LedImageUpdaterService.Models;
using LedImageUpdaterService.Services;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ServiceOptions _options;
    private readonly ScreenModelReader _screenReader;
    private readonly LedPayloadBuilder _payloadBuilder;
    private readonly WifiNetworkGuard _wifiGuard;
    private readonly RenderOnlyRunner _renderOnlyRunner;
    private readonly IReadOnlyDictionary<string, IPublishStrategy> _strategies;

    private string? _lastPublishedFile;
    private DateTime _lastPublishedWriteUtc;
    private DateTime _lastRenderInputWriteUtc;

    public Worker(
        ILogger<Worker> logger,
        IOptions<ServiceOptions> options,
        ScreenModelReader screenReader,
        LedPayloadBuilder payloadBuilder,
        WifiNetworkGuard wifiGuard,
        RenderOnlyRunner renderOnlyRunner,
        IEnumerable<IPublishStrategy> strategies)
    {
        _logger = logger;
        _options = options.Value;
        _screenReader = screenReader;
        _payloadBuilder = payloadBuilder;
        _wifiGuard = wifiGuard;
        _renderOnlyRunner = renderOnlyRunner;
        _strategies = strategies.ToDictionary(s => s.Mode, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LED image updater started");

        if (_options.LayoutTestMode)
        {
            _logger.LogInformation(
                "LayoutTestMode=true. Worker will run in RenderOnly test mode regardless of RunMode setting.");
            await RunRenderOnlyAsync(stoppingToken);
            _logger.LogInformation("LED image updater stopped");
            return;
        }

        if (string.Equals(_options.RunMode, ServiceOptions.RunModeRenderOnly, StringComparison.OrdinalIgnoreCase))
        {
            await RunRenderOnlyAsync(stoppingToken);
            _logger.LogInformation("LED image updater stopped");
            return;
        }

        if (string.Equals(_options.RunMode, ServiceOptions.RunModeFull, StringComparison.OrdinalIgnoreCase))
        {
            await RunFullAsync(stoppingToken);
            _logger.LogInformation("LED image updater stopped");
            return;
        }

        var screen = _screenReader.Read(_options.ScreenXmlPath, _options.ForceRemoteRoot, _options.NetworkJsonPath);
        _wifiGuard.ValidateTarget(screen.FtpIp, _options);

        if (!_strategies.TryGetValue(_options.PublishMode, out var strategy))
        {
            throw new InvalidOperationException($"Unknown PublishMode '{_options.PublishMode}'. Allowed: {ServiceOptions.PublishModeWifiFtp}, {ServiceOptions.PublishModeWifiRelay}");
        }

        _logger.LogInformation(
            "Loaded screen info: ScreenId={ScreenId}, ProgramId={ProgramId}, FtpPort={FtpPort}, RemoteRoot={RemoteRoot}, Size={Width}x{Height}, Mode={Mode}, Candidates=[{IPs}]",
            screen.ScreenId, screen.ProgramId, screen.FtpPort, screen.RemoteRoot, screen.Width, screen.Height, strategy.Mode,
            string.Join(", ", screen.FtpIpCandidates));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var image = GetNewestImage(_options.WatchFolder);
                if (image is null)
                {
                    _logger.LogInformation("No images found in watch folder: {WatchFolder}", _options.WatchFolder);
                }
                else
                {
                    if (!ShouldPublish(image) && _options.SkipIfUnchanged && !_options.ForceComposeEveryPoll)
                    {
                        _logger.LogInformation("Newest image unchanged, skip: {Image}", image.FullName);
                    }
                    else
                    {
                        var payload = _payloadBuilder.Build(screen, image.FullName);
                        await strategy.PublishAsync(screen, _options, payload, stoppingToken);

                        _lastPublishedFile = image.FullName;
                        _lastPublishedWriteUtc = image.LastWriteTimeUtc;

                        _logger.LogInformation(
                            "Published image: {Image} -> root {RemoteRoot}, share {ShareFile}, program {ProgramFile}, list {ListFile}",
                            image.FullName,
                            screen.RemoteRoot,
                            payload.ShareFileName,
                            payload.ProgramFileName,
                            payload.ListFileName);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Publish iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken);
        }

        _logger.LogInformation("LED image updater stopped");
    }

    // Render image from rates+template, then immediately publish to controller.
    private async Task RunFullAsync(CancellationToken stoppingToken)
    {
        var screen = _screenReader.Read(_options.ScreenXmlPath, _options.ForceRemoteRoot, _options.NetworkJsonPath);
        _wifiGuard.ValidateTarget(screen.FtpIp, _options);

        if (!_strategies.TryGetValue(_options.PublishMode, out var strategy))
        {
            throw new InvalidOperationException(
                $"Unknown PublishMode '{_options.PublishMode}'. Allowed: {ServiceOptions.PublishModeWifiFtp}, {ServiceOptions.PublishModeWifiRelay}");
        }

        _logger.LogInformation(
            "RunMode=Full. Screen {ScreenId}, {Width}x{Height}, FtpPort={FtpPort}, Mode={Mode}, Candidates=[{IPs}]",
            screen.ScreenId, screen.Width, screen.Height, screen.FtpPort, strategy.Mode,
            string.Join(", ", screen.FtpIpCandidates));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var latestInputUtc = GetRenderInputsLastWriteUtc();
                bool inputChanged = _options.ForceComposeEveryPoll
                    || !_options.SkipIfUnchanged
                    || latestInputUtc == DateTime.MinValue
                    || latestInputUtc != _lastRenderInputWriteUtc;

                if (!inputChanged)
                {
                    _logger.LogInformation("Full: no changes in rates/images, skip render+publish");
                }
                else
                {
                    // Step 1: render
                    var exitCode = await _renderOnlyRunner.RunComposeAsync(_options, stoppingToken);
                    if (exitCode != 0)
                    {
                        _logger.LogError("Full: compose failed with exit code {ExitCode}", exitCode);
                    }
                    else if (!_options.PermanentInternet)
                    {
                        // No permanent internet: render only, do NOT auto-publish on the timer.
                        // The operator pushes the image manually from the Design tab.
                        _lastRenderInputWriteUtc = latestInputUtc;
                        _logger.LogInformation(
                            "Full: PermanentInternet=false — изображение перерисовано, автоотправка пропущена. " +
                            "Отправьте на табло вручную кнопкой «Отправить на табло».");
                    }
                    else
                    {
                        // Step 2: pick the freshly rendered image from WatchFolder
                        var image = GetNewestImage(_options.WatchFolder);
                        if (image is null)
                        {
                            _logger.LogWarning("Full: compose OK but no image found in {WatchFolder}", _options.WatchFolder);
                        }
                        else
                        {
                            // Step 3: build payload and publish
                            var payload = _payloadBuilder.Build(screen, image.FullName);
                            await strategy.PublishAsync(screen, _options, payload, stoppingToken);

                            // Only mark inputs as processed after full success.
                            // If publish fails (exception), _lastRenderInputWriteUtc stays stale
                            // so next cycle will re-render + re-try publish automatically.
                            _lastRenderInputWriteUtc = latestInputUtc;
                            _lastPublishedFile = image.FullName;
                            _lastPublishedWriteUtc = image.LastWriteTimeUtc;

                            _logger.LogInformation(
                                "Full: published {Image} → {RemoteRoot} (share={Share}, program={Program}, list={List})",
                                image.FullName, screen.RemoteRoot,
                                payload.ShareFileName, payload.ProgramFileName, payload.ListFileName);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("FTP"))
            {
                _logger.LogError("[FTP] {Msg}", ex.Message);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Wi-Fi") || ex.Message.Contains("интерфейс") || ex.Message.Contains("маршрут"))
            {
                _logger.LogError("[Wi-Fi] {Msg}", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError("[ФАЙЛ] Не найден файл: {File}. Проверьте что config/screen.xml и layout/ папки есть рядом с exe.",
                    ex.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ОШИБКА] Непредвиденная ошибка в цикле отправки. Повтор через {Sec} сек.",
                    _options.PollSeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken);
        }
    }

    private async Task RunRenderOnlyAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RunMode=RenderOnly (.NET native). Config={Config}, Rates={Rates}",
            _options.ComposeConfigPath,
            _options.RatesJsonPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var latestInputUtc = GetRenderInputsLastWriteUtc();
                if (!_options.ForceComposeEveryPoll
                    && _options.SkipIfUnchanged
                    && latestInputUtc != DateTime.MinValue
                    && latestInputUtc == _lastRenderInputWriteUtc)
                {
                    _logger.LogInformation("RenderOnly: no changes in images/rates, skip compose");
                }
                else
                {
                    var ratesPath = ResolvePath(_options.RatesJsonPath);
                    if (!File.Exists(ratesPath))
                    {
                        _logger.LogInformation(
                            "RenderOnly: rates file is not ready yet: {RatesPath}. Waiting for RatesFetcherService.",
                            ratesPath);
                    }
                    else
                    {
                        var exitCode = await _renderOnlyRunner.RunComposeAsync(_options, stoppingToken);
                        if (exitCode != 0)
                        {
                            _logger.LogError("RenderOnly compose failed with exit code {ExitCode}", exitCode);
                        }
                        else
                        {
                            _lastRenderInputWriteUtc = latestInputUtc;
                            _logger.LogInformation("RenderOnly compose finished successfully");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RenderOnly iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken);
        }
    }

    private DateTime GetRenderInputsLastWriteUtc()
    {
        var latest = DateTime.MinValue;

        var configPath = ResolvePath(_options.ComposeConfigPath);
        if (File.Exists(configPath))
        {
            latest = MaxUtc(latest, File.GetLastWriteTimeUtc(configPath));

            var sourceDir = TryReadSourceDirFromComposeConfig(configPath);
            if (!string.IsNullOrWhiteSpace(sourceDir))
            {
                var resolvedSourceDir = ResolvePath(sourceDir);
                var dir = new DirectoryInfo(resolvedSourceDir);
                if (dir.Exists)
                {
                    foreach (var file in dir.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
                    {
                        latest = MaxUtc(latest, file.LastWriteTimeUtc);
                    }
                }
            }
        }

        var ratesPath = ResolvePath(_options.RatesJsonPath);
        if (File.Exists(ratesPath))
        {
            latest = MaxUtc(latest, File.GetLastWriteTimeUtc(ratesPath));
        }

        return latest;
    }

    private static string? TryReadSourceDirFromComposeConfig(string configPath)
    {
        using var fs = File.OpenRead(configPath);
        using var doc = System.Text.Json.JsonDocument.Parse(fs, new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (doc.RootElement.TryGetProperty("sourceDir", out var sourceDirProp) && sourceDirProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return sourceDirProp.GetString();
        }

        return null;
    }

    private static DateTime MaxUtc(DateTime a, DateTime b) => a >= b ? a : b;

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private bool ShouldPublish(FileInfo image)
    {
        return !string.Equals(_lastPublishedFile, image.FullName, StringComparison.OrdinalIgnoreCase)
               || _lastPublishedWriteUtc != image.LastWriteTimeUtc;
    }

    private static FileInfo? GetNewestImage(string watchFolder)
    {
        var dir = new DirectoryInfo(watchFolder);
        if (!dir.Exists)
        {
            return null;
        }

        return dir.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }
}
