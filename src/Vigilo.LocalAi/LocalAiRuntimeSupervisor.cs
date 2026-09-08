using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.LocalAi;

public sealed class LocalAiRuntimeSupervisor(
    IModelManager modelManager,
    ModelOptions options,
    ILogger<LocalAiRuntimeSupervisor> logger,
    TimeSpan? probeRetryDelay = null) : ILocalAiRuntimeSupervisor, IHostedService, IAsyncDisposable
{
    private static readonly string OpenAiCompatibleBackend = ModelProfileCatalog.OpenAiCompatibleBackend;

    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _probeRetryDelay = probeRetryDelay ?? TimeSpan.FromSeconds(2);
    private Process? _ownedProcess;
    private Task? _monitorTask;
    private int _restartAttempts;
    private int _diagnosticLineCount;

    public event EventHandler<LocalAiRuntimeStatus>? StatusChanged;

    public LocalAiRuntimeStatus CurrentStatus { get; private set; } =
        new(LocalAiRuntimeState.Starting, "Checking the local AI runtime.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorTask = MonitorAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken);
        try
        {
            var status = await modelManager.GetStatusAsync(cancellationToken);
            // A busy loopback server can exceed the short status-probe timeout while staying
            // perfectly usable for inference. A transport-level miss on an OpenAI-compatible
            // backend probes again before declaring the runtime unavailable, so server
            // saturation does not burn a classification attempt.
            for (var reprobe = 0; !status.IsInstalled
                    && string.Equals(options.Backend, OpenAiCompatibleBackend, StringComparison.Ordinal)
                    && reprobe < 3;
                reprobe++)
            {
                await Task.Delay(_probeRetryDelay, cancellationToken);
                status = await modelManager.GetStatusAsync(cancellationToken);
            }
            if (status.IsInstalled)
            {
                SetStatus(new(
                    LocalAiRuntimeState.Ready,
                    string.Equals(options.Backend, ModelProfileCatalog.OpenAiCompatibleBackend, StringComparison.OrdinalIgnoreCase)
                        ? "The local AI server is ready."
                        : "The local ONNX model is ready.",
                    _ownedProcess is not null,
                    TryGetProcessId(_ownedProcess)));
                return true;
            }

            if (!string.Equals(options.Backend, ModelProfileCatalog.OpenAiCompatibleBackend, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(new(LocalAiRuntimeState.SetupRequired, status.Message));
                return false;
            }

            if (_ownedProcess is null || _ownedProcess.HasExited)
            {
                if (_ownedProcess is { HasExited: true }
                    && _restartAttempts >= Math.Max(0, options.RuntimeMaximumRestartAttempts))
                {
                    SetStatus(new(
                        LocalAiRuntimeState.Unavailable,
                        $"The local AI server stopped after {_restartAttempts} restart attempts. Open Vigilo settings to check the runtime configuration.",
                        OwnsProcess: true,
                        ProcessId: TryGetProcessId(_ownedProcess),
                        LastExitCode: TryGetExitCode(_ownedProcess)));
                    return false;
                }

                if (string.IsNullOrWhiteSpace(options.RuntimeExecutablePath))
                {
                    SetStatus(new(
                        LocalAiRuntimeState.SetupRequired,
                        $"{status.Message} Configure Model:RuntimeExecutablePath to let Vigilo start the server."));
                    return false;
                }

                StartOwnedProcess();
            }

            SetStatus(new(
                LocalAiRuntimeState.Starting,
                "Starting the local AI server.",
                OwnsProcess: true,
                ProcessId: TryGetProcessId(_ownedProcess)));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.RuntimeStartupTimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, timeout.Token);
            var delay = TimeSpan.FromMilliseconds(250);
            while (!linked.IsCancellationRequested && _ownedProcess is { HasExited: false })
            {
                status = await modelManager.GetStatusAsync(linked.Token);
                if (status.IsInstalled)
                {
                    _restartAttempts = 0;
                    SetStatus(new(
                        LocalAiRuntimeState.Ready,
                        "The local AI server is ready.",
                        OwnsProcess: true,
                        ProcessId: TryGetProcessId(_ownedProcess)));
                    return true;
                }

                await Task.Delay(delay, linked.Token);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
            }

            if (_ownedProcess is { HasExited: false } timedOutProcess && timeout.IsCancellationRequested)
            {
                timedOutProcess.Kill(entireProcessTree: true);
                await timedOutProcess.WaitForExitAsync(CancellationToken.None);
            }

            var exitCode = TryGetExitCode(_ownedProcess);
            SetStatus(new(
                LocalAiRuntimeState.Unavailable,
                exitCode.HasValue
                    ? $"The local AI server exited with code {exitCode.Value}. Open Vigilo settings to check the runtime configuration."
                    : "The local AI server did not become ready before the startup timeout.",
                _ownedProcess is not null,
                TryGetProcessId(_ownedProcess),
                exitCode));
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (_ownedProcess is { HasExited: false } timedOutProcess)
            {
                timedOutProcess.Kill(entireProcessTree: true);
                await timedOutProcess.WaitForExitAsync(CancellationToken.None);
            }

            SetStatus(new(LocalAiRuntimeState.Unavailable, "The local AI server did not become ready before the startup timeout.", _ownedProcess is not null, TryGetProcessId(_ownedProcess)));
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local AI runtime startup failed. Backend={Backend}", options.Backend);
            SetStatus(new(LocalAiRuntimeState.Unavailable, "The local AI runtime could not be started. Open Vigilo settings for details."));
            return false;
        }
        finally
        {
            _startupGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await StopOwnedProcessAsync(cancellationToken);
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        SetStatus(new(LocalAiRuntimeState.Stopped, "The local AI runtime supervisor is stopped."));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var ready = await EnsureReadyAsync(cancellationToken);
            var wait = ready ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, _restartAttempts)));
            if (!ready && _ownedProcess is { HasExited: true } && _restartAttempts < Math.Max(0, options.RuntimeMaximumRestartAttempts))
            {
                _restartAttempts++;
                logger.LogWarning(
                    "Owned local AI runtime exited. ExitCode={ExitCode} RestartAttempt={RestartAttempt} MaximumRestartAttempts={MaximumRestartAttempts} DiagnosticLineCount={DiagnosticLineCount}",
                    TryGetExitCode(_ownedProcess),
                    _restartAttempts,
                    options.RuntimeMaximumRestartAttempts,
                    _diagnosticLineCount);
                _ownedProcess.Dispose();
                _ownedProcess = null;
            }

            await Task.Delay(wait, cancellationToken);
        }
    }

    private void StartOwnedProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.RuntimeExecutablePath,
            Arguments = options.RuntimeArguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _diagnosticLineCount = 0;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += CountDiagnosticLine;
        process.ErrorDataReceived += CountDiagnosticLine;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The configured local AI runtime process did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ownedProcess = process;
        logger.LogInformation(
            "Started owned local AI runtime. ProcessId={ProcessId} Executable={Executable}",
            process.Id,
            options.RuntimeExecutablePath);
    }

    private void CountDiagnosticLine(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            Interlocked.Increment(ref _diagnosticLineCount);
        }
    }

    private async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }

            logger.LogInformation(
                "Stopped owned local AI runtime. ExitCode={ExitCode} DiagnosticLineCount={DiagnosticLineCount}",
                TryGetExitCode(process),
                _diagnosticLineCount);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void SetStatus(LocalAiRuntimeStatus status)
    {
        if (CurrentStatus == status)
        {
            return;
        }

        CurrentStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private static int? TryGetProcessId(Process? process)
    {
        try { return process?.Id; } catch (InvalidOperationException) { return null; }
    }

    private static int? TryGetExitCode(Process? process)
    {
        try { return process is { HasExited: true } ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await StopOwnedProcessAsync(CancellationToken.None);
        _shutdown.Dispose();
        _startupGate.Dispose();
    }
}
