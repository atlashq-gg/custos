using System.Reflection;
using System.Text.Json;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

public sealed class OpenApiTests
{
    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task OpenApi_document_describes_the_upload_api()
    {
        var document = await GetJsonAsync("/openapi/v1.json");
        var paths = document.GetProperty("paths");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths.TryGetProperty("/.well-known/custos-configuration", out _), Is.False);
            Assert.That(
                paths
                    .GetProperty("/api/v1/uploads")
                    .GetProperty("post")
                    .GetProperty("requestBody")
                    .GetProperty("content")
                    .TryGetProperty("application/octet-stream", out _),
                Is.True
            );
        }
    }

    [Test]
    public async Task OpenApi_document_has_the_version_of_the_build()
    {
        var document = await GetJsonAsync("/openapi/v1.json");
        var expected = typeof(Program)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.That(
            document.GetProperty("info").GetProperty("version").GetString(),
            Is.EqualTo(expected)
        );
    }

    [Test]
    public async Task Reference_page_shows_the_OpenApi_document()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/docs");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
            Assert.That(
                await response.Content.ReadAsStringAsync(),
                Does.Contain("\"url\":\"openapi/v1.json\"")
            );
        }
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.That(response.IsSuccessStatusCode, Is.True, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
