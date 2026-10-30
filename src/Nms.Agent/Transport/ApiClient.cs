using Nms.Shared;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Nms.Shared.Contracts;

namespace Nms.Agent.Transport;

/// <summary>Plain HTTPS calls to the central API (enrollment, bulk result upload, version check).</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly CredentialStore _creds;
    private readonly ILogger<ApiClient> _log;

    public ApiClient(IOptions<AgentOptions> options, CredentialStore creds, ILogger<ApiClient> log)
    {
        _creds = creds;
        _log = log;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All };
        if (options.Value.DangerousAcceptAnyServerCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        _http = new HttpClient(handler) { BaseAddress = new Uri(options.Value.ApiBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"nms-agent/{AgentVersion.Current}");
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/agents/enroll", req, NmsJson.Options, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var reason = await resp.Content.ReadAsStringAsync(ct);
            throw new EnrollmentRejectedException(
                $"Enrollment token rejected by the API{(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})")}. " +
                "Tokens are single-use and expire after 24 h: re-issue one for this agent in the portal and run install.sh --token <token>.");
        }
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Enrollment failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        return (await resp.Content.ReadFromJsonAsync<EnrollResponse>(NmsJson.Options, ct))!;
    }

    public async Task<ResultsAck> PostResultsAsync(ResultsBatch batch, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/results") { Content = JsonContent.Create(batch, options: NmsJson.Options) };
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Agent credentials rejected");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ResultsAck>(NmsJson.Options, ct))!;
    }

    public async Task<AgentVersionInfo?> GetLatestVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/agents/version");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentVersionInfo>(NmsJson.Options, ct);
    }

    public async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destination);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private void AddAuth(HttpRequestMessage req)
    {
        var c = _creds.Load() ?? throw new InvalidOperationException("Agent is not enrolled");
        req.Headers.Add(AgentAuth.AgentIdHeader, c.AgentId.ToString());
        req.Headers.Add(AgentAuth.AgentSecretHeader, c.Secret);
    }
}

public static class AgentVersion
{
    public static readonly string Current =
        typeof(AgentVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
