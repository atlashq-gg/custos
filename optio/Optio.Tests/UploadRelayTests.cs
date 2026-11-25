using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

public sealed class UploadRelayTests
{
    private const string UploadPath = "/api/v1/uploads";

    private OptioFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new OptioFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Upload_goes_to_the_server_with_an_exchanged_token()
    {
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        var (authorization, form) = _factory.AuthorizationServer.Requests.Single();
        var upload = _factory.UploadApi.Requests.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                await response.Content.ReadAsStringAsync(),
                Is.EqualTo("{\"status\":\"accepted\"}")
            );
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/json")
            );
            Assert.That(
                form["grant_type"],
                Is.EqualTo("urn:ietf:params:oauth:grant-type:token-exchange")
            );
            Assert.That(form["subject_token"], Is.EqualTo(Bearer(client)));
            Assert.That(form["scope"], Is.EqualTo("custos:write"));
            Assert.That(form.ContainsKey("audience"), Is.False);
            Assert.That(
                authorization,
                Is.EqualTo(
                    "Basic "
                        + Convert.ToBase64String(
                            Encoding.UTF8.GetBytes($"optio:{OptioFactory.Secret}")
                        )
                )
            );
            Assert.That(upload.Authorization, Is.EqualTo("Bearer exchanged-1"));
            Assert.That(upload.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(Encoding.ASCII.GetString(upload.Body), Is.EqualTo("GVAS"));
        }
    }

    [Test]
    public async Task Exchanged_token_is_used_again_until_it_expires()
    {
        using var client = _factory.CreateCustosClient();

        (await client.PostAsync(UploadPath, OptioFactory.OctetStream("1"))).Dispose();
        (await client.PostAsync(UploadPath, OptioFactory.OctetStream("2"))).Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_factory.AuthorizationServer.Requests, Has.Count.EqualTo(1));
            Assert.That(_factory.UploadApi.Requests, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task Answer_of_the_server_comes_back_with_no_change()
    {
        _factory.UploadApi.Respond = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    "{\"title\":\"Too many uploads\"}",
                    Encoding.UTF8,
                    "application/problem+json"
                ),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        };
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(42)));
            Assert.That(
                response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json")
            );
            Assert.That(
                await response.Content.ReadAsStringAsync(),
                Is.EqualTo("{\"title\":\"Too many uploads\"}")
            );
        }
    }

    [Test]
    public async Task Token_of_another_client_is_403()
    {
        using var client = _factory.CreateCustosClient(azp: "another-client");

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_factory.UploadApi.Requests, Is.Empty);
    }

    [TestCase("http://localhost/mcp")]
    [TestCase(null)]
    public async Task Token_without_the_audience_optio_is_401(string? audience)
    {
        using var client = audience is null
            ? _factory.CreateClient()
            : _factory.CreateCustosClient(audience: audience);

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_factory.AuthorizationServer.Requests, Is.Empty);
    }

    [Test]
    public async Task Other_content_type_is_415()
    {
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(UploadPath, new StringContent("GVAS"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
        Assert.That(_factory.AuthorizationServer.Requests, Is.Empty);
    }

    [Test]
    public async Task Body_larger_than_the_limit_is_413()
    {
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(
            UploadPath,
            OptioFactory.OctetStream(new string('x', OptioFactory.UploadMaxBytes + 1))
        );

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(_factory.UploadApi.Requests, Is.Empty);
    }

    [Test]
    public async Task Refused_exchange_is_503()
    {
        _factory.AuthorizationServer.Status = HttpStatusCode.BadRequest;
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(_factory.UploadApi.Requests, Is.Empty);
    }

    [Test]
    public async Task Server_that_does_not_answer_is_503()
    {
        _factory.UploadApi.Respond = () => throw new HttpRequestException("No connection.");
        using var client = _factory.CreateCustosClient();

        using var response = await client.PostAsync(UploadPath, OptioFactory.OctetStream("GVAS"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    private static string Bearer(HttpClient client) =>
        client.DefaultRequestHeaders.Authorization!.Parameter!;
}
