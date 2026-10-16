using Nms.Data.Entities;
using Nms.Shared;
using Nms.Worker.Services;

namespace Nms.Worker.Tests;

public class AlertRouterTests
{
    private static readonly Guid SiteA = Guid.NewGuid(), SiteB = Guid.NewGuid();
    private static NotificationChannel Channel(string name) => new() { Id = Guid.NewGuid(), Name = name, Type = NotificationChannelType.Email, Enabled = true };
    private static AlertRule Rule(AlertScope scope, NotificationChannel ch, Guid? siteId = null, string? tag = null, bool criticalOnly = true, int cooldown = 300) =>
        new() { Id = Guid.NewGuid(), Name = scope.ToString(), Scope = scope, Channel = ch, ChannelId = ch.Id, SiteId = siteId, Tag = tag, CriticalOnly = criticalOnly, CooldownSec = cooldown, Enabled = true };
    private static Incident Down(Guid site, Guid? checkId = null) => new() { Id = Guid.NewGuid(), Kind = IncidentKind.CheckDown, SiteId = site, CheckId = checkId ?? Guid.NewGuid(), OpenedUtc = DateTimeOffset.UtcNow };
    private static Device Dev(bool critical = true, params string[] tags) => new() { Id = Guid.NewGuid(), SiteId = SiteA, Name = "d", Address = "10.0.0.1", IsCritical = critical, Tags = tags };

    [Fact]
    public void Global_rule_matches_everything_site_rule_only_its_site()
    {
        var global = Channel("global"); var siteOnly = Channel("siteA");
        var rules = new[] { Rule(AlertScope.Global, global), Rule(AlertScope.Site, siteOnly, SiteA) };
        var router = new AlertRouter();

        var a = router.Resolve(rules, Down(SiteA), Dev(), DateTimeOffset.UtcNow);
        var b = router.Resolve(rules, Down(SiteB), Dev(), DateTimeOffset.UtcNow);

        Assert.Equal(new[] { global.Id, siteOnly.Id }.Order(), a.Select(t => t.Channel.Id).Order());
        Assert.Equal(new[] { global.Id }, b.Select(t => t.Channel.Id));
    }

    [Fact]
    public void Tag_rule_is_case_insensitive()
    {
        var ch = Channel("tag");
        var rules = new[] { Rule(AlertScope.Tag, ch, tag: "Core") };
        var router = new AlertRouter();
        Assert.Single(router.Resolve(rules, Down(SiteA), Dev(true, "core", "switch"), DateTimeOffset.UtcNow));
        Assert.Empty(router.Resolve(rules, Down(SiteA), Dev(true, "edge"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Critical_only_skips_non_critical_devices()
    {
        var ch = Channel("c");
        var router = new AlertRouter();
        Assert.Empty(router.Resolve(new[] { Rule(AlertScope.Global, ch, criticalOnly: true) }, Down(SiteA), Dev(critical: false), DateTimeOffset.UtcNow));
        Assert.Single(router.Resolve(new[] { Rule(AlertScope.Global, ch, criticalOnly: false) }, Down(SiteA), Dev(critical: false), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cooldown_suppresses_repeat_for_same_check_but_not_other_checks()
    {
        var ch = Channel("c");
        var rules = new[] { Rule(AlertScope.Global, ch, cooldown: 300) };
        var router = new AlertRouter();
        var checkId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        Assert.Single(router.Resolve(rules, Down(SiteA, checkId), Dev(), t0));
        Assert.Empty(router.Resolve(rules, Down(SiteA, checkId), Dev(), t0.AddSeconds(60)));
        Assert.Single(router.Resolve(rules, Down(SiteA, Guid.NewGuid()), Dev(), t0.AddSeconds(60)));
        Assert.Single(router.Resolve(rules, Down(SiteA, checkId), Dev(), t0.AddSeconds(301)));
    }

    [Fact]
    public void Two_rules_to_the_same_channel_send_once()
    {
        var ch = Channel("c");
        var rules = new[] { Rule(AlertScope.Global, ch), Rule(AlertScope.Site, ch, SiteA) };
        Assert.Single(new AlertRouter().Resolve(rules, Down(SiteA), Dev(), DateTimeOffset.UtcNow));
    }
}
