using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests;

public abstract partial class AdapterContract<TAdapter>
{
    protected const string CompletionText = "CANARY-COMPLETION-text";

    protected static readonly NeutralUsage ReportedUsage = new(42, 17, null);

    [Fact]
    public async Task Complete_returns_text_and_the_exact_reported_usage()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub(CompletionText, FinishReason.Stop, ReportedUsage)));

        var outcome = await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        var success = outcome.ShouldBeOfType<CompletionOutcome.Success>();
        success.Text.ShouldBe(CompletionText);
        success.FinishReason.ShouldBe(FinishReason.Stop);
        success.Usage.ShouldBe(ReportedUsage);
    }

    [Fact]
    public async Task Complete_request_is_a_post_to_the_chat_path_carrying_the_provider_model_id()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("ok", FinishReason.Stop, ReportedUsage)));

        await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        var entry = Server.LogEntries.Single(e => e.RequestMessage?.Path == Wire.ChatPath);
        entry.RequestMessage!.Method.ToUpperInvariant().ShouldBe("POST");
        LastChatRequestBody()["model"]!.GetValue<string>().ShouldBe(ModelId);
    }

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Request_is_sent_in_the_mode_asked_for(bool streaming)
    {
        if (streaming)
        {
            StubStreamBody(Wire.EncodeStream(new StreamStub(["a"], FinishReason.Stop, ReportedUsage)));
            await StreamAllAsync(Context(), ChatRequest());
        }
        else
        {
            StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("a", FinishReason.Stop, ReportedUsage)));
            await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);
        }

        var body = LastChatRequestBody();
        var flag = body["stream"]?.GetValue<bool>() ?? false;
        flag.ShouldBe(streaming);
    }

    [Theory]
    [MemberData(nameof(ContractCases.CachedTokens), MemberType = typeof(ContractCases))]
    public async Task Complete_maps_cached_input_tokens(int? reportedCached, int? expectedCached)
    {
        var reported = new NeutralUsage(1000, 50, reportedCached);
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("ok", FinishReason.Stop, reported)));

        var outcome = await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        outcome.ShouldBeOfType<CompletionOutcome.Success>().Usage.ShouldBe(new NeutralUsage(1000, 50, expectedCached));
    }

    [Theory]
    [MemberData(nameof(ContractCases.CachedTokens), MemberType = typeof(ContractCases))]
    public async Task Stream_maps_cached_input_tokens(int? reportedCached, int? expectedCached)
    {
        var reported = new NeutralUsage(1000, 50, reportedCached);
        StubStreamBody(Wire.EncodeStream(new StreamStub(["ok"], FinishReason.Stop, reported)));

        var events = await StreamAllAsync(Context(), ChatRequest());

        ShouldEndWithExactlyOneTerminalEvent(events);
        events.OfType<StreamEvent.Usage>().Single().Value.ShouldBe(new NeutralUsage(1000, 50, expectedCached));
    }

    [Fact]
    public async Task Stream_yields_deltas_in_order_then_finish_and_usage()
    {
        string[] deltas = ["Hel", "lo", ", ", "world"];
        StubStreamBody(Wire.EncodeStream(new StreamStub(deltas, FinishReason.Stop, ReportedUsage)));

        var events = await StreamAllAsync(Context(), ChatRequest());

        ShouldEndWithExactlyOneTerminalEvent(events);
        events.OfType<StreamEvent.ContentDelta>().Select(d => d.Text).ShouldBe(deltas);
        events.Where(e => e is StreamEvent.ContentDelta).ShouldBe(events.Take(deltas.Length), "all deltas come before Finish and Usage");
        events.OfType<StreamEvent.Finish>().Single().Reason.ShouldBe(FinishReason.Stop);
        events.OfType<StreamEvent.Usage>().Single().Value.ShouldBe(ReportedUsage);
    }

    [Fact]
    public async Task Complete_without_reported_usage_returns_null_usage_and_never_estimates()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("some text with several words", FinishReason.Stop, null)));

        var outcome = await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        outcome.ShouldBeOfType<CompletionOutcome.Success>().Usage.ShouldBeNull();
    }

    [Fact]
    public async Task Stream_without_reported_usage_emits_no_usage_event()
    {
        StubStreamBody(Wire.EncodeStream(new StreamStub(["some", " text"], FinishReason.Stop, null)));

        var events = await StreamAllAsync(Context(), ChatRequest());

        ShouldEndWithExactlyOneTerminalEvent(events);
        events.OfType<StreamEvent.Usage>().ShouldBeEmpty();
        events[^1].ShouldBeOfType<StreamEvent.Finish>();
    }

    [Theory]
    [MemberData(nameof(ContractCases.FinishReasons), MemberType = typeof(ContractCases))]
    public async Task Finish_reason_is_mapped(bool streaming, FinishReason reason)
    {
        if (streaming)
        {
            StubStreamBody(Wire.EncodeStream(new StreamStub(["x"], reason, ReportedUsage)));
            var events = await StreamAllAsync(Context(), ChatRequest());
            ShouldEndWithExactlyOneTerminalEvent(events);
            events.OfType<StreamEvent.Finish>().Single().Reason.ShouldBe(reason);
        }
        else
        {
            StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("x", reason, ReportedUsage)));
            var outcome = await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);
            outcome.ShouldBeOfType<CompletionOutcome.Success>().FinishReason.ShouldBe(reason);
        }
    }

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Json_object_response_format_is_encoded_in_the_request(bool streaming)
    {
        await SendAsync(streaming, ChatRequest(new ResponseFormat.JsonObject()));

        Wire.AssertJsonObjectRequest(LastChatRequestBody());
    }

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Json_schema_response_format_is_encoded_in_the_request(bool streaming)
    {
        var schema = ParseElement("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""");

        await SendAsync(streaming, ChatRequest(new ResponseFormat.JsonSchema("answer_schema", schema, Strict: true)));

        Wire.AssertJsonSchemaRequest(LastChatRequestBody(), "answer_schema", schema, strict: true);
    }

    [Fact]
    public async Task Check_succeeds_against_a_healthy_provider()
    {
        StubJson(Wire.CheckPath, 200, Wire.EncodeCheckBody());

        var result = await Adapter.CheckAsync(Context(), TestToken);

        result.Reachable.ShouldBeTrue();
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task List_models_returns_the_ids_the_provider_reports()
    {
        string[] ids = ["model-a", "model-b"];
        StubJson(Wire.ListModelsPath, 200, Wire.EncodeModelList(ids));

        var models = await Adapter.ListModelsAsync(Context(), TestToken);

        models.Select(m => m.Id).ShouldBe(ids, ignoreOrder: true);
    }

    private async Task SendAsync(bool streaming, NeutralChatRequest request)
    {
        if (streaming)
        {
            StubStreamBody(Wire.EncodeStream(new StreamStub(["{}"], FinishReason.Stop, ReportedUsage)));
            await StreamAllAsync(Context(), request);
        }
        else
        {
            StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("{}", FinishReason.Stop, ReportedUsage)));
            await Adapter.CompleteAsync(Context(), request, TestToken);
        }
    }
}
