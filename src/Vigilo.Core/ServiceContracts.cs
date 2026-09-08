namespace Vigilo.Core;

public interface IAccountSettingsService
{
    Task<IReadOnlyList<EmailAccount>> GetAccountsAsync(CancellationToken cancellationToken);
    Task<EmailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<EmailAccount> GetOrCreateDefaultAccountAsync(CancellationToken cancellationToken);
    Task SaveAccountAsync(EmailAccount account, CancellationToken cancellationToken);
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> HasPasswordAsync(Guid accountId, CancellationToken cancellationToken);
    Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken);
    Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken);
    Task DeletePasswordAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed record SettingsSaveRequest(
    EmailAccount Account,
    OutlookStoreBinding? OutlookBinding,
    string ModelPreset,
    string? NewPassword);

public interface ISettingsCoordinator
{
    Task SaveAsync(SettingsSaveRequest request, CancellationToken cancellationToken);
}

public interface ISenderRuleService
{
    Task<SenderRuleKind?> GetRuleKindAsync(Guid accountId, string senderEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<SenderRuleView>> GetRulesAsync(CancellationToken cancellationToken);
    Task SetRuleAsync(Guid accountId, string senderEmail, SenderRuleKind kind, CancellationToken cancellationToken);
    Task RemoveRuleAsync(Guid ruleId, CancellationToken cancellationToken);
}

public interface IEmailProcessingService
{
    Task<long> GetHighestKnownUidAsync(Guid accountId, string folder, long uidValidity, CancellationToken cancellationToken);
    Task<long?> GetKnownUidValidityAsync(Guid accountId, string folder, CancellationToken cancellationToken);
    Task MarkFolderUidValidityChangedAsync(Guid accountId, string folder, long newUidValidity, CancellationToken cancellationToken);
    Task<EmailProcessingOutcome> ProcessFetchedEmailAsync(
        FetchedEmail fetchedEmail,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null);
    Task<int> RetryDeferredClassificationsAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress = null);
}

public interface IEmailScanner
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken, IProgress<EmailScanProgress>? progress = null);
    Task<ScanResult> ScanFolderAsync(EmailAccount account, string folderName, CancellationToken cancellationToken);
}

public interface IEmailScanQueue
{
    Task<ScanResult> QueueScanAsync(CancellationToken cancellationToken, IProgress<EmailScanProgress>? progress = null);
}

public interface IEmailScanProgressNotifier
{
    event Action<EmailScanProgress>? ProgressChanged;

    void NotifyProgress(EmailScanProgress progress);
}

public interface ISentReplyDetectionService
{
    Task<int> DetectSentRepliesAsync(CancellationToken cancellationToken);
}

public interface IMessageClassifier
{
    Task<ClassificationResult> ClassifyAsync(
        EmailMessage message,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null);
}

public interface IModelManager
{
    Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<ModelInstallResult> EnsureModelAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress = null);
}

public interface ILocalAiRuntimeSupervisor
{
    event EventHandler<LocalAiRuntimeStatus>? StatusChanged;

    LocalAiRuntimeStatus CurrentStatus { get; }

    Task<bool> EnsureReadyAsync(CancellationToken cancellationToken);
}

public enum LocalAiRuntimeState
{
    Starting,
    Ready,
    SetupRequired,
    Unavailable,
    Stopped
}

public sealed record LocalAiRuntimeStatus(
    LocalAiRuntimeState State,
    string Message,
    bool OwnsProcess = false,
    int? ProcessId = null,
    int? LastExitCode = null);

public interface IModelSelectionService
{
    IReadOnlyList<AiModelProfile> GetProfiles();
    AiModelProfile GetCurrentProfile();
    Task SelectProfileAsync(string preset, CancellationToken cancellationToken);
}

public interface ILocalAiClient
{
    LocalAiInferenceCapabilities InferenceCapabilities => LocalAiInferenceCapabilities.Conservative;

    Task<string> CompleteAsync(
        string prompt,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null);

    Task<string> CompleteConversationAsync(
        IReadOnlyList<LocalAiMessage> messages,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var prompt = string.Join(
            "\n\n",
            messages.Select(message => $"{message.Role}:\n{message.Content}"));
        return CompleteAsync(prompt, cancellationToken, progress);
    }

    Task<string> CompleteConversationAsync(
        IReadOnlyList<LocalAiMessage> messages,
        string? responseJsonSchema,
        LocalAiThinkingOptions? thinking,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null) =>
        CompleteConversationAsync(messages, cancellationToken, progress);
}

public enum LocalAiMessageRole
{
    System,
    User,
    Assistant
}

public sealed record LocalAiMessage(LocalAiMessageRole Role, string Content);

/// <summary>
/// Per-request reasoning controls for local models that support a thinking pass.
/// Runtimes without such controls ignore these options; <see cref="MaximumThinkingTokens"/>
/// is honored by inflating the request's generation ceiling so a bounded thought cannot
/// consume the final answer's token budget.
/// </summary>
public sealed record LocalAiThinkingOptions(
    bool Enabled,
    string ReasoningEffort = "low",
    int MaximumThinkingTokens = 256);

[Flags]
public enum LiveReportChange
{
    None = 0,
    Summary = 1,
    TrackedItems = 2,
    All = Summary | TrackedItems
}

public sealed class LiveReportChangedEventArgs(LiveReportChange changes) : EventArgs
{
    public LiveReportChange Changes { get; } = changes;
}

public interface ILiveReportService
{
    event EventHandler<LiveReportChangedEventArgs>? ReportChanged;

