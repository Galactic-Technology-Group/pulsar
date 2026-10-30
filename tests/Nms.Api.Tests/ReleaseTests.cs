using System.Text.Json;
using Nms.Api.Services;
using Nms.Data.Entities;

namespace Nms.Api.Tests;

public class ReleaseTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v0.1.3",
          "assets": [
            { "name": "install.sh", "browser_download_url": "https://github.com/x/y/releases/download/v0.1.3/install.sh" },
            { "name": "nms-agent-linux-x64", "browser_download_url": "https://github.com/x/y/releases/download/v0.1.3/nms-agent-linux-x64" },
            { "name": "nms-agent-linux-x64.sha256", "browser_download_url": "https://github.com/x/y/releases/download/v0.1.3/nms-agent-linux-x64.sha256" },
            { "name": "nms-agent-linux-arm64", "browser_download_url": "https://github.com/x/y/releases/download/v0.1.3/nms-agent-linux-arm64" },
            { "name": "nms-agent-linux-arm64.sha256", "browser_download_url": "https://github.com/x/y/releases/download/v0.1.3/nms-agent-linux-arm64.sha256" }
          ]
        }
        """;

    [Fact]
    public void GitHub_release_yields_version_and_binary_urls()
    {
        using var doc = JsonDocument.Parse(ReleaseJson);
        var (version, urls) = GitHubReleaseSource.ParseRelease(doc.RootElement);
        Assert.Equal("0.1.3", version);
        Assert.EndsWith("/nms-agent-linux-arm64", urls["nms-agent-linux-arm64"]);
        Assert.Equal(4, urls.Count);
    }

    [Fact]
    public void Release_without_ci_assets_is_refused()
    {
        using var doc = JsonDocument.Parse("""{ "tag_name": "v9.9.9", "assets": [] }""");
        var ex = Assert.Throws<InvalidOperationException>(() => GitHubReleaseSource.ParseRelease(doc.RootElement));
        Assert.Contains("nms-agent-linux-x64", ex.Message);
    }

    [Fact]
    public void Checksum_file_parses_sha256sum_format()
    {
        var hex = new string('a', 64);
        Assert.Equal(hex, GitHubReleaseSource.ParseSha($"{hex.ToUpperInvariant()}  nms-agent-linux-x64\n"));
        Assert.Throws<InvalidOperationException>(() => GitHubReleaseSource.ParseSha("not a hash"));
    }

    [Fact]
    public void Heartbeat_offers_update_only_when_versions_differ()
    {
        var current = new AgentRelease { Version = "0.1.3", DownloadUrlX64 = "x", DownloadUrlArm64 = "a", Sha256X64 = "1", Sha256Arm64 = "2" };
        Assert.Null(ReleasePublisher.PendingUpdate(null, "0.1.0"));
        Assert.Null(ReleasePublisher.PendingUpdate(current, "0.1.3"));
        var u = ReleasePublisher.PendingUpdate(current, "0.1.0");
        Assert.NotNull(u);
        Assert.Equal("0.1.3", u.Version);
        Assert.Equal("a", u.DownloadUrlArm64);
    }
}
