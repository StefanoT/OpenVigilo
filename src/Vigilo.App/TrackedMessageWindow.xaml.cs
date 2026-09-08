using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Navigation;
using AngleSharp.Html.Parser;
using Vigilo.Core;

namespace Vigilo.App;

public partial class TrackedMessageWindow : Window
{
    private const string MissingOriginalBodyMessage =
        "Original message body is not stored for this email. Rescan the message with local body storage enabled to view the original email.";

    private readonly TrackedMessageDetail _detail;
    private readonly Func<TrackedItemStatus, Task> _updateStatusAsync;
    private readonly Func<DateTimeOffset?, Task> _updateDeadlineAsync;
    private readonly Func<SenderRuleKind, Task<bool>> _applySenderRuleAsync;
    private static readonly HtmlParser HtmlParser = new();
    private static readonly Regex ConditionalCommentMarkerRegex = new(
        @"<!--\s*(?:<!\s*)?\[endif\]\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private TrackedItemStatus? _pendingStatus;
    private SenderRuleKind? _pendingSenderRule;
    private bool _isInitializing = true;

    public TrackedMessageWindow(
        TrackedMessageDetail detail,
        Func<TrackedItemStatus, Task> updateStatusAsync,
        Func<DateTimeOffset?, Task> updateDeadlineAsync,
        Func<SenderRuleKind, Task<bool>> applySenderRuleAsync)
    {
        InitializeComponent();
        _detail = detail;
        _updateStatusAsync = updateStatusAsync;
        _updateDeadlineAsync = updateDeadlineAsync;
        _applySenderRuleAsync = applySenderRuleAsync;
        DataContext = detail;
        DeadlinePicker.SelectedDate = detail.Deadline?.LocalDateTime.Date;
        ReplyButton.IsEnabled = !string.IsNullOrWhiteSpace(detail.SenderEmail);
        VipSenderButton.IsEnabled = ReplyButton.IsEnabled;
        IgnoreSenderButton.IsEnabled = ReplyButton.IsEnabled;
        CurrentStatusText.Text = $"Currently {FormatStatus(detail.Status)}. Choose a new status or leave it unchanged.";
        SenderRuleStatusText.Text = detail.SenderRule switch
        {
            SenderRuleKind.Vip => "Currently always flagged",
            SenderRuleKind.Ignored => "Currently ignored",
            _ => "No sender rule is active"
        };
        Title = string.IsNullOrWhiteSpace(detail.Subject)
            ? "Email"
            : $"Email - {detail.Subject}";
        _isInitializing = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_detail.OriginalHtmlBody))
        {
            PlainTextBodyBox.Visibility = Visibility.Collapsed;
            HtmlBodyBrowser.Visibility = Visibility.Visible;
            HtmlBodyBrowser.NavigateToString(PrepareHtmlForDisplay(_detail.OriginalHtmlBody));
            return;
        }

