namespace LedImageUpdaterService.Models;

public sealed record PublishPayload(
    string LocalImagePath,
    string ShareFileName,
    string ProgramFileName,
    string ListFileName,
    string ProgramXml,
    string ListXml,
    string ProgramId,
    int Width,
    int Height
);
