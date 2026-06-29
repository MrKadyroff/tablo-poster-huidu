namespace LedImageUpdaterService.UI;

/// <summary>
/// Reference catalog of Onbon BX-Y series controllers and their SDK device-type
/// codes. Used to auto-populate OnbonLed.DeviceType from a friendly model name.
/// Codes match the documentation in Models/OnbonOptions.cs.
/// </summary>
internal static class ControllerCatalog
{
    public sealed record ControllerModel(string Name, int DeviceType, string Note);

    public static readonly IReadOnlyList<ControllerModel> Models =
    [
        new("BX-Y04",  8280,  "Базовый, монохром/RGB"),
        new("BX-Y08",  8536,  "8 портов"),
        new("BX-Y1",   9560,  "Серия Y1"),
        new("BX-Y1L",  10072, "Y1, увеличенная память"),
        new("BX-Y2",   8792,  "Популярный, RGB"),
        new("BX-Y2L",  9304,  "Y2, увеличенная память"),
        new("BX-Y3",   9048,  "Серия Y3"),
        new("BX-Y5E",  10584, "Ethernet, большие экраны"),
        new("BX-C08",  33026, "Серия C, 8 портов"),
    ];

    public const string CustomLabel = "Другой (вручную)";

    public static ControllerModel? FindByDeviceType(int deviceType)
        => Models.FirstOrDefault(m => m.DeviceType == deviceType);

    public static string DisplayName(ControllerModel m)
        => $"{m.Name}  —  {m.DeviceType}  ({m.Note})";
}

/// <summary>
/// LED controller family: which transport/SDK the service uses. Persisted as
/// <c>Led:Family</c> in appsettings/point config (see Models/LedControllerOptions.cs).
/// </summary>
internal static class ControllerFamilyCatalog
{
    /// <param name="Key">Stable value persisted in config (Onbon | Huidu).</param>
    /// <param name="Label">Friendly label shown in the settings combo box.</param>
    public sealed record Family(string Key, string Label);

    public const string Onbon = "Onbon";
    public const string Huidu = "Huidu";

    // This application drives Huidu cards only; Onbon lives in the separate eCash Tablo app.
    public static readonly IReadOnlyList<Family> Families =
    [
        new(Huidu, "Huidu / HDPlayer (BX A3L, C16L и др.)"),
    ];

    public static int IndexOfKey(string? key)
    {
        for (int i = 0; i < Families.Count; i++)
            if (string.Equals(Families[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0; // default Huidu
    }
}

/// <summary>
/// Reference catalog of Huidu async full-color controllers driven through HDPlayer.
/// Unlike Onbon, Huidu cards are NOT addressed by a numeric device-type code — the
/// model is informational. The actual panel resolution is set by the physical screen,
/// not the controller, so <see cref="HuiduModel.DefaultWidth"/>/<see cref="HuiduModel.DefaultHeight"/>
/// are only a convenience pre-fill (0 = unknown, leave the size as-is). The only
/// verified size is the BX A3L sample from the Wireshark dump (1216×192).
/// </summary>
internal static class HuiduControllerCatalog
{
    /// <param name="Name">Model name as printed on the card / shown in HDPlayer.</param>
    /// <param name="DefaultWidth">Convenience pre-fill for the panel width (0 = leave current).</param>
    /// <param name="DefaultHeight">Convenience pre-fill for the panel height (0 = leave current).</param>
    /// <param name="Note">Series / capability hint shown in the settings combo.</param>
    public sealed record HuiduModel(string Name, int DefaultWidth, int DefaultHeight, string Note);

    // Huidu async full-color, "L" = built-in LAN sending. Grouped by series (A = entry/
    // banner, C = mid-range, D/E = large screens). Resolution depends on the panel, so
    // only the documented A3L sample carries a default size.
    public static readonly IReadOnlyList<HuiduModel> Models =
    [
        // ── A-series (entry / banners) ──
        new("BX A1L",  0, 0,       "A-серия (вход), 1 LAN, HDPlayer"),
        new("BX A2L",  0, 0,       "A-серия (вход), 1 LAN, HDPlayer"),
        new("BX A3L",  1216, 192,  "A-серия, 1 LAN, HDPlayer (тест: 1216×192)"),
        new("BX A4L",  0, 0,       "A-серия, 1 LAN, HDPlayer"),
        new("BX A5L",  0, 0,       "A-серия, 1 LAN, HDPlayer"),
        new("BX A6L",  0, 0,       "A-серия, 1 LAN, HDPlayer"),
        // ── C-series (mid-range) ──
        new("BX C10L", 0, 0,       "C-серия, HDPlayer"),
        new("BX C12L", 0, 0,       "C-серия, HDPlayer"),
        new("BX C15L", 0, 0,       "C-серия, HDPlayer"),
        new("BX C16L", 0, 0,       "C-серия, HDPlayer"),
        new("BX C30L", 0, 0,       "C-серия, HDPlayer"),
        new("BX C35L", 0, 0,       "C-серия, HDPlayer"),
        // ── D / E-series (large screens) ──
        new("BX D15L", 0, 0,       "D-серия (крупные экраны), HDPlayer"),
        new("BX D35L", 0, 0,       "D-серия (крупные экраны), HDPlayer"),
        new("BX E40",  0, 0,       "E-серия (крупные экраны), HDPlayer"),
        new("BX E62",  0, 0,       "E-серия (крупные экраны), HDPlayer"),
    ];

    public const string CustomLabel = "Другая модель (вручную)";

    public static HuiduModel? FindByName(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(HuiduModel m) => $"{m.Name}  —  {m.Note}";
}

/// <summary>
/// Communication methods supported by the Onbon SDK. Per the SDK demo
/// (BX_Y_CSharp_SDK Program.cs: "1.固定IP，2.服务器模式") there are exactly two:
/// a fixed/direct IP connection and a server (cloud) connection. A "direct"
/// connection simply means talking to the controller's fixed IP on its port — it
/// is the same thing the user refers to as "по IP"; the server mode is only for
/// controllers that dial out to a server (e.g. 4G / no static LAN IP).
/// These map to the LedShow screen.xml <c>ip_mode</c> attribute (1 = fixed IP,
/// 2 = server), matching the working descriptor sample (ip_mode="1").
/// </summary>
internal static class ConnectionModeCatalog
{
    /// <param name="Key">Stable value persisted in appsettings (OnbonLed:ConnectionMode).</param>
    /// <param name="Label">Friendly label shown in the settings combo box.</param>
    /// <param name="IpMode">LedShow screen.xml <c>ip_mode</c> value.</param>
    public sealed record ConnectionMode(string Key, string Label, int IpMode);

    public const string DefaultKey = "Ip";

    public static readonly IReadOnlyList<ConnectionMode> Modes =
    [
        new("Ip",     "Прямое — по IP (фиксированный IP)", 1),
        new("Server", "Сервер (облако)",                   2),
    ];

    public static ConnectionMode FindByKey(string? key)
        => Modes.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
           ?? Modes[0];
}
