using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Json.Schema;

namespace Custos.SystemTests;

/// <summary>The full system starts, and a client of the contract finds the service.</summary>
public sealed class SmokeTests
{
    [Test]
    public async Task Optio_serves_a_discovery_document_that_conforms_to_the_spec()
    {
        using var optio = FullSystem.App.CreateHttpClient("optio");

        var document = await optio.GetFromJsonAsync<JsonElement>(
            "/.well-known/custos-configuration"
        );

        var schema = JsonSchema.FromFile(
            Path.Combine(AppContext.BaseDirectory, "spec", "discovery.schema.json")
        );
        var result = schema.Evaluate(document, new EvaluationOptions());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.True, document.ToString);
            Assert.That(
                document
                    .GetProperty("features_supported")
                    .EnumerateArray()
                    .Select(f => f.GetString()),
                Does.Contain("upload")
            );
        }
    }

    [Test]
    public async Task The_discovery_document_names_the_realm_of_the_system()
    {
        using var optio = FullSystem.App.CreateHttpClient("optio");
        var document = await optio.GetFromJsonAsync<JsonElement>(
            "/.well-known/custos-configuration"
        );
        var openIdConfiguration = document.GetProperty("openid_configuration").GetString();

        using var client = new HttpClient();
        var realm = await client.GetFromJsonAsync<JsonElement>(openIdConfiguration);

        Assert.That(
            realm.GetProperty("issuer").GetString() + "/.well-known/openid-configuration",
            Is.EqualTo(openIdConfiguration)
        );
    }
}
