using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class RenderOnlyRunner
{
    private readonly ILogger<RenderOnlyRunner> _logger;
    private readonly DotnetComposer _composer;

    public RenderOnlyRunner(ILogger<RenderOnlyRunner> logger, DotnetComposer composer)
    {
        _logger = logger;
        _composer = composer;
    }

    public async Task<int> RunComposeAsync(ServiceOptions options, CancellationToken ct)
    {
        var configPath = ResolvePath(options.ComposeConfigPath);
        var ratesPath = ResolvePath(options.RatesJsonPath);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Compose config not found: {configPath}");
        }

        if (!File.Exists(ratesPath))
        {
            throw new FileNotFoundException($"Rates json not found: {ratesPath}");
        }

        var output = await _composer.ComposeAsync(configPath, ratesPath, ct);
        _logger.LogInformation("Compose output: {Path}", output);
        return 0;
    }

    private static string ResolvePath(string rawPath)
    {
        if (Path.IsPathRooted(rawPath))
        {
            return rawPath;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rawPath));
    }
}
