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
        new(Huidu, "Huidu / HDPlayer (BX A3L)"),
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
/// Reference catalog of Huidu controllers driven through HDPlayer / the Huidu SDK.
/// Unlike Onbon, Huidu cards are not addressed by a numeric device-type code, so this
/// is informational (model name + screen note). BX A3L is the supported model.
/// </summary>
internal static class HuiduControllerCatalog
{
    public sealed record HuiduModel(string Name, string Note);

    public static readonly IReadOnlyList<HuiduModel> Models =
    [
        new("BX A3L",  "Huidu async full-color, управляется через HDPlayer"),
        new("BX C16L", "Huidu async full-color, управляется через HDPlayer"),
    ];
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
