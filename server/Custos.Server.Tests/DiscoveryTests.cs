using System.Text.Json;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

public sealed class DiscoveryTests
{
    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Discovery_document_conforms_to_the_spec()
    {
        var document = await GetJsonAsync("/.well-known/custos-configuration");

        SpecSchemas.AssertDiscoveryDocument(document);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                document.GetProperty("custos_versions_supported")[0].GetString(),
                Is.EqualTo("0")
            );
            Assert.That(
                document.GetProperty("upload_endpoint").GetString(),
                Is.EqualTo(CustosFactory.PublicBaseUrl + "/api/v1/uploads")
            );
            Assert.That(
                document.GetProperty("client_id").GetString(),
                Is.EqualTo(CustosFactory.ClientId)
            );
            Assert.That(
                document.GetProperty("openid_configuration").GetString(),
                Is.EqualTo(CustosFactory.Issuer + "/.well-known/openid-configuration")
            );
            Assert.That(
                document.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()),
                Is.EqualTo(["openid"])
            );
            Assert.That(
                document.TryGetProperty("authorization_endpoint", out _),
                Is.False,
                "The endpoints are in the OpenID configuration."
            );
        }
    }

    [Test]
    public async Task Client_metadata_document_names_itself_and_the_redirect_uri()
    {
        var document = await GetJsonAsync("/custos/client-metadata.json");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                document.GetProperty("client_id").GetString(),
                Is.EqualTo(CustosFactory.ClientId)
            );
            Assert.That(
                document.GetProperty("redirect_uris")[0].GetString(),
                Is.EqualTo("custos://oauth2callback")
            );
            Assert.That(
                document.GetProperty("token_endpoint_auth_method").GetString(),
                Is.EqualTo("none")
            );
        }
    }

    [Test]
    public async Task OpenApi_document_describes_the_contract_endpoints()
    {
        var document = await GetJsonAsync("/openapi/v1.json");
        var paths = document.GetProperty("paths");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths.TryGetProperty("/.well-known/custos-configuration", out _), Is.True);
            Assert.That(paths.TryGetProperty("/custos/client-metadata.json", out _), Is.True);
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

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.That(response.IsSuccessStatusCode, Is.True, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
