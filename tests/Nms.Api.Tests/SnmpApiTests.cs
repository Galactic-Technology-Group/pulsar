using Microsoft.EntityFrameworkCore;
using Nms.Api.Endpoints;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared;
using Nms.Shared.Contracts;
using Nms.Shared.Snmp;

namespace Nms.Api.Tests;

public class SnmpTemplateLibraryTests
{
    [Fact]
    public void Every_template_loads_with_unique_keys_valid_oids_and_limits_on_real_readings()
    {
        Assert.NotEmpty(SnmpTemplates.All);
        Assert.Equal(SnmpTemplates.All.Count, SnmpTemplates.All.Select(t => t.Key).Distinct().Count());
        foreach (var t in SnmpTemplates.All)
        {
            Assert.Equal(t.Metrics.Count, t.Metrics.Select(m => m.Key).Distinct().Count());
            foreach (var m in t.Metrics)
            {
                foreach (var oid in new[] { m.Oid, m.LabelOid, m.FallbackOid }.Where(o => o is not null))
                    Assert.Matches(@"^1\.3\.6\.1(\.\d+)+$", oid);
                // Scalars are read by GET and need their ".0" instance; table columns must not have one.
                Assert.Equal(!m.Table, m.Oid.EndsWith(".0"));
                Assert.True(m.Scale > 0);
            }
            foreach (var th in t.Thresholds) Assert.NotNull(t.Metric(th.Metric));
        }
    }

    [Theory]
    [InlineData(5, "battery")]
    [InlineData(3, "mains")]
    public void Enum_readings_read_as_words(double value, string expected) =>
        Assert.Equal(expected, SnmpTemplates.Find("ups")!.Metric("upsOutputSource")!.Format(value));

    [Fact]
    public void Numbers_carry_units_and_traffic_gets_si_prefixes()
    {
        var ifs = SnmpTemplates.Find("interfaces")!;
        Assert.Equal("12.3 Mbit/s", ifs.Metric("ifInBps")!.Format(12_345_678));
        Assert.Equal("87 %", SnmpTemplates.Find("ups")!.Metric("upsChargeRemaining")!.Format(87));
        Assert.Equal("23.5 °C", SnmpTemplates.Find("apc-environment")!.Metric("apcProbeTemperature")!.Format(23.5));
    }

    [Theory]
    [InlineData(ThresholdOp.Above, 31, 30, true)]
    [InlineData(ThresholdOp.Above, 30, 30, false)]
    [InlineData(ThresholdOp.Below, 49, 50, true)]
    [InlineData(ThresholdOp.Equals, 5, 5, true)]
    [InlineData(ThresholdOp.NotEquals, 3, 5, true)]
    [InlineData(ThresholdOp.NotEquals, 5, 5, false)]
    public void Breach_rules(ThresholdOp op, double value, double limit, bool expected) => Assert.Equal(expected, SnmpTemplates.Breached(op, value, limit));
}

public class SnmpConfigTests
{
    [Theory]
    [InlineData("0.1.8", true)]
    [InlineData("0.1.10", true)]
    [InlineData("v0.2.0", true)]
    [InlineData("0.1.8-beta", true)]
    [InlineData("0.1.7", false)]
    [InlineData(null, false)]
    [InlineData("dev", false)]
    public void Only_agents_with_the_probe_receive_snmp_checks(string? version, bool expected) =>
        Assert.Equal(expected, AgentConfigBuilder.Supports(version, AgentConfigBuilder.SnmpSince));

    [Fact]
    public void An_snmp_check_carries_its_credential_and_template_readings()
    {
        var device = new Device { Id = Guid.NewGuid(), Name = "ups-1", Address = "10.0.0.9" };
        var cred = new SnmpCredential { Id = Guid.NewGuid(), Version = SnmpVersion.V3, User = "nms", Auth = SnmpAuth.Sha256, AuthPassword = "authpass1", Priv = SnmpPriv.Aes128, PrivPassword = "privpass1" };
        var check = new Check { Id = Guid.NewGuid(), Device = device, DeviceId = device.Id, Type = CheckType.Snmp, Port = 161, SnmpTemplate = "ups", SnmpCredential = cred };

        var def = AgentConfigBuilder.ToDefinition(check)!;
        Assert.Equal(SnmpAuth.Sha256, def.Snmp!.Auth);
        Assert.Equal("privpass1", def.Snmp.PrivPassword);
        Assert.Contains(def.Metrics!, m => m.Key == "upsOutputSource" && m.Oid == "1.3.6.1.2.1.33.1.4.1.0");

        check.SnmpCredential = null;   // credential gone: nothing for the agent to run
        Assert.Null(AgentConfigBuilder.ToDefinition(check));
    }