    Task<LiveReport> GetLiveReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TrackedItemView>> GetItemsAsync(CancellationToken cancellationToken);
    Task<TrackedMessageDetail?> GetTrackedMessageDetailAsync(Guid trackedItemId, CancellationToken cancellationToken);
    Task<string?> GetTrackedMessageClipboardTextAsync(Guid trackedItemId, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid trackedItemId, TrackedItemStatus status, CancellationToken cancellationToken);
    Task ReclassifyAsync(Guid trackedItemId, string section, CancellationToken cancellationToken);
    Task UpdateDeadlineAsync(Guid trackedItemId, DateTimeOffset? deadline, CancellationToken cancellationToken);
    Task SnoozeAsync(Guid trackedItemId, DateTimeOffset snoozedUntil, CancellationToken cancellationToken);
    Task DeleteAllLocalDataAsync(CancellationToken cancellationToken);
    void NotifyChanged(LiveReportChange changes);
}

public interface ILocalDataMaintenanceService
{
    Task<LocalDataMaintenanceResult> ApplyRetentionAsync(CancellationToken cancellationToken);
}

public interface INotificationService
{
    Task NotifyAsync(NotificationKind kind, TrackedItem item, EmailMessage message, CancellationToken cancellationToken);
}

public interface IUserApprovalService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
}

public interface IAppBootstrapper
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IOutlookStaDispatcher : IAsyncDisposable
{
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

public interface IOutlookClient
{
    Task<OutlookConnectionInfo> ProbeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OutlookStoreInfo>> GetStoresAsync(CancellationToken cancellationToken);
    Task EnsureManagedCategoriesAsync(OutlookStoreBinding binding, CancellationToken cancellationToken);
    Task<OutlookMessageMatchResult> FindMessageAsync(OutlookStoreBinding binding, OutlookMessageIdentity identity, CancellationToken cancellationToken);
    Task ApplyCategoriesAsync(OutlookItemReference item, IReadOnlySet<string> desiredCategories, CancellationToken cancellationToken);
}

public interface IOutlookSyncStore
{
    Task EnqueueOrSupersedeAsync(Guid messageId, IReadOnlySet<string> desiredCategories, CancellationToken cancellationToken, bool force = false);
    Task<IReadOnlyList<OutlookCategorySyncOperation>> GetDueAsync(int maximumCount, DateTimeOffset now, CancellationToken cancellationToken);
    Task<OutlookStoreBinding?> GetEnabledBindingAsync(Guid mailboxId, CancellationToken cancellationToken);
    Task<OutlookStoreBinding?> GetBindingForMessageAsync(Guid messageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutlookStoreBinding>> GetBindingsAsync(CancellationToken cancellationToken);
    Task SaveBindingAsync(OutlookStoreBinding binding, CancellationToken cancellationToken);
    Task<OutlookMessageIdentity?> GetIdentityAsync(Guid messageId, CancellationToken cancellationToken);
    Task SaveItemBindingAsync(Guid messageId, string? internetMessageId, OutlookItemReference item, CancellationToken cancellationToken);
    Task MarkInProgressAsync(Guid operationId, CancellationToken cancellationToken);
    Task MarkCompletedAsync(Guid operationId, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid operationId, OutlookSyncStatus status, string code, string sanitizedMessage, DateTimeOffset? nextAttempt, CancellationToken cancellationToken);
    Task<int> RecoverStaleInProgressAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
    Task<OutlookQueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
    Task EnqueueAllTrackedMessagesAsync(CancellationToken cancellationToken);
    Task EnqueueTrackedMessagesAsync(Guid mailboxId, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies the live classification pipeline version so persistence can detect classifier
/// or prompt changes without duplicating version constants. <see cref="ClassificationMetadata"/>
/// remains only as the static fallback for composition roots that do not register a source.
/// </summary>
public interface IClassificationVersionSource
{
    /// <summary>Composite of harness version and prompt content version; changes on either.</summary>
    string ClassificationVersion { get; }

    /// <summary>Version derived from the current prompt content; changes on any prompt edit.</summary>
    string PromptVersion { get; }
}

public sealed class ModelOptions
{
    public string Preset { get; set; } = "Phi4Mini";
    public string DisplayName { get; set; } = "Phi-4 Mini ONNX int4";
    public string Backend { get; set; } = "OnnxGenAi";
    public string ModelRoot { get; set; } = "";
    public string ModelBaseDirectory { get; set; } = "";
    public string Version { get; set; } = "Phi-4-mini-instruct-onnx/cpu-int4-rtn-block-32-acc-level-4";
    public string ManifestBaseUrl { get; set; } =
        "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/";
    public string EndpointBaseUrl { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public string SetupInstructions { get; set; } = "";
    public int InferenceTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Overrides the selected profile's advertised JSON-Schema enforcement capability.
    /// Null follows <see cref="InferenceCapabilities"/>; set it explicitly when pointing a
    /// profile at an endpoint whose schema enforcement differs from the profile default.
    /// </summary>
    public bool? EnforceResponseJsonSchemas { get; set; }
    public string RuntimeExecutablePath { get; set; } = "";
    public string RuntimeArguments { get; set; } = "";
    public int RuntimeStartupTimeoutSeconds { get; set; } = 90;
    public int RuntimeMaximumRestartAttempts { get; set; } = 3;
    public LocalAiInferenceCapabilities InferenceCapabilities { get; set; } =
        LocalAiInferenceCapabilities.Conservative;
    public string[] RequiredFiles { get; set; } =
    [
        "genai_config.json",
        "model.onnx",
        "model.onnx.data",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.json",
        "merges.txt",
        "special_tokens_map.json"
    ];
}

public sealed record AiModelProfile(
    string Preset,
    string DisplayName,
    string Backend,
    string Summary,
    LocalAiInferenceCapabilities InferenceCapabilities);
