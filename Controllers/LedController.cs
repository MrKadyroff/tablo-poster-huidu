using LedImageUpdaterService.Models;
using LedImageUpdaterService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Controllers;

/// <summary>
/// REST API for manual LED controller management.
/// Available at /swagger when the application is running.
/// </summary>
[ApiController]
[Route("api/led")]
[Produces("application/json")]
public sealed class LedController : ControllerBase
{
    private readonly ILedController _led;
    private readonly LedBoardService _boardService;
    private readonly InMemoryLogStore _logStore;
    private readonly HuiduOptions _huiduOptions;
    private readonly ServiceOptions _serviceOptions;
    private readonly BoardLinkState _boardLink;
    private readonly BoardLinkMonitor _boardLinkMonitor;

    public LedController(
        ILedController led,
        LedBoardService boardService,
        InMemoryLogStore logStore,
        IOptions<HuiduOptions> huiduOptions,
        IOptions<ServiceOptions> serviceOptions,
        BoardLinkState boardLink,
        BoardLinkMonitor boardLinkMonitor)
    {
        _led = led;
        _boardService = boardService;
        _logStore = logStore;
        _huiduOptions = huiduOptions.Value;
        _serviceOptions = serviceOptions.Value;
        _boardLink = boardLink;
        _boardLinkMonitor = boardLinkMonitor;
    }

    // ─── POST /api/led/update ─────────────────────────────────────────────────

