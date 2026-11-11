using System.Collections.Concurrent;
using System.Globalization;
using Lextm.SharpSnmpLib;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes.Snmp;

/// <summary>A numeric SNMP value. CounterBits is 32 or 64 for counters (so a wrap can be told from a reset), else 0.</summary>
public readonly record struct RawValue(double Value, int CounterBits = 0);

/// <summary>Turns SNMP varbinds into readings. Pure functions, so the probe's network code stays thin.</summary>
public static class SnmpReadings
{
    /// <summary>Null for missing objects (noSuchObject, noSuchInstance, endOfMibView) and for non-numeric data.</summary>
    public static RawValue? Convert(ISnmpData? data) => data switch
    {
        Integer32 i => new RawValue(i.ToInt32()),
        Gauge32 g => new RawValue(g.ToUInt32()),
        TimeTicks t => new RawValue(t.ToUInt32()),
        Counter32 c => new RawValue(c.ToUInt32(), 32),
        Counter64 c => new RawValue(c.ToUInt64(), 64),
        // Some sensors report "23.5" as a string.
        OctetString s when double.TryParse(s.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => new RawValue(d),
        _ => null,
    };

    /// <summary>"1.3.6.1.2.1.2.2.1.8.12" under column "1.3.6.1.2.1.2.2.1.8" → "12"; null when not under it.</summary>
    public static string? IndexUnder(ObjectIdentifier id, uint[] column)
    {
        var n = id.ToNumerical();
        if (n.Length <= column.Length) return null;
        for (var i = 0; i < column.Length; i++) if (n[i] != column[i]) return null;
        return string.Join('.', n.Skip(column.Length));
    }

    /// <summary>
    /// Builds the samples for one poll. <paramref name="scalars"/> holds GET answers by OID; <paramref name="tables"/> holds
    /// walked rows by column OID then row index; <paramref name="labels"/> holds name columns the same way.
    /// </summary>
    public static List<MetricSampleDto> Build(Guid checkId, IReadOnlyList<SnmpMetricDef> defs,
        IReadOnlyDictionary<string, ISnmpData> scalars,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ISnmpData>> tables,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ISnmpData>> labels,
        CounterRates rates, DateTimeOffset now)
    {
        var samples = new List<MetricSampleDto>();
        foreach (var def in defs)
        {
            if (!def.Table)
            {
                if (scalars.TryGetValue(def.Oid, out var data) && Value(def, null, data) is { } v) samples.Add(new MetricSampleDto(def.Key, null, v));
                continue;
            }
            if (!tables.TryGetValue(def.Oid, out var rows)) continue;
            var names = def.LabelOid is not null && labels.TryGetValue(def.LabelOid, out var l) ? l : null;
            var used = new HashSet<string>();
            foreach (var (index, data) in rows)
            {
                var label = names is not null && names.TryGetValue(index, out var n) && n.ToString() is { Length: > 0 } s ? s.Trim() : index;
                // Two rows with the same name (two "unrouted VLAN" interfaces) keep apart by index.
                if (!used.Add(label)) { label = $"{label} #{index}"; used.Add(label); }
                if (Value(def, index, data) is { } v) samples.Add(new MetricSampleDto(def.Key, label, v));
            }
        }
        return samples;

        double? Value(SnmpMetricDef def, string? index, ISnmpData data)
        {
            if (Convert(data) is not { } raw) return null;
            double value;
            if (def.Kind == MetricKind.Counter)
            {
                if (rates.Rate((checkId, def.Key, index), raw, now) is not { } rate) return null;
                value = rate;
            }
            else value = raw.Value;
            return value * def.Scale;
        }
    }
}

/// <summary>
/// Remembers the last raw value of every counter so a poll can report a per-second rate. The first poll after a start
/// reports nothing for counters; a 32-bit counter that wrapped is unwrapped, a counter that went backwards otherwise
/// (device reboot, counter reset) restarts the baseline.
/// </summary>
public sealed class CounterRates
{
    private readonly ConcurrentDictionary<(Guid check, string key, string? index), (double value, DateTimeOffset at)> _last = new();

    public double? Rate((Guid check, string key, string? index) id, RawValue raw, DateTimeOffset now)
    {
        var had = _last.TryGetValue(id, out var prev);
        _last[id] = (raw.Value, now);
        if (!had) return null;
        var seconds = (now - prev.at).TotalSeconds;
        if (seconds < 1) return null;
        var delta = raw.Value - prev.value;
        if (delta < 0)
        {
            // A 32-bit counter wraps after 4.29e9; on a 1 Gbit/s link that is every 34 s of full traffic, so allow it
            // only when the unwrapped delta is plausible (less than half the range).
            if (raw.CounterBits == 32 && (delta + 4294967296d) < 2147483648d) delta += 4294967296d;
            else return null;
        }
        return delta / seconds;
    }
}
