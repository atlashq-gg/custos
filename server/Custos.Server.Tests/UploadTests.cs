using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Custos.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests;

public sealed class UploadTests
{
    private const string Path = "/api/v1/uploads";
    private static readonly byte[] SaveFile = MapDataWriter.File(
        new Pinned(
            "OriginHex",
            0.25f,
            0.75f,
            "StorageFacility",
            FakeWarApi.War142.StartedAt!.Value.AddDays(1),
            [("ItemCrates", "RifleC", 20)],
            [("ATLAS REC", [("Vehicles", "TruckC", 2)])]
        )
    );

    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Accepted_upload_returns_a_result_that_conforms_to_the_spec()
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync(Path, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        SpecSchemas.AssertUploadResult(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("size_bytes").GetInt64(), Is.EqualTo(SaveFile.Length));
            Assert.That(
                result.GetProperty("sha256").GetString(),
                Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(SaveFile)))
            );
            Assert.That(
                result.GetProperty("war").GetProperty("war_number").GetInt32(),
                Is.EqualTo(142)
            );
            Assert.That(
                result
                    .GetProperty("stockpiles")
                    .EnumerateArray()
                    .Select(s => s.GetProperty("name").GetString()),
                Is.EqualTo(new[] { "Public", "ATLAS REC" })
            );
            Assert.That(
                result.GetProperty("stockpiles")[0].GetProperty("location").GetString(),
                Is.EqualTo("StorageFacility, OriginHex")
            );
        }
    }

    [Test]
    public async Task War_api_failure_without_an_earlier_answer_is_503()
    {
        _factory.WarApi.Fail = true;
        using var client = _factory.CreateAuthorizedClient();

        await AssertProblemAsync(
            await client.PostAsync(Path, Body(SaveFile)),
            HttpStatusCode.ServiceUnavailable
        );
    }

    [Test]
    public async Task War_api_failure_uses_the_last_answer()
    {
        using var client = _factory.CreateAuthorizedClient();
        (await client.PostAsync(Path, Body(SaveFile))).Dispose();
        _factory.WarApi.Fail = true;
        // The fresh answer expires.
        _factory.Services.GetRequiredService<IMemoryCache>().Remove("war:current");

        using var response = await client.PostAsync(Path, Body(SaveFile));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_factory.WarApi.Calls, Is.EqualTo(2));
            Assert.That(
                result.GetProperty("war").GetProperty("war_number").GetInt32(),
                Is.EqualTo(142)
            );
        }
    }

    [Test]
    public async Task War_api_answer_is_cached()
    {
        using var client = _factory.CreateAuthorizedClient();
        (await client.PostAsync(Path, Body(SaveFile))).Dispose();
        (await client.PostAsync(Path, Body(SaveFile))).Dispose();

        Assert.That(_factory.WarApi.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task War_api_host_is_configurable()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Custos:WarApiBaseUrl", "https://war-service-live-2.foxholeservices.com")
        );
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = _factory
            .CreateAuthorizedClient()
            .DefaultRequestHeaders.Authorization;

        (await client.PostAsync(Path, Body(SaveFile))).Dispose();

        Assert.That(
            _factory.WarApi.LastRequest?.ToString(),
            Is.EqualTo("https://war-service-live-2.foxholeservices.com/api/worldconquest/war")
        );
    }

    [Test]
    public async Task Missing_token_gets_a_bearer_challenge()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync(Path, Body(SaveFile));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(
                response.Headers.WwwAuthenticate.Select(h => h.Scheme),
                Does.Contain("Bearer")
            );
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json")
            );
        }
    }

    [Test]
    public async Task Token_for_a_different_client_is_forbidden()
    {
        using var client = _factory.CreateAuthorizedClient(azp: "some-other-client");
        using var response = await client.PostAsync(Path, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-number")]
    [TestCase("-5")]
    [TestCase("0")]
    public async Task Token_without_a_valid_guild_is_403(string? guild)
    {
        using var client = _factory.CreateAuthorizedClient(guild: guild);

        await AssertProblemAsync(
            await client.PostAsync(Path, Body(SaveFile)),
            HttpStatusCode.Forbidden
        );
    }

    [Test]
    public async Task Development_guild_applies_in_development_only()
    {
        using var development = new CustosFactory(environment: "Development");
        using var developmentClient = development.CreateAuthorizedClient(guild: null);
        using var productionClient = _factory.CreateAuthorizedClient(guild: null);

        using var accepted = await developmentClient.PostAsync(Path, Body(SaveFile));
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await AssertProblemAsync(
            await productionClient.PostAsync(Path, Body(SaveFile)),
            HttpStatusCode.Forbidden
        );
    }

    [Test]
    public async Task Wrong_media_type_is_415()
    {
        using var client = _factory.CreateAuthorizedClient();
        var content = new ByteArrayContent(SaveFile);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        await AssertProblemAsync(
            await client.PostAsync(Path, content),
            HttpStatusCode.UnsupportedMediaType
        );
    }

    [Test]
    public async Task Save_file_of_another_class_is_422()
    {
        using var client = _factory.CreateAuthorizedClient();
        var file = MapDataWriter.File("/Script/War.PlayerSaveGame", MapDataWriter.Properties());

        await AssertProblemAsync(
            await client.PostAsync(Path, Body(file)),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Test]
    public async Task File_that_is_not_gvas_is_422()
    {
        using var client = _factory.CreateAuthorizedClient();

        await AssertProblemAsync(
            await client.PostAsync(Path, Body("NOPE"u8.ToArray())),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Test]
    public async Task Body_over_the_limit_is_413()
    {
        using var client = _factory.CreateAuthorizedClient();
        var body = new byte[CustosFactory.UploadMaxBytes + 1];
        "GVAS"u8.CopyTo(body);

        await AssertProblemAsync(
            await client.PostAsync(Path, Body(body)),
            HttpStatusCode.RequestEntityTooLarge
        );
    }

    [Test]
    public async Task Body_over_the_limit_without_content_length_is_413()
    {
        using var client = _factory.CreateAuthorizedClient();
        var body = new byte[CustosFactory.UploadMaxBytes + 1];
        "GVAS"u8.CopyTo(body);
        var content = new StreamContent(new UnknownLengthStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        await AssertProblemAsync(
            await client.PostAsync(Path, content),
            HttpStatusCode.RequestEntityTooLarge
        );
    }

    [Test]
    public async Task Second_upload_within_the_interval_is_429_with_retry_after()
    {
        using var factory = new CustosFactory(uploadMinIntervalSeconds: 60);
        using var client = factory.CreateAuthorizedClient();

        (await client.PostAsync(Path, Body(SaveFile))).Dispose();
        using var response = await client.PostAsync(Path, Body(SaveFile));

        await AssertProblemAsync(response, HttpStatusCode.TooManyRequests);
        Assert.That(response.Headers.RetryAfter?.Delta?.TotalSeconds, Is.InRange(1, 60));
    }

    [Test]
    public async Task Interval_applies_to_each_user_separately()
    {
        using var factory = new CustosFactory(uploadMinIntervalSeconds: 60);
        using var first = factory.CreateAuthorizedClient(subject: "first");
        using var second = factory.CreateAuthorizedClient(subject: "second");

        (await first.PostAsync(Path, Body(SaveFile))).Dispose();
        using var response = await second.PostAsync(Path, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private static ByteArrayContent Body(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status
    )
    {
        using (response)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json")
            );
            Assert.That(
                await response.Content.ReadAsStringAsync(),
                Does.Contain($"\"status\":{(int)status}")
            );
        }
    }

    /// <summary>A stream that hides its length, so that the client sends the body without
    /// Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