    /// <summary>
    /// Triggers an immediate LED update.
    /// If <c>imagePath</c> is provided, that file is sent; otherwise the latest
    /// image from the configured WatchFolder is used.
    /// </summary>
    /// <remarks>
    /// Example body: <c>{ "imagePath": "C:/images/board.bmp" }</c>
    /// Leave body empty (or omit imagePath) to use the latest watch-folder image.
    /// </remarks>
    [HttpPost("update")]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateRequest? request,
        CancellationToken ct)
    {
        var imagePath = request?.ImagePath?.Trim();

        bool ok = await _boardService.ForceUpdateAsync(
            string.IsNullOrEmpty(imagePath) ? null : imagePath,
            ct);

        var result = new LedOperationResult(
            Success: ok,
            Message: ok
                ? "Image sent successfully."
                : "Failed to send image. Check /api/led/logs for details.");

        return ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    // ─── GET /api/led/connection ──────────────────────────────────────────────

    /// <summary>
    /// Checks TCP connectivity to the LED controller.
    /// Returns controller reachability status (does not require the SDK DLL).
    /// </summary>
    [HttpGet("connection")]
    [ProducesResponseType(typeof(ConnectionStatus), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckConnection(CancellationToken ct)
    {
        var result = await _led.CheckConnectionAsync(ct);

        return Ok(new ConnectionStatus(
            IsOnline: result.IsOnline,
            Details: result.Details,
            CheckedAt: DateTimeOffset.UtcNow));
    }

    // ─── GET /api/led/timer-status ───────────────────────────────────────────

    /// <summary>
    /// Returns runtime state of scheduled auto-send loop (ticks, last success/failure).
    /// Useful to quickly identify timer-send failures.
    /// </summary>
    [HttpGet("timer-status")]
    [ProducesResponseType(typeof(LedBoardRuntimeStatus), StatusCodes.Status200OK)]
    public IActionResult GetTimerStatus()
    {
        return Ok(_boardService.GetRuntimeStatus());
    }

    // ─── GET /api/led/diagnostics ────────────────────────────────────────────

    /// <summary>
    /// Returns an extended diagnostics snapshot:
    /// connection, timer state, SDK options and live controller probes.
    /// </summary>
    [HttpGet("diagnostics")]
    [ProducesResponseType(typeof(LedDiagnosticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnostics(CancellationToken ct)
    {
        var connection = await _led.CheckConnectionAsync(ct);
        var timer = _boardService.GetRuntimeStatus();
        var status = await _led.GetScreenStatusAsync(ct);
        var info = await _led.GetControllerInfoAsync(ct);
        var firmware = await _led.GetFirmwareAsync(ct);

        return Ok(new LedDiagnosticsDto(
            CheckedAt: DateTimeOffset.UtcNow,
            ConnectionOnline: connection.IsOnline,
            ConnectionDetails: connection.Details,
            Timer: timer,
            ActivePointId: HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["ActivePointId"] ?? string.Empty,
            Config: new LedDiagnosticsConfigDto(
                RunMode: _serviceOptions.RunMode,
                WatchFolder: _serviceOptions.WatchFolder,
                LayoutTestMode: _serviceOptions.LayoutTestMode,
                AutoSend: _huiduOptions.AutoSend,
                PollSeconds: _huiduOptions.PollSeconds,
                ListenPort: _huiduOptions.ListenPort,
                CardIp: _huiduOptions.CardIp,
                DeviceId: _huiduOptions.DeviceId,
                ScreenWidth: _huiduOptions.ScreenWidth,
                ScreenHeight: _huiduOptions.ScreenHeight),
            ScreenStatus: status,
            ControllerInfo: info,
            Firmware: firmware));
    }

    // ─── GET /api/led/board-link ──────────────────────────────────────────────

    /// <summary>
    /// Returns the latest "board link" snapshot from <c>BoardLinkMonitor</c>: whether the
    /// PC is on the LED card's Wi-Fi, the discovered card IP, connectivity probe results,
    /// and the recommendation (e.g. which CardIp to set). Null fields until the first scan.
    /// </summary>
    [HttpGet("board-link")]
    [ProducesResponseType(typeof(BoardLinkReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult GetBoardLink()
    {
        var latest = _boardLink.Latest;
        return latest is null ? NoContent() : Ok(latest);
    }

    // ─── POST /api/led/board-link/recheck ─────────────────────────────────────

    /// <summary>Forces an immediate board-link re-scan and returns the fresh report.</summary>
    [HttpPost("board-link/recheck")]
    [ProducesResponseType(typeof(BoardLinkReport), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecheckBoardLink(CancellationToken ct)
        => Ok(await _boardLinkMonitor.RecheckAsync(ct));

    // ─── POST /api/led/clear ──────────────────────────────────────────────────

    /// <summary>
    /// Clears all programs from the LED controller display (makes screen blank).
    /// Requires YQNetCom.dll — only functional on Windows.
    /// </summary>
    [HttpPost("clear")]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ClearScreen(CancellationToken ct)
    {
        bool ok = await _led.ClearScreenAsync(ct);

        var result = new LedOperationResult(
            Success: ok,
            Message: ok
                ? "Screen cleared successfully."
                : "Failed to clear screen. Check /api/led/logs for details.");

        return ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    // ─── GET /api/led/logs ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent log entries produced by LED services.
    /// Use <c>count</c> to limit the number of records (1–500, default 100).
    /// </summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(IReadOnlyList<LogEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetLogs([FromQuery] int count = 100)
    {
        if (count < 1 || count > 500)
            return BadRequest("count must be between 1 and 500.");

        var entries = _logStore
            .GetRecent(count)
            .Select(e => new LogEntryDto(
                e.Timestamp,
                e.Level.ToString(),
                e.Category,
                e.Message))
            .ToList();

        return Ok(entries);
    }

    // ─── POST /api/led/upload ─────────────────────────────────────────────────

    /// <summary>
    /// Uploads an image file and sends it directly to the LED controller.
    /// Accepts BMP, PNG, JPG. Max size: 10 MB.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [ProducesResponseType(typeof(LedSendResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LedSendResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".bmp" or ".png" or ".jpg" or ".jpeg"))
            return BadRequest($"Unsupported file type '{ext}'. Allowed: .bmp .png .jpg .jpeg");

        // Save to temp, send, then delete
        var tmpPath = Path.Combine(Path.GetTempPath(), $"upload_{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var fs = System.IO.File.Create(tmpPath))
                await file.CopyToAsync(fs, ct);

            var status = await _led.SendImageWithStatusAsync(tmpPath, ct);
            var response = new LedSendResponse(
                Success: status.Success,
                Message: status.Success
                    ? $"File '{file.FileName}' sent successfully."
                    : $"Failed to send '{file.FileName}'.",
                ErrorType: status.ErrorType.ToString(),
                ErrorDetails: status.Message,
                DuplicateSkipped: status.DuplicateSkipped);

            if (!status.Success && status.ErrorType == LedSendErrorType.InvalidImageSize)
                return BadRequest(response);

            return status.Success
                ? Ok(response)
                : StatusCode(StatusCodes.Status500InternalServerError, response);
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath))
                try { System.IO.File.Delete(tmpPath); } catch { /* ignore */ }
        }
    }
    // ─── GET /api/led/status ──────────────────────────────────────────────────

    /// <summary>
    /// Reads the live operational status of the LED screen:
    /// power state, brightness, volume, and lock flags.
    /// Requires YQNetCom.dll — only functional on Windows with OnbonLed:Enabled=true.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ScreenStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetScreenStatus(CancellationToken ct)
    {
        var info = await _led.GetScreenStatusAsync(ct);

        if (info is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new LedOperationResult(false, "SDK unavailable or not running on Windows. Check /api/led/logs."));

        return Ok(new ScreenStatusDto(
            IsPoweredOn: info.IsPoweredOn,
            Brightness: info.Brightness,
            BrightnessAuto: info.BrightnessAuto,
            Volume: info.Volume,
            ScreenLocked: info.ScreenLocked,
            ProgramLocked: info.ProgramLocked,
            ControllerTime: info.ControllerTime,
            QueriedAt: DateTimeOffset.UtcNow));
    }

    // ─── GET /api/led/info ────────────────────────────────────────────────────

    /// <summary>
    /// Returns hardware info (screen size, barcode, MAC, etc.) and firmware versions
    /// by querying the controller via SDK.
    /// Requires YQNetCom.dll — only functional on Windows with OnbonLed:Enabled=true.
    /// </summary>
    [HttpGet("info")]
    [ProducesResponseType(typeof(ControllerInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetControllerInfo(CancellationToken ct)
    {
        var hw = await _led.GetControllerInfoAsync(ct);
        var fw = await _led.GetFirmwareAsync(ct);

        if (hw is null && fw is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new LedOperationResult(false, "SDK unavailable or not running on Windows. Check /api/led/logs."));

        return Ok(new ControllerInfoDto(
            Barcode: hw?.Barcode,
            ScreenIp: hw?.ScreenIp,
            Port: hw?.Port,
            ScreenWidth: hw?.ScreenWidth,
            ScreenHeight: hw?.ScreenHeight,
            ScreenType: hw?.ScreenType,
            MacAddress: hw?.MacAddress,
            StorageMedia: hw?.StorageMedia,
            FirmwareVersion: fw?.FirmwareVersion,
            AppVersion: fw?.AppVersion,
            FpgaVersion: fw?.FpgaVersion,
            QueriedAt: DateTimeOffset.UtcNow));
    }

    // ─── POST /api/led/brightness ─────────────────────────────────────────────

    /// <summary>
    /// Sets the LED panel brightness.
    /// Body: <c>{ "value": 128 }</c> — valid range 1–255.
    /// Requires YQNetCom.dll — only functional on Windows.
    /// </summary>
    [HttpPost("brightness")]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SetBrightness(
        [FromBody] BrightnessRequest request,
        CancellationToken ct)
    {
        if (request.Value < 1 || request.Value > 255)
            return BadRequest("Brightness value must be between 1 and 255.");

        bool ok = await _led.SetBrightnessAsync(request.Value, ct);

        var result = new LedOperationResult(
            Success: ok,
            Message: ok
                ? $"Brightness set to {request.Value}."
                : "Failed to set brightness. Check /api/led/logs.");

        return ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    // ─── POST /api/led/power ──────────────────────────────────────────────────

    /// <summary>
    /// Powers the LED screen ON or OFF.
    /// Body: <c>{ "on": true }</c>
    /// Requires YQNetCom.dll — only functional on Windows.
    /// </summary>
    [HttpPost("power")]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SetPower(
        [FromBody] PowerRequest request,
        CancellationToken ct)
    {
        bool ok = await _led.SetPowerAsync(request.On, ct);

        var result = new LedOperationResult(
            Success: ok,
            Message: ok
                ? $"Screen powered {(request.On ? "ON" : "OFF")} successfully."
                : "Failed to change power state. Check /api/led/logs.");

        return ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }

    // ─── POST /api/led/reboot ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a reboot command to the LED controller.
    /// Requires YQNetCom.dll — only functional on Windows.
    /// </summary>
    [HttpPost("reboot")]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LedOperationResult), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RebootController(CancellationToken ct)
    {
        bool ok = await _led.RebootControllerAsync(ct);

        var result = new LedOperationResult(
            Success: ok,
            Message: ok
                ? "Reboot command sent successfully."
                : "Failed to reboot controller. Check /api/led/logs.");

        return ok ? Ok(result) : StatusCode(StatusCodes.Status500InternalServerError, result);
    }
}

// ─── DTO records ──────────────────────────────────────────────────────────────

/// <param name="ImagePath">Optional absolute path to the image file. Leave null to use WatchFolder.</param>
public sealed record UpdateRequest(string? ImagePath);

public sealed record LedOperationResult(bool Success, string Message);

public sealed record ConnectionStatus(bool IsOnline, string Details, DateTimeOffset CheckedAt);

public sealed record LogEntryDto(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message);

public sealed record ScreenStatusDto(
    bool IsPoweredOn,
    int Brightness,
    bool BrightnessAuto,
    int Volume,
    bool ScreenLocked,
    bool ProgramLocked,
    string ControllerTime,
    DateTimeOffset QueriedAt);

public sealed record ControllerInfoDto(
    string? Barcode,
    string? ScreenIp,
    int? Port,
    int? ScreenWidth,
    int? ScreenHeight,
    ushort? ScreenType,
    string? MacAddress,
    string? StorageMedia,
    string? FirmwareVersion,
    string? AppVersion,
    string? FpgaVersion,
    DateTimeOffset QueriedAt);

/// <param name="Value">Brightness level 1–255.</param>
public sealed record BrightnessRequest(int Value);

/// <param name="On">true = power on, false = power off.</param>
public sealed record PowerRequest(bool On);

public sealed record LedSendResponse(
    bool Success,
    string Message,
    string ErrorType,
    string ErrorDetails,
    bool DuplicateSkipped);

public sealed record LedDiagnosticsConfigDto(
    string RunMode,
    string WatchFolder,
    bool LayoutTestMode,
    bool AutoSend,
    int PollSeconds,
    int ListenPort,
    string CardIp,
    string DeviceId,
    int ScreenWidth,
    int ScreenHeight);

public sealed record LedDiagnosticsDto(
    DateTimeOffset CheckedAt,
    bool ConnectionOnline,
    string ConnectionDetails,
    LedBoardRuntimeStatus Timer,
    string ActivePointId,
    LedDiagnosticsConfigDto Config,
    ScreenStatusInfo? ScreenStatus,
    ControllerHardwareInfo? ControllerInfo,
    FirmwareInfo? Firmware);
