using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigilo.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ImapHost = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ImapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    UseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    FoldersToMonitor = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SentFoldersToMonitor = table.Column<string>(type: "TEXT", nullable: false),
                    PollingFallbackMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    UseImapIdle = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DigestTime = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    DataRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreMessageBodies = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DueTodayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpcomingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WaitingReplyCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsReviewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CommercialOfferCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedMessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastMessageProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MessageIdHeader = table.Column<string>(type: "TEXT", nullable: true),
                    SenderName = table.Column<string>(type: "TEXT", nullable: false),
                    SenderEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Snippet = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedBody = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalTextBody = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalHtmlBody = table.Column<string>(type: "TEXT", nullable: true),
                    InReplyTo = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    References = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    HasAttachments = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailMessages_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutlookStoreBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VigiloMailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutlookStoreId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OutlookStoreDisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookStoreBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutlookStoreBindings_Accounts_VigiloMailboxId",
                        column: x => x.VigiloMailboxId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SenderRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    NormalizedSenderEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SenderRules_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCategorySyncOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VigiloMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DesiredCategoriesJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastErrorMessageSanitized = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCategorySyncOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutlookCategorySyncOperations_EmailMessages_VigiloMessageId",
                        column: x => x.VigiloMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutlookItemBindings",
                columns: table => new
                {
                    VigiloMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InternetMessageId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    OutlookEntryId = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    OutlookStoreId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LastKnownFolderEntryId = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    LastMatchedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    MatchMethod = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookItemBindings", x => x.VigiloMessageId);
                    table.ForeignKey(
                        name: "FK_OutlookItemBindings_EmailMessages_VigiloMessageId",
                        column: x => x.VigiloMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UidValidity = table.Column<long>(type: "INTEGER", nullable: false),
                    ImapUid = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageIdHeader = table.Column<string>(type: "TEXT", nullable: true),
                    SubjectHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BodyHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FlagsHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ProcessingStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassificationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ClassifierModelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ClassificationHash = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    EmailMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassificationAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextClassificationAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastClassificationFailureKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastClassificationRecoveryKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessedEmails_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProcessedEmails_EmailMessages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmailMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsEscalation = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresReply = table.Column<bool>(type: "INTEGER", nullable: false),
                    MessageType = table.Column<int>(type: "INTEGER", nullable: false),
                    ThreadKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SentReplyDetectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SentReplyMessageId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ActionSummary = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SnoozedUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SectionOverride = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedItems_EmailMessages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackedItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRecords_TrackedItems_TrackedItemId",
                        column: x => x.TrackedItemId,
                        principalTable: "TrackedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_AccountId_Folder_ReceivedAt",
                table: "EmailMessages",
                columns: new[] { "AccountId", "Folder", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_AccountId_ProviderMessageId",
                table: "EmailMessages",
                columns: new[] { "AccountId", "ProviderMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_MessageIdHeader",
                table: "EmailMessages",
                column: "MessageIdHeader");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_ThreadKey",
                table: "EmailMessages",
                column: "ThreadKey");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecords_TrackedItemId_Kind_DedupeKey",
                table: "NotificationRecords",
                columns: new[] { "TrackedItemId", "Kind", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCategorySyncOperations_NextAttemptAtUtc",
                table: "OutlookCategorySyncOperations",
                column: "NextAttemptAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCategorySyncOperations_Status_NextAttemptAtUtc",
                table: "OutlookCategorySyncOperations",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCategorySyncOperations_VigiloMessageId_Status",
                table: "OutlookCategorySyncOperations",
                columns: new[] { "VigiloMessageId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookStoreBindings_VigiloMailboxId",
                table: "OutlookStoreBindings",
                column: "VigiloMailboxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEmails_AccountId_Folder_LastSeenAt",
                table: "ProcessedEmails",
                columns: new[] { "AccountId", "Folder", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEmails_AccountId_Folder_UidValidity_ImapUid",
                table: "ProcessedEmails",
                columns: new[] { "AccountId", "Folder", "UidValidity", "ImapUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEmails_EmailMessageId",
                table: "ProcessedEmails",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEmails_MessageIdHeader",
                table: "ProcessedEmails",
                column: "MessageIdHeader");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEmails_ProcessingStatus_NextClassificationAttemptAt",
                table: "ProcessedEmails",
                columns: new[] { "ProcessingStatus", "NextClassificationAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SenderRules_AccountId_NormalizedSenderEmail",
                table: "SenderRules",
                columns: new[] { "AccountId", "NormalizedSenderEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedItems_EmailMessageId",
                table: "TrackedItems",
                column: "EmailMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedItems_MessageType_Status",
                table: "TrackedItems",
                columns: new[] { "MessageType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedItems_Status_Deadline",
                table: "TrackedItems",
                columns: new[] { "Status", "Deadline" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedItems_ThreadKey_Status",
                table: "TrackedItems",
                columns: new[] { "ThreadKey", "Status" });

            // SettingsConfigurationCommits is deliberately outside the EF model (raw-SQL
            // commit marker written by SettingsCoordinator), so it is not part of the
            // generated snapshot and must be created explicitly.
            migrationBuilder.Sql(
                """
                CREATE TABLE "SettingsConfigurationCommits" (
                    "TransactionId" TEXT NOT NULL CONSTRAINT "PK_SettingsConfigurationCommits" PRIMARY KEY,
                    "CreatedAtUtc" TEXT NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveReports");

            migrationBuilder.DropTable(
                name: "NotificationRecords");

            migrationBuilder.DropTable(
                name: "OutlookCategorySyncOperations");

            migrationBuilder.DropTable(
                name: "OutlookItemBindings");

            migrationBuilder.DropTable(
                name: "OutlookStoreBindings");

            migrationBuilder.DropTable(
                name: "ProcessedEmails");

            migrationBuilder.DropTable(
                name: "SenderRules");

            migrationBuilder.DropTable(
                name: "TrackedItems");

            migrationBuilder.DropTable(
                name: "EmailMessages");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.Sql("DROP TABLE IF EXISTS \"SettingsConfigurationCommits\";");
        }
    }
}
