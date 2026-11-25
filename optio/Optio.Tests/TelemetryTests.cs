using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Optio.Telemetry;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The resource of the telemetry of Optio.</summary>
public sealed class TelemetryTests
{
    [Test]
    public void Service_name_is_Optio_without_the_variable()
    {
        using var factory = new OptioFactory();

        Assert.That(ServiceName(factory), Is.EqualTo(TelemetrySetup.Name));
    }

    [Test]
    public void Variable_replaces_the_service_name()
    {
        using var factory = new OptioFactory();
        using var named = factory.WithWebHostBuilder(builder =>
            builder.UseSetting(TelemetrySetup.ServiceNameVariable, "optio-test")
        );

        Assert.That(ServiceName(named), Is.EqualTo("optio-test"));
    }

    private static object? ServiceName<T>(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<T> factory
    )
        where T : class =>
        factory
            .Services.GetRequiredService<TracerProvider>()
            .GetResource()
            .Attributes.Single(a => a.Key == "service.name")
            .Value;
}
