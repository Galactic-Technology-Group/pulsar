using System.Text.Json;
using Nms.Shared;
using Nms.Shared.Contracts;

namespace Nms.Agent.Tests;

/// <summary>The API serializes enums as strings; every client must read them back with the shared options.</summary>
public class WireFormatTests
{
    [Fact]
    public void AgentConfig_round_trips_with_string_enums()
    {
        var cfg = new AgentConfigDto(Guid.NewGuid(), Guid.NewGuid(), VlanMode.Trunked, 30, 10,
            [new CheckDefinition(Guid.NewGuid(), Guid.NewGuid(), "core-sw", "10.0.0.1", 10, CheckType.TcpPort, 22, null, null, null, 60, 2000, true)], 7);
        var json = JsonSerializer.Serialize(cfg, NmsJson.Options);
        Assert.Contains("\"vlanMode\":\"Trunked\"", json);
        Assert.Contains("\"type\":\"TcpPort\"", json);
        var back = JsonSerializer.Deserialize<AgentConfigDto>(json, NmsJson.Options);
        Assert.NotNull(back);
        Assert.Equal(cfg.VlanMode, back.VlanMode);
        Assert.Equal(cfg.Version, back.Version);
        Assert.Equal(cfg.Checks, back.Checks);   // CheckDefinition is a record: value equality
    }

    [Fact]
    public void Default_web_options_cannot_read_string_enums()
    {
        var json = """{"vlanMode":"Routed"}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<AgentConfigDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
