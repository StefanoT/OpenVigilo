using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.LocalAi;
using Xunit;

namespace Vigilo.Tests;

public sealed class LocalAiRuntimeSupervisorTests
{
    private static ModelOptions OpenAiCompatibleOptions() => new()
    {
        Preset = "Gemma4E2BLiteRt",
        DisplayName = "Google Gemma 4 E2B (LiteRT-LM)",
        Backend = ModelProfileCatalog.OpenAiCompatibleBackend,
        EndpointBaseUrl = "http://localhost:9379/v1",
        ChatModel = "gemma4-e2b",
        SetupInstructions = "Install LiteRT-LM and import the model."
    };

    private sealed class ScriptedStatusManager : IModelManager
    {
        private int _calls;

        public int Calls => _calls;

        public IReadOnlyList<int> MissesBeforeReady { get; init; } = [];

        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var call = _calls++;
            if (call < MissesBeforeReady.Count)
            {
                return Task.FromResult(ModelStatus(false));
            }

            return Task.FromResult(ModelStatus(true));
        }

        private static ModelStatus ModelStatus(bool installed) => new(
            installed,
            "http://localhost:9379/v1",
            "gemma4-e2b",
            installed
                ? "available"
                : "Local server is not reachable at http://localhost:9379/v1. Install LiteRT-LM.",
            []);

        public Task<ModelInstallResult> EnsureModelAsync(
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) =>
            throw new NotSupportedException("Status tests never install models.");
    }

    [Fact]
    public async Task ReprobesBusyOpenAiCompatibleServer_BeforeReportingReady()
    {
        // Production failure: the loopback server was saturated and answered the 3-second
        // status probe slower than its timeout; one failed probe must not fail inference.
        ScriptedStatusManager manager = new() { MissesBeforeReady = [0, 1] };
        LocalAiRuntimeSupervisor supervisor = new(
            manager,
            OpenAiCompatibleOptions(),
            NullLogger<LocalAiRuntimeSupervisor>.Instance,
            TimeSpan.FromMilliseconds(1));

        var ready = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.True(ready);
        Assert.Equal(3, manager.Calls);
        Assert.Equal(LocalAiRuntimeState.Ready, supervisor.CurrentStatus.State);
    }

    [Fact]
    public async Task ReportsSetupRequired_WhenOpenAiCompatibleServerStaysAbsent()
    {
        ScriptedStatusManager manager = new() { MissesBeforeReady = [0, 1, 2, 3, 4] };
        LocalAiRuntimeSupervisor supervisor = new(
            manager,
            OpenAiCompatibleOptions(),
            NullLogger<LocalAiRuntimeSupervisor>.Instance,
            TimeSpan.FromMilliseconds(1));

        var ready = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.False(ready);
        Assert.Equal(4, manager.Calls);
        Assert.Equal(LocalAiRuntimeState.SetupRequired, supervisor.CurrentStatus.State);
    }

    [Fact]
    public async Task DoesNotReprobeOnnxBackend()
    {
        var options = OpenAiCompatibleOptions();
        options.Backend = "OnnxGenAi";
        ScriptedStatusManager manager = new() { MissesBeforeReady = [0, 1, 2, 3, 4] };
        LocalAiRuntimeSupervisor supervisor = new(
            manager,
            options,
            NullLogger<LocalAiRuntimeSupervisor>.Instance,
            TimeSpan.FromMilliseconds(1));

        var ready = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.False(ready);
        Assert.Equal(1, manager.Calls);
        Assert.Equal(LocalAiRuntimeState.SetupRequired, supervisor.CurrentStatus.State);
    }
}
