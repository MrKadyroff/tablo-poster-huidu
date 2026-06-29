using System.Text;
using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class RelayPublisher : IPublishStrategy
{
    public string Mode => ServiceOptions.PublishModeWifiRelay;

    public async Task PublishAsync(ScreenInfo screen, ServiceOptions options, PublishPayload payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.RelayOutRoot))
        {
            throw new InvalidOperationException("RelayOutRoot must be set for WifiRelay mode");
        }

        string root = options.RelayOutRoot;
        string screenRoot = Path.Combine(root, screen.ScreenId);
        string listsDir = Path.Combine(screenRoot, "lists");
        string programsDir = Path.Combine(screenRoot, "programs", "program_0");
        string shareDir = Path.Combine(screenRoot, "share");
        string programOwnedImageDir = Path.Combine(root, screen.ProgramId);

        Directory.CreateDirectory(listsDir);
        Directory.CreateDirectory(programsDir);
        Directory.CreateDirectory(shareDir);
        Directory.CreateDirectory(programOwnedImageDir);

        string copiedImageInShare = Path.Combine(shareDir, payload.ShareFileName);
        string copiedImageInProgramDir = Path.Combine(programOwnedImageDir, payload.ShareFileName);
        string listFilePath = Path.Combine(listsDir, payload.ListFileName);
        string programFilePath = Path.Combine(programsDir, payload.ProgramFileName);

        File.Copy(payload.LocalImagePath, copiedImageInShare, overwrite: true);
        File.Copy(payload.LocalImagePath, copiedImageInProgramDir, overwrite: true);

        await File.WriteAllTextAsync(programFilePath, payload.ProgramXml, Encoding.UTF8, ct);
        await File.WriteAllTextAsync(listFilePath, payload.ListXml, Encoding.UTF8, ct);
    }
}
