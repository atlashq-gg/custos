using NetCord;

namespace Custos.Server.Discord;

/// <summary>Gets the text of a settings file that a user attached to a command.</summary>
public sealed class SettingsAttachmentReader(HttpClient http)
{
    /// <summary>A settings document with 3 full role lists and 1 full guild list has less than
    /// 3 KiB.</summary>
    public const int MaxBytes = 16 * 1024;

    public async Task<(string? Text, string? Refusal)> ReadAsync(
        Attachment file,
        CancellationToken cancellationToken
    )
    {
        if (file.Size > MaxBytes)
            return (null, $"The file is larger than {MaxBytes} bytes.");

        try
        {
            return (await http.GetStringAsync(file.Url, cancellationToken), null);
        }
        catch (HttpRequestException)
        {
            return (null, "The server could not get the file from Discord. Try again.");
        }
    }
}
