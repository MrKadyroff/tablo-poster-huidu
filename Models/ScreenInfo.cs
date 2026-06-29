namespace LedImageUpdaterService.Models;

public sealed record ScreenInfo(
    string ScreenId,
    string ProgramId,
    int Width,
    int Height,
    string FtpIp,
    int FtpPort,
    string RemoteRoot,
    string ProgramPathFromScreenXml,
    /// <summary>All known IPs for this controller (from screen.xml + network.json).</summary>
    IReadOnlyList<string> FtpIpCandidates
);
