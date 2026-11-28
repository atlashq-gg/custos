using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace Optio.Harness;

/// <summary>The model through the Messages API of Anthropic, with the Anthropic C# SDK.</summary>
internal sealed class AnthropicModelClient(IOptions<OptioOptions> options) : IModelClient
{
    /// <summary>Room for the thinking and the tool calls of one turn.</summary>
    private const int MaxTokens = 16000;

    private readonly AnthropicClient _client = new() { ApiKey = options.Value.ClaudeApiKey };

    public async Task<ModelTurn> NextAsync(
        ModelRequest request,
        CancellationToken cancellationToken
    )
    {
        var o = options.Value;
        var message = await _client.Messages.Create(
            new MessageCreateParams
            {
                Model = o.ModelId,
                MaxTokens = MaxTokens,
                System = request.System,
                Tools = [.. request.Tools.Select(ToTool)],
                // The messages can hold tool calls, so the request keeps the tools.
                ToolChoice = request.Answer ? new ToolChoiceNone() : new ToolChoiceAuto(),
                Messages = [.. request.Messages.Select(ToParam)],
                OutputConfig = new OutputConfig { Effort = ToEffort(o.ModelEffort) },
                // The system prompt and the tools are the same for each turn of a query.
                CacheControl = new CacheControlEphemeral(),
            },
            cancellationToken: cancellationToken
        );

        List<ContentBlockParam> content = [];
        List<ToolCall> calls = [];
        List<string> texts = [];
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var text))
            {
                texts.Add(text.Text);
                content.Add(new TextBlockParam { Text = text.Text });
            }
            else if (block.TryPickThinking(out var thinking))
            {
                content.Add(
                    new ThinkingBlockParam
                    {
                        Thinking = thinking.Thinking,
                        Signature = thinking.Signature,
                    }
                );
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                content.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                calls.Add(new ToolCall(toolUse.ID, toolUse.Name, toolUse.Input));
                content.Add(
                    new ToolUseBlockParam
                    {
                        ID = toolUse.ID,
                        Name = toolUse.Name,
                        Input = toolUse.Input,
                    }
                );
            }
        }

        var stop = message.StopReason?.Value() switch
        {
            StopReason.ToolUse => ModelStop.ToolCalls,
            StopReason.Refusal => ModelStop.Refusal,
            StopReason.MaxTokens => ModelStop.Limit,
            _ => calls.Count > 0 ? ModelStop.ToolCalls : ModelStop.End,
        };
        var usage = new ModelUsage(
            o.ModelId,
            message.Usage.InputTokens,
            message.Usage.OutputTokens,
            message.Usage.CacheReadInputTokens ?? 0,
            message.Usage.CacheCreationInputTokens ?? 0
        );
        return new ModelTurn(string.Concat(texts).Trim(), calls, stop, content, usage);
    }

    private static ToolUnion ToTool(ModelTool tool)
    {
        var schema = tool.Schema;
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new()
            {
                Properties = schema.TryGetProperty("properties", out var properties)
                    ? properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : new Dictionary<string, JsonElement>(),
                Required = schema.TryGetProperty("required", out var required)
                    ? [.. required.EnumerateArray().Select(r => r.GetString()!)]
                    : [],
            },
        };
    }

    private static MessageParam ToParam(ModelMessage message) =>
        message switch
        {
            UserMessage user => new UserMessageParam { Content = user.Text },
            AssistantMessage assistant => new AssistantMessageParam
            {
                Content =
                    assistant.Turn.ProviderContent as List<ContentBlockParam>
                    ?? FromTurn(assistant.Turn),
            },
            ToolResultsMessage results => new UserMessageParam
            {
                Content = results
                    .Results.Select(r =>
                        (ContentBlockParam)
                            new ToolResultBlockParam
                            {
                                ToolUseID = r.CallId,
                                Content = r.Content,
                                IsError = r.IsError,
                            }
                    )
                    .ToList(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };

    /// <summary>The content of a turn of an earlier query: its text and its tool calls. The
    /// provider does not need the thinking of a turn before the last user message.</summary>
    private static List<ContentBlockParam> FromTurn(ModelTurn turn)
    {
        List<ContentBlockParam> content = [];
        if (turn.Text.Length > 0)
            content.Add(new TextBlockParam { Text = turn.Text });
        content.AddRange(
            turn.ToolCalls.Select(c =>
                (ContentBlockParam)
                    new ToolUseBlockParam
                    {
                        ID = c.Id,
                        Name = c.Name,
                        Input = c.Arguments.ToDictionary(),
                    }
            )
        );
        return content;
    }

    private static Effort ToEffort(string effort) =>
        effort switch
        {
            "low" => Effort.Low,
            "high" => Effort.High,
            "xhigh" => Effort.Xhigh,
            "max" => Effort.Max,
            _ => Effort.Medium,
        };
}
