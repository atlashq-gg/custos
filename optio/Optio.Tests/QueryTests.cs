using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Optio.Harness;
using Optio.Queries;
using Optio.Storage;
using Optio.Telemetry;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The query of spec/06-query.md, with a fake model, fake tools, and the stream and the
/// buckets of a NATS container. Needs Docker.</summary>
[NonParallelizable]
public sealed class QueryTests
{
    private const string QueryPath = "/api/v1/query";
    private const string StreamPath = "/api/v1/stream";
    private const string Session = "session-1";

    private NatsBroker _broker = null!;
    private OptioFactory _factory = null!;

    [OneTimeSetUp]
    public async Task StartBrokerAsync() => _broker = await NatsBroker.StartAsync();

    [OneTimeTearDown]
    public async Task StopBrokerAsync() => await _broker.DisposeAsync();

    [SetUp]
    public async Task SetUpAsync()
    {
        _factory = new OptioFactory(stream: true, broker: _broker.Url("optio"), query: true);
        await _factory.WaitForStreamAsync();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Discovery_document_has_the_query_endpoint_only_with_the_query()
    {
        using var without = new OptioFactory(stream: true);
        var on = await _factory
            .CreateClient()
            .GetFromJsonAsync<JsonElement>("/.well-known/custos-configuration");
        var off = await without
            .CreateClient()
            .GetFromJsonAsync<JsonElement>("/.well-known/custos-configuration");

        SpecSchemas.AssertDiscoveryDocument(on);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                on.GetProperty("query_endpoint").GetString(),
                Is.EqualTo(OptioFactory.PublicBaseUrl + QueryPath)
            );
            Assert.That(off.TryGetProperty("query_endpoint", out _), Is.False);
            Assert.That(DiscoveryTests.Features(on), Is.EqualTo(["upload", "stream", "query"]));
            Assert.That(DiscoveryTests.Features(off), Is.EqualTo(["upload", "stream"]));
        }
    }

    [Test]
    public async Task Query_needs_a_session_in_the_token()
    {
        var response = await _factory.CreateMemberClient().PostAsJsonAsync(QueryPath, Query("q1"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [TestCase("""{"reference":"short","text":"a"}""")]
    [TestCase("""{"reference":"reference-1"}""")]
    [TestCase("""{"reference":"reference-1","text":""}""")]
    [TestCase("""{"reference":"reference-1","text":"a","action":"stop"}""")]
    [TestCase("""{"reference":"reference-1","action":"jump"}""")]
    [TestCase("""{"reference":"ref:12345","text":"a"}""")]
    public async Task Body_that_is_not_valid_gets_400(string body)
    {
        var response = await Client()
            .PostAsync(
                QueryPath,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            );

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Query_sends_its_states_and_its_answer_to_the_stream_of_the_client()
    {
        _factory.Model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("40 rifles are in the seaport."));
        using var stream = await OpenStreamAsync();

        var response = await Client().PostAsJsonAsync(QueryPath, Query("query-0001"));
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();
        var messages = await ReadQueryAsync(stream, "query-0001");

        SpecSchemas.AssertQueryState(state);
        foreach (var message in messages)
            SpecSchemas.AssertMessage(message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(state.GetProperty("state").GetString(), Is.EqualTo("working"));
            Assert.That(state.TryGetProperty("deadline", out _), Is.True);
            Assert.That(
                messages.Select(m => m.GetProperty("state").GetString()),
                Is.EqualTo(new[] { "working", "working", "closed" })
            );
            Assert.That(
                messages[1].GetProperty("text").GetString(),
                Is.EqualTo("Custos: Find items")
            );
            Assert.That(messages[2].GetProperty("cause").GetString(), Is.EqualTo("answer"));
            Assert.That(
                messages[2].GetProperty("text").GetString(),
                Is.EqualTo("40 rifles are in the seaport.")
            );
            Assert.That(messages[2].TryGetProperty("deadline", out _), Is.False);
            Assert.That(_factory.Tools.Calls, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Same_reference_is_the_same_query()
    {
        var client = Client();
        await client.PostAsJsonAsync(QueryPath, Query("query-0002"));
        await WaitForStateAsync("query-0002", "closed");

        var again = await client.PostAsJsonAsync(QueryPath, Query("query-0002"));
        var state = await again.Content.ReadFromJsonAsync<JsonElement>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(state.GetProperty("state").GetString(), Is.EqualTo("closed"));
            Assert.That(_factory.Model.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Other_client_of_the_user_gets_409_while_a_query_is_open()
    {
        _factory.Model.Gate = new();
        await Client().PostAsJsonAsync(QueryPath, Query("query-0003"));

        var other = await Client("session-2").PostAsJsonAsync(QueryPath, Query("query-0004"));
        _factory.Model.Gate.SetResult();
        await WaitForStateAsync("query-0003", "closed");
        var after = await Client("session-2").PostAsJsonAsync(QueryPath, Query("query-0005"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        }
    }

    [Test]
    public async Task New_query_of_the_client_replaces_its_open_query()
    {
        _factory.Model.Gate = new();
        using var stream = await OpenStreamAsync();
        var client = Client();
        await client.PostAsJsonAsync(QueryPath, Query("query-0006"));
        await _factory.Model.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await client.PostAsJsonAsync(QueryPath, Query("query-0007"));
        _factory.Model.Gate.SetResult();
        var first = await ReadQueryAsync(stream, "query-0006");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(first[^1].GetProperty("cause").GetString(), Is.EqualTo("replaced"));
            Assert.That(
                (await WaitForStateAsync("query-0007", "closed")).Cause,
                Is.EqualTo("answer")
            );
        }
    }

    [Test]
    public async Task Stop_closes_the_query_and_a_second_stop_gets_the_same_response()
    {
        _factory.Model.Gate = new();
        var client = Client();
        await client.PostAsJsonAsync(QueryPath, Query("query-0008"));

        var stop = await client.PostAsJsonAsync(QueryPath, Action("query-0008", "stop"));
        var again = await client.PostAsJsonAsync(QueryPath, Action("query-0008", "stop"));
        var accept = await client.PostAsJsonAsync(QueryPath, Action("query-0008", "accept"));
        var unknown = await client.PostAsJsonAsync(QueryPath, Action("query-9999", "stop"));
        var state = await stop.Content.ReadFromJsonAsync<JsonElement>();
        _factory.Model.Gate.SetResult();

        SpecSchemas.AssertQueryState(state);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.GetProperty("cause").GetString(), Is.EqualTo("stopped"));
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(accept.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        await Task.Delay(200);
        Assert.That((await RecordAsync())?.Cause, Is.EqualTo("stopped"));
    }

    [Test]
    public async Task Stream_that_opens_sends_the_state_of_the_open_query_after_the_position()
    {
        _factory.Model.Gate = new();
        await Client().PostAsJsonAsync(QueryPath, Query("query-0010"));
        await _factory.Model.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var response = await StreamClient()
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Get, StreamPath),
                HttpCompletionOption.ResponseHeadersRead
            );
        using var stream = new SseReader(response);
        var position = await stream.NextAsync();
        var open = await stream.NextAsync();
        _factory.Model.Gate.SetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(position!.Data, Is.Null);
            Assert.That(open!.Id, Is.EqualTo(position.Id));
            Assert.That(
                open.Data!.Value.GetProperty("reference").GetString(),
                Is.EqualTo("query-0010")
            );
            Assert.That(open.Data.Value.GetProperty("state").GetString(), Is.EqualTo("working"));
        }
    }

    [Test]
    public async Task Stream_of_another_client_gets_no_query_message()
    {
        var response = await StreamClient("session-2")
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Get, StreamPath),
                HttpCompletionOption.ResponseHeadersRead
            );
        using var other = new SseReader(response);
        await other.NextAsync();

        await Client().PostAsJsonAsync(QueryPath, Query("query-0011"));
        await WaitForStateAsync("query-0011", "closed");

        Assert.That(await other.IsQuietAsync(TimeSpan.FromSeconds(1)), Is.True);
    }

    [Test]
    public async Task Follow_up_continues_the_conversation_and_a_new_conversation_starts_empty()
    {
        // Its own session: the conversations of the other tests stay in the bucket.
        const string sid = "session-follow";
        var client = Client(sid);
        _factory.Model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("40 rifles."));
        await client.PostAsJsonAsync(QueryPath, Query("query-0012", "How many rifles?"));
        await WaitForStateAsync("query-0012", "closed", sid);

        await client.PostAsJsonAsync(QueryPath, Query("query-0013", "And in the depot?"));
        await WaitForStateAsync("query-0013", "closed", sid);
        await client.PostAsJsonAsync(
            QueryPath,
            new
            {
                reference = "query-0014",
                text = "Start again.",
                new_conversation = true,
            }
        );
        await WaitForStateAsync("query-0014", "closed", sid);

        var requests = _factory.Model.Requests.ToArray();
        using (Assert.EnterMultipleScope())
        {
            // The follow-up: the first query (question, call, results, answer), then the new one.
            Assert.That(requests[2].Messages, Has.Count.EqualTo(5));
            Assert.That(
                ((UserMessage)requests[2].Messages[0]).Text,
                Is.EqualTo("How many rifles?")
            );
            Assert.That(requests[2].Messages[2], Is.TypeOf<ToolResultsMessage>());
            Assert.That(
                ((UserMessage)requests[2].Messages[4]).Text,
                Is.EqualTo("And in the depot?")
            );
            Assert.That(requests[3].Messages, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Model_gets_the_prompt_of_the_guild_from_the_database()
    {
        using var factory = new OptioFactory(
            stream: true,
            broker: _broker.Url("optio"),
            query: true,
            database: true
        );
        await factory.WaitForStreamAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope
                .ServiceProvider.GetRequiredService<GuildSettingsStore>()
                .UpdateAsync(
                    Guild,
                    s => s with { Prompts = new() { Guild = "Answer in French." } },
                    Owner,
                    default
                );
        }

        await factory
            .CreateMemberClient(Reader, "session-guild")
            .PostAsJsonAsync(QueryPath, Query("query-0018"));
        await factory.Model.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(
            factory.Model.Requests.Single().System,
            Does.EndWith("<guild_instructions>\nAnswer in French.\n</guild_instructions>")
        );
    }

    [Test]
    public async Task Each_request_to_the_model_writes_a_usage_row_and_counts_the_tokens()
    {
        using var factory = new OptioFactory(
            stream: true,
            broker: _broker.Url("optio"),
            query: true,
            database: true
        );
        await factory.WaitForStreamAsync();
        using var tokens = new MetricCollector<long>(
            factory.Services.GetRequiredService<OptioMetrics>().Meter,
            OptioMetrics.ModelTokens
        );
        factory.Model.Turns.Enqueue(
            FakeModel.Call(FakeTools.FindItems) with
            {
                Usage = new("model-1", "msg_1", 100, 20, 0, 50),
            }
        );
        factory.Model.Turns.Enqueue(
            FakeModel.Answer("40 rifles.") with
            {
                Usage = new("model-1", "msg_1", 30, 10, 50, 0),
            }
        );

        await factory
            .CreateMemberClient(Reader, Session)
            .PostAsJsonAsync(QueryPath, Query("query-0030"));
        for (
            var i = 0;
            (await Store(factory).GetAsync(Key(Session), default)).Record?.IsOpen != false;
            i++
        )
        {
            Assert.That(i, Is.LessThan(200), "The query did not close.");
            await Task.Delay(50);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await scope
            .ServiceProvider.GetRequiredService<OptioDbContext>()
            .QueryUsage.OrderBy(u => u.Id)
            .ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                rows.Select(u =>
                    (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens)
                ),
                Is.EqualTo(new[] { (100L, 20L, 0L, 50L), (30L, 10L, 50L, 0L) })
            );
            Assert.That(rows.Select(u => u.QueryReference), Is.All.EqualTo("query-0030"));
            Assert.That(rows.Select(u => u.ClientKey), Is.All.EqualTo(Key(Session)));
            Assert.That(rows.Select(u => u.UserId), Is.All.EqualTo((long)Reader));
            Assert.That(rows.Select(u => u.GuildId), Is.All.EqualTo((long)Guild));
            Assert.That(rows.Select(u => u.Model), Is.All.EqualTo("model-1"));
            Assert.That(rows.Select(u => u.ExternalReference), Is.All.EqualTo("msg_1"));
            Assert.That(
                tokens
                    .GetMeasurementSnapshot()
                    .Where(m => (string?)m.Tags[OptioMetrics.TypeTag] == "input")
                    .Sum(m => m.Value),
                Is.EqualTo(130)
            );
        }
    }

    [Test]
    public async Task Query_without_a_database_answers_and_writes_no_row()
    {
        _factory.Model.Turns.Enqueue(
            FakeModel.Answer("40 rifles.") with
            {
                Usage = new("model-1", "msg_1", 30, 10, 0, 0),
            }
        );

        await Client().PostAsJsonAsync(QueryPath, Query("query-0031"));
        var record = await WaitForStateAsync("query-0031", QueryStates.Closed);

        Assert.That(record.Cause, Is.EqualTo(QueryStates.Answer));
    }

    [Test]
    public async Task Accepted_change_runs_with_the_token_of_the_decision_and_the_model_answers()
    {
        QueueChange("Done: the seaport is Main Depot.");
        using var stream = await OpenStreamAsync();
        var client = Client();
        await client.PostAsJsonAsync(QueryPath, Query("query-0020"));
        var waiting = await WaitForStateAsync("query-0020", "waiting");

        var accept = await client.PostAsJsonAsync(QueryPath, Action("query-0020", "accept"));
        var again = await client.PostAsJsonAsync(QueryPath, Action("query-0020", "accept"));
        var refuse = await client.PostAsJsonAsync(QueryPath, Action("query-0020", "refuse"));
        var messages = await ReadQueryAsync(stream, "query-0020");
        var state = await accept.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var message in messages)
            SpecSchemas.AssertMessage(message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.Text, Is.EqualTo("Set Label? \"Main Depot\""));
            Assert.That(
                waiting.Deadline,
                Is.EqualTo(_factory.Time.GetUtcNow().AddSeconds(60)).Within(TimeSpan.FromSeconds(1))
            );
            Assert.That(accept.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(state.GetProperty("state").GetString(), Is.EqualTo("applying"));
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(refuse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(
                messages.Select(m => m.GetProperty("state").GetString()).Distinct(),
                Is.EqualTo(new[] { "working", "waiting", "applying", "closed" })
            );
            Assert.That(messages[^1].GetProperty("cause").GetString(), Is.EqualTo("answer"));
            Assert.That(_factory.Tools.Writes, Has.Count.EqualTo(1));
            // The model reads the result of the change.
            Assert.That(
                ((ToolResultsMessage)_factory.Model.Requests.Last().Messages[^1])
                    .Results.Single()
                    .Content,
                Is.EqualTo(_factory.Tools.WriteResult)
            );
        }
    }

    [Test]
    public async Task Refused_change_closes_the_query_and_the_follow_up_knows_it()
    {
        const string sid = "session-refuse";
        QueueChange();
        var client = Client(sid);
        await client.PostAsJsonAsync(QueryPath, Query("query-0021"));
        await WaitForStateAsync("query-0021", "waiting", sid);

        var refuse = await client.PostAsJsonAsync(QueryPath, Action("query-0021", "refuse"));
        await client.PostAsJsonAsync(QueryPath, Query("query-0022", "Why not?"));
        await WaitForStateAsync("query-0022", "closed", sid);

        var history = _factory.Model.Requests.Last().Messages;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refuse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_factory.Tools.Writes, Is.Empty);
            Assert.That(
                ((ToolResultsMessage)history[^2]).Results.Single().Content,
                Does.Contain("refused")
            );
        }
    }

    [Test]
    public async Task Question_with_no_decision_expires_at_its_deadline()
    {
        const string sid = "session-expire";
        QueueChange();
        await Client(sid).PostAsJsonAsync(QueryPath, Query("query-0023"));
        await WaitForStateAsync("query-0023", "waiting", sid);

        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        var record = await WaitForStateAsync("query-0023", "closed", sid);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Cause, Is.EqualTo("expired"));
            Assert.That(_factory.Tools.Writes, Is.Empty);
        }
    }

    [Test]
    public async Task Failed_change_closes_the_query_with_a_result_that_is_not_known()
    {
        const string sid = "session-fail";
        QueueChange();
        _factory.Tools.WriteFails = true;
        var client = Client(sid);
        await client.PostAsJsonAsync(QueryPath, Query("query-0024"));
        await WaitForStateAsync("query-0024", "waiting", sid);

        await client.PostAsJsonAsync(QueryPath, Action("query-0024", "accept"));
        var record = await WaitForStateAsync("query-0024", "closed", sid);

        Assert.That(record.Cause, Is.EqualTo("result_unknown"));
    }

    [Test]
    public async Task Question_continues_after_a_restart_of_optio()
    {
        const string sid = "session-restart";
        QueueChange();
        await Client(sid).PostAsJsonAsync(QueryPath, Query("query-0025"));
        await WaitForStateAsync("query-0025", "waiting", sid);
        _factory.Dispose();

        _factory = new OptioFactory(stream: true, broker: _broker.Url("optio"), query: true);
        await _factory.WaitForStreamAsync();
        _factory.Model.Turns.Enqueue(FakeModel.Answer("Done after the restart."));
        var accept = await Client(sid).PostAsJsonAsync(QueryPath, Action("query-0025", "accept"));
        var record = await WaitForStateAsync("query-0025", "closed", sid);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accept.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(record.Cause, Is.EqualTo("answer"));
            Assert.That(_factory.Tools.Writes, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task New_query_waits_until_the_change_in_progress_completes()
    {
        const string sid = "session-apply";
        QueueChange();
        _factory.Tools.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(sid);
        await client.PostAsJsonAsync(QueryPath, Query("query-0026"));
        await WaitForStateAsync("query-0026", "waiting", sid);
        await client.PostAsJsonAsync(QueryPath, Action("query-0026", "accept"));

        var stop = await client.PostAsJsonAsync(QueryPath, Action("query-0026", "stop"));
        var next = client.PostAsJsonAsync(QueryPath, Query("query-0027"));
        await Task.Delay(300);
        var waited = !next.IsCompleted;
        _factory.Tools.WriteGate.SetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(waited, Is.True);
            Assert.That((await next).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(_factory.Tools.Writes, Has.Count.EqualTo(1));
        }
    }

    /// <summary>The turns of a query that finds a structure, then asks to set its
    /// label.</summary>
    private void QueueChange(string answer = "Done.")
    {
        _factory.Model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _factory.Model.Turns.Enqueue(
            FakeModel.Call(
                FakeTools.SetLabel,
                """{"structure_id":"s-1","label":"Main Depot","""
                    + "\"confirm_text\":\"Set Label? \\\"Main Depot\\\"\"}"
            )
        );
        _factory.Model.Turns.Enqueue(FakeModel.Answer(answer));
    }

    [Test]
    public async Task User_with_too_many_queries_gets_429_with_a_wait()
    {
        using var factory = new OptioFactory(
            stream: true,
            broker: _broker.Url("optio"),
            query: true,
            settings: new() { ["Optio:MaxQueriesPerHour"] = "1" }
        );
        await factory.WaitForStreamAsync();
        var client = factory.CreateMemberClient(Owner, "session-limit");
        await client.PostAsJsonAsync(QueryPath, Query("query-0015"));
        for (
            var i = 0;
            (await Store(factory).GetAsync(Key("session-limit"), default)).Record
                is not { IsOpen: false };
            i++
        )
        {
            Assert.That(i, Is.LessThan(100));
            await Task.Delay(50);
        }

        var second = await client.PostAsJsonAsync(QueryPath, Query("query-0016"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(second.Headers.RetryAfter?.Delta, Is.GreaterThan(TimeSpan.Zero));
        }
    }

    [Test]
    public async Task Start_closes_a_query_that_a_stopped_instance_left_open()
    {
        var store = Store(_factory);
        var client = new QueryClient(Key("session-lost"), Guild, Reader);
        var (_, revision) = await store.GetAsync(client.Key, default);
        await store.TryPutAsync(
            new QueryRecord(
                "query-0017",
                client.Key,
                Guild,
                Reader,
                "Lost?",
                false,
                QueryStates.Working,
                null,
                DateTimeOffset.UtcNow.AddMinutes(1),
                null,
                "other-instance",
                DateTimeOffset.UtcNow
            ),
            revision,
            default
        );

        using var restarted = new OptioFactory(
            stream: true,
            broker: _broker.Url("optio"),
            query: true
        );
        await restarted.WaitForStreamAsync();
        QueryRecord? record = null;
        for (var i = 0; i < 100; i++)
        {
            record = (await store.GetAsync(client.Key, default)).Record;
            if (record is { IsOpen: false })
                break;
            await Task.Delay(50);
        }

        Assert.That(record?.Cause, Is.EqualTo("interrupted"));
    }

    private HttpClient Client(string sid = Session) => _factory.CreateMemberClient(Reader, sid);

    private HttpClient StreamClient(string sid = Session)
    {
        _factory.Members.Members[(Guild, Reader)] = [ReadRole];
        return _factory.CreateMemberClient(Reader, sid);
    }

    private async Task<SseReader> OpenStreamAsync()
    {
        var response = await StreamClient()
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Get, StreamPath),
                HttpCompletionOption.ResponseHeadersRead
            );
        var stream = new SseReader(response);
        await stream.NextAsync();
        return stream;
    }

    /// <summary>The query messages of <paramref name="reference"/> until its closing
    /// message.</summary>
    private static async Task<List<JsonElement>> ReadQueryAsync(SseReader stream, string reference)
    {
        List<JsonElement> messages = [];
        while (await stream.NextAsync() is { } item)
        {
            if (
                item.Data is not { } data
                || data.GetProperty("kind").GetString() != "query"
                || data.GetProperty("reference").GetString() != reference
            )
            {
                continue;
            }
            messages.Add(data);
            if (data.GetProperty("state").GetString() == "closed")
                break;
        }
        return messages;
    }

    private async Task<QueryRecord> WaitForStateAsync(
        string reference,
        string state,
        string sid = Session
    )
    {
        for (var i = 0; ; i++)
        {
            if (
                await RecordAsync(sid) is { } record
                && record.Reference == reference
                && record.State == state
            )
                return record;
            Assert.That(i, Is.LessThan(200), $"{reference} is not {state}.");
            await Task.Delay(50);
        }
    }

    private async Task<QueryRecord?> RecordAsync(string sid = Session) =>
        (await Store(_factory).GetAsync(Key(sid), default)).Record;

    private static QueryStore Store(OptioFactory factory) =>
        factory.Services.GetRequiredService<QueryStore>();

    private static string Key(string sid) => QueryClient.KeyOf(sid);

    private static object Query(string reference, string text = "How many rifles?") =>
        new { reference, text };

    private static object Action(string reference, string action) => new { reference, action };
}
