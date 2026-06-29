namespace LedImageUpdaterService.Models;

/// <summary>
/// Configuration for Telegram notifications, mapped from the "Telegram" section of
/// appsettings.json. When <see cref="Enabled"/> is false (default) or the token/chat id
/// are empty, the notifier silently does nothing.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Master switch. When false, no messages are sent.</summary>
    public bool Enabled { get; init; }

    /// <summary>Bot API token issued by @BotFather (e.g. "123456:ABC-DEF...").</summary>
    public string BotToken { get; init; } = "";

    /// <summary>Target chat id (a user, group, or channel). May be negative for groups.</summary>
    public string ChatId { get; init; } = "";
}
