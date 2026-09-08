using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Vigilo.App.Services;
using Vigilo.Core;

namespace Vigilo.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILiveReportService _liveReportService;
    private readonly IEmailScanQueue _scanQueue;
    private readonly IAccountSettingsService _settingsService;
    private readonly ISettingsCoordinator _settingsCoordinator;
    private readonly ISenderRuleService _senderRuleService;
    private readonly IModelManager _modelManager;
    private readonly IModelSelectionService _modelSelectionService;
    private readonly ILocalDataMaintenanceService _maintenanceService;
    private readonly IUserApprovalService _approvalService;
    private readonly IEmailScanProgressNotifier _scanProgressNotifier;
    private readonly IOutlookClient _outlookClient;
    private readonly IOutlookSyncStore _outlookSyncStore;
    private readonly AppLogStore _appLogStore;
    private readonly ILogger<MainWindowViewModel> _logger;
    private LiveReport _report = new() { GeneratedAt = DateTimeOffset.UtcNow };
    private EmailAccount _account = new();
    private EmailAccount _outlookMailbox = new();
    private EmailProviderPreset _selectedImapPreset;
    private bool _isSyncingPresetSelection;
    private bool _isScanInProgress;
    private string _statusText = "Ready";
    private string _modelStatus = "Unknown";
    private string _modelPath = "";
    private AiModelProfile _selectedModelProfile;
    private string _passwordStatus = "No app password saved";
    private string _scanProgressText = "";
    private string _logText = "";
    private DateTimeOffset? _scanProgressStartedAt;
    private string _scanProgressScope = "";
    private bool _outlookEnabled;
    private OutlookStoreInfo? _selectedOutlookStore;
    private SenderRuleView? _selectedSenderRule;
    private string _outlookStatus = "Disabled";
    private string _outlookVersion = "Not detected";
    private int _outlookPendingCount;
    private string _outlookLastSuccess = "Never";
    private string _outlookLastError = "None";
    private readonly object _reportRefreshLock = new();
    private LiveReportChange _pendingReportChanges;
    private bool _reportRefreshScheduled;

    public MainWindowViewModel(
        ILiveReportService liveReportService,
        IEmailScanQueue scanQueue,
        IAccountSettingsService settingsService,
        ISettingsCoordinator settingsCoordinator,
        ISenderRuleService senderRuleService,
        IModelManager modelManager,
        IModelSelectionService modelSelectionService,
        ILocalDataMaintenanceService maintenanceService,
        IUserApprovalService approvalService,
        IEmailScanProgressNotifier scanProgressNotifier,
        IOutlookClient outlookClient,
        IOutlookSyncStore outlookSyncStore,
        AppLogStore appLogStore,
        ILogger<MainWindowViewModel> logger)
    {
        _liveReportService = liveReportService;
        _scanQueue = scanQueue;
        _settingsService = settingsService;
        _settingsCoordinator = settingsCoordinator;
        _senderRuleService = senderRuleService;
        _modelManager = modelManager;
        _modelSelectionService = modelSelectionService;
        _maintenanceService = maintenanceService;
        _approvalService = approvalService;
        _scanProgressNotifier = scanProgressNotifier;
        _outlookClient = outlookClient;
        _outlookSyncStore = outlookSyncStore;
        _appLogStore = appLogStore;
        _logger = logger;
        _logText = appLogStore.Text;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TrackedItemView.Section)));
        RunScanCommand = new AsyncRelayCommand(_ => RunScanAsync(CancellationToken.None), logger: _logger, commandName: "RunScan");
        DoneCommand = new AsyncRelayCommand(p => UpdateStatusAsync(p, TrackedItemStatus.Done), logger: _logger, commandName: "MarkDone");
        DismissCommand = new AsyncRelayCommand(p => UpdateStatusAsync(p, TrackedItemStatus.Dismissed), logger: _logger, commandName: "Dismiss");
        SnoozeCommand = new AsyncRelayCommand(p => UpdateStatusAsync(p, TrackedItemStatus.Snoozed), logger: _logger, commandName: "Snooze");
        ReclassifyToDueTodayCommand = new AsyncRelayCommand(p => ReclassifyAsync(p, TrackedItemCategories.DueToday), logger: _logger, commandName: "ReclassifyToDueToday");
        ReclassifyToUpcomingCommand = new AsyncRelayCommand(p => ReclassifyAsync(p, TrackedItemCategories.Upcoming), logger: _logger, commandName: "ReclassifyToUpcoming");
        ReclassifyToWaitingReplyCommand = new AsyncRelayCommand(p => ReclassifyAsync(p, TrackedItemCategories.WaitingForMyReply), logger: _logger, commandName: "ReclassifyToWaitingReply");
        ReclassifyToCommercialOfferCommand = new AsyncRelayCommand(p => ReclassifyAsync(p, TrackedItemCategories.CommercialOffers), logger: _logger, commandName: "ReclassifyToCommercialOffer");
        ViewTrackedMessageCommand = new AsyncRelayCommand(ViewTrackedMessageAsync, logger: _logger, commandName: "ViewTrackedMessage");
        OpenEmailCommand = new AsyncRelayCommand(OpenEmailAsync, logger: _logger, commandName: "OpenEmail");
        EnsureModelCommand = new AsyncRelayCommand(_ => EnsureModelAsync(CancellationToken.None), logger: _logger, commandName: "EnsureModel");
        OpenModelPathCommand = new AsyncRelayCommand(_ => OpenModelPathAsync(), logger: _logger, commandName: "OpenModelPath");
        ApplyRetentionCommand = new AsyncRelayCommand(_ => ApplyRetentionAsync(CancellationToken.None), logger: _logger, commandName: "ApplyRetention");
        DeleteAllLocalDataCommand = new AsyncRelayCommand(_ => DeleteAllLocalDataAsync(CancellationToken.None), logger: _logger, commandName: "DeleteAllLocalData");
        DeleteAccountCommand = new AsyncRelayCommand(_ => DeleteAccountAsync(CancellationToken.None), logger: _logger, commandName: "DeleteAccount");
        TestOutlookCommand = new AsyncRelayCommand(_ => RefreshOutlookAsync(CancellationToken.None), logger: _logger, commandName: "TestOutlook");
        RepairOutlookCategoriesCommand = new AsyncRelayCommand(_ => RepairOutlookCategoriesAsync(CancellationToken.None), logger: _logger, commandName: "RepairOutlookCategories");
        SynchronizeOutlookCommand = new AsyncRelayCommand(_ => SynchronizeOutlookAsync(CancellationToken.None), logger: _logger, commandName: "SynchronizeOutlook");
        ExportOutlookDiagnosticsCommand = new AsyncRelayCommand(_ => ExportOutlookDiagnosticsAsync(CancellationToken.None), logger: _logger, commandName: "ExportOutlookDiagnostics");
        RemoveSenderRuleCommand = new AsyncRelayCommand(RemoveSenderRuleAsync, logger: _logger, commandName: "RemoveSenderRule");
        ModelProfiles = _modelSelectionService.GetProfiles();
        _selectedModelProfile = _modelSelectionService.GetCurrentProfile();
        AccountPresets =
        [
            new("Yahoo", "imap.mail.yahoo.com", "Sent;Sent Items"),
            new("Gmail", "imap.gmail.com", "[Gmail]/Sent Mail;Sent;Sent Items"),
            new("iCloud Mail", "imap.mail.me.com", "Sent Messages;Sent"),
            new("AOL Mail", "imap.aol.com", "Sent;Sent Items"),
            new("Fastmail", "imap.fastmail.com", "Sent;Sent Items"),
            new("Zoho Mail", "imap.zoho.com", "Sent;Sent Items"),
            new("Custom IMAP", "", "Sent;Sent Items", IsCustom: true)
        ];
        _selectedImapPreset = AccountPresets[0];
        AddAccountCommand = new AsyncRelayCommand(_ => AddAccountAsync(CreateNewAccount()), logger: _logger, commandName: "AddAccount");
        _liveReportService.ReportChanged += OnReportChanged;
        _scanProgressNotifier.ProgressChanged += OnScanProgressChanged;
        _appLogStore.Changed += OnAppLogChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TrackedMessageDetail>? TrackedMessageViewRequested;

    public ObservableCollection<TrackedItemView> Items { get; } = [];
    public ObservableCollection<EmailAccount> Accounts { get; } = [];
    public ObservableCollection<OutlookStoreInfo> OutlookStores { get; } = [];
    public ObservableCollection<SenderRuleView> SenderRules { get; } = [];
    public IReadOnlyList<EmailProviderPreset> AccountPresets { get; }
    public IReadOnlyList<AiModelProfile> ModelProfiles { get; }
    public ICollectionView ItemsView { get; }
    public ICommand RunScanCommand { get; }
    public ICommand DoneCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand SnoozeCommand { get; }
    public ICommand ReclassifyToDueTodayCommand { get; }
    public ICommand ReclassifyToUpcomingCommand { get; }
    public ICommand ReclassifyToWaitingReplyCommand { get; }
    public ICommand ReclassifyToCommercialOfferCommand { get; }
    public ICommand ViewTrackedMessageCommand { get; }
    public ICommand OpenEmailCommand { get; }
    public ICommand EnsureModelCommand { get; }
    public ICommand OpenModelPathCommand { get; }
    public ICommand ApplyRetentionCommand { get; }
    public ICommand DeleteAllLocalDataCommand { get; }
    public ICommand AddAccountCommand { get; }
    public ICommand DeleteAccountCommand { get; }
    public ICommand TestOutlookCommand { get; }
    public ICommand RepairOutlookCategoriesCommand { get; }
    public ICommand SynchronizeOutlookCommand { get; }
    public ICommand ExportOutlookDiagnosticsCommand { get; }
    public ICommand RemoveSenderRuleCommand { get; }

    public bool OutlookEnabled { get => _outlookEnabled; set => SetField(ref _outlookEnabled, value); }
    public OutlookStoreInfo? SelectedOutlookStore { get => _selectedOutlookStore; set => SetField(ref _selectedOutlookStore, value); }
    public SenderRuleView? SelectedSenderRule { get => _selectedSenderRule; set => SetField(ref _selectedSenderRule, value); }
    public EmailAccount OutlookMailbox
    {
        get => _outlookMailbox;
        set
        {
            if (value is not null && SetField(ref _outlookMailbox, value))
            {
                OutlookEnabled = false;
                SelectedOutlookStore = null;
                _ = LoadOutlookBindingAsync(value.Id, CancellationToken.None);
            }
        }
    }
    public string OutlookStatus { get => _outlookStatus; private set => SetField(ref _outlookStatus, value); }
    public string OutlookVersion { get => _outlookVersion; private set => SetField(ref _outlookVersion, value); }
    public int OutlookPendingCount { get => _outlookPendingCount; private set => SetField(ref _outlookPendingCount, value); }
    public string OutlookLastSuccess { get => _outlookLastSuccess; private set => SetField(ref _outlookLastSuccess, value); }
    public string OutlookLastError { get => _outlookLastError; private set => SetField(ref _outlookLastError, value); }

    public LiveReport Report
    {
        get => _report;
        private set => SetField(ref _report, value);
    }

    public EmailAccount Account
    {
        get => _account;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetField(ref _account, value))
            {
                OnPropertyChanged(nameof(AccountName));
                OnPropertyChanged(nameof(AccountEmailAddress));
                OnPropertyChanged(nameof(AccountUsername));
                SyncImapPresetSelection(value);
                _ = RefreshPasswordStatusAsync(value.Id, CancellationToken.None);
            }
        }
    }

    public string AccountName
    {
        get => Account.Name ?? "";
        set
        {
            if (Account.Name == value)
            {
                return;
            }

            Account.Name = value;
            OnPropertyChanged();
            RefreshAccountLabels();
        }
    }

    public string AccountEmailAddress
    {
        get => Account.EmailAddress ?? "";
        set
        {
            if (Account.EmailAddress == value)
            {
                return;
            }

            var previousEmailAddress = Account.EmailAddress;
            Account.EmailAddress = value;
            OnPropertyChanged();

            if (string.IsNullOrWhiteSpace(Account.Username)
                || string.Equals(Account.Username, previousEmailAddress, StringComparison.OrdinalIgnoreCase))
            {
                Account.Username = value;
                OnPropertyChanged(nameof(AccountUsername));
            }

            RefreshAccountLabels();
        }
    }

    public string AccountUsername
    {
        get => Account.Username ?? "";
        set
        {
            if (Account.Username == value)
            {
                return;
            }

            Account.Username = value;
            OnPropertyChanged();
        }
    }

    public EmailProviderPreset SelectedImapPreset
    {
        get => _selectedImapPreset;
        set
        {
            if (!SetField(ref _selectedImapPreset, value) || _isSyncingPresetSelection)
            {
                return;
            }

            ApplyPresetToSelectedAccount(value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public string ModelStatus
    {
        get => _modelStatus;
        private set => SetField(ref _modelStatus, value);
    }

    public string ModelPath
    {
        get => _modelPath;
        private set => SetField(ref _modelPath, value);
    }

    public AiModelProfile SelectedModelProfile
    {
        get => _selectedModelProfile;
        set
        {
            if (value is not null)
            {
                SetField(ref _selectedModelProfile, value);
            }
        }
    }

    public bool IsScanInProgress
    {
        get => _isScanInProgress;
        private set => SetField(ref _isScanInProgress, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetField(ref _scanProgressText, value);
    }

    public string PasswordStatus
    {
        get => _passwordStatus;
        private set => SetField(ref _passwordStatus, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Main window view model initialization started.");
        await LoadAccountsAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        await RefreshModelStatusAsync(cancellationToken);
        await RefreshOutlookAsync(cancellationToken);
        await RefreshSenderRulesAsync(cancellationToken);
        _logger.LogInformation("Main window view model initialization completed.");
    }

    public async Task<string?> BuildTrackedMessageClipboardTextAsync(
        TrackedItemView? item,
        CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            StatusText = "Preparing tracked message for clipboard...";
            var text = await _liveReportService.GetTrackedMessageClipboardTextAsync(item.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "Tracked message no longer exists";
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare tracked message clipboard text. TrackedItemId={TrackedItemId}", item.Id);
            StatusText = "Could not prepare tracked message for clipboard";
            return null;
        }
    }

    public void NotifyTrackedMessageCopied(TrackedItemView item)
    {
        StatusText = $"Copied tracked message: {item.Subject}";
    }

    public void NotifyTrackedMessageCopyFailed(Exception exception)
    {
        _logger.LogError(exception, "Failed to copy tracked message to clipboard.");
        StatusText = "Could not copy tracked message to clipboard";
    }

    public void NotifySettingsSaveFailed(Exception exception)
    {
        _logger.LogError(exception, "Failed to save account settings. AccountId={AccountId}", Account.Id);
        StatusText = "Could not save account settings. Your existing accounts were not changed.";
    }

    private async Task ViewTrackedMessageAsync(object? parameter)
    {
        if (parameter is not TrackedItemView item)
        {
            return;
        }

        try
        {
            StatusText = "Opening tracked message...";
            var detail = await _liveReportService.GetTrackedMessageDetailAsync(item.Id, CancellationToken.None);
            if (detail is null)
            {
                StatusText = "Tracked message no longer exists";
                return;
            }

            TrackedMessageViewRequested?.Invoke(detail);
            StatusText = $"Opened tracked message: {detail.Subject}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open tracked message detail. TrackedItemId={TrackedItemId}", item.Id);
            StatusText = "Could not open tracked message";
        }
    }

    public Task UpdateTrackedMessageStatusAsync(TrackedMessageDetail detail, TrackedItemStatus status) =>
        UpdateStatusAsync(detail, status);

    public async Task UpdateTrackedMessageDeadlineAsync(TrackedMessageDetail detail, DateTimeOffset? deadline)
    {
        await _liveReportService.UpdateDeadlineAsync(detail.TrackedItemId, deadline, CancellationToken.None);
        StatusText = deadline is null
            ? $"Deadline cleared for {detail.Subject}"
            : $"Deadline updated for {detail.Subject}";
    }

    public async Task<bool> ApplySenderRuleAsync(TrackedMessageDetail detail, SenderRuleKind kind)
    {
        if (detail.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(detail.SenderEmail))
        {
            throw new InvalidOperationException("This message does not have a valid account and sender address.");
        }

        if (kind == SenderRuleKind.Ignored)
        {
            var confirmed = await _approvalService.ConfirmAsync(
                "Ignore sender",
                $"Ignore {detail.SenderEmail}? Future messages from this sender will be discarded before local AI classification.",
                CancellationToken.None);
            if (!confirmed)
            {
                return false;
            }
        }

        await _senderRuleService.SetRuleAsync(detail.AccountId, detail.SenderEmail, kind, CancellationToken.None);
        if (kind == SenderRuleKind.Ignored)
        {
            await _liveReportService.UpdateStatusAsync(detail.TrackedItemId, TrackedItemStatus.Dismissed, CancellationToken.None);
        }

        await RefreshSenderRulesAsync(CancellationToken.None);
        await RefreshAsync(CancellationToken.None);
        StatusText = kind == SenderRuleKind.Vip
            ? $"{detail.SenderEmail} is now a VIP sender"
            : $"{detail.SenderEmail} is now ignored";
        return true;
    }

    public async Task SaveSettingsAsync(string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Account.Username))
        {
            Account.Username = Account.EmailAddress;
            OnPropertyChanged(nameof(AccountUsername));
        }

        var outlookBinding = await BuildOutlookBindingAsync(cancellationToken);
        await _settingsCoordinator.SaveAsync(
            new SettingsSaveRequest(
                Account,
                outlookBinding,
                SelectedModelProfile.Preset,
                string.IsNullOrWhiteSpace(password) ? null : password),
            cancellationToken);

        await RefreshModelStatusAsync(cancellationToken);
        await RefreshPasswordStatusAsync(Account.Id, cancellationToken);

        await LoadAccountsAsync(cancellationToken, Account.Id);
        StatusText = $"Settings saved for {Account.DisplayName}";
    }

    private async Task SaveOutlookBindingAsync(CancellationToken cancellationToken)
    {
        var binding = await BuildOutlookBindingAsync(cancellationToken);
        if (binding is not null)
        {
            await _outlookSyncStore.SaveBindingAsync(binding, cancellationToken);
        }
    }

    private async Task<OutlookStoreBinding?> BuildOutlookBindingAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedOutlookStore;
        var existing = (await _outlookSyncStore.GetBindingsAsync(cancellationToken))
            .FirstOrDefault(x => x.VigiloMailboxId == OutlookMailbox.Id);
        if (OutlookEnabled && selected is null && string.IsNullOrWhiteSpace(existing?.OutlookStoreId))
            throw new InvalidOperationException("Select an Outlook store before enabling category tagging.");

        if (selected is null && existing is null) return null;
        return new OutlookStoreBinding
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            VigiloMailboxId = OutlookMailbox.Id,
            OutlookStoreId = selected?.StoreId ?? existing?.OutlookStoreId ?? "",
            OutlookStoreDisplayName = selected?.DisplayName ?? existing?.OutlookStoreDisplayName ?? "",
            AccountAddress = selected?.AccountAddress,
            IsEnabled = OutlookEnabled,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow
        };
    }

    private async Task LoadOutlookBindingAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        var binding = (await _outlookSyncStore.GetBindingsAsync(cancellationToken)).FirstOrDefault(x => x.VigiloMailboxId == mailboxId);
        if (OutlookMailbox.Id != mailboxId) return;
        OutlookEnabled = binding?.IsEnabled == true;
        SelectedOutlookStore = binding is null ? null : OutlookStores.FirstOrDefault(x => x.StoreId == binding.OutlookStoreId)
            ?? new OutlookStoreInfo(binding.OutlookStoreId, binding.OutlookStoreDisplayName, null);
    }

    private async Task RefreshOutlookAsync(CancellationToken cancellationToken)
    {
        var connection = await _outlookClient.ProbeAsync(cancellationToken);
        OutlookStatus = connection.Status.ToString();
        OutlookVersion = connection.Version ?? "Not detected";
        IReadOnlyList<OutlookStoreInfo> detectedStores = [];
        if (connection.Status == OutlookIntegrationStatus.Connected)
        {
            detectedStores = await _outlookClient.GetStoresAsync(cancellationToken);
        }

        var selectedStoreId = SelectedOutlookStore?.StoreId;
        OutlookStores.Clear();
        foreach (var store in detectedStores) OutlookStores.Add(store);
        if (connection.Status == OutlookIntegrationStatus.Connected)
        {
            await LoadOutlookBindingAsync(OutlookMailbox.Id, cancellationToken);
            SelectedOutlookStore ??= OutlookStores.FirstOrDefault(x => x.StoreId == selectedStoreId);
            if (SelectedOutlookStore is null && OutlookStores.Count == 1) SelectedOutlookStore = OutlookStores[0];
        }
        var statistics = await _outlookSyncStore.GetStatisticsAsync(cancellationToken);
        OutlookPendingCount = statistics.PendingCount;
        OutlookLastSuccess = statistics.LastSuccessfulSynchronizationUtc?.ToLocalTime().ToString("g") ?? "Never";
        OutlookLastError = statistics.LastErrorSanitized ?? connection.SanitizedError ?? "None";
        StatusText = $"Outlook: {OutlookStatus}";
    }

    private async Task RepairOutlookCategoriesAsync(CancellationToken cancellationToken)
    {
        await SaveOutlookBindingAsync(cancellationToken);
        var binding = await _outlookSyncStore.GetEnabledBindingAsync(OutlookMailbox.Id, cancellationToken)
            ?? throw new InvalidOperationException("Enable Outlook tagging and select a store first.");
        await _outlookClient.EnsureManagedCategoriesAsync(binding, cancellationToken);
        StatusText = "Outlook categories are ready";
    }

    private async Task SynchronizeOutlookAsync(CancellationToken cancellationToken)
    {
        await SaveOutlookBindingAsync(cancellationToken);
        await _outlookSyncStore.EnqueueTrackedMessagesAsync(OutlookMailbox.Id, cancellationToken);
        await RefreshOutlookAsync(cancellationToken);
        StatusText = $"Tracked messages for {OutlookMailbox.DisplayName} queued for Outlook category synchronization";
    }

    private async Task ExportOutlookDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await RefreshOutlookAsync(cancellationToken);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigilo", "diagnostics");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"outlook-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllLinesAsync(path,
        [
            $"Generated: {DateTimeOffset.Now:O}", $"Status: {OutlookStatus}", $"Classic Outlook version: {OutlookVersion}",
            $"Detected store count: {OutlookStores.Count}", $"Pending operations: {OutlookPendingCount}",
            $"Last success: {OutlookLastSuccess}", $"Last sanitized error: {OutlookLastError}",
            "Account addresses and message metadata are intentionally omitted."
        ], cancellationToken);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        StatusText = "Exported privacy-safe Outlook diagnostics";
    }

    private async Task RefreshSenderRulesAsync(CancellationToken cancellationToken)
    {
        var selectedId = SelectedSenderRule?.Id;
        var rules = await _senderRuleService.GetRulesAsync(cancellationToken);
        SenderRules.Clear();
        foreach (var rule in rules)
        {
            SenderRules.Add(rule);
        }

        SelectedSenderRule = selectedId is null
            ? null
            : SenderRules.FirstOrDefault(rule => rule.Id == selectedId.Value);
    }

    private async Task RemoveSenderRuleAsync(object? parameter)
    {
        var rule = parameter as SenderRuleView ?? SelectedSenderRule;
        if (rule is null)
        {
            return;
        }

        await _senderRuleService.RemoveRuleAsync(rule.Id, CancellationToken.None);
        await RefreshSenderRulesAsync(CancellationToken.None);
        StatusText = $"Removed {rule.KindDisplayName.ToLowerInvariant()} rule for {rule.SenderEmail}";
    }

    private Task AddAccountAsync(EmailAccount account)
    {
        Accounts.Add(account);
        Account = account;
        PasswordStatus = "No app password saved";
        StatusText = $"New {account.ProviderName} account. Enter the address, username, and app password, then save settings.";
        return Task.CompletedTask;
    }

    private EmailAccount CreateNewAccount()
    {
        var preset = SelectedImapPreset.IsCustom
            ? AccountPresets.First()
            : SelectedImapPreset;

        return preset.CreateAccount();
    }

    private void ApplyPresetToSelectedAccount(EmailProviderPreset preset)
    {
        if (preset.IsCustom)
        {
            return;
        }

        preset.ApplyTo(Account);
        OnPropertyChanged(nameof(Account));
        RefreshAccountLabels();
        StatusText = $"{preset.Name} IMAP settings applied to {Account.DisplayName}";
    }

    private void SyncImapPresetSelection(EmailAccount account)
    {
        var preset = AccountPresets.FirstOrDefault(x => x.Matches(account))
            ?? AccountPresets.First(x => x.IsCustom);

        _isSyncingPresetSelection = true;
        try
        {
            SelectedImapPreset = preset;
        }
        finally
        {
            _isSyncingPresetSelection = false;
        }
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken, Guid? selectedAccountId = null)
    {
        var outlookMailboxId = Accounts.Any(x => x.Id == OutlookMailbox.Id)
            ? OutlookMailbox.Id
            : selectedAccountId;
        var accounts = await _settingsService.GetAccountsAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            accounts = [await _settingsService.GetOrCreateDefaultAccountAsync(cancellationToken)];
        }

        Accounts.Clear();
        foreach (var account in accounts)
        {
            Accounts.Add(account);
        }

        Account = Accounts.FirstOrDefault(x => x.Id == selectedAccountId)
            ?? Accounts.FirstOrDefault()
            ?? new EmailAccount();
        OutlookMailbox = Accounts.FirstOrDefault(x => x.Id == outlookMailboxId)
            ?? Account;
    }

    private async Task RunScanAsync(CancellationToken cancellationToken)
    {
        IsScanInProgress = true;
        ScanProgressText = "Preparing scan...";
        _scanProgressStartedAt = null;
        _scanProgressScope = "";
        StatusText = "Scanning mailboxes...";
        _logger.LogInformation("Manual mailbox scan requested.");
        try
        {
            var result = await _scanQueue.QueueScanAsync(cancellationToken);
            ScanProgressText = "";
            await RefreshAsync(cancellationToken);
            StatusText = $"Scan complete: {result.Classified} classified, {result.NeedsReview} needs review, {result.Failed} failed";
            _logger.LogInformation(
                "Manual mailbox scan completed. Fetched={Fetched} Classified={Classified} NeedsReview={NeedsReview} Failed={Failed}",
                result.Fetched,
                result.Classified,
                result.NeedsReview,
                result.Failed);
        }
        finally
        {
            IsScanInProgress = false;
            _scanProgressStartedAt = null;
            _scanProgressScope = "";
        }
    }

    private async void OnScanProgressChanged(EmailScanProgress progress)
    {
        await App.Current.Dispatcher.InvokeAsync(() => ApplyScanProgress(progress));
    }

    private void ApplyScanProgress(EmailScanProgress progress)
    {
        IsScanInProgress = true;
        StatusText = "Scanning mailboxes...";
        UpdateScanProgress(progress);

        if (!IsTerminalScanProgress(progress))
        {
            return;
        }

        IsScanInProgress = false;
        _scanProgressStartedAt = null;
        _scanProgressScope = "";
        StatusText = string.Equals(progress.Stage, "Complete", StringComparison.Ordinal)
            ? "Scan complete"
            : $"Scan finished: {progress.Stage}";
    }

    private static bool IsTerminalScanProgress(EmailScanProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.FolderName))
        {
            return false;
        }

        if (progress.AccountCount > 0 && progress.AccountIndex < progress.AccountCount)
        {
            return false;
        }

        return progress.Stage is "Complete"
            or "Skipped incomplete settings"
            or "Skipped missing app password"
            or "Authentication failed"
            or "Scan failed";
    }

    private void UpdateScanProgress(EmailScanProgress progress)
    {
        var accountPart = progress.AccountCount > 1
            ? $"Account {progress.AccountIndex}/{progress.AccountCount}: {progress.AccountName}"
            : progress.AccountName;

        if (string.IsNullOrWhiteSpace(progress.FolderName))
        {
            ScanProgressText = $"{accountPart} - {progress.Stage}";
            return;
        }

        var folderPart = progress.FolderCount > 1
            ? $"folder {progress.FolderIndex}/{progress.FolderCount} {progress.FolderName}"
            : progress.FolderName;
        var messagePart = progress.TotalMessages > 0
            ? $"{progress.ProcessedMessages}/{progress.TotalMessages} messages"
            : "0 messages to process";
        ScanProgressText = $"{accountPart} - {folderPart} - {progress.Stage} - {messagePart}{EstimateScanEta(progress)}";
    }

    private string EstimateScanEta(EmailScanProgress progress)
    {
        if (progress.TotalMessages <= 0)
        {
            _scanProgressStartedAt = null;
            _scanProgressScope = "";
            return "";
        }

        var scope = $"{progress.AccountIndex}:{progress.FolderIndex}:{progress.TotalMessages}";
        if (_scanProgressScope != scope || _scanProgressStartedAt is null)
        {
            _scanProgressScope = scope;
            _scanProgressStartedAt = DateTimeOffset.Now;
        }

        if (progress.ProcessedMessages <= 0 || progress.ProcessedMessages >= progress.TotalMessages)
        {
            return "";
        }

        var elapsed = DateTimeOffset.Now - _scanProgressStartedAt.Value;
        if (elapsed.TotalSeconds < 1)
        {
            return "";
        }

        var secondsPerMessage = elapsed.TotalSeconds / progress.ProcessedMessages;
        var remainingMessages = progress.TotalMessages - progress.ProcessedMessages;
        var remaining = TimeSpan.FromSeconds(secondsPerMessage * remainingMessages);
        return $" - ETA {FormatEta(remaining)}";
    }

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalMinutes < 1)
        {
            return "<1 min";
        }

        if (remaining.TotalHours < 1)
        {
            return $"{Math.Ceiling(remaining.TotalMinutes)} min";
        }

        return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(LiveReportChange.All, cancellationToken);
    }

    private async Task RefreshAsync(LiveReportChange changes, CancellationToken cancellationToken)
    {
        if ((changes & LiveReportChange.Summary) != 0)
        {
            Report = await _liveReportService.GetLiveReportAsync(cancellationToken);
        }

        if ((changes & LiveReportChange.TrackedItems) != 0)
        {
            ReconcileItems(await _liveReportService.GetItemsAsync(cancellationToken));
        }

        StatusText = $"Updated {DateTimeOffset.Now:t}";
    }

    private void ReconcileItems(IReadOnlyList<TrackedItemView> updatedItems)
    {
        var desiredIds = updatedItems.Select(item => item.Id).ToHashSet();
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(Items[index].Id))
            {
                Items.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < updatedItems.Count; targetIndex++)
        {
            var updatedItem = updatedItems[targetIndex];
            var currentIndex = IndexOfItem(updatedItem.Id, targetIndex);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, updatedItem);
                continue;
            }

            if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }

            if (Items[targetIndex] != updatedItem)
            {
                Items[targetIndex] = updatedItem;
            }
        }
    }

    private int IndexOfItem(Guid id, int startIndex)
    {
        for (var index = startIndex; index < Items.Count; index++)
        {
            if (Items[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        StatusText = "Checking model...";
        _logger.LogInformation("Local model install/check requested.");
        var progress = new Progress<string>(message => StatusText = message);
        var result = await _modelManager.EnsureModelAsync(cancellationToken, progress);
        ModelStatus = result.Status.Message;
        ModelPath = result.Status.ModelPath;
        StatusText = result.Message;
        _logger.LogInformation(
            "Local model install/check completed. Success={Success} Installed={Installed} ModelPath={ModelPath} MissingFiles={MissingFileCount}",
            result.Success,
            result.Status.IsInstalled,
            result.Status.ModelPath,
            result.Status.MissingFiles.Count);
    }

    private async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var result = await _maintenanceService.ApplyRetentionAsync(cancellationToken);
        StatusText = $"Retention applied: {result.DeletedMessages} messages deleted";
    }

    private async Task DeleteAllLocalDataAsync(CancellationToken cancellationToken)
    {
        await _liveReportService.DeleteAllLocalDataAsync(cancellationToken);
        StatusText = "All local email data deleted";
    }

    private async Task DeleteAccountAsync(CancellationToken cancellationToken)
    {
        var account = Account;
        var accountName = account.DisplayName;
        var confirmed = await _approvalService.ConfirmAsync(
            "Delete account",
            $"Delete {accountName} and its local Vigilo data? This does not delete email from the mailbox.",
            cancellationToken);
        if (!confirmed)
        {
            return;
        }

        await _settingsService.DeleteAccountAsync(account.Id, cancellationToken);
        await LoadAccountsAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = $"Deleted {accountName}";
    }

    private async Task RefreshModelStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _modelManager.GetStatusAsync(cancellationToken);
        ModelStatus = status.Message;
        ModelPath = status.ModelPath;
        _logger.LogInformation(
            "Local model status refreshed. Installed={Installed} ModelPath={ModelPath} MissingFiles={MissingFileCount}",
            status.IsInstalled,
            status.ModelPath,
            status.MissingFiles.Count);
    }

    private async Task RefreshPasswordStatusAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var hasPassword = await _settingsService.HasPasswordAsync(accountId, cancellationToken);
        if (Account.Id == accountId)
        {
            PasswordStatus = hasPassword ? "App password saved" : "No app password saved";
        }
    }

    private async Task UpdateStatusAsync(object? parameter, TrackedItemStatus status)
    {
        var trackedItemId = parameter switch
        {
            TrackedItemView item => item.Id,
            TrackedMessageDetail detail => detail.TrackedItemId,
            _ => (Guid?)null
        };
        if (trackedItemId is null)
        {
            return;
        }

        if (status == TrackedItemStatus.Snoozed)
        {
            await _liveReportService.SnoozeAsync(trackedItemId.Value, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        }
        else
        {
            await _liveReportService.UpdateStatusAsync(trackedItemId.Value, status, CancellationToken.None);
        }
    }

    private async Task ReclassifyAsync(object? parameter, string section)
    {
        var trackedItemId = parameter switch
        {
            TrackedItemView item => item.Id,
            TrackedMessageDetail detail => detail.TrackedItemId,
            _ => (Guid?)null
        };
        if (trackedItemId is null)
        {
            return;
        }

        await _liveReportService.ReclassifyAsync(trackedItemId.Value, section, CancellationToken.None);
    }

    private static Task OpenEmailAsync(object? parameter)
    {
        if (parameter is not TrackedItemView item || string.IsNullOrWhiteSpace(item.SenderEmail))
        {
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo($"mailto:{item.SenderEmail}") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private Task OpenModelPathAsync()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            StatusText = "Model path is not configured";
            return Task.CompletedTask;
        }

        var directory = Directory.Exists(ModelPath)
            ? ModelPath
            : FindNearestExistingDirectory(ModelPath);
        if (directory is null)
        {
            StatusText = "Model path does not exist yet";
            _logger.LogWarning("Model path could not be opened because no existing parent directory was found. ModelPath={ModelPath}", ModelPath);
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        StatusText = Directory.Exists(ModelPath)
            ? "Opened model folder"
            : "Opened nearest existing model parent folder";
        return Task.CompletedTask;
    }

    private static string? FindNearestExistingDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (directory.Exists)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private void OnReportChanged(object? sender, LiveReportChangedEventArgs e)
    {
        lock (_reportRefreshLock)
        {
            _pendingReportChanges |= e.Changes;
            if (_reportRefreshScheduled)
            {
                return;
            }

            _reportRefreshScheduled = true;
        }

        _ = App.Current.Dispatcher.InvokeAsync(ProcessPendingReportChangesAsync);
    }

    private async Task ProcessPendingReportChangesAsync()
    {
        while (true)
        {
            LiveReportChange changes;
            lock (_reportRefreshLock)
            {
                changes = _pendingReportChanges;
                _pendingReportChanges = LiveReportChange.None;
            }

            try
            {
                await RefreshAsync(changes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh live report after a change notification. Changes={Changes}", changes);
            }

            lock (_reportRefreshLock)
            {
                if (_pendingReportChanges != LiveReportChange.None)
                {
                    continue;
                }

                _reportRefreshScheduled = false;
                return;
            }
        }
    }

    private void OnAppLogChanged(object? sender, EventArgs e)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            LogText = _appLogStore.Text;
            return;
        }

        _ = dispatcher.InvokeAsync(() => LogText = _appLogStore.Text);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshAccountLabels()
    {
        CollectionViewSource.GetDefaultView(Accounts)?.Refresh();
        OnPropertyChanged(nameof(Account));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        _liveReportService.ReportChanged -= OnReportChanged;
        _scanProgressNotifier.ProgressChanged -= OnScanProgressChanged;
        _appLogStore.Changed -= OnAppLogChanged;
    }
}

public sealed record EmailProviderPreset(string Name, string ImapHost, string SentFolders, bool IsCustom = false)
{
    public EmailAccount CreateAccount() => new()
    {
        ImapHost = ImapHost,
        ImapPort = 993,
        UseSsl = true,
        FoldersToMonitor = "Inbox",
        SentFoldersToMonitor = SentFolders
    };

    public void ApplyTo(EmailAccount account)
    {
        account.ImapHost = ImapHost;
        account.ImapPort = 993;
        account.UseSsl = true;
        account.FoldersToMonitor = "Inbox";
        account.SentFoldersToMonitor = SentFolders;
    }

    public bool Matches(EmailAccount account) =>
        !IsCustom && string.Equals(account.ImapHost, ImapHost, StringComparison.OrdinalIgnoreCase);
}
