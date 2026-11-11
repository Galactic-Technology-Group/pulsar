using Microsoft.EntityFrameworkCore;
using Nms.Data.Entities;

namespace Nms.Data;

public class NmsDbContext(DbContextOptions<NmsDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentInterface> AgentInterfaces => Set<AgentInterface>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Check> Checks => Set<Check>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<AgentRelease> AgentReleases => Set<AgentRelease>();
    public DbSet<AgentClaim> AgentClaims => Set<AgentClaim>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SnmpCredential> SnmpCredentials => Set<SnmpCredential>();
    public DbSet<MetricThreshold> MetricThresholds => Set<MetricThreshold>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("nms");

        b.Entity<Site>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => new { x.SiteId, x.Name }).IsUnique();
            e.HasIndex(x => x.EnrollmentTokenHash);
            e.HasOne(x => x.Site).WithMany(s => s.Agents).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentInterface>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.Name }).IsUnique();
            e.HasOne(x => x.Agent).WithMany(a => a.Interfaces).HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => new { x.SiteId, x.Address, x.VlanId }).IsUnique();
            e.HasIndex(x => new { x.SiteId, x.MacAddress }).IsUnique().HasFilter("mac_address IS NOT NULL");
            e.Property(x => x.MacAddress).HasMaxLength(17);
            e.HasIndex(x => x.Tags).HasMethod("gin");
            e.HasOne(x => x.Site).WithMany(s => s.Devices).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Check>(e =>
        {
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.State);
            e.HasOne(x => x.Device).WithMany(d => d.Checks).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            // A credential in use cannot be deleted (the API answers 409 first; Restrict is the backstop).
            e.HasOne(x => x.SnmpCredential).WithMany().HasForeignKey(x => x.SnmpCredentialId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SnmpTemplate).HasMaxLength(64);
            e.Property(x => x.LastMetricsJson).HasColumnType("jsonb");
        });

        b.Entity<SnmpCredential>(e =>
        {
            e.HasIndex(x => new { x.SiteId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
        });

        b.Entity<MetricThreshold>(e =>
        {
            e.HasIndex(x => x.CheckId);
            e.HasOne(x => x.Check).WithMany(c => c.Thresholds).HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.MetricKey).HasMaxLength(64);
            e.Property(x => x.Instance).HasMaxLength(128);
            e.Property(x => x.Label).HasMaxLength(128);
        });

        b.Entity<MetricSample>(e =>
        {
            e.ToTable("metric_samples");
            // Timescale requires the partitioning column in every unique index.
            e.HasKey(x => new { x.Time, x.CheckId, x.Metric, x.Instance });
            e.HasIndex(x => new { x.CheckId, x.Metric, x.Time }).IsDescending(false, false, true);
            e.Property(x => x.Metric).HasMaxLength(64);
            e.Property(x => x.Instance).HasMaxLength(128);
        });

        b.Entity<CheckResult>(e =>
        {
            e.ToTable("check_results");
            // Timescale requires the partitioning column in every unique index.
            e.HasKey(x => new { x.Time, x.CheckId });
            e.HasIndex(x => new { x.CheckId, x.Time }).IsDescending(false, true);
            e.Property(x => x.Detail).HasMaxLength(512);
        });

        b.Entity<Incident>(e =>
        {
            e.HasIndex(x => new { x.SiteId, x.ClosedUtc });
            e.HasIndex(x => new { x.CheckId, x.ClosedUtc });
            e.HasIndex(x => new { x.AgentId, x.ClosedUtc });
            e.HasIndex(x => new { x.ThresholdId, x.ClosedUtc });
            e.Property(x => x.MetricInstance).HasMaxLength(128);
        });

        b.Entity<AlertRule>(e =>
            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<MaintenanceWindow>(e => e.HasIndex(x => new { x.StartUtc, x.EndUtc }));

        b.Entity<AgentRelease>(e => e.HasIndex(x => x.Version).IsUnique());

        b.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => x.TimeUtc).IsDescending();
            e.HasIndex(x => new { x.SiteId, x.TimeUtc }).IsDescending(false, true);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(256);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(512);
        });

        b.Entity<AgentClaim>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.ClaimKeyHash).HasMaxLength(64);
            e.Property(x => x.Hostname).HasMaxLength(255);
            e.HasIndex(x => x.Code);
            e.HasIndex(x => new { x.State, x.ExpiresUtc });
        });

        // snake_case table and column names for Postgres friendliness
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null && table != "check_results" && table != "metric_samples")
                entity.SetTableName(ToSnake(table));
            var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (var prop in entity.GetProperties())
                prop.SetColumnName(ToSnake(prop.GetColumnName(storeObject) ?? prop.Name));
        }
    }

    public static string ToSnake(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && char.IsLower(s[i - 1]);
                var acronymEnd = i > 0 && char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (prevLower || acronymEnd) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
