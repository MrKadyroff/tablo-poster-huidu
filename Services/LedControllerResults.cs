namespace LedImageUpdaterService.Services;

// Transport-neutral result types shared by the ILedController contract and its
// implementations. (Originally declared alongside the Onbon controller; moved here
// when the Huidu transport became the only implementation in this application.)

public enum LedSendErrorType
{
    None,
    FileNotFound,
    InvalidImageSize,
    SdkNotInitialized,
    NativeInteropError,
    SdkSendFailed,
    IsolatedProcessCrashed,
    Cancelled,
    UnexpectedError,
}

public sealed record LedSendStatus(
    bool Success,
    LedSendErrorType ErrorType,
    string Message,
    bool DuplicateSkipped)
{
    public static LedSendStatus Ok(string message, bool duplicateSkipped = false) =>
        new(true, LedSendErrorType.None, message, duplicateSkipped);

    public static LedSendStatus Fail(LedSendErrorType errorType, string message) =>
        new(false, errorType, message, false);
}

/// <summary>Result of a TCP connection check.</summary>
public sealed record ConnectionCheckResult(bool IsOnline, string Details);

/// <summary>Firmware version information from the LED controller.</summary>
public sealed record FirmwareInfo(
    string FirmwareVersion,
    string AppVersion,
    string FpgaVersion);

/// <summary>Live operational status of the LED screen.</summary>
public sealed record ScreenStatusInfo(
    bool IsPoweredOn,
    int Brightness,
    bool BrightnessAuto,
    int Volume,
    bool ScreenLocked,
    bool ProgramLocked,
    string ControllerTime);

/// <summary>Static hardware / configuration info read from the controller.</summary>
public sealed record ControllerHardwareInfo(
    string Barcode,
    string ScreenIp,
    int Port,
    int ScreenWidth,
    int ScreenHeight,
    ushort ScreenType,
    int Brightness,
    string StorageMedia,
    string MacAddress);
