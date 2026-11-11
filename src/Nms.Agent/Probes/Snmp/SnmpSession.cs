using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Probes.Snmp;

/// <summary>An SNMP failure worth naming in the result's detail ("TimedOut", "AuthFailure").</summary>
public sealed class SnmpFailure(string detail) : Exception(detail)
{
    public string Detail { get; } = detail;
}

/// <summary>
/// One poll's conversation with a device: SNMPv2c or SNMPv3 (USM) GET and GETBULK over a UDP socket the agent owns, so it
/// can be bound to the VLAN interface the check should leave from. SharpSnmpLib only encodes and decodes the messages.
/// </summary>
public sealed class SnmpSession : IDisposable
{
    private const int MaxRepetitions = 25;
    private const int MaxRowsPerColumn = 2000;
    private readonly Socket _socket;
    private readonly IPEndPoint _target;
    private readonly SnmpCredentialDto _cred;
    private readonly TimeSpan _timeout;
    private readonly IPrivacyProvider _privacy;
    private readonly UserRegistry _users = new();
    private ISnmpMessage? _report;
    private readonly byte[] _buffer = new byte[65535];

    /// <summary>Round-trip time of the first answered request: the "latency" shown for the check.</summary>
    public int? FirstAnswerMs { get; private set; }

    public SnmpSession(SnmpCredentialDto cred, IPEndPoint target, IPAddress? source, TimeSpan timeout)
    {
        _cred = cred;
        _target = target;
        _timeout = timeout;
        _privacy = Privacy(cred);
        if (cred.Version == SnmpVersion.V3) _users.Add(new OctetString(cred.User ?? ""), _privacy);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(source ?? IPAddress.Any, 0));
    }

    internal static IPrivacyProvider Privacy(SnmpCredentialDto c)
    {
        if (c.Version != SnmpVersion.V3) return new DefaultPrivacyProvider(DefaultAuthenticationProvider.Instance);
#pragma warning disable CS0618 // MD5, SHA-1 and DES are obsolete but still all that older UPS cards and switches offer.
        IAuthenticationProvider auth = c.Auth switch
        {
            SnmpAuth.None => DefaultAuthenticationProvider.Instance,
            SnmpAuth.Md5 => new MD5AuthenticationProvider(new OctetString(c.AuthPassword ?? "")),
            SnmpAuth.Sha1 => new SHA1AuthenticationProvider(new OctetString(c.AuthPassword ?? "")),
            SnmpAuth.Sha256 => new SHA256AuthenticationProvider(new OctetString(c.AuthPassword ?? "")),
            SnmpAuth.Sha384 => new SHA384AuthenticationProvider(new OctetString(c.AuthPassword ?? "")),
            SnmpAuth.Sha512 => new SHA512AuthenticationProvider(new OctetString(c.AuthPassword ?? "")),
            _ => throw new SnmpFailure("UnsupportedAuth"),
        };
        if (c.Priv != SnmpPriv.None && c.Auth == SnmpAuth.None) throw new SnmpFailure("PrivacyNeedsAuth");
        return c.Priv switch
        {
            SnmpPriv.None => new DefaultPrivacyProvider(auth),
            SnmpPriv.Des => new DESPrivacyProvider(new OctetString(c.PrivPassword ?? ""), auth),
            SnmpPriv.Aes128 => new AESPrivacyProvider(new OctetString(c.PrivPassword ?? ""), auth),
            SnmpPriv.Aes192 => new AES192PrivacyProvider(new OctetString(c.PrivPassword ?? ""), auth),
            SnmpPriv.Aes256 => new AES256PrivacyProvider(new OctetString(c.PrivPassword ?? ""), auth),
            _ => throw new SnmpFailure("UnsupportedPrivacy"),
        };
#pragma warning restore CS0618
    }

    /// <summary>GETs scalar OIDs in chunks; missing objects are simply absent from the answer.</summary>
    public async Task<Dictionary<string, ISnmpData>> GetAsync(IEnumerable<string> oids, CancellationToken ct)
    {
        var result = new Dictionary<string, ISnmpData>();
        foreach (var chunk in oids.Distinct().Chunk(10))
        {
            var vars = chunk.Select(o => new Variable(new ObjectIdentifier(o))).ToList();
            var resp = await RequestAsync(ids => _cred.Version == SnmpVersion.V3
                ? new GetRequestMessage(VersionCode.V3, ids.message, ids.request, new OctetString(_cred.User ?? ""), Context(), vars, _privacy, Messenger.MaxMessageSize, _report!)
                : new GetRequestMessage(ids.request, VersionCode.V2, new OctetString(_cred.Community ?? ""), vars), ct);
            var pdu = resp.Pdu();
            if (pdu.ErrorStatus.ToInt32() != 0) throw new SnmpFailure($"ErrorStatus{pdu.ErrorStatus.ToInt32()}");
            foreach (var v in pdu.Variables) result[v.Id.ToString()] = v.Data;
        }
        return result;
    }

    /// <summary>Walks one table column with GETBULK; returns row index → value.</summary>
    public async Task<Dictionary<string, ISnmpData>> WalkAsync(string columnOid, CancellationToken ct)
    {
        var column = new ObjectIdentifier(columnOid).ToNumerical();
        var rows = new Dictionary<string, ISnmpData>();
        var next = new ObjectIdentifier(columnOid);
        while (rows.Count < MaxRowsPerColumn)
        {
            var vars = new List<Variable> { new(next) };
            var resp = await RequestAsync(ids => _cred.Version == SnmpVersion.V3
                ? new GetBulkRequestMessage(VersionCode.V3, ids.message, ids.request, new OctetString(_cred.User ?? ""), Context(), 0, MaxRepetitions, vars, _privacy, Messenger.MaxMessageSize, _report!)
                : new GetBulkRequestMessage(ids.request, VersionCode.V2, new OctetString(_cred.Community ?? ""), 0, MaxRepetitions, vars), ct);
            var pdu = resp.Pdu();
            if (pdu.ErrorStatus.ToInt32() != 0) throw new SnmpFailure($"ErrorStatus{pdu.ErrorStatus.ToInt32()}");
            var progressed = false;
            foreach (var v in pdu.Variables)
            {
                if (v.Data is EndOfMibView || SnmpReadings.IndexUnder(v.Id, column) is not { } index) return rows;
                if (!rows.TryAdd(index, v.Data)) return rows;   // a device that loops
                next = v.Id;
                progressed = true;
            }
            if (!progressed) return rows;
        }
        return rows;
    }

    private OctetString Context() => string.IsNullOrEmpty(_cred.Context) ? OctetString.Empty : new OctetString(_cred.Context);

    /// <summary>Sends a request (after USM discovery for v3), with one retry, and maps v3 reports to a named failure.</summary>
    private async Task<ISnmpMessage> RequestAsync(Func<(int message, int request), ISnmpMessage> build, CancellationToken ct)
    {
        if (_cred.Version == SnmpVersion.V3 && _report is null)
        {
            var (m, r) = (Messenger.NextMessageId, Messenger.NextRequestId);
            var discovery = new Discovery(m, r, Messenger.MaxMessageSize);
            _report = await ExchangeAsync(discovery.ToBytes(), m, r, timed: false, ct);
        }
        for (var attempt = 0; ; attempt++)
        {
            var ids = (Messenger.NextMessageId, Messenger.NextRequestId);
            try
            {
                var resp = await ExchangeAsync(build(ids).ToBytes(), ids.Item1, ids.Item2, timed: true, ct);
                if (resp is ReportMessage report) throw new SnmpFailure(ReportReason(report));
                return resp;
            }
            catch (SnmpFailure f) when (f.Detail == "TimedOut" && attempt == 0) { }
        }
    }

    private async Task<ISnmpMessage> ExchangeAsync(byte[] request, int messageId, int requestId, bool timed, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await _socket.SendToAsync(request, SocketFlags.None, _target, cts.Token);
            while (true)
            {
                var got = await _socket.ReceiveFromAsync(_buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                IList<ISnmpMessage> msgs;
                try { msgs = MessageFactory.ParseMessages(_buffer, 0, got.ReceivedBytes, _users); }
                catch (Exception) { throw new SnmpFailure(_cred.Version == SnmpVersion.V3 ? "DecryptionFailed" : "BadResponse"); }
                if (msgs.Count == 0) continue;
                var m = msgs[0];
                // v3 answers echo the message id; v2c answers the request id. Anything else is a late answer to a retry.
                var mine = _cred.Version == SnmpVersion.V3 ? m.MessageId() == messageId : m.RequestId() == requestId;
                if (!mine) continue;
                if (timed && FirstAnswerMs is null) FirstAnswerMs = (int)sw.ElapsedMilliseconds;
                return m;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SnmpFailure("TimedOut"); }
        // Windows reports an ICMP port-unreachable on UDP as a reset: nothing listens on 161.
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { throw new SnmpFailure("PortUnreachable"); }
        catch (SocketException ex) { throw new SnmpFailure(ex.SocketErrorCode.ToString()); }
    }

    private static string ReportReason(ReportMessage report)
    {
        var oid = report.Pdu().Variables.FirstOrDefault()?.Id;
        if (oid == Messenger.AuthenticationFailure) return "AuthFailure";
        if (oid == Messenger.UnknownSecurityName) return "UnknownUser";
        if (oid == Messenger.UnsupportedSecurityLevel) return "UnsupportedSecurityLevel";
        if (oid == Messenger.DecryptionError) return "DecryptionFailed";
        if (oid == Messenger.NotInTimeWindow) return "NotInTimeWindow";
        if (oid == Messenger.UnknownEngineId) return "UnknownEngineId";
        return "Report " + oid;
    }

    public void Dispose() => _socket.Dispose();
}
