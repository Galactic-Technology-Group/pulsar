using System.Net;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes;

/// <summary>
/// WAN cross-check: pings every target listed in the check's address (comma separated) in parallel over the default
/// route and passes if any of them answers. Latency is the best reply. One dead resolver never looks like a dead line.
/// </summary>
public sealed class InternetProbe(IcmpProbe icmp) : IProbe
{
    public CheckType Type => CheckType.Internet;

    public async Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct)
    {
        var targets = check.TargetAddress.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Length == 0) return ProbeOutcome.Fail("NoTargets");

        var results = await Task.WhenAll(targets.Select(t => ProbeOneAsync(check, t, ct)));

        var ok = results.Where(r => r.Outcome.Ok).ToList();
        if (ok.Count > 0)
        {
            var best = ok.Min(r => r.Outcome.LatencyMs ?? int.MaxValue);
            var failed = results.Where(r => !r.Outcome.Ok).Select(r => r.Target).ToList();
            var detail = failed.Count == 0
                ? $"{ok.Count}/{results.Length} replied"
                : $"{ok.Count}/{results.Length} replied; no reply from {string.Join(", ", failed)}";
            return ProbeOutcome.Pass(best, detail);
        }
        return ProbeOutcome.Fail("no reply from " + string.Join(", ", results.Select(r => $"{r.Target} ({r.Outcome.Detail})")));
    }

    private async Task<(string Target, ProbeOutcome Outcome)> ProbeOneAsync(CheckDefinition check, string target, CancellationToken ct)
    {
        IPAddress? ip = IPAddress.TryParse(target, out var parsed) ? parsed : null;
        if (ip is null)
        {
            try { ip = (await Dns.GetHostAddressesAsync(target, System.Net.Sockets.AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
            catch { ip = null; }
        }
        if (ip is null) return (target, ProbeOutcome.Fail("DnsFailure"));
        try { return (target, await icmp.RunAsync(check, ip, null, ct)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (target, ProbeOutcome.Fail(ex.GetType().Name)); }
    }
}
