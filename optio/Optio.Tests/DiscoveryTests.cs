using System.Text.Json;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

public sealed class DiscoveryTests
{
    private OptioFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new OptioFactory();

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
                Is.EqualTo(OptioFactory.PublicBaseUrl + "/api/v1/uploads")
            );
            Assert.That(
                document.GetProperty("client_id").GetString(),
                Is.EqualTo(OptioFactory.ClientId)
            );
            Assert.That(
                document.GetProperty("openid_configuration").GetString(),
                Is.EqualTo(OptioFactory.Issuer + "/.well-known/openid-configuration")
            );
            Assert.That(
                document.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()),
                Is.EqualTo(["openid", "custos:client"])
            );
            Assert.That(document.TryGetProperty("service_documentation", out _), Is.False);
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
        var document = await GetJsonAsync("/.well-known/custos-client");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                document.GetProperty("client_id").GetString(),
                Is.EqualTo(OptioFactory.ClientId)
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

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.That(response.IsSuccessStatusCode, Is.True, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
