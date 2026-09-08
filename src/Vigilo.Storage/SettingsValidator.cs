using System.Net.Mail;
using Vigilo.Core;

namespace Vigilo.Storage;

internal static class SettingsValidator
{
    public static void ValidateAccount(EmailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Id == Guid.Empty)
        {
            throw new InvalidOperationException("The email account identifier is missing.");
        }

        if (string.IsNullOrWhiteSpace(account.EmailAddress)
            || !MailAddress.TryCreate(account.EmailAddress.Trim(), out var parsedAddress)
            || !string.Equals(parsedAddress.Address, account.EmailAddress.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Enter a valid mailbox email address before saving settings.");
        }

        RequireText(account.Username, 320, "IMAP username");
        RequireText(account.ImapHost, 256, "IMAP host");
        RequireText(account.FoldersToMonitor, 1024, "monitored folder");
        RequireMaximum(account.Name, 160, "account name");
        RequireMaximum(account.EmailAddress, 320, "email address");
        RequireMaximum(account.SentFoldersToMonitor, 1024, "sent-folder list");

        if (account.ImapPort is < 1 or > 65_535)
        {
            throw new InvalidOperationException("The IMAP port must be between 1 and 65535.");
        }

        if (account.PollingFallbackMinutes is < 1 or > 1_440)
        {
            throw new InvalidOperationException("The polling interval must be between 1 minute and 24 hours.");
        }

        if (account.DataRetentionDays is < 1 or > 3_650)
        {
            throw new InvalidOperationException("Data retention must be between 1 day and 10 years.");
        }
    }

    public static void ValidateOutlookBinding(OutlookStoreBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Id == Guid.Empty || binding.VigiloMailboxId == Guid.Empty)
        {
            throw new InvalidOperationException("The Outlook mailbox binding identity is incomplete.");
        }

        RequireMaximum(binding.OutlookStoreId, 1024, "Outlook store identifier");
        RequireMaximum(binding.OutlookStoreDisplayName, 256, "Outlook store name");
        RequireMaximum(binding.AccountAddress ?? "", 320, "Outlook account address");
        if (binding.IsEnabled
            && (string.IsNullOrWhiteSpace(binding.OutlookStoreId)
                || string.IsNullOrWhiteSpace(binding.OutlookStoreDisplayName)))
        {
            throw new InvalidOperationException(
                "An Outlook store identifier and display name are required when category tagging is enabled.");
        }
    }

    private static void RequireText(string value, int maximumLength, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"The {displayName} is required.");
        }

        RequireMaximum(value, maximumLength, displayName);
    }

    private static void RequireMaximum(string value, int maximumLength, string displayName)
    {
        if (value.Length > maximumLength)
        {
            throw new InvalidOperationException($"The {displayName} cannot exceed {maximumLength} characters.");
        }
    }
}
