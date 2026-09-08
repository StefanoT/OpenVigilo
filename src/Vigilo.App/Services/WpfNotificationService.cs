using Microsoft.EntityFrameworkCore;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.App.Services;

public sealed class WpfNotificationService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    IAccountSettingsService accountSettings,
    TrayIconService trayIconService) : INotificationService
{
    public async Task NotifyAsync(
        NotificationKind kind,
        TrackedItem item,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var account = await accountSettings.GetAccountAsync(message.AccountId, cancellationToken);
        if (account is null || !account.NotificationsEnabled)
        {
            return;
        }

        var dedupeKey = BuildDedupeKey(kind, item);
        var exists = await dbContext.NotificationRecords.AnyAsync(
            x => x.TrackedItemId == item.Id && x.Kind == kind && x.DedupeKey == dedupeKey,
            cancellationToken);
        if (exists)
        {
            return;
        }

        dbContext.NotificationRecords.Add(new NotificationRecord
        {
            TrackedItemId = item.Id,
            Kind = kind,
            DedupeKey = dedupeKey,
            SentAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        trayIconService.ShowBalloon(Title(kind), $"{item.ActionSummary}\n{message.Subject}");
    }

    private static string BuildDedupeKey(NotificationKind kind, TrackedItem item) => kind switch
    {
        NotificationKind.DeadlineDueToday => item.Deadline?.Date.ToString("O") ?? "no-deadline",
        NotificationKind.PossibleEscalation => item.IsEscalation.ToString(),
        _ => item.UpdatedAt.ToString("O")
    };

    private static string Title(NotificationKind kind) => kind switch
    {
        NotificationKind.NewActionableItem => "New actionable item",
        NotificationKind.DeadlineDueToday => "Deadline due today",
        NotificationKind.PossibleEscalation => "Possible escalation",
        _ => "Vigilo"
    };
}