        HtmlBodyBrowser.Visibility = Visibility.Collapsed;
        PlainTextBodyBox.Visibility = Visibility.Visible;
        PlainTextBodyBox.Text = string.IsNullOrWhiteSpace(_detail.OriginalTextBody)
            ? MissingOriginalBodyMessage
            : _detail.OriginalTextBody;
    }

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_detail.SenderEmail))
        {
            return;
        }

        var subject = _detail.Subject.Trim();
        if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
        {
            subject = $"Re: {subject}";
        }

        var mailtoUri = new Uri(
            $"mailto:{Uri.EscapeDataString(_detail.SenderEmail.Trim())}?subject={Uri.EscapeDataString(subject)}");

        try
        {
            Process.Start(new ProcessStartInfo(mailtoUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                this,
                "Windows could not open the reply in the default email application.",
                "Unable to reply",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void DeadlinePicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateDirtyState();

    private void ClearDeadline_Click(object sender, RoutedEventArgs e)
    {
        DeadlinePicker.SelectedDate = null;
        UpdateDirtyState();
    }

    private void StatusNoChange_Checked(object sender, RoutedEventArgs e)
    {
        _pendingStatus = null;
        UpdateDirtyState();
    }

    private void Done_Checked(object sender, RoutedEventArgs e) =>
        SetPendingStatus(TrackedItemStatus.Done);

    private void Snooze_Checked(object sender, RoutedEventArgs e) =>
        SetPendingStatus(TrackedItemStatus.Snoozed);

    private void Dismiss_Checked(object sender, RoutedEventArgs e) =>
        SetPendingStatus(TrackedItemStatus.Dismissed);

    private void SetPendingStatus(TrackedItemStatus status)
    {
        _pendingStatus = status;
        UpdateDirtyState();
    }

    private void SenderNoChange_Checked(object sender, RoutedEventArgs e)
    {
        _pendingSenderRule = null;
        MessageStatusPanel.IsEnabled = true;
        UpdateDirtyState();
    }

    private void VipSender_Checked(object sender, RoutedEventArgs e)
    {
        _pendingSenderRule = SenderRuleKind.Vip;
        MessageStatusPanel.IsEnabled = true;
        UpdateDirtyState();
    }

    private void IgnoreSender_Checked(object sender, RoutedEventArgs e)
    {
        _pendingSenderRule = SenderRuleKind.Ignored;
        _pendingStatus = null;
        StatusNoChangeRadio.IsChecked = true;
        MessageStatusPanel.IsEnabled = false;
        UpdateDirtyState();
    }

    private async void SaveChanges_Click(object sender, RoutedEventArgs e)
    {
        InteractionPanel.IsEnabled = false;
        SaveStatusText.Text = "Saving changes…";
        try
        {
            if (_pendingSenderRule is { } senderRule && senderRule != _detail.SenderRule)
            {
                if (!await _applySenderRuleAsync(senderRule))
                {
                    SaveStatusText.Text = "Nothing was saved.";
                    return;
                }
            }

            var selectedDeadline = GetSelectedDeadline();
            if (!DeadlinesMatch(selectedDeadline, _detail.Deadline))
            {
                await _updateDeadlineAsync(selectedDeadline);
            }

            if (_pendingStatus is { } status && status != _detail.Status)
            {
                await _updateStatusAsync(status);
            }

            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Your changes could not be saved.\n\n{ex.Message}",
                "Unable to save changes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            InteractionPanel.IsEnabled = true;
            UpdateDirtyState();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private DateTimeOffset? GetSelectedDeadline()
    {
        if (DeadlinePicker.SelectedDate is not { } selectedDate)
        {
            return null;
        }

        var existingTime = _detail.Deadline?.LocalDateTime.TimeOfDay ?? TimeSpan.Zero;
        var localDeadline = DateTime.SpecifyKind(selectedDate.Date + existingTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDeadline, TimeZoneInfo.Local.GetUtcOffset(localDeadline));
    }

    private static bool DeadlinesMatch(DateTimeOffset? left, DateTimeOffset? right) =>
        left?.LocalDateTime.Date == right?.LocalDateTime.Date;

    private void UpdateDirtyState()
    {
        if (_isInitializing)
        {
            return;
        }

        var isDirty = !DeadlinesMatch(GetSelectedDeadline(), _detail.Deadline) ||
                      (_pendingStatus is { } status && status != _detail.Status) ||
                      (_pendingSenderRule is { } rule && rule != _detail.SenderRule);
        SaveButton.IsEnabled = isDirty;
        SaveStatusText.Text = isDirty
            ? "Unsaved changes"
            : "Changes are applied only when you choose Save changes.";
    }

    private static string FormatStatus(TrackedItemStatus status) => status switch
    {
        TrackedItemStatus.NeedsReview => "needs review",
        _ => status.ToString().ToLowerInvariant()
    };

    private void HtmlBodyBrowser_Navigating(object sender, NavigatingCancelEventArgs e)
    {
        if (e.Uri is null || e.Uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (!CanOpenExternally(e.Uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Windows could not open this link with its registered application.\n\n{e.Uri}",
                "Unable to open link",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool CanOpenExternally(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));

    private static string PrepareHtmlForDisplay(string html)
    {
        var document = HtmlParser.ParseDocument(ConditionalCommentMarkerRegex.Replace(html, string.Empty));
        foreach (var element in document.QuerySelectorAll("a[target], base[target]"))
        {
            element.RemoveAttribute("target");
        }

        return document.DocumentElement?.OuterHtml ?? html;
    }
}
