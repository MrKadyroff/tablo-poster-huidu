using System.Text.Json.Serialization;

namespace LedImageUpdaterService.Services.DoubleSided;

/// <summary>
/// Settings of the «Аэропорт» mode (section <c>doubleSided</c> of a point's compose.json):
/// one layout is drawn on a single-side canvas and then duplicated to both faces of a
/// double-sided board, glued into one picture for the controller.
/// </summary>
public sealed class DoubleSidedOptions
{
    public const string Horizontal = "horizontal";
    public const string Vertical = "vertical";

    public const string SecondSame = "same";
    public const string SecondMirror = "mirror";
    public const string SecondRotate180 = "rotate180";
    public const string SecondFlipVertical = "flipVertical";

    public bool Enabled { get; set; }

    /// <summary>"horizontal" (sides left/right, each rotated) or "vertical" (A on top of B, no rotation).</summary>
    public string JoinLayout { get; set; } = Horizontal;

    /// <summary>Clockwise rotation of each side for the horizontal join: 90 or 270.</summary>
    public int SideRotation { get; set; } = 270;

    /// <summary>same | mirror | rotate180 | flipVertical — how side B differs from side A.</summary>
    public string SecondSide { get; set; } = SecondMirror;

    [JsonIgnore]
    public bool IsHorizontal => !string.Equals(JoinLayout, Vertical, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a copy with every field forced into its allowed range.</summary>
    public DoubleSidedOptions Normalized()
    {
        var join = string.Equals(JoinLayout, Vertical, StringComparison.OrdinalIgnoreCase) ? Vertical : Horizontal;
        var rot = SideRotation == 90 ? 90 : 270;
        var second = SecondSide?.Trim().ToLowerInvariant() switch
        {
            "same" => SecondSame,
            "rotate180" => SecondRotate180,
            "flipvertical" => SecondFlipVertical,
            _ => SecondMirror,
        };
        return new DoubleSidedOptions
        {
            Enabled = Enabled,
            JoinLayout = join,
            SideRotation = rot,
            SecondSide = second,
        };
    }

    /// <summary>
    /// Size of the picture sent to the controller (and of the card screen) for a side canvas
    /// of <paramref name="sideW"/>×<paramref name="sideH"/>. Horizontal: both sides rotated by
    /// 90° and placed next to each other (192×256 → 512×192). Vertical: stacked as is (→ 192×512).
    /// </summary>
    public (int Width, int Height) GetJoinedSize(int sideW, int sideH) =>
        IsHorizontal ? (sideH * 2, sideW) : (sideW, sideH * 2);
}
