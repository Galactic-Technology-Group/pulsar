using System.Text.Json;
using Nms.Shared.Contracts;

namespace Nms.Shared.Snmp;

/// <summary>A reading a template collects. Values maps raw enum numbers to words ("5" → "battery") for display.</summary>
public sealed record SnmpTemplateMetric(string Key, string Name, string Oid, MetricKind Kind = MetricKind.Gauge, string? Unit = null,
    double Scale = 1, bool Table = false, string? LabelOid = null, IReadOnlyDictionary<string, string>? Values = null, string? FallbackOid = null)
{
    public SnmpMetricDef ToDef() => new(Key, Oid, Kind, Scale, Table, LabelOid, FallbackOid);

    /// <summary>"battery" for an enum value, "23.5 °C" for a number.</summary>
    public string Format(double value)
    {
        if (Values is not null && Values.TryGetValue(((long)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture), out var name)) return name;
        if (Unit == "bit/s")
        {
            var (scaled, prefix) = Math.Abs(value) switch { >= 1e9 => (value / 1e9, "G"), >= 1e6 => (value / 1e6, "M"), >= 1e3 => (value / 1e3, "k"), _ => (value, "") };
            return $"{scaled.ToString(scaled >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)} {prefix}bit/s";
        }
        var number = Math.Abs(value) >= 1e6 ? value.ToString("0.###e0", System.Globalization.CultureInfo.InvariantCulture)
            : Math.Abs(value) >= 100 || value == Math.Round(value) ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return Unit is null ? number : $"{number} {Unit}";
    }
}

/// <summary>A limit created on a new check from its template; the check's own copy can then be edited or removed.</summary>
public sealed record SnmpTemplateThreshold(string Metric, ThresholdOp Op, double Value, string Label);

public sealed record SnmpTemplate(string Key, string Name, string Description, IReadOnlyList<SnmpTemplateMetric> Metrics, IReadOnlyList<SnmpTemplateThreshold> Thresholds)
{
    public SnmpTemplateMetric? Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key);
}

/// <summary>
/// The template library, shipped as <c>templates.json</c> inside this assembly so the API (which expands templates into
/// agent config and names readings in alerts) and the portal (which labels them) read the same file.
/// </summary>
public static class SnmpTemplates
{
    private static readonly Lazy<IReadOnlyList<SnmpTemplate>> _all = new(Load);

    public static IReadOnlyList<SnmpTemplate> All => _all.Value;

    public static SnmpTemplate? Find(string? key) => key is null ? null : All.FirstOrDefault(t => t.Key == key);

    public static string Describe(ThresholdOp op) => op switch
    {
        ThresholdOp.Above => ">", ThresholdOp.Below => "<", ThresholdOp.Equals => "=", ThresholdOp.NotEquals => "≠", _ => "?",
    };

    public static bool Breached(ThresholdOp op, double value, double limit) => op switch
    {
        ThresholdOp.Above => value > limit,
        ThresholdOp.Below => value < limit,
        ThresholdOp.Equals => Math.Abs(value - limit) < 1e-9,
        ThresholdOp.NotEquals => Math.Abs(value - limit) >= 1e-9,
        _ => false,
    };

    private static IReadOnlyList<SnmpTemplate> Load()
    {
        using var stream = typeof(SnmpTemplates).Assembly.GetManifestResourceStream("Nms.Shared.Snmp.templates.json")
            ?? throw new InvalidOperationException("SNMP template library is missing from Nms.Shared.");
        return JsonSerializer.Deserialize<List<SnmpTemplate>>(stream, NmsJson.Options) ?? [];
    }
}
