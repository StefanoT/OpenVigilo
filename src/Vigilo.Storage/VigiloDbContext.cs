using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public class VigiloDbContext(DbContextOptions<VigiloDbContext> options) : DbContext(options)
{
    public DbSet<EmailAccount> Accounts => Set<EmailAccount>();
    public DbSet<ProcessedEmail> ProcessedEmails => Set<ProcessedEmail>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<TrackedItem> TrackedItems => Set<TrackedItem>();
    public DbSet<LiveReport> LiveReports => Set<LiveReport>();
    public DbSet<NotificationRecord> NotificationRecords => Set<NotificationRecord>();
    public DbSet<SenderRule> SenderRules => Set<SenderRule>();
    public DbSet<OutlookStoreBinding> OutlookStoreBindings => Set<OutlookStoreBinding>();
    public DbSet<OutlookItemBinding> OutlookItemBindings => Set<OutlookItemBinding>();
    public DbSet<OutlookCategorySyncOperation> OutlookCategorySyncOperations => Set<OutlookCategorySyncOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailAccount>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).HasMaxLength(160);
            builder.Property(x => x.EmailAddress).HasMaxLength(320);
            builder.Property(x => x.ImapHost).HasMaxLength(256);
            builder.Property(x => x.Username).HasMaxLength(320);
            builder.Property(x => x.FoldersToMonitor).HasMaxLength(1024);
        });

        modelBuilder.Entity<ProcessedEmail>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.AccountId, x.Folder, x.UidValidity, x.ImapUid }).IsUnique();
            builder.HasIndex(x => new { x.AccountId, x.Folder, x.LastSeenAt });
            builder.HasIndex(x => new { x.ProcessingStatus, x.NextClassificationAttemptAt });
            builder.HasIndex(x => x.EmailMessageId);
            builder.HasIndex(x => x.MessageIdHeader);
            builder.HasOne<EmailAccount>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<EmailMessage>()
                .WithMany()
                .HasForeignKey(x => x.EmailMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.Folder).HasMaxLength(256);
            builder.Property(x => x.SubjectHash).HasMaxLength(128);
            builder.Property(x => x.BodyHash).HasMaxLength(128);
            builder.Property(x => x.FlagsHash).HasMaxLength(128);
            builder.Property(x => x.LastClassificationFailureKind).HasMaxLength(64);
            builder.Property(x => x.LastClassificationRecoveryKind).HasMaxLength(64);
        });

        modelBuilder.Entity<EmailMessage>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.AccountId, x.ProviderMessageId }).IsUnique();
            builder.HasIndex(x => new { x.AccountId, x.Folder, x.ReceivedAt });
            builder.HasIndex(x => x.MessageIdHeader);
            builder.HasIndex(x => x.ThreadKey);
            builder.HasOne<EmailAccount>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.Folder).HasMaxLength(256);
            builder.Property(x => x.ProviderMessageId).HasMaxLength(512);
            builder.Property(x => x.ThreadKey).HasMaxLength(512);
            builder.Property(x => x.InReplyTo).HasMaxLength(512);
            builder.Property(x => x.SenderEmail).HasMaxLength(320);
            builder.Property(x => x.Subject).HasMaxLength(1024);
        });

        modelBuilder.Entity<TrackedItem>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => x.EmailMessageId).IsUnique();
            builder.HasIndex(x => new { x.Status, x.Deadline });
            builder.HasIndex(x => new { x.ThreadKey, x.Status });
            builder.HasIndex(x => new { x.MessageType, x.Status });
            builder.HasOne<EmailMessage>()
                .WithOne()
                .HasForeignKey<TrackedItem>(x => x.EmailMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.ActionSummary).HasMaxLength(2048);
            builder.Property(x => x.ThreadKey).HasMaxLength(512);
            builder.Property(x => x.SentReplyMessageId).HasMaxLength(512);
            builder.Property(x => x.Reason).HasMaxLength(4096);
            builder.Property(x => x.Tags).HasMaxLength(1024);
        });

        modelBuilder.Entity<LiveReport>(builder =>
        {
            builder.HasKey(x => x.Id);
        });

        modelBuilder.Entity<NotificationRecord>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.TrackedItemId, x.Kind, x.DedupeKey }).IsUnique();
            builder.HasOne<TrackedItem>()
                .WithMany()
                .HasForeignKey(x => x.TrackedItemId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.DedupeKey).HasMaxLength(512);
        });

        modelBuilder.Entity<SenderRule>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.AccountId, x.NormalizedSenderEmail }).IsUnique();
            builder.HasOne<EmailAccount>()
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.SenderEmail).HasMaxLength(320);
            builder.Property(x => x.NormalizedSenderEmail).HasMaxLength(320);
        });

        modelBuilder.Entity<OutlookStoreBinding>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => x.VigiloMailboxId).IsUnique();
            builder.HasOne<EmailAccount>()
                .WithOne()
                .HasForeignKey<OutlookStoreBinding>(x => x.VigiloMailboxId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.OutlookStoreId).HasMaxLength(1024);
            builder.Property(x => x.OutlookStoreDisplayName).HasMaxLength(256);
            builder.Property(x => x.AccountAddress).HasMaxLength(320);
        });

        modelBuilder.Entity<OutlookItemBinding>(builder =>
        {
            builder.HasKey(x => x.VigiloMessageId);
            builder.HasOne<EmailMessage>()
                .WithOne()
                .HasForeignKey<OutlookItemBinding>(x => x.VigiloMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.InternetMessageId).HasMaxLength(1024);
            builder.Property(x => x.OutlookEntryId).HasMaxLength(2048);
            builder.Property(x => x.OutlookStoreId).HasMaxLength(1024);
            builder.Property(x => x.LastKnownFolderEntryId).HasMaxLength(2048);
        });

        modelBuilder.Entity<OutlookCategorySyncOperation>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.VigiloMessageId, x.Status });
            builder.HasIndex(x => x.NextAttemptAtUtc);
            builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            builder.HasOne<EmailMessage>()
                .WithMany()
                .HasForeignKey(x => x.VigiloMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Property(x => x.DesiredCategoriesJson).HasMaxLength(1024);
            builder.Property(x => x.LastErrorCode).HasMaxLength(64);
            builder.Property(x => x.LastErrorMessageSanitized).HasMaxLength(512);
        });
    }
}