    [Fact]
    public void Credential_validation()
    {
        Assert.NotNull(SnmpEndpoints.CredentialError(new("v2", SnmpVersion.V2c), null));
        Assert.Null(SnmpEndpoints.CredentialError(new("v2", SnmpVersion.V2c, Community: "c0mm"), null));
        // Editing without retyping the community keeps the stored one.
        Assert.Null(SnmpEndpoints.CredentialError(new("v2", SnmpVersion.V2c), new SnmpCredential { Version = SnmpVersion.V2c, Community = "c0mm" }));
        Assert.Contains("user", SnmpEndpoints.CredentialError(new("v3", SnmpVersion.V3), null));
        Assert.Contains("authentication protocol", SnmpEndpoints.CredentialError(new("v3", SnmpVersion.V3, User: "u", Priv: SnmpPriv.Aes128, PrivPassword: "privpass1"), null));
        Assert.Contains("8 characters", SnmpEndpoints.CredentialError(new("v3", SnmpVersion.V3, User: "u", Auth: SnmpAuth.Sha256, AuthPassword: "short"), null));
        Assert.Null(SnmpEndpoints.CredentialError(new("v3", SnmpVersion.V3, User: "u", Auth: SnmpAuth.Sha256, AuthPassword: "authpass1", Priv: SnmpPriv.Aes256, PrivPassword: "privpass1"), null));
        Assert.Null(SnmpEndpoints.CredentialError(new("v3", SnmpVersion.V3, User: "u"), null));   // noAuthNoPriv
    }

    [Fact]
    public async Task A_check_cannot_use_another_sites_credential()
    {
        await using var db = new NmsDbContext(new DbContextOptionsBuilder<NmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var mine = Guid.NewGuid(); var other = Guid.NewGuid();
        var device = new Device { Id = Guid.NewGuid(), SiteId = mine, Name = "ups-1", Address = "10.0.0.9" };
        var global = new SnmpCredential { Id = Guid.NewGuid(), Name = "global", Version = SnmpVersion.V2c, Community = "c" };
        var foreign = new SnmpCredential { Id = Guid.NewGuid(), SiteId = other, Name = "theirs", Version = SnmpVersion.V2c, Community = "c" };
        db.AddRange(global, foreign); await db.SaveChangesAsync();

        Assert.Null(await SnmpEndpoints.CheckError(db, device, new CheckInput(CheckType.Snmp, SnmpTemplate: "ups", SnmpCredentialId: global.Id)));
        Assert.Contains("another site", await SnmpEndpoints.CheckError(db, device, new CheckInput(CheckType.Snmp, SnmpTemplate: "ups", SnmpCredentialId: foreign.Id)));
        Assert.Contains("template", await SnmpEndpoints.CheckError(db, device, new CheckInput(CheckType.Snmp, SnmpTemplate: "nope", SnmpCredentialId: global.Id)));
        Assert.Null(await SnmpEndpoints.CheckError(db, device, new CheckInput(CheckType.Icmp)));
    }

    [Fact]
    public void New_checks_get_the_templates_default_limits()
    {
        var check = new Check { Id = Guid.NewGuid(), Type = CheckType.Snmp, SnmpTemplate = "apc-environment" };
        var limits = SnmpEndpoints.Defaults(check).ToList();
        Assert.Contains(limits, l => l.MetricKey == "apcProbeTemperature" && l.Op == ThresholdOp.Above && l.Value == 30);
        Assert.All(limits, l => Assert.Equal(check.Id, l.CheckId));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(24, 5)]
    [InlineData(168, 30)]
    public void History_is_raw_for_short_ranges_and_bucketed_for_long_ones(int hours, int? bucketMinutes) =>
        Assert.Equal(bucketMinutes is null ? null : TimeSpan.FromMinutes(bucketMinutes.Value), SnmpEndpoints.Bucket(TimeSpan.FromHours(hours)));
}
