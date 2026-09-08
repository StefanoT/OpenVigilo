using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.LocalAi;

namespace Vigilo.Tests;

public sealed class LocalAiActivityTrackerTests
{
    [Fact]
    public void Inference_activity_remains_active_until_every_scope_ends()
    {
        var tracker = new LocalAiActivityTracker();

        var first = tracker.BeginInference();
        var second = tracker.BeginInference();

        Assert.True(tracker.IsInferenceActive);
        first.Dispose();
        Assert.True(tracker.IsInferenceActive);
        second.Dispose();
        Assert.False(tracker.IsInferenceActive);
    }

    [Fact]
    public async Task OpenAi_status_check_is_suppressed_while_inference_is_active()
    {
        var options = new ModelOptions
        {
            Backend = ModelProfileCatalog.OpenAiCompatibleBackend,
            DisplayName = "Test model",
            EndpointBaseUrl = "http://127.0.0.1:1/v1",
            ChatModel = "test-model"
        };
        var tracker = new LocalAiActivityTracker();
        var manager = new Phi4MiniModelManager(
            options,
            new DenyApprovalService(),
            tracker,
            NullLogger<Phi4MiniModelManager>.Instance);

        using (tracker.BeginInference())
        {
            var activeStatus = await manager.GetStatusAsync(CancellationToken.None);

            Assert.True(activeStatus.IsInstalled);
            Assert.Contains("running local inference", activeStatus.Message, StringComparison.OrdinalIgnoreCase);
        }

        var idleStatus = await manager.GetStatusAsync(CancellationToken.None);
        Assert.False(idleStatus.IsInstalled);
    }

    private sealed class DenyApprovalService : IUserApprovalService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
