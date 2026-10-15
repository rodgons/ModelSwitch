using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Application;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class ApiKeyUsageRecorderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_key_is_noted_once_per_minute_and_keys_are_throttled_independently()
    {
        var recorder = new ApiKeyUsageRecorder(_time);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        recorder.Record(a);
        recorder.Record(b);
        _time.Advance(TimeSpan.FromSeconds(59));
        recorder.Record(a);
        _time.Advance(TimeSpan.FromSeconds(1));
        recorder.Record(a);

        Drain(recorder).Select(u => (u.ApiKeyId, u.At)).ShouldBe(
        [
            (a, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)),
            (b, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)),
            (a, new DateTimeOffset(2026, 10, 15, 12, 1, 0, TimeSpan.Zero)),
        ]);
    }

    [Fact]
    public async Task Writer_stores_notes_and_survives_a_failing_store()
    {
        var recorder = new ApiKeyUsageRecorder(_time);
        var store = new RecordingStore();
        var writer = new ApiKeyUsageWriter(recorder, store, NullLogger<ApiKeyUsageWriter>.Instance);
        var failing = Guid.NewGuid();
        var good = Guid.NewGuid();
        store.FailFor = failing;

        recorder.Record(failing);
        recorder.Record(good);
        await writer.StartAsync(CancellationToken.None);
        var marked = await store.FirstMarked;
        await writer.StopAsync(CancellationToken.None);

        marked.ShouldBe(good, "the failing write is skipped, the next one still lands");
    }

    private static List<ApiKeyUse> Drain(ApiKeyUsageRecorder recorder)
    {
        var uses = new List<ApiKeyUse>();
        while (recorder.Reader.TryRead(out var use))
        {
            uses.Add(use);
        }

        return uses;
    }

    private sealed class RecordingStore : IApiKeyStore
    {
        private readonly TaskCompletionSource<Guid> _first = new();

        public Guid FailFor { get; set; }

        public Task<Guid> FirstMarked => _first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) => Task.FromResult<ApiKeyRecord?>(null);

        public Task MarkUsedAsync(Guid apiKeyId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            if (apiKeyId == FailFor)
            {
                throw new InvalidOperationException("database down");
            }

            _first.TrySetResult(apiKeyId);
            return Task.CompletedTask;
        }
    }
}
