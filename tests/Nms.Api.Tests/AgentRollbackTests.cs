using Microsoft.Extensions.Logging.Abstractions;
using Nms.Api.Hubs;
using Nms.Data.Entities;
using Nms.Shared.Contracts;

namespace Nms.Api.Tests;

public class AgentRollbackTests
{
    [Fact]
    public void Heartbeat_rollback_is_stored_and_cleared_when_the_agent_stops_reporting_it()
    {
        var agent = new Agent { Id = Guid.NewGuid(), Name = "vm" };
        var at = new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);

        AgentHub.ApplyRollback(agent, new UpdateRollbackDto("0.1.8", "0.1.7", "agent 0.1.8 stopped 3 times before confirming itself", at), NullLogger.Instance);
        Assert.Equal("0.1.8", agent.RollbackFromVersion);
        Assert.Equal(at, agent.RollbackUtc);
        Assert.Contains("3 times", agent.RollbackReason);

        AgentHub.ApplyRollback(agent, null, NullLogger.Instance);
        Assert.Null(agent.RollbackFromVersion);
        Assert.Null(agent.RollbackReason);
        Assert.Null(agent.RollbackUtc);
    }
}
