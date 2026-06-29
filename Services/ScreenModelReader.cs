using System.Text.Json;
using System.Xml.Linq;
using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class ScreenModelReader
{
    public ScreenInfo Read(
        string screenXmlPath,
        string? forceRemoteRoot,
        string? networkJsonPath = null)
    {
        var doc = XDocument.Load(screenXmlPath);
        XNamespace ns = "http://Ledshow.Xml/Model/Xml";

        var screen = doc.Descendants(ns + "screen").FirstOrDefault();
        if (screen is null)
        {
            throw new InvalidOperationException("Cannot find <screen> node in screen.xml");
        }

        var program = screen.Elements(ns + "program").FirstOrDefault();
        if (program is null)
        {
            throw new InvalidOperationException("Cannot find <program> under <screen> in screen.xml");
        }

        string screenId = GetAttr(screen, "unique_identifier");
        string programId = GetAttr(program, "unique_identifier");
        int width = int.Parse(GetAttr(screen, "w"));
        int height = int.Parse(GetAttr(screen, "h"));
        string ftpIp = GetAttr(screen, "ftp_ip");
        int ftpPort = int.Parse(GetAttr(screen, "ftp_port"));
        string remoteRoot = string.IsNullOrWhiteSpace(forceRemoteRoot)
            ? GetAttr(screen, "remote_ftp_dir")
            : forceRemoteRoot.Trim();

        string programPath = GetAttr(program, "path");

        // Collect all candidate IPs (screen.xml + network.json if available)
        var candidates = new List<string> { ftpIp };
        if (networkJsonPath is not null && File.Exists(networkJsonPath))
        {
            try
            {
                using var netDoc = JsonDocument.Parse(File.ReadAllText(networkJsonPath));
                if (netDoc.RootElement.TryGetProperty("network", out var netEl))
                {
                    foreach (var iface in netEl.EnumerateObject())
                    {
                        if (iface.Value.TryGetProperty("ip", out var ipEl))
                        {
                            var ip = ipEl.GetString();
                            if (!string.IsNullOrWhiteSpace(ip) && !candidates.Contains(ip))
                                candidates.Add(ip);
                        }
                    }
                }
            }
            catch { /* malformed network.json — ignore, use screen.xml IP only */ }
        }

        return new ScreenInfo(
            ScreenId: screenId,
            ProgramId: programId,
            Width: width,
            Height: height,
            FtpIp: ftpIp,
            FtpPort: ftpPort,
            RemoteRoot: remoteRoot,
            ProgramPathFromScreenXml: programPath,
            FtpIpCandidates: candidates
        );
    }

    private static string GetAttr(XElement element, string attr)
    {
        var value = element.Attribute(attr)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required attribute '{attr}' in element '{element.Name.LocalName}'");
        }

        return value;
    }
}
