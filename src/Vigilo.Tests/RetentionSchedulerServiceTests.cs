using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.Email;

namespace Vigilo.Tests;

public sealed class RetentionSchedulerServiceTests
{
    [Fact]
    public async Task Runs_retention_repeatedly_and_stops_on_shutdown()
    {
        var calls = 0;
        var firstCall = NewSignal();
        var secondCall = NewSignal();
        var maintenance = new StubMaintenanceService(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstCall.SetResult();
            }
            else
            {
                secondCall.SetResult();
            }

            return Task.FromResult(new LocalDataMaintenanceResult(0, 0));
        });

        using var scheduler = CreateScheduler(maintenance);
        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);

        Assert.True(await AwaitSignalAsync(firstCall.Task), "retention did not run after the initial delay.");
        Assert.True(await AwaitSignalAsync(secondCall.Task), "retention did not reschedule after the first pass.");

        await cts.CancelAsync();
        await scheduler.StopAsync(CancellationToken.None);
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task Keeps_scheduling_after_a_failed_pass()
    {
        var calls = 0;
        var failedPass = NewSignal();
        var retryPass = NewSignal();
        var maintenance = new StubMaintenanceService(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failedPass.SetResult();
                throw new InvalidOperationException("retention failure");
            }

            retryPass.SetResult();
            return Task.FromResult(new LocalDataMaintenanceResult(0, 0));
        });

        using var scheduler = CreateScheduler(maintenance);
        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);

        Assert.True(await AwaitSignalAsync(failedPass.Task), "retention did not run after the initial delay.");
        Assert.True(await AwaitSignalAsync(retryPass.Task), "scheduler stopped scheduling after a failed pass.");

        await cts.CancelAsync();
        await scheduler.StopAsync(CancellationToken.None);
    }

    private static RetentionSchedulerService CreateScheduler(ILocalDataMaintenanceService maintenance) =>
        new(
            new FixedServiceScopeFactory(maintenance),
            NullLogger<RetentionSchedulerService>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(10),
            interval: TimeSpan.FromMilliseconds(30));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> AwaitSignalAsync(Task signal)
    {
        var completed = await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(5)));
        return completed == signal;
    }

    private sealed class StubMaintenanceService(
        Func<CancellationToken, Task<LocalDataMaintenanceResult>> behavior) : ILocalDataMaintenanceService
    {
        public Task<LocalDataMaintenanceResult> ApplyRetentionAsync(CancellationToken cancellationToken) =>
            behavior(cancellationToken);
    }

    private sealed class FixedServiceScopeFactory(ILocalDataMaintenanceService maintenance) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedServiceScope(maintenance);

        private sealed class FixedServiceScope(ILocalDataMaintenanceService maintenance) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } =
                new FixedServiceProvider(maintenance);

            public void Dispose()
            {
            }
        }

        private sealed class FixedServiceProvider(ILocalDataMaintenanceService maintenance) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ILocalDataMaintenanceService) ? maintenance : null;
        }
    }
}
