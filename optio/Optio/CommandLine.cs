using System.Text.RegularExpressions;
using Optio.Endpoints;

namespace Optio;

/// <summary>
/// The flags that Optio runs before it builds the host. The configuration provider for the
/// command line does not see them.
/// </summary>
internal static partial class CommandLine
{
    /// <summary>Prints the version of the build.</summary>
    public const string Version = "--version";

    /// <summary>
    /// Asks <see cref="Health.AlivePath"/> of Optio on this machine. The exit code is 0 for a
    /// healthy service, and 1 for all other answers. The <c>HEALTHCHECK</c> of the image uses it,
    /// because the image has no shell and no <c>curl</c>.
    /// </summary>
    public const string HealthCheck = "--healthcheck";

    private const string DefaultPort = "8080";

    /// <summary>The exit code of the flag in <paramref name="args"/>, or <c>null</c> without a
    /// flag.</summary>
    public static async Task<int?> RunAsync(string[] args, TextWriter output)
    {
        if (args.Contains(Version))
        {
            await output.WriteLineAsync(OptioVersion.Value);
            return 0;
        }

        if (args.Contains(HealthCheck))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return await ProbeAsync(
                http,
                ProbeUri(
                    Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
                    Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")
                )
            );
        }

        return null;
    }

    public static async Task<int> ProbeAsync(HttpClient http, Uri uri)
    {
        try
        {
            using var response = await http.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>
    /// The URL of <see cref="Health.AlivePath"/> on this machine: the first HTTP URL of
    /// <c>ASPNETCORE_URLS</c>, else the first port of <c>ASPNETCORE_HTTP_PORTS</c>, else port
    /// 8080. A wildcard host becomes <c>localhost</c>.
    /// </summary>
    public static Uri ProbeUri(string? urls, string? httpPorts)
    {
        var url = urls
            ?.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (url is null)
        {
            var port = httpPorts?.Split([';', ','], StringSplitOptions.TrimEntries)[0];
            url = $"http://localhost:{(string.IsNullOrEmpty(port) ? DefaultPort : port)}";
        }

        return new UriBuilder(WildcardHost().Replace(url, "://localhost"))
        {
            Path = Health.AlivePath,
        }.Uri;
    }

    [GeneratedRegex(@"://(\+|\*|0\.0\.0\.0|\[::\])")]
    private static partial Regex WildcardHost();
}
