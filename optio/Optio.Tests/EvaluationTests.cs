using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Optio.Harness;
using Optio.Queries;

namespace Optio.Tests;

/// <summary>
/// The evaluation set: does the model select the correct service and tool for a question? Each
/// case is one call of the real model with the system prompt and the tools of a query, and the
/// test examines the tool calls of the first turn. The set of the Custos server is
/// <c>optio/evaluation/custos.json</c>. <c>Optio__EvaluationSet</c> adds the path of a set of
/// the operator. The tests need <c>Optio__ClaudeApiKey</c>, and skip without it. The other
/// variables <c>Optio__*</c> change the options, for example <c>Optio__ModelId</c>. CI does not
/// run them: each run costs money, and the answer of the model can change from run to run.
/// </summary>
[Category("Evaluation")]
public sealed class EvaluationTests
{
    private const ulong Guild = 300000000000000001;

    private static readonly IConfigurationSection Settings = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build()
        .GetSection("Optio");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly ConcurrentDictionary<string, EvaluationSet> Sets = new();

    public static IEnumerable<TestCaseData> Cases()
    {
        List<string> files = [Path.Combine(AppContext.BaseDirectory, "evaluation", "custos.json")];
        if (Settings["EvaluationSet"] is { Length: > 0 } operatorSet)
            files.Add(Path.GetFullPath(operatorSet));
        foreach (var file in files)
        {
            var set = Set(file);
            for (var i = 0; i < set.Cases.Count; i++)
            {
                yield return new TestCaseData(file, i).SetArgDisplayNames(
                    Path.GetFileNameWithoutExtension(file),
                    set.Cases[i].Question
                );
            }
        }
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        if (string.IsNullOrEmpty(Settings["ClaudeApiKey"]))
            Assert.Ignore("The environment variable Optio__ClaudeApiKey is not set.");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Model_calls_an_accepted_tool_first_or_answers(string file, int index)
    {
        var set = Set(file);
        var question = set.Cases[index];
        var options = new OptioOptions();
        Settings.Bind(options);
        options.SystemPrompt ??= await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "docs", "system-prompt.example.txt")
        );
        options.OperatorText = set.OperatorText ?? options.OperatorText;
        var (services, tools) = set.Catalog();
        var system = QueryHarness.SystemPrompt(options, services, Guild, guildPrompt: null);

        var turn = await new AnthropicModelClient(Options.Create(options)).NextAsync(
            new ModelRequest(system, tools, [new UserMessage(question.Question)]),
            default
        );

        // The model can call one tool more than one time in a turn.
        var calls = turn.ToolCalls.Select(c => c.Name).Distinct().ToList();
        TestContext.Out.WriteLine($"Calls: {string.Join(", ", calls)}\nText: {turn.Text}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(turn.Stop, Is.AnyOf(ModelStop.End, ModelStop.ToolCalls));
            Assert.That(
                calls,
                question.Tools.Length == 0 ? Is.Empty : Is.Not.Empty.And.SubsetOf(question.Tools)
            );
        }
    }

    private static EvaluationSet Set(string file) =>
        Sets.GetOrAdd(file, f =>
            JsonSerializer.Deserialize<EvaluationSet>(File.ReadAllText(f), Json)!
        );

    /// <summary>A set: the catalog of each service as the service gives it, the hidden tools,
    /// the text of the operator, and the cases.</summary>
    private sealed record EvaluationSet(
        List<EvaluationService> Services,
        string[]? HiddenTools,
        string? OperatorText,
        List<EvaluationCase> Cases
    )
    {
        /// <summary>The services and the tools as a query gives them to the model.</summary>
        public (List<ServiceCard>, List<ModelTool>) Catalog()
        {
            List<ServiceCard> services = [];
            List<ModelTool> tools = [];
            foreach (var service in Services)
            {
                services.Add(new ServiceCard(service.Name, service.Title, service.Card, true));
                var catalog = new Catalog(
                    service.Title,
                    service.Card,
                    [
                        .. service.Tools.Select(t => new CatalogTool(
                            t.Name,
                            t.Title,
                            t.Description,
                            t.InputSchema,
                            t.ReadOnly
                        )),
                    ],
                    default
                );
                var options = new McpServiceOptions
                {
                    Name = service.Name,
                    WriteScope = service.Write ? "write" : null,
                };
                tools.AddRange(
                    McpCatalog.Tools(options, catalog, HiddenTools ?? []).Select(t => t.Tool)
                );
            }
            return (services, tools);
        }
    }

    /// <summary>A service. <paramref name="Write"/> is true when the configuration of Optio
    /// gives the service a write scope.</summary>
    private sealed record EvaluationService(
        string Name,
        string Title,
        string? Card,
        bool Write,
        List<EvaluationTool> Tools
    );

    private sealed record EvaluationTool(
        string Name,
        string Title,
        string Description,
        bool ReadOnly,
        JsonElement InputSchema
    );

    /// <summary>A question, and the tools that the model can call first, as
    /// <c>{service}_{tool}</c>. With no tool, the model must answer with no tool call.</summary>
    private sealed record EvaluationCase(string Question, string[] Tools);
}
