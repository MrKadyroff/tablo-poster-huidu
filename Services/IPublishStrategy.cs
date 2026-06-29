using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public interface IPublishStrategy
{
    string Mode { get; }

    Task PublishAsync(ScreenInfo screen, ServiceOptions options, PublishPayload payload, CancellationToken ct);
}
