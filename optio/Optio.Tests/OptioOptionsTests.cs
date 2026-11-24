using System.ComponentModel.DataAnnotations;

namespace Optio.Tests;

/// <summary>The validation of the MCP services in the options.</summary>
public sealed class OptioOptionsTests
{
    [Test]
    public void Valid_services_have_no_error() =>
        Assert.That(Errors(Service("custos"), Service("second")), Is.Empty);

    [TestCase("custos server")]
    [TestCase("")]
    public void Service_name_with_other_characters_is_an_error(string name) =>
        Assert.That(Errors(Service(name)), Is.Not.Empty);

    [Test]
    public void Two_services_with_one_name_are_an_error() =>
        Assert.That(
            Errors(Service("custos"), Service("custos")),
            Has.Some.Contains("its own name")
        );

    private static McpServiceOptions Service(string name) =>
        new()
        {
            Name = name,
            Url = "http://server.test/mcp",
            ReadScope = "custos:read",
        };

    private static List<string?> Errors(params McpServiceOptions[] services)
    {
        var options = new OptioOptions { McpServices = [.. services] };
        return [.. options.Validate(new ValidationContext(options)).Select(r => r.ErrorMessage)];
    }
}
