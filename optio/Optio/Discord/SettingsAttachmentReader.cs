using NetCord;

namespace Optio.Discord;

/// <summary>Gets the settings document that an administrator attached to a command.</summary>
public sealed class SettingsAttachmentReader(HttpClient http)
{
    /// <summary>The roles fill less than 4 KiB. The rest is for the limits of members.</summary>
    public const int MaxBytes = 64 * 1024;

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
            return (null, "Optio could not get the file from Discord. Try again.");
        }
    }
}
