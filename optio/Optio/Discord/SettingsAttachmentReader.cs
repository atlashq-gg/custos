using Microsoft.Extensions.Options;
using NetCord;

namespace Optio.Discord;

/// <summary>
/// Gets the settings document that an administrator attached to a command, up to
/// <see cref="OptioOptions.MaxSettingsFileBytes"/>. The roles fill less than 4 KiB. The rest is
/// for the limits of members.
/// </summary>
public sealed class SettingsAttachmentReader(HttpClient http, IOptions<OptioOptions> options)
{
    public async Task<(string? Text, string? Refusal)> ReadAsync(
        Attachment file,
        CancellationToken cancellationToken
    )
    {
        var maxBytes = options.Value.MaxSettingsFileBytes;
        if (file.Size > maxBytes)
            return (null, $"The file is larger than {maxBytes} bytes.");

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
