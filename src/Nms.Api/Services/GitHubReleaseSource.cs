using System.Net.Http.Headers;
using System.Text.Json;
using Nms.Shared.Contracts;

namespace Nms.Api.Services;

/// <summary>
/// Turns a GitHub release of this repository into a <see cref="ReleaseInput"/>: the two agent binaries' download
/// URLs plus the SHA-256 sums CI publishes next to them. Public repo, so no token is needed.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient http, IConfiguration config, ILogger<GitHubReleaseSource> log)
{
    public const string DefaultRepo = "Galactic-Technology-Group/pulsar";
    private static readonly string[] Assets = ["nms-agent-linux-x64", "nms-agent-linux-arm64", "nms-agent-linux-x64.sha256", "nms-agent-linux-arm64.sha256"];

    public string Repo => config["Releases:GitHubRepo"] is { Length: > 0 } r ? r : DefaultRepo;

    /// <param name="tag">A tag such as "v0.1.3", or null for the latest non-prerelease.</param>
    public async Task<ReleaseInput> FetchAsync(string? tag, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(tag) ? "releases/latest" : $"releases/tags/{Uri.EscapeDataString(tag.Trim())}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"No release {(tag is null ? "" : tag + " ")}found in {Repo}.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var (version, urls) = ParseRelease(doc.RootElement);
        log.LogInformation("GitHub release {Tag} resolved to agent version {Version}", doc.RootElement.GetProperty("tag_name").GetString(), version);

        var shaX64 = ParseSha(await http.GetStringAsync(urls["nms-agent-linux-x64.sha256"], ct));
        var shaArm64 = ParseSha(await http.GetStringAsync(urls["nms-agent-linux-arm64.sha256"], ct));
        return new ReleaseInput(version, urls["nms-agent-linux-x64"], urls["nms-agent-linux-arm64"], shaX64, shaArm64);
    }

    /// <summary>Version (tag without the leading "v") and the download URL of each required asset.</summary>
    internal static (string Version, Dictionary<string, string> Urls) ParseRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("Release has no tag_name");
        var version = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        if (release.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                if (name is not null && Assets.Contains(name)) urls[name] = a.GetProperty("browser_download_url").GetString()!;
            }
        var missing = Assets.Where(n => !urls.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Release {tag} is missing assets: {string.Join(", ", missing)}. Only tags built by CI can be published.");
        return (version, urls);
    }

    /// <summary>"&lt;hex&gt;  filename" as written by sha256sum.</summary>
    internal static string ParseSha(string sha256File)
    {
        var hex = sha256File.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) throw new InvalidOperationException("Checksum file does not start with a SHA-256 hex digest.");
        return hex.ToLowerInvariant();
    }
}
