using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Optio.Tests;

public sealed class HealthTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new WebApplicationFactory<Program>();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [TestCase("/health/alive")]
    [TestCase("/health/ready")]
    public async Task Health_endpoint_is_healthy(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Healthy"));
        }
    }

    [Test]
    public async Task Probe_gives_0_for_a_healthy_service()
    {
        using var client = _factory.CreateClient();

        Assert.That(
            await CommandLine.ProbeAsync(client, new Uri("http://localhost/health/alive")),
            Is.Zero
        );
    }

    [Test]
    public async Task Probe_gives_1_for_an_error_or_no_answer()
    {
        using var client = _factory.CreateClient();
        using var nobody = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                await CommandLine.ProbeAsync(client, new Uri("http://localhost/health/none")),
                Is.EqualTo(1)
            );
            Assert.That(
                await CommandLine.ProbeAsync(nobody, new Uri("http://127.0.0.1:9/health/alive")),
                Is.EqualTo(1)
            );
        }
    }

    [TestCase("http://+:8080", null, "http://localhost:8080/health/alive")]
    [TestCase("https://*:8443;http://0.0.0.0:5080", null, "http://localhost:5080/health/alive")]
    [TestCase("http://[::]:80", null, "http://localhost/health/alive")]
    [TestCase(null, "8081;8082", "http://localhost:8081/health/alive")]
    [TestCase(null, null, "http://localhost:8080/health/alive")]
    public void Probe_uri_comes_from_the_environment(
        string? urls,
        string? httpPorts,
        string expected
    ) => Assert.That(CommandLine.ProbeUri(urls, httpPorts).ToString(), Is.EqualTo(expected));

    [Test]
    public async Task Version_flag_prints_the_version()
    {
        using var output = new StringWriter();

        var exitCode = await CommandLine.RunAsync(["--version"], output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(output.ToString().Trim(), Is.EqualTo(OptioVersion.Value));
        }
    }

    [Test]
    public async Task Other_arguments_start_the_service() =>
        Assert.That(
            await CommandLine.RunAsync(["--urls", "http://+:8080"], TextWriter.Null),
            Is.Null
        );
}
