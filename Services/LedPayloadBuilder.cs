using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class LedPayloadBuilder
{
    public PublishPayload Build(ScreenInfo screen, string imagePath)
    {
        string extension = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        if (extension is not ("jpg" or "jpeg" or "png" or "bmp"))
        {
            throw new InvalidOperationException($"Unsupported image format: {extension}");
        }

        // Use fixed (stable) filenames derived from the screen/program IDs so that
        // each publish OVERWRITES the same files on the controller instead of creating
        // new names. This lets the controller pick up image updates automatically
        // without needing a Program_Setplaylist TCP command every time.
        string ext = extension == "jpg" ? "jpeg" : extension;
        var shareFileName = $"auto_{screen.ProgramId[..16]}.{ext}";
        var programFileName = $"auto_{screen.ProgramId[..16]}.xml";
        var listFileName = $"auto_{screen.ScreenId[..16]}.xml";

        string programXml = BuildProgramXml(screen.Width, screen.Height, shareFileName);
        string listXml = BuildListXml(screen.Width, screen.Height, programFileName);

        return new PublishPayload(
            LocalImagePath: imagePath,
            ShareFileName: shareFileName,
            ProgramFileName: programFileName,
            ListFileName: listFileName,
            ProgramXml: programXml,
            ListXml: listXml,
            ProgramId: screen.ProgramId,
            Width: screen.Width,
            Height: screen.Height
        );
    }

    private static string BuildListXml(int width, int height, string programFileName)
    {
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<list deviceType="8792" screenWidth="{width}" screenHeight="{height}">
    <program playMode="1" playTime="1" startDate="" stopDate="" startTime="" stopTime="" weekFlg="127" times="" programFile="/programs/{programFileName}" order="0" dllVersion="23.01.11.001"/>
</list>
""";
    }

    private static string BuildProgramXml(int width, int height, string shareFileName)
    {
        string fileType = Path.GetExtension(shareFileName).TrimStart('.').ToLowerInvariant();

        return $"""
<?xml version="1.0" encoding="utf-8"?>
<program name="program_0" bgColor="">
    <picturepanel x="0" y="0" w="{width}" h="{height}" zOrder="0" transparency="100">
        <picUnit file="/share/{shareFileName}" fileType="{fileType}" stuntType="0" stuntSpeed="16" stayTime="5" order="0"/>
    </picturepanel>
</program>
""";
    }
}
