using System.Net;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes;

public sealed record ProbeOutcome(bool Ok, int? LatencyMs, string? Detail)
{
    public static ProbeOutcome Fail(string detail) => new(false, null, detail);
    public static ProbeOutcome Pass(int latencyMs, string? detail = null) => new(true, latencyMs, detail);
}

public interface IProbe
{
    CheckType Type { get; }
    Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct);
}
