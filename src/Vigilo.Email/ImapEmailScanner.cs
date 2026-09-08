using System.Diagnostics;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Vigilo.Classification;
using Vigilo.Core;

namespace Vigilo.Email;

public sealed class ImapEmailScanner(
    IAccountSettingsService accountSettings,
    IEmailProcessingService processingService,
    IEmailScanProgressNotifier progressNotifier,
    IEmailContentNormalizer emailContentNormalizer,
    ILogger<ImapEmailScanner> logger) : IEmailScanner
{
    private const int ImapTransportRetryAttempts = 3;

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken, IProgress<EmailScanProgress>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var scanProgress = CreateBroadcastProgress(progress);
        var retriedClassifications = await processingService.RetryDeferredClassificationsAsync(cancellationToken);
        if (retriedClassifications > 0)
        {
            logger.LogInformation(
                "Deferred classifications retried before mailbox scan. Attempted={Attempted}",
                retriedClassifications);
        }

        var accounts = await GetAccountsToScanAsync(cancellationToken);
        logger.LogInformation("Mailbox scan started. AccountCount={AccountCount}", accounts.Count);
        var result = await ScanAccountsAsync(
            accounts,
            (account, accountIndex, accountCount, token) =>
                ScanAccountAsync(account, accountIndex, accountCount, scanProgress, token),
            (account, accountIndex, accountCount, exception) =>
            {
                logger.LogError(
                    exception,
                    "Mailbox scan failed for account {AccountName} ({AccountId}); continuing with remaining accounts.",
                    account.DisplayName,
                    account.Id);
                scanProgress.Report(new EmailScanProgress(
                    account.DisplayName,
                    accountIndex,
                    accountCount,
                    "",
                    0,
                    0,
                    0,
                    0,
                    "Scan failed"));
            },
            cancellationToken);

        logger.LogInformation(
            "Mailbox scan completed. AccountCount={AccountCount} Fetched={Fetched} Classified={Classified} NeedsReview={NeedsReview} Failed={Failed} ElapsedMilliseconds={ElapsedMilliseconds}",
            accounts.Count,
            result.Fetched,
            result.Classified,
            result.NeedsReview,
            result.Failed,
            stopwatch.ElapsedMilliseconds);
        return result;
    }

    internal static async Task<ScanResult> ScanAccountsAsync(
        IReadOnlyList<EmailAccount> accounts,
        Func<EmailAccount, int, int, CancellationToken, Task<ScanResult>> scanAccountAsync,
        Action<EmailAccount, int, int, Exception> reportFailure,
        CancellationToken cancellationToken)
    {
        var result = ScanResult.Empty;
        for (var accountIndex = 0; accountIndex < accounts.Count; accountIndex++)
        {
            var account = accounts[accountIndex];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = result.Add(await scanAccountAsync(
                    account,
                    accountIndex + 1,
                    accounts.Count,
                    cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                reportFailure(account, accountIndex + 1, accounts.Count, ex);
                result = result.Add(ScanResult.Empty with { Failed = 1 });
            }
        }

        return result;
    }

    private async Task<ScanResult> ScanAccountAsync(
        EmailAccount account,
        int accountIndex,
        int accountCount,
        IProgress<EmailScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new EmailScanProgress(
            account.DisplayName,
            accountIndex,
            accountCount,
            "",
            0,
            0,
            0,
            0,
            "Connecting"));

        if (string.IsNullOrWhiteSpace(account.EmailAddress) || string.IsNullOrWhiteSpace(account.Username))
        {
            logger.LogInformation("Skipping scan for account {AccountId} because email account settings are incomplete.", account.Id);
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                "",
                0,
                0,
                0,
                0,
                "Skipped incomplete settings"));
            return ScanResult.Empty;
        }

        var password = await accountSettings.GetPasswordAsync(account.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("Skipping scan for account {EmailAddress} because no protected app password is stored.", account.EmailAddress);
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                "",
                0,
                0,
                0,
                0,
                "Skipped missing app password"));
            return ScanResult.Empty;
        }

        try
        {
            for (var attempt = 1; attempt <= ImapTransportRetryAttempts; attempt++)
            {
                try
                {
                    return await ScanAccountOnceAsync(
                        account,
                        password,
                        accountIndex,
                        accountCount,
                        progress,
                        cancellationToken);
                }
                catch (Exception ex) when (attempt < ImapTransportRetryAttempts
                    && (ex is IOException || ex is ImapProtocolException || ex is SocketException))
                {
                    var retryDelay = TimeSpan.FromSeconds(attempt);
                    logger.LogWarning(
                        ex,
                        "IMAP transport connection was interrupted for account {EmailAddress}; retrying full account scan. Attempt={Attempt} MaxAttempts={MaxAttempts} RetryDelaySeconds={RetryDelaySeconds}",
                        account.EmailAddress,
                        attempt,
                        ImapTransportRetryAttempts,
                        retryDelay.TotalSeconds);
                    progress?.Report(new EmailScanProgress(
                        account.DisplayName,
                        accountIndex,
                        accountCount,
                        "",
                        0,
                        0,
                        0,
                        0,
                        $"Connection interrupted; retrying ({attempt}/{ImapTransportRetryAttempts})"));
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }

            throw new InvalidOperationException("IMAP account scan exited without producing a result.");
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning(
                ex,
                "Skipping scan for account {EmailAddress} because IMAP authentication failed. Gmail requires an app password.",
                account.EmailAddress);
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                "",
                0,
                0,
                0,
                0,
                "Authentication failed"));
            return ScanResult.Empty with { Failed = 1 };
        }
    }

    private async Task<ScanResult> ScanAccountOnceAsync(
        EmailAccount account,
        string password,
        int accountIndex,
        int accountCount,
        IProgress<EmailScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = new ImapClient();
            logger.LogInformation(
                "Connecting to IMAP account. AccountId={AccountId} EmailAddress={EmailAddress} Host={Host} Port={Port} UseSsl={UseSsl}",
                account.Id,
                account.EmailAddress,
                account.ImapHost,
                account.ImapPort,
                account.UseSsl);
            await ImapOperation.ExecuteWithTimeoutAsync(
                token => client.ConnectAsync(account.ImapHost, account.ImapPort, account.UseSsl, token),
                "connection",
                ImapOperation.ConnectionTimeout,
                cancellationToken);
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                "",
                0,
                0,
                0,
                0,
                "Authenticating"));
            await ImapOperation.ExecuteWithTimeoutAsync(
                token => client.AuthenticateAsync(account.Username, password, token),
                "authentication",
                ImapOperation.ConnectionTimeout,
                cancellationToken);
            logger.LogInformation(
                "IMAP account authenticated. AccountId={AccountId} EmailAddress={EmailAddress}",
                account.Id,
                account.EmailAddress);

            var result = ScanResult.Empty;
            var folders = GetAllScanFolders(account);
            for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
            {
                var folderName = folders[folderIndex];
                cancellationToken.ThrowIfCancellationRequested();
                result = await ScanFolderAsync(
                    client,
                    account,
                    accountIndex,
                    accountCount,
                    folderIndex + 1,
                    folders.Count,
                    folderName,
                    result,
                    progress,
                    cancellationToken);
            }

            await client.DisconnectAsync(true, cancellationToken);
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                "",
                0,
                0,
                0,
                0,
                "Complete"));
        return result;
    }

    private async Task<IReadOnlyList<EmailAccount>> GetAccountsToScanAsync(CancellationToken cancellationToken)
    {
        var accounts = await accountSettings.GetAccountsAsync(cancellationToken);
        if (accounts.Count > 0)
        {
            return accounts;
        }

        return [await accountSettings.GetOrCreateDefaultAccountAsync(cancellationToken)];
    }

    public async Task<ScanResult> ScanFolderAsync(
        EmailAccount account,
        string folderName,
        CancellationToken cancellationToken)
    {
        var progress = CreateBroadcastProgress(null);
        await processingService.RetryDeferredClassificationsAsync(cancellationToken);
        var password = await accountSettings.GetPasswordAsync(account.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            progress.Report(new EmailScanProgress(
                account.DisplayName,
                1,
                1,
                "",
                0,
                0,
                0,
                0,
                "Skipped missing app password"));
            return ScanResult.Empty;
        }

        try
        {
            using var client = new ImapClient();
            logger.LogInformation(
                "Connecting to IMAP account for folder scan. AccountId={AccountId} EmailAddress={EmailAddress} Folder={FolderName} Host={Host} Port={Port} UseSsl={UseSsl}",
                account.Id,
                account.EmailAddress,
                folderName,
                account.ImapHost,
                account.ImapPort,
                account.UseSsl);
            await ImapOperation.ExecuteWithTimeoutAsync(
                token => client.ConnectAsync(account.ImapHost, account.ImapPort, account.UseSsl, token),
                "connection",
                ImapOperation.ConnectionTimeout,
                cancellationToken);
            await ImapOperation.ExecuteWithTimeoutAsync(
                token => client.AuthenticateAsync(account.Username, password, token),
                "authentication",
                ImapOperation.ConnectionTimeout,
                cancellationToken);
            var result = await ScanFolderAsync(
                client,
                account,
                1,
                1,
                1,
                1,
                folderName,
                ScanResult.Empty,
                progress,
                cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            progress.Report(new EmailScanProgress(
                account.DisplayName,
                1,
                1,
                "",
                0,
                0,
                0,
                0,
                "Complete"));
            return result;
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning(
                ex,
                "Skipping IMAP folder scan for account {EmailAddress} because authentication failed.",
                account.EmailAddress);
            progress.Report(new EmailScanProgress(
                account.DisplayName,
                1,
                1,
                "",
                0,
                0,
                0,
                0,
                "Authentication failed"));
            return ScanResult.Empty with { Failed = 1 };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            progress.Report(new EmailScanProgress(
                account.DisplayName,
                1,
                1,
                "",
                0,
                0,
                0,
                0,
                "Scan failed"));
            throw;
        }
    }

    private async Task<ScanResult> ScanFolderAsync(
        ImapClient client,
        EmailAccount account,
        int accountIndex,
        int accountCount,
        int folderIndex,
        int folderCount,
        string folderName,
        ScanResult current,
        IProgress<EmailScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        void Report(int processedMessages, int totalMessages, string stage) =>
            progress?.Report(new EmailScanProgress(
                account.DisplayName,
                accountIndex,
                accountCount,
                folderName,
                folderIndex,
                folderCount,
                processedMessages,
                totalMessages,
                stage));

        Report(0, 0, "Opening folder");

        IMailFolder folder;
        try
        {
            folder = await client.GetFolderAsync(folderName, cancellationToken);
        }
        catch (Exception ex)
        {
            if (IsConfiguredSentFolder(account, folderName))
            {
                logger.LogDebug(ex, "Skipping unavailable configured IMAP sent-folder candidate {FolderName}", folderName);
            }
            else
            {
                logger.LogWarning(ex, "Skipping unavailable IMAP folder {FolderName}", folderName);
            }
            Report(0, 0, "Folder unavailable");
            return current;
        }

        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var uidValidity = (long)folder.UidValidity;
        var knownUidValidity = await processingService.GetKnownUidValidityAsync(account.Id, folderName, cancellationToken);
        if (knownUidValidity.HasValue && knownUidValidity.Value != uidValidity)
        {
            logger.LogWarning(
                "IMAP folder UIDVALIDITY changed. AccountId={AccountId} Folder={FolderName} PreviousUidValidity={PreviousUidValidity} NewUidValidity={NewUidValidity}",
                account.Id,
                folderName,
                knownUidValidity.Value,
                uidValidity);
            await processingService.MarkFolderUidValidityChangedAsync(account.Id, folderName, uidValidity, cancellationToken);
        }

        var highestUid = await processingService.GetHighestKnownUidAsync(account.Id, folderName, uidValidity, cancellationToken);
        var recentCutoff = DateTimeOffset.UtcNow.AddMonths(-1);
        Report(0, 0, $"Finding messages since {recentCutoff:MMM d}");
        var recentUids = await folder.SearchAsync(SearchQuery.DeliveredAfter(recentCutoff.UtcDateTime), cancellationToken);
        var newUids = recentUids.Where(uid => uid.Id > highestUid).OrderBy(uid => uid.Id).ToArray();
        logger.LogInformation(
            "IMAP folder scan prepared. AccountId={AccountId} Folder={FolderName} UidValidity={UidValidity} HighestKnownUid={HighestKnownUid} MailboxMessages={MailboxMessages} RecentMessages={RecentMessages} NewRecentMessages={NewRecentMessages} RecentCutoff={RecentCutoff}",
            account.Id,
            folderName,
            uidValidity,
            highestUid,
            folder.Count,
            recentUids.Count,
            newUids.Length,
            recentCutoff);
        Report(
            0,
            newUids.Length,
            newUids.Length == 0
                ? $"No messages to process; ignoring emails before {recentCutoff:MMM d}"
                : $"Ready to process {newUids.Length} messages");
        var summaries = newUids.Length == 0
            ? []
            : await folder.FetchAsync(newUids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken);

        var processedMessages = 0;
        foreach (var summary in summaries.OrderBy(x => x.UniqueId.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid = summary.UniqueId;
            var messageNumber = processedMessages + 1;
            Report(processedMessages, newUids.Length, $"Download email message #{messageNumber}");
            var message = await folder.GetMessageAsync(uid, cancellationToken);
            var flags = summary.Flags.GetValueOrDefault();
            var fetched = CreateFetchedEmail(account, folderName, uidValidity, uid.Id, message, flags);
            var messageTitle = FormatMessageTitle(fetched.Message.Subject);
            Report(processedMessages, newUids.Length, $"Processing email #{messageNumber}: {messageTitle}");
            logger.LogDebug(
                "Processing IMAP message. AccountId={AccountId} Folder={FolderName} UidValidity={UidValidity} ImapUid={ImapUid} MessageId={MessageId} ProviderMessageId={ProviderMessageId}",
                account.Id,
                folderName,
                uidValidity,
                uid.Id,
                fetched.Message.Id,
                fetched.Message.ProviderMessageId);
            var classificationProgress = new InlineProgress<string>(
                stage => Report(processedMessages, newUids.Length, $"{stage} for email #{messageNumber}: {messageTitle}"));
            var outcome = await processingService.ProcessFetchedEmailAsync(fetched, cancellationToken, classificationProgress);
            logger.LogInformation(
                "Processed IMAP message. AccountId={AccountId} Folder={FolderName} UidValidity={UidValidity} ImapUid={ImapUid} MessageId={MessageId} Outcome={Outcome}",
                account.Id,
                folderName,
                uidValidity,
                uid.Id,
                fetched.Message.Id,
                outcome);
            current = current.Add(outcome);
            processedMessages++;
            Report(processedMessages, newUids.Length, $"Processed email #{messageNumber}: {messageTitle}");
        }

        if (newUids.Length > 0)
        {
            Report(processedMessages, newUids.Length, "Folder complete");
        }

        return current;
    }

    private FetchedEmail CreateFetchedEmail(
        EmailAccount account,
        string folder,
        long uidValidity,
        long imapUid,
        MimeMessage mimeMessage,
        MessageFlags flags)
    {
        var mailbox = mimeMessage.From.Mailboxes.FirstOrDefault();
        var subject = mimeMessage.Subject ?? "";
        var receivedAt = mimeMessage.Date == DateTimeOffset.MinValue
            ? DateTimeOffset.UtcNow
            : mimeMessage.Date;
        var normalized = emailContentNormalizer.Normalize(new EmailContentInput(
            mimeMessage.TextBody,
            mimeMessage.HtmlBody,
            mimeMessage.Attachments.Select(CreateAttachmentSummary).ToArray()));
        var from = $"{mailbox?.Name ?? ""} <{mailbox?.Address ?? ""}>";
        var toRecipients = FormatRecipients(mimeMessage.To.Mailboxes.Select(recipient => recipient.Address));
        var ccRecipients = FormatRecipients(mimeMessage.Cc.Mailboxes.Select(recipient => recipient.Address));
        var normalizedBody = emailContentNormalizer.BuildLlmPayload(
            new EmailContentMetadata(
                subject,
                from,
                receivedAt,
                toRecipients,
                ccRecipients),
            normalized);
        var snippetSource = normalized.PlainText;

        var message = new EmailMessage
        {
            AccountId = account.Id,
            Folder = folder,
            ProviderMessageId = $"{folder}:{uidValidity}:{imapUid}",
            MessageIdHeader = mimeMessage.MessageId,
            SenderName = mailbox?.Name ?? "",
            SenderEmail = mailbox?.Address ?? "",
            Subject = subject,
            ReceivedAt = receivedAt,
            Snippet = snippetSource.Length <= 220 ? snippetSource : snippetSource[..220],
            NormalizedBody = normalizedBody,
            OriginalTextBody = mimeMessage.TextBody,
            OriginalHtmlBody = mimeMessage.HtmlBody,
            InReplyTo = mimeMessage.InReplyTo,
            References = string.Join(' ', mimeMessage.References),
            ThreadKey = BuildThreadKey(mimeMessage),
            HasAttachments = mimeMessage.Attachments.Any(),
            IsRead = flags.HasFlag(MessageFlags.Seen),
            LastScannedAt = DateTimeOffset.UtcNow
        };

        return new FetchedEmail(
            account.Id,
            folder,
            uidValidity,
            imapUid,
            message,
            EmailHash.Sha256(subject),
            EmailHash.Sha256(normalizedBody),
            EmailHash.Sha256(flags.ToString()));
    }

    private static string FormatRecipients(IEnumerable<string> addresses)
    {
        const int maximumCharacters = 512;
        var value = string.Join(", ", addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Take(20));
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters].TrimEnd();
    }

    private static AttachmentSummary CreateAttachmentSummary(MimeEntity entity)
    {
        var fileName = entity switch
        {
            MimePart part => part.FileName,
            _ => entity.ContentDisposition?.FileName ?? entity.ContentType.Name
        };

        return new AttachmentSummary(
            fileName,
            entity.ContentType?.MimeType,
            GetAttachmentSize(entity),
            entity.ContentDisposition?.Disposition.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static long? GetAttachmentSize(MimeEntity entity)
    {
        if (entity is not MimePart part || part.Content?.Stream is not { CanSeek: true } stream)
        {
            return null;
        }

        return stream.Length;
    }

    internal static string BuildThreadKey(MimeMessage message)
    {
        var rootReference = message.References.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rootReference))
        {
            return EmailHash.Sha256(rootReference.Trim().ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            return EmailHash.Sha256(message.InReplyTo.Trim().ToUpperInvariant());
        }

        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return EmailHash.Sha256(message.MessageId.Trim().ToUpperInvariant());
        }

        return EmailHash.Sha256(NormalizedSubject(message.Subject ?? ""));
    }

    internal static string NormalizedSubject(string subject)
    {
        var value = subject.Trim();
        while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("AW:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("SV:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf(':') + 1)..].Trim();
        }

        return value.ToUpperInvariant();
    }

    private static IReadOnlyList<string> GetAllScanFolders(EmailAccount account)
    {
        return account.Folders
            .Concat(account.SentFoldersToMonitor.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("Inbox")
            .ToArray();
    }

    private static bool IsConfiguredSentFolder(EmailAccount account, string folderName) =>
        account.SentFoldersToMonitor
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(folderName, StringComparer.OrdinalIgnoreCase);

    private static string FormatMessageTitle(string? subject)
    {
        var title = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject.Trim();
        return title.Length <= 80 ? title : title[..77] + "...";
    }

    private IProgress<EmailScanProgress> CreateBroadcastProgress(IProgress<EmailScanProgress>? progress) =>
        new InlineProgress<EmailScanProgress>(scanProgress =>
        {
            progress?.Report(scanProgress);
            progressNotifier.NotifyProgress(scanProgress);
        });

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
