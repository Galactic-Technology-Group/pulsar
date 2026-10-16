using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nms.Agent;
using Nms.Agent.Storage;
using Nms.Shared.Contracts;

namespace Nms.Agent.Tests;

public sealed class ResultOutboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nms-outbox-" + Guid.NewGuid());
    private ResultOutbox Create() => new(Options.Create(new AgentOptions { StateDir = _dir }), NullLogger<ResultOutbox>.Instance);

    [Fact]
    public void Preserves_insertion_order_and_acks_by_id()
    {
        using var outbox = Create();
        var c = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++) outbox.Enqueue(new CheckResultDto(c, t0.AddSeconds(i), i % 2 == 0, i, $"r{i}"));

        Assert.Equal(10, outbox.Count());
        var (page, lastId) = outbox.Peek(4);
        Assert.Equal(new[] { "r0", "r1", "r2", "r3" }, page.Select(p => p.Detail));
        Assert.Equal(t0.AddSeconds(2).ToUnixTimeMilliseconds(), page[2].Time.ToUnixTimeMilliseconds());

        outbox.Ack(lastId);
        Assert.Equal(6, outbox.Count());
        Assert.Equal("r4", outbox.Peek(1).Items.Single().Detail);
    }

    [Fact]
    public void Survives_reopen()
    {
        using (var a = Create()) a.Enqueue(new CheckResultDto(Guid.NewGuid(), DateTimeOffset.UtcNow, true, 3, null));
        using var b = Create();
        Assert.Equal(1, b.Count());
        var item = b.Peek(1).Items.Single();
        Assert.True(item.Ok);
        Assert.Equal(3, item.LatencyMs);
        Assert.Null(item.Detail);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
