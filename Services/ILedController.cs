namespace LedImageUpdaterService.Services;

/// <summary>
/// Common abstraction over an LED controller transport so the rest of the service
/// (REST API <see cref="Controllers.LedController"/> and background
/// <see cref="LedBoardService"/>) does not depend on a specific hardware family.
///
/// Two implementations exist:
///   • <see cref="OnbonLedController"/>  — Onbon BX-Y series via the native YQNetCom.dll SDK.
///   • <see cref="HuiduLedController"/>   — Huidu cards (e.g. BX A3L) via the HDPlayer/Huidu
///                                          XML-over-TCP SDK protocol.
///
/// The active implementation is selected at startup from <c>Led:Family</c>
/// (see <see cref="Models.LedControllerOptions"/>). Both families receive the same
/// full-screen rendered image; only the delivery transport differs.
/// </summary>
public interface ILedController
{
    /// <summary>Checks whether the controller is reachable.</summary>
    Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct = default);

    /// <summary>Sends an image file to the LED controller (full-screen).</summary>
    Task<bool> SendImageAsync(string imagePath, CancellationToken ct = default);

    /// <summary>Sends an image file and returns typed success/error details.</summary>
    Task<LedSendStatus> SendImageWithStatusAsync(string imagePath, CancellationToken ct = default);

    /// <summary>Clears all programs from the display (blank screen).</summary>
    Task<bool> ClearScreenAsync(CancellationToken ct = default);

    /// <summary>Reads live screen status (power, brightness, lock flags). Null if unsupported/unavailable.</summary>
    Task<ScreenStatusInfo?> GetScreenStatusAsync(CancellationToken ct = default);

    /// <summary>Reads static hardware info. Null if unsupported/unavailable.</summary>
    Task<ControllerHardwareInfo?> GetControllerInfoAsync(CancellationToken ct = default);

    /// <summary>Reads firmware versions. Null if unsupported/unavailable.</summary>
    Task<FirmwareInfo?> GetFirmwareAsync(CancellationToken ct = default);

    /// <summary>Sets panel brightness (1–255).</summary>
    Task<bool> SetBrightnessAsync(int brightness, CancellationToken ct = default);

    /// <summary>Powers the screen ON (<c>true</c>) or OFF (<c>false</c>).</summary>
    Task<bool> SetPowerAsync(bool on, CancellationToken ct = default);

    /// <summary>Reboots the controller.</summary>
    Task<bool> RebootControllerAsync(CancellationToken ct = default);
}
