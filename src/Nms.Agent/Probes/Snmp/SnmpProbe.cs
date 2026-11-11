using System.Net;
using Lextm.SharpSnmpLib;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes.Snmp;

/// <summary>
/// Reads a template's values from a device. The check is Up when the device answers (so a dead SNMP agent alerts like a
/// dead port); the readings ride along as metrics and the API compares them with the check's thresholds.
/// </summary>
public sealed class SnmpProbe(CounterRates rates, ILogger<SnmpProbe> log) : IProbe
{
    public CheckType Type => CheckType.Snmp;

    public async Task<ProbeOutcome> RunAsync(CheckDefinition check, IPAddress target, IPAddress? source, CancellationToken ct)
    {
        if (check.Snmp is null || check.Metrics is not { Count: > 0 } defs) return ProbeOutcome.Fail("NoSnmpConfig");
        try
        {
            using var session = new SnmpSession(check.Snmp, new IPEndPoint(target, check.Port ?? 161), source, TimeSpan.FromMilliseconds(check.TimeoutMs));
            var scalars = defs.Any(d => !d.Table) ? await session.GetAsync(defs.Where(d => !d.Table).Select(d => d.Oid), ct) : new();
            var tables = new Dictionary<string, IReadOnlyDictionary<string, ISnmpData>>();
            foreach (var def in defs.Where(d => d.Table).DistinctBy(d => d.Oid))
            {
                var rows = await session.WalkAsync(def.Oid, ct);
                if (rows.Count == 0 && def.FallbackOid is not null) rows = await session.WalkAsync(def.FallbackOid, ct);
                tables[def.Oid] = rows;
            }
            var labels = new Dictionary<string, IReadOnlyDictionary<string, ISnmpData>>();
            foreach (var oid in defs.Where(d => d.Table && d.LabelOid is not null).Select(d => d.LabelOid!).Distinct())
                if (tables.Values.Any(t => t.Count > 0)) labels[oid] = await session.WalkAsync(oid, ct);

            var samples = SnmpReadings.Build(check.CheckId, defs, scalars, tables, labels, rates, DateTimeOffset.UtcNow);
            // Answered but none of the template's objects exist: the wrong template for this device.
            var detail = scalars.Values.All(v => SnmpReadings.Convert(v) is null) && tables.Values.All(t => t.Count == 0) ? "NoSuchObject" : null;
            return new ProbeOutcome(true, session.FirstAnswerMs ?? 0, detail, samples);
        }
        catch (SnmpFailure f) { return ProbeOutcome.Fail(f.Detail); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "SNMP poll of {Target} failed", target);
            return ProbeOutcome.Fail(ex.GetType().Name);
        }
    }
}
