using LedImageUpdaterService;
using LedImageUpdaterService.Controllers;
using LedImageUpdaterService.Models;
using LedImageUpdaterService.Services;
using LedImageUpdaterService.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

// ─── Huidu diagnostic mode (read-only card probe) ─────────────────────────────
// Run:  LedImageUpdaterService.exe --huidu-diag
if (HuiduDiagnostics.IsDiagInvocation(args))
{
    Environment.ExitCode = await HuiduDiagnostics.RunAsync(args);
    return;
}

// ─── Decide: Windows Service vs Tray app ─────────────────────────────────────
bool runAsService = WindowsServiceHelpers.IsWindowsService() || args.Contains("--service");

if (runAsService)
{
    // Headless service mode (installed as Windows Service)
    var app = Program.BuildWebApp(args);
    var second = await Program.TryStartSecondBoardAsync(args);
    try
    {
        await app.RunAsync();
    }
    finally
    {
        if (second is not null)
        {
            try { await second.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await second.DisposeAsync(); } catch { }
        }
    }
}
else
{
    // Interactive tray-app mode — single instance only.
    using var singleInstance = new Mutex(initiallyOwned: true, "eCashTablo_SingleInstance_Mutex", out bool isNew);
    if (!isNew)
    {
        MessageBox.Show(
            "eCash Tablo уже запущен.\nЗначок находится в области уведомлений (рядом с часами).",
            "eCash Tablo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    // Global safety net: log unhandled exceptions and keep the UI alive instead of
    // letting Windows kill the app with the default crash dialog.
    CrashLogger.Install();

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new TrayApplicationContext(args));
    GC.KeepAlive(singleInstance);
}

// ─── Shared host factory ──────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>
    /// Builds the second board's host when <c>SecondPointId</c> is configured, otherwise null.
    /// It is a full copy of the pipeline for that point on the next API port, so the two
    /// boards render and send independently of each other.
    /// </summary>
    internal static WebApplication? BuildSecondBoardWebApp(string[] args)
    {
        var probe = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var secondId = BoardTopology.ResolveSecondPointId(probe, AppContext.BaseDirectory);
        if (secondId is null) return null;

        // Same controller IP on both boards = the second picture would go to the first board.
        var clash = BoardTopology.FindIpClash(AppContext.BaseDirectory, probe["ActivePointId"] ?? "", secondId);
        if (clash is not null)
            throw new InvalidOperationException(
                clash + "\nЗадайте разные IP на вкладке «Подключение» — второе табло не запущено, чтобы не перепутать экраны.");

        var urls = BoardTopology.SecondUrls(probe["Urls"] ?? "http://localhost:5050", probe["SecondUrls"]);
        return BuildWebApp(args, secondId, urls);
    }

    /// <summary>Starts the second board (service mode). Failure never takes the first board down.</summary>
    internal static async Task<WebApplication?> TryStartSecondBoardAsync(string[] args)
    {
        try
        {
            var app = BuildSecondBoardWebApp(args);
            if (app is null) return null;
            await app.StartAsync();
            return app;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Second board failed to start: {ex.Message}");
            return null;
        }
    }

    internal static WebApplication BuildWebApp(string[] args, string? pointId = null, string? urls = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Second board: same pipeline, different point and API address.
        if (pointId is not null) builder.Configuration["ActivePointId"] = pointId;
        if (urls is not null) builder.WebHost.UseUrls(urls);

        // Logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        // Persist the full ILogger firehose to a day-rolling file under <app>/logs.
        // Survives restarts and works headless (Windows Service / tray, where there is no console).
        builder.Logging.AddFile();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // ─── Point config overlay ─────────────────────────────────────────
        var activePointId = builder.Configuration["ActivePointId"];
        if (string.IsNullOrWhiteSpace(activePointId))
            throw new InvalidOperationException(
                "ActivePointId is not set in appsettings.json.");

        var pointConfigPath = Path.Combine(
            builder.Environment.ContentRootPath, "config", "points", $"{activePointId}.json");

        if (!File.Exists(pointConfigPath))
            throw new InvalidOperationException(
                $"Point config file not found: {pointConfigPath}");

        builder.Configuration.AddJsonFile(pointConfigPath, optional: false, reloadOnChange: true);

        // Windows Service support (the second board lives inside the same service process)
        if (pointId is null)
        {
            builder.Host.UseWindowsService(options =>
            {
                options.ServiceName = "eCashTabloService";
            });
        }

        // ─── Options ──────────────────────────────────────────────────────
        builder.Services.AddOptions<ServiceOptions>()
            .Bind(builder.Configuration.GetSection(ServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<HuiduOptions>()
            .Bind(builder.Configuration.GetSection(HuiduOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Telegram notifications are optional; bind without ValidateOnStart so a
        // missing/empty section never blocks startup.
        builder.Services.AddOptions<TelegramOptions>()
            .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName));

        // ─── Infrastructure ───────────────────────────────────────────────
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<InMemoryLogStore>();
        builder.Services.AddSingleton<TelegramNotifier>();
        builder.Services.AddSingleton<BoardLinkState>();

        // ─── Domain services ──────────────────────────────────────────────
        builder.Services.AddSingleton<ScreenModelReader>();
        builder.Services.AddSingleton<LedPayloadBuilder>();
        builder.Services.AddSingleton<WifiNetworkGuard>();
        builder.Services.AddSingleton<ControllerDiscovery>();
        builder.Services.AddSingleton<DotnetComposer>();
        builder.Services.AddSingleton<RenderOnlyRunner>();
        builder.Services.AddSingleton<IPublishStrategy, FtpPublisher>();
        builder.Services.AddSingleton<IPublishStrategy, RelayPublisher>();

        // LED transport: Huidu HDPlayer (TCP push) by default, or FTP upload to a fixed IP
        // when HuiduLed:Transport = "Ftp". Both implement ILedController, so the manual
        // "Отправить на табло" button and the background auto-send loop pick up the choice.
        builder.Services.AddSingleton<HuiduLedController>();
        builder.Services.AddSingleton<FtpLedController>();
        builder.Services.AddSingleton<ILedController>(sp =>
        {
            var transport = sp.GetRequiredService<IOptions<HuiduOptions>>().Value.Transport;
            return string.Equals(transport, HuiduOptions.TransportFtp, StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<FtpLedController>()
                : sp.GetRequiredService<HuiduLedController>();
        });

        // ─── Background workers ───────────────────────────────────────────
        // One fetcher refreshes the rates of ALL points; the second board's host skips it.
        if (pointId is null)
            builder.Services.AddHostedService<RatesFetcherService>();
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddSingleton<LedBoardService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LedBoardService>());
        builder.Services.AddSingleton<BoardLinkMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BoardLinkMonitor>());

        // ─── Web API + Swagger ────────────────────────────────────────────
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();

        const string CorsPolicy = "AllowAll";
        builder.Services.AddCors(options =>
            options.AddPolicy(CorsPolicy, p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "eCash Tablo Huidu — LED Management API",
                Version = "v1",
                Description = "REST API for manual control of the Huidu (HDPlayer) LED controller.",
            });
        });

        // ─── Build & configure pipeline ───────────────────────────────────
        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "eCash Tablo API v1");
            c.RoutePrefix = string.Empty;
            c.DocumentTitle = "eCash Tablo — LED API";
        });

        app.UseCors(CorsPolicy);
        app.MapControllers();

        return app;
    }
}
