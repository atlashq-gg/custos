using System.Text.Json;

namespace Optio.Tests.Infrastructure;

/// <summary>One item of an SSE stream: an event with its id and data, or a comment.</summary>
internal sealed record SseItem(string? Id, JsonElement? Data, string? Comment);

/// <summary>Reads the items of an SSE response, one at a time.</summary>
internal sealed class SseReader(HttpResponseMessage response) : IDisposable
{
    private StreamReader? _reader;

    /// <summary>The next item, or null when the stream ended.</summary>
    public async Task<SseItem?> NextAsync(TimeSpan? timeout = null)
    {
        _reader ??= new StreamReader(await response.Content.ReadAsStreamAsync());
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        string? id = null;
        JsonElement? data = null;
        while (await _reader.ReadLineAsync(cancel.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (id is not null || data is not null)
                    return new SseItem(id, data, null);
                continue;
            }
            if (line.StartsWith(':'))
            {
                // A comment ends at its empty line.
                await _reader.ReadLineAsync(cancel.Token);
                return new SseItem(null, null, line[1..].Trim());
            }
            var (field, value) = line.IndexOf(':') is var i and >= 0
                ? (line[..i], line[(i + 1)..].TrimStart())
                : (line, "");
            if (field == "id")
                id = value;
            else if (field == "data")
                data = JsonDocument.Parse(value).RootElement.Clone();
        }
        return null;
    }

    /// <summary>True when no item comes within <paramref name="wait"/>.</summary>
    public async Task<bool> IsQuietAsync(TimeSpan wait)
    {
        try
        {
            await NextAsync(wait);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        response.Dispose();
    }
}
