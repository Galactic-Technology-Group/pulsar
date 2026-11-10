using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nms.Api.Endpoints;
using Nms.Api.Services;
using Nms.Data;
using Nms.Data.Entities;
using Nms.Shared.Contracts;

namespace Nms.Api.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aa-bb-cc-dd-ee-ff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("aabb.ccdd.eeff", "aa:bb:cc:dd:ee:ff")]
    [InlineData(" AABBCCDDEEFF ", "aa:bb:cc:dd:ee:ff")]
    [InlineData("00:00:00:00:00:00", null)]   // incomplete ARP entry
    [InlineData("ff:ff:ff:ff:ff:ff", null)]   // broadcast
    [InlineData("aa:bb:cc:dd:ee", null)]
    [InlineData("zz:bb:cc:dd:ee:ff", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalizes_to_lowercase_colon_form(string? input, string? expected) => Assert.Equal(expected, MacAddresses.Normalize(input));

    [Fact]
    public void Empty_is_valid_garbage_is_not()
    {
        Assert.True(MacAddresses.IsValidOrEmpty(null));
        Assert.True(MacAddresses.IsValidOrEmpty("  "));
        Assert.False(MacAddresses.IsValidOrEmpty("not-a-mac"));
    }
}

public class SiteLocationTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(27.95, -82.46, null)]                    // Tampa
    [InlineData(27.95, null, "both")]
    [InlineData(null, -82.46, "both")]
    [InlineData(91.0, 0.0, "Latitude")]
    [InlineData(0.0, -181.0, "Longitude")]
    public void Coordinates_come_together_and_in_range(double? lat, double? lon, string? errorContains)
    {
        var error = ManagementEndpoints.SiteLocationError(new SiteInput("HQ", Latitude: lat, Longitude: lon));
        if (errorContains is null) Assert.Null(error);
        else Assert.Contains(errorContains, error);
    }
}

public class DeviceMacTests
{
    [Fact]
    public async Task Same_mac_twice_at_one_site_conflicts_but_not_across_sites_or_for_itself()
    {
        await using var db = new NmsDbContext(new DbContextOptionsBuilder<NmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var siteA = Guid.NewGuid(); var siteB = Guid.NewGuid();
        var sw = new Device { Id = Guid.NewGuid(), SiteId = siteA, Name = "core-sw", Address = "10.0.0.2", MacAddress = "aa:bb:cc:dd:ee:ff" };
        db.Devices.Add(sw); await db.SaveChangesAsync();

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(await ManagementEndpoints.MacError(db, siteA, "AA-BB-CC-DD-EE-FF", null));
        Assert.Equal(409, ((IStatusCodeHttpResult)(await ManagementEndpoints.MacError(db, siteA, "aabb.ccdd.eeff", null))!).StatusCode);
        Assert.Null(await ManagementEndpoints.MacError(db, siteB, "aa:bb:cc:dd:ee:ff", null));
        Assert.Null(await ManagementEndpoints.MacError(db, siteA, "aa:bb:cc:dd:ee:ff", sw.Id));
        Assert.Equal(400, ((IStatusCodeHttpResult)(await ManagementEndpoints.MacError(db, siteA, "nope", null))!).StatusCode);
        Assert.Null(await ManagementEndpoints.MacError(db, siteA, null, null));
    }
}

public class AuditFilterTests
{
    private static (ServiceProvider sp, AuditFilter filter) Setup()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<NmsDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return (sp, new AuditFilter(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditFilter>.Instance));
    }

    private static DefaultEndpointFilterInvocationContext Call(string method, string pattern, object? routeValues, string user = "chris@example.com")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user), new Claim("oid", "user-oid")], "test"));
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, null, pattern));
        foreach (var (k, v) in new RouteValueDictionary(routeValues)) http.Request.RouteValues[k] = v;
        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static async Task<List<AuditEvent>> Events(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NmsDbContext>().AuditEvents.ToListAsync();
    }

    [Fact]
    public async Task A_delete_records_who_what_where_and_the_name_of_the_deleted_object()
    {
        var (sp, filter) = Setup();
        var siteId = Guid.NewGuid(); var deviceId = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
            db.Devices.Add(new Device { Id = deviceId, SiteId = siteId, Name = "core-sw", Address = "10.0.0.2" });
            await db.SaveChangesAsync();
        }

        await filter.InvokeAsync(Call("DELETE", "/api/devices/{id:guid}", new { id = deviceId.ToString() }), async _ =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
            db.Devices.Remove(await db.Devices.SingleAsync()); await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var e = Assert.Single(await Events(sp));
        Assert.Equal("chris@example.com", e.Actor);
        Assert.Equal("user-oid", e.ActorId);
        Assert.Equal("DELETE /api/devices/{id:guid}", e.Action);
        Assert.Equal("devices", e.TargetType);
        Assert.Equal(deviceId, e.TargetId);
        Assert.Equal(siteId, e.SiteId);
        Assert.Equal(204, e.Status);
        Assert.Equal("device core-sw (10.0.0.2)", e.Detail);
    }

    [Fact]
    public async Task A_create_records_the_new_object_and_its_site()
    {
        var (sp, filter) = Setup();
        var siteId = Guid.NewGuid(); var newId = Guid.NewGuid();

        var result = await filter.InvokeAsync(Call("POST", "/api/sites/{siteId:guid}/devices", new { siteId = siteId.ToString() }),
            _ => ValueTask.FromResult<object?>(Results.Created($"/api/devices/{newId}", new { id = newId })));

        Assert.IsAssignableFrom<IResult>(result);
        var e = Assert.Single(await Events(sp));
        Assert.Equal("devices", e.TargetType);
        Assert.Equal(newId, e.TargetId);
        Assert.Equal(siteId, e.SiteId);
        Assert.Equal(201, e.Status);
    }

    [Fact]
    public async Task A_failing_endpoint_is_recorded_as_500_and_still_throws()
    {
        var (sp, filter) = Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filter.InvokeAsync(Call("POST", "/api/channels", null), _ => throw new InvalidOperationException("boom")));
        var e = Assert.Single(await Events(sp));
        Assert.Equal(500, e.Status);
        Assert.Equal("channels", e.TargetType);
        Assert.Null(e.TargetId);
    }

    [Theory]
    [InlineData("/api/devices/{deviceId:guid}/checks", "devices")]
    [InlineData("/api/claims/{id:guid}/approve", "claims")]
    [InlineData("/api/releases/import", "releases")]
    public void Target_type_is_the_first_segment_after_api(string pattern, string expected) => Assert.Equal(expected, AuditFilter.TargetType(pattern));

    [Fact]
    public void Plain_objects_count_as_200_and_null_as_204()
    {
        Assert.Equal(200, AuditFilter.StatusOf(new { ok = true }));
        Assert.Equal(204, AuditFilter.StatusOf(null));
        Assert.Equal(404, AuditFilter.StatusOf(Results.NotFound()));
    }
}
