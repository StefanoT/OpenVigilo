using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.LocalAi;

namespace Vigilo.Tests;

public sealed class StartupAndRuntimeTests
{
    [Theory]
    [InlineData(new[] { "--background" }, true)]
    [InlineData(new[] { "--BACKGROUND" }, true)]
    [InlineData(new[] { "--other" }, false)]
    [InlineData(new string[0], false)]
    public void Launch_options_parse_background_argument(string[] arguments, bool expected) =>
        Assert.Equal(expected, ApplicationLaunchOptions.Parse(arguments).Background);

    [Fact]
    public async Task Healthy_runtime_is_reused_without_starting_a_process()
    {
        var manager = new StubModelManager(installed: true);
        var options = new ModelOptions { Backend = ModelProfileCatalog.OpenAiCompatibleBackend };
        await using var supervisor = new LocalAiRuntimeSupervisor(
            manager,
            options,
            NullLogger<LocalAiRuntimeSupervisor>.Instance);

        Assert.True(await supervisor.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(LocalAiRuntimeState.Ready, supervisor.CurrentStatus.State);
        Assert.False(supervisor.CurrentStatus.OwnsProcess);
        Assert.Equal(1, manager.StatusChecks);
    }

    [Fact]
    public async Task Stopped_runtime_is_started_and_owned_until_readiness_succeeds()
    {
        // The server stays absent through the supervisor's saturation re-probe window, so
        // the owned-process startup path must take over and poll until readiness.
        var manager = new SequencedModelManager(false, false, false, false, false, true);
        await using var supervisor = new LocalAiRuntimeSupervisor(
            manager,
            new ModelOptions
            {
                Backend = ModelProfileCatalog.OpenAiCompatibleBackend,
                RuntimeExecutablePath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                RuntimeArguments = "/c ping 127.0.0.1 -n 30 > nul",
                RuntimeStartupTimeoutSeconds = 10
            },
            NullLogger<LocalAiRuntimeSupervisor>.Instance);

        Assert.True(await supervisor.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(LocalAiRuntimeState.Ready, supervisor.CurrentStatus.State);
        Assert.True(supervisor.CurrentStatus.OwnsProcess);
        Assert.NotNull(supervisor.CurrentStatus.ProcessId);
        Assert.True(manager.StatusChecks >= 3);
    }

    [Fact]
    public async Task Missing_in_process_model_reports_setup_required_without_downloading()
    {
        var manager = new StubModelManager(installed: false);
        await using var supervisor = new LocalAiRuntimeSupervisor(
            manager,
            new ModelOptions { Backend = ModelProfileCatalog.OnnxBackend },
            NullLogger<LocalAiRuntimeSupervisor>.Instance);

        Assert.False(await supervisor.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(LocalAiRuntimeState.SetupRequired, supervisor.CurrentStatus.State);
        Assert.Equal(0, manager.EnsureCalls);
    }

    [Fact]
    public async Task Unconfigured_external_runtime_failure_keeps_supervisor_operational()
    {
        var manager = new StubModelManager(installed: false);
        await using var supervisor = new LocalAiRuntimeSupervisor(
            manager,
            new ModelOptions
            {
                Backend = ModelProfileCatalog.OpenAiCompatibleBackend,
                RuntimeExecutablePath = ""
            },
            NullLogger<LocalAiRuntimeSupervisor>.Instance);

        Assert.False(await supervisor.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(LocalAiRuntimeState.SetupRequired, supervisor.CurrentStatus.State);
        Assert.Contains("RuntimeExecutablePath", supervisor.CurrentStatus.Message);
    }

    [Fact]
    public async Task Runtime_readiness_check_honors_cancellation()
    {
        var manager = new CancelingModelManager();
        await using var supervisor = new LocalAiRuntimeSupervisor(
            manager,
            new ModelOptions { Backend = ModelProfileCatalog.OnnxBackend },
            NullLogger<LocalAiRuntimeSupervisor>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.EnsureReadyAsync(cancellation.Token));
    }

    private sealed class StubModelManager(bool installed) : IModelManager
    {
        public int StatusChecks { get; private set; }
        public int EnsureCalls { get; private set; }

        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusChecks++;
            return Task.FromResult(new ModelStatus(installed, "model", "test", installed ? "Ready." : "Model setup is required.", installed ? [] : ["model"]));
        }

        public Task<ModelInstallResult> EnsureModelAsync(CancellationToken cancellationToken, IProgress<string>? progress = null)
        {
            EnsureCalls++;
            throw new NotSupportedException();
        }
    }

    private sealed class CancelingModelManager : IModelManager
    {
        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled<ModelStatus>(cancellationToken);

        public Task<ModelInstallResult> EnsureModelAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
            throw new NotSupportedException();
    }

    private sealed class SequencedModelManager(params bool[] installedStates) : IModelManager
    {
        private int _index;

        public int StatusChecks { get; private set; }

        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusChecks++;
            var stateIndex = Math.Min(_index++, installedStates.Length - 1);
            var installed = installedStates[stateIndex];
            return Task.FromResult(new ModelStatus(installed, "model", "test", installed ? "Ready." : "Starting.", installed ? [] : ["model"]));
        }

        public Task<ModelInstallResult> EnsureModelAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
            throw new NotSupportedException();
    }
}
