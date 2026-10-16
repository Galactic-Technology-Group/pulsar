using Nms.Data.Entities;
using Nms.Shared;

namespace Nms.Worker.Notifications;

public sealed record NotificationMessage(string Title, string Summary, IReadOnlyList<NotificationLine> Lines, bool IsRecovery, string? PortalUrl);
public sealed record NotificationLine(string Site, string Text, DateTimeOffset At, IncidentKind Kind);

public interface INotificationSender
{
    NotificationChannelType Type { get; }
    Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct);
}
