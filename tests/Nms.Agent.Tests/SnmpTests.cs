using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nms.Agent.Probes.Snmp;
using Nms.Agent.Storage;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Tests;

public class SnmpReadingTests
{
    [Fact]
    public void Numeric_types_convert_and_missing_objects_do_not()
    {
        Assert.Equal(new RawValue(-5), SnmpReadings.Convert(new Integer32(-5)));
        Assert.Equal(new RawValue(7), SnmpReadings.Convert(new Gauge32(7)));
        Assert.Equal(new RawValue(100), SnmpReadings.Convert(new TimeTicks(100)));
        Assert.Equal(new RawValue(9, 32), SnmpReadings.Convert(new Counter32(9)));
        Assert.Equal(new RawValue(9, 64), SnmpReadings.Convert(new Counter64(9)));
        Assert.Equal(new RawValue(23.5), SnmpReadings.Convert(new OctetString(" 23.5 ")));
        Assert.Null(SnmpReadings.Convert(new OctetString("eth0")));
        Assert.Null(SnmpReadings.Convert(new NoSuchObject()));
        Assert.Null(SnmpReadings.Convert(new NoSuchInstance()));
        Assert.Null(SnmpReadings.Convert(new EndOfMibView()));
    }

    [Fact]
    public void Row_index_is_the_suffix_under_the_column()
    {
        var column = new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8").ToNumerical();
        Assert.Equal("12", SnmpReadings.IndexUnder(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8.12"), column));
        Assert.Equal("1.4", SnmpReadings.IndexUnder(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8.1.4"), column));
        Assert.Null(SnmpReadings.IndexUnder(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.9.1"), column));
        Assert.Null(SnmpReadings.IndexUnder(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.8"), column));
    }

    [Fact]
    public void Counters_become_rates_and_survive_a_32_bit_wrap_but_not_a_reset()
    {
        var rates = new CounterRates();
        var id = (Guid.NewGuid(), "ifInBps", (string?)"1");
        var t = DateTimeOffset.UtcNow;
        Assert.Null(rates.Rate(id, new RawValue(1000, 32), t));                                       // first poll: no baseline
        Assert.Equal(100, rates.Rate(id, new RawValue(7000, 32), t.AddSeconds(60)));                  // 6000 in 60 s
        // Dropping from 7000 to 500 would be a 4.29e9 jump if read as a wrap: implausible, so it is a reset.
        Assert.Null(rates.Rate(id, new RawValue(500, 32), t.AddSeconds(120)));
        Assert.Equal(50, rates.Rate(id, new RawValue(3500, 32), t.AddSeconds(180)));                // new baseline holds
    }

    [Fact]
    public void A_wrap_is_unwrapped_only_when_plausible()
    {
        var rates = new CounterRates();
        var id = (Guid.NewGuid(), "ifInBps", (string?)"1");
        var t = DateTimeOffset.UtcNow;
        rates.Rate(id, new RawValue(4294967000, 32), t);
        // 296 to the wrap plus 304 after it = 600 in 60 s.
        Assert.Equal(10, rates.Rate(id, new RawValue(304, 32), t.AddSeconds(60))!.Value, 6);
        // A 64-bit counter going backwards is a reset (reboot): no rate, new baseline.
        var id64 = (Guid.NewGuid(), "ifInBps", (string?)"1");
        rates.Rate(id64, new RawValue(5000, 64), t);
        Assert.Null(rates.Rate(id64, new RawValue(10, 64), t.AddSeconds(60)));
        Assert.Equal(1, rates.Rate(id64, new RawValue(70, 64), t.AddSeconds(120)));
    }

    [Fact]
    public void Build_scales_scalars_names_rows_and_keeps_duplicate_names_apart()
    {
        var check = Guid.NewGuid();
        var defs = new List<SnmpMetricDef>
        {
            new("charge", "1.3.6.1.2.1.33.1.2.4.0"),
            new("uptime", "1.3.6.1.2.1.1.3.0", Scale: 0.01),
            new("status", "1.3.6.1.2.1.2.2.1.8", Table: true, LabelOid: "1.3.6.1.2.1.2.2.1.2"),
            new("inBps", "1.3.6.1.2.1.2.2.1.10", MetricKind.Counter, Scale: 8, Table: true, LabelOid: "1.3.6.1.2.1.2.2.1.2"),
        };
        var scalars = new Dictionary<string, ISnmpData> { ["1.3.6.1.2.1.33.1.2.4.0"] = new Integer32(87), ["1.3.6.1.2.1.1.3.0"] = new TimeTicks(12345) };
        var labels = new Dictionary<string, IReadOnlyDictionary<string, ISnmpData>>
        {
            ["1.3.6.1.2.1.2.2.1.2"] = new Dictionary<string, ISnmpData> { ["1"] = new OctetString("eth0"), ["2"] = new OctetString("eth0"), ["3"] = new OctetString("") },
        };
        Dictionary<string, IReadOnlyDictionary<string, ISnmpData>> Tables(uint octets) => new()
        {
            ["1.3.6.1.2.1.2.2.1.8"] = new Dictionary<string, ISnmpData> { ["1"] = new Integer32(1), ["2"] = new Integer32(2), ["3"] = new Integer32(1) },
            ["1.3.6.1.2.1.2.2.1.10"] = new Dictionary<string, ISnmpData> { ["1"] = new Counter32(octets) },
        };
        var rates = new CounterRates();
        var t = DateTimeOffset.UtcNow;

        var first = SnmpReadings.Build(check, defs, scalars, Tables(1000), labels, rates, t);
        Assert.Contains(new MetricSampleDto("charge", null, 87), first);
        Assert.Contains(new MetricSampleDto("uptime", null, 123.45), first);
        Assert.Contains(new MetricSampleDto("status", "eth0", 1), first);
        Assert.Contains(new MetricSampleDto("status", "eth0 #2", 2), first);     // second row with the same name
        Assert.Contains(new MetricSampleDto("status", "3", 1), first);           // blank name falls back to the index
        Assert.DoesNotContain(first, s => s.Key == "inBps");                      // no baseline yet

        var second = SnmpReadings.Build(check, defs, scalars, Tables(2000), labels, rates, t.AddSeconds(100));
        Assert.Contains(new MetricSampleDto("inBps", "eth0", 80), second);       // 1000 octets / 100 s × 8
    }

    [Fact]
    public void Privacy_without_authentication_is_refused_before_any_packet()
    {
        var f = Assert.Throws<SnmpFailure>(() => SnmpSession.Privacy(new SnmpCredentialDto(SnmpVersion.V3, null, "u", SnmpAuth.None, null, SnmpPriv.Aes128, "privpass1")));
        Assert.Equal("PrivacyNeedsAuth", f.Detail);
    }
}

/// <summary>SnmpSession against a minimal SNMPv2c responder on loopback: GET, GETBULK walks, wrong community, silence.</summary>
public sealed class SnmpSessionTests : IDisposable
{
    private readonly FakeSnmpAgent _agent = new("secret", new SortedDictionary<string, ISnmpData>(new OidComparer())
    {
        ["1.3.6.1.2.1.1.3.0"] = new TimeTicks(500),
        ["1.3.6.1.2.1.2.2.1.2.1"] = new OctetString("lo"),
        ["1.3.6.1.2.1.2.2.1.2.2"] = new OctetString("eth0"),
        ["1.3.6.1.2.1.2.2.1.8.1"] = new Integer32(1),
        ["1.3.6.1.2.1.2.2.1.8.2"] = new Integer32(2),
        ["1.3.6.1.2.1.2.2.1.10.1"] = new Counter32(42),
    });

    private SnmpSession Session(string community = "secret", int timeoutMs = 1000) =>
        new(new SnmpCredentialDto(SnmpVersion.V2c, community), _agent.EndPoint, IPAddress.Loopback, TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public async Task Get_returns_values_and_marks_missing_objects()
    {
        using var s = Session();
        var r = await s.GetAsync(["1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.33.1.2.4.0"], default);
        Assert.Equal(500u, ((TimeTicks)r["1.3.6.1.2.1.1.3.0"]).ToUInt32());
        Assert.IsType<NoSuchObject>(r["1.3.6.1.2.1.33.1.2.4.0"]);
        Assert.NotNull(s.FirstAnswerMs);
    }

    [Fact]
    public async Task Walk_stops_at_the_end_of_the_column()
    {
        using var s = Session();
        var rows = await s.WalkAsync("1.3.6.1.2.1.2.2.1.8", default);
        Assert.Equal(["1", "2"], rows.Keys.Order());
        Assert.Empty(await s.WalkAsync("1.3.6.1.2.1.31.1.1.1.6", default));   // no 64-bit counters on this device
    }

    [Fact]
    public async Task Wrong_community_times_out_like_a_real_agent()
    {
        using var s = Session("public", timeoutMs: 200);
        var f = await Assert.ThrowsAsync<SnmpFailure>(() => s.GetAsync(["1.3.6.1.2.1.1.3.0"], default));
        Assert.Equal("TimedOut", f.Detail);
        Assert.Equal(2, _agent.Ignored);   // one retry
    }

    public void Dispose() => _agent.Dispose();

    private sealed class OidComparer : IComparer<string>
    {
        public int Compare(string? a, string? b) => new ObjectIdentifier(a!).CompareTo(new ObjectIdentifier(b!));
    }

    /// <summary>Answers v2c GET and GETBULK from a sorted OID table; ignores other communities, as devices do.</summary>
    private sealed class FakeSnmpAgent : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly string _community;
        private readonly SortedDictionary<string, ISnmpData> _mib;
        public int Ignored;
        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

        public FakeSnmpAgent(string community, SortedDictionary<string, ISnmpData> mib)
        {
            _community = community; _mib = mib;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult got;
                try { got = await _udp.ReceiveAsync(_stop.Token); } catch { return; }
                var msg = MessageFactory.ParseMessages(got.Buffer, new Lextm.SharpSnmpLib.Security.UserRegistry())[0];
                if (msg.Community().ToString() != _community) { Interlocked.Increment(ref Ignored); continue; }
                var vars = msg switch
                {
                    GetRequestMessage g => g.Variables().Select(v => new Variable(v.Id, _mib.TryGetValue(v.Id.ToString(), out var d) ? d : new NoSuchObject())).ToList(),
                    GetBulkRequestMessage b => Bulk(b.Variables()[0].Id, b.Pdu().ErrorIndex.ToInt32()),   // GETBULK carries max-repetitions in the error-index field
                    _ => new List<Variable>(),
                };
                var resp = new ResponseMessage(msg.RequestId(), VersionCode.V2, new OctetString(_community), ErrorCode.NoError, 0, vars);
                await _udp.SendAsync(resp.ToBytes(), got.RemoteEndPoint);
            }
        }

        private List<Variable> Bulk(ObjectIdentifier start, int max)
        {
            var after = _mib.Keys.Where(k => new ObjectIdentifier(k) > start).Take(max).Select(k => new Variable(new ObjectIdentifier(k), _mib[k])).ToList();
            if (after.Count < max) after.Add(new Variable(after.Count > 0 ? after[^1].Id : start, new EndOfMibView()));
            return after;
        }

        public void Dispose() { _stop.Cancel(); _udp.Dispose(); }
    }
}

public sealed class OutboxMetricsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nms-outbox-" + Guid.NewGuid());

    [Fact]
    public void Readings_survive_the_outbox_and_an_old_outbox_gains_the_column()
    {
        // An outbox written by 0.1.7 has no metrics column.
        Directory.CreateDirectory(_dir);
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "outbox.db")};Pooling=false"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = "CREATE TABLE outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, check_id TEXT NOT NULL, time TEXT NOT NULL, ok INTEGER NOT NULL, latency_ms INTEGER NULL, detail TEXT NULL);" +
                              $"INSERT INTO outbox(check_id,time,ok) VALUES('{Guid.NewGuid()}','{DateTimeOffset.UtcNow:O}',1);";
            cmd.ExecuteNonQuery();
        }

        using var outbox = new ResultOutbox(Options.Create(new AgentOptions { StateDir = _dir }), NullLogger<ResultOutbox>.Instance);
        var check = Guid.NewGuid();
        outbox.Enqueue(new CheckResultDto(check, DateTimeOffset.UtcNow, true, 4, null, [new("upsOutputSource", null, 5), new("ifInBps", "eth0", 1.5e6)]));

        var (items, _) = outbox.Peek(10);
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].Metrics);
        Assert.Equal([new MetricSampleDto("upsOutputSource", null, 5), new MetricSampleDto("ifInBps", "eth0", 1.5e6)], items[1].Metrics);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
