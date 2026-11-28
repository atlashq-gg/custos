using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Optio.Harness;

namespace Optio.Tests;

/// <summary>The model client with a fake Messages API: no call to Anthropic.</summary>
public sealed class AnthropicModelClientTests
{
    [Test]
    public async Task Turn_has_the_usage_and_the_message_id_of_the_response()
    {
        var client = new AnthropicModelClient(
            Microsoft.Extensions.Options.Options.Create(
                new OptioOptions { ClaudeApiKey = "test", ModelId = "claude-test" }
            ),
            new FakeMessagesApi()
        );

        var turn = await client.NextAsync(
            new ModelRequest("System.", [], [new UserMessage("Hello?")]),
            CancellationToken.None
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(turn.Text, Is.EqualTo("Hello."));
            Assert.That(
                turn.Usage,
                Is.EqualTo(new ModelUsage("claude-test", "msg_test", 10, 2, 3, 0))
            );
        }
    }

    [Test]
    public void Host_makes_the_client_for_the_real_api()
    {
        using var services = new ServiceCollection()
            .AddHttpClient()
            .Configure<OptioOptions>(o => o.ClaudeApiKey = "test")
            .AddSingleton<IModelClient, AnthropicModelClient>()
            .BuildServiceProvider();

        Assert.That(
            services.GetRequiredService<IModelClient>(),
            Is.InstanceOf<AnthropicModelClient>()
        );
    }

    private sealed class FakeMessagesApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "id": "msg_test",
                      "type": "message",
                      "role": "assistant",
                      "model": "claude-test",
                      "content": [{ "type": "text", "text": "Hello." }],
                      "stop_reason": "end_turn",
                      "stop_sequence": null,
                      "usage": {
                        "input_tokens": 10,
                        "output_tokens": 2,
                        "cache_read_input_tokens": 3,
                        "cache_creation_input_tokens": null
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                ),
            };
            return Task.FromResult(response);
        }
    }
}
