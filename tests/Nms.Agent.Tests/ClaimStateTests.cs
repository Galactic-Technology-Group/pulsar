using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nms.Agent;

namespace Nms.Agent.Tests;

public sealed class ClaimStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nms-claim-" + Guid.NewGuid());
    private CredentialStore Store() => new(Options.Create(new AgentOptions { StateDir = _dir }), NullLogger<CredentialStore>.Instance);

    [Fact]
    public void Claim_survives_restart_and_code_file_is_written_for_the_console()
    {
        var claim = new PendingClaim(Guid.NewGuid(), "k".PadRight(43, 'x'), "KD7P-4QRS", DateTimeOffset.UtcNow.AddMinutes(15));
        Store().SaveClaim(claim);

        var reloaded = Store().LoadClaim();
        Assert.Equal(claim, reloaded);
        Assert.Equal("KD7P-4QRS", File.ReadAllText(Path.Combine(_dir, "claim-code")).Trim());
    }

    [Fact]
    public void Saving_credentials_clears_the_claim_and_the_code_file()
    {
        var store = Store();
        store.SaveClaim(new PendingClaim(Guid.NewGuid(), "k".PadRight(43, 'x'), "KD7P-4QRS", DateTimeOffset.UtcNow.AddMinutes(15)));
        store.Save(new AgentCredentials(Guid.NewGuid(), "secret", Guid.NewGuid(), "HQ"));

        Assert.Null(Store().LoadClaim());
        Assert.False(File.Exists(Path.Combine(_dir, "claim-code")));
        Assert.NotNull(Store().Load());
    }

    [Fact]
    public void Unreadable_claim_file_starts_a_new_claim_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "claim.json"), "{not json");
        Assert.Null(Store().LoadClaim());
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }
}
